using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.FunctionalUI;
using static OpenClawTray.FunctionalUI.Factories;

namespace OpenClawTray.Chat;

/// <summary>
/// Keyed, renderer-agnostic row descriptor consumed by the virtualized chat host.
/// The row keeps identity separate from the bubble/tool/permission UI that renders it.
/// </summary>
public sealed record ChatTimelineRow(string Key, Func<Element> Render, double EstimatedHeight = 0);

/// <summary>
/// Snapshot passed from <see cref="ChatTimeline"/> into the native
/// virtualized host. It contains the rendered row descriptors plus the scroll
/// anchors needed to preserve existing chat timeline behavior.
/// </summary>
public sealed record ChatTimelineView(
    IReadOnlyList<ChatTimelineRow> Rows,
    string? SessionId,
    int EntryCount,
    string? FirstEntryId,
    string? LastEntryId,
    Func<string, bool> ContainsEntryId,
    bool HasMoreHistory,
    Action? OnLoadMoreHistory,
    int ScrollToBottomToken,
    int SuppressAutoFollowToken)
{
    public static ChatTimelineView Empty { get; } = new(
        Array.Empty<ChatTimelineRow>(),
        null,
        0,
        null,
        null,
        static _ => false,
        false,
        null,
        0,
        0);
}

public partial class ChatTimeline
{
    private static ChatTimelineView BuildVirtualizedTimelineView(
        ChatTimelineProps props,
        IReadOnlyList<Element> timelineRows,
        Element loadMoreButton,
        int entryCount,
        string? firstEntryId,
        string? lastEntryId,
        int suppressAutoFollowToken)
    {
        var virtualizedRows = new List<ChatTimelineRow>(timelineRows.Count + 3);
        if (props.HasMoreHistory)
        {
            var loadMoreRow = loadMoreButton;
            virtualizedRows.Add(new ChatTimelineRow(
                "chrome:load-more",
                () => loadMoreRow));
        }

        virtualizedRows.Add(new ChatTimelineRow(
            "chrome:top-spacer",
            () => Border(Empty()).Height(20)));

        for (var rowIndex = 0; rowIndex < timelineRows.Count; rowIndex++)
        {
            var rowElement = timelineRows[rowIndex];
            var rowKey = string.IsNullOrEmpty(rowElement.Key)
                ? $"row:index:{rowIndex}"
                : $"row:{rowElement.Key}";
            virtualizedRows.Add(new ChatTimelineRow(rowKey, () => rowElement, EstimatedHeight: 64));
        }

        virtualizedRows.Add(new ChatTimelineRow(
            "chrome:bottom-spacer",
            () => Border(Empty()).Height(24)));

        return new ChatTimelineView(
            virtualizedRows,
            props.SessionId,
            entryCount,
            firstEntryId,
            lastEntryId,
            id => props.Entries.Any(e => string.Equals(e.Id, id, StringComparison.Ordinal)),
            props.HasMoreHistory,
            props.OnLoadMoreHistory,
            props.ScrollToBottomToken,
            suppressAutoFollowToken);
    }
}
