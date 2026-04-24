namespace OpenClawTray.Infrastructure.Hooks;

/// <summary>
/// Shared loading-state ref-count consumed by the <c>Pending</c> element and populated by
/// <c>UseResource</c> / <c>UseInfiniteResource</c> hooks inside the scope. When the scope
/// observes <b>any</b> registered resource in the <c>Loading</c> state (not <c>Reloading</c>),
/// the owning <c>Pending</c> element renders its fallback instead of the child subtree.
/// </summary>
/// <remarks>
/// <para><b>Semantics.</b> Only <c>Loading</c> triggers the fallback — spec §10.1. A
/// <c>Reloading(previous)</c> is "we already have something to show" and the subtree
/// continues to render normally.</para>
/// <para><b>Threading.</b> All members are thread-safe. <see cref="Changed"/> fires on the
/// thread that caused the mutation — consumers (typically <c>Pending</c>'s re-render
/// trigger) marshal it to the dispatcher themselves.</para>
/// <para><b>Scope nesting.</b> Each <c>Pending</c> provides a fresh scope to its subtree,
/// so nested <c>Pending</c>s are independent. A hook registers only with its nearest
/// ancestor scope.</para>
/// </remarks>
public sealed class PendingScope
{
    private readonly Dictionary<object, bool> _loadingByToken = new(capacity: 4);
    private readonly object _lock = new();

    /// <summary>Fires when a resource joins, leaves, or changes its loading state.</summary>
    public event Action? Changed;

    /// <summary>
    /// Start tracking <paramref name="token"/> with the given initial <paramref name="isLoading"/>
    /// state. A hook typically uses its own <c>this</c>-equivalent as the token.
    /// </summary>
    public void Register(object token, bool isLoading)
    {
        lock (_lock) _loadingByToken[token] = isLoading;
        Changed?.Invoke();
    }

    /// <summary>
    /// Update <paramref name="token"/>'s loading state. Silently ignored if the token
    /// was never registered (defensive — avoids forcing the caller to track whether they
    /// registered).
    /// </summary>
    public void SetLoading(object token, bool isLoading)
    {
        bool changed;
        lock (_lock)
        {
            if (!_loadingByToken.TryGetValue(token, out var prev)) return;
            if (prev == isLoading) return;
            _loadingByToken[token] = isLoading;
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Stop tracking <paramref name="token"/>. Idempotent.</summary>
    public void Unregister(object token)
    {
        bool removed;
        lock (_lock) removed = _loadingByToken.Remove(token);
        if (removed) Changed?.Invoke();
    }

    /// <summary>True iff any tracked token is currently <c>Loading</c>.</summary>
    public bool AnyLoading
    {
        get
        {
            lock (_lock)
            {
                foreach (var v in _loadingByToken.Values) if (v) return true;
                return false;
            }
        }
    }

    /// <summary>Snapshot the number of registered tokens (loading or not). Diagnostic only.</summary>
    public int Count { get { lock (_lock) return _loadingByToken.Count; } }
}
