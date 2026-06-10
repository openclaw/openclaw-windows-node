using OpenClaw.Shared;
using OpenClaw.Connection;

namespace OpenClaw.Connection.Tests;

/// <summary>
/// Regression tests for the node command-upgrade auto-approval path in
/// <see cref="GatewayConnectionManager"/>.
///
/// Bug: when a node's PairingStatusChanged fires with Pending + RequestId,
/// the manager must call <c>NodePairApproveAsync</c> (node.pair.approve).
/// A previous version incorrectly called <c>DevicePairApproveAsync</c>
/// (device.pair.approve), which targets a completely separate pairing
/// system and silently fails, leaving the node with 0 effective commands.
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
    public async Task AutoApprove_CallsNodePairApprove_NotDevicePairApprove()
    {
        using var manager = CreateConnectedManager();

        var lifecycle = _factory.CreatedClients[0];
        lifecycle.TrackingClient.SetGrantedScopes(["operator.admin"]);
        lifecycle.TrackingClient.SetIsConnected(true);

        await FireAndWait(manager, () =>
            _nodeConnector.FireStatusChanged(ConnectionStatus.Connecting));

        // Set up signal before firing the event
        var approvalDone = lifecycle.TrackingClient.WaitForApprovalCallAsync();
        _nodeConnector.FirePairingStatusChanged(PairingStatus.Pending, requestId: "req-node-cmd-upgrade-42");
        await approvalDone;

        var client = lifecycle.TrackingClient;
        Assert.Contains("node.pair.approve", client.ApprovalMethodsCalled);
        Assert.DoesNotContain("device.pair.approve", client.ApprovalMethodsCalled);
    }

    [Fact]
    public async Task AutoApprove_WithoutRequestId_DoesNotAttemptApproval()
    {
        using var manager = CreateConnectedManager();

        var lifecycle = _factory.CreatedClients[0];
        lifecycle.TrackingClient.SetGrantedScopes(["operator.admin"]);
        lifecycle.TrackingClient.SetIsConnected(true);

        await FireAndWait(manager, () =>
            _nodeConnector.FireStatusChanged(ConnectionStatus.Connecting));

        // Pending WITHOUT a requestId — auto-approval should not trigger.
        // The handler returns synchronously in this path (no await before early return),
        // so no delay is needed: the assertion is safe immediately after the fire.
        _nodeConnector.FirePairingStatusChanged(PairingStatus.Pending, requestId: null);

        Assert.Empty(lifecycle.TrackingClient.ApprovalMethodsCalled);
    }

    [Fact]
    public async Task AutoApprove_WithoutAdminScope_DoesNotAttemptApproval()
    {
        using var manager = CreateConnectedManager();

        var lifecycle = _factory.CreatedClients[0];
        lifecycle.TrackingClient.SetGrantedScopes(["operator.read"]);
        lifecycle.TrackingClient.SetIsConnected(true);

        await FireAndWait(manager, () =>
            _nodeConnector.FireStatusChanged(ConnectionStatus.Connecting));

        // Insufficient scope — auto-approval should not trigger.
        // The scope check runs synchronously before any await, so no delay is needed.
        _nodeConnector.FirePairingStatusChanged(PairingStatus.Pending, requestId: "req-123");

        Assert.Empty(lifecycle.TrackingClient.ApprovalMethodsCalled);
    }

    [Fact]
    public async Task AutoApprove_WhenNodePairApproveRejects_FallsBackToDevicePairApproveWithAdminScope()
    {
        using var manager = CreateConnectedManager();

        var lifecycle = _factory.CreatedClients[0];
        lifecycle.TrackingClient.SetGrantedScopes(["operator.admin", "operator.pairing"]);
        lifecycle.TrackingClient.SetIsConnected(true);
        lifecycle.TrackingClient.NodePairApproveResult = false;

        await FireAndWait(manager, () =>
            _nodeConnector.FireStatusChanged(ConnectionStatus.Connecting));

        var approvalDone = lifecycle.TrackingClient.WaitForApprovalCallCountAsync(2);
        _nodeConnector.FirePairingStatusChanged(PairingStatus.Pending, requestId: "req-device-role-upgrade");
        await approvalDone;

        var client = lifecycle.TrackingClient;
        Assert.Equal(["node.pair.approve", "device.pair.approve"], client.ApprovalMethodsCalled);
    }

    [Fact]
    public async Task AutoApprove_WhenNodePairApproveRejects_WithoutAdminScope_DoesNotFallbackToDevicePairApprove()
    {
        using var manager = CreateConnectedManager();

        var lifecycle = _factory.CreatedClients[0];
        lifecycle.TrackingClient.SetGrantedScopes(["operator.pairing"]);
        lifecycle.TrackingClient.SetIsConnected(true);
        lifecycle.TrackingClient.NodePairApproveResult = false;

        await FireAndWait(manager, () =>
            _nodeConnector.FireStatusChanged(ConnectionStatus.Connecting));

        var approvalDone = lifecycle.TrackingClient.WaitForApprovalCallAsync();
        _nodeConnector.FirePairingStatusChanged(PairingStatus.Pending, requestId: "req-device-role-upgrade");
        await approvalDone;

        var client = lifecycle.TrackingClient;
        Assert.Equal(["node.pair.approve"], client.ApprovalMethodsCalled);
    }

    [Fact]
    public async Task AutoApprove_SameRequestId_DoesNotApproveTwice()
    {
        using var manager = CreateConnectedManager();

        var lifecycle = _factory.CreatedClients[0];
        lifecycle.TrackingClient.SetGrantedScopes(["operator.admin"]);
        lifecycle.TrackingClient.SetIsConnected(true);

        await FireAndWait(manager, () =>
            _nodeConnector.FireStatusChanged(ConnectionStatus.Connecting));

        // Block the approve call so it stays in-flight
        lifecycle.TrackingClient.BlockApproveUntilReleased();

        // Set up signal to know when first approve is entered
        var firstEntered = lifecycle.TrackingClient.WaitForApprovalCallAsync();
        _nodeConnector.FirePairingStatusChanged(PairingStatus.Pending, requestId: "req-same");
        await firstEntered; // First call has entered NodePairApproveAsync but is blocked

        // Fire second event while first is still in-flight — CAS guard rejects it synchronously
        // (the guard path has no await), so no delay is needed after this fire.
        _nodeConnector.FirePairingStatusChanged(PairingStatus.Pending, requestId: "req-same");

        // Release the blocked first approval and wait for it to complete deterministically.
        var firstComplete = lifecycle.TrackingClient.WaitForApprovalCompleteAsync();
        lifecycle.TrackingClient.ReleaseApproveGate();
        await firstComplete;

        // Only one approval call should have been made
        Assert.Equal(1, lifecycle.TrackingClient.ApprovalMethodsCalled
            .Count(m => m == "node.pair.approve"));
    }

    // ─── Helpers ───

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
        void Handler(object? _, GatewayConnectionSnapshot s)
        {
            manager.StateChanged -= Handler;
            tcs.TrySetResult(s);
        }
        manager.StateChanged += Handler;
        action();
        return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    // ─── Mocks ───

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

        public IGatewayClientLifecycle Create(string gatewayUrl, GatewayCredential credential, string identityPath, IOpenClawLogger logger)
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

    /// <summary>
    /// Extends OpenClawGatewayClient so it can be returned as DataClient.
    /// Overrides the virtual approve methods to track which approval API
    /// the manager actually calls.
    /// </summary>
    internal sealed class TrackingGatewayClient : OpenClawGatewayClient
    {
        private readonly List<string> _approvalMethodsCalled = [];
        private bool _simulatedConnected;
        private TaskCompletionSource? _approvalSignal;
        private TaskCompletionSource? _approvalCompleteSignal;
        private TaskCompletionSource? _approveGate; // blocks NodePairApproveAsync until released
        private int _approvalCallCount;
        private int _approvalCallTarget = 1;

        public IReadOnlyList<string> ApprovalMethodsCalled => _approvalMethodsCalled;
        public bool NodePairApproveResult { get; set; } = true;

        public TrackingGatewayClient(string url)
            : base(url, "mock-token", NullLogger.Instance) { }

        public void SetGrantedScopes(string[] scopes)
        {
            var field = typeof(OpenClawGatewayClient).GetField(
                "_grantedOperatorScopes",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field); // Fail loudly if the field is renamed
            field!.SetValue(this, scopes);
        }

        public void SetIsConnected(bool connected)
        {
            _simulatedConnected = connected;
        }

        /// <summary>
        /// Returns a task that completes when the next approval method is called.
        /// Use instead of Task.Delay for deterministic test synchronization.
        /// </summary>
        public Task WaitForApprovalCallAsync(int timeoutMs = 5000)
        {
            _approvalSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _approvalSignal.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }

        public Task WaitForApprovalCallCountAsync(int count, int timeoutMs = 5000)
        {
            _approvalCallTarget = count;
            _approvalSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_approvalCallCount >= count)
                _approvalSignal.TrySetResult();
            return _approvalSignal.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }

        /// <summary>
        /// Makes NodePairApproveAsync block until <see cref="ReleaseApproveGate"/> is called.
        /// Used to keep an approval in-flight so concurrent calls can be tested.
        /// </summary>
        public void BlockApproveUntilReleased()
        {
            _approveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>Releases a blocked NodePairApproveAsync call.</summary>
        public void ReleaseApproveGate()
        {
            _approveGate?.TrySetResult();
        }

        /// <summary>
        /// Returns a task that completes when the next NodePairApproveAsync call has returned.
        /// Call this before <see cref="ReleaseApproveGate"/> to set up the signal, then
        /// release the gate, then await the returned task for deterministic synchronization.
        /// </summary>
        public Task WaitForApprovalCompleteAsync(int timeoutMs = 5000)
        {
            _approvalCompleteSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _approvalCompleteSignal.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }

        public override bool IsConnectedToGateway => _simulatedConnected;

        public override async Task<bool> NodePairApproveAsync(string requestId)
        {
            _approvalMethodsCalled.Add("node.pair.approve");
            SignalApprovalCall();
            if (_approveGate != null)
                await _approveGate.Task;
            _approvalCompleteSignal?.TrySetResult();
            return NodePairApproveResult;
        }

        public override Task<bool> DevicePairApproveAsync(string requestId)
        {
            _approvalMethodsCalled.Add("device.pair.approve");
            SignalApprovalCall();
            return Task.FromResult(true);
        }

        private void SignalApprovalCall()
        {
            var count = Interlocked.Increment(ref _approvalCallCount);
            if (count >= _approvalCallTarget)
                _approvalSignal?.TrySetResult();
        }

    }

    private sealed class ScriptedNodeConnector : INodeConnector
    {
        public bool IsConnected { get; private set; }
        public PairingStatus PairingStatus { get; set; } = PairingStatus.Unknown;
        public string? NodeDeviceId { get; set; }
        public NodeConnectionMode Mode { get; set; } = NodeConnectionMode.Disabled;

        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;
#pragma warning disable CS0067
        public event EventHandler<DeviceTokenReceivedEventArgs>? DeviceTokenReceived;
        public event EventHandler<NodeClientCreatedEventArgs>? ClientCreated;
#pragma warning restore CS0067

        public Task ConnectAsync(string gatewayUrl, GatewayCredential credential,
            string identityPath, bool useV2Signature = false)
        {
            Mode = NodeConnectionMode.Gateway;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync() => Task.CompletedTask;

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
