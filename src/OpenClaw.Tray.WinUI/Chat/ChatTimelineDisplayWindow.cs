using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using OpenClaw.Chat;

namespace OpenClawTray.Chat;

/// <summary>
/// A bounded, MOVABLE display window over the full retained timeline. The complete logical/durable
/// history is never truncated or mutated: the window selects a fixed-size slice for the expensive
/// per-render projection. Older/newer navigation moves the window; it never grows to the full history.
/// </summary>
public sealed record ChatTimelineDisplayWindow(
    IReadOnlyList<ChatTimelineItem> VisibleEntries,
    int HiddenEarlierCount,
    int WindowSize,
    int HiddenLaterCount = 0,
    // True while a deep anchor source position is still being resolved off the UI path: the caller must NOT
    // treat this window as resolved (do not feed it into the projection cache / stale HiddenEarlierCount).
    bool Pending = false);

/// <summary>
/// Portable navigation state reused by the ACTUAL root: per-thread older/newer anchors over the
/// retained timeline. Common-case resolution is O(1); the anchor is reconciled by stable entry identity
/// only when the FRONT of the retained list changed (server older-page prepend or reset), never on every
/// live append/render. Keeps a fixed window size and never projects the full retained history.
/// </summary>
public sealed class ChatTimelineNavigationState
{
    private sealed record Anchor(int EndExclusive, string FirstRetainedId, string? FirstVisibleId);

    private readonly Dictionary<string, Anchor> _anchors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingAnchors = new(StringComparer.Ordinal);
    private long _revision;   // bumped on every anchor mutation so a lease is revision-bound
    // The IMMUTABLE retained root the pending anchor was last observed against: a lease captured from a
    // DIFFERENT root (same count/front) must be rejected.
    private readonly Dictionary<string, IReadOnlyList<ChatTimelineItem>> _pendingAnchorRoots = new(StringComparer.Ordinal);
    // LOGICAL navigation identity (provider REFERENCE + accepted session UUID) the anchors for a thread belong
    // to. Enforced BEFORE a window is computed so a UUID/provider transition on the SAME thread drops the scoped
    // anchor state even when NO anchor flight is pending; a pre-reset RESOLVED anchor can never leak.
    private readonly Dictionary<string, NavigationIdentity> _identities = new(StringComparer.Ordinal);
    // Short state lock: the off-UI anchor resolver commits under THIS lock only (never a full scan under it).
    private readonly object _sync = new();

    private sealed record NavigationIdentity(object? Provider, string? AcceptedSessionUuid);

    /// <summary>Count of source-revision anchor searches (one per irreconcilable front change).</summary>
    public int FindIdLookups { get; private set; }

    /// <summary>Resolves the current bounded window for a thread (tail when never navigated).</summary>
    public ChatTimelineDisplayWindow Resolve(string? threadId, ChatTimelineState timeline, int windowSize)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (threadId is null)
            return ChatTimelineDisplayWindowPolicy.Compute(timeline, windowSize);
        Anchor anchor;
        lock (_sync)
        {
            if (!_anchors.TryGetValue(threadId, out anchor!))
                return ChatTimelineDisplayWindowPolicy.Compute(timeline, windowSize);
        }

        var entries = timeline.Entries;
        if (entries.Count == 0)
        {
            // Empty / reset timeline: drop any stale anchor and return an empty window. Never index
            // entries[0] here (an empty retained list has no front).
            lock (_sync)
            {
                _anchors.Remove(threadId);
                _pendingAnchors.Remove(threadId);
            }
            return ChatTimelineDisplayWindowPolicy.Compute(timeline, windowSize);
        }

        var frontChanged = !string.Equals(entries[0].Id, anchor.FirstRetainedId, StringComparison.Ordinal);
        var end = anchor.EndExclusive;
        if (frontChanged)
        {
            if (entries.Count <= windowSize)
            {
                // The whole retained timeline fits: no anchor is needed.
                lock (_sync)
                {
                    _anchors.Remove(threadId);
                    _pendingAnchors.Remove(threadId);
                }
                return ChatTimelineDisplayWindowPolicy.Compute(timeline, windowSize);
            }
            if (anchor.FirstVisibleId is { } firstVisibleId)
            {
                // Reconcile by stable entry identity so a server older-page prepend does not jump the
                // view. This is O(n) on the FRONT-change path only (rare: prepend/reset), not per render.
                FindIdLookups++;
                // BOUNDED synchronous first lookup. A NOT-found deep anchor is NOT absent: retain it and return
                // an EXPLICIT PENDING window (never a tail mislabeled as resolved). The off-UI resolver completes
                // the source-position index and the next render rebases, preserving the deep IDs.
                var index = FindId(entries, firstVisibleId, ChatTimelineDisplayWindowPolicy.BoundedFindIdOperations);
                lock (_sync)
                {
                    // Re-verify the anchor under the short lock: a background commit or live action may have
                    // superseded it while the bounded scan ran outside the lock.
                    if (!_anchors.TryGetValue(threadId, out var current) ||
                        !string.Equals(current.FirstVisibleId, firstVisibleId, StringComparison.Ordinal))
                    {
                        return ChatTimelineDisplayWindowPolicy.ComputeAnchored(timeline, current?.EndExclusive ?? entries.Count, windowSize);
                    }
                    if (index < 0)
                    {
                        _pendingAnchors.Add(threadId);
                        _pendingAnchorRoots[threadId] = entries;   // bind pending to THIS immutable root
                        return ChatTimelineDisplayWindowPolicy.Compute(timeline, windowSize) with { Pending = true };
                    }
                    _pendingAnchors.Remove(threadId);
                    end = index + windowSize;
                    _anchors[threadId] = current with { EndExclusive = end, FirstRetainedId = entries[0].Id };
                }
            }
        }

