using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenClaw.Shared;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Connection;

internal readonly record struct GatewayAttemptStamp(
    long LifecycleGeneration,
    string? GatewayRecordId);

internal readonly record struct NodeAttemptStamp(
    GatewayAttemptStamp GatewayAttempt,
    long NodeGeneration);

internal sealed record NodeConnectionTarget(
    GatewayAttemptStamp GatewayAttempt,
    GatewayRecord Record,
    string IdentityPath,
    bool UseV2Signature);

internal interface INodeLifecycleSource
{
    GatewayAttemptStamp CaptureGatewayAttempt();
    bool IsCurrentLifecycle(GatewayAttemptStamp attempt);
    CancellationToken GetLifecycleCancellationToken(GatewayAttemptStamp attempt);
    NodeConnectionTarget? GetNodeConnectionTarget(GatewayAttemptStamp attempt);
    bool ShouldStartNodeConnection(NodeConnectionTarget target);
}

internal interface INodeConnectionStateSink
{
    Task<bool> PublishNodeStartingAsync(
        NodeAttemptStamp attempt,
        CancellationToken cancellationToken);

    Task<bool> PublishNodeBlockedAsync(
        NodeAttemptStamp attempt,
        string detail,
        GatewayCredentialResolution? resolution,
        bool preserveCredentialResolution,
        CancellationToken cancellationToken);

    Task<bool> PublishNodeCredentialResolvedAsync(
        NodeAttemptStamp attempt,
        GatewayCredentialResolution resolution,
        CancellationToken cancellationToken);

    Task<bool> PublishNodeStatusAsync(
        NodeAttemptStamp attempt,
        ConnectionStatus status,
        NodeConnectorSnapshot connector,
        CancellationToken cancellationToken);

    Task<bool> PublishNodePairingAsync(
        NodeAttemptStamp attempt,
        PairingStatusEventArgs pairing,
        NodeConnectorSnapshot connector,
        CancellationToken cancellationToken);

}

internal interface INodeConnectionStateSource
{
    bool IsOperatorConnectedUnderAttemptLease(NodeAttemptStamp attempt);
}

internal readonly record struct NodeConnectorSnapshot(
    bool IsConnected,
    PairingStatus PairingStatus,
    string? NodeDeviceId);

internal enum NodeAutomaticStartDisposition
{
    NotIntended,
    NotRequested,
    AlreadyStarted,
    MissingActiveGateway,
    MissingGatewayRecord,
    MissingConnector,
    Start
}

internal sealed record NodeAutomaticStartPlan(
    NodeAutomaticStartDisposition Disposition,
    GatewayAttemptStamp GatewayAttempt,
    long? GuardVersion = null,
    string? BlockDetail = null);

internal enum NodeStartOutcome
{
    Started,
    BlockedMissingCredential,
    BlockedNoConnector,
    BlockedMissingGateway,
    Superseded,
    Faulted
}

internal sealed record NodeStartResult(
    NodeStartOutcome Outcome,
    NodeAttemptStamp? Attempt = null);

internal interface INodePairReconnectPort
{
    bool IsCurrentNodeAttempt(NodeAttemptStamp attempt);

    Task<NodeStartResult> StartAsync(
        long expectedLifecycleGeneration,
        long? expectedNodeGeneration = null);
}

internal sealed class NodeConnectionCoordinator : INodePairReconnectPort
{
    internal const string NodeConnectSpanName = "openclaw.connection.node.connect";
    internal const string NodeReconnectSpanName = "openclaw.connection.node.reconnect";
    internal const string NodePrepareSpanName = "openclaw.connection.node.prepare";
    internal const string NodeTransportSpanName = "openclaw.connection.node.transport";
    internal const string NodeHandshakeSpanName = "openclaw.connection.node.handshake";

    private const string RoleTag = "openclaw.connection.role";
    private const string OperationTag = "openclaw.connection.operation";
    private const string MissingNodeConnectorMessage =
        "Node mode is enabled, but no node connector is configured.";
    private const string MissingActiveGatewayForNodeMessage =
        "Node mode is enabled, but there is no active gateway context for node startup.";
    private const string MissingGatewayRecordForNodeMessage =
        "Node mode is enabled, but the active gateway record could not be found.";

    private readonly INodeLifecycleSource _lifecycleSource;
    private readonly INodeConnectionStateSink _stateSink;
    private readonly INodeConnectionStateSource _stateSource;
    private readonly IGatewayAttemptLeaseSource _attemptLeases;
    private readonly IEndpointCredentialSecurity _endpointSecurity;
    private readonly BootstrapTokenLifecycle _bootstrapLifecycle;
    private readonly ICredentialResolver _credentialResolver;
    private readonly INodeConnector? _nodeConnector;
    private readonly IOpenClawLogger _logger;
    private readonly ConnectionDiagnostics _diagnostics;
    private readonly Func<TimeSpan, Task> _reconnectDelay;
    private readonly Counter<long> _connectionAttempts;
    private readonly Histogram<double> _connectionAttemptDuration;
    private readonly SemaphoreSlim _startSemaphore = new(1, 1);
    private readonly object _operationLock = new();
    private readonly object _telemetryLock = new();
    private readonly object _backgroundLock = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly HashSet<Task> _backgroundTasks = [];

    private long _nodeGeneration;
    private long _startLifecycleGeneration = -1;
    private long _startGuardVersion;
    private CancellationTokenSource? _operationCts;
    private string? _tokenRecoveryAttemptedGatewayId;
    private TelemetryAttempt? _telemetryAttempt;
    private int _stopped;

