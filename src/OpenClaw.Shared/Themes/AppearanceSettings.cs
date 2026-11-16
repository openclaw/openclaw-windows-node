using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared.Themes;

/// <summary>
/// Persisted Companion appearance. A missing object (legacy settings) means the Windows Fluent look
/// with Mica. The selection is global today; a per-Gateway map can be added beside <see cref="Theme"/>
/// later without migrating it. Reading is field-by-field tolerant: SettingsData.FromJson discards the
/// whole settings file on any JsonException, so a malformed appearance value falls back to its default.
/// </summary>
[JsonConverter(typeof(AppearanceSettingsJsonConverter))]
public sealed record AppearanceSettings
{
    /// <summary>The selected Gateway theme, or null for the Windows default look.</summary>
    public ThemeSelection? Theme { get; init; }

    /// <summary>Use the Mica backdrop on themed windows instead of an opaque theme canvas.</summary>
    public bool UseMica { get; init; } = true;

    /// <summary>Show the connected Gateway's selected dashboard theme instead of <see cref="Theme"/>.</summary>
    public bool FollowGatewayTheme { get; init; }

    /// <summary>
    /// The last Gateway theme the Companion could render while following, kept so an offline start or a
    /// Gateway theme the Companion can't render still shows it. <see cref="Theme"/> stays the manual choice.
    /// </summary>
    public ThemeSelection? FollowedTheme { get; init; }

    public static AppearanceSettings Default { get; } = new();
}

/// <summary>
/// A Companion-owned theme choice. Built-ins resolve from <see cref="BuiltinGatewayThemes"/>; plugin and
/// imported themes keep a validated <see cref="Definition"/> snapshot so they still render when the
/// Gateway that supplied them is offline or no longer lists them.
/// </summary>
public sealed record ThemeSelection
{
    // Not `required`: a malformed entry must not make SettingsData.FromJson discard every setting.
    public string Id { get; init; } = "";
    public string? Name { get; init; }
    /// <summary>Wire <c>source</c> value (builtin, plugin or user); kept as text so unknown future values never fail load.</summary>
    public string Source { get; init; } = "builtin";
    /// <summary>Saved Gateway id that supplied a non-built-in theme; informational only.</summary>
    public string? OriginGatewayId { get; init; }
    /// <summary>Upstream-shaped <c>ThemeDefinition</c> snapshot for non-built-in themes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Definition { get; init; }

    public bool Equals(ThemeSelection? other) =>
        other is not null &&
        Id == other.Id && Name == other.Name && Source == other.Source &&
        OriginGatewayId == other.OriginGatewayId &&
        // Semantic comparison: the settings writer re-indents the snapshot.
        (Definition, other.Definition) switch
        {
            (null, null) => true,
            ({ } left, { } right) => JsonElement.DeepEquals(left, right),
            _ => false,
        };

    public override int GetHashCode() => HashCode.Combine(Id, Source, OriginGatewayId);
}

/// <summary>Reads <see cref="AppearanceSettings"/> without ever throwing on a malformed value.</summary>
internal sealed class AppearanceSettingsJsonConverter : JsonConverter<AppearanceSettings>
{
    public override AppearanceSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return AppearanceSettings.Default;
        var useMica = !root.TryGetProperty(nameof(AppearanceSettings.UseMica), out var mica) ||
            mica.ValueKind != JsonValueKind.False;
        // Only a literal true enables following, so a malformed value keeps the manual choice in effect.
        var follow = root.TryGetProperty(nameof(AppearanceSettings.FollowGatewayTheme), out var followValue) &&
            followValue.ValueKind == JsonValueKind.True;
        var theme = root.TryGetProperty(nameof(AppearanceSettings.Theme), out var value) ? ReadSelection(value) : null;
        var followed = root.TryGetProperty(nameof(AppearanceSettings.FollowedTheme), out var followedValue)
            ? ReadSelection(followedValue)
            : null;
        return new AppearanceSettings { Theme = theme, UseMica = useMica, FollowGatewayTheme = follow, FollowedTheme = followed };
    }

    public override void Write(Utf8JsonWriter writer, AppearanceSettings value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteSelection(writer, nameof(AppearanceSettings.Theme), value.Theme);
        writer.WriteBoolean(nameof(AppearanceSettings.UseMica), value.UseMica);
        writer.WriteBoolean(nameof(AppearanceSettings.FollowGatewayTheme), value.FollowGatewayTheme);
        WriteSelection(writer, nameof(AppearanceSettings.FollowedTheme), value.FollowedTheme);
        writer.WriteEndObject();
    }

    private static void WriteSelection(Utf8JsonWriter writer, string name, ThemeSelection? selection)
    {
        if (selection is null)
            return;
        writer.WriteStartObject(name);
        writer.WriteString(nameof(ThemeSelection.Id), selection.Id);
        WriteOptional(writer, nameof(ThemeSelection.Name), selection.Name);
        writer.WriteString(nameof(ThemeSelection.Source), selection.Source);
        WriteOptional(writer, nameof(ThemeSelection.OriginGatewayId), selection.OriginGatewayId);
        if (selection.Definition is { } definition)
        {
            writer.WritePropertyName(nameof(ThemeSelection.Definition));
            definition.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static ThemeSelection? ReadSelection(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || Text(value, nameof(ThemeSelection.Id)) is not { Length: > 0 } id)
            return null;
        return new ThemeSelection
        {
            Id = id,
            Name = Text(value, nameof(ThemeSelection.Name)),
            Source = Text(value, nameof(ThemeSelection.Source)) ?? "builtin",
            OriginGatewayId = Text(value, nameof(ThemeSelection.OriginGatewayId)),
            Definition = value.TryGetProperty(nameof(ThemeSelection.Definition), out var definition) &&
                definition.ValueKind == JsonValueKind.Object
                    ? definition.Clone()
                    : null,
        };
    }

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
            writer.WriteString(name, value);
    }
}

