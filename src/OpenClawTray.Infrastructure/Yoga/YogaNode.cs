// C# port of Meta's Yoga layout engine Node.
// Ported from yoga/node/Node.h, yoga/node/Node.cpp
//
// AI-HINT: YogaNode is a mutable tree node. Key relationships:
//   - _owner = parent (null for root), _children = child list
//   - _style (YogaStyle) = input CSS-like properties, Layout (LayoutResults) = computed output
//   - _measureFunc = leaf-node sizing callback (mutually exclusive with children)
//   - Dirty propagation: MarkDirtyAndPropagate() walks up to root
//   - ProcessedDimensions: min/max collapsed — if min==max, dimension is locked
//   - GetLayoutChildren() flattens Display.Contents nodes (like CSS display:contents)

using OpenClawTray.Infrastructure.Layout;

namespace OpenClawTray.Infrastructure.Layout;

/// <summary>
/// Size returned by measure functions.
/// </summary>
internal struct YogaSize
{
    public float Width;
    public float Height;

    public YogaSize(float width, float height)
    {
        Width = width;
        Height = height;
    }
}

/// <summary>
/// Measure function delegate. Called by Yoga to determine the intrinsic size of leaf nodes.
/// </summary>
internal delegate YogaSize YogaMeasureFunc(
    YogaNode node, float availableWidth, YogaMeasureMode widthMode,
    float availableHeight, YogaMeasureMode heightMode);

/// <summary>
/// Baseline function delegate. Returns the baseline offset from the top of the node.
/// </summary>
internal delegate float YogaBaselineFunc(YogaNode node, float width, float height);

/// <summary>
/// Called when a node is marked dirty.
/// </summary>
internal delegate void YogaDirtiedFunc(YogaNode node);

/// <summary>
/// Core node of the Yoga layout tree. Holds style, layout results, children, and callbacks.
/// </summary>
internal sealed class YogaNode
{
    private readonly YogaStyle _style = new();
    internal readonly LayoutResults Layout = new();
    private readonly List<YogaNode> _children = new();
    private YogaConfig _config;

    private YogaMeasureFunc? _measureFunc;
    private YogaBaselineFunc? _baselineFunc;
    private YogaDirtiedFunc? _dirtiedFunc;

    private YogaNode? _owner;
    private bool _hasNewLayout = true;
    private bool _isReferenceBaseline;
    private bool _isDirty = true;
    private bool _alwaysFormsContainingBlock;
    private YogaNodeType _nodeType = YogaNodeType.Default;
    internal int LineIndex;

    // Processed dimensions: if max == min, use max; otherwise use dimension.
    internal readonly YogaValue[] ProcessedDimensions = { YogaValue.Undefined, YogaValue.Undefined };

    /// <summary>User-defined context object.</summary>
    public object? Context { get; set; }

    public YogaNode() : this(YogaConfig.Default) { }

    public YogaNode(YogaConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (config.UseWebDefaults)
        {
            _style.FlexDirection = FlexDirection.Row;
            _style.AlignContent = FlexAlign.Stretch;
        }
    }

    // ── Tree structure ──

    public YogaNode? Owner => _owner;
    public IReadOnlyList<YogaNode> Children => _children;
    public int ChildCount => _children.Count;

    public YogaNode GetChild(int index) => _children[index];

    public void InsertChild(YogaNode child, int index)
    {
        if (_measureFunc != null)
            throw new InvalidOperationException("Cannot add children to a node with a measure function.");
        _children.Insert(index, child);
        child._owner = this;
        MarkDirtyAndPropagate();
    }

    public bool RemoveChild(YogaNode child)
    {
        if (_children.Remove(child))
        {
            child._owner = null;
            MarkDirtyAndPropagate();
            return true;
        }
        return false;
    }

    public void RemoveChild(int index)
    {
        var child = _children[index];
        _children.RemoveAt(index);
        child._owner = null;
        MarkDirtyAndPropagate();
    }

    public void ClearChildren()
    {
        foreach (var child in _children)
            child._owner = null;
        _children.Clear();
        MarkDirtyAndPropagate();
    }

