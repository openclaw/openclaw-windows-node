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

    public void RequestAdvance()
    {
        var subs = AdvanceRequested?.GetInvocationList().Length ?? 0;
        OpenClawTray.Services.Logger.Info($"[OnboardingState] RequestAdvance invoked; subscriber count = {subs}");
        AdvanceRequested?.Invoke(this, EventArgs.Empty);
        OpenClawTray.Services.Logger.Info("[OnboardingState] AdvanceRequested invoked; returned");
    }

    /// <summary>
    /// Per-page nav-bar Next button state override. Pages that want fine-grained
    /// control over the nav-bar Next button (Hidden / Visible+Disabled /
    /// Visible+Enabled) push a value here and raise <see cref="NavBarStateChanged"/>;
    /// <see cref="OnboardingApp"/> consults this for routes that opt in (currently
    /// only <see cref="OnboardingRoute.LocalSetupProgress"/>) and falls back to its
    /// legacy logic everywhere else.
    /// </summary>
    public OnboardingNextButtonState NextButtonState { get; private set; } = OnboardingNextButtonState.Default;

    /// <summary>
    /// Raised when <see cref="NextButtonState"/> changes so <see cref="OnboardingApp"/>
    /// can re-render the nav bar.
    /// </summary>
    public event EventHandler? NavBarStateChanged;

    /// <summary>
    /// Sets <see cref="NextButtonState"/> and raises <see cref="NavBarStateChanged"/>
    /// if the value actually changed.
    /// </summary>
    public void SetNextButtonState(OnboardingNextButtonState state)
    {
        if (NextButtonState == state) return;
        NextButtonState = state;
        NavBarStateChanged?.Invoke(this, EventArgs.Empty);
    }

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

    /// <summary>
    /// Guard that detects existing tray configuration.
    /// Set by <see cref="OnboardingWindow"/> after construction.
    /// Null when not available (startup auto-onboarding or env-override paths).
    /// </summary>
    public OnboardingExistingConfigGuard? ExistingConfigGuard { get; set; }

    /// <summary>
    /// Set to true by <see cref="SetupWarningPage"/> warn-and-confirm flow
    /// before advancing to the local setup path. Required by
    /// <see cref="LocalSetupProgressPage"/> defense-in-depth guard and the
    /// <see cref="LocalGatewaySetupEngineFactory"/> fail-closed check.
    /// </summary>
    public bool ReplaceExistingConfigurationConfirmed { get; set; }

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

        // Node mode: skip Wizard and Chat — remote-node clients can't use operator RPCs.
        // Exception (Bug #1, manual test 2026-05-05): Local easy-setup pairs the tray
        // as BOTH operator (Phase 12) AND node (Phase 14) on the loopback gateway it
        // just stood up. Even though PairAsync flips EnableNodeMode=true mid-onboarding
        // (LocalGatewaySetup.cs:2147), the tray still has operator credentials and the
        // Wizard hop's wizard.start RPC works. Only skip Wizard for explicit Advanced
        // remote-node deployments.
        if (Settings.EnableNodeMode && path != Onboarding.Services.SetupPath.Local)
        {
            return [OnboardingRoute.SetupWarning, OnboardingRoute.Connection, OnboardingRoute.Permissions, OnboardingRoute.Ready];
        }

        if (path == Onboarding.Services.SetupPath.Local)
        {
            // Local setup always runs the wizard locally after the gateway is up.
            // The WebView2 chat-preview step was removed per UX update (PR #274 follow-up):
            // post-Permissions we go straight to Ready, then optionally launch the Hub
            // chat tab from OnboardingWindow.OnWizardComplete based on whether the
            // wizard reached its "complete" lifecycle state (i.e. user picked a model).
            return [OnboardingRoute.SetupWarning, OnboardingRoute.LocalSetupProgress, OnboardingRoute.Wizard, OnboardingRoute.Permissions, OnboardingRoute.Ready];
        }

        // Advanced path: keep the legacy ConnectionMode-aware ordering.
        // ShowChat (the in-wizard WebView2 chat preview) is intentionally not consulted
        // anymore — the preview step has been removed from every flow.
        return Mode switch
        {
            ConnectionMode.Local or ConnectionMode.Wsl or ConnectionMode.Ssh => [OnboardingRoute.SetupWarning, OnboardingRoute.Connection, OnboardingRoute.Wizard, OnboardingRoute.Permissions, OnboardingRoute.Ready],
            ConnectionMode.Remote => [OnboardingRoute.SetupWarning, OnboardingRoute.Connection, OnboardingRoute.Permissions, OnboardingRoute.Ready],
            ConnectionMode.Later => [OnboardingRoute.SetupWarning, OnboardingRoute.Connection, OnboardingRoute.Ready],
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

/// <summary>
/// Per-page nav-bar Next button state override (Phase 5 final). Pages set this on
/// <see cref="OnboardingState.SetNextButtonState"/> to opt out of the default
/// "always visible+enabled (Disabled only on SetupWarning until path chosen)"
/// behavior. <see cref="OnboardingApp"/> consults this for routes that opt in
/// (currently only <see cref="OnboardingRoute.LocalSetupProgress"/>).
/// </summary>
public enum OnboardingNextButtonState
{
    /// <summary>Use legacy nav-bar logic — visible+enabled unless route-specific defaults apply.</summary>
    Default,
    /// <summary>Next button collapsed entirely (e.g., LocalSetupProgress Idle state).</summary>
    Hidden,
    /// <summary>Next button visible but disabled (e.g., LocalSetupProgress Running / FailedRetryable / FailedTerminal).</summary>
    VisibleDisabled,
    /// <summary>Next button visible and enabled (e.g., LocalSetupProgress Complete during the 1s pre-auto-advance window).</summary>
    VisibleEnabled,
}
