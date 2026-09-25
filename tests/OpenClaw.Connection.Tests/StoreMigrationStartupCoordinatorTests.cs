using OpenClaw.Connection.Migration;
using OpenClaw.Shared;

namespace OpenClaw.Connection.Tests;

public sealed class StoreMigrationStartupCoordinatorTests
{
    [Fact]
    public void Disabled_DoesNotInspectInstallationOrRecords()
    {
        var coordinator = new StoreMigrationStartupCoordinator(
            new Detector(() => throw new InvalidOperationException("Must not read registry.")),
            new Records(() => throw new InvalidOperationException("Must not read state.")),
            NullLogger.Instance);

        var result = coordinator.Evaluate(false, null, "unknown");

        Assert.Equal(StoreMigrationStartupState.Disabled, result.State);
        Assert.True(result.AllowsNormalStartup);
    }

    [Theory]
    [InlineData(null, "x64")]
    [InlineData("", "x64")]
    [InlineData("2026.9.1-beta.1", "x64")]
    [InlineData("2026.9.1", "x86")]
    public void InvalidPolicy_StopsBeforeDetection(string? minimum, string architecture)
    {
        var coordinator = new StoreMigrationStartupCoordinator(
            new Detector(() => throw new InvalidOperationException("Must not read registry.")),
            new Records(() => new(MigrationStartupRecordStatus.None)),
            NullLogger.Instance);

        var result = coordinator.Evaluate(true, minimum, architecture);

        Assert.Equal(StoreMigrationStartupState.InspectionFailed, result.State);
        Assert.False(result.AllowsNormalStartup);
        Assert.False(result.BlocksStartup);
    }

    [Theory]
    [InlineData("", "x64")]
    [InlineData("2026.9.1", "x86")]
    public void InvalidPolicy_StillBlocksWhenAReceiptExists(string? minimum, string architecture)
    {
        // Records are read before the policy check precisely so a misconfigured build cannot
        // wave through a launch that would run against data the handoff already moved.
        var coordinator = new StoreMigrationStartupCoordinator(
            new Detector(() => throw new InvalidOperationException("Must not read registry.")),
            new Records(() => new(MigrationStartupRecordStatus.Unavailable, null, CompletionPresent: true)),
            NullLogger.Instance);

        var result = coordinator.Evaluate(true, minimum, architecture);

        Assert.Equal(StoreMigrationStartupState.InspectionFailed, result.State);
        Assert.True(result.BlocksStartup);
    }

    [Theory]
    [InlineData(MigrationStartupRecordStatus.None, StoreMigrationStartupState.NotRequired, true)]
    [InlineData(MigrationStartupRecordStatus.Intent, StoreMigrationStartupState.RecoveryRequired, false)]
    [InlineData(MigrationStartupRecordStatus.Completed, StoreMigrationStartupState.FinalizationRequired, false)]
    [InlineData(MigrationStartupRecordStatus.Invalid, StoreMigrationStartupState.RecoveryRequired, false)]
    [InlineData(MigrationStartupRecordStatus.Unavailable, StoreMigrationStartupState.InspectionFailed, false)]
    public void AbsentInno_OnlyFreshStateAllowsNormalStartup(
        MigrationStartupRecordStatus pending, StoreMigrationStartupState expected, bool allowsNormal)
    {
        var result = Evaluate(new(InnoInstallationStatus.NotInstalled), new(pending));

        Assert.Equal(expected, result.State);
        Assert.Equal(allowsNormal, result.AllowsNormalStartup);
    }

    [Theory]
    [InlineData(InnoInstallationStatus.Unsupported, StoreMigrationStartupState.UnsupportedInstallation)]
    [InlineData(InnoInstallationStatus.InspectionFailed, StoreMigrationStartupState.InspectionFailed)]
    public void FailedDiscovery_DoesNotFallThroughToFreshStartup(
        InnoInstallationStatus discovery, StoreMigrationStartupState expected)
    {
        var result = Evaluate(new(discovery), new(MigrationStartupRecordStatus.None));

        Assert.Equal(expected, result.State);
        Assert.False(result.AllowsNormalStartup);
    }

