using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenClaw.Chat;
using OpenClawTray.Chat;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Behavioral proof for the bounded MOVING display window and the reference-identity projection cache:
/// per-render work is bounded by the window (measured), older/newer navigation moves a fixed-size
/// window without ever projecting the full retained history, the retained history is never truncated,
/// and immutable same-count/same-NextId content edits invalidate the cached window.
/// </summary>
public class ChatTimelineDisplayWindowTests
{
    private static ChatTimelineState Timeline(int count)
    {
        var entries = ImmutableList.CreateRange(Enumerable.Range(1, count)
            .Select(n => new ChatTimelineItem($"e{n}", ChatTimelineItemKind.User, $"msg {n}")));
        return ChatTimelineState.Initial() with { Entries = entries, NextId = count + 1 };
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static System.WeakReference CacheAbandonedHistory(ChatTimelineProjectionCache cache)
    {
        var timeline = Timeline(20_000);
        cache.Get("old", timeline, 1, 400);
        return new System.WeakReference(timeline.Entries);
    }

    private static void ForceFullBlockingCollection()
    {
        System.GC.Collect(System.GC.MaxGeneration, System.GCCollectionMode.Forced, blocking: true, compacting: true);
        System.GC.WaitForPendingFinalizers();
        System.GC.Collect(System.GC.MaxGeneration, System.GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    // Adopted from Mini-Actual-Cache-Retention-Counterexample (1 executed / 1 FAILED at 7ae735d8): the bounded
    // visible ROW count did NOT bound the retained root - the cache key strongly held the full 20000-entry
    // ImmutableList across a forced blocking compacting full GC. The cache must key by WEAK root identity so an
    // abandoned root is collectible while the cache (and its bounded rows) stays alive.
    [Fact]
    public void Cache_RowBoundMustNotRetainAbandonedFullHistoryRoot()
    {
        var cache = new ChatTimelineProjectionCache(capacity: 8, maxRows: 4000);
        var old = CacheAbandonedHistory(cache);
        cache.Get("replacement", Timeline(400), 2, 400);
        Assert.Equal(800, cache.CachedRows);
        ForceFullBlockingCollection();
        var retained = old.IsAlive;
        System.GC.KeepAlive(cache);
        Assert.False(retained);
    }

    [Fact]
    public void Cache_LiveWindowStillServedWhileAbandonedRootsAreRepeatedlyCollected()
    {
        var cache = new ChatTimelineProjectionCache(capacity: 8, maxRows: 4000);
        var live = Timeline(400);
        var first = cache.Get("live", live, 1, 400);
        Assert.Equal(400, first.VisibleEntries.Count);

        // Several obsolete full-history versions churned through the same cache; each must be collectible.
        for (var i = 0; i < 3; i++)
        {
            var abandoned = CacheAbandonedHistory(cache);
            ForceFullBlockingCollection();
            Assert.False(abandoned.IsAlive);
        }

        // The LIVE window is still a valid cache HIT (no rescan) and still correct, and rows stay bounded.
        var again = cache.Get("live", live, 1, 400);
        Assert.Equal(0, cache.LastMissVisitedEntries);
        Assert.Equal("e1", again.VisibleEntries[0].Id);
        Assert.Equal("e400", again.VisibleEntries[^1].Id);
        Assert.True(cache.CachedRows <= 4000);
        System.GC.KeepAlive(cache);
    }

    [Fact]
    public void Compute_BoundsPerRenderWorkOnLargeRetainedHistory()
    {
        var timeline = Timeline(20_000);
        var window = ChatTimelineDisplayWindowPolicy.Compute(
            timeline, ChatTimelineDisplayWindowPolicy.DefaultWindowSize);

        Assert.Equal(400, window.VisibleEntries.Count);
        Assert.Equal(19_600, window.HiddenEarlierCount);
        Assert.Equal(0, window.HiddenLaterCount);
        Assert.Equal("e19601", window.VisibleEntries[0].Id);
        Assert.Equal("e20000", window.VisibleEntries[^1].Id);
        Assert.Equal(20_000, timeline.Entries.Count);
    }

    [Fact]
    public void MoveOlder_StaysBoundedAndReachesOldestPageWithoutProjectingFullHistory()
    {
        var timeline = Timeline(20_000);
        var window = ChatTimelineDisplayWindowPolicy.Compute(timeline, 400);

        var maxVisible = window.VisibleEntries.Count;
        for (var i = 0; i < 200; i++)   // way more clicks than pages
        {
            window = ChatTimelineDisplayWindowPolicy.MoveOlder(window, timeline);
            maxVisible = System.Math.Max(maxVisible, window.VisibleEntries.Count);
        }

        Assert.True(maxVisible <= 400);                       // never grows to the full history
        Assert.Equal("e1", window.VisibleEntries[0].Id);      // oldest entries reachable
        Assert.Equal(20_000, timeline.Entries.Count);         // retained history untouched
    }

    [Fact]
    public void MoveNewer_ReturnsToLiveTailKeepingWindowBounded()
    {
        var timeline = Timeline(20_000);
        var window = ChatTimelineDisplayWindowPolicy.Compute(timeline, 400);
        for (var i = 0; i < 200; i++)
            window = ChatTimelineDisplayWindowPolicy.MoveOlder(window, timeline);
        for (var i = 0; i < 200; i++)
            window = ChatTimelineDisplayWindowPolicy.MoveNewer(window, timeline);

        Assert.Equal(400, window.VisibleEntries.Count);
        Assert.Equal(0, window.HiddenLaterCount);
        Assert.Equal("e20000", window.VisibleEntries[^1].Id);
    }

    [Fact]
    public void ProjectionCache_MissWorkIsBoundedByWindowAndHitAvoidsRescan()
    {
        var cache = new ChatTimelineProjectionCache();
        var timeline = Timeline(20_000);

        var first = cache.Get("main", timeline, generation: 1, windowSize: 400);
        Assert.Equal(400, cache.LastMissVisitedEntries);   // bounded by the window, not 20000
        Assert.Equal(1, cache.MissCount);

        var second = cache.Get("main", timeline, generation: 1, windowSize: 400);
        Assert.Equal(0, cache.LastMissVisitedEntries);     // hit: no rescan
        Assert.Equal(1, cache.MissCount);
        Assert.Same(first, second);
    }

    // Adopted from Mini-Render-Cache-Counterexample (was FAILING on the count/NextId key).
    [Fact]
    public void SameCountAndNextId_LiveTextEditMustInvalidateVisibleWindow()
    {
        var cache = new ChatTimelineProjectionCache();
        var timeline = Timeline(1_000);
        cache.Get("main", timeline, generation: 1, windowSize: 400);

        var edited = timeline with
        {
            Entries = timeline.Entries.SetItem(999,
                new ChatTimelineItem("e1000", ChatTimelineItemKind.User, "updated live text")),
        };
        var next = cache.Get("main", edited, generation: 1, windowSize: 400);

        Assert.Equal("updated live text", next.VisibleEntries[^1].Text);
        Assert.Equal(1_000, edited.Entries.Count);
        Assert.Equal(timeline.NextId, edited.NextId);
    }

    [Fact]
    public void ProjectionCache_BoundsCachedRows()
    {
        var cache = new ChatTimelineProjectionCache(capacity: 8, maxRows: 1_200);
        for (var i = 0; i < 40; i++)
        {
            var timeline = Timeline(10_000 + i);   // distinct immutable Entries references
            cache.Get("main", timeline, generation: i, windowSize: 400);
        }

        Assert.True(cache.CachedRows <= 1_200);    // bounded by rows as well as slots
    }

    [Fact]
    public void GenerationChange_RecomputesInsteadOfServingStaleWindow()
    {
        var cache = new ChatTimelineProjectionCache();
        var timeline = Timeline(1_000);
        cache.Get("main", timeline, generation: 1, windowSize: 400);
        cache.Get("main", timeline, generation: 2, windowSize: 400);

        Assert.Equal(2, cache.MissCount);
    }

    [Fact]
    public void DifferentSession_ProducesItsOwnWindowNotTheCachedOne()
    {
        var cache = new ChatTimelineProjectionCache();
        var a = Timeline(500);
        var b = Timeline(300);
        cache.Get("a", a, generation: 1, windowSize: 400);

        var windowB = cache.Get("b", b, generation: 1, windowSize: 400);
        Assert.Equal(300, windowB.VisibleEntries.Count);
        Assert.Equal(0, windowB.HiddenEarlierCount);
    }

    [Fact]
    public void NavigationState_MoveOlderNewerAndReturnToLiveStayBounded()
    {
        var timeline = Timeline(20_000);
        var navigation = new ChatTimelineNavigationState();
        const int windowSize = 400;

        var maxVisible = 0;
        ChatTimelineDisplayWindow window = default!;
        for (var i = 0; i < 200; i++)
        {
            window = navigation.MoveOlder("main", timeline, windowSize);
            maxVisible = System.Math.Max(maxVisible, window.VisibleEntries.Count);
        }
        Assert.True(maxVisible <= windowSize);           // never grows to the full history
        Assert.Equal("e1", window.VisibleEntries[0].Id); // oldest reachable
        Assert.True(window.HiddenLaterCount > 0);

        for (var i = 0; i < 200; i++)
            window = navigation.MoveNewer("main", timeline, windowSize);
        Assert.Equal(0, window.HiddenLaterCount);        // back at the live tail
        Assert.Equal("e20000", window.VisibleEntries[^1].Id);

        navigation.MoveOlder("main", timeline, windowSize);
        var live = navigation.ReturnToLive("main", timeline, windowSize);
        Assert.Equal(0, live.HiddenLaterCount);
        Assert.Equal("e20000", live.VisibleEntries[^1].Id);
    }

    [Fact]
    public void NavigationState_AnchorReconcilesAcrossOlderServerPrepend()
    {
        var timeline = Timeline(1_000);
        var navigation = new ChatTimelineNavigationState();
        const int windowSize = 400;

        var anchored = navigation.MoveOlder("main", timeline, windowSize);
        var anchoredFirstId = anchored.VisibleEntries[0].Id;
        Assert.Equal("e201", anchoredFirstId);

        // Simulate a server older-page prepend: 100 older entries inserted before the retained ones.
        var prepended = ImmutableList.CreateRange(Enumerable.Range(1, 100)
            .Select(n => new ChatTimelineItem($"prep{n}", ChatTimelineItemKind.User, $"old {n}")));
        var grown = timeline with
        {
            Entries = prepended.AddRange(timeline.Entries),
            NextId = timeline.NextId + 100,
        };

        var afterPrepend = navigation.Resolve("main", grown, windowSize);
        // The view does not jump: the same entry stays at the window start; size stays bounded.
        Assert.Equal(anchoredFirstId, afterPrepend.VisibleEntries[0].Id);
        Assert.True(afterPrepend.VisibleEntries.Count <= windowSize);
        Assert.True(grown.Entries.Count == 1_100);       // retained history preserved
    }

    [Fact]
    public void ProjectionCache_OversizedWindowIsNeverCached()
    {
        var cache = new ChatTimelineProjectionCache(capacity: 8, maxRows: 100);
        var timeline = Timeline(1_000);

        var window = cache.Get("main", timeline, generation: 1, windowSize: 400);
        Assert.Equal(400, window.VisibleEntries.Count);   // returned, bounded by window
        Assert.Equal(0, cache.CachedRows);                // but never cached beyond maxRows
        Assert.True(cache.CachedRows <= 100);
    }

    [Fact]
    public void NavigationState_EmptyResetTimelineDropsAnchorAndDoesNotThrow()
    {
        var timeline = Timeline(5_000);
        var navigation = new ChatTimelineNavigationState();
        const int windowSize = 400;
        navigation.MoveOlder("main", timeline, windowSize);

        // Reset/empty timeline must not index entries[0].
        var empty = navigation.Resolve("main", ChatTimelineState.Initial(), windowSize);
        Assert.Empty(empty.VisibleEntries);
        Assert.Equal(0, empty.HiddenEarlierCount);

        // The stale anchor was dropped: the same thread resolves back at the live tail.
        var tail = navigation.Resolve("main", timeline, windowSize);
        Assert.Equal("e5000", tail.VisibleEntries[^1].Id);
        Assert.Equal(0, tail.HiddenLaterCount);
    }

    [Fact]
    public void NavigationState_NewIdentityFrontChangeFallsBackToTailWhenAnchorEntryGone()
    {
        var timeline = Timeline(5_000);
        var navigation = new ChatTimelineNavigationState();
        const int windowSize = 400;
        var anchored = navigation.MoveOlder("main", timeline, windowSize);
        Assert.Equal("e4201", anchored.VisibleEntries[0].Id);

        // Same thread id, new transcript identity: the anchored entry no longer exists.
        var newIdentity = ImmutableList.CreateRange(Enumerable.Range(1, 5_000)
            .Select(n => new ChatTimelineItem($"n{n}", ChatTimelineItemKind.User, $"new {n}")));
        var replaced = ChatTimelineState.Initial() with { Entries = newIdentity, NextId = 5_001 };

        var after = navigation.Resolve("main", replaced, windowSize);
        Assert.Equal("n5000", after.VisibleEntries[^1].Id);   // fell back to the live tail
        Assert.Equal(0, after.HiddenLaterCount);
    }

    [Fact]
    public void NavigationState_ReturnToLiveClearsAnchorPermanently()
    {
        var timeline = Timeline(5_000);
        var navigation = new ChatTimelineNavigationState();
        const int windowSize = 400;
        navigation.MoveOlder("main", timeline, windowSize);
        navigation.MoveOlder("main", timeline, windowSize);

        var live = navigation.ReturnToLive("main", timeline, windowSize);
        Assert.Equal("e5000", live.VisibleEntries[^1].Id);
        Assert.Equal(0, live.HiddenLaterCount);

        // Anchor stays cleared on subsequent resolves.
        var again = navigation.Resolve("main", timeline, windowSize);
        Assert.Equal("e5000", again.VisibleEntries[^1].Id);
    }
}
