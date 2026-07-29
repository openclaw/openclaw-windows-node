using OpenClaw.Connection;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class GatewayRollbackPresentationTests
{
    [Fact]
    public void PlanSelection_PrefersMandatoryRecoveryReceipt()
    {
        var ordinary = Point("ordinary", restoreEligible: true);
        var mandatory = Point("mandatory", phase: GatewayRollbackPointPhase.RestoreStaged);

        var plan = GatewayRollbackPresentation.PlanSelection([ordinary, mandatory]);

        Assert.True(plan.CanSelect);
        Assert.Equal(1, plan.PreferredIndex);
        Assert.Equal("mandatory", plan.Choices[plan.PreferredIndex].Point.Id);
    }

    [Fact]
    public void PlanSelection_RejectsAmbiguousMandatoryRecoveryReceipts()
    {
        var plan = GatewayRollbackPresentation.PlanSelection([
            Point("update", phase: GatewayRollbackPointPhase.UpdateInProgress),
            Point("restore", phase: GatewayRollbackPointPhase.ImportPending)
        ]);

        Assert.False(plan.CanSelect);
        Assert.Equal(GatewayRollbackNoticeSeverity.Error, plan.Notice!.Severity);
        Assert.Contains("update, restore", plan.Notice.Message);
    }

    [Fact]
    public void PlanAction_NativeUpdateRequiresResolution()
    {
        var plan = GatewayRollbackPresentation.PlanAction(Point(
            "native",
            phase: GatewayRollbackPointPhase.UpdateInProgress,
            protectionMode: GatewayUpdateProtectionMode.NativeBackup,
            restoreEligible: false));

        Assert.Equal(GatewayRollbackActionKind.ResolveNativeRecovery, plan.Kind);
        Assert.Equal("Verify and resolve", plan.PrimaryButtonText);
    }

    [Fact]
    public void PlanAction_StagedFullVhdAllowsExactCancellation()
    {
        var plan = GatewayRollbackPresentation.PlanAction(Point(
            "staged",
            phase: GatewayRollbackPointPhase.RestoreStaged));

        Assert.Equal(GatewayRollbackActionKind.Restore, plan.Kind);
        Assert.True(plan.CanCancelStagedRestore);
        Assert.Equal("Resume this rollback point", plan.PrimaryButtonText);
        Assert.Equal("Cancel staged restore", plan.SecondaryButtonText);
        Assert.Contains("exact point staged", plan.ConfirmationMessage);
    }

    [Fact]
    public void ProjectRestoreResult_PreservesRequiredPointMismatchGuidance()
    {
        var notice = GatewayRollbackPresentation.ProjectRestoreResult(
            new(
                GatewayVersionAlignmentState.RestoreFailed,
                RequiredVersion: "2026.7.1",
                RollbackPointId: "required",
                FailureSummary: "Resume is required."),
            "selected");

        Assert.Equal(GatewayRollbackNoticeSeverity.Error, notice.Severity);
        Assert.Equal(
            "Recovery must resume exact rollback point required. Resume is required.",
            notice.Message);
    }

    [Theory]
    [InlineData(0, "No rollback protection files matched")]
    [InlineData(1, "Removed 1 rollback protection item using")]
    [InlineData(2, "Removed 2 rollback protection items using")]
    public void ProjectCleanupResult_UsesPreservedCountCopy(int deleted, string expected)
    {
        var notice = GatewayRollbackPresentation.ProjectCleanupResult(deleted);

        Assert.Contains(expected, notice.Message);
    }

    [Fact]
    public void ProjectStorage_FailsClosedForUnreadableReceipt()
    {
        var text = GatewayRollbackPresentation.ProjectStorage(
            [Point("ignored", approximateSizeBytes: 1024)],
            hasUnreadableReceipt: true);

        Assert.Equal(
            "Stored rollback protection is unavailable because a receipt cannot be read or validated.",
            text);
    }

    [Fact]
    public void ProjectNativeRecoveryResult_ErrorWithoutSummary_PreservesPendingTruth()
    {
        var notice = GatewayRollbackPresentation.ProjectNativeRecoveryResult(new(
            GatewayVersionAlignmentState.VerificationFailed,
            RequiredVersion: "2026.7.2-beta.5"));

        Assert.Equal(GatewayRollbackNoticeSeverity.Error, notice.Severity);
        Assert.Contains("remains unresolved", notice.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("was resolved", notice.Message, StringComparison.Ordinal);
    }

    private static GatewayRollbackPointInfo Point(
        string id,
        GatewayRollbackPointPhase phase = GatewayRollbackPointPhase.Verified,
        GatewayUpdateProtectionMode protectionMode = GatewayUpdateProtectionMode.FullVhd,
        bool restoreEligible = true,
        long approximateSizeBytes = 0) =>
        new(
            id,
            "OpenClawGateway",
            "2026.7.1",
            new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero),
            GatewayRollbackPointVerificationStatus.Verified,
            phase,
            protectionMode,
            approximateSizeBytes,
            restoreEligible,
            null);
}