    internal void SetOwner(YogaNode? owner) => _owner = owner;

    internal void SetChildren(List<YogaNode> children)
    {
        _children.Clear();
        _children.AddRange(children);
    }

    internal void ReplaceChild(YogaNode child, int index) => _children[index] = child;
    internal void ReplaceChild(YogaNode oldChild, YogaNode newChild)
    {
        int idx = _children.IndexOf(oldChild);
        if (idx >= 0) _children[idx] = newChild;
    }

    /// <summary>
    /// Iterate over "layoutable" children — flattening Display.Contents nodes
    /// by recursively yielding their children in place.
    /// Display.None children are NOT skipped here; the algorithm handles them explicitly.
    /// Ported from yoga/node/LayoutableChildren.h
    /// </summary>
    internal IEnumerable<YogaNode> GetLayoutChildren()
    {
        foreach (var child in _children)
        {
            if (child._style.Display == YogaDisplay.Contents)
            {
                foreach (var grandchild in child.GetLayoutChildren())
                    yield return grandchild;
            }
            else
            {
                yield return child;
            }
        }
    }

    /// <summary>
    /// Populates <paramref name="result"/> with layoutable children, avoiding
    /// the enumerator state-machine allocation of <see cref="GetLayoutChildren"/>.
    /// Use when the caller needs a materialized list.
    /// </summary>
    internal void CollectLayoutChildren(List<YogaNode> result)
    {
        foreach (var child in _children)
        {
            if (child._style.Display == YogaDisplay.Contents)
            {
                child.CollectLayoutChildren(result);
            }
            else
            {
                result.Add(child);
            }
        }
    }

    internal int GetLayoutChildCount()
    {
        int count = 0;
        foreach (var child in _children)
        {
            if (child._style.Display == YogaDisplay.Contents)
                count += child.GetLayoutChildCount();
            else
                count++;
        }
        return count;
    }

    // ── Style access ──

    internal YogaStyle Style => _style;

    // ── Public style property accessors ──

    public FlexDirection FlexDirection { get => _style.FlexDirection; set { _style.FlexDirection = value; MarkDirtyAndPropagate(); } }
    public FlexJustify JustifyContent { get => _style.JustifyContent; set { _style.JustifyContent = value; MarkDirtyAndPropagate(); } }
    public FlexAlign AlignItems { get => _style.AlignItems; set { _style.AlignItems = value; MarkDirtyAndPropagate(); } }
    public FlexAlign AlignSelf { get => _style.AlignSelf; set { _style.AlignSelf = value; MarkDirtyAndPropagate(); } }
    public FlexAlign AlignContent { get => _style.AlignContent; set { _style.AlignContent = value; MarkDirtyAndPropagate(); } }
    public FlexWrap FlexWrap { get => _style.FlexWrap; set { _style.FlexWrap = value; MarkDirtyAndPropagate(); } }
    public FlexPositionType PositionType { get => _style.PositionType; set { _style.PositionType = value; MarkDirtyAndPropagate(); } }
    public YogaDisplay Display
    {
        get => _style.Display;
        set
        {
            if (value == YogaDisplay.Grid)
                throw new NotImplementedException("Grid layout is not yet implemented in this C# port of Yoga.");
            _style.Display = value;
            MarkDirtyAndPropagate();
        }
    }
    public YogaOverflow Overflow { get => _style.Overflow; set { _style.Overflow = value; MarkDirtyAndPropagate(); } }

    public float FlexGrow { get => _style.FlexGrow; set { _style.FlexGrow = value; MarkDirtyAndPropagate(); } }
    public float FlexShrink { get => _style.FlexShrink; set { _style.FlexShrink = value; MarkDirtyAndPropagate(); } }
    public YogaValue FlexBasis { get => _style.FlexBasis; set { _style.FlexBasis = value; MarkDirtyAndPropagate(); } }

