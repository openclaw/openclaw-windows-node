using System.Text.Json;

namespace OpenClaw.TestSupport.Gateway;

/// <summary>
/// Synthetic Gateway theme catalog (<c>themes.list</c> / <c>themes.get</c>, Gateway 2026.9+) for the
/// fixture Gateway: built-ins without definitions, two plugin themes (dark-only and light/dark) and one
/// imported <c>user/*</c> theme (light-only). <see cref="RevealPluginTheme"/> adds a third plugin theme so a
/// following <c>plugins.changed</c> event proves push-driven refresh. The catalog is read-only:
/// <c>themes.set</c> and <c>themes.import</c> are not advertised.
/// <example><code>
/// var scenario = GatewayScenario.CreateThemes();
/// scenario.Themes!.RevealPluginTheme();
/// await server.PublishPluginsChangedAsync();
/// </code></example>
/// </summary>
public sealed class ThemesFixture
{
    public const string ScenarioName = "gateway-themes";
    public const string PluginId = "fixture-themes";
    public const string NeonThemeId = "fixture-themes/neon-night";
    public const string PaperThemeId = "fixture-themes/paper-mint";
    public const string UserThemeId = "user/fixture-sunset";
    public const string RevealedThemeId = "fixture-themes/aurora";

    public static readonly string[] Methods = ["themes.list", "themes.get"];

    public static readonly string[] Events = ["plugins.changed", "users.prefs.changed"];

    private static readonly Theme[] Builtins =
    [
        new("claw", "Claw", "builtin", null, ["light", "dark"], null),
        new("tide", "Tide", "builtin", null, ["light", "dark"], null),
    ];

    private static readonly Theme[] Catalog =
    [
        .. Builtins,
        new(NeonThemeId, "Fixture Neon Night", "plugin", PluginId, ["dark"], new Dictionary<string, object>
        {
            ["name"] = "Fixture Neon Night",
            ["description"] = "Synthetic dark-only plugin theme.",
            ["dark"] = Palette(
                "#05010a", "#f5e9ff", "#120320", "#ff2bd6", "#05010a", "#1d0a33", "#b9a3d1", "#28f0ff", "#05010a",
                "#ff4d6d", "#2c1446", "\"Space Grotesk\", sans-serif", "\"JetBrains Mono\", monospace"),
        }),
        new(PaperThemeId, "Fixture Paper Mint", "plugin", PluginId, ["light", "dark"], new Dictionary<string, object>
        {
            ["name"] = "Fixture Paper Mint",
            ["description"] = "Synthetic light and dark plugin theme.",
            ["light"] = Palette(
                "#f6fbf7", "#12261b", "#ffffff", "#1f8a5b", "#ffffff", "#e3f1e8", "#4d6b5a", "#2fb37a", "#ffffff",
                "#c2410c", "#cfe3d6", "\"Lora\", Georgia, serif", null),
            ["dark"] = Palette(
                "#0d1a14", "#e6f4ec", "#13261d", "#4ade80", "#0d1a14", "#1b3327", "#9cc2ad", "#34d399", "#0d1a14",
                "#f87171", "#24402f", "\"Lora\", Georgia, serif", null),
        }),
        new(UserThemeId, "Fixture Sunset", "user", null, ["light"], new Dictionary<string, object>
        {
            ["name"] = "Fixture Sunset",
            ["description"] = "Synthetic light-only imported theme.",
            ["light"] = Palette(
                "#fff4e8", "#3b1d0f", "#ffffff", "#e4572e", "#ffffff", "#ffe3cc", "#7a4a33", "#f3a712", "#3b1d0f",
                "#b91c1c", "#f2cfb3", "Georgia, serif", null),
        }),
    ];

