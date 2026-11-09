using System.Text.Json.Nodes;
using System.Globalization;
using OpenClaw.Chat;
using OpenClaw.Shared;

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
    int Count);

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
    ChatToolActivitySummary? Summary,
    IReadOnlyList<ChatTimelineItem>? Work = null,
    double? DurationMs = null)
{
    public bool IsActivityGroup => Tools.Count > 0 || Work is not null;
    public bool IsCompletedWork => Work is not null;
}

/// <summary>A tool call and the calls the Gateway recorded as nested under it.</summary>
public sealed record ChatToolCallNode(
    ChatTimelineItem Tool,
    IReadOnlyList<ChatToolCallNode> Children);

/// <summary>
/// WinUI-free chronological projection and summary policy for production chat tool activity.
/// </summary>
public static class ChatToolActivityPresentation
{
    public static IReadOnlyList<ChatToolActivityRow> Project(
        IReadOnlyList<ChatTimelineItem> entries,
        string? sessionId,
        long timelineGeneration,
        bool showToolCalls = true,
        IReadOnlyDictionary<string, ChatEntryMetadata>? metadata = null,
        bool runWorking = false,
        bool bubbleMode = false,
        ChatThread? session = null)
    {
        var scope = sessionId?.Trim().ToLowerInvariant().Split(':');
        if (scope is not ["agent", { Length: > 0 }, "dashboard", { Length: > 0 }] ||
            bubbleMode)
            return ProjectActivity(entries, sessionId, timelineGeneration, showToolCalls);

        ChatMessagePresentation? Facts(ChatTimelineItem entry) =>
            entry.Presentation ?? metadata?.GetValueOrDefault(entry.Id)?.Presentation;
        string? Run(ChatTimelineItem entry) => entry.ToolRunId ?? Facts(entry)?.RunId;
        bool Independent(ChatTimelineItem entry) => Facts(entry)?.IsIndependent == true || entry.IsBackgroundFinal;
        bool Media(ChatTimelineItem entry) =>
            metadata?.GetValueOrDefault(entry.Id)?.AssistantContent?.Media.Count > 0;
        bool Output(ChatTimelineItem entry) =>
            entry.Kind is ChatTimelineItemKind.Assistant or ChatTimelineItemKind.ToolCall or ChatTimelineItemKind.Reasoning &&
            !Independent(entry) && Facts(entry)?.SenderSession is null && Facts(entry)?.SenderAgentId is null;
        bool Failed(ChatTimelineItem entry) =>
            entry.ToolResult is ChatToolCallStatus.Error or ChatToolCallStatus.Interrupted ||
            entry.ToolReviews is { Count: > 0 } ||
            entry.ToolInputPreparing ||
            Facts(entry)?.EndsInFailure == true;
        string? Phase(ChatTimelineItem entry)
        {
            var facts = Facts(entry);
            if (facts?.StreamItemId is not null)
                return "commentary";
            return facts?.Phase ??
                (facts is { MirrorOrigin: "codex-app-server", RunTerminal: true, StopReason: "stop", Interrupted: false }
                    ? "final_answer" : null);
        }
        bool Reply(ChatTimelineItem entry) =>
            entry.Kind == ChatTimelineItemKind.Assistant && !entry.IsStreaming && !Independent(entry) &&
            Facts(entry)?.VoiceTranscript != true && !Failed(entry) &&
            Facts(entry)?.StopReason != "toolUse" &&
            (Facts(entry)?.MirrorOrigin != "codex-app-server" || Facts(entry)?.RunTerminal == true) &&
            Phase(entry) != "commentary" && (!string.IsNullOrWhiteSpace(entry.Text) || Media(entry));
        bool Work(ChatTimelineItem entry) =>
            Output(entry) && !entry.IsStreaming && !Media(entry) &&
            Facts(entry)?.VoiceTranscript != true && Phase(entry) != "final_answer";

        var turns = new List<List<ChatTimelineItem>>();
        foreach (var entry in entries)
        {
            if (turns.Count == 0 || entry.Kind == ChatTimelineItemKind.User ||
                Facts(entry)?.TurnBoundary == true || Independent(entry) ||
                Facts(entry)?.SenderSession is not null || Facts(entry)?.SenderAgentId is not null)
                turns.Add([]);
            turns[^1].Add(entry);
        }
        var liveTurn = runWorking ? turns.Count - 1 : -1;
        var steerTarget = runWorking && turns.Count > 0
            ? turns[^1].Where(entry => entry.Kind == ChatTimelineItemKind.User)
                .Select(entry => Facts(entry)?.SteerTargetRunId).LastOrDefault(id => id is not null)
            : null;
        if (steerTarget is not null)
        {
            var target = turns.FindIndex(turn => turn.Any(entry =>
                entry.Kind == ChatTimelineItemKind.User && Run(entry) == steerTarget));
            if (target >= 0)
                liveTurn = target;
        }

        var result = new List<ChatToolActivityRow>();
        void Append(IEnumerable<ChatTimelineItem> items) =>
            result.AddRange(ProjectActivity(items.ToArray(), sessionId, timelineGeneration, showToolCalls));
        for (var turnIndex = 0; turnIndex < turns.Count; turnIndex++)
        {
            var turn = turns[turnIndex];
            var final = turn.FindLastIndex(entry => Reply(entry));
            if (turn.Any(entry => entry.ConfirmedHandoff is not null))
            {
                var trailing = turn.Skip(final + 1).ToArray();
                var answerLast = final >= 0 && Run(turn[final]) is { } replyRun &&
                    trailing.Length > 0 && trailing.All(entry => Work(entry) &&
                        entry.ConfirmedHandoff is null && Run(entry) == replyRun && !Failed(entry));
                Append(answerLast
                    ? turn.Take(final).Concat(trailing).Append(turn[final])
                    : turn);
                continue;
            }
            if (turnIndex == liveTurn || turn.Any(entry =>
                    entry.IsStreaming || entry.ToolResult == ChatToolCallStatus.InProgress) || final < 0)
            {
                Append(turn);
                continue;
            }
            var start = final;
            while (start > 0 && Output(turn[start - 1]))
                start--;
            var replyRuns = turn.Where(entry => Run(entry) is not null &&
                (Media(entry) || entry.Kind == ChatTimelineItemKind.Assistant && !string.IsNullOrWhiteSpace(entry.Text)))
                .Select(Run).ToHashSet(StringComparer.Ordinal);
            var end = final;
            while (end + 1 < turn.Count && Output(turn[end + 1]) &&
                (Run(turn[end + 1]) is null || replyRuns.Contains(Run(turn[end + 1]))))
                end++;

            var preserved = new HashSet<string>(StringComparer.Ordinal) { turn[final].Id };
            if (Run(turn[final]) is { } finalRun)
            {
                var answered = new HashSet<string>(StringComparer.Ordinal) { finalRun };
                for (var index = final - 1; index >= start; index--)
                {
                    if (Run(turn[index]) is not { } run || answered.Contains(run))
                        continue;
                    if (Failed(turn[index]))
                        answered.Add(run);
                    else if (Reply(turn[index]))
                    {
                        answered.Add(run);
                        preserved.Add(turn[index].Id);
                    }
                }
            }
            var work = new List<ChatTimelineItem>();
            var answers = new List<ChatTimelineItem>();
            for (var index = start; index <= end; index++)
            {
                var entry = turn[index];
                if (!preserved.Contains(entry.Id) && Work(entry) && (index < final || !Failed(entry)))
                {
                    if (showToolCalls || entry.Kind != ChatTimelineItemKind.ToolCall)
                        work.Add(entry);
                }
                else
                    answers.Add(entry);
            }
            if (work.Count == 0)
            {
                Append(turn);
                continue;
            }
            Append(turn.Take(start));
            var tools = work.Where(entry => entry.Kind == ChatTimelineItemKind.ToolCall).ToArray();
            var ownerRun = Run(turn[final]);
            if (preserved.Count > 1)
            {
                var workRuns = new HashSet<string?>(StringComparer.Ordinal);
                for (var index = start; index <= end; index++)
                {
                    if (!work.Contains(turn[index]))
                        continue;
                    var run = Run(turn[index]);
                    for (var next = index + 1; run is null && next <= end; next++)
                        run = Run(turn[next]);
                    workRuns.Add(run);
                }
                var firstAnswerRun = answers.Count > 0 ? Run(answers[0]) : null;
                ownerRun = workRuns.Count == 1 && workRuns.Contains(firstAnswerRun) ? firstAnswerRun : null;
            }
            var duration = session is not null && session.Id == sessionId && ownerRun is not null &&
                session.LastRunId == ownerRun && session.RunStatus is "done" or "failed" or "timeout" or "killed" &&
                session.RuntimeMs is { } ms && double.IsFinite(ms) && ms >= 0
                    ? session.RuntimeMs : null;
            result.Add(new(
                $"thread:{sessionId}|generation:{timelineGeneration}|work:{turn[final].Id}",
                null, tools, Summarize(tools) with { NewestRunningTool = null }, work, duration));
            Append(answers);
            Append(turn.Skip(end + 1));
        }
        return result;
    }

