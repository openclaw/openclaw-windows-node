using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using Windows.UI.Text;
using WinUI = Microsoft.UI.Xaml.Controls;
using WinPrim = Microsoft.UI.Xaml.Controls.Primitives;
using WinShapes = Microsoft.UI.Xaml.Shapes;

namespace OpenClawTray.Infrastructure.Core;

// ════════════════════════════════════════════════════════════════════════
//  Base types
// ════════════════════════════════════════════════════════════════════════

/// <summary>
/// A lightweight, immutable description of a UI node (the "virtual DOM").
/// Elements are cheap to create and diff — they never touch real controls directly.
/// </summary>
public abstract record Element
{
    /// <summary>
    /// Optional key for stable identity across re-renders (like React's key prop).
    /// When set, the reconciler uses it to match elements across list reorderings.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>
    /// Layout modifiers (margin, padding, size, alignment, etc.) applied to this element.
    /// Set via fluent extension methods: Text("hi").Margin(10).Width(200)
    /// Modifiers are stored inline so the concrete element type is preserved through chaining.
    /// </summary>
    public ElementModifiers? Modifiers { get; init; }

    /// <summary>
    /// Attached properties from parent containers (Grid.Row, Canvas.Left, etc.).
    /// Set via fluent extension methods: Text("hi").Grid(row: 1, column: 2)
    /// Stored as a type-keyed dictionary so each provider defines its own data record.
    /// </summary>
    public IReadOnlyDictionary<Type, object>? Attached { get; init; }

    /// <summary>
    /// Implicit transitions (opacity, scale, rotation, translation, background).
    /// Set via fluent extension methods: Rectangle().WithOpacityTransition()
    /// Applied by the reconciler after mount/update, so they are always present when
    /// property values are set via .Set() callbacks.
    /// </summary>
    public ImplicitTransitions? ImplicitTransitions { get; init; }

    /// <summary>
    /// Theme transitions (children, item container).
    /// Set via fluent extension methods: VStack(children).WithThemeTransitions(...)
    /// </summary>
    public ThemeTransitions? ThemeTransitions { get; init; }

    /// <summary>
    /// Theme-resource bindings for brush properties (Background, Foreground, BorderBrush).
    /// When set, the reconciler resolves from WinUI theme resources instead of using local values.
    /// Set via fluent extension methods: Text("hi").Background(Theme.Accent)
    /// </summary>
    public IReadOnlyDictionary<string, ThemeRef>? ThemeBindings { get; init; }

    /// <summary>
    /// Composition-layer layout animation configuration.
    /// When set, the reconciler attaches implicit animations to the element's Visual
    /// so that layout-driven position (and optionally size) changes animate smoothly.
    /// Set via fluent extension methods: Border(child).LayoutAnimation()
    /// </summary>
    public LayoutAnimationConfig? LayoutAnimation { get; init; }

    /// <summary>
    /// Compositor property animation configuration (.Animate() modifier).
    /// When set, the reconciler creates ImplicitAnimationCollection entries on the
    /// element's Visual for Opacity/Scale/Rotation/Offset/CenterPoint.
    /// </summary>
    public OpenClawTray.Infrastructure.Animation.AnimationConfig? AnimationConfig { get; init; }

    /// <summary>
    /// Element enter/exit transition configuration (.Transition() modifier).
    /// When set, the reconciler animates mount (enter) and unmount (exit) with
    /// compositor animations, deferring removal until exit animation completes.
    /// </summary>
    public OpenClawTray.Infrastructure.Animation.ElementTransition? ElementTransition { get; init; }

    /// <summary>
    /// Interaction states configuration (.InteractionStates() modifier).
    /// When set, the reconciler registers pointer event handlers that drive
    /// zero-reconcile visual state transitions (hover, pressed, focused).
    /// </summary>
    public OpenClawTray.Infrastructure.Animation.InteractionStatesConfig? InteractionStates { get; init; }

    /// <summary>
    /// Stagger configuration for container children (.Stagger() modifier).
    /// When set, child animations (enter, layout, property) have incrementing
    /// DelayTime = childIndex * staggerDelay.
    /// </summary>
    public OpenClawTray.Infrastructure.Animation.StaggerConfig? StaggerConfig { get; init; }

    /// <summary>
    /// Keyframe animation definitions (.Keyframes() modifier).
    /// Trigger-based: plays when the trigger value changes between renders.
    /// </summary>
    public OpenClawTray.Infrastructure.Animation.KeyframeEntry[]? KeyframeAnimations { get; init; }

    /// <summary>
    /// Scroll-linked expression animation configuration (.ScrollLinked() modifier).
    /// Expression animations run on the compositor, driven by ScrollViewer position.
    /// </summary>
    public OpenClawTray.Infrastructure.Animation.ScrollAnimationConfig? ScrollAnimation { get; init; }

    /// <summary>
    /// Connected animation key for cross-container transitions.
    /// When set, the reconciler automatically captures a visual snapshot on unmount
    /// (via ConnectedAnimationService.PrepareToAnimate) and starts the animation on
    /// mount if a prepared animation with the same key exists.
    /// Set via fluent extension method: Border(child).ConnectedAnimation("hero")
    /// </summary>
    public string? ConnectedAnimationKey { get; init; }

    /// <summary>
    /// Per-control resource overrides (lightweight styling). When set, the reconciler
    /// injects these into <see cref="FrameworkElement.Resources"/> so that the control's
    /// VisualStateManager picks them up for hover/pressed/disabled states.
    /// Set via fluent extension: <c>Button("Go").Resources(r => r.Set("ButtonBackground", "#0078D4"))</c>
    /// </summary>
    public OpenClawTray.Infrastructure.Elements.ResourceOverrides? ResourceOverrides { get; init; }

    /// <summary>
    /// Context values provided to this element's subtree via .Provide().
    /// The reconciler pushes these onto the context scope when entering
    /// this element's subtree and pops them when leaving.
    /// </summary>
    public IReadOnlyDictionary<ContextBase, object?>? ContextValues { get; init; }

    /// <summary>
    /// Gets the attached property data of the specified type, or null if not set.
    /// </summary>
    internal T? GetAttached<T>() where T : class =>
        Attached is not null && Attached.TryGetValue(typeof(T), out var val) ? (T)val : null;

    /// <summary>
    /// Returns a copy of this element with the given attached property data set.
    /// Used by Grid/Canvas/RelativePanel extension methods.
    /// </summary>
    internal Element SetAttached(object data)
    {
        var dict = Attached is not null
            ? new Dictionary<Type, object>(Attached)
            : new Dictionary<Type, object>();
        dict[data.GetType()] = data;
        return this with { Attached = dict };
    }

    /// <summary>
    /// Convenience: implicitly convert a string to a TextBlockElement.
    /// Allows writing: VStack("Hello", "World") instead of VStack(Text("Hello"), Text("World"))
    /// </summary>
    public static implicit operator Element(string text) => new TextBlockElement(text);

    // ════════════════════════════════════════════════════════════════════════
    //  Fast structural comparison for reconciler short-circuit
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true if two elements are structurally identical AND the child can be
    /// completely skipped during reconciliation (no need to call Update at all).
    /// This is stricter than ShallowEquals: elements with ThemeBindings must still
    /// go through Update so bindings can be re-evaluated against the current theme.
    /// IMPORTANT: keep in sync with the ShallowEquals fast-path in Reconciler.Update().
    /// </summary>
    internal static bool CanSkipUpdate(Element oldEl, Element newEl)
        => ShallowEquals(oldEl, newEl) && newEl.ThemeBindings is null;

    /// <summary>
    /// Fast structural comparison that avoids the pitfalls of record Equals
    /// (Dictionary reference equality, Action[] reference equality, delegate equality).
    /// Returns true only when the two elements are provably identical for rendering purposes.
    /// Conservative: returns false for unknown element types.
    /// </summary>
    internal static bool ShallowEquals(Element a, Element b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.GetType() != b.GetType()) return false;
        if (!ModifiersEqual(a.Modifiers, b.Modifiers)) return false;
        if (!AttachedEqual(a.Attached, b.Attached)) return false;
        if (!ThemeBindingsEqual(a.ThemeBindings, b.ThemeBindings)) return false;
        if (!ContextValuesEqual(a.ContextValues, b.ContextValues)) return false;