    internal NodeConnectionCoordinator(
        INodeLifecycleSource lifecycleSource,
        INodeConnectionStateSink stateSink,
        INodeConnectionStateSource stateSource,
        IGatewayAttemptLeaseSource attemptLeases,
        IEndpointCredentialSecurity endpointSecurity,
        BootstrapTokenLifecycle bootstrapLifecycle,
        ICredentialResolver credentialResolver,
        INodeConnector? nodeConnector,
        IOpenClawLogger logger,
        ConnectionDiagnostics diagnostics,
        Func<TimeSpan, Task> reconnectDelay,
        Counter<long> connectionAttempts,
        Histogram<double> connectionAttemptDuration)
    {
        _lifecycleSource = lifecycleSource;
        _stateSink = stateSink;
        _stateSource = stateSource;
        _attemptLeases = attemptLeases;
        _endpointSecurity = endpointSecurity;
        _bootstrapLifecycle = bootstrapLifecycle;
        _credentialResolver = credentialResolver;
        _nodeConnector = nodeConnector;
        _logger = logger;
        _diagnostics = diagnostics;
        _reconnectDelay = reconnectDelay;
        _connectionAttempts = connectionAttempts;
        _connectionAttemptDuration = connectionAttemptDuration;
    }

    internal long CurrentNodeGeneration => Interlocked.Read(ref _nodeGeneration);

    internal NodeAttemptStamp CaptureCurrentAttempt() =>
        new(_lifecycleSource.CaptureGatewayAttempt(), CurrentNodeGeneration);

    internal bool IsCurrentNodeAttempt(NodeAttemptStamp attempt) =>
        Volatile.Read(ref _stopped) == 0 &&
        Interlocked.Read(ref _nodeGeneration) == attempt.NodeGeneration &&
        _lifecycleSource.IsCurrentLifecycle(attempt.GatewayAttempt);

    bool INodePairReconnectPort.IsCurrentNodeAttempt(NodeAttemptStamp attempt) =>
        IsCurrentNodeAttempt(attempt);

    internal NodeAutomaticStartPlan PrepareAutomaticStart(
        long lifecycleGeneration,
        bool nodeModeIntended)
    {
        var gatewayAttempt = _lifecycleSource.CaptureGatewayAttempt();
        if (Volatile.Read(ref _stopped) != 0 ||
            gatewayAttempt.LifecycleGeneration != lifecycleGeneration)
        {
            return new(NodeAutomaticStartDisposition.AlreadyStarted, gatewayAttempt);
        }
        if (gatewayAttempt.GatewayRecordId is null)
        {
            if (!nodeModeIntended)
                return new(NodeAutomaticStartDisposition.NotIntended, gatewayAttempt);
            return new(
                NodeAutomaticStartDisposition.MissingActiveGateway,
                gatewayAttempt,
                BlockDetail: MissingActiveGatewayForNodeMessage);
        }

        var target = _lifecycleSource.GetNodeConnectionTarget(gatewayAttempt);
        if (target is null)
        {
            if (!nodeModeIntended)
                return new(NodeAutomaticStartDisposition.NotIntended, gatewayAttempt);
            return new(
                NodeAutomaticStartDisposition.MissingGatewayRecord,
                gatewayAttempt,
                BlockDetail: MissingGatewayRecordForNodeMessage);
        }
        if (!_lifecycleSource.ShouldStartNodeConnection(target))
            return new(NodeAutomaticStartDisposition.NotRequested, gatewayAttempt);
        if (_nodeConnector is null)
        {
            return new(
                NodeAutomaticStartDisposition.MissingConnector,
                gatewayAttempt,
                BlockDetail: MissingNodeConnectorMessage);
        }

        lock (_operationLock)
        {
            if (Interlocked.Read(ref _startLifecycleGeneration) == lifecycleGeneration)
            {
                return new(
                    NodeAutomaticStartDisposition.AlreadyStarted,
                    gatewayAttempt);
            }

            var version = AcquireStartGuard(lifecycleGeneration);
            return new(NodeAutomaticStartDisposition.Start, gatewayAttempt, version);
        }
    }

    internal Task<NodeStartResult> StartAutomaticAsync(NodeAutomaticStartPlan plan)
    {
        if (plan.Disposition != NodeAutomaticStartDisposition.Start ||
            !plan.GuardVersion.HasValue)
        {
            return Task.FromResult(new NodeStartResult(NodeStartOutcome.Superseded));
        }

        return StartAsync(
            plan.GatewayAttempt.LifecycleGeneration,
            expectedNodeGeneration: null,
            plan.GuardVersion);
    }

    internal Task<NodeStartResult> StartAsync(
        long expectedLifecycleGeneration,
        long? expectedNodeGeneration = null) =>
        StartAsync(expectedLifecycleGeneration, expectedNodeGeneration, guardVersion: null);

    Task<NodeStartResult> INodePairReconnectPort.StartAsync(
        long expectedLifecycleGeneration,
        long? expectedNodeGeneration) =>
        StartAsync(expectedLifecycleGeneration, expectedNodeGeneration);

    private async Task<NodeStartResult> StartAsync(
        long expectedLifecycleGeneration,
        long? expectedNodeGeneration,
        long? guardVersion)
    {
        var guardLease = new NodeStartGuardLease { Version = guardVersion };
        var result = await StartAttemptAsync(
            expectedLifecycleGeneration,
            expectedNodeGeneration,
            guardLease).ConfigureAwait(false);
        if (result.Outcome != NodeStartOutcome.Started &&
            guardLease.Version.HasValue)
        {
            lock (_operationLock)
            {
                if (Interlocked.Read(ref _startGuardVersion) ==
                    guardLease.Version.Value)
                {
                    Interlocked.CompareExchange(
                        ref _startLifecycleGeneration,
                        -1,
                        expectedLifecycleGeneration);
                }
            }
        }

        return result;
    }

