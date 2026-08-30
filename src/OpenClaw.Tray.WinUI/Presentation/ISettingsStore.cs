using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

/// <summary>
/// WinUI-free seam over the App-owned settings for presentation code. It exposes an
/// immutable <see cref="SettingsSnapshot"/> for reads, supports field-scoped writes against
/// the live <see cref="SettingsManager"/>, and republishes every persisted save as a typed
/// <see cref="Changed"/> event tagged with the originating writer token when known.
/// </summary>
public interface ISettingsStore : IDisposable
{
    /// <summary>An immutable snapshot of the current settings values used by the settings surfaces.</summary>
    SettingsSnapshot Current { get; }

    /// <summary>Creates an opaque token that identifies one writer instance.</summary>
    SettingsWriteOrigin CreateOrigin();

    /// <summary>
    /// Applies <paramref name="edit"/> to the live settings manager and persists once. The raised
    /// <see cref="Changed"/> event carries <paramref name="origin"/> so the matching caller can
    /// ignore its own notification while other active listeners still refresh.
    /// </summary>
    void Update(SettingsWriteOrigin? origin, Action<ISettingsEditor> edit);

    /// <summary>
    /// Raised after settings are persisted. The event includes the persisted version, a detached
    /// snapshot, and the originating writer token when the save came through this store.
    /// External saves published directly through <see cref="SettingsManager.Save"/> use
    /// <see langword="null"/> origin.
    /// </summary>
    event EventHandler<SettingsChangedEventArgs>? Changed;
}

/// <summary>Opaque per-writer token used to tag change notifications.</summary>
public sealed class SettingsWriteOrigin
{
    internal SettingsWriteOrigin(long id) => Id = id;

    internal long Id { get; }
}

/// <summary>Typed settings-change payload published by <see cref="ISettingsStore"/>.</summary>
public sealed class SettingsChangedEventArgs : EventArgs
{
    public SettingsChangedEventArgs(SettingsWriteOrigin? origin, SettingsSnapshot snapshot)
    {
        Origin = origin;
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Version = snapshot.Version;
    }

    public SettingsWriteOrigin? Origin { get; }
    public long Version { get; }
    public SettingsSnapshot Snapshot { get; }
}

/// <summary>
/// Narrow write surface handed to <see cref="ISettingsStore.Update"/>. It exposes only the
/// fields the settings surfaces mutate, so presentation code never touches the concrete
/// settings manager. Grows as more pages adopt the store.
/// </summary>
public interface ISettingsEditor
{
    bool AutoStart { set; }
    bool GlobalHotkeyEnabled { set; }
    bool UseLegacyWebChat { set; }
    bool ShowNotifications { set; }
    string NotificationSound { set; }
    string AppTheme { set; }

    /// <summary>Writes the raw diagnostics override (null clears it back to the computed default).</summary>
    bool? ShowDiagnosticsOverride { set; }

    bool NotifyHealth { set; }
    bool NotifyUrgent { set; }
    bool NotifyReminder { set; }
    bool NotifyEmail { set; }
    bool NotifyCalendar { set; }
    bool NotifyBuild { set; }
    bool NotifyStock { set; }
    bool NotifyInfo { set; }

    bool EnableNodeMode { set; }
    bool EnableMcpServer { set; }
    bool NodeSystemRunEnabled { set; }
    bool NodeBrowserProxyEnabled { set; }
    bool NodeCameraEnabled { set; }
    bool NodeCanvasEnabled { set; }
    bool NodeScreenEnabled { set; }
    bool NodeLocationEnabled { set; }
    bool NodeTtsEnabled { set; }
    bool NodeSttEnabled { set; }
    bool NodeOllamaInferenceEnabled { set; }

    bool ScreenRecordingConsentGiven { set; }
    bool CameraRecordingConsentGiven { set; }
    bool VoiceTtsEnabled { set; }
    bool ShowChatToolCalls { set; }
}

/// <summary>
/// Immutable read snapshot of the settings values the settings surfaces display. Mirrors the
/// fields the settings page previously read directly off the settings manager.
/// </summary>
public sealed record SettingsSnapshot
{
    public long Version { get; init; }
    public bool AutoStart { get; init; }
    public bool GlobalHotkeyEnabled { get; init; }
    public bool UseLegacyWebChat { get; init; }
    public bool ShowNotifications { get; init; }
    public string NotificationSound { get; init; } = "Default";
    public string AppTheme { get; init; } = "System";

    /// <summary>The effective diagnostics visibility (override applied over the computed default).</summary>
    public bool ShowDiagnosticsEffective { get; init; }

    public bool NotifyHealth { get; init; }
    public bool NotifyUrgent { get; init; }
    public bool NotifyReminder { get; init; }
    public bool NotifyEmail { get; init; }
    public bool NotifyCalendar { get; init; }
    public bool NotifyBuild { get; init; }
    public bool NotifyStock { get; init; }
    public bool NotifyInfo { get; init; }

    public bool EnableNodeMode { get; init; }
    public bool EnableMcpServer { get; init; }
    public bool NodeSystemRunEnabled { get; init; }
    public bool NodeBrowserProxyEnabled { get; init; }
    public bool NodeCameraEnabled { get; init; }
    public bool NodeCanvasEnabled { get; init; }
    public bool NodeScreenEnabled { get; init; }
    public bool NodeLocationEnabled { get; init; }
    public bool NodeTtsEnabled { get; init; }
    public bool NodeSttEnabled { get; init; }
    public bool NodeOllamaInferenceEnabled { get; init; }
    public string SttModelName { get; init; } = "base";
    public string TtsProvider { get; init; } = "";
    public string TtsPiperVoiceId { get; init; } = "";
    public string TtsElevenLabsApiKey { get; init; } = "";
    public string TtsElevenLabsVoiceId { get; init; } = "";

    public bool ScreenRecordingConsentGiven { get; init; }
    public bool CameraRecordingConsentGiven { get; init; }

    public bool ShowChatToolCalls { get; init; }
}
