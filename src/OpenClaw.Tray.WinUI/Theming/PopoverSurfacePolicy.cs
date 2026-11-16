// <summary>
// Decides how a floating popover with its own acrylic backdrop (the desktop lobster's notification and quick-chat
// bubble) renders the Companion appearance. A Gateway theme paints it opaque like the dashboard's toast (.app-toast in
// ui/src/styles/components.css: --popover fill, --border stroke), as Gateway-themed windows are. The Windows look keeps
// Fluent's acrylic with the Mica setting on and its opaque flyout fill with it off; High Contrast keeps Fluent's resources.
// Pure, so it is tested without WinUI.
// Usage:
//   var surface = PopoverSurfacePolicy.Choose(BackdropPolicy.UseMica, BackdropPolicy.Theme, highContrast,
//       DesktopAcrylicController.IsSupported(), ThemeColorMode.Dark);
//   popup.SystemBackdrop = surface.Acrylic ? acrylic : null;
// </summary>

using OpenClaw.Shared.Themes;

namespace OpenClawTray.Theming;

/// <summary>How a popover renders for the current appearance.</summary>
/// <param name="Acrylic">True keeps the popup's system acrylic backdrop; false removes it.</param>
/// <param name="Fill">The theme fill, or null for the Fluent brush <paramref name="FillResource"/>.</param>
/// <param name="FillResource">Fluent fill resource key used when <paramref name="Fill"/> is null.</param>
/// <param name="Stroke">The theme border, or null for <see cref="PopoverSurfacePolicy.StrokeResource"/>.</param>
internal sealed record PopoverSurface(bool Acrylic, ThemeColor? Fill, string FillResource, ThemeColor? Stroke);

/// <summary>
/// Chooses a popover's backdrop, fill and stroke from the Mica checkbox, the active Gateway theme and High Contrast,
/// through <see cref="ThemeBackdropPolicy.Choose"/> so popovers follow the same rules as Mica windows.
/// <example><code>
/// var surface = PopoverSurfacePolicy.Choose(useMica: true, theme, highContrast: false, acrylicSupported: true, ThemeColorMode.Dark);
/// </code></example>
/// </summary>
internal static class PopoverSurfacePolicy
{
    /// <summary>Fluent's fill for content on acrylic: the Windows look, unchanged.</summary>
    internal const string AcrylicFillResource = "LayerOnAcrylicFillColorDefaultBrush";

    /// <summary>Fluent's opaque flyout fill (acrylic's own fallback color) when the backdrop is off.</summary>
    internal const string OpaqueFillResource = "SolidBackgroundFillColorQuarternaryBrush";

    internal const string StrokeResource = "SurfaceStrokeColorFlyoutBrush";


    public static PopoverSurface Choose(
        bool useMica, ResolvedTheme? theme, bool highContrast, bool acrylicSupported, ThemeColorMode mode)
    {
        var acrylic = acrylicSupported &&
            ThemeBackdropPolicy.Choose(useMica, theme is not null, highContrast) != ThemeBackdrop.Opaque;
        if (theme is null || highContrast)
            return new(acrylic, null, acrylic ? AcrylicFillResource : OpaqueFillResource, null);

        var palette = theme.PaletteFor(mode);
        // A translucent plugin popover token must not let the desktop show through the opaque popover.
        var canvas = palette.Canvas.Over(ThemeColor.Black);
        var popover = palette.Popover.Over(canvas);
        // The bubble's text is the theme's body and muted text; the palette doesn't carry the dashboard's
        // --popover-foreground. A popover that text can't be read on gives way to the card, which the legibility floor
        // checks with that text.
        var fill = TextIsReadableOn(palette, popover) ? popover : palette.Card.Over(canvas);
        return new(false, fill, OpaqueFillResource, palette.Border);
    }

    private static bool TextIsReadableOn(ResolvedThemePalette palette, ThemeColor surface) =>
        new[] { palette.Text, palette.Muted }.All(text =>
            ThemeLegibility.Contrast(text.Over(surface), surface) >= ThemeLegibility.MinimumContrast);
}
