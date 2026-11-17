using System.Collections.Immutable;

namespace OpenClaw.Shared.Dashboards;

/// <summary>Placed widget rectangle in grid cells (zero-based).</summary>
public readonly record struct BoardGridRect(string Name, int X, int Y, int W, int H);

/// <summary>Grid input: requested span and canonical order of one widget.</summary>
public readonly record struct BoardGridItem(string Name, int W, int H, int Order);

/// <summary>
/// Board grid geometry, matching upstream <c>ui/src/lib/board/grid.ts</c>:
/// a fixed 12-column grid where items in canonical order take the first
/// row-major cell that fits, so earlier widgets keep priority and later
/// widgets pack tightly.
/// </summary>
public static class BoardGridLayout
{
    public const int Columns = 12;
    public const int MaxItemRows = 20;
    public const double RowHeight = 56;
    public const double Gap = 12;
    public const int AutoMinRows = 2;
    public const double FrameInset = 12;
    public const double BorderWidth = 1;

    /// <summary>Height of the native widget title row above the content frame.</summary>
    public const double ChromeHeaderHeight = 40;

    public static ImmutableArray<BoardGridRect> Layout(IEnumerable<BoardGridItem> items, int maxItemRows = MaxItemRows)
    {
        var canonical = Canonical(items, maxItemRows);
        if (canonical.All(item => item.W == Columns))
        {
            // Full-width widgets leave no gaps for later widgets to fill.
            var y = 0;
            var stacked = ImmutableArray.CreateBuilder<BoardGridRect>(canonical.Count);
            foreach (var item in canonical)
            {
                stacked.Add(new BoardGridRect(item.Name, 0, y, item.W, item.H));
                y += item.H;
            }
            return stacked.MoveToImmutable();
        }

        var occupied = new List<int>();
        var rects = ImmutableArray.CreateBuilder<BoardGridRect>(canonical.Count);
        foreach (var item in canonical)
            rects.Add(PlaceFirstFit(occupied, item));
        return rects.MoveToImmutable();
    }

    /// <summary>Total rows used by a layout.</summary>
    public static int RowCount(IEnumerable<BoardGridRect> rects) =>
        rects.Select(rect => rect.Y + rect.H).DefaultIfEmpty(0).Max();

    /// <summary>
    /// Rows an auto-height HTML widget occupies for its reported content
    /// height. Fixed-height and non-HTML widgets keep <see cref="BoardWidget.SizeH"/>.
    /// </summary>
    public static int EffectiveRows(BoardWidget widget, double? contentHeightPx, int maxAutoRows = MaxItemRows)
    {
        if (widget.ContentKind != BoardWidgetContentKind.Html ||
            widget.HeightMode == BoardWidgetHeightMode.Fixed ||
            contentHeightPx is not { } content ||
            !double.IsFinite(content) || content <= 0)
            return widget.SizeH;

        var required = content + ChromeHeaderHeight + BorderWidth * 2 +
            (widget.Presentation == BoardWidgetPresentation.Card ? FrameInset * 2 : 0);
        var rows = (int)Math.Ceiling((required + Gap) / (RowHeight + Gap));
        return Math.Min(maxAutoRows, Math.Max(AutoMinRows, rows));
    }

    private static List<BoardGridItem> Canonical(IEnumerable<BoardGridItem> items, int maxItemRows) =>
        [.. items
            .Select(item => item with
            {
                W = Math.Clamp(item.W, 1, Columns),
                H = Math.Clamp(item.H, 1, Math.Max(1, maxItemRows)),
            })
            .OrderBy(item => item.Order)
            .ThenBy(item => item.Name, StringComparer.Ordinal)];

    private static BoardGridRect PlaceFirstFit(List<int> occupied, BoardGridItem item)
    {
        for (var y = 0; ; y++)
        {
            for (var x = 0; x <= Columns - item.W; x++)
            {
                if (!Fits(occupied, x, y, item.W, item.H))
                    continue;
                var columns = ((1 << item.W) - 1) << x;
                for (var row = y; row < y + item.H; row++)
                {
                    while (occupied.Count <= row)
                        occupied.Add(0);
                    occupied[row] |= columns;
                }
                return new BoardGridRect(item.Name, x, y, item.W, item.H);
            }
        }
    }

    private static bool Fits(List<int> occupied, int x, int y, int w, int h)
    {
        var columns = ((1 << w) - 1) << x;
        for (var row = y; row < y + h; row++)
        {
            if (row < occupied.Count && (occupied[row] & columns) != 0)
                return false;
        }
        return true;
    }
}