    [Theory]
    [InlineData("2026.8.31.0", "x64", "x64", StoreMigrationStartupState.UpdateInno)]
    [InlineData("2026.9.1.0", "x64", "x64", StoreMigrationStartupState.ConsentRequired)]
    [InlineData("2026.9.2.0", "x64", "x64", StoreMigrationStartupState.ConsentRequired)]
    [InlineData("2026.9.1.0", "arm64", "arm64", StoreMigrationStartupState.ConsentRequired)]
    [InlineData("2026.9.1.0", "x64", "arm64", StoreMigrationStartupState.UnsupportedInstallation)]
    [InlineData("2026.9.1.0", "arm64", "x64", StoreMigrationStartupState.UnsupportedInstallation)]
    public void StableVersionAndArchitecture_AreGatedBeforeConsent(
        string version, string sourceArchitecture, string targetArchitecture, StoreMigrationStartupState expected)
    {
        var result = Evaluate(Detected(version, sourceArchitecture), new(MigrationStartupRecordStatus.None), targetArchitecture);

        Assert.Equal(expected, result.State);
        Assert.False(result.AllowsNormalStartup);
    }

    [Fact]
    public void ExistingIntent_StillRequiresStoreConsent()
    {
        var result = Evaluate(Detected(), new(MigrationStartupRecordStatus.Intent, new MigrationRecord { Kind = "intent" }));

        Assert.Equal(StoreMigrationStartupState.ConsentRequired, result.State);
        Assert.False(result.AllowsNormalStartup);
    }

    [Fact]
    public void ValidCompletion_BlocksNormalStartupWhileInnoRemains()
    {
        var result = Evaluate(Detected(), new(MigrationStartupRecordStatus.Completed,
            new MigrationRecord { Kind = "completed", SourceVersion = "2026.9.1" }));

        Assert.Equal(StoreMigrationStartupState.AwaitingInnoRemoval, result.State);
        Assert.False(result.AllowsNormalStartup);
    }

    [Fact]
    public void CompletionForDifferentSourceVersion_RequiresRecovery()
    {
        var result = Evaluate(Detected(), new(MigrationStartupRecordStatus.Completed,
            new MigrationRecord { Kind = "completed", SourceVersion = "2026.8.1" }));

        Assert.Equal(StoreMigrationStartupState.RecoveryRequired, result.State);
        Assert.False(result.AllowsNormalStartup);
    }

    [Theory]
    [InlineData(MigrationStartupRecordStatus.Invalid, StoreMigrationStartupState.RecoveryRequired)]
    [InlineData(MigrationStartupRecordStatus.Unavailable, StoreMigrationStartupState.InspectionFailed)]
    public void BadRecords_BlockWithoutInspectingOrRenewingSource(
        MigrationStartupRecordStatus status, StoreMigrationStartupState expected)
    {
        var coordinator = new StoreMigrationStartupCoordinator(
            new Detector(() => throw new InvalidOperationException("Must not inspect installation.")),
            new Records(() => new(status)), NullLogger.Instance);

        Assert.Equal(expected, coordinator.Evaluate(true, "2026.9.1", "x64").State);
    }

    [Fact]
    public void Retry_ReevaluatesEvidenceRatherThanCachingAdmission()
    {
        var installation = Detected();
        var coordinator = new StoreMigrationStartupCoordinator(
            new Detector(() => installation),
            new Records(() => new(MigrationStartupRecordStatus.None)), NullLogger.Instance);
        Assert.Equal(StoreMigrationStartupState.ConsentRequired, coordinator.Evaluate(true, "2026.9.1", "x64").State);

        installation = new(InnoInstallationStatus.InspectionFailed);

        Assert.Equal(StoreMigrationStartupState.InspectionFailed, coordinator.Evaluate(true, "2026.9.1", "x64").State);
    }

    [Theory]
    [InlineData("2026.9.0.0", StoreMigrationStartupState.UpdateInno)]
    [InlineData("2026.9.1.0", StoreMigrationStartupState.UnsupportedInstallation)]
    [InlineData(null, StoreMigrationStartupState.UnsupportedInstallation)]
    public void UnsupportedInstallationBelowMinimum_AsksForAnUpdateInstead(
        string? registeredVersion, StoreMigrationStartupState expected)
    {
        var detection = new InnoInstallationDetection(
            InnoInstallationStatus.Unsupported, Reason: "payload missing",
            RegisteredVersion: registeredVersion is null ? null : Version.Parse(registeredVersion));

        var result = Evaluate(detection, new(MigrationStartupRecordStatus.None));

        Assert.Equal(expected, result.State);
    }

