using OpenClawTray.Presentation;

namespace OpenClawTray.Services;

/// <summary>
/// The single executor for command-search results, shared by the Settings and main-screen
/// search flyouts. Navigation is delegated to the calling surface so each keeps its own history
/// and context. The App-bound default constructor lives in HubCommandExecutor.App.cs so this
/// file stays free of WinUI types.
/// </summary>
internal sealed partial class HubCommandExecutor
{
    private readonly Func<SettingsManager?> _settings;
    private readonly Func<ISettingsStore?> _store;
    private readonly Action<string?> _openDashboard;
    private SettingsWriteOrigin? _settingsOrigin;
    internal HubCommandExecutor(
        Func<SettingsManager?> settings, Func<ISettingsStore?> store, Action<string?> openDashboard)
    {
        _settings = settings;
        _store = store;
        _openDashboard = openDashboard;
    }

    /// <summary>
    /// Runs a catalog result. A persisted toggle must be applied to the running app, so callers
    /// pass the same settings-saved callback their surface raises for page-driven changes.
    /// </summary>
    internal void Execute(HubCommand command, Action<string> navigate, Action? settingsSaved = null)
    {
        switch (command.Action.Kind)
        {
            case HubCommandActionKind.Navigate when command.Action.Value is { Length: > 0 } tag:
                navigate(tag);
                break;
            case HubCommandActionKind.OpenDashboard:
                _openDashboard(command.Action.Value);
                break;
            case HubCommandActionKind.ToggleSetting when command.Action.Toggle is { } toggle:
                if (!ToggleSetting(toggle)) break;
                try
                {
                    settingsSaved?.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[HubCommandExecutor] Failed to apply saved setting {GetSettingName(toggle)}: {ex}");
                }
                break;
        }
    }

    private bool ToggleSetting(HubSettingToggle toggle)
    {
        var settings = _settings();
        if (settings == null)
            return false;

        var settingName = GetSettingName(toggle);
        var nextValue = !GetCurrentSettingValue(settings, toggle);
        try
        {
            if (_store() is { } store)
            {
                _settingsOrigin ??= store.CreateOrigin();
                store.Update(_settingsOrigin, edit => ApplySettingValue(edit, toggle, nextValue));
            }
            else
            {
                Logger.Warn($"[HubCommandExecutor] ISettingsStore unavailable for {settingName}. Falling back to SettingsManager.Save.");
                ApplySettingValue(settings, toggle, nextValue);
                settings.Save();
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[HubCommandExecutor] Failed to persist {settingName}: {ex.Message}");
            return false;
        }
    }

    private static string GetSettingName(HubSettingToggle toggle) => toggle switch
    {
        HubSettingToggle.NodeMode => nameof(SettingsManager.EnableNodeMode),
        HubSettingToggle.Camera => nameof(SettingsManager.NodeCameraEnabled),
        HubSettingToggle.Canvas => nameof(SettingsManager.NodeCanvasEnabled),
        HubSettingToggle.ScreenCapture => nameof(SettingsManager.NodeScreenEnabled),
        HubSettingToggle.BrowserControl => nameof(SettingsManager.NodeBrowserProxyEnabled),
        _ => throw new ArgumentOutOfRangeException(nameof(toggle))
    };

    private static bool GetCurrentSettingValue(SettingsManager settings, HubSettingToggle toggle) =>
        toggle switch
        {
            HubSettingToggle.NodeMode => settings.EnableNodeMode,
            HubSettingToggle.Camera => settings.NodeCameraEnabled,
            HubSettingToggle.Canvas => settings.NodeCanvasEnabled,
            HubSettingToggle.ScreenCapture => settings.NodeScreenEnabled,
            HubSettingToggle.BrowserControl => settings.NodeBrowserProxyEnabled,
            _ => throw new ArgumentOutOfRangeException(nameof(toggle))
        };

    private static void ApplySettingValue(ISettingsEditor settings, HubSettingToggle toggle, bool value)
    {
        switch (toggle)
        {
            case HubSettingToggle.NodeMode: settings.EnableNodeMode = value; break;
            case HubSettingToggle.Camera: settings.NodeCameraEnabled = value; break;
            case HubSettingToggle.Canvas: settings.NodeCanvasEnabled = value; break;
            case HubSettingToggle.ScreenCapture: settings.NodeScreenEnabled = value; break;
            case HubSettingToggle.BrowserControl: settings.NodeBrowserProxyEnabled = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(toggle));
        }
    }

    private static void ApplySettingValue(SettingsManager settings, HubSettingToggle toggle, bool value)
    {
        switch (toggle)
        {
            case HubSettingToggle.NodeMode: settings.EnableNodeMode = value; break;
            case HubSettingToggle.Camera: settings.NodeCameraEnabled = value; break;
            case HubSettingToggle.Canvas: settings.NodeCanvasEnabled = value; break;
            case HubSettingToggle.ScreenCapture: settings.NodeScreenEnabled = value; break;
            case HubSettingToggle.BrowserControl: settings.NodeBrowserProxyEnabled = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(toggle));
        }
    }
}
