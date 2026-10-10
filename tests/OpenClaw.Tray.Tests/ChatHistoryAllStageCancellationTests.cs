using System.Collections.Immutable;
using OpenClaw.Chat;
using OpenClawTray.Chat;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// ALL-STAGE bounded cancellation over the ACTUAL older-merge reconstruction production paths: the initial
/// older/existing metadata copies, the stage entry/exit checks, the final active-tool-tracking rebuild given
/// the source-owned optional CancellationToken, and the immutable metadata-map conversion. Each measurable
/// test asserts cancellation is observed within 64 real entries of the trip point - no counter-only
/// abstraction and no full scan delegated to the UI thread.
/// </summary>
public class ChatHistoryAllStageCancellationTests
{
    private static ChatTimelineState StateWith(params ChatTimelineItem[] items)
    {
        var entries = ImmutableList<ChatTimelineItem>.Empty;
        foreach (var item in items)
            entries = entries.Add(item);
        return ChatTimelineState.Initial() with { Entries = entries, NextId = items.Length + 1 };
    }

    private static ChatHistoryRebuildPlan PlanOf(ChatTimelineState timeline) =>
        new("sess-1", timeline, new Dictionary<string, ChatEntryMetadata>(), 0);

    [Fact]
    public void OlderMerge_PreCancelledTinyMerge_ThrowsAtStageEntry()
    {
        var existing = StateWith(new ChatTimelineItem("e1", ChatTimelineItemKind.User, "tail"));
        var older = PlanOf(StateWith(new ChatTimelineItem("e9", ChatTimelineItemKind.User, "older")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ChatHistoryState.MergeOlderPage(
                older, existing, new Dictionary<string, ChatEntryMetadata>(), cts.Token));
    }

    [Fact]
    public void CopyMetadataCancellable_ObservesCancellationWithin64Entries()
    {
        using var cts = new CancellationTokenSource();
        var yielded = 0;
        IEnumerable<KeyValuePair<string, ChatEntryMetadata>> Source()
        {
            for (var i = 0; i < 4000; i++)
            {
                if (i == 500)
                    cts.Cancel();   // trip inside a real full-history copy
                yielded++;
                yield return new KeyValuePair<string, ChatEntryMetadata>($"k{i}", new ChatEntryMetadata(null, null));
            }
        }

        Assert.Throws<OperationCanceledException>(() =>
            ChatHistoryState.CopyMetadataCancellable(Source(), cts.Token));
        Assert.True(yielded <= 500 + 64, $"cancellation observed after {yielded} entries (bound 564)");
    }

    [Fact]
    public void ToImmutableMetadataCancellable_ObservesCancellationWithin64Entries()
    {
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ChatHistoryState.ToImmutableMetadataCancellable(
                new Dictionary<string, ChatEntryMetadata> { ["e1"] = new(null, null) },
                preCancelled.Token));

        using var cts = new CancellationTokenSource();
        var yielded = 0;
        IEnumerable<KeyValuePair<string, ChatEntryMetadata>> Source()
        {
            for (var i = 0; i < 4000; i++)
            {
                if (i == 700)
                    cts.Cancel();
                yielded++;
                yield return new KeyValuePair<string, ChatEntryMetadata>($"k{i}", new ChatEntryMetadata(null, null));
            }
        }

        Assert.Throws<OperationCanceledException>(() =>
            ChatHistoryState.ToImmutableMetadataCancellable(Source(), cts.Token));
        Assert.True(yielded <= 700 + 64, $"immutable conversion observed cancellation after {yielded} entries");
    }

