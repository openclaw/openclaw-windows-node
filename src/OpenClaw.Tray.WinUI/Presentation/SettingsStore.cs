using System.Threading;
using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

/// <summary>
/// Default <see cref="ISettingsStore"/> backed by the App-owned <see cref="SettingsManager"/>.
/// It serializes store-managed mutate/save operations, publishes a typed versioned change event
/// for every save, tags store-managed saves with an explicit writer token, and republishes
/// external <see cref="SettingsManager.Save"/> calls with <see langword="null"/> origin.
/// </summary>
internal sealed class SettingsStore : ISettingsStore
{
    private readonly SettingsManager _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly object _updateGate = new();
    private readonly object _snapshotGate = new();
    private readonly object _originGate = new();

    private long _nextOriginId;
    private long _version;
    private SettingsWriteOrigin? _activeOrigin;
    private int _activeOriginThreadId;
    private bool _disposed;

    public SettingsStore(SettingsManager settings, IUiDispatcher dispatcher)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _settings.Saved += OnManagerSaved;
    }

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public SettingsSnapshot Current
    {
        get
        {
            ThrowIfDisposed();
            lock (_snapshotGate)
            {
                return CreateSnapshot(_version);
            }
        }
    }

    public SettingsWriteOrigin CreateOrigin()
    {
        ThrowIfDisposed();
        return new SettingsWriteOrigin(Interlocked.Increment(ref _nextOriginId));
    }

    public void Update(SettingsWriteOrigin? origin, Action<ISettingsEditor> edit)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(edit);

        lock (_updateGate)
        {
            BeginSaveOrigin(origin);
            try
            {
                edit(new Editor(_settings));
                _settings.Save();
            }
            finally
            {
                EndSaveOrigin();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _settings.Saved -= OnManagerSaved;
        _disposed = true;
    }

    private void OnManagerSaved(object? sender, EventArgs e)
    {
        var origin = ConsumeMatchingOrigin();
        SettingsChangedEventArgs args;
        lock (_snapshotGate)
        {
            var version = ++_version;
            args = new SettingsChangedEventArgs(origin, CreateSnapshot(version));
        }

        if (_dispatcher.HasThreadAccess)
        {
            Changed?.Invoke(this, args);
        }
        else
        {
            _dispatcher.TryEnqueue(() => Changed?.Invoke(this, args));
        }
    }

    private void BeginSaveOrigin(SettingsWriteOrigin? origin)
    {
        lock (_originGate)
        {
            _activeOrigin = origin;
            _activeOriginThreadId = Environment.CurrentManagedThreadId;
        }
    }

    private void EndSaveOrigin()
    {
        lock (_originGate)
        {
            _activeOrigin = null;
            _activeOriginThreadId = 0;
        }
    }

    private SettingsWriteOrigin? ConsumeMatchingOrigin()
    {
        lock (_originGate)
        {
            if (_activeOriginThreadId != Environment.CurrentManagedThreadId)
            {
                return null;
            }

            var origin = _activeOrigin;
            _activeOrigin = null;
            _activeOriginThreadId = 0;
            return origin;
        }
    }

    private SettingsSnapshot CreateSnapshot(long version) => new()
    {
        Version = version,
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
        EnableNodeMode = _settings.EnableNodeMode,
        EnableMcpServer = _settings.EnableMcpServer,
        NodeSystemRunEnabled = _settings.NodeSystemRunEnabled,
        NodeBrowserProxyEnabled = _settings.NodeBrowserProxyEnabled,
        NodeCameraEnabled = _settings.NodeCameraEnabled,
        NodeCanvasEnabled = _settings.NodeCanvasEnabled,
        NodeScreenEnabled = _settings.NodeScreenEnabled,
        NodeLocationEnabled = _settings.NodeLocationEnabled,
        NodeTtsEnabled = _settings.NodeTtsEnabled,
        NodeSttEnabled = _settings.NodeSttEnabled,
        NodeOllamaInferenceEnabled = _settings.NodeOllamaInferenceEnabled,
        SttModelName = _settings.SttModelName,
        TtsProvider = _settings.TtsProvider,
        TtsPiperVoiceId = _settings.TtsPiperVoiceId,
        TtsElevenLabsApiKey = _settings.TtsElevenLabsApiKey,
        TtsElevenLabsVoiceId = _settings.TtsElevenLabsVoiceId,
        ScreenRecordingConsentGiven = _settings.ScreenRecordingConsentGiven,
        CameraRecordingConsentGiven = _settings.CameraRecordingConsentGiven,
        ShowChatToolCalls = _settings.ShowChatToolCalls,
    };

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        public bool EnableNodeMode { set => _settings.EnableNodeMode = value; }
        public bool EnableMcpServer { set => _settings.EnableMcpServer = value; }
        public bool NodeSystemRunEnabled { set => _settings.NodeSystemRunEnabled = value; }
        public bool NodeBrowserProxyEnabled { set => _settings.NodeBrowserProxyEnabled = value; }
        public bool NodeCameraEnabled { set => _settings.NodeCameraEnabled = value; }
        public bool NodeCanvasEnabled { set => _settings.NodeCanvasEnabled = value; }
        public bool NodeScreenEnabled { set => _settings.NodeScreenEnabled = value; }
        public bool NodeLocationEnabled { set => _settings.NodeLocationEnabled = value; }
        public bool NodeTtsEnabled { set => _settings.NodeTtsEnabled = value; }
        public bool NodeSttEnabled { set => _settings.NodeSttEnabled = value; }
        public bool NodeOllamaInferenceEnabled { set => _settings.NodeOllamaInferenceEnabled = value; }
        public bool ScreenRecordingConsentGiven { set => _settings.ScreenRecordingConsentGiven = value; }
        public bool CameraRecordingConsentGiven { set => _settings.CameraRecordingConsentGiven = value; }
        public bool VoiceTtsEnabled { set => _settings.VoiceTtsEnabled = value; }
        public bool ShowChatToolCalls { set => _settings.ShowChatToolCalls = value; }
    }
}
