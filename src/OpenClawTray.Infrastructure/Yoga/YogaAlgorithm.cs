// C# port of Meta's Yoga layout engine main algorithm.
// Ported from yoga/algorithm/CalculateLayout.cpp and yoga/algorithm/AbsoluteLayout.cpp
//
// AI-HINT: This is a faithful C# translation of Meta's Yoga flexbox layout engine.
// Method names map 1:1 to the C++ original (e.g. CalculateLayoutImpl = calculateLayoutImpl).
// The algorithm follows CSS Flexbox spec: resolve sizes → compute flex basis → distribute
// free space (two-pass) → position children on main+cross axes → recurse for absolutes.
// Key flow: CalculateLayout → CalculateLayoutInternal (cache check) → CalculateLayoutImpl
//   → ComputeFlexBasisForChildren → ResolveFlexibleLength (DistributeFreeSpaceFirstPass +
//     DistributeFreeSpaceSecondPass) → JustifyMainAxis → cross-axis alignment → absolute positioning.
// SizingMode maps to CSS sizing: StretchFit=definite, FitContent=max-content-clamped, MaxContent=intrinsic.
// All float math uses YogaFloat helpers (NaN = undefined, InexactEquals for float tolerance).

using System.Threading;
using OpenClawTray.Infrastructure.Layout;

namespace OpenClawTray.Infrastructure.Layout;

/// <summary>
/// Pure-static Yoga flexbox layout algorithm. Entry point: CalculateLayout().
/// Mutates YogaNode.Layout in-place. Thread-unsafe (uses static generation counter).
/// </summary>
internal static class YogaAlgorithm
{
    private static uint s_currentGenerationCount;

