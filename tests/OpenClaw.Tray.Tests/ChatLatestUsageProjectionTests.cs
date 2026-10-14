using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenClaw.Chat;
using OpenClawTray.Chat;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Behavioral proof for the full-retained latest qualifying usage projection: windowing never loses the
/// latest usable assistant usage, non-qualifying recent rows do not hide a prior usable one, metadata-only
/// and identity changes invalidate, and an unchanged input is an O(1) hit (no per-render rescan). Work is
/// measured via the returned ScannedEntries, not assigned from a result count.
/// </summary>
public class ChatLatestUsageProjectionTests
{
    private static ChatTimelineState Timeline(IEnumerable<(string Id, ChatTimelineItemKind Kind)> items)
    {
        var entries = ImmutableList.CreateRange(items.Select(i =>
            new ChatTimelineItem(i.Id, i.Kind, i.Id)));
        return ChatTimelineState.Initial() with { Entries = entries, NextId = entries.Count + 1 };
    }

    private static ChatEntryMetadata Usage(int input, int output, long context) =>
        new(null, null, InputTokens: input, OutputTokens: output, ContextTokens: context);

    private static ChatEntryMetadata NoUsage() => new(null, null);

    // Adopted from Mini-Actual-Retained-Version-Counterexample (was FAILING at d81469ed): a
    // metadata-only contribution must invalidate the older-merge snapshot even when the Entries
    // reference is unchanged.
    [Fact]
    public void Mini_RetainedVersion_MetadataOnlyContributionMustInvalidateMergeSnapshot()
    {
        var state = new ChatConversationState(OpenClaw.Shared.ConnectionStatus.Connected, null, null);
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        state.Load([new OpenClaw.Shared.SessionInfo { Key = "main", IsMain = true }], context);
        var initial = Usage(10, 5, 1000);
        state.ApplyEvent("main", new ChatMessageEvent("response"), initial, context);
        state.SnapshotAssistantUsageContribution("main", initial, context);
        var before = state.RetainedVersion("main");
        var timeline = state.Snapshot(context).Timelines["main"];
        var oldMetadata = state.GetEntryMetadataById("main", "e1");
        Assert.NotNull(state.SnapshotAssistantUsageContribution("main", Usage(900, 100, 2000), context));
        Assert.Same(timeline.Entries, state.Snapshot(context).Timelines["main"].Entries);
        Assert.NotEqual(oldMetadata, state.GetEntryMetadataById("main", "e1"));
        Assert.True(state.RetainedVersion("main") > before);
    }

    // Adopted from Mini-Actual-Ingest-Streaming-Counterexample (was FAILING at fd3f8d63).
    [Fact]
    public void ActualState_StreamingReconcileWithUnchangedUsage_DoesNotInvalidateCompletedScan()
    {
        var state = new ChatConversationState(OpenClaw.Shared.ConnectionStatus.Connected, null, null);
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        state.Load([new OpenClaw.Shared.SessionInfo { Key = "main", IsMain = true }], context);
        var metadata = Usage(10, 5, 1000);
        state.ApplyEvent("main", new ChatMessageEvent("first", IsStreaming: true), metadata, context);
        var before = state.AdvanceLatestUsage("main");
        Assert.True(before.Complete);
        var timeline = state.Snapshot(context).Timelines["main"];
        state.ApplyEvent("main", new ChatMessageEvent("first updated", ReconcilePrevious: true, IsStreaming: true), metadata, context);
        var afterTimeline = state.Snapshot(context).Timelines["main"];
        Assert.Equal(timeline.Entries.Count, afterTimeline.Entries.Count);
        Assert.Equal(timeline.Entries[0].Id, afterTimeline.Entries[0].Id);
        Assert.Equal("first updated", afterTimeline.Entries[0].Text);
        var after = state.AdvanceLatestUsage("main", maxSteps: 0);
        Assert.Equal(before.Summary, after.Summary);
        Assert.True(after.Complete);
    }

