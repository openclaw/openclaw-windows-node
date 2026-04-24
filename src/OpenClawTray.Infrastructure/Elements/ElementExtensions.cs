using System;
using OpenClawTray.Infrastructure.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Text;
using Windows.UI.Text;
using WinUI = Microsoft.UI.Xaml.Controls;
using WinPrim = Microsoft.UI.Xaml.Controls.Primitives;
using WinShapes = Microsoft.UI.Xaml.Shapes;

namespace OpenClawTray.Infrastructure;

/// <summary>
/// Fluent modifier extension methods for elements.
/// Modifiers are stored inline on Element.Modifiers, preserving the concrete type
/// through the entire fluent chain. This means .Set() works after any modifier:
///
///   Text("Hello")
///       .Bold()
///       .Margin(16)
///       .HAlign(HorizontalAlignment.Center)
///       .Set(tb => tb.TextWrapping = TextWrapping.Wrap)  // still TextBlockElement!
///
/// The Set() extension gives strongly-typed native property access:
///   Button("Click", onClick)
///       .Set(b => b.FlowDirection = FlowDirection.RightToLeft)
/// </summary>
public static class ElementExtensions
{
    // ════════════════════════════════════════════════════════════════
    //  Layout modifiers (stored inline on Element.Modifiers)
    // ════════════════════════════════════════════════════════════════

    public static T Margin<T>(this T el, double uniform) where T : Element =>
        Modify(el, new ElementModifiers { Margin = new Thickness(uniform) });

    public static T Margin<T>(this T el, double horizontal, double vertical) where T : Element =>
        Modify(el, new ElementModifiers { Margin = new Thickness(horizontal, vertical, horizontal, vertical) });

    public static T Margin<T>(this T el, double left, double top, double right, double bottom) where T : Element =>
        Modify(el, new ElementModifiers { Margin = new Thickness(left, top, right, bottom) });

    public static T Padding<T>(this T el, double uniform) where T : Element =>
        Modify(el, new ElementModifiers { Padding = new Thickness(uniform) });

    public static T Padding<T>(this T el, double horizontal, double vertical) where T : Element =>
        Modify(el, new ElementModifiers { Padding = new Thickness(horizontal, vertical, horizontal, vertical) });

    public static T Padding<T>(this T el, double left, double top, double right, double bottom) where T : Element =>
        Modify(el, new ElementModifiers { Padding = new Thickness(left, top, right, bottom) });

    // ── Logical (BiDi-aware) layout modifiers ───────────────────────
    // InlineStart = left in LTR, right in RTL. InlineEnd = right in LTR, left in RTL.
    // Resolved at mount/update time based on FlowDirection.

    public static T MarginInlineStart<T>(this T el, double value) where T : Element =>
        Modify(el, new ElementModifiers { MarginInlineStart = value });

    public static T MarginInlineEnd<T>(this T el, double value) where T : Element =>
        Modify(el, new ElementModifiers { MarginInlineEnd = value });

    public static T PaddingInlineStart<T>(this T el, double value) where T : Element =>
        Modify(el, new ElementModifiers { PaddingInlineStart = value });

    public static T PaddingInlineEnd<T>(this T el, double value) where T : Element =>
        Modify(el, new ElementModifiers { PaddingInlineEnd = value });

    public static T BorderInlineStart<T>(this T el, Thickness thickness) where T : Element =>
        Modify(el, new ElementModifiers { BorderInlineStart = thickness });

    public static T Width<T>(this T el, double width) where T : Element =>
        Modify(el, new ElementModifiers { Width = width });

    public static T Height<T>(this T el, double height) where T : Element =>
        Modify(el, new ElementModifiers { Height = height });

    public static T Size<T>(this T el, double width, double height) where T : Element =>
        Modify(el, new ElementModifiers { Width = width, Height = height });

    public static T MinWidth<T>(this T el, double w) where T : Element =>
        Modify(el, new ElementModifiers { MinWidth = w });

    public static T MinHeight<T>(this T el, double h) where T : Element =>
        Modify(el, new ElementModifiers { MinHeight = h });

    public static T MaxWidth<T>(this T el, double w) where T : Element =>
        Modify(el, new ElementModifiers { MaxWidth = w });

    public static T MaxHeight<T>(this T el, double h) where T : Element =>
        Modify(el, new ElementModifiers { MaxHeight = h });

    // ── Alignment ───────────────────────────────────────────────────

    public static T HAlign<T>(this T el, HorizontalAlignment alignment) where T : Element =>
        Modify(el, new ElementModifiers { HorizontalAlignment = alignment });

    public static T VAlign<T>(this T el, VerticalAlignment alignment) where T : Element =>
        Modify(el, new ElementModifiers { VerticalAlignment = alignment });

