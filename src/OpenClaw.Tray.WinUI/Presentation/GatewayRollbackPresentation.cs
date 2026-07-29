using OpenClaw.Connection;

namespace OpenClawTray.Presentation;

public enum GatewayRollbackNoticeSeverity
{
    Informational,
    Success,
    Warning,
    Error
}

public sealed record GatewayRollbackNotice(
    GatewayRollbackNoticeSeverity Severity,
    string Title,
    string Message);

public sealed record GatewayRollbackPointChoice(GatewayRollbackPointInfo Point)
{
    public string DisplayText =>
        $"Point {Point.Id} | {Point.Phase} | OpenClaw {Point.OpenClawVersion} | {Point.CreatedAtUtc.ToLocalTime():g} | " +
        $"{Point.ProtectionMode} | {Point.VerificationStatus} | {GatewayRollbackPresentation.FormatByteSize(Point.ApproximateSizeBytes)} | " +
        $"{(Point.RestoreEligible ? "restore eligible" : "not restorable by Companion")}";
}

public sealed record GatewayRollbackSelectionPlan(
    IReadOnlyList<GatewayRollbackPointChoice> Choices,
    int PreferredIndex,
    GatewayRollbackNotice? Notice)
{
    public bool CanSelect => Notice is null;
}

public enum GatewayRollbackActionKind
{
    ShowNotice,
    ResolveNativeRecovery,
    Restore
}

public sealed record GatewayRollbackActionPlan(
    GatewayRollbackActionKind Kind,
    GatewayRollbackPointInfo Point,
    GatewayRollbackNotice? Notice = null,
    bool CanCancelStagedRestore = false)
{
    public string ConfirmationTitle => Kind switch
    {
        GatewayRollbackActionKind.ResolveNativeRecovery => "Resolve native update recovery?",
        _ => $"Restore OpenClaw {Point.OpenClawVersion}?"
    };

    public string ConfirmationMessage => Kind switch
    {
        GatewayRollbackActionKind.ResolveNativeRecovery =>
            "Companion will not restore this native backup automatically. It will verify the retained backup, " +
            "the exact currently installed OpenClaw version, distro identity, Gateway, Windows Node, and pairing health. " +
            "The receipt is resolved only when the live installation exactly matches either the pre-update version or the intended target.",
        _ =>
            "Emergency restore will stop and unregister the current Companion-owned WSL distro, then import a verified copy " +
            $"of rollback point {Point.Id} under the same distro name. The complete retained filesystem and runtime state " +
            "will replace the current state. Companion will then verify Gateway, Windows Node, and pairing health." +
            (CanCancelStagedRestore
                ? $" Alternatively, cancel the staged restore for exact point {Point.Id} before any destructive boundary."
                : string.Empty)
    };

    public string PrimaryButtonText => Kind switch
    {
        GatewayRollbackActionKind.ResolveNativeRecovery => "Verify and resolve",
        _ when CanCancelStagedRestore => "Resume this rollback point",
        _ => "Restore this rollback point"
    };

    public string? SecondaryButtonText =>
        CanCancelStagedRestore ? "Cancel staged restore" : null;
}

public static class GatewayRollbackPresentation
{
    public static GatewayRollbackSelectionPlan PlanSelection(
        IReadOnlyList<GatewayRollbackPointInfo> points)
    {
        if (points.Count == 0)
        {
            return new([], -1, new(
                GatewayRollbackNoticeSeverity.Informational,
                "No rollback points",
                "A verified rollback point will be created before the next local Gateway update."));
        }

        var choices = points.Select(point => new GatewayRollbackPointChoice(point)).ToArray();
        var mandatoryChoices = choices.Where(choice => IsMandatoryRecoveryPhase(choice.Point.Phase)).ToArray();
        if (mandatoryChoices.Length > 1)
        {
            return new([], -1, new(
                GatewayRollbackNoticeSeverity.Error,
                "Gateway recovery is ambiguous",
                "Multiple mandatory recovery receipts exist: " +
                string.Join(", ", mandatoryChoices.Select(choice => choice.Point.Id)) +
                ". No rollback action was started."));
        }

        var preferredChoice = mandatoryChoices.SingleOrDefault()
            ?? choices.FirstOrDefault(choice => choice.Point.RestoreEligible);
        return new(choices, preferredChoice is null ? -1 : Array.IndexOf(choices, preferredChoice), null);
    }

