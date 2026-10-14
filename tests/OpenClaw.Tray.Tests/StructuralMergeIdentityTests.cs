using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Direct controls for the STRUCTURAL merge identity (GatewayMessageId, GatewayDisplayItemId) in ChatHistoryState.
/// Non-authoritative live merge isolates identity; authoritative policy is preserved elsewhere.
/// </summary>
public class StructuralMergeIdentityTests
{
    private static ChatTimelineItem Item(string id, ChatTimelineItemKind kind, string text) => new(id, kind, text);

    private static ChatEntryMetadata Meta(string? raw, string? display, int? seq = 1) =>
        new(Timestamp: DateTimeOffset.UnixEpoch, Model: null, GatewayMessageId: raw, OpenClawSeq: seq, GatewayDisplayItemId: display);

    private static ChatTimelineState Timeline(params ChatTimelineItem[] items) =>
        ChatTimelineState.Initial() with { Entries = items.ToImmutableList(), NextId = items.Length + 1 };

    private static ChatHistoryRebuildPlan Plan(ChatTimelineState t, Dictionary<string, ChatEntryMetadata> m) =>
        new(SessionId: null, Timeline: t, Metadata: m, MaxHistorySequence: 0);

    // ---- MergeOlderPage ----

    [Fact]
    public void MergeOlderPage_DistinctDisplayIds_SharingRawIdSurvive()
    {
        // IDENTICAL text/role/seq/time: only the display-item id differs (raw1 preserved).
        var t = Timeline(Item("e1", ChatTimelineItemKind.Assistant, "same"));
        var m = new Dictionary<string, ChatEntryMetadata> { ["e1"] = Meta("raw1", "itemA") };
        var olderT = Timeline(Item("e2", ChatTimelineItemKind.Assistant, "same"));
        var olderM = new Dictionary<string, ChatEntryMetadata> { ["e2"] = Meta("raw1", "itemB") };
        var (timeline, merged) = ChatHistoryState.MergeOlderPage(Plan(olderT, olderM), t, m);
        Assert.Equal(2, timeline.Entries.Count);
        Assert.All(timeline.Entries, e => Assert.Equal(ChatTimelineItemKind.Assistant, e.Kind));
        Assert.All(timeline.Entries, e => Assert.Equal("same", e.Text));
        var values = merged.Values.ToArray();
        Assert.Equal(2, values.Length);
        Assert.Contains(values, v => v.GatewayMessageId == "raw1" && v.GatewayDisplayItemId == "itemA");
        Assert.Contains(values, v => v.GatewayMessageId == "raw1" && v.GatewayDisplayItemId == "itemB");
    }

    [Fact]
    public void MergeOlderPage_IdenticalPair_ReplayDedups()
    {
        var t = Timeline(Item("e1", ChatTimelineItemKind.User, "alpha"));
        var m = new Dictionary<string, ChatEntryMetadata> { ["e1"] = Meta("raw1", "itemA") };
        var olderT = Timeline(Item("e2", ChatTimelineItemKind.User, "alpha"));
        var olderM = new Dictionary<string, ChatEntryMetadata> { ["e2"] = Meta("raw1", "itemA") };
        var (timeline, _) = ChatHistoryState.MergeOlderPage(Plan(olderT, olderM), t, m);
        Assert.Single(timeline.Entries);
    }

    [Fact]
    public void MergeOlderPage_OrdinaryRawOnly_ReplayDedups()
    {
        var t = Timeline(Item("e1", ChatTimelineItemKind.User, "alpha"));
        var m = new Dictionary<string, ChatEntryMetadata> { ["e1"] = Meta("raw1", null) };
        var olderT = Timeline(Item("e2", ChatTimelineItemKind.User, "alpha"));
        var olderM = new Dictionary<string, ChatEntryMetadata> { ["e2"] = Meta("raw1", null) };
        var (timeline, _) = ChatHistoryState.MergeOlderPage(Plan(olderT, olderM), t, m);
        Assert.Single(timeline.Entries);
    }

    [Fact]
    public void MergeOlderPage_MetadataPreservesRawAndDisplayIds()
    {
        var t = Timeline(Item("e1", ChatTimelineItemKind.User, "alpha"));
        var m = new Dictionary<string, ChatEntryMetadata> { ["e1"] = Meta("raw1", "itemA") };
        var olderT = Timeline(Item("e2", ChatTimelineItemKind.Assistant, "beta"));
        var olderM = new Dictionary<string, ChatEntryMetadata> { ["e2"] = Meta("raw1", "itemB") };
        var (_, merged) = ChatHistoryState.MergeOlderPage(Plan(olderT, olderM), t, m);
        var values = merged.Values.ToArray();
        Assert.Contains(values, v => v.GatewayMessageId == "raw1" && v.GatewayDisplayItemId == "itemA");
        Assert.Contains(values, v => v.GatewayMessageId == "raw1" && v.GatewayDisplayItemId == "itemB");
    }

