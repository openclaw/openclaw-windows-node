using System.Collections.Concurrent;
using OpenClawTray.Infrastructure.Core;

namespace OpenClawTray.Infrastructure.Hooks;

/// <summary>
/// Tuning for <see cref="UseResourceExtensions.UseResource{T}"/>. Defaults mirror
/// TanStack Query — zero <see cref="StaleTime"/> (always refetch-on-mount but dedup
/// concurrent requests), five-minute <see cref="CacheTime"/>.
/// </summary>
public sealed record ResourceOptions(
    TimeSpan? StaleTime = null,
    TimeSpan? CacheTime = null,
    int RetryCount = 0,
    bool RefetchOnMount = true,
    string? CacheKey = null,
    bool RefetchOnWindowFocus = false)
{
    public static readonly ResourceOptions Default = new();
    internal TimeSpan EffectiveStaleTime => StaleTime ?? TimeSpan.Zero;
    internal TimeSpan EffectiveCacheTime => CacheTime ?? TimeSpan.FromMinutes(5);
}

/// <summary>
/// Abstraction for marshalling continuations back to the render thread. Null-safe —
/// if the callback is absent the continuation runs inline on the thread-pool
/// completion thread (tests and headless hosts).
/// </summary>
/// <remarks>
/// The production implementation wraps <c>DispatcherQueue.TryEnqueue</c>; tests typically
/// install a synchronous stub that records invocations.
/// </remarks>
public interface IHookDispatcher
{
    /// <summary>Enqueue <paramref name="action"/> to run on the dispatcher thread.</summary>
    void Post(Action action);
}

/// <summary>
/// Default <see cref="IHookDispatcher"/> backed by <c>DispatcherQueue.GetForCurrentThread()</c>.
/// Falls back to inline invocation when called outside a WinUI dispatcher (unit tests).
/// </summary>
internal sealed class WindowsDispatcherHookDispatcher : IHookDispatcher
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _queue;

    public WindowsDispatcherHookDispatcher()
    {
        _queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    }

    public void Post(Action action)
    {
        if (_queue is null) { action(); return; }
        if (!_queue.TryEnqueue(() => action()))
            action(); // dispatcher shut down — fall back to inline
    }
}

/// <summary>
/// Extension methods providing <c>UseResource</c> on <see cref="RenderContext"/>.
/// </summary>
/// <remarks>
/// See <c>docs/specs/020-async-resources-design.md</c> §6 for the state machine
/// and the full design rationale (cancellation, stale-while-revalidate, retry).
/// </remarks>
public static class UseResourceExtensions
{
    /// <summary>
    /// Runs an async fetch keyed on <paramref name="deps"/>, returning an
    /// <see cref="AsyncValue{T}"/> that tracks the fetch's lifecycle. The hook
    /// owns the cancellation token, stores results in <paramref name="cache"/>,
    /// and re-renders when new results land.
    /// </summary>
    /// <remarks>
    /// <para><b>Sync-complete fast path.</b> If <paramref name="fetcher"/> returns an
    /// already-completed task, this call returns <c>Data(result)</c> on the same render,
    /// with no transient <c>Loading</c> flash.</para>
    /// <para><b>Dispatcher.</b> The hook captures the dispatcher at registration time
    /// (<c>DispatcherQueue.GetForCurrentThread()</c>). In unit tests without a WinUI
    /// dispatcher, continuations run inline on the thread-pool thread that completed
    /// the fetch.</para>
    /// </remarks>
    public static AsyncValue<T> UseResource<T>(
        this RenderContext ctx,
        Func<CancellationToken, Task<T>> fetcher,
        QueryCache cache,
        object[] deps,
        ResourceOptions? options = null,
        IHookDispatcher? dispatcher = null)
        => UseResourceCore(ctx, fetcher, cache, deps, options, dispatcher);

    /// <summary>
    /// Overload that reads the ambient <see cref="QueryCache"/> from
    /// <see cref="AppContexts.QueryCache"/>. <see cref="Hosting.ReactorHost"/> installs a
    /// process-wide default cache at startup; tests or subtrees may override it via
    /// <c>.Provide(AppContexts.QueryCache, customCache)</c>.
    /// </summary>
    public static AsyncValue<T> UseResource<T>(
        this RenderContext ctx,
        Func<CancellationToken, Task<T>> fetcher,
        object[] deps,
        ResourceOptions? options = null,
        IHookDispatcher? dispatcher = null)
        => UseResourceCore(ctx, fetcher, ctx.UseContext(AppContexts.QueryCache), deps, options, dispatcher);

