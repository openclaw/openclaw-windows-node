namespace OpenClawTray.Infrastructure.Core;

/// <summary>
/// Central service that invalidates stale <see cref="QueryCache"/> entries when the
/// app window is reactivated or resumed, so consumers that opted in via
/// <c>ResourceOptions.RefetchOnWindowFocus = true</c> see fresh data after an
/// Alt-Tab or resume. Disabled by default — see
/// <see cref="ReactorFeatureFlags.FocusRevalidation"/>.
/// </summary>
/// <remarks>
/// <para><b>Per-resource enrollment.</b> Hooks that opt in call <see cref="Enroll"/>
/// with their cache key; the service tracks enrolled keys as a set and invalidates
/// only those whose entry is past <c>StaleTime</c> at activation time. Non-enrolled
/// keys are untouched.</para>
/// <para><b>Throttling.</b> A default 30-second window between activation-driven
/// revalidation sweeps prevents Alt-Tab thrashing from refetching on every
/// transient focus event. Adjustable via <see cref="ThrottleWindow"/>.</para>
/// <para><b>Threading.</b> All members are thread-safe. Activation callbacks may
/// fire on the UI thread or the dispatcher thread — the service does not marshal.
/// Invalidation in turn fires <c>QueryCache.EntryChanged</c>, which the
/// <c>UseResource</c> hook listens to and re-renders from.</para>
/// </remarks>
public sealed class FocusRevalidationService
{
    private readonly QueryCache _cache;
    private readonly HashSet<string> _enrolled = new();
    private readonly object _lock = new();
    private DateTime _lastSweepUtc = DateTime.MinValue;

    /// <summary>
    /// Minimum time between activation-driven revalidation sweeps. Defaults to 30s.
    /// </summary>
    public TimeSpan ThrottleWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Clock override for deterministic tests.</summary>
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public FocusRevalidationService(QueryCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>Diagnostic: current number of keys enrolled for focus revalidation.</summary>
    public int EnrolledCount { get { lock (_lock) return _enrolled.Count; } }

    /// <summary>
    /// Enroll <paramref name="key"/> in focus revalidation. Hooks call this when
    /// <c>ResourceOptions.RefetchOnWindowFocus = true</c>. Idempotent — re-enrolling
    /// the same key is a no-op.
    /// </summary>
    public void Enroll(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (_lock) _enrolled.Add(key);
    }

    /// <summary>
    /// Remove <paramref name="key"/> from the focus-revalidation set. Hooks call this
    /// on unmount or when their cache key changes.
    /// </summary>
    public void Unenroll(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (_lock) _enrolled.Remove(key);
    }

    /// <summary>
    /// Revalidate enrolled entries. Invalidates every enrolled key whose entry is
    /// past its <c>StaleTime</c>. Returns the keys that were invalidated.
    /// </summary>
    /// <remarks>
    /// Short-circuits if the last call fired within <see cref="ThrottleWindow"/> —
    /// returns an empty list without touching the cache.
    /// </remarks>
    public IReadOnlyList<string> RevalidateNow()
    {
        var now = UtcNow();
        lock (_lock)
        {
            if (now - _lastSweepUtc < ThrottleWindow)
                return Array.Empty<string>();
            _lastSweepUtc = now;
        }

        // Copy the enrolled set out of the lock — Invalidate callbacks can fire
        // EntryChanged handlers that re-enter the service (e.g. unenroll on
        // unmount during the refetch storm).
        string[] snapshot;
        lock (_lock) snapshot = _enrolled.ToArray();

        var invalidated = new List<string>();
        foreach (var key in snapshot)
        {
            if (IsStale(key, now))
            {
                _cache.Invalidate(key);
                invalidated.Add(key);
            }
        }
        return invalidated;
    }

    /// <summary>
    /// Forces a revalidation sweep, bypassing the throttle window. Diagnostic /
    /// test-only — production code paths should go through <see cref="RevalidateNow"/>.
    /// </summary>
    public IReadOnlyList<string> RevalidateNowForce()
    {
        lock (_lock) _lastSweepUtc = DateTime.MinValue;
        return RevalidateNow();
    }

    private bool IsStale(string key, DateTime now)
    {
        // We don't have a typed TryGet here. Walk the cache's slot-level snapshot
        // via the non-generic tryGetUnchecked API.
        if (!_cache.TryGetFetchedAt(key, out var fetchedAt, out var staleTime))
            return false;
        return now - fetchedAt >= staleTime;
    }
}
