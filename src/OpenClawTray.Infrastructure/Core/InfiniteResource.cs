using System.Diagnostics;

namespace OpenClawTray.Infrastructure.Core;

/// <summary>
/// One page of paginated results, as returned by a <c>UseInfiniteResource</c> fetcher.
/// </summary>
/// <param name="Items">The page payload.</param>
/// <param name="NextCursor">Cursor for the next page; null signals the end of the list.</param>
/// <param name="TotalCount">Server-reported total (when known); enables stable virtualization.</param>
public sealed record Page<TItem, TCursor>(
    IReadOnlyList<TItem> Items,
    TCursor? NextCursor,
    int? TotalCount = null);

/// <summary>
/// The four discrete load states of an <see cref="InfiniteResource{TItem}"/>.
/// Mirrors Android Paging 3's <c>LoadState</c>.
/// </summary>
public abstract record LoadState
{
    private protected LoadState() { }

    /// <summary>A page fetch is in flight.</summary>
    public sealed record Loading : LoadState { public static readonly Loading Instance = new(); }

    /// <summary>No fetch pending; <c>HasMore</c> determines whether more pages are available.</summary>
    public sealed record Idle : LoadState { public static readonly Idle Instance = new(); }

    /// <summary>Server reported <c>NextCursor == null</c> — no further pages.</summary>
    public sealed record EndOfList : LoadState { public static readonly EndOfList Instance = new(); }

    /// <summary>Most recent fetch failed; call <c>Retry()</c> to retry the failed page.</summary>
    public sealed record Error(Exception Exception) : LoadState;
}

/// <summary>
/// Tuning for <c>UseInfiniteResource</c>. Defaults: 50 items per page, no LRU cap
/// (keeps every loaded page), 5-minute <c>CacheTime</c>, 0 <c>StaleTime</c>.
/// </summary>
public sealed record InfiniteResourceOptions(
    int PageSize = 50,
    int? MaxLoadedPages = null,
    TimeSpan? StaleTime = null,
    TimeSpan? CacheTime = null,
    string? CacheKeyPrefix = null)
{
    public static readonly InfiniteResourceOptions Default = new();
    internal TimeSpan EffectiveStaleTime => StaleTime ?? TimeSpan.Zero;
    internal TimeSpan EffectiveCacheTime => CacheTime ?? TimeSpan.FromMinutes(5);
}

/// <summary>
/// Pull-model paginated result view. Each call-site of <c>UseInfiniteResource</c> owns a
/// single <see cref="InfiniteResource{TItem}"/>; virtualized controls call
/// <see cref="ItemAt"/> / <see cref="EnsureRange"/> per row to drive fetches, and render
/// <c>null</c> slots as placeholders.
/// </summary>
/// <remarks>
/// All mutating methods are UI-thread-affined. Internally the implementation shares a
/// lock for structural mutations (page table, cursor, load state). Callers should not
/// enumerate <see cref="Items"/> from a background thread.
/// </remarks>
public sealed class InfiniteResource<TItem>
{
    private readonly object _lock = new();

    // Page index → page payload or "in flight" marker.
    private readonly Dictionary<int, PageSlot> _pages = new();

    // LRU eviction: least-recently-touched at the head.
    private readonly LinkedList<int> _lruOrder = new();
    private readonly Dictionary<int, LinkedListNode<int>> _lruNodes = new();

    private object? _nextCursor;                    // TCursor? (boxed to avoid generic on the resource class)
    private bool _hasFetchedFirst;
    private int _loadedItemCount;
    private LoadState _loadState = LoadState.Loading.Instance;
    private int? _totalCount;
    private int _highestInflightEnd; // max end-index (exclusive) across in-flight pages
    private int _highestLoadedEnd;   // same for completed pages

    private readonly InfiniteResourceOptions _options;
    private int _erroredPage = -1;

    internal InfiniteResource(InfiniteResourceOptions options)
    {
        _options = options;
    }

    // ────────── Observable state ──────────