    private async Task<NodeStartResult> StartAttemptAsync(
        long expectedLifecycleGeneration,
        long? expectedNodeGeneration,
        NodeStartGuardLease guardLease)
    {
        CancellationTokenSource? operationCts = null;
        CancellationToken operationToken = CancellationToken.None;
        NodeAttemptStamp attempt = default;
        NodeAttemptStamp preStartFence = default;
        string? preStartBlocker = null;
        CancellationToken preStartToken = CancellationToken.None;

        await _startSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            CancellationTokenSource? oldOperationCts;
            lock (_operationLock)
            {
                if (!TryCaptureExpectedAttempt(
                        expectedLifecycleGeneration,
                        expectedNodeGeneration,
                        out preStartFence))
                {
                    return new NodeStartResult(NodeStartOutcome.Superseded);
                }

                if (guardLease.Version.HasValue)
                {
                    if (Interlocked.Read(ref _startGuardVersion) !=
                            guardLease.Version.Value ||
                        Interlocked.Read(ref _startLifecycleGeneration) != expectedLifecycleGeneration)
                    {
                        return new NodeStartResult(NodeStartOutcome.Superseded);
                    }
                }
                else
                {
                    guardLease.Version = AcquireStartGuard(
                        expectedLifecycleGeneration);
                }

                oldOperationCts = _operationCts;
                _operationCts = null;
                oldOperationCts?.Cancel();
            }

            CancelTelemetryAttempt("superseded", null);

            if (_nodeConnector is not null)
            {
                try
                {
                    if (!await WaitWithTimeoutAsync(
                            _nodeConnector.DisconnectAsync(),
                            TimeSpan.FromSeconds(2),
                            "Previous node disconnect").ConfigureAwait(false))
                    {
                        _diagnostics.Record("node", "Previous node disconnect timed out");
                        preStartBlocker = "Previous node disconnect timed out";
                        preStartToken = _lifecycleSource.GetLifecycleCancellationToken(
                            preStartFence.GatewayAttempt);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"[ConnMgr] Previous node disconnect failed: {ex.Message}");
                    _diagnostics.Record("node", "Previous node disconnect failed", ex.Message);
                    preStartBlocker = $"Previous node disconnect failed: {ex.Message}";
                    preStartToken = _lifecycleSource.GetLifecycleCancellationToken(
                        preStartFence.GatewayAttempt);
                }
            }

            if (preStartBlocker is null)
            {
                lock (_operationLock)
                {
                    if (!TryCaptureExpectedAttempt(
                            expectedLifecycleGeneration,
                            expectedNodeGeneration,
                            out _))
                    {
                        return new NodeStartResult(NodeStartOutcome.Superseded);
                    }

                    var gatewayAttempt = _lifecycleSource.CaptureGatewayAttempt();
                    operationCts = CancellationTokenSource.CreateLinkedTokenSource(
                        _shutdownCts.Token,
                        _lifecycleSource.GetLifecycleCancellationToken(gatewayAttempt));
                    operationToken = operationCts.Token;
                    var nodeGeneration = Interlocked.Increment(ref _nodeGeneration);
                    attempt = new NodeAttemptStamp(gatewayAttempt, nodeGeneration);
                    _operationCts = operationCts;
                }

                StartTelemetryAttempt(
                    attempt,
                    "connect",
                    NodePrepareSpanName);
            }
        }
        finally
        {
            _startSemaphore.Release();
        }

