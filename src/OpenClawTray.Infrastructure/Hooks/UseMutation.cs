using OpenClawTray.Infrastructure.Core;

namespace OpenClawTray.Infrastructure.Hooks;

/// <summary>
/// Callbacks and side-effects for <see cref="UseMutationExtensions.UseMutation{TInput,TResult}"/>.
/// All callbacks are optional; all run on the dispatcher thread except <see cref="OnOptimistic"/>
/// which runs synchronously on the caller of <see cref="Mutation{TInput,TResult}.RunAsync"/>
/// — the typical case is a render-thread click handler, so the optimistic update lands in the
/// very next frame without a dispatcher hop.
/// </summary>
/// <remarks>
/// <para><b>InvalidateKeys.</b> On success, each key is passed to
/// <see cref="QueryCache.Invalidate(string)"/>. Sibling <c>UseResource</c> hooks subscribed to
/// those keys will observe the invalidation and refetch on their next render.</para>
/// <para>Error path: <see cref="OnError"/> fires but <see cref="InvalidateKeys"/> does
/// <b>not</b> — the assumption is the server state didn't change, so the cache is still valid.</para>
/// </remarks>
public sealed record MutationOptions<TInput, TResult>(
    Action<TInput>? OnOptimistic = null,
    Action<TResult, TInput>? OnSuccess = null,
    Action<Exception, TInput>? OnError = null,
    string[]? InvalidateKeys = null);

/// <summary>
/// Handle returned by <see cref="UseMutationExtensions.UseMutation{TInput,TResult}"/>. Carries
/// the pending/error/last-result state and the <see cref="RunAsync"/> entry point.
/// </summary>
/// <remarks>
/// <para><b>Concurrency.</b> Overlapping <see cref="RunAsync"/> calls each get their own
/// cancellation token; both complete and fire their callbacks in completion order.
/// <see cref="LastResult"/> is whichever finishes last. If you want strictly-serialized
/// mutations, wrap <see cref="RunAsync"/> behind your own gate (or disable the trigger
/// control while <see cref="IsPending"/> is true).</para>
/// <para><b>Reset.</b> <see cref="Reset"/> clears <see cref="Error"/> and
/// <see cref="LastResult"/> but does <b>not</b> cancel in-flight work — this is an explicit
/// choice so a "dismiss the error banner" action doesn't abort the user's retry.</para>
/// </remarks>
public sealed class Mutation<TInput, TResult>
{
    private readonly MutationHookState<TInput, TResult> _state;

    internal Mutation(MutationHookState<TInput, TResult> state) { _state = state; }

    /// <summary>True while at least one <see cref="RunAsync"/> call is in-flight.</summary>
    public bool IsPending => _state.PendingCount > 0;

    /// <summary>Most recent error, or null if the last completion was a success or <see cref="Reset"/> was called.</summary>
    public Exception? Error => _state.Error;

    /// <summary>Most recent successful result, or default if none yet.</summary>
    public TResult? LastResult => _state.LastResult;

    /// <summary>
    /// Kick off the mutator with <paramref name="input"/>. Returns a task that completes
    /// with the mutator's result or fault.
    /// </summary>
    /// <remarks>
    /// <para>Ordering: <see cref="MutationOptions{TInput,TResult}.OnOptimistic"/> fires
    /// synchronously before the mutator starts. <see cref="MutationOptions{TInput,TResult}.OnSuccess"/>
    /// or <see cref="MutationOptions{TInput,TResult}.OnError"/> fires on the hook's dispatcher
    /// thread after completion.</para>
    /// <para>If <see cref="MutationOptions{TInput,TResult}.OnOptimistic"/> throws, the
    /// mutator is never invoked and the returned task is faulted with the optimistic
    /// exception — prevents half-applied state where the optimistic patch ran but the
    /// real request can't.</para>
    /// </remarks>
    public Task<TResult> RunAsync(TInput input) => _state.RunAsync(input);

    /// <summary>
    /// Clear <see cref="Error"/> and <see cref="LastResult"/>. Does not cancel or affect any
    /// in-flight <see cref="RunAsync"/> call.
    /// </summary>
    public void Reset() => _state.Reset();
}