    /// <summary>Total items as reported by the server, or null when unknown.</summary>
    public int? TotalCount { get { lock (_lock) return _totalCount; } }

    /// <summary>Current load state — updated on every fetch lifecycle event.</summary>
    public LoadState LoadState { get { lock (_lock) return _loadState; } }

    /// <summary>True iff the server has not yet reported <c>NextCursor == null</c>.</summary>
    public bool HasMore
    {
        get { lock (_lock) return _loadState is not LoadState.EndOfList; }
    }

    /// <summary>Approximate number of items not yet loaded. 0 once <see cref="HasMore"/> is false.</summary>
    public int EstimatedRemaining
    {
        get
        {
            lock (_lock)
            {
                if (_totalCount is { } total) return Math.Max(0, total - _loadedItemCount);
                return HasMore ? _options.PageSize : 0;
            }
        }
    }

    /// <summary>
    /// Flat, virtualized view. Indices beyond loaded content are either null-placeholders
    /// (in-flight or about-to-fetch) or, past the known end, not returned.
    /// </summary>
    public IReadOnlyList<TItem?> Items { get; private set; } = new ItemsView<TItem>(Array.Empty<TItem?>());

    /// <summary>
    /// Returns the item at <paramref name="index"/> or null when the slot is in-flight /
    /// unloaded. Triggers a coalesced fetch when the slot's page is not yet requested,
    /// unless the index is past the known end.
    /// </summary>
    public TItem? ItemAt(int index)
    {
        TItem? result = default;
        bool scheduleFetch = false;
        int pageToFetch = -1;

        lock (_lock)
        {
            if (index < 0) return default;

            int pageIndex = index / _options.PageSize;
            if (_pages.TryGetValue(pageIndex, out var slot))
            {
                TouchLru(pageIndex);
                if (slot.Items is { } loaded)
                {
                    int offset = index - pageIndex * _options.PageSize;
                    if (offset >= 0 && offset < loaded.Count) result = loaded[offset];
                }
                // else: in-flight → placeholder
                return result;
            }

            // Unknown page — schedule fetch unless past known end.
            if (_totalCount is { } total && index >= total) return default;

            // Claim the slot before releasing the lock so concurrent ItemAt callers dedup —
            // they'll observe the in-flight placeholder on their next TryGetValue. The hook's
            // MarkPageInFlight is still correct: it's idempotent against an existing in-flight
            // slot and remains the entry point for FetchNext / Retry.
            MarkPageInFlightLocked(pageIndex);
            scheduleFetch = true;
            pageToFetch = pageIndex;
        }

        if (scheduleFetch) _pageRequestedCallback?.Invoke(pageToFetch);
        return default;
    }

    /// <summary>
    /// Eagerly ensures every page covering <c>[firstIndex, lastIndex]</c> is loaded
    /// (or in-flight). Dedups against existing in-flight fetches.
    /// </summary>
    public void EnsureRange(int firstIndex, int lastIndex)
    {
        if (firstIndex < 0) firstIndex = 0;
        if (lastIndex < firstIndex) return;

        var toFetch = new List<int>();
        lock (_lock)
        {
            if (_totalCount is { } total) lastIndex = Math.Min(lastIndex, total - 1);
            int firstPage = firstIndex / _options.PageSize;
            int lastPage = lastIndex / _options.PageSize;
            for (int p = firstPage; p <= lastPage; p++)
            {
                if (!_pages.ContainsKey(p))
                {
                    // Claim the slot inside the lock so concurrent callers dedup.
                    MarkPageInFlightLocked(p);
                    toFetch.Add(p);
                }
                else TouchLru(p);
            }
        }

        foreach (var p in toFetch) _pageRequestedCallback?.Invoke(p);
    }

