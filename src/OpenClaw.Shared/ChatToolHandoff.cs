// Projects public handoff receipts before display truncation. For example, pair
// ReadInput(callName, args) with ReadReceipt(result) using the existing tool/run ID.
using System.Text;
using System.Text.Json;

namespace OpenClaw.Shared;

public sealed record ChatToolHandoffInput(string ToolName, bool WaitForMessage);
public sealed record ChatToolHandoffReceipt(string ToolName, string? Acknowledgment);

/// <summary>Reads bounded presentation facts, never continuation context or execution authority.</summary>
public static class ChatToolHandoff
{
    private const int MaxReceiptChars = 64 * 1024;
    private const int MaxAcknowledgmentChars = 4000;

    /// <summary>Retains argument presence and the public wait mode, never the private continuation.</summary>
    public static ChatToolHandoffInput? ReadInput(string name, JsonElement? args) =>
        name is "sessions_yield" or "github_publish" && args.HasValue
            ? new(name, args.Value.ValueKind == JsonValueKind.Object &&
                args.Value.TryGetProperty("waitFor", out var wait) &&
                wait.ValueKind == JsonValueKind.String && wait.GetString() == "message")
            : null;

    /// <summary>Reads a candidate receipt; a successful, identity-matched call must still confirm it.</summary>
    public static ChatToolHandoffReceipt? ReadReceipt(JsonElement result, bool isError = false, string? text = null)
    {
        if (isError)
            return null;
        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            var receipt = ReadFacts(details);
            if (receipt is not null || details.TryGetProperty("status", out _))
                return receipt;
            // A present GitHub details object is authoritative even without status.
            var fallback = ReadText(text ?? ExtractText(result));
            return fallback?.ToolName == "sessions_yield" ? fallback : null;
        }
        return ReadText(text ?? ExtractText(result));
    }

    private static ChatToolHandoffReceipt? ReadText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxReceiptChars)
            return null;
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('{'))
            return null;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(trimmed);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 16 });
            using var document = JsonDocument.ParseValue(ref reader);
            var receipt = ReadFacts(document.RootElement);
            return receipt?.ToolName == "github_publish" && reader.BytesConsumed != bytes.Length
                ? null : receipt;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ChatToolHandoffReceipt? ReadFacts(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
            return null;
        if (status.GetString() == "yielded")
        {
            var acknowledgment = value.TryGetProperty("acknowledgment", out var ack) &&
                ack.ValueKind == JsonValueKind.String ? ack.GetString()?.Trim() : null;
            if (acknowledgment?.Length > MaxAcknowledgmentChars)
            {
                var length = char.IsHighSurrogate(acknowledgment[MaxAcknowledgmentChars - 1])
                    ? MaxAcknowledgmentChars - 1 : MaxAcknowledgmentChars;
                acknowledgment = acknowledgment[..length];
            }
            return new("sessions_yield", acknowledgment);
        }
        return status.GetString() == "started" &&
            value.TryGetProperty("async", out var asyncValue) && asyncValue.ValueKind == JsonValueKind.True &&
            value.TryGetProperty("yielded", out var yielded) && yielded.ValueKind == JsonValueKind.True
                ? new("github_publish", null) : null;
    }

    private static string? ExtractText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("content", out var content))
                return ExtractText(content);
            if (value.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                return text.GetString();
        }
        if (value.ValueKind != JsonValueKind.Array)
            return null;
        var parts = new StringBuilder();
        foreach (var block in value.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object ||
                !block.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                type.GetString() != "text" || !block.TryGetProperty("text", out var text) ||
                text.ValueKind != JsonValueKind.String)
                continue;
            if (parts.Length > 0)
                parts.Append('\n');
            parts.Append(text.GetString());
            if (parts.Length > MaxReceiptChars)
                return null;
        }
        return parts.ToString();
    }
}