        return ChatTimelineDisplayWindowPolicy.ComputeAnchored(timeline, end, windowSize);
    }

    /// <summary>Moves the bounded window one page toward older entries.</summary>
    public ChatTimelineDisplayWindow MoveOlder(
        string threadId, ChatTimelineState timeline, int windowSize)
    {
        var current = Resolve(threadId, timeline, windowSize);
        var newEnd = Math.Max(windowSize, current.HiddenEarlierCount);
        Store(threadId, timeline, newEnd);
        return Resolve(threadId, timeline, windowSize);
    }

    /// <summary>Moves the bounded window one page back toward newer entries.</summary>
    public ChatTimelineDisplayWindow MoveNewer(
        string threadId, ChatTimelineState timeline, int windowSize)
    {
        var current = Resolve(threadId, timeline, windowSize);
        var end = current.HiddenEarlierCount + current.VisibleEntries.Count;
        var newEnd = Math.Min(timeline.Entries.Count, end + ChatTimelineDisplayWindowPolicy.NavigationPageSize);
        if (newEnd >= timeline.Entries.Count)
        {
            lock (_sync)
            {
                _anchors.Remove(threadId);              // back at the live tail
                _pendingAnchors.Remove(threadId);       // supersede any in-flight deep-anchor resolution
                _pendingAnchorRoots.Remove(threadId);
                _revision++;                            // a lease from before the live return is stale
            }
            return ChatTimelineDisplayWindowPolicy.Compute(timeline, windowSize);
        }
        Store(threadId, timeline, newEnd);
        return Resolve(threadId, timeline, windowSize);
    }

    /// <summary>Returns the thread to the live tail.</summary>
    public ChatTimelineDisplayWindow ReturnToLive(
        string threadId, ChatTimelineState timeline, int windowSize)
    {
        lock (_sync)
        {
            _anchors.Remove(threadId);
            _pendingAnchors.Remove(threadId);   // supersede any in-flight deep-anchor resolution
            _pendingAnchorRoots.Remove(threadId);
            _revision++;
        }
        return ChatTimelineDisplayWindowPolicy.Compute(timeline, windowSize);
    }

    /// <summary>Drops navigation state for a thread (reset / identity change).</summary>
    public void Forget(string threadId)
    {
        lock (_sync)
        {
            _anchors.Remove(threadId);
            _pendingAnchors.Remove(threadId);
            _pendingAnchorRoots.Remove(threadId);
            _identities.Remove(threadId);   // release the provider reference with the scoped navigation
            _revision++;   // a lease bound to this navigation is now stale
        }
    }

    /// <summary>
    /// Enforces the LOGICAL navigation identity (provider REFERENCE + accepted session UUID) for a thread
    /// BEFORE any window is computed. A transition on the SAME thread drops the scoped anchor state and
    /// supersedes any pending resolution, so a pre-reset ANCHOR - whether or not a flight is pending - can never
    /// leak into the new identity. The SAME identity PRESERVES the thread's navigation across ordinary older
    /// pages. Returns true when a previously recorded identity was superseded.
    /// </summary>
    public bool EnsureNavigationIdentity(object? provider, string threadId, string? acceptedSessionUuid)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        lock (_sync)
        {
            if (_identities.TryGetValue(threadId, out var existing) &&
                ReferenceEquals(existing.Provider, provider) &&
                string.Equals(existing.AcceptedSessionUuid, acceptedSessionUuid, StringComparison.Ordinal))
            {
                return false;   // same logical identity: keep this thread's anchors
            }

            var superseded = _identities.ContainsKey(threadId);
            _identities[threadId] = new NavigationIdentity(provider, acceptedSessionUuid);
            if (superseded)
            {
                _anchors.Remove(threadId);
                _pendingAnchors.Remove(threadId);
                _pendingAnchorRoots.Remove(threadId);
                _revision++;   // a lease bound to the previous identity is now stale
            }
            return superseded;
        }
    }

    /// <summary>True while a deep anchor source position is still being resolved.</summary>
    public bool HasPendingAnchor(string threadId) => _pendingAnchors.Contains(threadId);

    /// <summary>
    /// Captures the EXACT pending-anchor identity the off-UI resolver must find. The lease is compare-and-committed
    /// via TryCommitAnchorIndex, so a stale completion can never reinsert an anchor or revert a live view.
    /// </summary>
    public AnchorResolutionLease? CapturePendingAnchor(
        string threadId, ChatTimelineState timeline, int windowSize, string? acceptedSessionUuid = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        lock (_sync)
        {
            if (!_pendingAnchors.Contains(threadId) ||
                !_anchors.TryGetValue(threadId, out var anchor) ||
                anchor.FirstVisibleId is not { } firstVisibleId)
            {
                return null;
            }
            return new AnchorResolutionLease(
                threadId, windowSize, firstVisibleId, anchor.FirstRetainedId, timeline.Entries.Count,
                timeline.Entries, _revision, acceptedSessionUuid);
        }
    }

    /// <summary>
    /// COMPLETE source-position index for a captured anchor. PURE: performs the unbounded linear search OFF the UI
    /// path and NEVER touches state, so it is safe on a background thread. The caller decides "truly absent" only
    /// from THIS complete index (-1); a partial scan is never treated as absence.
    /// </summary>
    public static int FindAnchorIndex(IReadOnlyList<ChatTimelineItem> entries, AnchorResolutionLease lease)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(lease);
        for (var i = 0; i < entries.Count; i++)
        {
            if (string.Equals(entries[i].Id, lease.FirstVisibleId, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Commits a completed index ONLY while the captured anchor is STILL the pending one for the thread. A
    /// superseded scope (ReturnToLive/Forget/Clear/reset/UUID/provider dispose/new front) returns false, so the
    /// stale completion is dropped instead of reverting the live view. index &lt; 0 forgets (truly absent).
    /// </summary>
    public bool TryCommitAnchorIndex(AnchorResolutionLease lease, ChatTimelineState timeline, int index)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(timeline);
        lock (_sync)
        {
            if (!_pendingAnchors.Contains(lease.ThreadId) ||
                !_anchors.TryGetValue(lease.ThreadId, out var anchor) ||
                !string.Equals(anchor.FirstVisibleId, lease.FirstVisibleId, StringComparison.Ordinal))
            {
                return false;   // superseded: never reinsert/revert
            }
            // IMMUTABLE-root + revision guard: a same-count/same-front but DIFFERENT root (or newer revision)
            // must reject a stale capture even when front/count still match.
            if (!ReferenceEquals(timeline.Entries, lease.Root) || _revision != lease.NavigationRevision)
                return false;
            if (!_pendingAnchorRoots.TryGetValue(lease.ThreadId, out var pendingRoot) ||
                !ReferenceEquals(pendingRoot, lease.Root))
            {
                return false;   // the app has moved to a different immutable root
            }
            _pendingAnchors.Remove(lease.ThreadId);
            _pendingAnchorRoots.Remove(lease.ThreadId);
            _revision++;
            if (index < 0)
            {
                _anchors.Remove(lease.ThreadId);   // truly absent after the COMPLETE index
                return true;
            }
            var entries = timeline.Entries;
            _anchors[lease.ThreadId] = anchor with
            {
                EndExclusive = index + lease.WindowSize,
                FirstRetainedId = entries.Count == 0 ? string.Empty : entries[0].Id,
            };
            return true;
        }
    }

    /// <summary>
    /// COMPLETE (unbounded) resolution of a pending deep anchor, run OFF the UI path by the root lifecycle.
    /// Applies the found index and clears pending; genuinely absent after the COMPLETE search forgets the anchor
    /// (never a scan limit treated as absence).
    /// </summary>
    public bool CompleteAnchorResolution(string threadId, ChatTimelineState timeline, int windowSize)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (!_anchors.TryGetValue(threadId, out var anchor) || anchor.FirstVisibleId is not { } firstVisibleId)
        {
            _pendingAnchors.Remove(threadId);
            return false;
        }
        var entries = timeline.Entries;
        var index = FindId(entries, firstVisibleId, entries.Count);   // COMPLETE search
        _pendingAnchors.Remove(threadId);
        if (index < 0)
        {
            _anchors.Remove(threadId);   // genuinely absent after the complete index
            return false;
        }
        _anchors[threadId] = anchor with
        {
            EndExclusive = index + windowSize,
            FirstRetainedId = entries.Count == 0 ? string.Empty : entries[0].Id,
        };
        return true;
    }

    public void Clear()
    {
        lock (_sync)
        {
            _anchors.Clear();
            _pendingAnchors.Clear();
            _pendingAnchorRoots.Clear();
            _identities.Clear();
            _revision++;   // a lease bound to this navigation is now stale
        }
    }

    private void Store(string threadId, ChatTimelineState timeline, int endExclusive)
    {
        var entries = timeline.Entries;
        if (entries.Count == 0)
        {
            _anchors.Remove(threadId);
            return;
        }
        var clamped = Math.Clamp(endExclusive, 0, entries.Count);
        var start = Math.Max(0, clamped - ChatTimelineDisplayWindowPolicy.DefaultWindowSize);
        var firstVisibleId = clamped > start ? entries[start].Id : null;
        lock (_sync)
        {
            _anchors[threadId] = new Anchor(clamped, entries[0].Id, firstVisibleId);
            _pendingAnchors.Remove(threadId);
            _pendingAnchorRoots.Remove(threadId);
            _revision++;
        }
    }

    private static int FindId(
        System.Collections.Immutable.ImmutableList<ChatTimelineItem> entries, string id, int maxOperations)
    {
        var bound = Math.Min(entries.Count, maxOperations);
        for (var i = 0; i < bound; i++)
        {
            if (string.Equals(entries[i].Id, id, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }
}

/// <summary>Bounded display-window policy: fixed window size and one-page navigation step.</summary>
public static class ChatTimelineDisplayWindowPolicy
{
    public const int DefaultWindowSize = 400;
    public const int NavigationPageSize = 200;

    /// <summary>Hard bound on the FIRST anchor-reconcile search (front-change path).</summary>
    public const int BoundedFindIdOperations = 4096;

    /// <summary>Window anchored at the end of the retained timeline (the live tail).</summary>
    public static ChatTimelineDisplayWindow Compute(ChatTimelineState timeline, int windowSize) =>
        ComputeAnchored(timeline, timeline.Entries.Count, windowSize);

    /// <summary>
    /// Window of at most <paramref name="windowSize"/> entries ending at <paramref name="endExclusive"/>
    /// (chronological order preserved). O(windowSize), never O(retained).
    /// </summary>
    public static ChatTimelineDisplayWindow ComputeAnchored(
        ChatTimelineState timeline, int endExclusive, int windowSize)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (windowSize < 1)
            windowSize = 1;
        var all = timeline.Entries;
        var count = all.Count;
        var end = Math.Clamp(endExclusive, 0, count);
        var start = Math.Max(0, end - windowSize);
        var visible = new List<ChatTimelineItem>(end - start);
        for (var i = start; i < end; i++)
            visible.Add(all[i]);
        return new ChatTimelineDisplayWindow(visible, start, windowSize, count - end);
    }

    /// <summary>Moves the bounded window one page toward older entries (size stays bounded).</summary>
    public static ChatTimelineDisplayWindow MoveOlder(
        ChatTimelineDisplayWindow current, ChatTimelineState timeline)
    {
        ArgumentNullException.ThrowIfNull(current);
        var end = current.HiddenEarlierCount + current.VisibleEntries.Count;
        var oldestEnd = Math.Min(current.WindowSize, timeline.Entries.Count);
        return ComputeAnchored(timeline, Math.Max(oldestEnd, end - NavigationPageSize), current.WindowSize);
    }

    /// <summary>Moves the bounded window one page back toward newer entries.</summary>
    public static ChatTimelineDisplayWindow MoveNewer(
        ChatTimelineDisplayWindow current, ChatTimelineState timeline)
    {
        ArgumentNullException.ThrowIfNull(current);
        var end = current.HiddenEarlierCount + current.VisibleEntries.Count;
        return ComputeAnchored(
            timeline,
            Math.Min(timeline.Entries.Count, end + NavigationPageSize),
            current.WindowSize);
    }
}

/// <summary>
/// Fixed-capacity projection cache keyed by a STABLE WEAK IDENTITY of the IMMUTABLE Entries root (not
/// count/NextId, which collide on immutable SetItem edits such as streaming text / tool / permission
/// updates), plus the actual session UUID, generation and window size. The identity is a plain token
/// obtained from a ConditionalWeakTable keyed by the Entries root: the token NEVER references the root,
/// so the cache cannot retain an abandoned full-history root merely because it holds a bounded NUMBER of
/// visible rows. Distinct roots (including a new root from an immutable SetItem edit) get distinct tokens,
/// preserving SetItem invalidation and reset/UUID/anchor isolation. Memory is bounded by entry slots AND
/// by cached rows. Keying uses reference identity only - no per-render content hashing or full scan.
/// </summary>
public sealed class ChatTimelineProjectionCache
{
    /// <summary>
    /// Identity-only token stored as the VALUE of the weak root table. It is intentionally empty: it must
    /// never reference the timeline root, so holding it can never keep an abandoned root alive.
    /// </summary>
    private sealed class RootIdentityToken
    {
    }

    private readonly int _capacity;
    private readonly int _maxRows;
    // Weak root -> token map: the root is held WEAKLY here, so an abandoned root is collectible while the
    // cache remains alive. The dictionary/LRU below retain only the token, never the root.
    private readonly ConditionalWeakTable<ImmutableList<ChatTimelineItem>, RootIdentityToken> _rootIdentities = new();
    private readonly Dictionary<
        (string? SessionId, long Generation, int WindowSize, int Anchor, RootIdentityToken Root),
        ChatTimelineDisplayWindow> _windows = new();
    private readonly Queue<
        (string? SessionId, long Generation, int WindowSize, int Anchor, RootIdentityToken Root)> _order = new();
    private int _cachedRows;

    public ChatTimelineProjectionCache(int capacity = 8, int maxRows = 4_000)
    {
        _capacity = Math.Max(1, capacity);
        _maxRows = Math.Max(1, maxRows);
    }

    /// <summary>Entries visited by the most recent MISS (0 on a hit) - measured projection work.</summary>
    public int LastMissVisitedEntries { get; private set; }

    /// <summary>Number of computed (miss) projections so far.</summary>
    public int MissCount { get; private set; }

    /// <summary>Rows currently held by the cache (bounded by maxRows).</summary>
    public int CachedRows => _cachedRows;

    public ChatTimelineDisplayWindow Get(
        string? sessionId, ChatTimelineState timeline, long generation, int windowSize,
        int? endExclusive = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var anchor = endExclusive ?? timeline.Entries.Count;
        var root = _rootIdentities.GetValue(timeline.Entries, static _ => new RootIdentityToken());
        var key = (sessionId, generation, windowSize, anchor, root);
        if (_windows.TryGetValue(key, out var cached))
        {
            LastMissVisitedEntries = 0;
            return cached;
        }

        var computed = ChatTimelineDisplayWindowPolicy.ComputeAnchored(timeline, anchor, windowSize);
        // Only cache windows that FIT the row bound; an oversized window is returned but never cached,
        // so CachedRows can never exceed maxRows for any accepted configuration.
        if (computed.VisibleEntries.Count <= _maxRows)
        {
            _windows[key] = computed;
            _order.Enqueue(key);
            _cachedRows += computed.VisibleEntries.Count;
            while (_order.Count > _capacity || _cachedRows > _maxRows)
            {
                var evicted = _order.Dequeue();
                if (_windows.Remove(evicted, out var removed))
                    _cachedRows -= removed.VisibleEntries.Count;
            }
        }
        LastMissVisitedEntries = computed.VisibleEntries.Count;
        MissCount++;
        return computed;
    }
}


/// <summary>
/// Scoped holder for the last STABLE bounded window: a held window is valid ONLY for the SAME logical session
/// identity (thread + accepted UUID + provider). A scope change discards it, so an offset from one timeline root
/// can never be applied to a DIFFERENT root.
/// </summary>
/// <summary>
/// STRUCTURED logical identity of a held window. Reference identity is enforced explicitly for the provider:
/// a provider that overrides Equals to report value-equality can NEVER be confused with a different object.
/// </summary>
public readonly struct ChatTimelineWindowScopeKey : IEquatable<ChatTimelineWindowScopeKey>
{
    public ChatTimelineWindowScopeKey(object? provider, string? threadId, string? acceptedSessionUuid)
    {
        Provider = provider;
        ThreadId = threadId;
        AcceptedSessionUuid = acceptedSessionUuid;
    }

    public object? Provider { get; }
    public string? ThreadId { get; }
    public string? AcceptedSessionUuid { get; }

    public bool Equals(ChatTimelineWindowScopeKey other) =>
        ReferenceEquals(Provider, other.Provider) &&
        string.Equals(ThreadId, other.ThreadId, StringComparison.Ordinal) &&
        string.Equals(AcceptedSessionUuid, other.AcceptedSessionUuid, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ChatTimelineWindowScopeKey other && Equals(other);

    public override int GetHashCode() =>
        (Provider is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Provider));
}

public sealed class ChatTimelineStableWindowScope
{
    private readonly object _sync = new();
    private (ChatTimelineWindowScopeKey Key, ChatTimelineDisplayWindow Window)? _held;

    public ChatTimelineDisplayWindow? Get(ChatTimelineWindowScopeKey key)
    {
        lock (_sync)
            return _held is { } held && held.Key.Equals(key) ? held.Window : null;
    }

    public void Set(ChatTimelineWindowScopeKey key, ChatTimelineDisplayWindow window)
    {
        lock (_sync)
            _held = (key, window);
    }

    public void Clear()
    {
        lock (_sync)
            _held = null;
    }
}

/// <summary>
/// ACTUAL pending presentation policy for a deep-anchor resolution in flight: the caller must NEVER render the
/// new tail / stale offset as resolved. Hold the last STABLE bounded window when one is available (same bounded
/// IDs, so expansion/context are preserved), otherwise present an explicit LOADING window (no rows, Pending).
/// </summary>
public static class ChatTimelinePendingPresentationPolicy
{
    public static ChatTimelineDisplayWindow Resolve(
        ChatTimelineDisplayWindow? lastStable, ChatTimelineDisplayWindow navigationWindow)
    {
        ArgumentNullException.ThrowIfNull(navigationWindow);
        if (!navigationWindow.Pending)
            return navigationWindow;
        if (lastStable is not null && !lastStable.Pending)
            return lastStable with { Pending = true };   // hold last stable bounded IDs, marked loading
        return navigationWindow with
        {
            VisibleEntries = Array.Empty<ChatTimelineItem>(),
            HiddenEarlierCount = 0,
            HiddenLaterCount = 0,
            Pending = true,
        };
    }
}

/// <summary>
/// Identity of a pending deep-anchor resolution: the exact (thread, window, first-visible id, retained front)
/// the off-UI resolver must find. Compare-and-committed so a stale completion cannot reinsert an anchor or
/// revert the live view.
/// </summary>
public sealed record AnchorResolutionLease(
    string ThreadId,
    int WindowSize,
    string FirstVisibleId,
    string FirstRetainedId,
    int RetainedCount,
    // IMMUTABLE-root identity + navigation revision: a lease is bound to the EXACT retained root and revision it
    // was captured from, so a same-count/same-front but DIFFERENT root (shifted positions) can never be reused or
    // apply a stale index.
    IReadOnlyList<ChatTimelineItem> Root,
    long NavigationRevision,
    // AUTHORITATIVE accepted session UUID this capture belongs to: a UUID identity transition invalidates the
    // scoped anchor state even when the root/front/count/row ids are identical.
    string? AcceptedSessionUuid);

/// <summary>
/// Lifecycle owner of the DEEP-ANCHOR source-position resolution for one root: one owned off-UI flight per
/// active lease, cancelled/superseded on scope change, and a completion that is committed ONLY while its lease
/// is still current. The COMPLETE index runs OFF the UI path (pure search); the commit happens under the
/// navigation state's short lock. A partial scan is never treated as absence.
/// </summary>
public sealed class ChatTimelineAnchorResolutionCoordinator : IDisposable
{
    private readonly ChatTimelineNavigationState _navigation;
    private readonly object _sync = new();
    private AnchorResolutionLease? _active;
    private Flight? _flight;      // the OWNED flight (identity), never a bare bool
    private Request? _latest;     // latest capture requested this render (for autonomous drain)
    private Action? _lastOnResolved;   // reused so a drained new capture still re-renders
    private bool _disposed;

    private sealed record Request(AnchorResolutionLease Lease, ChatTimelineState Timeline, Action OnResolved);

    private sealed class Flight
    {
        public Flight(Request request) => Request = request;
        public Request Request { get; }
        public CancellationTokenSource Cancellation { get; } = new();
    }

    public ChatTimelineAnchorResolutionCoordinator(ChatTimelineNavigationState navigation) =>
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));

    /// <summary>
    /// Registers this render's scope and returns the pending resolution lease (null when there is none). A
    /// DIFFERENT pending scope supersedes the previous one so its completion is dropped.
    /// </summary>
    public AnchorResolutionLease? Begin(
        string threadId, ChatTimelineState timeline, int windowSize, string? acceptedSessionUuid = null)
    {
        lock (_sync)
        {
            if (_disposed)
                return null;
            // UUID IDENTITY TRANSITION: the same thread with a NEW accepted UUID invalidates the scoped anchor
            // state, so a pre-reset lease can never be reused even when root/front/count/row ids match.
            if (_active is { } previous &&
                string.Equals(previous.ThreadId, threadId, StringComparison.Ordinal) &&
                !string.Equals(previous.AcceptedSessionUuid, acceptedSessionUuid, StringComparison.Ordinal))
            {
                _latest = null;
                _navigation.Forget(threadId);
                _flight?.Cancellation.Cancel();
            }
            var lease = _navigation.CapturePendingAnchor(threadId, timeline, windowSize, acceptedSessionUuid);
            if (lease is null)
            {
                _active = null;
                _latest = null;
                _lastOnResolved = null;
                _flight?.Cancellation.Cancel();   // no new capture: stop the obsolete producer
                return null;
            }
            if (_active is { } active &&
                string.Equals(active.ThreadId, lease.ThreadId, StringComparison.Ordinal) &&
                string.Equals(active.FirstVisibleId, lease.FirstVisibleId, StringComparison.Ordinal) &&
                ReferenceEquals(active.Root, lease.Root) &&          // SAME immutable root
                active.NavigationRevision == lease.NavigationRevision)
            {
                return active;
            }
            _active = lease;
            // A CHANGED capture (Begin-only, no TryResolvePending) must stop the obsolete producer PROMPTLY: its
            // completion would be dropped anyway, so cancel the in-flight full scan at its next batch boundary
            // instead of letting it run to completion. The drain resumes the latest capture when a callback is
            // registered for the current scope.
            if (_flight is { } obsolete && !SameLease(obsolete.Request.Lease, lease))
                obsolete.Cancellation.Cancel();
            // Record the new capture ONLY when a callback is registered for the current scope (a stale callback
            // from a previous provider must never be reused).
            _latest = _lastOnResolved is { } registered ? new Request(lease, timeline, registered) : null;
            return lease;
        }
    }

    /// <summary>
    /// Starts the ONE owned off-UI completion for the active lease (no spin/retry). Returns false when nothing
    /// was started (no pending anchor, a flight is already owned, or superseded). onResolved fires only when the
    /// commit was applied for the STILL-current lease.
    /// </summary>
    public bool TryResolvePending(ChatTimelineState timeline, Action onResolved)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(onResolved);
        AnchorResolutionLease active;
        Flight flight;
        lock (_sync)
        {
            if (_disposed || _active is not { } current)
                return false;
            active = current;
            _lastOnResolved = onResolved;
            _latest = new Request(active, timeline, onResolved);   // remember for autonomous drain
            if (_flight is { } owned)
            {
                if (SameLease(owned.Request.Lease, active))
                    return true;                                   // same capture already in flight
                owned.Cancellation.Cancel();                       // stop the obsolete producer promptly
                return true;                                       // ONE physical producer; the drain resumes latest
            }
            flight = new Flight(_latest!);
            _flight = flight;
        }
        _ = Task.Run(() => RunFlight(flight), CancellationToken.None);
        return true;
    }

    /// <summary>Deterministic test instrumentation: counts PHYSICAL producer tasks actually started.</summary>
    internal int PhysicalProducersStarted;

    /// <summary>Deterministic test instrumentation: maximum concurrently running producer tasks.</summary>
    internal int MaxConcurrentProducers;

    private int _concurrentProducers;

    /// <summary>Deterministic test gate invoked at the very start of a flight, BEFORE the scan.</summary>
    internal Action? BeforeScanForTests { get; set; }

    /// <summary>Resolves when a flight has passed its pre-scan gate (test synchronization, not a sleep).</summary>
    internal event Action? FlightEnteredScanForTests;

    /// <summary>Fired after a physical producer has fully drained (test synchronization, not a sleep).</summary>
    internal event Action? FlightCompletedForTests;

    private void RunFlight(Flight flight)
    {
        try
        {
            Interlocked.Increment(ref PhysicalProducersStarted);
            var concurrent = Interlocked.Increment(ref _concurrentProducers);
            for (var current = Volatile.Read(ref MaxConcurrentProducers); concurrent > current;
                 current = Volatile.Read(ref MaxConcurrentProducers))
            {
                Interlocked.CompareExchange(ref MaxConcurrentProducers, concurrent, current);
            }
            BeforeScanForTests?.Invoke();
            FlightEnteredScanForTests?.Invoke();
            // COMPLETE search OFF the UI path, PER-BATCH cancellable; no shared UI lock held.
            var index = FindIndex(
                flight.Request.Timeline.Entries, flight.Request.Lease.FirstVisibleId,
                flight.Cancellation.Token);
            if (flight.Cancellation.IsCancellationRequested)
                return;
            bool committed;
            lock (_sync)
            {
                if (_disposed || _active is not { } current ||
                    !SameLease(current, flight.Request.Lease) ||
                    !ReferenceEquals(_flight, flight))
                {
                    return;   // superseded: drop the completion (never publish obsolete work)
                }
                committed = _navigation.TryCommitAnchorIndex(
                    flight.Request.Lease, flight.Request.Timeline, index);
            }
            if (committed && !flight.Cancellation.IsCancellationRequested)
            {
                // FENCE the wakeup UNDER the ownership lock: there is no gap between the supersession check and
                // the invoke, so a Reset/Dispose/new scope cannot let obsolete work re-render.
                lock (_sync)
                {
                    if (!_disposed && ReferenceEquals(_active, flight.Request.Lease) &&
                        ReferenceEquals(_flight, flight))
                    {
                        flight.Request.OnResolved();   // re-render with the deep IDs preserved
                    }
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _concurrentProducers);
            DrainOwned(flight);
            FlightCompletedForTests?.Invoke();   // deterministic test signal: this physical producer fully drained
        }
    }

    /// <summary>
    /// Releases ownership ONLY if this flight still owns it, then AUTONOMOUSLY starts the latest requested
    /// capture when it differs (drain resume) - no extra UI query needed.
    /// </summary>
    private void DrainOwned(Flight flight)
    {
        Flight? nextFlight = null;
        lock (_sync)
        {
            if (ReferenceEquals(_flight, flight))
                _flight = null;
            if (!_disposed && _flight is null && _latest is { } latest &&
                !SameLease(latest.Lease, flight.Request.Lease))
            {
                nextFlight = new Flight(latest);
                _flight = nextFlight;
            }
        }
        flight.Cancellation.Dispose();   // the finished flight releases its CTS
        if (nextFlight is not null)
            _ = Task.Run(() => RunFlight(nextFlight), CancellationToken.None);   // exact instance
    }

    private static bool SameLease(AnchorResolutionLease a, AnchorResolutionLease b) =>
        string.Equals(a.ThreadId, b.ThreadId, StringComparison.Ordinal) &&
        string.Equals(a.AcceptedSessionUuid, b.AcceptedSessionUuid, StringComparison.Ordinal) &&   // UUID identity
        a.WindowSize == b.WindowSize &&   // full logical capture: a custom window must not reuse a lease
        string.Equals(a.FirstVisibleId, b.FirstVisibleId, StringComparison.Ordinal) &&
        ReferenceEquals(a.Root, b.Root) &&
        a.NavigationRevision == b.NavigationRevision;

    private static int FindIndex(IReadOnlyList<ChatTimelineItem> entries, string id, CancellationToken token)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if ((i & 4095) == 0 && token.IsCancellationRequested)
                return -1;   // per-batch cancellation; a cancelled partial is never treated as absence
            if (string.Equals(entries[i].Id, id, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    /// <summary>Cancels in-flight work but keeps this coordinator USABLE (provider change, not unmount).</summary>
    public void Reset()
    {
        Flight? owned;
        lock (_sync)
        {
            _active = null;
            _latest = null;
            _lastOnResolved = null;   // a stale callback must not be reused by the next provider
            owned = _flight;          // KEEP ownership until the flight physically drains
        }
        CancelSafely(owned?.Cancellation);   // ownership-safe: never throws on a concurrently disposed CTS
    }

    private static void CancelSafely(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
            return;
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The owning flight already drained and disposed it: nothing left to cancel.
        }
    }

    public void Dispose()
    {
        Flight? owned;
        lock (_sync)
        {
            _disposed = true;
            _active = null;
            _latest = null;
            _lastOnResolved = null;
            owned = _flight;
            _flight = null;
        }
        CancelSafely(owned?.Cancellation);   // cancel promptly on dispose
    }
}

/// <summary>
/// Retained logical-history facts derived from the FULL accepted timeline (never the visible window). Fields
/// are NULL while PENDING so the render never invents a false / new-session flag from a partial scan.
/// </summary>
public readonly record struct ChatTimelineRetainedFacts(
    bool? HasAnyUser,
    bool? CurrentTurnHasAssistant,
    bool Pending);

/// <summary>
/// Producer of retained facts with a BOUNDED synchronous fast path (never O(full retained) on the UI render),
/// a weak-root-keyed cache, and an off-UI completion under a cancellation guard. Same discipline as the
/// projection/activity caches: the weak-root table never keeps an abandoned root alive.
/// </summary>
public sealed class ChatTimelineRetainedFactsProducer
{
    private sealed class RootToken
    {
    }

    /// <summary>Hard bound on synchronous retained-scan work per render.</summary>
    public const int BoundedResumeOperations = 256;

    private readonly int _capacity;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, RootToken> _tokens = new();
    private readonly Dictionary<(RootToken Root, long Generation), ChatTimelineRetainedFacts> _facts = new();
    private readonly Queue<(RootToken Root, long Generation)> _order = new();
    private readonly object _sync = new();

    public ChatTimelineRetainedFactsProducer(int capacity = 32) => _capacity = Math.Max(1, capacity);

    /// <summary>Bounded fast path for the UI render; Pending=true when the bound was hit.</summary>
    public ChatTimelineRetainedFacts GetBounded(
        IReadOnlyList<ChatTimelineItem> entries, long generation, CancellationToken cancellationToken = default) =>
        Resolve(entries, generation, BoundedResumeOperations, cancellationToken: cancellationToken);

    /// <summary>Unbounded (deterministic) facts; tests and the async completion use this.</summary>
    public ChatTimelineRetainedFacts Get(IReadOnlyList<ChatTimelineItem> entries, long generation) =>
        Resolve(entries, generation, int.MaxValue);

    /// <summary>
    /// Schedules the off-UI completion. Returns FALSE when the facts are ALREADY faithful (nothing to do), so
    /// the caller reads the bounded path again instead of waiting for a wakeup that would never come.
    /// </summary>
    public bool TryScheduleCompletion(
        IReadOnlyList<ChatTimelineItem> entries, long generation, CancellationToken cancellationToken,
        Action onResolved)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(onResolved);
        var token = _tokens.GetValue(entries, static _ => new RootToken());
        lock (_sync)
        {
            if (_facts.TryGetValue((token, generation), out var cached) && !cached.Pending)
                return false;   // already faithful: the caller reads the bounded path instead of waiting
        }
        _ = Task.Run(() =>
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            var resolved = Resolve(entries, generation, int.MaxValue, allowCache: false, cancellationToken);
            if (resolved.Pending || cancellationToken.IsCancellationRequested)
                return;
            onResolved();
        }, CancellationToken.None);
        return true;
    }

    private ChatTimelineRetainedFacts Resolve(
        IReadOnlyList<ChatTimelineItem> entries, long generation, int maxOperations, bool allowCache = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var token = _tokens.GetValue(entries, static _ => new RootToken());
        if (allowCache)
        {
            lock (_sync)
            {
                if (_facts.TryGetValue((token, generation), out var cached) && !cached.Pending)
                    return cached;
            }
        }
        var computed = Compute(entries, maxOperations, cancellationToken);
        if (!computed.Pending)
            Publish(token, generation, computed);
        return computed;
    }

    private void Publish(RootToken token, long generation, ChatTimelineRetainedFacts computed)
    {
        lock (_sync)
        {
            var key = (token, generation);
            if (_facts.TryGetValue(key, out var existing) && !existing.Pending)
                return;   // never regress a faithful entry
            _facts[key] = computed;
            _order.Enqueue(key);
            while (_order.Count > _capacity)
                _facts.Remove(_order.Dequeue());
        }
    }

    private static ChatTimelineRetainedFacts Compute(
        IReadOnlyList<ChatTimelineItem> entries, int maxOperations, CancellationToken cancellationToken)
    {
        var operations = 0;
        var pending = false;
        bool Budget()
        {
            if (++operations > maxOperations)
            {
                pending = true;
                return false;
            }
            if ((operations & 63) == 0 && cancellationToken.IsCancellationRequested)
            {
                pending = true;
                return false;
            }
            return true;
        }

        bool? hasAnyUser = false;
        for (var i = 0; i < entries.Count; i++)
        {
            if (!Budget())
            {
                hasAnyUser = null;   // unknown while pending: never invent "no user"
                break;
            }
            if (entries[i].Kind == ChatTimelineItemKind.User)
            {
                hasAnyUser = true;
                break;
            }
        }

        bool? currentTurnHasAssistant = false;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (!Budget())
            {
                currentTurnHasAssistant = null;
                break;
            }
            var kind = entries[i].Kind;
            if (kind == ChatTimelineItemKind.User)
                break;
            if (kind == ChatTimelineItemKind.Assistant)
            {
                currentTurnHasAssistant = true;
                break;
            }
        }

        return new ChatTimelineRetainedFacts(hasAnyUser, currentTurnHasAssistant, pending);
    }
}

