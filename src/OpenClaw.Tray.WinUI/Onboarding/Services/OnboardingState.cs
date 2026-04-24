using OpenClawTray.Services;

namespace OpenClawTray.Onboarding.Services;

/// <summary>
/// Shared state across all onboarding pages.
/// Tracks the selected connection mode, current page, and completion.
/// </summary>
public sealed class OnboardingState
{
    public event EventHandler? Finished;

    public SettingsManager Settings { get; }

    /// <summary>
    /// Selected gateway connection mode.
    /// </summary>
    public ConnectionMode Mode { get; set; } = ConnectionMode.Local;

    /// <summary>
    /// Whether the onboarding chat page should be shown.
    /// </summary>
    public bool ShowChat { get; set; } = true;

    public OnboardingState(SettingsManager settings)
    {
        Settings = settings;
    }

    /// <summary>
    /// Returns the page order based on the selected mode and chat preference,
    /// matching the macOS onboarding flow.
    /// </summary>
    public OnboardingRoute[] GetPageOrder() => (Mode, ShowChat) switch
    {
        (ConnectionMode.Local, true) => [OnboardingRoute.Welcome, OnboardingRoute.Connection, OnboardingRoute.Wizard, OnboardingRoute.Permissions, OnboardingRoute.Chat, OnboardingRoute.Ready],
        (ConnectionMode.Local, false) => [OnboardingRoute.Welcome, OnboardingRoute.Connection, OnboardingRoute.Wizard, OnboardingRoute.Permissions, OnboardingRoute.Ready],
        (ConnectionMode.Remote, true) => [OnboardingRoute.Welcome, OnboardingRoute.Connection, OnboardingRoute.Permissions, OnboardingRoute.Chat, OnboardingRoute.Ready],
        (ConnectionMode.Remote, false) => [OnboardingRoute.Welcome, OnboardingRoute.Connection, OnboardingRoute.Permissions, OnboardingRoute.Ready],
        (ConnectionMode.Later, true) => [OnboardingRoute.Welcome, OnboardingRoute.Connection, OnboardingRoute.Chat, OnboardingRoute.Ready],
        (ConnectionMode.Later, false) => [OnboardingRoute.Welcome, OnboardingRoute.Connection, OnboardingRoute.Ready],
        _ => [OnboardingRoute.Welcome, OnboardingRoute.Connection, OnboardingRoute.Ready],
    };

    public void Complete()
    {
        Settings.Save();
        Finished?.Invoke(this, EventArgs.Empty);
    }
}

public enum ConnectionMode
{
    Local,
    Remote,
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
