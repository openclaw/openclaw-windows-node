using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

internal interface IDesktopCompanionView
{
    event EventHandler? HideRequested;
    void Show();
    void ApplyTheme(string theme);
    void Present(AppNotification? notification);
    void Close();
}

/// <summary>Owns the optional view lifetime and forwards only newly admitted app notifications.</summary>
internal sealed class DesktopCompanionController : IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly AppNotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<IDesktopCompanionView> _createView;
    private readonly Action<Exception> _reportFailure;
    private readonly DesktopCompanionState _state = new();
    private IDesktopCompanionView? _view;
    private bool _started, _disposed, _failed;

    public DesktopCompanionController(ISettingsStore settings, AppNotificationService notifications,
        IUiDispatcher dispatcher, Func<IDesktopCompanionView> createView, Action<Exception> reportFailure)
    {
        _settings = settings;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _createView = createView;
        _reportFailure = reportFailure;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        _started = true;
        _settings.Changed += OnSettingsChanged;
        _notifications.Changed += OnNotificationsChanged;
        Dispatch(ApplySettings);
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs args) => Dispatch(ApplySettings);

    private void ApplySettings()
    {
        var settings = _settings.Current;
        if (!settings.ShowDesktopCompanion)
        {
            CloseView();
            _failed = false;
            return;
        }
        if (_failed) return;
        if (_view is null)
        {
            _state.Reset(_notifications.Snapshot);
            _view = _createView();
            _view.HideRequested += OnHideRequested;
            _view.ApplyTheme(settings.AppTheme);
            _view.Show();
        }
        else
            _view.ApplyTheme(settings.AppTheme);

        if (!settings.ShowNotifications)
        {
            _state.Reset(_notifications.Snapshot);
            _view.Present(null);
        }
    }

    private void OnNotificationsChanged(object? sender, AppNotificationChangedEventArgs args) =>
        Dispatch(() =>
        {
            // Read the current service snapshot on the UI thread, not a potentially stale
            // queued event captured by a background producer.
            if (_state.Observe(_notifications.Snapshot,
                _view is not null && _settings.Current.ShowNotifications))
                _view?.Present(_state.Current);
        });

    private void OnHideRequested(object? sender, EventArgs args) =>
        Dispatch(() => _settings.Update(null, edit => edit.ShowDesktopCompanion = false));

    private void Dispatch(Action action)
    {
        void Run()
        {
            if (_disposed) return;
            try { action(); }
            catch (Exception exception)
            {
                // This optional surface must not break notification delivery or app startup.
                // Disable it until explicitly toggled, and publish visible recovery guidance.
                _failed = true;
                CloseView();
                _reportFailure(exception);
            }
        }
        if (_dispatcher.HasThreadAccess) Run();
        else if (!_dispatcher.TryEnqueue(Run))
            Logger.Warn("Desktop lobster could not reach the UI dispatcher.");
    }

    private void CloseView()
    {
        var view = _view;
        _view = null;
        if (view is null) return;
        view.HideRequested -= OnHideRequested;
        view.Close();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _notifications.Changed -= OnNotificationsChanged;
        CloseView();
    }
}
