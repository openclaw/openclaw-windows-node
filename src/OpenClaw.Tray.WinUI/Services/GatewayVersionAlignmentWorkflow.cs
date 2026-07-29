using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal interface IGatewayVersionAlignmentOperations
{
    string RequiredVersion { get; }
    IReadOnlyList<GatewayRollbackPointInfo> ListRollbackPoints();
    bool HasUnreadableRollbackReceipt();
    bool HasVerifiedPendingUpdate();
    GatewayUpdateProtectionMode ResolveProtectionMode(string sourceVersion);
    Task<GatewayVersionAlignmentResult> ProbeAsync(
        GatewayHostAccessPlan accessPlan,
        CancellationToken cancellationToken = default);
    Task<GatewayVersionAlignmentResult> UpdateAsync(
        GatewayHostAccessPlan accessPlan,
        CancellationToken cancellationToken = default);
    Task<int> CleanupRollbackPointsAsync(CancellationToken cancellationToken = default);
    Task<GatewayVersionAlignmentResult> RestoreAsync(
        GatewayHostAccessPlan accessPlan,
        string rollbackPointId,
        string confirmedRollbackPointId,
        CancellationToken cancellationToken = default);
    Task<GatewayVersionAlignmentResult> ResolveNativeRecoveryAsync(
        GatewayHostAccessPlan accessPlan,
        string rollbackPointId,
        string confirmedRollbackPointId,
        CancellationToken cancellationToken = default);
    GatewayVersionAlignmentResult CancelRestore(
        GatewayHostAccessPlan accessPlan,
        string rollbackPointId,
        string confirmedRollbackPointId);
}

internal interface IGatewayAlignmentConnection
{
    event EventHandler<GatewayConnectionSnapshot>? StateChanged;
    GatewayConnectionSnapshot CurrentSnapshot { get; }
    object? OperatorIdentity { get; }
    bool IsOperatorConnected { get; }
    Task ReconnectAsync();
    Task EnsureNodeConnectedAsync(CancellationToken cancellationToken);
    Task<JsonElement> SendCorrelatedRequestAsync(
        string expectedGatewayId,
        object expectedOperatorIdentity,
        string requestId,
        string method,
        object? parameters,
        int timeoutMs,
        CancellationToken cancellationToken);
}

/// <summary>
/// Owns the Companion-facing workflow around gateway version alignment:
/// single-flight scheduling, recovery/resume policy, active identity validation,
/// connection synchronization, and privileged correlated RPC dispatch.
/// </summary>
internal sealed class GatewayVersionAlignmentWorkflow
{
    private readonly Func<GatewayRecord?> _getActiveGateway;
    private readonly IGatewayAlignmentConnection _connection;
    private readonly IGatewayVersionAlignmentOperations _operations;
    private readonly Func<bool> _isNodeModeEnabled;
    private readonly Func<GatewayVersionAlignmentResult, GatewayUpdateProtectionMode, Task<bool>> _confirmUpdateAsync;
    private readonly Action<string, string> _showToast;
    private readonly Action<string> _warn;
    private string? _promptKey;
    private int _inFlight;
    private int _followUpQueued;

    internal GatewayVersionAlignmentWorkflow(
        Func<GatewayRecord?> getActiveGateway,
        IGatewayAlignmentConnection connection,
        IGatewayVersionAlignmentOperations operations,
        Func<bool> isNodeModeEnabled,
        Func<GatewayVersionAlignmentResult, GatewayUpdateProtectionMode, Task<bool>> confirmUpdateAsync,
        Action<string, string> showToast,
        Action<string>? warn = null)
    {
        _getActiveGateway = getActiveGateway ?? throw new ArgumentNullException(nameof(getActiveGateway));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _isNodeModeEnabled = isNodeModeEnabled ?? throw new ArgumentNullException(nameof(isNodeModeEnabled));
        _confirmUpdateAsync = confirmUpdateAsync ?? throw new ArgumentNullException(nameof(confirmUpdateAsync));
        _showToast = showToast ?? throw new ArgumentNullException(nameof(showToast));
        _warn = warn ?? (_ => { });
    }

