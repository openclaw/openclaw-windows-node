using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

/// <summary>Projects rejected settings saves into one persistent, content-free recovery notice.</summary>
internal sealed class SettingsPersistenceNotification : IDisposable
{
    internal const string NotificationId = "settings-persistence-conflict";
    private readonly SettingsManager _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly AppNotificationService _notifications;
    private readonly Func<string, string> _localize;
    private bool _disposed;

    public SettingsPersistenceNotification(SettingsManager settings, IUiDispatcher dispatcher,
        AppNotificationService notifications, Func<string, string> localize)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _notifications = notifications;
        _localize = localize;
        _settings.PersistenceStateChanged += OnPersistenceStateChanged;
        OnPersistenceStateChanged(this, EventArgs.Empty);
    }

    private void OnPersistenceStateChanged(object? sender, EventArgs args)
    {
        if (!_dispatcher.TryEnqueue(Present))
            Logger.Warn("Settings persistence notification could not reach the UI dispatcher.");
    }

    private void Present()
    {
        if (_disposed) return;
        if (!_settings.HasPersistenceConflict)
        {
            _notifications.Dismiss(NotificationId);
            return;
        }
        if (_notifications.Snapshot.ActiveNotifications.Any(item => item.Id == NotificationId))
            return;
        _notifications.Show(new AppNotification
        {
            Id = NotificationId,
            DedupeKey = NotificationId,
            Source = "settings",
            Title = _localize("Settings_PersistenceConflict_Title"),
            Message = _localize("Settings_PersistenceConflict_Message"),
            Severity = AppNotificationSeverity.Error,
            Persistence = AppNotificationPersistence.Persistent,
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _settings.PersistenceStateChanged -= OnPersistenceStateChanged;
    }
}
