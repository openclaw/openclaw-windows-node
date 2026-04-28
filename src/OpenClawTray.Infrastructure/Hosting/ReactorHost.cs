using System.Diagnostics;
using OpenClawTray.Infrastructure.Animation;
using OpenClawTray.Infrastructure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinUI = Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Infrastructure.Hosting;

/// <summary>
/// Hosts a Reactor component tree inside a WinUI Window.
/// Manages the render loop: when state changes, re-renders the component
/// and reconciles the virtual tree against the real WinUI control tree.
/// </summary>
public sealed class ReactorHost : IDisposable
{
#pragma warning disable CS0414 // Design constant for render-loop limiting; wiring pending
    private static readonly int MaxRenderIterations = 50;
#pragma warning restore CS0414

    private readonly Window _window;
    private readonly Reconciler _reconciler;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger _logger;

    private Component? _rootComponent;
    private Func<RenderContext, Element>? _rootRenderFunc;
    private RenderContext? _funcContext;

    private Element? _currentTree;
    private UIElement? _currentControl;
    private int _renderPending;    // 0 or 1 — Interlocked for thread-safe access
    private volatile bool _isRendering;     // only touched on UI thread
    private volatile bool _needsRerender;   // only touched on UI thread
    private FrameworkElement? _themeListenerElement;
    private volatile bool _disposed;
    private readonly global::Windows.Foundation.TypedEventHandler<object, WindowEventArgs> _closedHandler;

    // Accessibility: forced-colors and reduced-motion auto-propagation
    private global::Windows.UI.ViewManagement.AccessibilitySettings? _accessibilitySettings;
    private global::Windows.UI.ViewManagement.UISettings? _uiSettings;
    private volatile bool _isForcedColors;
    private volatile bool _isReducedMotion;

    // Captured AnimationScope curve — when a state setter is called inside
    // WithAnimation, the scope is synchronous but the render is async.
    // We capture the curve here so the reconcile pass can restore it.
    private Curve? _pendingAnimationCurve;

    // ── Reconcile highlight overlay (gated by ReactorFeatureFlags.HighlightReconcileChanges) ──
    private HighlightOverlayWiring? _highlightWiring;

    // Render phase timing instrumentation
    private readonly Stopwatch _phaseSw = new();
    private double _treeBuildSum;
    private double _reconcileSum;
    private double _effectsSum;
    private int _renderCount;
    private readonly Stopwatch _reportClock = Stopwatch.StartNew();
    private long _totalRenderCount;

    // Public perf snapshot — updated every ~1 second, readable from components
    private RenderStats _stats;

    /// <summary>
    /// Live render performance snapshot, updated every ~1 second.
    /// Always available (FPS, frame time). DEBUG builds include per-reconcile element counters.
    /// </summary>
    public ref readonly RenderStats Stats => ref _stats;

    /// <summary>
    /// Provides access to the underlying reconciler for RegisterType calls.
    /// </summary>
    public Reconciler Reconciler => _reconciler;

    /// <summary>
    /// Optional callback invoked after each render pass with phase timings (ms):
    /// treeBuildMs, reconcileMs, effectsMs. Used by perf harnesses to capture
    /// the breakdown of a Reactor render cycle.
    /// </summary>
    public Action<double, double, double>? OnRenderComplete { get; set; }

    /// <summary>
    /// The WinUI Window hosting this Reactor tree.
    /// Useful for obtaining the HWND (e.g., for file pickers in unpackaged apps).
    /// </summary>
    public Window Window => _window;

    /// <summary>
    /// The currently mounted root Component, if any. Used by MCP devtools to
    /// resolve event handlers on the root for the <c>fire</c> escape-hatch tool.
    /// </summary>
    internal Component? RootComponent => _rootComponent;

    /// <summary>
    /// Optional: when set, Reactor renders into this Border instead of Window.Content.
    /// Useful for embedding Reactor content in a pre-existing layout (e.g., a test harness
    /// with a persistent TitleBar).
    /// </summary>
    public WinUI.Border? ContentTarget { get; set; }