    public YogaValue Width { get => _style.Dimensions[(int)YogaDimension.Width]; set { _style.Dimensions[(int)YogaDimension.Width] = value; MarkDirtyAndPropagate(); } }
    public YogaValue Height { get => _style.Dimensions[(int)YogaDimension.Height]; set { _style.Dimensions[(int)YogaDimension.Height] = value; MarkDirtyAndPropagate(); } }
    public YogaValue MinWidth { get => _style.MinDimensions[(int)YogaDimension.Width]; set { _style.MinDimensions[(int)YogaDimension.Width] = value; MarkDirtyAndPropagate(); } }
    public YogaValue MinHeight { get => _style.MinDimensions[(int)YogaDimension.Height]; set { _style.MinDimensions[(int)YogaDimension.Height] = value; MarkDirtyAndPropagate(); } }
    public YogaValue MaxWidth { get => _style.MaxDimensions[(int)YogaDimension.Width]; set { _style.MaxDimensions[(int)YogaDimension.Width] = value; MarkDirtyAndPropagate(); } }
    public YogaValue MaxHeight { get => _style.MaxDimensions[(int)YogaDimension.Height]; set { _style.MaxDimensions[(int)YogaDimension.Height] = value; MarkDirtyAndPropagate(); } }

    public float AspectRatio
    {
        get => _style.AspectRatio;
        set
        {
            // Degenerate aspect ratios act as auto
            _style.AspectRatio = (value == 0 || float.IsInfinity(value)) ? float.NaN : value;
            MarkDirtyAndPropagate();
        }
    }

    public void SetMargin(YogaEdge edge, YogaValue value) { _style.Margin[(int)edge] = value; MarkDirtyAndPropagate(); }
    public void SetPadding(YogaEdge edge, YogaValue value) { _style.Padding[(int)edge] = value; MarkDirtyAndPropagate(); }
    public void SetBorder(YogaEdge edge, float value) { _style.Border[(int)edge] = YogaValue.Point(value); MarkDirtyAndPropagate(); }
    public void SetPosition(YogaEdge edge, YogaValue value) { _style.Position[(int)edge] = value; MarkDirtyAndPropagate(); }
    public void SetGap(YogaGutter gutter, float value) { _style.Gap[(int)gutter] = YogaValue.Point(value); MarkDirtyAndPropagate(); }
    public void SetGap(YogaGutter gutter, YogaValue value) { _style.Gap[(int)gutter] = value; MarkDirtyAndPropagate(); }

    // ── Measure/baseline callbacks ──

    public YogaMeasureFunc? MeasureFunction
    {
        get => _measureFunc;
        set
        {
            if (value != null && _children.Count > 0)
                throw new InvalidOperationException("Cannot set measure function on a node with children.");
            _measureFunc = value;
            _nodeType = value != null ? YogaNodeType.Text : YogaNodeType.Default;
            MarkDirtyAndPropagate();
        }
    }

    public YogaBaselineFunc? BaselineFunction
    {
        get => _baselineFunc;
        set => _baselineFunc = value;
    }

    public bool HasMeasureFunc => _measureFunc != null;
    public bool HasBaselineFunc => _baselineFunc != null;

    internal YogaSize Measure(float availableWidth, YogaMeasureMode widthMode, float availableHeight, YogaMeasureMode heightMode)
    {
        var size = _measureFunc!(this, availableWidth, widthMode, availableHeight, heightMode);
        // Validate measure result
        if (YogaFloat.IsUndefined(size.Width) || size.Width < 0)
            size.Width = YogaFloat.MaxOrDefined(0, size.Width);
        if (YogaFloat.IsUndefined(size.Height) || size.Height < 0)
            size.Height = YogaFloat.MaxOrDefined(0, size.Height);
        return size;
    }

    internal float Baseline(float width, float height) => _baselineFunc!(this, width, height);

    // ── Layout results (read after CalculateLayout) ──

    public float LayoutX => Layout.GetPosition(YogaPhysicalEdge.Left);
    public float LayoutY => Layout.GetPosition(YogaPhysicalEdge.Top);
    public float LayoutWidth => Layout.GetDimension(YogaDimension.Width);
    public float LayoutHeight => Layout.GetDimension(YogaDimension.Height);

