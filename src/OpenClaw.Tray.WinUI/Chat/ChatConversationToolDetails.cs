using OpenClaw.Chat;

namespace OpenClawTray.Chat;

/// <summary>Retains identity-only detail requests. Tool content always comes from the current snapshot.</summary>
internal sealed class ChatConversationToolDetails
{
    internal const string PanelId = "details";
    private sealed record Selection(ChatConversationContext Context, ChatToolDetailRequest Request);
    private readonly Dictionary<ChatConversationIdentity, Selection> _selections = [];

    internal static bool HasCurrentHistory(bool nativeProvider, string? sessionKey, IReadOnlySet<string>? currentHistory) =>
        !nativeProvider || sessionKey is not null && currentHistory?.Contains(sessionKey) == true;

    internal bool Open(
        ChatConversationPanelState panels,
        ChatConversationContext expectedContext,
        ChatToolDetailRequest request,
        string? sessionKey,
        long timelineGeneration,
        long historyRevision,
        IReadOnlyList<ChatTimelineItem> entries)
    {
        if (!panels.IsReady || !Equals(expectedContext, panels.Context) ||
            expectedContext.Identity.SessionKey != sessionKey ||
            !ChatToolActivityPresentation.TryResolveDetails(
                request, sessionKey, timelineGeneration, historyRevision, entries, out _))
            return false;

        _selections[expectedContext.Identity] = new(expectedContext, request);
        return panels.Open(PanelId,
            $"{request.TimelineGeneration}:{request.HistoryRevision}:{request.EntryId}", request.RunId);
    }

    internal bool TryResolve(
        ChatConversationPanelState panels,
        string? sessionKey,
        long timelineGeneration,
        long historyRevision,
        IReadOnlyList<ChatTimelineItem> entries,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ChatTimelineItem? entry)
    {
        entry = null;
        return panels.IsReady && panels.Context is { } context &&
            _selections.TryGetValue(context.Identity, out var selection) &&
            Equals(context, selection.Context) &&
            context.Identity.SessionKey == sessionKey &&
            ChatToolActivityPresentation.TryResolveDetails(
                selection.Request, sessionKey, timelineGeneration, historyRevision, entries, out entry);
    }
}
