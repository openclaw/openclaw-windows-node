// Standalone FlexPanel for WinUI3, implemented in OpenClawTray.Infrastructure.Layout.
// No dependency on OpenClawTray.Infrastructure.Core — usable in any WinUI3 app.
//
// AI-HINT: This is a WinUI Panel that delegates layout to Yoga.
// Two-pass measure: Pass 1 = content-size (NaN width/height), Pass 2 = flex distribution
// (definite main axis to enable grow/shrink). Arrange reads cached results from Yoga.
// Each child has a cached YogaNode; attached properties (Grow, Shrink, Basis, etc.)
// are synced to Yoga nodes in SyncYogaTree(). MeasureFunc bridge lets Yoga call
// back into WinUI Measure for leaf children that need intrinsic sizing.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Infrastructure.Layout;
using Windows.Foundation;

namespace OpenClawTray.Infrastructure.Layout;

/// <summary>
/// A WinUI3 Panel that implements CSS Flexbox layout using the Yoga layout engine.
/// Can be used standalone in XAML or through the Reactor framework.
/// </summary>
public partial class FlexPanel : Panel
{
    // ── Yoga node cache: one YogaNode per UIElement child ──
    private readonly Dictionary<UIElement, YogaNode> _nodeCache = new();
    private readonly YogaNode _rootNode = new();
    private readonly HashSet<UIElement> _syncCurrentChildren = new();
    private readonly List<UIElement> _syncToRemove = new();

    public FlexPanel()
    {
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Clear Yoga node cache when removed from the visual tree to avoid leaking references
        foreach (var node in _nodeCache.Values)
            _rootNode.RemoveChild(node);
        _nodeCache.Clear();
    }

    // ── Container dependency properties ──

    public static readonly DependencyProperty DirectionProperty =
        DependencyProperty.Register(nameof(Direction), typeof(FlexDirection), typeof(FlexPanel),
            new PropertyMetadata(FlexDirection.Row, OnContainerPropertyChanged));

    public static readonly DependencyProperty JustifyContentProperty =
        DependencyProperty.Register(nameof(JustifyContent), typeof(FlexJustify), typeof(FlexPanel),
            new PropertyMetadata(FlexJustify.FlexStart, OnContainerPropertyChanged));

    public static readonly DependencyProperty AlignItemsProperty =
        DependencyProperty.Register(nameof(AlignItems), typeof(FlexAlign), typeof(FlexPanel),
            new PropertyMetadata(FlexAlign.Stretch, OnContainerPropertyChanged));

    public static readonly DependencyProperty AlignContentProperty =
        DependencyProperty.Register(nameof(AlignContent), typeof(FlexAlign), typeof(FlexPanel),
            new PropertyMetadata(FlexAlign.FlexStart, OnContainerPropertyChanged));

    public static readonly DependencyProperty WrapProperty =
        DependencyProperty.Register(nameof(Wrap), typeof(FlexWrap), typeof(FlexPanel),
            new PropertyMetadata(FlexWrap.NoWrap, OnContainerPropertyChanged));

    public static readonly DependencyProperty LayoutDirectionProperty =
        DependencyProperty.Register(nameof(LayoutDirection), typeof(FlexLayoutDirection), typeof(FlexPanel),
            new PropertyMetadata(FlexLayoutDirection.LTR, OnContainerPropertyChanged));

    public static readonly DependencyProperty ColumnGapProperty =
        DependencyProperty.Register(nameof(ColumnGap), typeof(double), typeof(FlexPanel),
            new PropertyMetadata(0.0, OnContainerPropertyChanged));

    public static readonly DependencyProperty RowGapProperty =
        DependencyProperty.Register(nameof(RowGap), typeof(double), typeof(FlexPanel),
            new PropertyMetadata(0.0, OnContainerPropertyChanged));

