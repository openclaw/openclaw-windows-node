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
/// exports an offline rollback point and runs only the native pinned updater in
/// the existing distro. WSL unregister/import is isolated to RestoreAsync and
/// requires an explicit rollback-point confirmation.
/// </summary>
public sealed partial class GatewayVersionAlignmentCoordinator
{
    private readonly IWslCommandRunner _commandRunner;
    private readonly GatewayRollbackPointManager _rollbackPoints;
    private readonly string _requiredVersion;
    private readonly Func<string, CancellationToken, Task> _synchronizeAsync;
    private readonly Func<GatewayRollbackRetentionPolicy> _retentionPolicy;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public GatewayVersionAlignmentCoordinator(
        IWslCommandRunner commandRunner,
        string requiredVersion,
        GatewayRollbackPointManager rollbackPoints,
        Func<string, CancellationToken, Task>? synchronizeAsync = null,
        Func<GatewayRollbackRetentionPolicy>? retentionPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(commandRunner);
        ArgumentNullException.ThrowIfNull(rollbackPoints);

        var normalizedVersion = requiredVersion?.Trim();
        if (normalizedVersion is null || !ExactVersionRegex().IsMatch(normalizedVersion))
        {
            throw new ArgumentException(
                "Required gateway version must be a strict exact semantic version without a leading 'v'.",
                nameof(requiredVersion));
        }

        _commandRunner = commandRunner;
        _rollbackPoints = rollbackPoints;
        _requiredVersion = normalizedVersion;
        _synchronizeAsync = synchronizeAsync ?? ((_, _) => Task.CompletedTask);
        _retentionPolicy = retentionPolicy ?? (() => GatewayRollbackRetentionPolicy.Default);
    }

    public string RequiredVersion => _requiredVersion;

    public IReadOnlyList<GatewayRollbackPointInfo> ListRollbackPoints() => _rollbackPoints.List();

