using System.Numerics;
using OpenClawTray.Infrastructure.Animation;
using OpenClawTray.Infrastructure.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using WinUI = Microsoft.UI.Xaml.Controls;
using WinPrim = Microsoft.UI.Xaml.Controls.Primitives;

namespace OpenClawTray.Infrastructure.Core;

/// <summary>
/// The reconciler diffs old and new element trees and patches the real WinUI control tree.
///
/// Split across partial classes:
///   - Reconciler.cs           — orchestration, children, unmount, helpers
///   - Reconciler.Mount.cs     — Mount() dispatch + per-control MountXxx methods
///   - Reconciler.Update.cs    — Update() dispatch + per-control UpdateXxx methods
/// </summary>
public sealed partial class Reconciler : IDisposable
{
    private readonly Dictionary<UIElement, ComponentNode> _componentNodes = new();
    private readonly Dictionary<UIElement, ErrorBoundaryNode> _errorBoundaryNodes = new();
    private readonly Dictionary<UIElement, NavigationHostNode> _navigationHostNodes = new();
    private readonly ElementPool _pool = new();
    private readonly Dictionary<Type, ITypeRegistration> _typeRegistry = new();
    private readonly ILogger _logger;
    private readonly List<(ConnectedAnimation Animation, UIElement Target)> _pendingConnectedAnimationStarts = new();
    private readonly ContextScope _contextScope = new();
    private int _errorBoundaryDepth;

    // ── Style cache: avoids redundant XamlReader.Load() for identical theme binding sets ──
    private static readonly global::System.Collections.Concurrent.ConcurrentDictionary<string, Style> _styleCache = new();