    private static IReadOnlyList<ChatToolActivityRow> ProjectActivity(
        IReadOnlyList<ChatTimelineItem> entries,
        string? sessionId,
        long timelineGeneration,
        bool showToolCalls)
    {
        var rows = new List<ChatToolActivityRow>(entries.Count);
        for (var index = 0; index < entries.Count;)
        {
            var entry = entries[index];
            if (entry.ConfirmedHandoff is { } handoff)
            {
                if (handoff.ToolName == "sessions_yield")
                {
                    if (!string.IsNullOrWhiteSpace(handoff.Acknowledgment))
                        rows.Add(Standalone(entry with
                        {
                            Id = $"{entry.Id}:handoff",
                            Kind = ChatTimelineItemKind.Assistant,
                            Text = handoff.Acknowledgment,
                            ToolName = null, ToolArgs = null, ToolOutput = null, ToolResult = null,
                            Presentation = entry.Presentation ?? new(RunId: entry.ToolRunId),
                        }, sessionId, timelineGeneration));
                }
                else if (showToolCalls)
                    rows.Add(Standalone(entry, sessionId, timelineGeneration));
                index++;
                continue;
            }
            if (entry.Kind != ChatTimelineItemKind.ToolCall)
            {
                rows.Add(Standalone(entry, sessionId, timelineGeneration));
                index++;
                continue;
            }

            // Failed steps join the group with an inline outcome, as on the web.
            // Reviews and input preparation keep their standalone disclosure.
            if (entry.ToolReviews is { Count: > 0 } || entry.ToolInputPreparing)
            {
                if (showToolCalls)
                    rows.Add(Standalone(entry, sessionId, timelineGeneration));
                index++;
                continue;
            }

            var end = index + 1;
            while (end < entries.Count
                && entries[end].Kind == ChatTimelineItemKind.ToolCall
                && entries[end].ConfirmedHandoff is null
                && entries[end].ToolReviews is not { Count: > 0 }
                && !entries[end].ToolInputPreparing
                && string.Equals(entries[end].ToolRunId, entry.ToolRunId, StringComparison.Ordinal)
                && entries[end].ToolLegacyTurn == entry.ToolLegacyTurn)
            {
                end++;
            }

            if (!showToolCalls)
            {
                index = end;
                continue;
            }

            var tools = entries.Skip(index).Take(end - index).ToArray();
            rows.Add(new ChatToolActivityRow(
                ActivityKey(sessionId, timelineGeneration, tools[0].Id),
                null,
                tools,
                Summarize(tools)));

            index = end;
        }

        return rows;
    }

