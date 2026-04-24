using System.Collections.Concurrent;
using OpenClawTray.Infrastructure.Core;

namespace OpenClawTray.Infrastructure.Hooks;

/// <summary>
/// Extension methods providing <c>UseInfiniteResource</c> on <see cref="RenderContext"/>.
/// </summary>
/// <remarks>
/// See <c>docs/specs/020-async-resources-design.md</c> §7 for the pull-model contract and
/// §11 for DataGrid integration notes.
/// </remarks>
public static class UseInfiniteResourceExtensions
{
    /// <summary>
    /// Returns the <see cref="InfiniteResource{TItem}"/> owned by this hook slot. The
    /// resource's state is driven by <paramref name="fetchPage"/>; <paramref name="deps"/>
    /// controls cache-keying and deps-change restart.
    /// </summary>
    public static InfiniteResource<TItem> UseInfiniteResource<TItem, TCursor>(
        this RenderContext ctx,
        Func<TCursor?, CancellationToken, Task<Page<TItem, TCursor>>> fetchPage,
        QueryCache cache,
        object[] deps,
        InfiniteResourceOptions? options = null,
        IHookDispatcher? dispatcher = null,
        Func<int, TCursor?>? cursorFromPageIndex = null)
        => UseInfiniteResourceCore(ctx, fetchPage, cache, deps, options, dispatcher, cursorFromPageIndex);

    /// <summary>
    /// Overload that reads the ambient <see cref="QueryCache"/> from
    /// <see cref="AppContexts.QueryCache"/>. <see cref="Hosting.ReactorHost"/> installs a
    /// process-wide default cache at startup; tests or subtrees may override it via
    /// <c>.Provide(AppContexts.QueryCache, customCache)</c>.
    /// </summary>
    public static InfiniteResource<TItem> UseInfiniteResource<TItem, TCursor>(
        this RenderContext ctx,
        Func<TCursor?, CancellationToken, Task<Page<TItem, TCursor>>> fetchPage,
        object[] deps,
        InfiniteResourceOptions? options = null,
        IHookDispatcher? dispatcher = null,
        Func<int, TCursor?>? cursorFromPageIndex = null)
        => UseInfiniteResourceCore(ctx, fetchPage, ctx.UseContext(AppContexts.QueryCache), deps, options, dispatcher, cursorFromPageIndex);

    private static InfiniteResource<TItem> UseInfiniteResourceCore<TItem, TCursor>(
        RenderContext ctx,
        Func<TCursor?, CancellationToken, Task<Page<TItem, TCursor>>> fetchPage,
        QueryCache cache,
        object[] deps,
        InfiniteResourceOptions? options,
        IHookDispatcher? dispatcher,
        Func<int, TCursor?>? cursorFromPageIndex)
    {
        options ??= InfiniteResourceOptions.Default;

        var hookIdRef = ctx.UseRef<string?>(null);
        hookIdRef.Current ??= Guid.NewGuid().ToString("N");

        var stateRef = ctx.UseRef<InfiniteHookState<TItem, TCursor>?>(null);
        var (_, rerenderTick) = ctx.UseReducer(0, threadSafe: true);
        // Nearest Pending scope (null when the hook is outside any <c>Pending</c>).
        var pendingScope = ctx.UseContext(AppContexts.PendingScope);

        var state = stateRef.Current ??= CreateState<TItem, TCursor>(
            ctx, cache, options,
            hookId: hookIdRef.Current!,
            rerender: () => rerenderTick(n => n + 1),
            dispatcher: dispatcher,
            pendingScope: pendingScope);
        state.SetFetcher(fetchPage);
        state.CursorFromPageIndex = cursorFromPageIndex;

        ctx.UseEffect(() => () => state.Dispose());

        // Deps-change restart.
        string newKeyPrefix = BuildKeyPrefix(hookIdRef.Current!, deps, options);
        if (!string.Equals(state.KeyPrefix, newKeyPrefix, StringComparison.Ordinal))
        {
            state.TransitionToDeps(newKeyPrefix, deps);
            state.KickOffFirstPage();
        }

        return state.Resource;
    }

