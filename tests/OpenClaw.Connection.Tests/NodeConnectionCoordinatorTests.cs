using System.Diagnostics.Metrics;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

public sealed class NodeConnectionCoordinatorTests
{
    [Fact]
    public async Task SupersededGeneration_DoesNotWriteSnapshot()
    {
        using var temp = new TempDirectory("openclaw-node-owner-");
        using var meter = new Meter("openclaw-node-owner-tests");
        var registry = new GatewayRegistry(temp.Path);
        var attemptLeases = new AlwaysCurrentAttemptLeaseSource();
        var security = new AllowEndpointCredentialSecurity();
        var bootstrap = new BootstrapTokenLifecycle(
            registry,
            identityStore: null,
            attemptLeases,
            security,
            new RecordingReconnectScheduler(),
            new RecordingV2SignatureSink(),
            NullLogger.Instance,
            new ConnectionDiagnostics());
        var lifecycle = new FakeNodeLifecycleSource();
        var state = new RecordingNodeStateSink();
        var coordinator = new NodeConnectionCoordinator(
            lifecycle,
            state,
            state,
            attemptLeases,
            security,
            bootstrap,
            new NullCredentialResolver(),
            nodeConnector: null,
            NullLogger.Instance,
            new ConnectionDiagnostics(),
            _ => Task.CompletedTask,
            meter.CreateCounter<long>("attempts"),
            meter.CreateHistogram<double>("duration"));
        var stale = coordinator.CaptureCurrentAttempt();

        await coordinator.RetireAsync();
        var published = await coordinator.PublishPairingStatusAsync(
            new PairingStatusEventArgs(
                PairingStatus.Pending,
                "node-1",
                requestId: "request-1",
                approvalKind: PairingApprovalKind.DevicePair),
            stale);

        Assert.False(published);
        Assert.Equal(0, state.PairingWrites);
        await coordinator.StopAsync();
    }

    [Fact]
    public void TelemetryContract_UsesExistingNames()
    {
        Assert.Equal(
            "openclaw.connection.node.connect",
            NodeConnectionCoordinator.NodeConnectSpanName);
        Assert.Equal(
            "openclaw.connection.node.reconnect",
            NodeConnectionCoordinator.NodeReconnectSpanName);
        Assert.Equal(
            "openclaw.connection.node.prepare",
            NodeConnectionCoordinator.NodePrepareSpanName);
        Assert.Equal(
            "openclaw.connection.node.transport",
            NodeConnectionCoordinator.NodeTransportSpanName);
        Assert.Equal(
            "openclaw.connection.node.handshake",
            NodeConnectionCoordinator.NodeHandshakeSpanName);
        Assert.Equal(
            "openclaw.connection.attempts",
            GatewayConnectionManager.AttemptsMetricName);
        Assert.Equal(
            "openclaw.connection.attempt.duration",
            GatewayConnectionManager.AttemptDurationMetricName);
    }

    [Fact]
    public async Task FailedExplicitStart_ReleasesAutomaticStartGuard()
    {
        using var temp = new TempDirectory("openclaw-node-guard-");
        using var meter = new Meter("openclaw-node-guard-tests");
        var registry = new GatewayRegistry(temp.Path);
        var record = new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://gateway.example",
            SharedGatewayToken = "shared-token"
        };
        registry.AddOrUpdate(record);
        var gatewayAttempt = new GatewayAttemptStamp(1, record.Id);
        var lifecycle = new FakeNodeLifecycleSource(
            new NodeConnectionTarget(
                gatewayAttempt,
                record,
                registry.GetIdentityDirectory(record.Id),
                UseV2Signature: false),
            shouldStart: true);
        var state = new RecordingNodeStateSink();
        var attemptLeases = new AlwaysCurrentAttemptLeaseSource();
        var security = new AllowEndpointCredentialSecurity();
        var coordinator = new NodeConnectionCoordinator(
            lifecycle,
            state,
            state,
            attemptLeases,
            security,
            CreateBootstrap(registry, attemptLeases, security),
            new NullCredentialResolver(),
            new TestNodeConnector { ThrowOnDisconnect = true },
            NullLogger.Instance,
            new ConnectionDiagnostics(),
            _ => Task.CompletedTask,
            meter.CreateCounter<long>("attempts"),
            meter.CreateHistogram<double>("duration"));

