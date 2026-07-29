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

    [Fact]
    public async Task ExplicitDevicePairRequest_PendingDuringReconnect_QueuesOneBoundedRetry()
    {
        var firstDelayStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstDelay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayCount = 0;
        Task ReconnectDelay(TimeSpan _)
        {
            if (Interlocked.Increment(ref delayCount) == 1)
            {
                firstDelayStarted.SetResult(true);
                return releaseFirstDelay.Task;
            }

            return Task.CompletedTask;
        }

        using var manager = CreateConnectedManager(ReconnectDelay);
        var client = GetConnectedClient(["operator.admin"]);
        var connectCountBeforeApproval = _nodeConnector.ConnectCount;

        var approvalDone = client.WaitForApprovalCallAsync();
        _nodeConnector.FirePairingStatusChanged(
            PairingStatus.Pending,
            requestId: "req-device-role-upgrade",
            approvalKind: PairingApprovalKind.DevicePair);
        await approvalDone;
        await firstDelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await FireAndWait(manager, () =>
            _nodeConnector.FirePairingStatusChanged(
                PairingStatus.Pending,
                requestId: "req-device-role-upgrade",
                approvalKind: PairingApprovalKind.DevicePair));
        await FireAndWait(manager, () =>
            _nodeConnector.FirePairingStatusChanged(
                PairingStatus.Pending,
                requestId: "req-device-role-upgrade",
                approvalKind: PairingApprovalKind.DevicePair));
        releaseFirstDelay.SetResult(true);

        await WaitUntilAsync(() => _nodeConnector.ConnectCount >= connectCountBeforeApproval + 2);
        await FireAndWait(manager, () =>
            _nodeConnector.FirePairingStatusChanged(
                PairingStatus.Pending,
                requestId: "req-device-role-upgrade",
                approvalKind: PairingApprovalKind.DevicePair));
        await Task.Delay(50);

        Assert.Equal(connectCountBeforeApproval + 2, _nodeConnector.ConnectCount);
        Assert.Equal(2, Volatile.Read(ref delayCount));
        Assert.Equal(["device.pair.approve"], client.ApprovalMethodsCalled);
    }

    [Fact]
    public async Task ExplicitDevicePairRequest_QueuedRetryConsumesSlotBeforeReconnect()
    {
        var firstDelayStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstDelay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDelayStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondDelay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayCount = 0;
        Task ReconnectDelay(TimeSpan _)
        {
            return Interlocked.Increment(ref delayCount) switch
            {
                1 => WaitForReleaseAsync(firstDelayStarted, releaseFirstDelay),
                2 => WaitForReleaseAsync(secondDelayStarted, releaseSecondDelay),
                _ => Task.CompletedTask,
            };
        }

        static async Task WaitForReleaseAsync(
            TaskCompletionSource<bool> started,
            TaskCompletionSource<bool> release)
        {
            started.SetResult(true);
            await release.Task;
        }

        using var manager = CreateConnectedManager(ReconnectDelay);
        var client = GetConnectedClient(["operator.admin"]);
        var connectCountBeforeApproval = _nodeConnector.ConnectCount;

        _nodeConnector.FirePairingStatusChanged(
            PairingStatus.Pending,
            requestId: "req-first",
            approvalKind: PairingApprovalKind.DevicePair);
        await firstDelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        _nodeConnector.FirePairingStatusChanged(
            PairingStatus.Pending,
            requestId: "req-second",
            approvalKind: PairingApprovalKind.DevicePair);
        await WaitUntilAsync(() => manager.Diagnostics.GetAll().Count(
            diagnostic => diagnostic.Message == "Device role-upgrade reconnect retry queued") == 1);
        releaseFirstDelay.SetResult(true);
        await secondDelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        _nodeConnector.FirePairingStatusChanged(
            PairingStatus.Pending,
            requestId: "req-third",
            approvalKind: PairingApprovalKind.DevicePair);
        await WaitUntilAsync(() => manager.Diagnostics.GetAll().Count(
            diagnostic => diagnostic.Message == "Device role-upgrade reconnect retry queued") == 2);
        releaseSecondDelay.SetResult(true);

        await WaitUntilAsync(() => _nodeConnector.ConnectCount >= connectCountBeforeApproval + 3);

        Assert.Equal(connectCountBeforeApproval + 3, _nodeConnector.ConnectCount);
        Assert.Equal(3, Volatile.Read(ref delayCount));
        Assert.Equal(
            ["device.pair.approve", "device.pair.approve", "device.pair.approve"],
            client.ApprovalMethodsCalled);
    }

    [Fact]
    public async Task ExplicitDevicePairRequest_DisconnectDuringReconnectDelay_DoesNotReviveNode()
    {
        var delayStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var manager = CreateConnectedManager(_ =>
        {
            delayStarted.SetResult(true);
            return releaseDelay.Task;
        });
        var client = GetConnectedClient(["operator.admin"]);
        var connectCountBeforeApproval = _nodeConnector.ConnectCount;

        var approvalDone = client.WaitForApprovalCallAsync();
        _nodeConnector.FirePairingStatusChanged(
            PairingStatus.Pending,
            requestId: "req-device-role-upgrade",
            approvalKind: PairingApprovalKind.DevicePair);
        await approvalDone;
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await manager.DisconnectAsync();
        releaseDelay.SetResult(true);
        await Task.Delay(50);

        Assert.Equal(connectCountBeforeApproval, _nodeConnector.ConnectCount);
    }

    [Fact]
    public async Task ExplicitDevicePairRequest_ManualReconnectDuringDelay_DoesNotSupersedeManualAttempt()
    {
        var delayStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var manager = CreateConnectedManager(_ =>
        {
            delayStarted.SetResult(true);
            return releaseDelay.Task;
        });
        var client = GetConnectedClient(["operator.admin"]);
        await MarkOperatorConnectedAsync(manager);
        var operatorClient = manager.OperatorClient;

        var approvalDone = client.WaitForApprovalCallAsync();
        _nodeConnector.FirePairingStatusChanged(
            PairingStatus.Pending,
            requestId: "req-device-role-upgrade",
            approvalKind: PairingApprovalKind.DevicePair);
        await approvalDone;
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await manager.ConnectNodeOnlyAsync();
        var connectCountAfterManualReconnect = _nodeConnector.ConnectCount;
        Assert.Same(operatorClient, manager.OperatorClient);
        releaseDelay.SetResult(true);
        await Task.Delay(50);

        Assert.Equal(connectCountAfterManualReconnect, _nodeConnector.ConnectCount);
    }

    [Fact]
    public async Task ExplicitDevicePairRequest_ApprovalCompletesAfterManualReconnect_DoesNotReconnectAgain()
    {
        using var manager = CreateConnectedManager();
        var client = GetConnectedClient(["operator.admin"]);
        await MarkOperatorConnectedAsync(manager);
        var operatorClient = manager.OperatorClient;
        var approvalStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApproval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DevicePairApproveOverride = _ =>
        {
            approvalStarted.SetResult(true);
            return releaseApproval.Task;
        };

        _nodeConnector.FirePairingStatusChanged(
            PairingStatus.Pending,
            requestId: "req-device-role-upgrade",
            approvalKind: PairingApprovalKind.DevicePair);
        await approvalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await manager.ConnectNodeOnlyAsync();
        var connectCountAfterManualReconnect = _nodeConnector.ConnectCount;
        Assert.Same(operatorClient, manager.OperatorClient);
        releaseApproval.SetResult(true);
        await Task.Delay(50);

        Assert.Equal(connectCountAfterManualReconnect, _nodeConnector.ConnectCount);
    }

    [Fact]
    public async Task ManualReconnect_RetiresOldNodeBeforePublishingReplacementAttempt()
    {
        using var manager = CreateConnectedManager();
        var client = GetConnectedClient(["operator.admin"]);
        await MarkOperatorConnectedAsync(manager);
        var operatorClient = manager.OperatorClient;
        _nodeConnector.EmitRetiredDevicePairingOnReplacementConnect = true;

        await manager.ConnectNodeOnlyAsync();
        await Task.Delay(50);

        Assert.Same(operatorClient, manager.OperatorClient);
        Assert.Empty(client.ApprovalMethodsCalled);
    }

    [Fact]
    public async Task ExplicitDevicePairRequest_SameRequestId_CanBeApprovedAgainAfterPairingCompletes()
    {
        using var manager = CreateConnectedManager();
        var client = GetConnectedClient(["operator.admin"]);
        var connectCountBeforeApproval = _nodeConnector.ConnectCount;

        var firstApprovalDone = client.WaitForApprovalCallAsync();
        _nodeConnector.FirePairingStatusChanged(
            PairingStatus.Pending,
            requestId: "reused-request",
            approvalKind: PairingApprovalKind.DevicePair);
        await firstApprovalDone;
        await WaitUntilAsync(() => _nodeConnector.ConnectCount > connectCountBeforeApproval);

        await FireAndWait(manager, () =>
            _nodeConnector.FirePairingStatusChanged(PairingStatus.Paired));

        var secondApprovalDone = client.WaitForApprovalCallAsync();
        _nodeConnector.FirePairingStatusChanged(
            PairingStatus.Pending,
            requestId: "reused-request",
            approvalKind: PairingApprovalKind.DevicePair);
        await secondApprovalDone;

        Assert.Equal(
            ["device.pair.approve", "device.pair.approve"],
            client.ApprovalMethodsCalled);
    }

    private TrackingGatewayClient GetConnectedClient(string[] scopes)
    {
        var lifecycle = _factory.CreatedClients[0];
        lifecycle.TrackingClient.SetGrantedScopes(scopes);
        lifecycle.TrackingClient.SetIsConnected(true);
        return lifecycle.TrackingClient;
    }

    private GatewayConnectionManager CreateConnectedManager(Func<TimeSpan, Task>? reconnectDelay = null)
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
            reconnectDelay: reconnectDelay ?? (_ => Task.CompletedTask));

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

    private static async Task MarkOperatorConnectedAsync(GatewayConnectionManager manager)
    {
        var generationField = typeof(GatewayConnectionManager).GetField(
            "_generation",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var handshakeMethod = typeof(GatewayConnectionManager).GetMethod(
            "HandleHandshakeSucceededAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(generationField);
        Assert.NotNull(handshakeMethod);

        var generation = Assert.IsType<long>(generationField!.GetValue(manager));
        await Assert.IsAssignableFrom<Task>(handshakeMethod!.Invoke(manager, [generation]));
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
            var mock = new TrackingLifecycle(gatewayUrl, identityPath);
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

        public TrackingLifecycle(string url, string identityPath)
        {
            TrackingClient = new TrackingGatewayClient(url, identityPath);
        }
    }

    internal sealed class TrackingGatewayClient : OpenClawGatewayClient
    {
        private readonly List<string> _approvalMethodsCalled = [];
        private bool _simulatedConnected;
        private TaskCompletionSource? _approvalSignal;

        public IReadOnlyList<string> ApprovalMethodsCalled => _approvalMethodsCalled;
        public Func<string, Task<bool>>? DevicePairApproveOverride { get; set; }

        public TrackingGatewayClient(string url, string identityPath)
            : base(url, "mock-token", NullLogger.Instance, identityPath: identityPath) { }

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
            return DevicePairApproveOverride?.Invoke(requestId) ?? Task.FromResult(true);
        }
    }

    private sealed class ScriptedNodeConnector : INodeConnector
    {
        public bool IsConnected { get; private set; }
        public int ConnectCount { get; private set; }
        public int ConnectFailuresRemaining { get; set; }
        public bool EmitRetiredDevicePairingOnReplacementConnect { get; set; }
        public PairingStatus PairingStatus { get; set; } = PairingStatus.Unknown;
        public string? NodeDeviceId { get; set; }
        public NodeConnectionMode Mode { get; set; } = NodeConnectionMode.Disabled;
        private bool _currentClientRetired = true;

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
            if (EmitRetiredDevicePairingOnReplacementConnect && !_currentClientRetired)
            {
                PairingStatusChanged?.Invoke(this, new PairingStatusEventArgs(
                    PairingStatus.Pending,
                    NodeDeviceId ?? "test-node",
                    requestId: "retired-device-pair",
                    approvalKind: PairingApprovalKind.DevicePair));
            }

            ConnectCount++;
            _currentClientRetired = false;
            Mode = NodeConnectionMode.Gateway;
            if (ConnectFailuresRemaining > 0)
            {
                ConnectFailuresRemaining--;
                throw new InvalidOperationException("transient node reconnect failure");
            }
            return Task.CompletedTask;
        }

        public Task ConnectAsync(
            string gatewayUrl,
            GatewayCredential credential,
            string identityPath,
            bool useV2Signature,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ConnectAsync(gatewayUrl, credential, identityPath, useV2Signature);
        }

        public Task DisconnectAsync()
        {
            _currentClientRetired = true;
            return Task.CompletedTask;
        }

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
