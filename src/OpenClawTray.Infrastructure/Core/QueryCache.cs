using System.Collections.Concurrent;

namespace OpenClawTray.Infrastructure.Core;

/// <summary>
/// A point-in-time cache entry for a resource keyed by cache key.
/// Immutable — the cache mutates it by replacing with a copy via <c>with</c>.
/// </summary>
/// <remarks>
/// <see cref="SubscriberCount"/> mirrors the cache's internal ref-count. It is exposed
/// on the entry so callers can observe it for diagnostics, but the authoritative
/// mutation path is <see cref="QueryCache.Subscribe"/> / <see cref="QueryCache.Unsubscribe"/>.
/// </remarks>
/// <summary>
/// Non-generic view over a typed <see cref="CacheEntry{T}"/>. Lets the cache track and
/// mutate <see cref="SubscriberCount"/> without knowing the payload type.
/// </summary>
internal interface ICacheEntry
{
    int SubscriberCount { get; }
    DateTime FetchedAt { get; }
    TimeSpan StaleTime { get; }
    ICacheEntry WithSubscriberCount(int count);
}

public sealed record CacheEntry<T>(
    T Value,
    DateTime FetchedAt,
    TimeSpan StaleTime,
    int SubscriberCount) : ICacheEntry
{
    ICacheEntry ICacheEntry.WithSubscriberCount(int count) => this with { SubscriberCount = count };
}

/// <summary>
/// Process-wide query cache with TTL, ref-counted subscriptions, and pattern invalidation.
/// Shared across components via <see cref="Context{T}"/> — see <see cref="AppContexts.QueryCache"/>.
/// </summary>
/// <remarks>
/// <para><b>Threading.</b> All public methods are safe to call from any thread. Slots are
/// protected by a per-key lock; <see cref="EntryChanged"/> fires either inline or on
/// the configured <see cref="DispatcherPost"/> callback (set by <c>ReactorHost</c> at
/// bootstrap). Tests can leave it null to observe events inline.</para>
/// <para><b>Eviction.</b> A single shared timer (started lazily on first subscription)
/// polls all slots every <see cref="EvictionPollInterval"/>. Slots whose
/// <c>SubscriberCount == 0</c> for longer than their <c>CacheTime</c> are evicted.
/// Using one timer keeps the entry count's impact on system timers O(1).</para>
/// </remarks>
public sealed class QueryCache : IDisposable
{
    /// <summary>How often the eviction timer checks every slot for expiry. Default 1s.</summary>
    public static TimeSpan EvictionPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Injected marshaller for <see cref="EntryChanged"/>. Null → fires inline.</summary>
    public Action<Action>? DispatcherPost { get; set; }

    /// <summary>Clock override for deterministic tests.</summary>
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    private readonly ConcurrentDictionary<string, Slot> _slots = new();
    private Timer? _evictionTimer;
    private readonly object _timerLock = new();
    private int _disposed;

    /// <summary>Fires when an entry is added, replaced, invalidated, or evicted.</summary>
    public event Action<string>? EntryChanged;

    /// <summary>
    /// Retrieves the current entry for <paramref name="key"/> if present and typed as
    /// <typeparamref name="T"/>. Returns false on miss or on type-mismatch.
    /// Staleness is a caller-side decision — the entry's <see cref="CacheEntry{T}.FetchedAt"/>
    /// and <see cref="CacheEntry{T}.StaleTime"/> are both returned on a hit.
    /// </summary>
    public bool TryGet<T>(string key, out CacheEntry<T> entry)
    {
        if (_slots.TryGetValue(key, out var slot))
        {
            lock (slot.Lock)
            {
                if (slot.Entry is CacheEntry<T> typed)
                {
                    entry = typed;
                    return true;
                }
            }
        }
        entry = default!;
        return false;
    }

    /// <summary>
    /// Stores or replaces the entry for <paramref name="key"/>. Preserves any existing
    /// subscriber count on the slot. Fires <see cref="EntryChanged"/>.
    /// </summary>
    public void Set<T>(string key, T value, TimeSpan staleTime, TimeSpan cacheTime)
    {
        while (true)
        {
            var slot = _slots.GetOrAdd(key, _ => new Slot());
            lock (slot.Lock)
            {
                if (slot.IsEvicted) continue;
                slot.CacheTime = cacheTime;
                slot.Entry = new CacheEntry<T>(value, UtcNow(), staleTime, slot.SubscriberCount);
                // A Set clears a pending eviction only if there are subscribers; otherwise
                // the newly-set entry is still evictable after CacheTime with no one listening.
                slot.ZeroSubscribersAt = slot.SubscriberCount == 0 ? UtcNow() : null;
            }
            FireEntryChanged(key);
            return;
        }
    }

