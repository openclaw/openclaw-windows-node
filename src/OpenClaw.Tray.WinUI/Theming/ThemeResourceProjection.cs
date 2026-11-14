using OpenClaw.Shared.Themes;

namespace OpenClawTray.Theming;

/// <summary>The WinUI resource values one theme mode projects to.</summary>
/// <param name="Brushes">SolidColorBrush resource keys and their colors (Fluent, App and chat keys).</param>
/// <param name="Colors">Color resource keys overridden at the application root (the accent ramp).</param>
/// <param name="BodyFontSource">XAML FontFamily source for UI text.</param>
/// <param name="ChatFontSource">XAML FontFamily source for chat prose.</param>
/// <param name="MonoFontSource">XAML FontFamily source for chat code.</param>
internal sealed record ThemeProjection(
    IReadOnlyDictionary<string, ThemeColor> Brushes,
    IReadOnlyDictionary<string, ThemeColor> Colors,
    string BodyFontSource,
    string ChatFontSource,
    string MonoFontSource);

/// <summary>
/// Maps a resolved Gateway theme palette onto the WinUI resource keys the Companion renders with.
/// Translucent Fluent control fills are deliberately left alone: they derive from the text color and
/// stay legible on any themed surface. High Contrast dictionaries are never part of a projection.
/// <example><code>
/// var projection = ThemeResourceProjection.Project(theme.PaletteFor(ThemeColorMode.Dark));
/// </code></example>
/// </summary>
internal static class ThemeResourceProjection
{
    public static ThemeProjection Project(ResolvedThemePalette p)
    {
        var brushes = new Dictionary<string, ThemeColor>(StringComparer.Ordinal)
        {
            ["SolidBackgroundFillColorBaseBrush"] = p.Canvas,
            ["SolidBackgroundFillColorBaseAltBrush"] = p.Content,
            ["SolidBackgroundFillColorSecondaryBrush"] = p.Surface,
            ["SolidBackgroundFillColorTertiaryBrush"] = p.Content,
            ["SolidBackgroundFillColorQuarternaryBrush"] = p.Card,
            // Layers stay partly translucent so a Mica backdrop still reads through them.
            ["LayerFillColorDefaultBrush"] = p.Surface.WithAlpha(0.5),
            ["LayerFillColorAltBrush"] = p.Surface,
            ["CardBackgroundFillColorDefaultBrush"] = p.Card,
            ["CardBackgroundFillColorSecondaryBrush"] = p.Surface,

            ["TextFillColorPrimaryBrush"] = p.Text,
            ["TextFillColorSecondaryBrush"] = p.Muted,
            ["TextFillColorTertiaryBrush"] = p.Muted.WithAlpha(0.8),
            ["TextFillColorDisabledBrush"] = p.Muted.WithAlpha(0.5),

            ["AccentFillColorDefaultBrush"] = p.Primary,
            ["AccentFillColorSecondaryBrush"] = p.Primary.WithAlpha(0.9),
            ["AccentFillColorTertiaryBrush"] = p.Primary.WithAlpha(0.8),
            ["AccentTextFillColorPrimaryBrush"] = p.Accent,
            ["AccentTextFillColorSecondaryBrush"] = p.Accent,
            ["AccentTextFillColorTertiaryBrush"] = p.AccentHover,
            ["TextOnAccentFillColorPrimaryBrush"] = p.PrimaryForeground,
            ["TextOnAccentFillColorSecondaryBrush"] = p.PrimaryForeground.WithAlpha(0.7),

            ["CardStrokeColorDefaultBrush"] = p.Border,
            ["ControlStrokeColorDefaultBrush"] = p.Border,
            ["ControlStrokeColorSecondaryBrush"] = p.BorderStrong,
            ["DividerStrokeColorDefaultBrush"] = p.Border,
            ["SurfaceStrokeColorDefaultBrush"] = p.BorderStrong,
            ["FocusStrokeColorOuterBrush"] = p.Ring,

            ["SystemFillColorSuccessBrush"] = p.Ok,
            ["SystemFillColorCautionBrush"] = p.Warn,
            ["SystemFillColorCriticalBrush"] = p.Danger,
            ["SystemFillColorAttentionBrush"] = p.Info,

            ["ChatComposerBrush"] = p.ComposerSurface,
            ["ChatCardBrush"] = p.Card,
            ["ChatStrokeBrush"] = p.Border,
            ["ChatTextBrush"] = p.Text,
            ["ChatStrongTextBrush"] = p.TextStrong,
            ["ChatSecondaryTextBrush"] = p.Muted,
            ["ChatCommandOutputBrush"] = p.Text,
            ["ChatToolActionHoverForegroundBrush"] = p.TextStrong,
            ["ChatProseTextBrush"] = p.Text,
            ["ChatMutedTextBrush"] = p.Muted.WithAlpha(0.8),
            ["ChatSyntaxCommentBrush"] = p.Muted,
            ["ChatSandboxOnBrush"] = p.Accent,
            ["ChatClawBrush"] = p.Accent,
            // These two keep their XAML Opacity (a 12% tint); only the color changes.
            ["ChatQuestionSelectedBrush"] = p.Accent,
            ["ChatUserBrush"] = p.Accent,
            ["ChatQuestionSelectedStrokeBrush"] = p.Accent,
            ["ChatQuestionCardStrokeBrush"] = p.Border,
            ["ChatUserTextBrush"] = p.Text,
            ["ChatCopySuccessBrush"] = p.Ok,
        };

        // Fluent shades its accent ramp around SystemAccentColor; follow the theme's primary instead.
        // AppThemeApplicator also moves app brushes bound to a ramp shade onto the matching theme shade.
        var colors = new Dictionary<string, ThemeColor>(StringComparer.Ordinal)
        {
            ["SystemAccentColor"] = p.Primary,
            ["SystemAccentColorLight1"] = p.Primary.Mix(ThemeColor.White, 0.85),
            ["SystemAccentColorLight2"] = p.Primary.Mix(ThemeColor.White, 0.7),
            ["SystemAccentColorLight3"] = p.Primary.Mix(ThemeColor.White, 0.55),
            ["SystemAccentColorDark1"] = p.Primary.Mix(ThemeColor.Black, 0.85),
            ["SystemAccentColorDark2"] = p.Primary.Mix(ThemeColor.Black, 0.7),
            ["SystemAccentColorDark3"] = p.Primary.Mix(ThemeColor.Black, 0.55),
            // Color twin of AccentFillColorDefaultBrush, read by code that tints its own brushes (PageHero).
            ["AccentFillColorDefault"] = p.Primary,
        };

        return new ThemeProjection(
            brushes,
            colors,
            ThemeFontMapper.ToXamlSource(p.BodyFont, ThemeFontMapper.Role.Ui),
            ThemeFontMapper.ToXamlSource(p.ChatFont, ThemeFontMapper.Role.Ui),
            ThemeFontMapper.ToXamlSource(p.MonoFont, ThemeFontMapper.Role.Code));
    }
}

