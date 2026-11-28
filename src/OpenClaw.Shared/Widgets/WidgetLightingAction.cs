// Admits literal, user-clicked lighting controls. This is not a general command or automation surface.
// Usage: Parse(actionData), then IsDeclared(presentation, request) before using a locally opted-in driver.
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared.Widgets;

public enum WidgetLightingOutcome { Disabled, Ready, NoDevices, Acknowledged, Released, Unavailable, Busy, Failed }
public sealed record WidgetLightingFeedback(WidgetLightingOutcome Outcome, int DeviceCount = 0);

/// <summary>Fixed DynamicLightingDriver protocol operations. All changes target its first discovered device.</summary>
public sealed partial record WidgetLightingAction(string Operation, string? Color = null, string? Preset = null, string? AccentColor = null)
{
    public const string Verb = "widget.lighting";
    private static readonly string[] Presets = ["solid", "wave", "breathe", "twinkle", "gradient", "rainbow"];

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColor();

    /// <summary>Rejects computed values, extra properties and unbounded driver commands.</summary>
    public static WidgetLightingAction Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Lighting action data must be an object.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in data.EnumerateObject())
            if (!keys.Add(field.Name) || field.Name is not ("operation" or "color" or "preset" or "accentColor") ||
                field.Value.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Lighting actions require unique literal operation, color, preset and accentColor fields.");
        string? Read(string key) => data.TryGetProperty(key, out var value) ? value.GetString() : null;
        var result = new WidgetLightingAction(Read("operation") ?? "", Read("color"), Read("preset"), Read("accentColor"));
        var valid = result.Operation switch
        {
            "status" or "release" => keys.Count == 1,
            "color" => keys.Count == 2 && result.Color is not null && HexColor().IsMatch(result.Color),
            "preset" => result.Color is not null && HexColor().IsMatch(result.Color) &&
                Presets.Contains(result.Preset, StringComparer.Ordinal) &&
                (result.AccentColor is null || HexColor().IsMatch(result.AccentColor)) &&
                keys.Count == (result.AccentColor is null ? 3 : 4),
            _ => false
        };
        if (!valid) throw new ArgumentException("Lighting requires status, release, a #RRGGBB color, or a fixed preset and colors.");
        return result;
    }

    /// <summary>Only fixed protocol commands can reach the driver; no source value becomes an executable argument.</summary>
    public string Command => Operation switch
    {
        "status" => "GET_ALL_LAYOUTS",
        "release" => "QUIT",
        "color" => $"SET_ALL {Color}",
        "preset" => $"CREATE_EFFECT {Preset} base_color={Color}" +
            (AccentColor is null ? "" : $" accent_color={AccentColor}"),
        _ => throw new ArgumentException("Unknown lighting operation.")
    };

    public static bool Contains(WidgetPresentation? presentation) => Actions(presentation).Any();

    /// <summary>Checks an invocation against the current admitted card, not against source data or submitted inputs.</summary>
    public static bool IsDeclared(WidgetPresentation? presentation, WidgetLightingAction request) =>
        Actions(presentation).Any(action => Parse(action.GetProperty("data")) == request);

    private static IEnumerable<JsonElement> Actions(WidgetPresentation? presentation)
    {
        if (presentation is null) yield break;
        foreach (var action in Visit(presentation.Card)) yield return action;

        static IEnumerable<JsonElement> Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray())
                    foreach (var action in Visit(item)) yield return action;
            if (element.ValueKind != JsonValueKind.Object) yield break;
            if (element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                type.GetString() == "Action.Execute" && element.TryGetProperty("verb", out var verb) &&
                verb.ValueKind == JsonValueKind.String && verb.GetString() == Verb)
                yield return element;
            foreach (var field in element.EnumerateObject())
                if (field.Name is not ("data" or "choices" or "facts"))
                    foreach (var action in Visit(field.Value)) yield return action;
        }
    }
}
