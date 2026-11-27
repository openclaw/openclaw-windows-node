// WinUI-free agent-run framing for the chat timeline, ported from the web agent run frame.
// Usage:
//   var positions = ChatTimelineAssistantRuns.Describe(projectedRows, Facts, lastRunWorking);
//   // positions[i].IsStart: row i opens a block of agent rows and carries its avatar.
//   // positions[i].IsEnd: row i is its run's final answer and owns the actions footer.
using OpenClaw.Chat;
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

internal readonly record struct ChatAssistantRunPosition(bool IsStart, bool IsEnd);

internal static class ChatTimelineAssistantRuns
{
    /// <summary>
    /// Like the web transcript, one avatar marks a contiguous block of agent rows (assistant
    /// text, tool rows, tool groups and reasoning). A block ends at any other row kind (user
    /// messages, status notices) or when a forwarded sender changes. Its first row carries the
    /// avatar, whatever its kind, so back-to-back runs such as heartbeats share one avatar.
    /// Inside a block, each run owns one actions footer: a run also ends where its recorded
    /// identity changes and at rows that start their own turn (turn boundaries, independent or
    /// background messages). The run's last non-commentary assistant message owns the footer,
    /// except while that run is still working, when no message does. A merged tool group can
    /// span several runs, so each of its tools is checked for a run change. While the turn is
    /// active, the working runs are those holding a streaming message or a running tool; when no
    /// row shows live work, the last run is assumed to be the working one.
    /// </summary>
    public static IReadOnlyList<ChatAssistantRunPosition> Describe(
        IReadOnlyList<ChatToolActivityRow> rows,
        Func<ChatTimelineItem, ChatMessagePresentation?>? facts = null,
        bool lastRunWorking = false)
    {
        var positions = new ChatAssistantRunPosition[rows.Count];
        var runs = new List<(int Owner, bool Live)>();
        var inBlock = false;
        string? sender = null;
        var runOpen = false;
        string? run = null;
        var owner = -1;
        var live = false;
        void CloseRun()
        {
            if (runOpen)
                runs.Add((owner, live));
            runOpen = false;
            run = null;
            owner = -1;
            live = false;
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (!IsAgentRow(row))
            {
                CloseRun();
                inBlock = false;
                continue;
            }
            var lead = row.Entry ?? (row.Tools.Count > 0 ? row.Tools[0] : row.Work?.FirstOrDefault());
            var presentation = lead is null ? null : facts?.Invoke(lead);
            var rowSender = presentation?.SenderSession ?? presentation?.SenderAgentId;
            if (inBlock && rowSender != sender)
            {
                CloseRun();
                inBlock = false;
            }
            if (!inBlock)
            {
                positions[index] = positions[index] with { IsStart = true };
                inBlock = true;
                sender = rowSender;
            }
            foreach (var (item, itemRun) in RunSteps(row, facts))
            {
                if (runOpen &&
                    (StartsTurn(item, facts) || run is not null && itemRun is not null && itemRun != run))
                    CloseRun();
                runOpen = true;
                run ??= itemRun;
                live |= item.IsStreaming ||
                    item.Kind == ChatTimelineItemKind.ToolCall && item.ToolResult == ChatToolCallStatus.InProgress;
            }
            if (row.Entry is { Kind: ChatTimelineItemKind.Assistant } entry &&
                facts?.Invoke(entry)?.Phase != "commentary")
                owner = index;
        }
        CloseRun();

        var anyLive = runs.Exists(static item => item.Live);
        for (var index = 0; index < runs.Count; index++)
        {
            var (runOwner, runLive) = runs[index];
            var working = lastRunWorking && (anyLive ? runLive : index == runs.Count - 1);
            if (runOwner >= 0 && !working)
                positions[runOwner] = positions[runOwner] with { IsEnd = true };
        }
        return positions;
    }

    private static bool IsAgentRow(ChatToolActivityRow row) =>
        row.IsActivityGroup ||
        row.Entry?.Kind is ChatTimelineItemKind.Assistant or ChatTimelineItemKind.ToolCall or ChatTimelineItemKind.Reasoning;

    // Single rows and merged tool groups step through each entry. A Worked rollup is one
    // completed turn, so it counts once with its first recorded run.
    private static IEnumerable<(ChatTimelineItem Item, string? Run)> RunSteps(
        ChatToolActivityRow row, Func<ChatTimelineItem, ChatMessagePresentation?>? facts)
    {
        string? RunId(ChatTimelineItem item) =>
            item.ToolRunId is { Length: > 0 } toolRun ? toolRun
            : facts?.Invoke(item)?.RunId is { Length: > 0 } run ? run : null;

        if (row.Entry is { } entry)
        {
            yield return (entry, RunId(entry));
            yield break;
        }
        if (row.Tools.Count > 0)
        {
            foreach (var tool in row.Tools)
                yield return (tool, RunId(tool));
            yield break;
        }
        if (row.Work is { Count: > 0 } work)
            yield return (work[0], work.Select(RunId).FirstOrDefault(static id => id is not null));
    }

    // The same turn starts ChatToolActivityPresentation.Project uses for completed-work rollups.
    private static bool StartsTurn(ChatTimelineItem entry, Func<ChatTimelineItem, ChatMessagePresentation?>? facts)
    {
        var presentation = facts?.Invoke(entry);
        return entry.IsBackgroundFinal || presentation is { TurnBoundary: true } or { IsIndependent: true } ||
            presentation?.SenderSession is not null || presentation?.SenderAgentId is not null;
    }
}
