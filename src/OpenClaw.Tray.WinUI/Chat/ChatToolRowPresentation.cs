// WinUI-free projection of one tool call into the facts its collapsed row shows, ported
// from the web resolveToolCallView, ToolRowContent and resolveSpawnedSubagent. Example:
//   var view = ChatToolRowPresentation.Resolve(entry, childThreads);
//   // read_file { path: "skills/telegram/SKILL.md" } -> Verb Read, Target "SKILL.md",
//   // TargetDetail "skills/telegram"; a labeled sessions_spawn -> Subagent { Label, Status }.
// Callers localize verbs and compose the text; an empty view means "use the generic preview".
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenClaw.Chat;

namespace OpenClawTray.Chat;

public enum ChatToolRowVerb
{
    Read,
    Edit, Editing, Edited,
    Create, Creating, Created,
    Delete, Deleting, Deleted,
    Change, Changing, Changed,
    Write, Writing, Wrote,
    Searched,
    Fetched,
}

/// <summary>
/// The subagent a <c>sessions_spawn</c> call started, named by its launch label. Status and
/// elapsed time come from <see cref="ChatSubagentPresentation"/>; both are null until the
/// session snapshot lists the child. <see cref="ChildSessionKey"/> is the session the launch's
/// own result names, once that result arrives.
/// </summary>
internal sealed record ChatSpawnedSubagent(
    string Label,
    ChatSubagentStatus? Status = null,
    TimeSpan? Elapsed = null,
    string? ChildSessionKey = null);

/// <summary>
/// Collapsed row facts. <see cref="Title"/> is an agent-supplied purpose; <see cref="Command"/>
/// is shown after a shell prompt; <see cref="FileCount"/> replaces the target for multi-file patches.
/// </summary>
internal sealed record ChatToolRowView(
    string? Title = null,
    string? Command = null,
    ChatToolRowVerb? Verb = null,
    string? Target = null,
    string? TargetDetail = null,
    int FileCount = 0,
    ChatSpawnedSubagent? Subagent = null)
{
    public bool IsEmpty => Title is null && Command is null && Verb is null && Subagent is null;
}

internal static class ChatToolRowPresentation
{
    private const int MaxTitleChars = 120;
    private const int MaxCommandChars = 200;
    private const int MaxParsedOutputChars = 64 * 1024;
    private const int MaxPreambleStages = 4;
    // Markers ExecApprovalCommandDisplaySanitizer leaves when it cuts or suppresses a value.
    private const string CutMarker = "\u2026[truncated]";
    private const string SuppressedMarker = "full text suppressed]";

    // Like the web, only shell tools title their row with an agent-supplied purpose.
    private static readonly HashSet<string> s_titledCommandTools = new(StringComparer.Ordinal)
    {
        "bash", "exec", "shell", "run_command", "run_terminal_cmd", "powershell", "pwsh", "system.run",
    };

