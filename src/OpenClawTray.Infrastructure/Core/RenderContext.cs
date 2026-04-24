using System.Diagnostics.CodeAnalysis;
using OpenClawTray.Infrastructure.Hooks;

namespace OpenClawTray.Infrastructure.Core;

/// <summary>
/// Passed to function components and provides access to hooks.
/// Each component instance gets its own RenderContext which tracks hook call order.
/// </summary>
public sealed class RenderContext
{
    private readonly List<HookState> _hooks = new();
    private int _hookIndex;
    private Action? _requestRerender;
    private ContextScope? _contextScope;
    private int _uiThreadId;

    internal void BeginRender(Action requestRerender)
    {
        _hookIndex = 0;
        _requestRerender = requestRerender;
        _uiThreadId = Environment.CurrentManagedThreadId;
    }

    internal void BeginRender(Action requestRerender, ContextScope contextScope)
    {
        _hookIndex = 0;
        _requestRerender = requestRerender;
        _contextScope = contextScope;
        _uiThreadId = Environment.CurrentManagedThreadId;
    }

    [global::System.Diagnostics.Conditional("DEBUG")]
    private void AssertUIThread(string hookName)
    {
        if (Environment.CurrentManagedThreadId != _uiThreadId)
            throw new InvalidOperationException(
                $"{hookName} setter was called from thread {Environment.CurrentManagedThreadId}, " +
                $"but the UI thread is {_uiThreadId}. Use threadSafe: true to allow cross-thread calls.");
    }

    /// <summary>
    /// DEBUG ONLY: Directly set a UseState hook value by index and trigger re-render.
    /// Used for testing state changes without event handlers.
    /// </summary>
    internal void UseStateSetterByIndex<T>(int index, T newValue)
    {
        if (index < _hooks.Count && _hooks[index] is ValueHookState<T> hook)
        {
            hook.Value = newValue;
            _requestRerender?.Invoke();
        }
    }

    /// <summary>
    /// Declares a piece of state. Returns (currentValue, setter).
    /// Must be called in the same order every render (just like React hooks).
    /// When <paramref name="threadSafe"/> is true, the setter can be called from any thread
    /// and reads/writes are synchronized. When false (default), the setter must be called
    /// from the UI thread — in DEBUG builds, a cross-thread call throws.
    /// </summary>
    public (T Value, Action<T> Set) UseState<T>(T initialValue, bool threadSafe = false)
    {
        if (_hookIndex >= _hooks.Count)
        {
            _hooks.Add(new ValueHookState<T>(initialValue, threadSafe));
        }

        var currentIndex = _hookIndex;
        _hookIndex++;

        if (_hooks[currentIndex] is not ValueHookState<T> hook)
            throw new InvalidOperationException(
                $"Hook at index {currentIndex} is {_hooks[currentIndex].GetType().Name}, expected ValueHookState<{typeof(T).Name}> (UseState). " +
                "Hooks must be called in the same order every render.");

        T current;
        if (hook.ThreadSafe)
            lock (hook.Lock) { current = hook.Value; }
        else
            current = hook.Value;

        void Setter(T newValue)
        {
            var h = (ValueHookState<T>)_hooks[currentIndex];
            bool changed;
            if (h.ThreadSafe)
            {
                lock (h.Lock)
                {
                    changed = !EqualityComparer<T>.Default.Equals(h.Value, newValue);
                    if (changed) h.Value = newValue;
                }
                if (Diagnostics.ReactorEventSource.Log.IsEnabled(
                        global::System.Diagnostics.Tracing.EventLevel.Verbose,
                        Diagnostics.ReactorEventSource.Keywords.State))
                    Diagnostics.ReactorEventSource.Log.StateChange("UseState", typeof(T).Name, changed);
                if (changed) _requestRerender?.Invoke();
            }
            else
            {
                AssertUIThread("UseState");
                changed = !EqualityComparer<T>.Default.Equals(h.Value, newValue);
                if (changed) h.Value = newValue;
                if (Diagnostics.ReactorEventSource.Log.IsEnabled(
                        global::System.Diagnostics.Tracing.EventLevel.Verbose,
                        Diagnostics.ReactorEventSource.Keywords.State))
                    Diagnostics.ReactorEventSource.Log.StateChange("UseState", typeof(T).Name, changed);
                if (changed) _requestRerender?.Invoke();
            }
        }

        return (current, Setter);
    }

