// <summary>
// Owns how Mica windows and dialogs render the Companion appearance: the user's Mica setting, the active Gateway
// theme's UI font, and Windows High Contrast. Mica belongs to the Windows default look; under a Gateway theme a
// window paints the theme's opaque canvas, as the dashboard does. A window or dialog calls Apply once; App calls
// Configure whenever the appearance, theme or High Contrast state changes, after the theme's resources are
// applied, and every tracked open window follows. The window keeps its own backdrop throughout: the opaque look
// paints the root over it, because swapping backdrops across App's theme refresh faults inside WinUI. Standalone
// dialogs, which App's window refresh doesn't reach, also take the light/dark preference App gives them (the app's own
// mode, or a one-palette theme's pinned mode) and re-resolve their theme resources when the theme changes.
// UI thread only.
// Usage:
//   BackdropPolicy.Configure(settings.Appearance.UseMica, applicator.Current); // App, on every appearance change
//   BackdropPolicy.ConfigureWindowTheme(preference, themeChanged);             // App, right after
//   BackdropPolicy.Apply(this);                                                  // a dialog, after SystemBackdrop is set
//   BackdropPolicy.Apply(window, themedByApp: true);                             // App's own windows
// </summary>

using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared.Themes;
using OpenClawTray.Services;
using Windows.UI.ViewManagement;

namespace OpenClawTray.Theming;

/// <summary>
/// Applies the Mica setting to windows that own a Mica or acrylic backdrop. Mica off, or an active Gateway theme
/// outside High Contrast, paints the opaque canvas (SolidBackgroundFillColorBaseBrush, or the High Contrast window
/// color) over the backdrop; otherwise the window's own Mica or acrylic shows through.
/// </summary>
internal static class BackdropPolicy
{
    private sealed class State
    {
        public Brush? OriginalBackground;
        public ThemeBackdrop Applied = ThemeBackdrop.System;
        public bool WatchingTheme;
        public bool Logged;
        public bool WaitingForContent;
        public bool ThemedByApp;
    }

    private static readonly ConditionalWeakTable<Window, State> s_states = new();
    private static bool s_useMica = true;
    private static ResolvedTheme? s_theme;
    private static string? s_windowTheme;
    private static bool s_refreshResources;

    /// <summary>Records the appearance and re-applies it to every tracked open window.</summary>
    public static void Configure(bool useMica, ResolvedTheme? theme)
    {
        s_useMica = useMica;
        s_theme = theme;
        foreach (var (window, _) in s_states.ToList())
            Apply(window);
    }

    /// <summary>
    /// Records the light/dark preference standalone windows render in and gives it to every tracked one;
    /// <paramref name="refreshResources"/> re-resolves their theme resources after the theme's resources changed.
    /// App's own windows take their preference from App.
    /// </summary>
    public static void ConfigureWindowTheme(string? preference, bool refreshResources)
    {
        s_windowTheme = preference;
        s_refreshResources = refreshResources;
        try
        {
            foreach (var (window, state) in s_states.ToList())
            {
                if (!state.ThemedByApp)
                    Apply(window);
            }
        }
        finally
        {
            s_refreshResources = false;
        }
    }

    /// <summary>
    /// Applies the current appearance to <paramref name="window"/> and keeps it in sync until it closes.
    /// <paramref name="themedByApp"/> marks App's own windows, whose light/dark preference App applies itself.
    /// </summary>
    public static void Apply(Window? window, bool themedByApp = false)
    {
        if (window is null)
            return;
        try
        {
            ApplyCore(window, themedByApp);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Themes] Backdrop update failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    private static void ApplyCore(Window window, bool themedByApp)
    {
        if (!s_states.TryGetValue(window, out var state))
        {
            // Only windows that chose a Mica or acrylic backdrop are managed.
            if (window.SystemBackdrop is not (MicaBackdrop or DesktopAcrylicBackdrop))
                return;
            state = new State();
            s_states.Add(window, state);
            window.Closed += (_, _) => s_states.Remove(window);
        }
        state.ThemedByApp |= themedByApp;
        if (window.Content is not FrameworkElement root)
        {
            // Dialogs may build their content after choosing a backdrop; finish on first activation.
            if (!state.WaitingForContent)
            {
                state.WaitingForContent = true;
                void OnActivated(object sender, WindowActivatedEventArgs args)
                {
                    window.Activated -= OnActivated;
                    state.WaitingForContent = false;
                    Apply(window);
                }
                window.Activated += OnActivated;
            }
            return;
        }

        // Standalone dialogs are not refreshed with the main windows: they take the light/dark preference App gives
        // them, re-resolve styled text after a theme change, and their unstyled text takes the theme's UI font.
        if (!state.ThemedByApp && s_windowTheme is not null)
            Helpers.ThemeHelper.ApplyTheme(window, s_windowTheme, s_refreshResources);
        else
            Helpers.ThemeHelper.ApplyWindowFont(root);
        var highContrast = new AccessibilitySettings().HighContrast;
        var mode = ThemeBackdropPolicy.Choose(s_useMica, s_theme is not null, highContrast);
        if (mode == ThemeBackdrop.Opaque)
        {
            if (state.Applied != ThemeBackdrop.Opaque)
                state.OriginalBackground = GetBackground(root);
            SetBackground(root, CanvasBrush(root));
            if (!state.WatchingTheme)
            {
                state.WatchingTheme = true;
                root.ActualThemeChanged += (sender, _) =>
                {
                    if (state.Applied == ThemeBackdrop.Opaque)
                        SetBackground(sender, CanvasBrush(sender));
                };
            }
        }
        else if (state.Applied == ThemeBackdrop.Opaque)
        {
            SetBackground(root, state.OriginalBackground);
        }
        if (state.Applied != mode || !state.Logged)
        {
            state.Logged = true;
            Logger.Info($"[Themes] Backdrop {window.GetType().Name}: {mode} (Mica setting {(s_useMica ? "on" : "off")}, " +
                $"theme {s_theme?.Id ?? "Windows default"}, High Contrast {highContrast}); " +
                $"system backdrop {window.SystemBackdrop?.GetType().Name ?? "none"}, root {Describe(GetBackground(root))}");
        }
        state.Applied = mode;
    }

    private static string Describe(Brush? brush) => brush switch
    {
        null => "transparent",
        SolidColorBrush solid => $"#{solid.Color.A:X2}{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}",
        _ => brush.GetType().Name,
    };

    private static Brush? CanvasBrush(FrameworkElement root)
    {
        const string key = "SolidBackgroundFillColorBaseBrush";
        var resources = Application.Current.Resources;
        var themeKey = new AccessibilitySettings().HighContrast ? "HighContrast"
            : root.ActualTheme == ElementTheme.Light ? "Light" : "Default";
        if (resources.ThemeDictionaries.TryGetValue(themeKey, out var theme) &&
            theme is ResourceDictionary dictionary && dictionary.TryGetValue(key, out var brush) && brush is Brush themed)
            return themed;
        // Hosts without app-level theme dictionaries still resolve Fluent's brush for the app theme.
        return resources.TryGetValue(key, out var fallback) ? fallback as Brush : null;
    }

    private static Brush? GetBackground(FrameworkElement root) => root switch
    {
        Panel panel => panel.Background,
        Control control => control.Background,
        Border border => border.Background,
        _ => null,
    };

    private static void SetBackground(FrameworkElement root, Brush? brush)
    {
        switch (root)
        {
            case Panel panel:
                panel.Background = brush;
                break;
            case Control control:
                control.Background = brush;
                break;
            case Border border:
                border.Background = brush;
                break;
        }
    }
}
