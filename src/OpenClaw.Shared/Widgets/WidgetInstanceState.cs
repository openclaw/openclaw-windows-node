// Owns bounded per-pinned-instance state, separate from source observations and Gateway credentials.
// Usage: Parse(customState, snapshots).Apply(snapshot.Presentation, verb, input).Serialize().
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.Shared.Widgets;

/// <summary>Pure native interaction policy. No generated action can invoke a node command or source fetch.</summary>
public sealed record WidgetInstanceState(string? TrackerId, JsonObject Values, WidgetLightingFeedback? Lighting = null)
{
    private const string Prefix = "v2:";

    /// <summary>Restores v1/v2 bindings and reconciles saved values with the current design's declarations.</summary>
    public static WidgetInstanceState Parse(string? customState, IReadOnlyList<WidgetSnapshot> snapshots)
    {
        if (customState?.StartsWith(Prefix, StringComparison.Ordinal) == true)
        {
            if (customState.Length > WidgetPresentation.MaxStateBytes * 6 + 512)
                throw new ArgumentException("Widget instance state is too large.");
            var saved = JsonNode.Parse(customState[Prefix.Length..]) as JsonObject
                ?? throw new ArgumentException("Widget instance state requires an object.");
            if (saved["trackerId"] is null || saved["values"] is not JsonObject values)
                throw new ArgumentException("Widget instance state requires trackerId and values.");
            var id = saved["trackerId"]!.GetValue<string>();
            WidgetSnapshotStore.ValidateId(id);
            WidgetPresentation.ValidateState(JsonSerializer.SerializeToElement(values));
            WidgetLightingFeedback? lighting = null;
            if (saved["lighting"] is JsonObject feedback)
            {
                if (!Enum.TryParse<WidgetLightingOutcome>(feedback["outcome"]?.GetValue<string>(), out var outcome) ||
                    !Enum.IsDefined(outcome) || feedback["deviceCount"]?.GetValue<int>() is not (>= 0 and <= 256))
                    throw new ArgumentException("Invalid widget lighting feedback.");
                lighting = new(outcome, feedback["deviceCount"]!.GetValue<int>());
            }
            return new(id, Reconcile(snapshots.FirstOrDefault(s => s.Id == id)?.Presentation, values), lighting);
        }
        var trackerId = customState?.StartsWith(WidgetCard.StatePrefix, StringComparison.Ordinal) == true
            ? customState[WidgetCard.StatePrefix.Length..] : snapshots.Count == 1 ? snapshots[0].Id : null;
        return new(trackerId, Initial(snapshots.FirstOrDefault(s => s.Id == trackerId)?.Presentation));
    }

    /// <summary>Detaches the design's defaults so instances never share mutable state.</summary>
    public static JsonObject Initial(WidgetPresentation? presentation) =>
        presentation?.InitialState is { } initial ? JsonNode.Parse(initial.GetRawText())!.AsObject() : new();

    private static JsonObject Reconcile(WidgetPresentation? presentation, JsonObject previous)
    {
        var result = Initial(presentation);
        foreach (var key in result.Select(p => p.Key).ToArray())
        {
            if (!previous.TryGetPropertyValue(key, out var saved)) continue;
            var initialKind = result[key]?.GetValueKind() ?? JsonValueKind.Null;
            var savedKind = saved?.GetValueKind() ?? JsonValueKind.Null;
            if (initialKind == JsonValueKind.Null || initialKind == savedKind || initialKind is JsonValueKind.True or JsonValueKind.False &&
                savedKind is JsonValueKind.True or JsonValueKind.False)
                result[key] = saved?.DeepClone();
        }
        return result;
    }

    /// <summary>Encodes the binding and values for Windows-owned per-instance CustomState persistence.</summary>
    public string Serialize() => TrackerId is null ? "" : Prefix + new JsonObject
    {
        ["trackerId"] = TrackerId, ["values"] = Values.DeepClone(),
        ["lighting"] = Lighting is null ? null : new JsonObject
        { ["outcome"] = Lighting.Outcome.ToString(), ["deviceCount"] = Lighting.DeviceCount }
    }.ToJsonString();

    /// <summary>Applies only declared scalar state keys; source facts and other pinned instances stay unchanged.</summary>
    public WidgetInstanceState Apply(WidgetPresentation? presentation, string verb, JsonElement input)
    {
        if (verb == WidgetLightingAction.Verb || !WidgetPresentation.Verbs.Contains(verb, StringComparer.Ordinal))
            throw new ArgumentException("Unknown widget interaction.");
        if (input.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Widget action input must be an object.");
        var declared = Initial(presentation);
        var values = Reconcile(presentation, Values);
        if (verb == "widget.reset") values = declared;
        else if (verb == "widget.set")
        {
            foreach (var key in declared.Select(p => p.Key))
            {
                if (!input.TryGetProperty(key, out var value)) continue;
                var kind = declared[key]?.GetValueKind() ?? JsonValueKind.Null;
                if (value.ValueKind == JsonValueKind.String && kind is JsonValueKind.True or JsonValueKind.False)
                {
                    if (!bool.TryParse(value.GetString(), out var boolean))
                        throw new ArgumentException("Widget toggle input requires true or false.");
                    values[key] = boolean;
                }
                else
                {
                    if (kind != JsonValueKind.Null &&
                        !(kind is JsonValueKind.True or JsonValueKind.False && value.ValueKind is JsonValueKind.True or JsonValueKind.False) &&
                        kind != value.ValueKind)
                        throw new ArgumentException("Widget input must match the declared state's JSON type.");
                    values[key] = JsonNode.Parse(value.GetRawText());
                }
            }
        }
        else if (verb is "widget.increment" or "widget.toggle")
        {
            var key = input.GetProperty("key").GetString()
                ?? throw new ArgumentException("Widget action key is required.");
            if (!declared.ContainsKey(key))
                throw new ArgumentException("Widget action key is not declared in initialState.");
            if (verb == "widget.toggle")
                values[key] = !values[key]!.GetValue<bool>();
            else
            {
                var amount = input.TryGetProperty("amount", out var step) ? step.GetDouble() : 1;
                var result = values[key]!.GetValue<double>() + amount;
                if (!double.IsFinite(result)) throw new ArgumentException("Widget increment must remain finite.");
                values[key] = result;
            }
        }
        WidgetPresentation.ValidateState(JsonSerializer.SerializeToElement(values));
        return new(TrackerId, values, Lighting);
    }
}