    /// <summary>
    /// Declares a piece of state with a functional updater variant.
    /// The updater receives the previous value and returns the next.
    /// When <paramref name="threadSafe"/> is true, the updater can be called from any thread.
    /// </summary>
    public (T Value, Action<Func<T, T>> Update) UseReducer<T>(T initialValue, bool threadSafe = false)
    {
        if (_hookIndex >= _hooks.Count)
        {
            _hooks.Add(new ValueHookState<T>(initialValue, threadSafe));
        }

        var currentIndex = _hookIndex;
        _hookIndex++;

        if (_hooks[currentIndex] is not ValueHookState<T> hook)
            throw new InvalidOperationException(
                $"Hook at index {currentIndex} is {_hooks[currentIndex].GetType().Name}, expected ValueHookState<{typeof(T).Name}> (UseReducer). " +
                "Hooks must be called in the same order every render.");

        T current;
        if (hook.ThreadSafe)
            lock (hook.Lock) { current = hook.Value; }
        else
            current = hook.Value;

        void Updater(Func<T, T> reducer)
        {
            var h = (ValueHookState<T>)_hooks[currentIndex];
            bool changed;
            if (h.ThreadSafe)
            {
                lock (h.Lock)
                {
                    var prev = h.Value;
                    var next = reducer(prev);
                    changed = !EqualityComparer<T>.Default.Equals(prev, next);
                    if (changed) h.Value = next;
                }
                if (Diagnostics.ReactorEventSource.Log.IsEnabled(
                        global::System.Diagnostics.Tracing.EventLevel.Verbose,
                        Diagnostics.ReactorEventSource.Keywords.State))
                    Diagnostics.ReactorEventSource.Log.StateChange("UseReducer", typeof(T).Name, changed);
                if (changed) _requestRerender?.Invoke();
            }
            else
            {
                AssertUIThread("UseReducer");
                var prev = h.Value;
                var next = reducer(prev);
                changed = !EqualityComparer<T>.Default.Equals(prev, next);
                if (changed) h.Value = next;
                if (Diagnostics.ReactorEventSource.Log.IsEnabled(
                        global::System.Diagnostics.Tracing.EventLevel.Verbose,
                        Diagnostics.ReactorEventSource.Keywords.State))
                    Diagnostics.ReactorEventSource.Log.StateChange("UseReducer", typeof(T).Name, changed);
                if (changed) _requestRerender?.Invoke();
            }
        }

        return (current, Updater);
    }

    /// <summary>
    /// Declares a piece of state managed by a reducer function (like Redux).
    /// The reducer takes (currentState, action) and returns the next state.
    /// Returns (currentState, dispatch) where dispatch sends an action through the reducer.
    /// When <paramref name="threadSafe"/> is true, dispatch can be called from any thread.
    /// </summary>
    public (TState Value, Action<TAction> Dispatch) UseReducer<TState, TAction>(
        Func<TState, TAction, TState> reducer, TState initialValue, bool threadSafe = false)
    {
        if (_hookIndex >= _hooks.Count)
        {
            _hooks.Add(new ValueHookState<TState>(initialValue, threadSafe));
        }

        var currentIndex = _hookIndex;
        _hookIndex++;

        if (_hooks[currentIndex] is not ValueHookState<TState> hook)
            throw new InvalidOperationException(
                $"Hook at index {currentIndex} is {_hooks[currentIndex].GetType().Name}, expected ValueHookState<{typeof(TState).Name}> (UseReducer). " +
                "Hooks must be called in the same order every render.");

        TState current;
        if (hook.ThreadSafe)
            lock (hook.Lock) { current = hook.Value; }
        else
            current = hook.Value;

        void Dispatch(TAction action)
        {
            var h = (ValueHookState<TState>)_hooks[currentIndex];
            if (h.ThreadSafe)
            {
                bool changed;
                lock (h.Lock)
                {
                    var prev = h.Value;
                    var next = reducer(prev, action);
                    changed = !EqualityComparer<TState>.Default.Equals(prev, next);
                    if (changed) h.Value = next;
                }
                if (changed) _requestRerender?.Invoke();
            }
            else
            {
                AssertUIThread("UseReducer");
                var prev = h.Value;
                var next = reducer(prev, action);
                if (!EqualityComparer<TState>.Default.Equals(prev, next))
                {
                    h.Value = next;
                    _requestRerender?.Invoke();
                }
            }
        }

        return (current, Dispatch);
    }

    /// <summary>
    /// Runs a side effect after render. The effect re-runs when any dependency changes.
    /// Pass an empty array for "run once on mount" semantics.
    /// Returns a cleanup action that runs before the next effect or on unmount.
    /// </summary>
    public void UseEffect(Action effect, params object[] dependencies)
    {
        if (_hookIndex >= _hooks.Count)
        {
            _hooks.Add(new EffectHookState { Dependencies = null, Effect = effect });
        }

        if (_hooks[_hookIndex] is not EffectHookState hook)
            throw new InvalidOperationException(
                $"Hook at index {_hookIndex} is {_hooks[_hookIndex].GetType().Name}, expected EffectHookState. " +
                "Hooks must be called in the same order every render.");
        _hookIndex++;

        if (hook.Dependencies is null || !DepsEqual(hook.Dependencies, dependencies))
        {
            hook.PendingCleanup = hook.Cleanup;
            hook.Cleanup = null;
            hook.Dependencies = dependencies.ToArray();
            hook.Effect = effect;
            hook.Pending = true;
        }
    }

