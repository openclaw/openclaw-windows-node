using System.Numerics;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared.Dashboards;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Dashboards;

internal sealed record BoardWidgetCellProps(
    BoardWidget Widget,
    BoardWidgetBodyKind Body,
    string KindLabel,
    Uri? GatewayOrigin,
    BoardWidgetTicketFailure? TicketFailure,
    Action<string> OnUnauthorized,
    Action<string> OnReady,
    Action<string> OnRetryAuthorization,
    Action<string, double> OnContentHeight,
    bool ReducedMotion = false);

/// <summary>
/// One widget card: native chrome (title, kind, granted access) and a body
/// chosen by <see cref="BoardWidgetBodyPolicy"/>. Web content loads in an
/// isolated <see cref="BoardWidgetWebHost"/>; loading, slow, and error
/// states are native overlays so a failure stays contained to this card.
/// </summary>
internal sealed record BoardWebsiteProps(BoardWidget Widget, Uri Website);

/// <summary>
/// Body of a <c>session:website</c> widget: the isolated website host plus a
/// native failure overlay with Retry, since built-in browser error pages are off.
/// </summary>
internal sealed class ReactorBoardWebsite : Component<BoardWebsiteProps>
{
    public override Element Render()
    {
        var props = Props;
        var (failed, setFailed) = UseState(false, threadSafe: true);
        var (attempt, setAttempt) = UseState(0);
        var host = BoardWebsiteHostElement.BoardWebsiteHost()
            .Set(control =>
            {
                control.FailedChanged = setFailed;
                control.Update(props.Website, attempt);
            })
            .Opacity(failed ? 0 : 1)
            .AutomationName(props.Widget.Label)
            .AutomationId("BoardWebsite_" + props.Widget.Name);
        // The host stays first and mounted so a retry reuses this cell instead of remounting it.
        return Grid([GridSize.Star()], [GridSize.Star()],
            host,
            failed
                ? Border(ReactorBoardWidgets.Error(
                        LocalizationHelper.GetString("Dashboards_Widget_WebsiteFailed"),
                        null,
                        () =>
                        {
                            setFailed(false);
                            setAttempt(attempt + 1);
                        }))
                    .Background(Theme.Ref("CardBackgroundFillColorDefaultBrush"))
                    .AutomationId("BoardWebsiteFailed_" + props.Widget.Name)
                : Empty());
    }
}

