using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using OpenClawTray.Services;
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
        catch (Exception ex)
        {
            Logger.Debug($"ThemeHelper: Failed to read Windows dark-mode setting: {ex.Message}");
            return false;
        }
    }

    public static ElementTheme GetCurrentTheme()
    {
        return IsDarkMode() ? ElementTheme.Dark : ElementTheme.Light;
    }

    public static ElementTheme GetRequestedTheme(string? preference) =>
        SettingsManager.NormalizeAppTheme(preference) switch
        {
            SettingsManager.AppThemeLight => ElementTheme.Light,
            SettingsManager.AppThemeDark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

    /// <summary>
    /// Applies the requested theme. <paramref name="refreshThemeResources"/> briefly flips the root theme so
    /// existing {ThemeResource} references re-resolve after application resources were replaced.
    /// </summary>
    public static void ApplyTheme(Window? window, string? preference, bool refreshThemeResources = false)
    {
        if (window?.Content is FrameworkElement rootElement)
        {
            var requested = GetRequestedTheme(preference);
            if (refreshThemeResources)
                rootElement.RequestedTheme = rootElement.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            rootElement.RequestedTheme = requested;
            // Native caption buttons do not inherit the XAML root theme.
            window.AppWindow.TitleBar.PreferredTheme = rootElement.RequestedTheme switch
            {
                ElementTheme.Light => TitleBarTheme.Light,
                ElementTheme.Dark => TitleBarTheme.Dark,
                _ => TitleBarTheme.UseDefaultAppMode
            };
            ApplyWindowFont(rootElement);
        }
    }

    /// <summary>
    /// Window content roots are panels, which cannot carry FontFamily, so unstyled text in a window
    /// otherwise falls back to the system font instead of the theme's UI font. Pins the current
    /// ContentControlThemeFontFamily on the framework host above the content; called again on every
    /// theme change because a value set from code does not track resource replacement.
    /// </summary>
    internal static void ApplyWindowFont(FrameworkElement root)
    {
        if (FindFontHost(root) is not { } host)
        {
            if (!root.IsLoaded)
            {
                RoutedEventHandler? onLoaded = null;
                onLoaded = (_, _) =>
                {
                    root.Loaded -= onLoaded;
                    if (FindFontHost(root) is { } loadedHost)
                        SetThemeFont(loadedHost);
                };
                root.Loaded += onLoaded;
            }
            return;
        }
        SetThemeFont(host);
    }

    private static DependencyObject? FindFontHost(FrameworkElement root)
    {
        if (root is Control or ContentPresenter)
            return root;
        for (var parent = VisualTreeHelper.GetParent(root); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is Control or ContentPresenter)
                return parent;
        }
        return null;
    }

    private static void SetThemeFont(DependencyObject host)
    {
        if (!Application.Current.Resources.TryGetValue("ContentControlThemeFontFamily", out var value) || value is not FontFamily font)
            return;
        if (host is Control control)
            control.FontFamily = font;
        else if (host is ContentPresenter presenter)
            presenter.FontFamily = font;
    }

    public static Color GetAccentColor()
    {
        // Returns the user's Windows accent color. Reads
        // HKCU\Software\Microsoft\Windows\DWM\AccentColor, which is stored as
        // ABGR DWORD, and falls back to the WinUI default blue if missing.
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
        catch (Exception ex)
        {
            Logger.Debug($"ThemeHelper: Failed to read Windows accent color: {ex.Message}");
        }
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
