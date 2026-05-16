using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace OpenClawTray.Onboarding.V2;

/// <summary>
/// Resolves the user's Windows app-mode color preference into a concrete
/// <see cref="ElementTheme"/>. Used by both the SetupPreview shell and the
/// V2 bridge so System mode matches what Windows actually advertises
/// (<see cref="Application.RequestedTheme"/> on unpackaged WinUI 3 apps
/// returns Light unless explicitly set, which doesn't reflect the user's
/// dark-mode setting).
/// </summary>
public static class V2SystemTheme
{
    /// <summary>
    /// True when the Windows app-mode color setting is "Dark" (the default
    /// Windows 11 install). Reads <see cref="UISettings.GetColorValue"/>'s
    /// foreground colour: white = dark mode, black = light mode. Falls
    /// back to Dark if the read fails (matches the designer mocks).
    /// </summary>
    public static bool IsDark()
    {
        try
        {
            var fg = new UISettings().GetColorValue(UIColorType.Foreground);
            // Foreground is near-white in dark mode and near-black in light mode.
            return ((fg.R + fg.G + fg.B) / 3) > 128;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Resolves a <see cref="V2ThemeMode"/> preference to a concrete
    /// <see cref="ElementTheme"/>. <see cref="V2ThemeMode.System"/> reads
    /// the Windows app-mode color setting via <see cref="IsDark"/>.
    /// </summary>
    public static ElementTheme Resolve(V2ThemeMode mode) => mode switch
    {
        V2ThemeMode.Light => ElementTheme.Light,
        V2ThemeMode.Dark => ElementTheme.Dark,
        _ => IsDark() ? ElementTheme.Dark : ElementTheme.Light,
    };
}