/// <summary>How a Mica window's backdrop renders for the current appearance.</summary>
internal enum ThemeBackdrop
{
    /// <summary>The window's own system Mica, untinted.</summary>
    System,
    /// <summary>Mica tinted with the active theme's canvas color.</summary>
    Tinted,
    /// <summary>No Mica: the window shows the opaque theme (or Fluent) canvas.</summary>
    Opaque,
}

/// <summary>Mica controller tint for one theme mode.</summary>
internal readonly record struct MicaTint(ThemeColor Color, float TintOpacity, float LuminosityOpacity);

/// <summary>
/// Decides the backdrop for the Mica checkbox, the active Gateway theme and Windows High Contrast, and
/// derives the theme's Mica tint. High Contrast always gets the plain system behavior.
/// <example><code>
/// var mode = ThemeBackdropPolicy.Choose(useMica, theme is not null, highContrast);
/// var tint = ThemeBackdropPolicy.TintFor(theme.PaletteFor(ThemeColorMode.Dark), ThemeColorMode.Dark);
/// </code></example>
/// </summary>
internal static class ThemeBackdropPolicy
{
    // Without Mica support (Windows 10) a tint cannot render, so the theme canvas is painted opaque instead.
    public static ThemeBackdrop Choose(bool useMica, bool themeActive, bool highContrast, bool micaSupported = true) =>
        !useMica ? ThemeBackdrop.Opaque
        : themeActive && !highContrast ? (micaSupported ? ThemeBackdrop.Tinted : ThemeBackdrop.Opaque)
        : ThemeBackdrop.System;