    // ──────────────────────────────────────────────────────────────────────
    // Public entry point
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Entry point. Resolves root sizing mode, runs layout, applies pixel-grid rounding.
    /// </summary>
    public static void CalculateLayout(
        YogaNode node, float availableWidth, float availableHeight, FlexLayoutDirection ownerDirection)
    {
        Interlocked.Increment(ref s_currentGenerationCount);
        node.ProcessDimensions();
        var direction = node.ResolveDirection(ownerDirection);

        float width = float.NaN;
        var widthSizingMode = SizingMode.MaxContent;
        var style = node.Style;

        if (node.HasDefiniteLength(YogaDimension.Width, availableWidth))
        {
            width = node.GetResolvedDimension(direction, FlexDirectionHelper.Dimension(FlexDirection.Row), availableWidth, availableWidth)
                  + node.Style.ComputeMarginForAxis(FlexDirection.Row, availableWidth);
            widthSizingMode = SizingMode.StretchFit;
        }
        else if (YogaFloat.IsDefined(style.ResolvedMaxDimension(direction, YogaDimension.Width, availableWidth, availableWidth)))
        {
            width = style.ResolvedMaxDimension(direction, YogaDimension.Width, availableWidth, availableWidth);
            widthSizingMode = SizingMode.FitContent;
        }
        else
        {
            width = availableWidth;
            widthSizingMode = YogaFloat.IsUndefined(width) ? SizingMode.MaxContent : SizingMode.StretchFit;
        }

        float height = float.NaN;
        var heightSizingMode = SizingMode.MaxContent;

        if (node.HasDefiniteLength(YogaDimension.Height, availableHeight))
        {
            height = node.GetResolvedDimension(direction, FlexDirectionHelper.Dimension(FlexDirection.Column), availableHeight, availableWidth)
                   + node.Style.ComputeMarginForAxis(FlexDirection.Column, availableWidth);
            heightSizingMode = SizingMode.StretchFit;
        }
        else if (YogaFloat.IsDefined(style.ResolvedMaxDimension(direction, YogaDimension.Height, availableHeight, availableWidth)))
        {
            height = style.ResolvedMaxDimension(direction, YogaDimension.Height, availableHeight, availableWidth);
            heightSizingMode = SizingMode.FitContent;
        }
        else
        {
            height = availableHeight;
            heightSizingMode = YogaFloat.IsUndefined(height) ? SizingMode.MaxContent : SizingMode.StretchFit;
        }

        if (CalculateLayoutInternal(
                node, width, height, ownerDirection,
                widthSizingMode, heightSizingMode,
                availableWidth, availableHeight,
                true, 0, s_currentGenerationCount))
        {
            node.SetPosition(node.Layout.Direction, availableWidth, availableHeight);
            PixelGridHelper.RoundLayoutResultsToPixelGrid(node, 0.0f, 0.0f);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // calculateLayoutInternal — cache wrapper
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cache wrapper: checks if previous layout results are reusable before running full algo.
    /// performLayout=false is a measure-only pass (no position assignment).
    /// </summary>
    private static bool CalculateLayoutInternal(
        YogaNode node, float availableWidth, float availableHeight,
        FlexLayoutDirection ownerDirection,
        SizingMode widthSizingMode, SizingMode heightSizingMode,
        float ownerWidth, float ownerHeight,
        bool performLayout,
        uint depth, uint generationCount)
    {
        var layout = node.Layout;
        depth++;

        bool needToVisitNode =
            (node.IsDirty && layout.GenerationCount != generationCount) ||
            layout.ConfigVersion != node.Config.Version ||
            layout.LastOwnerDirection != ownerDirection;

        if (needToVisitNode)
        {
            layout.NextCachedMeasurementsIndex = 0;
            layout.CachedLayout.AvailableWidth = -1;
            layout.CachedLayout.AvailableHeight = -1;
            layout.CachedLayout.WidthSizingMode = SizingMode.MaxContent;
            layout.CachedLayout.HeightSizingMode = SizingMode.MaxContent;
            layout.CachedLayout.ComputedWidth = -1;
            layout.CachedLayout.ComputedHeight = -1;
        }

        CachedMeasurement? cachedResults = null;
        int cachedIndex = -1; // -1 = cachedLayout, >= 0 = cachedMeasurements index

        if (node.HasMeasureFunc)
        {
            float marginAxisRow = node.Style.ComputeMarginForAxis(FlexDirection.Row, ownerWidth);
            float marginAxisColumn = node.Style.ComputeMarginForAxis(FlexDirection.Column, ownerWidth);

            if (CacheHelper.CanUseCachedMeasurement(
                    widthSizingMode, availableWidth, heightSizingMode, availableHeight,
                    layout.CachedLayout.WidthSizingMode, layout.CachedLayout.AvailableWidth,
                    layout.CachedLayout.HeightSizingMode, layout.CachedLayout.AvailableHeight,
                    layout.CachedLayout.ComputedWidth, layout.CachedLayout.ComputedHeight,
                    marginAxisRow, marginAxisColumn, node.Config))
            {
                cachedResults = layout.CachedLayout;
                cachedIndex = -1;
            }
            else
            {
                for (int i = 0; i < (int)layout.NextCachedMeasurementsIndex; i++)
                {
                    if (CacheHelper.CanUseCachedMeasurement(
                            widthSizingMode, availableWidth, heightSizingMode, availableHeight,
                            layout.CachedMeasurements[i].WidthSizingMode, layout.CachedMeasurements[i].AvailableWidth,
                            layout.CachedMeasurements[i].HeightSizingMode, layout.CachedMeasurements[i].AvailableHeight,
                            layout.CachedMeasurements[i].ComputedWidth, layout.CachedMeasurements[i].ComputedHeight,
                            marginAxisRow, marginAxisColumn, node.Config))
                    {
                        cachedResults = layout.CachedMeasurements[i];
                        cachedIndex = i;
                        break;
                    }
                }
            }
        }
        else if (performLayout)
        {
            if (YogaFloat.InexactEquals(layout.CachedLayout.AvailableWidth, availableWidth) &&
                YogaFloat.InexactEquals(layout.CachedLayout.AvailableHeight, availableHeight) &&
                layout.CachedLayout.WidthSizingMode == widthSizingMode &&
                layout.CachedLayout.HeightSizingMode == heightSizingMode)
            {
                cachedResults = layout.CachedLayout;
                cachedIndex = -1;
            }
        }
        else
        {
            for (int i = 0; i < (int)layout.NextCachedMeasurementsIndex; i++)
            {
                if (YogaFloat.InexactEquals(layout.CachedMeasurements[i].AvailableWidth, availableWidth) &&
                    YogaFloat.InexactEquals(layout.CachedMeasurements[i].AvailableHeight, availableHeight) &&
                    layout.CachedMeasurements[i].WidthSizingMode == widthSizingMode &&
                    layout.CachedMeasurements[i].HeightSizingMode == heightSizingMode)
                {
                    cachedResults = layout.CachedMeasurements[i];
                    cachedIndex = i;
                    break;
                }
            }
        }

        if (!needToVisitNode && cachedResults != null)
        {
            layout.SetMeasuredDimension(YogaDimension.Width, cachedResults.Value.ComputedWidth);
            layout.SetMeasuredDimension(YogaDimension.Height, cachedResults.Value.ComputedHeight);
        }
        else
        {
            CalculateLayoutImpl(
                node, availableWidth, availableHeight, ownerDirection,
                widthSizingMode, heightSizingMode,
                ownerWidth, ownerHeight,
                performLayout, depth, generationCount);

            layout.LastOwnerDirection = ownerDirection;
            layout.ConfigVersion = node.Config.Version;

            if (cachedResults == null)
            {
                if (layout.NextCachedMeasurementsIndex == LayoutResults.MaxCachedMeasurements)
                    layout.NextCachedMeasurementsIndex = 0;

                if (performLayout)
                {
                    layout.CachedLayout.AvailableWidth = availableWidth;
                    layout.CachedLayout.AvailableHeight = availableHeight;
                    layout.CachedLayout.WidthSizingMode = widthSizingMode;
                    layout.CachedLayout.HeightSizingMode = heightSizingMode;
                    layout.CachedLayout.ComputedWidth = layout.GetMeasuredDimension(YogaDimension.Width);
                    layout.CachedLayout.ComputedHeight = layout.GetMeasuredDimension(YogaDimension.Height);
                }
                else
                {
                    int idx = (int)layout.NextCachedMeasurementsIndex;
                    layout.CachedMeasurements[idx].AvailableWidth = availableWidth;
                    layout.CachedMeasurements[idx].AvailableHeight = availableHeight;
                    layout.CachedMeasurements[idx].WidthSizingMode = widthSizingMode;
                    layout.CachedMeasurements[idx].HeightSizingMode = heightSizingMode;
                    layout.CachedMeasurements[idx].ComputedWidth = layout.GetMeasuredDimension(YogaDimension.Width);
                    layout.CachedMeasurements[idx].ComputedHeight = layout.GetMeasuredDimension(YogaDimension.Height);
                    layout.NextCachedMeasurementsIndex++;
                }
            }
        }

        if (performLayout)
        {
            node.SetLayoutDimension(
                node.Layout.GetMeasuredDimension(YogaDimension.Width), YogaDimension.Width);
            node.SetLayoutDimension(
                node.Layout.GetMeasuredDimension(YogaDimension.Height), YogaDimension.Height);
            node.HasNewLayout = true;
            node.SetDirty(false);
        }

        layout.GenerationCount = generationCount;
        return needToVisitNode || cachedResults == null;
    }

    // ──────────────────────────────────────────────────────────────────────
    // calculateLayoutImpl — the main algorithm (~600 lines, the core flexbox loop)
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Main flexbox algorithm. Handles: padding/border computation, flex basis for all children,
    /// multi-line wrapping, free space distribution, cross-axis alignment, and absolute positioning.
    /// </summary>
    private static void CalculateLayoutImpl(
        YogaNode node, float availableWidth, float availableHeight,
        FlexLayoutDirection ownerDirection,
        SizingMode widthSizingMode, SizingMode heightSizingMode,
        float ownerWidth, float ownerHeight,
        bool performLayout, uint depth, uint generationCount)
    {
        // Set the resolved direction in the node's layout.
        var direction = node.ResolveDirection(ownerDirection);
        node.SetLayoutDirection(direction);

        var flexRowDirection = FlexDirectionHelper.ResolveDirection(FlexDirection.Row, direction);
        var flexColumnDirection = FlexDirectionHelper.ResolveDirection(FlexDirection.Column, direction);

        var startEdge = direction == FlexLayoutDirection.LTR ? YogaPhysicalEdge.Left : YogaPhysicalEdge.Right;
        var endEdge = direction == FlexLayoutDirection.LTR ? YogaPhysicalEdge.Right : YogaPhysicalEdge.Left;

        float marginRowLeading = node.Style.ComputeInlineStartMargin(flexRowDirection, direction, ownerWidth);
        node.SetLayoutMargin(marginRowLeading, startEdge);
        float marginRowTrailing = node.Style.ComputeInlineEndMargin(flexRowDirection, direction, ownerWidth);
        node.SetLayoutMargin(marginRowTrailing, endEdge);
        float marginColumnLeading = node.Style.ComputeInlineStartMargin(flexColumnDirection, direction, ownerWidth);
        node.SetLayoutMargin(marginColumnLeading, YogaPhysicalEdge.Top);
        float marginColumnTrailing = node.Style.ComputeInlineEndMargin(flexColumnDirection, direction, ownerWidth);
        node.SetLayoutMargin(marginColumnTrailing, YogaPhysicalEdge.Bottom);

        float marginAxisRow = marginRowLeading + marginRowTrailing;
        float marginAxisColumn = marginColumnLeading + marginColumnTrailing;

        node.SetLayoutBorder(node.Style.ComputeInlineStartBorder(flexRowDirection, direction), startEdge);
        node.SetLayoutBorder(node.Style.ComputeInlineEndBorder(flexRowDirection, direction), endEdge);
        node.SetLayoutBorder(node.Style.ComputeInlineStartBorder(flexColumnDirection, direction), YogaPhysicalEdge.Top);
        node.SetLayoutBorder(node.Style.ComputeInlineEndBorder(flexColumnDirection, direction), YogaPhysicalEdge.Bottom);

        node.SetLayoutPadding(node.Style.ComputeInlineStartPadding(flexRowDirection, direction, ownerWidth), startEdge);
        node.SetLayoutPadding(node.Style.ComputeInlineEndPadding(flexRowDirection, direction, ownerWidth), endEdge);
        node.SetLayoutPadding(node.Style.ComputeInlineStartPadding(flexColumnDirection, direction, ownerWidth), YogaPhysicalEdge.Top);
        node.SetLayoutPadding(node.Style.ComputeInlineEndPadding(flexColumnDirection, direction, ownerWidth), YogaPhysicalEdge.Bottom);

        if (node.HasMeasureFunc)
        {
            MeasureNodeWithMeasureFunc(
                node, direction,
                availableWidth - marginAxisRow, availableHeight - marginAxisColumn,
                widthSizingMode, heightSizingMode,
                ownerWidth, ownerHeight);
            CleanupContentsNodesRecursively(node);
            return;
        }

        int childCount = node.GetLayoutChildCount();
        if (childCount == 0)
        {
            MeasureNodeWithoutChildren(
                node, direction,
                availableWidth - marginAxisRow, availableHeight - marginAxisColumn,
                widthSizingMode, heightSizingMode,
                ownerWidth, ownerHeight);
            CleanupContentsNodesRecursively(node);
            return;
        }

        // If we're not being asked to perform a full layout we can skip the algorithm
        // if we already know the size
        if (!performLayout &&
            MeasureNodeWithFixedSize(
                node, direction,
                availableWidth - marginAxisRow, availableHeight - marginAxisColumn,
                widthSizingMode, heightSizingMode,
                ownerWidth, ownerHeight))
        {
            CleanupContentsNodesRecursively(node);
            return;
        }

        // Reset layout flags
        node.SetLayoutHadOverflow(false);

        // Clean and update all display: contents nodes
        CleanupContentsNodesRecursively(node);

        // STEP 1: CALCULATE VALUES FOR REMAINDER OF ALGORITHM
        var mainAxis = FlexDirectionHelper.ResolveDirection(node.Style.FlexDirection, direction);
        var crossAxis = FlexDirectionHelper.ResolveCrossDirection(mainAxis, direction);
        bool isMainAxisRow = FlexDirectionHelper.IsRow(mainAxis);
        bool isNodeFlexWrap = node.Style.FlexWrap != FlexWrap.NoWrap;

        float mainAxisOwnerSize = isMainAxisRow ? ownerWidth : ownerHeight;
        float crossAxisOwnerSize = isMainAxisRow ? ownerHeight : ownerWidth;

        float paddingAndBorderAxisMain = BoundAxisHelper.PaddingAndBorderForAxis(node, mainAxis, direction, ownerWidth);
        float paddingAndBorderAxisCross = BoundAxisHelper.PaddingAndBorderForAxis(node, crossAxis, direction, ownerWidth);
        float leadingPaddingAndBorderCross = node.Style.ComputeFlexStartPaddingAndBorder(crossAxis, direction, ownerWidth);

        var sizingModeMainDim = isMainAxisRow ? widthSizingMode : heightSizingMode;
        var sizingModeCrossDim = isMainAxisRow ? heightSizingMode : widthSizingMode;

        float paddingAndBorderAxisRow = isMainAxisRow ? paddingAndBorderAxisMain : paddingAndBorderAxisCross;
        float paddingAndBorderAxisColumn = isMainAxisRow ? paddingAndBorderAxisCross : paddingAndBorderAxisMain;

        // STEP 2: DETERMINE AVAILABLE SIZE IN MAIN AND CROSS DIRECTIONS
        float availableInnerWidth = CalculateAvailableInnerDimension(
            node, direction, YogaDimension.Width,
            availableWidth - marginAxisRow, paddingAndBorderAxisRow, ownerWidth, ownerWidth);
        float availableInnerHeight = CalculateAvailableInnerDimension(
            node, direction, YogaDimension.Height,
            availableHeight - marginAxisColumn, paddingAndBorderAxisColumn, ownerHeight, ownerWidth);

        float availableInnerMainDim = isMainAxisRow ? availableInnerWidth : availableInnerHeight;
        float availableInnerCrossDim = isMainAxisRow ? availableInnerHeight : availableInnerWidth;

        // STEP 3: DETERMINE FLEX BASIS FOR EACH ITEM
        float ownerWidthForChildren = availableInnerWidth;
        float ownerHeightForChildren = availableInnerHeight;

        if (node.Config.IsExperimentalFeatureEnabled(YogaExperimentalFeature.FixFlexBasisFitContent))
        {
            var owner = node.Owner;
            bool isChildOfScrollContainer = owner != null && owner.Style.Overflow == YogaOverflow.Scroll;

            if (!isChildOfScrollContainer)
            {
                if (YogaFloat.IsUndefined(ownerWidthForChildren) && YogaFloat.IsDefined(ownerWidth))
                {
                    ownerWidthForChildren = CalculateAvailableInnerDimension(
                        node, direction, YogaDimension.Width,
                        ownerWidth - marginAxisRow, paddingAndBorderAxisRow, ownerWidth, ownerWidth);
                }
                if (YogaFloat.IsUndefined(ownerHeightForChildren) && YogaFloat.IsDefined(ownerHeight))
                {
                    ownerHeightForChildren = CalculateAvailableInnerDimension(
                        node, direction, YogaDimension.Height,
                        ownerHeight - marginAxisColumn, paddingAndBorderAxisColumn, ownerHeight, ownerWidth);
                }
            }
        }

        float totalMainDim = 0;
        totalMainDim += ComputeFlexBasisForChildren(
            node, availableInnerWidth, availableInnerHeight,
            ownerWidthForChildren, ownerHeightForChildren,
            widthSizingMode, heightSizingMode,
            direction, mainAxis, performLayout, depth, generationCount);

        if (childCount > 1)
        {
            totalMainDim += node.Style.ComputeGapForAxis(mainAxis, availableInnerMainDim) * (childCount - 1);
        }

        bool mainAxisOverflows =
            sizingModeMainDim != SizingMode.MaxContent && totalMainDim > availableInnerMainDim;

        if (isNodeFlexWrap && mainAxisOverflows && sizingModeMainDim == SizingMode.FitContent)
            sizingModeMainDim = SizingMode.StretchFit;

        // STEP 4: COLLECT FLEX ITEMS INTO FLEX LINES
        int startOfLineIndex = 0;
        int lineCount = 0;
        float totalLineCrossDim = 0;
        float crossAxisGap = node.Style.ComputeGapForAxis(crossAxis, availableInnerCrossDim);
        float maxLineMainDim = 0;

        // Build the list of layout children once for iteration
        var layoutChildren = new List<YogaNode>();
        node.CollectLayoutChildren(layoutChildren);

        while (startOfLineIndex < layoutChildren.Count)
        {
            int currentIndex = startOfLineIndex;
            var flexLine = FlexLineHelper.CalculateFlexLine(
                node, ownerDirection, ownerWidth,
                mainAxisOwnerSize, availableInnerWidth, availableInnerMainDim,
                ref currentIndex, lineCount,
                layoutChildren);
            startOfLineIndex = currentIndex;

            bool canSkipFlex = !performLayout && sizingModeCrossDim == SizingMode.StretchFit;

            // STEP 5: RESOLVING FLEXIBLE LENGTHS ON MAIN AXIS
            bool sizeBasedOnContent = false;
            if (sizingModeMainDim != SizingMode.StretchFit)
            {
                var nodeStyle = node.Style;
                float minInnerWidth = nodeStyle.ResolvedMinDimension(direction, YogaDimension.Width, ownerWidth, ownerWidth) - paddingAndBorderAxisRow;
                float maxInnerWidth = nodeStyle.ResolvedMaxDimension(direction, YogaDimension.Width, ownerWidth, ownerWidth) - paddingAndBorderAxisRow;
                float minInnerHeight = nodeStyle.ResolvedMinDimension(direction, YogaDimension.Height, ownerHeight, ownerWidth) - paddingAndBorderAxisColumn;
                float maxInnerHeight = nodeStyle.ResolvedMaxDimension(direction, YogaDimension.Height, ownerHeight, ownerWidth) - paddingAndBorderAxisColumn;

                float minInnerMainDim = isMainAxisRow ? minInnerWidth : minInnerHeight;
                float maxInnerMainDim = isMainAxisRow ? maxInnerWidth : maxInnerHeight;

                if (YogaFloat.IsDefined(minInnerMainDim) && flexLine.SizeConsumed < minInnerMainDim)
                {
                    availableInnerMainDim = minInnerMainDim;
                }
                else if (YogaFloat.IsDefined(maxInnerMainDim) && flexLine.SizeConsumed > maxInnerMainDim)
                {
                    availableInnerMainDim = maxInnerMainDim;
                }
                else
                {
                    bool useLegacyStretchBehaviour = node.HasErrata(YogaErrata.StretchFlexBasis);
                    if (!useLegacyStretchBehaviour &&
                        ((YogaFloat.IsDefined(flexLine.Layout.TotalFlexGrowFactors) &&
                          flexLine.Layout.TotalFlexGrowFactors == 0) ||
                         (YogaFloat.IsDefined(node.ResolveFlexGrow()) &&
                          node.ResolveFlexGrow() == 0)))
                    {
                        availableInnerMainDim = flexLine.SizeConsumed;
                    }
                    sizeBasedOnContent = !useLegacyStretchBehaviour;
                }
            }

            if (!sizeBasedOnContent && YogaFloat.IsDefined(availableInnerMainDim))
            {
                flexLine.Layout.RemainingFreeSpace = availableInnerMainDim - flexLine.SizeConsumed;
            }
            else if (flexLine.SizeConsumed < 0)
            {
                flexLine.Layout.RemainingFreeSpace = -flexLine.SizeConsumed;
            }

            if (!canSkipFlex)
            {
                ResolveFlexibleLength(
                    node, flexLine, mainAxis, crossAxis, direction, ownerWidth,
                    mainAxisOwnerSize, availableInnerMainDim, availableInnerCrossDim,
                    availableInnerWidth, availableInnerHeight,
                    mainAxisOverflows, sizingModeCrossDim, performLayout,
                    depth, generationCount);
            }

            node.SetLayoutHadOverflow(
                node.Layout.HadOverflow || flexLine.Layout.RemainingFreeSpace < 0);

            // STEP 6: MAIN-AXIS JUSTIFICATION & CROSS-AXIS SIZE DETERMINATION
            JustifyMainAxis(
                node, flexLine, mainAxis, crossAxis, direction,
                sizingModeMainDim, sizingModeCrossDim,
                mainAxisOwnerSize, ownerWidth,
                availableInnerMainDim, availableInnerCrossDim, availableInnerWidth,
                performLayout);

            float containerCrossAxis = availableInnerCrossDim;
            if (sizingModeCrossDim == SizingMode.MaxContent || sizingModeCrossDim == SizingMode.FitContent)
            {
                containerCrossAxis =
                    BoundAxisHelper.BoundAxis(node, crossAxis, direction,
                        flexLine.Layout.CrossDim + paddingAndBorderAxisCross, crossAxisOwnerSize, ownerWidth) -
                    paddingAndBorderAxisCross;
            }

            if (!isNodeFlexWrap && sizingModeCrossDim == SizingMode.StretchFit)
                flexLine.Layout.CrossDim = availableInnerCrossDim;

            if (!isNodeFlexWrap)
            {
                flexLine.Layout.CrossDim =
                    BoundAxisHelper.BoundAxis(node, crossAxis, direction,
                        flexLine.Layout.CrossDim + paddingAndBorderAxisCross, crossAxisOwnerSize, ownerWidth) -
                    paddingAndBorderAxisCross;
            }

            // STEP 7: CROSS-AXIS ALIGNMENT
            if (performLayout)
            {
                for (int ci = 0; ci < flexLine.ItemsInFlow.Count; ci++)
                {
                    var child = flexLine.ItemsInFlow[ci];
                    float leadingCrossDim = leadingPaddingAndBorderCross;

                    var alignItem = AlignHelper.ResolveChildAlignment(node, child);

                    if (alignItem == FlexAlign.Stretch &&
                        !child.Style.FlexStartMarginIsAuto(crossAxis, direction) &&
                        !child.Style.FlexEndMarginIsAuto(crossAxis, direction))
                    {
                        if (!child.HasDefiniteLength(FlexDirectionHelper.Dimension(crossAxis), availableInnerCrossDim))
                        {
                            float childMainSize = child.Layout.GetMeasuredDimension(FlexDirectionHelper.Dimension(mainAxis));
                            var childStyle = child.Style;
                            float childCrossSize = YogaFloat.IsDefined(childStyle.AspectRatio)
                                ? child.Style.ComputeMarginForAxis(crossAxis, availableInnerWidth) +
                                  (isMainAxisRow
                                      ? childMainSize / childStyle.AspectRatio
                                      : childMainSize * childStyle.AspectRatio)
                                : flexLine.Layout.CrossDim;

                            childMainSize += child.Style.ComputeMarginForAxis(mainAxis, availableInnerWidth);

                            var childMainSizingMode = SizingMode.StretchFit;
                            var childCrossSizingMode = SizingMode.StretchFit;
                            ConstrainMaxSizeForMode(
                                child, direction, mainAxis, availableInnerMainDim, availableInnerWidth,
                                ref childMainSizingMode, ref childMainSize);
                            ConstrainMaxSizeForMode(
                                child, direction, crossAxis, availableInnerCrossDim, availableInnerWidth,
                                ref childCrossSizingMode, ref childCrossSize);

                            float childWidth = isMainAxisRow ? childMainSize : childCrossSize;
                            float childHeight = !isMainAxisRow ? childMainSize : childCrossSize;

                            var alignContent = node.Style.AlignContent;
                            bool crossAxisDoesNotGrow = alignContent != FlexAlign.Stretch && isNodeFlexWrap;
                            var childWidthSizingMode =
                                YogaFloat.IsUndefined(childWidth) || (!isMainAxisRow && crossAxisDoesNotGrow)
                                    ? SizingMode.MaxContent
                                    : SizingMode.StretchFit;
                            var childHeightSizingMode =
                                YogaFloat.IsUndefined(childHeight) || (isMainAxisRow && crossAxisDoesNotGrow)
                                    ? SizingMode.MaxContent
                                    : SizingMode.StretchFit;

                            CalculateLayoutInternal(
                                child, childWidth, childHeight, direction,
                                childWidthSizingMode, childHeightSizingMode,
                                availableInnerWidth, availableInnerHeight,
                                true, depth, generationCount);
                        }
                    }
                    else
                    {
                        float remainingCrossDim = containerCrossAxis -
                            child.DimensionWithMargin(crossAxis, availableInnerWidth);

                        if (child.Style.FlexStartMarginIsAuto(crossAxis, direction) &&
                            child.Style.FlexEndMarginIsAuto(crossAxis, direction))
                        {
                            leadingCrossDim += YogaFloat.MaxOrDefined(0.0f, remainingCrossDim / 2);
                        }
                        else if (child.Style.FlexEndMarginIsAuto(crossAxis, direction))
                        {
                            // No-Op
                        }
                        else if (child.Style.FlexStartMarginIsAuto(crossAxis, direction))
                        {
                            leadingCrossDim += YogaFloat.MaxOrDefined(0.0f, remainingCrossDim);
                        }
                        else if (alignItem == FlexAlign.FlexStart)
                        {
                            // No-Op
                        }
                        else if (alignItem == FlexAlign.Center)
                        {
                            leadingCrossDim += remainingCrossDim / 2;
                        }
                        else
                        {
                            leadingCrossDim += remainingCrossDim;
                        }
                    }

                    child.SetLayoutPosition(
                        child.Layout.GetPosition(FlexDirectionHelper.FlexStartEdge(crossAxis)) +
                            totalLineCrossDim + leadingCrossDim,
                        FlexDirectionHelper.FlexStartEdge(crossAxis));
                }
            }

            float appliedCrossGap = lineCount != 0 ? crossAxisGap : 0.0f;
            totalLineCrossDim += flexLine.Layout.CrossDim + appliedCrossGap;
            maxLineMainDim = YogaFloat.MaxOrDefined(maxLineMainDim, flexLine.Layout.MainDim);
            FlexLineHelper.ReturnList(flexLine.ItemsInFlow);
            lineCount++;
        }

        // STEP 8: MULTI-LINE CONTENT ALIGNMENT
        if (performLayout && (isNodeFlexWrap || BaselineHelper.IsBaselineLayout(node)))
        {
            float leadPerLine = 0;
            float currentLead = leadingPaddingAndBorderCross;
            float extraSpacePerLine = 0;

            float unclampedCrossDim = sizingModeCrossDim == SizingMode.StretchFit
                ? availableInnerCrossDim + paddingAndBorderAxisCross
                : node.HasDefiniteLength(FlexDirectionHelper.Dimension(crossAxis), crossAxisOwnerSize)
                    ? node.GetResolvedDimension(direction, FlexDirectionHelper.Dimension(crossAxis), crossAxisOwnerSize, ownerWidth)
                    : totalLineCrossDim + paddingAndBorderAxisCross;

            float innerCrossDim = BoundAxisHelper.BoundAxis(
                node, crossAxis, direction, unclampedCrossDim, crossAxisOwnerSize, ownerWidth) -
                paddingAndBorderAxisCross;

            float remainingAlignContentDim = innerCrossDim - totalLineCrossDim;

            var alignContent = remainingAlignContentDim >= 0
                ? node.Style.AlignContent
                : AlignHelper.FallbackAlignment(node.Style.AlignContent);

            switch (alignContent)
            {
                case FlexAlign.FlexEnd:
                    currentLead += remainingAlignContentDim;
                    break;
                case FlexAlign.Center:
                    currentLead += remainingAlignContentDim / 2;
                    break;
                case FlexAlign.Stretch:
                    extraSpacePerLine = remainingAlignContentDim / (float)lineCount;
                    break;
                case FlexAlign.SpaceAround:
                    currentLead += remainingAlignContentDim / (2 * (float)lineCount);
                    leadPerLine = remainingAlignContentDim / (float)lineCount;
                    break;
                case FlexAlign.SpaceEvenly:
                    currentLead += remainingAlignContentDim / (float)(lineCount + 1);
                    leadPerLine = remainingAlignContentDim / (float)(lineCount + 1);
                    break;
                case FlexAlign.SpaceBetween:
                    if (lineCount > 1)
                        leadPerLine = remainingAlignContentDim / (float)(lineCount - 1);
                    break;
                default:
                    // Start, End, Auto, FlexStart, Baseline: No-Op
                    break;
            }

            int endIdx = 0;
            for (int i = 0; i < lineCount; i++)
            {
                int startIdx = endIdx;

                // Compute the line's height and find the endIndex
                float lineHeight = 0;
                float maxAscentForCurrentLine = 0;
                float maxDescentForCurrentLine = 0;
                int iter = startIdx;
                for (; iter < layoutChildren.Count; iter++)
                {
                    var child = layoutChildren[iter];
                    if (child.Style.Display == YogaDisplay.None) continue;
                    if (child.Style.PositionType != FlexPositionType.Absolute)
                    {
                        if (child.LineIndex != i) break;
                        if (child.IsLayoutDimensionDefined(crossAxis))
                        {
                            lineHeight = YogaFloat.MaxOrDefined(
                                lineHeight,
                                child.Layout.GetMeasuredDimension(FlexDirectionHelper.Dimension(crossAxis)) +
                                child.Style.ComputeMarginForAxis(crossAxis, availableInnerWidth));
                        }
                        if (AlignHelper.ResolveChildAlignment(node, child) == FlexAlign.Baseline)
                        {
                            float ascent = BaselineHelper.CalculateBaseline(child) +
                                child.Style.ComputeFlexStartMargin(FlexDirection.Column, direction, availableInnerWidth);
                            float descent =
                                child.Layout.GetMeasuredDimension(YogaDimension.Height) +
                                child.Style.ComputeMarginForAxis(FlexDirection.Column, availableInnerWidth) - ascent;
                            maxAscentForCurrentLine = YogaFloat.MaxOrDefined(maxAscentForCurrentLine, ascent);
                            maxDescentForCurrentLine = YogaFloat.MaxOrDefined(maxDescentForCurrentLine, descent);
                            lineHeight = YogaFloat.MaxOrDefined(lineHeight, maxAscentForCurrentLine + maxDescentForCurrentLine);
                        }
                    }
                }
                endIdx = iter;
                currentLead += i != 0 ? crossAxisGap : 0;
                lineHeight += extraSpacePerLine;

                for (iter = startIdx; iter < endIdx; iter++)
                {
                    var child = layoutChildren[iter];
                    if (child.Style.Display == YogaDisplay.None) continue;
                    if (child.Style.PositionType != FlexPositionType.Absolute)
                    {
                        switch (AlignHelper.ResolveChildAlignment(node, child))
                        {
                            case FlexAlign.Start:
                            case FlexAlign.End:
                                // Not yet implemented
                                break;
                            case FlexAlign.FlexStart:
                                child.SetLayoutPosition(
                                    currentLead + child.Style.ComputeFlexStartPosition(crossAxis, direction, availableInnerWidth),
                                    FlexDirectionHelper.FlexStartEdge(crossAxis));
                                break;
                            case FlexAlign.FlexEnd:
                                child.SetLayoutPosition(
                                    currentLead + lineHeight -
                                    child.Style.ComputeFlexEndMargin(crossAxis, direction, availableInnerWidth) -
                                    child.Layout.GetMeasuredDimension(FlexDirectionHelper.Dimension(crossAxis)),
                                    FlexDirectionHelper.FlexStartEdge(crossAxis));
                                break;
                            case FlexAlign.Center:
                            {
                                float childDimHeight = child.Layout.GetMeasuredDimension(FlexDirectionHelper.Dimension(crossAxis));
                                child.SetLayoutPosition(
                                    currentLead + (lineHeight - childDimHeight) / 2,
                                    FlexDirectionHelper.FlexStartEdge(crossAxis));
                                break;
                            }
                            case FlexAlign.Stretch:
                                child.SetLayoutPosition(
                                    currentLead + child.Style.ComputeFlexStartMargin(crossAxis, direction, availableInnerWidth),
                                    FlexDirectionHelper.FlexStartEdge(crossAxis));
                                if (!child.HasDefiniteLength(FlexDirectionHelper.Dimension(crossAxis), availableInnerCrossDim))
                                {
                                    float cw = isMainAxisRow
                                        ? (child.Layout.GetMeasuredDimension(YogaDimension.Width) +
                                           child.Style.ComputeMarginForAxis(mainAxis, availableInnerWidth))
                                        : leadPerLine + lineHeight;
                                    float ch = !isMainAxisRow
                                        ? (child.Layout.GetMeasuredDimension(YogaDimension.Height) +
                                           child.Style.ComputeMarginForAxis(crossAxis, availableInnerWidth))
                                        : leadPerLine + lineHeight;

                                    if (!(YogaFloat.InexactEquals(cw, child.Layout.GetMeasuredDimension(YogaDimension.Width)) &&
                                          YogaFloat.InexactEquals(ch, child.Layout.GetMeasuredDimension(YogaDimension.Height))))
                                    {
                                        CalculateLayoutInternal(
                                            child, cw, ch, direction,
                                            SizingMode.StretchFit, SizingMode.StretchFit,
                                            availableInnerWidth, availableInnerHeight,
                                            true, depth, generationCount);
                                    }
                                }
                                break;
                            case FlexAlign.Baseline:
                                child.SetLayoutPosition(
                                    currentLead + maxAscentForCurrentLine -
                                    BaselineHelper.CalculateBaseline(child) +
                                    child.Style.ComputeFlexStartPosition(FlexDirection.Column, direction, availableInnerCrossDim),
                                    YogaPhysicalEdge.Top);
                                break;
                            default:
                                break;
                        }
                    }
                }

                currentLead = currentLead + leadPerLine + lineHeight;
            }
        }

        // STEP 9: COMPUTING FINAL DIMENSIONS
        node.SetLayoutMeasuredDimension(
            BoundAxisHelper.BoundAxis(node, FlexDirection.Row, direction,
                availableWidth - marginAxisRow, ownerWidth, ownerWidth),
            YogaDimension.Width);
        node.SetLayoutMeasuredDimension(
            BoundAxisHelper.BoundAxis(node, FlexDirection.Column, direction,
                availableHeight - marginAxisColumn, ownerHeight, ownerWidth),
            YogaDimension.Height);

        if (sizingModeMainDim == SizingMode.MaxContent ||
            (node.Style.Overflow != YogaOverflow.Scroll && sizingModeMainDim == SizingMode.FitContent))
        {
            node.SetLayoutMeasuredDimension(
                BoundAxisHelper.BoundAxis(node, mainAxis, direction,
                    maxLineMainDim, mainAxisOwnerSize, ownerWidth),
                FlexDirectionHelper.Dimension(mainAxis));
        }
        else if (sizingModeMainDim == SizingMode.FitContent && node.Style.Overflow == YogaOverflow.Scroll)
        {
            node.SetLayoutMeasuredDimension(
                YogaFloat.MaxOrDefined(
                    YogaFloat.MinOrDefined(
                        availableInnerMainDim + paddingAndBorderAxisMain,
                        BoundAxisHelper.BoundAxisWithinMinAndMax(
                            node, direction, mainAxis, maxLineMainDim, mainAxisOwnerSize, ownerWidth)),
                    paddingAndBorderAxisMain),
                FlexDirectionHelper.Dimension(mainAxis));
        }

        if (sizingModeCrossDim == SizingMode.MaxContent ||
            (node.Style.Overflow != YogaOverflow.Scroll && sizingModeCrossDim == SizingMode.FitContent))
        {
            node.SetLayoutMeasuredDimension(
                BoundAxisHelper.BoundAxis(node, crossAxis, direction,
                    totalLineCrossDim + paddingAndBorderAxisCross, crossAxisOwnerSize, ownerWidth),
                FlexDirectionHelper.Dimension(crossAxis));
        }
        else if (sizingModeCrossDim == SizingMode.FitContent && node.Style.Overflow == YogaOverflow.Scroll)
        {
            node.SetLayoutMeasuredDimension(
                YogaFloat.MaxOrDefined(
                    YogaFloat.MinOrDefined(
                        availableInnerCrossDim + paddingAndBorderAxisCross,
                        BoundAxisHelper.BoundAxisWithinMinAndMax(
                            node, direction, crossAxis,
                            totalLineCrossDim + paddingAndBorderAxisCross, crossAxisOwnerSize, ownerWidth)),
                    paddingAndBorderAxisCross),
                FlexDirectionHelper.Dimension(crossAxis));
        }

        // Wrap-reverse: reverse cross positions
        if (performLayout && node.Style.FlexWrap == FlexWrap.WrapReverse)
        {
            foreach (var child in node.GetLayoutChildren())
            {
                if (child.Style.PositionType != FlexPositionType.Absolute)
                {
                    child.SetLayoutPosition(
                        node.Layout.GetMeasuredDimension(FlexDirectionHelper.Dimension(crossAxis)) -
                        child.Layout.GetPosition(FlexDirectionHelper.FlexStartEdge(crossAxis)) -
                        child.Layout.GetMeasuredDimension(FlexDirectionHelper.Dimension(crossAxis)),
                        FlexDirectionHelper.FlexStartEdge(crossAxis));
                }
            }
        }

        if (performLayout)
        {
            // STEP 10: SETTING TRAILING POSITIONS FOR CHILDREN
            bool needsMainTrailingPos = TrailingPositionHelper.NeedsTrailingPosition(mainAxis);
            bool needsCrossTrailingPos = TrailingPositionHelper.NeedsTrailingPosition(crossAxis);

            if (needsMainTrailingPos || needsCrossTrailingPos)
            {
                foreach (var child in node.GetLayoutChildren())
                {
                    if (child.Style.Display == YogaDisplay.None ||
                        child.Style.PositionType == FlexPositionType.Absolute)
                        continue;
                    if (needsMainTrailingPos)
                        TrailingPositionHelper.SetChildTrailingPosition(node, child, mainAxis);
                    if (needsCrossTrailingPos)
                        TrailingPositionHelper.SetChildTrailingPosition(node, child, crossAxis);
                }
            }

            // STEP 11: SIZING AND POSITIONING ABSOLUTE CHILDREN
            if (node.Style.PositionType != FlexPositionType.Static ||
                node.AlwaysFormsContainingBlock || depth == 1)
            {
                LayoutAbsoluteDescendants(
                    node, node,
                    isMainAxisRow ? sizingModeMainDim : sizingModeCrossDim,
                    direction, depth, generationCount,
                    0.0f, 0.0f, availableInnerWidth, availableInnerHeight);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // constrainMaxSizeForMode
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Clamps size to max-dimension and adjusts sizing mode accordingly.
    /// </summary>
    private static void ConstrainMaxSizeForMode(
        YogaNode node, FlexLayoutDirection direction, FlexDirection axis,
        float ownerAxisSize, float ownerWidth,
        ref SizingMode mode, ref float size)
    {
        float maxSize = node.Style.ResolvedMaxDimension(
            direction, FlexDirectionHelper.Dimension(axis), ownerAxisSize, ownerWidth);
        if (YogaFloat.IsDefined(maxSize))
            maxSize += node.Style.ComputeMarginForAxis(axis, ownerWidth);

        switch (mode)
        {
            case SizingMode.StretchFit:
            case SizingMode.FitContent:
                if (YogaFloat.IsDefined(maxSize) && size > maxSize)
                    size = maxSize;
                break;
            case SizingMode.MaxContent:
                if (YogaFloat.IsDefined(maxSize))
                {
                    mode = SizingMode.FitContent;
                    size = maxSize;
                }
                break;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // computeFlexBasisForChild
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Determines flex basis for a single child. Handles explicit basis, aspect ratio,
    /// and measure-only recursive layout. Sets child.Layout.ComputedFlexBasis.
    /// </summary>
    private static void ComputeFlexBasisForChild(
        YogaNode node, YogaNode child,
        float width, SizingMode widthMode, float height,
        float ownerWidth, float ownerHeight,
        SizingMode heightMode, FlexLayoutDirection direction,
        uint depth, uint generationCount)
    {
        var mainAxis = FlexDirectionHelper.ResolveDirection(node.Style.FlexDirection, direction);
        bool isMainAxisRow = FlexDirectionHelper.IsRow(mainAxis);
        float mainAxisSize = isMainAxisRow ? width : height;
        float mainAxisOwnerSize = isMainAxisRow ? ownerWidth : ownerHeight;

        float childWidth = float.NaN;
        float childHeight = float.NaN;
        SizingMode childWidthSizingMode = SizingMode.MaxContent;
        SizingMode childHeightSizingMode = SizingMode.MaxContent;

        float resolvedFlexBasis = child.ResolveFlexBasis(direction, mainAxis, mainAxisOwnerSize, ownerWidth);
        bool isRowStyleDimDefined = child.HasDefiniteLength(YogaDimension.Width, ownerWidth);
        bool isColumnStyleDimDefined = child.HasDefiniteLength(YogaDimension.Height, ownerHeight);

        bool fixFlexBasisFitContent = node.Config.IsExperimentalFeatureEnabled(YogaExperimentalFeature.FixFlexBasisFitContent);

        bool useResolvedFlexBasis = YogaFloat.IsDefined(resolvedFlexBasis) && YogaFloat.IsDefined(mainAxisSize);
        if (fixFlexBasisFitContent && YogaFloat.IsDefined(resolvedFlexBasis) && resolvedFlexBasis > 0)
            useResolvedFlexBasis = true;

        if (useResolvedFlexBasis)
        {
            if (YogaFloat.IsUndefined(child.Layout.ComputedFlexBasis) ||
                (child.Config.IsExperimentalFeatureEnabled(YogaExperimentalFeature.WebFlexBasis) &&
                 child.Layout.ComputedFlexBasisGeneration != generationCount))
            {
                float paddingAndBorder = BoundAxisHelper.PaddingAndBorderForAxis(child, mainAxis, direction, ownerWidth);
                child.SetLayoutComputedFlexBasis(YogaFloat.MaxOrDefined(resolvedFlexBasis, paddingAndBorder));
            }
        }
        else if (isMainAxisRow && isRowStyleDimDefined)
        {
            float paddingAndBorder = BoundAxisHelper.PaddingAndBorderForAxis(child, FlexDirection.Row, direction, ownerWidth);
            child.SetLayoutComputedFlexBasis(
                YogaFloat.MaxOrDefined(
                    child.GetResolvedDimension(direction, YogaDimension.Width, ownerWidth, ownerWidth),
                    paddingAndBorder));
        }
        else if (!isMainAxisRow && isColumnStyleDimDefined)
        {
            float paddingAndBorder = BoundAxisHelper.PaddingAndBorderForAxis(child, FlexDirection.Column, direction, ownerWidth);
            child.SetLayoutComputedFlexBasis(
                YogaFloat.MaxOrDefined(
                    child.GetResolvedDimension(direction, YogaDimension.Height, ownerHeight, ownerWidth),
                    paddingAndBorder));
        }
        else
        {
            childWidthSizingMode = SizingMode.MaxContent;
            childHeightSizingMode = SizingMode.MaxContent;

            float marginRow = child.Style.ComputeMarginForAxis(FlexDirection.Row, ownerWidth);
            float marginColumn = child.Style.ComputeMarginForAxis(FlexDirection.Column, ownerWidth);

            if (isRowStyleDimDefined)
            {
                childWidth = child.GetResolvedDimension(direction, YogaDimension.Width, ownerWidth, ownerWidth) + marginRow;
                childWidthSizingMode = SizingMode.StretchFit;
            }
            if (isColumnStyleDimDefined)
            {
                childHeight = child.GetResolvedDimension(direction, YogaDimension.Height, ownerHeight, ownerWidth) + marginColumn;
                childHeightSizingMode = SizingMode.StretchFit;
            }

            var childStyle = child.Style;
            if (YogaFloat.IsDefined(childStyle.AspectRatio))
            {
                if (!isMainAxisRow && childWidthSizingMode == SizingMode.StretchFit)
                {
                    childHeight = marginColumn + (childWidth - marginRow) / childStyle.AspectRatio;
                    childHeightSizingMode = SizingMode.StretchFit;
                }
                else if (isMainAxisRow && childHeightSizingMode == SizingMode.StretchFit)
                {
                    childWidth = marginRow + (childHeight - marginColumn) * childStyle.AspectRatio;
                    childWidthSizingMode = SizingMode.StretchFit;
                }
            }

            // Cross axis stretch
            bool hasExactWidth = YogaFloat.IsDefined(width) && widthMode == SizingMode.StretchFit;
            bool childWidthStretch =
                AlignHelper.ResolveChildAlignment(node, child) == FlexAlign.Stretch &&
                childWidthSizingMode != SizingMode.StretchFit;
            if (!isMainAxisRow && !isRowStyleDimDefined && hasExactWidth && childWidthStretch)
            {
                childWidth = width;
                childWidthSizingMode = SizingMode.StretchFit;
                if (YogaFloat.IsDefined(childStyle.AspectRatio))
                {
                    childHeight = (childWidth - marginRow) / childStyle.AspectRatio;
                    childHeightSizingMode = SizingMode.StretchFit;
                }
            }

            bool hasExactHeight = YogaFloat.IsDefined(height) && heightMode == SizingMode.StretchFit;
            bool childHeightStretch =
                AlignHelper.ResolveChildAlignment(node, child) == FlexAlign.Stretch &&
                childHeightSizingMode != SizingMode.StretchFit;
            if (isMainAxisRow && !isColumnStyleDimDefined && hasExactHeight && childHeightStretch)
            {
                childHeight = height;
                childHeightSizingMode = SizingMode.StretchFit;
                if (YogaFloat.IsDefined(childStyle.AspectRatio))
                {
                    childWidth = (childHeight - marginColumn) * childStyle.AspectRatio;
                    childWidthSizingMode = SizingMode.StretchFit;
                }
            }

            // Overflow scroll logic
            if ((!isMainAxisRow && node.Style.Overflow == YogaOverflow.Scroll) ||
                node.Style.Overflow != YogaOverflow.Scroll)
            {
                if (YogaFloat.IsUndefined(childWidth) && YogaFloat.IsDefined(width))
                {
                    childWidth = width;
                    childWidthSizingMode = SizingMode.FitContent;
                }
            }

            bool applyHeightFitContent = isMainAxisRow || node.Style.Overflow != YogaOverflow.Scroll;
            if (fixFlexBasisFitContent)
            {
                applyHeightFitContent = isMainAxisRow ||
                    (child.HasMeasureFunc && node.Style.Overflow != YogaOverflow.Scroll);
            }
            if (applyHeightFitContent && YogaFloat.IsUndefined(childHeight) && YogaFloat.IsDefined(height))
            {
                childHeight = height;
                childHeightSizingMode = SizingMode.FitContent;
            }

            ConstrainMaxSizeForMode(child, direction, FlexDirection.Row, ownerWidth, ownerWidth, ref childWidthSizingMode, ref childWidth);
            ConstrainMaxSizeForMode(child, direction, FlexDirection.Column, ownerHeight, ownerWidth, ref childHeightSizingMode, ref childHeight);

            // Measure the child
            CalculateLayoutInternal(
                child, childWidth, childHeight, direction,
                childWidthSizingMode, childHeightSizingMode,
                ownerWidth, ownerHeight,
                false, depth, generationCount);

            child.SetLayoutComputedFlexBasis(
                YogaFloat.MaxOrDefined(
                    child.Layout.GetMeasuredDimension(FlexDirectionHelper.Dimension(mainAxis)),
                    BoundAxisHelper.PaddingAndBorderForAxis(child, mainAxis, direction, ownerWidth)));
        }
        child.SetLayoutComputedFlexBasisGeneration(generationCount);
    }

    // ──────────────────────────────────────────────────────────────────────
    // measureNodeWithMeasureFunc — leaf nodes with custom measure (e.g. text)
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Invokes the node's custom MeasureFunc for leaf nodes. Applies padding/border and max constraints.
    /// </summary>
    private static void MeasureNodeWithMeasureFunc(
        YogaNode node, FlexLayoutDirection direction,
        float availableWidth, float availableHeight,
        SizingMode widthSizingMode, SizingMode heightSizingMode,
        float ownerWidth, float ownerHeight)
    {
        if (widthSizingMode == SizingMode.MaxContent) availableWidth = float.NaN;
        if (heightSizingMode == SizingMode.MaxContent) availableHeight = float.NaN;

        var layout = node.Layout;
        float paddingAndBorderAxisRow =
            layout.GetPadding(YogaPhysicalEdge.Left) + layout.GetPadding(YogaPhysicalEdge.Right) +
            layout.GetBorder(YogaPhysicalEdge.Left) + layout.GetBorder(YogaPhysicalEdge.Right);
        float paddingAndBorderAxisColumn =
            layout.GetPadding(YogaPhysicalEdge.Top) + layout.GetPadding(YogaPhysicalEdge.Bottom) +
            layout.GetBorder(YogaPhysicalEdge.Top) + layout.GetBorder(YogaPhysicalEdge.Bottom);

        float innerWidth = YogaFloat.IsUndefined(availableWidth)
            ? availableWidth
            : YogaFloat.MaxOrDefined(0.0f, availableWidth - paddingAndBorderAxisRow);
        float innerHeight = YogaFloat.IsUndefined(availableHeight)
            ? availableHeight
            : YogaFloat.MaxOrDefined(0.0f, availableHeight - paddingAndBorderAxisColumn);

        if (widthSizingMode == SizingMode.StretchFit && heightSizingMode == SizingMode.StretchFit)
        {
            node.SetLayoutMeasuredDimension(
                BoundAxisHelper.BoundAxis(node, FlexDirection.Row, direction, availableWidth, ownerWidth, ownerWidth),
                YogaDimension.Width);
            node.SetLayoutMeasuredDimension(
                BoundAxisHelper.BoundAxis(node, FlexDirection.Column, direction, availableHeight, ownerHeight, ownerWidth),
                YogaDimension.Height);
        }
        else
        {
            var measuredSize = node.Measure(
                innerWidth, SizingModeHelper.ToMeasureMode(widthSizingMode),
                innerHeight, SizingModeHelper.ToMeasureMode(heightSizingMode));

            node.SetLayoutMeasuredDimension(
                BoundAxisHelper.BoundAxis(node, FlexDirection.Row, direction,
                    (widthSizingMode == SizingMode.MaxContent || widthSizingMode == SizingMode.FitContent)
                        ? measuredSize.Width + paddingAndBorderAxisRow
                        : availableWidth,
                    ownerWidth, ownerWidth),
                YogaDimension.Width);
            node.SetLayoutMeasuredDimension(
                BoundAxisHelper.BoundAxis(node, FlexDirection.Column, direction,
                    (heightSizingMode == SizingMode.MaxContent || heightSizingMode == SizingMode.FitContent)
                        ? measuredSize.Height + paddingAndBorderAxisColumn
                        : availableHeight,
                    ownerHeight, ownerWidth),
                YogaDimension.Height);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // measureNodeWithoutChildren
    // ──────────────────────────────────────────────────────────────────────

    private static void MeasureNodeWithoutChildren(
        YogaNode node, FlexLayoutDirection direction,
        float availableWidth, float availableHeight,
        SizingMode widthSizingMode, SizingMode heightSizingMode,
        float ownerWidth, float ownerHeight)
    {
        var layout = node.Layout;

        float w = availableWidth;
        if (widthSizingMode == SizingMode.MaxContent || widthSizingMode == SizingMode.FitContent)
        {
            w = layout.GetPadding(YogaPhysicalEdge.Left) + layout.GetPadding(YogaPhysicalEdge.Right) +
                layout.GetBorder(YogaPhysicalEdge.Left) + layout.GetBorder(YogaPhysicalEdge.Right);
        }
        node.SetLayoutMeasuredDimension(
            BoundAxisHelper.BoundAxis(node, FlexDirection.Row, direction, w, ownerWidth, ownerWidth),
            YogaDimension.Width);

        float h = availableHeight;
        if (heightSizingMode == SizingMode.MaxContent || heightSizingMode == SizingMode.FitContent)
        {
            h = layout.GetPadding(YogaPhysicalEdge.Top) + layout.GetPadding(YogaPhysicalEdge.Bottom) +
                layout.GetBorder(YogaPhysicalEdge.Top) + layout.GetBorder(YogaPhysicalEdge.Bottom);
        }
        node.SetLayoutMeasuredDimension(
            BoundAxisHelper.BoundAxis(node, FlexDirection.Column, direction, h, ownerHeight, ownerWidth),
            YogaDimension.Height);
    }

    // ──────────────────────────────────────────────────────────────────────
    // measureNodeWithFixedSize
    // ──────────────────────────────────────────────────────────────────────

    private static bool IsFixedSize(float dim, SizingMode sizingMode)
        => sizingMode == SizingMode.StretchFit ||
           (YogaFloat.IsDefined(dim) && sizingMode == SizingMode.FitContent && dim <= 0.0f);

    private static bool MeasureNodeWithFixedSize(
        YogaNode node, FlexLayoutDirection direction,
        float availableWidth, float availableHeight,
        SizingMode widthSizingMode, SizingMode heightSizingMode,
        float ownerWidth, float ownerHeight)
    {
        if (IsFixedSize(availableWidth, widthSizingMode) && IsFixedSize(availableHeight, heightSizingMode))
        {
            node.SetLayoutMeasuredDimension(
                BoundAxisHelper.BoundAxis(node, FlexDirection.Row, direction,
                    YogaFloat.IsUndefined(availableWidth) || (widthSizingMode == SizingMode.FitContent && availableWidth < 0.0f)
                        ? 0.0f : availableWidth,
                    ownerWidth, ownerWidth),
                YogaDimension.Width);
            node.SetLayoutMeasuredDimension(
                BoundAxisHelper.BoundAxis(node, FlexDirection.Column, direction,
                    YogaFloat.IsUndefined(availableHeight) || (heightSizingMode == SizingMode.FitContent && availableHeight < 0.0f)
                        ? 0.0f : availableHeight,
                    ownerHeight, ownerWidth),
                YogaDimension.Height);
            return true;
        }
        return false;
    }

    // ──────────────────────────────────────────────────────────────────────
    // zeroOutLayoutRecursively
    // ──────────────────────────────────────────────────────────────────────

    private static void ZeroOutLayoutRecursively(YogaNode node)
    {
        // Reset layout to defaults
        node.SetLayoutDimension(0, YogaDimension.Width);
        node.SetLayoutDimension(0, YogaDimension.Height);
        node.SetLayoutPosition(0, YogaPhysicalEdge.Left);
        node.SetLayoutPosition(0, YogaPhysicalEdge.Top);
        node.SetLayoutPosition(0, YogaPhysicalEdge.Right);
        node.SetLayoutPosition(0, YogaPhysicalEdge.Bottom);
        node.SetLayoutMeasuredDimension(float.NaN, YogaDimension.Width);
        node.SetLayoutMeasuredDimension(float.NaN, YogaDimension.Height);
        node.SetLayoutMargin(0, YogaPhysicalEdge.Left);
        node.SetLayoutMargin(0, YogaPhysicalEdge.Top);
        node.SetLayoutMargin(0, YogaPhysicalEdge.Right);
        node.SetLayoutMargin(0, YogaPhysicalEdge.Bottom);
        node.SetLayoutBorder(0, YogaPhysicalEdge.Left);
        node.SetLayoutBorder(0, YogaPhysicalEdge.Top);
        node.SetLayoutBorder(0, YogaPhysicalEdge.Right);
        node.SetLayoutBorder(0, YogaPhysicalEdge.Bottom);
        node.SetLayoutPadding(0, YogaPhysicalEdge.Left);
        node.SetLayoutPadding(0, YogaPhysicalEdge.Top);
        node.SetLayoutPadding(0, YogaPhysicalEdge.Right);
        node.SetLayoutPadding(0, YogaPhysicalEdge.Bottom);
        node.Layout.HadOverflow = false;
        node.Layout.Direction = FlexLayoutDirection.Inherit;
        node.Layout.ComputedFlexBasis = float.NaN;
        node.Layout.ComputedFlexBasisGeneration = 0;
        node.HasNewLayout = true;

        foreach (var child in node.Children)
            ZeroOutLayoutRecursively(child);
    }

    // ──────────────────────────────────────────────────────────────────────
    // cleanupContentsNodesRecursively
    // Ported from yoga/algorithm/CalculateLayout.cpp
    // Zeroes out layout for display:contents nodes since they are not
    // traversed directly by the algorithm (their children are promoted).
    // ──────────────────────────────────────────────────────────────────────

    private static void CleanupContentsNodesRecursively(YogaNode node)
    {
        foreach (var child in node.Children)
        {
            if (child.Style.Display == YogaDisplay.Contents)
            {
                child.SetLayoutDimension(0, YogaDimension.Width);
                child.SetLayoutDimension(0, YogaDimension.Height);
                child.SetLayoutPosition(0, YogaPhysicalEdge.Left);
                child.SetLayoutPosition(0, YogaPhysicalEdge.Top);
                child.SetLayoutPosition(0, YogaPhysicalEdge.Right);
                child.SetLayoutPosition(0, YogaPhysicalEdge.Bottom);
                child.HasNewLayout = true;
                child.SetDirty(false);
                CleanupContentsNodesRecursively(child);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // calculateAvailableInnerDimension
    // ──────────────────────────────────────────────────────────────────────

    private static float CalculateAvailableInnerDimension(
        YogaNode node, FlexLayoutDirection direction, YogaDimension dimension,
        float availableDim, float paddingAndBorder, float ownerDim, float ownerWidth)
    {
        float availableInnerDim = availableDim - paddingAndBorder;

        if (YogaFloat.IsDefined(availableInnerDim))
        {
            float minDimensionOptional = node.Style.ResolvedMinDimension(direction, dimension, ownerDim, ownerWidth);
            float minInnerDim = YogaFloat.IsUndefined(minDimensionOptional) ? 0.0f : minDimensionOptional - paddingAndBorder;

            float maxDimensionOptional = node.Style.ResolvedMaxDimension(direction, dimension, ownerDim, ownerWidth);
            float maxInnerDim = YogaFloat.IsUndefined(maxDimensionOptional) ? float.MaxValue : maxDimensionOptional - paddingAndBorder;

            availableInnerDim = YogaFloat.MaxOrDefined(
                YogaFloat.MinOrDefined(availableInnerDim, maxInnerDim), minInnerDim);
        }

        return availableInnerDim;
    }

    // ──────────────────────────────────────────────────────────────────────
    // computeFlexBasisForChildren
    // ──────────────────────────────────────────────────────────────────────

    private static float ComputeFlexBasisForChildren(
        YogaNode node,
        float availableInnerWidth, float availableInnerHeight,
        float ownerWidth, float ownerHeight,
        SizingMode widthSizingMode, SizingMode heightSizingMode,
        FlexLayoutDirection direction, FlexDirection mainAxis,
        bool performLayout, uint depth, uint generationCount)
    {
        float totalOuterFlexBasis = 0.0f;
        YogaNode? singleFlexChild = null;
        var children = new List<YogaNode>();
        node.CollectLayoutChildren(children);
        var sizingModeMainDim = FlexDirectionHelper.IsRow(mainAxis) ? widthSizingMode : heightSizingMode;

        if (sizingModeMainDim == SizingMode.StretchFit)
        {
            foreach (var child in children)
            {
                if (child.IsNodeFlexible())
                {
                    if (singleFlexChild != null ||
                        YogaFloat.InexactEquals(child.ResolveFlexGrow(), 0.0f) ||
                        YogaFloat.InexactEquals(child.ResolveFlexShrink(), 0.0f))
                    {
                        singleFlexChild = null;
                        break;
                    }
                    else
                    {
                        singleFlexChild = child;
                    }
                }
            }
        }

        foreach (var child in children)
        {
            child.ProcessDimensions();
            if (child.Style.Display == YogaDisplay.None)
            {
                ZeroOutLayoutRecursively(child);
                child.HasNewLayout = true;
                child.SetDirty(false);
                continue;
            }
            if (performLayout)
            {
                var childDirection = child.ResolveDirection(direction);
                child.SetPosition(childDirection, availableInnerWidth, availableInnerHeight);
            }

            if (child.Style.PositionType == FlexPositionType.Absolute)
                continue;

            if (child == singleFlexChild)
            {
                child.SetLayoutComputedFlexBasisGeneration(generationCount);
                child.SetLayoutComputedFlexBasis(0);
            }
            else
            {
                ComputeFlexBasisForChild(
                    node, child, availableInnerWidth, widthSizingMode,
                    availableInnerHeight, ownerWidth, ownerHeight,
                    heightSizingMode, direction, depth, generationCount);
            }

            totalOuterFlexBasis +=
                YogaFloat.UnwrapOrDefault(child.Layout.ComputedFlexBasis) +
                child.Style.ComputeMarginForAxis(mainAxis, availableInnerWidth);
        }

        return totalOuterFlexBasis;
    }

    // ──────────────────────────────────────────────────────────────────────
    // distributeFreeSpaceFirstPass
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pass 1: finds clamped children (those hitting min/max bounds) and adjusts remaining free space.
    /// </summary>
    private static void DistributeFreeSpaceFirstPass(
        FlexLine flexLine, FlexLayoutDirection direction, FlexDirection mainAxis,
        float ownerWidth, float mainAxisOwnerSize,
        float availableInnerMainDim, float availableInnerWidth)
    {
        float flexShrinkScaledFactor = 0;
        float flexGrowFactor = 0;
        float baseMainSize = 0;
        float boundMainSize = 0;
        float deltaFreeSpace = 0;

        foreach (var currentLineChild in flexLine.ItemsInFlow)
        {
            float childFlexBasis = BoundAxisHelper.BoundAxisWithinMinAndMax(
                currentLineChild, direction, mainAxis,
                currentLineChild.Layout.ComputedFlexBasis,
                mainAxisOwnerSize, ownerWidth);

            if (flexLine.Layout.RemainingFreeSpace < 0)
            {
                flexShrinkScaledFactor = -currentLineChild.ResolveFlexShrink() * childFlexBasis;
                if (YogaFloat.IsDefined(flexShrinkScaledFactor) && flexShrinkScaledFactor != 0)
                {
                    baseMainSize = childFlexBasis +
                        flexLine.Layout.RemainingFreeSpace /
                        flexLine.Layout.TotalFlexShrinkScaledFactors *
                        flexShrinkScaledFactor;
                    boundMainSize = BoundAxisHelper.BoundAxis(
                        currentLineChild, mainAxis, direction,
                        baseMainSize, availableInnerMainDim, availableInnerWidth);
                    if (YogaFloat.IsDefined(baseMainSize) && YogaFloat.IsDefined(boundMainSize) &&
                        baseMainSize != boundMainSize)
                    {
                        deltaFreeSpace += boundMainSize - childFlexBasis;
                        flexLine.Layout.TotalFlexShrinkScaledFactors -=
                            (-currentLineChild.ResolveFlexShrink() *
                             YogaFloat.UnwrapOrDefault(currentLineChild.Layout.ComputedFlexBasis));
                    }
                }
            }
            else if (YogaFloat.IsDefined(flexLine.Layout.RemainingFreeSpace) &&
                     flexLine.Layout.RemainingFreeSpace > 0)
            {
                flexGrowFactor = currentLineChild.ResolveFlexGrow();
                if (YogaFloat.IsDefined(flexGrowFactor) && flexGrowFactor != 0)
                {
                    baseMainSize = childFlexBasis +
                        flexLine.Layout.RemainingFreeSpace /
                        flexLine.Layout.TotalFlexGrowFactors * flexGrowFactor;
                    boundMainSize = BoundAxisHelper.BoundAxis(
                        currentLineChild, mainAxis, direction,
                        baseMainSize, availableInnerMainDim, availableInnerWidth);
                    if (YogaFloat.IsDefined(baseMainSize) && YogaFloat.IsDefined(boundMainSize) &&
                        baseMainSize != boundMainSize)
                    {
                        deltaFreeSpace += boundMainSize - childFlexBasis;
                        flexLine.Layout.TotalFlexGrowFactors -= flexGrowFactor;
                    }
                }
            }
        }
        flexLine.Layout.RemainingFreeSpace -= deltaFreeSpace;
    }

    // ──────────────────────────────────────────────────────────────────────
    // distributeFreeSpaceSecondPass
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pass 2: distributes remaining free space among unclamped flexible children.
    /// Returns total delta applied. Also triggers recursive layout for children needing it.
    /// </summary>
    private static float DistributeFreeSpaceSecondPass(
        FlexLine flexLine, YogaNode node,
        FlexDirection mainAxis, FlexDirection crossAxis,
        FlexLayoutDirection direction, float ownerWidth,
        float mainAxisOwnerSize, float availableInnerMainDim,
        float availableInnerCrossDim, float availableInnerWidth, float availableInnerHeight,
        bool mainAxisOverflows, SizingMode sizingModeCrossDim,
        bool performLayout, uint depth, uint generationCount)
    {
        float childFlexBasis = 0;
        float flexShrinkScaledFactor = 0;
        float flexGrowFactor = 0;
        float deltaFreeSpace = 0;
        bool isMainAxisRow = FlexDirectionHelper.IsRow(mainAxis);
        bool isNodeFlexWrap = node.Style.FlexWrap != FlexWrap.NoWrap;

        for (int i = 0; i < flexLine.ItemsInFlow.Count; i++)
        {
            var currentLineChild = flexLine.ItemsInFlow[i];
            childFlexBasis = BoundAxisHelper.BoundAxisWithinMinAndMax(
                currentLineChild, direction, mainAxis,
                currentLineChild.Layout.ComputedFlexBasis,
                mainAxisOwnerSize, ownerWidth);
            float updatedMainSize = childFlexBasis;

            if (YogaFloat.IsDefined(flexLine.Layout.RemainingFreeSpace) &&
                flexLine.Layout.RemainingFreeSpace < 0)
            {
                flexShrinkScaledFactor = -currentLineChild.ResolveFlexShrink() * childFlexBasis;
                if (flexShrinkScaledFactor != 0)
                {
                    float childSize;
                    if (YogaFloat.IsDefined(flexLine.Layout.TotalFlexShrinkScaledFactors) &&
                        flexLine.Layout.TotalFlexShrinkScaledFactors == 0)
                    {
                        childSize = childFlexBasis + flexShrinkScaledFactor;
                    }
                    else
                    {
                        childSize = childFlexBasis +
                            (flexLine.Layout.RemainingFreeSpace /
                             flexLine.Layout.TotalFlexShrinkScaledFactors) *
                            flexShrinkScaledFactor;
                    }
                    updatedMainSize = BoundAxisHelper.BoundAxis(
                        currentLineChild, mainAxis, direction,
                        childSize, availableInnerMainDim, availableInnerWidth);
                }
            }
            else if (YogaFloat.IsDefined(flexLine.Layout.RemainingFreeSpace) &&
                     flexLine.Layout.RemainingFreeSpace > 0)
            {
                flexGrowFactor = currentLineChild.ResolveFlexGrow();
                if (!float.IsNaN(flexGrowFactor) && flexGrowFactor != 0)
                {
                    updatedMainSize = BoundAxisHelper.BoundAxis(
                        currentLineChild, mainAxis, direction,
                        childFlexBasis +
                            flexLine.Layout.RemainingFreeSpace /
                            flexLine.Layout.TotalFlexGrowFactors * flexGrowFactor,
                        availableInnerMainDim, availableInnerWidth);
                }
            }

            deltaFreeSpace += updatedMainSize - childFlexBasis;

            float marginMain = currentLineChild.Style.ComputeMarginForAxis(mainAxis, availableInnerWidth);
            float marginCross = currentLineChild.Style.ComputeMarginForAxis(crossAxis, availableInnerWidth);

            float childCrossSize = float.NaN;
            float childMainSize = updatedMainSize + marginMain;
            SizingMode childCrossSizingMode;
            SizingMode childMainSizingMode = SizingMode.StretchFit;

            var childStyle = currentLineChild.Style;
            if (YogaFloat.IsDefined(childStyle.AspectRatio))
            {
                childCrossSize = isMainAxisRow
                    ? (childMainSize - marginMain) / childStyle.AspectRatio
                    : (childMainSize - marginMain) * childStyle.AspectRatio;
                childCrossSizingMode = SizingMode.StretchFit;
                childCrossSize += marginCross;
            }
            else if (
                !float.IsNaN(availableInnerCrossDim) &&
                !currentLineChild.HasDefiniteLength(FlexDirectionHelper.Dimension(crossAxis), availableInnerCrossDim) &&
                sizingModeCrossDim == SizingMode.StretchFit &&
                !(isNodeFlexWrap && mainAxisOverflows) &&
                AlignHelper.ResolveChildAlignment(node, currentLineChild) == FlexAlign.Stretch &&
                !currentLineChild.Style.FlexStartMarginIsAuto(crossAxis, direction) &&
                !currentLineChild.Style.FlexEndMarginIsAuto(crossAxis, direction))
            {
                childCrossSize = availableInnerCrossDim;
                childCrossSizingMode = SizingMode.StretchFit;
            }
            else if (!currentLineChild.HasDefiniteLength(FlexDirectionHelper.Dimension(crossAxis), availableInnerCrossDim))
            {
                childCrossSize = availableInnerCrossDim;
                childCrossSizingMode = YogaFloat.IsUndefined(childCrossSize)
                    ? SizingMode.MaxContent
                    : SizingMode.FitContent;
            }
            else
            {
                childCrossSize = currentLineChild.GetResolvedDimension(
                    direction, FlexDirectionHelper.Dimension(crossAxis),
                    availableInnerCrossDim, availableInnerWidth) + marginCross;
                bool isLoosePercentageMeasurement =
                    currentLineChild.ProcessedDimensions[(int)FlexDirectionHelper.Dimension(crossAxis)].IsPercent &&
                    sizingModeCrossDim != SizingMode.StretchFit;
                childCrossSizingMode =
                    YogaFloat.IsUndefined(childCrossSize) || isLoosePercentageMeasurement
                        ? SizingMode.MaxContent
                        : SizingMode.StretchFit;
            }

            ConstrainMaxSizeForMode(
                currentLineChild, direction, mainAxis, availableInnerMainDim, availableInnerWidth,
                ref childMainSizingMode, ref childMainSize);
            ConstrainMaxSizeForMode(
                currentLineChild, direction, crossAxis, availableInnerCrossDim, availableInnerWidth,
                ref childCrossSizingMode, ref childCrossSize);

            bool requiresStretchLayout =
                !currentLineChild.HasDefiniteLength(FlexDirectionHelper.Dimension(crossAxis), availableInnerCrossDim) &&
                AlignHelper.ResolveChildAlignment(node, currentLineChild) == FlexAlign.Stretch &&
                !currentLineChild.Style.FlexStartMarginIsAuto(crossAxis, direction) &&
                !currentLineChild.Style.FlexEndMarginIsAuto(crossAxis, direction);

            float childWidth = isMainAxisRow ? childMainSize : childCrossSize;
            float childHeight = !isMainAxisRow ? childMainSize : childCrossSize;
            var childWidthSizingMode = isMainAxisRow ? childMainSizingMode : childCrossSizingMode;
            var childHeightSizingMode = !isMainAxisRow ? childMainSizingMode : childCrossSizingMode;

            bool isLayoutPass = performLayout && !requiresStretchLayout;
            CalculateLayoutInternal(
                currentLineChild, childWidth, childHeight,
                node.Layout.Direction,
                childWidthSizingMode, childHeightSizingMode,
                availableInnerWidth, availableInnerHeight,
                isLayoutPass, depth, generationCount);

            node.SetLayoutHadOverflow(
                node.Layout.HadOverflow || currentLineChild.Layout.HadOverflow);
        }
        return deltaFreeSpace;
    }

    // ──────────────────────────────────────────────────────────────────────
    // resolveFlexibleLength
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Orchestrates the two-pass free space distribution (first pass finds clamped items,
    /// second pass allocates to remaining). Called once per flex line.
    /// </summary>
    private static void ResolveFlexibleLength(
        YogaNode node, FlexLine flexLine,
        FlexDirection mainAxis, FlexDirection crossAxis,
        FlexLayoutDirection direction, float ownerWidth,
        float mainAxisOwnerSize, float availableInnerMainDim,
        float availableInnerCrossDim, float availableInnerWidth, float availableInnerHeight,
        bool mainAxisOverflows, SizingMode sizingModeCrossDim,
        bool performLayout, uint depth, uint generationCount)
    {
        float originalFreeSpace = flexLine.Layout.RemainingFreeSpace;

        DistributeFreeSpaceFirstPass(
            flexLine, direction, mainAxis, ownerWidth,
            mainAxisOwnerSize, availableInnerMainDim, availableInnerWidth);

        float distributedFreeSpace = DistributeFreeSpaceSecondPass(
            flexLine, node, mainAxis, crossAxis, direction, ownerWidth,
            mainAxisOwnerSize, availableInnerMainDim, availableInnerCrossDim,
            availableInnerWidth, availableInnerHeight,
            mainAxisOverflows, sizingModeCrossDim, performLayout,
            depth, generationCount);

        flexLine.Layout.RemainingFreeSpace = originalFreeSpace - distributedFreeSpace;
    }

    // ──────────────────────────────────────────────────────────────────────
    // justifyMainAxis
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Positions children along the main axis based on justify-content.
    /// Handles: flex-start, center, flex-end, space-between, space-around, space-evenly.
    /// </summary>
    private static void JustifyMainAxis(
        YogaNode node, FlexLine flexLine,
        FlexDirection mainAxis, FlexDirection crossAxis,
        FlexLayoutDirection direction,
        SizingMode sizingModeMainDim, SizingMode sizingModeCrossDim,
        float mainAxisOwnerSize, float ownerWidth,
        float availableInnerMainDim, float availableInnerCrossDim,
        float availableInnerWidth, bool performLayout)
    {
        var style = node.Style;

        float leadingPaddingAndBorderMain = style.ComputeFlexStartPaddingAndBorder(mainAxis, direction, ownerWidth);
        float trailingPaddingAndBorderMain = style.ComputeFlexEndPaddingAndBorder(mainAxis, direction, ownerWidth);
        float gap = style.ComputeGapForAxis(mainAxis, availableInnerMainDim);

        if (sizingModeMainDim == SizingMode.FitContent && flexLine.Layout.RemainingFreeSpace > 0)
        {
            if (style.MinDimensions[(int)FlexDirectionHelper.Dimension(mainAxis)].IsDefined &&
                YogaFloat.IsDefined(style.ResolvedMinDimension(
                    direction, FlexDirectionHelper.Dimension(mainAxis), mainAxisOwnerSize, ownerWidth)))
            {
                float minAvailableMainDim =
                    style.ResolvedMinDimension(
                        direction, FlexDirectionHelper.Dimension(mainAxis), mainAxisOwnerSize, ownerWidth) -
                    leadingPaddingAndBorderMain - trailingPaddingAndBorderMain;
                float occupiedSpaceByChildNodes = availableInnerMainDim - flexLine.Layout.RemainingFreeSpace;
                flexLine.Layout.RemainingFreeSpace = YogaFloat.MaxOrDefined(
                    0.0f, minAvailableMainDim - occupiedSpaceByChildNodes);
            }
            else
            {
                flexLine.Layout.RemainingFreeSpace = 0;
            }
        }

        float leadingMainDim = 0;
        float betweenMainDim = gap;
        var justifyContent = flexLine.Layout.RemainingFreeSpace >= 0
            ? style.JustifyContent
            : AlignHelper.FallbackAlignment(style.JustifyContent);

        if (flexLine.NumberOfAutoMargins == 0)
        {
            switch (justifyContent)
            {
                case FlexJustify.Center:
                    leadingMainDim = flexLine.Layout.RemainingFreeSpace / 2;
                    break;
                case FlexJustify.FlexEnd:
                    leadingMainDim = flexLine.Layout.RemainingFreeSpace;
                    break;
                case FlexJustify.SpaceBetween:
                    if (flexLine.ItemsInFlow.Count > 1)
                        betweenMainDim += flexLine.Layout.RemainingFreeSpace / (float)(flexLine.ItemsInFlow.Count - 1);
                    break;
                case FlexJustify.SpaceEvenly:
                    leadingMainDim = flexLine.Layout.RemainingFreeSpace / (float)(flexLine.ItemsInFlow.Count + 1);
                    betweenMainDim += leadingMainDim;
                    break;
                case FlexJustify.SpaceAround:
                    leadingMainDim = 0.5f * flexLine.Layout.RemainingFreeSpace / (float)flexLine.ItemsInFlow.Count;
                    betweenMainDim += leadingMainDim * 2;
                    break;
                default:
                    // Start, End, Auto, FlexStart, Stretch: No-Op
                    break;
            }
        }

        flexLine.Layout.MainDim = leadingPaddingAndBorderMain + leadingMainDim;
        flexLine.Layout.CrossDim = 0;

        float maxAscentForCurrentLine = 0;
        float maxDescentForCurrentLine = 0;
        bool isNodeBaselineLayout = BaselineHelper.IsBaselineLayout(node);
        bool isMainAxisRow = FlexDirectionHelper.IsRow(mainAxis);

        for (int ci = 0; ci < flexLine.ItemsInFlow.Count; ci++)
        {
            var child = flexLine.ItemsInFlow[ci];
            var childLayout = child.Layout;

            if (child.Style.FlexStartMarginIsAuto(mainAxis, direction) &&
                flexLine.Layout.RemainingFreeSpace > 0.0f)
            {
                flexLine.Layout.MainDim += flexLine.Layout.RemainingFreeSpace / (float)flexLine.NumberOfAutoMargins;
            }

            if (performLayout)
            {
                child.SetLayoutPosition(
                    childLayout.GetPosition(FlexDirectionHelper.FlexStartEdge(mainAxis)) + flexLine.Layout.MainDim,
                    FlexDirectionHelper.FlexStartEdge(mainAxis));
            }

            if (ci != flexLine.ItemsInFlow.Count - 1)
                flexLine.Layout.MainDim += betweenMainDim;

            if (child.Style.FlexEndMarginIsAuto(mainAxis, direction) &&
                flexLine.Layout.RemainingFreeSpace > 0.0f)
            {
                flexLine.Layout.MainDim += flexLine.Layout.RemainingFreeSpace / (float)flexLine.NumberOfAutoMargins;
            }

            bool canSkipFlex = !performLayout && sizingModeCrossDim == SizingMode.StretchFit;
            if (canSkipFlex)
            {
                flexLine.Layout.MainDim +=
                    child.Style.ComputeMarginForAxis(mainAxis, availableInnerWidth) +
                    YogaFloat.UnwrapOrDefault(childLayout.ComputedFlexBasis);
                flexLine.Layout.CrossDim = availableInnerCrossDim;
            }
            else
            {
                flexLine.Layout.MainDim += child.DimensionWithMargin(mainAxis, availableInnerWidth);

                if (isNodeBaselineLayout)
                {
                    float ascent = BaselineHelper.CalculateBaseline(child) +
                        child.Style.ComputeFlexStartMargin(FlexDirection.Column, direction, availableInnerWidth);
                    float descent =
                        child.Layout.GetMeasuredDimension(YogaDimension.Height) +
                        child.Style.ComputeMarginForAxis(FlexDirection.Column, availableInnerWidth) - ascent;
                    maxAscentForCurrentLine = YogaFloat.MaxOrDefined(maxAscentForCurrentLine, ascent);
                    maxDescentForCurrentLine = YogaFloat.MaxOrDefined(maxDescentForCurrentLine, descent);
                }
                else
                {
                    flexLine.Layout.CrossDim = YogaFloat.MaxOrDefined(
                        flexLine.Layout.CrossDim,
                        child.DimensionWithMargin(crossAxis, availableInnerWidth));
                }
            }
        }
        flexLine.Layout.MainDim += trailingPaddingAndBorderMain;

        if (isNodeBaselineLayout)
            flexLine.Layout.CrossDim = maxAscentForCurrentLine + maxDescentForCurrentLine;
    }

    // ══════════════════════════════════════════════════════════════════════
    // ABSOLUTE LAYOUT (from AbsoluteLayout.cpp)
    // ══════════════════════════════════════════════════════════════════════

    private static void SetFlexStartLayoutPosition(
        YogaNode parent, YogaNode child,
        FlexLayoutDirection direction, FlexDirection axis, float containingBlockWidth)
    {
        float position = child.Style.ComputeFlexStartMargin(axis, direction, containingBlockWidth) +
            parent.Layout.GetBorder(FlexDirectionHelper.FlexStartEdge(axis));

        if (!child.HasErrata(YogaErrata.AbsolutePositionWithoutInsetsExcludesPadding))
            position += parent.Layout.GetPadding(FlexDirectionHelper.FlexStartEdge(axis));

        child.SetLayoutPosition(position, FlexDirectionHelper.FlexStartEdge(axis));
    }

    private static void SetFlexEndLayoutPosition(
        YogaNode parent, YogaNode child,
        FlexLayoutDirection direction, FlexDirection axis, float containingBlockWidth)
    {
        float flexEndPosition = parent.Layout.GetBorder(FlexDirectionHelper.FlexEndEdge(axis)) +
            child.Style.ComputeFlexEndMargin(axis, direction, containingBlockWidth);

        if (!child.HasErrata(YogaErrata.AbsolutePositionWithoutInsetsExcludesPadding))
            flexEndPosition += parent.Layout.GetPadding(FlexDirectionHelper.FlexEndEdge(axis));

        child.SetLayoutPosition(
            TrailingPositionHelper.GetPositionOfOppositeEdge(flexEndPosition, axis, parent, child),
            FlexDirectionHelper.FlexStartEdge(axis));
    }

    private static void SetCenterLayoutPosition(
        YogaNode parent, YogaNode child,
        FlexLayoutDirection direction, FlexDirection axis, float containingBlockWidth)
    {
        float parentContentBoxSize =
            parent.Layout.GetMeasuredDimension(FlexDirectionHelper.Dimension(axis)) -
            parent.Layout.GetBorder(FlexDirectionHelper.FlexStartEdge(axis)) -
            parent.Layout.GetBorder(FlexDirectionHelper.FlexEndEdge(axis));

        if (!child.HasErrata(YogaErrata.AbsolutePositionWithoutInsetsExcludesPadding))
        {
            parentContentBoxSize -= parent.Layout.GetPadding(FlexDirectionHelper.FlexStartEdge(axis));
            parentContentBoxSize -= parent.Layout.GetPadding(FlexDirectionHelper.FlexEndEdge(axis));
        }

        float childOuterSize =
            child.Layout.GetMeasuredDimension(FlexDirectionHelper.Dimension(axis)) +
            child.Style.ComputeMarginForAxis(axis, containingBlockWidth);

        float position = (parentContentBoxSize - childOuterSize) / 2.0f +
            parent.Layout.GetBorder(FlexDirectionHelper.FlexStartEdge(axis)) +
            child.Style.ComputeFlexStartMargin(axis, direction, containingBlockWidth);

        if (!child.HasErrata(YogaErrata.AbsolutePositionWithoutInsetsExcludesPadding))
            position += parent.Layout.GetPadding(FlexDirectionHelper.FlexStartEdge(axis));

        child.SetLayoutPosition(position, FlexDirectionHelper.FlexStartEdge(axis));
    }

    /// <summary>
    /// Positions an absolutely-positioned child on the main axis using parent's justify-content.
    /// Only called when the child has no inset on that axis.
    /// </summary>
    private static void JustifyAbsoluteChild(
        YogaNode parent, YogaNode child,
        FlexLayoutDirection direction, FlexDirection mainAxis, float containingBlockWidth)
    {
        var justify = parent.Style.JustifyContent;
        switch (justify)
        {
            case FlexJustify.Start:
            case FlexJustify.Auto:
            case FlexJustify.Stretch:
            case FlexJustify.FlexStart:
            case FlexJustify.SpaceBetween:
                SetFlexStartLayoutPosition(parent, child, direction, mainAxis, containingBlockWidth);
                break;
            case FlexJustify.End:
            case FlexJustify.FlexEnd:
                SetFlexEndLayoutPosition(parent, child, direction, mainAxis, containingBlockWidth);
                break;
            case FlexJustify.Center:
            case FlexJustify.SpaceAround:
            case FlexJustify.SpaceEvenly:
                SetCenterLayoutPosition(parent, child, direction, mainAxis, containingBlockWidth);
                break;
        }
    }

    private static void AlignAbsoluteChild(
        YogaNode parent, YogaNode child,
        FlexLayoutDirection direction, FlexDirection crossAxis, float containingBlockWidth)
    {
        var itemAlign = AlignHelper.ResolveChildAlignment(parent, child);
        var parentWrap = parent.Style.FlexWrap;
        if (parentWrap == FlexWrap.WrapReverse)
        {
            if (itemAlign == FlexAlign.FlexEnd)
                itemAlign = FlexAlign.FlexStart;
            else if (itemAlign != FlexAlign.Center)
                itemAlign = FlexAlign.FlexEnd;
        }

        switch (itemAlign)
        {
            case FlexAlign.Start:
            case FlexAlign.Auto:
            case FlexAlign.FlexStart:
            case FlexAlign.Baseline:
            case FlexAlign.SpaceAround:
            case FlexAlign.SpaceBetween:
            case FlexAlign.Stretch:
            case FlexAlign.SpaceEvenly:
                SetFlexStartLayoutPosition(parent, child, direction, crossAxis, containingBlockWidth);
                break;
            case FlexAlign.End:
            case FlexAlign.FlexEnd:
                SetFlexEndLayoutPosition(parent, child, direction, crossAxis, containingBlockWidth);
                break;
            case FlexAlign.Center:
                SetCenterLayoutPosition(parent, child, direction, crossAxis, containingBlockWidth);
                break;
        }
    }

    private static void PositionAbsoluteChild(
        YogaNode containingNode, YogaNode parent, YogaNode child,
        FlexLayoutDirection direction, FlexDirection axis, bool isMainAxis,
        float containingBlockWidth, float containingBlockHeight)
    {
        bool isAxisRow = FlexDirectionHelper.IsRow(axis);
        float containingBlockSize = isAxisRow ? containingBlockWidth : containingBlockHeight;

        if (child.Style.IsInlineStartPositionDefined(axis, direction) &&
            !child.Style.IsInlineStartPositionAuto(axis, direction))
        {
            float positionRelativeToInlineStart =
                child.Style.ComputeInlineStartPosition(axis, direction, containingBlockSize) +
                containingNode.Style.ComputeInlineStartBorder(axis, direction) +
                child.Style.ComputeInlineStartMargin(axis, direction, containingBlockSize);
            float positionRelativeToFlexStart =
                FlexDirectionHelper.InlineStartEdge(axis, direction) != FlexDirectionHelper.FlexStartEdge(axis)
                    ? TrailingPositionHelper.GetPositionOfOppositeEdge(positionRelativeToInlineStart, axis, containingNode, child)
                    : positionRelativeToInlineStart;
            child.SetLayoutPosition(positionRelativeToFlexStart, FlexDirectionHelper.FlexStartEdge(axis));
        }
        else if (child.Style.IsInlineEndPositionDefined(axis, direction) &&
                 !child.Style.IsInlineEndPositionAuto(axis, direction))
        {
            float positionRelativeToInlineStart =
                containingNode.Layout.GetMeasuredDimension(FlexDirectionHelper.Dimension(axis)) -
                child.Layout.GetMeasuredDimension(FlexDirectionHelper.Dimension(axis)) -
                containingNode.Style.ComputeInlineEndBorder(axis, direction) -
                child.Style.ComputeInlineEndMargin(axis, direction, containingBlockSize) -
                child.Style.ComputeInlineEndPosition(axis, direction, containingBlockSize);
            float positionRelativeToFlexStart =
                FlexDirectionHelper.InlineStartEdge(axis, direction) != FlexDirectionHelper.FlexStartEdge(axis)
                    ? TrailingPositionHelper.GetPositionOfOppositeEdge(positionRelativeToInlineStart, axis, containingNode, child)
                    : positionRelativeToInlineStart;
            child.SetLayoutPosition(positionRelativeToFlexStart, FlexDirectionHelper.FlexStartEdge(axis));
        }
        else
        {
            if (isMainAxis)
                JustifyAbsoluteChild(parent, child, direction, axis, containingBlockWidth);
            else
                AlignAbsoluteChild(parent, child, direction, axis, containingBlockWidth);
        }
    }

    /// <summary>
    /// Measures and positions a single absolutely-positioned child.
    /// Resolves containing block, computes available space, runs recursive layout,
    /// then positions using insets or fallback to justify/align.
    /// </summary>
    private static void LayoutAbsoluteChild(
        YogaNode containingNode, YogaNode node, YogaNode child,
        float containingBlockWidth, float containingBlockHeight,
        SizingMode widthMode, FlexLayoutDirection direction,
        uint depth, uint generationCount)
    {
        var mainAxis = FlexDirectionHelper.ResolveDirection(node.Style.FlexDirection, direction);
        var crossAxis = FlexDirectionHelper.ResolveCrossDirection(mainAxis, direction);
        bool isMainAxisRow = FlexDirectionHelper.IsRow(mainAxis);

        float childWidth = float.NaN;
        float childHeight = float.NaN;
        var childWidthSizingMode = SizingMode.MaxContent;
        var childHeightSizingMode = SizingMode.MaxContent;

        float marginRow = child.Style.ComputeMarginForAxis(FlexDirection.Row, containingBlockWidth);
        float marginColumn = child.Style.ComputeMarginForAxis(FlexDirection.Column, containingBlockWidth);

        if (child.HasDefiniteLength(YogaDimension.Width, containingBlockWidth))
        {
            childWidth = child.GetResolvedDimension(direction, YogaDimension.Width, containingBlockWidth, containingBlockWidth) + marginRow;
        }
        else
        {
            if (child.Style.IsFlexStartPositionDefined(FlexDirection.Row, direction) &&
                child.Style.IsFlexEndPositionDefined(FlexDirection.Row, direction) &&
                !child.Style.IsFlexStartPositionAuto(FlexDirection.Row, direction) &&
                !child.Style.IsFlexEndPositionAuto(FlexDirection.Row, direction))
            {
                childWidth =
                    containingNode.Layout.GetMeasuredDimension(YogaDimension.Width) -
                    (containingNode.Style.ComputeFlexStartBorder(FlexDirection.Row, direction) +
                     containingNode.Style.ComputeFlexEndBorder(FlexDirection.Row, direction)) -
                    (child.Style.ComputeFlexStartPosition(FlexDirection.Row, direction, containingBlockWidth) +
                     child.Style.ComputeFlexEndPosition(FlexDirection.Row, direction, containingBlockWidth));
                childWidth = BoundAxisHelper.BoundAxis(
                    child, FlexDirection.Row, direction, childWidth, containingBlockWidth, containingBlockWidth);
            }
        }

        if (child.HasDefiniteLength(YogaDimension.Height, containingBlockHeight))
        {
            childHeight = child.GetResolvedDimension(direction, YogaDimension.Height, containingBlockHeight, containingBlockWidth) + marginColumn;
        }
        else
        {
            if (child.Style.IsFlexStartPositionDefined(FlexDirection.Column, direction) &&
                child.Style.IsFlexEndPositionDefined(FlexDirection.Column, direction) &&
                !child.Style.IsFlexStartPositionAuto(FlexDirection.Column, direction) &&
                !child.Style.IsFlexEndPositionAuto(FlexDirection.Column, direction))
            {
                childHeight =
                    containingNode.Layout.GetMeasuredDimension(YogaDimension.Height) -
                    (containingNode.Style.ComputeFlexStartBorder(FlexDirection.Column, direction) +
                     containingNode.Style.ComputeFlexEndBorder(FlexDirection.Column, direction)) -
                    (child.Style.ComputeFlexStartPosition(FlexDirection.Column, direction, containingBlockHeight) +
                     child.Style.ComputeFlexEndPosition(FlexDirection.Column, direction, containingBlockHeight));
                childHeight = BoundAxisHelper.BoundAxis(
                    child, FlexDirection.Column, direction, childHeight, containingBlockHeight, containingBlockWidth);
            }
        }

        // Aspect ratio
        if (YogaFloat.IsUndefined(childWidth) ^ YogaFloat.IsUndefined(childHeight))
        {
            if (YogaFloat.IsDefined(child.Style.AspectRatio))
            {
                if (YogaFloat.IsUndefined(childWidth))
                    childWidth = marginRow + (childHeight - marginColumn) * child.Style.AspectRatio;
                else if (YogaFloat.IsUndefined(childHeight))
                    childHeight = marginColumn + (childWidth - marginRow) / child.Style.AspectRatio;
            }
        }

        // If still missing dimension(s), measure content
        if (YogaFloat.IsUndefined(childWidth) || YogaFloat.IsUndefined(childHeight))
        {
            childWidthSizingMode = YogaFloat.IsUndefined(childWidth) ? SizingMode.MaxContent : SizingMode.StretchFit;
            childHeightSizingMode = YogaFloat.IsUndefined(childHeight) ? SizingMode.MaxContent : SizingMode.StretchFit;

            if (!isMainAxisRow && YogaFloat.IsUndefined(childWidth) &&
                widthMode != SizingMode.MaxContent &&
                YogaFloat.IsDefined(containingBlockWidth) && containingBlockWidth > 0)
            {
                childWidth = containingBlockWidth;
                childWidthSizingMode = SizingMode.FitContent;
            }

            CalculateLayoutInternal(
                child, childWidth, childHeight, direction,
                childWidthSizingMode, childHeightSizingMode,
                containingBlockWidth, containingBlockHeight,
                false, depth, generationCount);

            childWidth = child.Layout.GetMeasuredDimension(YogaDimension.Width) +
                child.Style.ComputeMarginForAxis(FlexDirection.Row, containingBlockWidth);
            childHeight = child.Layout.GetMeasuredDimension(YogaDimension.Height) +
                child.Style.ComputeMarginForAxis(FlexDirection.Column, containingBlockWidth);
        }

        CalculateLayoutInternal(
            child, childWidth, childHeight, direction,
            SizingMode.StretchFit, SizingMode.StretchFit,
            containingBlockWidth, containingBlockHeight,
            true, depth, generationCount);

        PositionAbsoluteChild(
            containingNode, node, child, direction, mainAxis, true,
            containingBlockWidth, containingBlockHeight);
        PositionAbsoluteChild(
            containingNode, node, child, direction, crossAxis, false,
            containingBlockWidth, containingBlockHeight);
    }

    private static bool LayoutAbsoluteDescendants(
        YogaNode containingNode, YogaNode currentNode,
        SizingMode widthSizingMode, FlexLayoutDirection currentNodeDirection,
        uint currentDepth, uint generationCount,
        float currentNodeLeftOffsetFromContainingBlock,
        float currentNodeTopOffsetFromContainingBlock,
        float containingNodeAvailableInnerWidth,
        float containingNodeAvailableInnerHeight)
    {
        bool hasNewLayout = false;

        foreach (var child in currentNode.GetLayoutChildren())
        {
            if (child.Style.Display == YogaDisplay.None)
                continue;

            if (child.Style.PositionType == FlexPositionType.Absolute)
            {
                bool absoluteErrata = currentNode.HasErrata(YogaErrata.AbsolutePercentAgainstInnerSize);
                float containingBlockWidth = absoluteErrata
                    ? containingNodeAvailableInnerWidth
                    : containingNode.Layout.GetMeasuredDimension(YogaDimension.Width) -
                      containingNode.Style.ComputeBorderForAxis(FlexDirection.Row);
                float containingBlockHeight = absoluteErrata
                    ? containingNodeAvailableInnerHeight
                    : containingNode.Layout.GetMeasuredDimension(YogaDimension.Height) -
                      containingNode.Style.ComputeBorderForAxis(FlexDirection.Column);

                LayoutAbsoluteChild(
                    containingNode, currentNode, child,
                    containingBlockWidth, containingBlockHeight,
                    widthSizingMode, currentNodeDirection,
                    currentDepth, generationCount);

                hasNewLayout = hasNewLayout || child.HasNewLayout;

                var parentMainAxis = FlexDirectionHelper.ResolveDirection(currentNode.Style.FlexDirection, currentNodeDirection);
                var parentCrossAxis = FlexDirectionHelper.ResolveCrossDirection(parentMainAxis, currentNodeDirection);

                if (TrailingPositionHelper.NeedsTrailingPosition(parentMainAxis))
                {
                    bool mainInsetsDefined = FlexDirectionHelper.IsRow(parentMainAxis)
                        ? child.Style.HorizontalInsetsDefined
                        : child.Style.VerticalInsetsDefined;
                    TrailingPositionHelper.SetChildTrailingPosition(
                        mainInsetsDefined ? containingNode : currentNode, child, parentMainAxis);
                }
                if (TrailingPositionHelper.NeedsTrailingPosition(parentCrossAxis))
                {
                    bool crossInsetsDefined = FlexDirectionHelper.IsRow(parentCrossAxis)
                        ? child.Style.HorizontalInsetsDefined
                        : child.Style.VerticalInsetsDefined;
                    TrailingPositionHelper.SetChildTrailingPosition(
                        crossInsetsDefined ? containingNode : currentNode, child, parentCrossAxis);
                }

                float childLeftPosition = child.Layout.GetPosition(YogaPhysicalEdge.Left);
                float childTopPosition = child.Layout.GetPosition(YogaPhysicalEdge.Top);

                float childLeftOffsetFromParent = child.Style.HorizontalInsetsDefined
                    ? (childLeftPosition - currentNodeLeftOffsetFromContainingBlock)
                    : childLeftPosition;
                float childTopOffsetFromParent = child.Style.VerticalInsetsDefined
                    ? (childTopPosition - currentNodeTopOffsetFromContainingBlock)
                    : childTopPosition;

                child.SetLayoutPosition(childLeftOffsetFromParent, YogaPhysicalEdge.Left);
                child.SetLayoutPosition(childTopOffsetFromParent, YogaPhysicalEdge.Top);
            }
            else if (child.Style.PositionType == FlexPositionType.Static &&
                     !child.AlwaysFormsContainingBlock)
            {
                var childDirection = child.ResolveDirection(currentNodeDirection);
                float childLeftOffsetFromContainingBlock =
                    currentNodeLeftOffsetFromContainingBlock +
                    child.Layout.GetPosition(YogaPhysicalEdge.Left);
                float childTopOffsetFromContainingBlock =
                    currentNodeTopOffsetFromContainingBlock +
                    child.Layout.GetPosition(YogaPhysicalEdge.Top);

                hasNewLayout = LayoutAbsoluteDescendants(
                    containingNode, child, widthSizingMode, childDirection,
                    currentDepth + 1, generationCount,
                    childLeftOffsetFromContainingBlock, childTopOffsetFromContainingBlock,
                    containingNodeAvailableInnerWidth, containingNodeAvailableInnerHeight) || hasNewLayout;

                if (hasNewLayout)
                    child.HasNewLayout = hasNewLayout;
            }
        }
        return hasNewLayout;
    }
}
