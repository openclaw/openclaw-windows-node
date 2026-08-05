using OpenClaw.Shared;

namespace OpenClaw.Connection;

internal sealed record OperatorApprovalGatewayLease(
    NodeAttemptStamp Attempt,
    IOperatorGatewayClient Gateway);

internal interface IOperatorApprovalGatewayLeaseSource
{
    OperatorApprovalGatewayLease? TryAcquireOperatorApprovalGateway(
        NodeAttemptStamp attempt);
}

internal enum DevicePairApprovalOutcome
{
    Approved,
    SkippedNoAdminScope,
    RejectedByGateway,
    Superseded,
    RetryQueued
}

internal sealed class DevicePairApprovalCoordinator
{
    private readonly INodePairReconnectPort _nodeCoordinator;
    private readonly IOperatorApprovalGatewayLeaseSource _gatewayLeases;
    private readonly IOpenClawLogger _logger;
    private readonly ConnectionDiagnostics _diagnostics;
    private readonly Func<TimeSpan, Task> _reconnectDelay;
    private readonly object _reconnectLock = new();
    private readonly object _backgroundLock = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Dictionary<string, int> _reconnectAttempts =
        new(StringComparer.Ordinal);
    private readonly HashSet<Task> _backgroundTasks = [];

    private ApprovedRequest? _lastAutoApprovedRequest;
    private AutoApproveLease? _autoApproveInFlight;
    private bool _reconnectInFlight;
    private QueuedReconnect? _queuedReconnect;
    private long _reconnectWorkflowVersion;
    private int _stopped;

    internal DevicePairApprovalCoordinator(
        INodePairReconnectPort nodeCoordinator,
        IOperatorApprovalGatewayLeaseSource gatewayLeases,
        IOpenClawLogger logger,
        ConnectionDiagnostics diagnostics,
        Func<TimeSpan, Task> reconnectDelay)
    {
        _nodeCoordinator = nodeCoordinator;
        _gatewayLeases = gatewayLeases;
        _logger = logger;
        _diagnostics = diagnostics;
        _reconnectDelay = reconnectDelay;
    }

    internal void HandlePairingStatus(
        PairingStatusEventArgs pairing,
        NodeAttemptStamp attempt)
    {
        if (Volatile.Read(ref _stopped) != 0 ||
            !_nodeCoordinator.IsCurrentNodeAttempt(attempt))
        {
            return;
        }

        if (pairing.Status == PairingStatus.Paired)
        {
            ResetAfterPairing();
            return;
        }

        if (pairing.Status != PairingStatus.Pending ||
            string.IsNullOrWhiteSpace(pairing.RequestId))
        {
            return;
        }

        if (pairing.ApprovalKind == PairingApprovalKind.DevicePair)
        {
            _diagnostics.Record(
                "node",
                "Node device role-upgrade pending",
                $"requestId={pairing.RequestId}");
            TrackBackground(HandleDevicePairPendingAsync(
                pairing.RequestId,
                attempt));
        }
        else
        {
            _diagnostics.Record(
                "node",
                "Node command-trust request is awaiting explicit operator approval",
                $"requestId={pairing.RequestId}");
        }
    }

    internal void HandleNodePairListUpdated(
        PairingListInfo list,
        GatewayAttemptStamp operatorAttempt,
        NodeAttemptStamp nodeAttempt,
        string? nodeDeviceId)
    {
        if (Volatile.Read(ref _stopped) != 0 ||
            string.IsNullOrWhiteSpace(nodeDeviceId) ||
            nodeAttempt.GatewayAttempt != operatorAttempt ||
            !_nodeCoordinator.IsCurrentNodeAttempt(nodeAttempt))
        {
            return;
        }

        var request = list.Pending.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(p.RequestId) &&
            string.Equals(
                p.NodeId,
                nodeDeviceId,
                StringComparison.OrdinalIgnoreCase));
        if (request is null)
            return;

        _diagnostics.Record(
            "node",
            "Local node command-trust request is awaiting explicit operator approval",
            $"requestId={request.RequestId}");

        var lease = ReacquireGateway(nodeAttempt);
        if (lease is null)
            return;

