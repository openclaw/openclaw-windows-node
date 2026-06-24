using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClawTray.Services;

internal readonly record struct CronRunDisplayText(string PreviewText, string? FullText)
{
    public bool HasExpandableFullText =>
        !string.IsNullOrWhiteSpace(FullText) &&
        (FullText!.Length > CronRunHistoryDisplay.PreviewMaxChars ||
         FullText.Contains('\n') ||
         FullText.Contains('\r') ||
         !string.Equals(PreviewText, FullText, StringComparison.Ordinal));
}

internal static class CronRunHistoryDisplay
{
    public const int PreviewMaxChars = 120;

    private static readonly string[] FullResponseKeys =
    [
        "fullResponse",
        "full_response",
        "assistantResponse",
        "assistant_response",
        "response",
        "answer",
        "output",
        "result",
        "content",
        "text"
    ];

    private static readonly string[] TranscriptKeys =
    [
        "transcript",
        "transcriptJsonl",
        "transcript_jsonl",
        "events",
        "messages"
    ];

    private static readonly string[] TextKeys = ["text", "content", "message"];

    private static readonly Regex AbsolutePathPattern = new(
        @"(?:[A-Za-z]:\\(?:Users|home|usr|var|tmp)\\[^\s""']+)|(?:/(?:home|Users|usr|var|tmp)/[^\s""']+)",
        RegexOptions.Compiled);

    public static CronRunDisplayText ExtractText(JsonElement entry, bool isError)
    {
        var summary = SanitizeForDisplay(GetStringProperty(entry, "summary")) ?? string.Empty;
        var error = SanitizeForDisplay(GetStringProperty(entry, "error")) ?? string.Empty;
        var preview = isError && !string.IsNullOrWhiteSpace(error) ? error : summary;

        var fullText = isError && !string.IsNullOrWhiteSpace(error)
            ? error
            : FindFullResponseText(entry) ?? FindTranscriptResponseText(entry) ?? summary;
        fullText = SanitizeForDisplay(fullText);

        if (string.IsNullOrWhiteSpace(preview) && !string.IsNullOrWhiteSpace(fullText))
            preview = fullText!;

        return new CronRunDisplayText(preview, string.IsNullOrWhiteSpace(fullText) ? null : fullText);
    }

    public static string? SanitizeForDisplay(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sanitized = TokenSanitizer.Sanitize(text);
        sanitized = AbsolutePathPattern.Replace(sanitized, m =>
        {
            var sep = m.Value.Contains('\\') ? '\\' : '/';
            var lastSep = m.Value.LastIndexOf(sep);
            return lastSep >= 0 ? "…" + sep + m.Value[(lastSep + 1)..] : m.Value;
        });
        return sanitized;
    }

    private static string? FindFullResponseText(JsonElement entry)
    {
        var direct = FindStringByKeys(entry, FullResponseKeys);
        if (!string.IsNullOrWhiteSpace(direct))
            return NormalizePotentialTranscript(direct);

        if (entry.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            var nested = FindStringByKeys(result, FullResponseKeys);
            if (!string.IsNullOrWhiteSpace(nested))
                return NormalizePotentialTranscript(nested);
        }

        if (entry.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object)
        {
            var nested = FindStringByKeys(response, FullResponseKeys);
            if (!string.IsNullOrWhiteSpace(nested))
                return NormalizePotentialTranscript(nested);
        }

        return null;
    }

    private static string? FindTranscriptResponseText(JsonElement entry)
    {
        foreach (var key in TranscriptKeys)
        {
            if (!entry.TryGetProperty(key, out var value))
                continue;

            var extracted = value.ValueKind == JsonValueKind.String
                ? TryExtractAssistantTextFromTranscript(value.GetString())
                : TryExtractAssistantTextFromTranscript(value);
            if (!string.IsNullOrWhiteSpace(extracted))
                return extracted;
        }

        return null;
    }

    private static string? NormalizePotentialTranscript(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        return TryExtractAssistantTextFromTranscript(text) ?? text;
    }

