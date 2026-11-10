// Projects admitted reasoning occurrences into the native transcript without rewriting history.
// The timeline calls Project with its selected session's reasoning preferences and current run.
using OpenClaw.Chat;
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

internal static class ChatReasoningPresentation
{
    internal static ChatTimelineState Reconcile(
        ChatTimelineState timeline, IReadOnlyDictionary<string, ChatEntryMetadata>? metadata)
    {
        if (timeline.ReasoningStream is not { } stream)
            return timeline;
        var items = stream.Items;
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index].Receipt is { Persisted: false } receipt &&
                timeline.Entries.Any(entry => Matches(entry, metadata, receipt)))
                items = items.SetItem(index, items[index] with { Receipt = receipt with { Persisted = true } });
        }
        return ReferenceEquals(items, stream.Items) ? timeline
            : timeline with { ReasoningStream = stream with { Items = items } };
    }

    private static ChatMessagePresentation? Facts(
        ChatTimelineItem entry, IReadOnlyDictionary<string, ChatEntryMetadata>? metadata) =>
        entry.Presentation ?? metadata?.GetValueOrDefault(entry.Id)?.Presentation;

    private static bool Matches(ChatTimelineItem entry,
        IReadOnlyDictionary<string, ChatEntryMetadata>? metadata, ChatReasoningReceipt receipt) =>
        Facts(entry, metadata) is { IsImported: false } facts &&
        (facts.SourceRole == "assistant" ||
            entry.Kind is ChatTimelineItemKind.Assistant or ChatTimelineItemKind.Reasoning) &&
        facts.RunId == receipt.RunId && facts.MessageId == receipt.MessageId;

    /// <summary>Selects saved thoughts or live previews, handing ownership to exact accepted receipts.</summary>
    internal static IReadOnlyList<ChatTimelineItem> Project(
        IReadOnlyList<ChatTimelineItem> entries,
        ChatReasoningStream? stream,
        bool showReasoning,
        bool showPreview,
        IReadOnlyDictionary<string, ChatEntryMetadata>? metadata = null)
    {
        var rows = entries.Where(entry => entry.Kind != ChatTimelineItemKind.Reasoning ||
            (entry.IsStreaming ? showPreview : showReasoning) && !string.IsNullOrWhiteSpace(entry.Text)).ToList();
        if (!showPreview || stream is null)
            return rows;

        var positions = entries.Select((entry, index) => (entry.Id, index))
            .ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.Ordinal);
        var priorPreviews = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var itemIndex = 0; itemIndex < stream.Items.Count; itemIndex++)
        {
            var item = stream.Items[itemIndex];
            if (string.IsNullOrWhiteSpace(item.Text))
                continue;
            var saved = item.Receipt is { } receipt
                ? entries.FirstOrDefault(entry => Matches(entry, metadata, receipt))
                : null;
            if (showReasoning && (saved is not null || item.Receipt?.Persisted == true))
                continue;

            var index = saved is not null
                ? rows.FindIndex(row => positions.TryGetValue(row.Id, out var position) && position >= positions[saved.Id])
                : -1;
            if (index < 0 && saved is null && itemIndex < stream.Items.Count - 1 &&
                item.AfterEntryId is { } afterId)
            {
                var anchorId = priorPreviews.GetValueOrDefault(afterId, afterId);
                var anchor = rows.FindIndex(row => row.Id == anchorId &&
                    (Facts(row, metadata)?.RunId == stream.RunId || row.ToolRunId == stream.RunId));
                if (anchor >= 0)
                    index = anchor + 1;
            }
            var id = "thinking:" + System.Text.Json.JsonSerializer.Serialize(new[] { stream.RunId, item.ItemId });
            rows.Insert(index >= 0 ? index : rows.Count,
                new(id, ChatTimelineItemKind.Reasoning, item.Text, IsStreaming: true,
                    Presentation: new(RunId: stream.RunId, BlockId: item.ItemId)));
            if (item.AfterEntryId is { } anchorKey)
                priorPreviews[anchorKey] = id;
        }
        return rows;
    }
}
