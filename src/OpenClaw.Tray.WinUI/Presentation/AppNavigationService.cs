namespace OpenClawTray.Presentation;

/// <summary>
/// Default <see cref="INavigationService"/> implementation. It forwards navigation
/// requests to delegates supplied by <c>App</c>, which drive the existing
/// <c>HubWindow</c> navigation path. This keeps HubWindow as the single owner of
/// frame navigation, back-stack, and rail selection while giving future view models
/// a WinUI-free navigation surface to depend on.
/// </summary>
/// <remarks>
/// The documented UI-thread affinity is <b>enforced</b>, not just described: mutating
/// operations (<see cref="Navigate"/>, <see cref="GoBack"/>) are marshaled onto the UI
/// thread through <see cref="IUiDispatcher"/>, and the <see cref="CanGoBack"/> read fails
/// fast when called off the UI thread. This prevents a future background view model from
/// touching the WinUI frame off-thread.
/// </remarks>
internal sealed class AppNavigationService : INavigationService
{
    private readonly IUiDispatcher _dispatcher;
    private readonly Action<string> _navigate;
    private readonly Func<bool> _canGoBack;
    private readonly Action _goBack;

    public AppNavigationService(IUiDispatcher dispatcher, Action<string> navigate, Func<bool> canGoBack, Action goBack)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _navigate = navigate ?? throw new ArgumentNullException(nameof(navigate));
        _canGoBack = canGoBack ?? throw new ArgumentNullException(nameof(canGoBack));
        _goBack = goBack ?? throw new ArgumentNullException(nameof(goBack));
    }

    public void Navigate(string tag) => RunOnUiThread(() => _navigate(tag));

    public bool CanGoBack
    {
        get
        {
            if (!_dispatcher.HasThreadAccess)
            {
                throw new InvalidOperationException(
                    "INavigationService.CanGoBack reflects live WinUI frame state and must be read on the UI thread.");
            }

            return _canGoBack();
        }
    }

    public void GoBack() => RunOnUiThread(_goBack);

    /// <summary>
    /// Runs frame-touching work on the UI thread: directly when already on it, otherwise
    /// marshaled through the dispatcher so the WinUI frame is never touched off-thread.
    /// </summary>
    private void RunOnUiThread(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(action);
        }
    }
}