internal sealed class ReactorBoardWidgetCell : Component<BoardWidgetCellProps>
{
    public override Element Render()
    {
        var props = Props;
        var widget = props.Widget;
        var (hostState, setHostState) = UseState<BoardWidgetWebHostState?>(null, threadSafe: true);
        var (retryToken, setRetryToken) = UseState(0);

        var header = Header(widget, props.KindLabel);
        var body = props.Body switch
        {
            BoardWidgetBodyKind.Frame => Frame(props, hostState, setHostState, retryToken, setRetryToken),
            BoardWidgetBodyKind.Report => ScrollView(ReactorBoardWidgets.Report(widget).Margin(16, 4, 16, 16)),
            BoardWidgetBodyKind.Website => ReactorBoardWidgets.Website(widget, props.GatewayOrigin),
            BoardWidgetBodyKind.NeedsApproval => ReactorBoardWidgets.NeedsApproval(widget),
            BoardWidgetBodyKind.Rejected => ReactorBoardWidgets.Notice(
                "BoardWidgetRejected", DashboardsArt.GlyphBadge(DashboardsGlyphs.Shield, "SystemFillColorCriticalBrush", "SystemFillColorCriticalBackgroundBrush", 44),
                LocalizationHelper.GetString("Dashboards_Widget_Rejected"),
                LocalizationHelper.GetString("Dashboards_Widget_RejectedDetail")),
            BoardWidgetBodyKind.McpApp => ReactorBoardWidgets.Notice(
                "BoardWidgetMcpApp", DashboardsArt.HueBadge(DashboardsGlyphsExtra.Apps, DashboardHues.Pumpkin, 44),
                LocalizationHelper.GetString("Dashboards_Widget_McpAppTitle"),
                LocalizationHelper.GetString("Dashboards_Widget_McpAppDetail")),
            BoardWidgetBodyKind.DisabledPlugin => ReactorBoardWidgets.Notice(
                "BoardWidgetDisabledPlugin", DashboardsArt.GlyphBadge(DashboardsGlyphsExtra.Puzzle, "TextFillColorSecondaryBrush", "SubtleFillColorSecondaryBrush", 44),
                LocalizationHelper.Format("Dashboards_Widget_DisabledPluginFormat", widget.PluginId),
                null),
            BoardWidgetBodyKind.UnsupportedCorePlugin => ReactorBoardWidgets.Notice(
                "BoardWidgetUnsupported", DashboardsArt.HueBadge(DashboardsGlyphsExtra.ForKind(widget), DashboardHues.ForKind(widget), 44),
                LocalizationHelper.GetString("Dashboards_Widget_UnsupportedTitle"),
                LocalizationHelper.Format("Dashboards_Widget_UnsupportedFormat", props.KindLabel)),
            _ => ReactorBoardWidgets.Error(LocalizationHelper.GetString("Dashboards_Widget_ContentUnavailable"), null),
        };

        var framed = widget.Presentation == BoardWidgetPresentation.Card || props.Body != BoardWidgetBodyKind.Frame;
        Element cell = Border(Grid(
                [GridSize.Star()],
                [GridSize.Auto, GridSize.Star()],
                header.Grid(row: 0),
                Border(body)
                    .Padding(framed && props.Body == BoardWidgetBodyKind.Frame ? BoardGridLayout.FrameInset : 0)
                    .Grid(row: 1)))
            .Background(Theme.Ref(widget.Presentation == BoardWidgetPresentation.Frameless && props.Body == BoardWidgetBodyKind.Frame
                ? "SubtleFillColorTransparentBrush"
                : "CardBackgroundFillColorDefaultBrush"))
            .BorderBrush(Theme.Ref("CardStrokeColorDefaultBrush"))
            .BorderThickness(BoardGridLayout.BorderWidth)
            .CornerRadius(10)
            .AutomationName(LocalizationHelper.Format("Dashboards_Widget_CellFormat", widget.Label))
            .AutomationId("BoardWidget_" + widget.Name);
        return DashboardsArt.Enter(cell, props.ReducedMotion);
    }

    private static Element Header(BoardWidget widget, string kindLabel)
    {
        var capabilities = DashboardsPresentation.CapabilityLines(widget);
        var granted = widget.GrantState == BoardWidgetGrantState.Granted && capabilities.Length > 0
            ? Border(HStack(4,
                    DashboardsStates.Glyph("\uE73E", 10, "SystemFillColorSuccessBrush").VAlign(VerticalAlignment.Center),
                    TextBlock(LocalizationHelper.GetString("Dashboards_Widget_Granted")).FontSize(11).SemiBold()
                        .Foreground(Theme.Ref("TextFillColorPrimaryBrush")).VAlign(VerticalAlignment.Center)))
                .Padding(7, 2, 8, 3).CornerRadius(999)
                .Background(Theme.Ref("SystemFillColorSuccessBackgroundBrush"))
                .ToolTip(LocalizationHelper.GetString("Dashboards_Widget_ActiveCapabilities") + "\n" + string.Join("\n", capabilities))
                .AutomationName(LocalizationHelper.GetString("Dashboards_Widget_ActiveCapabilities") + ": " + string.Join(", ", capabilities))
                .AutomationId("BoardWidgetGranted_" + widget.Name)
            : Empty();
        return Border(Grid(
                [GridSize.Star(), GridSize.Auto, GridSize.Auto],
                [GridSize.Auto],
                TextBlock(widget.Label).FontSize(14).SemiBold()
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .ToolTip(widget.Label)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(column: 0),
                granted.Margin(8, 0, 0, 0).VAlign(VerticalAlignment.Center).Grid(column: 1),
                DashboardsArt.KindChip(widget, kindLabel)
                    .Margin(8, 0, 0, 0)
                    .VAlign(VerticalAlignment.Center)
                    .AutomationId("BoardWidgetKind_" + widget.Name)
                    .Grid(column: 2)))
            .Padding(14, 8, 10, 4)
            .Height(BoardGridLayout.ChromeHeaderHeight);
    }

