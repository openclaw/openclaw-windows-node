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
    Subagent,
}

public sealed record ChatToolActivityCount(
    ChatToolActivityCategory Category,
    int Count);

public sealed record ChatToolActivitySummary(
    IReadOnlyList<ChatToolActivityCount> Counts,
    int ToolCount,
    ChatTimelineItem? NewestRunningTool,
    int FailedCount = 0, int BlockedCount = 0, int SkippedCount = 0, int UnknownCount = 0)
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
            return CoalesceActivity(
                ProjectActivity(entries, sessionId, timelineGeneration, showToolCalls), sessionId, timelineGeneration);

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
                entry.Kind == ChatTimelineItemKind.Status && ChatNoticePresentation.StartsTurn(Facts(entry)) ||
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
        return CoalesceActivity(result, sessionId, timelineGeneration);
    }

    /// <summary>
    /// Port of the web <c>coalesceActivityRuns</c>: tool rows and tool groups that nothing visible
    /// separates read as one log, even across runs, so a burst of heartbeat calls is one group.
    /// The merged group keeps the first call's key, which a single row already used.
    /// </summary>
    private static List<ChatToolActivityRow> CoalesceActivity(
        IReadOnlyList<ChatToolActivityRow> rows, string? sessionId, long timelineGeneration)
    {
        var result = new List<ChatToolActivityRow>(rows.Count);
        var pending = new List<ChatToolActivityRow>();
        void Flush()
        {
            if (pending.Count == 1)
                result.Add(pending[0]);
            else if (pending.Count > 1)
            {
                var tools = pending.SelectMany(row =>
                    row.Entry is { } single ? new[] { single } : (IEnumerable<ChatTimelineItem>)row.Tools).ToArray();
                result.Add(new(ActivityKey(sessionId, timelineGeneration, tools[0].Id), null, tools, Summarize(tools)));
            }
            pending.Clear();
        }
        foreach (var row in rows)
        {
            var joins = row.IsActivityGroup && !row.IsCompletedWork ||
                row.Entry is { Kind: ChatTimelineItemKind.ToolCall, ConfirmedHandoff: null, ToolInputPreparing: false } entry &&
                entry.ToolReviews is not { Count: > 0 };
            if (!joins)
            {
                Flush();
                result.Add(row);
                continue;
            }
            pending.Add(row);
        }
        Flush();
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

            // As on the web, only two or more operations form a counted group; a lone call
            // keeps its own row so its verb, target or subagent stays visible. Both share a key.
            var tools = entries.Skip(index).Take(end - index).ToArray();
            rows.Add(tools.Length == 1
                ? Standalone(tools[0], sessionId, timelineGeneration)
                : new ChatToolActivityRow(
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
    /// Counts operations per category, not distinct files, matching the web summary. A completed
    /// call that wraps counted nested calls is not counted itself; its nested calls are. Routine
    /// bookkeeping is left out unless nothing else remains, and only launches that started a
    /// subagent count as subagents.
    /// </summary>
    public static ChatToolActivitySummary Summarize(IReadOnlyList<ChatTimelineItem> tools)
    {
        var summary = Summarize(tools, skipRoutine: true);
        return summary.ToolCount > 0 || tools.Any(tool => tool.ToolActivityFacts?.IsCanonical == true)
            ? summary : Summarize(tools, skipRoutine: false);
    }

    private static ChatToolActivitySummary Summarize(IReadOnlyList<ChatTimelineItem> tools, bool skipRoutine)
    {
        // Port of the web resolveCompletedActivityWrappers: a completed wrapper is replaced only
        // by a nested call that stays counted, so a wrapper over routine calls still counts.
        var authoritative = tools.Any(tool => tool.ToolActivityFacts?.IsCanonical == true);
        var wrappers = new HashSet<ChatTimelineItem>(ReferenceEqualityComparer.Instance);
        bool Shown(ChatToolCallNode node)
        {
            var childShown = false;
            foreach (var child in node.Children)
                childShown |= Shown(child);
            var facts = node.Tool.ToolActivityFacts;
            var visible = facts is not { HideFromChannelProgress: true } and not { SuppressChannelProgress: true } &&
                (facts?.IsCanonical == true || !(skipRoutine && ChatToolRowPresentation.IsRoutine(node.Tool)));
            if (childShown && node.Tool.ToolResult == ChatToolCallStatus.Success)
                wrappers.Add(node.Tool);
            return childShown || visible;
        }
        foreach (var root in GroupNested(tools))
            Shown(root);

        var categoryCounts = new Dictionary<ChatToolActivityCategory, int>();
        var counted = 0;
        var failed = 0;
        var blocked = 0;
        var skipped = 0;
        var unknown = 0;
        // Presentation-only dedup over the combined group. Reducer run/call identities stay distinct.
        var canonical = tools.Where(tool => tool.ToolActivityFacts?.IsCanonical == true && tool.ToolActivityFacts.SuppressChannelProgress != true)
            .GroupBy(tool => tool.ToolCallId ?? tool.Id, StringComparer.Ordinal)
            .Select(group => group.Last()).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var tool in tools)
        {
            var facts = tool.ToolActivityFacts;
            if (facts is { HideFromChannelProgress: true } or { SuppressChannelProgress: true } ||
                (facts?.IsCanonical == true ? !canonical.Contains(tool)
                    : authoritative || wrappers.Contains(tool) || skipRoutine && ChatToolRowPresentation.IsRoutine(tool)))
                continue;
            var category = facts?.CommandBearing == true ? ChatToolActivityCategory.Command
                : facts?.IsCanonical == true ? ClassifyActivity(tool.ToolName)
                : tool.ToolInputDetails?.Command is not null ? ChatToolActivityCategory.Command
                    : Classify(tool.ToolName, tool.ToolArgs);
            var outcome = Outcome(tool);
            if (category == ChatToolActivityCategory.Subagent &&
                (!ChatToolRowPresentation.IsSubagentLaunch(tool) || outcome is "failed" or "blocked" or "skipped"))
                category = ChatToolActivityCategory.Generic;
            categoryCounts[category] = categoryCounts.GetValueOrDefault(category) + 1;
            counted++;
            if (outcome == "failed") failed++;
            else if (outcome == "blocked") blocked++;
            else if (outcome == "skipped") skipped++;
            else if (facts is { IsCanonical: true, AuthoritativeAbsence: false } && facts.UnpairedCall != true && outcome is null) unknown++;
        }

        var counts = Enum.GetValues<ChatToolActivityCategory>()
            .Where(categoryCounts.ContainsKey)
            .Select(category => new ChatToolActivityCount(category, categoryCounts[category]))
            .ToArray();
        var newestRunning = tools.LastOrDefault(
            tool => (!authoritative || tool.ToolActivityFacts?.IsCanonical == true) && Outcome(tool) == "running" &&
                tool.ToolActivityFacts is not { HideFromChannelProgress: true } &&
                tool.ToolActivityFacts is not { SuppressChannelProgress: true });
        return new ChatToolActivitySummary(counts, counted, newestRunning, failed, blocked, skipped, unknown);
    }

    /// <summary>Display outcome facts do not change the reducer's execution outcome or admission.</summary>
    internal static string? Outcome(ChatTimelineItem tool) => tool.ToolActivityFacts is { AuthoritativeAbsence: false } facts &&
        (facts.IsCanonical || facts.Status is not null)
        ? !facts.IsCanonical && tool.ToolResult == ChatToolCallStatus.Error
            ? facts.Status == "blocked" ? "blocked" : "failed"
            : !facts.IsCanonical && tool.ToolResult == ChatToolCallStatus.Interrupted ? null
            : facts.Status == "running" && tool.ToolResult is ChatToolCallStatus.Success or ChatToolCallStatus.Error
                ? tool.ToolResult == ChatToolCallStatus.Error ? "failed" : "completed" : facts.Status
        : tool.ToolResult switch
        {
            null or ChatToolCallStatus.InProgress => "running",
            ChatToolCallStatus.Success => "completed",
            ChatToolCallStatus.Error => "failed",
            _ => null,
        };

    // Canonical Gateway activity uses the same narrow category map as the web. Legacy tool rows
    // retain their broader native aliases only when the producer supplied no activity facts.
    private static ChatToolActivityCategory ClassifyActivity(string? toolName) => NormalizeToolName(toolName) switch
    {
        "sessions_spawn" => ChatToolActivityCategory.Subagent,
        "exec" or "bash" or "shell" or "run_command" or "run_terminal_cmd" => ChatToolActivityCategory.Command,
        "read" or "read_file" or "readfile" or "notebookread" or "notebook_read" => ChatToolActivityCategory.Read,
        "edit" or "apply_patch" or "applypatch" or "patch" or "edit_file" or "multiedit" or "multi_edit" or
            "notebookedit" or "notebook_edit" => ChatToolActivityCategory.Edit,
        "write" or "write_file" or "create_file" => ChatToolActivityCategory.Write,
        "grep" or "glob" or "find" or "ls" or "list" or "codebase_search" or "web_search" => ChatToolActivityCategory.Search,
        "web_fetch" or "webfetch" or "fetch" => ChatToolActivityCategory.Fetch,
        _ => ChatToolActivityCategory.Generic,
    };

    public static ChatToolActivityCategory Classify(string? toolName, JsonObject? args = null)
    {
        var name = NormalizeToolName(toolName);
        if (name == "sessions_spawn")
            return ChatToolActivityCategory.Subagent;
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
        ReadOnlySpan<string> prefixes = ["functions.", "tools.", "mcp."];
        foreach (var prefix in prefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                name = name[prefix.Length..];
        }
        return name.Replace('-', '_').Replace(' ', '_');
    }

    private static bool Matches(string name, params ReadOnlySpan<string> aliases)
    {
        foreach (var alias in aliases)
        {
            if (name.EndsWith(alias, StringComparison.Ordinal)
                && (name.Length == alias.Length || name[name.Length - alias.Length - 1] is '.' or '_'))
                return true;
        }
        return false;
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
    string Running,
    string ToolFallback,
    string SubagentOne = "{0} subagent",
    string SubagentMany = "{0} subagents",
    string FailedCount = "{0} failed",
    string RawDetails = "Raw details",
    string BlockedCount = "{0} blocked", string SkippedCount = "{0} skipped", string UnknownCount = "{0} unknown");

/// <summary>Pure localized-template formatter matching the web tool-group summary policy.</summary>
public static class ChatToolActivityFormatter
{
    public static string Format(
        ChatToolActivitySummary summary,
        ChatToolActivityFormatTemplates templates)
    {
        // Like the web group summary, failures follow the counts as their own outcome.
        var parts = summary.Counts.Select(count => FormatCount(count, templates)).ToList();
        if (summary.FailedCount > 0)
            parts.Add(string.Format(CultureInfo.CurrentCulture, templates.FailedCount, summary.FailedCount));
        if (summary.BlockedCount > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, templates.BlockedCount, summary.BlockedCount));
        if (summary.SkippedCount > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, templates.SkippedCount, summary.SkippedCount));
        if (summary.UnknownCount > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, templates.UnknownCount, summary.UnknownCount));
        return parts.Count > 0 ? string.Join(" · ", parts) : templates.RawDetails;
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
            (ChatToolActivityCategory.Subagent, true) => templates.SubagentOne,
            (ChatToolActivityCategory.Subagent, false) => templates.SubagentMany,
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

    /// <summary>
    /// True once the user has opened or closed this row since the last collapse-all, so content
    /// built on first open is kept (and reused) when the row is closed and reopened.
    /// </summary>
    public bool HasChoice(string key, int collapseVersion)
    {
        ResetIfNeeded(collapseVersion);
        return _overrides.ContainsKey(key);
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

    /// <summary>
    /// Returns the keys, in display order, that arrived at the conversation's tail since the
    /// previous update. Rows above the newest already shown row (an older page) never enter.
    /// A history refresh of the same thread keeps entrances for rows it appends after rows that
    /// stayed, such as a recovered final reply; a refresh that keeps none replaced the history.
    /// </summary>
    /// <param name="admitInitial">
    /// The first ready update follows an authoritatively empty conversation (for example the
    /// welcome screen's first send), so its rows are new rather than loaded history.
    /// </param>
    public IReadOnlySet<string> Update(
        IEnumerable<string> orderedKeys,
        string? sessionId,
        long generation,
        long historyRevision,
        bool historyReady = true,
        bool admitInitial = false,
        IEnumerable<string>? eligibleKeys = null)
    {
        var entering = new HashSet<string>(StringComparer.Ordinal);
        if (!historyReady)
        {
            _scope = null;
            _keys.Clear();
            return entering;
        }
        var scope = (sessionId, generation, historyRevision);
        var ordered = orderedKeys.Distinct(StringComparer.Ordinal).ToList();
        var eligible = eligibleKeys?.ToHashSet(StringComparer.Ordinal);
        var lastKnown = ordered.FindLastIndex(_keys.Contains);
        var sameThread = _scope is { } previous &&
            previous.Session == sessionId && previous.Generation == generation;
        if (_scope == scope || sameThread && lastKnown >= 0 || _scope is null && admitInitial)
        {
            for (var index = lastKnown + 1; index < ordered.Count; index++)
            {
                if (eligible is null || eligible.Contains(ordered[index]))
                    entering.Add(ordered[index]);
            }
        }
        _scope = scope;
        _keys = ordered.ToHashSet(StringComparer.Ordinal);
        return entering;
    }
}

/// <summary>Tracks independently timed close holds for each tool-detail disclosure.</summary>
internal sealed class ChatToolDetailsCloseState
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _versions = new(StringComparer.Ordinal);
    private HashSet<string> _closingKeys = new(StringComparer.Ordinal);
    private int _revision;

    internal int Revision
    {
        get
        {
            lock (_gate)
                return _revision;
        }
    }

    internal int SetClosing(string key, bool closing)
    {
        lock (_gate)
        {
            var version = ++_revision;
            _versions[key] = version;
            var next = new HashSet<string>(_closingKeys, StringComparer.Ordinal);
            if (closing)
                next.Add(key);
            else
                next.Remove(key);
            _closingKeys = next;
            return version;
        }
    }

    internal IReadOnlySet<string> Snapshot()
    {
        lock (_gate)
            return new HashSet<string>(_closingKeys, StringComparer.Ordinal);
    }

    internal bool CompleteClose(string key, int version)
    {
        lock (_gate)
        {
            if (!_versions.TryGetValue(key, out var current) || current != version || !_closingKeys.Contains(key))
                return false;
            var next = new HashSet<string>(_closingKeys, StringComparer.Ordinal);
            next.Remove(key);
            _closingKeys = next;
            _revision++;
            return true;
        }
    }
}