    // Adopted from Mini-Usage-Resume-Counterexamples (both were FAILING at ad1d5d27).
    [Fact]
    public void ActualState_CompletedEmptyScanThenAssistant_AdoptsNewUsage()
    {
        var state = new ChatConversationState(OpenClaw.Shared.ConnectionStatus.Connected, null, null);
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        state.Load([new OpenClaw.Shared.SessionInfo { Key = "main", IsMain = true }], context);
        Assert.True(state.AdvanceLatestUsage("main").Complete);
        var metadata = Usage(10, 5, 1000);
        state.ApplyEvent("main", new ChatMessageEvent("response"), metadata, context);
        var result = state.AdvanceLatestUsage("main");
        Assert.True(result.Complete);
        Assert.Equal(ChatUsageFormatter.Format(metadata), result.Summary);
    }

    [Fact]
    public void ActualState_RevisionRestart_PreservesPreviousSameIdentitySummaryWhilePending()
    {
        var state = new ChatConversationState(OpenClaw.Shared.ConnectionStatus.Connected, null, null);
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        state.Load([new OpenClaw.Shared.SessionInfo { Key = "main", IsMain = true }], context);
        var metadata = Usage(10, 5, 1000);
        state.ApplyEvent("main", new ChatMessageEvent("response"), metadata, context);
        state.SnapshotAssistantUsageContribution("main", metadata, context);
        var previous = state.AdvanceLatestUsage("main");
        Assert.NotNull(previous.Summary);
        state.SnapshotAssistantUsageContribution("main", Usage(900, 100, 2000), context);
        var pending = state.AdvanceLatestUsage("main", maxSteps: 0);
        Assert.False(pending.Complete);
        Assert.Equal(previous.Summary, pending.Summary);
    }

    // Mini follow-up: the ACTUAL state/provider path must be coherent and bounded (no capture/release
    // window in which newer metadata pairs with an older timeline/revision; no unbounded first scan).
    [Fact]
    public void ActualState_LatestUsage_MetadataUpdateAdoptsTheNewRevision()
    {
        var state = new ChatConversationState(OpenClaw.Shared.ConnectionStatus.Connected, null, null);
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        state.Load([new OpenClaw.Shared.SessionInfo { Key = "main", IsMain = true }], context);
        var initial = Usage(10, 5, 1000);
        state.ApplyEvent("main", new ChatMessageEvent("response"), initial, context);
        state.SnapshotAssistantUsageContribution("main", initial, context);

        var before = state.AdvanceLatestUsage("main");
        Assert.True(before.Complete);
        Assert.NotNull(before.Summary);

        var update = Usage(900, 100, 2000);
        Assert.NotNull(state.SnapshotAssistantUsageContribution("main", update, context));

        var after = state.AdvanceLatestUsage("main");   // coherence: same gate, new revision
        Assert.True(after.Complete);
        Assert.NotEqual(before.Summary, after.Summary);
        Assert.Equal(
            ChatUsageFormatter.Format(state.GetEntryMetadataById("main", after.EntryId!)!),
            after.Summary);
    }

    [Fact]
    public void ActualState_LatestUsage_InitializationIsBoundedAndResumable()
    {
        var state = new ChatConversationState(OpenClaw.Shared.ConnectionStatus.Connected, null, null);
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        state.Load([new OpenClaw.Shared.SessionInfo { Key = "main", IsMain = true }], context);
        state.ApplyEvent("main", new ChatMessageEvent("response"), Usage(10, 5, 1000), context);
        Assert.NotEmpty(state.Snapshot(context).Timelines["main"].Entries);

        // A zero-step call must remain incomplete: the scan is bounded/resumable, never a full-history
        // single pass. (Proves the step bound independent of the retained length.)
        var bounded = state.AdvanceLatestUsage("main", maxSteps: 0);
        Assert.False(bounded.Complete);

        var complete = bounded;
        for (var i = 0; i < 1_000 && !complete.Complete; i++)
            complete = state.AdvanceLatestUsage("main", maxSteps: 1);
        Assert.True(complete.Complete);
    }