    /// <summary>
    /// Like UseEffect but the effect returns a cleanup function.
    /// </summary>
    public void UseEffect(Func<Action> effectWithCleanup, params object[] dependencies)
    {
        if (_hookIndex >= _hooks.Count)
        {
            _hooks.Add(new EffectHookState { Dependencies = null });
        }

        if (_hooks[_hookIndex] is not EffectHookState hook)
            throw new InvalidOperationException(
                $"Hook at index {_hookIndex} is {_hooks[_hookIndex].GetType().Name}, expected EffectHookState. " +
                "Hooks must be called in the same order every render.");
        _hookIndex++;

        if (hook.Dependencies is null || !DepsEqual(hook.Dependencies, dependencies))
        {
            hook.PendingCleanup = hook.Cleanup;
            hook.Cleanup = null;
            hook.Dependencies = dependencies.ToArray();
            hook.EffectWithCleanup = effectWithCleanup;
            hook.Pending = true;
        }
    }

    /// <summary>
    /// Memoizes a computed value, recomputing only when dependencies change.
    /// </summary>
    public T UseMemo<T>(Func<T> factory, params object[] dependencies)
    {
        if (_hookIndex >= _hooks.Count)
        {
            _hooks.Add(new MemoHookState<T> { Dependencies = null });
        }

        if (_hooks[_hookIndex] is not MemoHookState<T> hook)
            throw new InvalidOperationException(
                $"Hook at index {_hookIndex} is {_hooks[_hookIndex].GetType().Name}, expected MemoHookState<{typeof(T).Name}>. " +
                "Hooks must be called in the same order every render.");
        _hookIndex++;

        if (hook.Dependencies is null || !DepsEqual(hook.Dependencies, dependencies))
        {
            hook.Value = factory();
            hook.Dependencies = dependencies.ToArray();
        }

        return hook.Value;
    }

    /// <summary>
    /// Returns a stable callback reference that doesn't change between renders.
    /// </summary>
    public Action UseCallback(Action callback, params object[] dependencies)
    {
        return UseMemo(() => callback, dependencies);
    }

    /// <summary>
    /// Returns a mutable ref object that persists across renders.
    /// </summary>
    public Ref<T> UseRef<T>(T initialValue = default!)
    {
        if (_hookIndex >= _hooks.Count)
        {
            _hooks.Add(new ValueHookState<Ref<T>>(new Ref<T>(initialValue)));
        }

        var currentIndex = _hookIndex;
        _hookIndex++;

        if (_hooks[currentIndex] is not ValueHookState<Ref<T>> hook)
            throw new InvalidOperationException(
                $"Hook at index {currentIndex} expected ValueHookState<Ref<{typeof(T).Name}>>, got {_hooks[currentIndex].GetType().Name}. " +
                "Hooks must be called in the same order every render.");
        return hook.Value;
    }

    // ════════════════════════════════════════════════════════════════
    //  Persisted state hooks
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Like UseState, but the value survives unmount/remount via an in-memory cache.
    /// On first mount, uses cached value if available, otherwise uses initialValue.
    /// Value is saved to cache on unmount.
    /// </summary>
    public (T Value, Action<T> Set) UsePersisted<T>(string key, T initialValue)
    {
        if (_hookIndex >= _hooks.Count)
        {
            T initial = PersistedStateCache.TryGet<T>(key, out var cached) ? cached : initialValue;
            _hooks.Add(new PersistedHookState<T>(initial) { PersistKey = key });
        }

        var currentIndex = _hookIndex;
        _hookIndex++;

        if (_hooks[currentIndex] is not PersistedHookState<T> hook)
            throw new InvalidOperationException(
                $"Hook at index {currentIndex} is {_hooks[currentIndex].GetType().Name}, expected PersistedHookState<{typeof(T).Name}> (UsePersisted). " +
                "Hooks must be called in the same order every render.");

        T current = hook.Value;

        void Setter(T newValue)
        {
            var h = (PersistedHookState<T>)_hooks[currentIndex];
            if (!EqualityComparer<T>.Default.Equals(h.Value, newValue))
            {
                h.Value = newValue;
                _requestRerender?.Invoke();
            }
        }

        return (current, Setter);
    }

    // ════════════════════════════════════════════════════════════════
    //  Observable interop hooks
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Observes an object and all nested INotifyPropertyChanged values
    /// reachable through its properties. Re-renders when any property
    /// at any depth changes. Automatically subscribes/unsubscribes as
    /// property values change.
    /// </summary>
    public T UseObservableTree<T>(T source) where T : global::System.ComponentModel.INotifyPropertyChanged
    {
        var (_, forceRender) = UseReducer(false);
        var trackerRef = UseRef<ObservableTreeTracker?>(null);

        UseEffect(() =>
        {
            var tracker = new ObservableTreeTracker(() => forceRender(v => !v));
            trackerRef.Current = tracker;
            tracker.SyncSubscriptions(source);
            return () => tracker.Dispose();
        }, source);

        return source;
    }

    /// <summary>
    /// Subscribes to an INotifyPropertyChanged source and re-renders when any property changes.
    /// Returns the same source object.
    /// </summary>
    public T UseObservable<T>(T source) where T : global::System.ComponentModel.INotifyPropertyChanged
    {
        var (_, forceRender) = UseReducer(false);
        UseEffect(() =>
        {
            void handler(object? s, global::System.ComponentModel.PropertyChangedEventArgs e)
                => forceRender(v => !v);
            source.PropertyChanged += handler;
            return () => source.PropertyChanged -= handler;
        }, source);
        return source;
    }