    private static AsyncValue<T> UseResourceCore<T>(
        RenderContext ctx,
        Func<CancellationToken, Task<T>> fetcher,
        QueryCache cache,
        object[] deps,
        ResourceOptions? options,
        IHookDispatcher? dispatcher)
    {
        options ??= ResourceOptions.Default;

        // Stable per-call-site identity. UseRef persists across renders; the GUID is
        // chosen once on first render for this component instance + hook index.
        var hookIdRef = ctx.UseRef<string?>(null);
        hookIdRef.Current ??= Guid.NewGuid().ToString("N");

        var stateRef = ctx.UseRef<ResourceHookState<T>?>(null);
        var (_, rerenderTick) = ctx.UseReducer(0, threadSafe: true);
        // Peek the nearest Pending scope at the call-site. Null outside any <see cref="Pending"/>
        // — zero-overhead when no bubble-up is installed.
        var pendingScope = ctx.UseContext(AppContexts.PendingScope);
        // Focus revalidation service: null when no service is installed in the context
        // (tests, headless hosts). Hooks with RefetchOnWindowFocus=true enroll their
        // cache key so activation events can invalidate stale entries.
        var focusService = ctx.UseContext(AppContexts.FocusRevalidation);

        var state = stateRef.Current ??= new ResourceHookState<T>(
            cache: cache,
            dispatcher: dispatcher ?? TryCaptureCurrentDispatcher(),
            requestRerender: () => rerenderTick(n => n + 1),
            pendingScope: pendingScope,
            focusService: options.RefetchOnWindowFocus ? focusService : null);

        // Run-once-on-unmount cleanup. Deps-change is driven synchronously below rather than
        // via UseEffect, because we need the updated value inside this same render.
        ctx.UseEffect(() => () => state.Dispose());

        string newKey = options.CacheKey ?? $"{hookIdRef.Current}/{DepsHash(deps)}";
        bool firstRender = state.LastDeps is null;
        bool depsChanged = !firstRender && (!string.Equals(state.CacheKey, newKey, StringComparison.Ordinal));

        if (firstRender || depsChanged)
        {
            state.TransitionToKey(newKey, deps);
            EnterKey(state, fetcher, options);
        }
        else
        {
            // Re-render that did NOT change deps — look up current state from cache for
            // cache-invalidation edge cases (another subscriber invalidated us).
            ReconcileWithCache(state, fetcher, options);
        }

        return state.LastValue;
    }

