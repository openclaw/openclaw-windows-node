using System.Diagnostics;

namespace OpenClawTray.Infrastructure.Navigation;

/// <summary>
/// Diagnostic events for the navigation system. Subscribe to these events
/// to observe navigation operations for debugging and telemetry.
/// All events fire synchronously on the UI thread.
/// </summary>
public static class NavigationDiagnostics
{
    /// <summary>Fires when a navigation is attempted (before guards run).</summary>
    public static event Action<NavigationDiagnosticEvent>? NavigationRequested;

    /// <summary>Fires when a navigation completes successfully.</summary>
    public static event Action<NavigationDiagnosticEvent>? NavigationCompleted;

    /// <summary>Fires when a navigation guard cancels a navigation.</summary>
    public static event Action<NavigationDiagnosticEvent>? NavigationCancelled;

    /// <summary>Fires on a cache hit during navigation.</summary>
    public static event Action<CacheDiagnosticEvent>? CacheHit;

    /// <summary>Fires on a cache miss during navigation.</summary>
    public static event Action<CacheDiagnosticEvent>? CacheMiss;

    /// <summary>Fires when a page is evicted from the cache.</summary>
    public static event Action<CacheDiagnosticEvent>? CacheEviction;

    /// <summary>Fires when a transition animation starts.</summary>
    public static event Action<TransitionDiagnosticEvent>? TransitionStarted;

    /// <summary>Fires when a transition animation completes.</summary>
    public static event Action<TransitionDiagnosticEvent>? TransitionCompleted;

    /// <summary>Fires when a deep link resolves (match or miss).</summary>
    public static event Action<DeepLinkDiagnosticEvent>? DeepLinkResolved;

    internal static void OnNavigationRequested(object from, object to, NavigationMode mode)
    {
        Debug.WriteLine($"[Reactor.Nav] Requested: {mode} from {from} → {to}");
        NavigationRequested?.Invoke(new(from, to, mode));
    }

    internal static void OnNavigationCompleted(object from, object to, NavigationMode mode)
    {
        Debug.WriteLine($"[Reactor.Nav] Completed: {mode} from {from} → {to}");
        NavigationCompleted?.Invoke(new(from, to, mode));
    }

    internal static void OnNavigationCancelled(object from, object to, NavigationMode mode, string reason)
    {
        Debug.WriteLine($"[Reactor.Nav] Cancelled: {mode} from {from} → {to} ({reason})");
        NavigationCancelled?.Invoke(new(from, to, mode) { Reason = reason });
    }

    internal static void OnCacheHit(object route)
    {
        Debug.WriteLine($"[Reactor.Nav] Cache HIT: {route}");
        CacheHit?.Invoke(new(route));
    }

    internal static void OnCacheMiss(object route)
    {
        Debug.WriteLine($"[Reactor.Nav] Cache MISS: {route}");
        CacheMiss?.Invoke(new(route));
    }

    internal static void OnCacheEviction(object route)
    {
        Debug.WriteLine($"[Reactor.Nav] Cache EVICT: {route}");
        CacheEviction?.Invoke(new(route));
    }

    internal static void OnTransitionStarted(NavigationTransition transition, NavigationMode mode)
    {
        Debug.WriteLine($"[Reactor.Nav] Transition START: {transition.GetType().Name} ({mode})");
        TransitionStarted?.Invoke(new(transition, mode));
    }

    internal static void OnTransitionCompleted(NavigationTransition transition, NavigationMode mode)
    {
        Debug.WriteLine($"[Reactor.Nav] Transition END: {transition.GetType().Name} ({mode})");
        TransitionCompleted?.Invoke(new(transition, mode));
    }

    internal static void OnDeepLinkResolved(string path, bool matched, int routeCount)
    {
        Debug.WriteLine($"[Reactor.Nav] DeepLink: '{path}' → {(matched ? $"matched ({routeCount} routes)" : "no match")}");
        DeepLinkResolved?.Invoke(new(path, matched, routeCount));
    }
}

public sealed class NavigationDiagnosticEvent(object from, object to, NavigationMode mode)
{
    public object From { get; } = from;
    public object To { get; } = to;
    public NavigationMode Mode { get; } = mode;
    public string? Reason { get; init; }
}

public sealed class CacheDiagnosticEvent(object route)
{
    public object Route { get; } = route;
}

public sealed class TransitionDiagnosticEvent(NavigationTransition transition, NavigationMode mode)
{
    public NavigationTransition Transition { get; } = transition;
    public NavigationMode Mode { get; } = mode;
}

public sealed class DeepLinkDiagnosticEvent(string path, bool matched, int routeCount)
{
    public string Path { get; } = path;
    public bool Matched { get; } = matched;
    public int RouteCount { get; } = routeCount;
}