/// <summary>
/// Extension methods providing <c>UseMutation</c> on <see cref="RenderContext"/>.
/// </summary>
/// <remarks>
/// See <c>docs/specs/020-async-resources-design.md</c> §8 for the state machine and
/// the rationale for splitting reads (<c>UseResource</c>) from writes.
/// </remarks>
public static class UseMutationExtensions
{
    /// <summary>
    /// Registers a <see cref="Mutation{TInput,TResult}"/> for this hook slot. The handle
    /// is stable across renders (pass it to buttons, context menus, etc.).
    /// </summary>
    /// <param name="mutator">The async write. Receives the caller's input and a token that
    /// fires on unmount. Rethrow <see cref="OperationCanceledException"/> to honour it.</param>
    /// <param name="cache">The cache whose keys to invalidate on success, or null to skip
    /// invalidation regardless of <see cref="MutationOptions{TInput,TResult}.InvalidateKeys"/>.</param>
    /// <param name="options">Optional lifecycle callbacks; null uses defaults (no callbacks).</param>
    /// <param name="dispatcher">Optional dispatcher override; null captures the current
    /// <c>DispatcherQueue</c> at registration time (same convention as <c>UseResource</c>).</param>
    public static Mutation<TInput, TResult> UseMutation<TInput, TResult>(
        this RenderContext ctx,
        Func<TInput, CancellationToken, Task<TResult>> mutator,
        QueryCache? cache,
        MutationOptions<TInput, TResult>? options = null,
        IHookDispatcher? dispatcher = null)
        => UseMutationCore(ctx, mutator, cache, options, dispatcher);

    /// <summary>
    /// Overload that reads the ambient <see cref="QueryCache"/> from
    /// <see cref="AppContexts.QueryCache"/>.
    /// </summary>
    public static Mutation<TInput, TResult> UseMutation<TInput, TResult>(
        this RenderContext ctx,
        Func<TInput, CancellationToken, Task<TResult>> mutator,
        MutationOptions<TInput, TResult>? options = null,
        IHookDispatcher? dispatcher = null)
        => UseMutationCore(ctx, mutator, ctx.UseContext(AppContexts.QueryCache), options, dispatcher);

