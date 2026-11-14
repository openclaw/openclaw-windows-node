using System.Collections.Immutable;

namespace OpenClaw.Shared.Themes;

/// <summary>
/// Companion-owned palettes for the Gateway's built-in themes. <c>themes.list</c> returns built-ins as
/// descriptors only; their colors live in the Control UI's stylesheets, so the generated half of this
/// class (<c>BuiltinGatewayThemes.g.cs</c>) captures them from a pinned upstream ref. Regenerate with
/// <c>scripts/Update-GatewayThemeTable.ps1 -UpstreamRef &lt;sha&gt;</c>; the daily
/// <c>gateway-theme-sync.yml</c> workflow does this when the latest stable packaged Gateway ships a new openclaw release.
/// <example><code>
/// if (BuiltinGatewayThemes.TryGet("tide", out var tide)) Apply(tide.PaletteFor(ThemeColorMode.Dark));
/// </code></example>
/// </summary>
public static partial class BuiltinGatewayThemes
{
    private static readonly Lazy<ImmutableArray<ResolvedTheme>> s_themes = new(() =>
        [.. Data.Select(theme => new ResolvedTheme(
            theme.Id, theme.Name, theme.Description, GatewayThemeSource.Builtin,
                ToPalette(theme.Light), ToPalette(theme.Dark))
            {
                Branding = ThemeBranding.Resolve(theme.Mascot, theme.BrandIcon, theme.WorkingIndicator),
            })]);

    /// <summary>The built-in themes in upstream catalog order.</summary>
    public static ImmutableArray<ResolvedTheme> All => s_themes.Value;

    public static bool TryGet(string? id, out ResolvedTheme theme)
    {
        theme = All.FirstOrDefault(entry => entry.Id == id)!;
        return theme is not null;
    }

    public static bool IsBuiltin(string? id) => All.Any(entry => entry.Id == id);

    private static ResolvedThemePalette ToPalette(BuiltinPalette palette) => new()
    {
        Canvas = ThemeColor.FromHex(palette.Canvas),
        Content = ThemeColor.FromHex(palette.Content),
        Surface = ThemeColor.FromHex(palette.Surface),
        Card = ThemeColor.FromHex(palette.Card),
        Popover = ThemeColor.FromHex(palette.Popover),
        Hover = ThemeColor.FromHex(palette.Hover),
        Text = ThemeColor.FromHex(palette.Text),
        TextStrong = ThemeColor.FromHex(palette.TextStrong),
        Muted = ThemeColor.FromHex(palette.Muted),
        Border = ThemeColor.FromHex(palette.Border),
        BorderStrong = ThemeColor.FromHex(palette.BorderStrong),
        Accent = ThemeColor.FromHex(palette.Accent),
        AccentHover = ThemeColor.FromHex(palette.AccentHover),
        AccentForeground = ThemeColor.FromHex(palette.AccentForeground),
        Primary = ThemeColor.FromHex(palette.Primary),
        PrimaryForeground = ThemeColor.FromHex(palette.PrimaryForeground),
        Ring = ThemeColor.FromHex(palette.Ring),
        Destructive = ThemeColor.FromHex(palette.Destructive),
        Danger = ThemeColor.FromHex(palette.Danger),
        Ok = ThemeColor.FromHex(palette.Ok),
        Warn = ThemeColor.FromHex(palette.Warn),
        Info = ThemeColor.FromHex(palette.Info),
        ComposerSurface = ThemeColor.FromHex(palette.ComposerSurface),
        InlineCode = ThemeColor.FromHex(palette.InlineCode),
        BodyFont = ThemeFontStack.Parse(palette.Body),
        ChatFont = ThemeFontStack.Parse(palette.Chat),
        MonoFont = ThemeFontStack.Parse(palette.Mono),
    };

    // Branding is resolved by the generator with upstream's resolveThemeBranding defaults.
    private sealed record BuiltinTheme(
        string Id,
        string Name,
        string Description,
        string Mascot,
        string BrandIcon,
        string WorkingIndicator,
        BuiltinPalette Light,
        BuiltinPalette Dark);

    private sealed record BuiltinPalette(
        string Canvas,
        string Content,
        string Surface,
        string Card,
        string Popover,
        string Hover,
        string Text,
        string TextStrong,
        string Muted,
        string Border,
        string BorderStrong,
        string Accent,
        string AccentHover,
        string AccentForeground,
        string Primary,
        string PrimaryForeground,
        string Ring,
        string Destructive,
        string Danger,
        string Ok,
        string Warn,
        string Info,
        string ComposerSurface,
        string InlineCode,
        string Body,
        string Chat,
        string Mono);
}
