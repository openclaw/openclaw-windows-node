namespace OpenClawTray.Infrastructure.Navigation;

/// <summary>
/// Represents the direction/type of a navigation operation.
/// </summary>
public enum NavigationMode
{
    Push,
    Pop,
    Replace,
    Reset,
    Forward,
}

/// <summary>
/// Context passed to navigation guards before a navigation occurs.
/// Call <see cref="Cancel"/> to prevent the navigation.
/// </summary>
public sealed class NavigatingFromContext
{
    public object Route { get; }
    public object TargetRoute { get; }
    public NavigationMode Mode { get; }
    public bool IsCancelled { get; private set; }

    internal NavigatingFromContext(object route, object targetRoute, NavigationMode mode)
    {
        Route = route;
        TargetRoute = targetRoute;
        Mode = mode;
    }

    public void Cancel() => IsCancelled = true;
}

/// <summary>
/// Internal navigation stack managing back/forward history.
/// Pure data structure — no UI dependencies.
/// </summary>
internal sealed class NavigationStack<TRoute> where TRoute : notnull
{
    private readonly List<TRoute> _backStack = new();
    private TRoute _current;
    private readonly List<TRoute> _forwardStack = new();

    /// <summary>
    /// Creates a navigation stack with the given initial route.
    /// </summary>
    public NavigationStack(TRoute initial)
    {
        _current = initial;
    }

    /// <summary>The currently active route.</summary>
    public TRoute Current => _current;

    /// <summary>True if there are entries in the back stack to navigate to.</summary>
    public bool CanGoBack => _backStack.Count > 0;

    /// <summary>True if there are entries in the forward stack to navigate to.</summary>
    public bool CanGoForward => _forwardStack.Count > 0;

    /// <summary>Readonly view of the back stack (most recent entry last).</summary>
    public IReadOnlyList<TRoute> BackStack => _backStack;

    /// <summary>Readonly view of the forward stack (most recent entry last).</summary>
    public IReadOnlyList<TRoute> ForwardStack => _forwardStack;

    /// <summary>Total depth: back stack count + 1 (current).</summary>
    public int Depth => _backStack.Count + 1;

    /// <summary>
    /// Guard called before any mutation that changes the current route.
    /// Return false from the guard (via <see cref="NavigatingFromContext.Cancel"/>) to prevent the navigation.
    /// </summary>
    public Action<NavigatingFromContext>? Guard { get; set; }

    /// <summary>
    /// Callback invoked after every successful mutation. Used to trigger re-renders.
    /// </summary>
    public Action? OnChanged { get; set; }

    /// <summary>
    /// Lifecycle guard set by NavigationHost. Called before the programmatic <see cref="Guard"/>
    /// to invoke component-level <c>onNavigatingFrom</c> callbacks. Can cancel via
    /// <see cref="NavigatingFromContext.Cancel"/>.
    /// </summary>
    internal Action<NavigatingFromContext>? LifecycleGuard { get; set; }

    /// <summary>
    /// Detaches all delegates from this stack, breaking strong references
    /// to component render infrastructure. Called during unmount.
    /// </summary>
    internal void Detach()
    {
        OnChanged = null;
        Guard = null;
        LifecycleGuard = null;
    }

    /// <summary>
    /// Push a new route onto the stack. Current becomes back stack entry; forward stack is cleared.
    /// </summary>
    public bool Push(TRoute route)
    {
        if (!InvokeGuard(_current, route, NavigationMode.Push))
            return false;

        _backStack.Add(_current);
        _current = route;
        _forwardStack.Clear();
        OnChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Pop the current route and go back. Current moves to forward stack; back stack top becomes current.
    /// </summary>
    public bool Pop()
    {
        if (_backStack.Count == 0)
            return false;

        var target = _backStack[^1];
        if (!InvokeGuard(_current, target, NavigationMode.Pop))
            return false;

        _forwardStack.Add(_current);
        _current = target;
        _backStack.RemoveAt(_backStack.Count - 1);
        OnChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Go forward. Current moves to back stack; forward stack top becomes current.
    /// </summary>
    public bool Forward()
    {
        if (_forwardStack.Count == 0)
            return false;

        var target = _forwardStack[^1];
        if (!InvokeGuard(_current, target, NavigationMode.Forward))
            return false;

        _backStack.Add(_current);
        _current = target;
        _forwardStack.RemoveAt(_forwardStack.Count - 1);
        OnChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Replace the current route without touching back/forward stacks.
    /// </summary>
    public bool Replace(TRoute route)
    {
        if (!InvokeGuard(_current, route, NavigationMode.Replace))
            return false;

        _current = route;
        OnChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Reset the entire stack to a single route. Clears back and forward stacks.
    /// </summary>
    public bool Reset(TRoute route)
    {
        if (!InvokeGuard(_current, route, NavigationMode.Reset))
            return false;

        _backStack.Clear();
        _forwardStack.Clear();
        _current = route;
        OnChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Replaces the entire stack state. Used for deserialization and deep linking.
    /// Does NOT invoke guards. Fires OnChanged.
    /// </summary>
    internal void RestoreState(IList<TRoute> backStack, TRoute current, IList<TRoute> forwardStack)
    {
        _backStack.Clear();
        _backStack.AddRange(backStack);
        _current = current;
        _forwardStack.Clear();
        _forwardStack.AddRange(forwardStack);
        OnChanged?.Invoke();
    }

    /// <summary>
    /// Pop entries from back stack until predicate matches, collecting popped entries into forward stack.
    /// Returns false if no entry in the back stack matches.
    /// </summary>
    public bool PopTo(Func<TRoute, bool> predicate)
    {
        // Find the matching entry in the back stack (search from top/most recent).
        var matchIndex = -1;
        for (var i = _backStack.Count - 1; i >= 0; i--)
        {
            if (predicate(_backStack[i]))
            {
                matchIndex = i;
                break;
            }
        }

        if (matchIndex < 0)
            return false;

        var target = _backStack[matchIndex];
        if (!InvokeGuard(_current, target, NavigationMode.Pop))
            return false;

        // Move current to forward stack
        _forwardStack.Add(_current);

        // Move entries between match and top of back stack to forward stack (in order)
        for (var i = _backStack.Count - 1; i > matchIndex; i--)
        {
            _forwardStack.Add(_backStack[i]);
            _backStack.RemoveAt(i);
        }

        // Pop the matched entry to become current
        _current = _backStack[matchIndex];
        _backStack.RemoveAt(matchIndex);

        OnChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Invokes lifecycle guard then programmatic guard. Returns true if navigation should proceed.
    /// </summary>
    private bool InvokeGuard(TRoute from, TRoute to, NavigationMode mode)
    {
        if (Guard is null && LifecycleGuard is null)
            return true;

        var ctx = new NavigatingFromContext(from!, to!, mode);

        // Lifecycle hooks first (component-level onNavigatingFrom)
        LifecycleGuard?.Invoke(ctx);
        if (ctx.IsCancelled)
            return false;

        // Then programmatic guard
        Guard?.Invoke(ctx);
        return !ctx.IsCancelled;
    }
}
