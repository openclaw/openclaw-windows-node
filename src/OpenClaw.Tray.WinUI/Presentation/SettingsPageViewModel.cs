using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

/// <summary>
/// WinUI-free view model for General, including notification preferences. It owns its
/// settings read/persist logic: it loads the current values from
/// <see cref="ISettingsStore"/> on activation, exposes two-way-bindable properties, and persists
/// each change through the store while preserving the exact auto-save contract
/// (mutate -> save -> notify).
/// </summary>
/// <remarks>
/// Echo handling lives in the store, not here: a property change persists through
/// <see cref="ISettingsStore.Update"/>, and the resulting <see cref="ISettingsStore.Changed"/>
/// event is ignored only when it carries this instance's writer token. Any other change arrives
/// via <see cref="ISettingsStore.Changed"/> and reloads the properties under the <c>_loading</c>
/// guard so it cannot re-persist. View-only side effects (the "Saved" indicator flash and
/// refreshing the view-owned gateway section) are surfaced as events the page handles. OS-coupled
/// auto-start registration is delegated to <see cref="IAppCommands"/>, so this type stays free of
/// WinUI and OS APIs.
/// </remarks>
internal sealed class SettingsPageViewModel : INavigationAware, IDisposable, INotifyPropertyChanged
{
    private const string DefaultNotificationSound = "Default";
    private const string DefaultAppTheme = "System";

    private static readonly string[] SoundTags = { "Default", "None", "Subtle" };
    private static readonly string[] ThemeTags = { "System", "Light", "Dark" };

    private readonly ISettingsStore _store;
    private readonly IAppCommands _appCommands;
    private readonly SettingsWriteOrigin _origin;
    private readonly Theming.IGatewayThemeCatalog? _themes;

    private bool _loading;
    private bool _subscribed;
    private long _lastAppliedSettingsVersion = -1;

    private OpenClaw.Shared.Themes.AppearanceSettings _appearance = OpenClaw.Shared.Themes.AppearanceSettings.Default;
    private IReadOnlyList<ThemeChoice> _themeChoices = [];
    private string _selectedThemeKey = "";
    private string _defaultThemeLabel = "Windows default";
    private bool _useMica = true;
    private bool _followGatewayTheme;

    private bool _autoStart;
    private bool _globalHotkeyEnabled;
    private bool _useLegacyWebChat;
    private bool _showNotifications;
    private bool _showDesktopCompanion;
    private int _desktopCompanionScalePercent = DesktopCompanionScale.DefaultPercent;
    private string _notificationSound = DefaultNotificationSound;
    private string _appTheme = DefaultAppTheme;
    private bool _showDiagnostics;
    private bool _notifyHealth;
    private bool _notifyUrgent;
    private bool _notifyReminder;
    private bool _notifyEmail;
    private bool _notifyCalendar;
    private bool _notifyBuild;
    private bool _notifyStock;
    private bool _notifyInfo;
    private bool _showChatToolCalls;
    private bool _showChatThinking = true;

    public SettingsPageViewModel(ISettingsStore store, IAppCommands appCommands, Theming.IGatewayThemeCatalog? themes = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _appCommands = appCommands ?? throw new ArgumentNullException(nameof(appCommands));
        _themes = themes;
        _origin = _store.CreateOrigin();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised (on the caller's thread) when a change should flash the page's "Saved" indicator.</summary>
    public event EventHandler? SavedIndicated;

    /// <summary>
    /// Raised when the settings change from an external source (not this view model's own writes),
    /// so the page can refresh the view-owned sections that are not settings-bound (the gateway
    /// section). Not raised during the initial activation load.
    /// </summary>
    public event EventHandler? ExternalChanged;

    internal bool IsActive { get; private set; }
    internal bool IsDisposed { get; private set; }

    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (SetField(ref _autoStart, value) && !_loading)
            {
                // Auto-start owns an OS registration with a required save -> OS-write -> notify
                // order. It is applied through the app command that preserves that ordering, and
                // the saved indicator is flashed only after the OS write succeeds (matching the
                // original behavior where a failed OS write showed no confirmation).
                _ = ApplyAutoStartAsync(value);
            }
        }
    }

