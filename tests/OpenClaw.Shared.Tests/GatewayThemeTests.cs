using System.Text.Json;
using OpenClaw.Shared.Themes;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class GatewayThemeTests
{
    // Reference conversions from CSS Color 4 (sRGB primaries expressed in other spaces).
    [Theory]
    [InlineData("#f0a", "#ff00aa")]
    [InlineData("#11223344", "#11223344")]
    [InlineData("rgb(255, 0, 0)", "#ff0000")]
    [InlineData("rgb(100% 50% 0%)", "#ff8000")]
    [InlineData("rgba(0, 0, 255, 0.4)", "#0000ff66")]
    [InlineData("hsl(120 100% 25%)", "#008000")]
    [InlineData("hsla(240, 100%, 50%, 0.4)", "#0000ff66")]
    [InlineData("hsl(0.5turn 100% 50%)", "#00ffff")]
    [InlineData("oklch(0.62796 0.25768 29.2339)", "#ff0000")]
    [InlineData("oklch(62.796% 64.42% 29.2339deg)", "#ff0000")]
    [InlineData("oklab(1 0 0)", "#ffffff")]
    [InlineData("lab(54.29 80.82 69.89)", "#ff0000")]
    [InlineData("lch(54.29 106.84 40.85)", "#ff0000")]
    [InlineData("color(srgb 0 1 0 / 0.4)", "#00ff0066")]
    [InlineData("color(srgb-linear 0.2140 0.2140 0.2140)", "#808080")]
    [InlineData("color(display-p3 1 0 0)", "#ff0000")]
    [InlineData("color(xyz-d65 0.4124 0.2126 0.0193)", "#ff0000")]
    [InlineData("BLACK", "#000000")]
    [InlineData("transparent", "#00000000")]
    public void CssColorConvertsToSrgb(string css, string expectedHex)
    {
        Assert.True(CssColor.TryParse(css, out var actual), css);
        var expected = ThemeColor.FromHex(expectedHex);
        Assert.InRange(actual.A, expected.A - 1, expected.A + 1);
        Assert.InRange(actual.R, expected.R - 1, expected.R + 1);
        Assert.InRange(actual.G, expected.G - 1, expected.G + 1);
        Assert.InRange(actual.B, expected.B - 1, expected.B + 1);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("currentcolor")]
    [InlineData("var(--accent)")]
    [InlineData("url(https://example.invalid/x.png)")]
    [InlineData("rgb(1 2)")]
    [InlineData("rgb(1, 2 3)")]
    [InlineData("hsl(10, 20, 30)")]
    [InlineData("rgb(10%, 20, 30)")]
    [InlineData("color(rec2100 1 0 0)")]
    [InlineData("#12345")]
    [InlineData("")]
    public void CssColorRejectsValuesOutsideTheGatewayGrammar(string css) =>
        Assert.False(CssColor.TryParse(css, out _));

    [Fact]
    public void FontStackKeepsQuotedCommasAndGenerics()
    {
        var stack = ThemeFontStack.Parse("\"IBM Plex Sans\", 'Weird, Name' ,  -apple-system,sans-serif");

        Assert.Equal(new[] { "IBM Plex Sans", "Weird, Name", "-apple-system", "sans-serif" }, stack.Families);
        Assert.Equal("IBM Plex Sans", stack.Primary);
    }

    [Fact]
    public void ListParsesLiveCatalogShapeAndDropsInvalidEntries()
    {
        using var json = JsonDocument.Parse("""
        {
          "current": { "id": "claw", "mode": "system", "scope": "gateway", "overrides": {} },
          "theme": { "id": "claw", "name": "Claw", "description": "d", "source": "builtin", "modes": ["light", "dark"] },
          "themes": [
            { "id": "claw", "name": "Claw", "description": "d", "source": "builtin", "modes": ["light", "dark"], "mascot": "claw" },
            { "id": "acme/neon", "name": "Neon", "description": "", "source": "plugin", "pluginId": "acme", "modes": ["dark"] },
            { "id": "user/mine", "name": "Mine", "description": "", "source": "user", "modes": ["light"] },
            { "id": "claw", "name": "Duplicate", "source": "builtin", "modes": ["dark"] },
            { "id": "../escape", "name": "Bad id", "source": "builtin", "modes": ["dark"] },
            { "id": "acme/nomodes", "name": "No modes", "source": "plugin", "modes": [] },
            { "id": "acme/unknown", "name": "Unknown source", "source": "remote", "modes": ["dark"] }
          ]
        }
        """);

        var list = GatewayThemeProtocol.ParseList(json.RootElement);

        Assert.Equal(new[] { "claw", "acme/neon", "user/mine" }, list.Themes.Select(theme => theme.Id));
        Assert.Equal(GatewayThemeSource.Plugin, list.Themes[1].Source);
        Assert.Equal("acme", list.Themes[1].PluginId);
        Assert.Equal(new[] { ThemeColorMode.Dark }, list.Themes[1].Modes);
        Assert.Equal("claw", list.Current?.Id);
        Assert.Equal(GatewayThemeSource.Builtin, list.Current!.Source);
        Assert.Null(list.CurrentDefinition);
    }

    [Fact]
    public void DefinitionAcceptsSingleModeIgnoresBrandingAndRoundTrips()
    {
        var raw = Definition(light: null, dark: Palette("#000000", font: "\"Fira Code\", monospace"), extra: "\"mascot\": \"none\",");

        Assert.True(GatewayThemeProtocol.TryParseDefinition(raw, out var definition, out var error), error);
        Assert.Null(definition.Light);
        Assert.Equal("\"Fira Code\", monospace", definition.Dark!.FontSans);

        var snapshot = GatewayThemeProtocol.ToJson(definition);
        Assert.True(GatewayThemeProtocol.TryParseDefinition(snapshot, out var reparsed, out _));
        Assert.Equal(definition, reparsed);
    }

    [Theory]
    [InlineData("missing-color")]
    [InlineData("bad-color")]
    [InlineData("bad-font")]
    [InlineData("no-palette")]
    [InlineData("too-large")]
    public void DefinitionRejectsInvalidThemes(string defect)
    {
        var palette = Palette("#101010");
        JsonElement raw = defect switch
        {
            "missing-color" => Definition(light: null, dark: palette.Replace("\"ring\": \"#101010\",", "")),
            "bad-color" => Definition(light: null, dark: palette.Replace("\"ring\": \"#101010\"", "\"ring\": \"var(--x)\"")),
            "bad-font" => Definition(light: null, dark: Palette("#101010", font: "inherit; color: red")),
            "no-palette" => Definition(light: null, dark: null),
            _ => Definition(light: null, dark: palette, extra: $"\"description\": \"{new string('x', 9000)}\","),
        };

        Assert.False(GatewayThemeProtocol.TryParseDefinition(raw, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ExpanderFollowsControlUiDerivations()
    {
        var raw = Definition(
            light: Palette("#ffffff", card: "#000000", accent: "#ff0000"),
            dark: Palette("#000000", card: "#ffffff", accent: "#ff0000"));
        Assert.True(GatewayThemeProtocol.TryParseDefinition(raw, out var definition, out _));

        var dark = ThemePaletteExpander.Expand(definition.Dark!, ThemeColorMode.Dark);
        var light = ThemePaletteExpander.Expand(definition.Light!, ThemeColorMode.Light);

        // color-mix(in srgb, var(--bg) 92%, var(--card) 8%) and 88%/12%.
        Assert.Equal("#141414", dark.Content.ToHex());
        Assert.Equal("#1f1f1f", dark.Surface.ToHex());
        // accent-hover mixes 18% white in dark mode and 18% black in light mode.
        Assert.Equal("#ff2e2e", dark.AccentHover.ToHex());
        Assert.Equal("#d10000", light.AccentHover.ToHex());
        Assert.Equal(ThemeColor.FromHex("#22c55e"), dark.Ok);
        Assert.Equal(ThemeColor.FromHex("#166534"), light.Ok);
        Assert.Equal("Inter", dark.BodyFont.Primary);
        Assert.Equal(dark.BodyFont, dark.ChatFont);
        Assert.Equal("JetBrains Mono", dark.MonoFont.Primary);
    }

    [Fact]
    public void ResolvedSingleModeThemeIsPinnedToItsMode()
    {
        var raw = Definition(light: Palette("#ffffff"), dark: null);
        Assert.True(GatewayThemeProtocol.TryParseDefinition(raw, out var definition, out _));
        var descriptor = new GatewayThemeDescriptor("user/paper", "Paper", "", GatewayThemeSource.User, [ThemeColorMode.Light], null);

        var theme = ThemePaletteExpander.Resolve(descriptor, definition);

        Assert.Equal(ThemeColorMode.Light, theme.PinnedMode);
        Assert.Same(theme.Light, theme.PaletteFor(ThemeColorMode.Dark));
    }

    [Fact]
    public void BuiltinTableHasEveryUpstreamThemeInBothModes()
    {
        string[] upstreamIds = ["claw", "knot", "dash", "absolutely", "tide", "beacon", "phosphor", "crt", "manuscript", "rose", "miami"];

        Assert.Equal(upstreamIds, BuiltinGatewayThemes.All.Select(theme => theme.Id));
        Assert.Matches("^[0-9a-f]{40}$", BuiltinGatewayThemes.UpstreamRef);
        foreach (var theme in BuiltinGatewayThemes.All)
        {
            Assert.Null(theme.PinnedMode);
            foreach (var palette in new[] { theme.Light!, theme.Dark! })
            {
                Assert.NotEqual(palette.Canvas, palette.Text);
                Assert.NotEmpty(palette.BodyFont.Primary);
                Assert.NotEmpty(palette.ChatFont.Primary);
                Assert.Equal("JetBrains Mono", palette.MonoFont.Primary);
            }
            Assert.True(theme.Dark!.Canvas.RelativeLuminance < theme.Light!.Canvas.RelativeLuminance, theme.Id);
        }
    }

    [Theory]
    [InlineData("""{ "Theme": { "Id": "", "Source": "remote", "Definition": 42 } }""")]
    [InlineData("""{ "Theme": { "Id": 7, "Name": ["x"] } }""")]
    [InlineData("""{ "Theme": [ "tide" ] }""")]
    [InlineData("""[ 1, 2 ]""")]
    public void MalformedAppearanceNeitherDiscardsSettingsNorAppliesATheme(string appearance)
    {
        var settings = SettingsData.FromJson($$"""{ "AppTheme": "Dark", "Appearance": {{appearance}} }""");

        Assert.NotNull(settings);
        Assert.Equal("Dark", settings.AppTheme);
        Assert.Null(AppearanceResolver.ResolveTheme(settings.Appearance));
        Assert.Null(AppearanceResolver.ResolveTheme(SettingsData.FromJson("""{ "AppTheme": "Light" }""")!.Appearance));
    }

    [Fact]
    public void SelectedGatewayThemeSurvivesSettingsRoundTrip()
    {
        Assert.True(GatewayThemeProtocol.TryParseDefinition(Definition(light: null, dark: Palette("#000000")), out var definition, out _));
        var descriptor = new GatewayThemeDescriptor("acme/ink", "Ink", "", GatewayThemeSource.Plugin, [ThemeColorMode.Dark], "acme");
        var data = new SettingsData
        {
            Appearance = new AppearanceSettings { Theme = AppearanceResolver.Select(descriptor, definition, "gw-1") },
        };

        var reloaded = SettingsData.FromJson(data.ToJson())!;

        Assert.Equal(data.Appearance, reloaded.Appearance);
        var theme = AppearanceResolver.ResolveTheme(reloaded.Appearance);
        Assert.Equal("acme/ink", theme?.Id);
        Assert.Equal(ThemeColorMode.Dark, theme?.PinnedMode);
    }

    private static GatewayThemeDescriptor Descriptor(string id, GatewayThemeSource source) =>
        new(id, id, "", source, [ThemeColorMode.Light, ThemeColorMode.Dark], source == GatewayThemeSource.Plugin ? "acme" : null);

    [Theory]
    [InlineData("\"foreground\": \"#808080\"", "\"foreground\": \"#000000\"")]   // text the color of the background
    [InlineData("\"foreground\": \"#808080\"", "\"foreground\": \"#00000000\"")] // transparent text
    [InlineData("\"muted-foreground\": \"#909090\"", "\"muted-foreground\": \"#000000\"")] // approval details vanish
    [InlineData("\"popover\": \"#000000\"", "\"popover\": \"#808080\"")]          // composer text on its own color
    [InlineData("\"popover\": \"#000000\"", "\"popover\": \"#ffffff\"")]          // readable only on an opaque composer
    public void GatewayThemeWithUnreadableTextIsNeverApplied(string token, string unreadable)
    {
        var palette = Palette("#000000").Replace(token, unreadable);
        Assert.NotEqual(Palette("#000000"), palette);
        Assert.True(GatewayThemeProtocol.TryParseDefinition(Definition(light: null, dark: palette), out var definition, out _));
        var descriptor = Descriptor("acme/ghost", GatewayThemeSource.Plugin);
        var manual = new AppearanceSettings { Theme = AppearanceResolver.Select(descriptor, definition, "gw-1") };

        Assert.Null(AppearanceResolver.ResolveTheme(manual));
        Assert.All(BuiltinGatewayThemes.All, theme => Assert.True(ThemeLegibility.IsReadable(theme), theme.Id));
    }

    // Settings cards sit on the page shell, not the bare canvas: a translucent card that reaches 3:1 over the canvas
    // can fall below it over the shell.
    [Fact]
    public void ATranslucentCardIsJudgedOnThePageShellItSitsOn()
    {
        var palette = Palette("#000000")
            .Replace("\"card\": \"#000000\"", "\"card\": \"#ffffff4d\"")
            .Replace("\"#808080\"", "\"#9b9b9b\"")
            .Replace("\"muted-foreground\": \"#909090\"", "\"muted-foreground\": \"#9b9b9b\"");
        Assert.True(GatewayThemeProtocol.TryParseDefinition(Definition(light: null, dark: palette), out var definition, out _));
        var theme = ThemePaletteExpander.Resolve(Descriptor("acme/frost", GatewayThemeSource.Plugin), definition);
        var dark = theme.Dark!;
        var canvas = dark.Canvas.Over(ThemeColor.Black);
        Assert.True(ThemeLegibility.Contrast(dark.Text, dark.Card.Over(canvas)) >= ThemeLegibility.MinimumContrast);
        Assert.True(ThemeLegibility.Contrast(dark.Text, dark.Card.Over(dark.Shell.Over(canvas))) < ThemeLegibility.MinimumContrast);

        Assert.False(ThemeLegibility.IsReadable(theme));
    }
    // Manuscript dark tokens from Gateway 2026.11.1 (Control UI 4ffdd204), pinned here so a table refresh
    // can't move them. Expected values are pixels sampled from that Control UI in Edge.
    [Fact]
    public void DerivedSurfacesMatchControlUiPixels()
    {
        Assert.True(BuiltinGatewayThemes.TryGet("manuscript", out var theme));
        var dark = theme.Dark! with
        {
            Canvas = ThemeColor.FromHex("#211e18"),
            Content = ThemeColor.FromHex("#211e18"),
            Surface = ThemeColor.FromHex("#262218"),
            Popover = ThemeColor.FromHex("#2f2b22"),
            ComposerSurface = ThemeColor.FromHex("#2f2b22"),
            Elevated = ThemeColor.FromHex("#2a271f"),
            Panel = ThemeColor.FromHex("#211e18"),
        };

        AssertNear("#211e18", dark.Shell);
        AssertNear("#211e18", dark.Sidebar);
        AssertNear("#2c2920", dark.ComposerFill.Over(dark.Shell));
    }

    private static void AssertNear(string expectedHex, ThemeColor actual)
    {
        var expected = ThemeColor.FromHex(expectedHex);
        Assert.True(
            Math.Abs(expected.R - actual.R) <= 2 && Math.Abs(expected.G - actual.G) <= 2 && Math.Abs(expected.B - actual.B) <= 2,
            $"expected {expectedHex}, got {actual}");
    }

    private static string Palette(string background, string? card = null, string? accent = null, string? font = null)
    {
        var fontEntry = font is null ? "" : $", \"font-sans\": {JsonSerializer.Serialize(font)}";
        return $$"""
        {
          "background": "{{background}}", "foreground": "#808080", "card": "{{card ?? background}}",
          "card-foreground": "#808080", "popover": "{{background}}", "popover-foreground": "#808080",
          "primary": "#336699", "primary-foreground": "#ffffff", "secondary": "#202020",
          "secondary-foreground": "#ffffff", "muted": "#303030", "muted-foreground": "#909090",
          "accent": "{{accent ?? "#336699"}}", "accent-foreground": "#ffffff", "destructive": "#cc0000",
          "destructive-foreground": "#ffffff", "border": "#404040", "input": "#404040",
          "ring": "#101010",
          "unknown-future-token": "#123456"{{fontEntry}}
        }
        """;
    }

    private static JsonElement Definition(string? light, string? dark, string extra = "")
    {
        var palettes = string.Join(", ", new[] { light is null ? null : $"\"light\": {light}", dark is null ? null : $"\"dark\": {dark}" }
            .Where(entry => entry is not null));
        var separator = palettes.Length == 0 ? "" : ", ";
        return JsonDocument.Parse($"{{ {extra} \"name\": \"Test\"{separator}{palettes} }}").RootElement.Clone();
    }
}
