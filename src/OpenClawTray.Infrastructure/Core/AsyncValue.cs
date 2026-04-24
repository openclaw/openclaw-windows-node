using System.Diagnostics;

namespace OpenClawTray.Infrastructure.Core;

/// <summary>
/// Algebraic data type representing the four observable states of an async fetch:
/// <see cref="Loading"/>, <see cref="Data"/>, <see cref="Error"/>, and <see cref="Reloading"/>.
/// Designed for exhaustive pattern matching with C# switch expressions.
/// </summary>
/// <remarks>
/// See spec <c>docs/specs/020-async-resources-design.md</c> §5 for the design rationale
/// and §6.2 for the transition rules driven by <c>UseResource</c>.
/// </remarks>
/// <typeparam name="T">The value type produced by the fetcher.</typeparam>
public abstract record AsyncValue<T>
{
    // Keep the hierarchy closed to prevent third-party cases that break exhaustive matching.
    private protected AsyncValue() { }

    /// <summary>
    /// First fetch in flight; no prior data is available.
    /// Transitions to <see cref="Data"/> or <see cref="Error"/>.
    /// </summary>
    public sealed record Loading : AsyncValue<T>
    {
        /// <summary>Singleton instance — <see cref="Loading"/> has no payload.</summary>
        public static readonly Loading Instance = new();
    }

    /// <summary>
    /// Fetch succeeded; <paramref name="Value"/> is authoritative.
    /// Transitions to <see cref="Reloading"/> when a refetch starts, or back to
    /// <see cref="Data"/> on cache hit within <c>StaleTime</c>.
    /// </summary>
    public sealed record Data(T Value) : AsyncValue<T>;

    /// <summary>
    /// Fetch failed. Any prior data is discarded — <c>UseResource</c> does not retain
    /// stale values across a failure. If you need "last known good", wrap the fetcher.
    /// </summary>
    public sealed record Error(Exception Exception) : AsyncValue<T>;

    /// <summary>
    /// Refetching with the previous value still observable (stale-while-revalidate).
    /// The <paramref name="Previous"/> payload is intentionally non-nullable so call
    /// sites can render the stale tree without null-guards. Components may choose to
    /// dim, overlay a spinner, or render identically to <see cref="Data"/> — §5.1.
    /// </summary>
    public sealed record Reloading(T Previous) : AsyncValue<T>;

    /// <summary>
    /// Convenience match that treats <see cref="Reloading"/> as <see cref="Data"/> by default.
    /// Pass <paramref name="reloading"/> explicitly to distinguish (e.g. dim stale content).
    /// </summary>
    /// <remarks>
    /// Allocates a delegate per arm at the call site; prefer a <c>switch</c> expression in
    /// per-row render paths (see spec §5.1).
    /// </remarks>
    public TResult Match<TResult>(
        Func<TResult> loading,
        Func<T, TResult> data,
        Func<Exception, TResult> error,
        Func<T, TResult>? reloading = null)
        => this switch
        {
            Loading => loading(),
            Data d => data(d.Value),
            Error e => error(e.Exception),
            Reloading r => (reloading ?? data)(r.Previous),
            _ => throw new UnreachableException()
        };
}
