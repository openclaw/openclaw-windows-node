using Microsoft.UI.Xaml;

namespace OpenClawTray.Infrastructure.Core;

/// <summary>
/// Represents the effective color scheme at a position in the element tree.
/// Used by <see cref="RenderContext.UseColorScheme"/> to let components
/// adapt their rendering to the current theme variant.
/// </summary>
public enum ColorScheme
{
    /// <summary>The standard light theme.</summary>
    Light,

    /// <summary>The standard dark theme.</summary>
    Dark,

    /// <summary>Windows High Contrast mode is active.</summary>
    HighContrast,
}

/// <summary>
/// Tracks the effective color scheme and provides mapping from WinUI
/// <see cref="ElementTheme"/> values to <see cref="ColorScheme"/>.
/// </summary>
internal class ColorSchemeContext
{
    public ColorScheme CurrentScheme { get; private set; } = ColorScheme.Light;

    /// <summary>
    /// Updates the current scheme based on an <see cref="ElementTheme"/> value.
    /// <see cref="ElementTheme.Dark"/> → <see cref="ColorScheme.Dark"/>,
    /// <see cref="ElementTheme.Light"/> → <see cref="ColorScheme.Light"/>,
    /// <see cref="ElementTheme.Default"/> → checks High Contrast, then falls back to <see cref="ColorScheme.Light"/>.
    /// </summary>
    public void Update(ElementTheme actualTheme)
    {
        CurrentScheme = actualTheme switch
        {
            ElementTheme.Dark => ColorScheme.Dark,
            ElementTheme.Light => ColorScheme.Light,
            _ => DetectHighContrast() ? ColorScheme.HighContrast : ColorScheme.Light,
        };
    }

    /// <summary>
    /// Maps an <see cref="ElementTheme"/> to <see cref="ColorScheme"/> with
    /// High Contrast detection for the Default case.
    /// </summary>
    internal static ColorScheme FromActualTheme(ElementTheme actualTheme)
    {
        return actualTheme switch
        {
            ElementTheme.Dark => ColorScheme.Dark,
            ElementTheme.Light => ColorScheme.Light,
            _ => DetectHighContrast() ? ColorScheme.HighContrast : ColorScheme.Light,
        };
    }

    private static bool DetectHighContrast()
    {
        try
        {
            var settings = new global::Windows.UI.ViewManagement.AccessibilitySettings();
            return settings.HighContrast;
        }
        catch
        {
            return false;
        }
    }
}