    public bool HasVerifiedPendingUpdate(string gatewayId) =>
        !string.IsNullOrWhiteSpace(gatewayId) &&
        _rollbackPoints.FindPendingUpdates().Count > 0;

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
                return await FinalizePostUpdateAsync(
                    distroName, gatewayId, before.InstalledVersion!, pending.OpenClawVersion, pending.Id,
                    pending.NodeCommandAllowSnapshotJson, before.ExitCode, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (before.State != GatewayVersionAlignmentState.Mismatch || before.InstalledVersion is null)
                return before;

            var previousVersion = pending?.OpenClawVersion ?? before.InstalledVersion;

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
                        failureSummary: "The pending update's integral rollback point no longer verifies, so retry was blocked.");
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
            }
            else
            {
                try
                {
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
                    rollback = await _rollbackPoints.CreateVerifiedAsync(
                        distroName, gatewayId, previousVersion, _requiredVersion, cancellationToken).ConfigureAwait(false);
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
                        "A verified integral rollback point could not be created, so no update was attempted.");
                }
                rollbackPoint = rollback.Point;
                if (policySnapshot.NormalizedArrayJson is not null)
                {
                    rollbackPoint = _rollbackPoints.RecordNodeCommandAllowSnapshot(
                        rollbackPoint.Id, policySnapshot.NormalizedArrayJson);
                }
            }

            var pointId = rollbackPoint.Id;
            if (pending is null)
                _rollbackPoints.MarkUpdateInProgress(pointId);
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
            var update = await _commandRunner.RunInDistroAsync(
                distroName,
                GatewayVersionAlignmentCommandBuilder.BuildUpdate(_requiredVersion),
                cancellationToken).ConfigureAwait(false);
            if (!update.Success || !IsStructuredUpdateSuccess(update.StandardOutput, _requiredVersion))
            {
                var current = await ProbeInstalledVersionAsync(distroName, cancellationToken).ConfigureAwait(false);
                await TrySynchronizeAsync(gatewayId, cancellationToken).ConfigureAwait(false);
                return Result(
                    GatewayVersionAlignmentState.RecoveryAvailable,
                    current.Version,
                    previousVersion,
                    pointId,
                    update.ExitCode,
                    update.Success
                        ? "The updater did not return a trusted exact-version result. The verified rollback point is available for explicit recovery."
                        : $"The update failed with exit code {update.ExitCode}. The verified rollback point is available for explicit recovery.");
            }

            var after = await ProbeInstalledVersionAsync(distroName, cancellationToken).ConfigureAwait(false);
            if (after.Failure is not null || !string.Equals(after.Version, _requiredVersion, StringComparison.Ordinal))
            {
                await TrySynchronizeAsync(gatewayId, cancellationToken).ConfigureAwait(false);
                return Result(
                    GatewayVersionAlignmentState.RecoveryAvailable,
                    after.Version,
                    previousVersion,
                    pointId,
                    after.ExitCode,
                    "The installed version could not be verified exactly after update. The verified rollback point is available for explicit recovery.");
            }

            return await FinalizePostUpdateAsync(
                distroName, gatewayId, after.Version!, previousVersion, pointId,
                rollbackPoint.NodeCommandAllowSnapshotJson, after.ExitCode, cancellationToken).ConfigureAwait(false);
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
        int? exitCode,
        CancellationToken cancellationToken)
    {
        var policyMigration = await ApplyNodeCommandPolicyMigrationAsync(
            distroName,
            previousVersion,
            installedVersion,
            nodeCommandAllowSnapshotJson,
            cancellationToken).ConfigureAwait(false);
        if (policyMigration is not null)
        {
            return Result(
                GatewayVersionAlignmentState.RecoveryAvailable,
                installedVersion,
                previousVersion,
                pointId,
                exitCode,
                policyMigration);
        }

        try
        {
            await _synchronizeAsync(gatewayId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result(
                GatewayVersionAlignmentState.RecoveryAvailable,
                installedVersion,
                previousVersion,
                pointId,
                exitCode,
                $"Post-update synchronization failed: {ex.GetType().Name}. The verified rollback point is available for explicit recovery.");
        }

        if (!await _rollbackPoints.VerifyAsync(pointId, cancellationToken).ConfigureAwait(false))
        {
            return Result(
                GatewayVersionAlignmentState.VerificationFailed,
                installedVersion,
                previousVersion,
                pointId,
                exitCode,
                "Post-update health passed, but the rollback point no longer verifies. Retention cleanup was not run.");
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
                "Post-update health passed, but the live distro no longer matches the exact expected version and rollback receipt. Finalization and cleanup were blocked.");
        }

        _rollbackPoints.MarkPostUpdateHealthy(pointId);
        if (!await _rollbackPoints.AttestLiveDistroAsync(
                pointId, distroName, installedVersion, cancellationToken).ConfigureAwait(false))
        {
            _rollbackPoints.MarkUpdateInProgress(pointId);
            return Result(
                GatewayVersionAlignmentState.VerificationFailed,
                installedVersion,
                previousVersion,
                pointId,
                exitCode,
                "The live distro changed immediately before retention cleanup. The update receipt was preserved and cleanup was blocked.");
        }
        await _rollbackPoints.CleanupAsync(_retentionPolicy(), cancellationToken).ConfigureAwait(false);
        return Result(GatewayVersionAlignmentState.Updated, installedVersion, previousVersion, pointId, exitCode);
    }

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
        if (pending.VerificationStatus != GatewayRollbackPointVerificationStatus.Verified ||
            !pending.RestoreEligible)
        {
            return Result(
                GatewayVersionAlignmentState.VerificationFailed,
                previousVersion: pending.OpenClawVersion,
                rollbackPointId: pending.Id,
                failureSummary: "The mandatory pending Gateway update receipt no longer has an eligible verified rollback point. Recovery must be resolved before probing ordinary alignment.");
        }
        if (!string.Equals(pending.GatewayId, gatewayId, StringComparison.Ordinal))
        {
            return Result(
                GatewayVersionAlignmentState.RecoveryAvailable,
                previousVersion: pending.OpenClawVersion,
                rollbackPointId: pending.Id,
                failureSummary: "A mandatory pending update belongs to an earlier Gateway record for this Companion-owned distro. Resume or explicitly restore that exact point before ordinary alignment.");
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

    private static bool IsStructuredUpdateSuccess(string output, string requiredVersion)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String &&
                   string.Equals(status.GetString(), "ok", StringComparison.Ordinal) &&
                   root.TryGetProperty("after", out var after) && after.ValueKind == JsonValueKind.Object &&
                   after.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String &&
                   string.Equals(version.GetString(), requiredVersion, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
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
    private sealed record SemanticVersionParts(
        IReadOnlyList<BigInteger> Core,
        IReadOnlyList<string> PreRelease,
        IReadOnlyList<string> BuildMetadata);

    private const string ExactVersionPattern =
        @"(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)" +
        @"(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?" +
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
    public static IReadOnlyList<string> BuildProbe() =>
        ["bash", "-lc", $"{WslGatewayControlCommandBuilder.OpenClawWslPathPrefix} && openclaw --version"];

    public static IReadOnlyList<string> BuildUpdate(string requiredVersion) =>
        [
            "bash",
            "-lc",
            $"{WslGatewayControlCommandBuilder.OpenClawWslPathPrefix} && openclaw update --tag {requiredVersion} --yes --json"
        ];

    public static IReadOnlyList<string> BuildGetNodeCommandAllow(string gatewayVersion) =>
        BuildConfigCommand($"get {GatewayNodeCommandPolicyConfig.ResolveAllowKey(gatewayVersion)} --json");

    public static IReadOnlyList<string> BuildUnsetNodeCommandAllow(string gatewayVersion) =>
        BuildConfigCommand($"unset {GatewayNodeCommandPolicyConfig.ResolveAllowKey(gatewayVersion)}");

    public static IReadOnlyList<string> BuildSetNodeCommandAllow(string gatewayVersion, string completeArrayJson) =>
        BuildConfigCommand(
            $"set {GatewayNodeCommandPolicyConfig.ResolveAllowKey(gatewayVersion)} {QuotePosix(completeArrayJson)}");

    private static IReadOnlyList<string> BuildConfigCommand(string operation) =>
        [
            "bash",
            "-lc",
            $"{WslGatewayControlCommandBuilder.OpenClawWslPathPrefix} && openclaw config {operation}"
        ];

    private static string QuotePosix(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
}
