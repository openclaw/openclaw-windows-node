using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.Shared;

/// <summary>Reconstructs append-only chat frames once, before all local consumers see them.</summary>
internal sealed class GatewayChatStreamProjection
{
    private sealed record Baseline(JsonObject Message, long Size, long LastUsed);
    private readonly Dictionary<(string Session, string Agent, string Run), Baseline> _messages = new();
    private long _generation = -1;
    private long _sequence;

    internal JsonElement? Project(JsonElement root, long generation, out bool missingBaseline)
    {
        missingBaseline = false;
        if (generation < _generation)
            return null;
        if (generation != _generation)
        {
            _generation = generation;
            _messages.Clear();
        }
        if (!root.TryGetProperty("payload", out var payload)
            || ReadString(payload, "sessionKey") is not { } session
            || ReadString(payload, "runId") is not { } run)
            return root;
        var key = (session, ReadString(payload, "agentId") ?? "", run);
        var state = ReadString(payload, "state");
        if (state is "final" or "error" or "aborted")
        {
            _messages.Remove(key);
            return root;
        }
        if (state != "delta")
            return root;
        JsonObject? message;
        long size;
        if (payload.TryGetProperty("message", out var snapshot))
        {
            if (snapshot.ValueKind != JsonValueKind.Object)
                return root;
            message = JsonNode.Parse(snapshot.GetRawText())!.AsObject();
            size = snapshot.GetRawText().Length;
        }
        else
        {
            // Legacy snapshots still belong to HandleChatEvent's payload.text path.
            if (!payload.TryGetProperty("deltaText", out _) && ReadString(payload, "text") is not null)
                return root;
            var replace = payload.TryGetProperty("replace", out var flag) && flag.ValueKind == JsonValueKind.True;
            _messages.TryGetValue(key, out var baseline);
            message = baseline?.Message;
            size = baseline?.Size ?? 0;
            var delta = ReadString(payload, "deltaText");
            if (message is null && !replace)
            {
                missingBaseline = true;
                return null;
            }
            message ??= new JsonObject { ["role"] = "assistant" };
            if (delta is not null && (delta.Length > 0 || replace))
            {
                size += delta.Length - (replace ? TextLength(message["content"]) : 0);
                if (message["content"] is JsonValue content && content.TryGetValue<string>(out var text))
                    message["content"] = replace ? delta : text + delta;
                else
                {
                    var blocks = message["content"] as JsonArray ?? new JsonArray();
                    if (replace)
                    {
                        var remaining = blocks.Where(block => !IsText(block))
                            .Select(block => block?.DeepClone()).ToArray();
                        blocks = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = delta });
                        foreach (var block in remaining)
                            blocks.Add(block);
                        message["content"] = blocks;
                    }
                    else
                    {
                        var lastText = blocks.OfType<JsonObject>().LastOrDefault(IsText);
                        if (lastText is null)
                            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = delta });
                        else
                            lastText["text"] = (lastText["text"] is JsonValue textValue
                                && textValue.TryGetValue<string>(out var priorText) ? priorText : "") + delta;
                        if (message["content"] is not JsonArray)
                            message["content"] = blocks;
                    }
                }
            }
        }
        // The transport already bounds frames. Also bound retained reconstruction.
        if (size > 2 * 1024 * 1024)
        {
            _messages.Remove(key);
            missingBaseline = true;
            return null;
        }
        if (_messages.Count >= 16 && !_messages.ContainsKey(key))
            _messages.Remove(_messages.MinBy(pair => pair.Value.LastUsed).Key);
        _messages[key] = new(message, size, ++_sequence);
        var projected = JsonNode.Parse(root.GetRawText())!.AsObject();
        projected["payload"]!["message"] = message.DeepClone();
        return JsonSerializer.SerializeToElement(projected);
    }

    private static long TextLength(JsonNode? content) =>
        content is JsonValue value && value.TryGetValue<string>(out var text) ? text.Length
            : content is JsonArray blocks ? blocks.OfType<JsonObject>().Where(IsText)
                .Sum(block => block["text"] is JsonValue part && part.TryGetValue<string>(out var partText)
                    ? (long)partText.Length : 0) : 0;

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static bool IsText(JsonNode? block) =>
        block is JsonObject obj && obj["type"] is JsonValue type
        && type.TryGetValue<string>(out var name) && name == "text";
}