    public float LayoutMarginLeft => Layout.GetMargin(YogaPhysicalEdge.Left);
    public float LayoutMarginTop => Layout.GetMargin(YogaPhysicalEdge.Top);
    public float LayoutMarginRight => Layout.GetMargin(YogaPhysicalEdge.Right);
    public float LayoutMarginBottom => Layout.GetMargin(YogaPhysicalEdge.Bottom);

    public float LayoutPaddingLeft => Layout.GetPadding(YogaPhysicalEdge.Left);
    public float LayoutPaddingTop => Layout.GetPadding(YogaPhysicalEdge.Top);
    public float LayoutPaddingRight => Layout.GetPadding(YogaPhysicalEdge.Right);
    public float LayoutPaddingBottom => Layout.GetPadding(YogaPhysicalEdge.Bottom);

    public float LayoutBorderLeft => Layout.GetBorder(YogaPhysicalEdge.Left);
    public float LayoutBorderTop => Layout.GetBorder(YogaPhysicalEdge.Top);
    public float LayoutBorderRight => Layout.GetBorder(YogaPhysicalEdge.Right);
    public float LayoutBorderBottom => Layout.GetBorder(YogaPhysicalEdge.Bottom);

    public bool HasNewLayout { get => _hasNewLayout; set => _hasNewLayout = value; }

    // ── Config ──

    public YogaConfig Config => _config;

    internal void SetConfig(YogaConfig config)
    {
        if (YogaConfig.ConfigUpdateInvalidatesLayout(_config, config))
        {
            MarkDirtyAndPropagate();
            Layout.ConfigVersion = 0;
        }
        else
        {
            Layout.ConfigVersion = config.Version;
        }
        _config = config;
    }

    // ── Dirty tracking ──

    public bool IsDirty => _isDirty;

    internal void SetDirty(bool isDirty)
    {
        if (_isDirty == isDirty) return;
        _isDirty = isDirty;
        if (isDirty) _dirtiedFunc?.Invoke(this);
    }

    internal void MarkDirtyAndPropagate()
    {
        if (!_isDirty)
        {
            SetDirty(true);
            Layout.ComputedFlexBasis = float.NaN;
            _owner?.MarkDirtyAndPropagate();
        }
    }

    /// <summary>Mark this node dirty (for leaf nodes with measure functions).</summary>
    public void MarkDirty()
    {
        if (_measureFunc == null)
            throw new InvalidOperationException("Only leaf nodes with measure functions can be marked dirty.");
        MarkDirtyAndPropagate();
    }

    // ── Internal state ──

    internal bool AlwaysFormsContainingBlock { get => _alwaysFormsContainingBlock; set => _alwaysFormsContainingBlock = value; }
    internal YogaNodeType NodeType { get => _nodeType; set => _nodeType = value; }
    internal bool IsReferenceBaseline { get => _isReferenceBaseline; set => _isReferenceBaseline = value; }
    internal YogaDirtiedFunc? DirtiedFunc { get => _dirtiedFunc; set => _dirtiedFunc = value; }

    internal bool HasErrata(YogaErrata errata) => _config.HasErrata(errata);

    // ── Computed helpers ──

    internal float DimensionWithMargin(FlexDirection axis, float widthSize)
        => Layout.GetMeasuredDimension(FlexDirectionHelper.Dimension(axis))
           + _style.ComputeMarginForAxis(axis, widthSize);

    internal bool IsLayoutDimensionDefined(FlexDirection axis)
    {
        float value = Layout.GetMeasuredDimension(FlexDirectionHelper.Dimension(axis));
        return YogaFloat.IsDefined(value) && value >= 0;
    }

    internal bool HasDefiniteLength(YogaDimension dimension, float ownerSize)
    {
        float usedValue = ProcessedDimensions[(int)dimension].Resolve(ownerSize);
        return YogaFloat.IsDefined(usedValue) && usedValue >= 0;
    }