    /// <summary>
    /// Builds a deterministic cache key for a style based on its target type and
    /// the set of ThemeRef bindings. Keys are sorted by property name so that
    /// dictionaries with the same entries in different enumeration order produce
    /// the same key.
    /// </summary>
    internal static string BuildCacheKey(string targetType, IReadOnlyDictionary<string, ThemeRef> bindings)
    {
        // Format: "TargetType|Prop1=Key1|Prop2=Key2" with properties sorted by Ordinal
        var sortedKeys = bindings.Keys.ToArray();
        Array.Sort(sortedKeys, StringComparer.Ordinal);
        var sb = new global::System.Text.StringBuilder(targetType);
        foreach (var key in sortedKeys)
        {
            sb.Append('|').Append(key).Append('=').Append(bindings[key].ResourceKey);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Clears the compiled style cache. Called on theme change as conservative
    /// memory cleanup — not a correctness requirement since {ThemeResource}
    /// setters are live-resolved by WinUI.
    /// </summary>
    internal static void ClearStyleCache() => _styleCache.Clear();


    /// <summary>
    /// Thread-static stagger context for enter transitions. When a parent with StaggerConfig
    /// mounts children, it pushes this context so each child's ApplyEnterTransition can
    /// compute stagger delay from its index among siblings.
    /// </summary>
    [ThreadStatic] private static StaggerScope? _staggerScope;

    private sealed class StaggerScope
    {
        public TimeSpan Delay;
        public int NextIndex;
        public StaggerScope? Previous;
    }

    private static void PushStaggerScope(TimeSpan delay)
    {
        _staggerScope = new StaggerScope { Delay = delay, NextIndex = 0, Previous = _staggerScope };
    }

    private static void PopStaggerScope()
    {
        if (_staggerScope is not null)
            _staggerScope = _staggerScope.Previous;
    }

    private static (int index, TimeSpan delay) ConsumeStaggerIndex()
    {
        if (_staggerScope is null) return (0, default);
        var idx = _staggerScope.NextIndex++;
        return (idx, _staggerScope.Delay);
    }

    /// <summary>
    /// Per-reconcile counters for diagnosing diff and mount/update volume.
    /// Reset before each top-level Reconcile() call; read afterward. Always
    /// populated (including Release builds) so ETW trace consumers can read
    /// them off the <c>ReconcileStop</c> event payload.
    /// </summary>
    public int DebugElementsDiffed;
    public int DebugElementsSkipped;
    public int DebugUIElementsCreated;
    public int DebugUIElementsModified;
    private int _debugReconcileDepth;

    // ── Reconcile-highlight capture (gated by ReactorFeatureFlags.HighlightReconcileChanges) ──
    private List<UIElement>? _highlightMounted;
    private List<UIElement>? _highlightModified;

    /// <summary>
    /// UIElements that were newly mounted during the last top-level Reconcile pass.
    /// Only populated when <see cref="ReactorFeatureFlags.HighlightReconcileChanges"/> is true;
    /// returns an empty list otherwise so callers never see stale data after the flag is toggled off.
    /// </summary>
    public IReadOnlyList<UIElement> LastMountedElements =>
        ReactorFeatureFlags.HighlightReconcileChanges
            ? (IReadOnlyList<UIElement>?)_highlightMounted ?? Array.Empty<UIElement>()
            : Array.Empty<UIElement>();

    /// <summary>
    /// UIElements that were modified in-place during the last top-level Reconcile pass.
    /// Only populated when <see cref="ReactorFeatureFlags.HighlightReconcileChanges"/> is true;
    /// returns an empty list otherwise so callers never see stale data after the flag is toggled off.
    /// </summary>
    public IReadOnlyList<UIElement> LastModifiedElements =>
        ReactorFeatureFlags.HighlightReconcileChanges
            ? (IReadOnlyList<UIElement>?)_highlightModified ?? Array.Empty<UIElement>()
            : Array.Empty<UIElement>();

    /// <summary>
    /// The element pool used by this reconciler. Disable via Pool.Enabled = false
    /// to prevent recycled controls from retaining stale property state.
    /// </summary>
    public ElementPool Pool => _pool;

    /// <summary>
    /// EXP-2: When true, UpdateText uses bitmask diff (old vs new Element comparison)
    /// instead of reading WinUI control properties via COM interop to guard writes.
    /// </summary>
    private static volatile bool _enableBitmaskDiff;
    public static bool EnableBitmaskDiff
    {
        get => _enableBitmaskDiff;
        set => _enableBitmaskDiff = value;
    }

    public Reconciler() : this(NullLogger.Instance) { }

    public Reconciler(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Associates a control with its current element via Tag.
    /// Only call for interactive controls that need the Tag-based event handler pattern.
    /// Layout-only controls (Border, StackPanel, TextBlock, etc.) should NOT set Tag
    /// to avoid expensive COM DependencyProperty calls on the hot path.
    /// </summary>
    /// <summary>
    /// A shared DataTemplate containing a ContentControl shell.
    /// Parsed once via XamlReader.Load, reused across all items controls (ListView, GridView, FlipView).
    /// </summary>
    internal static readonly Lazy<DataTemplate> SharedContentControlTemplate = new(() =>
        (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<ContentControl HorizontalContentAlignment='Stretch' VerticalContentAlignment='Stretch'/>" +
            "</DataTemplate>"));

    internal static void SetElementTag(FrameworkElement control, Element element) => control.Tag = element;

    /// <summary>
    /// Retrieves the element associated with a control via Tag, or null.
    /// </summary>
    internal static Element? GetElementTag(UIElement control) =>
        control is FrameworkElement fe ? fe.Tag as Element : null;

    // ════════════════════════════════════════════════════════════════════
    //  Extensible type registry (Feature 1: RegisterType API)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Registers a custom element type so the reconciler knows how to mount, update, and unmount it.
    /// Registered types take priority over built-in types.
    ///
    /// The mount and update handlers receive the Reconciler instance so they can
    /// recursively mount/update/unmount child elements without capturing external state.
    /// </summary>
    public void RegisterType<TElement, TControl>(
        Func<Reconciler, TElement, Action, TControl> mount,
        Func<Reconciler, TElement, TElement, TControl, Action, UIElement?> update,
        Action<Reconciler, TControl>? unmount = null)
        where TElement : Element
        where TControl : UIElement
    {
        _typeRegistry[typeof(TElement)] = new TypeRegistration<TElement, TControl>(mount, update, unmount);
    }

    internal interface ITypeRegistration
    {
        UIElement Mount(Element element, Action requestRerender, Reconciler reconciler);
        UIElement? Update(Element oldEl, Element newEl, UIElement control, Action requestRerender, Reconciler reconciler);
        void Unmount(UIElement control, Reconciler reconciler);
        bool HasUnmount { get; }
    }

    private sealed class TypeRegistration<TElement, TControl> : ITypeRegistration
        where TElement : Element
        where TControl : UIElement
    {
        private readonly Func<Reconciler, TElement, Action, TControl> _mount;
        private readonly Func<Reconciler, TElement, TElement, TControl, Action, UIElement?> _update;
        private readonly Action<Reconciler, TControl>? _unmount;

        public TypeRegistration(
            Func<Reconciler, TElement, Action, TControl> mount,
            Func<Reconciler, TElement, TElement, TControl, Action, UIElement?> update,
            Action<Reconciler, TControl>? unmount)
        {
            _mount = mount;
            _update = update;
            _unmount = unmount;
        }

        public bool HasUnmount => _unmount is not null;

        public UIElement Mount(Element element, Action requestRerender, Reconciler reconciler)
            => _mount(reconciler, (TElement)element, requestRerender);

        public UIElement? Update(Element oldEl, Element newEl, UIElement control, Action requestRerender, Reconciler reconciler)
        {
            // Guard against control type mismatch (e.g., recycled from pool or element type changed at this position).
            // If the existing control isn't our expected type, force a fresh mount instead of crashing.
            if (control is not TControl typedControl || oldEl is not TElement typedOldEl)
                return _mount(reconciler, (TElement)newEl, requestRerender);

            return _update(reconciler, typedOldEl, (TElement)newEl, typedControl, requestRerender);
        }

        public void Unmount(UIElement control, Reconciler reconciler)
        {
            if (control is TControl typedControl)
                _unmount?.Invoke(reconciler, typedControl);
        }
    }

    public UIElement? Reconcile(
        Element? oldElement,
        Element? newElement,
        UIElement? existingControl,
        Action requestRerender)
    {
        // Trace only top-level reconcile passes (depth == 0) to avoid flooding
        // the provider with per-subtree entries; nested Reconcile() calls during
        // the same pass don't emit their own start/stop. Gate the depth counter
        // and Start emit on IsEnabled so the disabled path pays nothing extra.
        bool emitTrace = Diagnostics.ReactorEventSource.Log.IsEnabled(
            global::System.Diagnostics.Tracing.EventLevel.Informational,
            Diagnostics.ReactorEventSource.Keywords.Reconcile)
            && _reconcileTraceDepth++ == 0;
        if (emitTrace)
        {
            Diagnostics.ReactorEventSource.Log.ReconcileStart(
                newElement?.GetType().Name ?? "null");
        }
        if (_debugReconcileDepth++ == 0)
        {
            DebugElementsDiffed = 0;
            DebugElementsSkipped = 0;
            DebugUIElementsCreated = 0;
            DebugUIElementsModified = 0;
            if (ReactorFeatureFlags.HighlightReconcileChanges)
            {
                (_highlightMounted ??= new()).Clear();
                (_highlightModified ??= new()).Clear();
            }
        }
        try {
        try
        {
            if (newElement is null or EmptyElement)
            {
                if (existingControl is not null)
                    Unmount(existingControl);
                return null;
            }

            if (oldElement is null or EmptyElement || existingControl is null)
                return Mount(newElement, requestRerender);

            return ReconcileImperative(oldElement, newElement, existingControl, requestRerender);
        }
        finally
        {
            if (emitTrace)
            {
                _reconcileTraceDepth--;
                Diagnostics.ReactorEventSource.Log.ReconcileStop(
                    DebugElementsDiffed, DebugElementsSkipped,
                    DebugUIElementsCreated, DebugUIElementsModified);
            }
        }
        } finally { _debugReconcileDepth--; }
    }

    // Tracks top-level Reconcile() entries so trace start/stop only fires once
    // per pass. Only mutated when the Reconcile keyword is enabled.
    private int _reconcileTraceDepth;

    private static void FlushEffectsTraced(RenderContext ctx, string? componentName)
    {
        // Fast path when the Render keyword is off: no Stopwatch, no event emit.
        if (!Diagnostics.ReactorEventSource.Log.IsEnabled(
                global::System.Diagnostics.Tracing.EventLevel.Informational,
                Diagnostics.ReactorEventSource.Keywords.Render))
        {
            ctx.FlushEffects();
            return;
        }

        var name = componentName ?? string.Empty;
        Diagnostics.ReactorEventSource.Log.EffectsFlushStart(name);
        var start = global::System.Diagnostics.Stopwatch.GetTimestamp();
        try { ctx.FlushEffects(); }
        finally
        {
            var us = (long)((global::System.Diagnostics.Stopwatch.GetTimestamp() - start)
                * 1_000_000.0 / global::System.Diagnostics.Stopwatch.Frequency);
            Diagnostics.ReactorEventSource.Log.EffectsFlushStop(name, us);
        }
    }

    /// <summary>
    /// The original C# imperative reconciliation path.
    /// </summary>
    private UIElement? ReconcileImperative(
        Element oldElement, Element newElement,
        UIElement existingControl, Action requestRerender)
    {
        // Contract: when we return a replacement (either from Update remounting or
        // from a type-change full remount), the caller must place it in the parent
        // collection — e.g. `g.Children[i] = replacement`. WinUI's indexer assignment
        // detaches the old control from its parent as part of the swap, so we must
        // leave the parent collection alone here. The UnmountAndPool path would
        // invoke ElementPool.Return.DetachFromParent, which synchronously removes
        // the old child from the parent's Children collection before the caller's
        // assignment runs; that shifts subsequent sibling indices and corrupts any
        // positional update loop that was iterating over the collection (see #34,
        // where DataGrid row cells were dropped off the end during inline-edit flips).
        //
        // Trade-off: the replaced control is no longer handed back to ElementPool.
        // Pooling is a performance optimization; correctness dominates here. Type
        // flips are uncommon in practice (inline edit transitions, ErrorBoundary
        // remounts) so the lost reuse is minor. A follow-up could reintroduce
        // pool-return by having the caller invoke a post-swap pool hook once the
        // old control is detached, but doing so safely across every Reconcile
        // caller is out of scope for this fix.
        if (CanUpdate(oldElement, newElement))
        {
            var replacement = Update(oldElement, newElement, existingControl, requestRerender);
            if (replacement is not null && replacement != existingControl)
                Unmount(existingControl);
            return replacement ?? existingControl;
        }

        Unmount(existingControl);
        return Mount(newElement, requestRerender);
    }

    // ════════════════════════════════════════════════════════════════════
    //  Component reconciliation
    // ════════════════════════════════════════════════════════════════════

    private void ReconcileComponent(Element oldEl, Element newEl, UIElement control, Action requestRerender)
    {
        if (!_componentNodes.TryGetValue(control, out var node))
        {
            _logger.LogWarning("ReconcileComponent: component node not found for control — component will not update");
            return;
        }

        // ── Memo check: skip render if props/context unchanged and not self-triggered ──
        bool selfTriggered = node.SelfTriggered;
        node.SelfTriggered = false;

        if (!selfTriggered)
        {
            bool skipRender = false;

            if (node.Component is not null && newEl is ComponentElement newCompEl)
            {
                // Class component memo check
                var oldProps = node.PreviousProps;
                var newProps = newCompEl.Props;

                bool propsChanged;
                if (node.Component is IPropsReceiver)
                {
                    // Component<TProps>: delegate to ShouldUpdate(oldProps, newProps)
                    propsChanged = ShouldUpdateWithProps(node.Component, oldProps, newProps);
                }
                else
                {
                    // Propless Component: delegate to ShouldUpdate()
                    propsChanged = node.Component.ShouldUpdate();
                }

                bool contextChanged = HasConsumedContextChanged(node);
                skipRender = !propsChanged && !contextChanged;
            }
            else if (node.Context is not null && newEl is MemoElement newMemo)
            {
                // MemoElement memo check
                var oldDeps = node.MemoDependencies;
                var newDeps = newMemo.Dependencies;
                bool depsChanged = oldDeps is null && newDeps is null
                    ? false // both null = render once, never re-render from parent
                    : oldDeps is null || newDeps is null || !DepsEqual(oldDeps, newDeps);
                bool contextChanged = HasConsumedContextChanged(node);
                skipRender = !depsChanged && !contextChanged;
            }

            if (skipRender)
            {
                // Still update the element reference (modifiers may have changed on the ComponentElement itself)
                node.Element = newEl;
                return;
            }
        }

        // ── Render the component ──
        // Pass the component's own wrapped rerender to children so that child state
        // changes propagate SelfTriggered up through all component ancestors.
        var componentRerender = CreateComponentRerender(node, requestRerender);

        // Only compute component name + timestamps when the Render keyword is
        // enabled, so the disabled path avoids the reflection and Stopwatch work.
        bool traceRender = Diagnostics.ReactorEventSource.Log.IsEnabled(
            global::System.Diagnostics.Tracing.EventLevel.Informational,
            Diagnostics.ReactorEventSource.Keywords.Render);
        string? componentName = null;
        long renderStart = 0;
        if (traceRender)
        {
            componentName = node.Component?.GetType().Name ?? newEl.GetType().Name;
            Diagnostics.ReactorEventSource.Log.ComponentRenderStart(
                componentName, selfTriggered ? "self" : "parent");
            renderStart = global::System.Diagnostics.Stopwatch.GetTimestamp();
        }

        Element newChildElement;
        try
        {
            if (node.Component is not null)
            {
                // Update props before re-rendering so the component sees fresh data
                if (newEl is ComponentElement compEl && compEl.Props is not null
                    && node.Component is IPropsReceiver receiver)
                {
                    receiver.SetProps(compEl.Props);
                }

                node.Component.Context.BeginRender(componentRerender, _contextScope);
                newChildElement = node.Component.Render();
                FlushEffectsTraced(node.Component.Context, componentName);
            }
            else if (node.Context is not null && newEl is FuncElement func)
            {
                node.Context.BeginRender(componentRerender, _contextScope);
                newChildElement = func.RenderFunc(node.Context);
                FlushEffectsTraced(node.Context, componentName);
            }
            else if (node.Context is not null && newEl is MemoElement memo)
            {
                node.Context.BeginRender(componentRerender, _contextScope);
                newChildElement = memo.RenderFunc(node.Context);
                FlushEffectsTraced(node.Context, componentName);
            }
            else
            {
                if (traceRender)
                    Diagnostics.ReactorEventSource.Log.ComponentRenderStop(componentName!, 0);
                return;
            }
        }
        catch (Exception ex) when (_errorBoundaryDepth == 0 && ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogError(ex, "Component Render() threw: {ComponentName}", newEl.GetType().Name);
            if (Diagnostics.ReactorEventSource.Log.IsEnabled(
                    global::System.Diagnostics.Tracing.EventLevel.Error,
                    Diagnostics.ReactorEventSource.Keywords.Errors))
            {
                Diagnostics.ReactorEventSource.Log.RenderError(
                    componentName ?? newEl.GetType().Name, ex.GetType().Name, ex.Message);
            }
            newChildElement = new TextBlockElement($"⚠ Render error: {ex.Message}");
        }

        if (traceRender)
        {
            var renderElapsedUs = (long)((global::System.Diagnostics.Stopwatch.GetTimestamp() - renderStart)
                * 1_000_000.0 / global::System.Diagnostics.Stopwatch.Frequency);
            Diagnostics.ReactorEventSource.Log.ComponentRenderStop(componentName!, renderElapsedUs);
        }

        // Dereference the Border wrapper to get the actual child control.
        // Each component is wrapped in a Border as an identity anchor, so we
        // reconcile the child inside the wrapper, not the wrapper itself.
        var existingChild = (control as Border)?.Child;
        var newControl = Reconcile(node.RenderedElement, newChildElement, existingChild, componentRerender);
        if (control is Border border)
        {
            if (newControl != existingChild)
                border.Child = newControl; // handles both replacement and null (child removed)
        }

        node.RenderedElement = newChildElement;
        node.Element = newEl;
        // Store current props for next memo comparison
        if (newEl is ComponentElement compEl2)
            node.PreviousProps = compEl2.Props;
        else if (newEl is MemoElement memoEl)
            node.MemoDependencies = memoEl.Dependencies;
    }

    /// <summary>
    /// Creates a rerender callback that marks the component node as self-triggered
    /// before invoking the parent requestRerender, so the memo check is bypassed.
    /// Captures the node directly to avoid accessing _componentNodes from background threads.
    /// </summary>
    private static Action CreateComponentRerender(ComponentNode node, Action requestRerender)
    {
        return () =>
        {
            node.SelfTriggered = true;
            requestRerender();
        };
    }

    /// <summary>
    /// Checks whether any context consumed by a component has changed since the last render.
    /// </summary>
    private bool HasConsumedContextChanged(ComponentNode node)
    {
        var renderCtx = node.Component?.Context ?? node.Context;
        if (renderCtx is null) return false;

        foreach (var ctxHook in renderCtx.ContextHooks)
        {
            var currentValue = _contextScope.Read(ctxHook.Context);
            if (!Equals(currentValue, ctxHook.LastValue))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Calls ShouldUpdate(oldProps, newProps) on a Component&lt;TProps&gt; via interface dispatch.
    /// </summary>
    private static bool ShouldUpdateWithProps(Component component, object? oldProps, object? newProps)
    {
        if (component is IPropsComparable comparable)
            return comparable.CompareProps(oldProps, newProps);

        // Fallback: always re-render
        return true;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Children reconciliation (keyed LIS + positional)
    // ════════════════════════════════════════════════════════════════════

    private void ReconcileChildren(
        Element[] oldChildren, Element[] newChildren,
        WinUI.Panel panel, Action requestRerender)
    {
        var childCollection = new PanelChildCollection(panel);
        // Skip the try/finally and event emit when the Reconcile keyword is off.
        if (!Diagnostics.ReactorEventSource.Log.IsEnabled(
                global::System.Diagnostics.Tracing.EventLevel.Informational,
                Diagnostics.ReactorEventSource.Keywords.Reconcile))
        {
            ChildReconciler.Reconcile(oldChildren, newChildren, childCollection, this, requestRerender);
            return;
        }
        Diagnostics.ReactorEventSource.Log.ChildReconcileStart(oldChildren.Length, newChildren.Length);
        try { ChildReconciler.Reconcile(oldChildren, newChildren, childCollection, this, requestRerender); }
        finally { Diagnostics.ReactorEventSource.Log.ChildReconcileStop(); }
    }

    private void ReconcileItemsChildren(
        Element[] oldChildren, Element[] newChildren,
        WinUI.ItemsControl itemsControl, Action requestRerender)
    {
        var childCollection = new ItemsControlChildCollection(itemsControl);
        if (!Diagnostics.ReactorEventSource.Log.IsEnabled(
                global::System.Diagnostics.Tracing.EventLevel.Informational,
                Diagnostics.ReactorEventSource.Keywords.Reconcile))
        {
            ChildReconciler.Reconcile(oldChildren, newChildren, childCollection, this, requestRerender);
            return;
        }
        Diagnostics.ReactorEventSource.Log.ChildReconcileStart(oldChildren.Length, newChildren.Length);
        try { ChildReconciler.Reconcile(oldChildren, newChildren, childCollection, this, requestRerender); }
        finally { Diagnostics.ReactorEventSource.Log.ChildReconcileStop(); }
    }

    /// <summary>
    /// Updates a single child element. Returns non-null if the child control was replaced.
    /// Public so registered type handlers can recursively reconcile children.
    /// </summary>
    public UIElement? UpdateChild(Element oldEl, Element newEl, UIElement control, Action requestRerender)
    {
        return Update(oldEl, newEl, control, requestRerender);
    }

    /// <summary>
    /// Unmounts a child control. Public so registered type handlers can unmount children.
    /// </summary>
    public void UnmountChild(UIElement control)
    {
        Unmount(control);
    }

    // ════════════════════════════════════════════════════════════════════
    //  Unmount
    // ════════════════════════════════════════════════════════════════════

    private void Unmount(UIElement control)
    {
        UnmountRecursive(control);
    }

    private void UnmountRecursive(UIElement control)
    {
        // Capture connected animation snapshot while element is still in the visual tree
        if (control is FrameworkElement caFe && caFe.Tag is Element caEl
            && caEl.ConnectedAnimationKey is not null)
        {
            try
            {
                var service = ConnectedAnimationService.GetForCurrentView();
                service.PrepareToAnimate(caEl.ConnectedAnimationKey, control);
            }
            catch (global::System.Runtime.InteropServices.COMException) { }
            catch (Exception ex) { global::System.Diagnostics.Debug.WriteLine($"[Reactor] ConnectedAnimation PrepareToAnimate failed: {ex}"); }
        }

        // Clean up animation state (mirrors UnmountAndCollect)
        if (control is FrameworkElement animFe && animFe.Tag is Element animEl)
        {
            if (animEl.InteractionStates is not null)
                ClearInteractionStates(control);
            if (animEl.KeyframeAnimations is not null)
                ClearKeyframeAnimations(control, animEl.KeyframeAnimations);
            if (animEl.ScrollAnimation is not null)
                ClearScrollAnimation(control, animEl.ScrollAnimation);
        }

        if (_componentNodes.TryGetValue(control, out var node))
        {
            Diagnostics.ReactorEventSource.Log.ComponentUnmount(
                node.Component?.GetType().Name ?? node.Element?.GetType().Name ?? "unknown");
            node.Component?.Context.RunCleanups();
            node.Context?.RunCleanups();
            _componentNodes.Remove(control);
        }

        _errorBoundaryNodes.Remove(control);

        if (_navigationHostNodes.TryGetValue(control, out var navNode))
        {
            if (navNode.RouteChangedHandler is not null)
                navNode.Handle.RouteChanged -= navNode.RouteChangedHandler;
            navNode.Handle.Detach();
            navNode.Cache?.Clear();
            if (navNode.CurrentChildControl is not null)
                UnmountRecursive(navNode.CurrentChildControl);
            _navigationHostNodes.Remove(control);
            return; // Children already handled above; don't recurse into Grid children again
        }

        // Check registered type unmount handlers via Tag
        if (control is FrameworkElement fe && fe.Tag is Element tagEl
            && _typeRegistry.TryGetValue(tagEl.GetType(), out var reg) && reg.HasUnmount)
        {
            reg.Unmount(control, this);
            return;
        }

        // XamlHostElement children were created outside Reactor's tree —
        // do NOT recurse into them (they may have stale parent references
        // or be types Reactor doesn't know how to clean).
        if (control is FrameworkElement hostFe && hostFe.Tag is XamlHostElement)
        {
            hostFe.Tag = null;
            return;
        }

        // XamlPageElement — clear content to trigger Page.OnNavigatedFrom cleanup
        if (control is WinUI.Frame pageFrame && pageFrame.Tag is XamlPageElement)
        {
            pageFrame.Content = null;
            pageFrame.Tag = null;
            return;
        }

        if (control is WinUI.Panel panel)
        {
            foreach (var child in panel.Children)
                UnmountRecursive(child);
        }
        else if (control is WinUI.Border border && border.Child is not null)
        {
            UnmountRecursive(border.Child);
        }
        else if (control is WinUI.ScrollViewer sv && sv.Content is UIElement svChild)
        {
            UnmountRecursive(svChild);
        }
        else if (control is WinUI.UserControl uc && uc.Content is UIElement ucChild)
        {
            UnmountRecursive(ucChild);
        }
        else if (control is WinUI.ContentControl cc && cc.Content is UIElement ccChild)
        {
            UnmountRecursive(ccChild);
        }
    }

    /// <summary>
    /// Unmounts and returns all descendants + root to the pool.
    /// Call AFTER the root has been detached from the visual tree.
    /// Collects all controls first, then pools bottom-up so DetachFromParent
    /// removes children before parents clear their collections.
    /// </summary>
    /// <summary>
    /// Removes a child from its collection with exit transition support.
    /// If the child has an ElementTransition with an exit side, the removal is deferred
    /// until the exit animation completes. Otherwise, immediate removal.
    /// </summary>
    internal void RemoveChildWithExitTransition(IChildCollection children, int index)
    {
        var control = children.Get(index);
        var transition = (control is FrameworkElement fe && fe.Tag is Element el)
            ? el.ElementTransition : null;

        if (transition?.GetExitTransition() is not null)
        {
            // Defer removal: play exit animation, then remove + pool on completion.
            ApplyExitTransition(control, transition, () =>
            {
                // Find current index — it may have shifted if earlier items were removed.
                for (int i = 0; i < children.Count; i++)
                {
                    if (ReferenceEquals(children.Get(i), control))
                    {
                        children.RemoveAt(i);
                        break;
                    }
                }
                UnmountAndPool(control);
            });
        }
        else
        {
            children.RemoveAt(index);
            UnmountAndPool(control);
        }
    }

    /// <summary>
    /// Replaces a child at an index with exit transition support on the old child.
    /// If the old child has an exit transition, the new child is inserted immediately
    /// and the old child animates out then gets removed. Otherwise, immediate replace.
    /// </summary>
    internal void ReplaceChildWithExitTransition(IChildCollection children, int index, UIElement newControl)
    {
        var oldControl = children.Get(index);
        var transition = (oldControl is FrameworkElement fe && fe.Tag is Element el)
            ? el.ElementTransition : null;

        if (transition?.GetExitTransition() is not null)
        {
            // Replace immediately with the new control so the UI updates.
            children.Replace(index, newControl);
            // Re-insert the old control after the new one for exit animation.
            // It will be positioned by layout but the animation (fade/slide) makes
            // it visually leave. We insert it right after the replacement.
            children.Insert(index + 1, oldControl);
            ApplyExitTransition(oldControl, transition, () =>
            {
                for (int i = 0; i < children.Count; i++)
                {
                    if (ReferenceEquals(children.Get(i), oldControl))
                    {
                        children.RemoveAt(i);
                        break;
                    }
                }
                UnmountAndPool(oldControl);
            });
        }
        else
        {
            Unmount(oldControl);
            children.Replace(index, newControl);
        }
    }

    internal void UnmountAndPool(UIElement control)
    {
        var toPool = new List<FrameworkElement>();
        UnmountAndCollect(control, toPool);

        // Pool top-down: parent's CleanElement calls Children.Clear() which
        // detaches children, so by the time children are pooled they're parentless.
        for (int i = 0; i < toPool.Count; i++)
            _pool.Return(toPool[i]);
    }

    private void UnmountAndCollect(UIElement control, List<FrameworkElement> toPool)
    {
        // Capture connected animation snapshot while element is still in the visual tree
        if (control is FrameworkElement caFe && caFe.Tag is Element caEl
            && caEl.ConnectedAnimationKey is not null)
        {
            try
            {
                var service = ConnectedAnimationService.GetForCurrentView();
                service.PrepareToAnimate(caEl.ConnectedAnimationKey, control);
            }
            catch (global::System.Runtime.InteropServices.COMException) { }
            catch (Exception ex) { global::System.Diagnostics.Debug.WriteLine($"[Reactor] ConnectedAnimation PrepareToAnimate failed: {ex}"); }
        }

        // Clean up animation state
        if (control is FrameworkElement animFe && animFe.Tag is Element animEl)
        {
            if (animEl.InteractionStates is not null)
                ClearInteractionStates(control);
            if (animEl.KeyframeAnimations is not null)
                ClearKeyframeAnimations(control, animEl.KeyframeAnimations);
            if (animEl.ScrollAnimation is not null)
                ClearScrollAnimation(control, animEl.ScrollAnimation);
        }

        // Run cleanup logic (component teardown, etc.)
        if (_componentNodes.TryGetValue(control, out var node))
        {
            Diagnostics.ReactorEventSource.Log.ComponentUnmount(
                node.Component?.GetType().Name ?? node.Element?.GetType().Name ?? "unknown");
            node.Component?.Context.RunCleanups();
            node.Context?.RunCleanups();
            _componentNodes.Remove(control);
        }

        if (control is FrameworkElement fe && fe.Tag is Element tagEl
            && _typeRegistry.TryGetValue(tagEl.GetType(), out var reg) && reg.HasUnmount)
        {
            reg.Unmount(control, this);
            // Collect this control for pooling, but do NOT recurse into children —
            // they were created outside Reactor's tree and must not be pooled.
            // (Mirrors UnmountRecursive which returns early in this case.)
            if (control is FrameworkElement poolCandidate2)
                toPool.Add(poolCandidate2);
            return;
        }

        // Recurse into children.
        if (control is WinUI.Panel panel)
        {
            foreach (var child in panel.Children)
                UnmountAndCollect(child, toPool);
        }
        else if (control is WinUI.Border border && border.Child is not null)
        {
            UnmountAndCollect(border.Child, toPool);
        }
        else if (control is WinUI.ScrollViewer sv && sv.Content is UIElement svChild)
        {
            UnmountAndCollect(svChild, toPool);
        }
        else if (control is WinUI.UserControl uc && uc.Content is UIElement ucChild)
        {
            UnmountAndCollect(ucChild, toPool);
        }
        else if (control is WinUI.ContentControl cc && cc.Content is UIElement ccChild)
        {
            UnmountAndCollect(ccChild, toPool);
            cc.Content = null; // Detach so pooled child has no parent
        }

        if (control is FrameworkElement poolCandidate)
            toPool.Add(poolCandidate);
    }

    // ════════════════════════════════════════════════════════════════════
    //  CanUpdate
    // ════════════════════════════════════════════════════════════════════

    internal bool CanUpdate(Element oldEl, Element newEl)
    {
        if (oldEl.GetType() != newEl.GetType()) return false;
        if (oldEl.Key != newEl.Key) return false;
        if (oldEl is ComponentElement oldComp && newEl is ComponentElement newComp)
            return oldComp.ComponentType == newComp.ComponentType;
        if (oldEl is XamlHostElement oldHost && newEl is XamlHostElement newHost)
            return oldHost.TypeKey == newHost.TypeKey;
        // MemoElement can always update to MemoElement (same type check above handles it)
        return true;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Shared helpers (used by Mount + Update)
    // ════════════════════════════════════════════════════════════════════

    private static bool DepsEqual(object?[] prev, object?[] next)
    {
        if (prev.Length != next.Length) return false;
        for (int i = 0; i < prev.Length; i++)
        {
            if (!Equals(prev[i], next[i])) return false;
        }
        return true;
    }

    internal static void ApplySetters<T>(Action<T>[] setters, T control) where T : class
    {
        foreach (var setter in setters) setter(control);
    }

    // WPF/WinUI auto-populate UIA Name from a control's string Content through the
    // automation peer, but AutomationProperties.GetName on the raw element only
    // returns an explicitly-set attached property. UIA clients that read the attached
    // property directly (our own tree walker, Appium's AutomationName getter, some
    // screen readers that probe before invoking the peer) see an empty string when
    // the author never set .AutomationName(). Mirroring the caption into the
    // attached property at mount makes both lookup paths agree, so
    // click { selector: "[name='+ 1']" } and a screen reader saying "+ 1" both work
    // without the author having to say so twice. Skips when the author already set
    // an AutomationName via modifier or setter — explicit always wins.
    internal static void ApplyDefaultAutomationName(FrameworkElement fe, string? caption)
    {
        if (fe is null) return;
        if (string.IsNullOrWhiteSpace(caption)) return;
        var existing = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(fe);
        if (!string.IsNullOrEmpty(existing)) return;
        var trimmed = caption.Length > 100 ? caption.Substring(0, 100) : caption;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(fe, trimmed);
    }

    // Update variant: a label change ("+ 1" → "+ 2") should update UIA Name as
    // long as the author didn't override it. We can't distinguish "author set
    // it to the previous caption" from "our default set it" without tracking
    // provenance, so the rule is: overwrite when the current value is empty or
    // equals the previous caption — any other value means the author intervened
    // (via modifier or setter) and we leave it alone.
    internal static void UpdateDefaultAutomationName(FrameworkElement fe, string? oldCaption, string? newCaption)
    {
        if (fe is null) return;
        if (string.IsNullOrWhiteSpace(newCaption)) return;
        var current = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(fe);
        bool authorOverride =
            !string.IsNullOrEmpty(current) &&
            (oldCaption is null || !string.Equals(current, oldCaption, StringComparison.Ordinal));
        if (authorOverride) return;
        var trimmed = newCaption.Length > 100 ? newCaption.Substring(0, 100) : newCaption;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(fe, trimmed);
    }

    internal static string? ExtractElementCaption(Element? element) => element switch
    {
        TextBlockElement te => te.Content,
        null => null,
        _ => null,
    };

    // Resolves the caption-bearing string for controls whose visible text is
    // implicit (Button's Label, CheckBox's Label, TextBlock's Content, …). Returns
    // null for elements without a natural caption so ApplyDefaultAutomationName
    // leaves them alone. Kept alongside the helper so adding new caption-bearing
    // controls is one place to change.
    internal static string? ResolveCaptionForElement(Element element) => element switch
    {
        TextBlockElement te => te.Content,
        ButtonElement be => be.Label ?? ExtractElementCaption(be.ContentElement),
        HyperlinkButtonElement hle => hle.Content,
        RepeatButtonElement rbe => rbe.Label,
        ToggleButtonElement tbe => tbe.Label,
        DropDownButtonElement dde => dde.Label,
        SplitButtonElement sbe => sbe.Label,
        ToggleSplitButtonElement tsbe => tsbe.Label,
        CheckBoxElement cbe => cbe.Label,
        RadioButtonElement rbe => rbe.Label,
        ToggleSwitchElement tse => tse.Header as string ?? tse.OnContent ?? tse.OffContent,
        TextFieldElement tfe => tfe.Header as string ?? tfe.Placeholder,
        _ => null,
    };

    internal static void ApplyTransitions(UIElement uie, ImplicitTransitions? implicitT, ThemeTransitions? themeT)
    {
        if (implicitT is not null)
        {
            try
            {
                if (implicitT.Opacity is not null)
                    uie.OpacityTransition = implicitT.Opacity;
                if (implicitT.Rotation is not null)
                    uie.RotationTransition = implicitT.Rotation;
                if (implicitT.Scale is not null)
                    uie.ScaleTransition = implicitT.Scale;
                if (implicitT.Translation is not null)
                    uie.TranslationTransition = implicitT.Translation;
            }
            catch (UnauthorizedAccessException)
            {
                // WinUI blocks XAML implicit transition APIs once GetElementVisual()
                // has been called on the element (e.g., from .Animate(), enter transitions,
                // or a previous owner via the pool). Fall back to compositor implicit
                // animations which always work.
                ApplyTransitionsViaCompositor(uie, implicitT);
            }
            if (implicitT.Background is not null)
            {
                switch (uie)
                {
                    case WinUI.Grid g: g.BackgroundTransition = implicitT.Background; break;
                    case WinUI.StackPanel sp: sp.BackgroundTransition = implicitT.Background; break;
                    case WinUI.ContentPresenter cp: cp.BackgroundTransition = implicitT.Background; break;
                }
            }
        }

        if (themeT?.Children is { Length: > 0 } children)
        {
            var tc = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
            foreach (var t in children) tc.Add(t);
            switch (uie)
            {
                case WinUI.StackPanel sp: sp.ChildrenTransitions = tc; break;
                case WinUI.Grid g: g.ChildrenTransitions = tc; break;
                case WinUI.Canvas c: c.ChildrenTransitions = tc; break;
                case WinUI.Border b: b.ChildTransitions = tc; break;
                case WinUI.ContentPresenter cp: cp.ContentTransitions = tc; break;
                case WinUI.ContentControl cc: cc.ContentTransitions = tc; break;
            }
        }

        if (themeT?.ItemContainer is { Length: > 0 } itemTransitions)
        {
            var tc = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
            foreach (var t in itemTransitions) tc.Add(t);
            if (uie is WinUI.ListViewBase lvb)
                lvb.ItemContainerTransitions = tc;
        }
    }

    /// <summary>
    /// Fallback: applies XAML-style implicit transitions via compositor ImplicitAnimationCollection
    /// when the XAML API is blocked by a prior GetElementVisual() call.
    /// </summary>
    private static void ApplyTransitionsViaCompositor(UIElement uie, ImplicitTransitions implicitT)
    {
        ElementPool.MarkCompositorTainted(uie);
        var visual = ElementCompositionPreview.GetElementVisual(uie);
        var compositor = visual.Compositor;
        var implicitAnimations = visual.ImplicitAnimations ?? compositor.CreateImplicitAnimationCollection();

        if (implicitT.Opacity is not null)
        {
            var dur = implicitT.Opacity.Duration;
            implicitAnimations["Opacity"] = AnimationHelper.CreateScalarImplicitAnimation(
                compositor, "Opacity", new Animation.LinearCurve(dur));
        }
        if (implicitT.Rotation is not null)
        {
            var dur = implicitT.Rotation.Duration;
            implicitAnimations["RotationAngle"] = AnimationHelper.CreateScalarImplicitAnimation(
                compositor, "RotationAngle", new Animation.LinearCurve(dur));
        }
        if (implicitT.Scale is not null)
        {
            var dur = implicitT.Scale.Duration;
            implicitAnimations["Scale"] = AnimationHelper.CreateVector3ImplicitAnimation(
                compositor, "Scale", new Animation.LinearCurve(dur));
        }
        if (implicitT.Translation is not null)
        {
            var dur = implicitT.Translation.Duration;
            implicitAnimations["Offset"] = AnimationHelper.CreateVector3ImplicitAnimation(
                compositor, "Offset", new Animation.LinearCurve(dur));
        }

        visual.ImplicitAnimations = implicitAnimations;
    }

    /// <summary>
    /// Sets up Composition-layer implicit animations on the element's Visual so that
    /// layout-driven Offset (and optionally Size) changes animate smoothly.
    /// Runs entirely on the Composition thread — zero managed-code callbacks during animation.
    /// </summary>
    internal static void ApplyLayoutAnimation(UIElement uie, LayoutAnimationConfig config)
    {
        ElementPool.MarkCompositorTainted(uie);
        var visual = ElementCompositionPreview.GetElementVisual(uie);
        var compositor = visual.Compositor;

        var implicitAnimations = compositor.CreateImplicitAnimationCollection();

        if (config.AnimateOffset)
        {
            if (config.UseSpring)
            {
                var spring = compositor.CreateSpringVector3Animation();
                spring.DampingRatio = config.DampingRatio;
                spring.Period = TimeSpan.FromSeconds(config.Period);
                spring.Target = "Offset";
                implicitAnimations["Offset"] = spring;
            }
            else
            {
                var anim = compositor.CreateVector3KeyFrameAnimation();
                anim.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
                anim.Duration = config.Duration;
                anim.Target = "Offset";
                implicitAnimations["Offset"] = anim;
            }
        }

        if (config.AnimateSize)
        {
            if (config.UseSpring)
            {
                var spring = compositor.CreateSpringVector2Animation();
                spring.DampingRatio = config.DampingRatio;
                spring.Period = TimeSpan.FromSeconds(config.Period);
                spring.Target = "Size";
                implicitAnimations["Size"] = spring;
            }
            else
            {
                var anim = compositor.CreateVector2KeyFrameAnimation();
                anim.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
                anim.Duration = config.Duration;
                anim.Target = "Size";
                implicitAnimations["Size"] = anim;
            }
        }

        visual.ImplicitAnimations = implicitAnimations;
    }

    /// <summary>
    /// Clears Composition-layer implicit animations from an element's Visual.
    /// Called when an element previously had LayoutAnimation but no longer does
    /// (e.g., after a state change removes the modifier, or a pooled control is reused).
    /// </summary>
    internal static void ClearLayoutAnimation(UIElement uie)
    {
        var visual = ElementCompositionPreview.GetElementVisual(uie);
        visual.ImplicitAnimations = null;
    }

    // ════════════════════════════════════════════════════════════════
    //  Property animation (.Animate() modifier)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates ImplicitAnimationCollection entries on the element's composition Visual
    /// for the targeted properties using the specified Curve. Merges with existing
    /// layout animation entries (Offset/Size) to avoid overwriting each other.
    /// </summary>
    internal static void ApplyPropertyAnimation(UIElement uie, AnimationConfig config, LayoutAnimationConfig? layoutConfig)
    {
        ElementPool.MarkCompositorTainted(uie);
        var visual = ElementCompositionPreview.GetElementVisual(uie);
        var compositor = visual.Compositor;

        // Start from existing implicit animations (layout may have set Offset/Size)
        // or create a new collection
        var implicitAnimations = visual.ImplicitAnimations ?? compositor.CreateImplicitAnimationCollection();

        var props = config.Properties;
        var curve = config.Curve;

        if (props.HasFlag(AnimateProperty.Opacity))
            implicitAnimations["Opacity"] = AnimationHelper.CreateScalarImplicitAnimation(compositor, "Opacity", curve);

        if (props.HasFlag(AnimateProperty.Offset))
        {
            // Only add Offset if layout animation hasn't already claimed it
            if (layoutConfig is null || !layoutConfig.AnimateOffset)
                implicitAnimations["Offset"] = AnimationHelper.CreateVector3ImplicitAnimation(compositor, "Offset", curve);
        }

        if (props.HasFlag(AnimateProperty.Scale))
            implicitAnimations["Scale"] = AnimationHelper.CreateVector3ImplicitAnimation(compositor, "Scale", curve);

        if (props.HasFlag(AnimateProperty.Rotation))
            implicitAnimations["RotationAngle"] = AnimationHelper.CreateScalarImplicitAnimation(compositor, "RotationAngle", curve);

        if (props.HasFlag(AnimateProperty.CenterPoint))
            implicitAnimations["CenterPoint"] = AnimationHelper.CreateVector3ImplicitAnimation(compositor, "CenterPoint", curve);

        visual.ImplicitAnimations = implicitAnimations;
    }

    /// <summary>
    /// Clears property animation entries from an element's Visual's ImplicitAnimationCollection.
    /// Preserves layout animation entries if they exist.
    /// </summary>
    internal static void ClearPropertyAnimation(UIElement uie, LayoutAnimationConfig? layoutConfig)
    {
        if (layoutConfig is null)
        {
            // No layout animation either — clear everything
            var visual = ElementCompositionPreview.GetElementVisual(uie);
            visual.ImplicitAnimations = null;
        }
        // If layout animation exists, ApplyLayoutAnimation will recreate the collection
        // with just layout entries on next update
    }

    // ════════════════════════════════════════════════════════════════
    //  Enter/exit transitions (.Transition() modifier)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Applies enter transition on mount: sets initial visual state then animates to final state.
    /// </summary>
    internal static void ApplyEnterTransition(UIElement uie, ElementTransition transition, int staggerIndex = 0, TimeSpan staggerDelay = default)
    {
        var enter = transition.GetEnterTransition();
        if (enter is null) return;
        ElementPool.MarkCompositorTainted(uie);

        var visual = ElementCompositionPreview.GetElementVisual(uie);
        var compositor = visual.Compositor;
        var curve = transition.Curve ?? Curve.Ease(300, Easing.Decelerate);

        // Override with ambient scope if present
        if (AnimationScope.HasScope && AnimationScope.Current is not null)
            curve = AnimationScope.Current;

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var delay = staggerIndex > 0 ? TimeSpan.FromTicks(staggerDelay.Ticks * staggerIndex) : TimeSpan.Zero;

        ApplyTransitionAnimations(uie, visual, compositor, enter, curve, isEnter: true, delay);

        batch.End();
    }

    /// <summary>
    /// Applies exit transition before unmount: animates out, then invokes onComplete to remove/pool.
    /// </summary>
    internal void ApplyExitTransition(UIElement uie, ElementTransition transition, Action onComplete, int staggerIndex = 0, TimeSpan staggerDelay = default)
    {
        var exit = transition.GetExitTransition();
        if (exit is null) { onComplete(); return; }
        ElementPool.MarkCompositorTainted(uie);

        var visual = ElementCompositionPreview.GetElementVisual(uie);
        var compositor = visual.Compositor;
        var curve = transition.Curve ?? Curve.Ease(300, Easing.Decelerate);

        if (AnimationScope.HasScope && AnimationScope.Current is not null)
            curve = AnimationScope.Current;

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var delay = staggerIndex > 0 ? TimeSpan.FromTicks(staggerDelay.Ticks * staggerIndex) : TimeSpan.Zero;

        ApplyTransitionAnimations(uie, visual, compositor, exit, curve, isEnter: false, delay);

        batch.End();
        batch.Completed += (_, _) =>
        {
            // Reset visual state after exit
            visual.Opacity = 1;
            visual.Offset = Vector3.Zero;
            visual.Scale = Vector3.One;
            onComplete();
        };
    }

    private static void ApplyTransitionAnimations(
        UIElement uie, Visual visual, Compositor compositor,
        Animation.Transition transition, Curve curve, bool isEnter, TimeSpan delay)
    {
        switch (transition)
        {
            case FadeTransition:
                ApplyFadeTransition(uie, compositor, curve, isEnter, delay);
                break;
            case SlideTransition slide:
                ApplySlideTransition(uie, visual, compositor, slide.Edge, curve, isEnter, delay);
                break;
            case ScaleTransition scale:
                ApplyScaleTransition(uie, visual, compositor, scale.From, curve, isEnter, delay);
                break;
            case CombinedTransition combined:
                ApplyTransitionAnimations(uie, visual, compositor, combined.First, curve, isEnter, delay);
                ApplyTransitionAnimations(uie, visual, compositor, combined.Second, curve, isEnter, delay);
                break;
            case AsymmetricTransition asym:
                var inner = isEnter ? asym.EnterTransition : asym.ExitTransition;
                if (inner is not null)
                    ApplyTransitionAnimations(uie, visual, compositor, inner, curve, isEnter, delay);
                break;
            case DirectionalTransition dir:
                var dirInner = isEnter ? dir.EnterTransition : dir.ExitTransition;
                if (dirInner is not null)
                    ApplyTransitionAnimations(uie, visual, compositor, dirInner, curve, isEnter, delay);
                break;
        }
    }

    private static void ApplyFadeTransition(UIElement uie, Compositor compositor, Curve curve, bool isEnter, TimeSpan delay)
    {
        var visual = ElementCompositionPreview.GetElementVisual(uie);
        if (isEnter)
        {
            visual.Opacity = 0;
            var anim = AnimationHelper.CreateScalarTargetAnimation(compositor, 1.0f, curve);
            if (delay > TimeSpan.Zero) AnimationHelper.SetDelay(anim, delay);
            visual.StartAnimation("Opacity", anim);
        }
        else
        {
            var anim = AnimationHelper.CreateScalarTargetAnimation(compositor, 0f, curve);
            if (delay > TimeSpan.Zero) AnimationHelper.SetDelay(anim, delay);
            visual.StartAnimation("Opacity", anim);
        }
    }

    private static void ApplySlideTransition(UIElement uie, Visual visual, Compositor compositor, Edge edge, Curve curve, bool isEnter, TimeSpan delay)
    {
        var slideDistance = 40f;
        var offset = edge switch
        {
            Edge.Left => new Vector3(-slideDistance, 0, 0),
            Edge.Top => new Vector3(0, -slideDistance, 0),
            Edge.Right => new Vector3(slideDistance, 0, 0),
            Edge.Bottom => new Vector3(0, slideDistance, 0),
            _ => new Vector3(0, slideDistance, 0),
        };

        if (isEnter)
        {
            visual.Offset = offset;
            var anim = AnimationHelper.CreateVector3TargetAnimation(compositor, Vector3.Zero, curve);
            if (delay > TimeSpan.Zero) AnimationHelper.SetDelay(anim, delay);
            visual.StartAnimation("Offset", anim);
        }
        else
        {
            var anim = AnimationHelper.CreateVector3TargetAnimation(compositor, offset, curve);
            if (delay > TimeSpan.Zero) AnimationHelper.SetDelay(anim, delay);
            visual.StartAnimation("Offset", anim);
        }
    }

    private static void ApplyScaleTransition(UIElement uie, Visual visual, Compositor compositor, float from, Curve curve, bool isEnter, TimeSpan delay)
    {
        if (isEnter)
        {
            visual.Scale = new Vector3(from, from, 1f);
            var anim = AnimationHelper.CreateVector3TargetAnimation(compositor, Vector3.One, curve);
            if (delay > TimeSpan.Zero) AnimationHelper.SetDelay(anim, delay);
            visual.StartAnimation("Scale", anim);
        }
        else
        {
            var anim = AnimationHelper.CreateVector3TargetAnimation(compositor, new Vector3(from, from, 1f), curve);
            if (delay > TimeSpan.Zero) AnimationHelper.SetDelay(anim, delay);
            visual.StartAnimation("Scale", anim);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Interaction states (.InteractionStates() modifier)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// State tracking for elements with InteractionStates — stores current state and cached animations.
    /// Stored on FrameworkElement.Tag cannot be used (already used for Element reference),
    /// so we use a static dictionary keyed by UIElement.
    /// </summary>
    private static readonly Dictionary<UIElement, InteractionStateTracker> _interactionTrackers = new();

    private sealed class InteractionStateTracker
    {
        public InteractionState CurrentState;
        public InteractionStatesConfig Config = null!;
        public Brush? NormalBackground;
        public Brush? NormalForeground;
        public Brush? NormalBorderBrush;
    }

    private enum InteractionState { Normal, PointerOver, Pressed, Focused }

    /// <summary>
    /// Sets up or updates InteractionStates on an element. Registers pointer event handlers
    /// on first setup; updates cached config on subsequent calls.
    /// </summary>
    internal static void ApplyInteractionStates(UIElement uie, InteractionStatesConfig config)
    {
        ElementPool.MarkCompositorTainted(uie);
        if (!_interactionTrackers.TryGetValue(uie, out var tracker))
        {
            tracker = new InteractionStateTracker();
            _interactionTrackers[uie] = tracker;

            // Capture normal brush values
            if (uie is FrameworkElement fe)
            {
                tracker.NormalBackground = fe switch
                {
                    WinUI.Panel p => p.Background,
                    WinUI.Control c => c.Background,
                    WinUI.Border b => b.Background,
                    _ => null,
                };
                tracker.NormalForeground = fe switch
                {
                    WinUI.Control c => c.Foreground,
                    TextBlock tb => tb.Foreground,
                    _ => null,
                };
                tracker.NormalBorderBrush = fe switch
                {
                    WinUI.Control c => c.BorderBrush,
                    WinUI.Border b => b.BorderBrush,
                    _ => null,
                };
            }

            // Register handlers
            uie.PointerEntered += OnInteractionPointerEntered;
            uie.PointerExited += OnInteractionPointerExited;
            uie.PointerPressed += OnInteractionPointerPressed;
            uie.PointerReleased += OnInteractionPointerReleased;
            uie.PointerCaptureLost += OnInteractionPointerCaptureLost;
            uie.GotFocus += OnInteractionGotFocus;
            uie.LostFocus += OnInteractionLostFocus;
        }

        tracker.Config = config;
    }

    /// <summary>
    /// Removes InteractionStates from an element, unregistering handlers and clearing state.
    /// </summary>
    internal static void ClearInteractionStates(UIElement uie)
    {
        if (!_interactionTrackers.Remove(uie)) return;

        uie.PointerEntered -= OnInteractionPointerEntered;
        uie.PointerExited -= OnInteractionPointerExited;
        uie.PointerPressed -= OnInteractionPointerPressed;
        uie.PointerReleased -= OnInteractionPointerReleased;
        uie.PointerCaptureLost -= OnInteractionPointerCaptureLost;
        uie.GotFocus -= OnInteractionGotFocus;
        uie.LostFocus -= OnInteractionLostFocus;
    }

    private static void OnInteractionPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is UIElement uie && _interactionTrackers.TryGetValue(uie, out var tracker))
            TransitionToState(uie, tracker, InteractionState.PointerOver);
    }

    private static void OnInteractionPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is UIElement uie && _interactionTrackers.TryGetValue(uie, out var tracker))
            TransitionToState(uie, tracker, InteractionState.Normal);
    }

    private static void OnInteractionPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is UIElement uie && _interactionTrackers.TryGetValue(uie, out var tracker))
            TransitionToState(uie, tracker, InteractionState.Pressed);
    }

    private static void OnInteractionPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is UIElement uie && _interactionTrackers.TryGetValue(uie, out var tracker))
        {
            // Released goes back to PointerOver (pointer is still over the element)
            TransitionToState(uie, tracker, InteractionState.PointerOver);
        }
    }

    private static void OnInteractionPointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is UIElement uie && _interactionTrackers.TryGetValue(uie, out var tracker))
            TransitionToState(uie, tracker, InteractionState.Normal);
    }

    private static void OnInteractionGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement uie && _interactionTrackers.TryGetValue(uie, out var tracker)
            && tracker.Config.Focused is not null)
            TransitionToState(uie, tracker, InteractionState.Focused);
    }

    private static void OnInteractionLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement uie && _interactionTrackers.TryGetValue(uie, out var tracker))
            TransitionToState(uie, tracker, InteractionState.Normal);
    }

    private static void TransitionToState(UIElement uie, InteractionStateTracker tracker, InteractionState newState)
    {
        if (tracker.CurrentState == newState) return;
        tracker.CurrentState = newState;

        var config = tracker.Config;
        var curve = config.Curve ?? Curve.Ease(200, Easing.Standard);

        // Resolve effective state values (Pressed inherits from PointerOver)
        var values = newState switch
        {
            InteractionState.PointerOver => config.PointerOver,
            InteractionState.Pressed => MergePressed(config.PointerOver, config.Pressed),
            InteractionState.Focused => config.Focused,
            _ => null, // Normal
        };

        var visual = ElementCompositionPreview.GetElementVisual(uie);
        var compositor = visual.Compositor;

        // Compositor properties — animate via Visual
        var targetOpacity = values?.Opacity ?? 1.0f;
        var opacityAnim = AnimationHelper.CreateScalarTargetAnimation(compositor, targetOpacity, curve);
        visual.StartAnimation("Opacity", opacityAnim);

        if (values?.Scale.HasValue == true || values?.ScaleV.HasValue == true || newState == InteractionState.Normal)
        {
            var targetScale = values?.ScaleV ?? (values?.Scale.HasValue == true ? new Vector3(values.Scale.Value, values.Scale.Value, 1f) : Vector3.One);
            var scaleAnim = AnimationHelper.CreateVector3TargetAnimation(compositor, targetScale, curve);
            visual.StartAnimation("Scale", scaleAnim);
        }

        if (values?.Translation.HasValue == true || newState == InteractionState.Normal)
        {
            var targetTranslation = values?.Translation ?? Vector3.Zero;
            var translationAnim = AnimationHelper.CreateVector3TargetAnimation(compositor, targetTranslation, curve);
            visual.StartAnimation("Offset", translationAnim);
        }

        if (values?.Rotation.HasValue == true || newState == InteractionState.Normal)
        {
            var targetRotation = values?.Rotation ?? 0f;
            var rotationAnim = AnimationHelper.CreateScalarTargetAnimation(compositor, targetRotation, curve);
            visual.StartAnimation("RotationAngle", rotationAnim);
        }

        // Brush properties — direct set
        if (uie is FrameworkElement fe)
        {
            var bg = values?.Background ?? tracker.NormalBackground;
            if (bg is not null)
            {
                if (fe is WinUI.Panel p) p.Background = bg;
                else if (fe is WinUI.Control c) c.Background = bg;
                else if (fe is WinUI.Border b) b.Background = bg;
            }

            var fg = values?.Foreground ?? tracker.NormalForeground;
            if (fg is not null)
            {
                if (fe is WinUI.Control c) c.Foreground = fg;
                else if (fe is TextBlock tb) tb.Foreground = fg;
            }

            var bb = values?.BorderBrush ?? tracker.NormalBorderBrush;
            if (bb is not null)
            {
                if (fe is WinUI.Control c) c.BorderBrush = bb;
                else if (fe is WinUI.Border b) b.BorderBrush = bb;
            }
        }
    }

    private static InteractionStateValues? MergePressed(InteractionStateValues? pointerOver, InteractionStateValues? pressed)
    {
        if (pressed is null) return pointerOver;
        if (pointerOver is null) return pressed;

        // Pressed inherits unoverridden values from PointerOver
        return new InteractionStateValues(
            Opacity: pressed.Opacity ?? pointerOver.Opacity,
            Scale: pressed.Scale ?? pointerOver.Scale,
            ScaleV: pressed.ScaleV ?? pointerOver.ScaleV,
            Translation: pressed.Translation ?? pointerOver.Translation,
            Rotation: pressed.Rotation ?? pointerOver.Rotation,
            Background: pressed.Background ?? pointerOver.Background,
            Foreground: pressed.Foreground ?? pointerOver.Foreground,
            BorderBrush: pressed.BorderBrush ?? pointerOver.BorderBrush);
    }

    // ════════════════════════════════════════════════════════════════
    //  Stagger animation (DelayTime on child animations)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Applies stagger delays to an element's Visual's implicit animations.
    /// Called after layout/property animations are set up on children.
    /// </summary>
    internal static void ApplyStaggerDelays(UIElement parent, StaggerConfig config)
    {
        if (parent is not WinUI.Panel panel) return;

        for (int i = 0; i < panel.Children.Count; i++)
        {
            var child = panel.Children[i];
            var visual = ElementCompositionPreview.GetElementVisual(child);
            if (visual.ImplicitAnimations is null) continue;

            var delay = TimeSpan.FromTicks(config.Delay.Ticks * i);
            foreach (var key in new[] { "Offset", "Opacity", "Scale", "RotationAngle", "Size", "CenterPoint" })
            {
                try
                {
                    var anim = visual.ImplicitAnimations[key];
                    if (anim is KeyFrameAnimation kfa) kfa.DelayTime = delay;
                    else if (anim is SpringScalarNaturalMotionAnimation ssa) ssa.DelayTime = delay;
                    else if (anim is SpringVector3NaturalMotionAnimation sva) sva.DelayTime = delay;
                }
                catch { /* Key not present in collection — skip */ }
            }
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Keyframe animation (.Keyframes() modifier)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tracks previous trigger values for keyframe animations, keyed by (UIElement, name).
    /// </summary>
    private static readonly Dictionary<(UIElement, string), object?> _keyframeTriggerValues = new();

    /// <summary>
    /// Checks trigger values and starts keyframe animations when they change.
    /// </summary>
    internal static void ApplyKeyframeAnimations(UIElement uie, KeyframeEntry[] entries)
    {
        ElementPool.MarkCompositorTainted(uie);
        var visual = ElementCompositionPreview.GetElementVisual(uie);
        var compositor = visual.Compositor;

        foreach (var entry in entries)
        {
            var key = (uie, entry.Name);
            var changed = false;

            if (_keyframeTriggerValues.TryGetValue(key, out var prevTrigger))
                changed = !Equals(prevTrigger, entry.Trigger);
            else
                changed = true; // First mount

            _keyframeTriggerValues[key] = entry.Trigger;

            if (!changed) continue;

            var def = entry.Definition;
            var group = compositor.CreateAnimationGroup();

            // Create per-property keyframe animations
            bool hasOpacity = false, hasScale = false, hasTranslation = false, hasRotation = false;
            foreach (var kf in def.Keyframes)
            {
                if (kf.Opacity.HasValue) hasOpacity = true;
                if (kf.Scale.HasValue) hasScale = true;
                if (kf.Translation.HasValue) hasTranslation = true;
                if (kf.Rotation.HasValue) hasRotation = true;
            }

            if (hasOpacity)
            {
                var anim = compositor.CreateScalarKeyFrameAnimation();
                anim.Duration = def.Duration;
                anim.Target = "Opacity";
                if (def.Loop) anim.IterationBehavior = AnimationIterationBehavior.Forever;
                foreach (var kf in def.Keyframes)
                {
                    if (!kf.Opacity.HasValue) continue;
                    if (kf.Easing.HasValue)
                    {
                        var e = kf.Easing.Value;
                        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(e.X1, e.Y1), new Vector2(e.X2, e.Y2));
                        anim.InsertKeyFrame(kf.Progress, kf.Opacity.Value, easing);
                    }
                    else
                        anim.InsertKeyFrame(kf.Progress, kf.Opacity.Value);
                }
                group.Add(anim);
            }

            if (hasScale)
            {
                var anim = compositor.CreateVector3KeyFrameAnimation();
                anim.Duration = def.Duration;
                anim.Target = "Scale";
                if (def.Loop) anim.IterationBehavior = AnimationIterationBehavior.Forever;
                foreach (var kf in def.Keyframes)
                {
                    if (!kf.Scale.HasValue) continue;
                    if (kf.Easing.HasValue)
                    {
                        var e = kf.Easing.Value;
                        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(e.X1, e.Y1), new Vector2(e.X2, e.Y2));
                        anim.InsertKeyFrame(kf.Progress, kf.Scale.Value, easing);
                    }
                    else
                        anim.InsertKeyFrame(kf.Progress, kf.Scale.Value);
                }
                group.Add(anim);
            }

            if (hasTranslation)
            {
                var anim = compositor.CreateVector3KeyFrameAnimation();
                anim.Duration = def.Duration;
                anim.Target = "Offset";
                if (def.Loop) anim.IterationBehavior = AnimationIterationBehavior.Forever;
                foreach (var kf in def.Keyframes)
                {
                    if (!kf.Translation.HasValue) continue;
                    if (kf.Easing.HasValue)
                    {
                        var e = kf.Easing.Value;
                        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(e.X1, e.Y1), new Vector2(e.X2, e.Y2));
                        anim.InsertKeyFrame(kf.Progress, kf.Translation.Value, easing);
                    }
                    else
                        anim.InsertKeyFrame(kf.Progress, kf.Translation.Value);
                }
                group.Add(anim);
            }

            if (hasRotation)
            {
                var anim = compositor.CreateScalarKeyFrameAnimation();
                anim.Duration = def.Duration;
                anim.Target = "RotationAngle";
                if (def.Loop) anim.IterationBehavior = AnimationIterationBehavior.Forever;
                foreach (var kf in def.Keyframes)
                {
                    if (!kf.Rotation.HasValue) continue;
                    if (kf.Easing.HasValue)
                    {
                        var e = kf.Easing.Value;
                        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(e.X1, e.Y1), new Vector2(e.X2, e.Y2));
                        anim.InsertKeyFrame(kf.Progress, kf.Rotation.Value, easing);
                    }
                    else
                        anim.InsertKeyFrame(kf.Progress, kf.Rotation.Value);
                }
                group.Add(anim);
            }

            // Start the animation group via Visual
            foreach (CompositionAnimation anim in group)
                visual.StartAnimation(anim.Target, anim);
        }
    }

    internal static void ClearKeyframeAnimations(UIElement uie, KeyframeEntry[] entries)
    {
        foreach (var entry in entries)
            _keyframeTriggerValues.Remove((uie, entry.Name));

        // Stop composition animations so they don't keep running on pooled/unmounted controls.
        // We stop all four possible targets since KeyframeAnimationDef doesn't track which
        // targets were started — StopAnimation is a no-op if no animation is running on that property.
        try
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(uie);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Scale");
            visual.StopAnimation("Offset");
            visual.StopAnimation("RotationAngle");
        }
        catch { /* No compositor (e.g. unit tests) */ }
    }

    // ════════════════════════════════════════════════════════════════
    //  Scroll-linked expression animation (.ScrollLinked() modifier)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Applies scroll-linked expression animations to an element's Visual.
    /// </summary>
    internal static void ApplyScrollAnimation(UIElement uie, ScrollAnimationConfig config)
    {
        if (config.ScrollViewer is null) return;
        ElementPool.MarkCompositorTainted(uie);
        var visual = ElementCompositionPreview.GetElementVisual(uie);
        var compositor = visual.Compositor;
        var scrollPropertySet = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(config.ScrollViewer);

        foreach (var expr in config.Expressions)
        {
            var animation = compositor.CreateExpressionAnimation(expr.Expression);
            animation.SetReferenceParameter("scroll", scrollPropertySet);
            visual.StartAnimation(expr.Property, animation);
        }
    }

    internal static void ClearScrollAnimation(UIElement uie, ScrollAnimationConfig config)
    {
        var visual = ElementCompositionPreview.GetElementVisual(uie);
        foreach (var expr in config.Expressions)
            visual.StopAnimation(expr.Property);
    }

    // ════════════════════════════════════════════════════════════════
    //  Connected animations (cross-container transitions)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Queues a connected animation start for an element that was just mounted.
    /// Called from Mount() when the element has a ConnectedAnimationKey and a
    /// prepared animation exists with that key.
    /// </summary>
    internal void QueueConnectedAnimationStart(UIElement target, string key)
    {
        try
        {
            var service = ConnectedAnimationService.GetForCurrentView();
            var anim = service.GetAnimation(key);
            if (anim is not null)
                _pendingConnectedAnimationStarts.Add((anim, target));
        }
        catch (global::System.Runtime.InteropServices.COMException) { }
        catch (Exception ex) { global::System.Diagnostics.Debug.WriteLine($"[Reactor] ConnectedAnimation GetAnimation failed: {ex}"); }
    }

    /// <summary>
    /// Starts all queued connected animations. Call AFTER the new tree has been
    /// attached to the visual tree (e.g., after Window.Content = newControl).
    /// </summary>
    public void FlushConnectedAnimations()
    {
        if (_pendingConnectedAnimationStarts.Count == 0) return;

        foreach (var (anim, target) in _pendingConnectedAnimationStarts)
        {
            try { anim.TryStart(target); }
            catch (global::System.Runtime.InteropServices.COMException) { }
            catch (Exception ex) { global::System.Diagnostics.Debug.WriteLine($"[Reactor] ConnectedAnimation TryStart failed: {ex}"); }
        }
        _pendingConnectedAnimationStarts.Clear();
    }

    internal void ApplyModifiers(FrameworkElement fe, ElementModifiers m, Action requestRerender)
        => ApplyModifiers(fe, null, m, requestRerender);

    internal void ApplyModifiers(FrameworkElement fe, ElementModifiers? oldM, ElementModifiers m, Action requestRerender)
    {
        // Guard each property: only call into WinUI when the value actually changed.
        // Each WinUI property set is a managed→native interop call, so avoiding
        // unnecessary sets is critical for large element counts.

        // RequestedTheme must be set BEFORE ApplyThemeBindings so that ThemeRef
        // bindings resolve against the correct theme variant.
        if (m.RequestedTheme.HasValue && m.RequestedTheme != oldM?.RequestedTheme)
            fe.RequestedTheme = m.RequestedTheme.Value;
        else if (!m.RequestedTheme.HasValue && oldM?.RequestedTheme.HasValue == true)
            fe.ClearValue(FrameworkElement.RequestedThemeProperty);

        // Apply physical margin, then overlay logical (BiDi-aware) inline margin
        var resolvedMargin = m.Margin ?? oldM?.Margin;
        if (m.MarginInlineStart.HasValue || m.MarginInlineEnd.HasValue)
        {
            var isRtl = fe.FlowDirection == FlowDirection.RightToLeft;
            var baseMargin = resolvedMargin ?? fe.Margin;
            var left = isRtl ? (m.MarginInlineEnd ?? baseMargin.Left) : (m.MarginInlineStart ?? baseMargin.Left);
            var right = isRtl ? (m.MarginInlineStart ?? baseMargin.Right) : (m.MarginInlineEnd ?? baseMargin.Right);
            resolvedMargin = new Thickness(left, baseMargin.Top, right, baseMargin.Bottom);
        }
        if (resolvedMargin.HasValue && resolvedMargin != oldM?.Margin) fe.Margin = resolvedMargin.Value;
        else if (!resolvedMargin.HasValue && oldM?.Margin.HasValue == true) fe.Margin = new Thickness(0);

        // Apply physical padding, then overlay logical (BiDi-aware) inline padding
        var resolvedPadding = m.Padding ?? oldM?.Padding;
        if (m.PaddingInlineStart.HasValue || m.PaddingInlineEnd.HasValue)
        {
            var isRtl = fe.FlowDirection == FlowDirection.RightToLeft;
            var basePad = resolvedPadding ?? (fe is WinUI.Control pc ? pc.Padding : fe is WinUI.Border pb ? pb.Padding : fe is WinUI.StackPanel psp ? psp.Padding : new Thickness());
            var left = isRtl ? (m.PaddingInlineEnd ?? basePad.Left) : (m.PaddingInlineStart ?? basePad.Left);
            var right = isRtl ? (m.PaddingInlineStart ?? basePad.Right) : (m.PaddingInlineEnd ?? basePad.Right);
            resolvedPadding = new Thickness(left, basePad.Top, right, basePad.Bottom);
        }
        if (resolvedPadding.HasValue && resolvedPadding != oldM?.Padding)
        {
            if (fe is WinUI.Control padCtrl) padCtrl.Padding = resolvedPadding.Value;
            else if (fe is WinUI.Border padBdr) padBdr.Padding = resolvedPadding.Value;
            else if (fe is WinUI.StackPanel padSp) padSp.Padding = resolvedPadding.Value;
        }
        else if (!resolvedPadding.HasValue && oldM?.Padding.HasValue == true)
        {
            if (fe is WinUI.Control padCtrl) padCtrl.Padding = new Thickness(0);
            else if (fe is WinUI.Border padBdr) padBdr.Padding = new Thickness(0);
            else if (fe is WinUI.StackPanel padSp) padSp.Padding = new Thickness(0);
        }
        if (m.Width.HasValue && m.Width != oldM?.Width) fe.Width = m.Width.Value;
        else if (!m.Width.HasValue && oldM?.Width.HasValue == true) fe.Width = double.NaN;
        if (m.Height.HasValue && m.Height != oldM?.Height) fe.Height = m.Height.Value;
        else if (!m.Height.HasValue && oldM?.Height.HasValue == true) fe.Height = double.NaN;
        if (m.MinWidth.HasValue && m.MinWidth != oldM?.MinWidth) fe.MinWidth = m.MinWidth.Value;
        else if (!m.MinWidth.HasValue && oldM?.MinWidth.HasValue == true) fe.MinWidth = 0;
        if (m.MinHeight.HasValue && m.MinHeight != oldM?.MinHeight) fe.MinHeight = m.MinHeight.Value;
        else if (!m.MinHeight.HasValue && oldM?.MinHeight.HasValue == true) fe.MinHeight = 0;
        if (m.MaxWidth.HasValue && m.MaxWidth != oldM?.MaxWidth) fe.MaxWidth = m.MaxWidth.Value;
        else if (!m.MaxWidth.HasValue && oldM?.MaxWidth.HasValue == true) fe.MaxWidth = double.PositiveInfinity;
        if (m.MaxHeight.HasValue && m.MaxHeight != oldM?.MaxHeight) fe.MaxHeight = m.MaxHeight.Value;
        else if (!m.MaxHeight.HasValue && oldM?.MaxHeight.HasValue == true) fe.MaxHeight = double.PositiveInfinity;
        if (m.HorizontalAlignment.HasValue && m.HorizontalAlignment != oldM?.HorizontalAlignment) fe.HorizontalAlignment = m.HorizontalAlignment.Value;
        else if (!m.HorizontalAlignment.HasValue && oldM?.HorizontalAlignment.HasValue == true) fe.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (m.VerticalAlignment.HasValue && m.VerticalAlignment != oldM?.VerticalAlignment) fe.VerticalAlignment = m.VerticalAlignment.Value;
        else if (!m.VerticalAlignment.HasValue && oldM?.VerticalAlignment.HasValue == true) fe.VerticalAlignment = VerticalAlignment.Stretch;
        if (m.Opacity.HasValue && m.Opacity != oldM?.Opacity)
            AnimationHelper.SetOrAnimate(fe, "Opacity", (float)m.Opacity.Value);
        else if (!m.Opacity.HasValue && oldM?.Opacity.HasValue == true)
            fe.Opacity = 1.0;
        if (m.Scale.HasValue && m.Scale != oldM?.Scale)
            AnimationHelper.SetOrAnimateVector3(fe, "Scale", m.Scale.Value);
        if (m.Rotation.HasValue && m.Rotation != oldM?.Rotation)
            AnimationHelper.SetOrAnimate(fe, "Rotation", m.Rotation.Value);
        if (m.Translation.HasValue && m.Translation != oldM?.Translation)
            AnimationHelper.SetOrAnimateVector3(fe, "Translation", m.Translation.Value);
        if (m.CenterPoint.HasValue && m.CenterPoint != oldM?.CenterPoint)
            AnimationHelper.SetOrAnimateVector3(fe, "CenterPoint", m.CenterPoint.Value);
        if (m.IsVisible.HasValue && m.IsVisible != oldM?.IsVisible)
            fe.Visibility = m.IsVisible.Value ? Visibility.Visible : Visibility.Collapsed;
        else if (!m.IsVisible.HasValue && oldM?.IsVisible.HasValue == true)
            fe.Visibility = Visibility.Visible;
        if (m.RichToolTip is not null)
        {
            var oldTipEl = oldM?.RichToolTip;
            var existingTip = WinUI.ToolTipService.GetToolTip(fe) as UIElement;
            if (oldTipEl is not null && existingTip is not null && CanUpdate(oldTipEl, m.RichToolTip))
            {
                var replacement = Update(oldTipEl, m.RichToolTip, existingTip, requestRerender);
                if (replacement is not null)
                    WinUI.ToolTipService.SetToolTip(fe, replacement);
            }
            else
            {
                WinUI.ToolTipService.SetToolTip(fe, Mount(m.RichToolTip, requestRerender));
            }
        }
        else if (m.ToolTip is not null && m.ToolTip != oldM?.ToolTip)
            WinUI.ToolTipService.SetToolTip(fe, m.ToolTip);
        else if (m.RichToolTip is null && m.ToolTip is null && (oldM?.RichToolTip is not null || oldM?.ToolTip is not null))
            fe.ClearValue(WinUI.ToolTipService.ToolTipProperty);

        if (m.AttachedFlyout is not null)
            ApplyFlyoutAttachment(fe, oldM?.AttachedFlyout, m.AttachedFlyout, requestRerender);

        if (m.ContextFlyout is not null)
        {
            var oldContextEl = oldM?.ContextFlyout;
            if (oldContextEl is not null && fe.ContextFlyout is WinPrim.FlyoutBase existingCtx)
                UpdateFlyoutInPlace(existingCtx, oldContextEl, m.ContextFlyout, requestRerender);
            else
                fe.ContextFlyout = CreateFlyoutFromElement(m.ContextFlyout, requestRerender);
        }
        else if (oldM?.ContextFlyout is not null)
            fe.ContextFlyout = null;

        // IsEnabled (on Control)
        if (m.IsEnabled.HasValue && m.IsEnabled != oldM?.IsEnabled && fe is WinUI.Control enCtrl)
            enCtrl.IsEnabled = m.IsEnabled.Value;
        else if (!m.IsEnabled.HasValue && oldM?.IsEnabled.HasValue == true && fe is WinUI.Control enCtrl2)
            enCtrl2.IsEnabled = true;

        // CornerRadius (on Control and Border)
        if (m.CornerRadius.HasValue && m.CornerRadius != oldM?.CornerRadius)
        {
            if (fe is WinUI.Control crCtrl) crCtrl.CornerRadius = m.CornerRadius.Value;
            else if (fe is WinUI.Border crBdr) crBdr.CornerRadius = m.CornerRadius.Value;
        }
        else if (!m.CornerRadius.HasValue && oldM?.CornerRadius.HasValue == true)
        {
            if (fe is WinUI.Control crCtrl) crCtrl.CornerRadius = new CornerRadius(0);
            else if (fe is WinUI.Border crBdr) crBdr.CornerRadius = new CornerRadius(0);
        }

        // BorderBrush / BorderThickness (on Control and Border)
        if (m.BorderBrush is not null && !ReferenceEquals(m.BorderBrush, oldM?.BorderBrush))
        {
            if (fe is WinUI.Control bbCtrl) bbCtrl.BorderBrush = m.BorderBrush;
            else if (fe is WinUI.Border bbBdr) bbBdr.BorderBrush = m.BorderBrush;
        }
        else if (m.BorderBrush is null && oldM?.BorderBrush is not null)
        {
            if (fe is WinUI.Control bbCtrl) bbCtrl.ClearValue(WinUI.Control.BorderBrushProperty);
            else if (fe is WinUI.Border bbBdr) bbBdr.ClearValue(WinUI.Border.BorderBrushProperty);
        }
        // Apply physical border thickness, then overlay logical (BiDi-aware) inline border
        var resolvedBorder = m.BorderThickness;
        if (m.BorderInlineStart.HasValue)
        {
            var isRtl = fe.FlowDirection == FlowDirection.RightToLeft;
            var baseBorder = resolvedBorder ?? (fe is WinUI.Control bc ? bc.BorderThickness : fe is WinUI.Border bb ? bb.BorderThickness : new Thickness());
            var inlineStartThickness = m.BorderInlineStart.Value;
            if (isRtl)
                resolvedBorder = new Thickness(baseBorder.Left, baseBorder.Top, inlineStartThickness.Left, baseBorder.Bottom);
            else
                resolvedBorder = new Thickness(inlineStartThickness.Left, baseBorder.Top, baseBorder.Right, baseBorder.Bottom);
        }
        if (resolvedBorder.HasValue && resolvedBorder != oldM?.BorderThickness)
        {
            if (fe is WinUI.Control btCtrl) btCtrl.BorderThickness = resolvedBorder.Value;
            else if (fe is WinUI.Border btBdr) btBdr.BorderThickness = resolvedBorder.Value;
        }
        else if (!resolvedBorder.HasValue && oldM?.BorderThickness.HasValue == true)
        {
            if (fe is WinUI.Control btCtrl) btCtrl.BorderThickness = new Thickness(0);
            else if (fe is WinUI.Border btBdr) btBdr.BorderThickness = new Thickness(0);
        }

        // Background (Panel, Control, or Border)
        if (m.Background is not null && !ReferenceEquals(m.Background, oldM?.Background))
        {
            if (fe is WinUI.Panel panel) panel.Background = m.Background;
            else if (fe is WinUI.Control ctrl2) ctrl2.Background = m.Background;
            else if (fe is WinUI.Border bdr) bdr.Background = m.Background;
        }
        else if (m.Background is null && oldM?.Background is not null)
        {
            if (fe is WinUI.Panel panel) panel.ClearValue(WinUI.Panel.BackgroundProperty);
            else if (fe is WinUI.Control ctrl2) ctrl2.ClearValue(WinUI.Control.BackgroundProperty);
            else if (fe is WinUI.Border bdr) bdr.ClearValue(WinUI.Border.BackgroundProperty);
        }

        // Foreground (Control or TextBlock)
        if (m.Foreground is not null && !ReferenceEquals(m.Foreground, oldM?.Foreground))
        {
            if (fe is WinUI.Control fgCtrl) fgCtrl.Foreground = m.Foreground;
            else if (fe is TextBlock fgTb) fgTb.Foreground = m.Foreground;
        }
        else if (m.Foreground is null && oldM?.Foreground is not null)
        {
            if (fe is WinUI.Control fgCtrl) fgCtrl.ClearValue(WinUI.Control.ForegroundProperty);
            else if (fe is TextBlock fgTb) fgTb.ClearValue(TextBlock.ForegroundProperty);
        }

        // AutomationProperties.Name
        if (m.AutomationName is not null && m.AutomationName != oldM?.AutomationName)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(fe, m.AutomationName);
        else if (m.AutomationName is null && oldM?.AutomationName is not null)
            fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty);

        // AutomationProperties.AutomationId
        if (m.AutomationId is not null && m.AutomationId != oldM?.AutomationId)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(fe, m.AutomationId);
        else if (m.AutomationId is null && oldM?.AutomationId is not null)
            fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AutomationIdProperty);

        // ElementSoundMode (on Control, not FrameworkElement)
        if (m.ElementSoundMode.HasValue && m.ElementSoundMode != oldM?.ElementSoundMode && fe is WinUI.Control ctrl)
            ctrl.ElementSoundMode = m.ElementSoundMode.Value;

        // ── Accessibility — Tier 1 (inline properties) ──────────────
        if (m.HeadingLevel.HasValue && m.HeadingLevel != oldM?.HeadingLevel)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(fe, m.HeadingLevel.Value);

        if (m.IsTabStop.HasValue && m.IsTabStop != oldM?.IsTabStop)
            fe.IsTabStop = m.IsTabStop.Value;

        if (m.TabIndex.HasValue && m.TabIndex != oldM?.TabIndex && fe is WinUI.Control tabIdxCtrl)
            tabIdxCtrl.TabIndex = m.TabIndex.Value;

        if (m.AccessKey is not null && m.AccessKey != oldM?.AccessKey)
            fe.AccessKey = m.AccessKey;
        else if (m.AccessKey is null && oldM?.AccessKey is not null)
            fe.AccessKey = "";

        if (m.XYFocusKeyboardNavigation.HasValue && m.XYFocusKeyboardNavigation != oldM?.XYFocusKeyboardNavigation)
            fe.XYFocusKeyboardNavigation = m.XYFocusKeyboardNavigation.Value;

        // ── Accessibility — Tier 2/3 (lazy sub-record) ─────────────
        var a11y = m.Accessibility;
        var oldA11y = oldM?.Accessibility;
        if (a11y is not null || oldA11y is not null)
            ApplyAccessibilityModifiers(fe, oldA11y, a11y);

        // ── Typography (FontFamily, FontSize, FontWeight) ──────────
        if (m.FontFamily is not null && !ReferenceEquals(m.FontFamily, oldM?.FontFamily))
        {
            if (fe is WinUI.Control ffCtrl) ffCtrl.FontFamily = m.FontFamily;
            else if (fe is TextBlock ffTb) ffTb.FontFamily = m.FontFamily;
        }
        else if (m.FontFamily is null && oldM?.FontFamily is not null)
        {
            if (fe is WinUI.Control ffCtrl) ffCtrl.ClearValue(WinUI.Control.FontFamilyProperty);
            else if (fe is TextBlock ffTb) ffTb.ClearValue(TextBlock.FontFamilyProperty);
        }
        if (m.FontSize.HasValue && m.FontSize != oldM?.FontSize)
        {
            if (fe is WinUI.Control fsCtrl) fsCtrl.FontSize = m.FontSize.Value;
            else if (fe is TextBlock fsTb) fsTb.FontSize = m.FontSize.Value;
        }
        else if (!m.FontSize.HasValue && oldM?.FontSize.HasValue == true)
        {
            if (fe is WinUI.Control fsCtrl) fsCtrl.ClearValue(WinUI.Control.FontSizeProperty);
            else if (fe is TextBlock fsTb) fsTb.ClearValue(TextBlock.FontSizeProperty);
        }
        if (m.FontWeight.HasValue && m.FontWeight != oldM?.FontWeight)
        {
            if (fe is WinUI.Control fwCtrl) fwCtrl.FontWeight = m.FontWeight.Value;
            else if (fe is TextBlock fwTb) fwTb.FontWeight = m.FontWeight.Value;
        }
        else if (!m.FontWeight.HasValue && oldM?.FontWeight.HasValue == true)
        {
            if (fe is WinUI.Control fwCtrl) fwCtrl.ClearValue(WinUI.Control.FontWeightProperty);
            else if (fe is TextBlock fwTb) fwTb.ClearValue(TextBlock.FontWeightProperty);
        }

        // ── Declarative event handlers ────────────────────────────
        // Detach previous handler (if any) before attaching new one.
        // Handlers are stored in Tag via a wrapper so we can find them for detach.
        ApplyEventHandlers(fe, oldM, m);

        // Gesture recognizers (.OnPan / .OnPinch / .OnRotate)
        ApplyGestureHandlers(fe, oldM, m);

        // Drag-and-drop (.OnDragStart / .OnDrop / .OnDragEnter / .OnDragOver / .OnDragLeave)
        ApplyDragDropHandlers(fe, oldM, m);

        // OnMountAction — only run on initial mount (oldM is null)
        if (m.OnMountAction is not null && oldM is null)
            m.OnMountAction(fe);

        // Element ref — populate on mount/update so imperative APIs (FocusManager.Focus)
        // can target the mounted control. Writing on every update is cheap (single field
        // write) and keeps the ref fresh when the pool recycles elements.
        if (m.Ref is not null)
            m.Ref._current = fe;
    }

    // ════════════════════════════════════════════════════════════════
    //  Declarative event handler management
    // ════════════════════════════════════════════════════════════════
    //  Accessibility modifiers (Tier 2/3 sub-record)
    // ════════════════════════════════════════════════════════════════

    private static void ApplyAccessibilityModifiers(FrameworkElement fe, AccessibilityModifiers? oldA, AccessibilityModifiers? a)
    {
        if (a is null) return;

        if (a.HelpText is not null && a.HelpText != oldA?.HelpText)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(fe, a.HelpText);

        if (a.FullDescription is not null && a.FullDescription != oldA?.FullDescription)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetFullDescription(fe, a.FullDescription);

        if (a.LandmarkType.HasValue && a.LandmarkType != oldA?.LandmarkType)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetLandmarkType(fe, a.LandmarkType.Value);

        if (a.AccessibilityView.HasValue && a.AccessibilityView != oldA?.AccessibilityView)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(fe, a.AccessibilityView.Value);

        if (a.IsRequiredForForm.HasValue && a.IsRequiredForForm != oldA?.IsRequiredForForm)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetIsRequiredForForm(fe, a.IsRequiredForForm.Value);

        if (a.LiveSetting.HasValue && a.LiveSetting != oldA?.LiveSetting)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(fe, a.LiveSetting.Value);

        if (a.PositionInSet.HasValue && a.PositionInSet != oldA?.PositionInSet)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetPositionInSet(fe, a.PositionInSet.Value);

        if (a.SizeOfSet.HasValue && a.SizeOfSet != oldA?.SizeOfSet)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetSizeOfSet(fe, a.SizeOfSet.Value);

        if (a.Level.HasValue && a.Level != oldA?.Level)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetLevel(fe, a.Level.Value);

        if (a.ItemStatus is not null && a.ItemStatus != oldA?.ItemStatus)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(fe, a.ItemStatus);

        if (a.TabFocusNavigation.HasValue && a.TabFocusNavigation != oldA?.TabFocusNavigation)
            fe.TabFocusNavigation = a.TabFocusNavigation.Value;

        // LabeledBy — resolve AutomationId string to the target element in the visual tree.
        // During mount the element may not be in the visual tree yet (XamlRoot is null),
        // so defer resolution to the Loaded event if needed.
        if (a.LabeledBy is not null && a.LabeledBy != oldA?.LabeledBy)
        {
            var target = FindByAutomationId(fe, a.LabeledBy);
            if (target is not null)
            {
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetLabeledBy(fe, target);
            }
            else
            {
                // Element not yet in visual tree — defer until Loaded.
                var labelId = a.LabeledBy;
                void OnLoaded(object sender, RoutedEventArgs _)
                {
                    fe.Loaded -= OnLoaded;
                    var deferred = FindByAutomationId(fe, labelId);
                    if (deferred is not null)
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLabeledBy(fe, deferred);
                }
                fe.Loaded += OnLoaded;
            }
        }
        else if (a.LabeledBy is null && oldA?.LabeledBy is not null)
        {
            fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.LabeledByProperty);
        }
    }

    /// <summary>
    /// Walks the visual tree from <paramref name="element"/>'s XamlRoot to find
    /// the first UIElement whose AutomationProperties.AutomationId matches <paramref name="automationId"/>.
    /// </summary>
    private static UIElement? FindByAutomationId(FrameworkElement element, string automationId)
    {
        var root = element.XamlRoot?.Content;
        if (root is null) return null;
        return WalkForAutomationId(root, automationId);
    }

    private static UIElement? WalkForAutomationId(DependencyObject node, string automationId)
    {
        if (node is UIElement uie
            && Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(uie) == automationId)
            return uie;

        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i);
            var found = WalkForAutomationId(child, automationId);
            if (found is not null) return found;
        }
        return null;
    }

    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tracks the currently-attached event handlers on a FrameworkElement so they
    /// can be detached before new ones are attached. Stored as the element's Tag
    /// (or alongside it in a wrapper if Tag is already used for pool identity).
    /// </summary>
    /// <summary>
    /// Per-element handler state. Holds the <b>current</b> user delegate for each event
    /// plus a bit tracking whether the stable trampoline has been attached yet. The
    /// trampoline reads from the mutable <c>Current*</c> field when it fires, so updating
    /// a handler just swaps the field — no WinUI subscribe/unsubscribe churn.
    /// </summary>
    internal sealed class EventHandlerState
    {
        // Current user handlers (mutable; null means "no-op")
        public Action<object, SizeChangedEventArgs>? CurrentSizeChanged;
        public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? CurrentPointerPressed;
        public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? CurrentPointerMoved;
        public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? CurrentPointerReleased;
        public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? CurrentPointerEntered;
        public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? CurrentPointerExited;
        public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? CurrentPointerCanceled;
        public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? CurrentPointerCaptureLost;
        public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? CurrentPointerWheelChanged;
        public Action<object, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs>? CurrentTapped;
        public Action<object, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs>? CurrentDoubleTapped;
        public Action<object, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs>? CurrentRightTapped;
        public Action<object, Microsoft.UI.Xaml.Input.HoldingRoutedEventArgs>? CurrentHolding;
        public Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs>? CurrentKeyDown;
        public Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs>? CurrentKeyUp;
        public Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs>? CurrentPreviewKeyDown;
        public Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs>? CurrentPreviewKeyUp;
        public Action<UIElement, Microsoft.UI.Xaml.Input.CharacterReceivedRoutedEventArgs>? CurrentCharacterReceived;
        public Action<object, RoutedEventArgs>? CurrentGotFocus;
        public Action<object, RoutedEventArgs>? CurrentLostFocus;
        public Action<UIElement, Microsoft.UI.Xaml.Input.AccessKeyDisplayRequestedEventArgs>? CurrentAccessKeyDisplayRequested;

        // Stable trampoline delegates — captured for reference-equality detach (never used)
        // and to prevent GC collection of the compiler-generated closure.
        public SizeChangedEventHandler? SizeChangedTrampoline;
        public Microsoft.UI.Xaml.Input.PointerEventHandler? PointerPressedTrampoline;
        public Microsoft.UI.Xaml.Input.PointerEventHandler? PointerMovedTrampoline;
        public Microsoft.UI.Xaml.Input.PointerEventHandler? PointerReleasedTrampoline;
        public Microsoft.UI.Xaml.Input.PointerEventHandler? PointerEnteredTrampoline;
        public Microsoft.UI.Xaml.Input.PointerEventHandler? PointerExitedTrampoline;
        public Microsoft.UI.Xaml.Input.PointerEventHandler? PointerCanceledTrampoline;
        public Microsoft.UI.Xaml.Input.PointerEventHandler? PointerCaptureLostTrampoline;
        public Microsoft.UI.Xaml.Input.PointerEventHandler? PointerWheelChangedTrampoline;
        public Microsoft.UI.Xaml.Input.TappedEventHandler? TappedTrampoline;
        public Microsoft.UI.Xaml.Input.DoubleTappedEventHandler? DoubleTappedTrampoline;
        public Microsoft.UI.Xaml.Input.RightTappedEventHandler? RightTappedTrampoline;
        public Microsoft.UI.Xaml.Input.HoldingEventHandler? HoldingTrampoline;
        public Microsoft.UI.Xaml.Input.KeyEventHandler? KeyDownTrampoline;
        public Microsoft.UI.Xaml.Input.KeyEventHandler? KeyUpTrampoline;
        public Microsoft.UI.Xaml.Input.KeyEventHandler? PreviewKeyDownTrampoline;
        public Microsoft.UI.Xaml.Input.KeyEventHandler? PreviewKeyUpTrampoline;
        public global::Windows.Foundation.TypedEventHandler<UIElement, Microsoft.UI.Xaml.Input.CharacterReceivedRoutedEventArgs>? CharacterReceivedTrampoline;
        public RoutedEventHandler? GotFocusTrampoline;
        public RoutedEventHandler? LostFocusTrampoline;
        public global::Windows.Foundation.TypedEventHandler<UIElement, Microsoft.UI.Xaml.Input.AccessKeyDisplayRequestedEventArgs>? AccessKeyDisplayRequestedTrampoline;
    }

    // Key for storing EventHandlerState in a dictionary attached to the element.
    // We use FrameworkElement's Tag only when no setter has claimed it.
    // To avoid conflicts, we use an attached-property-like pattern via a ConditionalWeakTable.
    private static readonly global::System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, EventHandlerState> _eventStates = new();

    private static EventHandlerState GetOrCreateEventState(FrameworkElement fe)
    {
        if (!_eventStates.TryGetValue(fe, out var state))
        {
            state = new EventHandlerState();
            _eventStates.AddOrUpdate(fe, state);
        }
        return state;
    }

    private static bool HasAnyEventHandler(ElementModifiers? m)
    {
        if (m is null) return false;
        return m.OnSizeChanged is not null
            || m.OnPointerPressed is not null || m.OnPointerMoved is not null || m.OnPointerReleased is not null
            || m.OnPointerEntered is not null || m.OnPointerExited is not null || m.OnPointerCanceled is not null
            || m.OnPointerCaptureLost is not null || m.OnPointerWheelChanged is not null
            || m.OnTapped is not null || m.OnDoubleTapped is not null || m.OnRightTapped is not null || m.OnHolding is not null
            || m.OnKeyDown is not null || m.OnKeyUp is not null
            || m.OnPreviewKeyDown is not null || m.OnPreviewKeyUp is not null
            || m.OnCharacterReceived is not null
            || m.OnGotFocus is not null || m.OnLostFocus is not null
            || m.OnAccessKeyDisplayRequested is not null;
    }

    private static bool HasAnyPointerHandler(ElementModifiers m)
    {
        return m.OnPointerPressed is not null || m.OnPointerMoved is not null || m.OnPointerReleased is not null
            || m.OnPointerEntered is not null || m.OnPointerExited is not null || m.OnPointerCanceled is not null
            || m.OnPointerCaptureLost is not null || m.OnPointerWheelChanged is not null
            || m.OnTapped is not null || m.OnDoubleTapped is not null || m.OnRightTapped is not null || m.OnHolding is not null;
    }

    private static void ApplyEventHandlers(FrameworkElement fe, ElementModifiers? oldM, ElementModifiers m)
    {
        // Fast path: nothing to do
        if (!HasAnyEventHandler(m) && !HasAnyEventHandler(oldM)) return;

        var state = GetOrCreateEventState(fe);

        // Trampoline pattern: each Ensure* helper points the current-handler field at
        // the new delegate and, if the trampoline isn't attached yet, attaches it once.
        // Subsequent renders that just hand us a fresh closure touch only the field —
        // no add_/remove_ COM traffic on the underlying WinUI event.
        EnsureSizeChangedSubscribed(fe, state, m.OnSizeChanged);
        EnsurePointerPressedSubscribed(fe, state, m.OnPointerPressed);
        EnsurePointerMovedSubscribed(fe, state, m.OnPointerMoved);
        EnsurePointerReleasedSubscribed(fe, state, m.OnPointerReleased);
        EnsurePointerEnteredSubscribed(fe, state, m.OnPointerEntered);
        EnsurePointerExitedSubscribed(fe, state, m.OnPointerExited);
        EnsurePointerCanceledSubscribed(fe, state, m.OnPointerCanceled);
        EnsurePointerCaptureLostSubscribed(fe, state, m.OnPointerCaptureLost);
        EnsurePointerWheelChangedSubscribed(fe, state, m.OnPointerWheelChanged);
        EnsureTappedSubscribed(fe, state, m.OnTapped, oldM?.OnTapped);
        EnsureDoubleTappedSubscribed(fe, state, m.OnDoubleTapped, oldM?.OnDoubleTapped);
        EnsureRightTappedSubscribed(fe, state, m.OnRightTapped, oldM?.OnRightTapped);
        EnsureHoldingSubscribed(fe, state, m.OnHolding, oldM?.OnHolding);
        EnsureKeyDownSubscribed(fe, state, m.OnKeyDown);
        EnsureKeyUpSubscribed(fe, state, m.OnKeyUp);
        EnsurePreviewKeyDownSubscribed(fe, state, m.OnPreviewKeyDown);
        EnsurePreviewKeyUpSubscribed(fe, state, m.OnPreviewKeyUp);
        EnsureCharacterReceivedSubscribed(fe, state, m.OnCharacterReceived);
        EnsureGotFocusSubscribed(fe, state, m.OnGotFocus);
        EnsureLostFocusSubscribed(fe, state, m.OnLostFocus);
        EnsureAccessKeyDisplayRequestedSubscribed(fe, state, m.OnAccessKeyDisplayRequested);

        // Shape auto-fill: Shape subclasses need a non-null Fill to hit-test pointer events.
        // If any pointer-family handler is attached and Fill is null, set transparent brush.
        if (fe is Microsoft.UI.Xaml.Shapes.Shape shape && shape.Fill is null && HasAnyPointerHandler(m))
        {
            shape.Fill = new SolidColorBrush(global::Microsoft.UI.Colors.Transparent);
        }
    }

    // ── Trampoline Ensure* helpers ──────────────────────────────────────
    // Each helper:
    //   1. Updates state.Current<Event> to the new user handler (may be null).
    //   2. On first non-null handler, allocates the stable trampoline, attaches
    //      it to the WinUI event, emits reactor:event.reattach once.
    //   3. Never detaches — the trampoline stays bound for the element's lifetime.
    //      When the user handler becomes null again, the trampoline dispatches no-op.

    private static void EnsureSizeChangedSubscribed(FrameworkElement fe, EventHandlerState state, Action<object, SizeChangedEventArgs>? handler)
    {
        state.CurrentSizeChanged = handler;
        if (state.SizeChangedTrampoline is null && handler is not null)
        {
            state.SizeChangedTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("SizeChanged");
                state.CurrentSizeChanged?.Invoke(s!, e);
            };
            fe.SizeChanged += state.SizeChangedTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("SizeChanged", fe.GetType().Name);
        }
    }

    private static void EnsurePointerPressedSubscribed(FrameworkElement fe, EventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? handler)
    {
        state.CurrentPointerPressed = handler;
        if (state.PointerPressedTrampoline is null && handler is not null)
        {
            state.PointerPressedTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("PointerPressed");
                state.CurrentPointerPressed?.Invoke(s!, e);
            };
            fe.PointerPressed += state.PointerPressedTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("PointerPressed", fe.GetType().Name);
        }
    }

    private static void EnsurePointerMovedSubscribed(FrameworkElement fe, EventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? handler)
    {
        state.CurrentPointerMoved = handler;
        if (state.PointerMovedTrampoline is null && handler is not null)
        {
            state.PointerMovedTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("PointerMoved");
                state.CurrentPointerMoved?.Invoke(s!, e);
            };
            fe.PointerMoved += state.PointerMovedTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("PointerMoved", fe.GetType().Name);
        }
    }

    private static void EnsurePointerReleasedSubscribed(FrameworkElement fe, EventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? handler)
    {
        state.CurrentPointerReleased = handler;
        if (state.PointerReleasedTrampoline is null && handler is not null)
        {
            state.PointerReleasedTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("PointerReleased");
                state.CurrentPointerReleased?.Invoke(s!, e);
            };
            fe.PointerReleased += state.PointerReleasedTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("PointerReleased", fe.GetType().Name);
        }
    }

    private static void EnsurePointerEnteredSubscribed(FrameworkElement fe, EventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? handler)
    {
        state.CurrentPointerEntered = handler;
        if (state.PointerEnteredTrampoline is null && handler is not null)
        {
            state.PointerEnteredTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("PointerEntered");
                state.CurrentPointerEntered?.Invoke(s!, e);
            };
            fe.PointerEntered += state.PointerEnteredTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("PointerEntered", fe.GetType().Name);
        }
    }

    private static void EnsurePointerExitedSubscribed(FrameworkElement fe, EventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? handler)
    {
        state.CurrentPointerExited = handler;
        if (state.PointerExitedTrampoline is null && handler is not null)
        {
            state.PointerExitedTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("PointerExited");
                state.CurrentPointerExited?.Invoke(s!, e);
            };
            fe.PointerExited += state.PointerExitedTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("PointerExited", fe.GetType().Name);
        }
    }

    private static void EnsurePointerCanceledSubscribed(FrameworkElement fe, EventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? handler)
    {
        state.CurrentPointerCanceled = handler;
        if (state.PointerCanceledTrampoline is null && handler is not null)
        {
            state.PointerCanceledTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("PointerCanceled");
                state.CurrentPointerCanceled?.Invoke(s!, e);
            };
            fe.PointerCanceled += state.PointerCanceledTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("PointerCanceled", fe.GetType().Name);
        }
    }

    private static void EnsurePointerCaptureLostSubscribed(FrameworkElement fe, EventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? handler)
    {
        state.CurrentPointerCaptureLost = handler;
        if (state.PointerCaptureLostTrampoline is null && handler is not null)
        {
            state.PointerCaptureLostTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("PointerCaptureLost");
                state.CurrentPointerCaptureLost?.Invoke(s!, e);
            };
            fe.PointerCaptureLost += state.PointerCaptureLostTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("PointerCaptureLost", fe.GetType().Name);
        }
    }

    private static void EnsurePointerWheelChangedSubscribed(FrameworkElement fe, EventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? handler)
    {
        state.CurrentPointerWheelChanged = handler;
        if (state.PointerWheelChangedTrampoline is null && handler is not null)
        {
            state.PointerWheelChangedTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("PointerWheelChanged");
                state.CurrentPointerWheelChanged?.Invoke(s!, e);
            };
            fe.PointerWheelChanged += state.PointerWheelChangedTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("PointerWheelChanged", fe.GetType().Name);
        }
    }

    private static void EnsureTappedSubscribed(FrameworkElement fe, EventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs>? handler, Action<object, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs>? oldHandler)
    {
        state.CurrentTapped = handler;
        if (state.TappedTrampoline is null && handler is not null)
        {
            state.TappedTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("Tapped");
                state.CurrentTapped?.Invoke(s!, e);
            };
            fe.Tapped += state.TappedTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("Tapped", fe.GetType().Name);
        }
        if (handler is not null) fe.IsTapEnabled = true;
        else if (oldHandler is not null) fe.IsTapEnabled = false;
    }

    private static void EnsureDoubleTappedSubscribed(FrameworkElement fe, EventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs>? handler, Action<object, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs>? oldHandler)
    {
        state.CurrentDoubleTapped = handler;
        if (state.DoubleTappedTrampoline is null && handler is not null)
        {
            state.DoubleTappedTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("DoubleTapped");
                state.CurrentDoubleTapped?.Invoke(s!, e);
            };
            fe.DoubleTapped += state.DoubleTappedTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("DoubleTapped", fe.GetType().Name);
        }
        if (handler is not null) fe.IsDoubleTapEnabled = true;
        else if (oldHandler is not null) fe.IsDoubleTapEnabled = false;
    }

    private static void EnsureRightTappedSubscribed(FrameworkElement fe, EventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs>? handler, Action<object, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs>? oldHandler)
    {
        state.CurrentRightTapped = handler;
        if (state.RightTappedTrampoline is null && handler is not null)
        {
            state.RightTappedTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("RightTapped");
                state.CurrentRightTapped?.Invoke(s!, e);
            };
            fe.RightTapped += state.RightTappedTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("RightTapped", fe.GetType().Name);
        }
        if (handler is not null) fe.IsRightTapEnabled = true;
        else if (oldHandler is not null) fe.IsRightTapEnabled = false;
    }

    private static void EnsureHoldingSubscribed(FrameworkElement fe, EventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.HoldingRoutedEventArgs>? handler, Action<object, Microsoft.UI.Xaml.Input.HoldingRoutedEventArgs>? oldHandler)
    {
        state.CurrentHolding = handler;
        if (state.HoldingTrampoline is null && handler is not null)
        {
            state.HoldingTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("Holding");
                state.CurrentHolding?.Invoke(s!, e);
            };
            fe.Holding += state.HoldingTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("Holding", fe.GetType().Name);
        }
        if (handler is not null) fe.IsHoldingEnabled = true;
        else if (oldHandler is not null) fe.IsHoldingEnabled = false;
    }

    private static void EnsureKeyDownSubscribed(FrameworkElement fe, EventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs>? handler)
    {
        state.CurrentKeyDown = handler;
        if (state.KeyDownTrampoline is null && handler is not null)
        {
            state.KeyDownTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("KeyDown");
                state.CurrentKeyDown?.Invoke(s!, e);
            };
            fe.KeyDown += state.KeyDownTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("KeyDown", fe.GetType().Name);
        }
    }

    private static void EnsureKeyUpSubscribed(FrameworkElement fe, EventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs>? handler)
    {
        state.CurrentKeyUp = handler;
        if (state.KeyUpTrampoline is null && handler is not null)
        {
            state.KeyUpTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("KeyUp");
                state.CurrentKeyUp?.Invoke(s!, e);
            };
            fe.KeyUp += state.KeyUpTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("KeyUp", fe.GetType().Name);
        }
    }

    private static void EnsurePreviewKeyDownSubscribed(FrameworkElement fe, EventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs>? handler)
    {
        state.CurrentPreviewKeyDown = handler;
        if (state.PreviewKeyDownTrampoline is null && handler is not null)
        {
            state.PreviewKeyDownTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("PreviewKeyDown");
                state.CurrentPreviewKeyDown?.Invoke(s!, e);
            };
            fe.PreviewKeyDown += state.PreviewKeyDownTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("PreviewKeyDown", fe.GetType().Name);
        }
    }

    private static void EnsurePreviewKeyUpSubscribed(FrameworkElement fe, EventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs>? handler)
    {
        state.CurrentPreviewKeyUp = handler;
        if (state.PreviewKeyUpTrampoline is null && handler is not null)
        {
            state.PreviewKeyUpTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("PreviewKeyUp");
                state.CurrentPreviewKeyUp?.Invoke(s!, e);
            };
            fe.PreviewKeyUp += state.PreviewKeyUpTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("PreviewKeyUp", fe.GetType().Name);
        }
    }

    private static void EnsureCharacterReceivedSubscribed(FrameworkElement fe, EventHandlerState state, Action<UIElement, Microsoft.UI.Xaml.Input.CharacterReceivedRoutedEventArgs>? handler)
    {
        state.CurrentCharacterReceived = handler;
        if (state.CharacterReceivedTrampoline is null && handler is not null)
        {
            state.CharacterReceivedTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("CharacterReceived");
                state.CurrentCharacterReceived?.Invoke(s, e);
            };
            fe.CharacterReceived += state.CharacterReceivedTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("CharacterReceived", fe.GetType().Name);
        }
    }

    private static void EnsureGotFocusSubscribed(FrameworkElement fe, EventHandlerState state, Action<object, RoutedEventArgs>? handler)
    {
        state.CurrentGotFocus = handler;
        if (state.GotFocusTrampoline is null && handler is not null)
        {
            state.GotFocusTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("GotFocus");
                state.CurrentGotFocus?.Invoke(s!, e);
            };
            fe.GotFocus += state.GotFocusTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("GotFocus", fe.GetType().Name);
        }
    }

    private static void EnsureLostFocusSubscribed(FrameworkElement fe, EventHandlerState state, Action<object, RoutedEventArgs>? handler)
    {
        state.CurrentLostFocus = handler;
        if (state.LostFocusTrampoline is null && handler is not null)
        {
            state.LostFocusTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("LostFocus");
                state.CurrentLostFocus?.Invoke(s!, e);
            };
            fe.LostFocus += state.LostFocusTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("LostFocus", fe.GetType().Name);
        }
    }

    private static void EnsureAccessKeyDisplayRequestedSubscribed(FrameworkElement fe, EventHandlerState state, Action<UIElement, Microsoft.UI.Xaml.Input.AccessKeyDisplayRequestedEventArgs>? handler)
    {
        state.CurrentAccessKeyDisplayRequested = handler;
        if (state.AccessKeyDisplayRequestedTrampoline is null && handler is not null)
        {
            state.AccessKeyDisplayRequestedTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("AccessKeyDisplayRequested");
                state.CurrentAccessKeyDisplayRequested?.Invoke(s!, e);
            };
            fe.AccessKeyDisplayRequested += state.AccessKeyDisplayRequestedTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("AccessKeyDisplayRequested", fe.GetType().Name);
        }
    }

    /// <summary>
    /// Applies ThemeRef bindings by setting properties through WinUI's {ThemeResource}
    /// mechanism. Builds a local Style with ThemeResource setters and applies it to the
    /// element. WinUI then handles theme-reactive resolution natively for system theme
    /// changes (Light ↔ Dark). Note: {ThemeResource} in dynamically-loaded Styles resolves
    /// against the app theme, not per-element RequestedTheme overrides — for subtree theme
    /// overrides, rely on native WinUI control theming instead of ThemeRef bindings.
    /// </summary>
    private static void ApplyThemeBindings(FrameworkElement fe, IReadOnlyDictionary<string, ThemeRef> bindings)
    {
        var targetType = GetStyleTargetType(fe);
        if (targetType is null) return;

        var cacheKey = BuildCacheKey(targetType, bindings);

        // Cache hit: reuse the previously compiled Style
        if (_styleCache.TryGetValue(cacheKey, out var cachedStyle))
        {
            ApplyStyleToElement(fe, cachedStyle);
            return;
        }

        // Cache miss: build XAML, parse, and cache
        var setters = new global::System.Text.StringBuilder();
        foreach (var (property, themeRef) in bindings)
        {
            var dp = GetDependencyPropertyName(fe, property);
            if (dp is null) continue;
            var escapedResourceKey = global::System.Security.SecurityElement.Escape(themeRef.ResourceKey);
            setters.Append($"<Setter Property='{dp}' Value='{{ThemeResource {escapedResourceKey}}}'/>");
        }

        if (setters.Length == 0) return;

        try
        {
            var xaml =
                $"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='{targetType}'>" +
                setters.ToString() +
                "</Style>";
            var style = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
            _styleCache.TryAdd(cacheKey, style);
            ApplyStyleToElement(fe, style);
        }
        catch (Exception ex)
        {
            global::System.Diagnostics.Debug.WriteLine($"[Reactor.Theme] Failed to apply ThemeBindings: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies a cached style to an element. Clears any existing style first to
    /// force WinUI to re-evaluate <c>{ThemeResource}</c> setters against the
    /// element's current effective theme (which may have changed due to a parent's
    /// <see cref="FrameworkElement.RequestedTheme"/> override).
    /// </summary>
    private static void ApplyStyleToElement(FrameworkElement fe, Style cachedStyle)
    {
        // Clearing first forces WinUI to process the subsequent set as a
        // genuine change, re-resolving {ThemeResource} values. Without this,
        // re-applying the same cached Style reference is a no-op.
        if (fe.Style is not null)
            fe.Style = null;
        fe.Style = cachedStyle;
    }

    private static string? GetStyleTargetType(FrameworkElement fe) => fe switch
    {
        WinUI.Border => "Border",
        WinUI.StackPanel => "StackPanel",
        WinUI.Grid => "Grid",
        WinUI.Button => "Button",
        WinUI.TextBox => "TextBox",
        TextBlock => "TextBlock",
        WinUI.ContentControl => "ContentControl",
        WinUI.Panel => "Panel",
        WinUI.Control => "Control",
        _ => fe.GetType().Name,
    };

    private static string? GetDependencyPropertyName(FrameworkElement fe, string property)
    {
        if (property == "Background" && (fe is WinUI.Panel || fe is WinUI.Control || fe is WinUI.Border))
            return "Background";
        if (property == "Foreground" && (fe is WinUI.Control || fe is TextBlock))
            return "Foreground";
        if (property == "BorderBrush" && (fe is WinUI.Control || fe is WinUI.Border))
            return "BorderBrush";
        return null;
    }

    // ── Lightweight Styling: per-control resource overrides ────────────────

    /// <summary>
    /// Tracks which resource keys in <see cref="FrameworkElement.Resources"/> were
    /// set by Reactor (vs. keys set by XAML or other sources). On update, only
    /// Reactor-managed keys are removed when overrides change.
    /// </summary>
    private static readonly global::System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, HashSet<string>>
        _managedResourceKeys = new();

    /// <summary>
    /// Applies per-control resource overrides (lightweight styling) to a
    /// <see cref="FrameworkElement"/>. Literal values are set directly;
    /// <see cref="ThemeRef"/>-based values are resolved from
    /// <see cref="Application.Current.Resources"/>.
    /// </summary>
    private static void ApplyResourceOverrides(
        FrameworkElement fe,
        OpenClawTray.Infrastructure.Elements.ResourceOverrides? oldOverrides,
        OpenClawTray.Infrastructure.Elements.ResourceOverrides? newOverrides)
    {
        // Track which keys Reactor has set on this element
        var managed = _managedResourceKeys.GetOrCreateValue(fe);

        // Remove old keys that are no longer present in the new overrides
        if (oldOverrides is not null)
        {
            var newKeys = newOverrides?.AllKeys.ToHashSet() ?? new HashSet<string>();
            foreach (var key in managed.ToArray())
            {
                if (!newKeys.Contains(key))
                {
                    fe.Resources.Remove(key);
                    managed.Remove(key);
                }
            }
        }

        if (newOverrides is null) return;

        fe.Resources ??= new ResourceDictionary();

        // Apply literal resources
        foreach (var (key, value) in newOverrides.Literals)
        {
            fe.Resources[key] = value;
            managed.Add(key);
        }

        // Apply ThemeRef resources (resolved from Application.Current.Resources)
        foreach (var (key, themeRef) in newOverrides.ThemeRefs)
        {
            var resolved = ThemeRef.Resolve(themeRef.ResourceKey, fe);
            if (resolved is not null)
            {
                fe.Resources[key] = resolved;
                managed.Add(key);
            }
        }
    }

    /// <summary>
    /// Sets or updates the flyout on a control. On first mount, creates a new flyout.
    /// On update, reconciles the content inside the existing flyout to keep it open.
    /// </summary>
    private void ApplyFlyoutAttachment(FrameworkElement fe, Element? oldFlyoutEl, Element newFlyoutEl, Action requestRerender)
    {
        // Try to get the existing flyout from the control.
        // SplitButton.Flyout and Button.Flyout are separate properties (different type hierarchies).
        WinPrim.FlyoutBase? existingFlyout = fe switch
        {
            WinUI.SplitButton sb => sb.Flyout,
            WinUI.Button btn => btn.Flyout,  // AppBarButton inherits from Button
            _ => WinPrim.FlyoutBase.GetAttachedFlyout(fe),
        };

        // If we have an existing flyout and old element, try to update in place
        if (oldFlyoutEl is not null && existingFlyout is not null)
        {
            UpdateFlyoutInPlace(existingFlyout, oldFlyoutEl, newFlyoutEl, requestRerender);
            return;
        }

        // First mount — create new flyout
        var flyout = CreateFlyoutFromElement(newFlyoutEl, requestRerender);
        if (flyout is null) return;

        SetFlyoutOnControl(fe, flyout);
    }

    /// <summary>
    /// Updates the content inside an existing flyout without replacing the flyout object.
    /// This keeps the flyout open while its content changes.
    /// </summary>
    private void UpdateFlyoutInPlace(WinPrim.FlyoutBase existingFlyout, Element oldEl, Element newEl, Action requestRerender)
    {
        // ContentFlyout → reconcile child content inside the existing Flyout
        if (newEl is ContentFlyoutElement newCf && existingFlyout is WinUI.Flyout flyout)
        {
            var oldContent = oldEl is ContentFlyoutElement oldCf ? oldCf.Content : null;
            if (oldContent is not null && flyout.Content is UIElement existingContent && CanUpdate(oldContent, newCf.Content))
            {
                var replacement = Update(oldContent, newCf.Content, existingContent, requestRerender);
                if (replacement is not null)
                    flyout.Content = replacement;
            }
            else
            {
                // Type changed — remount content
                flyout.Content = Mount(newCf.Content, requestRerender);
            }
            flyout.Placement = newCf.Placement;
            return;
        }

        // MenuFlyout → recreate items (lightweight, no open-state issue)
        if (newEl is MenuFlyoutContentElement newMf && existingFlyout is WinUI.MenuFlyout menuFlyout)
        {
            menuFlyout.Items.Clear();
            foreach (var item in newMf.Items) menuFlyout.Items.Add(CreateMenuFlyoutItem(item));
            if (newMf.Placement != WinPrim.FlyoutPlacementMode.Auto)
                menuFlyout.Placement = newMf.Placement;
            return;
        }

        // Fallback: plain element → reconcile inside existing Flyout
        if (existingFlyout is WinUI.Flyout plainFlyout && plainFlyout.Content is UIElement existingCtrl)
        {
            if (CanUpdate(oldEl, newEl))
            {
                var replacement = Update(oldEl, newEl, existingCtrl, requestRerender);
                if (replacement is not null)
                    plainFlyout.Content = replacement;
            }
            else
            {
                plainFlyout.Content = Mount(newEl, requestRerender);
            }
        }
    }

    private void SetFlyoutOnControl(FrameworkElement fe, WinPrim.FlyoutBase flyout)
    {
        // Check SplitButton before Button (SplitButton doesn't inherit from Button,
        // but DropDownButton does, so Button catch-all handles it).
        if (fe is WinUI.SplitButton sb)
            sb.Flyout = flyout;
        else if (fe is WinUI.Button btn)  // AppBarButton, DropDownButton inherit from Button
            btn.Flyout = flyout;
        else
            WinPrim.FlyoutBase.SetAttachedFlyout(fe, flyout);
    }

    /// <summary>
    /// Creates a WinUI FlyoutBase from a Reactor element descriptor.
    /// Recognizes ContentFlyoutElement and MenuFlyoutContentElement for configured flyouts,
    /// and falls back to wrapping plain elements in a basic Flyout.
    /// Used by both ApplyModifiers (for .WithFlyout()/.WithContextFlyout()) and
    /// button mount methods (for direct Flyout parameter).
    /// </summary>
    internal WinPrim.FlyoutBase? CreateFlyoutFromElement(Element flyoutEl, Action requestRerender)
    {
        switch (flyoutEl)
        {
            case ContentFlyoutElement cf:
            {
                var content = Mount(cf.Content, requestRerender);
                return content is not null ? new WinUI.Flyout { Content = content, Placement = cf.Placement } : null;
            }
            case MenuFlyoutContentElement mf:
            {
                var menuFlyout = new WinUI.MenuFlyout();
                // Only set Placement if explicitly specified (Auto can cause assertions on MenuFlyout)
                if (mf.Placement != WinPrim.FlyoutPlacementMode.Auto)
                    menuFlyout.Placement = mf.Placement;
                foreach (var item in mf.Items) menuFlyout.Items.Add(CreateMenuFlyoutItem(item));
                return menuFlyout;
            }
            default:
            {
                var content = Mount(flyoutEl, requestRerender);
                return content is not null ? new WinUI.Flyout { Content = content } : null;
            }
        }
    }

    // ── Enum conversions removed — Reactor now uses WinUI types directly ──

    internal static Symbol ParseSymbol(string name)
    {
        if (Enum.TryParse<Symbol>(name, ignoreCase: true, out var symbol)) return symbol;
        return Symbol.Placeholder;
    }

    // ── Grid definition parsing ─────────────────────────────────────

    internal static ColumnDefinition ParseColumnDef(string def) => def switch
    {
        "*" => new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
        "Auto" or "auto" => new ColumnDefinition { Width = GridLength.Auto },
        _ when double.TryParse(def, global::System.Globalization.NumberStyles.Float, global::System.Globalization.CultureInfo.InvariantCulture, out var px) => new ColumnDefinition { Width = new GridLength(px) },
        _ when def.EndsWith('*') && double.TryParse(def[..^1], global::System.Globalization.NumberStyles.Float, global::System.Globalization.CultureInfo.InvariantCulture, out var stars) =>
            new ColumnDefinition { Width = new GridLength(stars, GridUnitType.Star) },
        _ => new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
    };

    internal static RowDefinition ParseRowDef(string def) => def switch
    {
        "*" => new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
        "Auto" or "auto" => new RowDefinition { Height = GridLength.Auto },
        _ when double.TryParse(def, global::System.Globalization.NumberStyles.Float, global::System.Globalization.CultureInfo.InvariantCulture, out var px) => new RowDefinition { Height = new GridLength(px) },
        _ when def.EndsWith('*') && double.TryParse(def[..^1], global::System.Globalization.NumberStyles.Float, global::System.Globalization.CultureInfo.InvariantCulture, out var stars) =>
            new RowDefinition { Height = new GridLength(stars, GridUnitType.Star) },
        _ => new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
    };

    /// <summary>
    /// Tracks the state of a mounted component in the tree.
    /// </summary>
    internal class ComponentNode
    {
        /// <summary>The class-based Component instance (null for function components).</summary>
        public Component? Component { get; set; }
        /// <summary>The RenderContext for function components (null for class components).</summary>
        public RenderContext? Context { get; set; }
        /// <summary>The element tree output from the last Render() call.</summary>
        public Element? RenderedElement { get; set; }
        /// <summary>The ComponentElement or FuncElement that created this node.</summary>
        public Element? Element { get; set; }
        /// <summary>Previous props for memo comparison (class components only).</summary>
        public object? PreviousProps { get; set; }
        /// <summary>Dependencies from the last MemoElement render (null = render once).</summary>
        public object?[]? MemoDependencies { get; set; }
        /// <summary>Set to true when a self-triggered re-render is queued (UseState setter).
        /// Accessed from background threads (UseState callbacks) — use volatile field.</summary>
        private volatile bool _selfTriggered;
        public bool SelfTriggered { get => _selfTriggered; set => _selfTriggered = value; }
    }

    /// <summary>
    /// Tracks the state of a mounted ErrorBoundary in the tree.
    /// </summary>
    internal class ErrorBoundaryNode
    {
        public Element ChildElement { get; set; } = null!;
        public Element? RenderedElement { get; set; }
        public Exception? CaughtException { get; set; }
        public Func<Exception, Element> Fallback { get; set; } = null!;
    }

    /// <summary>
    /// Tracks the state of a mounted NavigationHost in the tree.
    /// Stores the current child control/element and the subscription to route changes
    /// so content can be swapped when navigation occurs.
    /// </summary>
    internal class NavigationHostNode
    {
        /// <summary>The type-erased navigation handle (implements INavigationHandle internally).</summary>
        public Navigation.INavigationHandle Handle { get; set; } = null!;
        /// <summary>The route that was last rendered.</summary>
        public object LastRenderedRoute { get; set; } = null!;
        /// <summary>The element returned by routeMap for the current route.</summary>
        public Element? CurrentChildElement { get; set; }
        /// <summary>The mounted WinUI control for the current route.</summary>
        public UIElement? CurrentChildControl { get; set; }
        /// <summary>The route-mapping function (type-erased).</summary>
        public Func<object, Element> RouteMap { get; set; } = null!;
        /// <summary>The rerender callback for triggering content swap.</summary>
        public Action? RequestRerender { get; set; }
        /// <summary>Handler attached to INavigationHandle.RouteChanged for cleanup.</summary>
        public Action? RouteChangedHandler { get; set; }
        /// <summary>Navigation mode recorded by the lifecycle guard before stack mutation.</summary>
        public Navigation.NavigationMode? PendingNavigationMode { get; set; }
        /// <summary>Previous route recorded by the lifecycle guard before stack mutation.</summary>
        public object? PendingPreviousRoute { get; set; }
        /// <summary>The host-level default transition.</summary>
        public Navigation.NavigationTransition HostTransition { get; set; } = Navigation.NavigationTransition.Default;
        /// <summary>True if a transition animation is currently running.</summary>
        public bool TransitionInProgress { get; set; }
        /// <summary>The host-level cache mode.</summary>
        public Navigation.NavigationCacheMode CacheMode { get; set; } = Navigation.NavigationCacheMode.Disabled;
        /// <summary>Page cache for this NavigationHost (null when CacheMode is Disabled).</summary>
        public Navigation.NavigationCache? Cache { get; set; }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Navigation lifecycle hook traversal
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Collects all <see cref="RenderContext.NavigationLifecycleHookState"/> instances from
    /// the component subtree rooted at <paramref name="root"/>.
    /// </summary>
    internal List<RenderContext.NavigationLifecycleHookState> CollectLifecycleHooks(UIElement? root)
    {
        var hooks = new List<RenderContext.NavigationLifecycleHookState>();
        CollectLifecycleHooksRecursive(root, hooks);
        return hooks;
    }

    private void CollectLifecycleHooksRecursive(UIElement? control, List<RenderContext.NavigationLifecycleHookState> hooks)
    {
        if (control is null) return;

        if (_componentNodes.TryGetValue(control, out var node))
        {
            var ctx = node.Component?.Context ?? node.Context;
            var hook = ctx?.GetNavigationLifecycleHook();
            if (hook is not null)
                hooks.Add(hook);
        }

        // Recurse into children (Border wraps components, Panel/Grid wraps layouts)
        if (control is WinUI.Panel panel)
        {
            foreach (UIElement child in panel.Children)
                CollectLifecycleHooksRecursive(child, hooks);
        }
        else if (control is WinUI.Border border && border.Child is not null)
        {
            CollectLifecycleHooksRecursive(border.Child, hooks);
        }
        else if (control is WinUI.ContentControl cc && cc.Content is UIElement content)
        {
            CollectLifecycleHooksRecursive(content, hooks);
        }
    }

    /// <summary>
    /// Invokes post-navigation lifecycle callbacks: onNavigatedTo on the new page,
    /// then onNavigatedFrom on the old page (using pre-collected hooks).
    /// </summary>
    internal void InvokePostNavigationLifecycle(
        UIElement? newChildControl,
        List<RenderContext.NavigationLifecycleHookState>? oldHooks,
        object currentRoute, object? previousRoute, Navigation.NavigationMode mode)
    {
        // onNavigatedTo on the new page's component tree
        var newHooks = CollectLifecycleHooks(newChildControl);
        var navigatedToCtx = new Navigation.NavigatedToContext(currentRoute, previousRoute, mode);
        foreach (var hook in newHooks)
        {
            try { hook.OnNavigatedTo?.Invoke(navigatedToCtx); }
            catch (Exception ex)
            {
                global::System.Diagnostics.Debug.WriteLine($"[Reactor] onNavigatedTo threw: {ex}");
            }
        }

        // onNavigatedFrom on the old page (callbacks captured at last render)
        if (oldHooks is not null)
        {
            var navigatedFromCtx = new Navigation.NavigatedFromContext(
                previousRoute!, currentRoute, mode);
            foreach (var hook in oldHooks)
            {
                try { hook.OnNavigatedFrom?.Invoke(navigatedFromCtx); }
                catch (Exception ex)
                {
                    global::System.Diagnostics.Debug.WriteLine($"[Reactor] onNavigatedFrom threw: {ex}");
                }
            }
        }
    }

    /// <summary>
    /// Invokes <c>onNavigatingTo</c> (destination-side guard) on all lifecycle hooks
    /// in the new page's subtree. Returns true if navigation should proceed.
    /// </summary>
    internal bool InvokeNavigatingTo(
        UIElement? newChildControl,
        object currentRoute, object? previousRoute, Navigation.NavigationMode mode)
    {
        var hooks = CollectLifecycleHooks(newChildControl);
        var ctx = new Navigation.NavigatingToContext(currentRoute, previousRoute, mode);
        foreach (var hook in hooks)
        {
            try { hook.OnNavigatingTo?.Invoke(ctx); }
            catch (Exception ex)
            {
                global::System.Diagnostics.Debug.WriteLine($"[Reactor] onNavigatingTo threw: {ex}");
            }
            if (ctx.IsCancelled)
            {
                Navigation.NavigationDiagnostics.OnNavigationCancelled(
                    previousRoute ?? currentRoute, currentRoute, mode, "destination guard");
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Invokes <c>onNavigatingFrom</c> on all lifecycle hooks in the subtree.
    /// Sets <see cref="Navigation.NavigatingFromContext.IsCancelled"/> if any callback cancels.
    /// </summary>
    internal void InvokeNavigatingFrom(UIElement? root, Navigation.NavigatingFromContext ctx)
    {
        if (root is null) return;

        if (_componentNodes.TryGetValue(root, out var node))
        {
            var renderCtx = node.Component?.Context ?? node.Context;
            var hook = renderCtx?.GetNavigationLifecycleHook();
            hook?.OnNavigatingFrom?.Invoke(ctx);
            if (ctx.IsCancelled) return;
        }

        if (root is WinUI.Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                InvokeNavigatingFrom(child, ctx);
                if (ctx.IsCancelled) return;
            }
        }
        else if (root is WinUI.Border border && border.Child is not null)
        {
            InvokeNavigatingFrom(border.Child, ctx);
        }
        else if (root is WinUI.ContentControl cc && cc.Content is UIElement content)
        {
            InvokeNavigatingFrom(content, ctx);
        }
    }

    public void Dispose()
    {
        foreach (var node in _componentNodes.Values)
        {
            node.Context?.RunCleanups();
            node.Component?.Context?.RunCleanups();
        }
        _componentNodes.Clear();
        _errorBoundaryNodes.Clear();
        foreach (var node in _navigationHostNodes.Values)
        {
            if (node.RouteChangedHandler is not null)
                node.Handle.RouteChanged -= node.RouteChangedHandler;
            node.Handle.Detach();
            if (node.CurrentChildControl is not null)
                UnmountRecursive(node.CurrentChildControl);
            node.Cache?.Clear();
        }
        _navigationHostNodes.Clear();
        _pool.Clear();
    }
}