        var failed = await coordinator.StartAsync(1);
        var automaticPlan = coordinator.PrepareAutomaticStart(
            lifecycleGeneration: 1,
            nodeModeIntended: true);

        Assert.Equal(NodeStartOutcome.Faulted, failed.Outcome);
        Assert.Equal(NodeAutomaticStartDisposition.Start, automaticPlan.Disposition);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task TokenMismatchRecovery_HoldsLifecycleLeaseAcrossSecurityCheck()
    {
        using var temp = new TempDirectory("openclaw-node-recovery-");
        using var meter = new Meter("openclaw-node-recovery-tests");
        var registry = new GatewayRegistry(temp.Path);
        var record = new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://gateway.example",
            SharedGatewayToken = "shared-token"
        };
        registry.AddOrUpdate(record);
        var identity = new DeviceIdentity(
            registry.GetIdentityDirectory(record.Id),
            NullLogger.Instance);
        identity.Initialize();
        identity.StoreDeviceTokenForRole("node", "stale-node-token");

        var gatewayAttempt = new GatewayAttemptStamp(1, record.Id);
        var lifecycle = new FakeNodeLifecycleSource(
            new NodeConnectionTarget(
                gatewayAttempt,
                record,
                registry.GetIdentityDirectory(record.Id),
                UseV2Signature: false),
            shouldStart: true);
        var state = new RecordingNodeStateSink();
        var attemptLeases = new TrackingAttemptLeaseSource();
        var security = new LeaseObservingSecurity(attemptLeases);
        var diagnostics = new ConnectionDiagnostics();
        var releaseReconnect =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new NodeConnectionCoordinator(
            lifecycle,
            state,
            state,
            attemptLeases,
            security,
            CreateBootstrap(registry, attemptLeases, security),
            new NullCredentialResolver(),
            new TestNodeConnector(),
            NullLogger.Instance,
            diagnostics,
            _ => releaseReconnect.Task,
            meter.CreateCounter<long>("attempts"),
            meter.CreateHistogram<double>("duration"));

        coordinator.HandleConnectionFailure(GatewayErrorKind.DeviceTokenMismatch);
        await WaitUntilAsync(() => diagnostics.GetAll().Any(
            entry => entry.Message.StartsWith(
                "Cleared stale node device token",
                StringComparison.Ordinal)));

