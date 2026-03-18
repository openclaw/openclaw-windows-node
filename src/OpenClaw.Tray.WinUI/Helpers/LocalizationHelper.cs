using Microsoft.Windows.ApplicationModel.Resources;
using OpenClaw.Shared;

namespace OpenClawTray.Helpers;

public static class LocalizationHelper
{
    private static ResourceManager? _resourceManager;
    private static ResourceContext? _overrideContext;
    private static string? _languageOverride;

    /// <summary>
    /// Force a specific language for testing (e.g. "zh-CN").
    /// Must be called before any GetString calls.
    /// </summary>
    public static void SetLanguageOverride(string language)
    {
        _languageOverride = language;
        _resourceManager = null;
        _overrideContext = null;
    }

    private static ResourceManager Manager => _resourceManager ??= new ResourceManager();

    private static ResourceContext GetContext()
    {
        if (_overrideContext != null) return _overrideContext;
        if (_languageOverride != null)
        {
            _overrideContext = Manager.CreateResourceContext();
            _overrideContext.QualifierValues["Language"] = _languageOverride;
            return _overrideContext;
        }
        return Manager.CreateResourceContext();
    }

    public static string GetString(string resourceKey)
    {
        try
        {
            var candidate = Manager.MainResourceMap.GetValue($"Resources/{resourceKey}", GetContext());
            var value = candidate?.ValueAsString;
            return string.IsNullOrEmpty(value) ? resourceKey : value;
        }
        catch
        {
            return resourceKey;
        }
    }

    public static string GetConnectionStatusText(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Connected => GetString("StatusDisplay_Connected"),
        ConnectionStatus.Connecting => GetString("StatusDisplay_Connecting"),
        ConnectionStatus.Disconnected => GetString("StatusDisplay_Disconnected"),
        ConnectionStatus.Error => GetString("StatusDisplay_Error"),
        _ => GetString("StatusDisplay_Unknown")
    };
}
