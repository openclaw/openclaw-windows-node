using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Shared;

namespace OpenClaw.Connection;

public enum GatewayVersionAlignmentState
{
    Ineligible,
    Busy,
    ProbeFailed,
    Aligned,
    Mismatch,
    NewerThanRequired,
    VersionOrderUnknown,
    PreUpdateHealthFailed,
    RollbackPointFailed,
    UpdateFailed,
    VerificationFailed,
    SynchronizationFailed,
    RecoveryAvailable,
    RestoreConfirmationRequired,
    RestoreFailed,
    RestoreVerificationFailed,
    RestoreCancelled,
    RecoveryResolved,
    Restored,
    Updated
}

public sealed record GatewayVersionAlignmentResult(
    GatewayVersionAlignmentState State,
    string RequiredVersion,
    string? InstalledVersion = null,
    string? PreviousVersion = null,
    string? RollbackPointId = null,
    int? ExitCode = null,
    string? FailureSummary = null)
{
    public bool IsAligned => State is GatewayVersionAlignmentState.Aligned or GatewayVersionAlignmentState.Updated;
}

/// <summary>
/// Aligns OpenClaw inside an existing Companion-owned WSL distro. Normal update
/// creates a verified native backup, durably arms one route, and dispatches
/// either the shared Companion installer or an explicitly audited Core
/// transaction. Full VHD protection is opt-in. WSL unregister/import is isolated
/// to RestoreAsync and requires an explicit rollback-point confirmation.
/// </summary>
public sealed partial class GatewayVersionAlignmentCoordinator
{
    private const string NativeBackupMinimumSourceVersion = "2026.7.2";
    private static readonly TimeSpan InstallerDispatchSettleDelay = TimeSpan.FromMinutes(2);
    private readonly IWslCommandRunner _commandRunner;
    private readonly GatewayRollbackPointManager _rollbackPoints;
    private readonly GatewayPackageTarget _target;
    private readonly GatewayPackageUpdateRoutePolicy _routePolicy;
    private readonly string _requiredVersion;
    private readonly Func<string, CancellationToken, Task> _synchronizeAsync;
    private readonly Func<GatewayRollbackRetentionPolicy> _retentionPolicy;
    private readonly Func<string, string, string, object?, int, CancellationToken, Task<JsonElement>>? _gatewayRequestAsync;
    private readonly Func<string?> _connectedGatewayId;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<GatewayUpdateProtectionMode> _protectionModeResolver;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public GatewayVersionAlignmentCoordinator(
        IWslCommandRunner commandRunner,
        string requiredVersion,
        GatewayRollbackPointManager rollbackPoints,
        Func<string, CancellationToken, Task>? synchronizeAsync = null,
        Func<GatewayRollbackRetentionPolicy>? retentionPolicy = null,
        Func<string, string, string, object?, int, CancellationToken, Task<JsonElement>>? gatewayRequestAsync = null,
        Func<string?>? connectedGatewayId = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<GatewayUpdateProtectionMode>? protectionModeResolver = null)
        : this(
            commandRunner,
            GatewayPackageTarget.Official(requiredVersion),
            rollbackPoints,
            synchronizeAsync,
            retentionPolicy,
            gatewayRequestAsync,
            connectedGatewayId,
            utcNow: utcNow,
            protectionModeResolver: protectionModeResolver)
    {
    }

    public GatewayVersionAlignmentCoordinator(
        IWslCommandRunner commandRunner,
        GatewayPackageTarget target,
        GatewayRollbackPointManager rollbackPoints,
        Func<string, CancellationToken, Task>? synchronizeAsync = null,
        Func<GatewayRollbackRetentionPolicy>? retentionPolicy = null,
        Func<string, string, string, object?, int, CancellationToken, Task<JsonElement>>? gatewayRequestAsync = null,
        Func<string?>? connectedGatewayId = null,
        GatewayPackageUpdateRoutePolicy? routePolicy = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<GatewayUpdateProtectionMode>? protectionModeResolver = null)
    {
        ArgumentNullException.ThrowIfNull(commandRunner);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(rollbackPoints);

        _commandRunner = commandRunner;
        _target = target;
        _routePolicy = routePolicy ?? new GatewayPackageUpdateRoutePolicy();
        _rollbackPoints = rollbackPoints;
        _requiredVersion = target.ExpectedVersion;
        _synchronizeAsync = synchronizeAsync ?? ((_, _) => Task.CompletedTask);
        _retentionPolicy = retentionPolicy ?? (() => GatewayRollbackRetentionPolicy.Default);
        _gatewayRequestAsync = gatewayRequestAsync;
        _connectedGatewayId = connectedGatewayId ?? (() => null);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _protectionModeResolver = protectionModeResolver ?? (() => GatewayUpdateProtectionMode.NativeBackup);
    }

    public string RequiredVersion => _requiredVersion;

    public IReadOnlyList<GatewayRollbackPointInfo> ListRollbackPoints() => _rollbackPoints.List();

    public bool HasUnreadableRollbackReceipt() => _rollbackPoints.HasUnreadableReceipt();

    public bool HasVerifiedPendingUpdate() =>
        _rollbackPoints.FindPendingUpdates().Any(IsVerifiedProtectionPoint);

    public GatewayUpdateProtectionMode ResolveProtectionMode(string sourceVersion) =>
        ResolveEffectiveProtectionMode(_protectionModeResolver(), sourceVersion);