    /// <summary>
    /// Removes the entry (if any) for <paramref name="key"/>. Subscribers are retained —
    /// the next <see cref="Set"/> will see their count. Fires <see cref="EntryChanged"/>
    /// if there was an entry to remove.
    /// </summary>
    public void Invalidate(string key)
    {
        if (_slots.TryGetValue(key, out var slot))
        {
            bool changed;
            lock (slot.Lock)
            {
                changed = slot.Entry is not null;
                slot.Entry = null;
            }
            if (changed) FireEntryChanged(key);
        }
    }

    /// <summary>
    /// Invalidates every key that starts with <paramref name="keyPrefix"/>. O(n) in
    /// the number of cached keys — document the pattern prefix carefully if you store
    /// a lot of unrelated keys under one cache.
    /// </summary>
    public void InvalidatePattern(string keyPrefix)
    {
        // Snapshot the key set — the live dictionary may mutate mid-iteration.
        foreach (var key in _slots.Keys.ToArray())
        {
            if (key.StartsWith(keyPrefix, StringComparison.Ordinal))
                Invalidate(key);
        }
    }

    /// <summary>
    /// Removes every entry. Subscribers are retained. Fires <see cref="EntryChanged"/>
    /// once per key that actually held a value.
    /// </summary>
    public void Clear()
    {
        foreach (var kvp in _slots.ToArray())
        {
            bool changed;
            lock (kvp.Value.Lock)
            {
                changed = kvp.Value.Entry is not null;
                kvp.Value.Entry = null;
            }
            if (changed) FireEntryChanged(kvp.Key);
        }
    }

    /// <summary>
    /// Increments the subscriber count on <paramref name="key"/>. Cancels any pending
    /// eviction for the slot. Returns the new subscriber count.
    /// </summary>
    public int Subscribe(string key)
    {
        while (true)
        {
            var slot = _slots.GetOrAdd(key, _ => new Slot());
            lock (slot.Lock)
            {
                if (slot.IsEvicted) continue; // racing eviction — retry with a fresh slot.
                slot.SubscriberCount++;
                slot.ZeroSubscribersAt = null;
                if (slot.Entry is ICacheEntry e)
                    slot.Entry = e.WithSubscriberCount(slot.SubscriberCount);
                EnsureEvictionTimer();
                return slot.SubscriberCount;
            }
        }
    }

    /// <summary>
    /// Decrements the subscriber count. When it reaches zero, starts the eviction clock
    /// based on the slot's most recent <see cref="Set"/> <c>cacheTime</c>. Throws
    /// <see cref="InvalidOperationException"/> if called on a slot that has no subscribers
    /// — this catches hook-logic bugs that would otherwise hide underflow forever.
    /// </summary>
    public int Unsubscribe(string key)
    {
        if (!_slots.TryGetValue(key, out var slot))
            throw new InvalidOperationException(
                $"QueryCache.Unsubscribe called for key '{key}' which has no slot. " +
                "This indicates a hook lifecycle bug (unsubscribed twice, or never subscribed).");

        lock (slot.Lock)
        {
            if (slot.SubscriberCount <= 0)
                throw new InvalidOperationException(
                    $"QueryCache.Unsubscribe called for key '{key}' with SubscriberCount already 0. " +
                    "This indicates a hook lifecycle bug (double-unsubscribe).");
            slot.SubscriberCount--;
            if (slot.Entry is ICacheEntry e)
                slot.Entry = e.WithSubscriberCount(slot.SubscriberCount);
            if (slot.SubscriberCount == 0)
                slot.ZeroSubscribersAt = UtcNow();
            return slot.SubscriberCount;
        }
    }

