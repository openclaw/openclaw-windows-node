using OpenClaw.Chat;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

public static class SessionVisibilityFilter
{
    public static IEnumerable<SessionInfo> VisibleSessions(IEnumerable<SessionInfo> sessions, bool showCompleted)
        => showCompleted ? sessions : sessions.Where(IsVisibleWhenCompletedHidden);

    public static bool IsVisibleWhenCompletedHidden(SessionInfo session)
        => !IsCleanCompleted(session);

    public static bool IsCleanCompleted(SessionInfo session)
    {
        if (session.AbortedLastRun)
            return false;

        var status = session.Status?.Trim();
        return string.Equals(status, "done", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);
    }

    public static ChatThreadStatus ToChatThreadStatus(SessionInfo session)
        => IsCleanCompleted(session) ? ChatThreadStatus.Completed : ChatThreadStatus.Running;

    public static IEnumerable<ChatThread> VisibleChatPickerThreads(IEnumerable<ChatThread> threads)
        => threads.Where(IsVisibleInChatPicker);

    public static bool IsVisibleInChatPicker(ChatThread thread)
        => thread.Status != ChatThreadStatus.Completed;

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
