// Carries agent-authored Adaptive Card templates to Windows without a parallel layout vocabulary.
// Usage: new WidgetPresentation(card, content, initialState).Validate(); Windows expands the template.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared.Widgets;

/// <summary>A native card template, separate display data and initial per-instance interaction state.</summary>
public sealed partial record WidgetPresentation(
    [property: JsonPropertyName("card")] JsonElement Card,
    [property: JsonPropertyName("data")] JsonElement? Data = null,
    [property: JsonPropertyName("initialState")] JsonElement? InitialState = null,
    [property: JsonPropertyName("assets")] JsonElement? Assets = null)
{
    public const int MaxBytes = 65_536;
    public const int MaxNodes = 256;
    public const int MaxDepth = 24;
    public const int MaxStateBytes = 4_096;
    public const string ReservedPrefix = "openclaw-";
    public static readonly string[] Verbs =
        ["widget.set", "widget.increment", "widget.toggle", "widget.reset", "widget.refresh", WidgetLightingAction.Verb];

    [GeneratedRegex(@"^\$\{\$root\.content(?:\.[A-Za-z_][A-Za-z0-9_]*)+\}$", RegexOptions.CultureInvariant)]
    private static partial Regex ContentBinding();

    /// <summary>
    /// Enforces the publication boundary, not a second Adaptive Cards renderer/schema.
    /// All native element properties pass through; actual host support still needs native proof.
    /// </summary>
    public void Validate()
    {
        if (JsonSerializer.SerializeToUtf8Bytes(this with { Assets = null }).Length > MaxBytes)
            throw new ArgumentException("Widget presentation exceeds 65536 bytes.");
        var artwork = WidgetArtwork.Read(Assets);
        if (Card.ValueKind != JsonValueKind.Object ||
            !Card.TryGetProperty("type", out var type) || type.GetString() != "AdaptiveCard" ||
            !Card.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String ||
            !Version.TryParse(version.GetString(), out var parsed) || parsed.Major != 1 ||
            parsed.Minor is < 0 or > 6 || parsed.Build != -1 ||
            !Card.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Widget card requires type AdaptiveCard, version 1.0-1.6 and a body array.");
        if (Data is { ValueKind: not JsonValueKind.Object })
            throw new ArgumentException("Widget presentation data must be an object, available as content.");
        if (Card.TryGetProperty("$data", out _) || Card.TryGetProperty("$when", out _))
            throw new ArgumentException("Place $data and $when on body elements, not the provider-owned root.");
        ValidateState(InitialState);
        if (Card.TryGetProperty("actions", out var actions) && actions.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Widget card actions must be an array.");
        if (Card.TryGetProperty("refresh", out var refresh) &&
            (refresh.ValueKind != JsonValueKind.Object ||
             !refresh.TryGetProperty("action", out var refreshAction) ||
             refreshAction.ValueKind != JsonValueKind.Object ||
             !refreshAction.TryGetProperty("verb", out var refreshVerb) ||
             refreshVerb.GetString() != "widget.refresh"))
            throw new ArgumentException("Automatic card refresh may only use widget.refresh, never a state mutation.");
        var nodes = 0;
        var expandedArtworkBytes = 0;
        Visit(Card, 0, template: true);
        if (Data is { } data) Visit(data, 0, template: false);
        if (Assets is { } assets) Visit(assets, 0, template: false);

        void Visit(JsonElement value, int depth, bool template)
        {
            if (depth > MaxDepth)
                throw new ArgumentException("Widget JSON exceeds depth 24.");
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in value.EnumerateArray()) Visit(child, depth + 1, template);
                return;
            }
            if (value.ValueKind != JsonValueKind.Object) return;
            if (++nodes > MaxNodes)
                throw new ArgumentException("Widget presentation exceeds 256 objects.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw new ArgumentException($"Duplicate widget property: {property.Name}.");
                if (template)
                {
                    if (property.Name == "$when" &&
                        (property.Value.ValueKind != JsonValueKind.String ||
                         !property.Value.GetString()!.StartsWith("${", StringComparison.Ordinal) ||
                         !property.Value.GetString()!.EndsWith('}')))
                        throw new ArgumentException("Widget $when requires an Adaptive Card template expression.");
                    if (property.Name is "authentication" or "webUrl")
                        throw new ArgumentException("Widget authentication and HTML web content are not enabled.");
                    if (property.Name == "id" && property.Value.ValueKind == JsonValueKind.String &&
                        (property.Value.GetString()!.StartsWith(ReservedPrefix, StringComparison.Ordinal) ||
                         property.Value.GetString()!.Contains("${", StringComparison.Ordinal)))
                        throw new ArgumentException("Widget ids must be literal and must not begin openclaw-.");
                    if (property.Name is "url" or "iconUrl" or "poster" ||
                        (property.Name == "backgroundImage" && property.Value.ValueKind == JsonValueKind.String))
                        ValidateResource(property.Value,
                            property.Name == "url" && value.TryGetProperty("type", out var resourceType) &&
                            resourceType.ValueKind == JsonValueKind.String && resourceType.GetString() == "Action.OpenUrl");
                    if (property.Name == "type" && property.Value.ValueKind == JsonValueKind.String &&
                        property.Value.GetString() is { } actionType && actionType.StartsWith("Action.", StringComparison.Ordinal))
                        ValidateAction(value, actionType);
                    if (property.Name == "targetElements")
                    {
                        if (property.Value.ValueKind != JsonValueKind.Array)
                            throw new ArgumentException("Widget targetElements must be an array.");
                        foreach (var target in property.Value.EnumerateArray())
                        {
                            var id = target.ValueKind == JsonValueKind.String ? target.GetString() :
                                target.GetProperty("elementId").GetString();
                            if (id is null || id.StartsWith(ReservedPrefix, StringComparison.Ordinal) || id.Contains("${", StringComparison.Ordinal))
                                throw new ArgumentException("ToggleVisibility can target only literal, non-provider element ids.");
                        }
                    }
                }
                // Action data and input choices are ordinary JSON, not more card definitions.
                Visit(property.Value, depth + 1, template && property.Name is not ("data" or "choices" or "facts"));
            }
        }

        void ValidateResource(JsonElement value, bool navigation)
        {
            if (value.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Widget resource URL must be a string.");
            var url = value.GetString()!;
            if (ContentBinding().IsMatch(url))
            {
                var resource = Data ?? throw new ArgumentException("Widget resource binding requires presentation data.");
                foreach (var key in url[2..^1].Split('.').Skip(2))
                {
                    if (resource.ValueKind != JsonValueKind.Object || !resource.TryGetProperty(key, out resource))
                        throw new ArgumentException("Widget resource binding was not found in presentation data.");
                }
                if (resource.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Widget resource binding must resolve to a URL string.");
                url = resource.GetString()!;
            }
            if (!navigation && artwork.TryGetValue(url, out var image))
            {
                expandedArtworkBytes += JsonSerializer.SerializeToUtf8Bytes(image).Length;
                // Reserve bounded authored content and provider controls before persisting a snapshot.
                if (expandedArtworkBytes > WidgetCard.MaxExpandedTemplateBytes - MaxBytes * 2)
                    throw new ArgumentException("Expanded widget artwork exceeds the native template limit. Use fewer repeated image resources.");
                return;
            }
            ValidatePublicUrl(url);
        }
    }

    private static void ValidateAction(JsonElement action, string type)
    {
        if (type is not ("Action.OpenUrl" or "Action.Execute" or "Action.ShowCard" or "Action.ToggleVisibility"))
            throw new ArgumentException("Use native OpenUrl, Execute, ShowCard or ToggleVisibility actions, subject to host support.");
        if (type == "Action.OpenUrl" && !action.TryGetProperty("url", out _))
            throw new ArgumentException("Widget OpenUrl requires a URL.");
        if (type == "Action.Execute" &&
            (!action.TryGetProperty("verb", out var verb) || verb.ValueKind != JsonValueKind.String ||
             !Verbs.Contains(verb.GetString(), StringComparer.Ordinal)))
            throw new ArgumentException("Unknown widget Execute verb.");
        if (type == "Action.Execute" && action.GetProperty("verb").GetString() == WidgetLightingAction.Verb)
        {
            if (!action.TryGetProperty("associatedInputs", out var inputs) || inputs.ValueKind != JsonValueKind.String ||
                inputs.GetString() != "none" || !action.TryGetProperty("data", out var data))
                throw new ArgumentException("Lighting actions require literal data and associatedInputs none.");
            _ = WidgetLightingAction.Parse(data);
        }
    }

    internal static void ValidateState(JsonElement? state)
    {
        if (state is null) return;
        if (state.Value.ValueKind != JsonValueKind.Object ||
            JsonSerializer.SerializeToUtf8Bytes(state.Value).Length > MaxStateBytes)
            throw new ArgumentException("Widget initialState must be an object of at most 4096 bytes.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in state.Value.EnumerateObject())
        {
            if (!keys.Add(property.Name) || property.Name.Length is < 1 or > 64 ||
                !property.Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ||
                property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                throw new ArgumentException("Widget state requires unique simple keys and scalar JSON values.");
        }
    }

    internal static void ValidatePublicUrl(string url)
    {
        if (url.Length > 2_048 || url.Contains("${", StringComparison.Ordinal) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.IsLoopback ||
            uri.HostNameType != UriHostNameType.Dns || !uri.IdnHost.Contains('.') ||
            new[] { ".localhost", ".local", ".internal", ".home", ".lan" }
                .Any(suffix => uri.IdnHost.TrimEnd('.').EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Widget resources require public HTTPS URLs without credentials, custom ports, local hosts or computed URLs.");
    }

    /// <summary>Returns a detached card. Only the provider supplies facts and the state/data namespaces.</summary>
    internal JsonObject CreateCard() => JsonNode.Parse(Card.GetRawText())!.AsObject();

    /// <summary>Translates saved PoC presets once when reading old state; new tool requests never accept presets.</summary>
    public static WidgetPresentation MigrateLegacy(JsonElement legacy)
    {
        var theme = legacy.TryGetProperty("theme", out var oldTheme) ? oldTheme.GetString() : "native";
        if (theme is not ("native" or "minimal" or "cosmic" or "mystic"))
            throw new ArgumentException("Unknown saved widget preset.");
        JsonArray body;
        if (legacy.TryGetProperty("body", out var savedBody) && savedBody.ValueKind != JsonValueKind.Null)
            body = JsonNode.Parse(savedBody.GetRawText())!.AsArray();
        else
        {
            var center = theme == "mystic";
            var title = new { type = "TextBlock", text = "${title}", weight = "Bolder", wrap = true,
                horizontalAlignment = center ? "Center" : "Left" };
            var value = new { type = "TextBlock", text = "${value}", size = "ExtraLarge", wrap = true,
                horizontalAlignment = center ? "Center" : "Left" };
            var detail = new Dictionary<string, object> { ["type"] = "TextBlock", ["text"] = "${detail}",
                ["wrap"] = true, ["$when"] = "${$host.widgetSize != 'small'}" };
            var image = new Dictionary<string, object> { ["type"] = "Image", ["size"] = "Small",
                ["url"] = "https://science.nasa.gov/wp-content/uploads/2023/11/mercury-messenger-globe-pia15162.jpg",
                ["altText"] = "Mercury", ["horizontalAlignment"] = center ? "Center" : "Left",
                ["$when"] = "${$host.widgetSize != 'small'}" };
            object[] items = theme switch
            {
                "minimal" => [title, value],
                "mystic" => [image, title, value, detail],
                "cosmic" => [new { type = "ColumnSet", columns = new object[]
                    { new { type = "Column", width = "stretch", items = new object[] { title, value } },
                      new { type = "Column", width = "auto", items = new[] { image } } } }, detail],
                _ => [title, value, detail]
            };
            body = JsonSerializer.SerializeToNode(items)!.AsArray();
        }
        if (theme is "cosmic" or "mystic")
            body = new JsonArray(new JsonObject { ["type"] = "Container", ["style"] = "emphasis", ["items"] = body });
        var result = new WidgetPresentation(JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "AdaptiveCard", ["version"] = "1.6", ["body"] = body
        }));
        result.Validate();
        return result;
    }
}
