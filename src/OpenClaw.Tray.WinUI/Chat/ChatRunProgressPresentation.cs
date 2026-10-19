using OpenClaw.Chat;

namespace OpenClawTray.Chat;

public sealed record ChatRunProgressPresentation(
    DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, string Label, bool Active, bool Waiting,
    long? OutputTokens = null);

internal static class ChatRunProgressProjection
{
    internal static ChatRunProgressPresentation? Project(ChatTimelineState timeline, bool awaitingQuestion = false)
    {
        if (!timeline.TurnActive && timeline.TurnStartedAt is null && !awaitingQuestion)
            return null;
        var waiting = awaitingQuestion || timeline.PendingPermission is not null
            || timeline.ProgressText == "Waiting for approval";
        var activeTool = timeline.Entries.LastOrDefault(entry =>
            entry.Kind == ChatTimelineItemKind.ToolCall && entry.ToolResult == ChatToolCallStatus.InProgress);
        var label = awaitingQuestion ? "Waiting for your answer"
            : !timeline.TurnActive ? "Finished"
            : waiting ? "Waiting for approval"
            : timeline.ProgressText is { Length: > 0 } progress ? progress
            : activeTool is not null ? $"Running {activeTool.ToolName ?? "tool"}"
            : timeline.ActiveReasoningId is not null ? "Thinking"
            : timeline.ActiveAssistantId is not null ? "Responding" : "Working";
        return new(timeline.TurnStartedAt, timeline.TurnEndedAt, label, timeline.TurnActive, waiting,
            timeline.RunUsage?.OutputTokens);
    }

    internal static string Elapsed(DateTimeOffset? start, DateTimeOffset now)
    {
        if (start is null)
            return "";
        var elapsed = TimeSpan.FromSeconds(Math.Max(0, Math.Floor((now - start.Value).TotalSeconds)));
        return elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m {elapsed.Seconds}s"
            : elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
            : $"{elapsed.Seconds}s";
    }
}
