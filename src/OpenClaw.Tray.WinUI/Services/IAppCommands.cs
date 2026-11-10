// <summary>
// Defines the app-level actions presentation code can request without owning an application window.
// Implementations route navigation and side effects through the application shell.
// Usage:
//   IAppCommands commands = app;
//   commands.Navigate("settings");
//   await commands.ApplyAutoStart(SettingsWriteOrigin.User, true);
// </summary>
using OpenClawTray.Presentation;

namespace OpenClawTray.Services;

/// <summary>
/// Typed interface for page-to-app commands. Decouples pages from <c>HubWindow</c>
/// so they do not need a reference to the full window for navigation and actions.
/// Implemented by <see cref="App"/>.
/// </summary>
internal interface IAppCommands
{
    /// <summary>Opens the dashboard, optionally at a supplied path.</summary>
    void OpenDashboard(string? path = null);
    /// <summary>Returns whether Windows accepted the Dashboard browser launch, not whether the page loaded.</summary>
    Task<bool> TryOpenDashboardAsync(string? path = null);
    /// <summary>Navigates the application shell to a page tag.</summary>
    void Navigate(string pageTag);
    /// <summary>
    /// Returns native destination admission or confirmed Dashboard browser launch.
    /// Refused, redirected, unmounted and modal-deferred requests return false.
    /// A chat key belongs only to this request.
    /// </summary>
    Task<bool> TryNavigateAsync(string pageTag, string? chatSessionKey = null);
    void Reconnect();
    void Disconnect();
    void ShowVoiceOverlay();
    void CheckForUpdates();
    void ShowOnboarding();
    void ShowLocalAiSetup();
    void OpenLocalAiLogs() { }
    void ShowGatewayWizard();
    void ShowConnectionStatus();
    void NotifySettingsSaved();
    /// <summary>Applies the auto-start preference and reports whether it was saved.</summary>
    Task<bool> ApplyAutoStart(SettingsWriteOrigin origin, bool autoStart);
    /// <summary>Resends the OpenTelemetry probe and reports whether it completed.</summary>
    Task<bool> ResendOpenTelemetryProbeAsync();
}