    /// <summary>
    /// Test / diagnostic hook: drive the eviction sweep explicitly rather than waiting
    /// for the background <see cref="EvictionPollInterval"/> timer. Returns the set of
    /// keys that were evicted on this pass. Safe to call from any thread.
    /// </summary>
    /// <remarks>
    /// Production code should not need this — subscribers keep entries alive and the
    /// shared timer evicts zero-subscriber entries past their <c>CacheTime</c>. It's
    /// exposed publicly so framerate / stress tests can make eviction deterministic.
    /// </remarks>
    public IReadOnlyList<string> EvictNow()
    {
        var evicted = new List<string>();
        var now = UtcNow();
        foreach (var kvp in _slots.ToArray())
        {
            var slot = kvp.Value;
            string key = kvp.Key;
            lock (slot.Lock)
            {
                // Hold the slot lock across the dictionary removal so any concurrent
                // Subscribe sees IsEvicted=true and retries with a fresh slot, rather
                // than losing its increment on an orphaned slot.
                if (slot.SubscriberCount == 0 && slot.ZeroSubscribersAt is { } t &&
                    now - t >= slot.CacheTime)
                {
                    slot.IsEvicted = true;
                    var removed = ((ICollection<KeyValuePair<string, Slot>>)_slots)
                        .Remove(new KeyValuePair<string, Slot>(key, slot));
                    if (removed) evicted.Add(key);
                }
            }
        }
        if (evicted.Count > 0)
        {
            foreach (var key in evicted) FireEntryChanged(key);
        }
        return evicted;
    }

    /// <summary>
    /// Snapshot the current cache size. Diagnostic only — do not use for correctness.
    /// </summary>
    public int Count => _slots.Count;

    /// <summary>
    /// Non-generic metadata peek used by <see cref="FocusRevalidationService"/> to
    /// decide whether an entry is stale without knowing its payload type. Returns
    /// false if the slot is missing, empty, or holds a non-<see cref="CacheEntry{T}"/>
    /// payload (which should be impossible through the public API).
    /// </summary>
    internal bool TryGetFetchedAt(string key, out DateTime fetchedAt, out TimeSpan staleTime)
    {
        if (_slots.TryGetValue(key, out var slot))
        {
            lock (slot.Lock)
            {
                if (slot.Entry is ICacheEntry e)
                {
                    fetchedAt = e.FetchedAt;
                    staleTime = e.StaleTime;
                    return true;
                }
            }
        }
        fetchedAt = default;
        staleTime = default;
        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_timerLock)
        {
            _evictionTimer?.Dispose();
            _evictionTimer = null;
        }
    }

    private void EnsureEvictionTimer()
    {
        if (_evictionTimer is not null) return;
        lock (_timerLock)
        {
            if (_evictionTimer is not null) return;
            if (_disposed != 0) return;
            _evictionTimer = new Timer(_ => EvictNow(), null, EvictionPollInterval, EvictionPollInterval);
        }
    }

    private void FireEntryChanged(string key)
    {
        var handler = EntryChanged;
        if (handler is null) return;
        var post = DispatcherPost;
        if (post is null)
        {
            handler(key);
        }
        else
        {
            // Captured handler reference — protect against concurrent unsubscribe.
            post(() => handler(key));
        }
    }

    // Untyped slot wrapper — the generic payload lives in Entry (boxed CacheEntry<T>).
    private sealed class Slot
    {
        public readonly object Lock = new();
        public object? Entry;
        public int SubscriberCount;
        public TimeSpan CacheTime = TimeSpan.FromMinutes(5);
        public DateTime? ZeroSubscribersAt;
        // Set under Lock when the slot has been removed from the dictionary, so
        // concurrent Subscribe/Set observers can discard it and retry with a fresh slot.
        public bool IsEvicted;
    }
}

/// <summary>
/// Static context registrations shared across the Reactor core. Install the cache via
/// <c>ReactorHost</c> bootstrap (or override per test).
/// </summary>
public static class AppContexts
{
    /// <summary>
    /// The ambient <see cref="Core.QueryCache"/>. The default value is a fresh cache
    /// instance installed at app root; tests can provide their own.
    /// </summary>
    public static readonly Context<QueryCache> QueryCache =
        new(new QueryCache(), nameof(QueryCache));

    /// <summary>
    /// Nearest ancestor <see cref="Hooks.PendingScope"/>, or <c>null</c> at the root.
    /// The <c>Pending</c> element provides a fresh scope to its subtree; hooks that
    /// participate in Pending-style bubble-up read this via <c>UseContext</c>.
    /// </summary>
    public static readonly Context<Hooks.PendingScope?> PendingScope =
        new(null, nameof(PendingScope));

    /// <summary>
    /// Ambient <see cref="Core.FocusRevalidationService"/> bound to the root
    /// <see cref="QueryCache"/>. Hooks that opt in via
    /// <c>ResourceOptions.RefetchOnWindowFocus = true</c> enroll themselves so the
    /// service can invalidate their cache entry on window activation / resume.
    /// </summary>
    public static readonly Context<FocusRevalidationService?> FocusRevalidation =
        new(new FocusRevalidationService(QueryCache.DefaultValue), nameof(FocusRevalidation));
}
