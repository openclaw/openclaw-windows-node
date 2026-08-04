using System.Text.Json.Nodes;
using System.Globalization;
using System.Text;
using OpenClaw.Chat;

namespace OpenClawTray.Chat;

public enum ChatToolActivityCategory
{
    Command,
    Read,
    Edit,
    Write,
    Search,
    Fetch,
    Generic,
}

public sealed record ChatToolActivityCount(
    ChatToolActivityCategory Category,
    int Count,
    IReadOnlyList<string>? ToolNames = null);

public sealed record ChatToolActivitySummary(
    IReadOnlyList<ChatToolActivityCount> Counts,
    int ToolCount,
    ChatTimelineItem? NewestRunningTool)
{
    public bool IsRunning => NewestRunningTool is not null;
}

public sealed record ChatToolActivityRow(
    string Key,
    ChatTimelineItem? Entry,
    IReadOnlyList<ChatTimelineItem> Tools,
    ChatToolActivitySummary? Summary)
{
    public bool IsActivityGroup => Tools.Count >= 2;
}

/// <summary>
/// WinUI-free chronological projection and summary policy for production chat tool activity.
/// </summary>
public static class ChatToolActivityPresentation
{
    private static readonly string[] s_pathPropertyNames =
    [
        "path", "file_path", "filePath", "filepath", "filename", "file", "notebook_path", "paths", "files",
    ];

    public static IReadOnlyList<ChatToolActivityRow> Project(
        IReadOnlyList<ChatTimelineItem> entries,
        string? sessionId,
        long timelineGeneration,
        bool showToolCalls = true)
    {
        var rows = new List<ChatToolActivityRow>(entries.Count);
        for (var index = 0; index < entries.Count;)
        {
            var entry = entries[index];
            if (entry.Kind != ChatTimelineItemKind.ToolCall)
            {
                rows.Add(Standalone(entry, sessionId, timelineGeneration));
                index++;
                continue;
            }

            var end = index + 1;
            while (end < entries.Count && entries[end].Kind == ChatTimelineItemKind.ToolCall)
                end++;

            if (!showToolCalls)
            {
                index = end;
                continue;
            }

            var count = end - index;
            if (count == 1)
            {
                rows.Add(Standalone(entry, sessionId, timelineGeneration));
            }
            else
            {
                var tools = entries.Skip(index).Take(count).ToArray();
                rows.Add(new ChatToolActivityRow(
                    ActivityKey(sessionId, timelineGeneration, tools[0].Id),
                    null,
                    tools,
                    Summarize(tools)));
            }

            index = end;
        }

        return rows;
    }

    public static string ActivityKey(string? sessionId, long timelineGeneration, string firstToolEntryId) =>
        $"thread:{sessionId ?? "none"}|generation:{timelineGeneration}|activity:{firstToolEntryId}";

