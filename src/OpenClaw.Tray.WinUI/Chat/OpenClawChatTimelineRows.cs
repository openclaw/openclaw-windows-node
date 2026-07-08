using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.FunctionalUI;
using static OpenClawTray.FunctionalUI.Factories;

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
    Func<string, bool> ContainsEntryId,
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
        static _ => false,
        false,
        null,
        0,
        0);
}

public partial class OpenClawChatTimeline
{
    private static OpenClawChatTimelineView BuildVirtualizedTimelineView(
        OpenClawChatTimelineProps props,
        IReadOnlyList<Element> timelineRows,
        Element loadMoreButton,
        int entryCount,
        string? firstEntryId,
        string? lastEntryId,
        int suppressAutoFollowToken)
    {
        var virtualizedRows = new List<OpenClawChatTimelineRow>(timelineRows.Count + 3);
        if (props.HasMoreHistory)
        {
            var loadMoreRow = loadMoreButton;
            virtualizedRows.Add(new OpenClawChatTimelineRow(
                "chrome:load-more",
                () => loadMoreRow));
        }

        virtualizedRows.Add(new OpenClawChatTimelineRow(
            "chrome:top-spacer",
            () => Border(Empty()).Height(20)));

        for (var rowIndex = 0; rowIndex < timelineRows.Count; rowIndex++)
        {
            var rowElement = timelineRows[rowIndex];
            var rowKey = string.IsNullOrEmpty(rowElement.Key)
                ? $"row:index:{rowIndex}"
                : $"row:{rowElement.Key}";
            virtualizedRows.Add(new OpenClawChatTimelineRow(rowKey, () => rowElement, EstimatedHeight: 64));
        }

        virtualizedRows.Add(new OpenClawChatTimelineRow(
            "chrome:bottom-spacer",
            () => Border(Empty()).Height(24)));

        return new OpenClawChatTimelineView(
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
