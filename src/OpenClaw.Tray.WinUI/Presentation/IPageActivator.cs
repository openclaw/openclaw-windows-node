namespace OpenClawTray.Presentation;

/// <summary>
/// Assigns a page's data context / view model from dependency injection when the
/// page is navigated to. The concrete WinUI adapter maps the page type to its
/// view-model type, resolves the view model through <see cref="NavigationScopeManager"/>
/// (which owns the per-navigation scope + activation lifetime), and sets it as the
/// page's <c>DataContext</c>.
/// </summary>
/// <remarks>
/// Pages mapped to a view model (currently the Settings page) have it resolved, activated, and
/// assigned as their <c>DataContext</c> by <see cref="OnNavigatedTo"/>. Unmapped pages only advance
/// the navigation scope (deactivating and disposing any prior view model) with no data-context change.
/// </remarks>
public interface IPageActivator
{
    /// <summary>
    /// Called after the frame has navigated to <paramref name="page"/> with the
    /// given navigation <paramref name="parameter"/>.
    /// </summary>
    void OnNavigatedTo(object page, object? parameter);

    /// <summary>
    /// Deactivates and disposes the current page view model and its navigation scope
    /// without tearing down the activator itself. Called when the owning window closes
    /// so page view models, subscriptions, and timers do not survive past the window.
    /// </summary>
    void Reset();
}
