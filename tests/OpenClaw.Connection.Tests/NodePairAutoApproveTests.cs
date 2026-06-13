using OpenClaw.Shared;
using OpenClaw.Connection;

namespace OpenClaw.Connection.Tests;

/// <summary>
/// Regression tests for the pairing approval trust boundary in
/// <see cref="GatewayConnectionManager"/>.
///
/// Explicitly typed device-pair role upgrades may auto-approve during
/// bootstrap. Gateway-owned node-pair command trust, including reapproval,
/// must remain pending for an explicit operator decision.
/// </summary>
public class NodePairAutoApproveTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GatewayRegistry _registry;
    private readonly MockCredentialResolver _resolver;
    private readonly TrackingClientFactory _factory;
    private readonly ScriptedNodeConnector _nodeConnector;

    public NodePairAutoApproveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openclaw-autoapprove-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _registry = new GatewayRegistry(_tempDir);
        _resolver = new MockCredentialResolver();
        _factory = new TrackingClientFactory();
        _nodeConnector = new ScriptedNodeConnector();
    }

    public void Dispose()
    {
        _nodeConnector.Dispose();
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task NodePairRequest_WithAdminScope_RemainsPendingForManualApproval()
    {
        using var manager = CreateConnectedManager();
        var client = GetConnectedClient(["operator.admin", "operator.pairing"]);

        var snapshot = await FireAndWait(manager, () =>
            _nodeConnector.FirePairingStatusChanged(
                PairingStatus.Pending,
                requestId: "req-node-command-trust",
                approvalKind: PairingApprovalKind.NodePair));

        Assert.Equal("req-node-command-trust", snapshot.NodePairingRequestId);
        Assert.Equal(PairingApprovalKind.NodePair, snapshot.NodePairingApprovalKind);
        Assert.Empty(client.ApprovalMethodsCalled);
    }

    [Fact]
    public async Task UnknownPairingRequest_WithAdminScope_RemainsPendingForManualApproval()
    {
        using var manager = CreateConnectedManager();
        var client = GetConnectedClient(["operator.admin", "operator.pairing"]);

        var snapshot = await FireAndWait(manager, () =>
            _nodeConnector.FirePairingStatusChanged(
                PairingStatus.Pending,
                requestId: "req-unknown-kind",
                approvalKind: PairingApprovalKind.Unknown));

        Assert.Equal("req-unknown-kind", snapshot.NodePairingRequestId);
        Assert.Equal(PairingApprovalKind.Unknown, snapshot.NodePairingApprovalKind);
        Assert.Empty(client.ApprovalMethodsCalled);
    }

    [Fact]
    public async Task NodePairListUpdate_ForLocalNodeReapproval_DoesNotAutoApprove()
    {
        _nodeConnector.NodeDeviceId = "local-node";
        using var manager = CreateConnectedManager();
        var client = GetConnectedClient(["operator.admin", "operator.pairing"]);

        client.SimulateNodePairListUpdated(new PairingRequest
        {
            RequestId = "req-local-reapproval",
            NodeId = "local-node"
        });

        await Task.Delay(50);

        Assert.Empty(client.ApprovalMethodsCalled);
    }

    [Fact]
    public async Task ExplicitDevicePairRequest_AutoApprovesWithDeviceMethodOnly()
    {
        using var manager = CreateConnectedManager();
        var client = GetConnectedClient(["operator.admin", "operator.pairing"]);

        var approvalDone = client.WaitForApprovalCallAsync();
        _nodeConnector.FirePairingStatusChanged(
            PairingStatus.Pending,
            requestId: "req-device-role-upgrade",
            approvalKind: PairingApprovalKind.DevicePair);
        await approvalDone;

        Assert.Equal(["device.pair.approve"], client.ApprovalMethodsCalled);
    }

    [Fact]
    public async Task ExplicitDevicePairRequest_WithoutAdminScope_DoesNotAutoApprove()
    {
        using var manager = CreateConnectedManager();
        var client = GetConnectedClient(["operator.pairing"]);

        await FireAndWait(manager, () =>
            _nodeConnector.FirePairingStatusChanged(
                PairingStatus.Pending,
                requestId: "req-device-role-upgrade",
                approvalKind: PairingApprovalKind.DevicePair));

        await Task.Delay(50);

        Assert.Empty(client.ApprovalMethodsCalled);
    }

    [Fact]
    public async Task ExplicitDevicePairRequest_SameRequestId_RetriesReconnectWithoutReapproving()
    {
        using var manager = CreateConnectedManager();
        var client = GetConnectedClient(["operator.admin"]);
        var connectCountBeforeApproval = _nodeConnector.ConnectCount;
        _nodeConnector.ConnectFailuresRemaining = 1;

        var approvalDone = client.WaitForApprovalCallAsync();
        _nodeConnector.FirePairingStatusChanged(
            PairingStatus.Pending,
            requestId: "req-device-role-upgrade",
            approvalKind: PairingApprovalKind.DevicePair);
        await approvalDone;
        await WaitUntilAsync(() => _nodeConnector.ConnectCount > connectCountBeforeApproval);
        var connectCountAfterFailedReconnect = _nodeConnector.ConnectCount;

        _nodeConnector.FirePairingStatusChanged(
            PairingStatus.Pending,
            requestId: "req-device-role-upgrade",
            approvalKind: PairingApprovalKind.DevicePair);
        await WaitUntilAsync(() => _nodeConnector.ConnectCount > connectCountAfterFailedReconnect);

        Assert.Equal(["device.pair.approve"], client.ApprovalMethodsCalled);
    }

    private TrackingGatewayClient GetConnectedClient(string[] scopes)
    {
        var lifecycle = _factory.CreatedClients[0];
        lifecycle.TrackingClient.SetGrantedScopes(scopes);
        lifecycle.TrackingClient.SetIsConnected(true);
        return lifecycle.TrackingClient;
    }

    private GatewayConnectionManager CreateConnectedManager()
    {
        _registry.AddOrUpdate(new GatewayRecord { Id = "gw1", Url = "wss://test" });
        _registry.SetActive("gw1");
        Directory.CreateDirectory(_registry.GetIdentityDirectory("gw1"));

        _resolver.OperatorCredential = new GatewayCredential("op-tok", false, "test");
        _resolver.NodeCredential = new GatewayCredential("node-tok", false, "test");

        var manager = new GatewayConnectionManager(
            _resolver, _factory, _registry, NullLogger.Instance,
            nodeConnector: _nodeConnector,
            isNodeEnabled: () => true,
            reconnectDelay: _ => Task.CompletedTask);

        manager.ConnectAsync("gw1").GetAwaiter().GetResult();
        return manager;
    }

    private static async Task<GatewayConnectionSnapshot> FireAndWait(
        GatewayConnectionManager manager, Action action, int timeoutMs = 5000)
    {
        var tcs = new TaskCompletionSource<GatewayConnectionSnapshot>();
        void Handler(object? _, GatewayConnectionSnapshot snapshot)
        {
            manager.StateChanged -= Handler;
            tcs.TrySetResult(snapshot);
        }

        manager.StateChanged += Handler;
        action();
        return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }

        Assert.True(condition(), "Condition was not met before timeout.");
    }

    private sealed class MockCredentialResolver : ICredentialResolver
    {
        public GatewayCredential? OperatorCredential { get; set; }
        public GatewayCredential? NodeCredential { get; set; }
        public GatewayCredential? ResolveOperator(GatewayRecord record, string identityPath) => OperatorCredential;
        public GatewayCredential? ResolveNode(GatewayRecord record, string identityPath) => NodeCredential;
    }

    private sealed class TrackingClientFactory : IGatewayClientFactory
    {
        public List<TrackingLifecycle> CreatedClients { get; } = [];

        public IGatewayClientLifecycle Create(
            string gatewayUrl,
            GatewayCredential credential,
            string identityPath,
            IOpenClawLogger logger)
        {
            var mock = new TrackingLifecycle(gatewayUrl);
            CreatedClients.Add(mock);
            return mock;
        }
    }

    internal sealed class TrackingLifecycle : IGatewayClientLifecycle
    {
        public TrackingGatewayClient TrackingClient { get; }
        public OpenClawGatewayClient DataClient => TrackingClient;
#pragma warning disable CS0067 // Events required by interface but not fired in tests
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<string>? AuthenticationFailed;
#pragma warning restore CS0067
        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }

        public TrackingLifecycle(string url)
        {
            TrackingClient = new TrackingGatewayClient(url);
        }
    }

    internal sealed class TrackingGatewayClient : OpenClawGatewayClient
    {
        private readonly List<string> _approvalMethodsCalled = [];
        private bool _simulatedConnected;
        private TaskCompletionSource? _approvalSignal;

        public IReadOnlyList<string> ApprovalMethodsCalled => _approvalMethodsCalled;

        public TrackingGatewayClient(string url)
            : base(url, "mock-token", NullLogger.Instance) { }

        public void SetGrantedScopes(string[] scopes)
        {
            var field = typeof(OpenClawGatewayClient).GetField(
                "_grantedOperatorScopes",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(this, scopes);
        }

        public void SetIsConnected(bool connected)
        {
            _simulatedConnected = connected;
        }

        public void SimulateNodePairListUpdated(params PairingRequest[] pending)
        {
            var field = typeof(OpenClawGatewayClient).GetField(
                nameof(NodePairListUpdated),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            var handler = field!.GetValue(this) as EventHandler<PairingListInfo>;
            handler?.Invoke(this, new PairingListInfo { Pending = pending.ToList() });
        }

        public Task WaitForApprovalCallAsync(int timeoutMs = 5000)
        {
            _approvalSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _approvalSignal.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }

        public override bool IsConnectedToGateway => _simulatedConnected;

        public override Task<bool> NodePairApproveAsync(string requestId)
        {
            _approvalMethodsCalled.Add("node.pair.approve");
            _approvalSignal?.TrySetResult();
            return Task.FromResult(true);
        }

        public override Task<bool> DevicePairApproveAsync(string requestId)
        {
            _approvalMethodsCalled.Add("device.pair.approve");
            _approvalSignal?.TrySetResult();
            return Task.FromResult(true);
        }
    }

    private sealed class ScriptedNodeConnector : INodeConnector
    {
        public bool IsConnected { get; private set; }
        public int ConnectCount { get; private set; }
        public int ConnectFailuresRemaining { get; set; }
        public PairingStatus PairingStatus { get; set; } = PairingStatus.Unknown;
        public string? NodeDeviceId { get; set; }
        public NodeConnectionMode Mode { get; set; } = NodeConnectionMode.Disabled;

        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;
#pragma warning disable CS0067
        public event EventHandler<DeviceTokenReceivedEventArgs>? DeviceTokenReceived;
        public event EventHandler<NodeClientCreatedEventArgs>? ClientCreated;
#pragma warning restore CS0067

        public Task ConnectAsync(
            string gatewayUrl,
            GatewayCredential credential,
            string identityPath,
            bool useV2Signature = false)
        {
            ConnectCount++;
            Mode = NodeConnectionMode.Gateway;
            if (ConnectFailuresRemaining > 0)
            {
                ConnectFailuresRemaining--;
                throw new InvalidOperationException("transient node reconnect failure");
            }
            return Task.CompletedTask;
        }

        public Task DisconnectAsync() => Task.CompletedTask;

        public void FireStatusChanged(ConnectionStatus status)
        {
            IsConnected = status == ConnectionStatus.Connected;
            StatusChanged?.Invoke(this, status);
        }

        public void FirePairingStatusChanged(
            PairingStatus status,
            string? requestId = null,
            PairingApprovalKind approvalKind = PairingApprovalKind.Unknown)
        {
            PairingStatus = status;
            PairingStatusChanged?.Invoke(this, new PairingStatusEventArgs(
                status,
                NodeDeviceId ?? "test-node",
                requestId: requestId,
                approvalKind: approvalKind));
        }

        public void Dispose() { }
    }
}
