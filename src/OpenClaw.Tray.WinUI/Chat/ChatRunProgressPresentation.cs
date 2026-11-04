using OpenClaw.Chat;
#if !OPENCLAW_TRAY_TESTS
using OpenClawTray.Helpers;
#endif

namespace OpenClawTray.Chat;

public sealed record ChatRunProgressPresentation(
    DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, string Label, bool Active, bool Waiting,
    long? OutputTokens = null);

internal static class ChatRunProgressProjection
{
    internal static ChatRunProgressPresentation? Project(ChatTimelineState timeline, bool awaitingQuestion = false)
    {
        var waiting = awaitingQuestion || timeline.PendingPermission is not null
            || timeline.ProgressText == "Waiting for approval";
        if (!timeline.TurnActive && !waiting)
            return null;
        var activeTool = timeline.Entries.LastOrDefault(entry =>
            entry.Kind == ChatTimelineItemKind.ToolCall && entry.ToolResult == ChatToolCallStatus.InProgress);
        var label = awaitingQuestion ? "Waiting for your answer"
            : waiting ? "Waiting for approval"
            : timeline.ProgressText is { Length: > 0 } progress ? progress
            : activeTool is not null ? $"Running {activeTool.ToolName ?? "tool"}"
            : timeline.ActiveReasoningId is not null ? "Thinking"
            : timeline.ActiveAssistantId is not null ? "Responding" : "Working";
        return new(timeline.TurnStartedAt, timeline.TurnEndedAt, label, timeline.TurnActive, waiting,
            timeline.RunUsage?.OutputTokens);
    }

    internal static string Elapsed(DateTimeOffset? start, DateTimeOffset now, Func<string, string>? getString = null)
    {
        if (start is null)
            return "";
        var elapsed = TimeSpan.FromSeconds(Math.Max(0, Math.Floor((now - start.Value).TotalSeconds)));
        var (key, fallback) = elapsed.TotalHours >= 1
            ? ("Chat_Run_ElapsedHours", "{0}h {1}m {2}s")
            : elapsed.TotalMinutes >= 1
                ? ("Chat_Run_ElapsedMinutes", "{1}m {2}s")
                : ("Chat_Run_ElapsedSeconds", "{2}s");
        var format = (getString ?? LocalizationHelper.GetString)(key);
        if (string.IsNullOrEmpty(format) || format == key)
            format = fallback;
        return string.Format(format, (int)elapsed.TotalHours, elapsed.Minutes, elapsed.Seconds);
    }
}