    private static IHookDispatcher? TryCaptureCurrentDispatcher()
    {
        // DispatcherQueue.GetForCurrentThread() throws COMException in processes without
        // the WinUI activation registered (unit test hosts). Swallow and treat as "no
        // dispatcher" so the hook's inline continuation path kicks in.
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

    private static void EnterKey<T>(
        ResourceHookState<T> state,
        Func<CancellationToken, Task<T>> fetcher,
        ResourceOptions options)
    {
        // Cache hit path — check what's there first so we honour stale-while-revalidate.
        if (state.Cache.TryGet<T>(state.CacheKey, out var entry))
        {
            var age = state.Cache.UtcNow() - entry.FetchedAt;
            if (age <= options.EffectiveStaleTime)
            {
                // Fresh — no fetch.
                state.LastValue = new AsyncValue<T>.Data(entry.Value);
                return;
            }
            // Stale — surface previous value as Reloading and kick off a refetch.
            state.LastValue = new AsyncValue<T>.Reloading(entry.Value);
            BeginFetch(state, fetcher, options);
            return;
        }

        // Cache miss.
        if (!options.RefetchOnMount)
        {
            state.LastValue = AsyncValue<T>.Loading.Instance;
            return;
        }

        BeginFetch(state, fetcher, options);
    }

    private static void ReconcileWithCache<T>(
        ResourceHookState<T> state,
        Func<CancellationToken, Task<T>> fetcher,
        ResourceOptions options)
    {
        // An external invalidation removed the cache entry. Re-enter the key so we
        // refetch with the current deps.
        if (state.LastValue is AsyncValue<T>.Data &&
            !state.Cache.TryGet<T>(state.CacheKey, out _))
        {
            // Treat as stale-without-prior: kick off a fetch but keep the Data state
            // visible during the refetch. Callers generally want the existing value
            // until the new one lands.
            BeginFetch(state, fetcher, options);
        }
    }

    private static void BeginFetch<T>(
        ResourceHookState<T> state,
        Func<CancellationToken, Task<T>> fetcher,
        ResourceOptions options)
    {
        if (state.InFlight) return; // dedup concurrent fetches for the same key/state

        state.Cts?.Dispose();
        state.Cts = new CancellationTokenSource();
        StartAttempt(state, fetcher, options, attempt: 0, inlineSyncResult: true);
    }

    private static void StartAttempt<T>(
        ResourceHookState<T> state,
        Func<CancellationToken, Task<T>> fetcher,
        ResourceOptions options,
        int attempt,
        bool inlineSyncResult)
    {
        if (state.IsDisposed) return;
        var cts = state.Cts;
        if (cts is null || cts.IsCancellationRequested) return;
        var ct = cts.Token;

        Task<T> task;
        try
        {
            task = fetcher(ct);
        }
        catch (Exception ex)
        {
            HandleFailure(state, fetcher, options, attempt, ex, inlineSyncResult);
            return;
        }

        // Sync-complete fast path (only on the initial attempt): honour §6.2 step 2.
        if (inlineSyncResult && task.IsCompletedSuccessfully)
        {
            var v = task.Result;
            state.Cache.Set(state.CacheKey, v, options.EffectiveStaleTime, options.EffectiveCacheTime);
            state.LastValue = new AsyncValue<T>.Data(v);
            state.InFlight = false;
            return;
        }

        if (inlineSyncResult && task.IsCanceled)
        {
            state.LastValue = state.LastValue is AsyncValue<T>.Data
                ? state.LastValue
                : AsyncValue<T>.Loading.Instance;
            state.InFlight = false;
            return;
        }

        if (inlineSyncResult && task.IsFaulted)
        {
            HandleFailure(state, fetcher, options, attempt, task.Exception!.GetBaseException(), inlineSyncResult: true);
            return;
        }

        // Pending — render Loading unless we already have a prior value (stale).
        if (inlineSyncResult)
        {
            state.LastValue = state.LastValue switch
            {
                AsyncValue<T>.Data d => new AsyncValue<T>.Reloading(d.Value),
                AsyncValue<T>.Reloading r => r,
                _ => AsyncValue<T>.Loading.Instance,
            };
        }
        state.InFlight = true;
        ScheduleCompletion(state, task, fetcher, options, attempt);
    }

    private static void HandleFailure<T>(
        ResourceHookState<T> state,
        Func<CancellationToken, Task<T>> fetcher,
        ResourceOptions options,
        int attempt,
        Exception ex,
        bool inlineSyncResult)
    {
        if (attempt < options.RetryCount)
        {
            int delayMs = 100 * (1 << attempt);
            state.InFlight = true;
            state.ScheduleRetry(TimeSpan.FromMilliseconds(delayMs), () =>
            {
                StartAttempt(state, fetcher, options, attempt + 1, inlineSyncResult: false);
            });
            return;
        }

        state.LastValue = new AsyncValue<T>.Error(ex);
        state.InFlight = false;
        if (!inlineSyncResult) state.RequestRerender();
    }

    private static void ScheduleCompletion<T>(
        ResourceHookState<T> state,
        Task<T> task,
        Func<CancellationToken, Task<T>> fetcher,
        ResourceOptions options,
        int attempt)
    {
        var cts = state.Cts!;
        var ct = cts.Token;
        task.ContinueWith(t =>
        {
            void Apply()
            {
                if (state.IsDisposed) return;
                if (ct.IsCancellationRequested) return; // deps changed or unmount — drop silently

                if (t.IsCanceledOrDropped())
                {
                    // §6.4: we don't surface cancellation as Error — drop silently.
                    return;
                }

                if (t.IsFaulted)
                {
                    HandleFailure(state, fetcher, options, attempt, t.Exception!.GetBaseException(), inlineSyncResult: false);
                    return;
                }

                // Success.
                var value = t.Result;
                state.Cache.Set(state.CacheKey, value, options.EffectiveStaleTime, options.EffectiveCacheTime);
                var next2 = new AsyncValue<T>.Data(value);
                var changed = !EqualityComparer<AsyncValue<T>>.Default.Equals(state.LastValue, next2);
                state.LastValue = next2;
                state.InFlight = false;
                if (changed) state.RequestRerender();
            }

            if (state.Dispatcher is { } disp) disp.Post(Apply);
            else Apply();
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    private static int DepsHash(object[] deps)
    {
        unchecked
        {
            int h = 17;
            foreach (var d in deps)
                h = h * 31 + (d?.GetHashCode() ?? 0);
            return h;
        }
    }

    private static bool IsCanceledOrDropped<T>(this Task<T> t) =>
        t.IsCanceled || (t.IsFaulted && t.Exception?.InnerException is OperationCanceledException);
}

/// <summary>
/// Mutable state backing a single <c>UseResource</c> hook instance. Instances are
/// owned per-component, per-hook-slot, and are disposed on unmount.
/// </summary>
internal sealed class ResourceHookState<T> : IDisposable
{
    public readonly QueryCache Cache;
    public readonly IHookDispatcher? Dispatcher;
    public readonly Action RequestRerender;
    public readonly PendingScope? PendingScope;
    public string CacheKey = "";
    public object[]? LastDeps;
    public CancellationTokenSource? Cts;
    private AsyncValue<T> _lastValue = AsyncValue<T>.Loading.Instance;
    public AsyncValue<T> LastValue
    {
        get => _lastValue;
        set { _lastValue = value; NotifyPending(); }
    }
    public bool InFlight;
    public bool IsDisposed;

    private readonly Action<string> _onEntryChanged;
    private readonly FocusRevalidationService? _focusService;

    public ResourceHookState(
        QueryCache cache,
        IHookDispatcher? dispatcher,
        Action requestRerender,
        PendingScope? pendingScope = null,
        FocusRevalidationService? focusService = null)
    {
        Cache = cache;
        Dispatcher = dispatcher;
        RequestRerender = requestRerender;
        PendingScope = pendingScope;
        _focusService = focusService;
        // Subscribe to the cache's change notifications so external invalidations
        // (mutation side-effects, focus revalidation) reach this hook without
        // depending on a parent re-render. The handler runs on whichever thread
        // fired the event; RequestRerender marshals through the thread-safe reducer.
        _onEntryChanged = OnEntryChanged;
        cache.EntryChanged += _onEntryChanged;
        // Register with initial Loading state — the bubble-up scope should show the
        // fallback immediately, before the first fetch round-trip.
        PendingScope?.Register(this, isLoading: true);
    }

    private void OnEntryChanged(string key)
    {
        if (IsDisposed) return;
        if (!string.Equals(key, CacheKey, StringComparison.Ordinal)) return;
        // Only react when the entry is gone — a Set that just wrote the value we
        // already hold would trigger a no-op re-render otherwise.
        if (LastValue is AsyncValue<T>.Data && !Cache.TryGet<T>(key, out _))
            RequestRerender();
    }

    private void NotifyPending()
    {
        if (PendingScope is null) return;
        bool loading = _lastValue is AsyncValue<T>.Loading;
        PendingScope.SetLoading(this, loading);
    }

    public void TransitionToKey(string newKey, object[] newDeps)
    {
        // Cancel outstanding work for the previous key.
        Cts?.Cancel();
        Cts?.Dispose();
        Cts = null;
        InFlight = false;

        if (!string.IsNullOrEmpty(CacheKey) && !string.Equals(CacheKey, newKey, StringComparison.Ordinal))
        {
            try { Cache.Unsubscribe(CacheKey); } catch (InvalidOperationException) { /* defensive */ }
            _focusService?.Unenroll(CacheKey);
        }

        CacheKey = newKey;
        Cache.Subscribe(newKey);
        _focusService?.Enroll(newKey);
        LastDeps = newDeps.ToArray();
    }

    public void ScheduleRetry(TimeSpan delay, Action afterDelay)
    {
        // Use a Timer so the retry is cancellable via the hook-owned token.
        Timer? timer = null;
        var ct = Cts?.Token ?? CancellationToken.None;
        timer = new Timer(_ =>
        {
            timer?.Dispose();
            if (IsDisposed || ct.IsCancellationRequested) return;
            if (Dispatcher is { } d) d.Post(afterDelay);
            else afterDelay();
        }, null, delay, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Cache.EntryChanged -= _onEntryChanged;
        Cts?.Cancel();
        Cts?.Dispose();
        Cts = null;
        if (!string.IsNullOrEmpty(CacheKey))
        {
            try { Cache.Unsubscribe(CacheKey); } catch (InvalidOperationException) { /* defensive */ }
            _focusService?.Unenroll(CacheKey);
        }
        PendingScope?.Unregister(this);
    }
}