    public static GatewayVersionAlignmentWorkflow Create(
        IWslCommandRunner commandRunner,
        GatewayPackageTarget target,
        GatewayRollbackPointManager rollbackPoints,
        GatewayRegistry registry,
        IGatewayConnectionManager connectionManager,
        Func<bool> isNodeModeEnabled,
        Func<GatewayRollbackRetentionPolicy> retentionPolicy,
        Func<GatewayUpdateProtectionMode> protectionMode,
        Func<GatewayVersionAlignmentResult, GatewayUpdateProtectionMode, Task<bool>> confirmUpdateAsync,
        Action<string, string> showToast,
        Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(commandRunner);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(rollbackPoints);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(connectionManager);

        GatewayVersionAlignmentWorkflow? workflow = null;
        var connection = new GatewayAlignmentConnection(connectionManager);
        var coordinator = new GatewayVersionAlignmentCoordinator(
            commandRunner,
            target,
            rollbackPoints,
            (gatewayId, cancellationToken) =>
                (workflow ?? throw new InvalidOperationException("Gateway alignment workflow is not initialized."))
                .SynchronizeAsync(gatewayId, cancellationToken),
            retentionPolicy,
            (gatewayId, requestId, method, parameters, timeoutMs, cancellationToken) =>
                (workflow ?? throw new InvalidOperationException("Gateway alignment workflow is not initialized."))
                .SendRequestAsync(gatewayId, requestId, method, parameters, timeoutMs, cancellationToken),
            () => connectionManager.CurrentSnapshot.GatewayId,
            protectionModeResolver: protectionMode);

        workflow = new GatewayVersionAlignmentWorkflow(
            registry.GetActive,
            connection,
            new GatewayVersionAlignmentOperations(coordinator),
            isNodeModeEnabled,
            confirmUpdateAsync,
            showToast,
            warn);
        return workflow;
    }

