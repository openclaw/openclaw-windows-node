using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared.Themes;

/// <summary>One catalog entry from <c>themes.list</c> (<c>ThemeDescriptor</c>). Branding fields are not read.</summary>
public sealed record GatewayThemeDescriptor(
    string Id,
    string Name,
    string Description,
    GatewayThemeSource Source,
    ImmutableArray<ThemeColorMode> Modes,
    string? PluginId);

/// <summary>The portable palette of one mode: 19 CSS colors plus optional font-family lists.</summary>
public sealed record GatewayThemeTokens(
    ImmutableSortedDictionary<string, string> Colors,
    string? FontSans,
    string? FontMono)
{
    public ThemeColor Color(string key) => CssColor.Parse(Colors[key]);

    public bool Equals(GatewayThemeTokens? other) =>
        other is not null &&
        FontSans == other.FontSans && FontMono == other.FontMono &&
        Colors.SequenceEqual(other.Colors);

    public override int GetHashCode() => HashCode.Combine(Colors.Count, FontSans, FontMono);
}

/// <summary>A validated plugin or imported theme definition (<c>ThemeDefinition</c>), without branding.</summary>
public sealed record GatewayThemeDefinition(
    string Name,
    string Description,
    GatewayThemeTokens? Light,
    GatewayThemeTokens? Dark);

/// <summary>The parsed <c>themes.list</c> result. <paramref name="Current"/> is the theme the Gateway renders for this connection.</summary>
public sealed record GatewayThemeList(
    ImmutableArray<GatewayThemeDescriptor> Themes,
    GatewayThemeDescriptor? Current,
    GatewayThemeDefinition? CurrentDefinition);

/// <summary>
/// Reads the Gateway <c>themes.list</c> / <c>themes.get</c> wire shapes (operator.read, Gateway 2026.9+)
/// and validates theme definitions against the same limits upstream enforces before storage.
/// Invalid entries are dropped rather than failing the whole catalog.
/// <example><code>
/// var list = GatewayThemeProtocol.ParseList(await client.SendWizardRequestAsync("themes.list", new { }));
/// if (GatewayThemeProtocol.TryParseGet(getResult, out var id, out var definition)) Use(definition);
/// </code></example>
/// </summary>
public static class GatewayThemeProtocol
{
    public const string ListMethod = "themes.list";
    public const string GetMethod = "themes.get";

    public static readonly ImmutableArray<string> ColorKeys =
    [
        "background", "foreground", "card", "card-foreground", "popover", "popover-foreground",
        "primary", "primary-foreground", "secondary", "secondary-foreground", "muted", "muted-foreground",
        "accent", "accent-foreground", "destructive", "destructive-foreground", "border", "input", "ring",
    ];

    /// <summary>Upper bound for a stored definition's compact JSON (upstream caps definitions at 4096 bytes).</summary>
    public const int MaxDefinitionBytes = 8192;
    private const int MaxNameLength = 80;
    private const int MaxDescriptionLength = 320;
    private const int MaxTokenLength = 120;
    private const int MaxIdLength = 256;

