using System.Diagnostics;
using Microsoft.Windows.ApplicationModel.Resources;

namespace OpenClaw.SetupEngine.UI;

/// <summary>
/// Resource-string lookup for <see cref="OpenClaw.SetupEngine.UI"/> code-behind. This project has
/// no <c>.resw</c> files of its own and cannot reference the Tray app project (which references
/// this project, so the reverse would be circular). There is no standalone Setup UI executable in
/// Release builds: this project is always hosted inside the Tray app process, so string keys
/// resolve against the same merged app resource map that the Tray app's own localization helper
/// reads, using keys defined in the Tray app's <c>Strings\*\Resources.resw</c> files (the existing
/// <c>x:Uid="Onboarding_LocalAi_RecheckAvailabilityButton"</c> binding already relies on the same
/// mechanism for XAML-declared strings).
/// </summary>
internal static class SetupLocalization
{
    private static ResourceManager? s_resourceManager;

    private static ResourceManager Manager => s_resourceManager ??= new ResourceManager();

    public static string GetString(string resourceKey)
    {
        string? value = TryGetValueAsString(resourceKey);
        if (!string.IsNullOrEmpty(value))
            return value;

        // XAML property resources (the ones an x:Uid="Key" binding resolves for a "Key.Property"
        // entry, e.g. "Onboarding_Welcome_LocalAiAvailableBadge.Text") are stored under a
        // "Key/Property" path, not a literal dot. Retry that shape so code-behind can share the
        // same resw entry an x:Uid binding already uses, instead of duplicating the string under
        // a second key.
        int propertySeparator = resourceKey.LastIndexOf('.');
        if (propertySeparator > 0 && propertySeparator < resourceKey.Length - 1)
        {
            string propertyResourcePath =
                $"{resourceKey[..propertySeparator]}/{resourceKey[(propertySeparator + 1)..]}";
            value = TryGetValueAsString(propertyResourcePath);
            if (!string.IsNullOrEmpty(value))
                return value;
        }

        return resourceKey;
    }

    private static string? TryGetValueAsString(string resourceKey)
    {
        try
        {
            ResourceCandidate? candidate = Manager.MainResourceMap.GetValue(
                $"Resources/{resourceKey}",
                Manager.CreateResourceContext());
            return candidate?.ValueAsString;
        }
        catch (Exception ex)
        {
            // Trace.TraceWarning (unlike Debug.WriteLine) is not compiled out in Release: the
            // TRACE constant is defined in both build configurations by default, and the
            // default trace listener forwards to OutputDebugString, so this remains visible via
            // DebugView/ETW in a packaged Release build instead of silently disappearing.
            Trace.TraceWarning($"SetupLocalization: resource lookup failed for '{resourceKey}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Localized <see cref="string.Format(string, object?[])"/>. Catches <see cref="FormatException"/>
    /// caused by a malformed translation so a translator typo can't crash the UI thread.
    /// </summary>
    public static string Format(string resourceKey, params object?[] args)
    {
        string template = GetString(resourceKey);
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            Trace.TraceWarning($"SetupLocalization: format failed for '{resourceKey}': template='{template}'");
            return template;
        }
    }
}
