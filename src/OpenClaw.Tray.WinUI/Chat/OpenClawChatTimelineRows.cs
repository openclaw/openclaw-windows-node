using OpenClawTray.FunctionalUI.Core;

namespace OpenClawTray.Chat;

/// <summary>
/// Keyed, renderer-agnostic row descriptor consumed by the virtualized chat host.
/// The row keeps identity separate from the bubble/tool/permission UI that renders it.
/// </summary>
public sealed record OpenClawChatTimelineRow(string Key, Func<Element> Render, double EstimatedHeight = 0);

/// <summary>
/// Snapshot passed from <see cref="OpenClawChatTimeline"/> into the native
/// virtualized host. It contains the rendered row descriptors plus the scroll
/// anchors needed to preserve existing chat timeline behavior.
/// </summary>
public sealed record OpenClawChatTimelineView(
    IReadOnlyList<OpenClawChatTimelineRow> Rows,
    string? SessionId,
    int EntryCount,
    string? FirstEntryId,
    string? LastEntryId,
    IReadOnlySet<string> EntryIds,
    bool HasMoreHistory,
    Action? OnLoadMoreHistory,
    int ScrollToBottomToken,
    int SuppressAutoFollowToken)
{
    public static OpenClawChatTimelineView Empty { get; } = new(
        Array.Empty<OpenClawChatTimelineRow>(),
        null,
        0,
        null,
        null,
        new HashSet<string>(StringComparer.Ordinal),
        false,
        null,
        0,
        0);
}