        if (preStartBlocker is not null)
        {
            try
            {
                await _stateSink.PublishNodeBlockedAsync(
                    preStartFence,
                    preStartBlocker,
                    resolution: null,
                    preserveCredentialResolution: false,
                    preStartToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (preStartToken.IsCancellationRequested)
            {
                return new NodeStartResult(NodeStartOutcome.Superseded);
            }
            CancelTelemetryAttempt("superseded", null);
            RecordPreflightTelemetryFailure(ConnectionErrorCategory.InternalError);
            return new NodeStartResult(NodeStartOutcome.Faulted);
        }

        try
        {
            return await StartCoreAsync(attempt, operationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            CompleteTelemetryAttempt(
                attempt.NodeGeneration,
                "canceled",
                ConnectionErrorCategory.Cancelled);
            return new NodeStartResult(NodeStartOutcome.Superseded);
        }
        finally
        {
            lock (_operationLock)
            {
                if (ReferenceEquals(_operationCts, operationCts))
                    _operationCts = null;
            }
            operationCts!.Dispose();
        }
    }

    private async Task<NodeStartResult> StartCoreAsync(
        NodeAttemptStamp attempt,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentNodeAttempt(attempt) || cancellationToken.IsCancellationRequested)
            return new(NodeStartOutcome.Superseded);

        if (_nodeConnector is null)
        {
            await _stateSink.PublishNodeBlockedAsync(
                attempt,
                MissingNodeConnectorMessage,
                resolution: null,
                preserveCredentialResolution: false,
                cancellationToken).ConfigureAwait(false);
            CompleteTelemetryAttempt(
                attempt.NodeGeneration,
                "failure",
                ConnectionErrorCategory.InternalError);
            return new(NodeStartOutcome.BlockedNoConnector);
        }

        var target = _lifecycleSource.GetNodeConnectionTarget(attempt.GatewayAttempt);
        if (target is null)
        {
            var detail = attempt.GatewayAttempt.GatewayRecordId is null
                ? MissingActiveGatewayForNodeMessage
                : MissingGatewayRecordForNodeMessage;
            await _stateSink.PublishNodeBlockedAsync(
                attempt,
                detail,
                resolution: null,
                preserveCredentialResolution: false,
                cancellationToken).ConfigureAwait(false);
            CompleteTelemetryAttempt(
                attempt.NodeGeneration,
                "failure",
                ConnectionErrorCategory.InternalError);
            return new(NodeStartOutcome.BlockedMissingGateway);
        }

        if (!await _stateSink.PublishNodeStartingAsync(attempt, cancellationToken).ConfigureAwait(false))
            return new(NodeStartOutcome.Superseded);

        var resolution = _credentialResolver.ResolveNodeDetailed(
            target.Record,
            target.IdentityPath);
        var credential = resolution.Credential;
        if (HasPersistedIdentityFailure(resolution))
        {
            _diagnostics.RecordCredentialResolutionResult(resolution);
            _diagnostics.Record(
                "identity",
                "Stored device identity could not be loaded for node connection",
                resolution.Detail);
            await _stateSink.PublishNodeBlockedAsync(
                attempt,
                DeviceIdentityLoadException.RecoveryMessage,
                resolution,
                preserveCredentialResolution: true,
                cancellationToken).ConfigureAwait(false);
            CompleteTelemetryAttempt(
                attempt.NodeGeneration,
                "failure",
                ConnectionErrorCategory.InternalError);
            return new(NodeStartOutcome.BlockedMissingCredential);
        }

        if (credential is null)
        {
            _logger.Warn("[ConnMgr] No node credential available — skipping node connection");
            _diagnostics.RecordCredentialResolutionResult(resolution);
            await _stateSink.PublishNodeBlockedAsync(
                attempt,
                CredentialResolutionFailureFormatter.Format(
                    ConnectionCredentialRole.Node,
                    resolution),
                resolution,
                preserveCredentialResolution: true,
                cancellationToken).ConfigureAwait(false);
            CompleteTelemetryAttempt(
                attempt.NodeGeneration,
                "failure",
                ConnectionErrorCategory.AuthFailure);
            return new(NodeStartOutcome.BlockedMissingCredential);
        }

        var authorization = await _endpointSecurity.AuthorizeCredentialAsync(
            target.Record,
            credential,
            cancellationToken).ConfigureAwait(false);
        var expectedEndpointOwnership = authorization.OwnershipProof;
        if (!IsCurrentNodeAttempt(attempt))
            return new(NodeStartOutcome.Superseded);
        if (!authorization.Allowed)
        {
            _diagnostics.Record(
                "setup",
                "Blocked node credential before managed-local endpoint ownership was proven",
                authorization.Detail);
            await _stateSink.PublishNodeBlockedAsync(
                attempt,
                authorization.Detail,
                resolution,
                preserveCredentialResolution: true,
                cancellationToken).ConfigureAwait(false);
            CompleteTelemetryAttempt(
                attempt.NodeGeneration,
                "failure",
                ConnectionErrorCategory.AuthFailure);
            return new(NodeStartOutcome.BlockedMissingCredential);
        }

        _diagnostics.RecordCredentialResolutionResult(resolution);
        if (!await _stateSink.PublishNodeCredentialResolvedAsync(
                attempt,
                resolution,
                cancellationToken).ConfigureAwait(false))
        {
            return new(NodeStartOutcome.Superseded);
        }

        var connectUrl = target.Record.SshTunnel is not null
            ? $"ws://localhost:{target.Record.SshTunnel.LocalPort}"
            : target.Record.Url;
        _diagnostics.Record(
            "node",
            $"Starting node connection to {connectUrl}",
            $"Credential source: {credential.Source}");

        if (_nodeConnector is INodeConnectorReconnectPolicy reconnectPolicy)
        {
            async Task<ReconnectAuthorizationResult> AuthorizeNodeCredentialHandoffAsync(
                CancellationToken authorizationCancellationToken)
            {
                var authorization = await _endpointSecurity.AuthorizeCredentialHandoffAsync(
                        target.Record,
                        credential,
                        expectedEndpointOwnership,
                        () => IsCurrentNodeAttempt(attempt),
                        cancellationToken,
                        authorizationCancellationToken,
                        "node")
                    .ConfigureAwait(false);
                if (!authorization.Allowed &&
                    authorization.FailureKind != GatewayErrorKind.Unknown)
                {
                    await _stateSink.PublishNodeBlockedAsync(
                            attempt,
                            authorization.Detail ?? "Node credential handoff was not authorized.",
                            resolution,
                            preserveCredentialResolution: true,
                            authorizationCancellationToken)
                        .ConfigureAwait(false);
                }
                return new ReconnectAuthorizationResult(
                    authorization.Allowed,
                    authorization.FailureKind,
                    authorization.Detail);
            }

            reconnectPolicy.HandshakeAuthorizationAsync =
                AuthorizeNodeCredentialHandoffAsync;
            reconnectPolicy.ReconnectAuthorizationAsync =
                AuthorizeNodeCredentialHandoffAsync;
        }

        try
        {
            await _nodeConnector.ConnectAsync(
                connectUrl,
                credential,
                target.IdentityPath,
                target.UseV2Signature,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CompleteTelemetryAttempt(
                attempt.NodeGeneration,
                "canceled",
                ConnectionErrorCategory.Cancelled);
            return new(NodeStartOutcome.Superseded);
        }
        catch (DeviceIdentityLoadException ex)
        {
            if (cancellationToken.IsCancellationRequested ||
                !IsCurrentNodeAttempt(attempt))
                return new(NodeStartOutcome.Superseded);

            var detail = BuildIdentityFailureDetail(ex);
            _logger.Error($"[ConnMgr] Stored device identity load failed for node connection: {detail}");
            _diagnostics.Record(
                "identity",
                "Stored device identity could not be loaded for node connection",
                detail);
            await _stateSink.PublishNodeBlockedAsync(
                attempt,
                DeviceIdentityLoadException.RecoveryMessage,
                resolution: null,
                preserveCredentialResolution: false,
                cancellationToken).ConfigureAwait(false);
            CompleteTelemetryAttempt(
                attempt.NodeGeneration,
                "failure",
                ConnectionErrorCategory.InternalError);
            return new(NodeStartOutcome.Faulted);
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested ||
                !IsCurrentNodeAttempt(attempt))
                return new(NodeStartOutcome.Superseded);

            _logger.Error($"[ConnMgr] Node connect failed: {ex.Message}");
            _diagnostics.Record("node", "Node connect failed", ex.Message);
            await _stateSink.PublishNodeBlockedAsync(
                attempt,
                $"Node connect failed: {ex.Message}",
                resolution: null,
                preserveCredentialResolution: false,
                cancellationToken).ConfigureAwait(false);
            CompleteTelemetryAttempt(
                attempt.NodeGeneration,
                "failure",
                ConnectionErrorCategory.NetworkUnreachable);
            return new(NodeStartOutcome.Faulted);
        }

        return IsCurrentNodeAttempt(attempt)
            ? new(NodeStartOutcome.Started, attempt)
            : new(NodeStartOutcome.Superseded);
    }

    internal void HandleStatusChanged(ConnectionStatus status)
    {
        var attempt = CaptureCurrentAttempt();
        ObserveTelemetryStatus(status, attempt);
        TrackBackground(HandleStatusChangedAsync(status, attempt));
    }

    private async Task HandleStatusChangedAsync(
        ConnectionStatus status,
        NodeAttemptStamp attempt)
    {
        if (!IsCurrentNodeAttempt(attempt))
            return;

        _diagnostics.Record("node", $"Node status: {status}");
        var connector = CaptureConnectorSnapshot();
        if (connector.PairingStatus == PairingStatus.Pending &&
            status is ConnectionStatus.Disconnected or ConnectionStatus.Error)
        {
            return;
        }

        if (!await _stateSink.PublishNodeStatusAsync(
                attempt,
                status,
                connector,
                _shutdownCts.Token).ConfigureAwait(false))
        {
            return;
        }

        if (status == ConnectionStatus.Connected &&
            IsCurrentNodeAttempt(attempt))
        {
            _tokenRecoveryAttemptedGatewayId = null;
        }

        await _bootstrapLifecycle.TryClearAfterDurablePairingAsync(
            attempt.GatewayAttempt,
            _shutdownCts.Token).ConfigureAwait(false);
    }

    internal void HandleTransportConnected()
    {
        var attempt = CaptureCurrentAttempt();
        if (IsCurrentNodeAttempt(attempt))
            TransitionTelemetryPhase(attempt.NodeGeneration, NodeHandshakeSpanName);
    }

    internal void HandleConnectionFailure(GatewayErrorKind errorKind)
    {
        var attempt = CaptureCurrentAttempt();
        if (!IsCurrentNodeAttempt(attempt))
            return;

        lock (_operationLock)
        {
            Interlocked.CompareExchange(
                ref _startLifecycleGeneration,
                -1,
                attempt.GatewayAttempt.LifecycleGeneration);
        }

        CompleteTelemetryAttempt(
            attempt.NodeGeneration,
            "failure",
            MapNodeConnectionErrorCategory(errorKind));

        if (errorKind == GatewayErrorKind.DeviceTokenMismatch)
        {
            // NodeConnector raises this callback under its lifecycle lock, so the
            // entire token recovery must run off the callback stack. The unwrapped
            // proxy task is tracked and still drains on shutdown. Rationale and the
            // regression proofs live in NodeConnectionCoordinatorTests.
            TrackBackground(Task.Run(() => HandleDeviceTokenMismatchAsync(attempt)));
        }
    }

    internal void HandleDeviceTokenReceived(DeviceTokenReceivedEventArgs token)
    {
        var attempt = CaptureCurrentAttempt();
        if (!IsCurrentNodeAttempt(attempt))
            return;

        _diagnostics.Record(
            "credential",
            $"Node connector device token received for {token.Role}",
            $"Scopes={string.Join(",", token.Scopes ?? [])}");
        TrackBackground(_bootstrapLifecycle.TryClearAfterDurablePairingAsync(
            attempt.GatewayAttempt,
            _shutdownCts.Token));
    }

    internal void ObservePairingTelemetry(
        PairingStatusEventArgs pairing,
        NodeAttemptStamp attempt)
    {
        if (!IsCurrentNodeAttempt(attempt))
            return;

        if (pairing.Status == PairingStatus.Pending)
        {
            CompleteTelemetryAttempt(
                attempt.NodeGeneration,
                "pairing_required",
                ConnectionErrorCategory.PairingPending);
        }
        else if (pairing.Status == PairingStatus.Rejected)
        {
            CompleteTelemetryAttempt(
                attempt.NodeGeneration,
                "pairing_rejected",
                ConnectionErrorCategory.PairingRejected);
        }
        else if (pairing.Status == PairingStatus.Paired &&
                 _nodeConnector?.IsConnected == true)
        {
            CompleteTelemetryAttempt(attempt.NodeGeneration, "success");
        }
    }

    internal async Task<bool> PublishPairingStatusAsync(
        PairingStatusEventArgs pairing,
        NodeAttemptStamp attempt)
    {
        if (!IsCurrentNodeAttempt(attempt))
            return false;

        _diagnostics.Record("node", $"Node pairing: {pairing.Status}");
        var published = await _stateSink.PublishNodePairingAsync(
            attempt,
            pairing,
            CaptureConnectorSnapshot(),
            _shutdownCts.Token).ConfigureAwait(false);
        if (!published)
            return false;

        if (pairing.Status == PairingStatus.Paired &&
            IsCurrentNodeAttempt(attempt))
        {
            _tokenRecoveryAttemptedGatewayId = null;
        }

        await _bootstrapLifecycle.TryClearAfterDurablePairingAsync(
            attempt.GatewayAttempt,
            _shutdownCts.Token).ConfigureAwait(false);
        return IsCurrentNodeAttempt(attempt);
    }

    private async Task HandleDeviceTokenMismatchAsync(NodeAttemptStamp attempt)
    {
        try
        {
            if (!IsCurrentNodeAttempt(attempt))
                return;

            var target = _lifecycleSource.GetNodeConnectionTarget(attempt.GatewayAttempt);
            if (target is null)
                return;

            using var lease = await _attemptLeases.AcquireCurrentAttemptAsync(
                attempt.GatewayAttempt,
                _shutdownCts.Token).ConfigureAwait(false);
            if (lease is null ||
                !IsCurrentNodeAttempt(attempt) ||
                !_stateSource.IsOperatorConnectedUnderAttemptLease(attempt) ||
                _tokenRecoveryAttemptedGatewayId == target.Record.Id)
            {
                return;
            }

            var hasSharedToken =
                !string.IsNullOrWhiteSpace(target.Record.SharedGatewayToken);
            var hasBootstrapToken =
                !string.IsNullOrWhiteSpace(target.Record.BootstrapToken);
            if (!hasSharedToken && !hasBootstrapToken)
                return;

            if (!await _endpointSecurity.IsRecoverySafeEndpointAsync(
                    target.Record,
                    _shutdownCts.Token).ConfigureAwait(false))
            {
                _diagnostics.Record(
                    "credential",
                    "Skipped node token recovery: endpoint not trusted for credential fallback");
                return;
            }

            if (!IsCurrentNodeAttempt(attempt) ||
                !_stateSource.IsOperatorConnectedUnderAttemptLease(attempt))
            {
                return;
            }

            if (!DeviceIdentity.TryClearDeviceTokenForRole(
                    target.IdentityPath,
                    "node",
                    _logger))
            {
                return;
            }

            if (!IsCurrentNodeAttempt(attempt))
                return;

            _tokenRecoveryAttemptedGatewayId = target.Record.Id;
            var fallbackLabel = hasSharedToken ? "shared gateway token" : "bootstrap token";
            _diagnostics.Record(
                "credential",
                $"Cleared stale node device token; reconnecting node with {fallbackLabel}");
            TrackBackground(ScheduleDelayedReconnectAsync(attempt));
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"[ConnMgr] Node token recovery failed: {ex.Message}");
            _diagnostics.Record("credential", "Node token recovery failed", ex.Message);
        }
    }

