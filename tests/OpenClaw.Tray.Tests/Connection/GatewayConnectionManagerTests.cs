using OpenClaw.Shared;
using OpenClawTray.Services.Connection;

namespace OpenClaw.Tray.Tests.Connection;

public class GatewayConnectionManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GatewayRegistry _registry;
    private readonly MockCredentialResolver _resolver;
    private readonly MockClientFactory _factory;
    private readonly GatewayConnectionManager _manager;

    public GatewayConnectionManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openclaw-mgr-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _registry = new GatewayRegistry(_tempDir);
        _resolver = new MockCredentialResolver();
        _factory = new MockClientFactory();
        _manager = new GatewayConnectionManager(
            _resolver, _factory, _registry, NullLogger.Instance);
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void InitialState_IsIdle()
    {
        Assert.Equal(OverallConnectionState.Idle, _manager.CurrentSnapshot.OverallState);
        Assert.Null(_manager.OperatorClient);
        Assert.Null(_manager.ActiveGatewayUrl);
    }

    [Fact]
    public async Task ConnectAsync_WithNoGateway_DoesNothing()
    {
        await _manager.ConnectAsync();
        Assert.Equal(OverallConnectionState.Idle, _manager.CurrentSnapshot.OverallState);
    }

    [Fact]
    public async Task ConnectAsync_WithNoCredential_TransitionsToError()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = null;

        GatewayConnectionSnapshot? lastSnap = null;
        _manager.StateChanged += (_, s) => lastSnap = s;

        await _manager.ConnectAsync("gw-1");

        Assert.Equal(OverallConnectionState.Error, _manager.CurrentSnapshot.OverallState);
        Assert.NotNull(lastSnap);
    }

    [Fact]
    public async Task ConnectAsync_WithCredential_TransitionsToConnecting()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");

        Assert.Equal(OverallConnectionState.Connecting, _manager.CurrentSnapshot.OverallState);
        Assert.Equal("wss://test", _manager.ActiveGatewayUrl);
        Assert.Equal("gw-1", _manager.CurrentSnapshot.GatewayId);
    }

    [Fact]
    public async Task ConnectAsync_CreatesClient()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");

        Assert.Single(_factory.CreatedClients);
        Assert.NotNull(_manager.OperatorClient);
    }

    [Fact]
    public async Task DisconnectAsync_TransitionsToIdle()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");
        await _manager.ConnectAsync("gw-1");

        await _manager.DisconnectAsync();

        Assert.Equal(OverallConnectionState.Idle, _manager.CurrentSnapshot.OverallState);
        Assert.Null(_manager.OperatorClient);
    }

    [Fact]
    public async Task SwitchGatewayAsync_DisconnectsAndReconnects()
    {
        SetupGateway("gw-1", "wss://test1");
        SetupGateway("gw-2", "wss://test2");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");
        await _manager.SwitchGatewayAsync("gw-2");

        Assert.Equal("gw-2", _manager.CurrentSnapshot.GatewayId);
        Assert.Equal("wss://test2", _manager.ActiveGatewayUrl);
    }

    [Fact]
    public async Task StateChanged_Fires_OnConnect()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        var snapshots = new List<GatewayConnectionSnapshot>();
        _manager.StateChanged += (_, s) => snapshots.Add(s);

        await _manager.ConnectAsync("gw-1");

        Assert.NotEmpty(snapshots);
        Assert.Contains(snapshots, s => s.OverallState == OverallConnectionState.Connecting);
    }

    [Fact]
    public async Task DiagnosticEvent_Fires_OnCredentialResolution()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test.source");

        var events = new List<ConnectionDiagnosticEvent>();
        _manager.DiagnosticEvent += (_, e) => events.Add(e);

        await _manager.ConnectAsync("gw-1");

        Assert.Contains(events, e => e.Category == "credential");
    }

    [Fact]
    public async Task Dispose_CleansUp()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");
        await _manager.ConnectAsync("gw-1");

        _manager.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _manager.ConnectAsync("gw-1").GetAwaiter().GetResult());
    }

    [Fact]
    public void Diagnostics_IsAccessible()
    {
        Assert.NotNull(_manager.Diagnostics);
        Assert.Equal(0, _manager.Diagnostics.Count);
    }

    [Fact]
    public async Task HandshakeSucceeded_SuppressesManagerNodeConnector_WhenLocalNodeServiceOwnsIdentity()
    {
        SetupGateway("gw-local", "ws://localhost:18789", isLocal: true);
        _resolver.OperatorCredential = new GatewayCredential("op-tok", false, "test");
        _resolver.NodeCredential = new GatewayCredential("node-tok", false, "test");
        var nodeConnector = new CountingNodeConnector();
        using var manager = new GatewayConnectionManager(
            _resolver, _factory, _registry, NullLogger.Instance,
            nodeConnector: nodeConnector,
            shouldStartNodeConnection: (record, _) => !record.IsLocal);

        await manager.ConnectAsync("gw-local");
        await InvokeHandshakeSucceededAsync(manager);

        Assert.Equal(0, nodeConnector.ConnectCount);
    }

    [Fact]
    public async Task HandshakeSucceeded_StartsManagerNodeConnector_WhenNoLocalNodeServiceOwnsIdentity()
    {
        SetupGateway("gw-remote", "wss://remote.example", isLocal: false);
        _resolver.OperatorCredential = new GatewayCredential("op-tok", false, "test");
        _resolver.NodeCredential = new GatewayCredential("node-tok", false, "test");
        var nodeConnector = new CountingNodeConnector();
        using var manager = new GatewayConnectionManager(
            _resolver, _factory, _registry, NullLogger.Instance,
            nodeConnector: nodeConnector,
            shouldStartNodeConnection: (record, _) => !record.IsLocal);

        await manager.ConnectAsync("gw-remote");
        await InvokeHandshakeSucceededAsync(manager);

        Assert.Equal(1, nodeConnector.ConnectCount);
        Assert.Equal("wss://remote.example", nodeConnector.LastGatewayUrl);
    }

    [Fact]
    public async Task ChatPageNavigationReadiness_DoesNotCompleteUntilHandshakeSucceeded()
    {
        SetupGateway("gw-chat", "ws://localhost:18789", isLocal: true);
        _resolver.OperatorCredential = new GatewayCredential("op-tok", false, "test");

        await _manager.ConnectAsync("gw-chat");

        var readiness = ChatNavigationReadiness.WaitForOperatorHandshakeAsync(
            _manager,
            TimeSpan.FromSeconds(5));

        Assert.False(readiness.IsCompleted);

        await InvokeHandshakeSucceededAsync(_manager);

        Assert.True(await readiness);
    }

    // ─── Helpers ───

    private void SetupGateway(string id, string url, bool isLocal = false)
    {
        _registry.AddOrUpdate(new GatewayRecord { Id = id, Url = url, IsLocal = isLocal });
        _registry.SetActive(id);
    }

    private static async Task InvokeHandshakeSucceededAsync(GatewayConnectionManager manager)
    {
        var method = typeof(GatewayConnectionManager).GetMethod(
            "HandleHandshakeSucceededAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(manager, [1L])!;
        await task;
    }

    // ─── Mocks ───

    private sealed class MockCredentialResolver : ICredentialResolver
    {
        public GatewayCredential? OperatorCredential { get; set; }
        public GatewayCredential? NodeCredential { get; set; }

        public GatewayCredential? ResolveOperator(GatewayRecord record, string identityPath) => OperatorCredential;
        public GatewayCredential? ResolveNode(GatewayRecord record, string identityPath) => NodeCredential;
    }

    private sealed class MockClientFactory : IGatewayClientFactory
    {
        public List<MockLifecycle> CreatedClients { get; } = [];

        public IGatewayClientLifecycle Create(string gatewayUrl, GatewayCredential credential, string identityPath, IOpenClawLogger logger)
        {
            var mock = new MockLifecycle(gatewayUrl);
            CreatedClients.Add(mock);
            return mock;
        }
    }

    internal sealed class MockLifecycle : IGatewayClientLifecycle
    {
        private readonly MockGatewayClient _client;

        public MockLifecycle(string url)
        {
            _client = new MockGatewayClient(url);
        }

        public OpenClawGatewayClient DataClient => _client;
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<string>? AuthenticationFailed;

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

        public void SimulateStatusChanged(ConnectionStatus status) =>
            StatusChanged?.Invoke(this, status);

        public void SimulateAuthFailed(string msg) =>
            AuthenticationFailed?.Invoke(this, msg);

        public void SimulateHandshake() =>
            _client.SimulateHandshakeSucceeded();

        public void Dispose() { }
    }

    private sealed class MockGatewayClient : OpenClawGatewayClient
    {
        public MockGatewayClient(string url)
            : base(url, "mock-token", NullLogger.Instance) { }

        /// <summary>Simulate a successful hello-ok handshake for testing.</summary>
        public void SimulateHandshakeSucceeded()
        {
            // Fire the HandshakeSucceeded event to trigger the manager's handler
            OnHandshakeSucceeded();
        }

        // Protected invoker — OpenClawGatewayClient.HandshakeSucceeded is a public event.
        // We use reflection because the event doesn't have a virtual invoker.
        private void OnHandshakeSucceeded()
        {
            var field = typeof(OpenClawGatewayClient).GetField(
                nameof(HandshakeSucceeded),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            // Events compiled as backing fields in C# are named the same as the event.
            // In case the compiler generates a different name, fall back to raising through the base.
            if (field != null)
            {
                var handler = field.GetValue(this) as EventHandler;
                handler?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    [Fact]
    public async Task HandshakeSucceeded_StampsLastConnectedOnGatewayRecord()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");

        // Simulate successful handshake
        var lifecycle = _factory.CreatedClients[0];
        lifecycle.SimulateHandshake();

        // Allow async handler to complete
        await Task.Delay(100);

        var record = _registry.GetById("gw-1");
        Assert.NotNull(record?.LastConnected);
    }

    [Fact]
    public async Task HandshakeSucceeded_PreservesOtherRecordFields()
    {
        _registry.AddOrUpdate(new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://test",
            SharedGatewayToken = "shared-tok",
            FriendlyName = "TestGW"
        });
        _registry.SetActive("gw-1");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");

        var lifecycle = _factory.CreatedClients[0];
        lifecycle.SimulateHandshake();
        await Task.Delay(100);

        var record = _registry.GetById("gw-1")!;
        Assert.True(record.LastConnected.HasValue);
        Assert.Equal("shared-tok", record.SharedGatewayToken);
        Assert.Equal("TestGW", record.FriendlyName);
    }

    private sealed class CountingNodeConnector : INodeConnector
    {
        public int ConnectCount { get; private set; }
        public string? LastGatewayUrl { get; private set; }
        public bool IsConnected => ConnectCount > 0;
        public PairingStatus PairingStatus { get; private set; } = PairingStatus.Unknown;
        public string? NodeDeviceId => "test-node";
        public NodeConnectionMode Mode => IsConnected ? NodeConnectionMode.Gateway : NodeConnectionMode.Disabled;

#pragma warning disable CS0067 // Events required by interface but not fired in tests
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;
#pragma warning restore CS0067

        public Task ConnectAsync(string gatewayUrl, GatewayCredential credential, string identityPath, bool useV2Signature = false)
        {
            ConnectCount++;
            LastGatewayUrl = gatewayUrl;
            PairingStatus = PairingStatus.Paired;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync() => Task.CompletedTask;

        public void Dispose() { }
    }
}