    private static InfiniteHookState<TItem, TCursor> CreateState<TItem, TCursor>(
        RenderContext _,
        QueryCache cache,
        InfiniteResourceOptions options,
        string hookId,
        Action rerender,
        IHookDispatcher? dispatcher,
        PendingScope? pendingScope)
    {
        return new InfiniteHookState<TItem, TCursor>(cache, options, hookId, rerender,
            dispatcher ?? TryCaptureCurrentDispatcher(),
            pendingScope);
    }

    private static string BuildKeyPrefix(string hookId, object[] deps, InfiniteResourceOptions opts)
    {
        unchecked
        {
            int h = 17;
            foreach (var d in deps) h = h * 31 + (d?.GetHashCode() ?? 0);
            var prefix = opts.CacheKeyPrefix is null ? hookId : opts.CacheKeyPrefix;
            return $"{prefix}/{h}";
        }
    }

    private static IHookDispatcher? TryCaptureCurrentDispatcher()
    {
        try
        {
            var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            return dq is null ? null : new WindowsDispatcherHookDispatcher();
        }
        catch (global::System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}

/// <summary>
/// Per-hook state for <c>UseInfiniteResource</c>. Coordinates in-flight page fetches,
/// cache subscriptions, deps-change restart, and unmount teardown.
/// </summary>
internal sealed class InfiniteHookState<TItem, TCursor> : IDisposable
{
    private readonly QueryCache _cache;
    private readonly InfiniteResourceOptions _options;
    private readonly string _hookId;
    private readonly Action _rerender;
    private readonly IHookDispatcher? _dispatcher;

    private Func<TCursor?, CancellationToken, Task<Page<TItem, TCursor>>>? _fetcher;
    private readonly Dictionary<int, CancellationTokenSource> _pageCts = new();
    private readonly HashSet<string> _subscribedKeys = new();
    // Pages the hook wanted to fetch but couldn't yet because the cursor for that page
    // depends on a prior page that's still loading. When a prior page completes, the
    // smallest entry here is retried. See RequestPage for the rationale — cursor paging
    // is inherently serial, so deep-scroll requests must chain forward one page at a
    // time rather than all-at-once.
    private readonly SortedSet<int> _deferredRequests = new();

    /// <summary>
    /// Optional: computes the cursor for an arbitrary page index directly, bypassing the
    /// "wait for page N-1" constraint. Used by <c>UseDataSource</c> to expose the offset
    /// semantics of <see cref="Data.IDataSource{T}"/>'s <c>ContinuationToken</c>, so deep
    /// scrolls fetch pages in parallel instead of walking the chain one round-trip at a time.
    /// </summary>
    public Func<int, TCursor?>? CursorFromPageIndex { get; set; }
    public string KeyPrefix = "";
    public object[]? LastDeps;
    public InfiniteResource<TItem> Resource { get; private set; } = default!;
    private bool _disposed;

    private readonly PendingScope? _pendingScope;

    public InfiniteHookState(
        QueryCache cache, InfiniteResourceOptions options, string hookId,
        Action rerender, IHookDispatcher? dispatcher, PendingScope? pendingScope = null)
    {
        _cache = cache;
        _options = options;
        _hookId = hookId;
        _rerender = rerender;
        _dispatcher = dispatcher;
        _pendingScope = pendingScope;
        Resource = CreateResource();
        // Start as Loading from the scope's perspective — a fresh InfiniteResource
        // begins with LoadState.Loading and zero items.
        _pendingScope?.Register(this, isLoading: true);
    }

    /// <summary>Called from RequestPage / page-completion paths to keep the Pending scope in sync.</summary>
    private void NotifyPending()
    {
        if (_pendingScope is null) return;
        // Initial-load is the only state that contributes to Pending: once at least one
        // item is in the flat list the subtree can render meaningfully and <c>Reloading</c>-
        // style refetches should keep the existing UI visible (spec §10.1).
        bool loading = Resource.LoadState is LoadState.Loading && Resource.Items.Count == 0;
        _pendingScope.SetLoading(this, loading);
    }

    public void SetFetcher(Func<TCursor?, CancellationToken, Task<Page<TItem, TCursor>>> fetcher)
    {
        _fetcher = fetcher;
    }

    public void TransitionToDeps(string newPrefix, object[] newDeps)
    {
        // Cancel all in-flight.
        foreach (var cts in _pageCts.Values)
        {
            try { cts.Cancel(); cts.Dispose(); } catch { /* ignore */ }
        }
        _pageCts.Clear();
        _deferredRequests.Clear();

        // Unsubscribe from old keys.
        foreach (var key in _subscribedKeys)
        {
            try { _cache.Unsubscribe(key); } catch (InvalidOperationException) { /* defensive */ }
        }
        _subscribedKeys.Clear();

        KeyPrefix = newPrefix;
        LastDeps = newDeps.ToArray();
        Resource = CreateResource();
        // Deps change = new page set. Back to Loading from the scope's perspective.
        _pendingScope?.SetLoading(this, true);
    }

    public void KickOffFirstPage()
    {
        // Page 0 uses cursor=null.
        RequestPage(0);
    }

    private InfiniteResource<TItem> CreateResource()
    {
        var r = new InfiniteResource<TItem>(_options);
        r.BindCallbacks(RequestPage, Refresh);
        return r;
    }

    private string CacheKeyFor(int pageIndex) => $"{KeyPrefix}/page:{pageIndex}";

    private void RequestPage(int pageIndex)
    {
        if (_disposed) return;
        if (_fetcher is null) return;
        if (_pageCts.ContainsKey(pageIndex)) return; // already in-flight

        // Try cache first.
        string key = CacheKeyFor(pageIndex);
        if (_cache.TryGet<Page<TItem, TCursor>>(key, out var entry))
        {
            Resource.ApplyPageResult(pageIndex, entry.Value);
            SubscribeKey(key);
            NotifyPending(); _rerender();
            return;
        }

        // Need the cursor for this page. Two strategies:
        //
        // 1. If CursorFromPageIndex is set (e.g. offset-based data sources via UseDataSource),
        //    compute the cursor directly and skip the serial chain. Deep scrolls then fetch
        //    pages in parallel.
        // 2. Otherwise fall back to cursor paging: page N's cursor comes from page N-1's
        //    payload, so fetches must chain. Deferred requests park here until the chain
        //    advances (see CommitSuccess).
        TCursor? cursor;
        if (pageIndex == 0)
        {
            cursor = default;
        }
        else if (CursorFromPageIndex is { } compute)
        {
            cursor = compute(pageIndex);
            if (cursor is null)
            {
                Resource.ClearInflightSlot(pageIndex);
                return;
            }
        }
        else
        {
            if (!HasLoadedPage(pageIndex - 1))
            {
                RequestPage(pageIndex - 1);
                // If the recursive call completed synchronously (sync-complete fetcher, cache
                // hit), the previous page is now loaded — fall through and request this one.
                if (!HasLoadedPage(pageIndex - 1))
                {
                    // The prior page isn't loaded yet. Remember we want this one; we'll retry
                    // from CommitSuccess when the chain advances. Also clear the claim on
                    // Resource._pages[pageIndex] so EnsureRange can re-drive us later — without
                    // this, the orphan in-flight marker persists and deep-scroll hangs because
                    // EnsureRange sees the page as "already in flight" and skips it.
                    _deferredRequests.Add(pageIndex);
                    Resource.ClearInflightSlot(pageIndex);
                    return;
                }
            }
            cursor = Resource.GetCursor<TCursor>();
            if (cursor is null)
            {
                // No cursor means we're already at the end — don't fetch.
                Resource.ClearInflightSlot(pageIndex);
                return;
            }
        }
        _deferredRequests.Remove(pageIndex);

        var cts = new CancellationTokenSource();
        _pageCts[pageIndex] = cts;
        SubscribeKey(key);
        Resource.MarkPageInFlight(pageIndex);
        _rerender();

        Task<Page<TItem, TCursor>> task;
        try
        {
            task = _fetcher(cursor, cts.Token);
        }
        catch (Exception ex)
        {
            ApplyError(pageIndex, ex);
            return;
        }

        if (task.IsCompletedSuccessfully)
        {
            CommitSuccess(pageIndex, task.Result, key);
            return;
        }
        if (task.IsFaulted)
        {
            ApplyError(pageIndex, task.Exception!.GetBaseException());
            return;
        }
        if (task.IsCanceled) return;

        task.ContinueWith(t =>
        {
            void Apply()
            {
                if (_disposed) return;
                if (!_pageCts.TryGetValue(pageIndex, out var ourCts) || ourCts != cts) return;
                if (cts.IsCancellationRequested) return;

                if (t.IsCanceled) { _pageCts.Remove(pageIndex); return; }
                if (t.IsFaulted)
                {
                    var ex = t.Exception!.GetBaseException();
                    if (ex is OperationCanceledException) { _pageCts.Remove(pageIndex); return; }
                    ApplyError(pageIndex, ex);
                    return;
                }
                CommitSuccess(pageIndex, t.Result, key);
            }

            if (_dispatcher is { } d) d.Post(Apply);
            else Apply();
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    private void CommitSuccess(int pageIndex, Page<TItem, TCursor> page, string key)
    {
        _cache.Set(key, page, _options.EffectiveStaleTime, _options.EffectiveCacheTime);
        Resource.ApplyPageResult(pageIndex, page);
        _pageCts.Remove(pageIndex);
        // Advance any deferred request whose cursor chain this completion unblocks.
        // We fire only the smallest — cursor paging is serial, so requesting higher
        // pages now would just add them back to _deferredRequests. As each page in
        // the chain completes, the next one gets pulled off here and fired.
        if (_deferredRequests.Count > 0)
        {
            int next = _deferredRequests.Min;
            if (HasLoadedPage(next - 1))
            {
                _deferredRequests.Remove(next);
                RequestPage(next);
            }
        }
        NotifyPending();
        _rerender();
    }

    private void ApplyError(int pageIndex, Exception ex)
    {
        Resource.ApplyPageError(pageIndex, ex);
        _pageCts.Remove(pageIndex);
        // Drop any deferred requests that depend on this page — the cursor chain is
        // broken, and Retry() on the errored page is the right way to resume.
        _deferredRequests.Clear();
        NotifyPending();
        _rerender();
    }

    private bool HasLoadedPage(int pageIndex)
    {
        // Reading through the resource's Items is expensive; we instead rely on the
        // cache having the page. If present, consider it loaded for sequencing.
        return _cache.TryGet<Page<TItem, TCursor>>(CacheKeyFor(pageIndex), out _);
    }

    private void SubscribeKey(string key)
    {
        if (_subscribedKeys.Add(key))
            _cache.Subscribe(key);
    }

    private void Refresh()
    {
        if (_disposed) return;

        // Cancel in-flight, invalidate cached pages, clear the resource, restart.
        foreach (var cts in _pageCts.Values)
        {
            try { cts.Cancel(); cts.Dispose(); } catch { /* ignore */ }
        }
        _pageCts.Clear();
        _deferredRequests.Clear();
        foreach (var key in _subscribedKeys.ToArray())
        {
            _cache.Invalidate(key);
        }
        // Keep subscriptions so cache retains ref-count correctness — we'll re-subscribe
        // lazily on RequestPage calls. Actually simpler: unsubscribe and re-subscribe.
        foreach (var key in _subscribedKeys)
        {
            try { _cache.Unsubscribe(key); } catch (InvalidOperationException) { /* defensive */ }
        }
        _subscribedKeys.Clear();

        // Reset the resource and kick off page 0.
        Resource = CreateResource();
        _pendingScope?.SetLoading(this, true);
        _rerender();
        RequestPage(0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var cts in _pageCts.Values)
        {
            try { cts.Cancel(); cts.Dispose(); } catch { /* ignore */ }
        }
        _pageCts.Clear();
        _deferredRequests.Clear();
        foreach (var key in _subscribedKeys)
        {
            try { _cache.Unsubscribe(key); } catch (InvalidOperationException) { /* defensive */ }
        }
        _subscribedKeys.Clear();
        _pendingScope?.Unregister(this);
    }
}