    /// <summary>
    /// Fetches the next page using the last-known cursor. No-op if load-state is not
    /// <see cref="LoadState.Idle"/> or if <see cref="HasMore"/> is false.
    /// </summary>
    public void FetchNext()
    {
        bool shouldFetch;
        int pageIndex;
        lock (_lock)
        {
            shouldFetch = _loadState is LoadState.Idle && HasMore;
            pageIndex = NextPageIndexLocked();
        }
        if (shouldFetch) _pageRequestedCallback?.Invoke(pageIndex);
    }

    /// <summary>
    /// Retry the most recent failed page fetch. No-op if the current load-state is not
    /// <see cref="LoadState.Error"/>.
    /// </summary>
    public void Retry()
    {
        int page;
        lock (_lock)
        {
            if (_loadState is not LoadState.Error) return;
            page = _erroredPage;
        }
        if (page >= 0) _pageRequestedCallback?.Invoke(page);
    }

    /// <summary>
    /// Invalidate all cached pages and refetch from page 1. Preserves the current
    /// <see cref="TotalCount"/> — consumers that want to snapshot scroll state should do
    /// so inside their <see cref="LoadState.Loading"/> render.
    /// </summary>
    public void Refresh() => _refreshCallback?.Invoke();

    // ────────── Internal plumbing (driven by the hook) ──────────

    private Action<int>? _pageRequestedCallback;
    private Action? _refreshCallback;

    internal void BindCallbacks(Action<int> pageRequested, Action refresh)
    {
        _pageRequestedCallback = pageRequested;
        _refreshCallback = refresh;
    }

    internal void MarkPageInFlight(int pageIndex)
    {
        lock (_lock) MarkPageInFlightLocked(pageIndex);
    }

    private void MarkPageInFlightLocked(int pageIndex)
    {
        if (_pages.TryGetValue(pageIndex, out var slot) && slot.Items is not null)
        {
            // Already loaded — ignore (shouldn't happen unless deps changed).
            return;
        }
        _pages[pageIndex] = new PageSlot(null);
        TouchLru(pageIndex);
        int endExclusive = (pageIndex + 1) * _options.PageSize;
        if (endExclusive > _highestInflightEnd) _highestInflightEnd = endExclusive;
        _loadState = LoadState.Loading.Instance;
        RebuildItemsLocked();
    }

    internal bool ApplyPageResult<TCursor>(int pageIndex, Page<TItem, TCursor> page)
    {
        lock (_lock)
        {
            _pages[pageIndex] = new PageSlot(page.Items);
            TouchLru(pageIndex);
            _loadedItemCount += page.Items.Count;
            int endExclusive = pageIndex * _options.PageSize + page.Items.Count;
            if (endExclusive > _highestLoadedEnd) _highestLoadedEnd = endExclusive;

            if (page.TotalCount is { } tc) _totalCount = tc;

            // The cursor we cache is always the most recent one — that's what FetchNext uses.
            _nextCursor = page.NextCursor;
            bool atEnd = page.NextCursor is null;

            _loadState = atEnd ? LoadState.EndOfList.Instance : LoadState.Idle.Instance;
            if (!_hasFetchedFirst && pageIndex == 0) _hasFetchedFirst = true;
            _erroredPage = -1;

            EvictIfOverCapLocked();
            RebuildItemsLocked();
        }
        return true;
    }

    internal void ApplyPageError(int pageIndex, Exception ex)
    {
        lock (_lock)
        {
            _pages.Remove(pageIndex);
            _loadState = new LoadState.Error(ex);
            _erroredPage = pageIndex;
            RebuildItemsLocked();
        }
    }

    internal int NextPageIndex()
    {
        lock (_lock) return NextPageIndexLocked();
    }

    internal TCursor? GetCursor<TCursor>()
    {
        lock (_lock) return _nextCursor is TCursor c ? c : default;
    }

    internal void ClearAllPages()
    {
        lock (_lock)
        {
            _pages.Clear();
            _lruOrder.Clear();
            _lruNodes.Clear();
            _loadedItemCount = 0;
            _highestInflightEnd = 0;
            _highestLoadedEnd = 0;
            _hasFetchedFirst = false;
            _loadState = LoadState.Loading.Instance;
            _nextCursor = null;
            _erroredPage = -1;
            RebuildItemsLocked();
        }
    }

