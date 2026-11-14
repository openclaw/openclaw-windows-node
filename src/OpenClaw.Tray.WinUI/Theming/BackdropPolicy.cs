// <summary>
// Owns how Mica windows and dialogs render the Companion appearance: the user's Mica checkbox, the active
// Gateway theme's Mica tint and UI font, and Windows High Contrast. A window or dialog calls Apply once (its own
// Mica or acrylic backdrop is recorded as the original); App calls Configure whenever the appearance, theme or
// High Contrast state changes, after the theme's resources are applied, and every tracked open window follows.
// UI thread only.
// Usage:
//   BackdropPolicy.Configure(settings.Appearance.UseMica, applicator.Current); // App, on every appearance change
//   BackdropPolicy.Apply(this);                                                  // a dialog, after SystemBackdrop is set
// </summary>

using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared.Themes;
using OpenClawTray.Services;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace OpenClawTray.Theming;

/// <summary>
/// Applies the Mica checkbox and theme tint to windows that own a Mica or acrylic backdrop. Mica off shows the
/// opaque theme canvas (SolidBackgroundFillColorBaseBrush, or the High Contrast window color); Mica on with a
/// Gateway theme tints Mica with the theme canvas; otherwise, and always under High Contrast, the window shows
/// system Mica or acrylic: its original backdrop, or the untinted themed backdrop once it has adopted one.
/// </summary>
internal static class BackdropPolicy
{
    private sealed class State(SystemBackdrop original)
    {
        public SystemBackdrop Original { get; } = original;
        public Brush? OriginalBackground;
        public ThemedMicaBackdrop? Tinted;
        public ThemeBackdrop Applied = ThemeBackdrop.System;
        public bool WatchingTheme;
        public bool WaitingForContent;
    }

    private static readonly ConditionalWeakTable<Window, State> s_states = new();
    private static bool s_useMica = true;
    private static ResolvedTheme? s_theme;

    /// <summary>Records the appearance and re-applies it to every tracked open window.</summary>
    public static void Configure(bool useMica, ResolvedTheme? theme)
    {
        s_useMica = useMica;
        s_theme = theme;
        foreach (var (window, _) in s_states.ToList())
            Apply(window);
    }

    /// <summary>Applies the current appearance to <paramref name="window"/> and keeps it in sync until it closes.</summary>
    public static void Apply(Window? window)
    {
        if (window is null)
            return;
        try
        {
            ApplyCore(window);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Themes] Backdrop update failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    private static void ApplyCore(Window window)
    {
        if (!s_states.TryGetValue(window, out var state))
        {
            // Only windows that chose a Mica or acrylic backdrop are managed.
            if (window.SystemBackdrop is not (MicaBackdrop or DesktopAcrylicBackdrop))
                return;
            state = new State(window.SystemBackdrop);
            s_states.Add(window, state);
            window.Closed += (_, _) => s_states.Remove(window);
        }
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

        // Standalone dialogs are not refreshed with the main windows, so their unstyled text takes the theme's UI
        // font here.
        Helpers.ThemeHelper.ApplyWindowFont(root);
        var mode = ThemeBackdropPolicy.Choose(s_useMica, s_theme is not null, new AccessibilitySettings().HighContrast,
            MicaController.IsSupported());
        if (mode == ThemeBackdrop.Tinted && state.Original is not MicaBackdrop)
            mode = ThemeBackdrop.System;

        if (mode == ThemeBackdrop.Opaque)
        {
            if (state.Applied != ThemeBackdrop.Opaque)
                state.OriginalBackground = GetBackground(root);
            SetBackdrop(window, null);
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
        else
        {
            if (state.Applied == ThemeBackdrop.Opaque)
                SetBackground(root, state.OriginalBackground);
            // Once a Mica window adopts the themed backdrop it keeps it: the Windows look is the same backdrop with
            // its tint reset to system Mica. Swapping backdrops back and forth across App's theme refresh made WinUI
            // raise ArgumentException("target") from a framework callback on every later restore.
            if (mode == ThemeBackdrop.Tinted || state.Tinted is not null)
            {
                state.Tinted ??= new ThemedMicaBackdrop(((MicaBackdrop)state.Original).Kind);
                state.Tinted.SetTheme(mode == ThemeBackdrop.Tinted ? s_theme : null);
                SetBackdrop(window, state.Tinted);
            }
            else
            {
                SetBackdrop(window, state.Original);
            }
        }
        state.Applied = mode;
    }

    private static void SetBackdrop(Window window, SystemBackdrop? backdrop)
    {
        if (!ReferenceEquals(window.SystemBackdrop, backdrop))
            window.SystemBackdrop = backdrop;
    }

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

/// <summary>
/// System Mica tinted with a Gateway theme's canvas color. It follows the window's light or dark theme through
/// the framework's default backdrop configuration. With no theme, and always under High Contrast, it renders plain
/// system Mica, so a window can keep this backdrop when the theme is cleared.
/// </summary>
internal sealed class ThemedMicaBackdrop(MicaKind kind) : SystemBackdrop
{
    private MicaController? _controller;
    private SystemBackdropConfiguration? _configuration;
    private MicaTint _light;
    private MicaTint _dark;

    /// <summary>True while a Gateway theme tints the backdrop; false renders plain system Mica.</summary>
    internal bool IsTinted { get; private set; }

    /// <summary>Tints the backdrop with <paramref name="theme"/>'s canvas, or restores plain system Mica for null.</summary>
    public void SetTheme(ResolvedTheme? theme)
    {
        IsTinted = theme is not null;
        if (theme is not null)
        {
            _light = ThemeBackdropPolicy.TintFor(theme.PaletteFor(ThemeColorMode.Light), ThemeColorMode.Light);
            _dark = ThemeBackdropPolicy.TintFor(theme.PaletteFor(ThemeColorMode.Dark), ThemeColorMode.Dark);
        }
        UpdateTint();
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (_controller is not null || !MicaController.IsSupported())
            return;
        _configuration = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
        _controller = new MicaController { Kind = kind };
        UpdateTint();
        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(_configuration);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        if (_controller is null)
            return;
        _controller.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller.Dispose();
        _controller = null;
        _configuration = null;
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        if (target is not null)
            base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
        UpdateTint();
    }

    private void UpdateTint()
    {
        if (_controller is null || _configuration is null)
            return;
        if (!IsTinted || _configuration.IsHighContrast)
        {
            _controller.ResetProperties();
            return;
        }
        var light = _configuration.Theme switch
        {
            SystemBackdropTheme.Light => true,
            SystemBackdropTheme.Dark => false,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Light,
        };
        var tint = light ? _light : _dark;
        var color = Color.FromArgb(tint.Color.A, tint.Color.R, tint.Color.G, tint.Color.B);
        _controller.TintColor = color;
        _controller.FallbackColor = color;
        _controller.TintOpacity = tint.TintOpacity;
        _controller.LuminosityOpacity = tint.LuminosityOpacity;
    }
}
