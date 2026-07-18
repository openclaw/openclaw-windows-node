namespace OpenClawTray.Presentation;

/// <summary>
/// Implemented by page view models that need to react to navigation. The
/// navigation seam calls <see cref="Activate"/> when the owning page is navigated
/// to and <see cref="Deactivate"/> immediately before the view model's navigation
/// scope is disposed (i.e. when navigating away). This pair defines the lifetime
/// during which a transient page view model may hold subscriptions or timers.
/// </summary>
public interface INavigationAware
{
    /// <summary>
    /// Called when the page is navigated to. <paramref name="parameter"/> is the
    /// navigation parameter (the same tag/argument passed to the frame), or null.
    /// </summary>
    void Activate(object? parameter);

    /// <summary>
    /// Called when navigating away, before the view model's scope is disposed.
    /// Implementations must release subscriptions here; they must not schedule new
    /// work, because the process may be shutting down.
    /// </summary>
    void Deactivate();
}
