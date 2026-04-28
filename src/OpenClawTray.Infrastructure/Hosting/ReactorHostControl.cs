using System.Diagnostics;
using OpenClawTray.Infrastructure.Animation;
using OpenClawTray.Infrastructure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUI = Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Infrastructure.Hosting;

/// <summary>
/// A fully self-contained WinUI ContentControl that hosts a Reactor component tree.
/// Drop this into any vanilla WinUI app — no ReactorApp, ReactorApplication, or special
/// bootstrapping needed. Each instance owns its own Reconciler and render loop.
///
/// Usage in XAML:
///   <![CDATA[
///   <local:ReactorHostControl x:Name="ductHost" />
///   ]]>
///
/// Usage in code-behind:
///   ductHost.Mount(new MyComponent());
///   — or —
///   ductHost.Mount(ctx => VStack(Text("Hello from Reactor!")));
///   — or via XAML property —
///   <![CDATA[
///   <local:ReactorHostControl ComponentFactory="{x:Bind CreateMyComponent}" />
///   ]]>
///
/// Features:
///   - Thread-safe render batching (setState from any thread)
///   - Low-priority re-enqueue so layout/paint/input aren't starved
///   - Render performance stats (FPS, frame timing)
///   - Automatic theme change detection and re-render
///   - Connected animation flushing
///   - Error boundary with fallback UI
///   - Clean lifecycle via Loaded/Unloaded
/// </summary>
public sealed partial class ReactorHostControl : ContentControl, IDisposable
{
#pragma warning disable CS0414 // Design constant for render-loop limiting; wiring pending
    private static readonly int MaxRenderIterations = 50;
#pragma warning restore CS0414

    private readonly Reconciler _reconciler;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger _logger;

    private Component? _rootComponent;
    private Func<RenderContext, Element>? _rootRenderFunc;
    private RenderContext? _funcContext;

    private Element? _currentTree;
    private UIElement? _currentControl;
    private int _renderPending;      // 0 or 1 — Interlocked for thread-safe access
    private volatile bool _isRendering;       // only touched on UI thread
    private volatile bool _needsRerender;     // only touched on UI thread
    private bool _themeListenerAttached;
    private volatile bool _disposed;
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
    /// Factory to create the root component. Set this or use Mount() for more control.
    /// If set, the component is created and mounted when the control is Loaded.
    /// Example: ComponentFactory = () => new MyComponent();
    /// </summary>
    public Func<Component>? ComponentFactory { get; set; }

    /// <summary>
    /// Optional props to pass to the root component created by ComponentFactory.
    /// </summary>
    public object? Props { get; set; }

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

    public ReactorHostControl(Component? component = null, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _reconciler = new Reconciler(_logger);
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        // ContentControl inherits IsTabStop=true from Control. Set it to false
        // so focus navigation passes through to child elements directly. Without
        // this, Shift+Tab from the first child stops on the ReactorHostControl itself
        // (invisible focus) before departing — especially problematic in XAML Islands
        // where that extra stop prevents TakeFocusRequested from firing.
        IsTabStop = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        if (component is not null)
            Mount(component);
    }

    /// <summary>
    /// Mount a Component instance directly. Starts the render loop immediately.
    /// </summary>
    public void Mount(Component component)
    {
        _rootRenderFunc = null;
        _funcContext = null;
        _rootComponent = component;
        RequestRender();
    }

    /// <summary>
    /// Mount a function component. Starts the render loop immediately.
    /// </summary>
    public void Mount(Func<RenderContext, Element> renderFunc)
    {
        _rootComponent = null;
        _rootRenderFunc = renderFunc;
        _funcContext = new RenderContext();
        RequestRender();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_rootComponent is not null || _rootRenderFunc is not null)
            return; // Already mounted via Mount()

        if (ComponentFactory is null)
            return;

        var component = ComponentFactory();

        if (Props is not null && component is IPropsReceiver receiver)
            receiver.SetProps(Props);

        Mount(component);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }

    /// <summary>
    /// Thread-safe: can be called from any thread. Coalesces multiple calls into
    /// a single render. At most one RenderLoop is ever pending on the dispatcher.
    ///
    /// During render: setState calls set _needsRerender (no enqueue).
    /// Between renders: first setState CAS-flips _renderPending 0→1 and enqueues.
    /// _renderPending stays 1 throughout the render, blocking duplicate enqueues.
    /// </summary>
    private void RequestRender()
    {
        if (_disposed) return;

        if (AnimationScope.HasScope)
            _pendingAnimationCurve = AnimationScope.Current;

        // Flag re-render before the _isRendering / CAS checks so the request
        // survives the TOCTOU window between Render()'s finally
        // (_isRendering = false) and RenderLoop's gate-reset
        // (Interlocked.Exchange(_renderPending, 0)).
        _needsRerender = true;

        // During render: the flag is sufficient — RenderLoop re-checks after Render().
        if (_isRendering) return;

        // Between renders: CAS 0→1 gates a single TryEnqueue.
        if (Interlocked.CompareExchange(ref _renderPending, 1, 0) != 0) return;

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
                if (ReactorFeatureFlags.HighlightReconcileChanges)
                {
                    _highlightWiring ??= new HighlightOverlayWiring(_dispatcherQueue);
                    Content = _highlightWiring.SetContentViaWrapper(newControl);
                }
                else
                {
                    Content = newControl;
                }
                AttachThemeListener(newControl);
            }
            else if (ReactorFeatureFlags.HighlightReconcileChanges && _highlightWiring?.WrapperRoot is null)
            {
                // Flag was toggled on after initial render — install wrapper now
                _highlightWiring ??= new HighlightOverlayWiring(_dispatcherQueue);
                Content = _highlightWiring.SetContentViaWrapper(newControl);
            }
            else if (!ReactorFeatureFlags.HighlightReconcileChanges && _highlightWiring?.WrapperRoot is not null)
            {
                // Flag was toggled off while preserving the same root control — tear down
                // the wrapper and reinstate the raw control so we don't pay for an extra
                // layout layer when the feature is disabled.
                Content = newControl;
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
        if (_themeListenerAttached || control is not FrameworkElement fe) return;
        _themeListenerAttached = true;

        fe.ActualThemeChanged += (_, _) =>
        {
            _logger.LogDebug("Theme changed to {Theme} — re-rendering", fe.ActualTheme);
            RequestRender();
        };
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
        else
        {
            Content = errorPanel;
        }
        _currentControl = errorPanel;
        _currentTree = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;

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
        Content = null;
    }
}
