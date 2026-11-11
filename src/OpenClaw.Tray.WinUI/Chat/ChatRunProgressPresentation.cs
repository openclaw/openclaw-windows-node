using OpenClaw.Chat;
#if !OPENCLAW_TRAY_TESTS
using OpenClawTray.Helpers;
#endif

namespace OpenClawTray.Chat;

public sealed record ChatRunProgressPresentation(
    DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, string Label, bool Active, bool Waiting,
    long? OutputTokens = null, DateTimeOffset? RetryAt = null);

internal static class ChatRunProgressProjection
{
    internal const string RetryLabel = "Retrying the model request";

    internal static ChatRunProgressPresentation? Project(ChatTimelineState timeline, bool awaitingQuestion = false,
        bool awaitingSubagents = false)
    {
        var waiting = awaitingQuestion || timeline.PendingPermission is not null
            || timeline.ProgressText == "Waiting for approval" || awaitingSubagents;
        if (!timeline.TurnActive && !waiting)
            return null;
        var activeTool = timeline.Entries.LastOrDefault(entry =>
            entry.Kind == ChatTimelineItemKind.ToolCall && entry.ToolResult == ChatToolCallStatus.InProgress);
        var currentReasoning = timeline.ReasoningStream is { Items.Count: > 0 } stream
            && (timeline.RunUsage?.RunId is null || timeline.RunUsage.RunId == stream.RunId);
        var label = awaitingQuestion ? "Waiting for your answer"
            : timeline.PendingPermission is not null || timeline.ProgressText == "Waiting for approval" ? "Waiting for approval"
            : awaitingSubagents ? "Waiting for subagents"
            : timeline.ProgressText is { Length: > 0 } progress ? progress
            : activeTool is not null ? $"Running {activeTool.ToolName ?? "tool"}"
            : timeline.ActiveReasoningId is not null ? "Thinking"
            : timeline.ActiveAssistantId is not null ? "Responding"
            : currentReasoning ? "Thinking" : "Working";
        var retryAt = label == RetryLabel && timeline.ProgressRetryAtMs is { } retryAtMs
            ? DateTimeOffset.FromUnixTimeMilliseconds(retryAtMs) : (DateTimeOffset?)null;
        return new(timeline.TurnStartedAt, timeline.TurnEndedAt, label, timeline.TurnActive, waiting,
            timeline.RunUsage?.OutputTokens, retryAt);
    }

    internal static string RetryLabelText(ChatRunProgressPresentation progress, DateTimeOffset now,
        Func<string, string, string> localize)
    {
        if (progress.RetryAt is { } retryAt)
        {
            var seconds = (long)Math.Ceiling((retryAt - now).TotalMilliseconds / 1000);
            if (seconds > 0)
                return string.Format(localize("Chat_Run_RetryIn", "Retrying in {0}s"), seconds);
        }
        return localize("Chat_Run_Retry", RetryLabel);
    }

    internal static string Elapsed(DateTimeOffset? start, DateTimeOffset now, Func<string, string>? getString = null)
    {
        if (start is null)
            return "";
        return ElapsedMilliseconds(Math.Max(0, (now - start.Value).TotalMilliseconds), getString);
    }

    internal static string ElapsedMilliseconds(double milliseconds, Func<string, string>? getString = null)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0)
            return "";
        var seconds = Math.Floor(milliseconds / 1000);
        var (key, fallback) = seconds >= 3600
            ? ("Chat_Run_ElapsedHours", "{0}h {1}m {2}s")
            : seconds >= 60
                ? ("Chat_Run_ElapsedMinutes", "{1}m {2}s")
                : ("Chat_Run_ElapsedSeconds", "{2}s");
        var format = (getString ?? LocalizationHelper.GetString)(key);
        if (string.IsNullOrEmpty(format) || format == key)
            format = fallback;
        return string.Format(format, Math.Floor(seconds / 3600), (int)(Math.Floor(seconds / 60) % 60), (int)(seconds % 60));
    }
}