    public static ChatToolActivitySummary Summarize(IReadOnlyList<ChatTimelineItem> tools)
    {
        var categoryCounts = new Dictionary<ChatToolActivityCategory, int>();
        var genericCount = 0;
        var genericNames = new List<string>();
        var genericNameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var knownPaths = new Dictionary<ChatToolActivityCategory, HashSet<string>>();
        var pathCategoryCalls = new Dictionary<ChatToolActivityCategory, int>();

        foreach (var tool in tools)
        {
            var category = Classify(tool.ToolName, tool.ToolArgs);
            if (category is ChatToolActivityCategory.Read
                or ChatToolActivityCategory.Edit
                or ChatToolActivityCategory.Write)
            {
                pathCategoryCalls[category] = pathCategoryCalls.GetValueOrDefault(category) + 1;
                var paths = ExtractPaths(tool.ToolArgs, tool.ToolName);
                if (paths.Count > 0)
                {
                    if (!knownPaths.TryGetValue(category, out var categoryPaths))
                    {
                        categoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        knownPaths[category] = categoryPaths;
                    }

                    categoryPaths.UnionWith(paths);
                }
            }
            else if (category == ChatToolActivityCategory.Generic)
            {
                genericCount++;
                var name = DisplayToolName(tool.ToolName);
                if (!string.IsNullOrWhiteSpace(name) && genericNameSet.Add(name))
                    genericNames.Add(name);
            }
            else
            {
                categoryCounts[category] = categoryCounts.GetValueOrDefault(category) + 1;
            }
        }

        foreach (var category in new[]
                 {
                     ChatToolActivityCategory.Read,
                     ChatToolActivityCategory.Edit,
                     ChatToolActivityCategory.Write,
                 })
        {
            var calls = pathCategoryCalls.GetValueOrDefault(category);
            var count = knownPaths.TryGetValue(category, out var paths) && paths.Count > 0
                ? paths.Count
                : calls;
            if (count > 0)
                categoryCounts[category] = count;
        }

        var counts = new List<ChatToolActivityCount>();
        foreach (var category in Enum.GetValues<ChatToolActivityCategory>())
        {
            if (category != ChatToolActivityCategory.Generic
                && categoryCounts.TryGetValue(category, out var count))
            {
                counts.Add(new ChatToolActivityCount(category, count));
            }
        }

        if (genericCount > 0)
        {
            counts.Add(new ChatToolActivityCount(
                ChatToolActivityCategory.Generic,
                genericCount,
                genericNames.ToArray()));
        }

        var newestRunning = tools.LastOrDefault(
            static tool => tool.ToolResult is null or ChatToolCallStatus.InProgress);
        return new ChatToolActivitySummary(
            counts,
            tools.Count,
            newestRunning);
    }

    public static ChatToolActivityCategory Classify(string? toolName, JsonObject? args = null)
    {
        var name = NormalizeToolName(toolName);
        if (Matches(name, "str_replace_editor", "str_replace_based_edit_tool"))
        {
            var command = ReadString(args, "command")?.Trim().ToLowerInvariant();
            return command switch
            {
                "view" => ChatToolActivityCategory.Read,
                "str_replace" or "insert" or "undo_edit" => ChatToolActivityCategory.Edit,
                "create" => ChatToolActivityCategory.Write,
                _ => ChatToolActivityCategory.Generic,
            };
        }
        if (Matches(name, "powershell", "bash", "shell", "exec", "execute", "command", "cmd",
                "terminal", "process", "system.run", "run_command", "runcommand", "run_terminal_cmd"))
            return ChatToolActivityCategory.Command;
        if (Matches(name, "read", "read_file", "readfile", "notebookread", "notebook_read",
                "view", "open_file", "get_file"))
            return ChatToolActivityCategory.Read;
        if (Matches(name, "edit", "edit_file", "editfile", "multiedit", "multi_edit",
                "notebookedit", "notebook_edit", "apply_patch", "applypatch", "patch", "replace"))
            return ChatToolActivityCategory.Edit;
        if (Matches(name, "write", "write_file", "writefile", "create_file"))
            return ChatToolActivityCategory.Write;
        if (Matches(name, "search", "web_search", "websearch", "grep", "rg", "ripgrep", "glob", "find",
                "ls", "list", "codebase_search", "code_search"))
            return ChatToolActivityCategory.Search;
        if (Matches(name, "fetch", "web_fetch", "webfetch", "http_get", "download", "url_fetch"))
            return ChatToolActivityCategory.Fetch;
        if (args is { Count: <= 3 } && ReadString(args, "command") is not null)
            return ChatToolActivityCategory.Command;
        return ChatToolActivityCategory.Generic;
    }

    public static IReadOnlyList<string> ExtractPaths(JsonObject? args, string? toolName = null)
    {
        if (args is null)
            return [];

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Matches(NormalizeToolName(toolName), "apply_patch", "applypatch", "patch"))
        {
            AddStructuredChangePaths(args, paths);
            var patchText = ReadString(args, "patch")
                ?? ReadString(args, "input")
                ?? ReadString(args, "diff");
            if (patchText is not null)
                AddPatchTextPaths(patchText, paths);
        }

