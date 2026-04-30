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
    public OnboardingRoute CurrentRoute { get; set; } = OnboardingRoute.Welcome;

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
    /// Returns the page order based on the selected mode and chat preference,
    /// matching the macOS onboarding flow.
    /// </summary>
    public OnboardingRoute[] GetPageOrder()
    {
        // Node mode: skip Wizard and Chat — node clients can't use operator RPCs
        if (Settings.EnableNodeMode)
        {
            return Mode switch
            {
                ConnectionMode.Local or ConnectionMode.Wsl or ConnectionMode.Remote or ConnectionMode.Ssh =>
                    [OnboardingRoute.Welcome, OnboardingRoute.Connection, OnboardingRoute.Permissions, OnboardingRoute.Ready],
                _ => // Later or unknown
                    [OnboardingRoute.Welcome, OnboardingRoute.Connection, OnboardingRoute.Ready],
            };
        }

        return (Mode, ShowChat) switch
        {
            // Local-style flows (Local, WSL, SSH tunnel) all run wizard locally
            (ConnectionMode.Local or ConnectionMode.Wsl or ConnectionMode.Ssh, true) => [OnboardingRoute.Welcome, OnboardingRoute.Connection, OnboardingRoute.Wizard, OnboardingRoute.Permissions, OnboardingRoute.Chat, OnboardingRoute.Ready],
            (ConnectionMode.Local or ConnectionMode.Wsl or ConnectionMode.Ssh, false) => [OnboardingRoute.Welcome, OnboardingRoute.Connection, OnboardingRoute.Wizard, OnboardingRoute.Permissions, OnboardingRoute.Ready],
            (ConnectionMode.Remote, true) => [OnboardingRoute.Welcome, OnboardingRoute.Connection, OnboardingRoute.Permissions, OnboardingRoute.Chat, OnboardingRoute.Ready],
            (ConnectionMode.Remote, false) => [OnboardingRoute.Welcome, OnboardingRoute.Connection, OnboardingRoute.Permissions, OnboardingRoute.Ready],
            (ConnectionMode.Later, _) => [OnboardingRoute.Welcome, OnboardingRoute.Connection, OnboardingRoute.Ready],
            _ => [OnboardingRoute.Welcome, OnboardingRoute.Connection, OnboardingRoute.Ready],
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
    Welcome,
    Connection,
    Wizard,
    Permissions,
    Chat,
    Ready,
}