        return (a, b) switch
        {
            (TextBlockElement ta, TextBlockElement tb) =>
                ta.Content == tb.Content
                && ta.FontSize == tb.FontSize
                && ta.Weight == tb.Weight
                && ta.FontStyle == tb.FontStyle
                && ta.HorizontalAlignment == tb.HorizontalAlignment
                && ta.Setters.Length == 0 && tb.Setters.Length == 0,

            (ButtonElement ba, ButtonElement bb) =>
                ba.Label == bb.Label
                && ba.IsEnabled == bb.IsEnabled
                && ReferenceEquals(ba.OnClick, bb.OnClick)
                && ba.ContentElement is null && bb.ContentElement is null
                && ba.Setters.Length == 0 && bb.Setters.Length == 0,

            (ImageElement ia, ImageElement ib) =>
                ia.Source == ib.Source
                && ia.Setters.Length == 0 && ib.Setters.Length == 0,

            (RectangleElement ra, RectangleElement rb) =>
                ra.Setters.Length == 0 && rb.Setters.Length == 0,

            (EllipseElement ea, EllipseElement eb) =>
                ea.Setters.Length == 0 && eb.Setters.Length == 0,

            (RichTextBlockElement ra, RichTextBlockElement rb) =>
                ra.Text == rb.Text
                && ra.FontSize == rb.FontSize
                && ra.IsTextSelectionEnabled == rb.IsTextSelectionEnabled
                && ra.TextWrapping == rb.TextWrapping
                && ParagraphsEqual(ra.Paragraphs, rb.Paragraphs)
                && ra.Setters.Length == 0 && rb.Setters.Length == 0,

            // Container elements: compare own props + children by reference.
            // Same children reference = truly unchanged subtree = safe to skip entirely.
            // Different children reference = fall through to UpdateXxx which recurses.
            (StackElement sa, StackElement sb) =>
                sa.Orientation == sb.Orientation
                && sa.Spacing == sb.Spacing
                && sa.HorizontalAlignment == sb.HorizontalAlignment
                && sa.VerticalAlignment == sb.VerticalAlignment
                && ReferenceEquals(sa.Children, sb.Children)
                && sa.Setters.Length == 0 && sb.Setters.Length == 0,

            (BorderElement ba, BorderElement bb) =>
                ReferenceEquals(ba.Background, bb.Background)
                && ReferenceEquals(ba.BorderBrush, bb.BorderBrush)
                && ba.CornerRadius == bb.CornerRadius
                && ba.Padding == bb.Padding
                && ba.BorderThickness == bb.BorderThickness
                && ReferenceEquals(ba.Child, bb.Child)
                && ba.Setters.Length == 0 && bb.Setters.Length == 0,

            (GridElement ga, GridElement gb) =>
                ga.RowSpacing == gb.RowSpacing
                && ga.ColumnSpacing == gb.ColumnSpacing
                && ReferenceEquals(ga.Definition, gb.Definition)
                && ReferenceEquals(ga.Children, gb.Children)
                && ga.Setters.Length == 0 && gb.Setters.Length == 0,

            (ScrollViewElement sva, ScrollViewElement svb) =>
                sva.Orientation == svb.Orientation
                && sva.HorizontalScrollBarVisibility == svb.HorizontalScrollBarVisibility
                && sva.VerticalScrollBarVisibility == svb.VerticalScrollBarVisibility
                && sva.HorizontalScrollMode == svb.HorizontalScrollMode
                && sva.VerticalScrollMode == svb.VerticalScrollMode
                && sva.ZoomMode == svb.ZoomMode
                && ReferenceEquals(sva.Child, svb.Child)
                && sva.Setters.Length == 0 && svb.Setters.Length == 0,

            (EmptyElement, EmptyElement) => true,

            // ErrorBoundary contains delegates — always update
            (ErrorBoundaryElement, ErrorBoundaryElement) => false,

            // Conservative: unknown element types always update
            _ => false,
        };
    }

    /// <summary>
    /// Like ShallowEquals but for container types, ignores child/children references.
    /// Returns true when the element's own WinUI-mapped properties are unchanged,
    /// meaning the only reason Update was entered is to recurse into children.
    /// Used by the highlight overlay to avoid marking containers yellow when only
    /// their children changed (the children themselves will be individually captured).
    /// Conservative: returns false for unknown/non-container types (assume props changed).
    /// </summary>
    internal static bool OwnPropsEqual(Element a, Element b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.GetType() != b.GetType()) return false;

        return (a, b) switch
        {
            // Container types: same checks as ShallowEquals minus Children/Child refs
            (StackElement sa, StackElement sb) =>
                sa.Orientation == sb.Orientation
                && sa.Spacing == sb.Spacing
                && sa.HorizontalAlignment == sb.HorizontalAlignment
                && sa.VerticalAlignment == sb.VerticalAlignment
                && sa.Setters.Length == 0 && sb.Setters.Length == 0,

            (Core.GridElement ga, Core.GridElement gb) =>
                ga.RowSpacing == gb.RowSpacing
                && ga.ColumnSpacing == gb.ColumnSpacing
                && ReferenceEquals(ga.Definition, gb.Definition)
                && ga.Setters.Length == 0 && gb.Setters.Length == 0,

            (BorderElement ba, BorderElement bb) =>
                ReferenceEquals(ba.Background, bb.Background)
                && ReferenceEquals(ba.BorderBrush, bb.BorderBrush)
                && ba.CornerRadius == bb.CornerRadius
                && ba.Padding == bb.Padding
                && ba.BorderThickness == bb.BorderThickness
                && ba.Setters.Length == 0 && bb.Setters.Length == 0,

            (ScrollViewElement sva, ScrollViewElement svb) =>
                sva.Orientation == svb.Orientation
                && sva.HorizontalScrollBarVisibility == svb.HorizontalScrollBarVisibility
                && sva.VerticalScrollBarVisibility == svb.VerticalScrollBarVisibility
                && sva.HorizontalScrollMode == svb.HorizontalScrollMode
                && sva.VerticalScrollMode == svb.VerticalScrollMode
                && sva.ZoomMode == svb.ZoomMode
                && sva.Setters.Length == 0 && svb.Setters.Length == 0,

            (CanvasElement ca, CanvasElement cb) =>
                ca.Setters.Length == 0 && cb.Setters.Length == 0,

            (WrapGridElement wa, WrapGridElement wb) =>
                wa.Orientation == wb.Orientation
                && wa.ItemWidth == wb.ItemWidth
                && wa.ItemHeight == wb.ItemHeight
                && wa.MaximumRowsOrColumns == wb.MaximumRowsOrColumns
                && wa.Setters.Length == 0 && wb.Setters.Length == 0,

            (RelativePanelElement ra, RelativePanelElement rb) =>
                ra.Setters.Length == 0 && rb.Setters.Length == 0,

            (ViewboxElement va, ViewboxElement vb) =>
                va.Setters.Length == 0 && vb.Setters.Length == 0,

            // Structural wrappers that only contain children
            (NavigationHostElement, NavigationHostElement) => true,
            (CommandHostElement, CommandHostElement) => true,
            (PopupElement pa, PopupElement pb) =>
                pa.IsOpen == pb.IsOpen
                && pa.IsLightDismissEnabled == pb.IsLightDismissEnabled,

            // Non-container / leaf types: return false → always captured
            _ => false,
        };
    }

    /// <summary>
    /// Structural comparison of RichTextParagraph arrays.
    /// Compares each paragraph's inlines using record equality.
    /// </summary>
    private static bool ParagraphsEqual(RichTextParagraph[]? a, RichTextParagraph[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (!ParagraphEqual(a[i], b[i])) return false;
        }
        return true;
    }

    /// <summary>
    /// Structural comparison of a single RichTextParagraph (inline-by-inline record equality).
    /// </summary>
    internal static bool ParagraphEqual(RichTextParagraph a, RichTextParagraph b)
    {
        if (ReferenceEquals(a, b)) return true;
        var ai = a.Inlines;
        var bi = b.Inlines;
        if (ai.Length != bi.Length) return false;
        for (int j = 0; j < ai.Length; j++)
        {
            if (!ai[j].Equals(bi[j])) return false;
        }
        return true;
    }

    /// <summary>
    /// Compare two ElementModifiers for rendering equivalence.
    /// Uses ReferenceEquals for Brush properties (BrushHelper.Parse caches instances).
    /// Ignores OnMountAction (only runs at mount time, not during update).
    /// </summary>
    internal static bool ModifiersEqual(ElementModifiers? a, ElementModifiers? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;

        return a.Margin == b.Margin
            && a.Padding == b.Padding
            && a.Width == b.Width
            && a.Height == b.Height
            && a.MinWidth == b.MinWidth
            && a.MinHeight == b.MinHeight
            && a.MaxWidth == b.MaxWidth
            && a.MaxHeight == b.MaxHeight
            && a.HorizontalAlignment == b.HorizontalAlignment
            && a.VerticalAlignment == b.VerticalAlignment
            && a.Opacity == b.Opacity
            && a.IsVisible == b.IsVisible
            && a.IsEnabled == b.IsEnabled
            && a.CornerRadius == b.CornerRadius
            && a.BorderThickness == b.BorderThickness
            && a.ElementSoundMode == b.ElementSoundMode
            && a.ToolTip == b.ToolTip
            && a.AutomationName == b.AutomationName
            && a.AutomationId == b.AutomationId
            && ReferenceEquals(a.Background, b.Background)
            && ReferenceEquals(a.Foreground, b.Foreground)
            && ReferenceEquals(a.BorderBrush, b.BorderBrush)
            && a.FontSize == b.FontSize
            && a.FontWeight == b.FontWeight
            && ReferenceEquals(a.FontFamily, b.FontFamily)
            // Skip OnMountAction — only runs at mount time
            // Skip event handlers — delegate comparison is unreliable, conservative false
            && a.OnSizeChanged is null && b.OnSizeChanged is null
            && a.OnPointerPressed is null && b.OnPointerPressed is null
            && a.OnPointerMoved is null && b.OnPointerMoved is null
            && a.OnPointerReleased is null && b.OnPointerReleased is null
            && a.OnTapped is null && b.OnTapped is null
            && a.OnKeyDown is null && b.OnKeyDown is null
            // Skip RichToolTip, AttachedFlyout, ContextFlyout — rare, conservative false
            && a.RichToolTip is null && b.RichToolTip is null
            && a.AttachedFlyout is null && b.AttachedFlyout is null
            && a.ContextFlyout is null && b.ContextFlyout is null
            // Accessibility Tier 1
            && a.HeadingLevel == b.HeadingLevel
            && a.IsTabStop == b.IsTabStop
            && a.TabIndex == b.TabIndex
            && a.AccessKey == b.AccessKey
            // Accessibility Tier 2/3 — short-circuit on null
            && ReferenceEquals(a.Accessibility, b.Accessibility);
    }

    /// <summary>
    /// Compare two Attached property dictionaries by content.
    /// Common case: both have a single GridAttached entry (a record with structural equality).
    /// </summary>
    internal static bool AttachedEqual(IReadOnlyDictionary<Type, object>? a, IReadOnlyDictionary<Type, object>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a.Count != b.Count) return false;

        foreach (var (key, valA) in a)
        {
            if (!b.TryGetValue(key, out var valB)) return false;
            if (!Equals(valA, valB)) return false; // GridAttached is a record — Equals works
        }
        return true;
    }

    internal static bool ThemeBindingsEqual(IReadOnlyDictionary<string, ThemeRef>? a, IReadOnlyDictionary<string, ThemeRef>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a.Count != b.Count) return false;

        foreach (var (key, valA) in a)
        {
            if (!b.TryGetValue(key, out var valB)) return false;
            if (valA.ResourceKey != valB.ResourceKey) return false;
        }
        return true;
    }

    internal static bool ContextValuesEqual(IReadOnlyDictionary<ContextBase, object?>? a, IReadOnlyDictionary<ContextBase, object?>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a.Count != b.Count) return false;

        foreach (var (key, valA) in a)
        {
            if (!b.TryGetValue(key, out var valB)) return false;
            if (!Equals(valA, valB)) return false;
        }
        return true;
    }
}

/// <summary>
/// An element that renders nothing (used for conditional rendering).
/// </summary>
public record EmptyElement : Element
{
    public static readonly EmptyElement Instance = new();
}

/// <summary>
/// A transparent grouping element (like React's Fragment). Does not introduce
/// any layout container — its children are flattened into the parent.
/// Produced by <c>ForEach</c> and <c>Group()</c> in the DSL.
/// </summary>
public record GroupElement(Element[] Children) : Element;

/// <summary>
/// Catches render errors in its child subtree and displays fallback UI.
/// Like React's ErrorBoundary — catches errors during rendering, not event handlers.
/// When the ErrorBoundary re-renders, it retries the child (error recovery).
/// </summary>
public record ErrorBoundaryElement(Element Child, Func<Exception, Element> Fallback) : Element;

/// <summary>
/// Wraps any element with layout modifiers (margin, alignment, size, etc.).
/// Kept for backward compatibility. New code stores modifiers inline on Element.Modifiers.
/// </summary>
public record ModifiedElement(Element Inner, ElementModifiers WrappedModifiers) : Element;