    public static readonly DependencyProperty FlexPaddingProperty =
        DependencyProperty.Register(nameof(FlexPadding), typeof(Thickness), typeof(FlexPanel),
            new PropertyMetadata(default(Thickness), OnContainerPropertyChanged));

    public FlexDirection Direction
    {
        get => (FlexDirection)GetValue(DirectionProperty);
        set => SetValue(DirectionProperty, value);
    }

    public FlexJustify JustifyContent
    {
        get => (FlexJustify)GetValue(JustifyContentProperty);
        set => SetValue(JustifyContentProperty, value);
    }

    public FlexAlign AlignItems
    {
        get => (FlexAlign)GetValue(AlignItemsProperty);
        set => SetValue(AlignItemsProperty, value);
    }

    public FlexAlign AlignContent
    {
        get => (FlexAlign)GetValue(AlignContentProperty);
        set => SetValue(AlignContentProperty, value);
    }

    public FlexWrap Wrap
    {
        get => (FlexWrap)GetValue(WrapProperty);
        set => SetValue(WrapProperty, value);
    }

    public FlexLayoutDirection LayoutDirection
    {
        get => (FlexLayoutDirection)GetValue(LayoutDirectionProperty);
        set => SetValue(LayoutDirectionProperty, value);
    }

    public double ColumnGap
    {
        get => (double)GetValue(ColumnGapProperty);
        set => SetValue(ColumnGapProperty, value);
    }

    public double RowGap
    {
        get => (double)GetValue(RowGapProperty);
        set => SetValue(RowGapProperty, value);
    }

    public Thickness FlexPadding
    {
        get => (Thickness)GetValue(FlexPaddingProperty);
        set => SetValue(FlexPaddingProperty, value);
    }

