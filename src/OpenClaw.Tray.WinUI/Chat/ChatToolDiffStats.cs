// Pure, WinUI-free line counts ("+N -M") for edit, write and patch tool rows, matching the web
// chat's tool-call view. Inputs are the row's redacted disclosure input and bounded result facts.
// Usage: if (ChatToolDiffStats.Resolve(entry) is { } stat) show $"+{stat.Added} -{stat.Removed}";
//        result facts come from OpenClaw.Shared.ChatToolResultDiffFacts for live and history results.
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenClaw.Chat;
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

/// <summary>
/// Resolves exact line counts for a tool row, or null when they are unknown. Sources, in order:
/// a complete <c>details.diff</c>, edit old/new text pairs, patch hunks, then new-file write content.
/// Redacted, cut, oversized or unparseable input yields null rather than an approximate count.
/// </summary>
internal static class ChatToolDiffStats
{
    // Bounds match the web view so both clients show the same counts or none.
    private const int MaxDiffInputLines = 600;
    private const int MaxLocalDiffPairs = 8;
    private const int MaxLocalDiffInputChars = 120_000;

    private static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.CultureInvariant);
    private static readonly Regex CodexPatch = new(@"(?:^|\n)\s*\*\*\* (?:Begin Patch|Update File:|Add File:|Delete File:)", RegexOptions.CultureInvariant);
    private static readonly Regex CodexFileHeader = new(@"^\*\*\* (Update|Add|Delete) File: (.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex GitHeader = new(@"^diff --git a/(.+) b/(.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex GitMetadata = new(@"^index |^new file mode |^deleted file mode |^similarity index ", RegexOptions.CultureInvariant);

    // Rows re-render often; parse the disclosure input and diff its texts once per row outcome.
    private static readonly ConditionalWeakTable<ChatToolInputDetails, StatCache> s_cache = new();

    private sealed class StatCache
    {
        internal string? CategoryToolName;
        internal ChatToolActivityCategory? Category;
        internal (string? ToolName, ChatToolResultDiffFacts? Facts)? FinalKey;
        internal ChatToolDiffStat? Final;
    }

    /// <summary>
    /// The counts a row shows: final counts after success, supplied live counts while an edit or
    /// write runs, and nothing for failed, interrupted or other tools.
    /// </summary>
    internal static ChatToolDiffStat? Resolve(ChatTimelineItem entry)
    {
        if (entry.Kind != ChatTimelineItemKind.ToolCall)
            return null;
        if (entry.ToolInputDetails is not { } details)
        {
            var displayCategory = ChatToolActivityPresentation.Classify(entry.ToolName, entry.ToolArgs);
            return displayCategory is ChatToolActivityCategory.Edit or ChatToolActivityCategory.Write
                ? Outcome(entry, () => Final(entry.ToolName, displayCategory, null, entry.ToolResultDiffFacts))
                : null;
        }
        var cache = s_cache.GetValue(details, static _ => new StatCache());
        lock (cache)
        {
            if (cache.Category is null || cache.CategoryToolName != entry.ToolName)
            {
                cache.Category = ChatToolActivityPresentation.Classify(entry.ToolName,
                    NativeToolProjector.ReadInputArguments(details) ?? entry.ToolArgs);
                cache.CategoryToolName = entry.ToolName;
                cache.FinalKey = null;
            }
            var category = cache.Category.Value;
            if (category is not (ChatToolActivityCategory.Edit or ChatToolActivityCategory.Write))
                return null;
            return Outcome(entry, () =>
            {
                var key = (entry.ToolName, entry.ToolResultDiffFacts);
                if (cache.FinalKey != key)
                {
                    // Redacted or cut input no longer holds the tool's lines; only result facts remain exact.
                    var args = details.Lossy ? null : ReadToolArguments(details, entry.ToolName);
                    cache.Final = Final(entry.ToolName, category, args, entry.ToolResultDiffFacts);
                    cache.FinalKey = key;
                }
                return cache.Final;
            });
        }
    }

    private static ChatToolDiffStat? Outcome(ChatTimelineItem entry, Func<ChatToolDiffStat?> final) => entry.ToolResult switch
    {
        null or ChatToolCallStatus.InProgress => entry.ToolLiveDiff,
        ChatToolCallStatus.Success => final(),
        _ => null,
    };

    private static ChatToolDiffStat? Final(
        string? toolName, ChatToolActivityCategory category, JsonObject? args, ChatToolResultDiffFacts? facts)
    {
        var name = NormalizeToolName(toolName);
        // A details diff is authoritative even when truncated; never fall back to arguments then.
        if (facts is { HasDiff: true })
            return facts.Added is { } added && facts.Removed is { } removed ? new(added, removed) : null;
        if (IsPatchTool(name))
            return args is null ? null : PatchStat(args);
        if (args is null)
            return null;
        var editorCommand = IsTextEditorTool(name) ? ReadString(args, "command")?.Trim().ToLowerInvariant() : null;
        if (category == ChatToolActivityCategory.Edit)
            return editorCommand is "insert" or "undo_edit" ? null : EditStat(args);
        if (facts?.Changed == false)
            return null;
        var content = ReadString(args, editorCommand == "create" ? "file_text" : "content");
        if (content is null || Unreliable(content))
            return null;
        // Generic writes can overwrite; only creation facts or a create-only command prove zero removals.
        return facts?.Created == true || editorCommand == "create" ? new(SplitLines(content).Count, 0) : null;
    }

    private static ChatToolDiffStat? EditStat(JsonObject args)
    {
        var edits = args["edits"] is JsonArray array ? array.ToArray() : [args];
        var inputChars = 0;
        long added = 0, removed = 0;
        var pairs = 0;
        for (var index = 0; index < edits.Length; index++)
        {
            if (index >= MaxLocalDiffPairs)
                return null;
            if (edits[index] is not JsonObject edit)
                continue;
            var oldText = ReadText(edit, "oldText", "old_string", "oldString", "old_str");
            var newText = ReadText(edit, "newText", "new_string", "newString", "new_str");
            if (oldText is null || newText is null)
                continue;
            if (Unreliable(oldText) || Unreliable(newText))
                return null;
            inputChars += oldText.Length + newText.Length;
            if (inputChars > MaxLocalDiffInputChars || LineDiff(oldText, newText) is not { } stat)
                return null;
            added += stat.Added;
            removed += stat.Removed;
            pairs++;
        }
        return pairs == 0 ? null : new(added, removed);
    }

    /// <summary>Counts by longest common subsequence; null when either side exceeds the bound.</summary>
    internal static ChatToolDiffStat? LineDiff(string oldText, string newText)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        if (oldLines.SequenceEqual(newLines, StringComparer.Ordinal))
            return new(0, 0);
        if (oldLines.Count > MaxDiffInputLines || newLines.Count > MaxDiffInputLines)
            return null;
        var prefix = 0;
        while (prefix < oldLines.Count && prefix < newLines.Count &&
            string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
            prefix++;
        var suffix = 0;
        while (suffix < oldLines.Count - prefix && suffix < newLines.Count - prefix &&
            string.Equals(oldLines[^(suffix + 1)], newLines[^(suffix + 1)], StringComparison.Ordinal))
            suffix++;
        var oldCount = oldLines.Count - prefix - suffix;
        var newCount = newLines.Count - prefix - suffix;
        var previous = new int[newCount + 1];
        var current = new int[newCount + 1];
        for (var i = 1; i <= oldCount; i++)
        {
            for (var j = 1; j <= newCount; j++)
                current[j] = string.Equals(oldLines[prefix + i - 1], newLines[prefix + j - 1], StringComparison.Ordinal)
                    ? previous[j - 1] + 1
                    : Math.Max(previous[j], current[j - 1]);
            (previous, current) = (current, previous);
        }
        var common = previous[newCount];
        return new(newCount - common, oldCount - common);
    }

    private static ChatToolDiffStat? PatchStat(JsonObject args)
    {
        if (args["changes"] is JsonArray changes && StructuredPatchStat(changes) is { } structured)
            return structured;
        var text = ReadString(args, "patch") ?? ReadString(args, "input") ?? ReadString(args, "diff");
        if (text is null || Unreliable(text))
            return null;
        return CodexPatch.IsMatch(text) ? CodexPatchStat(SplitLines(text)) : UnifiedPatchStat(SplitLines(text));
    }

    private static ChatToolDiffStat? StructuredPatchStat(JsonArray changes)
    {
        long added = 0, removed = 0;
        var sections = 0;
        foreach (var node in changes)
        {
            if (node is not JsonObject change || ReadString(change, "path") is null)
                continue;
            sections++;
            var kind = change["kind"] is JsonObject kindObject ? ReadString(kindObject, "type") : ReadString(change, "kind");
            if (change["stat"] is JsonObject stat &&
                ReadCount(stat, "added") is { } exactAdded && ReadCount(stat, "removed") is { } exactRemoved)
            {
                added += exactAdded;
                removed += exactRemoved;
                continue;
            }
            if (ReadText(change, "diff") is not { } diff)
                return null;
            if (Unreliable(diff))
                return null;
            var lines = SplitLines(diff);
            if (kind == "add") added += lines.Count;
            else if (kind == "delete") removed += lines.Count;
            else
            {
                var counter = new HunkCounter();
                foreach (var line in lines)
                    counter.Count(line, allowUnnumbered: false);
                added += counter.Added;
                removed += counter.Removed;
            }
        }
        return sections == 0 ? null : new(added, removed);
    }

    private static ChatToolDiffStat? CodexPatchStat(IReadOnlyList<string> lines)
    {
        string? operation = null;
        var sections = 0;
        var counter = new HunkCounter();
        long added = 0;
        foreach (var raw in lines)
        {
            var structural = operation == "update" ? raw.TrimEnd() : raw.Trim();
            var header = CodexFileHeader.Match(structural);
            if (header.Success)
            {
                operation = header.Groups[1].Value.ToLowerInvariant();
                // Delete sections carry no old contents, so their removal count is unknown.
                if (operation == "delete")
                    return null;
                counter.Reset();
                sections++;
                continue;
            }
            if (operation is null || structural.StartsWith("*** ", StringComparison.Ordinal))
                continue;
            if (operation == "add" && raw.StartsWith('+'))
                added++;
            else if (operation == "update")
                counter.Count(raw, allowUnnumbered: true);
        }
        return sections == 0 ? null : new(added + counter.Added, counter.Removed);
    }

    private static ChatToolDiffStat? UnifiedPatchStat(IReadOnlyList<string> lines)
    {
        var hasSection = false;
        var counter = new HunkCounter();
        for (var index = 0; index < lines.Count; index++)
        {
            var raw = lines[index];
            if (GitHeader.IsMatch(raw))
            {
                hasSection = true;
                counter.Reset();
                continue;
            }
            if (!counter.InHunk && raw.StartsWith("--- ", StringComparison.Ordinal) &&
                index + 1 < lines.Count && lines[index + 1].StartsWith("+++ ", StringComparison.Ordinal))
            {
                hasSection = true;
                index++;
                continue;
            }
            if (GitMetadata.IsMatch(raw))
                continue;
            if (hasSection)
                counter.Count(raw, allowUnnumbered: false);
        }
        return hasSection ? new(counter.Added, counter.Removed) : null;
    }

    /// <summary>Counts hunk lines; numbered hunks stop at their declared lengths, as on the web.</summary>
    internal sealed class HunkCounter
    {
        private bool _inHunk;
        private long? _oldLeft;
        private long? _newLeft;
        internal long Added { get; private set; }
        internal long Removed { get; private set; }
        internal bool InHunk => _inHunk;

        internal void Reset() => _inHunk = false;

        internal void Count(string raw, bool allowUnnumbered)
        {
            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                var match = HunkHeader.Match(raw);
                _inHunk = true;
                _oldLeft = match.Success ? Length(match.Groups[2]) : null;
                _newLeft = match.Success ? Length(match.Groups[4]) : null;
                return;
            }
            if (!_inHunk && !allowUnnumbered)
                return;
            var kind = raw.Length == 0 ? (allowUnnumbered ? ' ' : '\0') : raw[0];
            if (kind is not ('+' or '-' or ' '))
                return;
            if (kind == '+') Added++;
            if (kind == '-') Removed++;
            if (!_inHunk)
                return;
            if (kind != '+' && _oldLeft is { } oldLeft) _oldLeft = Math.Max(0, oldLeft - 1);
            if (kind != '-' && _newLeft is { } newLeft) _newLeft = Math.Max(0, newLeft - 1);
            if (_oldLeft == 0 && _newLeft == 0)
                _inHunk = false;
        }

        private static long Length(Group group) =>
            group.Success && long.TryParse(group.Value, out var value) ? value : 1;
    }

    /// <summary>
    /// Cut text no longer holds the tool's lines. Redaction is tracked separately by
    /// <see cref="ChatToolInputDetails.Lossy"/>, because mask shapes also occur in ordinary text.
    /// </summary>
    internal static bool Unreliable(string text) => NativeToolProjector.IsCutDisplayText(text);

    private static List<string> SplitLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.Length == 0)
            return [];
        var lines = normalized.Split('\n').ToList();
        if (lines.Count > 1 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    internal static string NormalizeToolName(string? toolName)
    {
        var name = (toolName ?? string.Empty).Trim().ToLowerInvariant();
        foreach (var prefix in new[] { "functions.", "tools.", "mcp." })
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                name = name[prefix.Length..];
        return name.Replace('-', '_').Replace(' ', '_');
    }

    internal static bool IsPatchTool(string normalizedName) =>
        normalizedName is "apply_patch" or "applypatch" or "patch" ||
        normalizedName.EndsWith("_apply_patch", StringComparison.Ordinal);

    /// <summary>
    /// The tool's arguments from its disclosure input. A patch tool may receive the patch itself as free-form
    /// text rather than a JSON object; like the row title, that text is read as the patch <c>input</c>.
    /// </summary>
    internal static JsonObject? ReadToolArguments(ChatToolInputDetails? details, string? toolName)
    {
        if (NativeToolProjector.ReadInputArguments(details) is { } args)
            return args;
        return details is { Unavailable: false, Text: { } raw } && !string.IsNullOrWhiteSpace(raw) &&
            !raw.TrimStart().StartsWith('{') && IsPatchTool(NormalizeToolName(toolName))
                ? new JsonObject { ["input"] = raw } : null;
    }

    private static bool IsTextEditorTool(string normalizedName) =>
        normalizedName is "str_replace_editor" or "str_replace_based_edit_tool";

    private static string? ReadText(JsonObject source, params string[] keys)
    {
        foreach (var key in keys)
            if (source[key] is JsonValue value && value.TryGetValue<string>(out var text))
                return text;
        return null;
    }

    private static string? ReadString(JsonObject source, string key) =>
        ReadText(source, key) is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static long? ReadCount(JsonObject source, string key) =>
        source[key] is JsonValue value && value.TryGetValue<double>(out var number) &&
        number >= 0 && double.IsFinite(number) ? (long)Math.Truncate(number) : null;
}
