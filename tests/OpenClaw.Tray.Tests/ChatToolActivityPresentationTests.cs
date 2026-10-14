using System.Text.Json.Nodes;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatToolActivityPresentationTests
{
    private sealed class EqualOverridingProvider
    {
        public override bool Equals(object? obj) => obj is EqualOverridingProvider;   // value-equality lie
        public override int GetHashCode() => 0;
    }

    // A provider that overrides Equals to report equality must NOT be treated as the same scope object.
    [Fact]
    public void StableWindowScope_DistinctProviderObjectsWithEqualOverride_AreNotTheSameScope()
    {
        var scope = new ChatTimelineStableWindowScope();
        var window = new ChatTimelineDisplayWindow([Item("entry-9200", ChatTimelineItemKind.User)], 9200, 400);
        var providerA = new EqualOverridingProvider();
        var providerB = new EqualOverridingProvider();
        Assert.True(providerA.Equals(providerB));   // the lie
        scope.Set(new ChatTimelineWindowScopeKey(providerA, "t", "uuid"), window);
        Assert.Same(window, scope.Get(new ChatTimelineWindowScopeKey(providerA, "t", "uuid")));
        Assert.Null(scope.Get(new ChatTimelineWindowScopeKey(providerB, "t", "uuid")));   // REFERENCE identity
    }

    // UUID identity transition invalidates the scoped anchor state and any pre-reset lease.
    [Fact]
    public void AnchorLease_UuidTransitionInvalidatesOldLease()
    {
        var retained = Retained(Enumerable.Range(0, 10000)
            .Select(i => Item("entry-" + i, ChatTimelineItemKind.User)).ToArray());
        var navigation = new ChatTimelineNavigationState();
        navigation.MoveOlder("t", retained, 400);
        var prepended = retained with
        {
            Entries = retained.Entries.InsertRange(0, Enumerable.Range(0, 100)
                .Select(i => Item("older-" + i, ChatTimelineItemKind.User))),
        };
        Assert.True(navigation.Resolve("t", prepended, 400).Pending);
        using var coordinator = new ChatTimelineAnchorResolutionCoordinator(navigation);
        var oldLease = coordinator.Begin("t", prepended, 400, "uuid-a");
        Assert.NotNull(oldLease);
        var oldIndex = ChatTimelineNavigationState.FindAnchorIndex(prepended.Entries, oldLease!);

        // Same thread + same root/front/count, but a NEW accepted UUID: the old lease must be rejected.
        var newLease = coordinator.Begin("t", prepended, 400, "uuid-b");
        Assert.NotSame(oldLease, newLease);
        Assert.False(navigation.TryCommitAnchorIndex(oldLease!, prepended, oldIndex));
    }
    // STATE projection: the accepted UUID value AND the immutable timeline come from the SAME projection, and a
    // previously captured snapshot map must NOT mutate when a new UUID is accepted (copy-on-write).
    [Fact]
    public void AcceptedSessionIds_AndTimeline_AreCoherentInOneProjection_AndPreviousMapIsImmutable()
    {
        var history = new ChatHistoryState();
        var token = new ChatHistoryCommitToken("t", 0, 0, 0);
        history.MarkCommitted(token, "uuid-a");
        var timeline = Retained(Item("entry-1", ChatTimelineItemKind.User));

        var mapA = history.SnapshotAcceptedTranscriptIds();
        var snapshot = ChatSnapshotProjector.Project(new ChatSnapshotProjectionInput(
            Sessions: Array.Empty<SessionInfo>(),
            Timelines: new Dictionary<string, ChatTimelineState> { ["t"] = timeline },
            TimelineGenerations: new Dictionary<string, long>(),
            HistoryRevisions: new Dictionary<string, long> { ["t"] = 1 },
            QueuedMessages: new Dictionary<string, IReadOnlyList<ChatQueuedMessage>>(),
            SessionsListReceived: false,
            AvailableModels: Array.Empty<string>(),
            ModelChoices: Array.Empty<ChatModelChoice>(),
            CommandCatalog: null,
            Status: ConnectionStatus.Connected,
            MainSessionKey: null,
            HasHandshakeSnapshot: false,
            RememberedDefaultThreadId: null,
            RememberedThreadTitle: null,
            RememberedModel: null,
            RememberedModelProvider: null,
            AcceptedSessionIds: mapA));

        Assert.Equal("uuid-a", snapshot.AcceptedSessionIds!["t"]);
        Assert.Same(timeline, snapshot.Timelines["t"]);   // SAME immutable timeline object in the SAME projection

        // A NEW accepted UUID must NOT mutate the previously captured map (or the already-projected snapshot).
        history.MarkCommitted(token, "uuid-b");
        Assert.Equal("uuid-a", mapA["t"]);
        Assert.Equal("uuid-a", snapshot.AcceptedSessionIds!["t"]);
        Assert.Equal("uuid-b", history.SnapshotAcceptedTranscriptIds()["t"]);
    }

    // A UUID/provider transition on the SAME thread drops a RESOLVED anchor (NO pending flight needed) BEFORE the
    // window is computed, so a pre-reset anchor can never leak into the new identity; the SAME identity preserves
    // the thread's navigation across ordinary older pages.
    [Fact]
    public void NavigationIdentity_UuidTransitionDropsResolvedAnchor_EvenWithoutPendingFlight()
    {
        var navigation = new ChatTimelineNavigationState();
        var provider = new object();
        var retained = Retained(Enumerable.Range(0, 10000)
            .Select(i => Item("entry-" + i, ChatTimelineItemKind.User)).ToArray());
        navigation.EnsureNavigationIdentity(provider, "t", "uuid-a");
        navigation.MoveOlder("t", retained, 400);
        var anchored = navigation.Resolve("t", retained, 400);
        Assert.True(anchored.HiddenLaterCount > 0);            // moved OLDER: a resolved (non-pending) anchored view

        Assert.False(navigation.EnsureNavigationIdentity(provider, "t", "uuid-a"));   // same identity: preserved
        Assert.Equal(anchored.HiddenEarlierCount, navigation.Resolve("t", retained, 400).HiddenEarlierCount);

        // SAME thread + SAME root/front/count, NEW accepted UUID: the old resolved anchor is dropped => live tail.
        Assert.True(navigation.EnsureNavigationIdentity(provider, "t", "uuid-b"));
        var afterTransition = navigation.Resolve("t", retained, 400);
        Assert.False(afterTransition.Pending);
        Assert.Equal(0, afterTransition.HiddenLaterCount);                          // back to the CURRENT live tail
        Assert.Equal(retained.Entries.Count - 400, afterTransition.HiddenEarlierCount);   // not the pre-reset anchor
    }

    // Held stable IDs survive an ordinary older page (historyRevision advances, SAME UUID) because the revision is
    // NOT part of the scope identity; a UUID change invalidates even with the same thread/provider.
    [Fact]
    public void ScopeKey_ExcludesRevision_HeldStableIdsSurviveOlderPageUnderSameUuid()
    {
        var scope = new ChatTimelineStableWindowScope();
        var provider = new object();
        var window = ChatTimelineDisplayWindowPolicy.ComputeAnchored(
            Retained(Enumerable.Range(0, 10000)
                .Select(i => Item("entry-" + i, ChatTimelineItemKind.User)).ToArray()), 5000, 400);
        var key = new ChatTimelineWindowScopeKey(provider, "t", "uuid-a");
        scope.Set(key, window);

        // Older page: historyRevision advanced (NOT part of the key) and the UUID is unchanged => held view stays.
        Assert.Same(window, scope.Get(new ChatTimelineWindowScopeKey(provider, "t", "uuid-a")));
        // A NEW UUID (replacement) invalidates the held view even with the same thread/provider.
        Assert.Null(scope.Get(new ChatTimelineWindowScopeKey(provider, "t", "uuid-b")));
    }
    // Two DEEP roots over the same thread: rootA prepends an older page (front change -> deep PENDING anchor),
    // rootB additionally inserts mid items (a DIFFERENT immutable root with the same front/count identity shape).
    private static (ChatTimelineNavigationState Navigation, ChatTimelineState RootA, ChatTimelineState RootB) DeepTwoRoots(string thread)
    {
        var retained = Retained(Enumerable.Range(0, 10000)
            .Select(i => Item("entry-" + i, ChatTimelineItemKind.User)).ToArray());
        var navigation = new ChatTimelineNavigationState();
        navigation.MoveOlder(thread, retained, 400);
        var rootA = retained with
        {
            Entries = retained.Entries.InsertRange(0, Enumerable.Range(0, 100)
                .Select(i => Item("older-" + i, ChatTimelineItemKind.User))),
        };
        Assert.True(navigation.Resolve(thread, rootA, 400).Pending);
        var rootB = rootA with
        {
            Entries = rootA.Entries.InsertRange(5000, Enumerable.Range(0, 100)
                .Select(i => Item("mid-" + i, ChatTimelineItemKind.User))),
        };
        return (navigation, rootA, rootB);
    }

    // Reset while the obsolete producer is HELD before its scan must keep the coordinator usable: the reset
    // producer never wakes, exactly one CURRENT callback fires, and at most ONE physical producer runs at once.
    [Fact]
    public async Task AnchorCoordinator_ResetThenCurrentCapture_OldCallbackZeroCurrentCallbackOne()
    {
        var (navigation, rootA, rootB) = DeepTwoRoots("t");
        using var coordinator = new ChatTimelineAnchorResolutionCoordinator(navigation);

        using var firstEntered = new ManualResetEventSlim(false);
        using var releaseFirst = new ManualResetEventSlim(false);
        var gate = 0;
        coordinator.BeforeScanForTests = () =>
        {
            if (Interlocked.Increment(ref gate) == 1)
            {
                firstEntered.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(10));
            }
        };

        var oldCallbacks = 0;
        var currentCallbacks = 0;
        var currentDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.NotNull(coordinator.Begin("t", rootA, 400));
        Assert.True(coordinator.TryResolvePending(rootA, () => Interlocked.Increment(ref oldCallbacks)));
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));   // obsolete producer held BEFORE its scan

        coordinator.Reset();   // cancel in-flight work but keep the coordinator USABLE

        Assert.True(navigation.Resolve("t", rootB, 400).Pending);
        Assert.NotNull(coordinator.Begin("t", rootB, 400));
        Assert.True(coordinator.TryResolvePending(rootB, () =>
        {
            Interlocked.Increment(ref currentCallbacks);
            currentDone.TrySetResult(true);
        }));

        releaseFirst.Set();
        await currentDone.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, Volatile.Read(ref oldCallbacks));        // the reset producer never wakes
        Assert.Equal(1, Volatile.Read(ref currentCallbacks));
        Assert.Equal(1, coordinator.MaxConcurrentProducers);     // ONE physical producer at a time
        Assert.False(navigation.Resolve("t", rootB, 400).Pending);
    }

    // Dispose must cancel promptly, never wake a callback, never resume a pending capture, and fully drain.
    [Fact]
    public async Task AnchorCoordinator_Dispose_NoWakeupNoResume()
    {
        var (navigation, rootA, _) = DeepTwoRoots("t");
        var coordinator = new ChatTimelineAnchorResolutionCoordinator(navigation);

        using var firstEntered = new ManualResetEventSlim(false);
        using var releaseFirst = new ManualResetEventSlim(false);
        var beforeScan = 0;
        coordinator.BeforeScanForTests = () =>
        {
            if (Interlocked.Increment(ref beforeScan) == 1)
            {
                firstEntered.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(10));
            }
        };
        var drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.FlightCompletedForTests += () => drained.TrySetResult(true);

        var callbacks = 0;
        Assert.NotNull(coordinator.Begin("t", rootA, 400));
        Assert.True(coordinator.TryResolvePending(rootA, () => Interlocked.Increment(ref callbacks)));
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));

        coordinator.Dispose();
        releaseFirst.Set();
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(10));   // the held producer physically drained

        Assert.Equal(0, Volatile.Read(ref callbacks));            // no wakeup
        Assert.Equal(1, Volatile.Read(ref beforeScan));           // NO resumed producer
        Assert.Equal(1, coordinator.PhysicalProducersStarted);    // nothing else started
        Assert.False(coordinator.TryResolvePending(rootA, () => { }));   // no pending resumption
        Assert.Null(coordinator.Begin("t", rootA, 400));                 // no new capture
    }

    // A CHANGED capture arriving via Begin ONLY must cancel the obsolete producer promptly and RESUME the latest
    // capture (the registered current callback fires exactly once), with at most one physical producer at once.
    [Fact]
    public async Task AnchorCoordinator_ChangedCaptureBeginOnly_CancelsObsoleteAndResumesLatest()
    {
        var (navigation, rootA, rootB) = DeepTwoRoots("t");
        using var coordinator = new ChatTimelineAnchorResolutionCoordinator(navigation);

        using var firstEntered = new ManualResetEventSlim(false);
        using var releaseFirst = new ManualResetEventSlim(false);
        var gate = 0;
        coordinator.BeforeScanForTests = () =>
        {
            if (Interlocked.Increment(ref gate) == 1)
            {
                firstEntered.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(10));
            }
        };

        var callbacks = 0;
        var currentDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.NotNull(coordinator.Begin("t", rootA, 400));
        Assert.True(coordinator.TryResolvePending(rootA, () =>
        {
            Interlocked.Increment(ref callbacks);
            currentDone.TrySetResult(true);
        }));
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));   // obsolete producer held BEFORE its scan

        // Changed capture arrives via Begin ONLY; a current callback IS registered => the drain must resume it.
        Assert.True(navigation.Resolve("t", rootB, 400).Pending);
        Assert.NotNull(coordinator.Begin("t", rootB, 400));

        releaseFirst.Set();
        await currentDone.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, Volatile.Read(ref callbacks));            // exactly ONE wakeup (the resumed current capture)
        Assert.Equal(1, coordinator.MaxConcurrentProducers);      // obsolete cancelled; one producer at a time
        Assert.Equal(2, coordinator.PhysicalProducersStarted);    // obsolete + resumed latest
        var after = navigation.Resolve("t", rootB, 400);
        Assert.False(after.Pending);
        Assert.Equal("entry-9200", after.VisibleEntries[0].Id);   // exact current deep IDs preserved
    }

    [Fact]
    public void StableWindowScope_DoesNotCrossLogicalIdentities()
    {
        var scope = new ChatTimelineStableWindowScope();
        var window = new ChatTimelineDisplayWindow([Item("entry-9200", ChatTimelineItemKind.User)], 9200, 400);
        var providerA = new object();
        var providerB = new object();
        var a = new ChatTimelineWindowScopeKey(providerA, "thread-1", "uuid-a");
        scope.Set(a, window);
        Assert.Same(window, scope.Get(a));
        Assert.Null(scope.Get(new ChatTimelineWindowScopeKey(providerA, "thread-2", "uuid-a")));  // thread
        Assert.Null(scope.Get(new ChatTimelineWindowScopeKey(providerB, "thread-1", "uuid-a")));  // provider
        Assert.Null(scope.Get(new ChatTimelineWindowScopeKey(providerA, "thread-1", "uuid-b")));  // UUID
        Assert.Null(scope.Get(new ChatTimelineWindowScopeKey(providerA, "thread-1", null)));      // unknown
        scope.Clear();
        Assert.Null(scope.Get(a));
    }

    [Fact]
    public async Task AnchorCoordinator_DrainResumesLatestCaptureWithoutAnotherUiQuery()
    {
        var retained = Retained(Enumerable.Range(0, 10000)
            .Select(i => Item("entry-" + i, ChatTimelineItemKind.User)).ToArray());
        var navigation = new ChatTimelineNavigationState();
        navigation.MoveOlder("t", retained, 400);
        var rootA = retained with
        {
            Entries = retained.Entries.InsertRange(0, Enumerable.Range(0, 100)
                .Select(i => Item("older-" + i, ChatTimelineItemKind.User))),
        };
        Assert.True(navigation.Resolve("t", rootA, 400).Pending);
        using var coordinator = new ChatTimelineAnchorResolutionCoordinator(navigation);

        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.NotNull(coordinator.Begin("t", rootA, 400));
        Assert.True(coordinator.TryResolvePending(rootA, () => completed.TrySetResult(true)));

        // A NEW capture arrives via Begin only; the drain must resume it WITHOUT another TryResolvePending.
        var rootB = rootA with
        {
            Entries = rootA.Entries.InsertRange(5000, Enumerable.Range(0, 100)
                .Select(i => Item("mid-" + i, ChatTimelineItemKind.User))),
        };
        _ = navigation.Resolve("t", rootB, 400);
        Assert.NotNull(coordinator.Begin("t", rootB, 400));

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));   // fired by the drained rootB flight
        Assert.False(navigation.Resolve("t", rootB, 400).Pending);
    }
    // Deterministic gated producer evidence: the ORIGINAL producer is held BEFORE its scan, the scope is
    // replaced, then released; the old callback must NEVER fire and exactly one current callback fires with the
    // exact current deep IDs after the drain - with NO extra UI query and at most one physical producer at once.
    [Fact]
    public async Task AnchorProducer_GatedScopeChange_OldCallbackZeroCurrentCallbackOneExactIds()
    {
        var retained = Retained(Enumerable.Range(0, 10000)
            .Select(i => Item("entry-" + i, ChatTimelineItemKind.User)).ToArray());
        var navigation = new ChatTimelineNavigationState();
        navigation.MoveOlder("t", retained, 400);
        var rootA = retained with
        {
            Entries = retained.Entries.InsertRange(0, Enumerable.Range(0, 100)
                .Select(i => Item("older-" + i, ChatTimelineItemKind.User))),
        };
        Assert.True(navigation.Resolve("t", rootA, 400).Pending);
        using var coordinator = new ChatTimelineAnchorResolutionCoordinator(navigation);

        using var firstEntered = new ManualResetEventSlim(false);
        using var releaseFirst = new ManualResetEventSlim(false);
        var gate = 0;
        coordinator.BeforeScanForTests = () =>
        {
            if (Interlocked.Increment(ref gate) == 1)
            {
                firstEntered.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(10));
            }
        };

        var oldCallbacks = 0;
        var currentCallbacks = 0;
        var currentDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.NotNull(coordinator.Begin("t", rootA, 400));
        Assert.True(coordinator.TryResolvePending(rootA, () => Interlocked.Increment(ref oldCallbacks)));
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));   // old producer held BEFORE its scan

        var rootB = rootA with
        {
            Entries = rootA.Entries.InsertRange(5000, Enumerable.Range(0, 100)
                .Select(i => Item("mid-" + i, ChatTimelineItemKind.User))),
        };
        _ = navigation.Resolve("t", rootB, 400);
        Assert.NotNull(coordinator.Begin("t", rootB, 400));
        _ = coordinator.TryResolvePending(rootB, () =>
        {
            Interlocked.Increment(ref currentCallbacks);
            currentDone.TrySetResult(true);
        });

        releaseFirst.Set();
        await currentDone.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, Volatile.Read(ref oldCallbacks));       // superseded capture never wakes
        Assert.Equal(1, Volatile.Read(ref currentCallbacks));
        Assert.Equal(1, coordinator.MaxConcurrentProducers);    // at most ONE physical producer at a time
        var after = navigation.Resolve("t", rootB, 400);
        Assert.False(after.Pending);
        Assert.Equal("entry-9200", after.VisibleEntries[0].Id); // exact current deep IDs preserved
    }
    // Adopted from Mini-Actual-Anchor-ABA-37e79-Counterexample: Forget + recreate the SAME anchor/root must
    // invalidate the old lease (the navigation revision moved), so a stale completion cannot commit.
    [Fact]
    public void MiniReview_ForgetAndRecreateSameAnchorMustInvalidateOldLease()
    {
        var retained = Retained(Enumerable.Range(0, 10000)
            .Select(i => Item("entry-" + i, ChatTimelineItemKind.User)).ToArray());
        var navigation = new ChatTimelineNavigationState();
        navigation.MoveOlder("session", retained, 400);
        var prepended = retained with
        {
            Entries = retained.Entries.InsertRange(0, Enumerable.Range(0, 100)
                .Select(i => Item("older-" + i, ChatTimelineItemKind.User))),
        };
        Assert.True(navigation.Resolve("session", prepended, 400).Pending);
        using var coordinator = new ChatTimelineAnchorResolutionCoordinator(navigation);
        var stale = coordinator.Begin("session", prepended, 400);
        Assert.NotNull(stale);
        var oldIndex = ChatTimelineNavigationState.FindAnchorIndex(prepended.Entries, stale!);

        navigation.Forget("session");
        navigation.MoveOlder("session", retained, 400);
        Assert.True(navigation.Resolve("session", prepended, 400).Pending);
        var current = coordinator.Begin("session", prepended, 400);
        Assert.NotNull(current);
        Assert.NotSame(stale, current);
        Assert.False(navigation.TryCommitAnchorIndex(stale!, prepended, oldIndex));
    }
    // Adopted from Mini-Actual-Anchor-Root-Lease-b7a3-Counterexample (1 executed / 1 FAILED): a same-count /
    // same-front DIFFERENT immutable root shifts source positions, so the coordinator must create a NEW lease
    // and the stale lease must be rejected even when count/front/anchor id all match.
    [Fact]
    public void MiniReview_AnchorLeaseMustBindCurrentImmutableRootEvenWhenCountAndFrontMatch()
    {
        var retained = Retained(Enumerable.Range(0, 10000)
            .Select(i => Item("entry-" + i, ChatTimelineItemKind.User)).ToArray());
        var navigation = new ChatTimelineNavigationState();
        navigation.MoveOlder("session", retained, 400);
        var firstRoot = retained with
        {
            Entries = retained.Entries.InsertRange(0, Enumerable.Range(0, 100)
                .Select(i => Item("older-" + i, ChatTimelineItemKind.User))),
        };
        Assert.True(navigation.Resolve("session", firstRoot, 400).Pending);
        using var coordinator = new ChatTimelineAnchorResolutionCoordinator(navigation);
        var stale = coordinator.Begin("session", firstRoot, 400);
        Assert.NotNull(stale);
        var staleIndex = ChatTimelineNavigationState.FindAnchorIndex(firstRoot.Entries, stale!);

        var currentRoot = firstRoot with
        {
            Entries = firstRoot.Entries.InsertRange(5000, Enumerable.Range(0, 100)
                .Select(i => Item("middle-" + i, ChatTimelineItemKind.User))).RemoveRange(10100, 100),
        };
        Assert.Equal(firstRoot.Entries.Count, currentRoot.Entries.Count);
        Assert.Equal(firstRoot.Entries[0].Id, currentRoot.Entries[0].Id);
        Assert.NotSame(firstRoot.Entries, currentRoot.Entries);
        Assert.True(navigation.Resolve("session", currentRoot, 400).Pending);

        var current = coordinator.Begin("session", currentRoot, 400);
        Assert.NotNull(current);
        Assert.NotSame(stale, current);
        Assert.False(navigation.TryCommitAnchorIndex(stale!, firstRoot, staleIndex));
    }

    // Pending presentation must NEVER render the new tail as resolved: it holds the last stable bounded IDs,
    // and falls back to an explicit loading window when there is no stable window yet.
    [Fact]
    public void PendingPresentation_HoldsLastStableIdsNeverTail()
    {
        var stable = new ChatTimelineDisplayWindow(
            [Item("entry-9200", ChatTimelineItemKind.User), Item("entry-9201", ChatTimelineItemKind.User)],
            9200,
            400);
        var tailPending = new ChatTimelineDisplayWindow(
            [Item("entry-9600", ChatTimelineItemKind.User)],
            9600,
            400,
            Pending: true);

        var held = ChatTimelinePendingPresentationPolicy.Resolve(stable, tailPending);
        Assert.True(held.Pending);
        Assert.Equal("entry-9200", held.VisibleEntries[0].Id);   // held last stable IDs, NOT the tail

        var loading = ChatTimelinePendingPresentationPolicy.Resolve(null, tailPending);
        Assert.True(loading.Pending);
        Assert.Empty(loading.VisibleEntries);                   // explicit loading, never the tail

        var resolved = ChatTimelinePendingPresentationPolicy.Resolve(
            stable,
            new ChatTimelineDisplayWindow([Item("entry-9300", ChatTimelineItemKind.User)], 9300, 400));
        Assert.False(resolved.Pending);
        Assert.Equal("entry-9300", resolved.VisibleEntries[0].Id);
    }
    [Fact]
    public async Task DeepAnchorResolver_CompletesAndPreservesDeepIdsWithoutAnotherQuery()
    {
        var retained = Retained(Enumerable.Range(0, 10000)
            .Select(i => Item("entry-" + i, ChatTimelineItemKind.User)).ToArray());
        var nav = new ChatTimelineNavigationState();
        var before = nav.MoveOlder("session", retained, 400);
        Assert.Equal("entry-9200", before.VisibleEntries[0].Id);

        var withOlder = retained with
        {
            Entries = retained.Entries.InsertRange(0, Enumerable.Range(0, 100)
                .Select(i => Item("older-" + i, ChatTimelineItemKind.User))),
        };
        Assert.True(nav.Resolve("session", withOlder, 400).Pending);

        using var coordinator = new ChatTimelineAnchorResolutionCoordinator(nav);
        Assert.NotNull(coordinator.Begin("session", withOlder, 400));
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(coordinator.TryResolvePending(withOlder, () => done.TrySetResult(true)));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var after = nav.Resolve("session", withOlder, 400);
        Assert.False(after.Pending);
        Assert.Equal(before.VisibleEntries.Select(e => e.Id), after.VisibleEntries.Select(e => e.Id));
    }

    [Fact]
    public void DeepAnchorResolver_SupersededByReturnToLiveDoesNotRevert()
    {
        var retained = Retained(Enumerable.Range(0, 10000)
            .Select(i => Item("entry-" + i, ChatTimelineItemKind.User)).ToArray());
        var nav = new ChatTimelineNavigationState();
        nav.MoveOlder("t", retained, 400);
        var withOlder = retained with
        {
            Entries = retained.Entries.InsertRange(0, Enumerable.Range(0, 100)
                .Select(i => Item("older-" + i, ChatTimelineItemKind.User))),
        };
        Assert.True(nav.Resolve("t", withOlder, 400).Pending);
        var lease = nav.CapturePendingAnchor("t", withOlder, 400);
        Assert.NotNull(lease);

        nav.ReturnToLive("t", withOlder, 400);   // supersedes the pending anchor
        Assert.False(nav.TryCommitAnchorIndex(lease!, withOlder, 9300));   // stale completion dropped
        Assert.False(nav.HasPendingAnchor("t"));
    }

    [Fact]
    public async Task DeepAnchorResolver_TrulyAbsentAfterCompleteIndexForgetsAnchor()
    {
        var retained = Retained(Enumerable.Range(0, 10000)
            .Select(i => Item("entry-" + i, ChatTimelineItemKind.User)).ToArray());
        var nav = new ChatTimelineNavigationState();
        nav.MoveOlder("t", retained, 400);
        var renamed = retained with
        {
            Entries = System.Collections.Immutable.ImmutableList.CreateRange(
                retained.Entries.Select(e => e with { Id = "x" + e.Id })),
        };
        Assert.True(nav.Resolve("t", renamed, 400).Pending);

        using var coordinator = new ChatTimelineAnchorResolutionCoordinator(nav);
        Assert.NotNull(coordinator.Begin("t", renamed, 400));
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(coordinator.TryResolvePending(renamed, () => done.TrySetResult(true)));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(nav.HasPendingAnchor("t"));
        Assert.False(nav.Resolve("t", renamed, 400).Pending);
    }
    // Adopted from Mini-Actual-Deep-Anchor-069cbe85-Counterexample (1 executed / 1 FAILED): an existing VALID
    // deep anchor (entry-9200 of 10000) must survive a 100-entry older prepend. The bounded sync lookup may
    // NOT declare it absent; it returns an explicit PENDING window and the off-UI index resolution rebases to
    // the SAME visible IDs.
    [Fact]
    public void MiniReview_ExistingDeepAnchorMustSurviveOlderPrepend()
    {
        var retained = Retained(Enumerable.Range(0, 10000)
            .Select(i => Item("entry-" + i, ChatTimelineItemKind.User)).ToArray());
        var navigation = new ChatTimelineNavigationState();
        var before = navigation.MoveOlder("session", retained, 400);
        Assert.Equal("entry-9200", before.VisibleEntries[0].Id);

        var withOlder = retained with
        {
            Entries = retained.Entries.InsertRange(0, Enumerable.Range(0, 100)
                .Select(i => Item("older-" + i, ChatTimelineItemKind.User))),
        };

        var pending = navigation.Resolve("session", withOlder, 400);
        Assert.True(pending.Pending);   // deep: explicit PENDING, never tail-as-resolved
        Assert.True(navigation.HasPendingAnchor("session"));

        Assert.True(navigation.CompleteAnchorResolution("session", withOlder, 400));   // off-UI index resolved
        var after = navigation.Resolve("session", withOlder, 400);
        Assert.False(after.Pending);
        Assert.Equal(before.VisibleEntries.Select(e => e.Id), after.VisibleEntries.Select(e => e.Id));
    }

    [Fact]
    public void GenuinelyAbsentAnchorIsForgottenAfterTheCompleteIndex()
    {
        var retained = Retained(Enumerable.Range(0, 10000)
            .Select(i => Item("entry-" + i, ChatTimelineItemKind.User)).ToArray());
        var navigation = new ChatTimelineNavigationState();
        navigation.MoveOlder("s", retained, 400);

        var renamed = retained with
        {
            Entries = System.Collections.Immutable.ImmutableList.CreateRange(
                retained.Entries.Select(e => e with { Id = "x" + e.Id })),
        };
        Assert.True(navigation.Resolve("s", renamed, 400).Pending);
        Assert.False(navigation.CompleteAnchorResolution("s", renamed, 400));   // genuinely absent
        Assert.False(navigation.HasPendingAnchor("s"));
        Assert.False(navigation.Resolve("s", renamed, 400).Pending);            // tail, resolved
    }

    // The first-send completion must clear the state from the FAITHFUL facts alone (no further provider.Changed).
    [Fact]
    public async Task FirstSendFactsCompletion_ClearsWithoutAnotherChangedCallback()
    {
        var items = Enumerable.Range(0, 20_000).Select(i => Tool($"t{i}", "read_file")).ToList();
        items[19_000] = Item("u", ChatTimelineItemKind.User);   // deep user beyond the bounded pass
        var full = Retained(items.ToArray());
        var producer = new ChatTimelineRetainedFactsProducer();
        using var coordinator = new ChatTimelineRetainedFactsCoordinator(producer);
        var lease = coordinator.Begin("compose", full.Entries, 0);
        Assert.Null(coordinator.TryBounded(lease));   // deep => pending

        var cleared = false;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = coordinator.RequestCompletion(lease, () =>
        {
            cleared = coordinator.TryBounded(lease)?.HasAnyUser == true;
            done.TrySetResult(true);
        });
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(cleared, "the completion alone must clear first-send from the faithful facts");
    }

    [Fact]
    public async Task FirstSendFactsCompletion_NotInvokedAfterScopeChange()
    {
        var deep = Retained(Enumerable.Range(0, 20_000).Select(i => Tool($"t{i}", "read_file")).ToArray());
        var coordinator = new ChatTimelineRetainedFactsCoordinator(new ChatTimelineRetainedFactsProducer());
        var lease = coordinator.Begin("compose-a", deep.Entries, 0);
        var fired = false;
        _ = coordinator.RequestCompletion(lease, () => fired = true);

        _ = coordinator.Begin("compose-b", deep.Entries, 0);   // compose changed before completion
        await Task.Delay(250);
        Assert.False(fired, "a superseded compose scope must not clear first-send");
        coordinator.Dispose();
    }
    // Adopted (corrected) from Mini-Actual-Activity-Rerender-9186-Counterexample: an identical-scope rerender
    // while the first flight is still draining must NOT strand loading.
    private sealed class MiniBlockedHistory : IReadOnlyList<ChatTimelineItem>
    {
        private readonly ChatTimelineItem[] _items;
        public readonly ManualResetEventSlim Entered = new(false);
        public readonly ManualResetEventSlim Release = new(false);
        public bool Armed;
        public MiniBlockedHistory(ChatTimelineItem[] items) => _items = items;
        public int Count => _items.Length;
        public ChatTimelineItem this[int index]
        {
            get
            {
                if (Armed && index == 19997)
                {
                    Entered.Set();
                    if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test gate unreleased");
                }
                return _items[index];
            }
        }
        public IEnumerator<ChatTimelineItem> GetEnumerator() => ((IEnumerable<ChatTimelineItem>)_items).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task MiniReview_SameWindowRerenderMustResumeAfterCanceledFlightDrains()
    {
        var history = new MiniBlockedHistory(Enumerable.Range(0, 20000).Select(i => Tool("tool" + i, "read_file")).ToArray());
        var facts = new ChatToolActivityPresentation.ChatToolActivitySourceFacts();
        using var coordinator = new ChatToolActivityPresentation.ChatToolActivityRenderCoordinator(facts);
        var first = coordinator.Begin("s", history, 19998, 20000, 7);
        Assert.Null(coordinator.TryBounded(first));
        history.Armed = true;
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.RequestCompletion(first, () => completed.TrySetResult(true));
        Assert.True(history.Entered.Wait(TimeSpan.FromSeconds(5)));
        history.Armed = false;
        var second = coordinator.Begin("s", history, 19998, 20000, 7);
        Assert.Null(coordinator.TryBounded(second));
        coordinator.RequestCompletion(second, () => completed.TrySetResult(true));
        history.Release.Set();
        var finished = await Task.WhenAny(completed.Task, Task.Delay(2500)) == completed.Task;
        Assert.True(finished, "Same-window rerender canceled the first flight but could not acquire pending key; loading never completed without another UI query");
        Assert.NotNull(coordinator.TryBounded(second));
    }

    // Identical logical scope must REUSE the lease (no restart of in-flight work).
    [Fact]
    public void SameLogicalScope_ReusesLeaseAndDoesNotRestartWork()
    {
        var history = Retained(Enumerable.Range(0, 5_000).Select(i => Tool($"t{i}", "read_file")).ToArray());
        using var coordinator = new ChatToolActivityPresentation.ChatToolActivityRenderCoordinator(Facts());
        var first = coordinator.Begin("s", history.Entries, 4_000, 4_002, 7);
        var again = coordinator.Begin("s", history.Entries, 4_000, 4_002, 7);
        Assert.Same(first, again);
        Assert.False(first.Cancellation.IsCancellationRequested);   // identical scope does not cancel
    }

    // ABA: leaving and returning to a scope while a canceled flight drains must resume autonomously - the
    // newest active lease takes over and completes WITHOUT another UI query.
    [Fact]
    public async Task AbaScopeReturn_ResumesWithoutAdditionalUiQuery()
    {
        var a = Retained(Enumerable.Range(0, 6_000).Select(i => Tool($"a{i}", "read_file")).ToArray());
        var b = Retained(Enumerable.Range(0, 6_000).Select(i => Tool($"b{i}", "read_file")).ToArray());
        using var coordinator = new ChatToolActivityPresentation.ChatToolActivityRenderCoordinator(Facts());

        var leaseA = coordinator.Begin("s", a.Entries, 5_000, 5_002, 1);
        Assert.Null(coordinator.TryBounded(leaseA));
        coordinator.RequestCompletion(leaseA, () => { });

        var leaseB = coordinator.Begin("s", b.Entries, 5_000, 5_002, 1);   // different scope
        Assert.True(leaseA.Cancellation.IsCancellationRequested);
        Assert.Null(coordinator.TryBounded(leaseB));

        // Return to the A scope: a NEW lease takes over and must complete by itself.
        var leaseA2 = coordinator.Begin("s", a.Entries, 5_000, 5_002, 1);
        Assert.NotSame(leaseA, leaseA2);
        Assert.False(leaseA2.Cancellation.IsCancellationRequested);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.RequestCompletion(leaseA2, () => completed.TrySetResult(true));
        var finished = await Task.WhenAny(completed.Task, Task.Delay(5000)) == completed.Task;
        Assert.True(finished, "the newest active lease must resume and complete without another UI query");
        Assert.NotNull(coordinator.TryBounded(leaseA2));
    }
    private static ChatToolActivityPresentation.ChatToolActivitySourceFacts Facts() =>
        new();

    // REQUIRED: switching from a LONG provisional window to a SHORT faithful window on the next render must
    // cancel the previous lease so it can never publish or re-render.
    [Fact]
    public async Task RenderCoordinator_SwitchFromLongProvisionalToShortFaithful_CancelsPrevious()
    {
        var allTools = Retained(Enumerable.Range(0, 6_000).Select(i => Tool($"t{i}", "read_file")).ToArray());
        var shortFaithful = Retained(Tool("s0", "read_file"), Tool("s1", "read_file"));
        using var coordinator = new ChatToolActivityPresentation.ChatToolActivityRenderCoordinator(Facts());

        var longLease = coordinator.Begin("main", allTools.Entries, 5_000, 5_002, 1);
        Assert.Null(coordinator.TryBounded(longLease));   // deep all-tool window => provisional/pending
        var fired = 0;
        coordinator.RequestCompletion(longLease, () => Interlocked.Increment(ref fired));

        var shortLease = coordinator.Begin("main", shortFaithful.Entries, 0, 2, 1);
        Assert.True(longLease.Cancellation.IsCancellationRequested);   // superseded by the next render
        Assert.NotNull(coordinator.TryBounded(shortLease));            // faithful, no pending
        await Task.Delay(200);
        Assert.Equal(0, fired);   // the superseded long lease must never complete/re-render
    }

    [Fact]
    public void RenderCoordinator_DifferentRootOrSession_InvalidatesPreviousLease()
    {
        var a = Retained(Enumerable.Range(0, 5_000).Select(i => Tool($"t{i}", "read_file")).ToArray());
        var b = Retained(Enumerable.Range(0, 5_000).Select(i => Tool($"u{i}", "read_file")).ToArray());
        using var coordinator = new ChatToolActivityPresentation.ChatToolActivityRenderCoordinator(Facts());
        var leaseA = coordinator.Begin("s1", a.Entries, 4_000, 4_001, 1);
        var leaseB = coordinator.Begin("s2", b.Entries, 4_000, 4_001, 1);
        Assert.True(leaseA.Cancellation.IsCancellationRequested);   // different root/session supersedes
        Assert.False(leaseB.Cancellation.IsCancellationRequested);
    }

    [Fact]
    public void RenderCoordinator_Dispose_CancelsActiveLease()
    {
        var retention = Retained(Tool("one", "read_file"), Tool("two", "read_file"));
        var coordinator = new ChatToolActivityPresentation.ChatToolActivityRenderCoordinator(Facts());
        var lease = coordinator.Begin("s", retention.Entries, 0, 2, 1);
        coordinator.Dispose();
        Assert.True(lease.Cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task RenderCoordinator_ActiveProvisionalLease_CompletesOnceWithFaithfulFacts()
    {
        var allTools = Retained(Enumerable.Range(0, 6_000).Select(i => Tool($"t{i}", "read_file")).ToArray());
        using var coordinator = new ChatToolActivityPresentation.ChatToolActivityRenderCoordinator(Facts());
        var lease = coordinator.Begin("main", allTools.Entries, 5_000, 5_002, 1);
        Assert.Null(coordinator.TryBounded(lease));

        var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        coordinator.RequestCompletion(lease, () => { Interlocked.Increment(ref count); fired.TrySetResult(true); });
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, count);

        var faithful = coordinator.TryBounded(lease);
        Assert.NotNull(faithful);
        Assert.Equal("t0", faithful!.LeadingGroupFirstToolId);
    }

    // PENDING projection must mark rows pending and NEVER fabricate a logical continuation; after completion
    // the same window is a faithful group keyed on the logical first tool.
    [Fact]
    public void PendingProjection_MarksPending_AndCompletionIsSourceFaithful()
    {
        var full = Retained(
            Tool("one", "read_file"), Tool("two", "read_file"), Tool("three", "read_file"));
        var pendingRows = ChatToolActivityPresentation.Project(
            full.Entries.Take(2).ToArray(), "s", 7, true, null, pending: true);
        Assert.All(pendingRows, row => Assert.True(row.Pending));
        Assert.DoesNotContain(pendingRows, row => row.HiddenEarlierToolCount > 0);   // no invented prefix

        var context = Facts().ForWindow(full.Entries, 1, 3, 7);   // window [two, three]
        var faithful = ChatToolActivityPresentation.Project(
            full.Entries.Skip(1).Take(2).ToArray(), "s", 7, true, context);
        var reference = ChatToolActivityPresentation.Project(full.Entries, "s", 7).Single();
        Assert.Equal(reference.Key, faithful.Single().Key);
        Assert.False(faithful.Single().Pending);
    }
    // Retained facts must come from the FULL accepted history and match the full-source reference.
    [Fact]
    public void RetainedFacts_ToolOnlyHistory_MatchesFullSourceReference()
    {
        var full = Retained(Enumerable.Range(0, 20_000).Select(i => Tool($"t{i}", "read_file")).ToArray());
        var facts = new ChatTimelineRetainedFactsProducer().Get(full.Entries, 0);
        Assert.False(facts.Pending);
        Assert.False(facts.HasAnyUser);
        Assert.False(facts.CurrentTurnHasAssistant);
    }

    [Fact]
    public void RetainedFacts_EndingAssistant_ReportsCurrentTurnAssistant()
    {
        var items = Enumerable.Range(0, 5_000).Select(i => Tool($"t{i}", "read_file")).ToList();
        items.Add(Item("a", ChatTimelineItemKind.Assistant));
        var full = Retained(items.ToArray());
        var facts = new ChatTimelineRetainedFactsProducer().Get(full.Entries, 0);
        Assert.True(facts.CurrentTurnHasAssistant);
        Assert.False(facts.HasAnyUser);
    }

    [Fact]
    public void RetainedFacts_WithUser_ReportsHasAnyUser()
    {
        var items = new List<ChatTimelineItem> { Item("u0", ChatTimelineItemKind.User) };
        items.AddRange(Enumerable.Range(0, 5_000).Select(i => Tool($"t{i}", "read_file")));
        var full = Retained(items.ToArray());
        var facts = new ChatTimelineRetainedFactsProducer().Get(full.Entries, 0);
        Assert.True(facts.HasAnyUser);
    }

    [Fact]
    public async Task RetainedFacts_BoundedFastPathIsBoundedAndCompletesOffUi()
    {
        var full = Retained(Enumerable.Range(0, 20_000).Select(i => Tool($"t{i}", "read_file")).ToArray());
        var counting = new CountingList(full.Entries);
        var producer = new ChatTimelineRetainedFactsProducer();

        var bounded = producer.GetBounded(counting, 0);
        Assert.True(bounded.Pending);
        Assert.Null(bounded.HasAnyUser);
        var bound = ChatTimelineRetainedFactsProducer.BoundedResumeOperations * 2 + 8;
        Assert.True(counting.IndexReads <= bound, $"bounded facts read {counting.IndexReads} (bound {bound})");

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(producer.TryScheduleCompletion(counting, 0, CancellationToken.None, () => done.TrySetResult(true)));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var faithful = producer.GetBounded(counting, 0);
        Assert.False(faithful.Pending);
        Assert.False(faithful.HasAnyUser);
        Assert.False(producer.TryScheduleCompletion(counting, 0, CancellationToken.None, () => { }));   // already faithful
    }

    [Fact]
    public void NavigationAnchor_GenuinelyAbsentAfterCompleteIndexIsForgottenSoRepeatsDoNotRescan()
    {
        var nav = new ChatTimelineNavigationState();
        var original = Retained(Enumerable.Range(1, 20_000)
            .Select(i => new ChatTimelineItem($"e{i}", ChatTimelineItemKind.User, $"m{i}")).ToArray());
        nav.MoveOlder("t", original, 400);   // stores an anchor

        // A same-count edit renames EVERY id, so the anchor cannot be reconciled for this revision.
        var edited = original with
        {
            Entries = System.Collections.Immutable.ImmutableList.CreateRange(
                original.Entries.Select(e => e with { Id = "x" + e.Id })),
        };
        Assert.True(nav.Resolve("t", edited, 400).Pending);            // retained, NOT forgotten
        Assert.True(nav.HasPendingAnchor("t"));
        Assert.False(nav.CompleteAnchorResolution("t", edited, 400));  // genuinely absent after COMPLETE index
        Assert.False(nav.HasPendingAnchor("t"));

        var afterResolve = nav.FindIdLookups;
        nav.Resolve("t", edited, 400);
        nav.Resolve("t", edited, 400);
        Assert.Equal(afterResolve, nav.FindIdLookups);   // forgotten: repeats never rescan
    }
    [Fact]
    public async Task RetainedFactsCoordinator_SameScopeReusesLeaseAndCompletesOnce()
    {
        var full = Retained(Enumerable.Range(0, 20_000).Select(i => Tool($"t{i}", "read_file")).ToArray());
        using var coordinator = new ChatTimelineRetainedFactsCoordinator(new ChatTimelineRetainedFactsProducer());
        var lease = coordinator.Begin("s", full.Entries, 0);
        Assert.Null(coordinator.TryBounded(lease));   // provisional
        Assert.Same(lease, coordinator.Begin("s", full.Entries, 0));   // same scope reuses

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(coordinator.RequestCompletion(lease, () => done.TrySetResult(true)));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var facts = coordinator.TryBounded(lease);
        Assert.NotNull(facts);
        Assert.False(facts!.Value.HasAnyUser);
    }

    [Fact]
    public void RetainedFactsCoordinator_DifferentScopeCancelsPrevious()
    {
        var a = Retained(Enumerable.Range(0, 5_000).Select(i => Tool($"a{i}", "read_file")).ToArray());
        var b = Retained(Enumerable.Range(0, 5_000).Select(i => Tool($"b{i}", "read_file")).ToArray());
        using var coordinator = new ChatTimelineRetainedFactsCoordinator(new ChatTimelineRetainedFactsProducer());
        var leaseA = coordinator.Begin("s", a.Entries, 0);
        var leaseB = coordinator.Begin("s", b.Entries, 0);
        Assert.True(leaseA.Cancellation.IsCancellationRequested);
        Assert.False(leaseB.Cancellation.IsCancellationRequested);
    }

    [Fact]
    public void RetainedFactsCoordinator_NoLostWakeupWhenFaithfulBetweenReadAndRequest()
    {
        var full = Retained(Enumerable.Range(0, 20_000).Select(i => Tool($"t{i}", "read_file")).ToArray());
        var producer = new ChatTimelineRetainedFactsProducer();
        using var coordinator = new ChatTimelineRetainedFactsCoordinator(producer);
        var lease = coordinator.Begin("s", full.Entries, 0);
        Assert.Null(coordinator.TryBounded(lease));
        _ = producer.Get(full.Entries, 0);   // faithful lands between the read and the request
        Assert.False(coordinator.RequestCompletion(lease, () => { }));   // nothing scheduled
        Assert.NotNull(coordinator.TryBounded(lease));   // the reread sees them: no lost wakeup
    }

    [Fact]
    public void NavigationAnchor_DeepPendingAnchorIsCountedOncePerFrontChangeAndNotAbsent()
    {
        var nav = new ChatTimelineNavigationState();
        var original = Retained(Enumerable.Range(1, 20_000)
            .Select(i => new ChatTimelineItem($"e{i}", ChatTimelineItemKind.User, $"m{i}")).ToArray());
        nav.MoveOlder("t", original, 400);
        var afterStore = nav.FindIdLookups;

        // Prepend so the front changes AND the stored FirstVisibleId sits far beyond the bounded window.
        var prepended = original with
        {
            Entries = System.Collections.Immutable.ImmutableList.CreateRange(
                new[] { new ChatTimelineItem("new0", ChatTimelineItemKind.User, "new") })
                .AddRange(original.Entries.Select(e => e with { Id = "p" + e.Id })),
        };
        var pending = nav.Resolve("t", prepended, 400);
        Assert.True(pending.Pending);
        Assert.Equal(afterStore + 1, nav.FindIdLookups);   // exactly ONE bounded lookup per front change
        Assert.True(nav.HasPendingAnchor("t"));            // retained, NOT treated as absent

        // A COMPLETE index clears pending (rebases if present, forgets if genuinely gone).
        _ = nav.CompleteAnchorResolution("t", prepended, 400);
        Assert.False(nav.HasPendingAnchor("t"));
        Assert.False(nav.Resolve("t", prepended, 400).Pending);
    }
    private sealed class CountingList : IReadOnlyList<ChatTimelineItem>
    {
        private readonly IReadOnlyList<ChatTimelineItem> _inner;
        public CountingList(IReadOnlyList<ChatTimelineItem> inner) => _inner = inner;
        public int IndexReads { get; private set; }
        public ChatTimelineItem this[int index]
        {
            get { IndexReads++; return _inner[index]; }
        }
        public int Count => _inner.Count;
        public IEnumerator<ChatTimelineItem> GetEnumerator() => _inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _inner.GetEnumerator();
    }

    // ACTUAL bounded-first-render measurement (Mini preserved 20001 index reads / 2 rows at 9054): the
    // bounded fast path must not scan the full retained history.
    [Fact]
    public void BoundedFirstRender_ReadsAtMostTheHardBudget_NotFullHistory()
    {
        var items = Enumerable.Range(0, 20_000).Select(i => Tool($"t{i}", "read_file")).ToArray();
        var full = Retained(items);
        var counting = new CountingList(full.Entries);
        var facts = new ChatToolActivityPresentation.ChatToolActivitySourceFacts();

        var bounded = facts.ForWindowBounded(counting, 10_000, 10_002, 1);

        Assert.True(bounded.Provisional);
        var bound = ChatToolActivityPresentation.ChatToolActivitySourceFacts.BoundedResumeOperations + 16;
        Assert.True(counting.IndexReads <= bound,
            $"bounded first render read {counting.IndexReads} entries (bound {bound})");
    }

    private static int IndexOf(IReadOnlyList<ChatTimelineItem> entries, string id)
    {
        for (var index = 0; index < entries.Count; index++)
            if (string.Equals(entries[index].Id, id, StringComparison.Ordinal))
                return index;
        return -1;
    }

    private static ChatTimelineState Retained(params ChatTimelineItem[] items) =>
        ChatTimelineState.Initial() with
        {
            Entries = System.Collections.Immutable.ImmutableList.CreateRange(items),
            NextId = items.Length + 1,
        };

    // HARD BOUND: an all-tool retained history makes a deep window O(full history) if scanned synchronously.
    // The bounded fast path MUST hit the budget (provisional), and the async completion MUST resolve the true
    // source-faithful logical group start.
    [Fact]
    public async Task BoundedFastPath_IsBoundedAndCompletesFaithfullyOffRenderPath()
    {
        var items = Enumerable.Range(0, 5000)
            .Select(i => Tool($"t{i}", "read_file"))
            .ToArray();
        var full = Retained(items);

        var bounded = new ChatToolActivityPresentation.ChatToolActivitySourceFacts()
            .ForWindowBounded(full.Entries, 4000, 4001, 1);
        Assert.True(bounded.Provisional, "a deep window over an all-tool history must hit the hard budget");
        Assert.Null(bounded.LeadingGroupFirstToolId);   // budget hit before the true span start

        var asyncFacts = new ChatToolActivityPresentation.ChatToolActivitySourceFacts();
        Assert.True(asyncFacts.ForWindowBounded(full.Entries, 4500, 4501, 1).Provisional);
        var resolved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        asyncFacts.ScheduleCompletion(
            full.Entries, 4500, 4501, 1, CancellationToken.None, () => resolved.TrySetResult(true));
        await resolved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Read back through the ACTUAL bounded call (a faithful cache hit), not the unbounded helper.
        var faithful = asyncFacts.ForWindowBounded(full.Entries, 4500, 4501, 1);
        Assert.False(faithful.Provisional);
        Assert.Equal("t0", faithful.LeadingGroupFirstToolId);   // full-source logical group start
    }

    // Adopted from Mini-Actual-Window-Boundary-9054-Counterexample: a singleton window holding the FIRST tool
    // of a full-source group whose remaining tools are clipped AFTER the window must stay that GROUP.
    [Fact]
    public void FirstToolSingletonWindowRetainsFullSourceGroupMembership()
    {
        var full = Retained(Tool("one", "read_file"), Tool("two", "read_file"));
        var reference = ChatToolActivityPresentation.Project(full.Entries, "s", 7).Single();
        Assert.True(reference.IsActivityGroup);

        var context = new ChatToolActivityPresentation.ChatToolActivitySourceFacts()
            .ForWindow(full.Entries, 0, 1, 7);
        var actual = ChatToolActivityPresentation.Project(
            full.Entries.Take(1).ToArray(), "s", 7, true, context).Single();
        Assert.Equal(reference.Key, actual.Key);
        Assert.True(actual.IsActivityGroup);
    }

    // Adopted from the same counterexample: a window ending mid assistant-run must NOT report the last visible
    // assistant as a run END when the full source run continues after the window.
    [Fact]
    public void TrailingAssistantWindowPreservesFullSourceRunEnd()
    {
        var full = Retained(
            Item("a1", ChatTimelineItemKind.Assistant),
            Item("a2", ChatTimelineItemKind.Assistant));
        var expected = ChatToolActivityPresentation.ChatAssistantRunPolicy.Describe(full.Entries);
        Assert.False(expected[0].IsEnd);

        var context = new ChatToolActivityPresentation.ChatToolActivitySourceFacts()
            .ForWindow(full.Entries, 0, 1, 7);
        var actual = ChatToolActivityPresentation.ChatAssistantRunPolicy.Describe(
            full.Entries.Take(1).ToArray(),
            context.LeadingAssistantRunContinuesBefore,
            context.LeadingAssistantRunContinuesAfter);
        Assert.False(actual[0].IsEnd);
        Assert.Equal(expected[0].IsEnd, actual[0].IsEnd);
    }

    // PER-VISIBLE-ENTRY membership: every single-item window over a tool must be a group with the SAME logical
    // key as the full-source reference - including first/middle/last positions and windows that would otherwise
    // omit a group entirely.
    [Fact]
    public void SingleItemToolWindows_PerEntryMembershipMatchesFullSource()
    {
        var full = Retained(
            Item("user", ChatTimelineItemKind.User),
            Tool("one", "read_file"), Tool("two", "read_file"), Tool("three", "read_file"),
            Item("a1", ChatTimelineItemKind.Assistant),
            Tool("four", "read_file"), Tool("five", "read_file"));
        var membership = ChatToolActivityPresentation.Project(full.Entries, "s", 7)
            .Where(row => row.IsActivityGroup)
            .SelectMany(g => g.Tools.Select(tool => (tool.Id, g.Key)))
            .ToDictionary(x => x.Id, x => x.Key, StringComparer.Ordinal);

        var facts = new ChatToolActivityPresentation.ChatToolActivitySourceFacts();
        foreach (var id in new[] { "one", "two", "three", "four", "five" })
        {
            var start = IndexOf(full.Entries, id);
            var context = facts.ForWindow(full.Entries, start, start + 1, 7);
            var row = ChatToolActivityPresentation.Project(
                full.Entries.Skip(start).Take(1).ToArray(), "s", 7, true, context).Single();
            Assert.True(row.IsActivityGroup, "singleton window at {id} must remain a logical group");
            Assert.Equal(membership[id], row.Key);
        }
    }

    // Adopted from Mini-Actual-Window-Activity-Counterexample (1 executed / 1 FAILED at d81469ed): a bounded
    // window clipped [one,two,three,four] to [three,four]; the restricted projector re-anchored the logical
    // activity key from one to three. With the source-faithful window context the clipped projection keeps the
    // ORIGINAL logical group identity.
    [Fact]
    public void MovingWindow_PreservesLogicalActivityIdentityAcrossClippedBoundary()
    {
        var retained = Retained(
            Item("user", ChatTimelineItemKind.User),
            Tool("one", "read_file"),
            Tool("two", "read_file"),
            Tool("three", "read_file"),
            Tool("four", "read_file"));
        var reference = ChatToolActivityPresentation.Project(retained.Entries, "session", 7)
            .Single(row => row.IsActivityGroup);

        var window = ChatTimelineDisplayWindowPolicy.ComputeAnchored(retained, retained.Entries.Count, 2);
        Assert.Equal(2, window.VisibleEntries.Count);

        var facts = new ChatToolActivityPresentation.ChatToolActivitySourceFacts();
        var context = facts.ForWindow(
            retained.Entries, window.HiddenEarlierCount, retained.Entries.Count, 7);
        var clipped = ChatToolActivityPresentation.Project(
            window.VisibleEntries, "session", 7, true, context).Single();

        Assert.Equal(new[] { "three", "four" }, clipped.Tools.Select(tool => tool.Id));
        Assert.Equal(reference.Key, clipped.Key);
        Assert.True(clipped.IsActivityGroup);
    }

    // The bounded consumer must reproduce the SAME logical activity keys as the full-source reference for every
    // window position/size (moved and cut windows), so identity never depends on where the clip lands.
    [Fact]
    public void BoundedWindowConsumer_MatchesFullSourceActivityKeys_AcrossMovedAndCutWindows()
    {
        var retained = Retained(
            Item("user", ChatTimelineItemKind.User),
            Tool("one", "read_file"), Tool("two", "read_file"), Tool("three", "read_file"),
            Item("a1", ChatTimelineItemKind.Assistant),
            Tool("four", "read_file"), Tool("five", "read_file"),
            Item("user2", ChatTimelineItemKind.User),
            Tool("six", "read_file"), Tool("seven", "read_file"), Tool("eight", "read_file"),
            Item("a2", ChatTimelineItemKind.Assistant));

        var referenceKeys = ChatToolActivityPresentation.Project(retained.Entries, "s", 3)
            .Where(row => row.IsActivityGroup)
            .Select(row => row.Key)
            .ToHashSet();
        Assert.Equal(3, referenceKeys.Count);

        var facts = new ChatToolActivityPresentation.ChatToolActivitySourceFacts();
        for (var size = 1; size <= 3; size++)
        {
            for (var start = 0; start + size <= retained.Entries.Count; start++)
            {
                var end = start + size;
                var visible = retained.Entries.Skip(start).Take(size).ToArray();
                var context = facts.ForWindow(retained.Entries, start, end, 3);
                var rows = ChatToolActivityPresentation.Project(visible, "s", 3, true, context);
                foreach (var row in rows.Where(row => row.IsActivityGroup))
                    Assert.Contains(row.Key, referenceKeys);
            }
        }
    }

    // An immutable edit produces a NEW retained root: the source-fact context for the edited root is still
    // faithful (and never stale from the previous root).
    [Fact]
    public void BoundedWindowConsumer_EditedEntries_RemainSourceFaithful()
    {
        var retained = Retained(
            Item("user", ChatTimelineItemKind.User),
            Tool("one", "read_file"), Tool("two", "read_file"),
            Tool("three", "read_file"), Tool("four", "read_file"));
        var edited = retained with
        {
            Entries = retained.Entries.SetItem(3, retained.Entries[3] with { Text = "three-edited" }),
        };
        Assert.NotSame(retained.Entries, edited.Entries);

        var referenceKey = ChatToolActivityPresentation.Project(edited.Entries, "s", 9)
            .Single(row => row.IsActivityGroup).Key;
        var facts = new ChatToolActivityPresentation.ChatToolActivitySourceFacts();
        var context = facts.ForWindow(edited.Entries, 3, 5, 9);   // window [three-edited, four]
        var clipped = ChatToolActivityPresentation.Project(
            edited.Entries.Skip(3).Take(2).ToArray(), "s", 9, true, context)
            .Single(row => row.IsActivityGroup);
        Assert.Equal(referenceKey, clipped.Key);
    }

    // A window whose first visible entry is a mid-run assistant must NOT be reported as a run START.
    [Fact]
    public void BoundedWindow_AssistantRunContinuation_IsNotARunStart()
    {
        var retained = Retained(
            Item("a1", ChatTimelineItemKind.Assistant),
            Item("a2", ChatTimelineItemKind.Assistant),
            Item("user", ChatTimelineItemKind.User));
        var facts = new ChatToolActivityPresentation.ChatToolActivitySourceFacts();
        var context = facts.ForWindow(retained.Entries, 1, 3, 1);
        Assert.True(context.LeadingAssistantRunContinuesBefore);

        var positions = ChatToolActivityPresentation.ChatAssistantRunPolicy.Describe(
            retained.Entries.Skip(1).ToArray(), context.LeadingAssistantRunContinuesBefore);
        Assert.False(positions[0].IsStart);
        Assert.True(positions[0].IsEnd);
    }

    [Fact]
    public void Project_GroupsOnlyConsecutiveSpansOfAtLeastTwoTools()
    {
        var entries = new[]
        {
            Item("user", ChatTimelineItemKind.User),
            Tool("one", "powershell"),
            Item("assistant", ChatTimelineItemKind.Assistant),
            Tool("two", "read_file"),
            Tool("three", "write_file"),
            Item("reasoning", ChatTimelineItemKind.Reasoning),
            Tool("four", "web_fetch"),
            Item("status", ChatTimelineItemKind.Status),
            Tool("five", "grep"),
            Tool("six", "edit_file"),
            Item("permission", ChatTimelineItemKind.PermissionRequest),
        };

        var rows = ChatToolActivityPresentation.Project(entries, "session", 7);

        Assert.Equal(
            ["user", "one", "assistant", "two", "reasoning", "four", "status", "five", "permission"],
            rows.Select(row => row.Entry?.Id ?? row.Tools[0].Id));
        Assert.False(rows[1].IsActivityGroup);
        Assert.Equal(["two", "three"], rows[3].Tools.Select(static tool => tool.Id));
        Assert.Equal(["five", "six"], rows[7].Tools.Select(static tool => tool.Id));
    }

    [Fact]
    public void Project_PreservesChronologyAndHidesOnlyToolRows()
    {
        var entries = new[]
        {
            Item("user", ChatTimelineItemKind.User),
            Tool("tool-1", "powershell"),
            Tool("tool-2", "powershell"),
            Item("assistant", ChatTimelineItemKind.Assistant),
            Item("permission", ChatTimelineItemKind.PermissionRequest),
        };

        var rows = ChatToolActivityPresentation.Project(entries, "s", 1, showToolCalls: false);

        Assert.Equal(["user", "assistant", "permission"], rows.Select(row => row.Entry!.Id));
    }

    [Fact]
    public void ActivityKey_RemainsStableWhenAppendingAndChangesForGeneration()
    {
        var first = ChatToolActivityPresentation.Project(
            [Tool("a", "powershell"), Tool("b", "powershell")], "s", 4).Single();
        var appended = ChatToolActivityPresentation.Project(
            [Tool("a", "powershell"), Tool("b", "powershell"), Tool("c", "powershell")], "s", 4).Single();
        var newGeneration = ChatToolActivityPresentation.Project(
            [Tool("a", "powershell"), Tool("b", "powershell")], "s", 5).Single();
        var historyPrepended = ChatToolActivityPresentation.Project(
            [Item("history", ChatTimelineItemKind.Assistant), Tool("a", "powershell"), Tool("b", "powershell")],
            "s",
            4)[1];

        Assert.Equal(first.Key, appended.Key);
        Assert.Equal(first.Key, historyPrepended.Key);
        Assert.NotEqual(first.Key, newGeneration.Key);
    }

    [Fact]
    public void Project_KeepsRowKeyStableWhenStandaloneToolBecomesGroup()
    {
        var standalone = ChatToolActivityPresentation.Project(
            [Tool("first", "powershell")],
            "session",
            9).Single();
        var grouped = ChatToolActivityPresentation.Project(
            [Tool("first", "powershell"), Tool("second", "read_file")],
            "session",
            9).Single();

        Assert.False(standalone.IsActivityGroup);
        Assert.True(grouped.IsActivityGroup);
        Assert.Equal(standalone.Key, grouped.Key);
    }

    [Fact]
    public void Project_KeepsFailedToolsVisibleOutsideActivityGroups()
    {
        var rows = ChatToolActivityPresentation.Project(
            [
                Tool("success-1", "read_file"),
                Tool("success-2", "write_file"),
                Tool("failed", "powershell", result: ChatToolCallStatus.Error),
                Tool("success-3", "web_fetch"),
                Tool("success-4", "grep"),
            ],
            "session",
            9);

        Assert.Equal(3, rows.Count);
        Assert.True(rows[0].IsActivityGroup);
        Assert.False(rows[1].IsActivityGroup);
        Assert.Equal("failed", rows[1].Entry?.Id);
        Assert.Equal(ChatToolCallStatus.Error, rows[1].Entry?.ToolResult);
        Assert.True(rows[2].IsActivityGroup);
    }

    [Theory]
    [InlineData("powershell", ChatToolActivityCategory.Command)]
    [InlineData("system.run", ChatToolActivityCategory.Command)]
    [InlineData("functions.bash", ChatToolActivityCategory.Command)]
    [InlineData("process", ChatToolActivityCategory.Command)]
    [InlineData("run_terminal_cmd", ChatToolActivityCategory.Command)]
    [InlineData("read_file", ChatToolActivityCategory.Read)]
    [InlineData("notebook_read", ChatToolActivityCategory.Read)]
    [InlineData("view", ChatToolActivityCategory.Read)]
    [InlineData("apply_patch", ChatToolActivityCategory.Edit)]
    [InlineData("applypatch", ChatToolActivityCategory.Edit)]
    [InlineData("multi_edit", ChatToolActivityCategory.Edit)]
    [InlineData("notebookedit", ChatToolActivityCategory.Edit)]
    [InlineData("write_file", ChatToolActivityCategory.Write)]
    [InlineData("web_search", ChatToolActivityCategory.Search)]
    [InlineData("WebSearch", ChatToolActivityCategory.Search)]
    [InlineData("rg", ChatToolActivityCategory.Search)]
    [InlineData("ls", ChatToolActivityCategory.Search)]
    [InlineData("list", ChatToolActivityCategory.Search)]
    [InlineData("codebase_search", ChatToolActivityCategory.Search)]
    [InlineData("web_fetch", ChatToolActivityCategory.Fetch)]
    [InlineData("WebFetch", ChatToolActivityCategory.Fetch)]
    [InlineData("custom.calendar", ChatToolActivityCategory.Generic)]
    public void Classify_CoversWebAndWindowsAliases(
        string name,
        ChatToolActivityCategory expected) =>
        Assert.Equal(expected, ChatToolActivityPresentation.Classify(name));

    [Fact]
    public void Classify_TextEditorUsesCommand()
    {
        Assert.Equal(
            ChatToolActivityCategory.Read,
            ChatToolActivityPresentation.Classify("str_replace_editor", Args(("command", "view"))));
        Assert.Equal(
            ChatToolActivityCategory.Edit,
            ChatToolActivityPresentation.Classify("str_replace_based_edit_tool", Args(("command", "insert"))));
        Assert.Equal(
            ChatToolActivityCategory.Write,
            ChatToolActivityPresentation.Classify("str_replace_editor", Args(("command", "create"))));
    }

    [Fact]
    public void Summarize_UsesUniquePathsWhenAnyKnownOtherwiseInvocationCount()
    {
        var tools = new[]
        {
            Tool("r1", "read_file", Args(("path", "src/a.cs"))),
            Tool("r2", "read_file", Args(("file_path", "SRC/A.cs"))),
            Tool("r3", "read_file"),
            Tool("e1", "edit_file", Args(("path", "src/a.cs"))),
            Tool("e2", "apply_patch"),
            Tool("w1", "write_file", Args(("paths", new JsonArray("a", "b", "a")))),
        };

        var summary = ChatToolActivityPresentation.Summarize(tools);

        Assert.Equal(1, Count(summary, ChatToolActivityCategory.Read));
        Assert.Equal(1, Count(summary, ChatToolActivityCategory.Edit));
        Assert.Equal(2, Count(summary, ChatToolActivityCategory.Write));

        var pathless = ChatToolActivityPresentation.Summarize(
            [Tool("r1", "read_file"), Tool("r2", "read_file")]);
        Assert.Equal(2, Count(pathless, ChatToolActivityCategory.Read));
    }

    [Fact]
    public void Summarize_MultiFileCodexPatchCountsUniqueTargets()
    {
        const string patch = """
            *** Begin Patch
            *** Update File: src/a.cs
            @@
            -old
            +new
            *** Add File: src/b.cs
            +content
            *** End Patch
            """;
        var summary = ChatToolActivityPresentation.Summarize(
            [Tool("patch", "apply_patch", Args(("patch", patch)))]);

        Assert.Equal(2, Count(summary, ChatToolActivityCategory.Edit));
        Assert.Equal("Edited 2 files", Format(summary));
    }

    [Fact]
    public void ExtractPaths_StructuredChangesUseMoveDestination()
    {
        var changes = new JsonArray
        {
            new JsonObject { ["path"] = "src/a.cs" },
            new JsonObject
            {
                ["path"] = "src/old.cs",
                ["kind"] = new JsonObject { ["movePath"] = "src/new.cs" },
            },
        };

        var paths = ChatToolActivityPresentation.ExtractPaths(
            Args(("changes", changes)),
            "applypatch");

        Assert.Equal(
            ["src/a.cs", "src/new.cs"],
            paths.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractPaths_UnifiedDiffUsesNewPathAndOldPathForDeletion()
    {
        const string diff = """
            diff --git a/src/changed.cs b/src/changed.cs
            --- a/src/changed.cs
            +++ b/src/changed.cs
            @@ -1 +1 @@
            -old
            +new
            diff --git a/src/deleted.cs b/src/deleted.cs
            deleted file mode 100644
            --- a/src/deleted.cs
            +++ /dev/null
            @@ -1 +0,0 @@
            -gone
            """;

        var paths = ChatToolActivityPresentation.ExtractPaths(
            Args(("diff", diff)),
            "patch");

        Assert.Equal(
            ["src/changed.cs", "src/deleted.cs"],
            paths.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractPaths_RenameOnlyDiffPreservesDistinctSpacedTargets()
    {
        const string diff = """
            diff --git "a/src/old one.cs" "b/src/new one.cs"
            similarity index 100%
            rename from src/old one.cs
            rename to src/new one.cs
            diff --git "a/src/old two.cs" "b/src/new two.cs"
            similarity index 100%
            rename from src/old two.cs
            rename to src/new two.cs
            """;

        var paths = ChatToolActivityPresentation.ExtractPaths(
            Args(("diff", diff)),
            "patch");

        Assert.Equal(
            ["src/new one.cs", "src/new two.cs"],
            paths.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Summarize_UsesNewestRunningToolAndNamedGenericTools()
    {
        var tools = new[]
        {
            Tool("a", "calendar", result: ChatToolCallStatus.Success),
            Tool("b", "web_fetch"),
            Tool("c", "powershell", result: ChatToolCallStatus.InProgress),
            Tool("d", "custom.weather", result: null),
        };

        var summary = ChatToolActivityPresentation.Summarize(tools);

        Assert.Equal("d", summary.NewestRunningTool!.Id);
        var generic = Assert.Single(
            summary.Counts,
            count => count.Category == ChatToolActivityCategory.Generic);
        Assert.Equal(2, generic.Count);
        Assert.Equal(["calendar", "weather"], generic.ToolNames);
    }

    [Fact]
    public void Formatter_AggregatesGenericNamesAndCapsLongNameLists()
    {
        var named = ChatToolActivityPresentation.Summarize(
            [Tool("a", "calendar"), Tool("b", "weather"), Tool("c", "calendar")]);
        var manyNames = ChatToolActivityPresentation.Summarize(
            [Tool("a", "alpha"), Tool("b", "beta"), Tool("c", "gamma"), Tool("d", "delta")]);

        Assert.Equal("Used calendar, weather 3 times", Format(named));
        Assert.Equal("Used 4 tools", Format(manyNames));
        Assert.Single(named.Counts, count => count.Category == ChatToolActivityCategory.Generic);
        Assert.Single(manyNames.Counts, count => count.Category == ChatToolActivityCategory.Generic);
    }

    [Fact]
    public void Formatter_CapitalizesFirstSegment()
    {
        var summary = ChatToolActivityPresentation.Summarize(
        [
            Tool("r", "read_file", Args(("path", "a"))),
            Tool("e", "edit_file", Args(("path", "b"))),
        ]);

        Assert.Equal("Read 1 file, edited 1 file", Format(summary));
    }

    [Fact]
    public void Formatter_LocalizesUnnamedRunningToolFallback()
    {
        var summary = ChatToolActivityPresentation.Summarize(
            [Tool("running", "", result: ChatToolCallStatus.InProgress)]);
        var templates = EnglishTemplates() with { ToolFallback = "Localized tool" };

        Assert.Equal("Running Localized tool", ChatToolActivityFormatter.Format(summary, templates));
    }

    [Fact]
    public void ExpansionState_DefaultsCollapsedAndExplicitChoiceSurvivesUntilReset()
    {
        var state = new ChatToolActivityExpansionState();
        var running = ChatToolActivityPresentation.Summarize(
            [Tool("a", "powershell", result: ChatToolCallStatus.InProgress)]);

        Assert.False(state.IsExpanded("group", running, 0));
        state.SetExplicit("group", true, 0);
        Assert.True(state.IsExpanded("group", running, 0));
        Assert.False(state.IsExpanded("group", running, 1));
    }

    private static int Count(ChatToolActivitySummary summary, ChatToolActivityCategory category) =>
        summary.Counts.Single(count => count.Category == category).Count;

    private static string Format(ChatToolActivitySummary summary) =>
        ChatToolActivityFormatter.Format(summary, EnglishTemplates());

    private static ChatToolActivityFormatTemplates EnglishTemplates() => new(
            "Ran {0} command",
            "Ran {0} commands",
            "read {0} file",
            "read {0} files",
            "edited {0} file",
            "edited {0} files",
            "wrote {0} file",
            "wrote {0} files",
            "ran {0} search",
            "ran {0} searches",
            "fetched {0} page",
            "fetched {0} pages",
            "used {0} tool",
            "used {0} tools",
            "used {1}",
            "used {1} {0} times",
            "Running {0}",
            "Tool");

    private static ChatTimelineItem Item(string id, ChatTimelineItemKind kind) =>
        new(id, kind, id);

    private static ChatTimelineItem Tool(
        string id,
        string name,
        JsonObject? args = null,
        ChatToolCallStatus? result = ChatToolCallStatus.Success) =>
        new(id, ChatTimelineItemKind.ToolCall, id, ToolName: name, ToolResult: result, ToolArgs: args);

    private static JsonObject Args(params (string Name, object Value)[] values)
    {
        var result = new JsonObject();
        foreach (var (name, value) in values)
            result[name] = value is JsonNode node ? node : JsonValue.Create(value);
        return result;
    }
}