    internal float GetResolvedDimension(FlexLayoutDirection direction, YogaDimension dimension, float referenceLength, float ownerWidth)
    {
        float value = ProcessedDimensions[(int)dimension].Resolve(referenceLength);
        if (_style.BoxSizing == YogaBoxSizing.BorderBox)
            return value;

        float paddingAndBorder = _style.ComputePaddingAndBorderForDimension(direction, dimension, ownerWidth);
        float pb = YogaFloat.IsDefined(paddingAndBorder) ? paddingAndBorder : 0;
        return YogaFloat.IsDefined(value) ? value + pb : value;
    }

    internal YogaValue ProcessFlexBasis()
    {
        var flexBasis = _style.FlexBasis;
        if (!flexBasis.IsAuto && !flexBasis.IsUndefined)
            return flexBasis;
        if (YogaFloat.IsDefined(_style.Flex) && _style.Flex > 0)
            return _config.UseWebDefaults ? YogaValue.Auto : YogaValue.Zero;
        return YogaValue.Auto;
    }

    internal float ResolveFlexBasis(FlexLayoutDirection direction, FlexDirection flexDirection, float referenceLength, float ownerWidth)
    {
        float value = ProcessFlexBasis().Resolve(referenceLength);
        if (_style.BoxSizing == YogaBoxSizing.BorderBox)
            return value;

        var dim = FlexDirectionHelper.Dimension(flexDirection);
        float paddingAndBorder = _style.ComputePaddingAndBorderForDimension(direction, dim, ownerWidth);
        float pb = YogaFloat.IsDefined(paddingAndBorder) ? paddingAndBorder : 0;
        return YogaFloat.IsDefined(value) ? value + pb : value;
    }

    internal void ProcessDimensions()
    {
        for (int d = 0; d < 2; d++)
        {
            var dim = (YogaDimension)d;
            var maxDim = _style.MaxDimensions[(int)dim];
            var minDim = _style.MinDimensions[(int)dim];
            if (maxDim.IsDefined && YogaFloat.InexactEquals(maxDim.Value, minDim.Value) && maxDim.Unit == minDim.Unit)
            {
                ProcessedDimensions[(int)dim] = maxDim;
            }
            else
            {
                ProcessedDimensions[(int)dim] = _style.Dimensions[(int)dim];
            }
        }
    }

    internal FlexLayoutDirection ResolveDirection(FlexLayoutDirection ownerDirection)
    {
        if (_style.Direction == FlexLayoutDirection.Inherit)
            return ownerDirection != FlexLayoutDirection.Inherit ? ownerDirection : FlexLayoutDirection.LTR;
        return _style.Direction;
    }

    internal float ResolveFlexGrow()
    {
        if (_owner == null) return 0;
        if (YogaFloat.IsDefined(_style.FlexGrow))
            return _style.FlexGrow;
        if (YogaFloat.IsDefined(_style.Flex) && _style.Flex > 0)
            return _style.Flex;
        return YogaStyle.DefaultFlexGrow;
    }

    internal float ResolveFlexShrink()
    {
        if (_owner == null) return 0;
        if (YogaFloat.IsDefined(_style.FlexShrink))
            return _style.FlexShrink;
        if (!_config.UseWebDefaults && YogaFloat.IsDefined(_style.Flex) && _style.Flex < 0)
            return -_style.Flex;
        return _config.UseWebDefaults ? YogaStyle.WebDefaultFlexShrink : YogaStyle.DefaultFlexShrink;
    }

    internal bool IsNodeFlexible()
        => _style.PositionType != FlexPositionType.Absolute
           && (ResolveFlexGrow() != 0 || ResolveFlexShrink() != 0);

    internal float RelativePosition(FlexDirection axis, FlexLayoutDirection direction, float axisSize)
    {
        if (_style.PositionType == FlexPositionType.Static)
            return 0;
        if (_style.IsInlineStartPositionDefined(axis, direction) &&
            !_style.IsInlineStartPositionAuto(axis, direction))
        {
            return _style.ComputeInlineStartPosition(axis, direction, axisSize);
        }
        return -1 * _style.ComputeInlineEndPosition(axis, direction, axisSize);
    }