        foreach (var propertyName in s_pathPropertyNames)
        {
            if (!args.TryGetPropertyValue(propertyName, out var node) || node is null)
                continue;
            AddPathValues(node, paths);
        }

        return paths.ToArray();
    }

    private static ChatToolActivityRow Standalone(
        ChatTimelineItem entry,
        string? sessionId,
        long timelineGeneration) =>
        new(
            entry.Kind == ChatTimelineItemKind.ToolCall
                ? ActivityKey(sessionId, timelineGeneration, entry.Id)
                : $"thread:{sessionId ?? "none"}|generation:{timelineGeneration}|kind:{entry.Kind}|id:{entry.Id}",
            entry,
            [],
            null);

    private static void AddPathValues(JsonNode node, ISet<string> paths)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is not null)
                    AddPathValues(child, paths);
            }
            return;
        }

        if (node is JsonValue value
            && value.TryGetValue<string>(out var path)
            && !string.IsNullOrWhiteSpace(path))
        {
            paths.Add(path.Trim());
        }
    }

    private static void AddStructuredChangePaths(JsonObject args, ISet<string> paths)
    {
        if (!args.TryGetPropertyValue("changes", out var changesNode)
            || changesNode is not JsonArray changes)
            return;

        foreach (var changeNode in changes)
        {
            if (changeNode is not JsonObject change)
                continue;

            var target = change["kind"] is JsonObject kind
                ? ReadString(kind, "move_path") ?? ReadString(kind, "movePath")
                : null;
            target ??= ReadString(change, "path");
            AddNormalizedPath(target, paths);
        }
    }

    private static void AddPatchTextPaths(string patchText, ISet<string> paths)
    {
        const int maxPatchCharacters = 2_000_000;
        var bounded = patchText.Length <= maxPatchCharacters
            ? patchText
            : patchText[..maxPatchCharacters];
        var lines = bounded.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        AddCodexPatchPaths(lines, paths);
        AddUnifiedDiffPaths(lines, paths);
    }

    private static void AddCodexPatchPaths(IReadOnlyList<string> lines, ISet<string> paths)
    {
        var insidePatch = false;
        string? pendingUpdate = null;

        void FlushUpdate()
        {
            AddNormalizedPath(pendingUpdate, paths);
            pendingUpdate = null;
        }

        foreach (var line in lines)
        {
            if (line == "*** Begin Patch")
            {
                insidePatch = true;
                continue;
            }
            if (!insidePatch)
                continue;
            if (line == "*** End Patch")
            {
                FlushUpdate();
                break;
            }
            if (line.StartsWith("*** Update File: ", StringComparison.Ordinal))
            {
                FlushUpdate();
                pendingUpdate = line["*** Update File: ".Length..];
            }
            else if (line.StartsWith("*** Move to: ", StringComparison.Ordinal)
                     && pendingUpdate is not null)
            {
                pendingUpdate = line["*** Move to: ".Length..];
            }
            else if (line.StartsWith("*** Add File: ", StringComparison.Ordinal))
            {
                FlushUpdate();
                AddNormalizedPath(line["*** Add File: ".Length..], paths);
            }
            else if (line.StartsWith("*** Delete File: ", StringComparison.Ordinal))
            {
                FlushUpdate();
                AddNormalizedPath(line["*** Delete File: ".Length..], paths);
            }
        }

        FlushUpdate();
    }

    private static void AddUnifiedDiffPaths(IReadOnlyList<string> lines, ISet<string> paths)
    {
        string? blockOld = null;
        string? blockNew = null;
        string? headerOld = null;
        string? headerNew = null;

        void FlushBlock()
        {
            AddNormalizedPath(
                string.Equals(headerNew, "/dev/null", StringComparison.Ordinal)
                    ? headerOld ?? blockOld
                    : headerNew ?? blockNew,
                paths);
            blockOld = blockNew = headerOld = headerNew = null;
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                FlushBlock();
                var (oldPath, newPath) = ParseGitDiffPathPair(line["diff --git ".Length..]);
                if (oldPath is not null && newPath is not null)
                {
                    blockOld = StripDiffPrefix(oldPath);
                    blockNew = StripDiffPrefix(newPath);
                }
                continue;
            }

            if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                blockNew = ParseGitMetadataPath(line["rename to ".Length..]);
                continue;
            }

            if (line.StartsWith("copy to ", StringComparison.Ordinal))
            {
                blockNew = ParseGitMetadataPath(line["copy to ".Length..]);
                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal)
                && index + 1 < lines.Count
                && lines[index + 1].StartsWith("+++ ", StringComparison.Ordinal))
            {
                var oldPath = ParseUnifiedHeaderPath(line[4..]);
                var newPath = ParseUnifiedHeaderPath(lines[++index][4..]);
                if (blockOld is not null || blockNew is not null)
                {
                    headerOld = oldPath;
                    headerNew = newPath;
                }
                else
                {
                    AddNormalizedPath(
                        string.Equals(newPath, "/dev/null", StringComparison.Ordinal)
                            ? oldPath
                            : newPath,
                        paths);
                }
            }
        }

        FlushBlock();
    }

    private static string? ParseUnifiedHeaderPath(string value)
    {
        var path = ParseGitMetadataPath(value.Split('\t', 2)[0]);
        if (path.Length == 0)
            return null;
        return string.Equals(path, "/dev/null", StringComparison.Ordinal)
            ? path
            : StripDiffPrefix(path);
    }

    private static (string? OldPath, string? NewPath) ParseGitDiffPathPair(string value)
    {
        var offset = 0;

        string? ReadPath()
        {
            while (offset < value.Length && value[offset] == ' ')
                offset++;
            if (offset >= value.Length)
                return null;

            if (value[offset] != '"')
            {
                var start = offset;
                while (offset < value.Length && value[offset] != ' ')
                    offset++;
                return value[start..offset];
            }

            var path = new StringBuilder();
            offset++;
            var escaped = false;
            while (offset < value.Length)
            {
                var character = value[offset++];
                if (!escaped && character == '"')
                    return path.ToString();
                if (!escaped && character == '\\')
                {
                    escaped = true;
                    path.Append(character);
                    continue;
                }
                escaped = false;
                path.Append(character);
            }
            return null;
        }

        return (ReadPath(), ReadPath());
    }

    private static string ParseGitMetadataPath(string value)
    {
        var path = value.Trim();
        return path.Length >= 2 && path[0] == '"' && path[^1] == '"'
            ? path[1..^1]
            : path;
    }

    private static string StripDiffPrefix(string path) =>
        path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal)
            ? path[2..]
            : path;

    private static void AddNormalizedPath(string? path, ISet<string> paths)
    {
        if (string.IsNullOrWhiteSpace(path)
            || string.Equals(path.Trim(), "/dev/null", StringComparison.Ordinal))
            return;
        paths.Add(path.Trim());
    }

    private static string NormalizeToolName(string? toolName)
    {
        var name = (toolName ?? string.Empty).Trim().ToLowerInvariant();
        foreach (var prefix in new[] { "functions.", "tools.", "mcp." })
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                name = name[prefix.Length..];
        }
        return name.Replace('-', '_');
    }

    private static bool Matches(string name, params string[] aliases) =>
        aliases.Any(alias =>
            string.Equals(name, alias, StringComparison.Ordinal)
            || name.EndsWith($".{alias}", StringComparison.Ordinal)
            || name.EndsWith($"_{alias}", StringComparison.Ordinal));

    private static string? DisplayToolName(string? toolName)
    {
        var name = (toolName ?? string.Empty).Trim();
        if (name.Length == 0)
            return null;
        var separator = Math.Max(name.LastIndexOf('.'), name.LastIndexOf('/'));
        return separator >= 0 && separator + 1 < name.Length ? name[(separator + 1)..] : name;
    }

    private static string? ReadString(JsonObject? args, string propertyName) =>
        args?.TryGetPropertyValue(propertyName, out var node) == true
        && node is JsonValue value
        && value.TryGetValue<string>(out var text)
        && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}

