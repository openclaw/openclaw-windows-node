using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClawTray.A2UI.Protocol;

/// <summary>
/// Inbound A2UI v0.8 message envelopes (agent → node). Sealed hierarchy —
/// extend by adding a new record + a router branch. Unknown envelope kinds
/// surface as <see cref="UnknownEnvelopeMessage"/> and are logged.
/// </summary>
public abstract record A2UIMessage;

/// <summary>
/// surfaceUpdate: declares (or replaces) the components for a surface. The
/// surface is implicitly created on first surfaceUpdate — there is no
/// separate createSurface envelope in v0.8.
/// </summary>
public sealed record SurfaceUpdateMessage : A2UIMessage
{
    public required string SurfaceId { get; init; }
    public required IReadOnlyList<A2UIComponentDef> Components { get; init; }
}

/// <summary>
/// beginRendering: tells the client which component is the root for the
/// surface, and applies optional surface-level styles. Sent after the
/// surfaceUpdate that introduces the components.
/// </summary>
public sealed record BeginRenderingMessage : A2UIMessage
{
    public required string SurfaceId { get; init; }
    public required string Root { get; init; }
    public string? CatalogId { get; init; }
    public JsonObject? Styles { get; init; }
}

/// <summary>
/// dataModelUpdate: writes one or more entries into the surface's data
/// model. Each entry is a (key, typed value) pair; <c>path</c> scopes the
/// keys (omitted/"/" replaces the entire data model).
/// </summary>
public sealed record DataModelUpdateMessage : A2UIMessage
{
    public required string SurfaceId { get; init; }
    public string? Path { get; init; }
    public required IReadOnlyList<DataModelEntry> Contents { get; init; }
}

public sealed record DeleteSurfaceMessage : A2UIMessage
{
    public required string SurfaceId { get; init; }
}

public sealed record UnknownEnvelopeMessage : A2UIMessage
{
    public required string Kind { get; init; }
    public required JsonObject Body { get; init; }
}

/// <summary>
/// One component as carried in <c>surfaceUpdate.components</c>.
/// <see cref="ComponentName"/> is the discriminator (e.g. "Text", "Column"),
/// <see cref="Properties"/> is the body that lived under that key.
/// </summary>
public sealed record A2UIComponentDef
{
    public required string Id { get; init; }
    public required string ComponentName { get; init; }
    public required JsonObject Properties { get; init; }
    public double? Weight { get; init; }
}

/// <summary>
/// Single dataModelUpdate.contents entry. Exactly one of Value*/ValueMap is
/// expected on the wire; we expose them all and let the store apply whichever
/// is set.
/// </summary>
public sealed record DataModelEntry
{
    public required string Key { get; init; }
    public string? ValueString { get; init; }
    public double? ValueNumber { get; init; }
    public bool? ValueBoolean { get; init; }
    /// <summary>An adjacency-list map: each item is itself a DataModelEntry.</summary>
    public IReadOnlyList<DataModelEntry>? ValueMap { get; init; }

    /// <summary>Convert this entry's value to a JsonNode for storage.</summary>
    public JsonNode? ToJsonNode()
    {
        if (ValueString != null) return JsonValue.Create(ValueString);
        if (ValueNumber.HasValue) return JsonValue.Create(ValueNumber.Value);
        if (ValueBoolean.HasValue) return JsonValue.Create(ValueBoolean.Value);
        if (ValueMap != null)
        {
            var obj = new JsonObject();
            foreach (var entry in ValueMap)
                obj[entry.Key] = entry.ToJsonNode();
            return obj;
        }
        return null;
    }
}

/// <summary>
/// The A2UI v0.8 "value" tagged union. Almost every leaf property in a
/// component (text, url, label, etc.) is one of these. Resolves either to a
/// literal or to a JSON Pointer path into the surface's data model.
/// </summary>
public sealed record A2UIValue
{
    public string? LiteralString { get; init; }
    public double? LiteralNumber { get; init; }
    public bool? LiteralBoolean { get; init; }
    public IReadOnlyList<string>? LiteralArray { get; init; }
    public string? Path { get; init; }

    public bool HasLiteral => LiteralString != null || LiteralNumber.HasValue
                              || LiteralBoolean.HasValue || LiteralArray != null;
    public bool HasPath => !string.IsNullOrEmpty(Path);

