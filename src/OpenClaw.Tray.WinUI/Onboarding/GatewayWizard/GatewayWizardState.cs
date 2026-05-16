using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services;

namespace OpenClawTray.Onboarding.GatewayWizard;

/// <summary>
/// Host-owned state for the gateway-driven provider/model wizard embedded in V2 setup.
/// </summary>
public sealed class GatewayWizardState : IDisposable
{
    public SettingsManager Settings { get; }

    /// <summary>
    /// Shared gateway client established during local setup. The page prefers
    /// <c>App.GatewayClient</c> when available and uses this as a fallback.
    /// </summary>
    public IOperatorGatewayClient? GatewayClient { get; set; }

    public string? WizardSessionId { get; set; }

    public JsonElement? WizardStepPayload { get; set; }

    public string? WizardLifecycleState { get; set; }

    public string? WizardError { get; set; }

    public OnboardingExistingConfigGuard? ExistingConfigGuard { get; set; }

    public GatewayWizardState(SettingsManager settings)
    {
        Settings = settings;
    }

    public void Dispose()
    {
        if (GatewayClient is IDisposable disposable)
        {
            disposable.Dispose();
        }

        GatewayClient = null;
    }
}
