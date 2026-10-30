using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClawTray.Presentation.Usage;
using static Microsoft.UI.Reactor.Factories;
using static OpenClawTray.Pages.Usage.UsageUi;

namespace OpenClawTray.Pages.Usage;

/// <summary>
/// Native charts for the Usage page. Bars are focusable buttons whose stacked segments use proportional
/// grid rows, so charts scale with layout, follow theme brushes and expose one automation item per bar.
/// </summary>
internal static class UsageCharts
{
    public const double PlotHeight = 180;
    public const double AxisWidth = 56;

    public record BarColumn(
        string Key, double Total, IReadOnlyList<UsageChartSegment> Segments, string Label, string AccessibleText,
        bool Selected, bool Pending, bool LowerBound);

    /// <summary>Stacked column chart with a value axis. <paramref name="onActivate"/> receives the column key.</summary>
    public static Element Columns(
        IReadOnlyList<BarColumn> columns, IReadOnlyList<UsageSeries> series, double ceiling, Func<double, string> axisLabel,
        Action<string, bool>? onActivate, string automationId, double plotHeight = PlotHeight)
    {
        var brushes = SeriesBrushes(series);
        var anySelected = columns.Any(c => c.Selected);
        var labelEvery = Math.Max(1, (int)Math.Ceiling(columns.Count / 8.0));
        // Labels are anchored on the last day, so it is always labelled. Each label spans back to the previous
        // anchor and ends under its own day, so labels never overlap and narrow columns do not clip them.
        bool Labelled(int index) => (columns.Count - 1 - index) % labelEvery == 0;

        var plot = Grid(columns.Select(_ => GridSize.Star()).ToArray(), [GridSize.Px(plotHeight), GridSize.Auto],
            columns.SelectMany((column, index) =>
            {
                var first = Math.Max(0, index - labelEvery + 1);
                return new Element?[]
                {
                    Bar(column, brushes, ceiling, anySelected, onActivate, automationId).Enter().Grid(row: 0, column: index).WithKey("bar:" + column.Key),
                    Labelled(index)
                        ? TextBlock(column.Label).FontSize(11).Foreground(TertiaryText)
                            .TextTrimming(TextTrimming.Clip)
                            .HAlign(labelEvery == 1 ? HorizontalAlignment.Center : HorizontalAlignment.Right)
                            .Margin(0, 6, 0, 0)
                            .AccessibilityView(AccessibilityView.Raw).Grid(row: 1, column: first, columnSpan: index - first + 1)
                            .WithKey("label:" + column.Key)
                        : null,
                };
            }).ToArray());
        // A cascade reads well for a week or a month; for long ranges it would delay the newest bars by seconds.
        if (columns.Count <= 31) plot = plot.Cascade(14);

        var axis = Grid([GridSize.Star()], [GridSize.Star(), GridSize.Star()],
                TextBlock(axisLabel(ceiling)).FontSize(11).Foreground(TertiaryText).VAlign(VerticalAlignment.Top).Grid(row: 0),
                TextBlock(axisLabel(ceiling / 2)).FontSize(11).Foreground(TertiaryText).VAlign(VerticalAlignment.Top).Grid(row: 1))
            .Height(plotHeight)
            .VAlign(VerticalAlignment.Top)
            .AccessibilityView(AccessibilityView.Raw);

        var gridlines = Grid([GridSize.Star()], [GridSize.Star(), GridSize.Star()],
                Border(null).Height(1).Background(Track).VAlign(VerticalAlignment.Top).Grid(row: 0),
                Border(null).Height(1).Background(Track).VAlign(VerticalAlignment.Top).Grid(row: 1))
            .Height(plotHeight)
            .VAlign(VerticalAlignment.Top)
            .AccessibilityView(AccessibilityView.Raw);

        return Grid([GridSize.Px(AxisWidth), GridSize.Star()], [GridSize.Auto],
                axis.Grid(column: 0),
                gridlines.Grid(column: 1),
                Border(null).Height(1).Background(CardStroke).VAlign(VerticalAlignment.Top).Margin(0, plotHeight, 0, 0).Grid(column: 1),
                plot.Grid(column: 1))
            .AutomationId(automationId);
    }