    private static Mutation<TInput, TResult> UseMutationCore<TInput, TResult>(
        RenderContext ctx,
        Func<TInput, CancellationToken, Task<TResult>> mutator,
        QueryCache? cache,
        MutationOptions<TInput, TResult>? options,
        IHookDispatcher? dispatcher)
    {
        var stateRef = ctx.UseRef<MutationHookState<TInput, TResult>?>(null);
        var (_, rerenderTick) = ctx.UseReducer(0, threadSafe: true);

        var state = stateRef.Current ??= new MutationHookState<TInput, TResult>(
            cache: cache,
            dispatcher: dispatcher ?? TryCaptureCurrentDispatcher(),
            requestRerender: () => rerenderTick(n => n + 1));

        // Refresh per-render parameters — mutator lambdas close over props/state, and
        // callers expect the latest closure to win. The hook's identity (state, in-flight
        // tokens, LastResult) persists across renders; only these inputs rotate.
        state.Mutator = mutator;
        state.Options = options;

        ctx.UseEffect(() => () => state.Dispose());

        return state.Handle;
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
/// Mutable state backing a single <c>UseMutation</c> hook instance. Owned per-component,
/// per-hook-slot; disposed on unmount.
/// </summary>
internal sealed class MutationHookState<TInput, TResult> : IDisposable
{
    private readonly QueryCache? _cache;
    private readonly IHookDispatcher? _dispatcher;
    private readonly Action _requestRerender;
    private readonly CancellationTokenSource _unmountCts = new();
    private readonly object _lock = new();
    private readonly Mutation<TInput, TResult> _handle;

    private int _pendingCount;
    private TResult? _lastResult;
    private Exception? _error;
    private bool _isDisposed;

    // The current closure-captured mutator and options. Rotated on every render so the
    // hook always uses the freshest lambda — matches the UseCallback/UseEffect convention.
    public Func<TInput, CancellationToken, Task<TResult>> Mutator { get; set; } = (_, _) => Task.FromResult(default(TResult)!);
    public MutationOptions<TInput, TResult>? Options { get; set; }

    public Mutation<TInput, TResult> Handle => _handle;
    public int PendingCount { get { lock (_lock) return _pendingCount; } }
    public TResult? LastResult { get { lock (_lock) return _lastResult; } }
    public Exception? Error { get { lock (_lock) return _error; } }

    public MutationHookState(QueryCache? cache, IHookDispatcher? dispatcher, Action requestRerender)
    {
        _cache = cache;
        _dispatcher = dispatcher;
        _requestRerender = requestRerender;
        _handle = new Mutation<TInput, TResult>(this);
    }

    public Task<TResult> RunAsync(TInput input)
    {
        if (_isDisposed)
        {
            // The component unmounted but someone still holds the Mutation handle. Surface a
            // cancellation rather than silently firing callbacks on a dead component.
            return Task.FromCanceled<TResult>(new CancellationToken(canceled: true));
        }

        var options = Options;

        // OnOptimistic runs synchronously. If it throws, we abort before starting the
        // mutator so no half-applied state is possible.
        if (options?.OnOptimistic is { } opt)
        {
            try { opt(input); }
            catch (Exception ex) { return Task.FromException<TResult>(ex); }
        }

        // Link the call's token with the unmount token so in-flight work drops on unmount.
        var callCts = CancellationTokenSource.CreateLinkedTokenSource(_unmountCts.Token);
        var ct = callCts.Token;
        IncrementPending();
        RequestRerender();

        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<TResult> inner;
        try { inner = Mutator(input, ct); }
        catch (Exception ex)
        {
            // Synchronous throw from mutator.
            FinishFailure(options, ex, input, callCts);
            tcs.SetException(ex);
            return tcs.Task;
        }

        inner.ContinueWith(t =>
        {
            void Apply()
            {
                if (t.IsCanceled || (t.IsFaulted && t.Exception?.InnerException is OperationCanceledException))
                {
                    FinishCancelled(callCts);
                    // Surface cancellation to the returned task so callers that awaited it
                    // see the cancellation, but do not fire OnError (matches UseResource §6.4).
                    tcs.TrySetCanceled(ct);
                    return;
                }

                if (t.IsFaulted)
                {
                    var ex = t.Exception!.GetBaseException();
                    FinishFailure(options, ex, input, callCts);
                    tcs.TrySetException(ex);
                    return;
                }

                var result = t.Result;
                FinishSuccess(options, result, input, callCts);
                tcs.TrySetResult(result);
            }

            if (_dispatcher is { } d) d.Post(Apply);
            else Apply();
        }, TaskContinuationOptions.ExecuteSynchronously);

        return tcs.Task;
    }

    public void Reset()
    {
        lock (_lock)
        {
            _error = null;
            _lastResult = default;
        }
        RequestRerender();
    }

    private void FinishSuccess(MutationOptions<TInput, TResult>? options, TResult result, TInput input, CancellationTokenSource callCts)
    {
        if (_isDisposed) { callCts.Dispose(); return; }

        lock (_lock)
        {
            _pendingCount = Math.Max(0, _pendingCount - 1);
            _lastResult = result;
            _error = null;
        }

        // Invalidate cache keys before callbacks so an OnSuccess handler that re-reads the
        // cache observes the invalidation.
        if (options?.InvalidateKeys is { Length: > 0 } keys && _cache is not null)
        {
            foreach (var key in keys) _cache.Invalidate(key);
        }

        try { options?.OnSuccess?.Invoke(result, input); }
        finally
        {
            callCts.Dispose();
            RequestRerender();
        }
    }

    private void FinishFailure(MutationOptions<TInput, TResult>? options, Exception ex, TInput input, CancellationTokenSource callCts)
    {
        if (_isDisposed) { callCts.Dispose(); return; }

        lock (_lock)
        {
            _pendingCount = Math.Max(0, _pendingCount - 1);
            _error = ex;
        }

        try { options?.OnError?.Invoke(ex, input); }
        finally
        {
            callCts.Dispose();
            RequestRerender();
        }
    }

    private void FinishCancelled(CancellationTokenSource callCts)
    {
        lock (_lock)
        {
            _pendingCount = Math.Max(0, _pendingCount - 1);
        }
        callCts.Dispose();
        if (!_isDisposed) RequestRerender();
    }

    private void IncrementPending()
    {
        lock (_lock) _pendingCount++;
    }

    private void RequestRerender()
    {
        if (_isDisposed) return;
        if (_dispatcher is { } d) d.Post(_requestRerender);
        else _requestRerender();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _unmountCts.Cancel();
        _unmountCts.Dispose();
    }
}
