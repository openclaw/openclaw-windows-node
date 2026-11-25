using System.Collections.Concurrent;
using Microsoft.Windows.ApplicationModel.Resources;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Helpers;

public static class LocalizationHelper
{
    public static string CurrentLanguage =>
        _languageOverride ?? System.Globalization.CultureInfo.CurrentUICulture.Name;

    private static ResourceManager? _resourceManager;
    private static ResourceContext? _context;
    private static string? _languageOverride;
    private static readonly object s_resourceLock = new();
    private static readonly Dictionary<string, string> s_strings = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> s_loggedLookupFailures = new();
    private const int MaxLoggedLookupFailures = 1024;
    private static int s_lookupFailureLimitLogged;

    /// <summary>
    /// Force a specific language for testing (e.g. "zh-CN").
    /// Invalidates cached strings and resource qualifiers.
    /// </summary>
    public static void SetLanguageOverride(string language)
    {
        lock (s_resourceLock)
        {
            _languageOverride = language;
            _resourceManager = null;
            _context = null;
            s_strings.Clear();
        }
    }

    private static ResourceManager Manager => _resourceManager ??= new ResourceManager();

    private static ResourceContext GetContext()
    {
        if (_context != null) return _context;
        _context = Manager.CreateResourceContext();
        if (_languageOverride != null)
            _context.QualifierValues["Language"] = _languageOverride;
        return _context;
    }

    public static string GetString(string resourceKey)
    {
        lock (s_resourceLock)
        {
            if (s_strings.TryGetValue(resourceKey, out var cached))
                return cached;
            return LookupString(resourceKey);
        }
    }

    private static string LookupString(string resourceKey)
    {
        var found = TryGetValueAsString(resourceKey, out var value, out var lookupFailure);
        if (found && !string.IsNullOrEmpty(value))
        {
            return s_strings[resourceKey] = value;
        }

        if (TryGetXamlPropertyResourcePath(resourceKey, out var propertyResourcePath))
        {
            var propertyFound = TryGetValueAsString(propertyResourcePath, out value, out var propertyLookupFailure);
            if (propertyFound && !string.IsNullOrEmpty(value))
            {
                return s_strings[resourceKey] = value;
            }

            if (!found)
            {
                lookupFailure ??= propertyLookupFailure;
            }
        }

        if (lookupFailure is not null)
        {
            var logKey = $"{_languageOverride ?? "<default>"}:{resourceKey}:{lookupFailure.GetType().FullName}";
            if (s_loggedLookupFailures.ContainsKey(logKey))
                return resourceKey;

            if (s_loggedLookupFailures.Count < MaxLoggedLookupFailures && s_loggedLookupFailures.TryAdd(logKey, 0))
            {
                Logger.Warn($"LocalizationHelper: Resource lookup failed for '{resourceKey}' (language='{_languageOverride ?? "<default>"}'): {lookupFailure.Message}");
            }
            else if (System.Threading.Interlocked.Exchange(ref s_lookupFailureLimitLogged, 1) == 0)
            {
                Logger.Warn("LocalizationHelper: Resource lookup failure log limit reached; suppressing additional unique resource lookup failures");
            }
        }

        return resourceKey;
    }

    private static bool TryGetValueAsString(string resourceKey, out string? value, out Exception? lookupFailure)
    {
        try
        {
            var candidate = Manager.MainResourceMap.GetValue($"Resources/{resourceKey}", GetContext());
            value = candidate?.ValueAsString;
            lookupFailure = null;
            return true;
        }
        catch (Exception ex)
        {
            value = null;
            lookupFailure = ex;
            return false;
        }
    }

    private static bool TryGetXamlPropertyResourcePath(string resourceKey, out string resourcePath)
    {
        var propertySeparator = resourceKey.LastIndexOf('.');
        if (propertySeparator > 0 && propertySeparator < resourceKey.Length - 1)
        {
            resourcePath = $"{resourceKey[..propertySeparator]}/{resourceKey[(propertySeparator + 1)..]}";
            return true;
        }

        resourcePath = string.Empty;
        return false;
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
            Logger.Warn($"LocalizationHelper: Resource format failed for '{resourceKey}': template='{template}'");
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
