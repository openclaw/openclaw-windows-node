using OpenClawTray.Onboarding.GatewayWizard;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class GatewayWizardStateTests
{
    private static GatewayWizardState CreateState() => new(CreateSettings());

    private static SettingsManager CreateSettings()
    {
        return new SettingsManager(Path.Combine(
            Path.GetTempPath(),
            "OpenClaw.Tray.Tests",
            Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void WizardSessionId_DefaultsToNull()
    {
        Assert.Null(CreateState().WizardSessionId);
    }

    [Fact]
    public void WizardStepPayload_DefaultsToNull()
    {
        Assert.Null(CreateState().WizardStepPayload);
    }

    [Fact]
    public void WizardLifecycleState_DefaultsToNull()
    {
        Assert.Null(CreateState().WizardLifecycleState);
    }

    [Fact]
    public void WizardError_DefaultsToNull()
    {
        Assert.Null(CreateState().WizardError);
    }

    [Fact]
    public void Settings_ReturnsInjectedManager()
    {
        var settings = CreateSettings();
        var state = new GatewayWizardState(settings);

        Assert.Same(settings, state.Settings);
    }

    [Fact]
    public void Dispose_NullsOutGatewayClient()
    {
        var state = CreateState();

        state.Dispose();

        Assert.Null(state.GatewayClient);
    }
}
