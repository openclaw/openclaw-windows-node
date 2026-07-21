using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

/// <summary>
/// Default <see cref="ISettingsStore"/> backed by the App-owned <see cref="SettingsManager"/>.
/// It does not own the manager's lifetime (App does); it only reads/writes it and republishes
/// the manager's <c>Saved</c> event as <see cref="Changed"/> with self-origination suppressed
/// and UI-thread affinity.
/// </summary>
/// <remarks>
/// Self-origination is tracked with a <c>[ThreadStatic]</c> depth: <see cref="SettingsManager.Save"/>
/// raises <c>Saved</c> synchronously on the calling thread inside <see cref="Update"/> or a
/// <see cref="BeginSelfWrite"/> scope, so the handler observes a non-zero depth on that thread and
/// suppresses the echo. A save from any other source (another surface, onboarding, a background
/// writer) runs with depth zero and is republished as <see cref="Changed"/>, marshaled onto the UI
/// thread via <see cref="IUiDispatcher"/>.
/// </remarks>
internal sealed class SettingsStore : ISettingsStore
{
    [ThreadStatic]
    private static int t_selfUpdateDepth;

    private readonly SettingsManager _settings;
    private readonly IUiDispatcher _dispatcher;

    public SettingsStore(SettingsManager settings, IUiDispatcher dispatcher)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _settings.Saved += OnManagerSaved;
    }

    public event EventHandler? Changed;

    public SettingsSnapshot Current => new()
    {
        AutoStart = _settings.AutoStart,
        GlobalHotkeyEnabled = _settings.GlobalHotkeyEnabled,
        UseLegacyWebChat = _settings.UseLegacyWebChat,
        ShowNotifications = _settings.ShowNotifications,
        NotificationSound = _settings.NotificationSound,
        AppTheme = _settings.AppTheme,
        ShowDiagnosticsEffective = _settings.ShowDiagnosticsEffective,
        NotifyHealth = _settings.NotifyHealth,
        NotifyUrgent = _settings.NotifyUrgent,
        NotifyReminder = _settings.NotifyReminder,
        NotifyEmail = _settings.NotifyEmail,
        NotifyCalendar = _settings.NotifyCalendar,
        NotifyBuild = _settings.NotifyBuild,
        NotifyStock = _settings.NotifyStock,
        NotifyInfo = _settings.NotifyInfo,
        ScreenRecordingConsentGiven = _settings.ScreenRecordingConsentGiven,
        CameraRecordingConsentGiven = _settings.CameraRecordingConsentGiven,
        VoiceTtsEnabled = _settings.VoiceTtsEnabled,
        ShowChatToolCalls = _settings.ShowChatToolCalls,
    };

    public void Update(Action<ISettingsEditor> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        using (BeginSelfWrite())
        {
            edit(new Editor(_settings));
            _settings.Save();
        }
    }

    public IDisposable BeginSelfWrite()
    {
        t_selfUpdateDepth++;
        return new SelfWriteScope();
    }

    private void OnManagerSaved(object? sender, EventArgs e)
    {
        // Saved fires synchronously on the thread that called Save(). When that is our own
        // Update or an App-owned self-write on this thread, the depth is non-zero and the echo
        // is suppressed.
        if (t_selfUpdateDepth > 0)
        {
            return;
        }

        if (_dispatcher.HasThreadAccess)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _dispatcher.TryEnqueue(() => Changed?.Invoke(this, EventArgs.Empty));
        }
    }

    /// <summary>Decrements the self-write depth on dispose. Created and disposed on the same thread.</summary>
    private sealed class SelfWriteScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            t_selfUpdateDepth--;
        }
    }

    private sealed class Editor : ISettingsEditor
    {
        private readonly SettingsManager _settings;

        public Editor(SettingsManager settings) => _settings = settings;

        public bool AutoStart { set => _settings.AutoStart = value; }
        public bool GlobalHotkeyEnabled { set => _settings.GlobalHotkeyEnabled = value; }
        public bool UseLegacyWebChat { set => _settings.UseLegacyWebChat = value; }
        public bool ShowNotifications { set => _settings.ShowNotifications = value; }
        public string NotificationSound { set => _settings.NotificationSound = value; }
        public string AppTheme { set => _settings.AppTheme = value; }
        public bool? ShowDiagnosticsOverride { set => _settings.ShowDiagnosticsOverride = value; }
        public bool NotifyHealth { set => _settings.NotifyHealth = value; }
        public bool NotifyUrgent { set => _settings.NotifyUrgent = value; }
        public bool NotifyReminder { set => _settings.NotifyReminder = value; }
        public bool NotifyEmail { set => _settings.NotifyEmail = value; }
        public bool NotifyCalendar { set => _settings.NotifyCalendar = value; }
        public bool NotifyBuild { set => _settings.NotifyBuild = value; }
        public bool NotifyStock { set => _settings.NotifyStock = value; }
        public bool NotifyInfo { set => _settings.NotifyInfo = value; }
        public bool ScreenRecordingConsentGiven { set => _settings.ScreenRecordingConsentGiven = value; }
        public bool CameraRecordingConsentGiven { set => _settings.CameraRecordingConsentGiven = value; }
        public bool ShowChatToolCalls { set => _settings.ShowChatToolCalls = value; }
    }
}