/// <summary>
/// Wraps a Component class so it can participate in the element tree.
/// Created automatically by Component&lt;T&gt;() factory method.
/// </summary>
public record ComponentElement(
    [property: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    Type ComponentType,
    object? Props = null) : Element
{
    // Factory creates the component instance without reflection. Stored as a field
    // so it does not participate in record equality (two ComponentElements for the
    // same Type/Props are equal regardless of factory identity).
    internal Func<Component>? _factory;

    internal Component CreateInstance() =>
        _factory is not null ? _factory() : (Component)Activator.CreateInstance(ComponentType)!;
}

/// <summary>
/// A component defined inline via a render function (like a React function component).
/// </summary>
public record FuncElement(Func<RenderContext, Element> RenderFunc) : Element;

/// <summary>
/// A memoized function component. Skips re-render when Dependencies haven't changed.
/// null Dependencies = render once on mount + self-triggered state changes only.
/// </summary>
public record MemoElement(Func<RenderContext, Element> RenderFunc, object?[]? Dependencies = null) : Element;

// ════════════════════════════════════════════════════════════════════════
//  Semantic wrapper for composite accessibility
// ════════════════════════════════════════════════════════════════════════

/// <summary>
/// Describes the semantic role, value, and range of a composite component
/// for assistive technology. Used with the .Semantics() modifier.
/// </summary>
public record SemanticDescription(
    string? Role = null,
    string? Value = null,
    double? RangeMin = null,
    double? RangeMax = null,
    double? RangeValue = null,
    bool IsReadOnly = true);

/// <summary>
/// Wraps a child element in a SemanticPanel that provides custom automation
/// semantics to screen readers. This solves the problem where Reactor components
/// can't override OnCreateAutomationPeer().
/// </summary>
public record SemanticElement(Element Child, SemanticDescription Semantics) : Element;

public record ElementModifiers
{
    public Thickness? Margin { get; init; }
    public Thickness? Padding { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
    public double? MinWidth { get; init; }
    public double? MinHeight { get; init; }
    public double? MaxWidth { get; init; }
    public double? MaxHeight { get; init; }
    public HorizontalAlignment? HorizontalAlignment { get; init; }
    public VerticalAlignment? VerticalAlignment { get; init; }
    public double? Opacity { get; init; }
    public global::System.Numerics.Vector3? Scale { get; init; }
    public float? Rotation { get; init; }
    public global::System.Numerics.Vector3? Translation { get; init; }
    public global::System.Numerics.Vector3? CenterPoint { get; init; }
    public bool? IsVisible { get; init; }
    public string? ToolTip { get; init; }
    public Element? RichToolTip { get; init; }
    public Element? AttachedFlyout { get; init; }
    public Element? ContextFlyout { get; init; }
    public Brush? Background { get; init; }
    public Brush? Foreground { get; init; }
    public bool? IsEnabled { get; init; }
    public Microsoft.UI.Xaml.CornerRadius? CornerRadius { get; init; }
    public Brush? BorderBrush { get; init; }
    public Thickness? BorderThickness { get; init; }
    public string? AutomationName { get; init; }
    public string? AutomationId { get; init; }
    public ElementSoundMode? ElementSoundMode { get; init; }
    public Action<FrameworkElement>? OnMountAction { get; init; }

    // ── Typography (applies to any Control or TextBlock) ────────────
    public FontFamily? FontFamily { get; init; }
    public double? FontSize { get; init; }
    public FontWeight? FontWeight { get; init; }

    // ── Declarative event handlers (re-attached on every update) ────
    public Action<object, SizeChangedEventArgs>? OnSizeChanged { get; init; }
    public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? OnPointerPressed { get; init; }
    public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? OnPointerMoved { get; init; }
    public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? OnPointerReleased { get; init; }
    public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? OnPointerEntered { get; init; }
    public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? OnPointerExited { get; init; }
    public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? OnPointerCanceled { get; init; }
    public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? OnPointerCaptureLost { get; init; }
    public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? OnPointerWheelChanged { get; init; }
    public Action<object, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs>? OnTapped { get; init; }
    public Action<object, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs>? OnDoubleTapped { get; init; }
    public Action<object, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs>? OnRightTapped { get; init; }
    public Action<object, Microsoft.UI.Xaml.Input.HoldingRoutedEventArgs>? OnHolding { get; init; }
    public Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs>? OnKeyDown { get; init; }
    public Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs>? OnKeyUp { get; init; }
    public Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs>? OnPreviewKeyDown { get; init; }
    public Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs>? OnPreviewKeyUp { get; init; }
    public Action<UIElement, Microsoft.UI.Xaml.Input.CharacterReceivedRoutedEventArgs>? OnCharacterReceived { get; init; }
    public Action<object, RoutedEventArgs>? OnGotFocus { get; init; }
    public Action<object, RoutedEventArgs>? OnLostFocus { get; init; }

    // ── Declarative gesture recognizers (spec 027 Tier 3) ───────────
    // Drive a single ManipulationStarted/Delta/Completed subscription per element.
    public OpenClawTray.Infrastructure.Input.PanGestureConfig? Pan { get; init; }
    public OpenClawTray.Infrastructure.Input.PinchGestureConfig? Pinch { get; init; }
    public OpenClawTray.Infrastructure.Input.RotateGestureConfig? Rotate { get; init; }
    public OpenClawTray.Infrastructure.Input.LongPressGestureConfig? LongPress { get; init; }

    // ── Drag-and-drop (spec 027 Tier 6 — Phase 6a typed in-process) ─
    public OpenClawTray.Infrastructure.Input.DragSourceConfig? DragSource { get; init; }
    public OpenClawTray.Infrastructure.Input.DropTargetConfig? DropTarget { get; init; }

    // ── Logical (BiDi-aware) layout properties ──────────────────────
    // These resolve to physical left/right based on FlowDirection at mount/update time.
    // InlineStart = left in LTR, right in RTL. InlineEnd = right in LTR, left in RTL.
    public double? MarginInlineStart { get; init; }
    public double? MarginInlineEnd { get; init; }
    public double? PaddingInlineStart { get; init; }
    public double? PaddingInlineEnd { get; init; }
    public Thickness? BorderInlineStart { get; init; }

    // ── Theme override ───────────────────────────────────────────────
    /// <summary>
    /// Sets <see cref="FrameworkElement.RequestedTheme"/> on the control,
    /// forcing a subtree to render in a specific theme variant (e.g., dark
    /// sidebar in a light app). Applied before ThemeRef bindings resolve so
    /// that theme resources pick up the correct variant.
    /// </summary>
    public ElementTheme? RequestedTheme { get; init; }

    // ── Accessibility — Tier 1 (inline, commonly needed for WCAG AA) ─
    public Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel? HeadingLevel { get; init; }
    public bool? IsTabStop { get; init; }
    public int? TabIndex { get; init; }
    public string? AccessKey { get; init; }
    public Microsoft.UI.Xaml.Input.XYFocusKeyboardNavigationMode? XYFocusKeyboardNavigation { get; init; }
    public Action<UIElement, Microsoft.UI.Xaml.Input.AccessKeyDisplayRequestedEventArgs>? OnAccessKeyDisplayRequested { get; init; }

    /// <summary>
    /// Imperative ref slot (spec 027 Tier 5). The reconciler writes the mounted
    /// <see cref="FrameworkElement"/> into <see cref="OpenClawTray.Infrastructure.Input.ElementRef._current"/>
    /// so <c>FocusManager.Focus(ref)</c> (and future ref-based imperative APIs) can target it.
    /// </summary>
    public OpenClawTray.Infrastructure.Input.ElementRef? Ref { get; init; }

    // ── Accessibility — Tier 2/3 (lazy sub-record, zero allocation unless used) ─
    public AccessibilityModifiers? Accessibility { get; init; }

    public ElementModifiers Merge(ElementModifiers other)
    {
        return this with
        {
            Margin = other.Margin ?? Margin,
            Padding = other.Padding ?? Padding,
            Width = other.Width ?? Width,
            Height = other.Height ?? Height,
            MinWidth = other.MinWidth ?? MinWidth,
            MinHeight = other.MinHeight ?? MinHeight,
            MaxWidth = other.MaxWidth ?? MaxWidth,
            MaxHeight = other.MaxHeight ?? MaxHeight,
            HorizontalAlignment = other.HorizontalAlignment ?? HorizontalAlignment,
            VerticalAlignment = other.VerticalAlignment ?? VerticalAlignment,
            Opacity = other.Opacity ?? Opacity,
            Scale = other.Scale ?? Scale,
            Rotation = other.Rotation ?? Rotation,
            Translation = other.Translation ?? Translation,
            CenterPoint = other.CenterPoint ?? CenterPoint,
            IsVisible = other.IsVisible ?? IsVisible,
            ToolTip = other.ToolTip ?? ToolTip,
            RichToolTip = other.RichToolTip ?? RichToolTip,
            AttachedFlyout = other.AttachedFlyout ?? AttachedFlyout,
            ContextFlyout = other.ContextFlyout ?? ContextFlyout,
            Background = other.Background ?? Background,
            Foreground = other.Foreground ?? Foreground,
            IsEnabled = other.IsEnabled ?? IsEnabled,
            CornerRadius = other.CornerRadius ?? CornerRadius,
            BorderBrush = other.BorderBrush ?? BorderBrush,
            BorderThickness = other.BorderThickness ?? BorderThickness,
            AutomationName = other.AutomationName ?? AutomationName,
            AutomationId = other.AutomationId ?? AutomationId,
            ElementSoundMode = other.ElementSoundMode ?? ElementSoundMode,
            OnMountAction = other.OnMountAction ?? OnMountAction,
            FontFamily = other.FontFamily ?? FontFamily,
            FontSize = other.FontSize ?? FontSize,
            FontWeight = other.FontWeight ?? FontWeight,
            OnSizeChanged = other.OnSizeChanged ?? OnSizeChanged,
            OnPointerPressed = other.OnPointerPressed ?? OnPointerPressed,
            OnPointerMoved = other.OnPointerMoved ?? OnPointerMoved,
            OnPointerReleased = other.OnPointerReleased ?? OnPointerReleased,
            OnPointerEntered = other.OnPointerEntered ?? OnPointerEntered,
            OnPointerExited = other.OnPointerExited ?? OnPointerExited,
            OnPointerCanceled = other.OnPointerCanceled ?? OnPointerCanceled,
            OnPointerCaptureLost = other.OnPointerCaptureLost ?? OnPointerCaptureLost,
            OnPointerWheelChanged = other.OnPointerWheelChanged ?? OnPointerWheelChanged,
            OnTapped = other.OnTapped ?? OnTapped,
            OnDoubleTapped = other.OnDoubleTapped ?? OnDoubleTapped,
            OnRightTapped = other.OnRightTapped ?? OnRightTapped,
            OnHolding = other.OnHolding ?? OnHolding,
            OnKeyDown = other.OnKeyDown ?? OnKeyDown,
            OnKeyUp = other.OnKeyUp ?? OnKeyUp,
            OnPreviewKeyDown = other.OnPreviewKeyDown ?? OnPreviewKeyDown,
            OnPreviewKeyUp = other.OnPreviewKeyUp ?? OnPreviewKeyUp,
            OnCharacterReceived = other.OnCharacterReceived ?? OnCharacterReceived,
            OnGotFocus = other.OnGotFocus ?? OnGotFocus,
            OnLostFocus = other.OnLostFocus ?? OnLostFocus,
            Pan = other.Pan ?? Pan,
            Pinch = other.Pinch ?? Pinch,
            Rotate = other.Rotate ?? Rotate,
            LongPress = other.LongPress ?? LongPress,
            DragSource = other.DragSource ?? DragSource,
            DropTarget = other.DropTarget ?? DropTarget,
            MarginInlineStart = other.MarginInlineStart ?? MarginInlineStart,
            MarginInlineEnd = other.MarginInlineEnd ?? MarginInlineEnd,
            PaddingInlineStart = other.PaddingInlineStart ?? PaddingInlineStart,
            PaddingInlineEnd = other.PaddingInlineEnd ?? PaddingInlineEnd,
            BorderInlineStart = other.BorderInlineStart ?? BorderInlineStart,
            RequestedTheme = other.RequestedTheme ?? RequestedTheme,
            HeadingLevel = other.HeadingLevel ?? HeadingLevel,
            IsTabStop = other.IsTabStop ?? IsTabStop,
            TabIndex = other.TabIndex ?? TabIndex,
            AccessKey = other.AccessKey ?? AccessKey,
            XYFocusKeyboardNavigation = other.XYFocusKeyboardNavigation ?? XYFocusKeyboardNavigation,
            OnAccessKeyDisplayRequested = other.OnAccessKeyDisplayRequested ?? OnAccessKeyDisplayRequested,
            Ref = other.Ref ?? Ref,
            Accessibility = other.Accessibility is not null
                ? (Accessibility is not null ? Accessibility.Merge(other.Accessibility) : other.Accessibility)
                : Accessibility,
        };
    }
}

/// <summary>
/// Advanced accessibility properties (WCAG Tier 2/3). Stored as a lazy sub-record
/// on ElementModifiers to avoid allocating storage for elements that don't need
/// advanced accessibility annotations. All fluent extension methods create/merge
/// this record automatically — developers never need to construct it directly.
/// </summary>
public record AccessibilityModifiers
{
    /// <summary>AutomationProperties.HelpText — supplemental description read after the Name.</summary>
    public string? HelpText { get; init; }

    /// <summary>AutomationProperties.FullDescription — extended description for complex elements.</summary>
    public string? FullDescription { get; init; }

    /// <summary>AutomationProperties.LandmarkType — landmark region (Main, Navigation, Search, Form).</summary>
    public Microsoft.UI.Xaml.Automation.Peers.AutomationLandmarkType? LandmarkType { get; init; }

    /// <summary>AutomationProperties.AccessibilityView — UIA tree visibility (Content, Control, Raw).</summary>
    public Microsoft.UI.Xaml.Automation.Peers.AccessibilityView? AccessibilityView { get; init; }

    /// <summary>AutomationProperties.IsRequiredForForm — screen readers announce "required".</summary>
    public bool? IsRequiredForForm { get; init; }

    /// <summary>AutomationProperties.LiveSetting — live region announcement mode (Polite, Assertive).</summary>
    public Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting? LiveSetting { get; init; }

    /// <summary>AutomationProperties.PositionInSet — ordinal position (1-based) in a group.</summary>
    public int? PositionInSet { get; init; }

    /// <summary>AutomationProperties.SizeOfSet — total count in the group.</summary>
    public int? SizeOfSet { get; init; }

    /// <summary>AutomationProperties.Level — hierarchical depth (e.g., tree node level).</summary>
    public int? Level { get; init; }

    /// <summary>AutomationProperties.ItemStatus — status string (e.g., "3 unread").</summary>
    public string? ItemStatus { get; init; }

    /// <summary>AutomationProperties.LabeledBy target AutomationId — resolved by the reconciler.</summary>
    public string? LabeledBy { get; init; }

    /// <summary>UIElement.TabFocusNavigation — Tab behavior within a container (Local, Once, Cycle).</summary>
    public Microsoft.UI.Xaml.Input.KeyboardNavigationMode? TabFocusNavigation { get; init; }

    public AccessibilityModifiers Merge(AccessibilityModifiers other)
    {
        return this with
        {
            HelpText = other.HelpText ?? HelpText,
            FullDescription = other.FullDescription ?? FullDescription,
            LandmarkType = other.LandmarkType ?? LandmarkType,
            AccessibilityView = other.AccessibilityView ?? AccessibilityView,
            IsRequiredForForm = other.IsRequiredForForm ?? IsRequiredForForm,
            LiveSetting = other.LiveSetting ?? LiveSetting,
            PositionInSet = other.PositionInSet ?? PositionInSet,
            SizeOfSet = other.SizeOfSet ?? SizeOfSet,
            Level = other.Level ?? Level,
            ItemStatus = other.ItemStatus ?? ItemStatus,
            LabeledBy = other.LabeledBy ?? LabeledBy,
            TabFocusNavigation = other.TabFocusNavigation ?? TabFocusNavigation,
        };
    }
}

// ════════════════════════════════════════════════════════════════════════
//  Transition data records (stored on Element base, applied by Reconciler)
// ════════════════════════════════════════════════════════════════════════

/// <summary>
/// Declarative implicit transition configuration for a UIElement.
/// Each property maps to a WinUI implicit transition property on UIElement/Panel.
/// Null means "don't set this transition".
/// </summary>
public record ImplicitTransitions
{
    public ScalarTransition? Opacity { get; init; }
    public ScalarTransition? Rotation { get; init; }
    public Vector3Transition? Scale { get; init; }
    public Vector3Transition? Translation { get; init; }
    public BrushTransition? Background { get; init; }
}

/// <summary>
/// Declarative theme transition configuration.
/// Children applies to Panel.ChildrenTransitions / Border.ChildTransitions / ContentControl.ContentTransitions.
/// ItemContainer applies to ItemsControl.ItemContainerTransitions.
/// The reconciler picks the correct property based on control type.
/// </summary>
public record ThemeTransitions
{
    public Microsoft.UI.Xaml.Media.Animation.Transition[]? Children { get; init; }
    public Microsoft.UI.Xaml.Media.Animation.Transition[]? ItemContainer { get; init; }
}
// Note: Transition is in Microsoft.UI.Xaml.Media.Animation (not imported by default in Element.cs)

/// <summary>
/// Configuration for Composition-layer layout animations.
/// When applied to an element, the reconciler sets up implicit animations on the element's
/// Visual so that layout-driven Offset (position) and optionally Size changes animate smoothly.
/// Runs entirely on the Composition thread — zero managed-code involvement during animation.
///
/// Limitations:
/// - Hit-testing uses the final layout position, not the animated visual position.
/// - Elements must have stable keys (.WithKey()) for the reconciler to match them across reorders.
/// - Size animation is cosmetic: content does not re-layout during the Size animation.
/// - Only handles position changes for persistent elements; use theme transitions for enter/exit.
/// </summary>
public record LayoutAnimationConfig
{
    /// <summary>Duration of the layout animation. Default: 300ms.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>When true, use a spring natural motion animation instead of linear keyframes.</summary>
    public bool UseSpring { get; init; }

    /// <summary>Spring damping ratio (0..1). Only used when UseSpring is true. Default: 0.6.</summary>
    public float DampingRatio { get; init; } = 0.6f;

    /// <summary>Spring period in seconds. Only used when UseSpring is true. Default: 0.08.</summary>
    public float Period { get; init; } = 0.08f;

    /// <summary>Animate Offset (position) changes. Default: true.</summary>
    public bool AnimateOffset { get; init; } = true;

    /// <summary>Animate Size changes. Default: false (content won't re-layout during animation).</summary>
    public bool AnimateSize { get; init; }
}

// Reactor reuses WinUI types directly — no shadow enums.
// See: Microsoft.UI.Xaml (Thickness, HorizontalAlignment, VerticalAlignment)
//      Microsoft.UI.Xaml.Controls (Orientation, InfoBarSeverity, ExpandDirection, etc.)
//      Microsoft.UI.Xaml.Controls.Primitives (FlyoutPlacementMode)
//      global::Windows.UI.Text (FontWeight, FontWeights)

// ════════════════════════════════════════════════════════════════════════
//  Supporting data records (non-Element, used as structured params)
// ════════════════════════════════════════════════════════════════════════

public record GridDefinition(string[] Columns, string[] Rows);

/// <summary>Attached property data for Grid children. Set via .Grid(row:, column:) extension.</summary>
public record GridAttached(int Row = 0, int Column = 0, int RowSpan = 1, int ColumnSpan = 1);

/// <summary>Attached property data for Canvas children. Set via .Canvas(left:, top:) extension.</summary>
public record CanvasAttached(double Left = 0, double Top = 0);

/// <summary>Attached property data for RelativePanel children. Set via .RelativePanel(...) extension.</summary>
public record RelativePanelAttached(string Name)
{
    public string? RightOf { get; init; }
    public string? Below { get; init; }
    public string? LeftOf { get; init; }
    public string? Above { get; init; }
    public string? AlignLeftWith { get; init; }
    public string? AlignRightWith { get; init; }
    public string? AlignTopWith { get; init; }
    public string? AlignBottomWith { get; init; }
    public string? AlignHorizontalCenterWith { get; init; }
    public string? AlignVerticalCenterWith { get; init; }
    public bool AlignLeftWithPanel { get; init; }
    public bool AlignRightWithPanel { get; init; }
    public bool AlignTopWithPanel { get; init; }
    public bool AlignBottomWithPanel { get; init; }
    public bool AlignHorizontalCenterWithPanel { get; init; }
    public bool AlignVerticalCenterWithPanel { get; init; }
}

public record NavigationViewItemData(string Content, string? Icon = null, string? Tag = null)
{
    public NavigationViewItemData[]? Children { get; init; }
    public bool IsHeader { get; init; }
    public IconData? IconElement { get; init; }
}

public record TabViewItemData(string Header, Element Content)
{
    public string? Icon { get; init; }
    public bool IsClosable { get; init; } = true;
}

public record PivotItemData(string Header, Element Content);

public record BreadcrumbBarItemData(string Label, object? Tag = null);

public record TreeViewNodeData(string Content, TreeViewNodeData[]? Children = null)
{
    public bool IsExpanded { get; init; }

    /// <summary>
    /// Optional Reactor element to render as the node's visual content.
    /// When null, a TextBlock showing Content is rendered.
    /// </summary>
    public Element? ContentElement { get; init; }
}

public record MenuBarItemData(string Title, MenuFlyoutItemBase[] Items);

public abstract record MenuFlyoutItemBase;
public record MenuFlyoutItemData(string Text, Action? OnClick = null, string? Icon = null) : MenuFlyoutItemBase
{
    public bool IsEnabled { get; init; } = true;
    public IconData? IconElement { get; init; }
    public KeyboardAcceleratorData[]? KeyboardAccelerators { get; init; }
    public string? AccessKey { get; init; }
    public string? Description { get; init; }
}
public record MenuFlyoutSeparatorData() : MenuFlyoutItemBase;
public record MenuFlyoutSubItemData(string Text, MenuFlyoutItemBase[] Items, string? Icon = null) : MenuFlyoutItemBase
{
    public IconData? IconElement { get; init; }
}
public record ToggleMenuFlyoutItemData(string Text, bool IsChecked = false, Action<bool>? OnToggled = null, string? Icon = null) : MenuFlyoutItemBase
{
    public IconData? IconElement { get; init; }
}
public record RadioMenuFlyoutItemData(string Text, string GroupName, bool IsChecked = false, Action? OnClick = null, string? Icon = null) : MenuFlyoutItemBase
{
    public IconData? IconElement { get; init; }
}

// Keyboard accelerator data
public record KeyboardAcceleratorData(global::Windows.System.VirtualKey Key, global::Windows.System.VirtualKeyModifiers Modifiers = global::Windows.System.VirtualKeyModifiers.None);

// Icon data hierarchy — used to set icons on menu items, app bar buttons, etc.
public abstract record IconData;
public record SymbolIconData(string Symbol) : IconData;
public record FontIconData(string Glyph, string? FontFamily = null, double? FontSize = null) : IconData;
public record BitmapIconData(global::System.Uri Source, bool ShowAsMonochrome = true) : IconData;
public record PathIconData(string Data) : IconData;
public record ImageIconData(global::System.Uri Source) : IconData;

public abstract record AppBarItemBase;
public record AppBarButtonData(string Label, Action? OnClick = null, string? Icon = null) : AppBarItemBase
{
    public bool IsEnabled { get; init; } = true;
    public IconData? IconElement { get; init; }
    public KeyboardAcceleratorData[]? KeyboardAccelerators { get; init; }
    public string? AccessKey { get; init; }
    public string? Description { get; init; }
}
public record AppBarToggleButtonData(string Label, bool IsChecked = false, Action<bool>? OnToggled = null, string? Icon = null) : AppBarItemBase
{
    public IconData? IconElement { get; init; }
}
public record AppBarSeparatorData() : AppBarItemBase;

/// <summary>
/// Scopes keyboard accelerators from a set of commands to a subtree.
/// Accelerators are only active when the host or its descendants have focus.
/// </summary>
public record CommandHostElement(Command[] Commands, Element Child) : Element;

// ════════════════════════════════════════════════════════════════════════
//  Text elements
// ════════════════════════════════════════════════════════════════════════

public record TextBlockElement(string Content) : Element
{
    public double? FontSize { get; init; }
    public FontWeight? Weight { get; init; }
    public global::Windows.UI.Text.FontStyle? FontStyle { get; init; }
    public HorizontalAlignment? HorizontalAlignment { get; init; }
    public TextWrapping? TextWrapping { get; init; }
    public TextAlignment? TextAlignment { get; init; }
    public TextTrimming? TextTrimming { get; init; }
    public bool? IsTextSelectionEnabled { get; init; }
    public Microsoft.UI.Xaml.Media.FontFamily? FontFamily { get; init; }
    internal Action<WinUI.TextBlock>[] Setters { get; init; } = [];

    /// <summary>
    /// EXP-2: Bitmask diff — compare two TextBlockElement instances (pure C#, no COM interop)
    /// and return which properties actually changed. Callers only touch WinUI for set bits.
    /// </summary>
    internal static TextPropChanged DiffProps(TextBlockElement old, TextBlockElement cur)
    {
        var diff = TextPropChanged.None;
        if (old.Content != cur.Content) diff |= TextPropChanged.Content;
        if (old.FontSize != cur.FontSize) diff |= TextPropChanged.FontSize;
        if (old.Weight != cur.Weight) diff |= TextPropChanged.Weight;
        if (old.FontStyle != cur.FontStyle) diff |= TextPropChanged.FontStyle;
        if (old.HorizontalAlignment != cur.HorizontalAlignment) diff |= TextPropChanged.HorizontalAlignment;
        if (old.TextWrapping != cur.TextWrapping) diff |= TextPropChanged.TextWrapping;
        if (old.TextAlignment != cur.TextAlignment) diff |= TextPropChanged.TextAlignment;
        if (old.TextTrimming != cur.TextTrimming) diff |= TextPropChanged.TextTrimming;
        if (old.IsTextSelectionEnabled != cur.IsTextSelectionEnabled) diff |= TextPropChanged.IsTextSelectionEnabled;
        if (old.FontFamily != cur.FontFamily) diff |= TextPropChanged.FontFamily;
        if (old.Setters.Length != cur.Setters.Length) diff |= TextPropChanged.Setters;
        else if (cur.Setters.Length > 0) diff |= TextPropChanged.Setters; // can't compare delegates
        return diff;
    }
}

[Flags]
internal enum TextPropChanged : ushort
{
    None                = 0,
    Content             = 1 << 0,
    FontSize            = 1 << 1,
    Weight              = 1 << 2,
    FontStyle           = 1 << 3,
    HorizontalAlignment = 1 << 4,
    TextWrapping        = 1 << 5,
    TextAlignment       = 1 << 6,
    TextTrimming        = 1 << 7,
    IsTextSelectionEnabled = 1 << 8,
    FontFamily          = 1 << 9,
    Setters             = 1 << 10,
}

public record RichTextBlockElement(string Text) : Element
{
    public double? FontSize { get; init; }
    public RichTextParagraph[]? Paragraphs { get; init; }
    public bool IsTextSelectionEnabled { get; init; }
    public TextWrapping? TextWrapping { get; init; }
    internal Action<WinUI.RichTextBlock>[] Setters { get; init; } = [];
}

// Rich text inline content types
public record RichTextParagraph(RichTextInline[] Inlines);

public abstract record RichTextInline;

public record RichTextRun(string Text) : RichTextInline
{
    public bool IsBold { get; init; }
    public bool IsItalic { get; init; }
    public bool IsStrikethrough { get; init; }
    public double? FontSize { get; init; }
    public string? FontFamily { get; init; }
    public Brush? Foreground { get; init; }
}

public record RichTextHyperlink(string Text, Uri NavigateUri) : RichTextInline;

public record RichTextLineBreak() : RichTextInline;

// ════════════════════════════════════════════════════════════════════════
//  Button elements
// ════════════════════════════════════════════════════════════════════════

public record ButtonElement(string Label, Action? OnClick = null) : Element
{
    public bool IsEnabled { get; init; } = true;
    public Element? ContentElement { get; init; }
    internal Action<WinUI.Button>[] Setters { get; init; } = [];
}

public record HyperlinkButtonElement(string Content, Uri? NavigateUri = null, Action? OnClick = null) : Element
{
    internal Action<WinUI.HyperlinkButton>[] Setters { get; init; } = [];
}

public record RepeatButtonElement(string Label, Action? OnClick = null) : Element
{
    public int Delay { get; init; } = 250;
    public int Interval { get; init; } = 50;
    internal Action<WinPrim.RepeatButton>[] Setters { get; init; } = [];
}

public record ToggleButtonElement(string Label, bool IsChecked = false, Action<bool>? OnToggled = null) : Element
{
    internal Action<WinPrim.ToggleButton>[] Setters { get; init; } = [];
}

public record DropDownButtonElement(string Label, Element? Flyout = null) : Element
{
    internal Action<WinUI.DropDownButton>[] Setters { get; init; } = [];
}

public record SplitButtonElement(string Label, Action? OnClick = null, Element? Flyout = null) : Element
{
    internal Action<WinUI.SplitButton>[] Setters { get; init; } = [];
}

public record ToggleSplitButtonElement(string Label, bool IsChecked = false, Action<bool>? OnIsCheckedChanged = null, Element? Flyout = null) : Element
{
    internal Action<WinUI.ToggleSplitButton>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  Input elements
// ════════════════════════════════════════════════════════════════════════

public record TextFieldElement(
    string Value,
    Action<string>? OnChanged = null,
    string? Placeholder = null
) : Element
{
    public string? Header { get; init; }
    public bool? IsReadOnly { get; init; }
    public bool? AcceptsReturn { get; init; }
    public TextWrapping? TextWrapping { get; init; }
    /// <summary>Fires when the text selection changes. Receives (selectedText, selectionStart, selectionLength).</summary>
    public Action<string, int, int>? OnSelectionChanged { get; init; }
    /// <summary>Caret / selection start position. Set this to control where the caret sits after a text update.</summary>
    public int? SelectionStart { get; init; }
    /// <summary>Selection length. Set alongside SelectionStart to control the selection range.</summary>
    public int? SelectionLength { get; init; }
    internal Action<WinUI.TextBox>[] Setters { get; init; } = [];
}

public record PasswordBoxElement(
    string Password,
    Action<string>? OnPasswordChanged = null,
    string? PlaceholderText = null
) : Element
{
    internal Action<WinUI.PasswordBox>[] Setters { get; init; } = [];
}

public record NumberBoxElement(
    double Value,
    Action<double>? OnValueChanged = null,
    string? Header = null
) : Element
{
    public double Minimum { get; init; } = double.MinValue;
    public double Maximum { get; init; } = double.MaxValue;
    public string? PlaceholderText { get; init; }
    public NumberBoxSpinButtonPlacementMode SpinButtonPlacement { get; init; } = NumberBoxSpinButtonPlacementMode.Hidden;
    public double SmallChange { get; init; } = 1;
    public double LargeChange { get; init; } = 10;
    internal Action<WinUI.NumberBox>[] Setters { get; init; } = [];
}

public record AutoSuggestBoxElement(
    string Text,
    Action<string>? OnTextChanged = null,
    Action<string>? OnQuerySubmitted = null,
    Action<string>? OnSuggestionChosen = null
) : Element
{
    public string[] Suggestions { get; init; } = [];
    public string? PlaceholderText { get; init; }
    internal Action<WinUI.AutoSuggestBox>[] Setters { get; init; } = [];
}

public record CheckBoxElement(
    bool IsChecked,
    Action<bool>? OnChanged = null,
    string? Label = null
) : Element
{
    public bool IsThreeState { get; init; }
    public bool? CheckedState { get; init; }
    public Action<bool?>? OnCheckedStateChanged { get; init; }
    internal Action<WinUI.CheckBox>[] Setters { get; init; } = [];
}

public record RadioButtonElement(
    string Label,
    bool IsChecked = false,
    Action<bool>? OnChecked = null,
    string? GroupName = null
) : Element
{
    internal Action<WinUI.RadioButton>[] Setters { get; init; } = [];
}

public record RadioButtonsElement(
    string[] Items,
    int SelectedIndex = -1,
    Action<int>? OnSelectionChanged = null
) : Element
{
    public string? Header { get; init; }
    internal Action<WinUI.RadioButtons>[] Setters { get; init; } = [];
}

public record ComboBoxElement(
    string[] Items,
    int SelectedIndex = -1,
    Action<int>? OnSelectionChanged = null
) : Element
{
    public string? PlaceholderText { get; init; }
    public string? Header { get; init; }
    public bool IsEditable { get; init; }
    public Element[]? ItemElements { get; init; }
    internal Action<WinUI.ComboBox>[] Setters { get; init; } = [];
}

public record SliderElement(
    double Value,
    double Min = 0,
    double Max = 100,
    Action<double>? OnChanged = null
) : Element
{
    public double StepFrequency { get; init; } = 1;
    public string? Header { get; init; }
    internal Action<WinUI.Slider>[] Setters { get; init; } = [];
}

public record ToggleSwitchElement(
    bool IsOn,
    Action<bool>? OnChanged = null,
    string? OnContent = null,
    string? OffContent = null
) : Element
{
    public string? Header { get; init; }
    internal Action<WinUI.ToggleSwitch>[] Setters { get; init; } = [];
}

public record RatingControlElement(
    double Value = 0,
    Action<double>? OnValueChanged = null
) : Element
{
    public int MaxRating { get; init; } = 5;
    public bool IsReadOnly { get; init; }
    public string? Caption { get; init; }
    internal Action<WinUI.RatingControl>[] Setters { get; init; } = [];
}

public record ColorPickerElement(
    global::Windows.UI.Color Color,
    Action<global::Windows.UI.Color>? OnColorChanged = null
) : Element
{
    public bool IsAlphaEnabled { get; init; }
    public bool IsMoreButtonVisible { get; init; }
    public bool IsColorSpectrumVisible { get; init; } = true;
    public bool IsColorSliderVisible { get; init; } = true;
    public bool IsColorChannelTextInputVisible { get; init; } = true;
    public bool IsHexInputVisible { get; init; } = true;
    internal Action<WinUI.ColorPicker>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  Date / Time elements
// ════════════════════════════════════════════════════════════════════════

public record CalendarDatePickerElement(
    DateTimeOffset? Date = null,
    Action<DateTimeOffset?>? OnDateChanged = null
) : Element
{
    public string? PlaceholderText { get; init; }
    public string? Header { get; init; }
    public DateTimeOffset? MinDate { get; init; }
    public DateTimeOffset? MaxDate { get; init; }
    internal Action<WinUI.CalendarDatePicker>[] Setters { get; init; } = [];
}

public record DatePickerElement(
    DateTimeOffset Date,
    Action<DateTimeOffset>? OnDateChanged = null
) : Element
{
    public string? Header { get; init; }
    public DateTimeOffset? MinYear { get; init; }
    public DateTimeOffset? MaxYear { get; init; }
    public bool DayVisible { get; init; } = true;
    public bool MonthVisible { get; init; } = true;
    public bool YearVisible { get; init; } = true;
    internal Action<WinUI.DatePicker>[] Setters { get; init; } = [];
}

public record TimePickerElement(
    TimeSpan Time,
    Action<TimeSpan>? OnTimeChanged = null
) : Element
{
    public string? Header { get; init; }
    public int MinuteIncrement { get; init; } = 1;
    public int ClockIdentifier { get; init; } = 12;
    internal Action<WinUI.TimePicker>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  Progress elements
// ════════════════════════════════════════════════════════════════════════

public record ProgressElement(double? Value = null) : Element  // null = indeterminate
{
    public bool IsIndeterminate => Value is null;
    public double Minimum { get; init; } = 0;
    public double Maximum { get; init; } = 100;
    public bool ShowError { get; init; }
    public bool ShowPaused { get; init; }
    internal Action<WinUI.ProgressBar>[] Setters { get; init; } = [];
}

public record ProgressRingElement(double? Value = null) : Element
{
    public bool IsIndeterminate => Value is null;
    public double Minimum { get; init; } = 0;
    public double Maximum { get; init; } = 100;
    public bool IsActive { get; init; } = true;
    internal Action<WinUI.ProgressRing>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  Media elements
// ════════════════════════════════════════════════════════════════════════

public record ImageElement(string Source) : Element
{
    public double? Width { get; init; }
    public double? Height { get; init; }
    public string? Stretch { get; init; }
    internal Action<WinUI.Image>[] Setters { get; init; } = [];
}

public record PersonPictureElement() : Element
{
    public string? DisplayName { get; init; }
    public string? Initials { get; init; }
    public string? ProfilePicture { get; init; }
    public bool IsGroup { get; init; }
    public int BadgeNumber { get; init; }
    internal Action<WinUI.PersonPicture>[] Setters { get; init; } = [];
}

public record WebView2Element(Uri? Source = null) : Element
{
    public Action<Uri>? OnNavigationStarting { get; init; }
    public Action<Uri>? OnNavigationCompleted { get; init; }
    internal Action<WinUI.WebView2>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  Rich text elements
// ════════════════════════════════════════════════════════════════════════

public record RichEditBoxElement(
    string Text = ""
) : Element
{
    public bool IsReadOnly { get; init; }
    public string? Header { get; init; }
    public string? PlaceholderText { get; init; }
    public Action<string>? OnTextChanged { get; init; }
    internal Action<WinUI.RichEditBox>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  Layout / Container elements
// ════════════════════════════════════════════════════════════════════════

public record WrapGridElement(
    Element[] Children
) : Element
{
    public int MaximumRowsOrColumns { get; init; } = -1;
    public Orientation Orientation { get; init; } = Orientation.Horizontal;
    public double ItemWidth { get; init; } = double.NaN;
    public double ItemHeight { get; init; } = double.NaN;
    internal Action<WinUI.VariableSizedWrapGrid>[] Setters { get; init; } = [];
}

public record StackElement(
    Orientation Orientation,
    Element[] Children
) : Element
{
    public double Spacing { get; init; } = 8;
    public HorizontalAlignment? HorizontalAlignment { get; init; }
    public VerticalAlignment? VerticalAlignment { get; init; }
    internal Action<WinUI.StackPanel>[] Setters { get; init; } = [];
}

public record GridElement(
    GridDefinition Definition,
    Element[] Children
) : Element
{
    public double RowSpacing { get; init; }
    public double ColumnSpacing { get; init; }
    internal Action<WinUI.Grid>[] Setters { get; init; } = [];
}

public record ScrollViewElement(Element Child) : Element
{
    public Orientation Orientation { get; init; } = Orientation.Vertical;
    public ScrollBarVisibility HorizontalScrollBarVisibility { get; init; } = ScrollBarVisibility.Auto;
    public ScrollBarVisibility VerticalScrollBarVisibility { get; init; } = ScrollBarVisibility.Auto;
    public WinUI.ScrollMode HorizontalScrollMode { get; init; } = WinUI.ScrollMode.Auto;
    public WinUI.ScrollMode VerticalScrollMode { get; init; } = WinUI.ScrollMode.Auto;
    public WinUI.ZoomMode ZoomMode { get; init; } = WinUI.ZoomMode.Disabled;
    internal Action<WinUI.ScrollViewer>[] Setters { get; init; } = [];
}

public record BorderElement(Element Child) : Element
{
    public double? CornerRadius { get; init; }
    public Thickness? Padding { get; init; }
    public Brush? Background { get; init; }
    public Brush? BorderBrush { get; init; }
    public double? BorderThickness { get; init; }
    internal Action<WinUI.Border>[] Setters { get; init; } = [];
}

public record ExpanderElement(
    string Header,
    Element Content,
    bool IsExpanded = false,
    Action<bool>? OnExpandedChanged = null
) : Element
{
    public ExpandDirection ExpandDirection { get; init; } = ExpandDirection.Down;
    internal Action<WinUI.Expander>[] Setters { get; init; } = [];
}

public record SplitViewElement(
    Element? Pane = null,
    Element? Content = null
) : Element
{
    public bool IsPaneOpen { get; init; } = true;
    public double OpenPaneLength { get; init; } = 320;
    public double CompactPaneLength { get; init; } = 48;
    public SplitViewDisplayMode DisplayMode { get; init; } = SplitViewDisplayMode.Overlay;
    public Action<bool>? OnPaneOpenChanged { get; init; }
    internal Action<WinUI.SplitView>[] Setters { get; init; } = [];
}

public record ViewboxElement(Element Child) : Element
{
    public string? Stretch { get; init; }
    internal Action<WinUI.Viewbox>[] Setters { get; init; } = [];
}

public record CanvasElement(Element[] Children) : Element
{
    public double? Width { get; init; }
    public double? Height { get; init; }
    public Brush? Background { get; init; }
    internal Action<WinUI.Canvas>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  Navigation elements
// ════════════════════════════════════════════════════════════════════════

/// <summary>
/// Renders the content for the current route of a <see cref="Navigation.NavigationHandle{TRoute}"/>.
/// Created via <c>NavigationHost&lt;TRoute&gt;(nav, routeMap)</c> in the DSL.
/// The reconciler uses a Grid container so outgoing/incoming pages can overlap during transitions (Phase 4).
/// </summary>
public record NavigationHostElement(
    object NavigationHandle,
    Func<object, Element> RouteMap
) : Element
{
    public Navigation.NavigationTransition Transition { get; init; } = Navigation.NavigationTransition.Default;
    public Navigation.NavigationCacheMode CacheMode { get; init; } = Navigation.NavigationCacheMode.Disabled;
    public int CacheSize { get; init; } = 10;
}

public record NavigationViewElement(
    NavigationViewItemData[] MenuItems,
    Element? Content = null
) : Element
{
    public string? SelectedTag { get; init; }
    public Action<string?>? OnSelectionChanged { get; init; }
    public bool IsPaneOpen { get; init; } = true;
    public NavigationViewPaneDisplayMode PaneDisplayMode { get; init; } = NavigationViewPaneDisplayMode.Auto;
    public bool IsBackEnabled { get; init; }
    public Action? OnBackRequested { get; init; }
    public Element? Header { get; init; }
    public bool IsSettingsVisible { get; init; } = true;
    public string? PaneTitle { get; init; }
    internal Action<WinUI.NavigationView>[] Setters { get; init; } = [];
}

public record TitleBarElement(
    string Title
) : Element
{
    public string? Subtitle { get; init; }
    public bool IsBackButtonVisible { get; init; }
    public bool IsBackButtonEnabled { get; init; }
    public Action? OnBackRequested { get; init; }
    public bool IsPaneToggleButtonVisible { get; init; }
    public Action? OnPaneToggleRequested { get; init; }
    public Element? Content { get; init; }
    public Element? RightHeader { get; init; }
    internal Action<WinUI.TitleBar>[] Setters { get; init; } = [];
}

public record TabViewElement(
    TabViewItemData[] Tabs
) : Element
{
    public int SelectedIndex { get; init; } = 0;
    public Action<int>? OnSelectionChanged { get; init; }
    public Action<int>? OnTabCloseRequested { get; init; }
    public Action? OnAddTabButtonClick { get; init; }
    public bool IsAddTabButtonVisible { get; init; }
    internal Action<WinUI.TabView>[] Setters { get; init; } = [];
}

public record BreadcrumbBarElement(
    BreadcrumbBarItemData[] Items,
    Action<BreadcrumbBarItemData>? OnItemClicked = null
) : Element
{
    internal Action<WinUI.BreadcrumbBar>[] Setters { get; init; } = [];
}

public record PivotElement(
    PivotItemData[] Items
) : Element
{
    public int SelectedIndex { get; init; } = 0;
    public Action<int>? OnSelectionChanged { get; init; }
    public string? Title { get; init; }
    internal Action<WinUI.Pivot>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  Collection elements (simple, no item templating)
// ════════════════════════════════════════════════════════════════════════

public record ListViewElement(
    Element[] Items
) : Element
{
    public int SelectedIndex { get; init; } = -1;
    public Action<int>? OnSelectionChanged { get; init; }
    public Action<int>? OnItemClick { get; init; }
    public ListViewSelectionMode SelectionMode { get; init; } = ListViewSelectionMode.Single;
    public string? Header { get; init; }
    internal Action<WinUI.ListView>[] Setters { get; init; } = [];
}

public record GridViewElement(
    Element[] Items
) : Element
{
    public int SelectedIndex { get; init; } = -1;
    public Action<int>? OnSelectionChanged { get; init; }
    public Action<int>? OnItemClick { get; init; }
    public ListViewSelectionMode SelectionMode { get; init; } = ListViewSelectionMode.Single;
    public string? Header { get; init; }
    internal Action<WinUI.GridView>[] Setters { get; init; } = [];
}

public record TreeViewElement(
    TreeViewNodeData[] Nodes
) : Element
{
    public Action<TreeViewNodeData>? OnItemInvoked { get; init; }
    public Action<TreeViewNodeData>? OnExpanding { get; init; }
    public TreeViewSelectionMode SelectionMode { get; init; } = TreeViewSelectionMode.Single;
    public bool CanDragItems { get; init; }
    public bool AllowDrop { get; init; }
    public bool CanReorderItems { get; init; }
    internal Action<WinUI.TreeView>[] Setters { get; init; } = [];
}

public record FlipViewElement(
    Element[] Items
) : Element
{
    public int SelectedIndex { get; init; } = 0;
    public Action<int>? OnSelectionChanged { get; init; }
    internal Action<WinUI.FlipView>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  Dialog / Overlay elements
// ════════════════════════════════════════════════════════════════════════

/// <summary>
/// Declarative content dialog. Set IsOpen to true to show.
/// OnClosed fires with the result when the user dismisses the dialog.
/// </summary>
public record ContentDialogElement(
    string Title,
    Element Content,
    string PrimaryButtonText = "OK"
) : Element
{
    public bool IsOpen { get; init; }
    public string? SecondaryButtonText { get; init; }
    public string? CloseButtonText { get; init; }
    public ContentDialogButton DefaultButton { get; init; } = ContentDialogButton.Primary;
    public Action<ContentDialogResult>? OnClosed { get; init; }
    internal Action<WinUI.ContentDialog>[] Setters { get; init; } = [];
}

/// <summary>
/// A flyout attached to another element. Wrap the target element.
/// </summary>
public record FlyoutElement(
    Element Target,
    Element FlyoutContent
) : Element
{
    public bool IsOpen { get; init; }
    public FlyoutPlacementMode Placement { get; init; } = FlyoutPlacementMode.Auto;
    public Action? OnOpened { get; init; }
    public Action? OnClosed { get; init; }
    internal Action<WinUI.Flyout>[] Setters { get; init; } = [];
}

/// <summary>
/// Describes a content flyout (used as a slot value on buttons or as a modifier attachment).
/// NOT independently mountable — the reconciler recognizes it in flyout slots.
/// </summary>
public record ContentFlyoutElement(Element Content) : Element
{
    public FlyoutPlacementMode Placement { get; init; } = FlyoutPlacementMode.Auto;
}

/// <summary>
/// Describes a menu flyout (used as a slot value on buttons or as a modifier attachment).
/// NOT independently mountable — the reconciler recognizes it in flyout slots.
/// </summary>
public record MenuFlyoutContentElement(MenuFlyoutItemBase[] Items) : Element
{
    public FlyoutPlacementMode Placement { get; init; } = FlyoutPlacementMode.Auto;
}

public record TeachingTipElement(
    string Title,
    string? Subtitle = null
) : Element
{
    public bool IsOpen { get; init; }
    public Element? Content { get; init; }
    public string? ActionButtonContent { get; init; }
    public Action? OnActionButtonClick { get; init; }
    public string? CloseButtonContent { get; init; }
    public Action? OnClosed { get; init; }
    internal Action<WinUI.TeachingTip>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  Status / Info elements
// ════════════════════════════════════════════════════════════════════════

public record InfoBarElement(
    string? Title = null,
    string? Message = null
) : Element
{
    public InfoBarSeverity Severity { get; init; } = InfoBarSeverity.Informational;
    public bool IsOpen { get; init; } = true;
    public bool IsClosable { get; init; } = true;
    public string? ActionButtonContent { get; init; }
    public Action? OnActionButtonClick { get; init; }
    public Action? OnClosed { get; init; }
    internal Action<WinUI.InfoBar>[] Setters { get; init; } = [];
}

public record InfoBadgeElement() : Element
{
    public int? Value { get; init; }
    public string? Icon { get; init; }
    internal Action<WinUI.InfoBadge>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  Menu elements
// ════════════════════════════════════════════════════════════════════════

public record MenuBarElement(MenuBarItemData[] Items) : Element
{
    internal Action<WinUI.MenuBar>[] Setters { get; init; } = [];
}

public record CommandBarElement(
    AppBarItemBase[]? PrimaryCommands = null,
    AppBarItemBase[]? SecondaryCommands = null
) : Element
{
    public CommandBarDefaultLabelPosition DefaultLabelPosition { get; init; } = CommandBarDefaultLabelPosition.Bottom;
    public bool IsOpen { get; init; }
    public Element? Content { get; init; }
    internal Action<WinUI.CommandBar>[] Setters { get; init; } = [];
}

public record MenuFlyoutElement(
    Element Target,
    MenuFlyoutItemBase[] Items
) : Element
{
    internal Action<WinUI.MenuFlyout>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  Templated collection elements (data-driven ListView/GridView/FlipView)
// ════════════════════════════════════════════════════════════════════════

/// <summary>
/// Which WinUI control type a templated list element targets.
/// </summary>
public enum TemplatedControlKind { ListView, GridView, FlipView }

/// <summary>
/// Abstract base for data-driven items controls. Non-generic so the reconciler
/// can match on a single type in its switch expression (same pattern as LazyStackElementBase).
/// </summary>
public abstract record TemplatedListElementBase : Element
{
    public abstract TemplatedControlKind ControlKind { get; }
    public abstract int ItemCount { get; }
    public abstract int GetSelectedIndex();
    public abstract ListViewSelectionMode GetSelectionMode();
    public abstract string? GetHeader();
    public abstract bool GetIsItemClickEnabled();
    public abstract Element BuildItemView(int index);
    public abstract bool SameItemsAs(TemplatedListElementBase other);
    public abstract void InvokeSelectionChanged(int index);
    public abstract void InvokeItemClick(int index);
    public abstract void ApplyControlSetters(object control);
}

public record TemplatedListViewElement<T>(
    IReadOnlyList<T> Items,
    Func<T, string> KeySelector,
    Func<T, int, Element> ViewBuilder
) : TemplatedListElementBase
{
    public int SelectedIndex { get; init; } = -1;
    public Action<int>? OnSelectionChanged { get; init; }
    public Action<T>? OnItemClick { get; init; }
    public ListViewSelectionMode SelectionMode { get; init; } = ListViewSelectionMode.Single;
    public string? Header { get; init; }
    internal Action<WinUI.ListView>[] Setters { get; init; } = [];

    public override TemplatedControlKind ControlKind => TemplatedControlKind.ListView;
    public override int ItemCount => Items.Count;
    public override int GetSelectedIndex() => SelectedIndex;
    public override ListViewSelectionMode GetSelectionMode() => SelectionMode;
    public override string? GetHeader() => Header;
    public override bool GetIsItemClickEnabled() => OnItemClick is not null;
    public override Element BuildItemView(int index) => ViewBuilder(Items[index], index);
    public override bool SameItemsAs(TemplatedListElementBase o) =>
        o is TemplatedListViewElement<T> x && ReferenceEquals(Items, x.Items);
    public override void InvokeSelectionChanged(int index) => OnSelectionChanged?.Invoke(index);
    public override void InvokeItemClick(int index) =>
        OnItemClick?.Invoke(index >= 0 && index < Items.Count ? Items[index] : default!);
    public override void ApplyControlSetters(object control) =>
        Reconciler.ApplySetters(Setters, (WinUI.ListView)control);
}

public record TemplatedGridViewElement<T>(
    IReadOnlyList<T> Items,
    Func<T, string> KeySelector,
    Func<T, int, Element> ViewBuilder
) : TemplatedListElementBase
{
    public int SelectedIndex { get; init; } = -1;
    public Action<int>? OnSelectionChanged { get; init; }
    public Action<T>? OnItemClick { get; init; }
    public ListViewSelectionMode SelectionMode { get; init; } = ListViewSelectionMode.Single;
    public string? Header { get; init; }
    internal Action<WinUI.GridView>[] Setters { get; init; } = [];

    public override TemplatedControlKind ControlKind => TemplatedControlKind.GridView;
    public override int ItemCount => Items.Count;
    public override int GetSelectedIndex() => SelectedIndex;
    public override ListViewSelectionMode GetSelectionMode() => SelectionMode;
    public override string? GetHeader() => Header;
    public override bool GetIsItemClickEnabled() => OnItemClick is not null;
    public override Element BuildItemView(int index) => ViewBuilder(Items[index], index);
    public override bool SameItemsAs(TemplatedListElementBase o) =>
        o is TemplatedGridViewElement<T> x && ReferenceEquals(Items, x.Items);
    public override void InvokeSelectionChanged(int index) => OnSelectionChanged?.Invoke(index);
    public override void InvokeItemClick(int index) =>
        OnItemClick?.Invoke(index >= 0 && index < Items.Count ? Items[index] : default!);
    public override void ApplyControlSetters(object control) =>
        Reconciler.ApplySetters(Setters, (WinUI.GridView)control);
}

public record TemplatedFlipViewElement<T>(
    IReadOnlyList<T> Items,
    Func<T, string> KeySelector,
    Func<T, int, Element> ViewBuilder
) : TemplatedListElementBase
{
    public int SelectedIndex { get; init; } = 0;
    public Action<int>? OnSelectionChanged { get; init; }
    internal Action<WinUI.FlipView>[] Setters { get; init; } = [];

    public override TemplatedControlKind ControlKind => TemplatedControlKind.FlipView;
    public override int ItemCount => Items.Count;
    public override int GetSelectedIndex() => SelectedIndex;
    public override ListViewSelectionMode GetSelectionMode() => ListViewSelectionMode.Single;
    public override string? GetHeader() => null;
    public override bool GetIsItemClickEnabled() => false;
    public override Element BuildItemView(int index) => ViewBuilder(Items[index], index);
    public override bool SameItemsAs(TemplatedListElementBase o) =>
        o is TemplatedFlipViewElement<T> x && ReferenceEquals(Items, x.Items);
    public override void InvokeSelectionChanged(int index) => OnSelectionChanged?.Invoke(index);
    public override void InvokeItemClick(int index) { }
    public override void ApplyControlSetters(object control) =>
        Reconciler.ApplySetters(Setters, (WinUI.FlipView)control);
}

// ════════════════════════════════════════════════════════════════════════
//  Virtualized collection elements (backed by ItemsRepeater)
// ════════════════════════════════════════════════════════════════════════

/// <summary>
/// Abstract base for virtualized lazy stacks. Non-generic so the reconciler
/// can match on a single type in its switch expression.
/// </summary>
public abstract record LazyStackElementBase : Element
{
    public abstract Orientation Orientation { get; }
    public abstract double Spacing { get; init; }
    public abstract double EstimatedItemSize { get; init; }
    public abstract object GetItemsSource();
    public abstract IElementFactory CreateFactory(Reconciler reconciler, Action requestRerender, ElementPool? pool);
    /// <summary>
    /// Update an existing factory's items and viewBuilder in place, avoiding
    /// ItemsRepeater re-realization. Returns true if the factory was updated.
    /// </summary>
    public abstract bool TryUpdateFactory(IElementFactory existingFactory);
    /// <summary>
    /// After updating the factory in place, reconcile all realized items
    /// with the new viewBuilder output (property diffs only, no collection changes).
    /// </summary>
    public abstract void RefreshRealizedItems(IElementFactory factory, WinUI.ItemsRepeater repeater);
    internal Action<WinUI.ScrollViewer>[] ScrollViewerSetters { get; init; } = [];
    internal Action<WinUI.ItemsRepeater>[] RepeaterSetters { get; init; } = [];
}

public record LazyVStackElement<T>(
    IReadOnlyList<T> Items,
    Func<T, string> KeySelector,
    Func<T, int, Element> ViewBuilder
) : LazyStackElementBase
{
    public override Orientation Orientation => Orientation.Vertical;
    public override double Spacing { get; init; } = 8;
    public override double EstimatedItemSize { get; init; } = 40;

    public override object GetItemsSource() =>
        Enumerable.Range(0, Items.Count).ToList();

    public override IElementFactory CreateFactory(Reconciler reconciler, Action requestRerender, ElementPool? pool) =>
        new ElementFactory<T>(Items, ViewBuilder, reconciler, requestRerender, pool);

    public override bool TryUpdateFactory(IElementFactory existingFactory)
    {
        if (existingFactory is ElementFactory<T> f) { f.UpdateInPlace(Items, ViewBuilder); return true; }
        return false;
    }

    public override void RefreshRealizedItems(IElementFactory factory, WinUI.ItemsRepeater repeater)
    {
        if (factory is ElementFactory<T> f) f.RefreshRealizedItems(repeater);
    }
}

public record LazyHStackElement<T>(
    IReadOnlyList<T> Items,
    Func<T, string> KeySelector,
    Func<T, int, Element> ViewBuilder
) : LazyStackElementBase
{
    public override Orientation Orientation => Orientation.Horizontal;
    public override double Spacing { get; init; } = 8;
    public override double EstimatedItemSize { get; init; } = 100;

    public override object GetItemsSource() =>
        Enumerable.Range(0, Items.Count).ToList();

    public override IElementFactory CreateFactory(Reconciler reconciler, Action requestRerender, ElementPool? pool) =>
        new ElementFactory<T>(Items, ViewBuilder, reconciler, requestRerender, pool);

    public override bool TryUpdateFactory(IElementFactory existingFactory)
    {
        if (existingFactory is ElementFactory<T> f) { f.UpdateInPlace(Items, ViewBuilder); return true; }
        return false;
    }

    public override void RefreshRealizedItems(IElementFactory factory, WinUI.ItemsRepeater repeater)
    {
        if (factory is ElementFactory<T> f) f.RefreshRealizedItems(repeater);
    }
}

// ════════════════════════════════════════════════════════════════════════
//  Shape elements
// ════════════════════════════════════════════════════════════════════════

public record RectangleElement() : Element
{
    public Brush? Fill { get; init; }
    public Brush? Stroke { get; init; }
    public double StrokeThickness { get; init; }
    public double RadiusX { get; init; }
    public double RadiusY { get; init; }
    internal Action<WinShapes.Rectangle>[] Setters { get; init; } = [];
}

public record EllipseElement() : Element
{
    public Brush? Fill { get; init; }
    public Brush? Stroke { get; init; }
    public double StrokeThickness { get; init; }
    internal Action<WinShapes.Ellipse>[] Setters { get; init; } = [];
}

public record LineElement() : Element
{
    public double X1 { get; init; }
    public double Y1 { get; init; }
    public double X2 { get; init; }
    public double Y2 { get; init; }
    public Brush? Stroke { get; init; }
    public double StrokeThickness { get; init; } = 1;
    internal Action<WinShapes.Line>[] Setters { get; init; } = [];
}

public record PathElement() : Element
{
    /// <summary>
    /// Pre-parsed WinUI Geometry. When null, the reconciler resolves from <see cref="PathDataString"/>.
    /// Callers that construct PathElement directly (not via D3Path) can set this for non-SVG geometries.
    /// </summary>
    public Geometry? Data { get; init; }
    /// <summary>
    /// The original SVG path data string. When set, geometry is parsed lazily by the reconciler —
    /// only when mounting or when the string changes between renders. This avoids expensive
    /// PathDataParser.Parse + COM Geometry creation on every tree build.
    /// </summary>
    public string? PathDataString { get; init; }
    public Brush? Fill { get; init; }
    public Brush? Stroke { get; init; }
    public double StrokeThickness { get; init; } = 1;
    public Microsoft.UI.Xaml.Media.DoubleCollection? StrokeDashArray { get; init; }
    public Transform? RenderTransform { get; init; }
    internal Action<WinShapes.Path>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  Additional layout elements
// ════════════════════════════════════════════════════════════════════════

public record RelativePanelElement(Element[] Children) : Element
{
    internal Action<WinUI.RelativePanel>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  Additional media elements
// ════════════════════════════════════════════════════════════════════════

public record MediaPlayerElementElement(string? Source = null) : Element
{
    public bool AreTransportControlsEnabled { get; init; } = true;
    public bool AutoPlay { get; init; }
    internal Action<WinUI.MediaPlayerElement>[] Setters { get; init; } = [];
}

public record AnimatedVisualPlayerElement() : Element
{
    public bool AutoPlay { get; init; }
    internal Action<WinUI.AnimatedVisualPlayer>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  Additional collection elements
// ════════════════════════════════════════════════════════════════════════

public record SemanticZoomElement(Element ZoomedInView, Element ZoomedOutView) : Element
{
    internal Action<WinUI.SemanticZoom>[] Setters { get; init; } = [];
}

public record ListBoxElement(string[] Items) : Element
{
    public int SelectedIndex { get; init; } = -1;
    public Action<int>? OnSelectionChanged { get; init; }
    internal Action<WinUI.ListBox>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  Additional navigation elements
// ════════════════════════════════════════════════════════════════════════

public record SelectorBarElement(SelectorBarItemData[] Items) : Element
{
    public int SelectedIndex { get; init; } = 0;
    public Action<int>? OnSelectionChanged { get; init; }
    internal Action<WinUI.SelectorBar>[] Setters { get; init; } = [];
}

public record SelectorBarItemData(string Text, string? Icon = null);

public record PipsPagerElement(int NumberOfPages) : Element
{
    public int SelectedPageIndex { get; init; }
    public Action<int>? OnSelectedIndexChanged { get; init; }
    internal Action<WinUI.PipsPager>[] Setters { get; init; } = [];
}

public record AnnotatedScrollBarElement() : Element
{
    internal Action<WinUI.AnnotatedScrollBar>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  Additional overlay / container elements
// ════════════════════════════════════════════════════════════════════════

public record PopupElement(Element Child) : Element
{
    public bool IsOpen { get; init; }
    public bool IsLightDismissEnabled { get; init; } = true;
    public double HorizontalOffset { get; init; }
    public double VerticalOffset { get; init; }
    public Action? OnClosed { get; init; }
    internal Action<WinPrim.Popup>[] Setters { get; init; } = [];
}

public record RefreshContainerElement(Element Content) : Element
{
    public Action? OnRefreshRequested { get; init; }
    internal Action<WinUI.RefreshContainer>[] Setters { get; init; } = [];
}

public record CommandBarFlyoutElement(
    Element Target,
    AppBarItemBase[]? PrimaryCommands = null,
    AppBarItemBase[]? SecondaryCommands = null
) : Element
{
    public FlyoutPlacementMode Placement { get; init; } = FlyoutPlacementMode.Auto;
    internal Action<WinUI.CommandBarFlyout>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  Additional date/time elements
// ════════════════════════════════════════════════════════════════════════

public record CalendarViewElement() : Element
{
    public CalendarViewSelectionMode SelectionMode { get; init; } = CalendarViewSelectionMode.Single;
    public bool IsGroupLabelVisible { get; init; } = true;
    public bool IsOutOfScopeEnabled { get; init; } = true;
    public string? CalendarIdentifier { get; init; }
    public string? Language { get; init; }
    internal Action<WinUI.CalendarView>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  SwipeControl
// ════════════════════════════════════════════════════════════════════════

public record SwipeItemData(
    string Text,
    Action? OnInvoked = null,
    Microsoft.UI.Xaml.Controls.IconSource? IconSource = null,
    Microsoft.UI.Xaml.Media.Brush? Background = null,
    Microsoft.UI.Xaml.Media.Brush? Foreground = null,
    Microsoft.UI.Xaml.Controls.SwipeBehaviorOnInvoked BehaviorOnInvoked = Microsoft.UI.Xaml.Controls.SwipeBehaviorOnInvoked.Auto);

public record SwipeControlElement(Element Content) : Element
{
    public SwipeItemData[]? LeftItems { get; init; }
    public SwipeItemData[]? RightItems { get; init; }
    public Microsoft.UI.Xaml.Controls.SwipeMode LeftItemsMode { get; init; } = Microsoft.UI.Xaml.Controls.SwipeMode.Reveal;
    public Microsoft.UI.Xaml.Controls.SwipeMode RightItemsMode { get; init; } = Microsoft.UI.Xaml.Controls.SwipeMode.Reveal;
    internal Action<WinUI.SwipeControl>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  AnimatedIcon
// ════════════════════════════════════════════════════════════════════════

public record AnimatedIconElement() : Element
{
    public object? Source { get; init; }
    public IconSource? FallbackIconSource { get; init; }
    internal Action<WinUI.AnimatedIcon>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  ParallaxView
// ════════════════════════════════════════════════════════════════════════

public record ParallaxViewElement(Element Child) : Element
{
    public double VerticalShift { get; init; }
    public double HorizontalShift { get; init; }
    internal Action<WinUI.ParallaxView>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  MapControl
// ════════════════════════════════════════════════════════════════════════

public record MapControlElement() : Element
{
    public string? MapServiceToken { get; init; }
    public double ZoomLevel { get; init; } = 1;
    internal Action<WinUI.MapControl>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  Frame
// ════════════════════════════════════════════════════════════════════════

public record FrameElement() : Element
{
    public Type? SourcePageType { get; init; }
    public object? NavigationParameter { get; init; }
    internal Action<WinUI.Frame>[] Setters { get; init; } = [];
}

// ════════════════════════════════════════════════════════════════════════
//  ItemsView
// ════════════════════════════════════════════════════════════════════════

public enum ItemsViewLayoutKind
{
    StackLayout,
    LinedFlowLayout,
    UniformGridLayout,
}

public record ItemsViewElement<T>(
    IReadOnlyList<T> Items,
    Func<T, string> KeySelector,
    Func<T, int, Element> ViewBuilder
) : Element
{
    public ItemsViewLayoutKind LayoutKind { get; init; } = ItemsViewLayoutKind.StackLayout;
    public ItemsViewSelectionMode SelectionMode { get; init; } = ItemsViewSelectionMode.Single;
    public bool IsItemInvokedEnabled { get; init; }
    public Action<T>? OnItemInvoked { get; init; }
    internal Action<WinUI.ItemsView>[] Setters { get; init; } = [];
}
