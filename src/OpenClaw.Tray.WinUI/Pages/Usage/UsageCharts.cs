using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using static Microsoft.UI.Reactor.Factories;
using static OpenClawTray.Pages.Usage.UsageUi;

namespace OpenClawTray.Pages.Usage;

/// <summary>
/// Native daily trend chart. Bars use proportional grid rows, so the chart scales with layout, follows theme brushes
/// and exposes one automation item per day. The expandable daily table is its text equivalent.
/// </summary>
internal static class UsageCharts
{
    public const double PlotHeight = 180;
    public const double AxisWidth = 56;

    public record BarColumn(string Key, double Value, string Label, string AccessibleText, bool Pending, bool LowerBound, bool Today = false);

    /// <summary>Single-series column chart with a value axis.</summary>
    public static Element Columns(
        IReadOnlyList<BarColumn> columns, double ceiling, Func<double, string> axisLabel, string automationId,
        double plotHeight = PlotHeight, double? average = null, double barWidth = 28)
    {
        var labelEvery = Math.Max(1, (int)Math.Ceiling(columns.Count / 8.0));
        // Labels are anchored on the last day, so it is always labelled. Each label spans back to the previous
        // anchor and ends under its own day, so labels never overlap and narrow columns do not clip them.
        bool Labelled(int index) => (columns.Count - 1 - index) % labelEvery == 0;

        var plot = Grid(Enumerable.Repeat(GridSize.Star(), columns.Count).ToArray(), [GridSize.Px(plotHeight), GridSize.Auto],
            columns.SelectMany((column, index) =>
            {
                var first = Math.Max(0, index - labelEvery + 1);
                return new Element?[]
                {
                    Bar(column, ceiling, automationId, plotHeight, barWidth).Grid(row: 0, column: index).WithKey("bar:" + column.Key),
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

        Element? averageLine = null;
        if (average is >= 0 && ceiling > 0)
        {
            var y = Math.Clamp(plotHeight * (1 - average.Value / ceiling), 0, plotHeight);
            averageLine = Grid(Enumerable.Repeat(GridSize.Star(), 40).ToArray(), [GridSize.Px(1)],
                        Enumerable.Range(0, 40).Select(i => (Element)Border(null).Height(1).Background(SecondaryText)
                            .Margin(0, 0, 4, 0).Grid(column: i)).ToArray())
                .Margin(0, y, 0, 0).VAlign(VerticalAlignment.Top)
                .AutomationId("UsageDailyAverage");
        }
        return Grid([GridSize.Px(AxisWidth), GridSize.Star()], [GridSize.Auto],
                axis.Grid(column: 0),
                gridlines.Grid(column: 1),
                Border(null).Height(1).Background(CardStroke).VAlign(VerticalAlignment.Top).Margin(0, plotHeight, 0, 0).Grid(column: 1),
                averageLine?.Grid(column: 1),
                plot.Grid(column: 1))
            .HelpText(average is { } value ? F("Usage_DailyAverageFormat", axisLabel(value)) : "")
            .AutomationId(automationId);
    }

    public static Element AverageLegend(string label) =>
        Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
            Grid(Enumerable.Repeat(GridSize.Px(6), 4).ToArray(), [GridSize.Px(1)],
                Enumerable.Range(0, 4).Select(index => (Element)Border(null).Height(1).Background(SecondaryText)
                    .Margin(0, 0, 2, 0).Grid(column: index)).ToArray())
                .VAlign(VerticalAlignment.Center).AccessibilityHidden().Margin(0, 0, 8, 0).Grid(column: 0),
            TextBlock(label).FontSize(11).Foreground(SecondaryText).TextWrapping(TextWrapping.NoWrap)
                .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(label).Grid(column: 1))
            .VAlign(VerticalAlignment.Center).AutomationName(label).AutomationId("UsageDailyAverageLegend");

    private static Element Bar(BarColumn column, double ceiling, string automationId, double plotHeight, double barWidth)
    {
        var value = Math.Max(0, column.Value);
        var height = ceiling > 0 ? Math.Clamp(value / ceiling, 0, 1) * plotHeight : 0;
        var marks = new List<Element?>();
        if (value > 0)
        {
            marks.Add(Border(null).Background(Theme.Ref("UsageChartBarBrush")).Opacity(column.Today ? 0.4 : 1)
                .Height(height).VAlign(VerticalAlignment.Bottom).CornerRadius(4, 4, 0, 0)
                .AutomationId($"{automationId}_mark_{column.Key}"));
            if (column.Today)
                marks.Add(Grid([GridSize.Px(1), GridSize.Star(), GridSize.Px(1)], [GridSize.Px(1), GridSize.Star(), GridSize.Px(1)],
                    Dashes(false).Grid(row: 0, columnSpan: 3),
                    Dashes(true).Grid(row: 1, column: 0),
                    Dashes(true).Grid(row: 1, column: 2),
                    Dashes(false).Grid(row: 2, columnSpan: 3)).Height(height).VAlign(VerticalAlignment.Bottom));
        }
        else if (column.Pending)
        {
            marks.Add(Border(null).Background(Track).Height(2).VAlign(VerticalAlignment.Bottom));
        }

        var bar = Grid([GridSize.Star()], [GridSize.Px(plotHeight)], marks.ToArray())
            .Height(plotHeight)
            .Width(Math.Max(1, barWidth))
            .HAlign(HorizontalAlignment.Stretch)
            .ToolTip(column.AccessibleText);
        return Flyout(Button(bar).Padding(0).MinWidth(0).MinHeight(0).Height(plotHeight)
                .HAlign(HorizontalAlignment.Center).HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .VerticalContentAlignment(VerticalAlignment.Stretch)
                .Resources(OpenClawTray.Chat.ChatVisuals.ToolbarButtonResources)
                .AutomationName(column.AccessibleText).AutomationId($"{automationId}_{column.Key}"),
            VStack(8, TextBlock(column.AccessibleText).TextWrapping(TextWrapping.Wrap).MaxWidth(280)));
    }

    private static Element Dashes(bool vertical) =>
        Grid(vertical ? [GridSize.Px(1)] : Enumerable.Repeat(GridSize.Star(), 6).ToArray(),
            vertical ? Enumerable.Repeat(GridSize.Star(), 12).ToArray() : [GridSize.Px(1)],
            Enumerable.Range(0, vertical ? 12 : 6).Select(index => (Element)Border(null).Background(Accent)
                .Margin(0, 0, vertical ? 0 : 2, vertical ? 3 : 0)
                .Grid(row: vertical ? index : 0, column: vertical ? 0 : index)).ToArray());
}