    public static GatewayRollbackActionPlan PlanAction(GatewayRollbackPointInfo point)
    {
        if (point.ProtectionMode == GatewayUpdateProtectionMode.NativeBackup)
        {
            return point.Phase == GatewayRollbackPointPhase.UpdateInProgress
                ? new(GatewayRollbackActionKind.ResolveNativeRecovery, point)
                : new(GatewayRollbackActionKind.ShowNotice, point, new(
                    GatewayRollbackNoticeSeverity.Informational,
                    "Native backup is not restorable here",
                    "This point uses OpenClaw's native backup. It can be retained or cleaned up, but Companion cannot restore it as a full WSL VHD."));
        }

        if (!point.RestoreEligible)
        {
            return new(GatewayRollbackActionKind.ShowNotice, point, new(
                GatewayRollbackNoticeSeverity.Warning,
                "Rollback point is not eligible",
                "Verification or transaction state prevents restoring this point."));
        }

        return new(
            GatewayRollbackActionKind.Restore,
            point,
            CanCancelStagedRestore: point.Phase == GatewayRollbackPointPhase.RestoreStaged);
    }

    public static GatewayRollbackNotice ProjectNativeRecoveryResult(GatewayVersionAlignmentResult result) =>
        result.State is GatewayVersionAlignmentState.RecoveryResolved or GatewayVersionAlignmentState.Updated
            ? new(
                GatewayRollbackNoticeSeverity.Success,
                "Native update recovery resolved",
                result.FailureSummary ??
                "The live Gateway state was verified and the pending recovery receipt was resolved.")
            : new(
                GatewayRollbackNoticeSeverity.Error,
                "Native update recovery needs attention",
                result.FailureSummary ??
                "The pending recovery receipt remains unresolved. Review the Gateway state before retrying.");

    public static GatewayRollbackNotice ProjectCancellationResult(GatewayVersionAlignmentResult result) =>
        result.State == GatewayVersionAlignmentState.RestoreCancelled
            ? new(
                GatewayRollbackNoticeSeverity.Success,
                "Staged restore cancelled",
                "The staged copy was removed before the destructive boundary. Fresh Gateway updates are unblocked.")
            : new(
                GatewayRollbackNoticeSeverity.Error,
                "Staged restore cancellation needs attention",
                result.FailureSummary ?? "The durable recovery receipt was preserved.");

    public static GatewayRollbackNotice RestoringNotice() => new(
        GatewayRollbackNoticeSeverity.Informational,
        "Restoring local Gateway",
        "Do not close Companion while the WSL registration and health checks are being restored.");

    public static GatewayRollbackNotice ProjectRestoreResult(
        GatewayVersionAlignmentResult result,
        string selectedPointId)
    {
        if (result.State == GatewayVersionAlignmentState.Restored)
        {
            return new(
                GatewayRollbackNoticeSeverity.Success,
                "Gateway rollback restored",
                $"OpenClaw {result.InstalledVersion} and its retained state are healthy and synchronized.");
        }

        var message = result.RollbackPointId is { } requiredPointId &&
                      !string.Equals(requiredPointId, selectedPointId, StringComparison.Ordinal)
            ? $"Recovery must resume exact rollback point {requiredPointId}. " +
              (result.FailureSummary ?? "The mandatory receipt was preserved.")
            : result.FailureSummary ??
              "The durable recovery receipt and verified rollback point were preserved for retry.";
        return new(
            GatewayRollbackNoticeSeverity.Error,
            "Gateway rollback needs attention",
            message);
    }

    public static GatewayRollbackNotice ProjectCleanupResult(int deleted) => new(
        GatewayRollbackNoticeSeverity.Informational,
        "Rollback cleanup finished",
        deleted == 0
            ? "No rollback protection files matched the current retention settings."
            : $"Removed {deleted} rollback protection item{(deleted == 1 ? string.Empty : "s")} using the current retention settings.");

    public static GatewayRollbackNotice ProjectCleanupBlocked(string message) => new(
        GatewayRollbackNoticeSeverity.Error,
        "Rollback cleanup blocked",
        message);

    public static string ProjectStorage(
        IReadOnlyList<GatewayRollbackPointInfo> points,
        bool hasUnreadableReceipt)
    {
        if (hasUnreadableReceipt)
            return "Stored rollback protection is unavailable because a receipt cannot be read or validated.";

        var totalBytes = points.Sum(point => Math.Max(0, point.ApproximateSizeBytes));
        return $"Stored rollback protection: {points.Count} point{(points.Count == 1 ? string.Empty : "s")}, {FormatByteSize(totalBytes)} total.";
    }

    public static string FormatByteSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024d * 1024 * 1024):0.0} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024d * 1024):0.0} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024d:0.0} KB";
        return $"{bytes} B";
    }

    private static bool IsMandatoryRecoveryPhase(GatewayRollbackPointPhase phase) =>
        phase is GatewayRollbackPointPhase.UpdateInProgress
            or GatewayRollbackPointPhase.RestoreStaged
            or GatewayRollbackPointPhase.UnregisterPending
            or GatewayRollbackPointPhase.DistroUnregistered
            or GatewayRollbackPointPhase.ImportPending
            or GatewayRollbackPointPhase.Imported;
}