    private static Element Frame(
        BoardWidgetCellProps props,
        BoardWidgetWebHostState? state,
        Action<BoardWidgetWebHostState?> setState,
        int retryToken,
        Action<int> setRetryToken)
    {
        var widget = props.Widget;
        if (props.GatewayOrigin is not { } origin)
            return ReactorBoardWidgets.Error(LocalizationHelper.GetString("Dashboards_Widget_ContentUnavailable"), null);
        // A remote Gateway without a dedicated sandbox origin often can't route the derived sandbox port.
        bool SandboxHint() =>
            BoardWidgetUrlPolicy.TryResolveSandboxUrl(
                widget.SandboxUrl, widget.SandboxPort, widget.SandboxOrigin, origin, BoardWidgetHostPage.PageUri, out var sandbox) &&
            BoardWidgetUrlPolicy.SandboxOriginRequired(widget, sandbox);
        if (props.TicketFailure is { } failure)
        {
            return ReactorBoardWidgets.Error(
                LocalizationHelper.GetString(failure == BoardWidgetTicketFailure.RefreshUnavailable
                    ? "Dashboards_Widget_ContentUnavailable"
                    : SandboxHint()
                        ? "Dashboards_Widget_SandboxOriginRequired"
                        : "Dashboards_Widget_AuthorizationFailed"),
                null,
                () => props.OnRetryAuthorization(widget.Name));
        }

        var host = BoardWidgetWebHostElement.BoardWidgetWebHost()
            .Set(control =>
            {
                control.StateChanged = next =>
                {
                    setState(next);
                    if (next.Status == BoardWidgetWebHostStatus.Unauthorized)
                        props.OnUnauthorized(widget.Name);
                    else if (next.Status == BoardWidgetWebHostStatus.Ready)
                        props.OnReady(widget.Name);
                };
                control.ContentHeightChanged = height => props.OnContentHeight(widget.Name, height);
                control.Update(new BoardWidgetWebHostRequest(widget, origin, Attempt: retryToken));
            })
            .AutomationName(widget.Label)
            .AutomationId("BoardWidgetContent_" + widget.Name);

        Element overlay = state?.Status switch
        {
            null or BoardWidgetWebHostStatus.Loading or BoardWidgetWebHostStatus.Unauthorized =>
                ProgressRing().IsActive(true).Size(24, 24).Center()
                    .AutomationName(LocalizationHelper.GetString("Dashboards_Widget_Loading")),
            BoardWidgetWebHostStatus.Slow => VStack(8,
                    ProgressRing().IsActive(true).Size(24, 24).HAlign(HorizontalAlignment.Center),
                    TextBlock(LocalizationHelper.GetString("Dashboards_Widget_Slow")).FontSize(12)
                        .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                        .TextWrapping(TextWrapping.Wrap).TextAlignment(TextAlignment.Center))
                .Center().Margin(16).LiveRegion()
                .AutomationId("BoardWidgetSlow_" + widget.Name),
            BoardWidgetWebHostStatus.Failed => Border(ReactorBoardWidgets.Error(
                    LocalizationHelper.GetString("Dashboards_Widget_ErrorDetail"),
                    state.Detail,
                    () => setRetryToken(retryToken + 1)))
                .Background(Theme.Ref("CardBackgroundFillColorDefaultBrush")),
            BoardWidgetWebHostStatus.SandboxUnreachable => Border(ReactorBoardWidgets.Error(
                    LocalizationHelper.GetString(SandboxHint()
                        ? "Dashboards_Widget_SandboxUnreachableHint"
                        : "Dashboards_Widget_SandboxUnavailable"),
                    null,
                    () => setRetryToken(retryToken + 1)))
                .Background(Theme.Ref("CardBackgroundFillColorDefaultBrush")),
            _ => Empty(),
        };
        return Grid([GridSize.Star()], [GridSize.Star()],
            host.Opacity(state?.Status == BoardWidgetWebHostStatus.Ready ? 1 : 0).OpacityTransition().Grid(row: 0),
            overlay.Grid(row: 0));
    }
}