    public static string ActivityKey(string? sessionId, long timelineGeneration, string firstToolEntryId) =>
        $"thread:{sessionId ?? "none"}|generation:{timelineGeneration}|activity:{firstToolEntryId}";

    /// <summary>Expansion key of one tool row, distinct from the group that starts with it.</summary>
    public static string ToolRowKey(string? sessionId, long timelineGeneration, string toolEntryId) =>
        $"thread:{sessionId ?? "none"}|generation:{timelineGeneration}|tool:{toolEntryId}";

    /// <summary>Groups the completed-work narration into the same rows as the transcript.</summary>
    public static IReadOnlyList<ChatToolActivityRow> ProjectWork(
        IReadOnlyList<ChatTimelineItem> work,
        string? sessionId,
        long timelineGeneration) =>
        ProjectActivity(work, sessionId, timelineGeneration, showToolCalls: true);

    /// <summary>
    /// Port of the web <c>groupToolCalls</c>: nests a call under its recorded
    /// <c>parentToolCallId</c> within the same run only. Relationships are never guessed from
    /// names or order; ambiguous identities stay unlinked and cycle members become roots.
    /// </summary>
    public static IReadOnlyList<ChatToolCallNode> GroupNested(IReadOnlyList<ChatTimelineItem> tools)
    {
        var identities = new Dictionary<(string Run, string Call), int>();
        var ambiguous = new HashSet<(string Run, string Call)>();
        for (var index = 0; index < tools.Count; index++)
        {
            if (tools[index] is { ToolRunId: { Length: > 0 } run, ToolCallId: { Length: > 0 } call } &&
                !identities.TryAdd((run, call), index))
                ambiguous.Add((run, call));
        }
        int? Resolve(string run, string call) =>
            !ambiguous.Contains((run, call)) && identities.TryGetValue((run, call), out var index) ? index : null;

        var parents = new int?[tools.Count];
        for (var index = 0; index < tools.Count; index++)
        {
            var tool = tools[index];
            if (tool.ToolRunId is not { Length: > 0 } run ||
                tool.ToolParentCallId is not { Length: > 0 } parent ||
                parent == tool.ToolCallId ||
                tool.ToolCallId is { Length: > 0 } call && Resolve(run, call) != index)
                continue;
            parents[index] = Resolve(run, parent);
        }

        var visited = new bool[tools.Count];
        for (var index = 0; index < tools.Count; index++)
        {
            var path = new List<int>();
            int? current = index;
            while (current is { } node && !visited[node])
            {
                visited[node] = true;
                path.Add(node);
                current = parents[node];
            }
            var cycleStart = current is { } repeated ? path.IndexOf(repeated) : -1;
            if (cycleStart >= 0)
            {
                foreach (var member in path.Skip(cycleStart))
                    parents[member] = null;
            }
        }

        var children = tools.Select(static _ => new List<ChatToolCallNode>()).ToArray();
        var nodes = tools.Select((tool, index) => new ChatToolCallNode(tool, children[index])).ToArray();
        var roots = new List<ChatToolCallNode>();
        for (var index = 0; index < tools.Count; index++)
            (parents[index] is { } parent ? children[parent] : roots).Add(nodes[index]);
        return roots;
    }

