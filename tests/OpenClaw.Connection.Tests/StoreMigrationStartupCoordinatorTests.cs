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
    public void InvalidPolicy_BlocksBeforeInspection(string? minimum, string architecture)
    {
        var coordinator = new StoreMigrationStartupCoordinator(
            new Detector(() => throw new InvalidOperationException("Must not read registry.")),
            new Records(() => throw new InvalidOperationException("Must not read state.")),
            NullLogger.Instance);

        var result = coordinator.Evaluate(true, minimum, architecture);

        Assert.Equal(StoreMigrationStartupState.InspectionFailed, result.State);
        Assert.False(result.AllowsNormalStartup);
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

    private static InnoInstallationDetection Detected(string version = "2026.9.1.0", string architecture = "x64") =>
        new(InnoInstallationStatus.Detected, new InnoInstallation(
            @"C:\fixture\OpenClawTray", @"C:\fixture\OpenClawTray\OpenClaw.Tray.WinUI.exe",
            @"C:\fixture\OpenClawTray\unins000.exe", architecture, Version.Parse(version)));

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