    /// <summary>
    /// Subscribes to a specific property on an INotifyPropertyChanged source.
    /// Re-renders only when that property changes.
    /// </summary>
    public TProp UseObservableProperty<T, TProp>(T source, Func<T, TProp> selector, string propertyName)
        where T : global::System.ComponentModel.INotifyPropertyChanged
    {
        var (_, forceRender) = UseReducer(false);
        UseEffect(() =>
        {
            void handler(object? s, global::System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == propertyName || string.IsNullOrEmpty(e.PropertyName))
                    forceRender(v => !v);
            }
            source.PropertyChanged += handler;
            return () => source.PropertyChanged -= handler;
        }, source, propertyName);
        return selector(source);
    }

    /// <summary>
    /// Subscribes to an ObservableCollection and re-renders on Add/Remove/Reset.
    /// Returns the collection as IReadOnlyList.
    /// </summary>
    public IReadOnlyList<T> UseCollection<T>(global::System.Collections.ObjectModel.ObservableCollection<T> collection)
    {
        var (_, forceRender) = UseReducer(false);
        UseEffect(() =>
        {
            void handler(object? s, global::System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
                => forceRender(v => !v);
            collection.CollectionChanged += handler;
            return () => collection.CollectionChanged -= handler;
        }, collection);
        return collection;
    }

    // ════════════════════════════════════════════════════════════════
    //  Navigation hooks
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Root mode: creates a navigation stack with the given initial route.
    /// Returns a stable <see cref="Navigation.NavigationHandle{TRoute}"/> across re-renders.
    /// Wire this handle to a <c>NavigationHost</c> in the DSL to render route content.
    /// The handle is automatically provided to descendants via context so child components
    /// can call <c>UseNavigation&lt;TRoute&gt;()</c> (parameterless) to access it.
    /// </summary>
    public Navigation.NavigationHandle<TRoute> UseNavigation<TRoute>(TRoute initial) where TRoute : notnull
    {
        var stackRef = UseRef<Navigation.NavigationStack<TRoute>?>(null);
        if (stackRef.Current is null)
            stackRef.Current = new Navigation.NavigationStack<TRoute>(initial);

        var handleRef = UseRef<Navigation.NavigationHandle<TRoute>?>(null);
        if (handleRef.Current is null)
            handleRef.Current = new Navigation.NavigationHandle<TRoute>(stackRef.Current);

        // Capture the latest rerender callback every render so navigation mutations
        // that originate from event handlers always trigger a re-render of this component.
        stackRef.Current.OnChanged = _requestRerender;

        return handleRef.Current;
    }

    /// <summary>
    /// Child mode: retrieves an ancestor's <see cref="Navigation.NavigationHandle{TRoute}"/>
    /// from context. Throws if no ancestor provides one (i.e., no root <c>UseNavigation</c>
    /// with a <c>NavigationHost</c> exists above this component in the tree).
    /// </summary>
    public Navigation.NavigationHandle<TRoute> UseNavigation<TRoute>() where TRoute : notnull
    {
        var handle = UseContext(Navigation.NavigationContext<TRoute>.Instance);
        if (handle is null)
            throw new InvalidOperationException(
                $"UseNavigation<{typeof(TRoute).Name}>() (child mode) found no ancestor NavigationHost " +
                $"providing NavigationContext<{typeof(TRoute).Name}>. " +
                "Ensure a parent component calls UseNavigation<T>(initialRoute) and renders a NavigationHost.");
        return handle;
    }

    // ════════════════════════════════════════════════════════════════
    //  Navigation system back button
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Subscribes to Alt+Left and VirtualKey.GoBack keyboard events on the given window's content
    /// to call <see cref="Navigation.NavigationHandle{TRoute}.GoBack"/>. Unsubscribes on unmount.
    /// </summary>
    public void UseSystemBackButton<TRoute>(
        Navigation.NavigationHandle<TRoute> nav,
        Microsoft.UI.Xaml.Window window) where TRoute : notnull
    {
        UseEffect(() =>
        {
            void handler(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
            {
                if (e.Key == global::Windows.System.VirtualKey.GoBack ||
                    (e.Key == global::Windows.System.VirtualKey.Left &&
                     Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Menu)
                         .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down)))
                {
                    if (nav.CanGoBack)
                    {
                        nav.GoBack();
                        e.Handled = true;
                    }
                }
            }

            if (window.Content is Microsoft.UI.Xaml.UIElement rootElement)
            {
                rootElement.KeyDown += handler;
                return () => rootElement.KeyDown -= handler;
            }
            return () => { };
        }, nav, window);
    }

    // ════════════════════════════════════════════════════════════════
    //  Navigation lifecycle hooks
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Registers lifecycle callbacks that fire during navigation events.
    /// <list type="bullet">
    /// <item><c>onNavigatedTo</c> — fires after this page becomes active.</item>
    /// <item><c>onNavigatingFrom</c> — fires before navigating away. Call <c>ctx.Cancel()</c> to block.</item>
    /// <item><c>onNavigatedFrom</c> — fires after this page is no longer active.</item>
    /// </list>
    /// Callbacks are always updated to the latest references on every render.
    /// </summary>
    public void UseNavigationLifecycle(
        Action<Navigation.NavigatingToContext>? onNavigatingTo = null,
        Action<Navigation.NavigatedToContext>? onNavigatedTo = null,
        Action<Navigation.NavigatingFromContext>? onNavigatingFrom = null,
        Action<Navigation.NavigatedFromContext>? onNavigatedFrom = null)
    {
        if (_hookIndex >= _hooks.Count)
        {
            _hooks.Add(new NavigationLifecycleHookState());
        }

        if (_hooks[_hookIndex] is not NavigationLifecycleHookState hook)
            throw new InvalidOperationException(
                $"Hook at index {_hookIndex} is {_hooks[_hookIndex].GetType().Name}, expected NavigationLifecycleHookState. " +
                "Hooks must be called in the same order every render.");
        _hookIndex++;

        // Always update to latest callbacks so closures capture current state
        hook.OnNavigatingTo = onNavigatingTo;
        hook.OnNavigatedTo = onNavigatedTo;
        hook.OnNavigatingFrom = onNavigatingFrom;
        hook.OnNavigatedFrom = onNavigatedFrom;
    }

    /// <summary>
    /// Returns the navigation lifecycle hook state if one was registered, or null.
    /// Used by the reconciler to collect lifecycle callbacks from a component tree.
    /// </summary>
    internal NavigationLifecycleHookState? GetNavigationLifecycleHook()
    {
        for (int i = 0; i < _hooks.Count; i++)
        {
            if (_hooks[i] is NavigationLifecycleHookState hook)
                return hook;
        }
        return null;
    }

    // ════════════════════════════════════════════════════════════════
    //  Context hooks
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads the nearest ancestor's provided value for the given context.
    /// Returns the context's DefaultValue if no provider exists in the ancestor chain.
    /// Follows hook rules — must be called in the same order every render.
    /// </summary>
    public T UseContext<T>(Context<T> context)
    {
        if (_hookIndex >= _hooks.Count)
        {
            _hooks.Add(new ContextHookState { Context = context });
        }

        if (_hooks[_hookIndex] is not ContextHookState hook)
            throw new InvalidOperationException(
                $"Hook at index {_hookIndex} is {_hooks[_hookIndex].GetType().Name}, expected ContextHookState (UseContext). " +
                "Hooks must be called in the same order every render.");
        _hookIndex++;

        var value = _contextScope is not null
            ? _contextScope.Read(context)
            : context.DefaultValue;
        hook.LastValue = value;
        return value;
    }

    /// <summary>
    /// Enumerates ContextHookState entries for memo change detection (Phase 3).
    /// </summary>
    internal IEnumerable<ContextHookState> ContextHooks
    {
        get
        {
            for (int i = 0; i < _hooks.Count; i++)
            {
                if (_hooks[i] is ContextHookState ctx)
                    yield return ctx;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Color scheme hooks
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the effective <see cref="ColorScheme"/> at this component's
    /// position in the tree. Automatically reflects the current system theme,
    /// per-element <c>RequestedTheme</c> overrides, and High Contrast mode.
    /// <para>
    /// The value is re-evaluated on every render — when the theme changes,
    /// <see cref="OpenClawTray.Infrastructure.Hosting.ReactorHost"/> triggers a re-render so this hook
    /// naturally picks up the new value.
    /// </para>
    /// </summary>
    public ColorScheme UseColorScheme()
    {
        // Read effective theme from the application. On re-render after theme
        // change, this returns the updated value. Components inside a
        // RequestedTheme(Dark) subtree see the correct variant because the
        // FrameworkElement.ActualTheme is read at reconcile time.
        var theme = Microsoft.UI.Xaml.Application.Current?.RequestedTheme;
        var elementTheme = theme switch
        {
            Microsoft.UI.Xaml.ApplicationTheme.Dark => Microsoft.UI.Xaml.ElementTheme.Dark,
            Microsoft.UI.Xaml.ApplicationTheme.Light => Microsoft.UI.Xaml.ElementTheme.Light,
            _ => Microsoft.UI.Xaml.ElementTheme.Default,
        };
        return ColorSchemeContext.FromActualTheme(elementTheme);
    }

    /// <summary>
    /// Convenience wrapper — returns <c>true</c> when the effective color
    /// scheme is <see cref="ColorScheme.Dark"/>.
    /// </summary>
    public bool UseIsDarkTheme() => UseColorScheme() == ColorScheme.Dark;

    // ════════════════════════════════════════════════════════════════
    //  High contrast / accessibility display hooks
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns <c>true</c> when the system is in a High Contrast (forced colors) theme.
    /// Automatically re-renders the component when high contrast is toggled.
    /// <para>
    /// Use this to conditionally override custom styling (hardcoded backgrounds,
    /// foregrounds, border colors) that would ignore forced-colors mode.
    /// WinUI controls using ThemeResource brushes adapt automatically — this hook
    /// is for Reactor components that use explicit color values.
    /// </para>
    /// </summary>
    public bool UseHighContrast() => UseHighContrastState().IsHighContrast;

    /// <summary>
    /// Returns the high contrast scheme name (e.g., "High Contrast Black",
    /// "High Contrast White") or <c>null</c> if not in high contrast mode.
    /// Automatically re-renders the component when the scheme changes.
    /// <para>
    /// Must be called instead of (not in addition to) <see cref="UseHighContrast"/>
    /// because each consumes the same hook slots. Use one or the other.
    /// </para>
    /// </summary>
    public string? UseHighContrastScheme() => UseHighContrastState().HighContrastScheme;

    private HighContrastState UseHighContrastState()
    {
        var (state, _) = UseState(new HighContrastState());

        // Subscribe once to HighContrastChanged and re-render when it fires.
        UseEffect(() =>
        {
            state.Settings ??= new global::Windows.UI.ViewManagement.AccessibilitySettings();
            var rerender = _requestRerender;
            void OnChanged(global::Windows.UI.ViewManagement.AccessibilitySettings sender, object args)
            {
                state.IsHighContrast = sender.HighContrast;
                state.HighContrastScheme = sender.HighContrast ? sender.HighContrastScheme : null;
                rerender?.Invoke();
            }
            state.Settings.HighContrastChanged += OnChanged;
            // Sync initial value
            state.IsHighContrast = state.Settings.HighContrast;
            state.HighContrastScheme = state.Settings.HighContrast ? state.Settings.HighContrastScheme : null;
            return () => state.Settings.HighContrastChanged -= OnChanged;
        });

        return state;
    }

    private sealed class HighContrastState
    {
        public global::Windows.UI.ViewManagement.AccessibilitySettings? Settings;
        public bool IsHighContrast;
        public string? HighContrastScheme;
    }

    // ════════════════════════════════════════════════════════════════
    //  Reduced-motion hook
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns <c>true</c> when the user or system prefers reduced motion
    /// (e.g., Windows "Show animations" is off, or <c>SPI_GETCLIENTAREAANIMATION</c>
    /// returns false). Automatically re-renders the component when the preference changes.
    /// <para>
    /// Use this to skip entrance/exit animations, disable pan inertia, terminate
    /// force-graph simulations immediately, and keep only ≤ 150 ms opacity fades
    /// (WCAG 2.3.3).
    /// </para>
    /// </summary>
    public bool UseReducedMotion() => UseReducedMotionState().IsReducedMotion;

    private ReducedMotionState UseReducedMotionState()
    {
        var (state, _) = UseState(new ReducedMotionState());

        UseEffect(() =>
        {
            state.Settings ??= new global::Windows.UI.ViewManagement.UISettings();
            var rerender = _requestRerender;
            void OnChanged(global::Windows.UI.ViewManagement.UISettings sender, object args)
            {
                state.IsReducedMotion = !sender.AnimationsEnabled;
                rerender?.Invoke();
            }
            // UISettings.ColorValuesChanged also fires for AnimationsEnabled changes
            state.Settings.ColorValuesChanged += OnChanged;
            state.IsReducedMotion = !state.Settings.AnimationsEnabled;
            return () =>
            {
                if (state.Settings is not null)
                    state.Settings.ColorValuesChanged -= OnChanged;
            };
        });

        return state;
    }

    private sealed class ReducedMotionState
    {
        public global::Windows.UI.ViewManagement.UISettings? Settings;
        public bool IsReducedMotion;
    }

    // ════════════════════════════════════════════════════════════════
    //  Localization hooks
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns an IntlAccessor for the current locale. Re-renders this component
    /// when the locale changes via a parent LocaleProvider.
    /// If no LocaleProvider is present, returns a default accessor using the OS locale.
    /// Uses Context internally — the context system handles re-renders automatically.
    /// </summary>
    public Localization.IntlAccessor UseIntl()
    {
        var contextAccessor = UseContext(Localization.IntlContexts.Locale);
        return contextAccessor ?? _defaultAccessor.Value;
    }

    private static readonly Lazy<Localization.IntlAccessor> _defaultAccessor = new(() =>
    {
        var osLocale = global::System.Globalization.CultureInfo.CurrentUICulture.Name;
        if (string.IsNullOrEmpty(osLocale)) osLocale = "en-US";
        var cache = new Localization.MessageCache();
        var provider = new Localization.ReswResourceProvider(osLocale);
        return new Localization.IntlAccessor(osLocale, provider, cache, osLocale);
    });

    // ════════════════════════════════════════════════════════════════
    //  Command hooks
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Processes a Command for use in a component. For sync-only commands, returns
    /// the command unchanged (no hook slots consumed). For async commands, wraps ExecuteAsync
    /// with automatic IsExecuting tracking and re-entrance guards. The returned command
    /// always has a sync Execute action and ExecuteAsync = null.
    /// </summary>
    public Command UseCommand(Command command)
    {
        // Sync-only commands pass through unchanged — no hooks consumed
        if (command.ExecuteAsync is null)
            return command;

        var (isExecuting, setIsExecuting) = UseState(false, threadSafe: true);
        var guardRef = UseRef(false);
        var asyncAction = command.ExecuteAsync;

        var wrappedExecute = UseMemo<Action>(() => () =>
        {
            // Re-entrance guard using ref for live value (not stale closure capture)
            if (guardRef.Current) return;
            guardRef.Current = true;
            setIsExecuting(true);
            _ = Task.Run(async () =>
            {
                try
                {
                    await asyncAction();
                }
                catch (Exception ex)
                {
                    global::System.Diagnostics.Debug.WriteLine($"[Reactor] UseCommand async action threw: {ex}");
                }
                finally
                {
                    guardRef.Current = false;
                    setIsExecuting(false);
                }
            });
        }, command.ExecuteAsync);

        return command with { Execute = wrappedExecute, ExecuteAsync = null, IsExecuting = isExecuting };
    }

    /// <summary>
    /// Processes a parameterized Command for use in a component. For sync-only commands,
    /// returns unchanged. For async commands, wraps ExecuteAsync with IsExecuting tracking
    /// and re-entrance guards.
    /// </summary>
    public Command<T> UseCommand<T>(Command<T> command)
    {
        if (command.ExecuteAsync is null)
            return command;

        var (isExecuting, setIsExecuting) = UseState(false, threadSafe: true);
        var guardRef = UseRef(false);
        var asyncAction = command.ExecuteAsync;

        var wrappedExecute = UseMemo<Action<T>>(() => (arg) =>
        {
            // Re-entrance guard using ref for live value (not stale closure capture)
            if (guardRef.Current) return;
            guardRef.Current = true;
            setIsExecuting(true);
            _ = Task.Run(async () =>
            {
                try
                {
                    await asyncAction(arg);
                }
                catch (Exception ex)
                {
                    global::System.Diagnostics.Debug.WriteLine($"[Reactor] UseCommand<{typeof(T).Name}> async action threw: {ex}");
                }
                finally
                {
                    guardRef.Current = false;
                    setIsExecuting(false);
                }
            });
        }, command.ExecuteAsync);

        return command with { Execute = wrappedExecute, ExecuteAsync = null, IsExecuting = isExecuting };
    }

    // ════════════════════════════════════════════════════════════════
    //  Responsive layout hooks
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns (width, height) of the given window and re-renders when the window resizes.
    /// </summary>
    public (double Width, double Height) UseWindowSize(Microsoft.UI.Xaml.Window window)
    {
        var (size, setSize) = UseState((window.Bounds.Width, window.Bounds.Height));

        UseEffect(() =>
        {
            void handler(object sender, Microsoft.UI.Xaml.WindowSizeChangedEventArgs args)
            {
                setSize((args.Size.Width, args.Size.Height));
            }
            window.SizeChanged += handler;
            return () => window.SizeChanged -= handler;
        }, window);

        return size;
    }

    /// <summary>
    /// Returns true when the given window's width is >= minWidth.
    /// Re-renders when the window resizes across the breakpoint.
    /// </summary>
    public bool UseBreakpoint(Microsoft.UI.Xaml.Window window, double minWidth)
    {
        var (width, _) = UseWindowSize(window);
        return width >= minWidth;
    }

    internal void FlushEffects()
    {
        // Phase 1: Run all pending cleanups from previous effects
        for (int i = 0; i < _hooks.Count; i++)
        {
            if (_hooks[i] is EffectHookState hook && hook.PendingCleanup is not null)
            {
                try
                {
                    hook.PendingCleanup();
                }
                catch (Exception ex)
                {
                    global::System.Diagnostics.Debug.WriteLine($"[Reactor] Effect cleanup at index {i} threw: {ex}");
                }
                hook.PendingCleanup = null;
            }
        }

        // Phase 2: Run all pending new effects
        for (int i = 0; i < _hooks.Count; i++)
        {
            if (_hooks[i] is not EffectHookState hook || !hook.Pending) continue;
            hook.Pending = false;

            try
            {
                if (hook.EffectWithCleanup is not null)
                {
                    hook.Cleanup = hook.EffectWithCleanup();
                    hook.EffectWithCleanup = null;
                }
                else if (hook.Effect is not null)
                {
                    hook.Effect();
                    hook.Effect = null;
                }
            }
            catch (Exception ex)
            {
                global::System.Diagnostics.Debug.WriteLine($"[Reactor] Effect at index {i} threw: {ex}");
            }
        }
    }

    internal void RunCleanups()
    {
        // Phase 1: Run effect cleanups
        for (int i = 0; i < _hooks.Count; i++)
        {
            if (_hooks[i] is EffectHookState hook)
            {
                try
                {
                    hook.Cleanup?.Invoke();
                }
                catch (Exception ex)
                {
                    global::System.Diagnostics.Debug.WriteLine($"[Reactor] Cleanup at index {i} threw: {ex}");
                }
            }
        }

        // Phase 2: Save persisted state to cache
        for (int i = 0; i < _hooks.Count; i++)
        {
            if (_hooks[i] is PersistedHookStateBase persisted)
            {
                try
                {
                    persisted.SaveToCache();
                }
                catch (Exception ex)
                {
                    global::System.Diagnostics.Debug.WriteLine($"[Reactor] Persisted state save at index {i} threw: {ex}");
                }
            }
        }
    }

    private static bool DepsEqual(object[] prev, object[] next)
    {
        if (prev.Length != next.Length) return false;
        for (int i = 0; i < prev.Length; i++)
        {
            if (!Equals(prev[i], next[i])) return false;
        }
        return true;
    }

    /// <summary>
    /// Devtools-only: returns a snapshot of this context's hook table for
    /// <c>reactor.state</c>. Private hook-cell types are unpacked here where
    /// we have access; devtools code consumes the boxed values and does the
    /// JSON shaping. Must be called on the UI dispatcher.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "SnapshotHooks uses reflection on internal hook state types.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "SnapshotHooks uses reflection on internal hook state types.")]
    internal IReadOnlyList<HookSnapshot> SnapshotHooks()
    {
        var list = new List<HookSnapshot>(_hooks.Count);
        for (int i = 0; i < _hooks.Count; i++)
        {
            var h = _hooks[i];
            var t = h.GetType();
            string hookName;
            Type? valueType = null;
            object? value = null;

            if (t.IsGenericType)
            {
                var def = t.GetGenericTypeDefinition();
                if (def == typeof(ValueHookState<>))
                {
                    valueType = t.GetGenericArguments()[0];
                    value = t.GetField("Value")!.GetValue(h);
                    // UseRef uses the same cell, but its value is a Ref<T>.
                    hookName = valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(Ref<>)
                        ? "useRef"
                        : "useState";
                }
                else if (def == typeof(MemoHookState<>))
                {
                    valueType = t.GetGenericArguments()[0];
                    value = t.GetField("Value")!.GetValue(h);
                    hookName = "useMemo";
                }
                else if (def == typeof(PersistedHookState<>))
                {
                    valueType = t.GetGenericArguments()[0];
                    value = t.GetField("Value")!.GetValue(h);
                    hookName = "usePersisted";
                }
                else
                {
                    hookName = t.Name;
                }
            }
            else if (h is EffectHookState)
            {
                hookName = "useEffect";
            }
            else if (h is ContextHookState ch)
            {
                hookName = "useContext";
                value = ch.LastValue;
                valueType = value?.GetType();
            }
            else if (h is NavigationLifecycleHookState)
            {
                hookName = "useNavigationLifecycle";
            }
            else
            {
                hookName = t.Name;
            }

            list.Add(new HookSnapshot(i, hookName, valueType, value));
        }
        return list;
    }

    internal abstract class HookState { }

    private class ValueHookState<T> : HookState
    {
        public T Value;
        public readonly bool ThreadSafe;
        public readonly object Lock = new();
        public ValueHookState(T value, bool threadSafe = false)
        {
            Value = value;
            ThreadSafe = threadSafe;
        }
    }

    private class EffectHookState : HookState
    {
        public object[]? Dependencies;
        public Action? Effect;
        public Func<Action>? EffectWithCleanup;
        public Action? Cleanup;
        public Action? PendingCleanup;
        public bool Pending;
    }

    private class MemoHookState<T> : HookState
    {
        public T Value = default!;
        public object[]? Dependencies;
    }

    internal class ContextHookState : HookState
    {
        public ContextBase Context = default!;
        public object? LastValue;
    }

    internal class NavigationLifecycleHookState : HookState
    {
        public Action<Navigation.NavigatingToContext>? OnNavigatingTo;
        public Action<Navigation.NavigatedToContext>? OnNavigatedTo;
        public Action<Navigation.NavigatingFromContext>? OnNavigatingFrom;
        public Action<Navigation.NavigatedFromContext>? OnNavigatedFrom;
    }

    internal abstract class PersistedHookStateBase : HookState
    {
        public string PersistKey = default!;
        public abstract void SaveToCache();
    }

    private class PersistedHookState<T> : PersistedHookStateBase
    {
        public T Value;
        public PersistedHookState(T value) => Value = value;
        public override void SaveToCache() => PersistedStateCache.Set(PersistKey, Value);
    }
}

/// <summary>
/// Per-slot snapshot of a <see cref="RenderContext"/>'s hook table, produced by
/// <see cref="RenderContext.SnapshotHooks"/> for devtools inspection. The
/// <c>Value</c> is the live boxed hook value; serialization shaping happens in
/// the devtools state tool.
/// </summary>
internal readonly record struct HookSnapshot(int Index, string Hook, Type? ValueType, object? Value);

/// <summary>
/// A mutable reference that persists across renders (like React's useRef).
/// </summary>
public class Ref<T>
{
    public T Current { get; set; }
    public Ref(T initial) => Current = initial;
}