    public async Task CheckAsync(string? gatewayId)
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            Interlocked.Exchange(ref _followUpQueued, 1);
            return;
        }

        try
        {
            var activeRecord = _getActiveGateway();
            if (activeRecord == null ||
                !string.Equals(activeRecord.Id, gatewayId, StringComparison.Ordinal))
            {
                return;
            }

            var accessPlan = GatewayHostAccessClassifier.Classify(activeRecord);
            var probe = await _operations.ProbeAsync(accessPlan).ConfigureAwait(false);
            if (probe.State is GatewayVersionAlignmentState.RecoveryAvailable
                or GatewayVersionAlignmentState.VerificationFailed
                or GatewayVersionAlignmentState.VersionOrderUnknown)
            {
                var blockedKey =
                    $"blocked|{activeRecord.Id}|{probe.State}|{probe.RollbackPointId}|{probe.InstalledVersion}|{probe.RequiredVersion}";
                if (!string.Equals(_promptKey, blockedKey, StringComparison.Ordinal))
                {
                    _promptKey = blockedKey;
                    ReportResult(probe);
                }
                return;
            }

            if (probe.State == GatewayVersionAlignmentState.Aligned &&
                _operations.HasVerifiedPendingUpdate())
            {
                var resumed = await _operations.UpdateAsync(accessPlan).ConfigureAwait(false);
                ReportResult(resumed);
                if (!resumed.IsAligned)
                    _promptKey = null;
                return;
            }

            if ((probe.State is not GatewayVersionAlignmentState.Mismatch
                 and not GatewayVersionAlignmentState.NewerThanRequired) ||
                probe.InstalledVersion == null)
            {
                return;
            }

            var promptKey = $"{activeRecord.Id}|{probe.InstalledVersion}|{probe.RequiredVersion}";
            if (string.Equals(_promptKey, promptKey, StringComparison.Ordinal))
                return;

            var effectiveProtectionMode = _operations.ResolveProtectionMode(probe.InstalledVersion);
            if (!await _confirmUpdateAsync(probe, effectiveProtectionMode).ConfigureAwait(false))
            {
                _promptKey = promptKey;
                return;
            }

            var confirmedRecord = _getActiveGateway();
            var confirmedPlan = GatewayHostAccessClassifier.Classify(confirmedRecord);
            if (confirmedRecord == null ||
                !string.Equals(confirmedRecord.Id, activeRecord.Id, StringComparison.Ordinal) ||
                confirmedPlan.TerminalTarget != GatewayTerminalTarget.Wsl ||
                !confirmedPlan.CanControlWslGateway ||
                !string.Equals(confirmedPlan.DistroName, accessPlan.DistroName, StringComparison.Ordinal))
            {
                _warn("Local Gateway update canceled because the active Companion-owned Gateway changed while confirmation was open.");
                _showToast(
                    "Local Gateway update canceled",
                    "The active Gateway changed before the update started. No package change was made.");
                return;
            }

            _promptKey = promptKey;
            _showToast(
                "Updating local OpenClaw Gateway",
                "Creating a verified protection point, updating the existing WSL installation, and reconnecting Companion.");

            var result = await _operations.UpdateAsync(confirmedPlan).ConfigureAwait(false);
            ReportResult(result);
            if (!result.IsAligned)
                _promptKey = null;
        }
        catch (Exception ex)
        {
            _promptKey = null;
            _warn($"Companion-owned Gateway version alignment failed: {ex.GetType().Name}: {ex.Message}");
            _showToast(
                "Local Gateway update failed",
                "OpenClaw could not complete the in-place update. The existing distro was not recreated.");
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
            if (Interlocked.Exchange(ref _followUpQueued, 0) != 0)
                _ = CheckAsync(_getActiveGateway()?.Id);
        }
    }

    public void ResetPrompt() => _promptKey = null;

    public IReadOnlyList<GatewayRollbackPointInfo> ListRollbackPoints() =>
        _operations.ListRollbackPoints();

    public bool HasUnreadableRollbackReceipt() =>
        _operations.HasUnreadableRollbackReceipt();

    public Task<int> CleanupRollbackPointsAsync(CancellationToken cancellationToken = default) =>
        _operations.CleanupRollbackPointsAsync(cancellationToken);

    public async Task<GatewayVersionAlignmentResult> RestoreAsync(
        string rollbackPointId,
        CancellationToken cancellationToken = default)
    {
        var activeRecord = _getActiveGateway();
        if (activeRecord == null)
            return Ineligible();

        var result = await _operations.RestoreAsync(
            GatewayHostAccessClassifier.Classify(activeRecord),
            rollbackPointId,
            rollbackPointId,
            cancellationToken).ConfigureAwait(false);
        ReportResult(result);
        return result;
    }

    public async Task<GatewayVersionAlignmentResult> ResolveNativeRecoveryAsync(
        string rollbackPointId,
        CancellationToken cancellationToken = default)
    {
        var activeRecord = _getActiveGateway();
        if (activeRecord == null)
            return Ineligible();

        var result = await _operations.ResolveNativeRecoveryAsync(
            GatewayHostAccessClassifier.Classify(activeRecord),
            rollbackPointId,
            rollbackPointId,
            cancellationToken).ConfigureAwait(false);
        ReportResult(result);
        return result;
    }

    public GatewayVersionAlignmentResult CancelRestore(
        string rollbackPointId,
        string confirmedRollbackPointId)
    {
        var activeRecord = _getActiveGateway();
        if (activeRecord == null)
            return Ineligible();

        var result = _operations.CancelRestore(
            GatewayHostAccessClassifier.Classify(activeRecord),
            rollbackPointId,
            confirmedRollbackPointId);
        ReportResult(result);
        return result;
    }

    internal async Task SynchronizeAsync(
        string expectedGatewayId,
        CancellationToken cancellationToken)
    {
        var activeRecord = _getActiveGateway();
        if (activeRecord == null ||
            !string.Equals(activeRecord.Id, expectedGatewayId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The active Gateway changed during version alignment.");
        }

        var operatorReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveState(object? _, GatewayConnectionSnapshot snapshot)
        {
            if (!string.Equals(snapshot.GatewayId, expectedGatewayId, StringComparison.Ordinal))
                return;

            switch (snapshot.OperatorState)
            {
                case RoleConnectionState.Connected:
                    operatorReady.TrySetResult(true);
                    break;
                case RoleConnectionState.Error:
                case RoleConnectionState.PairingRejected:
                case RoleConnectionState.PairingRequired:
                case RoleConnectionState.RateLimited:
                    operatorReady.TrySetException(new InvalidOperationException(
                        snapshot.OperatorError ?? $"Gateway operator connection reached {snapshot.OperatorState}."));
                    break;
            }
        }

        _connection.StateChanged += ObserveState;
        try
        {
            await _connection.ReconnectAsync().ConfigureAwait(false);
            ObserveState(_connection, _connection.CurrentSnapshot);
            await operatorReady.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);

            if (_isNodeModeEnabled())
                await _connection.EnsureNodeConnectedAsync(cancellationToken).ConfigureAwait(false);

            var synchronized = _connection.CurrentSnapshot;
            if (!string.Equals(synchronized.GatewayId, expectedGatewayId, StringComparison.Ordinal) ||
                synchronized.OperatorState != RoleConnectionState.Connected ||
                (_isNodeModeEnabled() &&
                 (synchronized.NodeState != RoleConnectionState.Connected ||
                  synchronized.NodePairingStatus != PairingStatus.Paired)))
            {
                throw new InvalidOperationException(
                    "Companion connection state changed before synchronization completed.");
            }
        }
        finally
        {
            _connection.StateChanged -= ObserveState;
        }
    }

    internal Task<JsonElement> SendRequestAsync(
        string expectedGatewayId,
        string requestId,
        string method,
        object? parameters,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                _connection.CurrentSnapshot.GatewayId,
                expectedGatewayId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The active Gateway identity changed before privileged update RPC dispatch.");
        }

        var operatorIdentity = _connection.OperatorIdentity
            ?? throw new InvalidOperationException("Gateway operator client is unavailable.");
        if (!_connection.IsOperatorConnected)
            throw new InvalidOperationException("Gateway operator client is not connected.");

        return _connection.SendCorrelatedRequestAsync(
            expectedGatewayId,
            operatorIdentity,
            requestId,
            method,
            parameters,
            timeoutMs,
            cancellationToken);
    }

    private GatewayVersionAlignmentResult Ineligible() =>
        new(
            GatewayVersionAlignmentState.Ineligible,
            _operations.RequiredVersion,
            FailureSummary: "No active Companion-owned Gateway is available.");

    private void ReportResult(GatewayVersionAlignmentResult result)
    {
        switch (result.State)
        {
            case GatewayVersionAlignmentState.Updated:
                _showToast(
                    "Local OpenClaw Gateway updated",
                    $"Companion, Windows Node, and Gateway are synchronized on {result.RequiredVersion}.");
                break;
            case GatewayVersionAlignmentState.Restored:
                _showToast(
                    "Local Gateway rollback restored",
                    $"OpenClaw {result.InstalledVersion} and its complete retained state were restored and resynchronized.");
                break;
            case GatewayVersionAlignmentState.RestoreCancelled:
                _showToast(
                    "Staged Gateway restore cancelled",
                    "The non-destructive staged restore was cancelled. No WSL registration or installed Gateway state was changed.");
                break;
            case GatewayVersionAlignmentState.RecoveryResolved:
                _showToast(
                    "Native Gateway recovery resolved",
                    $"The retained backup, OpenClaw {result.InstalledVersion}, Gateway, Windows Node, and pairing state were verified without restoring or recreating the distro.");
                break;
            case GatewayVersionAlignmentState.RollbackPointFailed:
                _showToast(
                    "Local Gateway update not started",
                    "The required verified protection point could not be created, so Companion made no package change.");
                break;
            case GatewayVersionAlignmentState.RecoveryAvailable:
                _showToast(
                    "Local Gateway needs attention",
                    "The update did not complete healthy. Review the verified protection point in Settings; Companion restore is available only for Full VHD points.");
                break;
            default:
                _showToast(
                    "Local Gateway update failed",
                    result.FailureSummary ?? "The existing WSL installation was left in place.");
                break;
        }
    }

    private sealed class GatewayVersionAlignmentOperations(
        GatewayVersionAlignmentCoordinator coordinator) : IGatewayVersionAlignmentOperations
    {
        public string RequiredVersion => coordinator.RequiredVersion;
        public IReadOnlyList<GatewayRollbackPointInfo> ListRollbackPoints() => coordinator.ListRollbackPoints();
        public bool HasUnreadableRollbackReceipt() => coordinator.HasUnreadableRollbackReceipt();
        public bool HasVerifiedPendingUpdate() => coordinator.HasVerifiedPendingUpdate();
        public GatewayUpdateProtectionMode ResolveProtectionMode(string sourceVersion) =>
            coordinator.ResolveProtectionMode(sourceVersion);
        public Task<GatewayVersionAlignmentResult> ProbeAsync(
            GatewayHostAccessPlan accessPlan,
            CancellationToken cancellationToken = default) =>
            coordinator.ProbeAsync(accessPlan, cancellationToken);
        public Task<GatewayVersionAlignmentResult> UpdateAsync(
            GatewayHostAccessPlan accessPlan,
            CancellationToken cancellationToken = default) =>
            coordinator.UpdateAsync(accessPlan, cancellationToken);
        public Task<int> CleanupRollbackPointsAsync(CancellationToken cancellationToken = default) =>
            coordinator.CleanupRollbackPointsAsync(cancellationToken);
        public Task<GatewayVersionAlignmentResult> RestoreAsync(
            GatewayHostAccessPlan accessPlan,
            string rollbackPointId,
            string confirmedRollbackPointId,
            CancellationToken cancellationToken = default) =>
            coordinator.RestoreAsync(accessPlan, rollbackPointId, confirmedRollbackPointId, cancellationToken);
        public Task<GatewayVersionAlignmentResult> ResolveNativeRecoveryAsync(
            GatewayHostAccessPlan accessPlan,
            string rollbackPointId,
            string confirmedRollbackPointId,
            CancellationToken cancellationToken = default) =>
            coordinator.ResolveNativeRecoveryAsync(accessPlan, rollbackPointId, confirmedRollbackPointId, cancellationToken);
        public GatewayVersionAlignmentResult CancelRestore(
            GatewayHostAccessPlan accessPlan,
            string rollbackPointId,
            string confirmedRollbackPointId) =>
            coordinator.CancelRestore(accessPlan, rollbackPointId, confirmedRollbackPointId);
    }

    private sealed class GatewayAlignmentConnection(
        IGatewayConnectionManager manager) : IGatewayAlignmentConnection
    {
        public event EventHandler<GatewayConnectionSnapshot>? StateChanged
        {
            add => manager.StateChanged += value;
            remove => manager.StateChanged -= value;
        }

        public GatewayConnectionSnapshot CurrentSnapshot => manager.CurrentSnapshot;
        public object? OperatorIdentity => manager.OperatorClient;
        public bool IsOperatorConnected => manager.OperatorClient?.IsConnectedToGateway == true;
        public Task ReconnectAsync() => manager.ReconnectAsync();
        public Task EnsureNodeConnectedAsync(CancellationToken cancellationToken) =>
            manager.EnsureNodeConnectedAsync(cancellationToken);

        public Task<JsonElement> SendCorrelatedRequestAsync(
            string expectedGatewayId,
            object expectedOperatorIdentity,
            string requestId,
            string method,
            object? parameters,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            var client = manager.OperatorClient;
            if (!ReferenceEquals(client, expectedOperatorIdentity) ||
                !string.Equals(manager.CurrentSnapshot.GatewayId, expectedGatewayId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The active Gateway connection changed before privileged update RPC dispatch.");
            }

            return client.SendCorrelatedRequestAsync(
                requestId,
                method,
                parameters,
                timeoutMs,
                cancellationToken);
        }
    }
}