    // ---- MergeWithLiveEntries (non-authoritative isolates identity) ----

    [Fact]
    public void MergeWithLiveEntries_DistinctDisplayIds_SharingRawIdSurvive()
    {
        // IDENTICAL text/role/seq/time: only the display-item id differs (raw1 preserved).
        var priorT = Timeline(Item("e1", ChatTimelineItemKind.Assistant, "same"));
        var priorM = new Dictionary<string, ChatEntryMetadata> { ["e1"] = Meta("raw1", "itemA") };
        var planT = Timeline(Item("e2", ChatTimelineItemKind.Assistant, "same"));
        var planM = new Dictionary<string, ChatEntryMetadata> { ["e2"] = Meta("raw1", "itemB") };
        var (timeline, merged) = ChatHistoryState.MergeWithLiveEntries(Plan(planT, planM), priorT, priorM, DateTimeOffset.UnixEpoch, authoritative: false);
        Assert.Equal(2, timeline.Entries.Count);
        Assert.All(timeline.Entries, e => Assert.Equal(ChatTimelineItemKind.Assistant, e.Kind));
        Assert.All(timeline.Entries, e => Assert.Equal("same", e.Text));
        var values = merged.Values.ToArray();
        Assert.Equal(2, values.Length);
        Assert.Contains(values, v => v.GatewayMessageId == "raw1" && v.GatewayDisplayItemId == "itemA");
        Assert.Contains(values, v => v.GatewayMessageId == "raw1" && v.GatewayDisplayItemId == "itemB");
    }

    [Fact]
    public void MergeWithLiveEntries_IdenticalPair_ReplayDedups()
    {
        var priorT = Timeline(Item("e1", ChatTimelineItemKind.User, "alpha"));
        var priorM = new Dictionary<string, ChatEntryMetadata> { ["e1"] = Meta("raw1", "itemA") };
        var planT = Timeline(Item("e2", ChatTimelineItemKind.User, "alpha"));
        var planM = new Dictionary<string, ChatEntryMetadata> { ["e2"] = Meta("raw1", "itemA") };
        var (timeline, _) = ChatHistoryState.MergeWithLiveEntries(Plan(planT, planM), priorT, priorM, DateTimeOffset.UnixEpoch, authoritative: false);
        Assert.Single(timeline.Entries);
    }

    [Fact]
    public void MergeWithLiveEntries_OrdinaryRawOnly_ReplayDedups()
    {
        var priorT = Timeline(Item("e1", ChatTimelineItemKind.User, "alpha"));
        var priorM = new Dictionary<string, ChatEntryMetadata> { ["e1"] = Meta("raw1", null) };
        var planT = Timeline(Item("e2", ChatTimelineItemKind.User, "alpha"));
        var planM = new Dictionary<string, ChatEntryMetadata> { ["e2"] = Meta("raw1", null) };
        var (timeline, _) = ChatHistoryState.MergeWithLiveEntries(Plan(planT, planM), priorT, priorM, DateTimeOffset.UnixEpoch, authoritative: false);
        Assert.Single(timeline.Entries);
    }

    [Fact]
    public void MergeWithLiveEntries_MetadataPreservesRawAndDisplayIds()
    {
        var priorT = Timeline(Item("e1", ChatTimelineItemKind.User, "alpha"));
        var priorM = new Dictionary<string, ChatEntryMetadata> { ["e1"] = Meta("raw1", "itemA") };
        var planT = Timeline(Item("e2", ChatTimelineItemKind.Assistant, "beta"));
        var planM = new Dictionary<string, ChatEntryMetadata> { ["e2"] = Meta("raw1", "itemB") };
        var (_, merged) = ChatHistoryState.MergeWithLiveEntries(Plan(planT, planM), priorT, priorM, DateTimeOffset.UnixEpoch, authoritative: false);
        var values = merged.Values.ToArray();
        Assert.Contains(values, v => v.GatewayMessageId == "raw1" && v.GatewayDisplayItemId == "itemA");
        Assert.Contains(values, v => v.GatewayMessageId == "raw1" && v.GatewayDisplayItemId == "itemB");
    }
}