    public static A2UIValue? From(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        var v = new A2UIValue
        {
            LiteralString = AsString(obj["literalString"]),
            LiteralNumber = AsNumber(obj["literalNumber"]),
            LiteralBoolean = AsBool(obj["literalBoolean"]),
            LiteralArray = AsStringArray(obj["literalArray"]),
            Path = AsString(obj["path"]),
        };
        return v;
    }

    private static string? AsString(JsonNode? n) =>
        n is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;
    private static double? AsNumber(JsonNode? n) =>
        n is JsonValue jv && jv.TryGetValue<double>(out var d) ? d : null;
    private static bool? AsBool(JsonNode? n) =>
        n is JsonValue jv && jv.TryGetValue<bool>(out var b) ? b : null;
    private static IReadOnlyList<string>? AsStringArray(JsonNode? n)
    {
        if (n is not JsonArray arr) return null;
        var list = new List<string>(arr.Count);
        foreach (var i in arr)
            if (i is JsonValue jv && jv.TryGetValue<string>(out var s)) list.Add(s);
        return list;
    }
}

/// <summary>
/// Outbound A2UI action (node → agent). v0.8 client→server envelope shape.
/// </summary>
public sealed record A2UIAction
{
    /// <summary>
    /// Idempotency / dedup key. The gateway uses this as the <c>key</c> field
    /// on the <c>agent.request</c> deep-link so a double-click doesn't produce
    /// two agent turns. Generated per-Raise; callers don't supply it.
    /// </summary>
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public required string Name { get; init; }
    public required string SurfaceId { get; init; }
    public string? SourceComponentId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Per-action context entries assembled from <c>action.context</c>.</summary>
    public JsonObject? Context { get; init; }
}

/// <summary>
/// Parses a JSONL stream into <see cref="A2UIMessage"/> records. Tolerant:
/// malformed lines are skipped (logged when a logger is supplied), unknown
/// envelope keys yield <see cref="UnknownEnvelopeMessage"/> rather than throwing.
/// </summary>
public static class A2UIMessageParser
{
    /// <summary>
    /// Per-line cap. Single-line JSON envelopes above this are dropped to keep
    /// a hostile/malformed payload from forcing the JSON DOM allocator into a
    /// pathological state. Total stream size is bounded separately at the
    /// transport / capability boundary.
    /// </summary>
    public const int MaxLineLength = 1 * 1024 * 1024;

    public static IEnumerable<A2UIMessage> Parse(string jsonl) => Parse(jsonl, logger: null);

