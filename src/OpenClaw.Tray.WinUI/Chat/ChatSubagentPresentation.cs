// Pure projection of one parent's direct subagent threads for the Subagents pill and tab.
// Usage: var summary = ChatSubagentPresentation.Project(children, DateTimeOffset.UtcNow);
//        summary.RunningCount, summary.Running, summary.Finished.
using OpenClaw.Chat;
using OpenClaw.Shared.Sessions;

namespace OpenClawTray.Chat;

internal enum ChatSubagentStatus
{
    Queued,
    Working,
    AwaitingAnswer,
    AwaitingApproval,
    AwaitingSubagents,
    Done,
    Stopped,
    Failed,
    /// <summary>Not running, but the latest outcome is unknown or stale; never shown as success.</summary>
    Ended,
}

internal sealed record ChatSubagentRow(ChatThread Thread, ChatSubagentStatus Status, TimeSpan? Elapsed)
{
    internal bool IsRunning => Status <= ChatSubagentStatus.AwaitingSubagents;
}

internal sealed record ChatSubagentSummary(IReadOnlyList<ChatSubagentRow> Running, IReadOnlyList<ChatSubagentRow> Finished)
{
    internal static readonly ChatSubagentSummary Empty = new([], []);
    internal int RunningCount => Running.Count;
    internal int Total => Running.Count + Finished.Count;
}

/// <summary>Classifies subagents once so the pill counts and the tab groups always agree.</summary>
internal static class ChatSubagentPresentation
{
    internal static ChatSubagentSummary Project(IEnumerable<ChatThread> children, DateTimeOffset now)
    {
        var rows = children.Select(child => Row(child, now)).ToArray();
        return new(
            rows.Where(row => row.IsRunning)
                .OrderByDescending(row => row.Thread.CreatedAt ?? DateTimeOffset.MinValue).ToArray(),
            rows.Where(row => !row.IsRunning)
                .OrderByDescending(row => row.Thread.UpdatedAt ?? row.Thread.CreatedAt ?? DateTimeOffset.MinValue)
                .ToArray());
    }

    internal static ChatSubagentStatus Classify(ChatThread child)
    {
        // The live-run flag is authoritative; the recorded outcome describes only the latest finished run.
        if (child.Activity == ChatActivity.AwaitingPermission) return ChatSubagentStatus.AwaitingApproval;
        if (child.Activity == ChatActivity.AwaitingInput) return ChatSubagentStatus.AwaitingAnswer;
        if (child.Activity == ChatActivity.Working) return ChatSubagentStatus.Working;
        if (child.WaitingForSubagents) return ChatSubagentStatus.AwaitingSubagents;
        if (string.Equals(child.RunStatus?.Trim(), "queued", StringComparison.OrdinalIgnoreCase))
            return ChatSubagentStatus.Queued;
        if (child.Activity == ChatActivity.Error) return ChatSubagentStatus.Failed;
        if (child.LastRunStopped) return ChatSubagentStatus.Stopped;
        return SessionRunState.ResolveStatus(child.RunStatus) switch
        {
            SessionRunStatus.Completed => ChatSubagentStatus.Done,
            SessionRunStatus.Failed or SessionRunStatus.TimedOut => ChatSubagentStatus.Failed,
            SessionRunStatus.Stopped => ChatSubagentStatus.Stopped,
            // Unknown, or "running" without a live run (for example after a Gateway restart).
            _ => ChatSubagentStatus.Ended,
        };
    }

    private static ChatSubagentRow Row(ChatThread child, DateTimeOffset now)
    {
        var status = Classify(child);
        var running = status <= ChatSubagentStatus.AwaitingSubagents;
        TimeSpan? elapsed = null;
        if (running)
        {
            // Runtime samples are stale while a run is live; count from its start instead.
            if (status != ChatSubagentStatus.Queued && child.CreatedAt is { } started) elapsed = now - started;
        }
        else if (child.RuntimeMs is > 0 and var runtime)
            elapsed = TimeSpan.FromMilliseconds(runtime);
        else if (child.EndedAt is { } ended && child.CreatedAt is { } started)
            elapsed = DateTimeOffset.FromUnixTimeMilliseconds(ended) - started;
        return new(child, status, elapsed is { } value && value > TimeSpan.Zero ? value : null);
    }
}