    // Adopted from Mini-U1-Cross-Window-Consumer-Review: an earlier window must NOT label its
    // slice-local last assistant with the newer GLOBAL usage summary.
    [Fact]
    public void UsageAttribution_EarlierWindowRowIsNotLabelledWithGlobalUsage()
    {
        // Global latest qualifying assistant is e9; an earlier window only shows e1..e3.
        var attribution = ChatTimelineUsageAttribution.Resolve("e9", "e3");
        Assert.Equal("e9", attribution);
        Assert.False(ChatTimelineUsageAttribution.IsGlobalLatestAssistant("e3", attribution));
        Assert.True(ChatTimelineUsageAttribution.IsGlobalLatestAssistant("e9", attribution));

        // Unknown global id: fall back to the window-local latest (previous behaviour preserved).
        var fallback = ChatTimelineUsageAttribution.Resolve(null, "e3");
        Assert.True(ChatTimelineUsageAttribution.IsGlobalLatestAssistant("e3", fallback));
    }

    // Adopted from Mini-Usage-Metadata-Counterexample (was FAILING at 9551871f: stale 15/1.0K).
    [Fact]
    public void ActualState_MetadataOnlyUsageContribution_RefreshesProjection()
    {
        var state = new ChatConversationState(OpenClaw.Shared.ConnectionStatus.Connected, null, null);
        var context = new ChatProjectionContext("main", HasHandshakeSnapshot: true);
        state.Load([new OpenClaw.Shared.SessionInfo { Key = "main", IsMain = true }], context);
        var initial = Usage(10, 5, 1000);
        state.ApplyEvent("main", new ChatMessageEvent("response"), initial, context);
        state.SnapshotAssistantUsageContribution("main", initial, context);
        var timeline = state.Snapshot(context).Timelines["main"];
        var snapshot = state.TryCaptureUsageSnapshot("main");
        var projection = new ChatLatestUsageProjection();
        var before = projection.Get("main", snapshot.AcceptedIdentity, snapshot.UsageRevision, timeline,
            id => state.GetEntryMetadataById("main", id));
        Assert.NotNull(before.Summary);

        var update = Usage(900, 100, 2000);
        Assert.NotNull(state.SnapshotAssistantUsageContribution("main", update, context));
        var nextTimeline = state.Snapshot(context).Timelines["main"];
        Assert.Same(timeline.Entries, nextTimeline.Entries);   // unchanged Entries
        var next = state.TryCaptureUsageSnapshot("main");
        var after = projection.Get("main", next.AcceptedIdentity, next.UsageRevision, nextTimeline,
            id => state.GetEntryMetadataById("main", id));

        var expected = ChatUsageFormatter.Format(state.GetEntryMetadataById("main", "e1")!);
        Assert.NotEqual(before.Summary, expected);
        Assert.Equal(expected, after.Summary);
    }

    // Adopted from Mini-Usage-Streaming-Counterexample (was FAILING at 5ac4537b: 19,900 visits).
    [Fact]
    public void StreamingOnlyTextEdit_DoesNotRescanRetainedHistory()
    {
        var timeline = Timeline(Enumerable.Range(1, 20_000).Select(n =>
            ($"e{n}", n == 101 ? ChatTimelineItemKind.Assistant : ChatTimelineItemKind.User)));
        var metadata = new Dictionary<string, ChatEntryMetadata> { ["e101"] = Usage(100, 50, 2_000) };
        var projection = new ChatLatestUsageProjection();
        var before = projection.Get("main", "sess-1", 1, timeline,
            id => metadata.TryGetValue(id, out var m) ? m : null);
        var edited = timeline with
        {
            Entries = timeline.Entries.SetItem(19_999, timeline.Entries[19_999] with { Text = "stream update" }),
        };
        var after = projection.Get("main", "sess-1", 1, edited,
            id => metadata.TryGetValue(id, out var m) ? m : null);

        Assert.Equal(before.Summary, after.Summary);
        Assert.Equal(0, after.ScannedEntries);   // text-only edit: O(1) hit, no retained traversal
    }