/// <summary>
/// Resolves persisted appearance into the theme to apply. Unknown built-ins and invalid snapshots
/// fall back to the Windows default rather than failing. While following the Gateway, the last followed
/// theme wins and the manual choice is the fallback.
/// <example><code>
/// var theme = AppearanceResolver.ResolveTheme(settings.Appearance); // null = Windows default
/// var next = AppearanceResolver.Follow(settings.Appearance, gatewayCurrent, definition, gatewayId);
/// </code></example>
/// </summary>
public static class AppearanceResolver
{
    public static ResolvedTheme? ResolveTheme(AppearanceSettings? appearance) =>
        appearance is null ? null : Resolve(ActiveSelection(appearance));

    /// <summary>The selection in effect: the followed Gateway theme when following and renderable, else the manual choice.</summary>
    public static ThemeSelection? ActiveSelection(AppearanceSettings appearance) =>
        appearance.FollowGatewayTheme && Resolve(appearance.FollowedTheme) is not null
            ? appearance.FollowedTheme
            : appearance.Theme;

    /// <summary>
    /// Records the Gateway's selected theme while following. Returns <paramref name="appearance"/> unchanged when
    /// following is off, nothing is known, or the theme can't be rendered (for example a built-in newer than the
    /// Companion's table), so the last followed theme or the manual choice stays in effect.
    /// </summary>
    public static AppearanceSettings Follow(
        AppearanceSettings appearance, GatewayThemeDescriptor? gatewayTheme, GatewayThemeDefinition? definition, string? gatewayId)
    {
        if (!appearance.FollowGatewayTheme || gatewayTheme is null)
            return appearance;
        ThemeSelection selection;
        if (gatewayTheme.Source == GatewayThemeSource.Builtin)
        {
            if (!BuiltinGatewayThemes.TryGet(gatewayTheme.Id, out var builtin))
                return appearance;
            selection = new ThemeSelection { Id = builtin.Id, Name = builtin.Name };
        }
        else
        {
            if (definition is null)
                return appearance;
            selection = Select(gatewayTheme, definition, gatewayId);
            if (Resolve(selection) is null)
                return appearance;
        }
        return selection == appearance.FollowedTheme ? appearance : appearance with { FollowedTheme = selection };
    }

    public static ResolvedTheme? Resolve(ThemeSelection? selection)
    {
        if (selection is null || ParseSource(selection.Source) is not { } source)
            return null;
        if (source == GatewayThemeSource.Builtin)
            return BuiltinGatewayThemes.TryGet(selection.Id, out var builtin) ? builtin : null;
        if (selection.Definition is not { } raw ||
            !GatewayThemeProtocol.IsThemeId(selection.Id) ||
            !GatewayThemeProtocol.TryParseDefinition(raw, out var definition, out _))
            return null;

        var modes = new List<ThemeColorMode>(2);
        if (definition.Light is not null) modes.Add(ThemeColorMode.Light);
        if (definition.Dark is not null) modes.Add(ThemeColorMode.Dark);
        var descriptor = new GatewayThemeDescriptor(
            selection.Id, selection.Name ?? definition.Name, definition.Description, source, [.. modes], null);
        var theme = ThemePaletteExpander.Resolve(descriptor, definition);
        return ThemeLegibility.IsReadable(theme) ? theme : null;
    }

    public static string SourceText(GatewayThemeSource source) => source switch
    {
        GatewayThemeSource.Plugin => "plugin",
        GatewayThemeSource.User => "user",
        _ => "builtin",
    };

    private static GatewayThemeSource? ParseSource(string? source) => source switch
    {
        "builtin" => GatewayThemeSource.Builtin,
        "plugin" => GatewayThemeSource.Plugin,
        "user" => GatewayThemeSource.User,
        _ => null,
    };

    /// <summary>Builds the persisted selection for a catalog entry; non-built-ins need their definition.</summary>
    public static ThemeSelection Select(GatewayThemeDescriptor descriptor, GatewayThemeDefinition? definition, string? gatewayId) =>
        descriptor.Source == GatewayThemeSource.Builtin
            ? new ThemeSelection { Id = descriptor.Id, Name = descriptor.Name }
            : new ThemeSelection
            {
                Id = descriptor.Id,
                Name = descriptor.Name,
                Source = SourceText(descriptor.Source),
                OriginGatewayId = gatewayId,
                Definition = GatewayThemeProtocol.ToJson(
                    definition ?? throw new ArgumentNullException(nameof(definition))),
            };
}
