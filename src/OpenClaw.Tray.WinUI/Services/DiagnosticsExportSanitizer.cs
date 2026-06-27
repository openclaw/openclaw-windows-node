using OpenClaw.Shared;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClawTray.Services;

internal enum DiagnosticsExportLineKind
{
    Text,
    Jsonl
}

internal static class DiagnosticsExportSanitizer
{
    internal const string UnsafeTextLineSentinel = "[REDACTED_UNSAFE_LOG_LINE]";
    internal const string UnsafeJsonlLineSentinel = """{"event":"redacted_unsafe_log_line"}""";

    private static readonly JsonSerializerOptions ReadableJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string SanitizeTextBlock(string? text, DiagnosticsExportLineKind kind = DiagnosticsExportLineKind.Text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var normalized = kind == DiagnosticsExportLineKind.Jsonl
            ? NormalizeLegacyJsonlText(text)
            : text;
        var sanitized = TokenSanitizer.SanitizeLogMessage(normalized);
        return sanitized == TokenSanitizer.SanitizerTimeoutSentinel
            ? kind == DiagnosticsExportLineKind.Jsonl ? UnsafeJsonlLineSentinel : UnsafeTextLineSentinel
            : sanitized;
    }

    public static string SanitizeLine(string? line, DiagnosticsExportLineKind kind = DiagnosticsExportLineKind.Text)
    {
        if (line is null)
            return string.Empty;

        var sanitized = SanitizeTextBlock(line, kind);
        if (kind != DiagnosticsExportLineKind.Jsonl)
            return sanitized;

        return IsValidJsonLine(sanitized) ? sanitized : UnsafeJsonlLineSentinel;
    }

    internal static string SanitizeLineForTest(string? line, DiagnosticsExportLineKind kind = DiagnosticsExportLineKind.Text) =>
        SanitizeLine(line, kind);

    private static string NormalizeLegacyJsonlText(string text)
    {
        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        for (var i = 0; i < lines.Length; i++)
            lines[i] = NormalizeLegacyJsonLine(lines[i]);

        return string.Join('\n', lines);
    }

    private static string NormalizeLegacyJsonLine(string line)
    {
        if (!NeedsLegacyJsonNormalization(line))
            return line;

        try
        {
            var node = JsonNode.Parse(line);
            var normalized = NormalizeJsonNode(node);
            return normalized?.ToJsonString(ReadableJsonOptions) ?? line;
        }
        catch (JsonException)
        {
            return line;
        }
    }

    private static bool NeedsLegacyJsonNormalization(string text) =>
        text.Contains("\\u0022", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("\\u002B", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("\\u003C", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("\\u003E", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("\\u0026", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("\\u0060", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("\\r\\n", StringComparison.Ordinal);

    private static JsonNode? NormalizeJsonNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var normalizedObject = new JsonObject();
                foreach (var property in obj)
                    normalizedObject[property.Key] = NormalizeJsonNode(property.Value);
                return normalizedObject;

            case JsonArray array:
                var normalizedArray = new JsonArray();
                foreach (var item in array)
                    normalizedArray.Add(NormalizeJsonNode(item));
                return normalizedArray;

            case JsonValue value when value.TryGetValue<string>(out var text):
                var normalized = NormalizeLogString(text);
                var trimmed = normalized.TrimStart();
                if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
                {
                    try
                    {
                        return NormalizeJsonNode(JsonNode.Parse(normalized));
                    }
                    catch (JsonException)
                    {
                        // Keep malformed JSON-shaped text as flattened text.
                    }
                }

                return JsonValue.Create(normalized);

            default:
                return node?.DeepClone();
        }
    }

    private static string NormalizeLogString(string value) =>
        value
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');

    private static bool IsValidJsonLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        try
        {
            using var _ = JsonDocument.Parse(line);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