    internal void SetPosition(FlexLayoutDirection direction, float ownerWidth, float ownerHeight)
    {
        var directionRespectingRoot = _owner != null ? direction : FlexLayoutDirection.LTR;
        var mainAxis = FlexDirectionHelper.ResolveDirection(_style.FlexDirection, directionRespectingRoot);
        var crossAxis = FlexDirectionHelper.ResolveCrossDirection(mainAxis, directionRespectingRoot);

        float relativePositionMain = RelativePosition(mainAxis, directionRespectingRoot,
            FlexDirectionHelper.IsRow(mainAxis) ? ownerWidth : ownerHeight);
        float relativePositionCross = RelativePosition(crossAxis, directionRespectingRoot,
            FlexDirectionHelper.IsRow(mainAxis) ? ownerHeight : ownerWidth);

        var mainAxisLeadingEdge = FlexDirectionHelper.InlineStartEdge(mainAxis, direction);
        var mainAxisTrailingEdge = FlexDirectionHelper.InlineEndEdge(mainAxis, direction);
        var crossAxisLeadingEdge = FlexDirectionHelper.InlineStartEdge(crossAxis, direction);
        var crossAxisTrailingEdge = FlexDirectionHelper.InlineEndEdge(crossAxis, direction);

        Layout.SetPosition(mainAxisLeadingEdge,
            _style.ComputeInlineStartMargin(mainAxis, direction, ownerWidth) + relativePositionMain);
        Layout.SetPosition(mainAxisTrailingEdge,
            _style.ComputeInlineEndMargin(mainAxis, direction, ownerWidth) + relativePositionMain);
        Layout.SetPosition(crossAxisLeadingEdge,
            _style.ComputeInlineStartMargin(crossAxis, direction, ownerWidth) + relativePositionCross);
        Layout.SetPosition(crossAxisTrailingEdge,
            _style.ComputeInlineEndMargin(crossAxis, direction, ownerWidth) + relativePositionCross);
    }

    // ── Layout result setters (used by algorithm) ──

    internal void SetLayoutDirection(FlexLayoutDirection direction) => Layout.Direction = direction;
    internal void SetLayoutMargin(float margin, YogaPhysicalEdge edge) => Layout.SetMargin(edge, margin);
    internal void SetLayoutBorder(float border, YogaPhysicalEdge edge) => Layout.SetBorder(edge, border);
    internal void SetLayoutPadding(float padding, YogaPhysicalEdge edge) => Layout.SetPadding(edge, padding);
    internal void SetLayoutPosition(float position, YogaPhysicalEdge edge) => Layout.SetPosition(edge, position);
    internal void SetLayoutMeasuredDimension(float measuredDimension, YogaDimension dimension) => Layout.SetMeasuredDimension(dimension, measuredDimension);
    internal void SetLayoutHadOverflow(bool hadOverflow) => Layout.HadOverflow = hadOverflow;
    internal void SetLayoutLastOwnerDirection(FlexLayoutDirection direction) => Layout.LastOwnerDirection = direction;
    internal void SetLayoutComputedFlexBasis(float value) => Layout.ComputedFlexBasis = value;
    internal void SetLayoutComputedFlexBasisGeneration(uint gen) => Layout.ComputedFlexBasisGeneration = gen;

    internal void SetLayoutDimension(float lengthValue, YogaDimension dimension)
    {
        Layout.SetDimension(dimension, lengthValue);
        Layout.SetRawDimension(dimension, lengthValue);
    }

    // ── Public layout entry point ──

    /// <summary>
    /// Calculate the layout for this node and all its children.
    /// </summary>
    public void CalculateLayout(float availableWidth = float.NaN, float availableHeight = float.NaN, FlexLayoutDirection direction = FlexLayoutDirection.LTR)
    {
        YogaAlgorithm.CalculateLayout(this, availableWidth, availableHeight, direction);
    }
}
