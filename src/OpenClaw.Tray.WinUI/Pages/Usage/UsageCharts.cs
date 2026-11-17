using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
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
        double plotHeight = PlotHeight, double? average = null, double barWidth = 28) =>
        Component<UsageColumnChart, UsageColumnChartProps>(new(columns, ceiling, axisLabel, automationId, plotHeight, average, barWidth));

    internal static Element RenderColumns(
        UsageColumnChartProps props, string? selected, Action<string> select,
        Action<int, Button, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs> keyDown, Action<string, Button> mounted)
    {
        var (columns, ceiling, axisLabel, automationId, plotHeight, average, barWidth) = props;
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
                    Bar(column, ceiling, automationId, plotHeight, barWidth, selected == column.Key,
                        () => select(column.Key), (button, args) => keyDown(index, button, args),
                        button => mounted(column.Key, button))
                        .Grid(row: 0, column: index).WithKey("bar:" + column.Key),
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
            averageLine = Grid([GridSize.Star()], [GridSize.Px(plotHeight)],
                    Grid(Enumerable.Repeat(GridSize.Star(), 40).ToArray(), [GridSize.Px(1)],
                        Enumerable.Range(0, 40).Select(i => (Element)Border(null).Height(1).Background(SecondaryText)
                            .Margin(0, 0, 4, 0).Grid(column: i)).ToArray()).Margin(0, y, 0, 0).VAlign(VerticalAlignment.Top))
                .Height(plotHeight).VAlign(VerticalAlignment.Top)
                .Set(control => control.IsHitTestVisible = false)
                .AutomationId("UsageDailyAverage");
        }
        return Grid([GridSize.Px(AxisWidth), GridSize.Star()], [GridSize.Auto, GridSize.Auto],
                average is { } mean ? Border(TextBlock(F("Usage_DailyAverageFormat", axisLabel(mean)))
                        .FontSize(11).Foreground(SecondaryText).AutomationId("UsageDailyAverageLabel"))
                    .Background(CardFill).HAlign(HorizontalAlignment.Right).Margin(0, 0, 0, 8)
                    .AutomationId("UsageDailyAverageCaption").Grid(column: 1) : null,
                axis.Grid(row: 1, column: 0),
                gridlines.Grid(row: 1, column: 1),
                Border(null).Height(1).Background(CardStroke).VAlign(VerticalAlignment.Top).Margin(0, plotHeight, 0, 0).Grid(row: 1, column: 1),
                plot.Grid(row: 1, column: 1),
                averageLine?.Grid(row: 1, column: 1))
            .HelpText(average is { } value ? F("Usage_DailyAverageFormat", axisLabel(value)) : "")
            .AutomationId(automationId);
    }

    private static Element Bar(BarColumn column, double ceiling, string automationId, double plotHeight, double barWidth,
        bool selected, Action select, Action<Button, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs> keyDown, Action<Button> mounted)
    {
        var value = Math.Max(0, column.Value);
        var height = ceiling > 0 ? Math.Clamp(value / ceiling, 0, 1) * plotHeight : 0;
        var marks = new List<Element?>();
        if (value > 0)
        {
            marks.Add(Border(null).Background(Theme.Ref("UsageChartBarBrush"))
                .Height(height).VAlign(VerticalAlignment.Bottom).CornerRadius(4, 4, 0, 0)
                .AutomationId($"{automationId}_mark_{column.Key}"));
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
        return Flyout(Button(bar, select).Padding(0).MinWidth(0).MinHeight(0).Height(plotHeight)
                .HAlign(HorizontalAlignment.Center).HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .VerticalContentAlignment(VerticalAlignment.Stretch)
                .Resources(OpenClawTray.Chat.ChatVisuals.ToolbarButtonResources)
                .AutomationName(column.AccessibleText).ItemStatus(selected ? S("Usage_Selected") : "")
                .OnPreviewKeyDown((sender, args) => keyDown((Button)sender, args))
                .OnMountAdd(element => mounted((Button)element))
                .AutomationId($"{automationId}_{column.Key}"),
            VStack(8, TextBlock(column.AccessibleText).TextWrapping(TextWrapping.Wrap).MaxWidth(280)));
    }

}

internal sealed record UsageColumnChartProps(
    IReadOnlyList<UsageCharts.BarColumn> Columns, double Ceiling, Func<double, string> AxisLabel,
    string AutomationId, double PlotHeight, double? Average, double BarWidth);

/// <summary>Owns only day selection and native focus. Range data and metric remain controller-owned.</summary>
internal sealed class UsageColumnChart : Component<UsageColumnChartProps>
{
    public override Element Render()
    {
        var (selected, setSelected) = UseState<string?>(null);
        var buttons = UseRef(new Dictionary<string, Button>());
        UseEffect(() =>
        {
            var active = Props.Columns.Select(column => column.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var key in buttons.Current.Keys.Where(key => !active.Contains(key)).ToArray())
                buttons.Current.Remove(key);
            if (selected is not null && !active.Contains(selected)) setSelected(null);
        }, Props.Columns);
        return UsageCharts.RenderColumns(Props, selected, setSelected, (index, _, args) =>
        {
            if (args.Key is not (VirtualKey.Left or VirtualKey.Right) || Props.Columns.Count == 0) return;
            var next = Math.Clamp(index + (args.Key == VirtualKey.Left ? -1 : 1), 0, Props.Columns.Count - 1);
            var key = Props.Columns[next].Key;
            if (!buttons.Current.TryGetValue(key, out var button)) return;
            if (button.Focus(FocusState.Keyboard))
            {
                setSelected(key);
                args.Handled = true;
            }
        }, (key, button) => buttons.Current[key] = button);
    }
}
