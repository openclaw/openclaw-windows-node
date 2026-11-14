namespace OpenClaw.Shared.Themes;

/// <summary>
/// Expands a plugin or imported theme's 19 portable tokens into a <see cref="ResolvedThemePalette"/>,
/// porting the Control UI's derivations (<c>normalizeThemePalette</c> in upstream
/// <c>ui/src/app/theme-palette.ts</c>). Status colors keep the base stylesheet defaults, as they do
/// for imported themes in the Control UI.
/// <example><code>
/// var palette = ThemePaletteExpander.Expand(definition.Dark!, ThemeColorMode.Dark);
/// </code></example>
/// </summary>
public static class ThemePaletteExpander
{
    /// <summary>Control UI default for an imported theme without <c>font-sans</c>.</summary>
    public const string DefaultBodyFont = "\"Inter\", -apple-system, BlinkMacSystemFont, \"Segoe UI\", Roboto, sans-serif";

    /// <summary>Control UI default for an imported theme without <c>font-mono</c>.</summary>
    public const string DefaultMonoFont = "\"JetBrains Mono\", ui-monospace, SFMono-Regular, \"SF Mono\", Menlo, Monaco, Consolas, monospace";

    public static ResolvedTheme Resolve(GatewayThemeDescriptor descriptor, GatewayThemeDefinition definition) =>
        new(descriptor.Id, descriptor.Name, descriptor.Description, descriptor.Source,
            definition.Light is { } light ? Expand(light, ThemeColorMode.Light) : null,
            definition.Dark is { } dark ? Expand(dark, ThemeColorMode.Dark) : null)
        {
            Branding = descriptor.Branding,
        };

    public static ResolvedThemePalette Expand(GatewayThemeTokens tokens, ThemeColorMode mode)
    {
        var isLight = mode == ThemeColorMode.Light;
        var contrastTarget = isLight ? ThemeColor.Black : ThemeColor.White;
        var background = tokens.Color("background");
        var foreground = tokens.Color("foreground");
        var card = tokens.Color("card");
        var mutedForeground = tokens.Color("muted-foreground");
        var border = tokens.Color("border");
        var accent = tokens.Color("accent");
        var destructive = tokens.Color("destructive");
        var body = ThemeFontStack.Parse(tokens.FontSans ?? DefaultBodyFont);

        return new ResolvedThemePalette
        {
            Canvas = background,
            Content = background.Mix(card, 0.92),
            Surface = background.Mix(card, 0.88),
            Card = card,
            Popover = tokens.Color("popover"),
            // Upstream mixes the muted text token here; kept as-is so the palette matches the Control UI.
            Hover = mutedForeground.Mix(background, 0.68),
            Text = foreground,
            TextStrong = foreground,
            Muted = mutedForeground,
            Border = border,
            BorderStrong = border.Mix(foreground, 0.72),
            Accent = accent,
            AccentHover = accent.Mix(contrastTarget, 0.82),
            AccentForeground = tokens.Color("accent-foreground"),
            Primary = tokens.Color("primary"),
            PrimaryForeground = tokens.Color("primary-foreground"),
            Ring = tokens.Color("ring"),
            Destructive = destructive,
            Danger = destructive,
            Ok = ThemeColor.FromHex(isLight ? "#166534" : "#22c55e"),
            Warn = ThemeColor.FromHex(isLight ? "#92400e" : "#f59e0b"),
            Info = ThemeColor.FromHex(isLight ? "#1d4ed8" : "#60a5fa"),
            // Imported themes get base.css's :root composer surface and the inline-code fallback (--bg-muted).
            ComposerSurface = tokens.Color("popover"),
            InlineCode = tokens.Color("muted"),
            BodyFont = body,
            ChatFont = body,
            MonoFont = ThemeFontStack.Parse(tokens.FontMono ?? DefaultMonoFont),
        };
    }
}