    private static readonly Theme Revealed = new(RevealedThemeId, "Fixture Aurora", "plugin", PluginId, ["dark"],
        new Dictionary<string, object>
        {
            ["name"] = "Fixture Aurora",
            ["description"] = "Synthetic plugin theme revealed by plugins.changed.",
            ["dark"] = Palette(
                "#07131f", "#e0f2ff", "#0d1f30", "#7c3aed", "#ffffff", "#12304a", "#8fb3cc", "#22d3ee", "#07131f",
                "#fb7185", "#1c3a55", "\"Instrument Sans\", sans-serif", null),
        });

    private int _revealed;
    private string _selectedId = "claw";

    /// <summary>Adds <see cref="RevealedThemeId"/> to later reads. Publish <c>plugins.changed</c> to announce it.</summary>
    public void RevealPluginTheme() => Interlocked.Exchange(ref _revealed, 1);

    /// <summary>
    /// Changes the theme later reads report as selected, as a dashboard theme change would. An id the catalog
    /// doesn't list is reported as a built-in newer than the Companion's table. Publish <c>users.prefs.changed</c> to announce it.
    /// </summary>
    public void SelectTheme(string id) => Volatile.Write(ref _selectedId, id);

    public bool Handles(string method) => Methods.Contains(method, StringComparer.Ordinal);

    public object Respond(string method, JsonElement parameters)
    {
        Theme[] themes = Volatile.Read(ref _revealed) == 1 ? [.. Catalog, Revealed] : Catalog;
        var selectedId = Volatile.Read(ref _selectedId);
        var current = new { id = selectedId, mode = "system", scope = "profile" };
        if (method == "themes.list")
        {
            // Upstream spreads the selected theme's themes.get result into the list.
            var selected = themes.FirstOrDefault(theme => theme.Id == selectedId)
                ?? new Theme(selectedId, selectedId, "builtin", null, ["light", "dark"], null);
            var descriptors = themes.Select(theme => theme.Descriptor).ToArray();
            return selected.Definition is null
                ? new { themes = descriptors, current, theme = selected.Descriptor }
                : new { themes = descriptors, current, theme = selected.Descriptor, definition = selected.Definition };
        }
        if (method != "themes.get")
            throw new FixtureRequestException("METHOD_NOT_FOUND", "Unknown fixture theme method.");

        var id = parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        var theme = themes.FirstOrDefault(theme => theme.Id == id)
            ?? throw new FixtureRequestException("INVALID_PARAMS", "Unknown fixture theme.");
        return theme.Definition is null
            ? new { current, theme = theme.Descriptor }
            : new { current, theme = theme.Descriptor, definition = theme.Definition };
    }

    // Upstream ThemeDefinition palette: 19 shadcn-style colors plus optional font-family lists.
    private static Dictionary<string, string> Palette(
        string background, string foreground, string card, string primary, string primaryForeground,
        string secondary, string mutedForeground, string accent, string accentForeground, string destructive,
        string border, string fontSans, string? fontMono)
    {
        var palette = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["background"] = background,
            ["foreground"] = foreground,
            ["card"] = card,
            ["card-foreground"] = foreground,
            ["popover"] = card,
            ["popover-foreground"] = foreground,
            ["primary"] = primary,
            ["primary-foreground"] = primaryForeground,
            ["secondary"] = secondary,
            ["secondary-foreground"] = foreground,
            ["muted"] = secondary,
            ["muted-foreground"] = mutedForeground,
            ["accent"] = accent,
            ["accent-foreground"] = accentForeground,
            ["destructive"] = destructive,
            ["destructive-foreground"] = primaryForeground,
            ["border"] = border,
            ["input"] = border,
            ["ring"] = accent,
            ["font-sans"] = fontSans,
        };
        if (fontMono is not null)
            palette["font-mono"] = fontMono;
        return palette;
    }

    private sealed record Theme(string Id, string Name, string Source, string? PluginIdValue, string[] Modes,
        Dictionary<string, object>? Definition)
    {
        public object Descriptor => PluginIdValue is null
            ? new { id = Id, name = Name, description = "", source = Source, modes = Modes }
            : new { id = Id, name = Name, description = "", source = Source, pluginId = PluginIdValue, modes = Modes };
    }
}