    public async Task<GatewayVersionAlignmentResult> ProbeAsync(
        GatewayHostAccessPlan accessPlan,
        CancellationToken cancellationToken = default)
    {
        if (!_operationGate.Wait(0))
            return Result(GatewayVersionAlignmentState.Busy);

        try
        {
            if (!TryGetEligibleDistro(accessPlan, out var distroName) ||
                !string.Equals(distroName, _rollbackPoints.OwnedDistroName, StringComparison.Ordinal))
            {
                return Result(GatewayVersionAlignmentState.Ineligible, failureSummary: "Gateway is not a proven Companion-owned WSL gateway.");
            }
            var restoreGate = GetUnresolvedRestoreGate();
            if (restoreGate is not null)
                return restoreGate;
            var pendingUpdateGate = GetPendingUpdateProbeGate(accessPlan.GatewayId!);
            if (pendingUpdateGate is not null)
                return pendingUpdateGate;
            return await ProbeCoreAsync(distroName, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<GatewayVersionAlignmentResult> UpdateAsync(
        GatewayHostAccessPlan accessPlan,
        CancellationToken cancellationToken = default)
    {
        if (!_operationGate.Wait(0))
            return Result(GatewayVersionAlignmentState.Busy);

        try
        {
            if (!TryGetEligibleDistro(accessPlan, out var distroName) ||
                !string.Equals(distroName, _rollbackPoints.OwnedDistroName, StringComparison.Ordinal))
            {
                return Result(GatewayVersionAlignmentState.Ineligible, failureSummary: "Gateway is not the expected Companion-owned WSL distro.");
            }

            var gatewayId = accessPlan.GatewayId!;
            var restoreGate = GetUnresolvedRestoreGate();
            if (restoreGate is not null)
                return restoreGate;

            var pendingUpdates = _rollbackPoints.FindPendingUpdates();
            if (pendingUpdates.Count > 1)
            {
                return Result(
                    GatewayVersionAlignmentState.VerificationFailed,
                    failureSummary: "Multiple verified pending Gateway update receipts exist for the Companion-owned distro. Recovery is ambiguous and no package probe or update was attempted.");
            }

            var before = await ProbeCoreAsync(distroName, cancellationToken).ConfigureAwait(false);
            var pending = pendingUpdates.SingleOrDefault();
            if (pending is not null &&
                !string.Equals(pending.GatewayId, gatewayId, StringComparison.Ordinal))
            {
                return Result(
                    GatewayVersionAlignmentState.RecoveryAvailable,
                    before.InstalledVersion,
                    pending.OpenClawVersion,
                    pending.Id,
                    before.ExitCode,
                    "A verified pending update belongs to an earlier Gateway record for this Companion-owned distro. Explicit recovery is required before another update.");
            }
            if (pending is not null &&
                !string.Equals(pending.TargetOpenClawVersion, _requiredVersion, StringComparison.Ordinal))
            {
                return Result(
                    GatewayVersionAlignmentState.RecoveryAvailable,
                    before.InstalledVersion,
                    pending.OpenClawVersion,
                    pending.Id,
                    before.ExitCode,
                    $"A verified pending update targets OpenClaw {pending.TargetOpenClawVersion}; explicit recovery is required before aligning to {_requiredVersion}.");
            }
            var pendingRoute = default(GatewayPackageUpdateRoute);
            var pendingState = default(GatewayUpdateDispatchState);
            CoreUpdateTransaction? pendingTransaction = null;
            if (pending is not null &&
                !TryGetPendingDispatch(
                    pending,
                    requireCurrentTarget: true,
                    out pendingRoute,
                    out pendingState,
                    out pendingTransaction))
            {
                return Result(
                    GatewayVersionAlignmentState.RecoveryAvailable,
                    before.InstalledVersion,
                    pending.OpenClawVersion,
                    pending.Id,
                    before.ExitCode,
                    "The pending update predates durable package-target and dispatch provenance. A second dispatch or cross-lane fallback was blocked; explicit recovery is required.");
            }
            if (pending is not null &&
                pendingRoute != _routePolicy.Select(pending.OpenClawVersion, _target))
            {
                return Result(
                    GatewayVersionAlignmentState.RecoveryAvailable,
                    before.InstalledVersion,
                    pending.OpenClawVersion,
                    pending.Id,
                    before.ExitCode,
                    "The pending update route no longer matches the immutable package target policy. Cross-lane fallback was blocked; explicit recovery is required.");
            }
            if (before.State == GatewayVersionAlignmentState.Aligned && pending is not null)
            {
                if (!await _rollbackPoints.VerifyAsync(pending.Id, cancellationToken).ConfigureAwait(false) ||
                    !await _rollbackPoints.AttestLiveDistroAsync(
                        pending.Id, distroName, _requiredVersion, cancellationToken).ConfigureAwait(false))
                {
                    return Result(
                        GatewayVersionAlignmentState.VerificationFailed,
                        before.InstalledVersion,
                        pending.OpenClawVersion,
                        pending.Id,
                        before.ExitCode,
                        "The aligned Companion-owned distro no longer matches its pending rollback receipt, so finalization was blocked.");
                }
                if (pendingRoute == GatewayPackageUpdateRoute.CoreTransaction &&
                    (pendingState != GatewayUpdateDispatchState.Accepted || pendingTransaction is null))
                {
                    return Result(
                        GatewayVersionAlignmentState.RecoveryAvailable,
                        before.InstalledVersion,
                        pending.OpenClawVersion,
                        pending.Id,
                        before.ExitCode,
                        "The aligned Core update receipt has no accepted transaction provenance. A second update.run was blocked because the earlier response may have been lost; explicit recovery is required.");
                }
                return await FinalizePostUpdateAsync(
                    distroName, gatewayId, before.InstalledVersion!, pending.OpenClawVersion, pending.Id,
                    pending.NodeCommandAllowSnapshotJson, pendingTransaction,
                    before.ExitCode, cancellationToken)
                    .ConfigureAwait(false);
            }
            if ((before.State is not GatewayVersionAlignmentState.Mismatch
                 and not GatewayVersionAlignmentState.NewerThanRequired) ||
                before.InstalledVersion is null)
                return before;

            var previousVersion = pending?.OpenClawVersion ?? before.InstalledVersion;
            var route = _routePolicy.Select(before.InstalledVersion, _target);

            GatewayRollbackPointManifest rollbackPoint;
            if (pending is not null)
            {
                if (!await _rollbackPoints.VerifyAsync(pending.Id, cancellationToken).ConfigureAwait(false))
                {
                    return Result(
                        GatewayVersionAlignmentState.VerificationFailed,
                        before.InstalledVersion,
                        previousVersion,
                        pending.Id,
                        failureSummary: "The pending update's pre-update protection no longer verifies, so retry was blocked.");
                }
                if (!await _rollbackPoints.AttestLiveDistroAsync(
                        pending.Id, distroName, pending.OpenClawVersion, cancellationToken).ConfigureAwait(false))
                {
                    return Result(
                        GatewayVersionAlignmentState.VerificationFailed,
                        before.InstalledVersion,
                        previousVersion,
                        pending.Id,
                        failureSummary: "The live Companion-owned distro no longer matches the pending rollback receipt, so retry was blocked.");
                }
                rollbackPoint = pending;
                return Result(
                    GatewayVersionAlignmentState.RecoveryAvailable,
                    before.InstalledVersion,
                    previousVersion,
                    pending.Id,
                    before.ExitCode,
                    "The pending update receipt proves update dispatch was armed, but the installed version is not aligned. A second non-idempotent update.run was blocked; explicit recovery is required.");
            }
            else
            {
                try
                {
                    if (route == GatewayPackageUpdateRoute.CoreTransaction)
                        await _synchronizeAsync(gatewayId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return Result(
                        GatewayVersionAlignmentState.PreUpdateHealthFailed,
                        previousVersion,
                        previousVersion,
                        failureSummary: $"Pre-update Gateway, Companion, Node, or pairing health failed: {ex.GetType().Name}.");
                }

                var policySnapshot = await CaptureNodeCommandPolicyAsync(
                    distroName, previousVersion, _requiredVersion, cancellationToken).ConfigureAwait(false);
                if (policySnapshot.Failure is not null)
                {
                    return Result(
                        GatewayVersionAlignmentState.PreUpdateHealthFailed,
                        previousVersion,
                        previousVersion,
                        failureSummary: policySnapshot.Failure);
                }

                GatewayRollbackOperationResult rollback;
                try
                {
                    var protectionMode = ResolveEffectiveProtectionMode(
                        _protectionModeResolver(),
                        previousVersion);
                    if (!Enum.IsDefined(protectionMode))
                    {
                        return Result(
                            GatewayVersionAlignmentState.RollbackPointFailed,
                            previousVersion,
                            previousVersion,
                            failureSummary: "The Gateway update protection policy returned an unsupported mode.");
                    }
                    rollback = await _rollbackPoints.CreateVerifiedAsync(
                        distroName,
                        gatewayId,
                        previousVersion,
                        _requiredVersion,
                        protectionMode,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await TryRestoreExistingRuntimeAvailabilityAsync(
                        distroName, gatewayId, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                catch (Exception ex)
                {
                    await TryRestoreExistingRuntimeAvailabilityAsync(
                        distroName, gatewayId, CancellationToken.None).ConfigureAwait(false);
                    return Result(
                        GatewayVersionAlignmentState.RollbackPointFailed,
                        previousVersion,
                        previousVersion,
                        failureSummary: $"Rollback point creation stopped before update: {ex.GetType().Name}.");
                }
                if (!rollback.Success || rollback.Point is null ||
                    !await _rollbackPoints.VerifyAsync(rollback.Point.Id, cancellationToken).ConfigureAwait(false))
                {
                    await TryRestoreExistingRuntimeAvailabilityAsync(distroName, gatewayId, cancellationToken).ConfigureAwait(false);
                    return Result(
                        GatewayVersionAlignmentState.RollbackPointFailed,
                        previousVersion,
                        previousVersion,
                        rollback.Point?.Id,
                        rollback.ExitCode,
                        "Verified pre-update protection could not be created, so no update was attempted.");
                }
                rollbackPoint = rollback.Point;
                if (policySnapshot.NormalizedArrayJson is not null)
                {
                    rollbackPoint = _rollbackPoints.RecordNodeCommandAllowSnapshot(
                        rollbackPoint.Id, policySnapshot.NormalizedArrayJson);
                }
            }

            var pointId = rollbackPoint.Id;
            if (!await _rollbackPoints.AttestLiveDistroAsync(
                    pointId, distroName, before.InstalledVersion, cancellationToken).ConfigureAwait(false))
            {
                return Result(
                    GatewayVersionAlignmentState.VerificationFailed,
                    before.InstalledVersion,
                    previousVersion,
                    pointId,
                    failureSummary: "The live Companion-owned distro changed before package mutation, so the update was blocked.");
            }
            if (rollbackPoint.NodeCommandAllowSnapshotJson is { } expectedPolicy)
            {
                var currentPolicy = await CaptureNodeCommandPolicyAsync(
                    distroName, previousVersion, _requiredVersion, cancellationToken).ConfigureAwait(false);
                if (currentPolicy.Failure is not null ||
                    !string.Equals(currentPolicy.NormalizedArrayJson, expectedPolicy, StringComparison.Ordinal))
                {
                    return Result(
                        GatewayVersionAlignmentState.VerificationFailed,
                        before.InstalledVersion,
                        previousVersion,
                        pointId,
                        failureSummary: "The complete Gateway node command allowlist changed before package mutation. The update receipt was preserved and no updater command was invoked.");
                }
            }

            var requestId = route == GatewayPackageUpdateRoute.CoreTransaction
                ? $"windows-companion-gateway-update-{pointId}"
                : null;
            _rollbackPoints.ArmUpdateDispatch(pointId, _target, route, requestId);
            if (!await _rollbackPoints.AttestLiveDistroAsync(
                    pointId, distroName, before.InstalledVersion, cancellationToken).ConfigureAwait(false))
            {
                return Result(
                    GatewayVersionAlignmentState.VerificationFailed,
                    before.InstalledVersion,
                    previousVersion,
                    pointId,
                    failureSummary: "The live Companion-owned distro changed after update dispatch was durably armed. The receipt was preserved and no package mutation was invoked.");
            }

            CoreUpdateTransaction? transaction = null;
            if (route == GatewayPackageUpdateRoute.CoreTransaction)
            {
                var transactionStart = await BeginCoreUpdateTransactionAsync(
                    gatewayId, requestId!, cancellationToken).ConfigureAwait(false);
                if (transactionStart.Transaction is null)
                {
                    _rollbackPoints.MarkUpdateDispatchAmbiguous(pointId);
                    var current = await ProbeInstalledVersionAsync(distroName, cancellationToken).ConfigureAwait(false);
                    await TrySynchronizeAsync(gatewayId, cancellationToken).ConfigureAwait(false);
                    return Result(
                        GatewayVersionAlignmentState.RecoveryAvailable,
                        current.Version,
                        previousVersion,
                        pointId,
                        current.ExitCode,
                        transactionStart.Failure);
                }
                transaction = transactionStart.Transaction;
                _rollbackPoints.RecordCoreUpdateAccepted(
                    pointId,
                    transaction.TransactionId,
                    transaction.ConfirmDeadline);
            }
            else
            {
                var install = await _commandRunner.RunInDistroAsync(
                    distroName,
                    GatewayVersionAlignmentCommandBuilder.BuildVerifiedInstaller(_target, pointId),
                    cancellationToken).ConfigureAwait(false);
                if (install.TimedOut || install.ExitCode == -1)
                    _rollbackPoints.MarkUpdateDispatchAmbiguous(pointId);
                else
                    _rollbackPoints.MarkInstallerDispatchAccepted(pointId);
                if (!install.Success)
                {
                    var current = await ProbeInstalledVersionAsync(distroName, cancellationToken).ConfigureAwait(false);
                    await TrySynchronizeAsync(gatewayId, cancellationToken).ConfigureAwait(false);
                    return Result(
                        GatewayVersionAlignmentState.RecoveryAvailable,
                        current.Version,
                        previousVersion,
                        pointId,
                        install.ExitCode,
                        install.TimedOut
                            ? "The Companion installer exceeded the Windows dispatch timeout. Its state is ambiguous, and the armed receipt blocks a second install until explicit recovery."
                            : $"The Companion installer failed with exit code {install.ExitCode}. The armed receipt and protection evidence block a second install until explicit recovery.");
                }
            }

            var after = await ProbeInstalledVersionAsync(distroName, cancellationToken).ConfigureAwait(false);
            if (after.Failure is not null || !string.Equals(after.Version, _requiredVersion, StringComparison.Ordinal))
            {
                await TrySynchronizeAsync(gatewayId, cancellationToken).ConfigureAwait(false);
                var completionFailure = await TryCompleteCoreUpdateAsync(
                    gatewayId,
                    distroName,
                    pointId,
                    transaction,
                    "failed",
                    after.Version,
                    cancellationToken).ConfigureAwait(false);
                return Result(
                    GatewayVersionAlignmentState.RecoveryAvailable,
                    after.Version,
                    previousVersion,
                    pointId,
                    after.ExitCode,
                    AppendCompletionFailure(
                        "The installed version could not be verified exactly after update. The transaction receipt and verified protection evidence were preserved for manual recovery; no automatic native-backup restore was attempted.",
                        completionFailure));
            }

            return await FinalizePostUpdateAsync(
                distroName, gatewayId, after.Version!, previousVersion, pointId,
                rollbackPoint.NodeCommandAllowSnapshotJson, transaction,
                after.ExitCode, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<GatewayVersionAlignmentResult> RestoreAsync(
        GatewayHostAccessPlan accessPlan,
        string rollbackPointId,
        string confirmedRollbackPointId,
        CancellationToken cancellationToken = default)
    {
        if (!_operationGate.Wait(0))
            return Result(GatewayVersionAlignmentState.Busy);

        try
        {
            if (!TryGetEligibleDistro(accessPlan, out var distroName) ||
                !string.Equals(distroName, _rollbackPoints.OwnedDistroName, StringComparison.Ordinal))
            {
                return Result(GatewayVersionAlignmentState.Ineligible, failureSummary: "Gateway is not the expected Companion-owned WSL distro.");
            }

            var point = _rollbackPoints.List().SingleOrDefault(item => string.Equals(item.Id, rollbackPointId, StringComparison.Ordinal));
            if (point is null)
                return Result(GatewayVersionAlignmentState.RestoreFailed, failureSummary: "The selected rollback point no longer exists.");

            var restore = await _rollbackPoints.RestoreExplicitAsync(
                distroName,
                accessPlan.GatewayId!,
                rollbackPointId,
                confirmedRollbackPointId,
                cancellationToken).ConfigureAwait(false);
            if (restore.State == GatewayRollbackOperationState.ConfirmationRequired)
                return Result(GatewayVersionAlignmentState.RestoreConfirmationRequired, rollbackPointId: rollbackPointId);
            if (!restore.Success || restore.Point is null)
            {
                var requiredPointId = restore.Point?.Id ?? rollbackPointId;
                return Result(
                    GatewayVersionAlignmentState.RestoreFailed,
                    previousVersion: point.OpenClawVersion,
                    rollbackPointId: requiredPointId,
                    exitCode: restore.ExitCode,
                    failureSummary: restore.State switch
                    {
                        GatewayRollbackOperationState.ImportPending =>
                            "The old registration was removed but import did not complete. The verified rollback point and durable recovery receipt were preserved for retry.",
                        GatewayRollbackOperationState.ResumeRequired =>
                            $"Recovery must resume exact rollback point {requiredPointId}; the selected point was not mutated.",
                        GatewayRollbackOperationState.AmbiguousRecovery =>
                            "Multiple mandatory recovery receipts exist. Restore is ambiguous and no lifecycle mutation was attempted.",
                        _ => $"Emergency restore stopped safely: {restore.FailureCode ?? restore.State.ToString()}."
                    });
            }

            var restored = await ProbeInstalledVersionAsync(distroName, cancellationToken).ConfigureAwait(false);
            if (restored.Failure is not null || !string.Equals(restored.Version, restore.Point.OpenClawVersion, StringComparison.Ordinal))
            {
                return Result(
                    GatewayVersionAlignmentState.RestoreVerificationFailed,
                    restored.Version,
                    restore.Point.OpenClawVersion,
                    rollbackPointId,
                    restored.ExitCode,
                    "The restored distro registration exists, but its exact OpenClaw version could not be verified.");
            }

            try
            {
                await _synchronizeAsync(accessPlan.GatewayId!, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Result(
                    GatewayVersionAlignmentState.RestoreVerificationFailed,
                    restored.Version,
                    restore.Point.OpenClawVersion,
                    rollbackPointId,
                    failureSummary: $"The full state was restored, but Gateway, Companion, Node, or pairing health failed: {ex.GetType().Name}.");
            }

            if (!await VerifyRestoredNodeCommandPolicyAsync(
                    distroName, restore.Point, cancellationToken).ConfigureAwait(false))
            {
                return Result(
                    GatewayVersionAlignmentState.RestoreVerificationFailed,
                    restored.Version,
                    restore.Point.OpenClawVersion,
                    rollbackPointId,
                    restored.ExitCode,
                    "The restored distro version is exact, but its complete Gateway node command policy does not match the rollback receipt.");
            }

            if (!await _rollbackPoints.VerifyAsync(rollbackPointId, cancellationToken).ConfigureAwait(false) ||
                !await _rollbackPoints.AttestLiveDistroAsync(
                    rollbackPointId, distroName, restore.Point.OpenClawVersion, cancellationToken).ConfigureAwait(false))
            {
                return Result(
                    GatewayVersionAlignmentState.RestoreVerificationFailed,
                    restored.Version,
                    restore.Point.OpenClawVersion,
                    rollbackPointId,
                    restored.ExitCode,
                    "Restore health passed, but the live distro no longer matches the exact rollback receipt. Restore finalization was blocked.");
            }

            _rollbackPoints.MarkRestoreHealthy(rollbackPointId);
            if (!await _rollbackPoints.AttestLiveDistroAsync(
                    rollbackPointId, distroName, restore.Point.OpenClawVersion, cancellationToken).ConfigureAwait(false))
            {
                _rollbackPoints.MarkImported(rollbackPointId);
                return Result(
                    GatewayVersionAlignmentState.RestoreVerificationFailed,
                    restored.Version,
                    restore.Point.OpenClawVersion,
                    rollbackPointId,
                    restored.ExitCode,
                    "The live distro changed immediately before retention cleanup. The imported recovery receipt was preserved and cleanup was blocked.");
            }
            await _rollbackPoints.CleanupAsync(_retentionPolicy(), cancellationToken).ConfigureAwait(false);
            return Result(
                GatewayVersionAlignmentState.Restored,
                restored.Version,
                restore.Point.OpenClawVersion,
                rollbackPointId,
                restored.ExitCode);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public GatewayVersionAlignmentResult CancelRestore(
        GatewayHostAccessPlan accessPlan,
        string rollbackPointId,
        string confirmedRollbackPointId)
    {
        if (!_operationGate.Wait(0))
            return Result(GatewayVersionAlignmentState.Busy);

        try
        {
            if (!string.Equals(rollbackPointId, confirmedRollbackPointId, StringComparison.Ordinal))
            {
                return Result(
                    GatewayVersionAlignmentState.RestoreConfirmationRequired,
                    rollbackPointId: rollbackPointId);
            }
            if (!TryGetEligibleDistro(accessPlan, out var distroName) ||
                !string.Equals(distroName, _rollbackPoints.OwnedDistroName, StringComparison.Ordinal))
            {
                return Result(
                    GatewayVersionAlignmentState.Ineligible,
                    failureSummary: "Gateway is not the expected Companion-owned WSL distro.");
            }

            var cancelled = _rollbackPoints.CancelRestore(
                distroName, accessPlan.GatewayId!, rollbackPointId);
            return cancelled.State == GatewayRollbackOperationState.Cancelled
                ? Result(
                    GatewayVersionAlignmentState.RestoreCancelled,
                    previousVersion: cancelled.Point?.OpenClawVersion,
                    rollbackPointId: rollbackPointId)
                : Result(
                    GatewayVersionAlignmentState.RestoreFailed,
                    previousVersion: cancelled.Point?.OpenClawVersion,
                    rollbackPointId: rollbackPointId,
                    failureSummary: $"Staged restore cancellation stopped safely: {cancelled.FailureCode ?? cancelled.State.ToString()}.");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<GatewayVersionAlignmentResult> FinalizePostUpdateAsync(
        string distroName,
        string gatewayId,
        string installedVersion,
        string previousVersion,
        string pointId,
        string? nodeCommandAllowSnapshotJson,
        CoreUpdateTransaction? transaction,
        int? exitCode,
        CancellationToken cancellationToken,
        bool resolveHistoricalReceipt = false,
        string? transactionGatewayId = null)
    {
        var completionGatewayId = transactionGatewayId ?? gatewayId;
        var policyMigration = await ApplyNodeCommandPolicyMigrationAsync(
            distroName,
            previousVersion,
            installedVersion,
            nodeCommandAllowSnapshotJson,
            cancellationToken).ConfigureAwait(false);
        if (policyMigration is not null)
        {
            var completionFailure = await TryCompleteCoreUpdateAsync(
                completionGatewayId, distroName, pointId, transaction, "failed", installedVersion, cancellationToken).ConfigureAwait(false);
            return Result(
                GatewayVersionAlignmentState.RecoveryAvailable,
                installedVersion,
                previousVersion,
                pointId,
                exitCode,
                AppendCompletionFailure(policyMigration, completionFailure));
        }

        try
        {
            await _synchronizeAsync(gatewayId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var completionFailure = await TryCompleteCoreUpdateAsync(
                completionGatewayId, distroName, pointId, transaction, "failed", installedVersion, cancellationToken).ConfigureAwait(false);
            return Result(
                GatewayVersionAlignmentState.RecoveryAvailable,
                installedVersion,
                previousVersion,
                pointId,
                exitCode,
                AppendCompletionFailure(
                    $"Post-update synchronization failed: {ex.GetType().Name}. The transaction receipt and verified protection evidence were preserved for manual recovery.",
                    completionFailure));
        }

        if (!await _rollbackPoints.VerifyAsync(pointId, cancellationToken).ConfigureAwait(false))
        {
            var completionFailure = await TryCompleteCoreUpdateAsync(
                completionGatewayId, distroName, pointId, transaction, "failed", installedVersion, cancellationToken).ConfigureAwait(false);
            return Result(
                GatewayVersionAlignmentState.VerificationFailed,
                installedVersion,
                previousVersion,
                pointId,
                exitCode,
                AppendCompletionFailure(
                    "Post-update health passed, but the pre-update protection no longer verifies. Retention cleanup was not run.",
                    completionFailure));
        }

        if (!await _rollbackPoints.AttestLiveDistroAsync(
                pointId, distroName, installedVersion, cancellationToken).ConfigureAwait(false))
        {
            var completionFailure = await TryCompleteCoreUpdateAsync(
                completionGatewayId, distroName, pointId, transaction, "failed", installedVersion, cancellationToken).ConfigureAwait(false);
            return Result(
                GatewayVersionAlignmentState.VerificationFailed,
                installedVersion,
                previousVersion,
                pointId,
                exitCode,
                AppendCompletionFailure(
                    "Post-update health passed, but the live distro no longer matches the exact expected version and rollback receipt. Finalization and cleanup were blocked.",
                    completionFailure));
        }

        var healthyCompletionFailure = await TryCompleteCoreUpdateAsync(
            completionGatewayId, distroName, pointId, transaction, "healthy", installedVersion, cancellationToken).ConfigureAwait(false);
        if (healthyCompletionFailure is not null)
        {
            return Result(
                GatewayVersionAlignmentState.VerificationFailed,
                installedVersion,
                previousVersion,
                pointId,
                exitCode,
                $"Core did not accept healthy completion for update transaction {transaction!.TransactionId}. The UpdateInProgress receipt and verified protection evidence were preserved: {healthyCompletionFailure}");
        }

        if (!await _rollbackPoints.AttestLiveDistroAsync(
                pointId, distroName, installedVersion, cancellationToken).ConfigureAwait(false))
        {
            return Result(
                GatewayVersionAlignmentState.VerificationFailed,
                installedVersion,
                previousVersion,
                pointId,
                exitCode,
                "The live distro changed immediately before retention cleanup. The update receipt was preserved and cleanup was blocked.");
        }
        if (resolveHistoricalReceipt)
            _rollbackPoints.MarkNativeRecoveryResolved(pointId);
        else
            _rollbackPoints.MarkPostUpdateHealthy(pointId);
        await _rollbackPoints.CleanupAsync(_retentionPolicy(), cancellationToken).ConfigureAwait(false);
        return Result(
            resolveHistoricalReceipt
                ? GatewayVersionAlignmentState.RecoveryResolved
                : GatewayVersionAlignmentState.Updated,
            installedVersion,
            previousVersion,
            pointId,
            exitCode);
    }

    private async Task<CoreUpdateTransactionStart> BeginCoreUpdateTransactionAsync(
        string expectedGatewayId,
        string requestId,
        CancellationToken cancellationToken)
    {
        if (_gatewayRequestAsync is null)
        {
            return new(
                null,
                "The connected Gateway client does not expose the transactional update RPC. The UpdateInProgress receipt and verified protection evidence were preserved.");
        }

        var identityFailure = GetGatewayIdentityFailure(expectedGatewayId);
        if (identityFailure is not null)
            return new(null, identityFailure);

        JsonElement payload;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            payload = await _gatewayRequestAsync(
                expectedGatewayId,
                requestId,
                "update.run",
                new
                {
                    target = new { package = "openclaw", version = _requiredVersion },
                    confirmationTier = "external"
                },
                (int)TimeSpan.FromMinutes(35).TotalMilliseconds,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(
                null,
                $"Transactional update.run failed: {ex.GetType().Name}. The UpdateInProgress receipt and verified protection evidence were preserved.");
        }

        if (!TryParseCoreUpdateTransaction(payload, requestId, _utcNow(), out var transaction))
        {
            return new(
                null,
                "Transactional update.run did not return a trusted transactionId and confirmDeadline. The UpdateInProgress receipt and verified protection evidence were preserved.");
        }
        return new(transaction, null);
    }

    private async Task<string?> TryCompleteCoreUpdateAsync(
        string expectedGatewayId,
        string distroName,
        string pointId,
        CoreUpdateTransaction? transaction,
        string outcome,
        string? observedVersion,
        CancellationToken cancellationToken)
    {
        if (transaction is null)
            return null;
        if (_gatewayRequestAsync is null)
            return "the connected Gateway client no longer exposes update.complete";

        var pending = _rollbackPoints.FindPendingUpdate(expectedGatewayId, _requiredVersion);
        if (pending is null || !string.Equals(pending.Id, pointId, StringComparison.Ordinal))
            return "the durable update receipt is missing or no longer identifies this transaction";
        if (pending.UpdateCompletionState is { } priorCompletion)
        {
            return priorCompletion == GatewayUpdateCompletionState.Accepted &&
                   string.Equals(pending.UpdateCompletionOutcome, outcome, StringComparison.Ordinal)
                ? null
                : $"update.complete was already attempted with outcome '{pending.UpdateCompletionOutcome ?? "<unknown>"}' and state {priorCompletion}; automatic redispatch is blocked";
        }
        if (_utcNow() >= transaction.ConfirmDeadline)
            return $"the update confirmation deadline {transaction.ConfirmDeadline:O} has expired; update.complete was not dispatched";

        var identityFailure = GetGatewayIdentityFailure(expectedGatewayId);
        if (identityFailure is not null)
            return identityFailure;

        var completionRequestId = $"windows-companion-gateway-complete-{pointId}";
        _rollbackPoints.ArmCoreUpdateCompletion(
            pointId,
            completionRequestId,
            outcome,
            observedVersion);
        if (string.Equals(outcome, "healthy", StringComparison.Ordinal) &&
            (observedVersion is null ||
             !await _rollbackPoints.AttestLiveDistroAsync(
                 pointId, distroName, observedVersion, cancellationToken).ConfigureAwait(false)))
        {
            return "the live Companion-owned distro changed after healthy completion was durably armed; update.complete was not dispatched";
        }

        var remaining = transaction.ConfirmDeadline - _utcNow();
        if (remaining <= TimeSpan.Zero)
            return $"the update confirmation deadline {transaction.ConfirmDeadline:O} expired before update.complete dispatch";

        var timeoutMs = (int)Math.Max(
            1,
            Math.Min(
                TimeSpan.FromMinutes(2).TotalMilliseconds,
                Math.Ceiling(remaining.TotalMilliseconds)));
        using var deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineCancellation.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        try
        {
            object parameters = observedVersion is null
                ? new
                {
                    transactionId = transaction.TransactionId,
                    requestId = transaction.RequestId,
                    outcome
                }
                : new
                {
                    transactionId = transaction.TransactionId,
                    requestId = transaction.RequestId,
                    outcome,
                    observedVersion
                };
            await _gatewayRequestAsync(
                expectedGatewayId,
                completionRequestId,
                "update.complete",
                parameters,
                timeoutMs,
                deadlineCancellation.Token).ConfigureAwait(false);
            _rollbackPoints.MarkCoreUpdateCompletionAccepted(pointId);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _rollbackPoints.MarkCoreUpdateCompletionAmbiguous(pointId);
            return $"update.complete did not finish before confirmation deadline {transaction.ConfirmDeadline:O}";
        }
        catch (OperationCanceledException)
        {
            _rollbackPoints.MarkCoreUpdateCompletionAmbiguous(pointId);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _rollbackPoints.MarkCoreUpdateCompletionAmbiguous(pointId);
            return $"update.complete failed with {ex.GetType().Name}";
        }
    }

    private string? GetGatewayIdentityFailure(string expectedGatewayId)
    {
        var connectedGatewayId = _connectedGatewayId()?.Trim();
        return string.Equals(connectedGatewayId, expectedGatewayId, StringComparison.Ordinal)
            ? null
            : $"The active Gateway identity changed before privileged update RPC dispatch. Expected '{expectedGatewayId}', but the live connection reports '{connectedGatewayId ?? "<none>"}'. The UpdateInProgress receipt and verified protection evidence were preserved.";
    }

    private bool TryGetPendingDispatch(
        GatewayRollbackPointManifest pending,
        bool requireCurrentTarget,
        out GatewayPackageUpdateRoute route,
        out GatewayUpdateDispatchState state,
        out CoreUpdateTransaction? transaction)
    {
        route = default;
        state = default;
        transaction = null;
        if (pending.UpdateTargetSource is not { } source ||
            pending.UpdateRoute is not { } persistedRoute ||
            pending.UpdateDispatchState is not { } persistedState ||
            !GatewayPackageTarget.TryRestore(
                source,
                pending.TargetOpenClawVersion,
                 pending.UpdateTargetPackageUri,
                 pending.UpdateTargetSha256,
                 out var persistedTarget) ||
            (requireCurrentTarget && persistedTarget != _target))
        {
            return false;
        }

        route = persistedRoute;
        state = persistedState;
        if (route == GatewayPackageUpdateRoute.CoreTransaction &&
            state == GatewayUpdateDispatchState.Accepted)
        {
            if (string.IsNullOrWhiteSpace(pending.UpdateRequestId) ||
                string.IsNullOrWhiteSpace(pending.UpdateTransactionId) ||
                pending.UpdateConfirmationDeadlineUtc is not { } deadline)
            {
                return false;
            }
            transaction = new(
                pending.UpdateTransactionId,
                deadline,
                pending.UpdateRequestId);
        }

        return true;
    }

    private static bool TryParseCoreUpdateTransaction(
        JsonElement payload,
        string requestId,
        DateTimeOffset now,
        out CoreUpdateTransaction transaction)
    {
        transaction = default!;
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("transactionId", out var transactionIdValue) ||
            transactionIdValue.ValueKind != JsonValueKind.String ||
            !payload.TryGetProperty("confirmDeadline", out var confirmDeadlineValue) ||
            confirmDeadlineValue.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var transactionId = transactionIdValue.GetString()?.Trim();
        var confirmDeadlineText = confirmDeadlineValue.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(transactionId) ||
            !DateTimeOffset.TryParse(
                confirmDeadlineText,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var confirmDeadline) ||
            confirmDeadline <= now)
        {
            return false;
        }

        transaction = new(transactionId, confirmDeadline, requestId);
        return true;
    }

    private static string AppendCompletionFailure(string failure, string? completionFailure) =>
        completionFailure is null
            ? failure
            : $"{failure} Core failure completion was not acknowledged: {completionFailure}.";

    private async Task<NodeCommandPolicySnapshot> CaptureNodeCommandPolicyAsync(
        string distroName,
        string sourceVersion,
        string targetVersion,
        CancellationToken cancellationToken)
    {
        if (string.Equals(
                GatewayNodeCommandPolicyConfig.ResolveAllowKey(sourceVersion),
                GatewayNodeCommandPolicyConfig.ResolveAllowKey(targetVersion),
                StringComparison.Ordinal))
        {
            return new(null, null);
        }

        var result = await _commandRunner.RunInDistroAsync(
            distroName,
            GatewayVersionAlignmentCommandBuilder.BuildGetNodeCommandAllow(sourceVersion),
            cancellationToken).ConfigureAwait(false);
        if (!result.Success || !TryNormalizeCompleteCommandArray(result.StandardOutput, out var normalized))
        {
            return new(
                null,
                "The complete Gateway node command allowlist could not be captured before update, so no package mutation was attempted.");
        }

        return new(normalized, null);
    }

    private async Task<string?> ApplyNodeCommandPolicyMigrationAsync(
        string distroName,
        string sourceVersion,
        string targetVersion,
        string? normalizedArrayJson,
        CancellationToken cancellationToken)
    {
        var sourceKey = GatewayNodeCommandPolicyConfig.ResolveAllowKey(sourceVersion);
        var targetKey = GatewayNodeCommandPolicyConfig.ResolveAllowKey(targetVersion);
        if (string.Equals(sourceKey, targetKey, StringComparison.Ordinal))
            return null;
        if (!TryNormalizeCompleteCommandArray(normalizedArrayJson, out var expected))
            return "The update receipt does not contain a valid complete Gateway node command allowlist, so policy migration and finalization were blocked.";

        var set = await _commandRunner.RunInDistroAsync(
            distroName,
            GatewayVersionAlignmentCommandBuilder.BuildSetNodeCommandAllow(targetVersion, expected),
            cancellationToken).ConfigureAwait(false);
        if (!set.Success)
            return "The complete Gateway node command policy could not be written to the target-version path. The update receipt was preserved for retry or rollback.";

        var verify = await _commandRunner.RunInDistroAsync(
            distroName,
            GatewayVersionAlignmentCommandBuilder.BuildGetNodeCommandAllow(targetVersion),
            cancellationToken).ConfigureAwait(false);
        if (!verify.Success ||
            !TryNormalizeCompleteCommandArray(verify.StandardOutput, out var observed) ||
            !string.Equals(observed, expected, StringComparison.Ordinal))
        {
            return "The migrated Gateway node command policy did not preserve the complete array exactly. The source policy remained in place, the update receipt was preserved, and finalization was blocked.";
        }

        var unset = await _commandRunner.RunInDistroAsync(
            distroName,
            GatewayVersionAlignmentCommandBuilder.BuildUnsetNodeCommandAllow(sourceVersion),
            cancellationToken).ConfigureAwait(false);
        return unset.Success
            ? null
            : "The target Gateway node command policy is verified, but the legacy path could not be removed. Both policy copies remain available and the update receipt was preserved for retry.";
    }

    private async Task<bool> VerifyRestoredNodeCommandPolicyAsync(
        string distroName,
        GatewayRollbackPointManifest point,
        CancellationToken cancellationToken)
    {
        if (point.NodeCommandAllowSnapshotJson is null)
            return true;

        var result = await _commandRunner.RunInDistroAsync(
            distroName,
            GatewayVersionAlignmentCommandBuilder.BuildGetNodeCommandAllow(point.OpenClawVersion),
            cancellationToken).ConfigureAwait(false);
        return result.Success &&
               TryNormalizeCompleteCommandArray(point.NodeCommandAllowSnapshotJson, out var expected) &&
               TryNormalizeCompleteCommandArray(result.StandardOutput, out var observed) &&
               string.Equals(observed, expected, StringComparison.Ordinal);
    }

    private static bool TryNormalizeCompleteCommandArray(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;
            var commands = document.RootElement.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                .ToArray();
            if (commands.Any(string.IsNullOrWhiteSpace))
                return false;
            normalized = JsonSerializer.Serialize(commands);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task TryRestoreExistingRuntimeAvailabilityAsync(
        string distroName,
        string gatewayId,
        CancellationToken cancellationToken)
    {
        await ProbeInstalledVersionAsync(distroName, cancellationToken).ConfigureAwait(false);
        await TrySynchronizeAsync(gatewayId, cancellationToken).ConfigureAwait(false);
    }

    private async Task TrySynchronizeAsync(string gatewayId, CancellationToken cancellationToken)
    {
        try { await _synchronizeAsync(gatewayId, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    private GatewayVersionAlignmentResult? GetUnresolvedRestoreGate()
    {
        if (_rollbackPoints.HasUnreadableReceipt())
        {
            return Result(
                GatewayVersionAlignmentState.VerificationFailed,
                failureSummary: "A Gateway rollback receipt directory cannot be read or validated. Recovery is ambiguous and no package probe or lifecycle mutation was attempted.");
        }

        var unresolvedRestores = _rollbackPoints.FindUnresolvedRestores();
        if (unresolvedRestores.Count > 1)
        {
            return Result(
                GatewayVersionAlignmentState.VerificationFailed,
                failureSummary: "Multiple unresolved Gateway restore receipts exist for the Companion-owned distro. Recovery is ambiguous and no package probe or update was attempted.");
        }
        if (unresolvedRestores.Count == 0)
            return null;

        var unresolved = unresolvedRestores[0];
        var action = unresolved.Phase == GatewayRollbackPointPhase.RestoreStaged
            ? "Resume or durably cancel this pre-destructive restore before updating."
            : "Resume this exact restore point before updating.";
        return Result(
            GatewayVersionAlignmentState.RecoveryAvailable,
            previousVersion: unresolved.OpenClawVersion,
            rollbackPointId: unresolved.Id,
            failureSummary: $"An unresolved Gateway restore is in phase {unresolved.Phase}. {action}");
    }

    private GatewayVersionAlignmentResult? GetPendingUpdateProbeGate(string gatewayId)
    {
        var pendingUpdates = _rollbackPoints.FindPendingUpdates();
        if (pendingUpdates.Count > 1)
        {
            return Result(
                GatewayVersionAlignmentState.VerificationFailed,
                failureSummary: "Multiple pending Gateway update receipts exist for the Companion-owned distro. Recovery is ambiguous and no package probe was attempted.");
        }
        if (pendingUpdates.Count == 0)
            return null;

        var pending = pendingUpdates[0];
        if (!IsVerifiedProtectionPoint(pending))
        {
            return Result(
                GatewayVersionAlignmentState.VerificationFailed,
                previousVersion: pending.OpenClawVersion,
                rollbackPointId: pending.Id,
                failureSummary: "The mandatory pending Gateway update receipt no longer has verified pre-update protection. Recovery must be resolved before probing ordinary alignment.");
        }
        if (!string.Equals(pending.GatewayId, gatewayId, StringComparison.Ordinal))
        {
            return Result(
                GatewayVersionAlignmentState.RecoveryAvailable,
                previousVersion: pending.OpenClawVersion,
                rollbackPointId: pending.Id,
                failureSummary: "A mandatory pending update belongs to an earlier Gateway record for this Companion-owned distro. Resolve that exact receipt before ordinary alignment; VHD restore is available only when the point is marked restore-eligible.");
        }
        if (!string.Equals(pending.TargetOpenClawVersion, _requiredVersion, StringComparison.Ordinal))
        {
            return Result(
                GatewayVersionAlignmentState.RecoveryAvailable,
                previousVersion: pending.OpenClawVersion,
                rollbackPointId: pending.Id,
                failureSummary: $"A mandatory pending update targets OpenClaw {pending.TargetOpenClawVersion}. Resolve exact point {pending.Id} before aligning to {_requiredVersion}.");
        }
        return null;
    }

    public async Task<int> CleanupRollbackPointsAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_rollbackPoints.HasUnreadableReceipt())
            {
                throw new InvalidOperationException(
                    "Rollback cleanup is blocked because a receipt directory cannot be read or validated.");
            }

            return await _rollbackPoints.CleanupAsync(
                _retentionPolicy(),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<GatewayVersionAlignmentResult> ResolveNativeRecoveryAsync(
        GatewayHostAccessPlan accessPlan,
        string rollbackPointId,
        string confirmedRollbackPointId,
        CancellationToken cancellationToken = default)
    {
        if (!_operationGate.Wait(0))
            return Result(GatewayVersionAlignmentState.Busy);

        try
        {
            if (!string.Equals(rollbackPointId, confirmedRollbackPointId, StringComparison.Ordinal))
            {
                return Result(
                    GatewayVersionAlignmentState.RestoreConfirmationRequired,
                    rollbackPointId: rollbackPointId);
            }
            if (!TryGetEligibleDistro(accessPlan, out var distroName) ||
                !string.Equals(distroName, _rollbackPoints.OwnedDistroName, StringComparison.Ordinal))
            {
                return Result(
                    GatewayVersionAlignmentState.Ineligible,
                    failureSummary: "Gateway is not the expected Companion-owned WSL distro.");
            }

            var restoreGate = GetUnresolvedRestoreGate();
            if (restoreGate is not null)
                return restoreGate;

            var pendingUpdates = _rollbackPoints.FindPendingUpdates();
            if (pendingUpdates.Count != 1)
            {
                return Result(
                    GatewayVersionAlignmentState.RecoveryAvailable,
                    rollbackPointId: rollbackPointId,
                    failureSummary: "Native update recovery is ambiguous because the Companion does not have exactly one pending receipt; no receipt was changed.");
            }
            var pending = pendingUpdates[0];
            if (!string.Equals(pending.Id, rollbackPointId, StringComparison.Ordinal) ||
                pending.ProtectionMode != GatewayUpdateProtectionMode.NativeBackup ||
                pending.Phase != GatewayRollbackPointPhase.UpdateInProgress)
            {
                return Result(
                    GatewayVersionAlignmentState.RestoreFailed,
                    rollbackPointId: rollbackPointId,
                    failureSummary: "The selected native update receipt is not eligible for resolution.");
            }
            if (!await _rollbackPoints.VerifyAsync(rollbackPointId, cancellationToken).ConfigureAwait(false))
            {
                return Result(
                    GatewayVersionAlignmentState.VerificationFailed,
                    rollbackPointId: rollbackPointId,
                    failureSummary: "The native backup no longer verifies, so the recovery receipt was preserved.");
            }

            if (!TryGetPendingDispatch(
                    pending,
                    requireCurrentTarget: false,
                    out var recoveryRoute,
                    out var recoveryState,
                    out var recoveryTransaction))
            {
                return Result(
                    GatewayVersionAlignmentState.RecoveryAvailable,
                    rollbackPointId: rollbackPointId,
                    failureSummary: "The durable dispatch provenance is incomplete; the receipt was preserved.");
            }

            var cancelUnacceptedInstallerDispatch = false;
            if (recoveryState is GatewayUpdateDispatchState.Prepared or GatewayUpdateDispatchState.Ambiguous)
            {
                if (recoveryRoute != GatewayPackageUpdateRoute.CompanionInstaller)
                {
                    return Result(
                        GatewayVersionAlignmentState.RecoveryAvailable,
                        rollbackPointId: rollbackPointId,
                        failureSummary: $"The {recoveryRoute} dispatch remains {recoveryState}; the receipt was preserved.");
                }

                var dispatchAge = _utcNow() - pending.UpdatedAtUtc;
                if (dispatchAge < InstallerDispatchSettleDelay)
                {
                    var retryAt = pending.UpdatedAtUtc + InstallerDispatchSettleDelay;
                    return Result(
                        GatewayVersionAlignmentState.RecoveryAvailable,
                        rollbackPointId: rollbackPointId,
                        failureSummary: $"The Companion installer dispatch remains {recoveryState}. It cannot be adjudicated before {retryAt:O}; the receipt was preserved.");
                }

                var beforeTermination = await ProbeInstalledVersionAsync(
                    distroName,
                    cancellationToken).ConfigureAwait(false);
                if (beforeTermination.Failure is not null ||
                    beforeTermination.Version is null ||
                    (!string.Equals(beforeTermination.Version, pending.OpenClawVersion, StringComparison.Ordinal) &&
                     !string.Equals(beforeTermination.Version, pending.TargetOpenClawVersion, StringComparison.Ordinal)) ||
                    !await _rollbackPoints.AttestLiveDistroAsync(
                        rollbackPointId,
                        distroName,
                        beforeTermination.Version,
                        cancellationToken).ConfigureAwait(false))
                {
                    return Result(
                        GatewayVersionAlignmentState.VerificationFailed,
                        beforeTermination.Version,
                        pending.OpenClawVersion,
                        rollbackPointId,
                        beforeTermination.ExitCode,
                        "The Companion-owned distro could not be re-attested immediately before interruption; the receipt was preserved.");
                }

                var terminate = await _commandRunner.TerminateDistroAsync(
                    distroName,
                    cancellationToken).ConfigureAwait(false);
                if (!terminate.Success)
                {
                    return Result(
                        GatewayVersionAlignmentState.RecoveryAvailable,
                        rollbackPointId: rollbackPointId,
                        exitCode: terminate.ExitCode,
                        failureSummary: "The Companion-owned distro could not be stopped to exclude a still-running installer; the receipt was preserved.");
                }
                cancelUnacceptedInstallerDispatch = true;
            }
            else if (recoveryState != GatewayUpdateDispatchState.Accepted)
            {
                return Result(
                    GatewayVersionAlignmentState.RecoveryAvailable,
                    rollbackPointId: rollbackPointId,
                    failureSummary: $"The {recoveryRoute} dispatch is {recoveryState} and cannot be recovered from an UpdateInProgress receipt.");
            }

            var installed = await ProbeInstalledVersionAsync(distroName, cancellationToken).ConfigureAwait(false);
            if (installed.Failure is not null || installed.Version is null)
            {
                return Result(
                    GatewayVersionAlignmentState.VerificationFailed,
                    installed.Version,
                    pending.OpenClawVersion,
                    rollbackPointId,
                    installed.ExitCode,
                    installed.Failure ?? "The installed OpenClaw version could not be verified.");
            }
            if (string.Equals(installed.Version, pending.TargetOpenClawVersion, StringComparison.Ordinal))
            {
                var resolvesHistoricalTarget =
                    !string.Equals(pending.TargetOpenClawVersion, _requiredVersion, StringComparison.Ordinal);
                if (recoveryRoute == GatewayPackageUpdateRoute.CoreTransaction &&
                    recoveryTransaction is null)
                {
                    return Result(
                        GatewayVersionAlignmentState.RecoveryAvailable,
                        installed.Version,
                        pending.OpenClawVersion,
                        rollbackPointId,
                        installed.ExitCode,
                        "The target version is installed, but its dispatch provenance is incomplete; the receipt was preserved.");
                }
                if (cancelUnacceptedInstallerDispatch)
                {
                    _rollbackPoints.MarkInstallerDispatchAccepted(rollbackPointId);
                    cancelUnacceptedInstallerDispatch = false;
                }
                return await FinalizePostUpdateAsync(
                    distroName,
                    accessPlan.GatewayId!,
                    installed.Version,
                    pending.OpenClawVersion,
                    rollbackPointId,
                    pending.NodeCommandAllowSnapshotJson,
                    recoveryTransaction,
                     installed.ExitCode,
                     cancellationToken,
                     resolvesHistoricalTarget,
                     recoveryTransaction is null ? null : pending.GatewayId).ConfigureAwait(false);
            }
            if (!string.Equals(installed.Version, pending.OpenClawVersion, StringComparison.Ordinal) ||
                !await _rollbackPoints.AttestLiveDistroAsync(
                    rollbackPointId,
                    distroName,
                    pending.OpenClawVersion,
                    cancellationToken).ConfigureAwait(false))
            {
                return Result(
                    GatewayVersionAlignmentState.VerificationFailed,
                    installed.Version,
                    pending.OpenClawVersion,
                    rollbackPointId,
                    installed.ExitCode,
                    "The live distro matches neither the exact pre-update state nor the exact target state; the receipt was preserved.");
            }
            if (cancelUnacceptedInstallerDispatch)
            {
                var notStarted = await _commandRunner.RunInDistroAsync(
                    distroName,
                    GatewayVersionAlignmentCommandBuilder.BuildInstallerNotStartedProbe(rollbackPointId),
                    cancellationToken).ConfigureAwait(false);
                if (!notStarted.Success)
                {
                    return Result(
                        GatewayVersionAlignmentState.RecoveryAvailable,
                        installed.Version,
                        pending.OpenClawVersion,
                        rollbackPointId,
                        notStarted.ExitCode,
                        notStarted.ExitCode == 1
                            ? "The Companion installer started before interruption, so the original package state cannot be proven intact; the receipt was preserved."
                            : "The Companion installer start marker could not be verified; the receipt was preserved.");
                }
            }

            try
            {
                await _synchronizeAsync(accessPlan.GatewayId!, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Result(
                    GatewayVersionAlignmentState.SynchronizationFailed,
                    installed.Version,
                    pending.OpenClawVersion,
                    rollbackPointId,
                    installed.ExitCode,
                    $"The pre-update version is restored, but Gateway, Companion, Node, or pairing health failed: {ex.GetType().Name}.");
            }

            if (recoveryRoute == GatewayPackageUpdateRoute.CoreTransaction)
            {
                if (recoveryTransaction is null)
                {
                    return Result(
                        GatewayVersionAlignmentState.RecoveryAvailable,
                        installed.Version,
                        pending.OpenClawVersion,
                        rollbackPointId,
                        installed.ExitCode,
                        "The pre-update version is healthy, but the Core transaction lacks accepted provenance; the receipt was preserved.");
                }

                var completionFailure = await TryCompleteCoreUpdateAsync(
                    pending.GatewayId,
                    distroName,
                    rollbackPointId,
                    recoveryTransaction,
                    "failed",
                    installed.Version,
                    cancellationToken).ConfigureAwait(false);
                if (completionFailure is not null)
                {
                    return Result(
                        GatewayVersionAlignmentState.RecoveryAvailable,
                        installed.Version,
                        pending.OpenClawVersion,
                        rollbackPointId,
                        installed.ExitCode,
                        $"The pre-update version is healthy, but Core did not accept failed completion; the receipt was preserved: {completionFailure}");
                }
            }

            if (!await _rollbackPoints.VerifyAsync(rollbackPointId, cancellationToken).ConfigureAwait(false) ||
                !await _rollbackPoints.AttestLiveDistroAsync(
                    rollbackPointId,
                    distroName,
                    pending.OpenClawVersion,
                    cancellationToken).ConfigureAwait(false))
            {
                return Result(
                    GatewayVersionAlignmentState.VerificationFailed,
                    installed.Version,
                    pending.OpenClawVersion,
                    rollbackPointId,
                    installed.ExitCode,
                    "The backup or live distro changed after synchronization; the recovery receipt was preserved.");
            }

            _rollbackPoints.MarkNativeRecoveryResolved(
                rollbackPointId,
                cancelUnacceptedInstallerDispatch);
            await _rollbackPoints.CleanupAsync(_retentionPolicy(), cancellationToken).ConfigureAwait(false);
            return Result(
                GatewayVersionAlignmentState.RecoveryResolved,
                installed.Version,
                pending.OpenClawVersion,
                rollbackPointId,
                installed.ExitCode);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static bool IsVerifiedProtectionPoint(GatewayRollbackPointManifest point) =>
        point.VerificationStatus == GatewayRollbackPointVerificationStatus.Verified &&
        point.ProtectionMode switch
        {
            GatewayUpdateProtectionMode.NativeBackup =>
                !point.RestoreEligible &&
                point.NativeBackupSizeBytes > 0 &&
                !string.IsNullOrWhiteSpace(point.NativeBackupSha256),
            GatewayUpdateProtectionMode.FullVhd =>
                point.RestoreEligible &&
                point.VhdSizeBytes > 0 &&
                !string.IsNullOrWhiteSpace(point.VhdSha256),
            _ => false
        };

    private static GatewayUpdateProtectionMode ResolveEffectiveProtectionMode(
        GatewayUpdateProtectionMode requestedMode,
        string sourceVersion)
    {
        if (requestedMode != GatewayUpdateProtectionMode.NativeBackup)
            return requestedMode;

        var comparison = CompareSemanticVersions(sourceVersion, NativeBackupMinimumSourceVersion);
        return comparison is >= 0
            ? GatewayUpdateProtectionMode.NativeBackup
            : GatewayUpdateProtectionMode.FullVhd;
    }

    private async Task<GatewayVersionAlignmentResult> ProbeCoreAsync(string distroName, CancellationToken cancellationToken)
    {
        var probe = await ProbeInstalledVersionAsync(distroName, cancellationToken).ConfigureAwait(false);
        if (probe.Failure is not null)
            return Result(GatewayVersionAlignmentState.ProbeFailed, exitCode: probe.ExitCode, failureSummary: probe.Failure);

        if (string.Equals(probe.Version, _requiredVersion, StringComparison.Ordinal))
            return Result(GatewayVersionAlignmentState.Aligned, probe.Version, exitCode: probe.ExitCode);

        var comparison = CompareSemanticVersions(probe.Version!, _requiredVersion);
        return comparison switch
        {
            > 0 => Result(GatewayVersionAlignmentState.NewerThanRequired, probe.Version, exitCode: probe.ExitCode),
            < 0 => Result(GatewayVersionAlignmentState.Mismatch, probe.Version, exitCode: probe.ExitCode),
            _ => Result(
                GatewayVersionAlignmentState.VersionOrderUnknown,
                probe.Version,
                exitCode: probe.ExitCode,
                failureSummary: "Installed and required OpenClaw versions differ, but their build metadata cannot be safely ordered. No update was attempted.")
        };
    }

    private async Task<InstalledVersionProbe> ProbeInstalledVersionAsync(string distroName, CancellationToken cancellationToken)
    {
        var result = await _commandRunner.RunInDistroAsync(
            distroName, GatewayVersionAlignmentCommandBuilder.BuildProbe(), cancellationToken).ConfigureAwait(false);
        if (!result.Success)
            return new(null, result.ExitCode, $"OpenClaw version probe failed with exit code {result.ExitCode}.");

        var match = InstalledVersionRegex().Match(result.StandardOutput ?? string.Empty);
        return match.Success
            ? new(match.Groups["version"].Value, result.ExitCode, null)
            : new(null, result.ExitCode, "OpenClaw version probe returned an unrecognized version.");
    }

    private GatewayVersionAlignmentResult Result(
        GatewayVersionAlignmentState state,
        string? installedVersion = null,
        string? previousVersion = null,
        string? rollbackPointId = null,
        int? exitCode = null,
        string? failureSummary = null) =>
        new(state, _requiredVersion, installedVersion, previousVersion, rollbackPointId, exitCode, failureSummary);

    private static bool TryGetEligibleDistro(GatewayHostAccessPlan? accessPlan, out string distroName)
    {
        distroName = accessPlan?.DistroName?.Trim() ?? string.Empty;
        return accessPlan is not null &&
               !string.IsNullOrWhiteSpace(accessPlan.GatewayId) &&
               accessPlan.TerminalTarget == GatewayTerminalTarget.Wsl &&
               accessPlan.CanControlWslGateway &&
               distroName.Length > 0;
    }

    private static int? CompareSemanticVersions(string left, string right)
    {
        var leftParts = ParseSemanticVersion(left);
        var rightParts = ParseSemanticVersion(right);
        var core = leftParts.Core.Zip(rightParts.Core, (a, b) => a.CompareTo(b)).FirstOrDefault(value => value != 0);
        if (core != 0)
            return core;
        if (leftParts.PreRelease.Count == 0 && rightParts.PreRelease.Count > 0)
            return 1;
        if (leftParts.PreRelease.Count > 0 && rightParts.PreRelease.Count == 0)
            return -1;

        for (var i = 0; i < Math.Min(leftParts.PreRelease.Count, rightParts.PreRelease.Count); i++)
        {
            var leftNumeric = IsDecimalIdentifier(leftParts.PreRelease[i]);
            var rightNumeric = IsDecimalIdentifier(rightParts.PreRelease[i]);
            var comparison = leftNumeric && rightNumeric
                ? BigInteger.Parse(leftParts.PreRelease[i]).CompareTo(BigInteger.Parse(rightParts.PreRelease[i]))
                : leftNumeric ? -1
                : rightNumeric ? 1
                : string.Compare(leftParts.PreRelease[i], rightParts.PreRelease[i], StringComparison.Ordinal);
            if (comparison != 0)
                return comparison;
        }
        var preReleaseCount = leftParts.PreRelease.Count.CompareTo(rightParts.PreRelease.Count);
        if (preReleaseCount != 0)
            return preReleaseCount;

        if (leftParts.BuildMetadata.SequenceEqual(rightParts.BuildMetadata, StringComparer.Ordinal))
            return 0;
        if (leftParts.BuildMetadata.Count == 0 ||
            rightParts.BuildMetadata.Count == 0 ||
            leftParts.BuildMetadata.Count != rightParts.BuildMetadata.Count)
        {
            return null;
        }

        int? orderedComparison = null;
        for (var i = 0; i < leftParts.BuildMetadata.Count; i++)
        {
            if (string.Equals(leftParts.BuildMetadata[i], rightParts.BuildMetadata[i], StringComparison.Ordinal))
                continue;
            if (!IsDecimalIdentifier(leftParts.BuildMetadata[i]) ||
                !IsDecimalIdentifier(rightParts.BuildMetadata[i]))
            {
                return null;
            }
            var comparison = BigInteger.Parse(leftParts.BuildMetadata[i])
                .CompareTo(BigInteger.Parse(rightParts.BuildMetadata[i]));
            if (comparison != 0)
                orderedComparison ??= comparison;
        }
        return orderedComparison;
    }

    private static bool IsDecimalIdentifier(string value) =>
        value.Length > 0 && value.All(character => character is >= '0' and <= '9');

    private static SemanticVersionParts ParseSemanticVersion(string version)
    {
        var versionAndBuild = version.Split('+', 2);
        var coreAndPreRelease = versionAndBuild[0].Split('-', 2);
        return new(
            coreAndPreRelease[0].Split('.').Select(BigInteger.Parse).ToArray(),
            coreAndPreRelease.Length == 2 ? coreAndPreRelease[1].Split('.').ToArray() : [],
            versionAndBuild.Length == 2 ? versionAndBuild[1].Split('.').ToArray() : []);
    }

    private sealed record InstalledVersionProbe(string? Version, int ExitCode, string? Failure);
    private sealed record NodeCommandPolicySnapshot(string? NormalizedArrayJson, string? Failure);
    private sealed record CoreUpdateTransaction(
        string TransactionId,
        DateTimeOffset ConfirmDeadline,
        string RequestId);
    private sealed record CoreUpdateTransactionStart(
        CoreUpdateTransaction? Transaction,
        string? Failure);
    private sealed record SemanticVersionParts(
        IReadOnlyList<BigInteger> Core,
        IReadOnlyList<string> PreRelease,
        IReadOnlyList<string> BuildMetadata);

    private const string ExactVersionPattern =
        @"(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)" +
        @"(?:-(?:(?:0|[1-9]\d*)|(?:[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))" +
        @"(?:\.(?:(?:0|[1-9]\d*)|(?:[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)))*)?" +
        @"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?";
    [GeneratedRegex(@"\A" + ExactVersionPattern + @"\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExactVersionRegex();

    [GeneratedRegex(
        @"\A\s*(?:OpenClaw\s+)?(?<version>" + ExactVersionPattern + @")(?:\s+\([^\r\n()]+\))?\s*\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex InstalledVersionRegex();
}

internal static class GatewayVersionAlignmentCommandBuilder
{
    private const string InstallerReceiptRoot =
        "$HOME/.local/state/openclaw-windows-companion/update-receipts";

    public static IReadOnlyList<string> BuildProbe() =>
        ["bash", "-lc", $"{WslGatewayControlCommandBuilder.OpenClawWslPathPrefix} && openclaw --version"];

    public static IReadOnlyList<string> BuildVerifiedInstaller(
        GatewayPackageTarget target,
        string rollbackPointId)
    {
        var markerPath = GetInstallerMarkerPath(rollbackPointId);
        return
        [
            "bash",
            "-lc",
            $"{WslGatewayControlCommandBuilder.OpenClawWslPathPrefix} && " +
            $"mkdir -p \"{InstallerReceiptRoot}\" && : > \"{markerPath}\" && " +
            $"sync -f \"{markerPath}\" && " +
            GatewayPackageInstallCommandBuilder.Build(
                GatewayPackageInstallCommandBuilder.DefaultInstallUrl,
                target)
        ];
    }

    public static IReadOnlyList<string> BuildInstallerNotStartedProbe(string rollbackPointId) =>
        [
            "bash",
            "-lc",
            $"test ! -e \"{GetInstallerMarkerPath(rollbackPointId)}\""
        ];

    public static IReadOnlyList<string> BuildGetNodeCommandAllow(string gatewayVersion) =>
        BuildConfigCommand($"get {GatewayNodeCommandPolicyConfig.ResolveAllowKey(gatewayVersion)} --json");

    public static IReadOnlyList<string> BuildUnsetNodeCommandAllow(string gatewayVersion) =>
        BuildConfigCommand($"unset {GatewayNodeCommandPolicyConfig.ResolveAllowKey(gatewayVersion)}");

    public static IReadOnlyList<string> BuildSetNodeCommandAllow(string gatewayVersion, string completeArrayJson) =>
        BuildConfigCommand(
            $"set {GatewayNodeCommandPolicyConfig.ResolveAllowKey(gatewayVersion)} " +
            WslShellQuoting.QuotePosixSingleQuote(completeArrayJson));

    private static IReadOnlyList<string> BuildConfigCommand(string operation) =>
        [
            "bash",
            "-lc",
            $"{WslGatewayControlCommandBuilder.OpenClawWslPathPrefix} && openclaw config {operation}"
        ];

    private static string GetInstallerMarkerPath(string rollbackPointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rollbackPointId);
        if (rollbackPointId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("Rollback point id contains unsupported characters.", nameof(rollbackPointId));
        return $"{InstallerReceiptRoot}/{rollbackPointId}.started";
    }
}
