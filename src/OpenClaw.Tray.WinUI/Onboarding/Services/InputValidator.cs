using System;
using System.IO;
using System.Linq;

namespace OpenClawTray.Onboarding.Services;

/// <summary>
/// Centralizes input validation for security-sensitive operations.
/// Extracted from inline checks in App.xaml.cs, OnboardingWindow.cs, and PermissionsPage.cs.
/// </summary>
public static class InputValidator
{
    private static readonly string[] AllowedLocales = ["en-us", "fr-fr", "nl-nl", "zh-cn", "zh-tw"];

    /// <summary>
    /// Validates a locale string against the allowed whitelist.
    /// </summary>
    public static bool IsValidLocale(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return false;
        return AllowedLocales.Contains(locale.ToLowerInvariant());
    }

    /// <summary>
    /// Validates a port string is a number in the range 1–65535.
    /// </summary>
    public static bool IsValidPort(string portStr)
    {
        if (string.IsNullOrWhiteSpace(portStr)) return false;
        return int.TryParse(portStr, out var p) && p >= 1 && p <= 65535;
    }

    /// <summary>
    /// Validates a directory path is safe (no null bytes, no path traversal).
    /// Returns the full path if valid, null otherwise.
    /// </summary>
    public static string? ValidateTestDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path.Contains('\0')) return null;

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (fullPath.Contains("..")) return null;
            return fullPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Validates that a URI uses the ms-settings: scheme.
    /// </summary>
    public static bool IsSettingsUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;
        return Uri.TryCreate(uri, UriKind.Absolute, out var u)
            && u.Scheme.Equals("ms-settings", StringComparison.OrdinalIgnoreCase);
    }
}
