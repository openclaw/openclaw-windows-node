using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenClaw.Chat;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClawTray.Chat;

internal readonly record struct NativeToolIdentity(
    string Name,
    ChatToolIdentityStrength Strength);

/// <summary>
/// Pure deterministic projection of native gateway tool payloads and flattened
/// history output into bounded, truthful display metadata.
/// </summary>
internal static class NativeToolProjector
{
    internal static IReadOnlyList<string> DisplayArgumentKeys { get; } =
        ["command", "path", "file_path", "query", "url", "pattern"];

    internal const int MaxDisplayValueChars = 240;
    private const int MaxIdentityChars = 80;
    private const int ToolOutputMaxChars = 4000;

    private static readonly (string Alias, string Canonical)[] ToolAliases =
    [
        ("apply_patch", "Apply Patch"),
        ("apply patch", "Apply Patch"),
        ("system.run", "system.run"),
        ("browser.proxy", "browser.proxy"),
        ("canvas.navigate", "canvas.navigate"),
        ("powershell", "PowerShell"),
        ("pwsh", "PowerShell"),
        ("bash", "Bash")
    ];

    private static readonly Regex CliFlagRegex =
        new(@"(?:^|\s)(?:--[a-z][\w-]*|-[a-zA-Z])(?=\s|=|$)",
            RegexOptions.Compiled);