    [Fact]
    public void RebuildActiveToolTracking_PreCancelled_ThrowsAtStageEntry()
    {
        var state = StateWith(new ChatTimelineItem(
            "e1", ChatTimelineItemKind.ToolCall, "call",
            ToolResult: ChatToolCallStatus.InProgress,
            ToolCallId: "call-1"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ChatTimelineReducer.RebuildActiveToolTracking(state, cts.Token));
        // The live (non-cancelled) projection still rebuilds correctly through the same path.
        Assert.Equal("e1",
            ChatTimelineReducer.RebuildActiveToolTracking(state, CancellationToken.None).ActiveToolCallId);
    }

    [Fact]
    public void ImmutableMetadataRoot_IsCopyOnWrite_UnchangedAfterLaterWrite()
    {
        var root = ChatHistoryState.ToImmutableMetadataCancellable(
            new Dictionary<string, ChatEntryMetadata> { ["e1"] = new(null, null) },
            CancellationToken.None);
        var afterWrite = root.SetItem("e2", new ChatEntryMetadata(null, null));
        Assert.False(ReferenceEquals(root, afterWrite));
        // The captured immutable root is UNCHANGED by the later write (COW); the write is visible only
        // through the returned snapshot.
        Assert.True(root.ContainsKey("e1"));
        Assert.False(root.ContainsKey("e2"));
        Assert.True(afterWrite.ContainsKey("e2"));
    }

    [Fact]
    public void MergeOlderPage_UpdatedUsageAndPermissionEntry_SurvivesRebase()
    {
        var existing = StateWith(
            new ChatTimelineItem("e1", ChatTimelineItemKind.Assistant, "tail",
                PermissionDecision: ChatPermissionDecision.Allowed));
        var olderTimeline = StateWith(new ChatTimelineItem("e9", ChatTimelineItemKind.User, "older"));
        var olderPlan = new ChatHistoryRebuildPlan(
            "sess-1",
            olderTimeline,
            new Dictionary<string, ChatEntryMetadata> { ["e9"] = new(null, null) },
            0);
        var existingMeta = new Dictionary<string, ChatEntryMetadata>
        {
            ["e1"] = new ChatEntryMetadata(null, null, InputTokens: 5, OutputTokens: 5, UsageContributionTokens: 7),
        };

        var (timeline, merged) = ChatHistoryState.MergeOlderPage(
            olderPlan, existing, existingMeta, CancellationToken.None);

        // The updated usage metadata survives the rebase unchanged, and the older entry is unioned in.
        Assert.Equal(7, merged["e1"].UsageContributionTokens);
        Assert.True(merged.ContainsKey("e2"));
        // The retained entry keeps its permission decision (not the older page).
        Assert.Equal(ChatPermissionDecision.Allowed,
            timeline.Entries.Single(entry => entry.Id == "e1").PermissionDecision);
        Assert.Single(timeline.Entries, entry => entry.Text == "older");
    }

    [Fact]
    public void CopyMetadataCancellable_CancellationDuringShortCopy_IsObservedAtStageExit()
    {
        using var cts = new CancellationTokenSource();
        var yielded = 0;
        IEnumerable<KeyValuePair<string, ChatEntryMetadata>> Source()
        {
            for (var i = 0; i < 5; i++)   // fewer than the 64-entry checkpoint interval
            {
                if (i == 2)
                    cts.Cancel();   // cancels DURING enumeration, never at a 64-boundary
                yielded++;
                yield return new KeyValuePair<string, ChatEntryMetadata>($"k{i}", new ChatEntryMetadata(null, null));
            }
        }

        // Without the stage-exit check this short copy would return normally despite the cancellation.
        Assert.Throws<OperationCanceledException>(() =>
            ChatHistoryState.CopyMetadataCancellable(Source(), cts.Token));
        Assert.Equal(5, yielded);
    }

    [Fact]
    public void RebuildActiveToolTracking_LargeStateInWorkCancellation_IsBoundedWithin64Operations()
    {
        // 400 tool-call entries x 40 correlations each: a per-loop counter would allow ~63 correlations
        // between checks, i.e. thousands of real operations. The SHARED budget must bound it to 64.
        var entries = ImmutableList<ChatTimelineItem>.Empty;
        for (var i = 0; i < 400; i++)
        {
            var correlations = ImmutableHashSet<string>.Empty;
            for (var c = 0; c < 40; c++)
                correlations = correlations.Add($"c{c}");
            entries = entries.Add(new ChatTimelineItem(
                $"e{i}", ChatTimelineItemKind.ToolCall, "call",
                ToolResult: ChatToolCallStatus.InProgress,
                ToolCallId: $"call-{i}",
                ToolCorrelationIds: correlations));
        }
        var state = ChatTimelineState.Initial() with { Entries = entries, NextId = entries.Count + 1 };

        using var cts = new CancellationTokenSource();
        long lastObserved = 0;
        var cancelledInWork = false;
        void Observe(long operations)
        {
            lastObserved = operations;
            if (!cancelledInWork && operations >= 128)
            {
                cancelledInWork = true;
                cts.Cancel();   // deterministic trip IN WORK at a known operation count
            }
        }

        Assert.Throws<OperationCanceledException>(() =>
            ChatTimelineReducer.RebuildActiveToolTracking(state, cts.Token, Observe));
        Assert.True(cancelledInWork, "cancellation must occur during the scan, not before it");
        Assert.True(lastObserved <= 128 + 64, $"cancellation observed after {lastObserved} operations");
    }
}