    public ReactorHost(Window window, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _reconciler = new Reconciler(_logger);
        _window = window;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        ReactorApp.ActiveHost = this;

        // Route QueryCache.EntryChanged notifications through our dispatcher so subscribers
        // observe cache changes on the UI thread even when Set/Invalidate were called from
        // a background thread (fetch continuation). First host on the process wins — all
        // hosts share the same process-wide default cache.
        var dq = _dispatcherQueue;
        var defaultCache = AppContexts.QueryCache.DefaultValue;
        defaultCache.DispatcherPost ??= action =>
        {
            if (!dq.TryEnqueue(() => action()))
                action(); // dispatcher shut down — fall back to inline
        };

        // Hook the window's Activated event into the focus-revalidation service.
        // The service itself lives in <see cref="AppContexts.FocusRevalidation"/> and is
        // always live; enrollment is a no-op when nothing has opted in. Only fire the
        // sweep when the feature flag is on — apps that don't want window-focus
        // revalidation pay zero cost.
        var focusService = AppContexts.FocusRevalidation.DefaultValue;
        if (focusService is not null)
        {
            try
            {
                _window.Activated += (_, args) =>
                {
                    if (!ReactorFeatureFlags.FocusRevalidation) return;
                    if (args.WindowActivationState != WindowActivationState.Deactivated)
                        focusService.RevalidateNow();
                };
            }
            catch { /* windowless / headless host — no activation hook */ }
        }

        // ── Accessibility: auto-detect forced-colors and reduced-motion ──
        // D3Dsl.IsForcedColors is set each render; listeners trigger re-render.
        try
        {
            _accessibilitySettings = new global::Windows.UI.ViewManagement.AccessibilitySettings();
            _isForcedColors = _accessibilitySettings.HighContrast;
            _accessibilitySettings.HighContrastChanged += OnHighContrastChanged;
        }
        catch { /* headless / unit-test host — no accessibility settings */ }

        try
        {
            _uiSettings = new global::Windows.UI.ViewManagement.UISettings();
            _isReducedMotion = !_uiSettings.AnimationsEnabled;
            _uiSettings.ColorValuesChanged += OnColorValuesChanged;
        }
        catch { /* headless / unit-test host — no UI settings */ }

        // Stop the render loop when the window closes — background threads
        // may still call setState after this, but RequestRender will bail out.
        _closedHandler = (_, _) => Dispose();
        _window.Closed += _closedHandler;
    }

    public void Mount(Component component)
    {
        _rootComponent = component;
        RequestRender();
    }

    public void Mount(Func<RenderContext, Element> renderFunc)
    {
        _rootRenderFunc = renderFunc;
        _funcContext = new RenderContext();
        RequestRender();
    }

    /// <summary>
    /// Thread-safe: can be called from any thread. Coalesces multiple calls into
    /// a single render. At most one RenderLoop is ever pending on the dispatcher.
    ///
    /// During render: setState calls set _needsRerender (no enqueue).
    /// Between renders: first setState CAS-flips _renderPending 0→1 and enqueues.
    /// _renderPending stays 1 throughout the render, blocking duplicate enqueues.
    /// </summary>
    internal void RequestRender()
    {
        if (_disposed) return;

        // Capture ambient animation curve so the async render pass can restore it.
        // Multiple state changes may fire before the render — last curve wins.
        if (AnimationScope.HasScope)
            _pendingAnimationCurve = AnimationScope.Current;

        // During render: just flag — the render loop will re-enqueue after Render().
        if (_isRendering)
        {
            _needsRerender = true;
            return;
        }

        // Between renders: CAS 0→1 gates a single TryEnqueue.
        if (Interlocked.CompareExchange(ref _renderPending, 1, 0) != 0)
        {
            _needsRerender = true;
            return;
        }

        _dispatcherQueue.TryEnqueue(RenderLoop);
    }