    private async Task ApplyAutoStartAsync(bool value)
    {
        if (await _appCommands.ApplyAutoStart(_origin, value))
        {
            RaiseSaved();
            return;
        }

        _loading = true;
        try
        {
            SetField(ref _autoStart, _store.Current.AutoStart, nameof(AutoStart));
        }
        finally
        {
            _loading = false;
        }
    }

    public bool GlobalHotkeyEnabled
    {
        get => _globalHotkeyEnabled;
        set { if (SetField(ref _globalHotkeyEnabled, value) && !_loading) Persist(e => e.GlobalHotkeyEnabled = value); }
    }

    public bool UseLegacyWebChat
    {
        get => _useLegacyWebChat;
        set { if (SetField(ref _useLegacyWebChat, value) && !_loading) Persist(e => e.UseLegacyWebChat = value); }
    }

    public bool ShowNotifications
    {
        get => _showNotifications;
        set { if (SetField(ref _showNotifications, value) && !_loading) Persist(e => e.ShowNotifications = value); }
    }

    public string NotificationSound
    {
        get => _notificationSound;
        set
        {
            var normalized = string.IsNullOrEmpty(value) ? DefaultNotificationSound : value;
            if (SetField(ref _notificationSound, normalized) && !_loading) Persist(e => e.NotificationSound = normalized);
        }
    }

    public bool ShowDesktopCompanion
    {
        get => _showDesktopCompanion;
        set { if (SetField(ref _showDesktopCompanion, value) && !_loading) Persist(e => e.ShowDesktopCompanion = value); }
    }

    public IReadOnlyList<int> DesktopCompanionScaleOptions { get; } = [50, 75, 100, 125, 150, 175, 200];

    public int DesktopCompanionScalePercent
    {
        get => _desktopCompanionScalePercent;
        set
        {
            var percent = DesktopCompanionScale.Normalize(value);
            if (!SetField(ref _desktopCompanionScalePercent, percent)) return;
            Persist(edit => edit.DesktopCompanionScalePercent = percent);
        }
    }

    public string AppTheme
    {
        get => _appTheme;
        set
        {
            var normalized = string.IsNullOrEmpty(value) ? DefaultAppTheme : value;
            if (SetField(ref _appTheme, normalized) && !_loading) Persist(e => e.AppTheme = normalized);
        }
    }

    /// <summary>Theme picker items: the Windows default first, then built-in, plugin and imported Gateway themes.</summary>
    public IReadOnlyList<ThemeChoice> ThemeChoices
    {
        get => _themeChoices;
        private set => SetField(ref _themeChoices, value);
    }

    /// <summary>Localized label of the Windows default choice, set by the page.</summary>
    public string DefaultThemeLabel
    {
        get => _defaultThemeLabel;
        set
        {
            if (SetField(ref _defaultThemeLabel, string.IsNullOrWhiteSpace(value) ? _defaultThemeLabel : value))
                RebuildThemeChoices();
        }
    }

    /// <summary>Selected theme id, or empty for the Windows default. While following the Gateway it shows the followed theme.</summary>
    public string SelectedThemeKey
    {
        get => _followGatewayTheme
            ? OpenClaw.Shared.Themes.AppearanceResolver.ResolveTheme(_appearance)?.Id ?? ""
            : _selectedThemeKey;
        set
        {
            // Replacing ThemeChoices makes the ComboBox write null before it reselects; keep the saved key.
            // The picker is disabled while following, so a write then can only come from that reselection.
            if (_loading || _followGatewayTheme)
                return;
            var key = value ?? "";
            if (!SetField(ref _selectedThemeKey, key))
                return;
            var selection = SelectionFor(key);
            if (selection == _appearance.Theme)
                return;
            PersistAppearance(appearance => appearance with { Theme = selection });
        }
    }

