using System.Text.Json;
using OpenClawTray.Services;
using OpenClaw.Shared;

namespace OpenClawTray.Onboarding.Services;

/// <summary>
/// Shared state across all onboarding pages.
/// Tracks the selected connection mode, current page, and completion.
/// </summary>
public sealed class OnboardingState : IDisposable
{
    public event EventHandler? Finished;
    public event EventHandler? PageChanged;

    /// <summary>
    /// The currently displayed route. Updated by OnboardingApp on navigation.
    /// </summary>
    public OnboardingRoute CurrentRoute { get; set; } = OnboardingRoute.SetupWarning;

    /// <summary>
    /// Raised when the current route changes to or from the Chat page.
    /// OnboardingWindow uses this to show/hide the WebView2 overlay.
    /// </summary>
    public event EventHandler<OnboardingRoute>? RouteChanged;

    public SettingsManager Settings { get; }

    /// <summary>
    /// Selected gateway connection mode.
    /// </summary>
    public ConnectionMode Mode { get; set; } = ConnectionMode.Local;

    /// <summary>
    /// Forked-onboarding setup path (Phase 5). Null until the user picks a path
    /// on <see cref="OnboardingRoute.SetupWarning"/>. While null, the nav-bar
    /// "Next" button is disabled on the SetupWarning page.
    /// </summary>
    public SetupPath? SetupPath { get; set; }

    /// <summary>
    /// Raised by pages that want to advance the OnboardingApp programmatically
    /// (e.g., the SetupWarning page's "Set up locally" / "Advanced setup" buttons,
    /// the LocalSetupProgress page on auto-advance after success).
    /// </summary>
    public event EventHandler? AdvanceRequested;

    public void RequestAdvance() => AdvanceRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Whether the onboarding chat page should be shown.
    /// </summary>
    public bool ShowChat { get; set; } = true;

    /// <summary>
    /// Whether the connection was successfully tested during onboarding.
    /// </summary>
    public bool ConnectionTested { get; set; }

    /// <summary>
    /// Shared gateway client established during connection testing.
    /// Available for the Wizard page to make RPC calls (wizard.start/wizard.next).
    /// Null until connection is successfully tested.
    /// </summary>
    public OpenClawGatewayClient? GatewayClient { get; set; }

    // ── Wizard session state (persisted across page navigations) ──

    /// <summary>Wizard session ID from gateway wizard.start response.</summary>
    public string? WizardSessionId { get; set; }

    /// <summary>Current wizard step payload (JSON from last wizard.start/wizard.next response).</summary>
    public JsonElement? WizardStepPayload { get; set; }

    /// <summary>Wizard lifecycle state: null=not started, "active", "complete", "offline", "error".</summary>
    public string? WizardLifecycleState { get; set; }

    /// <summary>Wizard error message if in error state.</summary>
    public string? WizardError { get; set; }

    public OnboardingState(SettingsManager settings)
    {
        Settings = settings;
    }

    /// <summary>
    /// Returns the page order for the forked Phase-5 onboarding flow.
    /// SetupWarning is page 0 in every flow; the user's choice on that page
    /// (<see cref="SetupPath"/>) determines whether page 1 is the local-setup
    /// progress page or the legacy advanced Connection page.
    /// </summary>
    public OnboardingRoute[] GetPageOrder()
    {
        // Treat null SetupPath as Local for page-count purposes; the nav-bar
        // Next button is disabled on SetupWarning until the user picks a path.
        var path = SetupPath ?? Onboarding.Services.SetupPath.Local;

        // Node mode: skip Wizard and Chat — node clients can't use operator RPCs.
        if (Settings.EnableNodeMode)
        {
            return path == Onboarding.Services.SetupPath.Local
                ? [OnboardingRoute.SetupWarning, OnboardingRoute.LocalSetupProgress, OnboardingRoute.Permissions, OnboardingRoute.Ready]
                : [OnboardingRoute.SetupWarning, OnboardingRoute.Connection, OnboardingRoute.Permissions, OnboardingRoute.Ready];
        }

        if (path == Onboarding.Services.SetupPath.Local)
        {
            // Local setup always runs the wizard locally after the gateway is up.
            return ShowChat
                ? [OnboardingRoute.SetupWarning, OnboardingRoute.LocalSetupProgress, OnboardingRoute.Wizard, OnboardingRoute.Permissions, OnboardingRoute.Chat, OnboardingRoute.Ready]
                : [OnboardingRoute.SetupWarning, OnboardingRoute.LocalSetupProgress, OnboardingRoute.Wizard, OnboardingRoute.Permissions, OnboardingRoute.Ready];
        }

        // Advanced path: keep the legacy ConnectionMode-aware ordering.
        return (Mode, ShowChat) switch
        {
            (ConnectionMode.Local or ConnectionMode.Wsl or ConnectionMode.Ssh, true) => [OnboardingRoute.SetupWarning, OnboardingRoute.Connection, OnboardingRoute.Wizard, OnboardingRoute.Permissions, OnboardingRoute.Chat, OnboardingRoute.Ready],
            (ConnectionMode.Local or ConnectionMode.Wsl or ConnectionMode.Ssh, false) => [OnboardingRoute.SetupWarning, OnboardingRoute.Connection, OnboardingRoute.Wizard, OnboardingRoute.Permissions, OnboardingRoute.Ready],
            (ConnectionMode.Remote, true) => [OnboardingRoute.SetupWarning, OnboardingRoute.Connection, OnboardingRoute.Permissions, OnboardingRoute.Chat, OnboardingRoute.Ready],
            (ConnectionMode.Remote, false) => [OnboardingRoute.SetupWarning, OnboardingRoute.Connection, OnboardingRoute.Permissions, OnboardingRoute.Ready],
            (ConnectionMode.Later, _) => [OnboardingRoute.SetupWarning, OnboardingRoute.Connection, OnboardingRoute.Ready],
            _ => [OnboardingRoute.SetupWarning, OnboardingRoute.Connection, OnboardingRoute.Ready],
        };
    }

    public void NotifyPageChanged() => PageChanged?.Invoke(this, EventArgs.Empty);

    public void NotifyRouteChanged(OnboardingRoute route)
    {
        CurrentRoute = route;
        RouteChanged?.Invoke(this, route);
    }

    public void Complete()
    {
        Settings.Save();
        Finished?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (GatewayClient is IDisposable d)
            d.Dispose();
        GatewayClient = null;
    }
}

public enum ConnectionMode
{
    Local,
    Wsl,
    Remote,
    Ssh,
    Later,
}

public enum OnboardingRoute
{
    SetupWarning,
    LocalSetupProgress,
    Connection,
    Wizard,
    Permissions,
    Chat,
    Ready,
}

/// <summary>
/// Forked-onboarding setup path picked on <see cref="OnboardingRoute.SetupWarning"/>.
/// </summary>
public enum SetupPath
{
    /// <summary>User chose "Set up locally" — run the WSL gateway setup engine.</summary>
    Local,
    /// <summary>User chose "Advanced setup" — fall through to the legacy ConnectionPage.</summary>
    Advanced,
}