    /// <summary>
    /// Remove an in-flight slot that was claimed by <see cref="MarkPageInFlight"/> but
    /// whose fetch never actually started (e.g. because the hook could not resolve the
    /// cursor for a cursor-paginated source yet). Without this, the claim persists and
    /// <see cref="EnsureRange"/> skips the page forever, producing a hang on deep scroll.
    /// Loaded pages are left alone.
    /// </summary>
    internal void ClearInflightSlot(int pageIndex)
    {
        lock (_lock)
        {
            if (!_pages.TryGetValue(pageIndex, out var slot)) return;
            if (slot.Items is not null) return;
            _pages.Remove(pageIndex);
            if (_lruNodes.TryGetValue(pageIndex, out var node))
            {
                _lruOrder.Remove(node);
                _lruNodes.Remove(pageIndex);
            }
            // Recompute _highestInflightEnd conservatively.
            int maxEnd = _highestLoadedEnd;
            foreach (var (idx, s) in _pages)
            {
                if (s.Items is null)
                {
                    int end = (idx + 1) * _options.PageSize;
                    if (end > maxEnd) maxEnd = end;
                }
            }
            _highestInflightEnd = maxEnd;
            RebuildItemsLocked();
        }
    }

    internal bool HasInFlightFetch
    {
        get
        {
            lock (_lock)
            {
                foreach (var slot in _pages.Values)
                    if (slot.Items is null) return true;
                return false;
            }
        }
    }

    // ────────── Private helpers ──────────

    private int NextPageIndexLocked()
    {
        int maxLoaded = -1;
        foreach (var idx in _pages.Keys) if (idx > maxLoaded) maxLoaded = idx;
        return maxLoaded + 1;
    }

    private void TouchLru(int pageIndex)
    {
        if (_lruNodes.TryGetValue(pageIndex, out var node))
        {
            _lruOrder.Remove(node);
            _lruOrder.AddLast(node);
        }
        else
        {
            var newNode = _lruOrder.AddLast(pageIndex);
            _lruNodes[pageIndex] = newNode;
        }
    }

    private void EvictIfOverCapLocked()
    {
        if (_options.MaxLoadedPages is not { } cap) return;
        while (_pages.Count > cap && _lruOrder.First is { } oldest)
        {
            int pageIdx = oldest.Value;
            _lruOrder.RemoveFirst();
            _lruNodes.Remove(pageIdx);
            if (_pages.TryGetValue(pageIdx, out var slot) && slot.Items is { } items)
            {
                _loadedItemCount -= items.Count;
                _pages.Remove(pageIdx);
            }
        }
    }

    private void RebuildItemsLocked()
    {
        int desiredLength;
        if (_totalCount is { } total) desiredLength = total;
        else if (_loadState is LoadState.EndOfList) desiredLength = _highestLoadedEnd;
        else desiredLength = Math.Max(_highestLoadedEnd, _highestInflightEnd);

        var backing = new TItem?[desiredLength];
        foreach (var (pageIdx, slot) in _pages)
        {
            if (slot.Items is null) continue;
            int start = pageIdx * _options.PageSize;
            for (int i = 0; i < slot.Items.Count && start + i < desiredLength; i++)
                backing[start + i] = slot.Items[i];
        }
        Items = new ItemsView<TItem>(backing);
    }

    private readonly record struct PageSlot(IReadOnlyList<TItem>? Items);
}

/// <summary>Thin <see cref="IReadOnlyList{T}"/> wrapper so <c>Items</c> swaps atomically.</summary>
internal sealed class ItemsView<T> : IReadOnlyList<T?>
{
    private readonly T?[] _items;
    public ItemsView(T?[] items) { _items = items; }
    public T? this[int index] => _items[index];
    public int Count => _items.Length;
    public IEnumerator<T?> GetEnumerator() => ((IEnumerable<T?>)_items).GetEnumerator();
    global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();
}