    [Fact]
    public void LatestQualifyingUsage_OutsideDisplayWindow_IsStillResolved()
    {
        var items = new List<(string, ChatTimelineItemKind)>();
        for (var n = 1; n <= 20_000; n++)
            items.Add(($"e{n}", n == 101 ? ChatTimelineItemKind.Assistant : ChatTimelineItemKind.User));
        var timeline = Timeline(items);
        var metadata = new Dictionary<string, ChatEntryMetadata> { ["e101"] = Usage(100, 50, 2_000) };
        var projection = new ChatLatestUsageProjection();

        var result = projection.Get("main", "sess-1", 1, timeline,
            id => metadata.TryGetValue(id, out var m) ? m : null);

        Assert.NotNull(result.Summary);          // usage from far outside a 400-entry window still shows
        Assert.Equal("e101", result.EntryId);
        Assert.Equal(19_900, result.ScannedEntries);   // honest: bounded by distance to that entry

        // Unchanged input is an O(1) hit: no rescan on a plain re-render.
        var again = projection.Get("main", "sess-1", 1, timeline,
            id => metadata.TryGetValue(id, out var m) ? m : null);
        Assert.Equal(0, again.ScannedEntries);
        Assert.Equal(result.Summary, again.Summary);
    }

    [Fact]
    public void NonQualifyingRecentAssistant_DoesNotHidePriorUsableUsage()
    {
        var timeline = Timeline([
            ("e1", ChatTimelineItemKind.Assistant),   // older, usable
            ("e2", ChatTimelineItemKind.User),
            ("e3", ChatTimelineItemKind.Assistant)]); // newer, no usage
        var metadata = new Dictionary<string, ChatEntryMetadata>
        {
            ["e1"] = Usage(10, 5, 1_000),
            ["e3"] = NoUsage(),
        };
        var projection = new ChatLatestUsageProjection();

        var result = projection.Get("main", "sess-1", 1, timeline,
            id => metadata.TryGetValue(id, out var m) ? m : null);

        Assert.Equal("e1", result.EntryId);
        Assert.NotNull(result.Summary);
    }

    [Fact]
    public void MetadataOnlyRevisionChange_InvalidatesTheCachedProjection()
    {
        var timeline = Timeline([("e1", ChatTimelineItemKind.Assistant)]);
        var metadata = new Dictionary<string, ChatEntryMetadata> { ["e1"] = Usage(10, 5, 1_000) };
        var projection = new ChatLatestUsageProjection();

        projection.Get("main", "sess-1", 1, timeline,
            id => metadata.TryGetValue(id, out var m) ? m : null);
        var after = projection.Get("main", "sess-1", 2, timeline,
            id => metadata.TryGetValue(id, out var m) ? m : null);

        Assert.True(after.ScannedEntries > 0);   // metadata-only update recomputes
    }

    [Fact]
    public void NewerQualifyingAssistant_OverridesTheOlderOne()
    {
        var timeline = Timeline([
            ("e1", ChatTimelineItemKind.Assistant),
            ("e2", ChatTimelineItemKind.Assistant)]);
        var metadata = new Dictionary<string, ChatEntryMetadata>
        {
            ["e1"] = Usage(10, 5, 1_000),
            ["e2"] = Usage(900, 100, 2_000),
        };
        var projection = new ChatLatestUsageProjection();

        var result = projection.Get("main", "sess-1", 1, timeline,
            id => metadata.TryGetValue(id, out var m) ? m : null);

        Assert.Equal("e2", result.EntryId);
        Assert.Equal(1, result.ScannedEntries);   // newest entry qualifies immediately
    }

    [Fact]
    public void EmptyOrReset_ReturnsNoUsage()
    {
        var projection = new ChatLatestUsageProjection();
        var result = projection.Get("main", "sess-1", 1, ChatTimelineState.Initial(), _ => null);

        Assert.Null(result.Summary);
        Assert.Equal(0, result.ScannedEntries);
    }

    [Fact]
    public void SessionIdentityChange_Recomputes()
    {
        var timeline = Timeline([("e1", ChatTimelineItemKind.Assistant)]);
        var metadata = new Dictionary<string, ChatEntryMetadata> { ["e1"] = Usage(10, 5, 1_000) };
        var projection = new ChatLatestUsageProjection();

        projection.Get("main", "sess-A", 1, timeline, id => metadata.TryGetValue(id, out var m) ? m : null);
        var after = projection.Get("main", "sess-B", 1, timeline,
            id => metadata.TryGetValue(id, out var m) ? m : null);

        Assert.True(after.ScannedEntries > 0);
    }
}