public sealed record ChatToolActivityFormatTemplates(
    string CommandOne,
    string CommandMany,
    string ReadOne,
    string ReadMany,
    string EditOne,
    string EditMany,
    string WriteOne,
    string WriteMany,
    string SearchOne,
    string SearchMany,
    string FetchOne,
    string FetchMany,
    string GenericOne,
    string GenericMany,
    string GenericNamed,
    string GenericNamedRepeated,
    string Running,
    string ToolFallback);

/// <summary>Pure localized-template formatter matching the web tool-group summary policy.</summary>
public static class ChatToolActivityFormatter
{
    public static string Format(
        ChatToolActivitySummary summary,
        ChatToolActivityFormatTemplates templates)
    {
        if (summary.NewestRunningTool is { } running)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                templates.Running,
                string.IsNullOrWhiteSpace(running.ToolName)
                    ? templates.ToolFallback
                    : running.ToolName);
        }

        var segments = summary.Counts.Select(count => FormatCount(count, templates)).ToArray();
        var label = string.Join(", ", segments);
        if (label.Length == 0)
            return string.Empty;

        return char.ToUpper(label[0], CultureInfo.CurrentCulture) + label[1..];
    }

    private static string FormatCount(
        ChatToolActivityCount count,
        ChatToolActivityFormatTemplates templates)
    {
        if (count.Category == ChatToolActivityCategory.Generic)
        {
            var names = count.ToolNames ?? [];
            if (names.Count is > 0 and <= 2)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    count.Count > names.Count
                        ? templates.GenericNamedRepeated
                        : templates.GenericNamed,
                    count.Count,
                    string.Join(", ", names));
            }
        }

        var template = (count.Category, count.Count == 1) switch
        {
            (ChatToolActivityCategory.Command, true) => templates.CommandOne,
            (ChatToolActivityCategory.Command, false) => templates.CommandMany,
            (ChatToolActivityCategory.Read, true) => templates.ReadOne,
            (ChatToolActivityCategory.Read, false) => templates.ReadMany,
            (ChatToolActivityCategory.Edit, true) => templates.EditOne,
            (ChatToolActivityCategory.Edit, false) => templates.EditMany,
            (ChatToolActivityCategory.Write, true) => templates.WriteOne,
            (ChatToolActivityCategory.Write, false) => templates.WriteMany,
            (ChatToolActivityCategory.Search, true) => templates.SearchOne,
            (ChatToolActivityCategory.Search, false) => templates.SearchMany,
            (ChatToolActivityCategory.Fetch, true) => templates.FetchOne,
            (ChatToolActivityCategory.Fetch, false) => templates.FetchMany,
            (_, true) => templates.GenericOne,
            _ => templates.GenericMany,
        };
        return string.Format(CultureInfo.CurrentCulture, template, count.Count);
    }
}

/// <summary>
/// Pure explicit-override store. A collapse-version change clears user choices.
/// </summary>
public sealed class ChatToolActivityExpansionState
{
    private readonly Dictionary<string, bool> _overrides = new(StringComparer.Ordinal);
    private int _collapseVersion;

    public bool IsExpanded(string key, ChatToolActivitySummary summary, int collapseVersion)
    {
        ResetIfNeeded(collapseVersion);
        return _overrides.TryGetValue(key, out var value) && value;
    }

    public void SetExplicit(string key, bool isExpanded, int collapseVersion)
    {
        ResetIfNeeded(collapseVersion);
        _overrides[key] = isExpanded;
    }

    private void ResetIfNeeded(int collapseVersion)
    {
        if (_collapseVersion == collapseVersion)
            return;
        _collapseVersion = collapseVersion;
        _overrides.Clear();
    }
}
