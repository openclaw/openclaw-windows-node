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
    private readonly object _originGate = new();

    private long _nextOriginId;
    private readonly Dictionary<int, SettingsWriteOrigin?> _activeOrigins = new();
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
            return _settings.CapturePresentationSnapshot();
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

        // SettingsManager serializes edits and persistence. Do not retain another
        // lock across Saved/Changed callbacks after it releases that serialization.
        BeginSaveOrigin(origin);
        try
        {
            _settings.UpdateAndSave(() => edit(new Editor(_settings)));
        }
        finally
        {
            EndSaveOrigin();
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

    private void OnManagerSaved(object? sender, SettingsSavedEventArgs e)
    {
        var origin = ConsumeMatchingOrigin();
        var args = new SettingsChangedEventArgs(origin, e.Snapshot);

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
            _activeOrigins[Environment.CurrentManagedThreadId] = origin;
        }
    }

    private void EndSaveOrigin()
    {
        lock (_originGate)
        {
            _activeOrigins.Remove(Environment.CurrentManagedThreadId);
        }
    }

    private SettingsWriteOrigin? ConsumeMatchingOrigin()
    {
        lock (_originGate)
        {
            return _activeOrigins.Remove(Environment.CurrentManagedThreadId, out var origin) ? origin : null;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class Editor : ISettingsEditor
    {
        private readonly SettingsManager _settings;

        public Editor(SettingsManager settings) => _settings = settings;
        public void ApplySystemRunPreset(OpenClaw.Shared.SystemRunAccessPreset preset) => _settings.ApplySystemRunPreset(preset);

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
        public bool EnableManagedLocalGatewayAutoRepair { set => _settings.EnableManagedLocalGatewayAutoRepair = value; }
        public bool ScreenRecordingConsentGiven { set => _settings.ScreenRecordingConsentGiven = value; }
        public bool CameraRecordingConsentGiven { set => _settings.CameraRecordingConsentGiven = value; }
        public bool LocationConsentGiven { set => _settings.LocationConsentGiven = value; }
        public bool VoiceTtsEnabled { set => _settings.VoiceTtsEnabled = value; }
        public bool ShowChatToolCalls { set => _settings.ShowChatToolCalls = value; }
        public bool ShowChatThinking { set => _settings.ShowChatThinking = value; }
        public void SetPagePinned(string scopeKey, string tag, bool pinned) => _settings.TogglePinnedPage(scopeKey, tag, pinned);
    }
}
