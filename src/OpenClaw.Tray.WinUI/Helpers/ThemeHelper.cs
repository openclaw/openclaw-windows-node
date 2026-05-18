using Microsoft.UI.Xaml;
using Microsoft.Win32;
using Windows.UI;

namespace OpenClawTray.Helpers;

/// <summary>
/// Helpers for detecting and applying Windows theme (dark/light mode).
/// </summary>
public static class ThemeHelper
{
    public static bool IsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }

    public static ElementTheme GetCurrentTheme()
    {
        return IsDarkMode() ? ElementTheme.Dark : ElementTheme.Light;
    }

    public static Color GetAccentColor()
    {
        // Returns the user's Windows accent color (previously hard-coded to
        // lobster red). Reads HKCU\Software\Microsoft\Windows\DWM\AccentColor
        // which is stored as ABGR DWORD, falls back to the WinUI default
        // blue if the registry key is missing.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            var value = key?.GetValue("AccentColor");
            if (value is int abgr)
            {
                byte r = (byte)(abgr & 0xFF);
                byte g = (byte)((abgr >> 8) & 0xFF);
                byte b = (byte)((abgr >> 16) & 0xFF);
                return Color.FromArgb(255, r, g, b);
            }
        }
        catch { }
        return Color.FromArgb(255, 0, 120, 212); // #0078D4 — WinUI default accent
    }

    public static Color GetBackgroundColor()
    {
        return IsDarkMode() 
            ? Color.FromArgb(255, 32, 32, 32) 
            : Color.FromArgb(255, 249, 249, 249);
    }

    public static Color GetForegroundColor()
    {
        return IsDarkMode()
            ? Color.FromArgb(255, 255, 255, 255)
            : Color.FromArgb(255, 28, 28, 28);
    }

    public static Color GetSubtleTextColor()
    {
        return IsDarkMode()
            ? Color.FromArgb(255, 180, 180, 180)
            : Color.FromArgb(255, 100, 100, 100);
    }
}