    public static T Center<T>(this T el) where T : Element =>
        Modify(el, new ElementModifiers
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

    // ── Theme override ───────────────────────────────────────────────

    /// <summary>
    /// Sets <see cref="FrameworkElement.RequestedTheme"/> on this element's
    /// control, forcing the subtree to render in a specific theme variant.
    /// <para>
    /// <b>Dark sidebar:</b> <c>VStack(children).RequestedTheme(ElementTheme.Dark)</c>
    /// </para>
    /// <para>
    /// <b>Restore system default:</b> <c>panel.RequestedTheme(ElementTheme.Default)</c>
    /// </para>
    /// </summary>
    public static T RequestedTheme<T>(this T el, ElementTheme theme) where T : Element =>
        Modify(el, new ElementModifiers { RequestedTheme = theme });

    // ── Visibility ──────────────────────────────────────────────────

    public static T Visible<T>(this T el, bool isVisible) where T : Element =>
        Modify(el, new ElementModifiers { IsVisible = isVisible });

    public static T Opacity<T>(this T el, double opacity) where T : Element =>
        Modify(el, new ElementModifiers { Opacity = opacity });

    public static T Scale<T>(this T el, global::System.Numerics.Vector3 scale) where T : Element =>
        Modify(el, new ElementModifiers { Scale = scale });

    public static T Scale<T>(this T el, float uniform) where T : Element =>
        Modify(el, new ElementModifiers { Scale = new global::System.Numerics.Vector3(uniform, uniform, 1f) });

    public static T Rotation<T>(this T el, float degrees) where T : Element =>
        Modify(el, new ElementModifiers { Rotation = degrees });

    public static T CenterPoint<T>(this T el, global::System.Numerics.Vector3 center) where T : Element =>
        Modify(el, new ElementModifiers { CenterPoint = center });

    // ── Typography (any Control or TextBlock) ─────────────────────
    // These set font properties via ElementModifiers, so they work on ANY element
    // (buttons, borders wrapping text, etc.) — not just TextBlockElement.

    /// <summary>
    /// Sets the font family on any FrameworkElement that supports it (Control, TextBlock).
    /// For TextBlockElement-specific chaining that preserves the TextBlockElement return type,
    /// use the TextBlockElement.FontFamily() overload instead.
    /// </summary>
    public static T FontFamily<T>(this T el, string family) where T : Element =>
        Modify(el, new ElementModifiers { FontFamily = WinRTCache.GetFontFamily(family) });

    public static T FontFamily<T>(this T el, Microsoft.UI.Xaml.Media.FontFamily family) where T : Element =>
        Modify(el, new ElementModifiers { FontFamily = family });

    /// <summary>
    /// Sets the font size on any FrameworkElement that supports it (Control, TextBlock).
    /// For TextBlockElement-specific chaining, use the TextBlockElement.FontSize() overload.
    /// </summary>
    public static T FontSize<T>(this T el, double size) where T : Element =>
        Modify(el, new ElementModifiers { FontSize = size });

    /// <summary>
    /// Sets the font weight on any FrameworkElement that supports it (Control, TextBlock).
    /// </summary>
    public static T FontWeight<T>(this T el, global::Windows.UI.Text.FontWeight weight) where T : Element =>
        Modify(el, new ElementModifiers { FontWeight = weight });

    // ── Declarative event handlers ──────────────────────────────────
    // Unlike OnMount(), these re-attach on every update, so closures always
    // capture fresh state. The reconciler detaches the previous handler before
    // attaching the new one.

    public static T OnSizeChanged<T>(this T el, Action<object, SizeChangedEventArgs> handler) where T : Element =>
        Modify(el, new ElementModifiers { OnSizeChanged = handler });

    public static T OnPointerPressed<T>(this T el, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs> handler) where T : Element =>
        Modify(el, new ElementModifiers { OnPointerPressed = handler });

    public static T OnPointerMoved<T>(this T el, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs> handler) where T : Element =>
        Modify(el, new ElementModifiers { OnPointerMoved = handler });

    public static T OnPointerReleased<T>(this T el, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs> handler) where T : Element =>
        Modify(el, new ElementModifiers { OnPointerReleased = handler });

    public static T OnTapped<T>(this T el, Action<object, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs> handler) where T : Element =>
        Modify(el, new ElementModifiers { OnTapped = handler });

    public static T OnKeyDown<T>(this T el, Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs> handler) where T : Element =>
        Modify(el, new ElementModifiers { OnKeyDown = handler });

    public static T OnPointerEntered<T>(this T el, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs> handler) where T : Element =>
        Modify(el, new ElementModifiers { OnPointerEntered = handler });

    public static T OnPointerExited<T>(this T el, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs> handler) where T : Element =>
        Modify(el, new ElementModifiers { OnPointerExited = handler });

    public static T OnPointerCanceled<T>(this T el, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs> handler) where T : Element =>
        Modify(el, new ElementModifiers { OnPointerCanceled = handler });

    public static T OnPointerCaptureLost<T>(this T el, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs> handler) where T : Element =>
        Modify(el, new ElementModifiers { OnPointerCaptureLost = handler });

    public static T OnPointerWheelChanged<T>(this T el, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs> handler) where T : Element =>
        Modify(el, new ElementModifiers { OnPointerWheelChanged = handler });

    public static T OnDoubleTapped<T>(this T el, Action<object, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs> handler) where T : Element =>
        Modify(el, new ElementModifiers { OnDoubleTapped = handler });

    public static T OnRightTapped<T>(this T el, Action<object, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs> handler) where T : Element =>
        Modify(el, new ElementModifiers { OnRightTapped = handler });

    public static T OnHolding<T>(this T el, Action<object, Microsoft.UI.Xaml.Input.HoldingRoutedEventArgs> handler) where T : Element =>
        Modify(el, new ElementModifiers { OnHolding = handler });

    public static T OnKeyUp<T>(this T el, Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs> handler) where T : Element =>
        Modify(el, new ElementModifiers { OnKeyUp = handler });

    public static T OnPreviewKeyDown<T>(this T el, Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs> handler) where T : Element =>
        Modify(el, new ElementModifiers { OnPreviewKeyDown = handler });

    public static T OnPreviewKeyUp<T>(this T el, Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs> handler) where T : Element =>
        Modify(el, new ElementModifiers { OnPreviewKeyUp = handler });

    public static T OnCharacterReceived<T>(this T el, Action<UIElement, Microsoft.UI.Xaml.Input.CharacterReceivedRoutedEventArgs> handler) where T : Element =>
        Modify(el, new ElementModifiers { OnCharacterReceived = handler });

    public static T OnGotFocus<T>(this T el, Action<object, RoutedEventArgs> handler) where T : Element =>
        Modify(el, new ElementModifiers { OnGotFocus = handler });

    public static T OnLostFocus<T>(this T el, Action<object, RoutedEventArgs> handler) where T : Element =>
        Modify(el, new ElementModifiers { OnLostFocus = handler });

    // ── Gesture recognizers (spec 027 Tier 3) ───────────────────────

    /// <summary>
    /// Attaches a pan (single-finger translation) gesture recognizer. The reconciler
    /// wires <see cref="FrameworkElement.ManipulationDelta"/> and computes
    /// <see cref="UIElement.ManipulationMode"/> based on the chosen <paramref name="axis"/>
    /// and <paramref name="withInertia"/> flag. <paramref name="minimumDistance"/> gates
    /// callbacks until the cumulative translation exceeds that distance — on first
    /// crossing, <paramref name="onBegan"/> fires once with <see cref="OpenClawTray.Infrastructure.Input.GesturePhase.Began"/>,
    /// then a <see cref="OpenClawTray.Infrastructure.Input.GesturePhase.Changed"/> follows.
    /// </summary>
    public static T OnPan<T>(this T el,
        Action<OpenClawTray.Infrastructure.Input.PanGesture> onChanged,
        Action<OpenClawTray.Infrastructure.Input.PanGesture>? onEnded = null,
        Action<OpenClawTray.Infrastructure.Input.PanGesture>? onBegan = null,
        Action<OpenClawTray.Infrastructure.Input.PanGesture>? onCancelled = null,
        double minimumDistance = 0.0,
        OpenClawTray.Infrastructure.Input.PanAxis axis = OpenClawTray.Infrastructure.Input.PanAxis.Both,
        bool withInertia = false) where T : Element =>
        Modify(el, new ElementModifiers
        {
            Pan = new OpenClawTray.Infrastructure.Input.PanGestureConfig(onChanged)
            {
                OnBegan = onBegan,
                OnEnded = onEnded,
                OnCancelled = onCancelled,
                MinimumDistance = minimumDistance,
                Axis = axis,
                WithInertia = withInertia,
            },
        });

    /// <summary>Attaches a pinch (two-finger scale) gesture recognizer.</summary>
    public static T OnPinch<T>(this T el,
        Action<OpenClawTray.Infrastructure.Input.PinchGesture> onChanged,
        Action<OpenClawTray.Infrastructure.Input.PinchGesture>? onEnded = null,
        Action<OpenClawTray.Infrastructure.Input.PinchGesture>? onBegan = null,
        Action<OpenClawTray.Infrastructure.Input.PinchGesture>? onCancelled = null,
        bool withInertia = false) where T : Element =>
        Modify(el, new ElementModifiers
        {
            Pinch = new OpenClawTray.Infrastructure.Input.PinchGestureConfig(onChanged)
            {
                OnBegan = onBegan,
                OnEnded = onEnded,
                OnCancelled = onCancelled,
                WithInertia = withInertia,
            },
        });

    /// <summary>Attaches a rotate (two-finger twist) gesture recognizer.</summary>
    public static T OnRotate<T>(this T el,
        Action<OpenClawTray.Infrastructure.Input.RotateGesture> onChanged,
        Action<OpenClawTray.Infrastructure.Input.RotateGesture>? onEnded = null,
        Action<OpenClawTray.Infrastructure.Input.RotateGesture>? onBegan = null,
        Action<OpenClawTray.Infrastructure.Input.RotateGesture>? onCancelled = null,
        bool withInertia = false) where T : Element =>
        Modify(el, new ElementModifiers
        {
            Rotate = new OpenClawTray.Infrastructure.Input.RotateGestureConfig(onChanged)
            {
                OnBegan = onBegan,
                OnEnded = onEnded,
                OnCancelled = onCancelled,
                WithInertia = withInertia,
            },
        });

    /// <summary>
    /// Attaches a long-press gesture recognizer (spec 027 Tier 3 Part 2). Touch and pen
    /// route through <see cref="UIElement.Holding"/> (<c>IsHoldingEnabled = true</c> is auto-set).
    /// Mouse input is ignored by default — WinUI's <c>Holding</c> event does not raise for
    /// mouse pointers. Pass <paramref name="enableMouseEmulation"/> <c>true</c> to arm a
    /// dispatcher timer on <see cref="UIElement.PointerPressed"/> that fires after
    /// <paramref name="minimumDuration"/> and cancels on motion &gt; <paramref name="cancelDistance"/>,
    /// pointer release, or capture loss.
    /// </summary>
    /// <example>card.OnLongPress(g => ShowContextMenu(g.Position))</example>
    public static T OnLongPress<T>(this T el,
        Action<OpenClawTray.Infrastructure.Input.LongPressGesture> onTriggered,
        TimeSpan? minimumDuration = null,
        double cancelDistance = 10.0,
        bool enableMouseEmulation = false) where T : Element =>
        Modify(el, new ElementModifiers
        {
            LongPress = new OpenClawTray.Infrastructure.Input.LongPressGestureConfig(onTriggered)
            {
                MinimumDuration = minimumDuration ?? TimeSpan.FromMilliseconds(500),
                CancelDistance = cancelDistance,
                EnableMouseEmulation = enableMouseEmulation,
            },
        });

    /// <summary>
    /// Zero-argument convenience overload for long-press. Use when you don't need the
    /// gesture snapshot (Position, Duration, Phase).
    /// </summary>
    public static T OnLongPress<T>(this T el,
        Action onTriggered,
        TimeSpan? minimumDuration = null,
        double cancelDistance = 10.0,
        bool enableMouseEmulation = false) where T : Element =>
        el.OnLongPress(_ => onTriggered(), minimumDuration, cancelDistance, enableMouseEmulation);

    /// <summary>
    /// Zero-argument convenience overload for double-tap. Equivalent to
    /// <c>.OnDoubleTapped((_, _) =&gt; handler())</c>.
    /// </summary>
    public static T OnDoubleTap<T>(this T el, Action handler) where T : Element =>
        el.OnDoubleTapped((_, _) => handler());

    /// <summary>
    /// Position-aware convenience overload for double-tap. Hands back the tap position
    /// in element-local space.
    /// </summary>
    public static T OnDoubleTap<T>(this T el, Action<global::Windows.Foundation.Point> handler) where T : Element =>
        el.OnDoubleTapped((s, e) => handler(e.GetPosition(s as UIElement)));

    // ── Drag-and-drop (spec 027 Tier 6 / Phase 6a) ──────────────────

    /// <summary>
    /// Typed drag source. Auto-sets <see cref="UIElement.CanDrag"/> so the element reports
    /// as draggable. <paramref name="getPayload"/> is called each time a drag starts;
    /// the returned value is wrapped in a typed-payload <see cref="OpenClawTray.Infrastructure.Input.DragData"/>
    /// keyed by <typeparamref name="TPayload"/>. Use <paramref name="allowedOperations"/> to
    /// declare which final operations (Copy/Move/Link) the source will accept.
    /// <paramref name="onEnd"/> fires after <c>DropCompleted</c> with the final negotiated
    /// operation (or <see cref="OpenClawTray.Infrastructure.Input.DragOperations.None"/> on cancel).
    /// </summary>
    public static T OnDragStart<T, TPayload>(this T el,
        Func<TPayload> getPayload,
        OpenClawTray.Infrastructure.Input.DragOperations? allowedOperations = null,
        Action<OpenClawTray.Infrastructure.Input.DragEndContext>? onEnd = null) where T : Element =>
        Modify(el, new ElementModifiers
        {
            DragSource = new OpenClawTray.Infrastructure.Input.DragSourceConfig(
                () => OpenClawTray.Infrastructure.Input.DragData.Typed(getPayload()))
            {
                AllowedOperations = allowedOperations,
                OnEnd = onEnd,
            },
        });

    /// <summary>
    /// Raw drag source — the caller builds the <see cref="OpenClawTray.Infrastructure.Input.DragData"/>
    /// directly. Useful when advertising multiple formats at once (Phase 6b) or attaching
    /// additional metadata.
    /// </summary>
    public static T OnDragStart<T>(this T el,
        Func<OpenClawTray.Infrastructure.Input.DragData> getData,
        OpenClawTray.Infrastructure.Input.DragOperations? allowedOperations = null,
        Action<OpenClawTray.Infrastructure.Input.DragEndContext>? onEnd = null) where T : Element =>
        Modify(el, new ElementModifiers
        {
            DragSource = new OpenClawTray.Infrastructure.Input.DragSourceConfig(getData)
            {
                AllowedOperations = allowedOperations,
                OnEnd = onEnd,
            },
        });

    /// <summary>
    /// Gates an attached <c>.OnDragStart</c> — when <paramref name="canDrag"/> returns false,
    /// the drag is cancelled in <c>DragStarting</c> before any UI feedback appears. Merge with
    /// an existing <see cref="OpenClawTray.Infrastructure.Input.DragSourceConfig"/> so previously-set
    /// allowed ops / onEnd are preserved.
    /// </summary>
    public static T DraggableWhen<T>(this T el, Func<bool> canDrag) where T : Element
    {
        var existing = el.Modifiers?.DragSource;
        var cfg = existing is not null
            ? existing with { CanDrag = canDrag }
            : new OpenClawTray.Infrastructure.Input.DragSourceConfig(() => new OpenClawTray.Infrastructure.Input.DragData()) { CanDrag = canDrag };
        return Modify(el, new ElementModifiers { DragSource = cfg });
    }

    /// <summary>
    /// Typed drop target. Auto-sets <see cref="UIElement.AllowDrop"/>. The handler is invoked
    /// when a drag with a matching typed payload is dropped on this element; the accepted
    /// operation is set to the intersection of <paramref name="acceptedOps"/> and the source's
    /// allowed operations (preferring Move &gt; Copy &gt; Link).
    /// </summary>
    public static T OnDrop<T, TPayload>(this T el,
        Action<TPayload> onDrop,
        OpenClawTray.Infrastructure.Input.DragOperations acceptedOps = OpenClawTray.Infrastructure.Input.DragOperations.All) where T : Element
    {
        var existing = el.Modifiers?.DropTarget ?? new OpenClawTray.Infrastructure.Input.DropTargetConfig();
        var typedCallback = new Action<OpenClawTray.Infrastructure.Input.DragTargetArgs>(args =>
        {
            if (args.Data.TryGetTypedPayload<TPayload>(out var payload))
            {
                onDrop(payload);
                // Auto-accept if caller didn't already set.
                if (args.AcceptedOperation == OpenClawTray.Infrastructure.Input.DragOperations.None)
                {
                    args.AcceptedOperation = OpenClawTray.Infrastructure.Input.DragOperationNegotiation.Negotiate(
                        args.AllowedOperations, acceptedOps);
                }
            }
        });
        var cfg = existing with
        {
            TypedDrop = typedCallback,
            AcceptedOperations = acceptedOps,
        };
        return Modify(el, new ElementModifiers { DropTarget = cfg });
    }

    /// <summary>Raw drop handler — receives the full <see cref="OpenClawTray.Infrastructure.Input.DragTargetArgs"/>
    /// so multi-format targets can inspect available formats and accept operation manually.</summary>
    public static T OnDrop<T>(this T el,
        Action<OpenClawTray.Infrastructure.Input.DragTargetArgs> onDrop,
        OpenClawTray.Infrastructure.Input.DragOperations acceptedOps = OpenClawTray.Infrastructure.Input.DragOperations.All) where T : Element
    {
        var existing = el.Modifiers?.DropTarget ?? new OpenClawTray.Infrastructure.Input.DropTargetConfig();
        var cfg = existing with
        {
            OnDrop = onDrop,
            AcceptedOperations = acceptedOps,
        };
        return Modify(el, new ElementModifiers { DropTarget = cfg });
    }

    /// <summary>DragEnter callback — caller updates <see cref="OpenClawTray.Infrastructure.Input.DragTargetArgs.UIOverride"/>
    /// to customize the drop indicator, or sets <see cref="OpenClawTray.Infrastructure.Input.DragTargetArgs.AcceptedOperation"/>
    /// to override default negotiation.</summary>
    public static T OnDragEnter<T>(this T el, Action<OpenClawTray.Infrastructure.Input.DragTargetArgs> handler) where T : Element
    {
        var existing = el.Modifiers?.DropTarget ?? new OpenClawTray.Infrastructure.Input.DropTargetConfig();
        return Modify(el, new ElementModifiers { DropTarget = existing with { OnDragEnter = handler } });
    }

    /// <summary>DragOver callback — fires repeatedly as the pointer moves. Use for hover highlighting
    /// that depends on position within the target.</summary>
    public static T OnDragOver<T>(this T el, Action<OpenClawTray.Infrastructure.Input.DragTargetArgs> handler) where T : Element
    {
        var existing = el.Modifiers?.DropTarget ?? new OpenClawTray.Infrastructure.Input.DropTargetConfig();
        return Modify(el, new ElementModifiers { DropTarget = existing with { OnDragOver = handler } });
    }

    /// <summary>DragLeave callback — fires when the drag exits the target without dropping.</summary>
    public static T OnDragLeave<T>(this T el, Action<OpenClawTray.Infrastructure.Input.DragTargetArgs> handler) where T : Element
    {
        var existing = el.Modifiers?.DropTarget ?? new OpenClawTray.Infrastructure.Input.DropTargetConfig();
        return Modify(el, new ElementModifiers { DropTarget = existing with { OnDragLeave = handler } });
    }

    // ── Decoration ──────────────────────────────────────────────────

    public static T ToolTip<T>(this T el, string tip) where T : Element =>
        Modify(el, new ElementModifiers { ToolTip = tip });

    // ── Flyout / Context / Rich ToolTip attachments ─────────────
    public static T WithFlyout<T>(this T el, Element flyout) where T : Element =>
        Modify(el, new ElementModifiers { AttachedFlyout = flyout });

    public static T WithContextFlyout<T>(this T el, Element contextFlyout) where T : Element =>
        Modify(el, new ElementModifiers { ContextFlyout = contextFlyout });

    public static T WithToolTip<T>(this T el, Element tooltip) where T : Element =>
        Modify(el, new ElementModifiers { RichToolTip = tooltip });

    // ── Theme / Style ───────────────────────────────────────────────

    /// <summary>
    /// Apply a named WinUI Style to the element's control at mount/update time.
    /// Style is on FrameworkElement — works on any element.
    /// Usage: Text("Hello").ApplyStyle("BodyTextBlockStyle")
    /// </summary>
    public static T ApplyStyle<T>(this T el, string styleName) where T : Element =>
        el.OnMount(fe => fe.Style = (Style)Application.Current.Resources[styleName]);

    // ════════════════════════════════════════════════════════════════
    //  Sugar extensions (typed, return concrete element type)
    // ════════════════════════════════════════════════════════════════

    // ── Text sugar ──────────────────────────────────────────────────

    public static TextBlockElement Bold(this TextBlockElement el) =>
        el with { Weight = Microsoft.UI.Text.FontWeights.Bold };

    public static TextBlockElement SemiBold(this TextBlockElement el) =>
        el with { Weight = Microsoft.UI.Text.FontWeights.SemiBold };

    public static TextBlockElement FontSize(this TextBlockElement el, double size) =>
        el with { FontSize = size };

    public static TextBlockElement FontStyle(this TextBlockElement el, global::Windows.UI.Text.FontStyle style) =>
        el with { FontStyle = style };

    public static TextBlockElement TextWrapping(this TextBlockElement el, TextWrapping wrapping = Microsoft.UI.Xaml.TextWrapping.Wrap) =>
        el with { TextWrapping = wrapping };

    public static TextBlockElement TextAlignment(this TextBlockElement el, TextAlignment alignment) =>
        el with { TextAlignment = alignment };

    public static TextBlockElement TextTrimming(this TextBlockElement el, TextTrimming trimming) =>
        el with { TextTrimming = trimming };

    public static TextBlockElement Selectable(this TextBlockElement el, bool selectable = true) =>
        el with { IsTextSelectionEnabled = selectable };

    public static TextBlockElement FontFamily(this TextBlockElement el, string family) =>
        el with { FontFamily = WinRTCache.GetFontFamily(family) };

    public static TextBlockElement FontFamily(this TextBlockElement el, Microsoft.UI.Xaml.Media.FontFamily family) =>
        el with { FontFamily = family };

    // ── TextField sugar ────────────────────────────────────────────────

    public static TextFieldElement ReadOnly(this TextFieldElement el, bool readOnly = true) =>
        el with { IsReadOnly = readOnly };

    public static TextFieldElement AcceptsReturn(this TextFieldElement el, bool accepts = true) =>
        el with { AcceptsReturn = accepts };

    public static TextFieldElement TextWrapping(this TextFieldElement el, TextWrapping wrapping = Microsoft.UI.Xaml.TextWrapping.Wrap) =>
        el with { TextWrapping = wrapping };

    // ── Path sugar ─────────────────────────────────────────────────────

    public static PathElement StrokeDashArray(this PathElement el, params double[] dashes)
    {
        var dc = new Microsoft.UI.Xaml.Media.DoubleCollection();
        foreach (var d in dashes) dc.Add(d);
        return el with { StrokeDashArray = dc };
    }

    // ── IsEnabled (on Control — works on buttons, inputs, etc.) ────

    public static T Disabled<T>(this T el, bool disabled = true) where T : Element =>
        Modify(el, new ElementModifiers { IsEnabled = !disabled });

    // ── Background (Panel, Control, Border) ────────────────────────

    /// <summary>
    /// Sets the background from a color string. Allocates a new SolidColorBrush per call.
    /// On hot render paths, prefer the <see cref="Background{T}(T, Brush)"/> overload with a cached brush.
    /// </summary>
    public static T Background<T>(this T el, string color) where T : Element =>
        Modify(el, new ElementModifiers { Background = BrushHelper.Parse(color) });

    public static T Background<T>(this T el, Brush brush) where T : Element =>
        Modify(el, new ElementModifiers { Background = brush });

    /// <summary>
    /// Sets the background from a WinUI theme resource. Resolves at render time
    /// and adapts when the theme changes (Light ↔ Dark).
    /// Usage: <c>VStack(children).Background(Theme.CardBackground)</c>
    /// </summary>
    public static T Background<T>(this T el, ThemeRef theme) where T : Element =>
        ModifyTheme(el, "Background", theme);

    // ── Foreground (Control, TextBlock) ──────────────────────────

    /// <summary>
    /// Sets the foreground from a color string. Allocates a new SolidColorBrush per call.
    /// On hot render paths, prefer the <see cref="Foreground{T}(T, Brush)"/> overload with a cached brush.
    /// </summary>
    public static T Foreground<T>(this T el, string color) where T : Element =>
        Modify(el, new ElementModifiers { Foreground = BrushHelper.Parse(color) });

    public static T Foreground<T>(this T el, Brush brush) where T : Element =>
        Modify(el, new ElementModifiers { Foreground = brush });

    /// <summary>
    /// Sets the foreground from a WinUI theme resource. Resolves at render time
    /// and adapts when the theme changes (Light ↔ Dark).
    /// Usage: <c>Text("Hello").Foreground(Theme.PrimaryText)</c>
    /// </summary>
    public static T Foreground<T>(this T el, ThemeRef theme) where T : Element =>
        ModifyTheme(el, "Foreground", theme);

    // ── CornerRadius (on Control and Border) ────────────────────────

    public static T CornerRadius<T>(this T el, double radius) where T : Element =>
        Modify(el, new ElementModifiers { CornerRadius = new Microsoft.UI.Xaml.CornerRadius(radius) });

    public static T CornerRadius<T>(this T el, double topLeft, double topRight, double bottomRight, double bottomLeft) where T : Element =>
        Modify(el, new ElementModifiers { CornerRadius = new Microsoft.UI.Xaml.CornerRadius(topLeft, topRight, bottomRight, bottomLeft) });

    // ── Border brush/thickness (on Control and Border) ─────────────

    /// <summary>
    /// Sets the border from a color string. Allocates a new SolidColorBrush per call.
    /// On hot render paths, prefer the <see cref="WithBorder{T}(T, Brush, double)"/> overload with a cached brush.
    /// </summary>
    public static T WithBorder<T>(this T el, string color, double thickness = 1) where T : Element =>
        Modify(el, new ElementModifiers { BorderBrush = BrushHelper.Parse(color), BorderThickness = new Thickness(thickness) });

    public static T WithBorder<T>(this T el, Brush brush, double thickness = 1) where T : Element =>
        Modify(el, new ElementModifiers { BorderBrush = brush, BorderThickness = new Thickness(thickness) });

    /// <summary>
    /// Sets the border from a WinUI theme resource. Resolves at render time
    /// and adapts when the theme changes (Light ↔ Dark).
    /// Usage: <c>VStack(children).WithBorder(Theme.CardStroke)</c>
    /// </summary>
    public static T WithBorder<T>(this T el, ThemeRef theme, double thickness = 1) where T : Element =>
        ModifyTheme(el with { Modifiers = el.Modifiers is not null
            ? el.Modifiers.Merge(new ElementModifiers { BorderThickness = new Thickness(thickness) })
            : new ElementModifiers { BorderThickness = new Thickness(thickness) } },
            "BorderBrush", theme);

    // ── Lightweight Styling (per-control resource overrides) ────────

    /// <summary>
    /// Configures per-control resource overrides via WinUI's lightweight styling
    /// mechanism. Overrides are injected into <see cref="FrameworkElement.Resources"/>
    /// so the control's <see cref="Microsoft.UI.Xaml.VisualStateManager"/> picks them
    /// up automatically — hover, pressed, and disabled states all respect the overrides
    /// without requiring a custom template.
    /// <para>
    /// <b>Brand-colored button:</b>
    /// <code>
    /// Button("Submit").Resources(r => r
    ///     .Set("ButtonBackground", "#0078D4")
    ///     .Set("ButtonBackgroundPointerOver", "#106EBE")
    ///     .Set("ButtonBackgroundPressed", "#005A9E"))
    /// </code>
    /// </para>
    /// <para>
    /// <b>Scoped cascading:</b> resources set on a parent panel cascade to child
    /// controls, matching WinUI's resource lookup behavior.
    /// </para>
    /// </summary>
    public static T Resources<T>(this T el, Action<OpenClawTray.Infrastructure.Elements.ResourceBuilder> configure) where T : Element
    {
        var builder = new OpenClawTray.Infrastructure.Elements.ResourceBuilder();
        configure(builder);
        return el with { ResourceOverrides = builder.Build() };
    }

    // ── Flex sugar ──────────────────────────────────────────────────

    public static FlexElement FlexPadding(this FlexElement el, double uniform) =>
        el with { FlexPadding = new Thickness(uniform) };

    public static FlexElement FlexPadding(this FlexElement el, double horizontal, double vertical) =>
        el with { FlexPadding = new Thickness(horizontal, vertical, horizontal, vertical) };

    public static FlexElement FlexPadding(this FlexElement el, double left, double top, double right, double bottom) =>
        el with { FlexPadding = new Thickness(left, top, right, bottom) };

    // ── Stack sugar ─────────────────────────────────────────────────

    public static StackElement Spacing(this StackElement el, double spacing) =>
        el with { Spacing = spacing };

    // ── TextField sugar ─────────────────────────────────────────────

    public static TextFieldElement Header(this TextFieldElement el, string header) =>
        el with { Header = header };

    // ── ComboBox sugar ──────────────────────────────────────────────

    public static ComboBoxElement Placeholder(this ComboBoxElement el, string text) =>
        el with { PlaceholderText = text };

    public static ComboBoxElement Editable(this ComboBoxElement el, bool editable = true) =>
        el with { IsEditable = editable };

    public static ComboBoxElement Header(this ComboBoxElement el, string header) =>
        el with { Header = header };

    // ── NumberBox sugar ─────────────────────────────────────────────

    public static NumberBoxElement Range(this NumberBoxElement el, double min, double max) =>
        el with { Minimum = min, Maximum = max };

    public static NumberBoxElement SpinButtons(this NumberBoxElement el, NumberBoxSpinButtonPlacementMode placement = NumberBoxSpinButtonPlacementMode.Inline) =>
        el with { SpinButtonPlacement = placement };

    // ── Slider sugar ────────────────────────────────────────────────

    public static SliderElement StepFrequency(this SliderElement el, double step) =>
        el with { StepFrequency = step };

    public static SliderElement Header(this SliderElement el, string header) =>
        el with { Header = header };

    // ── ToggleSwitch sugar ──────────────────────────────────────────

    public static ToggleSwitchElement Header(this ToggleSwitchElement el, string header) =>
        el with { Header = header };

    // ── RatingControl sugar ─────────────────────────────────────────

    public static RatingControlElement MaxRating(this RatingControlElement el, int max) =>
        el with { MaxRating = max };

    public static RatingControlElement ReadOnly(this RatingControlElement el, bool readOnly = true) =>
        el with { IsReadOnly = readOnly };

    // ── InfoBar sugar ───────────────────────────────────────────────

    public static InfoBarElement Severity(this InfoBarElement el, InfoBarSeverity severity) =>
        el with { Severity = severity };

    public static InfoBarElement Closable(this InfoBarElement el, bool closable = true) =>
        el with { IsClosable = closable };

    // ── NavigationView sugar ────────────────────────────────────────

    public static NavigationViewElement PaneDisplayMode(this NavigationViewElement el, NavigationViewPaneDisplayMode mode) =>
        el with { PaneDisplayMode = mode };

    public static NavigationViewElement PaneTitle(this NavigationViewElement el, string title) =>
        el with { PaneTitle = title };

    /// <summary>
    /// Auto-syncs this NavigationView with a NavigationHandle: sets <c>SelectedTag</c>
    /// from the current route, wires <c>OnSelectionChanged</c> to navigate,
    /// <c>OnBackRequested</c> to <c>GoBack</c>, and <c>IsBackEnabled</c> to <c>CanGoBack</c>.
    /// </summary>
    /// <param name="el">The NavigationView element to configure.</param>
    /// <param name="nav">The navigation handle obtained from <c>UseNavigation</c>.</param>
    /// <param name="routeToTag">Maps a route to its NavigationViewItem tag. Return null for routes without a corresponding menu item.</param>
    /// <param name="tagToRoute">Maps a NavigationViewItem tag back to a route for <c>OnSelectionChanged</c>.</param>
    public static NavigationViewElement WithNavigation<TRoute>(
        this NavigationViewElement el,
        Navigation.NavigationHandle<TRoute> nav,
        Func<TRoute, string?> routeToTag,
        Func<string, TRoute> tagToRoute) where TRoute : notnull
    => el with
    {
        SelectedTag = routeToTag(nav.CurrentRoute),
        IsBackEnabled = nav.CanGoBack,
        OnSelectionChanged = tag =>
        {
            if (tag is not null)
            {
                var route = tagToRoute(tag);
                if (!EqualityComparer<TRoute>.Default.Equals(route, nav.CurrentRoute))
                    nav.Navigate(route);
            }
        },
        OnBackRequested = () => nav.GoBack(),
    };

    // ── TitleBar sugar ──────────────────────────────────────────────

    public static TitleBarElement Subtitle(this TitleBarElement el, string subtitle) =>
        el with { Subtitle = subtitle };

    /// <summary>
    /// Auto-syncs this TitleBar's back button with a NavigationHandle: sets
    /// <c>IsBackButtonVisible</c> and <c>IsBackButtonEnabled</c> from <c>CanGoBack</c>,
    /// and wires <c>OnBackRequested</c> to <c>GoBack</c>.
    /// </summary>
    public static TitleBarElement WithNavigation<TRoute>(
        this TitleBarElement el,
        Navigation.NavigationHandle<TRoute> nav) where TRoute : notnull
    => el with
    {
        IsBackButtonVisible = nav.CanGoBack,
        IsBackButtonEnabled = nav.CanGoBack,
        OnBackRequested = () => nav.GoBack(),
    };

    public static TitleBarElement Set(this TitleBarElement el, Action<WinUI.TitleBar> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // ── ExpanderElement sugar ───────────────────────────────────────

    public static ExpanderElement Direction(this ExpanderElement el, ExpandDirection dir) =>
        el with { ExpandDirection = dir };

    // ── Expander sugar ──────────────────────────────────────────────

    // ── RepeatButton sugar ──────────────────────────────────────────

    public static RepeatButtonElement Delay(this RepeatButtonElement el, int delay) =>
        el with { Delay = delay };

    public static RepeatButtonElement Interval(this RepeatButtonElement el, int interval) =>
        el with { Interval = interval };

    // ── ProgressRing sugar ──────────────────────────────────────────

    public static ProgressRingElement Active(this ProgressRingElement el, bool active = true) =>
        el with { IsActive = active };

    // ── PersonPicture sugar ─────────────────────────────────────────

    public static PersonPictureElement DisplayName(this PersonPictureElement el, string name) =>
        el with { DisplayName = name };

    public static PersonPictureElement Initials(this PersonPictureElement el, string initials) =>
        el with { Initials = initials };

    // ── ListView / GridView sugar ───────────────────────────────────

    public static ListViewElement SelectionMode(this ListViewElement el, ListViewSelectionMode mode) =>
        el with { SelectionMode = mode };

    public static GridViewElement SelectionMode(this GridViewElement el, ListViewSelectionMode mode) =>
        el with { SelectionMode = mode };

    // ── TabView sugar ───────────────────────────────────────────────

    public static TabViewElement ShowAddButton(this TabViewElement el, bool visible = true) =>
        el with { IsAddTabButtonVisible = visible };

    // ── Key ─────────────────────────────────────────────────────────

    public static T WithKey<T>(this T el, string key) where T : Element =>
        el with { Key = key };

    // ════════════════════════════════════════════════════════════════
    //  Set() — strongly-typed native property access per element type
    //
    //  Usage:  Button("Go", onClick).Set(b => b.FlowDirection = FlowDirection.RightToLeft)
    //
    //  The lambda parameter is the actual WinUI control type, giving you
    //  full IntelliSense and compile-time type checking for every property.
    //  Setters are applied at both mount and update (idempotent property sets).
    // ════════════════════════════════════════════════════════════════

    // Text
    public static TextBlockElement Set(this TextBlockElement el, Action<WinUI.TextBlock> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static RichTextBlockElement Set(this RichTextBlockElement el, Action<WinUI.RichTextBlock> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static RichEditBoxElement Set(this RichEditBoxElement el, Action<WinUI.RichEditBox> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // Buttons
    public static ButtonElement Set(this ButtonElement el, Action<WinUI.Button> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static HyperlinkButtonElement Set(this HyperlinkButtonElement el, Action<WinUI.HyperlinkButton> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static RepeatButtonElement Set(this RepeatButtonElement el, Action<WinPrim.RepeatButton> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static ToggleButtonElement Set(this ToggleButtonElement el, Action<WinPrim.ToggleButton> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static DropDownButtonElement Set(this DropDownButtonElement el, Action<WinUI.DropDownButton> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static SplitButtonElement Set(this SplitButtonElement el, Action<WinUI.SplitButton> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static ToggleSplitButtonElement Set(this ToggleSplitButtonElement el, Action<WinUI.ToggleSplitButton> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // Input
    public static TextFieldElement Set(this TextFieldElement el, Action<WinUI.TextBox> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static PasswordBoxElement Set(this PasswordBoxElement el, Action<WinUI.PasswordBox> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static NumberBoxElement Set(this NumberBoxElement el, Action<WinUI.NumberBox> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static AutoSuggestBoxElement Set(this AutoSuggestBoxElement el, Action<WinUI.AutoSuggestBox> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static CheckBoxElement Set(this CheckBoxElement el, Action<WinUI.CheckBox> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static RadioButtonElement Set(this RadioButtonElement el, Action<WinUI.RadioButton> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static RadioButtonsElement Set(this RadioButtonsElement el, Action<WinUI.RadioButtons> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static ComboBoxElement Set(this ComboBoxElement el, Action<WinUI.ComboBox> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static SliderElement Set(this SliderElement el, Action<WinUI.Slider> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static ToggleSwitchElement Set(this ToggleSwitchElement el, Action<WinUI.ToggleSwitch> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static RatingControlElement Set(this RatingControlElement el, Action<WinUI.RatingControl> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static ColorPickerElement Set(this ColorPickerElement el, Action<WinUI.ColorPicker> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // Date/Time
    public static CalendarDatePickerElement Set(this CalendarDatePickerElement el, Action<WinUI.CalendarDatePicker> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static DatePickerElement Set(this DatePickerElement el, Action<WinUI.DatePicker> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static TimePickerElement Set(this TimePickerElement el, Action<WinUI.TimePicker> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // Progress
    public static ProgressElement Set(this ProgressElement el, Action<WinUI.ProgressBar> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static ProgressRingElement Set(this ProgressRingElement el, Action<WinUI.ProgressRing> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // Media
    public static ImageElement Set(this ImageElement el, Action<WinUI.Image> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static PersonPictureElement Set(this PersonPictureElement el, Action<WinUI.PersonPicture> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static WebView2Element Set(this WebView2Element el, Action<WinUI.WebView2> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // Layout / Containers
    public static FlexElement Set(this FlexElement el, Action<Layout.FlexPanel> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static WrapGridElement Set(this WrapGridElement el, Action<WinUI.VariableSizedWrapGrid> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static StackElement Set(this StackElement el, Action<WinUI.StackPanel> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static GridElement Set(this GridElement el, Action<WinUI.Grid> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static ScrollViewElement Set(this ScrollViewElement el, Action<WinUI.ScrollViewer> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static BorderElement Set(this BorderElement el, Action<WinUI.Border> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static ExpanderElement Set(this ExpanderElement el, Action<WinUI.Expander> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static SplitViewElement Set(this SplitViewElement el, Action<WinUI.SplitView> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static ViewboxElement Set(this ViewboxElement el, Action<WinUI.Viewbox> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static CanvasElement Set(this CanvasElement el, Action<WinUI.Canvas> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // Navigation
    public static NavigationViewElement Set(this NavigationViewElement el, Action<WinUI.NavigationView> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static TabViewElement Set(this TabViewElement el, Action<WinUI.TabView> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static BreadcrumbBarElement Set(this BreadcrumbBarElement el, Action<WinUI.BreadcrumbBar> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static PivotElement Set(this PivotElement el, Action<WinUI.Pivot> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // Collections
    public static ListViewElement Set(this ListViewElement el, Action<WinUI.ListView> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static GridViewElement Set(this GridViewElement el, Action<WinUI.GridView> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static TreeViewElement Set(this TreeViewElement el, Action<WinUI.TreeView> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static FlipViewElement Set(this FlipViewElement el, Action<WinUI.FlipView> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // Dialogs / Overlays
    public static ContentDialogElement Set(this ContentDialogElement el, Action<WinUI.ContentDialog> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static FlyoutElement Set(this FlyoutElement el, Action<WinUI.Flyout> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static TeachingTipElement Set(this TeachingTipElement el, Action<WinUI.TeachingTip> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static InfoBarElement Set(this InfoBarElement el, Action<WinUI.InfoBar> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static InfoBadgeElement Set(this InfoBadgeElement el, Action<WinUI.InfoBadge> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // Menus
    public static MenuBarElement Set(this MenuBarElement el, Action<WinUI.MenuBar> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static CommandBarElement Set(this CommandBarElement el, Action<WinUI.CommandBar> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static MenuFlyoutElement Set(this MenuFlyoutElement el, Action<WinUI.MenuFlyout> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // Shapes
    public static RectangleElement Set(this RectangleElement el, Action<WinShapes.Rectangle> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static EllipseElement Set(this EllipseElement el, Action<WinShapes.Ellipse> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static LineElement Set(this LineElement el, Action<WinShapes.Line> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static PathElement Set(this PathElement el, Action<WinShapes.Path> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // Additional layout
    public static RelativePanelElement Set(this RelativePanelElement el, Action<WinUI.RelativePanel> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // Additional media
    public static MediaPlayerElementElement Set(this MediaPlayerElementElement el, Action<WinUI.MediaPlayerElement> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static AnimatedVisualPlayerElement Set(this AnimatedVisualPlayerElement el, Action<WinUI.AnimatedVisualPlayer> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // Additional collections
    public static SemanticZoomElement Set(this SemanticZoomElement el, Action<WinUI.SemanticZoom> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static ListBoxElement Set(this ListBoxElement el, Action<WinUI.ListBox> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // Additional navigation
    public static SelectorBarElement Set(this SelectorBarElement el, Action<WinUI.SelectorBar> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static PipsPagerElement Set(this PipsPagerElement el, Action<WinUI.PipsPager> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static AnnotatedScrollBarElement Set(this AnnotatedScrollBarElement el, Action<WinUI.AnnotatedScrollBar> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // Additional overlays / containers
    public static PopupElement Set(this PopupElement el, Action<WinPrim.Popup> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static RefreshContainerElement Set(this RefreshContainerElement el, Action<WinUI.RefreshContainer> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static CommandBarFlyoutElement Set(this CommandBarFlyoutElement el, Action<WinUI.CommandBarFlyout> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // Additional date/time
    public static CalendarViewElement Set(this CalendarViewElement el, Action<WinUI.CalendarView> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // SwipeControl
    public static SwipeControlElement Set(this SwipeControlElement el, Action<WinUI.SwipeControl> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // AnimatedIcon
    public static AnimatedIconElement Set(this AnimatedIconElement el, Action<WinUI.AnimatedIcon> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // ParallaxView
    public static ParallaxViewElement Set(this ParallaxViewElement el, Action<WinUI.ParallaxView> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // MapControl
    public static MapControlElement Set(this MapControlElement el, Action<WinUI.MapControl> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // Frame
    public static FrameElement Set(this FrameElement el, Action<WinUI.Frame> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // ItemsView
    public static ItemsViewElement<T> Set<T>(this ItemsViewElement<T> el, Action<WinUI.ItemsView> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // Typed templated collections
    public static TemplatedListViewElement<T> Set<T>(this TemplatedListViewElement<T> el, Action<WinUI.ListView> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static TemplatedGridViewElement<T> Set<T>(this TemplatedGridViewElement<T> el, Action<WinUI.GridView> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    public static TemplatedFlipViewElement<T> Set<T>(this TemplatedFlipViewElement<T> el, Action<WinUI.FlipView> configure) =>
        el with { Setters = [.. el.Setters, configure] };

    // ── Shape convenience modifiers ─────────────────────────────────

    public static RectangleElement Fill(this RectangleElement el, Brush brush) =>
        el with { Fill = brush };

    public static EllipseElement Fill(this EllipseElement el, Brush brush) =>
        el with { Fill = brush };

    public static PathElement Fill(this PathElement el, Brush brush) =>
        el with { Fill = brush };

    public static LineElement Stroke(this LineElement el, Brush brush) =>
        el with { Stroke = brush };

    public static LineElement StrokeThickness(this LineElement el, double thickness) =>
        el with { StrokeThickness = thickness };

    public static PathElement Stroke(this PathElement el, Brush brush) =>
        el with { Stroke = brush };

    public static PathElement StrokeThickness(this PathElement el, double thickness) =>
        el with { StrokeThickness = thickness };

    // ── Popup convenience modifiers ─────────────────────────────────

    public static PopupElement LightDismiss(this PopupElement el, bool enabled = true) =>
        el with { IsLightDismissEnabled = enabled };

    public static PopupElement Offset(this PopupElement el, double horizontal, double vertical) =>
        el with { HorizontalOffset = horizontal, VerticalOffset = vertical };

    // Virtualized collections (LazyVStack / LazyHStack)
    // .Set() targets the outer ScrollViewer; .SetRepeater() targets the inner ItemsRepeater
    public static LazyVStackElement<T> Set<T>(this LazyVStackElement<T> el, Action<WinUI.ScrollViewer> configure) =>
        el with { ScrollViewerSetters = [.. el.ScrollViewerSetters, configure] };

    public static LazyVStackElement<T> SetRepeater<T>(this LazyVStackElement<T> el, Action<WinUI.ItemsRepeater> configure) =>
        el with { RepeaterSetters = [.. el.RepeaterSetters, configure] };

    public static LazyHStackElement<T> Set<T>(this LazyHStackElement<T> el, Action<WinUI.ScrollViewer> configure) =>
        el with { ScrollViewerSetters = [.. el.ScrollViewerSetters, configure] };

    public static LazyHStackElement<T> SetRepeater<T>(this LazyHStackElement<T> el, Action<WinUI.ItemsRepeater> configure) =>
        el with { RepeaterSetters = [.. el.RepeaterSetters, configure] };

    // ════════════════════════════════════════════════════════════════
    //  Transitions (first-class, applied by reconciler)
    // ════════════════════════════════════════════════════════════════

    // ── Theme transitions (ChildrenTransitions / ItemContainerTransitions) ──

    /// <summary>
    /// Sets theme transitions declaratively. The reconciler applies ChildrenTransitions
    /// on panels, ChildTransitions on borders, ContentTransitions on content controls.
    /// Works on any element type.
    /// </summary>
    public static T WithTransitions<T>(this T el, params Transition[] transitions) where T : Element =>
        el with { ThemeTransitions = (el.ThemeTransitions ?? new()) with { Children = transitions } };

    /// <summary>
    /// Sets ItemContainerTransitions declaratively on ListView, GridView, etc.
    /// </summary>
    public static T ItemContainerTransitions<T>(this T el, params Transition[] transitions) where T : Element =>
        el with { ThemeTransitions = (el.ThemeTransitions ?? new()) with { ItemContainer = transitions } };

    // ── Implicit transitions (Opacity, Rotation, Scale, Translation, Background) ──

    /// <summary>
    /// Adds an implicit ScalarTransition on Opacity.
    /// Applied by the reconciler after .Set() callbacks — always safe to combine.
    /// </summary>
    public static T OpacityTransition<T>(this T el, TimeSpan? duration = null) where T : Element
    {
        var t = new ScalarTransition();
        if (duration.HasValue) t.Duration = duration.Value;
        return el with { ImplicitTransitions = (el.ImplicitTransitions ?? new()) with { Opacity = t } };
    }

    /// <summary>
    /// Adds an implicit ScalarTransition on Rotation.
    /// </summary>
    public static T RotationTransition<T>(this T el, TimeSpan? duration = null) where T : Element
    {
        var t = new ScalarTransition();
        if (duration.HasValue) t.Duration = duration.Value;
        return el with { ImplicitTransitions = (el.ImplicitTransitions ?? new()) with { Rotation = t } };
    }

    /// <summary>
    /// Adds an implicit Vector3Transition on Scale.
    /// Pass a pre-configured transition to set Components for axis-specific animation.
    /// </summary>
    public static T ScaleTransition<T>(this T el, Vector3Transition? transition = null) where T : Element =>
        el with { ImplicitTransitions = (el.ImplicitTransitions ?? new()) with { Scale = transition ?? new Vector3Transition() } };

    /// <summary>
    /// Adds an implicit Vector3Transition on Translation.
    /// Pass a pre-configured transition to set Components for axis-specific animation.
    /// </summary>
    public static T TranslationTransition<T>(this T el, Vector3Transition? transition = null) where T : Element =>
        el with { ImplicitTransitions = (el.ImplicitTransitions ?? new()) with { Translation = transition ?? new Vector3Transition() } };

    /// <summary>
    /// Adds an implicit BrushTransition on Background.
    /// Only available on Grid and Stack (VStack/HStack) — WinUI only supports
    /// BackgroundTransition on Grid, StackPanel, and ContentPresenter.
    /// </summary>
    public static GridElement BackgroundTransition(this GridElement el, TimeSpan? duration = null)
    {
        var t = new BrushTransition();
        if (duration.HasValue) t.Duration = duration.Value;
        return el with { ImplicitTransitions = (el.ImplicitTransitions ?? new()) with { Background = t } };
    }

    /// <inheritdoc cref="BackgroundTransition(GridElement, TimeSpan?)"/>
    public static StackElement BackgroundTransition(this StackElement el, TimeSpan? duration = null)
    {
        var t = new BrushTransition();
        if (duration.HasValue) t.Duration = duration.Value;
        return el with { ImplicitTransitions = (el.ImplicitTransitions ?? new()) with { Background = t } };
    }

    // ════════════════════════════════════════════════════════════════
    //  Layout animations (Composition-layer implicit animations on Offset/Size)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enables smooth layout animation: when WinUI repositions this element
    /// (e.g., list reorder, grid reflow), it animates from the old position to the
    /// new position via the Composition layer. Default duration: 300ms.
    /// Elements should have stable keys (.WithKey()) for the reconciler to match
    /// them across reorders.
    /// </summary>
    public static T LayoutAnimation<T>(this T el) where T : Element =>
        el with { LayoutAnimation = new LayoutAnimationConfig() };

    /// <summary>
    /// Enables layout animation with a custom duration.
    /// </summary>
    public static T LayoutAnimation<T>(this T el, TimeSpan duration) where T : Element =>
        el with { LayoutAnimation = new LayoutAnimationConfig { Duration = duration } };

    /// <summary>
    /// Enables layout animation with spring physics for a natural, bouncy feel.
    /// </summary>
    public static T SpringLayoutAnimation<T>(this T el,
        float dampingRatio = 0.6f, float period = 0.08f) where T : Element =>
        el with { LayoutAnimation = new LayoutAnimationConfig
        {
            UseSpring = true,
            DampingRatio = dampingRatio,
            Period = period
        } };

    /// <summary>
    /// Enables layout animation with a fully custom configuration.
    /// </summary>
    public static T LayoutAnimation<T>(this T el, LayoutAnimationConfig config) where T : Element =>
        el with { LayoutAnimation = config };

    // ════════════════════════════════════════════════════════════════
    //  Connected animations (cross-container transitions)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Declares this element as a participant in a connected animation.
    /// When the element is unmounted (e.g., parent container changes type),
    /// the reconciler captures its visual snapshot. When a new element with the
    /// same key is mounted, the snapshot animates to the new element's position.
    /// Both source and destination must use the same key string.
    /// </summary>
    public static T ConnectedAnimation<T>(this T el, string key) where T : Element =>
        el with { ConnectedAnimationKey = key };

    // ════════════════════════════════════════════════════════════════
    //  ScrollView zoom/scroll modifiers
    // ════════════════════════════════════════════════════════════════

    public static ScrollViewElement ZoomMode(this ScrollViewElement el, WinUI.ZoomMode mode) =>
        el with { ZoomMode = mode };

    public static ScrollViewElement HorizontalScrollMode(this ScrollViewElement el, WinUI.ScrollMode mode) =>
        el with { HorizontalScrollMode = mode };

    public static ScrollViewElement VerticalScrollMode(this ScrollViewElement el, WinUI.ScrollMode mode) =>
        el with { VerticalScrollMode = mode };

    // ════════════════════════════════════════════════════════════════
    //  AutomationProperties / ElementSoundMode / OnMount
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sets AutomationProperties.Name on the element's control.
    /// Usage: Button("Go", onClick).AutomationName("Navigate forward")
    /// </summary>
    public static T AutomationName<T>(this T el, string name) where T : Element =>
        Modify(el, new ElementModifiers { AutomationName = name });

    /// <summary>
    /// Sets AutomationProperties.AutomationId on the element's control.
    /// Provides a stable identifier for UI Automation / test tools (FlaUI, WinAppDriver).
    /// Usage: Button("Go", onClick).AutomationId("GoButton")
    /// </summary>
    public static T AutomationId<T>(this T el, string id) where T : Element =>
        Modify(el, new ElementModifiers { AutomationId = id });

    /// <summary>
    /// Sets ElementSoundMode on the element's control.
    /// Usage: Button("Play", onClick).SoundMode(ElementSoundMode.Off)
    /// </summary>
    public static T SoundMode<T>(this T el, ElementSoundMode mode) where T : Element =>
        Modify(el, new ElementModifiers { ElementSoundMode = mode });

    /// <summary>
    /// Runs an action once when the element is first mounted (not on re-renders).
    /// Use this instead of .Set() when attaching event handlers to avoid accumulation.
    /// Usage: Button("Go", null).OnMount(fe => { ((Button)fe).Click += ...; })
    /// </summary>
    public static T OnMount<T>(this T el, Action<FrameworkElement> action) where T : Element =>
        Modify(el, new ElementModifiers { OnMountAction = action });

    // ════════════════════════════════════════════════════════════════
    //  Accessibility — Tier 1 (inline on ElementModifiers)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sets AutomationProperties.HeadingLevel (Level1–Level9).
    /// Screen reader users navigate by headings, like HTML h1–h6.
    /// </summary>
    /// <example>Text("Settings").HeadingLevel(AutomationHeadingLevel.Level1)</example>
    public static T HeadingLevel<T>(this T el, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel level) where T : Element =>
        Modify(el, new ElementModifiers { HeadingLevel = level });

    /// <summary>
    /// Sets UIElement.IsTabStop — whether the element participates in Tab navigation.
    /// Works on any element type (Panel, Control, etc.) in WinUI 3.
    /// </summary>
    /// <example>Border(content).IsTabStop(false)</example>
    public static T IsTabStop<T>(this T el, bool isTabStop = true) where T : Element =>
        Modify(el, new ElementModifiers { IsTabStop = isTabStop });

    /// <summary>
    /// Sets Control.TabIndex — Tab order position. Lower values receive focus first.
    /// </summary>
    /// <example>Button("Submit").TabIndex(1)</example>
    public static T TabIndex<T>(this T el, int index) where T : Element =>
        Modify(el, new ElementModifiers { TabIndex = index });

    /// <summary>
    /// Sets UIElement.AccessKey — the Alt+Key shortcut (underlined hint shown on Alt press).
    /// When used on a button bound to a <see cref="Command"/>, this per-site access key
    /// overrides <see cref="Command.AccessKey"/> (per-site override always wins).
    /// </summary>
    /// <example>Button("File", onClick).AccessKey("F")</example>
    public static T AccessKey<T>(this T el, string key) where T : Element =>
        Modify(el, new ElementModifiers { AccessKey = key });

    /// <summary>
    /// Sets UIElement.XYFocusKeyboardNavigation — enables directional (Xbox-style)
    /// focus navigation with arrow keys or gamepad DPad.
    /// </summary>
    /// <example>Grid(tiles).XYFocusKeyboardNavigation(XYFocusKeyboardNavigationMode.Enabled)</example>
    public static T XYFocusKeyboardNavigation<T>(this T el, Microsoft.UI.Xaml.Input.XYFocusKeyboardNavigationMode mode) where T : Element =>
        Modify(el, new ElementModifiers { XYFocusKeyboardNavigation = mode });

    /// <summary>
    /// Handler for UIElement.AccessKeyDisplayRequested — fires when the access-key
    /// bubble should appear (e.g., user pressed Alt). Use to customize the visual.
    /// </summary>
    public static T AccessKeyDisplayRequested<T>(this T el, Action handler) where T : Element =>
        Modify(el, new ElementModifiers { OnAccessKeyDisplayRequested = (_, _) => handler() });

    /// <summary>
    /// Handler for UIElement.AccessKeyDisplayRequested with full event args.
    /// </summary>
    public static T AccessKeyDisplayRequested<T>(this T el, Action<UIElement, Microsoft.UI.Xaml.Input.AccessKeyDisplayRequestedEventArgs> handler) where T : Element =>
        Modify(el, new ElementModifiers { OnAccessKeyDisplayRequested = handler });

    /// <summary>
    /// Binds this element to an imperative <see cref="OpenClawTray.Infrastructure.Input.ElementRef"/>.
    /// Obtain the ref from <c>ctx.UseElementFocus()</c> (or construct one manually) and use
    /// <see cref="OpenClawTray.Infrastructure.Input.FocusManager.Focus"/> to imperatively focus the
    /// referenced element after mount.
    /// </summary>
    /// <example>
    /// var (inputRef, requestFocus) = ctx.UseElementFocus();
    /// return TextField(value, setValue).Ref(inputRef);
    /// </example>
    public static T Ref<T>(this T el, OpenClawTray.Infrastructure.Input.ElementRef target) where T : Element =>
        Modify(el, new ElementModifiers { Ref = target });

    // ════════════════════════════════════════════════════════════════
    //  Accessibility — Tier 2/3 (lazy AccessibilityModifiers sub-record)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sets AutomationProperties.HelpText — supplemental text read by screen readers
    /// after the Name. Analogous to SwiftUI's .accessibilityHint().
    /// </summary>
    /// <example>TextField(email, setEmail).HelpText("Enter your work email address")</example>
    public static T HelpText<T>(this T el, string text) where T : Element =>
        ModifyA11y(el, new AccessibilityModifiers { HelpText = text });

    /// <summary>
    /// Sets AutomationProperties.FullDescription — extended description for complex elements.
    /// </summary>
    /// <example>Chart(...).FullDescription("Bar chart showing Q1 revenue by region")</example>
    public static T FullDescription<T>(this T el, string desc) where T : Element =>
        ModifyA11y(el, new AccessibilityModifiers { FullDescription = desc });

    /// <summary>
    /// Sets AutomationProperties.LandmarkType (Main, Navigation, Search, Form, Custom).
    /// Screen readers announce landmarks and let users jump between them.
    /// </summary>
    /// <example>VStack(children).Landmark(AutomationLandmarkType.Main)</example>
    public static T Landmark<T>(this T el, Microsoft.UI.Xaml.Automation.Peers.AutomationLandmarkType type) where T : Element =>
        ModifyA11y(el, new AccessibilityModifiers { LandmarkType = type });

    /// <summary>
    /// Sets AutomationProperties.AccessibilityView (Content, Control, Raw).
    /// Use Raw to hide decorative elements from screen readers.
    /// </summary>
    /// <example>Image(decorativeUri).AccessibilityView(AccessibilityView.Raw)</example>
    public static T AccessibilityView<T>(this T el, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView view) where T : Element =>
        ModifyA11y(el, new AccessibilityModifiers { AccessibilityView = view });

    /// <summary>
    /// Hides element from screen readers entirely.
    /// Shorthand for .AccessibilityView(AccessibilityView.Raw).
    /// </summary>
    /// <example>Icon(decorativeGlyph).AccessibilityHidden()</example>
    public static T AccessibilityHidden<T>(this T el) where T : Element =>
        ModifyA11y(el, new AccessibilityModifiers { AccessibilityView = Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw });

    /// <summary>
    /// Sets AutomationProperties.IsRequiredForForm. Screen readers announce "required".
    /// </summary>
    /// <example>TextField(name, setName).Required()</example>
    public static T Required<T>(this T el) where T : Element =>
        ModifyA11y(el, new AccessibilityModifiers { IsRequiredForForm = true });

    /// <summary>
    /// Sets AutomationProperties.LiveSetting. Screen readers announce content changes.
    /// Polite = queued after current speech. Assertive = interrupts immediately.
    /// </summary>
    /// <example>Text(statusMessage).LiveRegion(AutomationLiveSetting.Polite)</example>
    public static T LiveRegion<T>(this T el, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting mode = Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite) where T : Element =>
        ModifyA11y(el, new AccessibilityModifiers { LiveSetting = mode });

    /// <summary>
    /// Sets AutomationProperties.PositionInSet and SizeOfSet (e.g., "item 3 of 10").
    /// </summary>
    /// <example>ListItem(text).PositionInSet(3, 10)</example>
    public static T PositionInSet<T>(this T el, int position, int size) where T : Element =>
        ModifyA11y(el, new AccessibilityModifiers { PositionInSet = position, SizeOfSet = size });

    /// <summary>
    /// Sets AutomationProperties.Level — hierarchical depth (e.g., tree node depth).
    /// </summary>
    /// <example>TreeItem(text).HierarchyLevel(2)</example>
    public static T HierarchyLevel<T>(this T el, int level) where T : Element =>
        ModifyA11y(el, new AccessibilityModifiers { Level = level });

    /// <summary>
    /// Sets AutomationProperties.ItemStatus — status string announced by screen readers.
    /// </summary>
    /// <example>MailFolder("Inbox").ItemStatus("3 unread")</example>
    public static T ItemStatus<T>(this T el, string status) where T : Element =>
        ModifyA11y(el, new AccessibilityModifiers { ItemStatus = status });

    /// <summary>
    /// Associates this element with a labelling element via its AutomationId.
    /// The reconciler resolves the reference at mount time.
    /// </summary>
    /// <example>TextField(email, setEmail).LabeledBy("EmailLabel")</example>
    public static T LabeledBy<T>(this T el, string labelAutomationId) where T : Element =>
        ModifyA11y(el, new AccessibilityModifiers { LabeledBy = labelAutomationId });

    /// <summary>
    /// Sets UIElement.TabFocusNavigation — how Tab navigates within a container.
    /// Local = cycle within container. Once = enter once then leave. Cycle = loop forever.
    /// </summary>
    /// <example>ToolBar(buttons).TabNavigation(KeyboardNavigationMode.Once)</example>
    public static T TabNavigation<T>(this T el, Microsoft.UI.Xaml.Input.KeyboardNavigationMode mode) where T : Element =>
        ModifyA11y(el, new AccessibilityModifiers { TabFocusNavigation = mode });

    // ════════════════════════════════════════════════════════════════
    //  Composite component semantics
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Wraps this element in a SemanticPanel that provides custom automation
    /// semantics to screen readers. Use this for composite components that need
    /// to describe their role and value (e.g., a star rating widget built from
    /// Image elements that should announce as "slider, 3 of 5 stars").
    ///
    /// Analogous to SwiftUI's .accessibilityRepresentation {} and Compose's
    /// Modifier.semantics { role = Role.Slider }.
    /// </summary>
    /// <example>
    /// StarRating(value: 3, max: 5)
    ///     .Semantics(role: "slider", value: "3 of 5 stars",
    ///                rangeValue: 3, rangeMin: 0, rangeMax: 5)
    /// </example>
    public static SemanticElement Semantics<T>(this T el,
        string? role = null,
        string? value = null,
        double? rangeMin = null,
        double? rangeMax = null,
        double? rangeValue = null,
        bool isReadOnly = true) where T : Element
    {
        return new SemanticElement(el, new SemanticDescription(role, value, rangeMin, rangeMax, rangeValue, isReadOnly));
    }

    // ════════════════════════════════════════════════════════════════
    //  ThemeShadow / Translation modifiers
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sets the Translation property (Vector3) on the element's control.
    /// Commonly used with ThemeShadow for z-depth effects.
    /// Routes through AnimationHelper so WithAnimation scopes animate the change.
    /// </summary>
    public static T Translation<T>(this T el, float x, float y, float z) where T : Element =>
        Modify(el, new ElementModifiers { Translation = new global::System.Numerics.Vector3(x, y, z) });

    // ════════════════════════════════════════════════════════════════
    //  Compositor property animation (.Animate() modifier)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enables implicit compositor animation on visual property changes.
    /// All visual property changes (Opacity, Scale, Rotation, Translation, CenterPoint)
    /// will animate using the specified curve.
    /// </summary>
    public static T Animate<T>(this T el, OpenClawTray.Infrastructure.Animation.Curve curve,
        OpenClawTray.Infrastructure.Animation.AnimateProperty properties = OpenClawTray.Infrastructure.Animation.AnimateProperty.All) where T : Element =>
        el with { AnimationConfig = new OpenClawTray.Infrastructure.Animation.AnimationConfig(curve, properties) };

    // ════════════════════════════════════════════════════════════════
    //  Enter/exit transitions (.Transition() modifier)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enables enter/exit transitions when this element is conditionally rendered.
    /// Enter: animates from initial state to visible. Exit: animates out before unmount.
    /// </summary>
    public static T Transition<T>(this T el, OpenClawTray.Infrastructure.Animation.Transition transition,
        OpenClawTray.Infrastructure.Animation.Curve? curve = null) where T : Element =>
        el with { ElementTransition = new OpenClawTray.Infrastructure.Animation.ElementTransition(transition, curve) };

    // ════════════════════════════════════════════════════════════════
    //  Interaction states (.InteractionStates() modifier)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Declares zero-reconcile interaction state visual changes (hover, pressed, focused).
    /// The reconciler registers pointer event handlers that drive compositor animations
    /// and direct brush swaps — no state variables or re-renders needed.
    /// </summary>
    public static T InteractionStates<T>(this T el,
        Func<OpenClawTray.Infrastructure.Animation.InteractionStatesBuilder, OpenClawTray.Infrastructure.Animation.InteractionStatesBuilder> configure,
        OpenClawTray.Infrastructure.Animation.Curve? curve = null) where T : Element
    {
        var builder = configure(new OpenClawTray.Infrastructure.Animation.InteractionStatesBuilder());
        var config = builder.Build();
        if (curve is not null)
            config = config with { Curve = curve };
        return el with { InteractionStates = config };
    }

    // ════════════════════════════════════════════════════════════════
    //  Staggered children animation (.Stagger() modifier)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Adds incrementing animation delays to children in a container.
    /// Child N gets N * delay applied to its compositor animations.
    /// </summary>
    public static T Stagger<T>(this T el, TimeSpan delay,
        OpenClawTray.Infrastructure.Animation.Curve? curve = null) where T : Element =>
        el with { StaggerConfig = new OpenClawTray.Infrastructure.Animation.StaggerConfig(delay, curve) };

    // ════════════════════════════════════════════════════════════════
    //  Keyframe animation (.Keyframes() modifier)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attaches a trigger-based keyframe animation. The animation plays when
    /// the trigger value changes between renders.
    /// </summary>
    public static T Keyframes<T>(this T el, string name, object? trigger,
        Func<OpenClawTray.Infrastructure.Animation.KeyframeBuilder, OpenClawTray.Infrastructure.Animation.KeyframeBuilder> configure) where T : Element
    {
        var builder = configure(new OpenClawTray.Infrastructure.Animation.KeyframeBuilder());
        var def = builder.Build();
        var entry = new OpenClawTray.Infrastructure.Animation.KeyframeEntry(name, trigger, def);

        var existing = el.KeyframeAnimations;
        var entries = existing is not null
            ? [.. existing, entry]
            : new[] { entry };
        return el with { KeyframeAnimations = entries };
    }

    // ════════════════════════════════════════════════════════════════
    //  Scroll-linked expression animation (.ScrollLinked() modifier)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attaches scroll-linked expression animations driven by a ScrollViewer.
    /// Expressions run on the compositor at display refresh rate with zero managed code.
    /// </summary>
    public static T ScrollLinked<T>(this T el,
        Microsoft.UI.Xaml.Controls.ScrollViewer scrollViewer,
        Func<OpenClawTray.Infrastructure.Animation.ScrollAnimationBuilder, OpenClawTray.Infrastructure.Animation.ScrollAnimationBuilder> configure) where T : Element
    {
        var builder = configure(new OpenClawTray.Infrastructure.Animation.ScrollAnimationBuilder());
        var expressions = builder.Build();
        return el with { ScrollAnimation = new OpenClawTray.Infrastructure.Animation.ScrollAnimationConfig(scrollViewer, expressions) };
    }

    // ════════════════════════════════════════════════════════════════
    //  Internal
    // ════════════════════════════════════════════════════════════════

    private static T Modify<T>(T el, ElementModifiers mods) where T : Element =>
        el with { Modifiers = el.Modifiers is not null ? el.Modifiers.Merge(mods) : mods };

    private static T ModifyA11y<T>(T el, AccessibilityModifiers a11y) where T : Element
    {
        var existing = el.Modifiers?.Accessibility;
        var merged = existing is not null ? existing.Merge(a11y) : a11y;
        return Modify(el, new ElementModifiers { Accessibility = merged });
    }

    private static T ModifyTheme<T>(T el, string property, ThemeRef theme) where T : Element
    {
        var bindings = el.ThemeBindings is not null
            ? new Dictionary<string, ThemeRef>(el.ThemeBindings) { [property] = theme }
            : new Dictionary<string, ThemeRef> { [property] = theme };
        return el with { ThemeBindings = bindings };
    }
}
