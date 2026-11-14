using System.Collections.Immutable;
using System.Text;

namespace OpenClaw.Shared.Themes;

/// <summary>Light or dark palette of a theme.</summary>
public enum ThemeColorMode
{
    Light,
    Dark,
}

/// <summary>Where a Gateway theme comes from (<c>ThemeDescriptor.source</c>).</summary>
public enum GatewayThemeSource
{
    Builtin,
    Plugin,
    User,
}

/// <summary>
/// Ordered CSS font-family list with quotes removed, for example
/// <c>"IBM Plex Sans", -apple-system, sans-serif</c> becomes <c>[IBM Plex Sans, -apple-system, sans-serif]</c>.
/// Generic families keep their CSS keyword (<c>sans-serif</c>, <c>monospace</c>, <c>system-ui</c>, ...).
/// </summary>
public sealed record ThemeFontStack(ImmutableArray<string> Families)
{
    public string Primary => Families.IsDefaultOrEmpty ? "" : Families[0];

    public static ThemeFontStack Parse(string? css)
    {
        var families = ImmutableArray.CreateBuilder<string>();
        if (!string.IsNullOrWhiteSpace(css))
        {
            var current = new StringBuilder();
            char quote = '\0';
            foreach (var c in css)
            {
                if (quote != '\0')
                {
                    if (c == quote)
                        quote = '\0';
                    else
                        current.Append(c);
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c == ',')
                {
                    Add(current, families);
                }
                else
                {
                    current.Append(c);
                }
            }
            Add(current, families);
        }
        return new ThemeFontStack(families.ToImmutable());

        static void Add(StringBuilder current, ImmutableArray<string>.Builder families)
        {
            var name = string.Join(' ', current.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (name.Length > 0)
                families.Add(name);
            current.Clear();
        }
    }

    public bool Equals(ThemeFontStack? other) =>
        other is not null && Families.SequenceEqual(other.Families, StringComparer.Ordinal);

    public override int GetHashCode() =>
        Families.Aggregate(0, (hash, family) => HashCode.Combine(hash, StringComparer.Ordinal.GetHashCode(family)));

    public override string ToString() => string.Join(", ", Families);
}

/// <summary>
/// The resolved colors and fonts of one theme mode, in the vocabulary the Companion projects onto WinUI.
/// Names follow the Control UI CSS variables they come from.
/// </summary>
public sealed record ResolvedThemePalette
{
    /// <summary>Page canvas (<c>--bg</c>).</summary>
    public required ThemeColor Canvas { get; init; }
    /// <summary>Deepest canvas anchor (<c>--bg-content</c>).</summary>
    public required ThemeColor Content { get; init; }
    /// <summary>Secondary panels and sidebars (<c>--bg-accent</c>).</summary>
    public required ThemeColor Surface { get; init; }
    /// <summary>Raised cards (<c>--card</c>).</summary>
    public required ThemeColor Card { get; init; }
    public required ThemeColor Popover { get; init; }
    /// <summary>Hovered rows (<c>--bg-hover</c>).</summary>
    public required ThemeColor Hover { get; init; }
    /// <summary>Body text (<c>--text</c>).</summary>
    public required ThemeColor Text { get; init; }
    /// <summary>Headings and emphasis (<c>--text-strong</c>).</summary>
    public required ThemeColor TextStrong { get; init; }
    /// <summary>Secondary text (<c>--muted</c>).</summary>
    public required ThemeColor Muted { get; init; }
    public required ThemeColor Border { get; init; }
    public required ThemeColor BorderStrong { get; init; }
    /// <summary>Accent used for text, links and indicators (<c>--accent</c>).</summary>
    public required ThemeColor Accent { get; init; }
    public required ThemeColor AccentHover { get; init; }
    public required ThemeColor AccentForeground { get; init; }
    /// <summary>Filled primary buttons (<c>--primary</c>).</summary>
    public required ThemeColor Primary { get; init; }
    public required ThemeColor PrimaryForeground { get; init; }
    public required ThemeColor Ring { get; init; }
    public required ThemeColor Destructive { get; init; }
    public required ThemeColor Danger { get; init; }
    public required ThemeColor Ok { get; init; }
    public required ThemeColor Warn { get; init; }
    public required ThemeColor Info { get; init; }
    /// <summary>Chat composer surface (<c>--chat-composer-surface</c>).</summary>
    public required ThemeColor ComposerSurface { get; init; }
    /// <summary>Inline code chip (<c>--inline-code-bg</c>).</summary>
    public required ThemeColor InlineCode { get; init; }
    /// <summary>UI text (<c>--font-body</c>).</summary>
    public required ThemeFontStack BodyFont { get; init; }
    /// <summary>Chat prose (<c>--font-chat</c>).</summary>
    public required ThemeFontStack ChatFont { get; init; }
    /// <summary>Code (<c>--mono</c>).</summary>
    public required ThemeFontStack MonoFont { get; init; }
}

/// <summary>A theme the Companion can apply: its identity plus a resolved palette for each mode it provides.</summary>
public sealed record ResolvedTheme(
    string Id,
    string Name,
    string Description,
    GatewayThemeSource Source,
    ResolvedThemePalette? Light,
    ResolvedThemePalette? Dark)
{
    public IReadOnlyList<ThemeColorMode> Modes =>
        [.. (Light is null ? Array.Empty<ThemeColorMode>() : [ThemeColorMode.Light]),
         .. (Dark is null ? Array.Empty<ThemeColorMode>() : [ThemeColorMode.Dark])];

    /// <summary>The single mode a one-palette theme is pinned to, or null when both are provided.</summary>
    public ThemeColorMode? PinnedMode =>
        Light is null ? ThemeColorMode.Dark : Dark is null ? ThemeColorMode.Light : null;

    /// <summary>Palette for <paramref name="mode"/>, or the theme's only palette.</summary>
    public ResolvedThemePalette PaletteFor(ThemeColorMode mode) =>
        (mode == ThemeColorMode.Light ? Light ?? Dark : Dark ?? Light)
        ?? throw new InvalidOperationException($"Theme {Id} has no palette.");
}

/// <summary>
/// A legibility floor for themes the Companion did not ship: body and secondary text on the canvas and cards,
/// body text on the chat composer, and text on accent buttons, must reach 3:1 contrast (WCAG's minimum for large
/// text and UI components).
/// Approval dialogs show their details in secondary text, so a plugin or imported theme that fails, for example
/// with text the color of its background, is not applied and cannot hide them. Translucent colors are
/// composited onto their surface first.
/// <example><code>
/// if (!ThemeLegibility.IsReadable(theme)) return null; // keep the previous look
/// </code></example>
/// </summary>
public static class ThemeLegibility
{
    public const double MinimumContrast = 3.0;

    public static bool IsReadable(ResolvedTheme theme) =>
        new[] { theme.Light, theme.Dark }.All(palette => palette is null || IsReadable(palette));

    private static bool IsReadable(ResolvedThemePalette palette)
    {
        var canvas = palette.Canvas.Over(ThemeColor.Black);
        var card = palette.Card.Over(canvas);
        var composer = palette.ComposerSurface.Over(canvas);
        var primary = palette.Primary.Over(canvas);
        return Readable(palette.Text, canvas) && Readable(palette.Text, card) && Readable(palette.Text, composer) &&
            Readable(palette.Muted, canvas) && Readable(palette.Muted, card) &&
            Readable(palette.PrimaryForeground, primary);

        static bool Readable(ThemeColor text, ThemeColor surface) =>
            Contrast(text.Over(surface), surface) >= MinimumContrast;
    }

    public static double Contrast(ThemeColor first, ThemeColor second)
    {
        var (lighter, darker) = first.RelativeLuminance >= second.RelativeLuminance
            ? (first.RelativeLuminance, second.RelativeLuminance)
            : (second.RelativeLuminance, first.RelativeLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }
}