/// <summary>
/// Lifecycle owner of retained-facts work for ONE chat root. Begin(...) on EVERY render cancels/supersedes a
/// DIFFERENT scope and REUSES the lease for an identical scope; exactly ONE completion flight is owned per
/// lease, cancellation is per-batch, and the callback commits under the same fence. Dispose() cancels.
/// </summary>
public sealed class ChatTimelineRetainedFactsCoordinator : IDisposable
{
    public sealed class Lease
    {
        internal Lease(string? sessionId, IReadOnlyList<ChatTimelineItem> root, long generation, long version)
        {
            SessionId = sessionId;
            Root = root;
            Generation = generation;
            Version = version;
        }

        public string? SessionId { get; }
        public IReadOnlyList<ChatTimelineItem> Root { get; }
        public long Generation { get; }
        internal long Version { get; }
        internal CancellationTokenSource Cancellation { get; } = new();
    }

    private readonly ChatTimelineRetainedFactsProducer _producer;
    private readonly object _sync = new();
    private Lease? _active;
    private Lease? _flight;
    private long _renderVersion;
    private bool _disposed;

    public ChatTimelineRetainedFactsCoordinator(ChatTimelineRetainedFactsProducer producer) =>
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));

    public Lease Begin(string? sessionId, IReadOnlyList<ChatTimelineItem> root, long generation)
    {
        lock (_sync)
        {
            if (_active is { } current &&
                string.Equals(current.SessionId, sessionId, StringComparison.Ordinal) &&
                ReferenceEquals(current.Root, root) &&
                current.Generation == generation)
            {
                return current;   // identical scope: do not cancel/restart in-flight work
            }
            _active?.Cancellation.Cancel();
            var lease = new Lease(sessionId, root, generation, ++_renderVersion);
            if (_disposed)
            {
                lease.Cancellation.Cancel();
                return lease;
            }
            _active = lease;
            return lease;
        }
    }

    /// <summary>Bounded facts for the lease, or NULL while the async completion is pending.</summary>
    public ChatTimelineRetainedFacts? TryBounded(Lease lease)
    {
        var facts = _producer.GetBounded(lease.Root, lease.Generation, lease.Cancellation.Token);
        return facts.Pending ? null : facts;
    }

    /// <summary>
    /// Owns exactly ONE flight per lease; returns FALSE when the facts are already faithful (caller rereads).
    /// </summary>
    public bool RequestCompletion(Lease lease, Action onResolved)
    {
        ArgumentNullException.ThrowIfNull(onResolved);
        lock (_sync)
        {
            if (_disposed || !ReferenceEquals(_active, lease))
                return false;
            if (ReferenceEquals(_flight, lease))
                return true;
            _flight = lease;
        }
        var scheduled = _producer.TryScheduleCompletion(
            lease.Root, lease.Generation, lease.Cancellation.Token,
            () =>
            {
                lock (_sync)
                {
                    if (!ReferenceEquals(_flight, lease))
                        return;
                    _flight = null;
                    if (_disposed || !ReferenceEquals(_active, lease))
                        return;
                    onResolved();   // commit under the same fence
                }
            });
        if (!scheduled)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_flight, lease))
                    _flight = null;
            }
        }
        return scheduled;
    }

    /// <summary>Cancels in-flight work but keeps this coordinator USABLE (provider change, not unmount).</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _active?.Cancellation.Cancel();
            _active = null;
            _flight = null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _active?.Cancellation.Cancel();
            _active = null;
            _flight = null;
        }
    }
}
