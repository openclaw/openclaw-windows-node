// C# port of Meta's Yoga layout engine algorithm utilities.
// Ported from yoga/algorithm/BoundAxis.h, Align.h, TrailingPosition.h, SizingMode.h,
// Cache.cpp, Baseline.cpp, FlexLine.h/cpp, PixelGrid.cpp
//
// AI-HINT: Utility statics consumed by YogaAlgorithm.cs. Key classes:
//   SizingModeHelper — converts SizingMode ↔ YogaMeasureMode
//   AlignHelper      — resolves alignment fallbacks (Auto → parent, Baseline → FlexStart)
//   BoundAxisHelper   — clamps a dimension by min/max + padding+border
//   TrailingPositionHelper — sets reverse-direction edge positions
//   BaselineHelper    — recursive baseline search (first baseline-aligned child)
//   PixelGridHelper   — DPI-aware rounding (text nodes round differently)
//   CacheHelper       — validates cached measurements to skip re-layout
//   FlexLineHelper    — builds FlexLine by accumulating children until wrap threshold

using OpenClawTray.Infrastructure.Layout;

namespace OpenClawTray.Infrastructure.Layout;

/// <summary>
/// Sizing mode conversion between internal SizingMode and public MeasureMode.
/// </summary>
internal static class SizingModeHelper
{
    public static YogaMeasureMode ToMeasureMode(SizingMode mode) => mode switch
    {
        SizingMode.StretchFit => YogaMeasureMode.Exactly,
        SizingMode.MaxContent => YogaMeasureMode.Undefined,
        SizingMode.FitContent => YogaMeasureMode.AtMost,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static SizingMode FromMeasureMode(YogaMeasureMode mode) => mode switch
    {
        YogaMeasureMode.Exactly => SizingMode.StretchFit,
        YogaMeasureMode.Undefined => SizingMode.MaxContent,
        YogaMeasureMode.AtMost => SizingMode.FitContent,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}

/// <summary>
/// Alignment resolution and fallback utilities.
/// </summary>
internal static class AlignHelper
{
    public static FlexAlign ResolveChildAlignment(YogaNode node, YogaNode child)
    {
        var align = child.Style.AlignSelf == FlexAlign.Auto
            ? node.Style.AlignItems
            : child.Style.AlignSelf;

        if (node.Style.Display == YogaDisplay.Flex && align == FlexAlign.Baseline &&
            FlexDirectionHelper.IsColumn(node.Style.FlexDirection))
        {
            return FlexAlign.FlexStart;
        }
        return align;
    }

    public static FlexAlign FallbackAlignment(FlexAlign align) => align switch
    {
        FlexAlign.SpaceBetween or FlexAlign.Stretch => FlexAlign.FlexStart,
        FlexAlign.SpaceAround or FlexAlign.SpaceEvenly => FlexAlign.FlexStart,
        _ => align,
    };

    // NOTE: C++ has a TODO for justify-content: stretch support.
    // Stretch is deliberately NOT included in the fallback here to match C++ behavior.
    public static FlexJustify FallbackAlignment(FlexJustify align) => align switch
    {
        FlexJustify.SpaceBetween => FlexJustify.FlexStart,
        FlexJustify.SpaceAround or FlexJustify.SpaceEvenly => FlexJustify.FlexStart,
        _ => align,
    };

    /// <summary>
    /// Resolves a child's justify-self value, falling back to the parent's justify-items
    /// when the child specifies Auto. Used for grid containers.
    /// Ported from C++ Align.h resolveChildJustification().
    /// </summary>
    public static FlexJustify ResolveChildJustification(YogaNode parent, YogaNode child)
    {
        return child.Style.JustifySelf == FlexJustify.Auto
            ? parent.Style.JustifyItems
            : child.Style.JustifySelf;
    }
}

/// <summary>
/// Axis bounding utilities (min/max constraints, padding+border).
/// </summary>
internal static class BoundAxisHelper
{
    public static float PaddingAndBorderForAxis(YogaNode node, FlexDirection axis, FlexLayoutDirection direction, float widthSize)
        => node.Style.ComputeInlineStartPaddingAndBorder(axis, direction, widthSize)
         + node.Style.ComputeInlineEndPaddingAndBorder(axis, direction, widthSize);

    public static float BoundAxisWithinMinAndMax(
        YogaNode node, FlexLayoutDirection direction, FlexDirection axis,
        float value, float axisSize, float widthSize)
    {
        float min, max;
        if (FlexDirectionHelper.IsColumn(axis))
        {
            min = node.Style.ResolvedMinDimension(direction, YogaDimension.Height, axisSize, widthSize);
            max = node.Style.ResolvedMaxDimension(direction, YogaDimension.Height, axisSize, widthSize);
        }
        else
        {
            min = node.Style.ResolvedMinDimension(direction, YogaDimension.Width, axisSize, widthSize);
            max = node.Style.ResolvedMaxDimension(direction, YogaDimension.Width, axisSize, widthSize);
        }

        if (YogaFloat.IsDefined(max) && max >= 0 && value > max)
            return max;
        if (YogaFloat.IsDefined(min) && min >= 0 && value < min)
            return min;
        return value;
    }

    public static float BoundAxis(
        YogaNode node, FlexDirection axis, FlexLayoutDirection direction,
        float value, float axisSize, float widthSize)
    {
        return YogaFloat.MaxOrDefined(
            BoundAxisWithinMinAndMax(node, direction, axis, value, axisSize, widthSize),
            PaddingAndBorderForAxis(node, axis, direction, widthSize));
    }
}

/// <summary>
/// Trailing position utilities for reverse flex directions.
/// </summary>
internal static class TrailingPositionHelper
{
    public static float GetPositionOfOppositeEdge(float position, FlexDirection axis,
        YogaNode containingNode, YogaNode node)
    {
        return containingNode.Layout.GetMeasuredDimension(FlexDirectionHelper.Dimension(axis))
             - node.Layout.GetMeasuredDimension(FlexDirectionHelper.Dimension(axis))
             - position;
    }

    public static void SetChildTrailingPosition(YogaNode node, YogaNode child, FlexDirection axis)
    {
        child.SetLayoutPosition(
            GetPositionOfOppositeEdge(
                child.Layout.GetPosition(FlexDirectionHelper.FlexStartEdge(axis)), axis, node, child),
            FlexDirectionHelper.FlexEndEdge(axis));
    }

    public static bool NeedsTrailingPosition(FlexDirection axis)
        => axis == FlexDirection.RowReverse || axis == FlexDirection.ColumnReverse;
}

/// <summary>
/// Baseline calculation utilities.
/// </summary>
internal static class BaselineHelper
{
    public static float CalculateBaseline(YogaNode node)
    {
        if (node.HasBaselineFunc)
        {
            float result = node.Baseline(
                node.Layout.GetMeasuredDimension(YogaDimension.Width),
                node.Layout.GetMeasuredDimension(YogaDimension.Height));
            if (float.IsNaN(result))
                throw new InvalidOperationException("Baseline function returned NaN.");
            return result;
        }

        YogaNode? baselineChild = null;
        foreach (var child in node.GetLayoutChildren())
        {
            if (child.LineIndex > 0) break;
            if (child.Style.PositionType == FlexPositionType.Absolute) continue;
            if (AlignHelper.ResolveChildAlignment(node, child) == FlexAlign.Baseline ||
                child.IsReferenceBaseline)
            {
                baselineChild = child;
                break;
            }
            baselineChild ??= child;
        }

        if (baselineChild == null)
            return node.Layout.GetMeasuredDimension(YogaDimension.Height);

        float baseline = CalculateBaseline(baselineChild);
        return baseline + baselineChild.Layout.GetPosition(YogaPhysicalEdge.Top);
    }

    public static bool IsBaselineLayout(YogaNode node)
    {
        if (FlexDirectionHelper.IsColumn(node.Style.FlexDirection))
            return false;
        if (node.Style.AlignItems == FlexAlign.Baseline)
            return true;
        foreach (var child in node.GetLayoutChildren())
        {
            if (child.Style.PositionType != FlexPositionType.Absolute &&
                child.Style.AlignSelf == FlexAlign.Baseline)
                return true;
        }
        return false;
    }
}

/// <summary>
/// Pixel grid rounding for DPI scaling.
/// </summary>
internal static class PixelGridHelper
{
    public static float RoundValueToPixelGrid(double value, double pointScaleFactor, bool forceCeil, bool forceFloor)
    {
        double scaledValue = value * pointScaleFactor;
        double fractial = scaledValue % 1.0;
        if (fractial < 0) fractial += 1.0;

        if (InexactEquals(fractial, 0))
            scaledValue -= fractial;
        else if (InexactEquals(fractial, 1.0))
            scaledValue = scaledValue - fractial + 1.0;
        else if (forceCeil)
            scaledValue = scaledValue - fractial + 1.0;
        else if (forceFloor)
            scaledValue -= fractial;
        else
            scaledValue = scaledValue - fractial +
                (!double.IsNaN(fractial) && (fractial > 0.5 || InexactEquals(fractial, 0.5)) ? 1.0 : 0.0);

        return (double.IsNaN(scaledValue) || double.IsNaN(pointScaleFactor))
            ? float.NaN
            : (float)(scaledValue / pointScaleFactor);
    }

    public static void RoundLayoutResultsToPixelGrid(YogaNode node, double absoluteLeft, double absoluteTop)
    {
        double pointScaleFactor = node.Config.PointScaleFactor;

        double nodeLeft = node.Layout.GetPosition(YogaPhysicalEdge.Left);
        double nodeTop = node.Layout.GetPosition(YogaPhysicalEdge.Top);
        double nodeWidth = node.Layout.GetDimension(YogaDimension.Width);
        double nodeHeight = node.Layout.GetDimension(YogaDimension.Height);

        double absoluteNodeLeft = absoluteLeft + nodeLeft;
        double absoluteNodeTop = absoluteTop + nodeTop;
        double absoluteNodeRight = absoluteNodeLeft + nodeWidth;
        double absoluteNodeBottom = absoluteNodeTop + nodeHeight;

        if (pointScaleFactor != 0)
        {
            bool textRounding = node.NodeType == YogaNodeType.Text;

            node.SetLayoutPosition(
                RoundValueToPixelGrid(nodeLeft, pointScaleFactor, false, textRounding),
                YogaPhysicalEdge.Left);
            node.SetLayoutPosition(
                RoundValueToPixelGrid(nodeTop, pointScaleFactor, false, textRounding),
                YogaPhysicalEdge.Top);

            double scaledNodeWidth = nodeWidth * pointScaleFactor;
            bool hasFractionalWidth = !InexactEquals(Math.Round(scaledNodeWidth), scaledNodeWidth);

            double scaledNodeHeight = nodeHeight * pointScaleFactor;
            bool hasFractionalHeight = !InexactEquals(Math.Round(scaledNodeHeight), scaledNodeHeight);

            node.Layout.SetDimension(YogaDimension.Width,
                RoundValueToPixelGrid(absoluteNodeRight, pointScaleFactor, textRounding && hasFractionalWidth, textRounding && !hasFractionalWidth)
                - RoundValueToPixelGrid(absoluteNodeLeft, pointScaleFactor, false, textRounding));

            node.Layout.SetDimension(YogaDimension.Height,
                RoundValueToPixelGrid(absoluteNodeBottom, pointScaleFactor, textRounding && hasFractionalHeight, textRounding && !hasFractionalHeight)
                - RoundValueToPixelGrid(absoluteNodeTop, pointScaleFactor, false, textRounding));
        }

        foreach (var child in node.Children)
        {
            RoundLayoutResultsToPixelGrid(child, absoluteNodeLeft, absoluteNodeTop);
        }
    }

    private static bool InexactEquals(double a, double b)
        => Math.Abs(a - b) < 0.0001;
}

/// <summary>
/// Measurement cache validation.
/// </summary>
internal static class CacheHelper
{
    public static bool CanUseCachedMeasurement(
        SizingMode widthMode, float availableWidth,
        SizingMode heightMode, float availableHeight,
        SizingMode lastWidthMode, float lastAvailableWidth,
        SizingMode lastHeightMode, float lastAvailableHeight,
        float lastComputedWidth, float lastComputedHeight,
        float marginRow, float marginColumn,
        YogaConfig config)
    {
        if ((YogaFloat.IsDefined(lastComputedHeight) && lastComputedHeight < 0) ||
            (YogaFloat.IsDefined(lastComputedWidth) && lastComputedWidth < 0))
            return false;

        float pointScaleFactor = config?.PointScaleFactor ?? 0;
        bool useRoundedComparison = config != null && pointScaleFactor != 0;

        float effectiveWidth = useRoundedComparison
            ? PixelGridHelper.RoundValueToPixelGrid(availableWidth, pointScaleFactor, false, false)
            : availableWidth;
        float effectiveHeight = useRoundedComparison
            ? PixelGridHelper.RoundValueToPixelGrid(availableHeight, pointScaleFactor, false, false)
            : availableHeight;
        float effectiveLastWidth = useRoundedComparison
            ? PixelGridHelper.RoundValueToPixelGrid(lastAvailableWidth, pointScaleFactor, false, false)
            : lastAvailableWidth;
        float effectiveLastHeight = useRoundedComparison
            ? PixelGridHelper.RoundValueToPixelGrid(lastAvailableHeight, pointScaleFactor, false, false)
            : lastAvailableHeight;

        bool hasSameWidthSpec = lastWidthMode == widthMode && YogaFloat.InexactEquals(effectiveLastWidth, effectiveWidth);
        bool hasSameHeightSpec = lastHeightMode == heightMode && YogaFloat.InexactEquals(effectiveLastHeight, effectiveHeight);

        bool widthIsCompatible = hasSameWidthSpec ||
            SizeIsExactAndMatchesOldMeasuredSize(widthMode, availableWidth - marginRow, lastComputedWidth) ||
            OldSizeIsMaxContentAndStillFits(widthMode, availableWidth - marginRow, lastWidthMode, lastComputedWidth) ||
            NewSizeIsStricterAndStillValid(widthMode, availableWidth - marginRow, lastWidthMode, lastAvailableWidth, lastComputedWidth);

        bool heightIsCompatible = hasSameHeightSpec ||
            SizeIsExactAndMatchesOldMeasuredSize(heightMode, availableHeight - marginColumn, lastComputedHeight) ||
            OldSizeIsMaxContentAndStillFits(heightMode, availableHeight - marginColumn, lastHeightMode, lastComputedHeight) ||
            NewSizeIsStricterAndStillValid(heightMode, availableHeight - marginColumn, lastHeightMode, lastAvailableHeight, lastComputedHeight);

        return widthIsCompatible && heightIsCompatible;
    }

    private static bool SizeIsExactAndMatchesOldMeasuredSize(SizingMode sizeMode, float size, float lastComputedSize)
        => sizeMode == SizingMode.StretchFit && YogaFloat.InexactEquals(size, lastComputedSize);

    private static bool OldSizeIsMaxContentAndStillFits(SizingMode sizeMode, float size, SizingMode lastSizeMode, float lastComputedSize)
        => sizeMode == SizingMode.FitContent && lastSizeMode == SizingMode.MaxContent &&
           (size >= lastComputedSize || YogaFloat.InexactEquals(size, lastComputedSize));

    private static bool NewSizeIsStricterAndStillValid(SizingMode sizeMode, float size, SizingMode lastSizeMode, float lastSize, float lastComputedSize)
        => lastSizeMode == SizingMode.FitContent && sizeMode == SizingMode.FitContent &&
           YogaFloat.IsDefined(lastSize) && YogaFloat.IsDefined(size) && YogaFloat.IsDefined(lastComputedSize) &&
           lastSize > size && (lastComputedSize <= size || YogaFloat.InexactEquals(size, lastComputedSize));
}

/// <summary>
/// Flex line data structures and calculation.
/// </summary>
internal struct FlexLineRunningLayout
{
    public float TotalFlexGrowFactors;
    public float TotalFlexShrinkScaledFactors;
    public float RemainingFreeSpace;
    public float MainDim;
    public float CrossDim;
}

internal sealed class FlexLine
{
    public List<YogaNode> ItemsInFlow = new();
    public float SizeConsumed;
    public int NumberOfAutoMargins;
    public FlexLineRunningLayout Layout;
}

internal static class FlexLineHelper
{
    [ThreadStatic]
    private static Stack<List<YogaNode>>? s_listPool;

    internal static List<YogaNode> RentList()
    {
        s_listPool ??= new Stack<List<YogaNode>>();
        return s_listPool.Count > 0 ? s_listPool.Pop() : new List<YogaNode>();
    }

    internal static void ReturnList(List<YogaNode> list)
    {
        list.Clear();
        s_listPool ??= new Stack<List<YogaNode>>();
        s_listPool.Push(list);
    }

    /// <summary>
    /// Calculates where a line starting at a given child index should break.
    /// Assumes all children have their computedFlexBasis computed.
    /// </summary>
    public static FlexLine CalculateFlexLine(
        YogaNode node, FlexLayoutDirection ownerDirection, float ownerWidth,
        float mainAxisOwnerSize, float availableInnerWidth, float availableInnerMainDim,
        ref int childIndex, int lineCount,
        List<YogaNode> layoutChildren)
    {
        var itemsInFlow = RentList();
        float sizeConsumed = 0;
        float totalFlexGrowFactors = 0;
        float totalFlexShrinkScaledFactors = 0;
        int numberOfAutoMargins = 0;
        YogaNode? firstElementInLine = null;

        float sizeConsumedIncludingMinConstraint = 0;
        var direction = node.ResolveDirection(ownerDirection);
        var mainAxis = FlexDirectionHelper.ResolveDirection(node.Style.FlexDirection, direction);
        bool isNodeFlexWrap = node.Style.FlexWrap != FlexWrap.NoWrap;
        float gap = node.Style.ComputeGapForAxis(mainAxis, availableInnerMainDim);

        for (; childIndex < layoutChildren.Count; childIndex++)
        {
            var child = layoutChildren[childIndex];
            if (child.Style.Display == YogaDisplay.None ||
                child.Style.PositionType == FlexPositionType.Absolute)
                continue;

            firstElementInLine ??= child;

            if (child.Style.FlexStartMarginIsAuto(mainAxis, ownerDirection))
                numberOfAutoMargins++;
            if (child.Style.FlexEndMarginIsAuto(mainAxis, ownerDirection))
                numberOfAutoMargins++;

            child.LineIndex = lineCount;
            float childMarginMainAxis = child.Style.ComputeMarginForAxis(mainAxis, availableInnerWidth);
            float childLeadingGapMainAxis = child == firstElementInLine ? 0 : gap;
            float flexBasisWithMinAndMaxConstraints = BoundAxisHelper.BoundAxisWithinMinAndMax(
                child, direction, mainAxis,
                child.Layout.ComputedFlexBasis,
                mainAxisOwnerSize, ownerWidth);
            // Unwrap NaN to 0 for constraint check
            if (YogaFloat.IsUndefined(flexBasisWithMinAndMaxConstraints))
                flexBasisWithMinAndMaxConstraints = 0;

            if (sizeConsumedIncludingMinConstraint + flexBasisWithMinAndMaxConstraints +
                    childMarginMainAxis + childLeadingGapMainAxis > availableInnerMainDim &&
                isNodeFlexWrap && itemsInFlow.Count > 0)
            {
                break;
            }

            sizeConsumedIncludingMinConstraint += flexBasisWithMinAndMaxConstraints + childMarginMainAxis + childLeadingGapMainAxis;
            sizeConsumed += flexBasisWithMinAndMaxConstraints + childMarginMainAxis + childLeadingGapMainAxis;

            if (child.IsNodeFlexible())
            {
                totalFlexGrowFactors += child.ResolveFlexGrow();
                totalFlexShrinkScaledFactors += -child.ResolveFlexShrink() *
                    YogaFloat.UnwrapOrDefault(child.Layout.ComputedFlexBasis);
            }

            itemsInFlow.Add(child);
        }

        // Floor flex factors to 1
        if (totalFlexGrowFactors > 0 && totalFlexGrowFactors < 1)
            totalFlexGrowFactors = 1;
        if (totalFlexShrinkScaledFactors > 0 && totalFlexShrinkScaledFactors < 1)
            totalFlexShrinkScaledFactors = 1;

        return new FlexLine
        {
            ItemsInFlow = itemsInFlow,
            SizeConsumed = sizeConsumed,
            NumberOfAutoMargins = numberOfAutoMargins,
            Layout = new FlexLineRunningLayout
            {
                TotalFlexGrowFactors = totalFlexGrowFactors,
                TotalFlexShrinkScaledFactors = totalFlexShrinkScaledFactors,
            }
        };
    }
}
