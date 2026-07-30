using OpenClaw.Chat;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

public static class SessionVisibilityFilter
{
    public static IEnumerable<SessionInfo> VisibleSessions(IEnumerable<SessionInfo> sessions, bool showEnded)
        => showEnded ? sessions : sessions.Where(IsVisibleWhenEndedHidden);

    public static bool IsVisibleWhenEndedHidden(SessionInfo session)
        => !IsEnded(session);

    public static bool IsEnded(SessionInfo session)
    {
        if (session.AbortedLastRun)
            return false;

        var status = session.Status?.Trim();
        return string.Equals(status, "done", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "killed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "timeout", StringComparison.OrdinalIgnoreCase);
    }

    public static ChatThreadStatus ToChatThreadStatus(SessionInfo session)
        => IsEnded(session) ? ChatThreadStatus.Ended : ChatThreadStatus.Running;

    public static IEnumerable<ChatThread> VisibleChatPickerThreads(
        IEnumerable<ChatThread> threads,
        string? activeThreadId = null)
        => threads.Where(thread => IsVisibleInChatPicker(thread, activeThreadId));

    public static bool IsVisibleInChatPicker(ChatThread thread, string? activeThreadId = null)
        => string.Equals(thread.Id, activeThreadId, StringComparison.Ordinal)
            || thread.Activity != ChatActivity.Idle
            || thread.InputTokens > 0
            || thread.OutputTokens > 0
            || thread.TotalTokens > 0;

    public static string ResolveActiveChannel(string activeChannel, IEnumerable<string> visibleChannels)
    {
        if (!string.Equals(activeChannel, "all", StringComparison.OrdinalIgnoreCase)
            && visibleChannels.Any(channel => string.Equals(channel, activeChannel, StringComparison.OrdinalIgnoreCase)))
        {
            return activeChannel;
        }

        return "all";
    }
}