    private async Task ScheduleDelayedReconnectAsync(NodeAttemptStamp attempt)
    {
        try
        {
            await _reconnectDelay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            if (!IsCurrentNodeAttempt(attempt) ||
                _shutdownCts.IsCancellationRequested)
            {
                return;
            }

            await StartAsync(
                attempt.GatewayAttempt.LifecycleGeneration,
                attempt.NodeGeneration).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"[ConnMgr] Node token recovery reconnect failed: {ex.Message}");
            _diagnostics.Record(
                "credential",
                "Node token recovery reconnect failed",
                ex.Message);
        }
    }

    internal void ResetPairingRecovery()
    {
        _tokenRecoveryAttemptedGatewayId = null;
    }

    internal void RecordPreflightTelemetryFailure(ConnectionErrorCategory errorCategory)
    {
        var tags = NodeTelemetryTags("connect");
        var rootActivity = OpenClawTelemetry.StartDetachedActivity(NodeConnectSpanName, tags);
        var attempt = new TelemetryAttempt(
            Generation: 0,
            Operation: "connect",
            StartTimestamp: Stopwatch.GetTimestamp(),
            Activity: rootActivity)
        {
            PhaseActivity = rootActivity is null
                ? null
                : OpenClawTelemetry.StartDetachedActivity(
                    NodePrepareSpanName,
                    rootActivity.Context,
                    tags)
        };

        OpenClawTelemetry.Add(_connectionAttempts, tags: tags);
        FinishTelemetryAttempt(attempt, "failure", errorCategory);
    }

    internal void CancelTelemetry(string outcome, ConnectionErrorCategory? errorCategory) =>
        CancelTelemetryAttempt(outcome, errorCategory);

    internal async Task RetireAsync(
        string telemetryOutcome = "canceled",
        ConnectionErrorCategory? errorCategory = ConnectionErrorCategory.Cancelled)
    {
        await _startSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            CancelOperation();
            if (_nodeConnector is not null)
            {
                try
                {
                    await WaitWithTimeoutAsync(
                        _nodeConnector.DisconnectAsync(),
                        TimeSpan.FromSeconds(2),
                        "Node disconnect").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Warn($"[ConnMgr] Node disconnect error: {ex.Message}");
                }
            }

            lock (_operationLock)
                Interlocked.Increment(ref _nodeGeneration);
            CancelTelemetryAttempt(telemetryOutcome, errorCategory);
        }
        finally
        {
            _startSemaphore.Release();
        }
    }

    internal async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        _shutdownCts.Cancel();
        await RetireAsync("disposed", ConnectionErrorCategory.Disposed).ConfigureAwait(false);

        Task[] background;
        lock (_backgroundLock)
            background = [.. _backgroundTasks];
        if (background.Length > 0)
        {
            await Task.WhenAny(
                Task.WhenAll(background),
                Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        }
    }

    private void CancelOperation()
    {
        lock (_operationLock)
        {
            var operationCts = _operationCts;
            _operationCts = null;
            operationCts?.Cancel();
        }
    }

    private bool TryCaptureExpectedAttempt(
        long expectedLifecycleGeneration,
        long? expectedNodeGeneration,
        out NodeAttemptStamp attempt)
    {
        var gatewayAttempt = _lifecycleSource.CaptureGatewayAttempt();
        var nodeGeneration = Interlocked.Read(ref _nodeGeneration);
        attempt = new NodeAttemptStamp(gatewayAttempt, nodeGeneration);
        return Volatile.Read(ref _stopped) == 0 &&
            gatewayAttempt.LifecycleGeneration == expectedLifecycleGeneration &&
            _lifecycleSource.IsCurrentLifecycle(gatewayAttempt) &&
            (!expectedNodeGeneration.HasValue ||
             nodeGeneration == expectedNodeGeneration.Value);
    }

    private long AcquireStartGuard(long lifecycleGeneration)
    {
        var version = Interlocked.Increment(ref _startGuardVersion);
        Interlocked.Exchange(ref _startLifecycleGeneration, lifecycleGeneration);
        return version;
    }

    private NodeConnectorSnapshot CaptureConnectorSnapshot() =>
        new(
            _nodeConnector?.IsConnected == true,
            _nodeConnector?.PairingStatus ?? PairingStatus.Unknown,
            _nodeConnector?.NodeDeviceId);

    private void TrackBackground(Task task)
    {
        lock (_backgroundLock)
            _backgroundTasks.Add(task);
        _ = task.ContinueWith(
            completed =>
            {
                lock (_backgroundLock)
                    _backgroundTasks.Remove(completed);
                if (completed.IsFaulted)
                {
                    _logger.Warn(
                        $"[ConnMgr] Node coordinator background task failed: " +
                        completed.Exception!.GetBaseException().Message);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task<bool> WaitWithTimeoutAsync(
        Task task,
        TimeSpan timeout,
        string operation)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != task)
        {
            _logger.Warn($"[ConnMgr] {operation} timed out after {timeout.TotalSeconds:F1}s");
            return false;
        }

        await task.ConfigureAwait(false);
        return true;
    }

    private void ObserveTelemetryStatus(
        ConnectionStatus status,
        NodeAttemptStamp attempt)
    {
        if (!IsCurrentNodeAttempt(attempt))
            return;

        switch (status)
        {
            case ConnectionStatus.Connecting:
                if (!TransitionTelemetryPhase(attempt.NodeGeneration, NodeTransportSpanName))
                {
                    StartTelemetryAttempt(
                        attempt,
                        "reconnect",
                        NodeTransportSpanName);
                }
                break;
            case ConnectionStatus.Connected when _nodeConnector?.PairingStatus == PairingStatus.Paired:
                CompleteTelemetryAttempt(attempt.NodeGeneration, "success");
                break;
            case ConnectionStatus.Disconnected:
                CompleteTelemetryAttempt(
                    attempt.NodeGeneration,
                    "failure",
                    ConnectionErrorCategory.ServerClose);
                break;
            case ConnectionStatus.Error:
                CompleteTelemetryAttempt(
                    attempt.NodeGeneration,
                    "failure",
                    ConnectionErrorCategory.NetworkUnreachable);
                break;
        }
    }

    private void StartTelemetryAttempt(
        NodeAttemptStamp attemptStamp,
        string operation,
        string initialPhaseSpanName)
    {
        if (!IsCurrentNodeAttempt(attemptStamp))
            return;

        var tags = NodeTelemetryTags(operation);
        var rootActivity = OpenClawTelemetry.StartDetachedActivity(
            operation == "connect" ? NodeConnectSpanName : NodeReconnectSpanName,
            tags);
        var attempt = new TelemetryAttempt(
            attemptStamp.NodeGeneration,
            operation,
            Stopwatch.GetTimestamp(),
            rootActivity)
        {
            PhaseActivity = rootActivity is null
                ? null
                : OpenClawTelemetry.StartDetachedActivity(
                    initialPhaseSpanName,
                    rootActivity.Context,
                    tags),
            PhaseName = initialPhaseSpanName
        };
        TelemetryAttempt? superseded = null;
        var accepted = false;

        lock (_telemetryLock)
        {
            if (IsCurrentNodeAttempt(attemptStamp))
            {
                superseded = _telemetryAttempt;
                _telemetryAttempt = attempt;
                accepted = true;
            }
        }

        if (!accepted)
        {
            OpenClawTelemetry.Add(_connectionAttempts, tags: tags);
            FinishTelemetryAttempt(attempt, "superseded", null);
            return;
        }

        if (superseded is not null)
            FinishTelemetryAttempt(superseded, "superseded", null);
        OpenClawTelemetry.Add(_connectionAttempts, tags: tags);
    }

    private bool TransitionTelemetryPhase(long nodeGeneration, string spanName)
    {
        TelemetryAttempt attempt;
        Activity? previousPhase;
        ActivityContext parentContext;
        string operation;
        long phaseGeneration;

        lock (_telemetryLock)
        {
            if (_telemetryAttempt is not { } active ||
                active.Generation != nodeGeneration)
            {
                return false;
            }

            if (active.PhaseName == spanName)
                return true;
            if (active.Activity is null)
            {
                active.PhaseName = spanName;
                return true;
            }

            attempt = active;
            previousPhase = attempt.PhaseActivity;
            attempt.PhaseActivity = null;
            attempt.PhaseName = null;
            phaseGeneration = ++attempt.PhaseGeneration;
            parentContext = attempt.Activity.Context;
            operation = attempt.Operation;
        }

        FinishTelemetryActivity(previousPhase, "success", null);
        var nextPhase = OpenClawTelemetry.StartDetachedActivity(
            spanName,
            parentContext,
            NodeTelemetryTags(operation));

        var accepted = false;
        lock (_telemetryLock)
        {
            if (ReferenceEquals(_telemetryAttempt, attempt) &&
                attempt.PhaseGeneration == phaseGeneration)
            {
                attempt.PhaseActivity = nextPhase;
                attempt.PhaseName = spanName;
                accepted = true;
            }
        }

        if (!accepted)
            FinishTelemetryActivity(nextPhase, "superseded", null);
        return true;
    }

    private void CompleteTelemetryAttempt(
        long nodeGeneration,
        string outcome,
        ConnectionErrorCategory? errorCategory = null)
    {
        TelemetryAttempt? attempt;
        lock (_telemetryLock)
        {
            if (_telemetryAttempt is not { } active ||
                active.Generation != nodeGeneration)
            {
                return;
            }

            attempt = active;
            _telemetryAttempt = null;
        }

        FinishTelemetryAttempt(attempt, outcome, errorCategory);
    }

    private void CancelTelemetryAttempt(
        string outcome,
        ConnectionErrorCategory? errorCategory)
    {
        TelemetryAttempt? attempt;
        lock (_telemetryLock)
        {
            attempt = _telemetryAttempt;
            _telemetryAttempt = null;
        }

        if (attempt is not null)
            FinishTelemetryAttempt(attempt, outcome, errorCategory);
    }

    private void FinishTelemetryAttempt(
        TelemetryAttempt attempt,
        string outcome,
        ConnectionErrorCategory? errorCategory)
    {
        var tags = new List<OpenClawTelemetryTag>
        {
            OpenClawTelemetryTag.String(RoleTag, "node"),
            OpenClawTelemetryTag.String(OperationTag, attempt.Operation),
            OpenClawTelemetryTag.String(OpenClawTelemetryTagKey.Outcome, outcome)
        };
        if (errorCategory.HasValue)
        {
            tags.Add(OpenClawTelemetryTag.String(
                OpenClawTelemetryTagKey.ErrorCategory,
                errorCategory.Value.ToString().ToLowerInvariant()));
        }

        FinishTelemetryActivity(attempt.PhaseActivity, outcome, errorCategory);
        FinishTelemetryActivity(attempt.Activity, outcome, errorCategory, tags);
        OpenClawTelemetry.Record(
            _connectionAttemptDuration,
            Stopwatch.GetElapsedTime(attempt.StartTimestamp).TotalMilliseconds,
            tags);
    }

    private static void FinishTelemetryActivity(
        Activity? activity,
        string outcome,
        ConnectionErrorCategory? errorCategory,
        IEnumerable<OpenClawTelemetryTag>? tags = null)
    {
        if (activity is null)
            return;

        if (tags is not null)
        {
            foreach (var tag in tags)
                activity.SetTag(tag.Key, tag.Value);
        }
        else
        {
            activity.SetTag(OpenClawTelemetryTagKey.Outcome.ToTelemetryName(), outcome);
            if (errorCategory.HasValue)
            {
                activity.SetTag(
                    OpenClawTelemetryTagKey.ErrorCategory.ToTelemetryName(),
                    errorCategory.Value.ToString().ToLowerInvariant());
            }
        }

        activity.SetStatus(
            outcome is "failure" or "pairing_rejected"
                ? ActivityStatusCode.Error
                : outcome == "success"
                    ? ActivityStatusCode.Ok
                    : ActivityStatusCode.Unset);
        OpenClawTelemetry.StopDetachedActivity(activity);
    }

    private static OpenClawTelemetryTag[] NodeTelemetryTags(string operation) =>
    [
        OpenClawTelemetryTag.String(RoleTag, "node"),
        OpenClawTelemetryTag.String(OperationTag, operation),
        OpenClawTelemetryTag.String(
            OpenClawTelemetryTagKey.Source,
            "gateway_connection")
    ];

    private static ConnectionErrorCategory MapNodeConnectionErrorCategory(
        GatewayErrorKind errorKind) =>
        errorKind switch
        {
            GatewayErrorKind.Auth or
            GatewayErrorKind.TokenDrift or
            GatewayErrorKind.DeviceTokenMismatch or
            GatewayErrorKind.ScopeMismatch => ConnectionErrorCategory.AuthFailure,
            GatewayErrorKind.PairingRequired => ConnectionErrorCategory.PairingPending,
            GatewayErrorKind.PairingRejected => ConnectionErrorCategory.PairingRejected,
            GatewayErrorKind.RateLimited => ConnectionErrorCategory.RateLimited,
            GatewayErrorKind.Tunnel => ConnectionErrorCategory.SshTunnelFailure,
            GatewayErrorKind.Network or
            GatewayErrorKind.Tls => ConnectionErrorCategory.NetworkUnreachable,
            GatewayErrorKind.Server => ConnectionErrorCategory.ServerClose,
            _ => ConnectionErrorCategory.ProtocolMismatch
        };

    private static bool HasPersistedIdentityFailure(
        GatewayCredentialResolution resolution) =>
        resolution.PrimaryStatus is GatewayCredentialResolutionStatus.Unreadable
            or GatewayCredentialResolutionStatus.Corrupt
        || resolution.Status is GatewayCredentialResolutionStatus.Unreadable
            or GatewayCredentialResolutionStatus.Corrupt;

    private static string BuildIdentityFailureDetail(DeviceIdentityLoadException ex)
    {
        var cause = ex.InnerException;
        return cause is null
            ? ex.GetType().Name
            : $"{cause.GetType().Name}: {cause.Message}";
    }

    private sealed record TelemetryAttempt(
        long Generation,
        string Operation,
        long StartTimestamp,
        Activity? Activity)
    {
        public Activity? PhaseActivity { get; set; }
        public string? PhaseName { get; set; }
        public long PhaseGeneration { get; set; }
    }

    private sealed class NodeStartGuardLease
    {
        internal long? Version { get; set; }
    }
}