    private static string? TryExtractAssistantTextFromTranscript(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return null;
        var trimmed = transcript.Trim();

        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var extracted = TryExtractAssistantTextFromTranscript(doc.RootElement);
                if (!string.IsNullOrWhiteSpace(extracted))
                    return extracted;
            }
            catch (JsonException)
            {
                // Not a single JSON document; fall through to JSONL parsing.
            }
        }

        if (!trimmed.Contains('\n')) return null;

        var finalCandidates = new List<string>();
        var deltas = new StringBuilder();
        foreach (var line in trimmed.Split('\n'))
        {
            var lineTrimmed = line.Trim();
            if (lineTrimmed.Length == 0 || lineTrimmed[0] != '{') continue;

            try
            {
                using var doc = JsonDocument.Parse(lineTrimmed);
                CollectAssistantText(doc.RootElement, finalCandidates, deltas);
            }
            catch (JsonException)
            {
                // Ignore non-JSONL lines in mixed diagnostic transcripts.
            }
        }

        return BuildAssistantText(finalCandidates, deltas);
    }

    private static string? TryExtractAssistantTextFromTranscript(JsonElement transcript)
    {
        var finalCandidates = new List<string>();
        var deltas = new StringBuilder();
        CollectAssistantText(transcript, finalCandidates, deltas);
        return BuildAssistantText(finalCandidates, deltas);
    }

    private static string? BuildAssistantText(List<string> finalCandidates, StringBuilder deltas)
    {
        if (finalCandidates.Count > 0)
        {
            var candidates = finalCandidates
                .Select(c => c.Trim())
                .Where(c => c.Length > 0)
                .ToArray();
            var retained = new List<string>();

            for (var i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];
                var appearsInLaterCandidate = candidates
                    .Skip(i + 1)
                    .Any(later => later.Contains(candidate, StringComparison.Ordinal));
                if (!appearsInLaterCandidate && !retained.Contains(candidate, StringComparer.Ordinal))
                    retained.Add(candidate);
            }

            if (retained.Count > 0)
                return string.Join("\n\n", retained);
        }

        return deltas.Length > 0 ? deltas.ToString().Trim() : null;
    }

    private static void CollectAssistantText(
        JsonElement element,
        List<string> finalCandidates,
        StringBuilder deltas)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectAssistantText(item, finalCandidates, deltas);
                return;
            case JsonValueKind.Object:
                break;
            default:
                return;
        }

        var stream = GetStringProperty(element, "stream");
        var role = GetStringProperty(element, "role");
        var type = GetStringProperty(element, "type");
        var kind = GetStringProperty(element, "kind");
        var assistantLike =
            string.Equals(stream, "assistant", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "assistant", StringComparison.OrdinalIgnoreCase) ||
            (type?.Contains("assistant", StringComparison.OrdinalIgnoreCase) == true);

        if (assistantLike)
        {
            AddAssistantTextFromObject(element, finalCandidates, deltas);
            if (element.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                AddAssistantTextFromObject(data, finalCandidates, deltas);
        }

        foreach (var key in new[] { "data", "event", "payload", "record" })
        {
            if (element.TryGetProperty(key, out var nested) && nested.ValueKind == JsonValueKind.Object)
                CollectAssistantText(nested, finalCandidates, deltas);
        }

        foreach (var key in new[] { "events", "messages", "entries", "items" })
        {
            if (element.TryGetProperty(key, out var nested) && nested.ValueKind == JsonValueKind.Array)
                CollectAssistantText(nested, finalCandidates, deltas);
        }

        if (element.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
            CollectAssistantText(message, finalCandidates, deltas);
    }

    private static void AddAssistantTextFromObject(
        JsonElement obj,
        List<string> finalCandidates,
        StringBuilder deltas)
    {
        if (obj.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.String)
        {
            var deltaText = delta.GetString();
            if (!string.IsNullOrEmpty(deltaText))
                deltas.Append(deltaText);
        }

        foreach (var key in TextKeys)
        {
            if (!obj.TryGetProperty(key, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    finalCandidates.Add(text);
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                ExtractTextContentArray(value, finalCandidates);
            }
        }
    }

    private static void ExtractTextContentArray(JsonElement array, List<string> finalCandidates)
    {
        var parts = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrEmpty(value)) parts.Add(value);
                continue;
            }

            if (item.ValueKind == JsonValueKind.Object)
            {
                var text = GetStringProperty(item, "text");
                if (!string.IsNullOrEmpty(text))
                    parts.Add(text);
            }
        }

        if (parts.Count > 0)
            finalCandidates.Add(string.Concat(parts));
    }

    private static string? FindStringByKeys(JsonElement element, IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                var nested = FindStringByKeys(value, TextKeys);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }

        return null;
    }

    private static string? GetStringProperty(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
