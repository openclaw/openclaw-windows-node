namespace OpenClawTray.Presentation;

/// <summary>
/// Navigation surface that future page view models depend on so they can request
/// navigation without referencing the window host or any WinUI type.
/// </summary>
/// <remarks>
/// <c>WorkspaceWindow</c> (through <c>WorkspaceNavigationHistory</c>) is the single owner of
/// back-stack and dedupe for chat and Settings; <c>SettingsShellView</c> owns the Settings rail
/// and frame. This service forwards tag-based navigation to that path (via
/// <c>IAppCommands</c>/<c>WindowManager</c>).
/// </remarks>
public interface INavigationService
{
    /// <summary>
    /// Navigate to the destination identified by <paramref name="tag"/>. Safe to call
    /// from any thread: the implementation marshals frame navigation onto the UI thread,
    /// so a background caller can never touch the WinUI frame off-thread. When already on
    /// the UI thread the navigation runs synchronously.
    /// </summary>
    void Navigate(string tag);

    /// <summary>
    /// True when the navigation back-stack can go back. Must be read on the UI thread
    /// (it reflects live WinUI frame state); reading it off the UI thread throws.
    /// </summary>
    bool CanGoBack { get; }

    /// <summary>
    /// Navigate back one entry when possible. Safe to call from any thread; the
    /// implementation marshals onto the UI thread like <see cref="Navigate"/>.
    /// </summary>
    void GoBack();
}
