namespace OpenClawTray.Services;

/// <summary>
/// Typed interface for page → app commands. Decouples pages from HubWindow
/// so they don't need a reference to the full window for navigation and actions.
/// Implemented by <see cref="App"/>.
/// </summary>
internal interface IAppCommands
{
    void OpenDashboard(string? path = null);
    void Navigate(string pageTag);
    void Reconnect();
    void Disconnect();
    void ShowVoiceOverlay();
    void ShowChat();
    void CheckForUpdates();
    void ShowOnboarding();
    void ShowGatewayWizard();
    void ShowConnectionStatus();
    void NotifySettingsSaved();
    Task<bool> ResendOpenTelemetryProbeAsync();
}