    private static Element Bar(
        BarColumn column, IReadOnlyDictionary<string, ThemeRef> brushes, double ceiling, bool anySelected,
        Action<string, bool>? onActivate, string automationId)
    {
        var segments = column.Segments.Where(s => s.Value > 0).ToList();
        var total = segments.Sum(s => s.Value);
        var headroom = Math.Max(ceiling - total, 0);
        // Top-to-bottom: headroom, then segments with the first series at the bottom (upstream stack order).
        var rows = new List<GridSize> { GridSize.Star(Math.Max(headroom, ceiling * 1e-6)) };
        var stack = new List<Element?>();
        var reversed = segments.AsEnumerable().Reverse().ToList();
        for (var i = 0; i < reversed.Count; i++)
        {
            rows.Add(GridSize.Star(reversed[i].Value));
            stack.Add(Border(null)
                .Background(brushes.TryGetValue(reversed[i].SeriesKey, out var brush) ? brush : Accent)
                .CornerRadius(i == 0 ? 2 : 0, i == 0 ? 2 : 0, 0, 0)
                .Grid(row: i + 1));
        }
        if (column.Pending)
        {
            stack.Add(Border(null).Height(2).Background(Track).VAlign(VerticalAlignment.Bottom).Grid(row: rows.Count - 1));
        }

        var visual = Grid([GridSize.Star()], rows.ToArray(), stack.ToArray())
            .Margin(1, 0, 1, 0)
            .MaxWidth(28)
            .HAlign(HorizontalAlignment.Stretch)
            .Opacity(anySelected && !column.Selected ? 0.4 : 1);

        Element content = Border(visual)
            .Background(column.Selected ? Track : Theme.Ref("SubtleFillColorTransparentBrush"))
            .CornerRadius(4)
            .Padding(1, 0, 1, 0);

        if (onActivate is null)
            return content.ToolTip(column.AccessibleText).AutomationName(column.AccessibleText);

        return Button(content, () => onActivate(column.Key, ControlPressed()))
            .Padding(0)
            .MinWidth(0)
            .MinHeight(0)
            .BorderThickness(0)
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch)
            // Button centers content by default, which would collapse the star-row bar to zero height.
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .VerticalContentAlignment(VerticalAlignment.Stretch)
            .Resources(r => r
                .Set("ButtonBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                .Set("ButtonBackgroundPointerOver", Theme.Ref("SubtleFillColorSecondaryBrush"))
                .Set("ButtonBackgroundPressed", Theme.Ref("SubtleFillColorTertiaryBrush"))
                .Set("ButtonBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush"))
                .Set("ButtonBorderBrushPointerOver", Theme.Ref("SubtleFillColorTransparentBrush"))
                .Set("ButtonBorderBrushPressed", Theme.Ref("SubtleFillColorTransparentBrush")))
            .ToolTip(column.AccessibleText)
            .AutomationName(column.AccessibleText)
            .ItemStatus(column.Selected ? S("Usage_Selected") : "")
            .AutomationId($"{automationId}_{column.Key}")
            .WithKey(column.Key);
    }

    public static Element Legend(IReadOnlyList<UsageSeries> series) =>
        series.Count <= 1
            ? Empty()
            : Flow(series.Select(s => LegendItem(SeriesLabel(s), SeriesBrushes(series)[s.Key]).Margin(0, 0, 16, 4).WithKey(s.Key)).ToArray())
                .AutomationId("UsageChartLegend");

    // ── activity mosaic ──

    public static Element ActivityStrip(
        IReadOnlyList<UsageActivityCell> cells, Func<int, string> label, Func<int, bool> showLabel,
        Action<int, bool>? onActivate, string automationId, double cellHeight, bool valuesInside = false)
    {
        var anySelected = cells.Any(c => c.Selected);
        return Grid(cells.Select(_ => GridSize.Star()).ToArray(), [GridSize.Px(cellHeight), GridSize.Auto],
                cells.SelectMany(cell => new Element?[]
                {
                    Cell(cell, label(cell.Index), anySelected, onActivate, automationId, valuesInside).Grid(row: 0, column: cell.Index)
                        .WithKey($"cell:{cell.Index}"),
                    !valuesInside && showLabel(cell.Index)
                        ? TextBlock(label(cell.Index)).FontSize(11).Foreground(TertiaryText).HAlign(HorizontalAlignment.Left)
                            .Margin(2, 4, 0, 0).AccessibilityView(AccessibilityView.Raw)
                            .Grid(row: 1, column: cell.Index, columnSpan: Math.Min(4, cells.Count - cell.Index))
                            .WithKey($"label:{cell.Index}")
                        : null,
                }).ToArray())
            .AutomationId(automationId);
    }

    private static Element Cell(
        UsageActivityCell cell, string label, bool anySelected, Action<int, bool>? onActivate, string automationId, bool valuesInside)
    {
        var text = F("Usage_Activity_CellFormat", label, UsageUi.Tokens(cell.Tokens));
        var minimumOpacity = valuesInside ? ThemeResource.Double("UsageActivityMinimumOpacity") : 0.18;
        Element swatch = Border(null)
            .Background(cell.Tokens > 0 ? Accent : Track)
            .Opacity(cell.Tokens > 0 ? minimumOpacity + (1 - minimumOpacity) * cell.Intensity : 1)
            .CornerRadius(4)
            .Margin(1.5);
        if (valuesInside)
        {
            // Like the web page's day-of-week tiles: the day and its total sit on the tile. Strong tiles use on-accent text.
            var strong = cell.Tokens > 0 && (minimumOpacity == 1 || cell.Intensity > 0.55);
            var ink = strong ? Theme.Ref("UsageActivityTextBrush") : Theme.Ref("TextFillColorPrimaryBrush");
            swatch = Grid([GridSize.Star()], [GridSize.Star()],
                swatch,
                VStack(0,
                        TextBlock(label).FontSize(12).Foreground(ink).Opacity(minimumOpacity == 1 ? 1 : 0.85),
                        TextBlock(UsageUi.Tokens(cell.Tokens)).FontSize(15).FontWeight(FontWeights.SemiBold).Foreground(ink))
                    .AccessibilityView(AccessibilityView.Raw)
                    .Margin(10, 6, 6, 6)
                    .VAlign(VerticalAlignment.Center));
        }
        Element framed = Border(swatch)
            .WithBorder(cell.Selected ? AccentText : Theme.Ref("SubtleFillColorTransparentBrush"), 2)
            .CornerRadius(6)
            .Opacity(anySelected && !cell.Selected ? 0.55 : 1);
        if (onActivate is null)
            return framed.ToolTip(text).AutomationName(text).AutomationId($"{automationId}_{cell.Index}");
        return Button(framed, () => onActivate(cell.Index, ControlPressed()))
            .Padding(0).MinWidth(0).MinHeight(0).BorderThickness(0)
            .HAlign(HorizontalAlignment.Stretch).VAlign(VerticalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch).VerticalContentAlignment(VerticalAlignment.Stretch)
            .Resources(r => r
                .Set("ButtonBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                .Set("ButtonBackgroundPointerOver", Theme.Ref("SubtleFillColorSecondaryBrush"))
                .Set("ButtonBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush"))
                .Set("ButtonBorderBrushPointerOver", Theme.Ref("SubtleFillColorTransparentBrush")))
            .ToolTip(text)
            .AutomationName(text)
            .ItemStatus(cell.Selected ? S("Usage_Selected") : "")
            .AutomationId($"{automationId}_{cell.Index}")
            .WithKey(cell.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // ── long-range heatmap ──

    public static Element Heatmap(UsageHeatmap heatmap)
    {
        const double cell = 11;
        var weeks = heatmap.Weeks;
        var dayLabels = new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday };
        var grid = Grid(
            new[] { GridSize.Px(32) }.Concat(weeks.Select(_ => GridSize.Px(cell + 3))).ToArray(),
            Enumerable.Range(0, 7).Select(_ => GridSize.Px(cell + 3)).ToArray(),
            dayLabels.Select(d => (Element)TextBlock(UsageFormat.ShortWeekday(d, Culture)).FontSize(10).Foreground(TertiaryText)
                    .VAlign(VerticalAlignment.Center).AccessibilityView(AccessibilityView.Raw).Grid(row: (int)d, column: 0)
                    .WithKey("day:" + (int)d))
                .Concat(weeks.SelectMany((week, w) => week.Select(c =>
                {
                    var tip = c.InRange
                        ? F("Usage_Heatmap_CellFormat", UsageFormat.FullDate(c.Date, Culture), UsageUi.Tokens(c.Tokens, c.LowerBound))
                        : "";
                    Element square = Border(null)
                        .Size(cell, cell)
                        .CornerRadius(2)
                        .Background(c.Level > 0 ? Accent : Track)
                        .Opacity(!c.InRange ? 0.25 : c.Level switch { 1 => 0.3, 2 => 0.5, 3 => 0.75, 4 => 1.0, _ => 1.0 })
                        .AccessibilityView(AccessibilityView.Raw)
                        .Grid(row: (int)c.Date.DayOfWeek, column: w + 1)
                        .WithKey(UsageFilters.Iso(c.Date));
                    return c.InRange ? square.ToolTip(tip) : square;
                })))
                .ToArray());
        var legend = HStack(4,
            TextBlock(S("Usage_Heatmap_Less")).FontSize(11).Foreground(TertiaryText).VAlign(VerticalAlignment.Center),
            HStack(3, new[] { 0, 1, 2, 3, 4 }.Select(level => (Element)Border(null).Size(cell, cell).CornerRadius(2)
                .Background(level > 0 ? Accent : Track)
                .Opacity(level switch { 1 => 0.3, 2 => 0.5, 3 => 0.75, _ => 1.0 })
                .WithKey("level:" + level)).ToArray()),
            TextBlock(S("Usage_Heatmap_More")).FontSize(11).Foreground(TertiaryText).VAlign(VerticalAlignment.Center));
        return VStack(8,
            ScrollViewer(grid)
                .Set(viewer =>
                {
                    viewer.HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto;
                    viewer.HorizontalScrollMode = Microsoft.UI.Xaml.Controls.ScrollMode.Enabled;
                    viewer.VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled;
                    viewer.VerticalScrollMode = Microsoft.UI.Xaml.Controls.ScrollMode.Disabled;
                })
                .AutomationId("UsageHeatmapGrid"),
            legend.HAlign(HorizontalAlignment.Right).AccessibilityView(AccessibilityView.Raw));
    }
}