        Assert.True(security.ObservedLeaseHeld);
        Assert.Equal(1, attemptLeases.AcquisitionCount);
        releaseReconnect.TrySetResult();
        await coordinator.StopAsync();
    }

    // P1 regression discriminator: proves credential recovery no longer runs on
    // the connector's lock-holding callback stack. Fails against the pre-fix inline
    // TrackBackground(HandleDeviceTokenMismatchAsync(attempt)) call.
    [Fact]
    public async Task ConnectionFailureRecovery_DetachesFromConnectorCallbackThread()
    {
        using var temp = new TempDirectory("openclaw-node-detach-");
        using var meter = new Meter("openclaw-node-detach-tests");
        var (registry, identityPath, lifecycle) = CreateRecoveryFixture(temp);
        var state = new RecordingNodeStateSink();
        var attemptLeases = new TrackingAttemptLeaseSource();
        var gate = new GatingEndpointSecurity();
        var diagnostics = new ConnectionDiagnostics();
        var releaseReconnect =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new NodeConnectionCoordinator(
            lifecycle,
            state,
            state,
            attemptLeases,
            gate,
            CreateBootstrap(registry, attemptLeases, gate),
            new NullCredentialResolver(),
            new TestNodeConnector(),
            NullLogger.Instance,
            diagnostics,
            _ => releaseReconnect.Task,
            meter.CreateCounter<long>("attempts"),
            meter.CreateHistogram<double>("duration"));

        var invokerThreadId = 0;
        var invokerReturned = new ManualResetEventSlim(false);
        var invoker = new Thread(() =>
        {
            invokerThreadId = Environment.CurrentManagedThreadId;
            coordinator.HandleConnectionFailure(GatewayErrorKind.DeviceTokenMismatch);
            invokerReturned.Set();
        })
        {
            IsBackground = true,
            Name = "connection-failure-invoker"
        };

        try
        {
            invoker.Start();

            Assert.True(
                gate.Entered.Wait(TimeSpan.FromSeconds(5)),
                "Credential recovery never reached the endpoint trust check.");
            Assert.True(
                invokerReturned.Wait(TimeSpan.FromSeconds(5)),
                "Callback remained blocked while recovery was suspended at the endpoint " +
                "trust check; recovery ran inline on the connector-lock-holding stack " +
                "instead of being scheduled off it.");
            Assert.NotEqual(invokerThreadId, gate.ObservedThreadId);
            Assert.DoesNotContain(
                diagnostics.GetAll(),
                entry => entry.Message.StartsWith(
                    "Cleared stale node device token",
                    StringComparison.Ordinal));
            Assert.Equal(
                "stale-node-token",
                DeviceIdentityFileReader.Instance.TryReadStoredNodeDeviceToken(identityPath));
        }
        finally
        {
            gate.Release();
            releaseReconnect.TrySetResult();
        }

        await WaitUntilAsync(() => diagnostics.GetAll().Any(
            entry => entry.Message.StartsWith(
                "Cleared stale node device token",
                StringComparison.Ordinal)));
        Assert.Null(
            DeviceIdentityFileReader.Instance.TryReadStoredNodeDeviceToken(identityPath));
        await coordinator.StopAsync();
        Assert.True(invoker.Join(TimeSpan.FromSeconds(5)));
    }

    // Behavior-preservation regression: recovery clears a stale node device token
    // on a trusted endpoint. Guards the recovery outcome (holds under both the old
    // inline call and the detached fix); the detachment itself is covered above.
    [Fact]
    public async Task ConnectionFailureRecovery_TrustedEndpoint_ClearsStaleNodeToken()
    {
        using var temp = new TempDirectory("openclaw-node-recovery-clear-");
        using var meter = new Meter("openclaw-node-recovery-clear-tests");
        var (registry, identityPath, lifecycle) = CreateRecoveryFixture(temp);
        var state = new RecordingNodeStateSink();
        var attemptLeases = new TrackingAttemptLeaseSource();
        var security = new AllowEndpointCredentialSecurity();
        var diagnostics = new ConnectionDiagnostics();
        var releaseReconnect =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new NodeConnectionCoordinator(
            lifecycle,
            state,
            state,
            attemptLeases,
            security,
            CreateBootstrap(registry, attemptLeases, security),
            new NullCredentialResolver(),
            new TestNodeConnector(),
            NullLogger.Instance,
            diagnostics,
            _ => releaseReconnect.Task,
            meter.CreateCounter<long>("attempts"),
            meter.CreateHistogram<double>("duration"));

        coordinator.HandleConnectionFailure(GatewayErrorKind.DeviceTokenMismatch);
        await WaitUntilAsync(() => diagnostics.GetAll().Any(
            entry => entry.Message.StartsWith(
                "Cleared stale node device token",
                StringComparison.Ordinal)));

        Assert.Null(
            DeviceIdentityFileReader.Instance.TryReadStoredNodeDeviceToken(identityPath));
        releaseReconnect.TrySetResult();
        await coordinator.StopAsync();
    }

    // Behavior-preservation regression: an untrusted endpoint must not clear the
    // node device token. Guards the recovery outcome, not the detachment.
    [Fact]
    public async Task ConnectionFailureRecovery_UntrustedEndpoint_PreservesNodeToken()
    {
        using var temp = new TempDirectory("openclaw-node-recovery-deny-");
        using var meter = new Meter("openclaw-node-recovery-deny-tests");
        var (registry, identityPath, lifecycle) = CreateRecoveryFixture(temp);
        var state = new RecordingNodeStateSink();
        var attemptLeases = new TrackingAttemptLeaseSource();
        var security = new DenyRecoveryEndpointSecurity();
        var diagnostics = new ConnectionDiagnostics();
        var coordinator = new NodeConnectionCoordinator(
            lifecycle,
            state,
            state,
            attemptLeases,
            security,
            CreateBootstrap(registry, attemptLeases, security),
            new NullCredentialResolver(),
            new TestNodeConnector(),
            NullLogger.Instance,
            diagnostics,
            _ => Task.CompletedTask,
            meter.CreateCounter<long>("attempts"),
            meter.CreateHistogram<double>("duration"));

        coordinator.HandleConnectionFailure(GatewayErrorKind.DeviceTokenMismatch);
        await WaitUntilAsync(() => diagnostics.GetAll().Any(
            entry => entry.Message.StartsWith(
                "Skipped node token recovery",
                StringComparison.Ordinal)));

        Assert.Equal(
            "stale-node-token",
            DeviceIdentityFileReader.Instance.TryReadStoredNodeDeviceToken(identityPath));
        Assert.DoesNotContain(
            diagnostics.GetAll(),
            entry => entry.Message.StartsWith(
                "Cleared stale node device token",
                StringComparison.Ordinal));
        await coordinator.StopAsync();
    }

    // Behavior-preservation regression: after StopAsync the stopped/generation
    // fence gates recovery to a no-op. Guards the disposed outcome, not detachment.
    [Fact]
    public async Task ConnectionFailure_AfterStop_SkipsRecovery()
    {
        using var temp = new TempDirectory("openclaw-node-recovery-stopped-");
        using var meter = new Meter("openclaw-node-recovery-stopped-tests");
        var (registry, identityPath, lifecycle) = CreateRecoveryFixture(temp);
        var state = new RecordingNodeStateSink();
        var attemptLeases = new TrackingAttemptLeaseSource();
        var security = new AllowEndpointCredentialSecurity();
        var diagnostics = new ConnectionDiagnostics();
        var coordinator = new NodeConnectionCoordinator(
            lifecycle,
            state,
            state,
            attemptLeases,
            security,
            CreateBootstrap(registry, attemptLeases, security),
            new NullCredentialResolver(),
            new TestNodeConnector(),
            NullLogger.Instance,
            diagnostics,
            _ => Task.CompletedTask,
            meter.CreateCounter<long>("attempts"),
            meter.CreateHistogram<double>("duration"));

        await coordinator.StopAsync();
        coordinator.HandleConnectionFailure(GatewayErrorKind.DeviceTokenMismatch);
        await Task.Delay(100);

        Assert.Equal(
            "stale-node-token",
            DeviceIdentityFileReader.Instance.TryReadStoredNodeDeviceToken(identityPath));
        Assert.DoesNotContain(
            diagnostics.GetAll(),
            entry => entry.Message.StartsWith(
                "Cleared stale node device token",
                StringComparison.Ordinal));
    }

    // P1 regression discriminator: recovery detached onto the pool can be superseded
    // mid-flight; the post-trust-check generation fence must abort before clearing
    // the token. Fails against the pre-fix inline call, which clears synchronously
    // before the supersession can land.
    [Fact]
    public async Task ConnectionFailureRecovery_SupersededNodeGeneration_PreservesNodeToken()
    {
        using var temp = new TempDirectory("openclaw-node-recovery-superseded-");
        using var meter = new Meter("openclaw-node-recovery-superseded-tests");
        var (registry, identityPath, lifecycle) = CreateRecoveryFixture(temp);
        var state = new RecordingNodeStateSink();
        var attemptLeases = new TrackingAttemptLeaseSource();
        var gate = new GatingEndpointSecurity();
        var diagnostics = new ConnectionDiagnostics();
        var coordinator = new NodeConnectionCoordinator(
            lifecycle,
            state,
            state,
            attemptLeases,
            gate,
            CreateBootstrap(registry, attemptLeases, gate),
            new NullCredentialResolver(),
            new TestNodeConnector(),
            NullLogger.Instance,
            diagnostics,
            _ => Task.CompletedTask,
            meter.CreateCounter<long>("attempts"),
            meter.CreateHistogram<double>("duration"));

        coordinator.HandleConnectionFailure(GatewayErrorKind.DeviceTokenMismatch);
        Assert.True(
            gate.Entered.Wait(TimeSpan.FromSeconds(5)),
            "Credential recovery never reached the endpoint trust check.");

        // Supersede the captured attempt while recovery is parked at the trust
        // check, then let it proceed. The post-check generation fence must abort
        // before clearing the node device token.
        await coordinator.RetireAsync();
        gate.Release();
        await coordinator.StopAsync();

        Assert.Equal(
            "stale-node-token",
            DeviceIdentityFileReader.Instance.TryReadStoredNodeDeviceToken(identityPath));
        Assert.DoesNotContain(
            diagnostics.GetAll(),
            entry => entry.Message.StartsWith(
                "Cleared stale node device token",
                StringComparison.Ordinal));
    }

    private static (GatewayRegistry Registry, string IdentityPath, FakeNodeLifecycleSource Lifecycle)
        CreateRecoveryFixture(TempDirectory temp)
    {
        var registry = new GatewayRegistry(temp.Path);
        var record = new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://gateway.example",
            SharedGatewayToken = "shared-token"
        };
        registry.AddOrUpdate(record);
        var identityPath = registry.GetIdentityDirectory(record.Id);
        var identity = new DeviceIdentity(identityPath, NullLogger.Instance);
        identity.Initialize();
        identity.StoreDeviceTokenForRole("node", "stale-node-token");

        var gatewayAttempt = new GatewayAttemptStamp(1, record.Id);
        var lifecycle = new FakeNodeLifecycleSource(
            new NodeConnectionTarget(
                gatewayAttempt,
                record,
                identityPath,
                UseV2Signature: false),
            shouldStart: true);
        return (registry, identityPath, lifecycle);
    }

    private static BootstrapTokenLifecycle CreateBootstrap(
        GatewayRegistry registry,
        IGatewayAttemptLeaseSource attemptLeases,
        IEndpointCredentialSecurity security) =>
        new(
            registry,
            identityStore: null,
            attemptLeases,
            security,
            new RecordingReconnectScheduler(),
            new RecordingV2SignatureSink(),
            NullLogger.Instance,
            new ConnectionDiagnostics());

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition timed out.");
            await Task.Delay(10);
        }
    }

    private sealed class FakeNodeLifecycleSource(
        NodeConnectionTarget? target = null,
        bool shouldStart = false) : INodeLifecycleSource
    {
        private readonly GatewayAttemptStamp _attempt = new(1, "gw-1");

        public GatewayAttemptStamp CaptureGatewayAttempt() => _attempt;

        public bool IsCurrentLifecycle(GatewayAttemptStamp attempt) =>
            attempt == _attempt;

        public CancellationToken GetLifecycleCancellationToken(
            GatewayAttemptStamp attempt) =>
            CancellationToken.None;

        public NodeConnectionTarget? GetNodeConnectionTarget(
            GatewayAttemptStamp attempt) =>
            target;

        public bool ShouldStartNodeConnection(NodeConnectionTarget target) =>
            shouldStart;
    }

    private sealed class RecordingNodeStateSink :
        INodeConnectionStateSink,
        INodeConnectionStateSource
    {
        public int PairingWrites { get; private set; }

        public Task<bool> PublishNodeStartingAsync(
            NodeAttemptStamp attempt,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<bool> PublishNodeBlockedAsync(
            NodeAttemptStamp attempt,
            string detail,
            GatewayCredentialResolution? resolution,
            bool preserveCredentialResolution,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<bool> PublishNodeCredentialResolvedAsync(
            NodeAttemptStamp attempt,
            GatewayCredentialResolution resolution,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<bool> PublishNodeStatusAsync(
            NodeAttemptStamp attempt,
            ConnectionStatus status,
            NodeConnectorSnapshot connector,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<bool> PublishNodePairingAsync(
            NodeAttemptStamp attempt,
            PairingStatusEventArgs pairing,
            NodeConnectorSnapshot connector,
            CancellationToken cancellationToken)
        {
            PairingWrites++;
            return Task.FromResult(true);
        }

        public bool IsOperatorConnectedUnderAttemptLease(
            NodeAttemptStamp attempt) =>
            true;
    }

    private sealed class NullCredentialResolver : ICredentialResolver
    {
        public GatewayCredential? ResolveOperator(
            GatewayRecord record,
            string identityPath) =>
            null;

        public GatewayCredential? ResolveNode(
            GatewayRecord record,
            string identityPath) =>
            null;
    }

    private sealed class TrackingAttemptLeaseSource : IGatewayAttemptLeaseSource
    {
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        public int AcquisitionCount { get; private set; }
        public bool IsHeld => _semaphore.CurrentCount == 0;

        public async Task<GatewayAttemptLease?> AcquireCurrentAttemptAsync(
            GatewayAttemptStamp attempt,
            CancellationToken cancellationToken)
        {
            await _semaphore.WaitAsync(cancellationToken);
            AcquisitionCount++;
            return new GatewayAttemptLease(_semaphore);
        }
    }

    private sealed class LeaseObservingSecurity(
        TrackingAttemptLeaseSource attemptLeases) : IEndpointCredentialSecurity
    {
        public bool ObservedLeaseHeld { get; private set; }

        public Task<EndpointCredentialAuthorization> AuthorizeCredentialAsync(
            GatewayRecord record,
            GatewayCredential credential,
            CancellationToken cancellationToken) =>
            Task.FromResult(EndpointCredentialAuthorization.AllowedResult);

        public Task<bool> IsRecoverySafeEndpointAsync(
            GatewayRecord record,
            CancellationToken cancellationToken)
        {
            ObservedLeaseHeld = attemptLeases.IsHeld;
            return Task.FromResult(true);
        }
    }

    private sealed class DenyRecoveryEndpointSecurity : IEndpointCredentialSecurity
    {
        public Task<EndpointCredentialAuthorization> AuthorizeCredentialAsync(
            GatewayRecord record,
            GatewayCredential credential,
            CancellationToken cancellationToken) =>
            Task.FromResult(EndpointCredentialAuthorization.AllowedResult);

        public Task<bool> IsRecoverySafeEndpointAsync(
            GatewayRecord record,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class GatingEndpointSecurity : IEndpointCredentialSecurity
    {
        private readonly ManualResetEventSlim _release = new(false);

        public ManualResetEventSlim Entered { get; } = new(false);

        public int ObservedThreadId { get; private set; }

        public Task<EndpointCredentialAuthorization> AuthorizeCredentialAsync(
            GatewayRecord record,
            GatewayCredential credential,
            CancellationToken cancellationToken) =>
            Task.FromResult(EndpointCredentialAuthorization.AllowedResult);

        public Task<bool> IsRecoverySafeEndpointAsync(
            GatewayRecord record,
            CancellationToken cancellationToken)
        {
            ObservedThreadId = Environment.CurrentManagedThreadId;
            Entered.Set();
            _release.Wait(TimeSpan.FromSeconds(10));
            return Task.FromResult(true);
        }

        public void Release() => _release.Set();
    }

    private sealed class TestNodeConnector : INodeConnector
    {
        public bool ThrowOnDisconnect { get; init; }
        public bool IsConnected => false;
        public PairingStatus PairingStatus => PairingStatus.Unknown;
        public string? NodeDeviceId => "node-1";
        public NodeConnectionMode Mode => NodeConnectionMode.Disabled;
#pragma warning disable CS0067
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;
        public event EventHandler<DeviceTokenReceivedEventArgs>? DeviceTokenReceived;
        public event EventHandler<NodeClientCreatedEventArgs>? ClientCreated;
#pragma warning restore CS0067

        public Task ConnectAsync(
            string gatewayUrl,
            GatewayCredential credential,
            string identityPath,
            bool useV2Signature = false) =>
            Task.CompletedTask;

        public Task ConnectAsync(
            string gatewayUrl,
            GatewayCredential credential,
            string identityPath,
            bool useV2Signature,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DisconnectAsync() =>
            ThrowOnDisconnect
                ? Task.FromException(new InvalidOperationException("disconnect failed"))
                : Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
