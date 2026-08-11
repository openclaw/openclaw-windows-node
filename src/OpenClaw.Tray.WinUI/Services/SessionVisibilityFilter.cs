using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;

namespace OpenClawTray.Services;

public static class SessionVisibilityFilter
{
    public static IEnumerable<SessionInfo> VisibleSessions(IEnumerable<SessionInfo> sessions, bool showCompleted)
        => showCompleted ? sessions : sessions.Where(IsVisibleWhenCompletedHidden);

    public static bool IsVisibleWhenCompletedHidden(SessionInfo session)
        => !IsCompleted(session);

    public static bool IsCompleted(SessionInfo session) => SessionRunState.IsCompleted(session);

    public static ChatThreadStatus ToChatThreadStatus(SessionInfo session)
        => SessionRunState.IsWorking(session) ? ChatThreadStatus.Running : ChatThreadStatus.Created;

    public static ChatActivity ToChatThreadActivity(SessionInfo session)
        => SessionRunState.IsWorking(session) ? ChatActivity.Working : ChatActivity.Idle;

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