    public bool UseMica
    {
        get => _useMica;
        set
        {
            if (!SetField(ref _useMica, value) || _loading)
                return;
            PersistAppearance(appearance => appearance with { UseMica = value });
        }
    }

    /// <summary>Show the connected Gateway's selected dashboard theme; the manual picker is disabled meanwhile.</summary>
    public bool FollowGatewayTheme
    {
        get => _followGatewayTheme;
        set
        {
            if (!SetField(ref _followGatewayTheme, value) || _loading)
                return;
            PersistAppearance(appearance => appearance with { FollowGatewayTheme = value });
            RaiseThemePickerState();
        }
    }

    public bool IsManualThemeEnabled => !_followGatewayTheme;

    // Edits the stored appearance rather than this page's copy: App records followed Gateway themes in the
    // same object, and a stale copy would overwrite them.
    private void PersistAppearance(Func<OpenClaw.Shared.Themes.AppearanceSettings, OpenClaw.Shared.Themes.AppearanceSettings> change)
    {
        var next = change(_store.Current.Appearance);
        _appearance = next;
        Persist(e => e.Appearance = next);
    }

    private void RaiseThemePickerState()
    {
        RebuildThemeChoices();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsManualThemeEnabled)));
    }

    private OpenClaw.Shared.Themes.ThemeSelection? SelectionFor(string key)
    {
        if (key.Length == 0)
            return null;
        if (_themes?.Options.FirstOrDefault(option => option.Id == key) is { } option)
            return OpenClaw.Shared.Themes.AppearanceResolver.Select(option.Descriptor, option.Definition, option.GatewayId);
        if (OpenClaw.Shared.Themes.BuiltinGatewayThemes.TryGet(key, out var builtin))
            return new OpenClaw.Shared.Themes.ThemeSelection { Id = builtin.Id, Name = builtin.Name };
        // An offline imported or followed theme stays selectable from its saved snapshot.
        return _appearance.Theme?.Id == key ? _appearance.Theme
            : _appearance.FollowedTheme?.Id == key ? _appearance.FollowedTheme
            : null;
    }

    private void RebuildThemeChoices()
    {
        var choices = new List<ThemeChoice> { new("", _defaultThemeLabel) };
        if (_themes is null)
            choices.AddRange(OpenClaw.Shared.Themes.BuiltinGatewayThemes.All.Select(theme => new ThemeChoice(theme.Id, theme.Name)));
        else
            choices.AddRange(_themes.Options.Select(option => new ThemeChoice(option.Id, option.Name)));
        // Offline plugin or imported themes stay listed from their saved snapshots, if they can still be applied.
        foreach (var saved in new[] { _appearance.Theme, _appearance.FollowedTheme })
        {
            if (saved is not null && OpenClaw.Shared.Themes.AppearanceResolver.Resolve(saved) is not null &&
                choices.All(choice => choice.Key != saved.Id))
                choices.Add(new ThemeChoice(saved.Id, saved.Name ?? saved.Id));
        }
        // Replacing the items closes an open picker, so an unchanged list (most catalog refreshes) keeps them.
        if (choices.SequenceEqual(_themeChoices))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedThemeKey)));
            return;
        }
        _loading = true;
        try
        {
            ThemeChoices = choices;
            // Re-raise so the picker reselects after its items were replaced.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedThemeKey)));
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnThemesChanged(object? sender, EventArgs e)
    {
        if (IsActive)
            RebuildThemeChoices();
    }

    /// <summary>Bound to the diagnostics toggle: reads the effective value, writes the override.</summary>
    public bool ShowDiagnostics
    {
        get => _showDiagnostics;
        set { if (SetField(ref _showDiagnostics, value) && !_loading) Persist(e => e.ShowDiagnosticsOverride = value); }
    }

    public bool NotifyHealth
    {
        get => _notifyHealth;
        set { if (SetField(ref _notifyHealth, value) && !_loading) Persist(e => e.NotifyHealth = value); }
    }

    public bool NotifyUrgent
    {
        get => _notifyUrgent;
        set { if (SetField(ref _notifyUrgent, value) && !_loading) Persist(e => e.NotifyUrgent = value); }
    }

    public bool NotifyReminder
    {
        get => _notifyReminder;
        set { if (SetField(ref _notifyReminder, value) && !_loading) Persist(e => e.NotifyReminder = value); }
    }

    public bool NotifyEmail
    {
        get => _notifyEmail;
        set { if (SetField(ref _notifyEmail, value) && !_loading) Persist(e => e.NotifyEmail = value); }
    }

    public bool NotifyCalendar
    {
        get => _notifyCalendar;
        set { if (SetField(ref _notifyCalendar, value) && !_loading) Persist(e => e.NotifyCalendar = value); }
    }

    public bool NotifyBuild
    {
        get => _notifyBuild;
        set { if (SetField(ref _notifyBuild, value) && !_loading) Persist(e => e.NotifyBuild = value); }
    }

    public bool NotifyStock
    {
        get => _notifyStock;
        set { if (SetField(ref _notifyStock, value) && !_loading) Persist(e => e.NotifyStock = value); }
    }

    public bool NotifyInfo
    {
        get => _notifyInfo;
        set { if (SetField(ref _notifyInfo, value) && !_loading) Persist(e => e.NotifyInfo = value); }
    }

    /// <summary>
    /// "Show tool calls and usage" persists the setting. App's settings-save path
    /// applies it to every live Reactor chat host.
    /// </summary>
    public bool ShowChatToolCalls
    {
        get => _showChatToolCalls;
        set
        {
            if (SetField(ref _showChatToolCalls, value) && !_loading)
            {
                Persist(e => e.ShowChatToolCalls = value);
            }
        }
    }

    /// <summary>
    /// "Show thinking" persists the setting. App's settings-save path applies it
    /// to every live Reactor chat host.
    /// </summary>
    public bool ShowChatThinking
    {
        get => _showChatThinking;
        set
        {
            if (SetField(ref _showChatThinking, value) && !_loading)
            {
                Persist(e => e.ShowChatThinking = value);
            }
        }
    }

    public void Activate(object? parameter)
    {
        IsActive = true;
        if (!_subscribed)
        {
            _store.Changed += OnStoreChanged;
            if (_themes is not null)
                _themes.Changed += OnThemesChanged;
            _subscribed = true;
        }

        LoadCurrentFromStore();
        _ = _themes?.RefreshAsync();
    }

    public void Deactivate()
    {
        IsActive = false;
        if (_subscribed)
        {
            _store.Changed -= OnStoreChanged;
            if (_themes is not null)
                _themes.Changed -= OnThemesChanged;
            _subscribed = false;
        }
    }

    public void Dispose()
    {
        Deactivate();
        IsDisposed = true;
    }

    private void OnStoreChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (!IsActive || !TryAdvanceSettingsVersion(e.Version))
        {
            return;
        }

        if (ApplySettingsSnapshotIfCurrent(e.Snapshot))
        {
            if (!ReferenceEquals(e.Origin, _origin))
            {
                ExternalChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Reloads all bound properties from the store snapshot without persisting.</summary>
    private void LoadCurrentFromStore()
    {
        var snapshot = _store.Current;
        TryAdvanceSettingsVersion(snapshot.Version);
        ApplySettingsSnapshotIfCurrent(snapshot);
    }

    private bool ApplySettingsSnapshotIfCurrent(SettingsSnapshot snapshot)
    {
        if (snapshot.Version != Volatile.Read(ref _lastAppliedSettingsVersion))
        {
            return false;
        }

        LoadFromStore(snapshot);
        return snapshot.Version == Volatile.Read(ref _lastAppliedSettingsVersion);
    }

    private bool TryAdvanceSettingsVersion(long version)
    {
        while (true)
        {
            var current = Volatile.Read(ref _lastAppliedSettingsVersion);
            if (version <= current)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _lastAppliedSettingsVersion, version, current) == current)
            {
                return true;
            }
        }
    }

    private void LoadFromStore(SettingsSnapshot s)
    {
        _loading = true;
        try
        {
            SetField(ref _autoStart, s.AutoStart, nameof(AutoStart));
            SetField(ref _globalHotkeyEnabled, s.GlobalHotkeyEnabled, nameof(GlobalHotkeyEnabled));
            SetField(ref _useLegacyWebChat, s.UseLegacyWebChat, nameof(UseLegacyWebChat));
            SetField(ref _showNotifications, s.ShowNotifications, nameof(ShowNotifications));
            SetField(ref _showDesktopCompanion, s.ShowDesktopCompanion, nameof(ShowDesktopCompanion));
            SetField(ref _desktopCompanionScalePercent, s.DesktopCompanionScalePercent, nameof(DesktopCompanionScalePercent));
            // Normalize combo values to a known tag so a legacy or odd stored value selects the
            // default item instead of rendering a blank combo (matching the old tag lookup).
            SetField(ref _notificationSound, NormalizeTag(s.NotificationSound, SoundTags, DefaultNotificationSound), nameof(NotificationSound));
            SetField(ref _appTheme, NormalizeTag(s.AppTheme, ThemeTags, DefaultAppTheme), nameof(AppTheme));
            _appearance = s.Appearance;
            SetField(ref _useMica, s.Appearance.UseMica, nameof(UseMica));
            SetField(ref _followGatewayTheme, s.Appearance.FollowGatewayTheme, nameof(FollowGatewayTheme));
            // A saved theme that can no longer be applied (for example one now below the contrast floor) shows the
            // Windows default it falls back to.
            SetField(ref _selectedThemeKey,
                OpenClaw.Shared.Themes.AppearanceResolver.Resolve(s.Appearance.Theme) is null ? "" : s.Appearance.Theme!.Id,
                nameof(SelectedThemeKey));
            SetField(ref _showDiagnostics, s.ShowDiagnosticsEffective, nameof(ShowDiagnostics));
            SetField(ref _notifyHealth, s.NotifyHealth, nameof(NotifyHealth));
            SetField(ref _notifyUrgent, s.NotifyUrgent, nameof(NotifyUrgent));
            SetField(ref _notifyReminder, s.NotifyReminder, nameof(NotifyReminder));
            SetField(ref _notifyEmail, s.NotifyEmail, nameof(NotifyEmail));
            SetField(ref _notifyCalendar, s.NotifyCalendar, nameof(NotifyCalendar));
            SetField(ref _notifyBuild, s.NotifyBuild, nameof(NotifyBuild));
            SetField(ref _notifyStock, s.NotifyStock, nameof(NotifyStock));
            SetField(ref _notifyInfo, s.NotifyInfo, nameof(NotifyInfo));
            SetField(ref _showChatToolCalls, s.ShowChatToolCalls, nameof(ShowChatToolCalls));
            SetField(ref _showChatThinking, s.ShowChatThinking, nameof(ShowChatThinking));
        }
        finally
        {
            _loading = false;
        }
        RaiseThemePickerState();
    }

    private void Persist(Action<ISettingsEditor> edit)
    {
        if (_loading)
        {
            return;
        }

        _store.Update(_origin, edit);
        _appCommands.NotifySettingsSaved();
        RaiseSaved();
    }

    private void RaiseSaved() => SavedIndicated?.Invoke(this, EventArgs.Empty);

    /// <summary>Maps a stored value to a known combo tag (case-insensitive), falling back to the default.</summary>
    private static string NormalizeTag(string? value, string[] tags, string fallback)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag, value, StringComparison.OrdinalIgnoreCase))
            {
                return tag;
            }
        }

        return fallback;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

/// <summary>One theme picker item; an empty <see cref="Key"/> is the Windows default look.</summary>
public sealed record ThemeChoice(string Key, string Name)
{
    // ComboBox item automation peers fall back to ToString for their accessible name.
    public override string ToString() => Name;
}
