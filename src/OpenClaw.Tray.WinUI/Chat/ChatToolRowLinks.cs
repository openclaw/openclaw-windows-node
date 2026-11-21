// Pure, WinUI-free targets for clickable tool rows, matching the web chat's file and subagent links.
// Usage: if (ChatToolRowLinks.WorkspaceFilePath(entry) is { } path) open it with sessions.files.get;
//        if (ChatToolRowLinks.IsOpenableSubagent(threads, parent, row.Subagent.ChildSessionKey)) select that session.
// Inputs are untrusted tool arguments and results. Returned values are request identities only;
// the Gateway still authorizes and resolves them.
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenClaw.Chat;

namespace OpenClawTray.Chat;

internal static class ChatToolRowLinks
{
    private const int MaxLinkPathChars = 4096;
    private static readonly string[] PathKeys = ["path", "file_path", "filePath", "notebook_path"];
    private static readonly Regex CodexPatch = new(@"(?:^|\n)\s*\*\*\* (?:Begin Patch|Update File:|Add File:|Delete File:)", RegexOptions.CultureInvariant);
    private static readonly Regex CodexFileHeader = new(@"^\*\*\* (Update|Add|Delete) File: (.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex CodexMove = new(@"^\*\*\* Move to: (.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex GitHeader = new(@"^diff --git a/(.+) b/(.+)$", RegexOptions.CultureInvariant);

    // Rows re-render often; parse each row's disclosure input once per tool identity.
    private static readonly ConditionalWeakTable<ChatToolInputDetails, PathCache> s_pathCache = new();

    private sealed class PathCache
    {
        internal (string? ToolName, JsonObject? ToolArgs)? Key;
        internal string? Value;
    }

    /// <summary>
    /// The workspace-relative file a read, edit, write or single-file patch row names. Null for other tools,
    /// deletes, moves, multi-file patches, paths that sanitization altered, and paths that are rooted or
    /// escape the workspace: the Gateway may read such paths when host reads are allowed.
    /// </summary>
    internal static string? WorkspaceFilePath(ChatTimelineItem entry)
    {
        if (entry.Kind != ChatTimelineItemKind.ToolCall || entry.ToolName == "sessions_yield")
            return null;
        if (entry.ToolInputDetails is not { } details)
            return ComputeWorkspaceFilePath(entry.ToolName, null, entry.ToolArgs);
        var cache = s_pathCache.GetValue(details, static _ => new PathCache());
        lock (cache)
        {
            var key = (entry.ToolName, entry.ToolArgs);
            if (cache.Key is not { } cached || cached.ToolName != key.ToolName || !ReferenceEquals(cached.ToolArgs, key.ToolArgs))
            {
                cache.Value = ComputeWorkspaceFilePath(entry.ToolName, details, entry.ToolArgs);
                cache.Key = key;
            }
            return cache.Value;
        }
    }

    private static string? ComputeWorkspaceFilePath(string? toolName, ChatToolInputDetails? details, JsonObject? displayArgs)
    {
        var args = ChatToolDiffStats.ReadToolArguments(details, toolName);
        var category = ChatToolActivityPresentation.Classify(toolName, args ?? displayArgs);
        if (category is not (ChatToolActivityCategory.Read or ChatToolActivityCategory.Edit or ChatToolActivityCategory.Write))
            return null;
        if (ChatToolDiffStats.IsPatchTool(ChatToolDiffStats.NormalizeToolName(toolName)))
        {
            // A patch names its files in its operations; a plain path argument must not bypass them.
            // Deletes remove their target, moves leave the old path empty, and aggregates name no single file.
            return args is not null && PatchOperations(args) is [{ Operation: not "delete", MovedFrom: null } single]
                ? WorkspaceRelativePath(single.Path) : null;
        }
        foreach (var source in new[] { args, displayArgs })
            foreach (var key in PathKeys)
                if (source?[key] is JsonValue value && value.TryGetValue<string>(out var text) &&
                    WorkspaceRelativePath(text) is { } path)
                    return path;
        return null;
    }

    /// <summary>
    /// Normalizes a workspace-relative path. Null for rooted, drive, UNC, home and URI-like paths, paths
    /// whose <c>..</c> segments leave the workspace, and paths display sanitization changed.
    /// </summary>
    internal static string? WorkspaceRelativePath(string path)
    {
        if (!UsablePath(path))
            return null;
        var normalized = path.Trim().Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.StartsWith('~') || normalized.Contains(':'))
            return null;
        var segments = new List<string>();
        foreach (var segment in normalized.Split('/'))
        {
            if (segment is "" or ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count == 0)
                    return null;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    /// <summary>A direct, unarchived subagent of <paramref name="parentSession"/>, as the Subagents pill lists.</summary>
    internal static bool IsOpenableSubagent(IEnumerable<ChatThread> threads, string parentSession, string key) =>
        key != parentSession && threads.Any(thread => thread.IsSubagent && !thread.IsArchived &&
            thread.ParentSessionKey == parentSession && string.Equals(thread.Id, key, StringComparison.Ordinal));

    // Display sanitization escapes invisible characters, joins duplicates with a newline, cuts long values
    // and masks secrets (short ones as "***", long ones as "first6…last4", PEM blocks with "…redacted…");
    // any of those would request a different file than the tool used.
    private static bool UsablePath(string path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= MaxLinkPathChars &&
        !path.Contains('\n') && !path.Contains("\\u{", StringComparison.Ordinal) &&
        !path.EndsWith("...", StringComparison.Ordinal) && !path.Contains('…') &&
        !path.Contains("***", StringComparison.Ordinal) && !NativeToolProjector.IsCutDisplayText(path);

    private sealed record PatchOperation(string Operation, string Path, string? MovedFrom);

    private static List<PatchOperation>? PatchOperations(JsonObject args)
    {
        var operations = new List<PatchOperation>();
        if (args["changes"] is JsonArray changes)
        {
            foreach (var node in changes)
            {
                if (node is not JsonObject change || ReadString(change, "path")?.Trim() is not { } path)
                    continue;
                var kindObject = change["kind"] as JsonObject;
                var kind = kindObject is not null ? ReadString(kindObject, "type") : ReadString(change, "kind");
                var operation = kind is "add" or "delete" ? kind : "update";
                var moved = kindObject is null ? null
                    : (ReadString(kindObject, "move_path") ?? ReadString(kindObject, "movePath"))?.Trim();
                operations.Add(moved is not null && moved != path
                    ? new(operation, moved, path) : new(operation, path, null));
            }
            if (operations.Count > 0)
                return operations;
        }
        var text = ReadString(args, "patch") ?? ReadString(args, "input") ?? ReadString(args, "diff");
        if (text is null || NativeToolProjector.IsCutDisplayText(text))
            return null;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var codex = CodexPatch.IsMatch(text);
        var awaitingGitHeaders = false;
        var hunk = new ChatToolDiffStats.HunkCounter();
        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index];
            if (codex)
            {
                // Update sections keep leading whitespace: indented context is not a file header.
                var structural = operations is [.., { Operation: "update" }] ? raw.TrimEnd() : raw.Trim();
                if (CodexFileHeader.Match(structural) is { Success: true } header)
                    operations.Add(new(header.Groups[1].Value.ToLowerInvariant(), header.Groups[2].Value.Trim(), null));
                else if (operations is [.., { Operation: "update" } last] && CodexMove.Match(structural) is { Success: true } move)
                    operations[^1] = last with { Path = move.Groups[1].Value.Trim(), MovedFrom = last.Path };
                continue;
            }
            if (GitHeader.Match(raw) is { Success: true } git)
            {
                operations.Add(new("update", git.Groups[2].Value,
                    git.Groups[1].Value != git.Groups[2].Value ? git.Groups[1].Value : null));
                awaitingGitHeaders = true;
                hunk.Reset();
                continue;
            }
            if (raw.StartsWith("@@", StringComparison.Ordinal))
                awaitingGitHeaders = false;
            // Inside a hunk, "--- x" / "+++ y" are a removed and an added line, not file headers.
            if (!hunk.InHunk && raw.StartsWith("--- ", StringComparison.Ordinal) && index + 1 < lines.Length &&
                lines[index + 1].StartsWith("+++ ", StringComparison.Ordinal))
            {
                var oldPath = UnifiedPath(raw[4..]);
                var newPath = UnifiedPath(lines[index + 1][4..]);
                var operation = oldPath == "/dev/null" ? "add" : newPath == "/dev/null" ? "delete" : "update";
                var target = newPath == "/dev/null" ? oldPath : newPath;
                PatchOperation parsed = new(operation, target,
                    operation == "update" && oldPath != newPath ? oldPath : null);
                // A git header already opened this file's operation.
                if (awaitingGitHeaders)
                    operations[^1] = parsed;
                else
                    operations.Add(parsed);
                awaitingGitHeaders = false;
                index++;
                continue;
            }
            if (operations.Count > 0)
                hunk.Count(raw, allowUnnumbered: false);
        }
        return operations.Count > 0 ? operations : null;
    }

    private static string UnifiedPath(string raw)
    {
        var path = raw.Split('\t', 2)[0].Trim();
        return path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal)
            ? path[2..] : path;
    }

    private static string? ReadString(JsonObject source, string key) =>
        source[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text : null;
}