    private static void OnContainerPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FlexPanel panel)
            panel.InvalidateMeasure();
    }

    // ── Attached properties (for children) ──

    public static readonly DependencyProperty GrowProperty =
        DependencyProperty.RegisterAttached("Grow", typeof(double), typeof(FlexPanel),
            new PropertyMetadata(0.0, OnChildPropertyChanged));

    public static readonly DependencyProperty ShrinkProperty =
        DependencyProperty.RegisterAttached("Shrink", typeof(double), typeof(FlexPanel),
            new PropertyMetadata(1.0, OnChildPropertyChanged));

    public static readonly DependencyProperty BasisProperty =
        DependencyProperty.RegisterAttached("Basis", typeof(double), typeof(FlexPanel),
            new PropertyMetadata(double.NaN, OnChildPropertyChanged));

    public static readonly DependencyProperty AlignSelfProperty =
        DependencyProperty.RegisterAttached("AlignSelf", typeof(FlexAlign), typeof(FlexPanel),
            new PropertyMetadata(FlexAlign.Auto, OnChildPropertyChanged));

    public static readonly DependencyProperty PositionProperty =
        DependencyProperty.RegisterAttached("Position", typeof(FlexPositionType), typeof(FlexPanel),
            new PropertyMetadata(FlexPositionType.Relative, OnChildPropertyChanged));

    public static readonly DependencyProperty LeftProperty =
        DependencyProperty.RegisterAttached("Left", typeof(double), typeof(FlexPanel),
            new PropertyMetadata(double.NaN, OnChildPropertyChanged));

    public static readonly DependencyProperty TopProperty =
        DependencyProperty.RegisterAttached("Top", typeof(double), typeof(FlexPanel),
            new PropertyMetadata(double.NaN, OnChildPropertyChanged));

    public static readonly DependencyProperty RightProperty =
        DependencyProperty.RegisterAttached("Right", typeof(double), typeof(FlexPanel),
            new PropertyMetadata(double.NaN, OnChildPropertyChanged));

    public static readonly DependencyProperty BottomProperty =
        DependencyProperty.RegisterAttached("Bottom", typeof(double), typeof(FlexPanel),
            new PropertyMetadata(double.NaN, OnChildPropertyChanged));

    // Attached property static accessors
    public static void SetGrow(UIElement el, double value) => el.SetValue(GrowProperty, value);
    public static double GetGrow(UIElement el) => (double)el.GetValue(GrowProperty);

    public static void SetShrink(UIElement el, double value) => el.SetValue(ShrinkProperty, value);
    public static double GetShrink(UIElement el) => (double)el.GetValue(ShrinkProperty);

    public static void SetBasis(UIElement el, double value) => el.SetValue(BasisProperty, value);
    public static double GetBasis(UIElement el) => (double)el.GetValue(BasisProperty);

    public static void SetAlignSelf(UIElement el, FlexAlign value) => el.SetValue(AlignSelfProperty, value);
    public static FlexAlign GetAlignSelf(UIElement el) => (FlexAlign)el.GetValue(AlignSelfProperty);

    public static void SetPosition(UIElement el, FlexPositionType value) => el.SetValue(PositionProperty, value);
    public static FlexPositionType GetPosition(UIElement el) => (FlexPositionType)el.GetValue(PositionProperty);

    public static void SetLeft(UIElement el, double value) => el.SetValue(LeftProperty, value);
    public static double GetLeft(UIElement el) => (double)el.GetValue(LeftProperty);

    public static void SetTop(UIElement el, double value) => el.SetValue(TopProperty, value);
    public static double GetTop(UIElement el) => (double)el.GetValue(TopProperty);

    public static void SetRight(UIElement el, double value) => el.SetValue(RightProperty, value);
    public static double GetRight(UIElement el) => (double)el.GetValue(RightProperty);

    public static void SetBottom(UIElement el, double value) => el.SetValue(BottomProperty, value);
    public static double GetBottom(UIElement el) => (double)el.GetValue(BottomProperty);

    private static void OnChildPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement el && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(el) is FlexPanel panel)
            panel.InvalidateMeasure();
    }

    // ── Layout ──

    // MeasureOverride — CSS block-level flex container semantics.
    //
    // CSS rule: a flex container is a block-level box. Its INLINE axis (width,
    // for horizontal writing mode) is resolved against the containing block
    // BEFORE flex layout runs — i.e. `width: auto` fills the parent's content
    // width. Its BLOCK axis (height) is `auto` → content-sized from children.
    // This is independent of flex-direction; direction only controls how
    // children flow within the container.
    //
    // Translating to Yoga: the root node is always called with a DEFINITE
    // inline-axis size (availableSize.Width when finite) and NaN on the block
    // axis. Children measured under this rule see their cross-axis constraint
    // naturally (align-items: stretch = fill container width), so text
    // controls (RichTextBlock, TextBlock) wrap correctly in a single pass —
    // no expensive infinite-width measurement followed by reflow.
    //
    // Escape hatch — `HorizontalAlignment != Stretch` on the FlexPanel itself
    // maps to CSS `width: fit-content`. In that case the inline axis is NaN
    // (content-size) capped by availableSize.Width. Slower for text-heavy
    // children, but the user opted in.

    // Cached child layout results from MeasureOverride, reused in ArrangeOverride
    // to avoid re-running Yoga (which calls child.Measure()) during the arrange
    // pass — calling Measure during Arrange can trigger LayoutCycleException.
    private struct ChildLayout { public float X, Y, Width, Height; }
    private readonly List<ChildLayout> _cachedChildLayouts = new();
    private Size _cachedDesiredSize;
    private bool _arranging;

    protected override Size MeasureOverride(Size availableSize)
    {
        SyncYogaTree();
        SetRootConstraints(availableSize);

        bool hasDefiniteWidth = !float.IsInfinity((float)availableSize.Width);
        bool hasDefiniteHeight = !float.IsInfinity((float)availableSize.Height);

        // Inline-axis fill (CSS default) unless the user asked for fit-content
        // via non-Stretch HorizontalAlignment. Width on the panel itself is
        // already clamped by FrameworkElement.Measure before we get here.
        bool fillInlineAxis = HorizontalAlignment == HorizontalAlignment.Stretch;

        float rootWidth;
        if (fillInlineAxis && hasDefiniteWidth)
        {
            // CSS: block-level flex container fills its containing block.
            rootWidth = (float)availableSize.Width;
            _rootNode.MaxWidth = YogaValue.Undefined;
        }
        else
        {
            // CSS fit-content: content-size the inline axis, capped by
            // availableSize. This is the opt-in "shrink-wrap" path.
            rootWidth = float.NaN;
            _rootNode.MaxWidth = hasDefiniteWidth
                ? YogaValue.Point((float)availableSize.Width)
                : YogaValue.Undefined;
        }

        // Block axis is always content-sized, with availableSize as a cap so
        // unbounded child lists can't exceed the scroll host's viewport.
        _rootNode.MaxHeight = hasDefiniteHeight
            ? YogaValue.Point((float)availableSize.Height)
            : YogaValue.Undefined;

        _rootNode.CalculateLayout(rootWidth, float.NaN, LayoutDirection);

        _cachedDesiredSize = new Size(_rootNode.LayoutWidth, _rootNode.LayoutHeight);

        // Cache child positions and measure children at Yoga's resolved sizes.
        // This fulfills the WinUI contract that all children must be Measured
        // during MeasureOverride, and caches positions for ArrangeOverride.
        _cachedChildLayouts.Clear();
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (_nodeCache.TryGetValue(child, out var childNode))
            {
                var layout = new ChildLayout
                {
                    X = childNode.LayoutX,
                    Y = childNode.LayoutY,
                    Width = childNode.LayoutWidth,
                    Height = childNode.LayoutHeight
                };
                _cachedChildLayouts.Add(layout);
                // Add margin back: Yoga's layout sizes are content-area,
                // but WinUI's Measure subtracts the child's Margin.
                var m = child is FrameworkElement cfe ? cfe.Margin : default;
                child.Measure(new Size(
                    layout.Width + m.Left + m.Right,
                    layout.Height + m.Top + m.Bottom));
            }
            else
            {
                _cachedChildLayouts.Add(default);
            }
        }

        return _cachedDesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // If finalSize matches what we measured for, use cached positions directly.
        // This avoids re-running Yoga (which would call child.Measure() via
        // MeasureFunction callbacks), preventing LayoutCycleException.
        bool sizeChanged =
            Math.Abs(finalSize.Width - _cachedDesiredSize.Width) > 0.5 ||
            Math.Abs(finalSize.Height - _cachedDesiredSize.Height) > 0.5;

        if (sizeChanged)
        {
            // Final size differs from measured size — re-run Yoga to redistribute
            // space, but suppress child.Measure() calls during this arrange pass.
            _arranging = true;
            try
            {
                _rootNode.MaxWidth = YogaValue.Undefined;
                _rootNode.MaxHeight = YogaValue.Undefined;
                _rootNode.CalculateLayout(
                    (float)finalSize.Width,
                    (float)finalSize.Height,
                    LayoutDirection);

                // Update cached positions from the new layout
                _cachedChildLayouts.Clear();
                for (int i = 0; i < Children.Count; i++)
                {
                    var child = Children[i];
                    if (_nodeCache.TryGetValue(child, out var childNode))
                    {
                        _cachedChildLayouts.Add(new ChildLayout
                        {
                            X = childNode.LayoutX,
                            Y = childNode.LayoutY,
                            Width = childNode.LayoutWidth,
                            Height = childNode.LayoutHeight
                        });
                    }
                    else
                    {
                        _cachedChildLayouts.Add(default);
                    }
                }
            }
            finally
            {
                _arranging = false;
            }
        }

        for (int i = 0; i < Children.Count && i < _cachedChildLayouts.Count; i++)
        {
            var layout = _cachedChildLayouts[i];
            var child = Children[i];
            // Expand arrange rect by margin: Yoga positions/sizes the content area,
            // but WinUI's Arrange subtracts the child's Margin from the rect.
            var m = child is FrameworkElement fe ? fe.Margin : default;
            child.Arrange(new Rect(
                layout.X - m.Left,
                layout.Y - m.Top,
                layout.Width + m.Left + m.Right,
                layout.Height + m.Top + m.Bottom));
        }

        return finalSize;
    }

    private void SetRootConstraints(Size availableSize)
    {
        // Container properties
        _rootNode.FlexDirection = Direction;
        _rootNode.JustifyContent = JustifyContent;
        _rootNode.AlignItems = AlignItems;
        _rootNode.AlignContent = AlignContent;
        _rootNode.FlexWrap = Wrap;
        _rootNode.SetGap(YogaGutter.Column, (float)ColumnGap);
        _rootNode.SetGap(YogaGutter.Row, (float)RowGap);

        // FlexPadding
        var p = FlexPadding;
        _rootNode.SetPadding(YogaEdge.Left, YogaValue.Point((float)p.Left));
        _rootNode.SetPadding(YogaEdge.Top, YogaValue.Point((float)p.Top));
        _rootNode.SetPadding(YogaEdge.Right, YogaValue.Point((float)p.Right));
        _rootNode.SetPadding(YogaEdge.Bottom, YogaValue.Point((float)p.Bottom));
    }

    private void SyncYogaTree()
    {
        // Remove nodes for children that are no longer present
        _syncCurrentChildren.Clear();
        foreach (UIElement child in Children)
            _syncCurrentChildren.Add(child);

        _syncToRemove.Clear();
        foreach (var kvp in _nodeCache)
        {
            if (!_syncCurrentChildren.Contains(kvp.Key))
                _syncToRemove.Add(kvp.Key);
        }
        foreach (var el in _syncToRemove)
        {
            if (_nodeCache.TryGetValue(el, out var node))
                _rootNode.RemoveChild(node);
            _nodeCache.Remove(el);
        }

        // Ensure each child has a YogaNode at the correct index
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (!_nodeCache.TryGetValue(child, out var childNode))
            {
                childNode = new YogaNode();
                _nodeCache[child] = childNode;

                // Set measure function: delegates to WinUI Measure.
                // During ArrangeOverride (_arranging=true), return the last
                // DesiredSize without calling Measure — calling Measure during
                // Arrange can trigger LayoutCycleException.
                //
                // Margin compensation: Yoga handles margins for positioning and
                // spacing between children (synced in ApplyAttachedProperties).
                // WinUI also subtracts Margin during Measure/Arrange. To avoid
                // double-counting, we add the margin back to Yoga's constraints
                // before calling WinUI Measure, and subtract it from DesiredSize
                // before returning to Yoga.
                var capturedChild = child;
                var panel = this;
                childNode.MeasureFunction = (node, w, wMode, h, hMode) =>
                {
                    var m = capturedChild is FrameworkElement cfe ? cfe.Margin : default;
                    double mH = m.Left + m.Right;
                    double mV = m.Top + m.Bottom;

                    if (panel._arranging)
                        return new YogaSize(
                            Math.Max(0, (float)(capturedChild.DesiredSize.Width - mH)),
                            Math.Max(0, (float)(capturedChild.DesiredSize.Height - mV)));

                    // Yoga's constraints are content-area (excluding margin).
                    // Add margin so WinUI's subtraction yields the correct content area.
                    var constraintW = wMode == YogaMeasureMode.Undefined ? double.PositiveInfinity : w + mH;
                    var constraintH = hMode == YogaMeasureMode.Undefined ? double.PositiveInfinity : h + mV;
                    capturedChild.Measure(new Size(constraintW, constraintH));
                    // Return content size (without margin) since Yoga tracks margins separately
                    return new YogaSize(
                        Math.Max(0, (float)(capturedChild.DesiredSize.Width - mH)),
                        Math.Max(0, (float)(capturedChild.DesiredSize.Height - mV)));
                };
            }

            // Apply attached properties from the UIElement to the YogaNode
            ApplyAttachedProperties(child, childNode);

            // Mirror WinUI Visibility=Collapsed onto Yoga's Display=None:
            // Collapsed is the XAML equivalent of CSS display:none — the
            // element contributes nothing to main-axis size and no gap slot.
            // StackPanel does the same.
            childNode.Display = child.Visibility == Visibility.Collapsed
                ? YogaDisplay.None
                : YogaDisplay.Flex;

            // Ensure correct child order in Yoga tree
            if (i < _rootNode.ChildCount)
            {
                if (_rootNode.GetChild(i) != childNode)
                {
                    // Remove if present elsewhere and re-insert at correct position
                    _rootNode.RemoveChild(childNode);
                    _rootNode.InsertChild(childNode, i);
                }
            }
            else
            {
                if (childNode.Owner != _rootNode)
                    _rootNode.InsertChild(childNode, i);
            }
        }

        // Remove extra Yoga children beyond current count
        while (_rootNode.ChildCount > Children.Count)
        {
            _rootNode.RemoveChild(_rootNode.ChildCount - 1);
        }
    }

    private static void ApplyAttachedProperties(UIElement el, YogaNode node)
    {
        var grow = GetGrow(el);
        var shrink = GetShrink(el);
        var basis = GetBasis(el);
        var alignSelf = GetAlignSelf(el);
        var position = GetPosition(el);

        node.Style.FlexGrow = (float)grow;
        node.Style.FlexShrink = (float)shrink;
        node.Style.FlexBasis = double.IsNaN(basis) ? YogaValue.Auto : YogaValue.Point((float)basis);
        node.Style.AlignSelf = alignSelf;
        node.Style.PositionType = position;

        // Position insets
        var left = GetLeft(el);
        var top = GetTop(el);
        var right = GetRight(el);
        var bottom = GetBottom(el);

        node.Style.Position[(int)YogaEdge.Left] = double.IsNaN(left) ? YogaValue.Undefined : YogaValue.Point((float)left);
        node.Style.Position[(int)YogaEdge.Top] = double.IsNaN(top) ? YogaValue.Undefined : YogaValue.Point((float)top);
        node.Style.Position[(int)YogaEdge.Right] = double.IsNaN(right) ? YogaValue.Undefined : YogaValue.Point((float)right);
        node.Style.Position[(int)YogaEdge.Bottom] = double.IsNaN(bottom) ? YogaValue.Undefined : YogaValue.Point((float)bottom);

        // If the child has explicit Width/Height set, pass them to Yoga
        if (el is FrameworkElement fe)
        {
            node.Width = double.IsNaN(fe.Width) ? YogaValue.Auto : YogaValue.Point((float)fe.Width);
            node.Height = double.IsNaN(fe.Height) ? YogaValue.Auto : YogaValue.Point((float)fe.Height);

            // Margins
            var margin = fe.Margin;
            if (margin.Left != 0 || margin.Top != 0 || margin.Right != 0 || margin.Bottom != 0)
            {
                node.SetMargin(YogaEdge.Left, YogaValue.Point((float)margin.Left));
                node.SetMargin(YogaEdge.Top, YogaValue.Point((float)margin.Top));
                node.SetMargin(YogaEdge.Right, YogaValue.Point((float)margin.Right));
                node.SetMargin(YogaEdge.Bottom, YogaValue.Point((float)margin.Bottom));
            }
            else
            {
                node.SetMargin(YogaEdge.Left, YogaValue.Undefined);
                node.SetMargin(YogaEdge.Top, YogaValue.Undefined);
                node.SetMargin(YogaEdge.Right, YogaValue.Undefined);
                node.SetMargin(YogaEdge.Bottom, YogaValue.Undefined);
            }
        }
    }
}
