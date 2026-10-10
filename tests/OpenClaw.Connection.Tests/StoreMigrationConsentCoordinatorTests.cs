using OpenClaw.Connection.Migration;

namespace OpenClaw.Connection.Tests;

public sealed class StoreMigrationConsentCoordinatorTests
{
    [Fact]
    public void DecliningEligibleAdmission_DoesNotProbeOrAuthorizeAdoption()
    {
        var coordinator = new StoreMigrationConsentCoordinator(new Probe(() =>
            throw new InvalidOperationException("A declined consent must not inspect Inno.")));

        var result = coordinator.Begin(EligibleAdmission(), consented: false);

        Assert.Equal(StoreMigrationConsentState.Declined, result.State);
    }

    [Fact]
    public void AcceptingEligibleAdmission_WaitsWhileInnoRuns()
    {
        var coordinator = new StoreMigrationConsentCoordinator(new Probe(() => true));

        var result = coordinator.Begin(EligibleAdmission(), consented: true);

        Assert.Equal(StoreMigrationConsentState.WaitingForInnoExit, result.State);
    }

    [Fact]
    public void Retry_RechecksInnoInsteadOfCachingTheFirstObservation()
    {
        var isRunning = true;
        var coordinator = new StoreMigrationConsentCoordinator(new Probe(() => isRunning));

        Assert.Equal(StoreMigrationConsentState.WaitingForInnoExit, coordinator.Retry().State);
        isRunning = false;

        Assert.Equal(StoreMigrationConsentState.ReadyForAdoption, coordinator.Retry().State);
    }

    [Theory]
    [InlineData(StoreMigrationStartupState.UpdateInno)]
    [InlineData(StoreMigrationStartupState.UnsupportedInstallation)]
    [InlineData(StoreMigrationStartupState.RecoveryRequired)]
    public void NonEligibleAdmission_CannotEnterConsentFlow(StoreMigrationStartupState state)
    {
        var coordinator = new StoreMigrationConsentCoordinator(new Probe(() =>
            throw new InvalidOperationException("Blocked admission must not inspect Inno.")));

        var result = coordinator.Begin(new(state), consented: true);

        Assert.Equal(StoreMigrationConsentState.Blocked, result.State);
    }

    private static StoreMigrationStartupDecision EligibleAdmission() =>
        new(StoreMigrationStartupState.ConsentRequired, new InnoInstallation(
            @"C:\fixture\OpenClawTray", @"C:\fixture\OpenClawTray\OpenClaw.Tray.WinUI.exe",
            @"C:\fixture\OpenClawTray\unins000.exe", "x64", new Version(2026, 9, 5)));

    private sealed class Probe(Func<bool> isRunning) : IInnoInstanceProbe
    {
        public bool IsRunning() => isRunning();
    }
}