/// <summary>Native widget bodies: core plugin renderers and contained notices.</summary>
internal static class ReactorBoardWidgets
{
    private const double ChartWidth = 600;
    private const double ChartHeight = 160;
    private const double ChartLabelHeight = 40;

    public static Element Notice(string id, Element badge, string title, string? detail)
    {
        var children = new List<Element>
        {
            badge.HAlign(HorizontalAlignment.Center).Margin(0, 0, 0, 4),
            TextBlock(title).FontSize(14).SemiBold()
                .TextWrapping(TextWrapping.Wrap).TextAlignment(TextAlignment.Center),
        };
        if (!string.IsNullOrWhiteSpace(detail))
        {
            children.Add(TextBlock(detail).FontSize(12)
                .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                .TextWrapping(TextWrapping.Wrap).TextAlignment(TextAlignment.Center));
        }
        return ScrollView(VStack(8, children.ToArray()).Center().Margin(16)).AutomationId(id);
    }

    public static Element Error(string detail, string? technical, Action? retry = null)
    {
        var children = new List<Element>
        {
            HStack(10,
                DashboardsArt.GlyphBadge(DashboardsGlyphs.Error, "SystemFillColorCriticalBrush", "SystemFillColorCriticalBackgroundBrush", 32)
                    .VAlign(VerticalAlignment.Center),
                TextBlock(LocalizationHelper.GetString("Dashboards_Widget_ErrorTitle")).FontSize(14).SemiBold()
                    .TextWrapping(TextWrapping.Wrap).VAlign(VerticalAlignment.Center)),
            TextBlock(detail).FontSize(12)
                .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                .TextWrapping(TextWrapping.Wrap),
        };
        if (!string.IsNullOrWhiteSpace(technical))
        {
            children.Add(Expander(
                    LocalizationHelper.GetString("Dashboards_Widget_ShowDetails"),
                    TextBlock(technical).FontSize(12).FontFamily("Cascadia Mono, Consolas")
                        .IsTextSelectionEnabled(true).TextWrapping(TextWrapping.Wrap))
                .HAlign(HorizontalAlignment.Stretch)
                .HorizontalContentAlignment(HorizontalAlignment.Stretch));
        }
        if (retry is not null)
        {
            children.Add(Button(LocalizationHelper.GetString("Dashboards_Retry"), retry)
                .AccentButton()
                .AutomationName(LocalizationHelper.GetString("Dashboards_Retry"))
                .AutomationId("BoardWidgetRetry"));
        }
        return ScrollView(VStack(8, children.ToArray()).Margin(16).VAlign(VerticalAlignment.Center))
            .LiveRegion(Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive)
            .AutomationId("BoardWidgetError");
    }

    public static Element NeedsApproval(BoardWidget widget)
    {
        var groups = new List<Element>();
        void AddGroup(string resourceKey, string glyph, IEnumerable<string> values)
        {
            var list = values.ToArray();
            if (list.Length == 0)
                return;
            groups.Add(VStack(6,
                new Element[] { TextBlock(LocalizationHelper.GetString(resourceKey)).FontSize(12).SemiBold() }
                    .Concat(list.Select(value => (Element)Border(HStack(6,
                            DashboardsStates.Glyph(glyph, 12, "SystemFillColorCautionBrush").VAlign(VerticalAlignment.Center),
                            TextBlock(value).FontSize(12).FontFamily("Cascadia Mono, Consolas")
                                .TextWrapping(TextWrapping.Wrap).VAlign(VerticalAlignment.Center)))
                        .Padding(8, 4, 10, 4)
                        .CornerRadius(6)
                        .Background(Theme.Ref("SubtleFillColorSecondaryBrush"))
                        .HAlign(HorizontalAlignment.Left)))
                    .ToArray()));
        }
        AddGroup("Dashboards_Widget_NetworkAccess", DashboardsGlyphsExtra.Globe, widget.Declared.NetOrigins);
        AddGroup("Dashboards_Widget_HostTools", DashboardsGlyphsExtra.Wrench, widget.Declared.Tools);
        if (groups.Count == 0)
        {
            groups.Add(widget.DeclaredSummary.IsDefaultOrEmpty
                ? TextBlock(LocalizationHelper.GetString("Dashboards_Widget_NeedsApprovalDetail")).FontSize(12)
                    .Foreground(Theme.Ref("TextFillColorSecondaryBrush")).TextWrapping(TextWrapping.Wrap)
                : VStack(4, widget.DeclaredSummary.Select(line => (Element)TextBlock("• " + line).FontSize(12)
                    .Foreground(Theme.Ref("TextFillColorSecondaryBrush")).TextWrapping(TextWrapping.Wrap)).ToArray()));
        }
        var children = new List<Element>
        {
            HStack(10,
                DashboardsArt.GlyphBadge(DashboardsGlyphs.Shield, "SystemFillColorCautionBrush", "SystemFillColorCautionBackgroundBrush", 32),
                TextBlock(LocalizationHelper.GetString("Dashboards_Widget_NeedsApproval")).FontSize(14).SemiBold()
                    .VAlign(VerticalAlignment.Center)),
        };
        children.AddRange(groups);
        return ScrollView(VStack(12, children.ToArray()).Margin(16))
            .AutomationId("BoardWidgetNeedsApproval_" + widget.Name);
    }

