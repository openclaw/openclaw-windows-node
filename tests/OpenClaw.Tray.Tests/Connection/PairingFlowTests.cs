using OpenClaw.Shared;
using OpenClawTray.Services.Connection;

namespace OpenClaw.Tray.Tests.Connection;

/// <summary>
/// Tests the pairing flow through GatewayConnectionManager — specifically
/// node pairing state transitions driven by INodeConnector events.
/// Uses a ScriptedNodeConnector to fire PairingStatusChanged / StatusChanged
/// and verifies the manager's state machine and snapshot update correctly.
/// </summary>
public class PairingFlowTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GatewayRegistry _registry;
    private readonly MockCredentialResolver _resolver;
    private readonly MockClientFactory _factory;
    private readonly ScriptedNodeConnector _nodeConnector;

    public PairingFlowTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openclaw-pairing-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _registry = new GatewayRegistry(_tempDir);
        _resolver = new MockCredentialResolver();
        _factory = new MockClientFactory();
        _nodeConnector = new ScriptedNodeConnector();
    }

    public void Dispose()
    {
        _nodeConnector.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ─── Tests ───

    [Fact]
    public async Task NodePairingPending_UpdatesSnapshotState()
    {
        using var manager = CreateConnectedManager();

        // Node: Idle → Connecting
        await FireAndWait(manager, () =>
            _nodeConnector.FireStatusChanged(ConnectionStatus.Connecting));

        // Node: Connecting → PairingRequired
        var snapshot = await FireAndWait(manager, () =>
            _nodeConnector.FirePairingStatusChanged(PairingStatus.Pending));

        Assert.Equal(RoleConnectionState.PairingRequired, snapshot.NodeState);
        Assert.Equal(PairingStatus.Pending, snapshot.NodePairingStatus);
    }

    [Fact]
    public async Task NodePairingApproved_TransitionsToConnected()
    {
        using var manager = CreateConnectedManager();

        // Node: Idle → Connecting → PairingRequired
        await FireAndWait(manager, () =>
            _nodeConnector.FireStatusChanged(ConnectionStatus.Connecting));
        await FireAndWait(manager, () =>
            _nodeConnector.FirePairingStatusChanged(PairingStatus.Pending));

        // PairingRequired → Connected (via NodePaired trigger)
        var snapshot = await FireAndWait(manager, () =>
            _nodeConnector.FirePairingStatusChanged(PairingStatus.Paired));

        Assert.Equal(RoleConnectionState.Connected, snapshot.NodeState);
        Assert.Equal(PairingStatus.Paired, snapshot.NodePairingStatus);
    }

    [Fact]
    public async Task NodePairingRejected_TransitionsToRejectedState()
    {
        using var manager = CreateConnectedManager();

        // Node: Idle → Connecting → PairingRequired
        await FireAndWait(manager, () =>
            _nodeConnector.FireStatusChanged(ConnectionStatus.Connecting));
        await FireAndWait(manager, () =>
            _nodeConnector.FirePairingStatusChanged(PairingStatus.Pending));

        // PairingRequired → PairingRejected
        var snapshot = await FireAndWait(manager, () =>
            _nodeConnector.FirePairingStatusChanged(PairingStatus.Rejected));

        Assert.Equal(RoleConnectionState.PairingRejected, snapshot.NodeState);
        Assert.Equal(PairingStatus.Rejected, snapshot.NodePairingStatus);
    }

    [Fact]
    public async Task NodeConnected_UpdatesSnapshot()
    {
        using var manager = CreateConnectedManager();

        // Node: Idle → Connecting → Connected
        await FireAndWait(manager, () =>
            _nodeConnector.FireStatusChanged(ConnectionStatus.Connecting));

        var snapshot = await FireAndWait(manager, () =>
            _nodeConnector.FireStatusChanged(ConnectionStatus.Connected));

        Assert.Equal(RoleConnectionState.Connected, snapshot.NodeState);
    }

    [Fact]
    public async Task StateChanged_FiresOnNodePairingEvents()
    {
        using var manager = CreateConnectedManager();

        // Get node into Connecting first
        await FireAndWait(manager, () =>
            _nodeConnector.FireStatusChanged(ConnectionStatus.Connecting));

        // Collect all subsequent StateChanged snapshots
        var snapshots = new List<GatewayConnectionSnapshot>();
        manager.StateChanged += (_, s) => snapshots.Add(s);

        var snapshot = await FireAndWait(manager, () =>
            _nodeConnector.FirePairingStatusChanged(PairingStatus.Pending));

        Assert.NotEmpty(snapshots);
        Assert.Contains(snapshots, s => s.NodePairingStatus == PairingStatus.Pending);
    }

    [Fact]
    public async Task NodeDeviceId_ReflectedInSnapshot()
    {
        using var manager = CreateConnectedManager();
        _nodeConnector.NodeDeviceId = "my-node-device-42";

        await FireAndWait(manager, () =>
            _nodeConnector.FireStatusChanged(ConnectionStatus.Connecting));

        var snapshot = await FireAndWait(manager, () =>
            _nodeConnector.FireStatusChanged(ConnectionStatus.Connected));

        Assert.Equal("my-node-device-42", snapshot.NodeDeviceId);
    }

    [Fact]
    public async Task NodePairingRejected_FromConnecting_TransitionsCorrectly()
    {
        using var manager = CreateConnectedManager();

        // Node: Idle → Connecting
        await FireAndWait(manager, () =>
            _nodeConnector.FireStatusChanged(ConnectionStatus.Connecting));

        // Connecting → PairingRejected (valid per state machine: Connecting or PairingRequired)
        var snapshot = await FireAndWait(manager, () =>
            _nodeConnector.FirePairingStatusChanged(PairingStatus.Rejected));

        Assert.Equal(RoleConnectionState.PairingRejected, snapshot.NodeState);
        Assert.Equal(PairingStatus.Rejected, snapshot.NodePairingStatus);
    }

    // ─── Helpers ───

    private GatewayConnectionManager CreateConnectedManager(string gatewayId = "gw1", string url = "wss://test")
    {
        _registry.AddOrUpdate(new GatewayRecord { Id = gatewayId, Url = url });
        _registry.SetActive(gatewayId);
        Directory.CreateDirectory(_registry.GetIdentityDirectory(gatewayId));

        _resolver.OperatorCredential = new GatewayCredential("op-tok", false, "test");
        _resolver.NodeCredential = new GatewayCredential("node-tok", false, "test");

        var manager = new GatewayConnectionManager(
            _resolver, _factory, _registry, NullLogger.Instance,
            nodeConnector: _nodeConnector,
            isNodeEnabled: () => true);

        // Kick the operator into Connecting state so the lifecycle is created
        manager.ConnectAsync(gatewayId).GetAwaiter().GetResult();

        return manager;
    }

    /// <summary>
    /// Subscribe to StateChanged, execute <paramref name="action"/> (which fires
    /// an event on the scripted connector), then wait for the resulting snapshot.
    /// </summary>
    private static async Task<GatewayConnectionSnapshot> FireAndWait(
        GatewayConnectionManager manager, Action action, int timeoutMs = 5000)
    {
        var tcs = new TaskCompletionSource<GatewayConnectionSnapshot>();

        void Handler(object? _, GatewayConnectionSnapshot s)
        {
            manager.StateChanged -= Handler;
            tcs.TrySetResult(s);
        }
        manager.StateChanged += Handler;

        action();

        return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    // ─── Fakes / Mocks ───

    private sealed class MockCredentialResolver : ICredentialResolver
    {
        public GatewayCredential? OperatorCredential { get; set; }
        public GatewayCredential? NodeCredential { get; set; }

        public GatewayCredential? ResolveOperator(GatewayRecord record, string identityPath) => OperatorCredential;
        public GatewayCredential? ResolveNode(GatewayRecord record, string identityPath) => NodeCredential;
    }

    private sealed class MockClientFactory : IGatewayClientFactory
    {
        public IGatewayClientLifecycle Create(string gatewayUrl, GatewayCredential credential,
            string identityPath, IOpenClawLogger logger)
        {
            return new MockLifecycle(gatewayUrl);
        }
    }

    private sealed class MockLifecycle : IGatewayClientLifecycle
    {
        private readonly MockGatewayClient _client;

        public MockLifecycle(string url)
        {
            _client = new MockGatewayClient(url);
        }

        public OpenClawGatewayClient DataClient => _client;

#pragma warning disable CS0067 // Events required by interface but not fired in tests
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<string>? AuthenticationFailed;
#pragma warning restore CS0067

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class MockGatewayClient : OpenClawGatewayClient
    {
        public MockGatewayClient(string url)
            : base(url, "mock-token", NullLogger.Instance) { }
    }

    private sealed class ScriptedNodeConnector : INodeConnector
    {
        public bool IsConnected { get; private set; }
        public PairingStatus PairingStatus { get; set; } = PairingStatus.Unknown;
        public string? NodeDeviceId { get; set; }
        public NodeConnectionMode Mode { get; set; } = NodeConnectionMode.Disabled;

        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;
#pragma warning disable CS0067 // never raised in this test fixture — the bridge to NodeService isn't exercised here
        public event EventHandler<NodeClientCreatedEventArgs>? ClientCreated;
#pragma warning restore CS0067

        public Task ConnectAsync(string gatewayUrl, GatewayCredential credential,
            string identityPath, bool useV2Signature = false)
        {
            Mode = NodeConnectionMode.Gateway;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            Mode = NodeConnectionMode.Disabled;
            return Task.CompletedTask;
        }

        public void FireStatusChanged(ConnectionStatus status)
        {
            IsConnected = status == ConnectionStatus.Connected;
            StatusChanged?.Invoke(this, status);
        }

        public void FirePairingStatusChanged(PairingStatus status, string? requestId = null)
        {
            PairingStatus = status;
            PairingStatusChanged?.Invoke(this, new PairingStatusEventArgs(
                status, NodeDeviceId ?? "test-node", requestId: requestId));
        }

        public void Dispose() { }
    }
}