        TrackBackground(RequestNodesAsync(lease, nodeAttempt));
    }

    private async Task RequestNodesAsync(
        OperatorApprovalGatewayLease lease,
        NodeAttemptStamp attempt)
    {
        try
        {
            if (!IsLeaseCurrent(lease, attempt))
                return;

            await lease.Gateway.RequestNodesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn(
                $"[ConnMgr] Node list refresh failed after local node trust request: {ex.Message}");
        }
    }

    private async Task HandleDevicePairPendingAsync(
        string requestId,
        NodeAttemptStamp attempt)
    {
        if (!_nodeCoordinator.IsCurrentNodeAttempt(attempt))
            return;

        if (string.Equals(
                requestId,
                Volatile.Read(ref _lastAutoApprovedRequest)?.RequestId,
                StringComparison.Ordinal) &&
            Volatile.Read(ref _lastAutoApprovedRequest)?.GatewayAttempt ==
                attempt.GatewayAttempt)
        {
            await ReconnectAfterApprovedDevicePairAsync(
                requestId,
                attempt).ConfigureAwait(false);
            return;
        }

        await AutoApproveDevicePairingRequestAsync(
            requestId,
            attempt).ConfigureAwait(false);
    }

    private async Task<DevicePairApprovalOutcome> AutoApproveDevicePairingRequestAsync(
        string requestId,
        NodeAttemptStamp attempt)
    {
        if (Volatile.Read(ref _lastAutoApprovedRequest) is
                { } approvedRequest &&
            approvedRequest.RequestId == requestId &&
            approvedRequest.GatewayAttempt == attempt.GatewayAttempt ||
            !_nodeCoordinator.IsCurrentNodeAttempt(attempt))
        {
            return DevicePairApprovalOutcome.Superseded;
        }

        var approvalLease = new AutoApproveLease(requestId, attempt);
        if (Interlocked.CompareExchange(
                ref _autoApproveInFlight,
                approvalLease,
                null) is not null)
        {
            return DevicePairApprovalOutcome.Superseded;
        }

        var attemptedApprove = false;
        var approved = false;
        var outcome = DevicePairApprovalOutcome.Superseded;
        try
        {
            var lease = ReacquireGateway(attempt);
            if (lease is null)
                return DevicePairApprovalOutcome.Superseded;

            var scopes = lease.Gateway.GrantedOperatorScopes;
            if (!OperatorScopeHelper.HasAdminScope(scopes))
            {
                _diagnostics.Record(
                    "node",
                    "Device role-upgrade auto-approval skipped",
                    BuildDeviceAutoApprovalFailureDetail(scopes));
                return DevicePairApprovalOutcome.SkippedNoAdminScope;
            }

            if (!IsLeaseCurrent(lease, attempt))
                return DevicePairApprovalOutcome.Superseded;

            _diagnostics.Record(
                "node",
                $"Auto-approving device role-upgrade pairing (requestId={requestId})");
            try
            {
                attemptedApprove = true;
                approved = await lease.Gateway
                    .DevicePairApproveAsync(requestId)
                    .ConfigureAwait(false);
                if (!approved)
                {
                    _diagnostics.Record(
                        "node",
                        "Device role-upgrade auto-approval failed",
                        BuildDeviceAutoApprovalFailureDetail(scopes));
                    outcome = DevicePairApprovalOutcome.RejectedByGateway;
                }
                else
                {
                    outcome = DevicePairApprovalOutcome.Approved;
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(
                    $"[ConnMgr] Device role-upgrade auto-approve failed: {ex.Message}");
                _diagnostics.Record(
                    "node",
                    $"Device role-upgrade auto-approve error: {ex.Message}");
                outcome = DevicePairApprovalOutcome.RejectedByGateway;
            }
        }
        finally
        {
            if (attemptedApprove &&
                approved &&
                _nodeCoordinator.IsCurrentNodeAttempt(attempt) &&
                ReacquireGateway(attempt) is not null)
            {
                Volatile.Write(
                    ref _lastAutoApprovedRequest,
                    new ApprovedRequest(requestId, attempt.GatewayAttempt));
            }
            Interlocked.CompareExchange(
                ref _autoApproveInFlight,
                null,
                approvalLease);
        }

        if (approved &&
            _nodeCoordinator.IsCurrentNodeAttempt(attempt) &&
            ReacquireGateway(attempt) is not null)
        {
            await ReconnectAfterApprovedDevicePairAsync(
                requestId,
                attempt).ConfigureAwait(false);
        }

        return outcome;
    }

    private async Task<DevicePairApprovalOutcome> ReconnectAfterApprovedDevicePairAsync(
        string requestId,
        NodeAttemptStamp attempt)
    {
        if (!_nodeCoordinator.IsCurrentNodeAttempt(attempt) ||
            ReacquireGateway(attempt) is null)
        {
            return DevicePairApprovalOutcome.Superseded;
        }

        var ownsReconnect = false;
        var queuedRetry = false;
        long workflowVersion;
        lock (_reconnectLock)
        {
            workflowVersion = _reconnectWorkflowVersion;
            _reconnectAttempts.TryGetValue(requestId, out var attemptCount);
            if (attemptCount >= 2)
                return DevicePairApprovalOutcome.Superseded;

            if (_reconnectInFlight)
            {
                if (_queuedReconnect is null)
                {
                    _reconnectAttempts[requestId] = attemptCount + 1;
                    _queuedReconnect = new QueuedReconnect(requestId, attempt);
                    queuedRetry = true;
                }
            }
            else
            {
                _reconnectAttempts[requestId] = attemptCount + 1;
                _reconnectInFlight = true;
                ownsReconnect = true;
            }
        }

        if (!ownsReconnect)
        {
            if (queuedRetry)
                _diagnostics.Record(
                    "node",
                    "Device role-upgrade reconnect retry queued");
            return queuedRetry
                ? DevicePairApprovalOutcome.RetryQueued
                : DevicePairApprovalOutcome.Superseded;
        }

        var guardOwned = true;
        try
        {
            var startedAttempt = await RunReconnectAttemptAsync(attempt)
                .ConfigureAwait(false);
            AdvanceQueuedNodeGeneration(attempt, startedAttempt);

            while (true)
            {
                QueuedReconnect? queued;
                lock (_reconnectLock)
                {
                    if (_reconnectWorkflowVersion != workflowVersion)
                    {
                        guardOwned = false;
                        return DevicePairApprovalOutcome.Superseded;
                    }

                    queued = _queuedReconnect;
                    _queuedReconnect = null;
                    if (queued is null)
                    {
                        _reconnectInFlight = false;
                        guardOwned = false;
                        return DevicePairApprovalOutcome.Approved;
                    }
                }

                if (!_nodeCoordinator.IsCurrentNodeAttempt(queued.Attempt) ||
                    ReacquireGateway(queued.Attempt) is null)
                {
                    continue;
                }

                _diagnostics.Record(
                    "node",
                    "Retrying device role-upgrade reconnect after repeated pending signal",
                    $"requestId={queued.RequestId}");
                startedAttempt = await RunReconnectAttemptAsync(queued.Attempt)
                    .ConfigureAwait(false);
                AdvanceQueuedNodeGeneration(queued.Attempt, startedAttempt);
            }
        }
        finally
        {
            if (guardOwned)
            {
                lock (_reconnectLock)
                {
                    if (_reconnectWorkflowVersion == workflowVersion)
                    {
                        _reconnectInFlight = false;
                        _queuedReconnect = null;
                    }
                }
            }
        }
    }

    private async Task<NodeAttemptStamp?> RunReconnectAttemptAsync(
        NodeAttemptStamp attempt)
    {
        _diagnostics.Record(
            "node",
            "Device role-upgrade pairing approved — reconnecting node");
        await _reconnectDelay(TimeSpan.FromMilliseconds(1000)).ConfigureAwait(false);

        if (_shutdownCts.IsCancellationRequested ||
            !_nodeCoordinator.IsCurrentNodeAttempt(attempt) ||
            ReacquireGateway(attempt) is null)
        {
            return null;
        }

        var result = await _nodeCoordinator.StartAsync(
            attempt.GatewayAttempt.LifecycleGeneration,
            attempt.NodeGeneration).ConfigureAwait(false);
        return result.Outcome == NodeStartOutcome.Started
            ? result.Attempt
            : null;
    }

    private void AdvanceQueuedNodeGeneration(
        NodeAttemptStamp previousAttempt,
        NodeAttemptStamp? startedAttempt)
    {
        if (!startedAttempt.HasValue)
            return;

        lock (_reconnectLock)
        {
            if (_queuedReconnect is { } queued &&
                queued.Attempt == previousAttempt)
            {
                _queuedReconnect = queued with { Attempt = startedAttempt.Value };
            }
        }
    }

    internal void Reset()
    {
        Volatile.Write(ref _lastAutoApprovedRequest, null);
        Interlocked.Exchange(ref _autoApproveInFlight, null);
        lock (_reconnectLock)
        {
            _reconnectWorkflowVersion++;
            _reconnectAttempts.Clear();
            _reconnectInFlight = false;
            _queuedReconnect = null;
        }
    }

    private void ResetAfterPairing()
    {
        Reset();
    }

    internal async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        _shutdownCts.Cancel();
        Reset();

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

    private OperatorApprovalGatewayLease? ReacquireGateway(
        NodeAttemptStamp attempt)
    {
        if (Volatile.Read(ref _stopped) != 0 ||
            !_nodeCoordinator.IsCurrentNodeAttempt(attempt))
        {
            return null;
        }

        var lease = _gatewayLeases.TryAcquireOperatorApprovalGateway(attempt);
        return lease is not null &&
               lease.Gateway.IsConnectedToGateway &&
               IsLeaseCurrent(lease, attempt)
            ? lease
            : null;
    }

    private bool IsLeaseCurrent(
        OperatorApprovalGatewayLease lease,
        NodeAttemptStamp attempt) =>
        lease.Attempt == attempt &&
        _nodeCoordinator.IsCurrentNodeAttempt(attempt) &&
        ReferenceEquals(
            _gatewayLeases.TryAcquireOperatorApprovalGateway(attempt)?.Gateway,
            lease.Gateway);

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
                        $"[ConnMgr] Device-pair coordinator background task failed: " +
                        completed.Exception!.GetBaseException().Message);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string BuildDeviceAutoApprovalFailureDetail(
        IReadOnlyList<string> scopes) =>
        OperatorScopeHelper.HasAdminScope(scopes)
            ? "Gateway rejected device.pair.approve; check requestId and gateway device-pair state."
            : "Operator token lacks operator.admin for device.pair.approve role-upgrade approval.";

    private sealed record QueuedReconnect(
        string RequestId,
        NodeAttemptStamp Attempt);

    private sealed record ApprovedRequest(
        string RequestId,
        GatewayAttemptStamp GatewayAttempt);

    private sealed record AutoApproveLease(
        string RequestId,
        NodeAttemptStamp Attempt);
}
