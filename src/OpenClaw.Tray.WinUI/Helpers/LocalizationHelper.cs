using System.Collections.Concurrent;
using Microsoft.Windows.ApplicationModel.Resources;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Helpers;

public static class LocalizationHelper
{
    private static ResourceManager? _resourceManager;
    private static ResourceContext? _overrideContext;
    private static string? _languageOverride;
    private static readonly ConcurrentDictionary<string, byte> s_loggedLookupFailures = new();
    private const int MaxLoggedLookupFailures = 1024;
    private static int s_lookupFailureLimitLogged;

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
        catch (Exception ex)
        {
            var logKey = $"{_languageOverride ?? "<default>"}:{resourceKey}:{ex.GetType().FullName}";
            if (s_loggedLookupFailures.ContainsKey(logKey))
                return resourceKey;

            if (s_loggedLookupFailures.Count < MaxLoggedLookupFailures && s_loggedLookupFailures.TryAdd(logKey, 0))
            {
                Logger.Warn($"Resource lookup failed for '{resourceKey}' (language='{_languageOverride ?? "<default>"}'): {ex.Message}");
            }
            else if (System.Threading.Interlocked.Exchange(ref s_lookupFailureLimitLogged, 1) == 0)
            {
                Logger.Warn("Resource lookup failure log limit reached; suppressing additional unique resource lookup failures");
            }
            return resourceKey;
        }
    }

    /// <summary>
    /// Localized <see cref="string.Format(string, object[])"/>. Use for resw values that
    /// contain placeholders like "{0}". Catches <see cref="FormatException"/> caused by
    /// malformed translations (e.g., a translator writing "{2}" with one arg, or "{a}")
    /// so the UI thread can't crash on a translator typo.
    /// </summary>
    public static string Format(string resourceKey, params object?[] args)
    {
        var template = GetString(resourceKey);
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            // Surface the unformatted template instead of crashing. The raw "{0}"
            // is still useful debugging signal but doesn't kill the page.
            Logger.Warn($"Resource format failed for '{resourceKey}': template='{template}'");
            return template;
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
