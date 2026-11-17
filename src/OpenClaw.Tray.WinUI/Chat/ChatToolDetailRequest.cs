// Identity-only requests for native tool-detail tabs. Capture from a rendered
// timeline row, then resolve against the current history before opening/rendering.
using OpenClaw.Chat;

namespace OpenClawTray.Chat;

/// <summary>Fences tool entry identities that can be reused by history replay.</summary>
public sealed record ChatToolDetailRequest(
    string SessionKey,
    long TimelineGeneration,
    string EntryId,
    string? RunId,
    long HistoryRevision = 0,
    string? ToolCallId = null,
    string? ToolName = null)
{
    /// <summary>Captures source identity without adding run metadata to rendered row keys or labels.</summary>
    public static ChatToolDetailRequest Capture(
        string sessionKey, long generation, long historyRevision, ChatTimelineItem entry) =>
        new(sessionKey, generation, entry.Id, entry.ToolRunId, historyRevision, entry.ToolCallId, entry.ToolName);

    /// <summary>Resolves an unambiguous entry without retaining its input or output.</summary>
    public ChatTimelineItem? Resolve(
        string? sessionKey, long generation, long historyRevision, IReadOnlyList<ChatTimelineItem> entries)
    {
        if (string.IsNullOrWhiteSpace(SessionKey) || SessionKey != sessionKey ||
            TimelineGeneration != generation || HistoryRevision != historyRevision)
            return null;

        ChatTimelineItem? found = null;
        foreach (var entry in entries)
        {
            if (entry.Kind != ChatTimelineItemKind.ToolCall || entry.Id != EntryId || entry.ToolRunId != RunId ||
                (!string.IsNullOrWhiteSpace(ToolCallId) && entry.ToolCallId != ToolCallId))
                continue;
            if (found is not null) return null;
            found = entry;
        }
        return found;
    }
}
