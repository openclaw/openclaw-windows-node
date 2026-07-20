namespace OpenClawTray.Presentation;

/// <summary>
/// Assigns a page's data context / view model from dependency injection when the
/// page is navigated to. The concrete WinUI adapter maps the page type to its
/// view-model type, resolves the view model through <see cref="NavigationScopeManager"/>
/// (which owns the per-navigation scope + activation lifetime), and sets it as the
/// page's <c>DataContext</c>.
/// </summary>
/// <remarks>
/// No page is mapped to a view model yet, so <see cref="OnNavigatedTo"/> only advances
/// the navigation scope (deactivating and disposing any prior view model) and never
/// touches a page's data context. The seam therefore has no observable runtime effect
/// today; it is proven by unit tests.
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