    public static Element Website(BoardWidget widget, Uri? gatewayOrigin)
    {
        var url = widget.Props is { } props && props.TryGetProperty("url", out var value) &&
            value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;
        if (!BoardWidgetUrlPolicy.TryParseWebsite(url, gatewayOrigin, out var website, out var sameHost))
            return Error(LocalizationHelper.GetString("Dashboards_Widget_WebsiteInvalid"), null);
        var open = HyperlinkButton(LocalizationHelper.GetString("Dashboards_Widget_WebsiteOpen"), website)
            .ToolTip(LocalizationHelper.GetString("Dashboards_Widget_WebsiteEmbedHint"))
            .AutomationId("BoardWebsiteOpen_" + widget.Name);
        var footer = Border(Grid(
                [GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                TextBlock(website.Host).FontSize(12)
                    .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(column: 0),
                open.Grid(column: 1)))
            .Padding(12, 2)
            .BorderBrush(Theme.Ref("CardStrokeColorDefaultBrush"))
            .BorderThickness(0, 1, 0, 0);
        Element content = sameHost
            ? Notice("BoardWebsiteSameOrigin", DashboardsArt.HueBadge(DashboardsGlyphs.Shield, DashboardHues.Teal, 44), LocalizationHelper.GetString("Dashboards_Widget_WebsiteSameOrigin"), null)
            : Component<ReactorBoardWebsite, BoardWebsiteProps>(new BoardWebsiteProps(widget, website));
        return Grid([GridSize.Star()], [GridSize.Star(), GridSize.Auto],
            content.Grid(row: 0),
            footer.Grid(row: 1));
    }

    /// <summary>Native <c>session:report</c>: text, metrics, table, chart, and link blocks.</summary>
    public static Element Report(BoardWidget widget)
    {
        BoardReport report;
        try
        {
            report = BoardReportParser.Parse(widget.Props);
        }
        catch (FormatException ex)
        {
            return Error(LocalizationHelper.GetString("Dashboards_Widget_ReportInvalid"), ex.Message);
        }
        return VStack(16, report.Blocks.Select((block, index) => Block(block).WithKey("block" + index)).ToArray())
            .AutomationName(widget.Label)
            .AutomationId("BoardReport_" + widget.Name);
    }

    private static Element Block(BoardReportBlock block) => block switch
    {
        BoardReportTextBlock text => VStack(4, Titled(text.Title,
            TextBlock(text.Text).FontSize(14).TextWrapping(TextWrapping.Wrap).IsTextSelectionEnabled(true))),
        BoardReportMetricsBlock metrics => WrapGrid(metrics.Items.Select((metric, index) => Metric(metric, index).WithKey("metric" + index)).ToArray())
            .Set(grid =>
            {
                grid.Orientation = Orientation.Horizontal;
                grid.ItemWidth = 160;
            }),
        BoardReportTableBlock table => VStack(4, Titled(table.Title, Table(table))),
        BoardReportChartBlock chart => VStack(4, Titled(chart.Title, Chart(chart))),
        BoardReportLinksBlock links => VStack(4, Titled(links.Title, VStack(4, links.Items.Select(Link).ToArray()))),
        _ => Empty(),
    };

    private static Element[] Titled(string? title, Element content) =>
        string.IsNullOrWhiteSpace(title)
            ? [content]
            : [TextBlock(title).FontSize(14).SemiBold().HeadingLevel(Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level3), content];

    private static Element Metric(BoardReportMetric metric, int index)
    {
        var hue = (DashboardHues.Purple + index) % DashboardHues.Count;
        var children = new List<Element>
        {
            TextBlock(metric.Label).FontSize(12).Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                .TextTrimming(TextTrimming.CharacterEllipsis),
            TextBlock(metric.Value).FontSize(26).SemiBold().TextTrimming(TextTrimming.CharacterEllipsis),
        };
        if (!string.IsNullOrWhiteSpace(metric.Detail))
        {
            children.Add(TextBlock(metric.Detail).FontSize(12).Foreground(Theme.Ref("TextFillColorTertiaryBrush"))
                .TextWrapping(TextWrapping.Wrap).MaxLines(2));
        }
        // Each metric gets its own hue: a colored rail on a matching soft wash.
        return Border(Grid([GridSize.Px(4), GridSize.Star()], [GridSize.Star()],
                Border(Empty()).CornerRadius(2).Background(Theme.Ref(DashboardHues.Strong(hue))).Margin(0, 2, 0, 2).Grid(column: 0),
                VStack(2, children.ToArray()).Margin(10, 0, 0, 0).Grid(column: 1)))
            .Padding(10, 10, 12, 10)
            .Margin(0, 0, 10, 10)
            .CornerRadius(10)
            .Background(Theme.Ref(DashboardHues.Wash(hue)))
            .AutomationName($"{metric.Label}: {metric.Value}");
    }

    private static Element Table(BoardReportTableBlock table)
    {
        var columns = Enumerable.Repeat(GridSize.Auto, table.Columns.Length).ToArray();
        var rows = Enumerable.Repeat(GridSize.Auto, table.Rows.Length + 1).ToArray();
        var cells = new List<Element>();
        for (var c = 0; c < table.Columns.Length; c++)
        {
            cells.Add(Border(TextBlock(table.Columns[c]).FontSize(12).SemiBold()
                    .Foreground(Theme.Ref("TextFillColorSecondaryBrush")))
                .Padding(10, 6)
                .Background(Theme.Ref("SubtleFillColorSecondaryBrush"))
                .Grid(row: 0, column: c));
        }
        for (var r = 0; r < table.Rows.Length; r++)
        {
            for (var c = 0; c < table.Columns.Length; c++)
            {
                cells.Add(Border(TextBlock(table.Rows[r][c]).FontSize(13).TextWrapping(TextWrapping.Wrap).MaxWidth(360))
                    .Padding(10, 7)
                    .BorderBrush(Theme.Ref("CardStrokeColorDefaultBrush"))
                    .BorderThickness(0, 1, 0, 0)
                    .Grid(row: r + 1, column: c));
            }
        }
        return ScrollViewer(Border(Grid(columns, rows, cells.ToArray()))
                .CornerRadius(8)
                .BorderBrush(Theme.Ref("CardStrokeColorDefaultBrush"))
                .BorderThickness(1)
                .HAlign(HorizontalAlignment.Left))
            .Set(viewer =>
            {
                viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                viewer.HorizontalScrollMode = ScrollMode.Auto;
                viewer.VerticalScrollMode = ScrollMode.Disabled;
            });
    }

    private static Element Chart(BoardReportChartBlock chart)
    {
        var values = chart.Points.Select(point => point.Value).ToArray();
        var minimum = Math.Min(0, values.Min());
        var span = Math.Max(0, values.Max()) - minimum;
        if (span <= 0)
            span = 1;
        double Y(double value) => (ChartHeight - 8) - ((value - minimum) / span) * (ChartHeight - 24);
        var baseline = Y(0);
        var slot = ChartWidth / chart.Points.Length;
        var series = DashboardHues.Strong(DashboardHues.Purple);
        var peak = Array.IndexOf(values, values.Max());
        var shapes = new List<Element>();
        // Soft gridlines at a quarter, half, and three quarters of the plot.
        foreach (var fraction in new[] { 0.25, 0.5, 0.75 })
        {
            shapes.Add(Border(Empty()).Width(ChartWidth).Height(1)
                .Background(Theme.Ref("DividerStrokeColorDefaultBrush"))
                .Opacity(0.6)
                .Canvas(0, 8 + (ChartHeight - 24) * (1 - fraction)));
        }
        shapes.Add(Border(Empty()).Width(ChartWidth).Height(1)
            .Background(Theme.Ref("CardStrokeColorDefaultBrush"))
            .Canvas(0, baseline));
        var points = chart.Points.Select((point, index) =>
            (X: chart.Line
                ? chart.Points.Length == 1 ? ChartWidth / 2 : 8 + index * (ChartWidth - 16) / (chart.Points.Length - 1)
                : (index + 0.5) * slot,
             Y: Y(point.Value))).ToArray();
        if (chart.Line)
        {
            for (var i = 1; i < points.Length; i++)
            {
                var (x1, y1) = points[i - 1];
                var (x2, y2) = points[i];
                var length = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
                var angle = Math.Atan2(y2 - y1, x2 - x1) * 180 / Math.PI;
                shapes.Add(Border(Empty()).Width(length).Height(3)
                    .CornerRadius(1.5)
                    .Background(Theme.Ref(series))
                    .CenterPoint(new Vector3(0, 1.5f, 0))
                    .Rotation((float)angle)
                    .Canvas(x1, y1 - 1.5));
            }
            shapes.AddRange(points.Select((point, index) => (Element)Border(Empty())
                .Size(index == peak ? 12 : 9, index == peak ? 12 : 9)
                .CornerRadius(6)
                .Background(Theme.Ref("CardBackgroundFillColorDefaultBrush"))
                .BorderBrush(Theme.Ref(series))
                .BorderThickness(index == peak ? 3 : 2)
                .Canvas(point.X - (index == peak ? 6 : 4.5), point.Y - (index == peak ? 6 : 4.5))));
        }
        else
        {
            var barWidth = slot * 0.62;
            shapes.AddRange(points.Select((point, index) => (Element)Border(Empty())
                .Width(barWidth)
                .Height(Math.Max(1, Math.Abs(point.Y - baseline)))
                .CornerRadius(6, 6, 0, 0)
                .Background(Theme.Ref(series))
                .Opacity(index == peak ? 1 : 0.55)
                .Canvas(point.X - barWidth / 2, Math.Min(point.Y, baseline))));
        }
        var labelWidth = chart.Line && chart.Points.Length > 1
            ? Math.Min(slot, (ChartWidth - 16) / (chart.Points.Length - 1))
            : slot;
        shapes.AddRange(chart.Points.Select((point, index) =>
        {
            var value = point.Value.ToString("G6", System.Globalization.CultureInfo.CurrentCulture);
            return (Element)VStack(0,
                    TextBlock(point.Label).FontSize(11).Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                        .TextTrimming(TextTrimming.CharacterEllipsis).TextAlignment(TextAlignment.Center),
                    TextBlock(value).FontSize(12).SemiBold().TextAlignment(TextAlignment.Center))
                .Width(labelWidth)
                .AutomationName($"{point.Label}: {value}")
                .Canvas(points[index].X - labelWidth / 2, ChartHeight + 4);
        }));
        return Viewbox(Canvas(shapes.ToArray()).Size(ChartWidth, ChartHeight + ChartLabelHeight))
            .Stretch(Microsoft.UI.Xaml.Media.Stretch.Uniform)
            .MaxWidth(ChartWidth)
            .HAlign(HorizontalAlignment.Left);
    }

    private static Element Link(BoardReportLink link)
    {
        var children = new List<Element>
        {
            HyperlinkButton(link.Label, link.Url).Padding(0).AutomationName(link.Label),
        };
        if (!string.IsNullOrWhiteSpace(link.Detail))
        {
            children.Add(TextBlock(link.Detail).FontSize(12)
                .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                .TextWrapping(TextWrapping.Wrap));
        }
        return VStack(0, children.ToArray());
    }
}