    private static readonly Regex s_fontFamilyList = new(
        """^(?:"[a-z0-9 ,'._-]*"|'[a-z0-9 ,"._-]*'|-?[a-z_][a-z0-9_-]*(?: +-?[a-z_][a-z0-9_-]*)*)(?: *, *(?:"[a-z0-9 ,'._-]*"|'[a-z0-9 ,"._-]*'|-?[a-z_][a-z0-9_-]*(?: +-?[a-z_][a-z0-9_-]*)*))*$""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex s_themeId = new(
        "^(?:[a-z0-9][a-z0-9_-]{0,63}|@?[a-z0-9][a-z0-9._-]*(?:/[a-z0-9][a-z0-9._-]*)*/[a-z0-9][a-z0-9_-]{0,63})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsThemeId(string? id) =>
        id is { Length: > 0 and <= MaxIdLength } && s_themeId.IsMatch(id);

    public static GatewayThemeList ParseList(JsonElement result)
    {
        var themes = ImmutableArray.CreateBuilder<GatewayThemeDescriptor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("themes", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                if (TryParseDescriptor(entry, out var descriptor) && seen.Add(descriptor.Id))
                    themes.Add(descriptor);
            }
        }

        GatewayThemeDescriptor? current = null;
        GatewayThemeDefinition? currentDefinition = null;
        if (TryParseGet(result, out var selected, out var definition))
        {
            current = selected;
            currentDefinition = definition;
        }
        return new GatewayThemeList(themes.ToImmutable(), current, currentDefinition);
    }

    /// <summary>
    /// Reads the inspected theme and its definition, when present, from a <c>themes.get</c> result. A <c>themes.list</c>
    /// result has the same <c>theme</c> and <c>definition</c> fields for the theme currently selected.
    /// </summary>
    public static bool TryParseGet(JsonElement result, out GatewayThemeDescriptor descriptor, out GatewayThemeDefinition? definition)
    {
        descriptor = null!;
        definition = null;
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("theme", out var theme) ||
            !TryParseDescriptor(theme, out descriptor))
            return false;
        if (result.TryGetProperty("definition", out var raw) &&
            TryParseDefinition(raw, out var parsed, out _))
        {
            definition = parsed;
        }
        return true;
    }

    public static bool TryParseDescriptor(JsonElement entry, out GatewayThemeDescriptor descriptor)
    {
        descriptor = null!;
        if (entry.ValueKind != JsonValueKind.Object ||
            !TryString(entry, "id", MaxIdLength, out var id) || !IsThemeId(id) ||
            !TryString(entry, "name", MaxNameLength, out var name) ||
            !TryString(entry, "source", 16, out var sourceText))
            return false;
        var source = sourceText switch
        {
            "builtin" => GatewayThemeSource.Builtin,
            "plugin" => GatewayThemeSource.Plugin,
            "user" => GatewayThemeSource.User,
            _ => (GatewayThemeSource?)null,
        };
        if (source is null)
            return false;

        var modes = ImmutableArray.CreateBuilder<ThemeColorMode>();
        if (entry.TryGetProperty("modes", out var modeList) && modeList.ValueKind == JsonValueKind.Array)
        {
            foreach (var mode in modeList.EnumerateArray())
            {
                var parsed = mode.ValueKind == JsonValueKind.String ? mode.GetString() : null;
                if (parsed == "light" && !modes.Contains(ThemeColorMode.Light))
                    modes.Add(ThemeColorMode.Light);
                else if (parsed == "dark" && !modes.Contains(ThemeColorMode.Dark))
                    modes.Add(ThemeColorMode.Dark);
            }
        }
        if (modes.Count == 0)
            return false;

        TryString(entry, "description", MaxDescriptionLength, out var description);
        TryString(entry, "pluginId", MaxIdLength, out var pluginId);
        descriptor = new GatewayThemeDescriptor(
            id, name, description, source.Value, modes.ToImmutable(), pluginId.Length == 0 ? null : pluginId);
        return true;
    }

    public static bool TryParseDefinition(JsonElement raw, out GatewayThemeDefinition definition, out string error)
    {
        definition = null!;
        error = "";
        if (raw.ValueKind != JsonValueKind.Object)
        {
            error = "theme must be an object";
            return false;
        }
        if (raw.GetRawText().Length > MaxDefinitionBytes * 2 ||
            JsonSerializer.SerializeToUtf8Bytes(raw).Length > MaxDefinitionBytes)
        {
            error = $"theme definition exceeds {MaxDefinitionBytes} bytes";
            return false;
        }
        if (!TryString(raw, "name", MaxNameLength, out var name))
        {
            error = "theme.name is required";
            return false;
        }
        TryString(raw, "description", MaxDescriptionLength, out var description);

        GatewayThemeTokens? light = null, dark = null;
        foreach (var (key, mode) in new[] { ("light", ThemeColorMode.Light), ("dark", ThemeColorMode.Dark) })
        {
            if (!raw.TryGetProperty(key, out var palette) || palette.ValueKind == JsonValueKind.Null)
                continue;
            if (!TryParseTokens(palette, key, out var tokens, out error))
                return false;
            if (mode == ThemeColorMode.Light)
                light = tokens;
            else
                dark = tokens;
        }
        if (light is null && dark is null)
        {
            error = "theme must provide at least one light or dark palette";
            return false;
        }
        definition = new GatewayThemeDefinition(name, description, light, dark);
        return true;
    }

    /// <summary>Serializes a definition back to the upstream wire shape, for settings snapshots.</summary>
    public static JsonElement ToJson(GatewayThemeDefinition definition)
    {
        var root = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = definition.Name,
            ["description"] = definition.Description,
        };
        if (definition.Light is { } light)
            root["light"] = TokensToJson(light);
        if (definition.Dark is { } dark)
            root["dark"] = TokensToJson(dark);
        return JsonSerializer.SerializeToElement(root);

        static Dictionary<string, string> TokensToJson(GatewayThemeTokens tokens)
        {
            var palette = new Dictionary<string, string>(tokens.Colors, StringComparer.Ordinal);
            if (tokens.FontSans is { } sans)
                palette["font-sans"] = sans;
            if (tokens.FontMono is { } mono)
                palette["font-mono"] = mono;
            return palette;
        }
    }

    private static bool TryParseTokens(JsonElement palette, string label, out GatewayThemeTokens tokens, out string error)
    {
        tokens = null!;
        error = "";
        if (palette.ValueKind != JsonValueKind.Object)
        {
            error = $"theme.{label} must be an object";
            return false;
        }
        var colors = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var key in ColorKeys)
        {
            if (!TryString(palette, key, MaxTokenLength, out var value) || !CssColor.TryParse(value, out _))
            {
                error = $"theme.{label}.{key} must be a supported CSS color";
                return false;
            }
            colors[key] = value;
        }
        if (!TryFont(palette, "font-sans", out var sans) || !TryFont(palette, "font-mono", out var mono))
        {
            error = $"theme.{label} fonts must contain only font family names";
            return false;
        }
        tokens = new GatewayThemeTokens(colors.ToImmutable(), sans, mono);
        return true;

        static bool TryFont(JsonElement palette, string key, out string? font)
        {
            font = null;
            if (!palette.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
                return true;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(text) || text.Length > MaxTokenLength || !s_fontFamilyList.IsMatch(text))
                return false;
            font = text;
            return true;
        }
    }

    private static bool TryString(JsonElement owner, string property, int maxLength, out string value)
    {
        value = "";
        if (!owner.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        var text = element.GetString()?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > maxLength || text.Any(char.IsControl))
            return false;
        value = text;
        return true;
    }
}