    /// <summary>
    /// Counts operations per category, not distinct files, matching the web summary. A call that
    /// wraps recorded nested calls is not counted itself; its nested calls are.
    /// </summary>
    public static ChatToolActivitySummary Summarize(IReadOnlyList<ChatTimelineItem> tools)
    {
        var wrappers = new HashSet<ChatTimelineItem>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<ChatToolCallNode>(GroupNested(tools));
        while (pending.TryPop(out var node))
        {
            if (node.Children.Count > 0)
                wrappers.Add(node.Tool);
            foreach (var child in node.Children)
                pending.Push(child);
        }

        var categoryCounts = new Dictionary<ChatToolActivityCategory, int>();
        var counted = 0;
        foreach (var tool in tools)
        {
            if (wrappers.Contains(tool))
                continue;
            var category = Classify(tool.ToolName, tool.ToolArgs);
            categoryCounts[category] = categoryCounts.GetValueOrDefault(category) + 1;
            counted++;
        }

        var counts = Enum.GetValues<ChatToolActivityCategory>()
            .Where(categoryCounts.ContainsKey)
            .Select(category => new ChatToolActivityCount(category, categoryCounts[category]))
            .ToArray();
        var newestRunning = tools.LastOrDefault(
            static tool => tool.ToolResult is null or ChatToolCallStatus.InProgress);
        return new ChatToolActivitySummary(counts, counted, newestRunning);
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

    private static string NormalizeToolName(string? toolName)
    {
        var name = (toolName ?? string.Empty).Trim().ToLowerInvariant();
        foreach (var prefix in new[] { "functions.", "tools.", "mcp." })
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                name = name[prefix.Length..];
        }
        return name.Replace('-', '_').Replace(' ', '_');
    }

    private static bool Matches(string name, params string[] aliases) =>
        aliases.Any(alias =>
            string.Equals(name, alias, StringComparison.Ordinal)
            || name.EndsWith($".{alias}", StringComparison.Ordinal)
            || name.EndsWith($"_{alias}", StringComparison.Ordinal));

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

        return string.Join(" · ", summary.Counts.Select(count => FormatCount(count, templates)));
    }

    private static string FormatCount(
        ChatToolActivityCount count,
        ChatToolActivityFormatTemplates templates)
    {
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
        => IsExpanded(key, collapseVersion);

    public bool IsExpanded(string key, int collapseVersion)
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

/// <summary>Admits completed-work entrances once, excluding initial and replacement history.</summary>
public sealed class ChatToolActivityMotionState
{
    private (string? Session, long Generation, long HistoryRevision)? _scope;
    private HashSet<string> _keys = new(StringComparer.Ordinal);

    public IReadOnlySet<string> Update(
        IEnumerable<string> completedWorkKeys,
        string? sessionId,
        long generation,
        long historyRevision,
        bool historyReady = true)
    {
        if (!historyReady)
        {
            _scope = null;
            _keys.Clear();
            return new HashSet<string>(StringComparer.Ordinal);
        }
        var scope = (sessionId, generation, historyRevision);
        var keys = completedWorkKeys.ToHashSet(StringComparer.Ordinal);
        var entering = _scope == scope
            ? keys.Where(key => !_keys.Contains(key)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        _scope = scope;
        _keys = keys;
        return entering;
    }
}