    public static IEnumerable<A2UIMessage> Parse(string jsonl, OpenClaw.Shared.IOpenClawLogger? logger)
    {
        if (string.IsNullOrWhiteSpace(jsonl)) yield break;

        // Stream-iterate without allocating a string[] for the whole blob.
        int start = 0;
        int len = jsonl.Length;
        while (start < len)
        {
            int end = jsonl.IndexOfAny(s_lineSeparators, start);
            if (end < 0) end = len;

            int lineLen = end - start;
            if (lineLen > 0)
            {
                string line = jsonl.Substring(start, lineLen);
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    if (trimmed.Length > MaxLineLength)
                    {
                        logger?.Warn($"[A2UI] dropping oversize JSONL line ({trimmed.Length} > {MaxLineLength} chars)");
                    }
                    else
                    {
                        A2UIMessage? msg = null;
                        try { msg = ParseLine(trimmed); }
                        catch (JsonException ex)
                        {
                            logger?.Warn($"[A2UI] dropping malformed JSONL line: {OpenClaw.Shared.TokenSanitizer.Sanitize(Truncate(trimmed, 200))} — {ex.Message}");
                        }
                        catch (FormatException ex)
                        {
                            logger?.Warn($"[A2UI] dropping malformed JSONL line: {OpenClaw.Shared.TokenSanitizer.Sanitize(Truncate(trimmed, 200))} — {ex.Message}");
                        }
                        if (msg != null) yield return msg;
                    }
                }
            }

            // Skip any contiguous run of CR/LF before the next line.
            start = end;
            while (start < len && (jsonl[start] == '\r' || jsonl[start] == '\n')) start++;
        }
    }

    private static readonly char[] s_lineSeparators = { '\r', '\n' };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";

    public static A2UIMessage? ParseLine(string json)
    {
        var node = JsonNode.Parse(json);
        if (node is not JsonObject root) return null;

        // surfaceUpdate
        if (root["surfaceUpdate"] is JsonObject su)
        {
            var surfaceId = RequireString(su, "surfaceId");
            var components = new List<A2UIComponentDef>();
            if (su["components"] is JsonArray comps)
            {
                foreach (var item in comps)
                {
                    if (item is not JsonObject co) continue;
                    var def = ParseComponent(co);
                    if (def != null) components.Add(def);
                }
            }
            return new SurfaceUpdateMessage { SurfaceId = surfaceId, Components = components };
        }

        // beginRendering
        if (root["beginRendering"] is JsonObject br)
        {
            var surfaceId = RequireString(br, "surfaceId");
            var rootId = RequireString(br, "root");
            var catalogId = OptionalString(br, "catalogId");
            var styles = br["styles"] as JsonObject;
            return new BeginRenderingMessage { SurfaceId = surfaceId, Root = rootId, CatalogId = catalogId, Styles = styles };
        }

        // dataModelUpdate
        if (root["dataModelUpdate"] is JsonObject dmu)
        {
            var surfaceId = RequireString(dmu, "surfaceId");
            var path = OptionalString(dmu, "path");
            var contents = new List<DataModelEntry>();
            if (dmu["contents"] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is not JsonObject e) continue;
                    var entry = ParseEntry(e);
                    if (entry != null) contents.Add(entry);
                }
            }
            return new DataModelUpdateMessage { SurfaceId = surfaceId, Path = path, Contents = contents };
        }

        // deleteSurface
        if (root["deleteSurface"] is JsonObject ds)
        {
            return new DeleteSurfaceMessage { SurfaceId = RequireString(ds, "surfaceId") };
        }

        // Empty root object: nothing to dispatch on. Return null so the
        // caller's per-line error path logs a malformed-input warning instead
        // of returning UnknownEnvelopeMessage{ Kind="" }, which would
        // present as a normal-but-unsupported envelope and confuse triage.
        if (root.Count == 0) return null;

        var kind = string.Empty;
        foreach (var kv in root) { kind = kv.Key; break; }
        return new UnknownEnvelopeMessage { Kind = kind, Body = root };
    }

    private static A2UIComponentDef? ParseComponent(JsonObject co)
    {
        var id = OptionalString(co, "id");
        if (id == null) return null;

        // component is a wrapper object whose single key is the component name.
        if (co["component"] is not JsonObject wrapper) return null;
        string? name = null;
        JsonObject? props = null;
        foreach (var kv in wrapper)
        {
            name = kv.Key;
            // Reject malformed payloads where the wrapper-key value is NOT a
            // JsonObject. Accepting an empty JsonObject masks gateway/model
            // bugs (e.g. shipping a string where a properties object was
            // expected) and renders an empty-but-named component, which is
            // indistinguishable from a legitimate component with no props.
            if (kv.Value is JsonObject o) props = o;
            else return null;
            break;
        }
        if (name == null) return null;

        double? weight = null;
        if (co["weight"] is JsonValue wv && wv.TryGetValue<double>(out var w)) weight = w;

        return new A2UIComponentDef
        {
            Id = id,
            ComponentName = name,
            Properties = props ?? new JsonObject(),
            Weight = weight,
        };
    }

    private static DataModelEntry? ParseEntry(JsonObject e)
    {
        var key = OptionalString(e, "key");
        if (key == null) return null;
        var entry = new DataModelEntry
        {
            Key = key,
            ValueString = OptionalString(e, "valueString"),
            ValueNumber = e["valueNumber"] is JsonValue jvn && jvn.TryGetValue<double>(out var n) ? n : null,
            ValueBoolean = e["valueBoolean"] is JsonValue jvb && jvb.TryGetValue<bool>(out var b) ? b : null,
            ValueMap = ParseValueMap(e["valueMap"] as JsonArray),
        };
        return entry;
    }

    private static IReadOnlyList<DataModelEntry>? ParseValueMap(JsonArray? arr)
    {
        if (arr == null) return null;
        var list = new List<DataModelEntry>(arr.Count);
        foreach (var item in arr)
        {
            if (item is not JsonObject e) continue;
            var nested = ParseEntry(e);
            if (nested != null) list.Add(nested);
        }
        return list;
    }

    private static string RequireString(JsonObject o, string key)
    {
        if (o[key] is JsonValue jv && jv.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
            return s;
        throw new FormatException($"missing or invalid '{key}'");
    }

    private static string? OptionalString(JsonObject o, string key) =>
        o[key] is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;
}
