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
        try
        {
            ResourceCandidate? candidate = Manager.MainResourceMap.GetValue(
                $"Resources/{resourceKey}",
                Manager.CreateResourceContext());
            string? value = candidate?.ValueAsString;
            return string.IsNullOrEmpty(value) ? resourceKey : value;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SetupLocalization: resource lookup failed for '{resourceKey}': {ex.Message}");
            return resourceKey;
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
            Debug.WriteLine($"SetupLocalization: format failed for '{resourceKey}': template='{template}'");
            return template;
        }
    }
}
