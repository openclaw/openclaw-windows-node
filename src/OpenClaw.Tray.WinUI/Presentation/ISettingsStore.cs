namespace OpenClawTray.Presentation;

/// <summary>
/// WinUI-free seam over the App-owned settings for presentation code. It exposes an
/// immutable <see cref="SettingsSnapshot"/> for reads and a single batched
/// <see cref="Update"/> for writes, and raises <see cref="Changed"/> only for
/// <b>external</b> changes (a caller's own <see cref="Update"/> is not echoed back).
/// </summary>
/// <remarks>
/// This removes the hand-rolled save/echo bookkeeping that page code-behind used to carry
/// (the <c>_saving</c>/<c>_loading</c> suppression flags). A view model reads
/// <see cref="Current"/>, writes through <see cref="Update"/>, and refreshes on
/// <see cref="Changed"/> without risking a save-storm: the store suppresses the
/// self-originated notification and marshals external ones onto the UI thread.
/// The auto-save contract itself is unchanged: <see cref="Update"/> mutates the settings
/// and saves once, exactly as the previous per-toggle code did; callers still invoke
/// <c>IAppCommands.NotifySettingsSaved()</c> for the reconnect/re-register side effect.
/// </remarks>
public interface ISettingsStore
{
    /// <summary>An immutable snapshot of the current settings values used by the settings surfaces.</summary>
    SettingsSnapshot Current { get; }

    /// <summary>
    /// Applies <paramref name="edit"/> to the settings and persists once. The store
    /// suppresses the <see cref="Changed"/> notification that would otherwise echo back to
    /// the caller from this save, so a two-way-bound view model cannot loop.
    /// </summary>
    void Update(Action<ISettingsEditor> edit);

    /// <summary>
    /// Marks the calling thread as performing a store-originated write for the scope's lifetime,
    /// so a <see cref="SettingsManager.Save"/> raised on that thread is treated as self-originated
    /// and does not echo <see cref="Changed"/>. Used by App-owned writes that persist settings
    /// directly (auto-start OS registration, speaker mute) instead of through <see cref="Update"/>,
    /// so they get the same echo suppression. Dispose on the same thread that created it.
    /// </summary>
    IDisposable BeginSelfWrite();

    /// <summary>
    /// Raised after settings change from a source other than this caller's <see cref="Update"/>
    /// (for example another surface saving, onboarding, or a background save). Always raised on
    /// the UI thread.
    /// </summary>
    event EventHandler? Changed;
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

    bool ScreenRecordingConsentGiven { set; }
    bool CameraRecordingConsentGiven { set; }

    bool ShowChatToolCalls { set; }
}

/// <summary>
/// Immutable read snapshot of the settings values the settings surfaces display. Mirrors the
/// fields the settings page previously read directly off the settings manager.
/// </summary>
public sealed record SettingsSnapshot
{
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

    public bool ScreenRecordingConsentGiven { get; init; }
    public bool CameraRecordingConsentGiven { get; init; }

    /// <summary>Reflects <c>VoiceTtsEnabled</c>; the "read responses aloud" toggle mirrors it.</summary>
    public bool VoiceTtsEnabled { get; init; }
    public bool ShowChatToolCalls { get; init; }
}