    /// <summary>
    /// End to end through admission rather than on a hand-built decision: an installed source that
    /// is too old to migrate keeps the Store app inactive, and the same state without a payload
    /// does not.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void AnInstalledSourceNeedingAnUpdate_BlocksOnlyWhenItsPayloadExists(
        bool payloadPresent, bool blocks)
    {
        var detection = new InnoInstallationDetection(
            InnoInstallationStatus.Unsupported, Reason: "payload missing",
            RegisteredVersion: Version.Parse("2026.9.0.0"), SourcePayloadPresent: payloadPresent);

        var result = Evaluate(detection, new(MigrationStartupRecordStatus.None));

        Assert.Equal(StoreMigrationStartupState.UpdateInno, result.State);
        Assert.Equal(blocks, result.BlocksStartup);
        Assert.False(result.AllowsNormalStartup);
    }

    /// <summary>
    /// An architecture mismatch reaches admission through a fully verified detection, so the
    /// payload evidence has to survive that path too.
    /// </summary>
    [Fact]
    public void AnArchitectureMismatchOnAVerifiedInstall_BlocksStartup()
    {
        var result = Evaluate(Detected(architecture: "arm64"), new(MigrationStartupRecordStatus.None));

        Assert.Equal(StoreMigrationStartupState.UnsupportedInstallation, result.State);
        Assert.True(result.BlocksStartup);
    }

    [Theory]
    [InlineData(StoreMigrationStartupState.FinalizationRequired, true)]
    [InlineData(StoreMigrationStartupState.AwaitingInnoRemoval, true)]
    [InlineData(StoreMigrationStartupState.RecoveryRequired, false)]
    [InlineData(StoreMigrationStartupState.InspectionFailed, false)]
    [InlineData(StoreMigrationStartupState.UnsupportedInstallation, false)]
    [InlineData(StoreMigrationStartupState.UpdateInno, false)]
    [InlineData(StoreMigrationStartupState.ConsentRequired, false)]
    [InlineData(StoreMigrationStartupState.Disabled, false)]
    [InlineData(StoreMigrationStartupState.NotRequired, false)]
    public void WithoutAnInstalledSourceOrReceipt_OnlyAHandoffHoldingDataBlocksStartup(
        StoreMigrationStartupState state, bool blocks)
    {
        Assert.Equal(blocks, new StoreMigrationStartupDecision(state).BlocksStartup);
    }

    /// <summary>
    /// Issue #1374 permits one active production client. An unsupported or out-of-date source is
    /// still an installed client, so the Store app must not start beside it.
    /// </summary>
    [Theory]
    [InlineData(StoreMigrationStartupState.UnsupportedInstallation)]
    [InlineData(StoreMigrationStartupState.UpdateInno)]
    public void AnInstalledUnsupportedSource_BlocksStartupWithoutAReceipt(StoreMigrationStartupState state)
    {
        Assert.True(new StoreMigrationStartupDecision(state, SourcePayloadPresent: true).BlocksStartup);
    }

    /// <summary>
    /// The mirror image, and the reason the payload check exists: an interrupted uninstall can
    /// leave a registration with no app behind it. Blocking on the leftover key alone would leave
    /// the user with no working app and no in-app escape.
    /// </summary>
    [Theory]
    [InlineData(StoreMigrationStartupState.UnsupportedInstallation)]
    [InlineData(StoreMigrationStartupState.UpdateInno)]
    public void AnOrphanRegistrationWithNoPayload_DoesNotBlockStartup(StoreMigrationStartupState state)
    {
        Assert.False(new StoreMigrationStartupDecision(state, SourcePayloadPresent: false).BlocksStartup);
    }

    /// <summary>
    /// Payload presence must not widen the block to states that can legitimately occur with no
    /// usable source, where refusing to launch would strand the user.
    /// </summary>
    [Theory]
    [InlineData(StoreMigrationStartupState.InspectionFailed)]
    [InlineData(StoreMigrationStartupState.RecoveryRequired)]
    public void PayloadPresenceDoesNotBlockTheRecoverableStates(StoreMigrationStartupState state)
    {
        Assert.False(new StoreMigrationStartupDecision(state, SourcePayloadPresent: true).BlocksStartup);
    }

    [Theory]
    [InlineData(StoreMigrationStartupState.RecoveryRequired)]
    [InlineData(StoreMigrationStartupState.InspectionFailed)]
    [InlineData(StoreMigrationStartupState.UnsupportedInstallation)]
    [InlineData(StoreMigrationStartupState.UpdateInno)]
    public void AReceiptBlocksStartupEvenUnderAnInformationalState(StoreMigrationStartupState state)
    {
        Assert.True(new StoreMigrationStartupDecision(state, null, true).BlocksStartup);
    }

    [Fact]
    public void CompletionForDifferentSourceVersion_StillProtectsTheHandoff()
    {
        // The source can change under a finished handoff when the Inno app updates itself
        // before the user removes it. The receipt still means data moved.
        var result = Evaluate(Detected(), new(MigrationStartupRecordStatus.Completed,
            new MigrationRecord { Kind = "completed", SourceVersion = "2026.8.1" }, true));

        Assert.Equal(StoreMigrationStartupState.RecoveryRequired, result.State);
        Assert.True(result.BlocksStartup);
    }

    [Fact]
    public void UnsupportedSourceWithAReceipt_DoesNotDowngradeToInformational()
    {
        var detection = new InnoInstallationDetection(
            InnoInstallationStatus.Unsupported, Reason: "payload missing",
            RegisteredVersion: Version.Parse("2026.9.0.0"));

        var result = Evaluate(detection, new(MigrationStartupRecordStatus.Completed,
            new MigrationRecord { Kind = "completed", SourceVersion = "2026.9.1" }, true));

        Assert.Equal(StoreMigrationStartupState.UpdateInno, result.State);
        Assert.True(result.BlocksStartup);
    }

    [Theory]
    [InlineData(MigrationStartupRecordStatus.Invalid, true, true)]
    [InlineData(MigrationStartupRecordStatus.Invalid, false, false)]
    [InlineData(MigrationStartupRecordStatus.Unavailable, true, true)]
    [InlineData(MigrationStartupRecordStatus.Unavailable, false, false)]
    public void AnUndecodableReceiptStillBlocks(
        MigrationStartupRecordStatus status, bool completionPresent, bool blocks)
    {
        var coordinator = new StoreMigrationStartupCoordinator(
            new Detector(() => throw new InvalidOperationException("Must not inspect installation.")),
            new Records(() => new(status, null, completionPresent)), NullLogger.Instance);

        Assert.Equal(blocks, coordinator.Evaluate(true, "2026.9.1", "x64").BlocksStartup);
    }

    [Fact]
    public void ArchitectureMismatchWithAReceipt_StillProtectsTheHandoff()
    {
        var result = Evaluate(Detected(architecture: "arm64"), new(MigrationStartupRecordStatus.Completed,
            new MigrationRecord { Kind = "completed", SourceVersion = "2026.9.1" }, true));

        Assert.Equal(StoreMigrationStartupState.UnsupportedInstallation, result.State);
        Assert.True(result.BlocksStartup);
    }

    [Fact]
    public void CompletedStatusWithoutADecodedRecord_RecoversInsteadOfThrowing()
    {
        // Throwing here would escape into the UI error boundary and be reported as a plain
        // inspection failure, dropping the receipt this status proves exists.
        var result = Evaluate(Detected(), new(MigrationStartupRecordStatus.Completed, null, true));

        Assert.Equal(StoreMigrationStartupState.RecoveryRequired, result.State);
        Assert.True(result.BlocksStartup);
    }

    private static InnoInstallationDetection Detected(string version = "2026.9.1.0", string architecture = "x64") =>
        new(InnoInstallationStatus.Detected, new InnoInstallation(
            @"C:\fixture\OpenClawTray", @"C:\fixture\OpenClawTray\OpenClaw.Tray.WinUI.exe",
            @"C:\fixture\OpenClawTray\unins000.exe", architecture, Version.Parse(version)),
            SourcePayloadPresent: true);

    private static StoreMigrationStartupDecision Evaluate(
        InnoInstallationDetection installation, MigrationStartupRecord record, string architecture = "x64") =>
        new StoreMigrationStartupCoordinator(new Detector(() => installation),
            new Records(() => record), NullLogger.Instance).Evaluate(true, "2026.9.1", architecture);

    private sealed class Detector(Func<InnoInstallationDetection> read) : IInnoInstallationDetector
    {
        public InnoInstallationDetection Detect() => read();
    }

    private sealed class Records(Func<MigrationStartupRecord> read) : IMigrationStartupRecordReader
    {
        public MigrationStartupRecord Read() => read();
    }
}