    // Fluent's Base Mica opacities (dark 0.8, light 0.5) over the theme canvas instead of its neutral grey.
    public static MicaTint TintFor(ResolvedThemePalette palette, ThemeColorMode mode) =>
        new(palette.Canvas with { A = 255 }, mode == ThemeColorMode.Dark ? 0.8f : 0.5f, 1f);
}

/// <summary>
/// Converts a CSS font-family stack into a XAML FontFamily source. Families the Companion bundles become
/// <c>ms-appx</c> font references, families Windows does not ship are dropped, and CSS generics map to
/// Windows fonts, so every stack ends in an installed face.
/// <example><code>
/// ThemeFontMapper.ToXamlSource(ThemeFontStack.Parse("\"Lora\", Georgia, serif"), ThemeFontMapper.Role.Ui)
/// // "ms-appx:///Assets/Fonts/Lora.ttf#Lora, Georgia, Segoe UI Variable Text"
/// </code></example>
/// </summary>
internal static class ThemeFontMapper
{
    internal enum Role
    {
        Ui,
        Code,
    }

    /// <summary>Bundled OFL families and their asset file names (scripts/Update-ThemeFonts.ps1).</summary>
    internal static readonly IReadOnlyDictionary<string, string> BundledFamilies =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Instrument Sans"] = "InstrumentSans",
            ["Geist"] = "Geist",
            ["DM Sans"] = "DMSans",
            ["IBM Plex Sans"] = "IBMPlexSans",
            ["Space Grotesk"] = "SpaceGrotesk",
            ["Atkinson Hyperlegible Next"] = "AtkinsonHyperlegibleNext",
            ["Fraunces"] = "Fraunces",
            ["Lora"] = "Lora",
            ["JetBrains Mono"] = "JetBrainsMono",
        };

    internal const string UiFallback = "Segoe UI Variable Text";
    private static readonly string[] s_codeFallback = ["Cascadia Mono", "Consolas"];

    private static readonly HashSet<string> s_unavailableOnWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        "-apple-system", "BlinkMacSystemFont", "Roboto", "SFMono-Regular", "SF Mono", "Menlo", "Monaco",
        "Helvetica", "Helvetica Neue", "ui-monospace", "ui-sans-serif", "ui-serif", "ui-rounded",
        "-webkit-body", "emoji", "math", "fangsong", "cursive", "fantasy",
    };

    private static readonly Dictionary<string, string[]> s_generics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sans-serif"] = [UiFallback],
        ["system-ui"] = [UiFallback],
        ["serif"] = ["Georgia"],
        ["monospace"] = s_codeFallback,
    };

    public static string ToXamlSource(ThemeFontStack stack, Role role)
    {
        var families = new List<string>();
        foreach (var family in stack.Families)
        {
            // XAML separates families with commas, so a quoted CSS name containing one cannot be expressed.
            string[] mapped = BundledFamilies.TryGetValue(family, out var file) ? [$"ms-appx:///Assets/Fonts/{file}.ttf#{family}"]
                : s_generics.TryGetValue(family, out var generic) ? generic
                : s_unavailableOnWindows.Contains(family) || family.Contains(',') ? []
                : [family];
            Add(families, mapped);
        }
        Add(families, role == Role.Code ? s_codeFallback : [UiFallback]);
        return string.Join(", ", families);

        static void Add(List<string> families, IEnumerable<string> candidates)
        {
            foreach (var candidate in candidates)
            {
                if (!families.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    families.Add(candidate);
            }
        }
    }
}