    private void RenderLoop()
    {
        if (_disposed) return;

        // _renderPending is 1 here — all concurrent RequestRender calls are
        // blocked from enqueuing duplicates. Render once, then decide.
        _needsRerender = false;
        Render();

        // Reset the gate so future setState calls can enqueue.
        Interlocked.Exchange(ref _renderPending, 0);

        // If state changed during render, re-enqueue at LOW priority so WinUI
        // layout/paint/input (normal priority + WM_PAINT) run first. Without this,
        // high-frequency setState sources cause back-to-back renders that starve the
        // compositor — layout never runs, property sets on dirty elements get
        // progressively slower, and reconcile time blows up non-linearly.
        if (_needsRerender)
        {
            if (Interlocked.CompareExchange(ref _renderPending, 1, 0) == 0)
                _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RenderLoop);
        }
    }

    private void Render()
    {
        _isRendering = true;
        try
        {
            Element? newTree = null;

            _phaseSw.Restart();

            if (_rootComponent is not null)
            {
                _rootComponent.Context.BeginRender(RequestRender);
                try
                {
                    newTree = _rootComponent.Render();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Component Render() threw");
                    ShowErrorFallback(ex);
                    return;
                }
            }
            else if (_rootRenderFunc is not null && _funcContext is not null)
            {
                _funcContext.BeginRender(RequestRender);
                try
                {
                    newTree = _rootRenderFunc(_funcContext);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Function component threw");
                    ShowErrorFallback(ex);
                    return;
                }
            }

            double treeBuildMs = _phaseSw.Elapsed.TotalMilliseconds;

            if (newTree is null) return;

            _phaseSw.Restart();

            // Restore captured animation scope so ApplyModifiers routes through
            // compositor animations instead of direct property sets.
            var capturedCurve = Interlocked.Exchange(ref _pendingAnimationCurve, null);
            if (capturedCurve is not null)
                AnimationScope.PushScope(capturedCurve);

            UIElement? newControl;
            try
            {
                newControl = _reconciler.Reconcile(
                    _currentTree,
                    newTree,
                    _currentControl,
                    RequestRender
                );
            }
            finally
            {
                if (capturedCurve is not null)
                    AnimationScope.PopScope();
            }

            if (newControl != _currentControl)
            {
                UIElement? contentToSet = newControl;
                if (ReactorFeatureFlags.HighlightReconcileChanges)
                {
                    _highlightWiring ??= new HighlightOverlayWiring(_dispatcherQueue);
                    contentToSet = _highlightWiring.SetContentViaWrapper(newControl);
                }

                if (ContentTarget is not null)
                    ContentTarget.Child = contentToSet;
                else
                    _window.Content = contentToSet;
                AttachThemeListener(newControl);
            }
            else if (ReactorFeatureFlags.HighlightReconcileChanges && _highlightWiring?.WrapperRoot is null)
            {
                // Flag was toggled on after initial render — install wrapper now
                _highlightWiring ??= new HighlightOverlayWiring(_dispatcherQueue);
                var wrapper = _highlightWiring.SetContentViaWrapper(newControl);
                if (ContentTarget is not null)
                    ContentTarget.Child = wrapper;
                else
                    _window.Content = wrapper;
            }
            else if (!ReactorFeatureFlags.HighlightReconcileChanges && _highlightWiring?.WrapperRoot is not null)
            {
                // Flag was toggled off while preserving the same root control — tear down
                // the wrapper and reinstate the raw control so we don't pay for an extra
                // layout layer when the feature is disabled.
                if (ContentTarget is not null)
                    ContentTarget.Child = newControl;
                else
                    _window.Content = newControl;
                _highlightWiring.Dispose();
                _highlightWiring = null;
            }

            _currentControl = newControl;
            _currentTree = newTree;

            // Start any connected animations now that the new tree is in the visual tree
            _reconciler.FlushConnectedAnimations();

            // Schedule highlight overlay after layout so elements have final bounds.
            _highlightWiring?.ScheduleHighlightFlush(_reconciler);

            double reconcileMs = _phaseSw.Elapsed.TotalMilliseconds;

            _phaseSw.Restart();

            if (_rootComponent is not null)
                _rootComponent.Context.FlushEffects();
            else if (_funcContext is not null)
                _funcContext.FlushEffects();

            double effectsMs = _phaseSw.Elapsed.TotalMilliseconds;

            OnRenderComplete?.Invoke(treeBuildMs, reconcileMs, effectsMs);

#if DEBUG
            _logger.LogDebug(
                "RECONCILE: tree={TreeBuildMs:F2}ms  reconcile={ReconcileMs:F2}ms  effects={EffectsMs:F2}ms  total={TotalMs:F2}ms  |  diffed={Diffed}  skipped={Skipped}  created={Created}  modified={Modified}",
                treeBuildMs, reconcileMs, effectsMs, treeBuildMs + reconcileMs + effectsMs,
                _reconciler.DebugElementsDiffed, _reconciler.DebugElementsSkipped,
                _reconciler.DebugUIElementsCreated, _reconciler.DebugUIElementsModified);
#endif

            // Accumulate and report every ~1 second
            _treeBuildSum += treeBuildMs;
            _reconcileSum += reconcileMs;
            _effectsSum += effectsMs;
            _renderCount++;
            _totalRenderCount++;

            if (_reportClock.Elapsed.TotalSeconds >= 1.0 && _renderCount > 0)
            {
                double avgTree = _treeBuildSum / _renderCount;
                double avgReconcile = _reconcileSum / _renderCount;
                double avgEffects = _effectsSum / _renderCount;
                double avgTotal = avgTree + avgReconcile + avgEffects;

                _stats = new RenderStats
                {
                    Fps = _renderCount / _reportClock.Elapsed.TotalSeconds,
                    RendersInWindow = _renderCount,
                    TotalRenders = _totalRenderCount,
                    AvgTreeBuildMs = avgTree,
                    AvgReconcileMs = avgReconcile,
                    AvgEffectsMs = avgEffects,
                    AvgTotalMs = avgTotal,
                    LastDiffed = _reconciler.DebugElementsDiffed,
                    LastSkipped = _reconciler.DebugElementsSkipped,
                    LastCreated = _reconciler.DebugUIElementsCreated,
                    LastModified = _reconciler.DebugUIElementsModified,
                };

                _logger.LogDebug(
                    "PERF [{RenderCount} renders]: tree={TreeMs:F2}ms  reconcile={ReconcileMs:F2}ms  effects={EffectsMs:F2}ms  total={TotalMs:F2}ms",
                    _renderCount, avgTree, avgReconcile, avgEffects, avgTotal);
                _treeBuildSum = 0;
                _reconcileSum = 0;
                _effectsSum = 0;
                _renderCount = 0;
                _reportClock.Restart();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Render FAILED");
            ShowErrorFallback(ex);
        }
        finally
        {
            _isRendering = false;
        }
    }

    /// <summary>
    /// Subscribes to ActualThemeChanged on the root content element so that
    /// ThemeRef-bound properties are re-resolved when the theme switches.
    /// WinUI controls handle theme changes natively via {ThemeResource} bindings,
    /// but Reactor's ThemeRef values are resolved once during reconciliation —
    /// this listener triggers a re-render so they pick up the new theme.
    /// </summary>
    private void AttachThemeListener(UIElement? control)
    {
        if (_themeListenerElement is not null)
            _themeListenerElement.ActualThemeChanged -= OnActualThemeChanged;

        if (control is not FrameworkElement fe)
        {
            _themeListenerElement = null;
            return;
        }

        _themeListenerElement = fe;
        fe.ActualThemeChanged += OnActualThemeChanged;
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        _logger.LogDebug("Theme changed to {Theme} — re-rendering", sender.ActualTheme);
        OpenClawTray.Infrastructure.Core.Reconciler.ClearStyleCache();
        RequestRender();
    }

    private void OnHighContrastChanged(
        global::Windows.UI.ViewManagement.AccessibilitySettings sender, object args)
    {
        _isForcedColors = sender.HighContrast;
        _logger.LogDebug("High-contrast changed to {IsHighContrast} — re-rendering", _isForcedColors);
        RequestRender();
    }

    private void OnColorValuesChanged(
        global::Windows.UI.ViewManagement.UISettings sender, object args)
    {
        // UISettings.ColorValuesChanged fires for palette changes and also when
        // AnimationsEnabled toggles. Re-read both signals.
        _isReducedMotion = !sender.AnimationsEnabled;
        // High-contrast palette may also change — re-read to be safe.
        if (_accessibilitySettings is { } a11y)
        {
            _isForcedColors = a11y.HighContrast;
        }
        RequestRender();
    }

    /// <summary>
    /// Awaits until the render loop is idle (no pending or in-flight renders).
    /// Yields to the dispatcher at Low priority in a loop so that Normal-priority
    /// RenderLoop callbacks and Low-priority re-renders all complete before returning.
    /// Used by test harnesses to replace blind Task.Delay waits.
    /// </summary>
    public Task WaitForIdleAsync(int maxYields = 10)
    {
        if (_disposed) return Task.CompletedTask;
        if (_renderPending == 0 && !_isRendering && !_needsRerender)
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource();
        int yields = 0;
        void CheckIdle()
        {
            if (_disposed || ++yields > maxYields ||
                (_renderPending == 0 && !_isRendering && !_needsRerender))
            {
                tcs.TrySetResult();
            }
            else
            {
                _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, CheckIdle);
            }
        }
        _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, CheckIdle);
        return tcs.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _window.Closed -= _closedHandler;

        // Theme listener touches UI-affine objects — marshal to UI thread if needed.
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_themeListenerElement is not null)
                _themeListenerElement.ActualThemeChanged -= OnActualThemeChanged;
            _themeListenerElement = null;
        });

        // Accessibility listener cleanup
        if (_accessibilitySettings is not null)
            _accessibilitySettings.HighContrastChanged -= OnHighContrastChanged;
        if (_uiSettings is not null)
            _uiSettings.ColorValuesChanged -= OnColorValuesChanged;

        _rootComponent?.Context.RunCleanups();
        _funcContext?.RunCleanups();
        _reconciler.Dispose();
        _rootComponent = null;
        _rootRenderFunc = null;
        _funcContext = null;
        _currentTree = null;
        _currentControl = null;
        _highlightWiring?.Dispose();
        _highlightWiring = null;
        ReactorApp.ActiveHost = null;
    }

    private void ShowErrorFallback(Exception ex)
    {
        var errorPanel = new WinUI.Border
        {
            BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                global::Windows.UI.Color.FromArgb(255, 255, 0, 0)),
            BorderThickness = new Thickness(2),
            Padding = new Thickness(16),
            Child = new WinUI.TextBlock
            {
                Text = $"Render error: {ex.GetType().Name}: {ex.Message}",
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            }
        };
        if (_highlightWiring is not null)
        {
            _highlightWiring.TryShowErrorInWrapper(errorPanel);
        }
        else if (ContentTarget is not null)
        {
            ContentTarget.Child = errorPanel;
        }
        else
        {
            _window.Content = errorPanel;
        }
        _currentControl = errorPanel;
        _currentTree = null;
    }
}