    private static readonly ConditionalWeakTable<ChatToolInputDetails, StrongBox<JsonObject?>> s_details = new();
    private static readonly ConditionalWeakTable<string, StrongBox<string?>> s_childKeys = new();
    private static readonly ConditionalWeakTable<string, List<PatchSection>> s_patchText = new();
    private static readonly ConditionalWeakTable<JsonArray, List<PatchSection>> s_patchChanges = new();
    private static readonly Regex s_shellWrapper = new(
        @"^\s*(?:/(?:usr/)?bin/)?(?:ba|z|da)?sh\s+-l?c\s+(['""])([\s\S]+)\1\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex s_whitespace = new(@"\s+", RegexOptions.CultureInvariant);
    private static readonly Regex s_snakeName = new(@"^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    private static readonly Regex s_codexFile = new(
        @"^\*\*\* (Update|Add|Delete) File: (.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex s_codexMove = new(@"^\*\*\* Move to: (.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex s_gitHeader = new(@"^diff --git a/(.+) b/(.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex s_hunkHeader = new(
        @"^@@ -\d+(?:,(\d+))? \+\d+(?:,(\d+))? @@", RegexOptions.CultureInvariant);

    public static ChatToolRowView Resolve(ChatTimelineItem entry, IReadOnlyList<ChatThread>? children = null)
    {
        var name = Normalize(entry.ToolName);
        if (name == "sessions_spawn")
            return ResolveSubagent(entry, children) is { } subagent ? new(Subagent: subagent) : new();

        // Persisted ToolArgs keep only display keys, so classify with the full redacted input
        // when it exists; the web's "few arguments plus command" rule needs every argument.
        var category = ChatToolActivityPresentation.Classify(entry.ToolName, Details(entry) ?? entry.ToolArgs);
        // The projector already classified the full input and moved its command out of the JSON.
        if (category == ChatToolActivityCategory.Generic && entry.ToolInputDetails?.Command is not null)
            category = ChatToolActivityCategory.Command;
        var running = entry.ToolResult is null or ChatToolCallStatus.InProgress;
        var succeeded = entry.ToolResult == ChatToolCallStatus.Success;
        switch (category)
        {
            case ChatToolActivityCategory.Command:
            {
                var title = s_titledCommandTools.Contains(name) ? Clean(String(entry, "title"), MaxTitleChars) : "";
                if (title.Length > 0)
                    return new(Title: title);
                var command = entry.ToolInputDetails?.Command ?? String(entry, "command");
                if (string.IsNullOrWhiteSpace(command) || IsCut(command))
                    return new();
                var wrapped = s_shellWrapper.Match(command);
                var shown = StripShellPreamble(wrapped.Success ? wrapped.Groups[2].Value : command);
                command = Clean(shown.Length > 0 ? shown : command, MaxCommandChars);
                return command.Length > 0 ? new(Command: command) : new();
            }
            case ChatToolActivityCategory.Edit when name is "apply_patch" or "applypatch" or "patch" ||
                name.EndsWith("_apply_patch", StringComparison.Ordinal):
                return ResolvePatch(entry, running, succeeded);
            case ChatToolActivityCategory.Read or ChatToolActivityCategory.Edit or ChatToolActivityCategory.Write:
            {
                var path = Path(entry);
                if (path is null)
                    return new();
                var (target, detail) = SplitPath(path);
                return new(
                    Verb: category switch
                    {
                        ChatToolActivityCategory.Read => ChatToolRowVerb.Read,
                        ChatToolActivityCategory.Write => MutationVerb("write", running, succeeded),
                        _ => MutationVerb("update", running, succeeded),
                    },
                    Target: target,
                    TargetDetail: detail);
            }
            case ChatToolActivityCategory.Search:
            {
                var pattern = Clean(String(entry, "pattern") ?? String(entry, "query") ?? String(entry, "glob"));
                var path = Path(entry) is { } raw && Clean(raw) is { Length: > 0 } cleaned ? cleaned : null;
                if (pattern.Length == 0 && path is null)
                    return new();
                return pattern.Length > 0
                    ? new(Verb: ChatToolRowVerb.Searched, Target: pattern, TargetDetail: path)
                    : new(Verb: ChatToolRowVerb.Searched, Target: path);
            }
            case ChatToolActivityCategory.Fetch:
            {
                var url = Clean(String(entry, "url"));
                return url.Length > 0 ? new(Verb: ChatToolRowVerb.Fetched, Target: url) : new();
            }
            default:
                return new();
        }
    }

    /// <summary>
    /// Port of the web <c>defaultTitle</c> for raw snake_case tool names:
    /// <c>heartbeat_respond</c> reads "Heartbeat Respond". Names that already carry a
    /// display form (<c>Apply Patch</c>, <c>PowerShell</c>, <c>system.run</c>) are kept.
    /// </summary>
    public static string DisplayName(string? toolName)
    {
        var name = (toolName ?? string.Empty).Trim();
        if (!s_snakeName.IsMatch(name))
            return name;
        return string.Join(' ', name.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    /// <summary>
    /// Bookkeeping the web leaves out of activity counts while it is running or completed:
    /// yields, plan progress, process polls and message reactions.
    /// </summary>
    public static bool IsRoutine(ChatTimelineItem entry)
    {
        if (entry.ToolResult is ChatToolCallStatus.Error or ChatToolCallStatus.Interrupted)
            return false;
        var name = Normalize(entry.ToolName);
        return name is "sessions_yield" or "update_plan" or "progress_card" ||
            name == "process" && String(entry, "action") == "poll" ||
            name == "message" && String(entry, "action") == "react";
    }

    /// <summary>
    /// A launch counts as a subagent only when it started one: failed launches and launches
    /// that opened a session in their own right count as ordinary operations.
    /// </summary>
    public static bool IsSubagentLaunch(ChatTimelineItem entry) =>
        Normalize(entry.ToolName) == "sessions_spawn" &&
        entry.ToolResult is not (ChatToolCallStatus.Error or ChatToolCallStatus.Interrupted) &&
        !IsOwnSessionLaunch(entry);

    private static ChatSpawnedSubagent? ResolveSubagent(ChatTimelineItem entry, IReadOnlyList<ChatThread>? children)
    {
        var label = Clean(String(entry, "label"), MaxTitleChars);
        if (label.Length == 0 || IsOwnSessionLaunch(entry))
            return null;
        var childKey = ChildSessionKey(entry);
        var child = childKey is null ? null : children?.FirstOrDefault(thread =>
            string.Equals(thread.Id, childKey, StringComparison.OrdinalIgnoreCase));
        if (child is null)
            return new(label, ChildSessionKey: childKey);
        // The Subagents pill owns subagent status, so a launch row and the pill always agree.
        var row = ChatSubagentPresentation.Project([child], DateTimeOffset.UtcNow) is var summary &&
            summary.Running.Count > 0 ? summary.Running[0] : summary.Finished[0];
        // The link opens the listed session, so carry its own ID, not the result's spelling of it.
        return new(label, row.Status, row.IsRunning ? null : row.Elapsed, child.Id);
    }

    private static bool IsOwnSessionLaunch(ChatTimelineItem entry) =>
        Bool(entry, "visible") == true || ChildSessionKey(entry) is { } key && IsDashboardSessionKey(key);

    private static bool IsDashboardSessionKey(string key) =>
        key.Trim().ToLowerInvariant().Split(':') is ["agent", { Length: > 0 }, "dashboard", { Length: > 0 }, ..];

    // History keeps the launch result as text; a live result may carry the same JSON.
    // Parsed once per output string: grouped rows and summaries ask on every render.
    private static string? ChildSessionKey(ChatTimelineItem entry) =>
        entry.ToolOutput is { } output
            ? s_childKeys.GetValue(output, static text => new(ReadChildSessionKey(text))).Value
            : null;

    private static string? ReadChildSessionKey(string text)
    {
        var output = text.Trim();
        if (output.Length == 0 || output[0] != '{' || output.Length > MaxParsedOutputChars)
            return null;
        try
        {
            // JsonDocument tolerates repeated keys, and a broken surrogate escape fails inside
            // this guard, so untrusted output can never break the timeline render.
            using var document = JsonDocument.Parse(output);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("childSessionKey", out var value) &&
                value.ValueKind == JsonValueKind.String && value.GetString() is { } key &&
                !string.IsNullOrWhiteSpace(key) ? key.Trim() : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static ChatToolRowView ResolvePatch(ChatTimelineItem entry, bool running, bool succeeded)
    {
        var sections = ParsePatch(entry);
        if (sections.Count == 0)
            return new();
        var operations = sections.Select(section => section.Operation).Distinct().ToArray();
        var verb = MutationVerb(operations.Length > 1 ? "mixed" : operations[0], running, succeeded);
        var paths = sections.Select(section => section.Path).Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length > 1)
            return new(Verb: verb, FileCount: paths.Length);
        var only = sections[^1];
        if (only.Source is { } source && source != only.Path)
        {
            var (fromBase, fromDir) = SplitPath(source);
            var (toBase, toDir) = SplitPath(only.Path);
            return fromDir == toDir
                ? new(Verb: verb, Target: $"{fromBase} \u2192 {toBase}", TargetDetail: fromDir)
                : new(Verb: verb, Target: $"{Clean(source)} \u2192 {Clean(only.Path)}");
        }
        var (target, detail) = SplitPath(only.Path);
        return new(Verb: verb, Target: target, TargetDetail: detail);
    }

    private sealed record PatchSection(string Operation, string Path, string? Source = null);

    private static List<PatchSection> ParsePatch(ChatTimelineItem entry)
    {
        if (Details(entry)?["changes"] is JsonArray changes)
        {
            var structured = s_patchChanges.GetValue(changes, ParseChanges);
            if (structured.Count > 0)
                return structured;
        }

        var patch = String(entry, "patch") ?? String(entry, "input") ?? String(entry, "diff");
        if (patch is null && entry.ToolInputDetails is { Unavailable: false, Text: { } raw } &&
            !raw.TrimStart().StartsWith('{'))
            patch = raw;
        // A cut patch can hide later files, so it gets the generic preview, not a partial summary.
        if (string.IsNullOrWhiteSpace(patch) || IsCut(patch))
            return [];
        return s_patchText.GetValue(patch, ParsePatchText);
    }

    private static List<PatchSection> ParseChanges(JsonArray changes)
    {
        var sections = new List<PatchSection>();
        foreach (var change in changes.OfType<JsonObject>())
        {
            if (ReadString(change, "path") is not { } path)
                continue;
            var kind = change["kind"];
            var type = kind is JsonValue kindValue && kindValue.TryGetValue<string>(out var text)
                ? text : kind is JsonObject kindObject ? ReadString(kindObject, "type") : null;
            var operation = type is "add" or "delete" ? type : "update";
            var moved = kind is JsonObject move ? ReadString(move, "move_path") ?? ReadString(move, "movePath") : null;
            sections.Add(moved is null ? new(operation, path) : new(operation, moved, path));
        }
        return sections;
    }

    private static List<PatchSection> ParsePatchText(string patch)
    {
        var sections = new List<PatchSection>();
        var lines = patch.ReplaceLineEndings("\n").Split('\n');
        var codex = lines.Any(line => line.Trim() == "*** Begin Patch" || s_codexFile.IsMatch(line.Trim()));
        (int Old, int New)? hunk = null;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (codex)
            {
                // Update bodies keep their leading context space, so content cannot pose as a header.
                var structural = sections.Count > 0 && sections[^1].Operation == "update" ? line.TrimEnd() : line.Trim();
                if (s_codexFile.Match(structural) is { Success: true } file)
                    sections.Add(new(file.Groups[1].Value.ToLowerInvariant(), file.Groups[2].Value.Trim()));
                else if (s_codexMove.Match(structural) is { Success: true } move && sections.Count > 0 &&
                    sections[^1].Operation == "update")
                    sections[^1] = sections[^1] with { Source = sections[^1].Path, Path = move.Groups[1].Value.Trim() };
                continue;
            }
            if (s_gitHeader.Match(line) is { Success: true } git)
            {
                sections.Add(new("update", git.Groups[2].Value.Trim(), git.Groups[1].Value.Trim()));
                hunk = null;
                continue;
            }
            if (s_hunkHeader.Match(line) is { Success: true } header)
            {
                hunk = (HunkCount(header.Groups[1]), HunkCount(header.Groups[2]));
                continue;
            }
            if (hunk is { } left)
            {
                // Hunk lines are content until their declared counts are consumed.
                left = line.Length == 0 ? left : line[0] switch
                {
                    '-' => (left.Old - 1, left.New),
                    '+' => (left.Old, left.New - 1),
                    ' ' => (left.Old - 1, left.New - 1),
                    _ => left,
                };
                hunk = left.Old <= 0 && left.New <= 0 ? null : left;
                continue;
            }
            if (line.StartsWith("--- ", StringComparison.Ordinal) && index + 1 < lines.Length &&
                lines[index + 1].StartsWith("+++ ", StringComparison.Ordinal))
            {
                var oldPath = UnifiedPath(line[4..]);
                var newPath = UnifiedPath(lines[index + 1][4..]);
                var operation = oldPath == "/dev/null" ? "add" : newPath == "/dev/null" ? "delete" : "update";
                var target = newPath == "/dev/null" ? oldPath : newPath;
                var fromGit = sections.Count > 0 && sections[^1].Source is not null &&
                    sections[^1].Path == target && sections[^1].Operation == "update";
                var section = new PatchSection(operation, target, operation == "update" ? oldPath : null);
                if (fromGit)
                    sections[^1] = section;
                else
                    sections.Add(section);
                index++;
            }
        }
        return sections;
    }

    private static int HunkCount(Group count) =>
        !count.Success ? 1
        : int.TryParse(count.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value
        : int.MaxValue;

    private static bool IsCut(string text) =>
        text.EndsWith(CutMarker, StringComparison.Ordinal) ||
        text.Contains(SuppressedMarker, StringComparison.Ordinal);

    /// <summary>
    /// Port of the web <c>stripShellPreamble</c>: drops up to four leading setup stages
    /// (<c>set</c>, <c>export</c>, <c>unset</c>, and <c>cd</c>/<c>pushd</c>/<c>popd</c> before
    /// <c>;</c>, newline or <c>&amp;&amp;</c>) so the row shows the command that does the work.
    /// Separators inside quotes or subshells are ignored.
    /// </summary>
    internal static string StripShellPreamble(string command)
    {
        var rest = command.Trim();
        for (var stage = 0; stage < MaxPreambleStages && rest.Length > 0; stage++)
        {
            var (index, length, isOr) = FirstTopLevelSeparator(rest);
            var head = (index < 0 ? rest : rest[..index]).Trim();
            var binary = FirstWord(head);
            var isChdir = (index >= 0 ? !isOr : stage > 0) && binary is "cd" or "pushd" or "popd";
            var isPreamble = head.StartsWith("set ", StringComparison.Ordinal) ||
                head.StartsWith("export ", StringComparison.Ordinal) ||
                head.StartsWith("unset ", StringComparison.Ordinal) || isChdir;
            if (!isPreamble)
                break;
            rest = index < 0 ? "" : rest[(index + length)..].TrimStart();
        }
        return rest.Trim();
    }

    private static (int Index, int Length, bool IsOr) FirstTopLevelSeparator(string text)
    {
        var depth = 0;
        char? quote = null;
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (c == '\\' && quote != '\'')
            {
                index++;
                continue;
            }
            if (quote is { } open)
            {
                if (c == open)
                    quote = null;
                continue;
            }
            switch (c)
            {
                case '\'' or '"' or '`':
                    quote = c;
                    break;
                case '(':
                    depth++;
                    break;
                case ')' when depth > 0:
                    depth--;
                    break;
                case '&' or '|' when depth == 0 && index + 1 < text.Length && text[index + 1] == c:
                    return (index, 2, c == '|');
                case ';' or '\n' when depth == 0:
                    return (index, 1, false);
            }
        }
        return (-1, 0, false);
    }

    private static string FirstWord(string head)
    {
        var end = 0;
        while (end < head.Length && !char.IsWhiteSpace(head[end]))
            end++;
        var word = head[..end].Trim('\'', '"');
        var slash = word.LastIndexOfAny(['/', '\\']);
        return (slash >= 0 ? word[(slash + 1)..] : word).ToLowerInvariant();
    }

    private static string UnifiedPath(string raw)
    {
        var path = raw.Split('\t', 2)[0].Trim();
        return path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal)
            ? path[2..] : path;
    }

    private static ChatToolRowVerb MutationVerb(string mutation, bool running, bool succeeded) =>
        (mutation, running, succeeded) switch
        {
            ("add", true, _) => ChatToolRowVerb.Creating,
            ("add", _, true) => ChatToolRowVerb.Created,
            ("add", _, _) => ChatToolRowVerb.Create,
            ("delete", true, _) => ChatToolRowVerb.Deleting,
            ("delete", _, true) => ChatToolRowVerb.Deleted,
            ("delete", _, _) => ChatToolRowVerb.Delete,
            ("mixed", true, _) => ChatToolRowVerb.Changing,
            ("mixed", _, true) => ChatToolRowVerb.Changed,
            ("mixed", _, _) => ChatToolRowVerb.Change,
            ("write", true, _) => ChatToolRowVerb.Writing,
            ("write", _, true) => ChatToolRowVerb.Wrote,
            ("write", _, _) => ChatToolRowVerb.Write,
            (_, true, _) => ChatToolRowVerb.Editing,
            (_, _, true) => ChatToolRowVerb.Edited,
            _ => ChatToolRowVerb.Edit,
        };

    private static string? Path(ChatTimelineItem entry)
    {
        foreach (var key in new[] { "path", "file_path", "filePath", "file", "filepath", "filename", "notebook_path" })
        {
            if (String(entry, key)?.Trim() is { Length: > 0 } path)
                return path;
        }
        return null;
    }

    /// <summary>
    /// Last nonempty segment for either separator; the remainder keeps its own separators.
    /// Each part is sanitized and bounded after splitting so long paths keep their file name.
    /// </summary>
    private static (string Base, string? Directory) SplitPath(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var slash = trimmed.LastIndexOfAny(['/', '\\']);
        return slash <= 0
            ? (Clean(trimmed.Length > 0 ? trimmed : path), null)
            : (Clean(trimmed[(slash + 1)..]), Clean(trimmed[..slash]) is { Length: > 0 } directory ? directory : null);
    }

    private static string Clean(string? value, int maxChars = NativeToolProjector.MaxDisplayValueChars)
    {
        var text = NativeToolProjector.SanitizeToolDisplayValue(
            value is null ? null : s_whitespace.Replace(value, " "));
        if (text.Length <= maxChars)
            return text;
        var end = maxChars - 1;
        if (char.IsHighSurrogate(text[end - 1]))
            end--;
        return text[..end] + "\u2026";
    }

    private static string? String(ChatTimelineItem entry, string key) =>
        ReadString(Details(entry), key) ?? ReadString(entry.ToolArgs, key);

    private static bool? Bool(ChatTimelineItem entry, string key) =>
        (Details(entry)?[key] ?? entry.ToolArgs?[key]) is JsonValue value && value.TryGetValue<bool>(out var flag)
            ? flag : null;

    private static string? ReadString(JsonObject? args, string key) =>
        args?[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text : null;

    // Persisted ToolArgs keep only display keys; the redacted input details keep the rest.
    private static JsonObject? Details(ChatTimelineItem entry)
    {
        if (entry.ToolInputDetails is not { Unavailable: false } details)
            return null;
        return s_details.GetValue(details, static value =>
        {
            var text = value.Text.TrimStart();
            if (!text.StartsWith('{'))
                return new(null);
            try
            {
                // JsonNode reads keys lazily; Count forces that read inside this guard.
                var parsed = JsonNode.Parse(text) as JsonObject;
                _ = parsed?.Count;
                return new(parsed);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            {
                return new(null);
            }
        }).Value;
    }

    private static string Normalize(string? toolName)
    {
        var name = (toolName ?? string.Empty).Trim().ToLowerInvariant();
        foreach (var prefix in new[] { "functions.", "tools.", "mcp." })
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                name = name[prefix.Length..];
        }
        return name.Replace('-', '_').Replace(' ', '_');
    }
}
