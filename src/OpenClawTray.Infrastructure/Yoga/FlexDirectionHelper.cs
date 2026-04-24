// C# port of Meta's Yoga layout engine FlexDirection utilities.
// Ported from yoga/algorithm/FlexDirection.h

using OpenClawTray.Infrastructure.Layout;

namespace OpenClawTray.Infrastructure.Layout;

/// <summary>
/// Direction-aware utility functions for resolving flex axes to physical edges.
/// </summary>
internal static class FlexDirectionHelper
{
    public static bool IsRow(FlexDirection flexDirection)
        => flexDirection == FlexDirection.Row || flexDirection == FlexDirection.RowReverse;

    public static bool IsColumn(FlexDirection flexDirection)
        => flexDirection == FlexDirection.Column || flexDirection == FlexDirection.ColumnReverse;

    /// <summary>Apply RTL transformation to flex direction.</summary>
    public static FlexDirection ResolveDirection(FlexDirection flexDirection, FlexLayoutDirection direction)
    {
        if (direction == FlexLayoutDirection.RTL)
        {
            if (flexDirection == FlexDirection.Row) return FlexDirection.RowReverse;
            if (flexDirection == FlexDirection.RowReverse) return FlexDirection.Row;
        }
        return flexDirection;
    }

    /// <summary>Get the perpendicular (cross) direction.</summary>
    public static FlexDirection ResolveCrossDirection(FlexDirection flexDirection, FlexLayoutDirection direction)
        => IsColumn(flexDirection)
            ? ResolveDirection(FlexDirection.Row, direction)
            : FlexDirection.Column;

    /// <summary>Get the physical edge at the flex-start of an axis.</summary>
    public static YogaPhysicalEdge FlexStartEdge(FlexDirection flexDirection) => flexDirection switch
    {
        FlexDirection.Column => YogaPhysicalEdge.Top,
        FlexDirection.ColumnReverse => YogaPhysicalEdge.Bottom,
        FlexDirection.Row => YogaPhysicalEdge.Left,
        FlexDirection.RowReverse => YogaPhysicalEdge.Right,
        _ => throw new ArgumentOutOfRangeException(nameof(flexDirection)),
    };

    /// <summary>Get the physical edge at the flex-end of an axis.</summary>
    public static YogaPhysicalEdge FlexEndEdge(FlexDirection flexDirection) => flexDirection switch
    {
        FlexDirection.Column => YogaPhysicalEdge.Bottom,
        FlexDirection.ColumnReverse => YogaPhysicalEdge.Top,
        FlexDirection.Row => YogaPhysicalEdge.Right,
        FlexDirection.RowReverse => YogaPhysicalEdge.Left,
        _ => throw new ArgumentOutOfRangeException(nameof(flexDirection)),
    };

    /// <summary>Get the inline-start edge (direction-aware).</summary>
    public static YogaPhysicalEdge InlineStartEdge(FlexDirection flexDirection, FlexLayoutDirection direction)
    {
        if (IsRow(flexDirection))
            return direction == FlexLayoutDirection.RTL ? YogaPhysicalEdge.Right : YogaPhysicalEdge.Left;
        return YogaPhysicalEdge.Top;
    }

    /// <summary>Get the inline-end edge (direction-aware).</summary>
    public static YogaPhysicalEdge InlineEndEdge(FlexDirection flexDirection, FlexLayoutDirection direction)
    {
        if (IsRow(flexDirection))
            return direction == FlexLayoutDirection.RTL ? YogaPhysicalEdge.Left : YogaPhysicalEdge.Right;
        return YogaPhysicalEdge.Bottom;
    }

    /// <summary>Get the dimension (Width or Height) for a flex direction.</summary>
    public static YogaDimension Dimension(FlexDirection flexDirection) => flexDirection switch
    {
        FlexDirection.Column => YogaDimension.Height,
        FlexDirection.ColumnReverse => YogaDimension.Height,
        FlexDirection.Row => YogaDimension.Width,
        FlexDirection.RowReverse => YogaDimension.Width,
        _ => throw new ArgumentOutOfRangeException(nameof(flexDirection)),
    };
}
