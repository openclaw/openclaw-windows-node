using Microsoft.UI.Xaml;

namespace OpenClawTray.Presentation.Adapters;

/// <summary>
/// WinUI adapter that implements <see cref="IPageActivator"/>. It maps a navigated
/// page's type to its view-model type, resolves + activates the view model through
/// <see cref="NavigationScopeManager"/>, and assigns it as the page's data context.
/// </summary>
/// <remarks>
/// The page→view-model map is currently empty, so every navigation takes the
/// "no view model" path: the scope manager only deactivates and disposes any prior
/// view model, and a page's <c>DataContext</c> is never touched. This makes the
/// activation hook a runtime no-op until pages adopt view models, at which point their
/// entries are added to the map.
/// </remarks>
internal sealed class FramePageActivator : IPageActivator
{
    private readonly NavigationScopeManager _scopes;
    private readonly IReadOnlyDictionary<Type, Type> _pageToViewModel;

    public FramePageActivator(NavigationScopeManager scopes, IReadOnlyDictionary<Type, Type> pageToViewModel)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _pageToViewModel = pageToViewModel ?? throw new ArgumentNullException(nameof(pageToViewModel));
    }

    public void OnNavigatedTo(object page, object? parameter)
    {
        if (page is null)
        {
            return;
        }

        if (_pageToViewModel.TryGetValue(page.GetType(), out var viewModelType))
        {
            var viewModel = _scopes.Navigate(viewModelType, parameter);
            if (page is FrameworkElement element)
            {
                element.DataContext = viewModel;
            }
        }
        else
        {
            // No view model mapped for this page yet: advance the scope (deactivate +
            // dispose any prior view model) without touching the page's data context.
            _scopes.Navigate(null, parameter);
        }
    }

    public void Reset() => _scopes.Reset();
}