    private static readonly Regex NumberedLineRegex =
        new(@"^\s*\d+\.\s", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex GrepResultRegex =
        new(@"^[^\s:]+\.\w+:\d+:", RegexOptions.Compiled | RegexOptions.Multiline);

    internal static NativeToolIdentity ExtractToolIdentity(JsonElement data)
    {
        var explicitName = GetStringProperty(data, "name", "toolName", "tool");
        if (!string.IsNullOrWhiteSpace(explicitName))
            return CanonicalizeToolIdentity(explicitName, ChatToolIdentityStrength.Explicit);

        var title = GetStringProperty(data, "title");
        if (TryGetTrustedToolTitleIdentity(title, out var known))
            return new NativeToolIdentity(known, ChatToolIdentityStrength.Specific);

        return new NativeToolIdentity("Tool", ChatToolIdentityStrength.Fallback);
    }

    internal static NativeToolIdentity CanonicalizeToolIdentity(
        string value,
        ChatToolIdentityStrength strength)
    {
        var sanitized = ExecApprovalCommandDisplaySanitizer.Sanitize(value).Trim();
        if (sanitized.Length > MaxIdentityChars)
            sanitized = sanitized[..MaxIdentityChars];

        if (TryGetKnownToolIdentity(sanitized, out var known))
        {
            return new NativeToolIdentity(
                known,
                ChatToolIdentityStrength.Specific > strength
                    ? ChatToolIdentityStrength.Specific
                    : strength);
        }

        if (sanitized.Length == 0
            || sanitized.Any(ch => !(IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' or ' ')))
        {
            return new NativeToolIdentity("Tool", ChatToolIdentityStrength.Fallback);
        }

        return new NativeToolIdentity(sanitized, strength);
    }

    internal static bool TryGetKnownToolIdentity(string? value, out string identity)
    {
        identity = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.TrimStart();
        foreach (var (prefix, canonical) in ToolAliases)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && (trimmed.Length == prefix.Length
                    || char.IsWhiteSpace(trimmed[prefix.Length])
                    || trimmed[prefix.Length] is ':' or '(' or '['))
            {
                identity = canonical;
                return true;
            }
        }
        return false;
    }

    internal static string? ExtractToolCorrelationId(JsonElement data)
    {
        if (data.TryGetProperty("toolCallId", out var toolCallIdValue)
            && toolCallIdValue.ValueKind == JsonValueKind.String)
        {
            var toolCallId = toolCallIdValue.GetString();
            if (!string.IsNullOrWhiteSpace(toolCallId))
                return toolCallId;
        }

        if (!data.TryGetProperty("itemId", out var itemIdValue)
            || itemIdValue.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var itemId = itemIdValue.GetString();
        if (string.IsNullOrWhiteSpace(itemId))
            return null;

        foreach (var prefix in new[] { "tool:", "command:", "patch:" })
        {
            if (!itemId.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            itemId = itemId[prefix.Length..];
            break;
        }

        return string.IsNullOrWhiteSpace(itemId) ? null : itemId;
    }

    internal static bool IsToolResultError(JsonElement data) =>
        data.TryGetProperty("isError", out var isError)
        && isError.ValueKind == JsonValueKind.True;

    internal static string GetStringProperty(JsonElement data, params string[] names)
    {
        foreach (var name in names)
        {
            if (data.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }
        return string.Empty;
    }

    internal static JsonObject? ExtractSafeToolDisplayArgs(JsonElement data)
    {
        var displayArgs = new JsonObject();
        AddSafeToolDisplayArgs(displayArgs, data);
        foreach (var containerName in new[] { "args", "details", "input" })
        {
            if (data.TryGetProperty(containerName, out var container)
                && container.ValueKind == JsonValueKind.Object)
            {
                AddSafeToolDisplayArgs(displayArgs, container);
            }
        }
        return displayArgs.Count == 0 ? null : displayArgs;
    }

    internal static string SanitizeToolDisplayValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var sanitized = ExecApprovalCommandDisplaySanitizer.Sanitize(value).Trim();
        if (sanitized.Length <= MaxDisplayValueChars)
            return sanitized;

        var end = MaxDisplayValueChars - 3;
        if (end > 0 && char.IsHighSurrogate(sanitized[end - 1]))
            end--;
        return sanitized[..end] + "...";
    }

    internal static string FirstToolDisplayValue(JsonObject? args)
    {
        if (args is null)
            return string.Empty;

        foreach (var key in DisplayArgumentKeys)
        {
            if (args[key] is JsonValue value
                && value.TryGetValue<string>(out var text)
                && !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }
        return string.Empty;
    }

    internal static string ExtractCommandOutputText(JsonElement data)
    {
        foreach (var key in new[] { "output", "text", "content", "stdout", "preview", "body", "stderr" })
        {
            if (data.TryGetProperty(key, out var value))
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrEmpty(text))
                        return TruncateToolOutput(text);
                }
                else if (value.ValueKind == JsonValueKind.Object
                         && value.TryGetProperty("text", out var inner)
                         && inner.ValueKind == JsonValueKind.String)
                {
                    var text = inner.GetString();
                    if (!string.IsNullOrEmpty(text))
                        return TruncateToolOutput(text);
                }
            }
        }

        return string.Empty;
    }

    internal static string ExtractToolLabel(JsonElement data, JsonObject? displayArgs)
    {
        var displayValue = FirstToolDisplayValue(displayArgs);
        if (!string.IsNullOrWhiteSpace(displayValue))
            return displayValue;
        return SanitizeToolDisplayValue(GetStringProperty(data, "name", "toolName", "title"));
    }

    internal static string ExtractToolResultText(JsonElement data, string fallback)
    {
        if (data.TryGetProperty("result", out var result))
        {
            if (result.ValueKind == JsonValueKind.String)
                return TruncateToolOutput(result.GetString() ?? "");
            if (result.ValueKind == JsonValueKind.Array)
            {
                var arrayText = ExtractTypedTextBlocks(result);
                if (!string.IsNullOrEmpty(arrayText))
                    return arrayText;
            }
            if (result.ValueKind == JsonValueKind.Object)
            {
                if (result.TryGetProperty("details", out var details)
                    && details.ValueKind == JsonValueKind.Object)
                {
                    var aggregated = GetStringProperty(details, "aggregated");
                    if (!string.IsNullOrEmpty(aggregated))
                        return TruncateToolOutput(aggregated);
                }

                if (result.TryGetProperty("content", out var resultContent)
                    && resultContent.ValueKind == JsonValueKind.String)
                {
                    return TruncateToolOutput(resultContent.GetString() ?? "");
                }

                if (result.TryGetProperty("content", out resultContent))
                {
                    var contentText = ExtractTypedTextBlocks(resultContent);
                    if (!string.IsNullOrEmpty(contentText))
                        return contentText;
                }

                var directText = ExtractTypedTextBlocks(result);
                if (!string.IsNullOrEmpty(directText))
                    return directText;
            }
        }

        foreach (var key in new[] { "output", "content", "text", "stdout" })
        {
            if (data.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrEmpty(text))
                    return TruncateToolOutput(text);
            }
        }
        return fallback;
    }

    internal static string ExtractToolErrorText(JsonElement data, string fallback)
    {
        if (data.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("details", out var details)
                && details.ValueKind == JsonValueKind.Object)
            {
                var detailsError = GetStringProperty(details, "error", "message", "reason");
                if (!string.IsNullOrEmpty(detailsError))
                    return TruncateToolOutput(detailsError);
            }

            var resultError = GetStringProperty(result, "error", "message", "reason");
            if (!string.IsNullOrEmpty(resultError))
                return TruncateToolOutput(resultError);
        }

        foreach (var key in new[] { "error", "message", "stderr", "summary" })
        {
            if (data.TryGetProperty(key, out var value))
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrEmpty(text))
                        return TruncateToolOutput(text);
                }
                else if (value.ValueKind == JsonValueKind.Object
                         && value.TryGetProperty("message", out var inner)
                         && inner.ValueKind == JsonValueKind.String)
                {
                    var text = inner.GetString();
                    if (!string.IsNullOrEmpty(text))
                        return TruncateToolOutput(text);
                }
            }
        }

        var resultText = ExtractToolResultText(data, string.Empty);
        if (!string.IsNullOrEmpty(resultText))
            return resultText;

        return fallback;
    }

    internal static string ExtractToolResultErrorText(JsonElement data)
    {
        var safeSummary = SanitizeToolDisplayValue(
            GetStringProperty(data, "toolErrorSummary"));
        return string.IsNullOrEmpty(safeSummary)
            ? ExtractToolErrorText(data, string.Empty)
            : safeSummary;
    }

    internal static bool HasSafeToolErrorSummary(JsonElement data) =>
        !string.IsNullOrEmpty(SanitizeToolDisplayValue(
            GetStringProperty(data, "toolErrorSummary")));

    internal static string ExtractPatchSummaryText(JsonElement data)
    {
        var summary = GetStringProperty(data, "summary");
        return string.IsNullOrEmpty(summary) ? string.Empty : TruncateToolOutput(summary);
    }

    private static string ExtractTypedTextBlocks(JsonElement content)
    {
        var builder = new StringBuilder();
        void AppendBlock(JsonElement block)
        {
            if (builder.Length > ToolOutputMaxChars
                || block.ValueKind != JsonValueKind.Object
                || !string.Equals(GetStringProperty(block, "type"), "text", StringComparison.Ordinal)
                || !block.TryGetProperty("text", out var textValue)
                || textValue.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var text = textValue.GetString()?.Trim();
            if (string.IsNullOrEmpty(text))
                return;

            if (builder.Length > 0)
                builder.Append('\n');
            var remaining = ToolOutputMaxChars + 1 - builder.Length;
            if (remaining > 0)
                builder.Append(text.AsSpan(0, Math.Min(text.Length, remaining)));
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
                AppendBlock(block);
        }
        else
        {
            AppendBlock(content);
        }

        return TruncateToolOutput(builder.ToString());
    }

    internal static bool LooksLikeSystemControlNote(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        var trimmed = text.TrimStart();
        var hasPrefix =
            trimmed.StartsWith("System (untrusted):", StringComparison.Ordinal)
            || trimmed.StartsWith("System:", StringComparison.Ordinal);
        if (!hasPrefix)
            return false;

        return trimmed.Contains("Exec completed (", StringComparison.Ordinal)
            || trimmed.Contains("Process exited with code", StringComparison.Ordinal)
            || trimmed.Contains("Command still running (session", StringComparison.Ordinal)
            || trimmed.Contains("An async command you ran", StringComparison.Ordinal)
            || trimmed.Contains("Tool reported", StringComparison.Ordinal)
            || trimmed.Contains("exec result for ", StringComparison.Ordinal)
            || trimmed.Contains("tool_call_", StringComparison.Ordinal)
            || trimmed.Contains("Reset session", StringComparison.Ordinal);
    }

    internal static bool LooksLikeFlattenedToolOutput(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 40)
            return false;

        if (text.Contains("Process exited with code", StringComparison.Ordinal))
            return true;
        if (text.Contains("Command still running (session", StringComparison.Ordinal))
            return true;
        if (text.Contains("Exec completed (", StringComparison.Ordinal))
            return true;

        var head = text.AsSpan(0, Math.Min(80, text.Length));
        if (head.StartsWith("\\\\wsl.localhost\\"))
            return true;
        if (head.StartsWith("/usr/")
            || head.StartsWith("/home/")
            || head.StartsWith("/var/")
            || head.StartsWith("/etc/")
            || head.StartsWith("/tmp/"))
        {
            return true;
        }

        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.StartsWith("OpenClaw 20")
            || trimmed.StartsWith("OpenClaw v")
            || trimmed.StartsWith("openclaw "))
        {
            return true;
        }

        if (text.Contains("Usage:", StringComparison.Ordinal)
            && (text.Contains("Options:", StringComparison.Ordinal)
                || text.Contains("Commands:", StringComparison.Ordinal)
                || text.Contains("Examples:", StringComparison.Ordinal)
                || text.Contains("Aliases:", StringComparison.Ordinal)))
        {
            return true;
        }

        if (text.Length >= 200)
        {
            var flagCount = 0;
            foreach (Match _ in CliFlagRegex.Matches(text))
            {
                if (++flagCount >= 5)
                    return true;
            }
        }

        return false;
    }

    internal static string ClassifyFlattenedToolOutput(string text)
    {
        if (TryGetKnownToolIdentity(text, out var known))
            return known;
        if (string.IsNullOrEmpty(text))
            return "Tool";

        if (text.Contains("Command still running", StringComparison.Ordinal)
            || text.Contains("Process exited with code", StringComparison.Ordinal))
        {
            return "bash";
        }

        if (NumberedLineRegex.IsMatch(text))
            return "view";

        if (GrepResultRegex.IsMatch(text))
            return "grep";

        if (text.Contains("Directory:", StringComparison.Ordinal)
            || text.Contains("Mode                ", StringComparison.Ordinal))
        {
            return "glob";
        }

        if (text.StartsWith("commit ", StringComparison.Ordinal)
            || text.StartsWith("diff --git", StringComparison.Ordinal)
            || text.Contains("Author:", StringComparison.Ordinal)
                && text.Contains("Date:", StringComparison.Ordinal))
        {
            return "git";
        }

        if (text.Contains("successfully created", StringComparison.OrdinalIgnoreCase)
            || text.Contains("File written", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Applied edit", StringComparison.OrdinalIgnoreCase))
        {
            return "edit";
        }

        if (text.Contains("Exec completed (", StringComparison.Ordinal))
            return "exec";

        return "Tool";
    }

    internal static string ExtractFlattenedToolSummary(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var firstLine = text.AsSpan().TrimStart();
        var lineEnd = firstLine.IndexOfAny('\r', '\n');
        if (lineEnd > 0)
            firstLine = firstLine[..lineEnd];
        return firstLine.Length > 80
            ? new string(firstLine[..77]) + "…"
            : new string(firstLine);
    }

    internal static ChatToolIdentityStrength ClassifyHistoryIdentityStrength(string toolName)
    {
        if (string.Equals(toolName, "Tool", StringComparison.Ordinal))
            return ChatToolIdentityStrength.Fallback;
        return TryGetKnownToolIdentity(toolName, out _)
            ? ChatToolIdentityStrength.Specific
            : ChatToolIdentityStrength.Heuristic;
    }

    private static bool TryGetTrustedToolTitleIdentity(string? value, out string identity)
    {
        identity = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim();
        foreach (var (alias, canonical) in ToolAliases)
        {
            if (!string.Equals(normalized, alias, StringComparison.OrdinalIgnoreCase))
                continue;
            identity = canonical;
            return true;
        }
        return false;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9';

    private static void AddSafeToolDisplayArgs(JsonObject target, JsonElement source)
    {
        foreach (var key in DisplayArgumentKeys)
        {
            if (!source.TryGetProperty(key, out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var sanitized = SanitizeToolDisplayValue(value.GetString());
            if (string.IsNullOrWhiteSpace(sanitized))
                continue;

            if (target[key] is JsonValue existing
                && existing.TryGetValue<string>(out var existingText)
                && !string.Equals(existingText, sanitized, StringComparison.Ordinal))
            {
                var combined = existingText + "\n" + sanitized;
                target[key] = combined.Length > 512
                    ? combined[..509] + "..."
                    : combined;
            }
            else
            {
                target[key] = sanitized;
            }
        }
    }

    internal static string TruncateToolOutput(string text)
    {
        if (text.Length <= ToolOutputMaxChars)
            return text;
        return text[..ToolOutputMaxChars] + "\n…(truncated)";
    }
}
