// <summary>
// Draws the active Gateway theme's background artwork behind the chat's new-chat surface and conversation, as the
// Control UI does (ui/src/components/session-background.tsx). App sets the theme and background choice after each
// appearance change; the chat root re-renders on Changed, which also fires when Windows transparency effects are
// turned on or off. There is no artwork for the Windows default look, under High Contrast, or with transparency
// effects off (the Control UI's forced-colors and reduced-transparency rules). The artwork sits on the opaque theme
// shell, so the Mica setting never changes it.
// Usage:
//   ChatBackgroundArtwork.Set(applicator.Current, AppearanceResolver.ActiveBackground(settings.Appearance)); // App
//   ChatBackgroundArtwork.Render(ThemeBackgroundSurface.Conversation).Grid(row: 0, rowSpan: 6)                // chat
// </summary>

using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenClaw.Shared.Themes;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using Windows.UI.ViewManagement;

namespace OpenClawTray.Chat;

internal static class ChatBackgroundArtwork
{
    // The Control UI's faded mask (session-background.css): eased steps to transparent at 55% of the viewport height.
    private const double FadeEnd = 0.55;
    private static readonly (double Offset, double Opacity)[] s_fadeStops =
        [(0, 0), (0.125, 0.04), (0.25, 0.16), (0.375, 0.32), (0.5, 0.5), (0.625, 0.68), (0.75, 0.84), (0.875, 0.96), (1, 1)];

    private static readonly ConditionalWeakTable<Border, Artwork> s_artwork = new();
    private static ResolvedTheme? s_theme;
    private static BackgroundPreference? s_preference;
    private static UISettings? s_uiSettings;
    private static long s_revision;

    /// <summary>
    /// Raised on the UI thread after <see cref="Set"/>, and on any thread when Windows transparency effects change
    /// while artwork is in use.
    /// </summary>
    internal static event Action? Changed;

    /// <summary>Increments with every change, so a surface can re-render when the artwork changes.</summary>
    internal static long Revision => Interlocked.Read(ref s_revision);

    /// <summary>Whether Windows transparency effects are on. Replaceable for tests.</summary>
    internal static Func<bool> TransparencyEffectsEnabled { get; set; } = ReadTransparencyEffects;

    /// <summary>Records the applied theme (null for Windows default) and the background choice in effect.</summary>
    internal static void Set(ResolvedTheme? theme, BackgroundPreference? preference)
    {
        s_theme = theme;
        s_preference = preference;
        Interlocked.Increment(ref s_revision);
        Changed?.Invoke();
    }

    /// <summary>Re-renders open chats after Windows transparency effects were turned on or off.</summary>
    internal static void OnTransparencyEffectsChanged()
    {
        if (ThemeBackgrounds.ArtworkFamily(s_theme) is null)
            return;
        Interlocked.Increment(ref s_revision);
        Changed?.Invoke();
    }

    /// <summary>The artwork's strength on <paramref name="surface"/> right now; 0 draws nothing.</summary>
    internal static double OpacityFor(ThemeBackgroundSurface surface)
    {
        if (ThemeBackgrounds.ArtworkFamily(s_theme) is null || ChatThemeChanges.IsHighContrast() || !TransparencyEffectsEnabled())
            return 0;
        return ThemeBackgrounds.Opacity(s_preference, surface);
    }

    internal static Element Render(ThemeBackgroundSurface surface)
    {
        var opacity = OpacityFor(surface);
        if (opacity <= 0)
            return Empty();
        var family = ThemeBackgrounds.ArtworkFamily(s_theme)!;
        var theme = s_theme!;
        var fades = ThemeBackgrounds.Fades(s_preference, surface);
        var artwork = new Artwork(family, theme, opacity, fades);
        // No declared child: Paint owns Border.Child, and a declared one would be mounted (as null) after Set runs.
        return Border(null)
            .IsHitTestVisible(false)
            .AccessibilityView(AccessibilityView.Raw)
            .Set(border =>
            {
                // Draft edits and streaming replies re-render the chat, not the artwork.
                if (border.Child is not null && s_artwork.TryGetValue(border, out var previous) && previous == artwork)
                    return;
                s_artwork.AddOrUpdate(border, artwork);
                Paint(border);
            })
            // Pooled borders are reused elsewhere, so the theme watch lives exactly as long as the mount. Before load,
            // ActualTheme reports the app theme rather than this subtree's, so Loaded paints again once it is known.
            .OnMount(control =>
            {
                if (control is not Border border)
                    return;
                border.Loaded += OnLoaded;
                border.ActualThemeChanged += OnActualThemeChanged;
            })
            .OnUnmount(control =>
            {
                if (control is not Border border)
                    return;
                border.Loaded -= OnLoaded;
                border.ActualThemeChanged -= OnActualThemeChanged;
                s_artwork.Remove(border);
                border.Child = null;
            })
            .AutomationId("ChatBackgroundArtwork");
    }

    private static void OnLoaded(object sender, RoutedEventArgs args) => Paint((FrameworkElement)sender);

    private static void OnActualThemeChanged(FrameworkElement sender, object args) => Paint(sender);

    private sealed record Artwork(string Family, ResolvedTheme Theme, double Opacity, bool Fades);

    // The artwork pair and the fade follow the element's own light or dark theme.
    private static void Paint(FrameworkElement element)
    {
        if (element is not Border border || !s_artwork.TryGetValue(border, out var artwork))
            return;
        var dark = border.ActualTheme == ElementTheme.Dark ||
            (border.ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        var mode = dark ? ThemeColorMode.Dark : ThemeColorMode.Light;
        var source = new Uri($"ms-appx:///Assets/ThemeBackgrounds/{artwork.Family}-{(dark ? "dark" : "light")}.png");
        var image = new Border
        {
            Background = new ImageBrush
            {
                ImageSource = new BitmapImage(source),
                Stretch = Stretch.UniformToFill,
                AlignmentY = AlignmentY.Top,
                Opacity = artwork.Opacity,
            },
        };
        if (!artwork.Fades)
        {
            border.Child = image;
            return;
        }
        // Fading the artwork into the canvas equals painting the canvas over it with the inverse of the mask.
        var palette = artwork.Theme.PaletteFor(mode);
        var shell = palette.Shell.Over(palette.WindowCanvas);
        var fade = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        foreach (var (offset, opacity) in s_fadeStops)
            fade.GradientStops.Add(new GradientStop
            {
                Offset = offset * FadeEnd,
                Color = global::Windows.UI.Color.FromArgb((byte)Math.Round(opacity * 255), shell.R, shell.G, shell.B),
            });
        border.Child = new Grid { Children = { image, new Border { Background = fade } } };
    }

    // The UISettings instance is kept, so its change event stays subscribed for the process.
    private static bool ReadTransparencyEffects()
    {
        try
        {
            if (s_uiSettings is null)
            {
                s_uiSettings = new UISettings();
                s_uiSettings.AdvancedEffectsEnabledChanged += (_, _) => OnTransparencyEffectsChanged();
            }
            return s_uiSettings.AdvancedEffectsEnabled;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
