using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClaw.Shared.Usage;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation.Usage;
using static Microsoft.UI.Reactor.Factories;
using static OpenClawTray.Pages.Usage.UsageUi;

namespace OpenClawTray.Pages.Usage;

/// <summary>Page width published by the XAML host so sections can choose responsive column counts.</summary>
internal sealed class UsageViewport
{
    public double Width { get; private set; } = 1000;

    public event Action? Changed;

    public void Update(double width)
    {
        if (width <= 0 || Math.Abs(width - Width) < 1) return;
        Width = width;
        Changed?.Invoke();
    }
}

internal sealed record UsagePageProps(
    UsagePageController Controller,
    UsageViewport Viewport,
    Action OpenConnection,
    Func<UsageExportFormat, Task> Export);

/// <summary>
/// Reactor root of the native Usage page. Renders <see cref="UsagePageController.State"/>; all data loading,
/// query state and Gateway contracts live in the controller and WinUI-free projections.
/// </summary>
internal sealed class ReactorUsagePage : Component<UsagePageProps>
{
    public const double MaxContentWidth = 1240;
    public const double WideLayoutBreakpoint = 1100;

    public override Element Render()
    {
        var props = Props;
        var controller = props.Controller;
        var (state, setState) = UseState(controller.State);
        var (width, setWidth) = UseState(props.Viewport.Width);
        UsageUi.ReduceMotion = UseReducedMotion();

        UseEffect((Func<Action>)(() =>
        {
            void Changed() => setState(controller.State);
            controller.Changed += Changed;
            setState(controller.State);
            return () => controller.Changed -= Changed;
        }), controller);

        UseEffect((Func<Action>)(() =>
        {
            void Resized() => setWidth(props.Viewport.Width);
            props.Viewport.Changed += Resized;
            // The host can publish a size between the first render and this subscription.
            Resized();
            return () => props.Viewport.Changed -= Resized;
        }), props.Viewport);

        var contentWidth = Math.Min(width, MaxContentWidth) - 48;
        var header = UsageHeader.Render(state, controller, props.Export, contentWidth);
        var sections = Banners(state, controller, props.OpenConnection).Concat(Body(state, controller, contentWidth))
            .Select(section => section.Enter());
        var body = VStack(24, sections.ToArray())
            .Cascade(45)
            .MaxWidth(MaxContentWidth)
            .HAlign(HorizontalAlignment.Stretch)
            .Padding(24, state.HeaderPinned ? 16 : 0, 24, 32);

        if (state.HeaderPinned)
        {
            return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Star()],
                    Border(header.MaxWidth(MaxContentWidth).HAlign(HorizontalAlignment.Stretch))
                        .Padding(24, 16, 24, 12)
                        .Background(Theme.Ref("SolidBackgroundFillColorBaseBrush"))
                        .WithBorder(CardStroke, 0)
                        .Grid(row: 0),
                    PageScroll(body).Grid(row: 1))
                .AutomationId("UsagePageRoot");
        }

        return PageScroll(VStack(SectionGap,
                    header.MaxWidth(MaxContentWidth).HAlign(HorizontalAlignment.Stretch).Margin(24, 24, 24, 0),
                    body))
            .AutomationId("UsagePageRoot");
    }

    // Reactor's ScrollViewer scrolls horizontally by default; the page must measure content at the viewport width
    // so wrapping rows and star-sized charts fit instead of extending past the window.
    private static ScrollViewerElement PageScroll(Element child) =>
        ScrollViewer(child) with
        {
            HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled,
            HorizontalScrollMode = Microsoft.UI.Xaml.Controls.ScrollMode.Disabled,
        };

    private static IEnumerable<Element> Banners(UsagePageState state, UsagePageController controller, Action openConnection)
    {
        const Microsoft.UI.Xaml.Controls.InfoBarSeverity warning = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning;
        const Microsoft.UI.Xaml.Controls.InfoBarSeverity error = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
        const Microsoft.UI.Xaml.Controls.InfoBarSeverity info = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;

        if (state.Availability == UsageAvailability.Disconnected)
        {
            yield return Banner(S("UsagePage_GatewayDisconnected.Title"), S("UsagePage_GatewayDisconnected.Message"), warning,
                "UsageDisconnectedBanner", S("UsagePage_OpenConnection.Content"), openConnection).WithKey("disconnected");
        }
        if (state.Availability == UsageAvailability.Forbidden)
            yield return Banner(S("Usage_Forbidden_Title"), S("Usage_Forbidden_Message"), error, "UsageForbiddenBanner").WithKey("forbidden");
        if (state.Availability == UsageAvailability.Legacy)
            yield return Banner(S("Usage_Legacy_Title"), S("Usage_Legacy_Message"), info, "UsageLegacyBanner").WithKey("legacy");
        if (state.BasicGateway)
            yield return Banner(S("Usage_Basic_Title"), S("Usage_Basic_Message"), info, "UsageBasicBanner").WithKey("basic");
        if (state.ShowLoadError)
        {
            var message = state.Error is { Length: > 0 } detail ? F("Usage_Error_MessageFormat", detail) : S("Usage_Error_Message");
            yield return Banner(S("Usage_Error_Title"), message, error, "UsageErrorBanner", S("Usage_Retry"), controller.Refresh).WithKey("error");
        }
        if (state.Report is not { } report || state.Availability != UsageAvailability.Connected)
            yield break;
        if (report.Partial is { } partial)
        {
            yield return Banner(S("Usage_Partial_Title"),
                string.IsNullOrWhiteSpace(partial.Message) ? S("Usage_Partial_Message") : partial.Message,
                warning, "UsagePartialBanner").WithKey("partial");
        }
        if (report.CacheStatus?.IsIncomplete == true || report.Sessions.Any(s => s.Computing))
        {
            yield return (state.AutoRefreshPaused
                    ? Banner(S("Usage_Cache_Title"), S("Usage_Cache_Paused"), info, "UsageCacheBanner", S("Usage_Refresh"), controller.Refresh)
                    : Banner(S("Usage_Cache_Title"), S("Usage_Cache_Checking"), info, "UsageCacheBanner"))
                .WithKey("cache");
        }
        if (state.Dashboard is { QueryWarnings.Count: > 0 } dashboard)
        {
            yield return Banner(S("Usage_QueryWarnings_Title"), string.Join(" ", dashboard.QueryWarnings), info, "UsageQueryWarningsBanner")
                .WithKey("query-warnings");
        }
        if (state.ExportNotice is { } notice)
        {
            yield return (notice.Error is { } exportError
                    ? Banner(S("Usage_Export_FailedTitle"), S("Usage_Export_Error_" + exportError), error, "UsageExportBanner",
                        S("Usage_Dismiss"), () => controller.SetExportNotice(null))
                    : Banner(S("Usage_Export_SavedTitle"), F("Usage_Export_SavedFormat", notice.FileName ?? ""),
                        Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success, "UsageExportBanner", S("Usage_Dismiss"), () => controller.SetExportNotice(null)))
                .WithKey("export");
        }
    }

    private static IEnumerable<Element> Body(UsagePageState state, UsagePageController controller, double width)
    {
        var dashboard = state.Dashboard;
        if (dashboard is null)
        {
            if (state.Phase == UsageLoadPhase.Loading)
                yield return Loading().WithKey("loading");
            // Provider plans load separately, so they still show when the usage overview could not load.
            if (state.Providers.Phase != UsageLoadPhase.Idle || state.Providers.Value is not null)
                yield return UsageSections.Providers(state).WithKey("providers");
            yield break;
        }

        if (dashboard.IsEmpty && !state.Filters.HasPopulationFilter && state.Availability != UsageAvailability.Legacy)
        {
            yield return EmptyState(controller).WithKey("empty");
            yield return UsageSections.Providers(state).WithKey("providers");
            yield break;
        }

        // Section order follows the web page: daily usage, started by, overview, cost windows, token activity,
        // activity by time, sessions. Provider plans close the page.
        yield return UsageSections.Daily(state, dashboard, controller, width).WithKey("daily");
        if (state.Availability == UsageAvailability.Legacy)
        {
            if (dashboard.CostWindows.Count > 0)
                yield return UsageSections.CostWindows(dashboard, state.Filters, width).WithKey("windows");
            yield return UsageSections.Providers(state).WithKey("providers");
            yield break;
        }
        if (dashboard.Creators.Count > 0)
            yield return UsageSections.Creators(state, dashboard, controller).WithKey("creators");
        yield return UsageSections.Insights(state, dashboard, width).WithKey("insights");
        if (dashboard.CostWindows.Count > 0)
            yield return UsageSections.CostWindows(dashboard, state.Filters, width).WithKey("windows");
        if (dashboard.Heatmap is { } heatmap)
            yield return UsageSections.HeatmapSection(heatmap).WithKey("heatmap");
        // The activity mosaic needs the server-side overview, which released Gateways do not return.
        if (!state.BasicGateway)
            yield return UsageSections.Activity(state, dashboard, controller).WithKey("activity");
        yield return UsageSessionsSection.Render(state, dashboard, controller, width).WithKey("sessions");
        yield return UsageSections.Providers(state).WithKey("providers");
    }

    private static Element Loading() =>
        Card(VStack(16,
                HStack(12, ProgressRing().Size(20, 20), TextBlock(S("Usage_Loading")).Foreground(SecondaryText).VAlign(VerticalAlignment.Center)),
                Border(null).Height(180).CornerRadius(6).Background(Track),
                Grid([GridSize.Star(), GridSize.Star(), GridSize.Star()], [GridSize.Px(72)],
                    Border(null).CornerRadius(6).Background(Track).Margin(0, 0, 8, 0).Grid(column: 0),
                    Border(null).CornerRadius(6).Background(Track).Margin(4, 0, 4, 0).Grid(column: 1),
                    Border(null).CornerRadius(6).Background(Track).Margin(8, 0, 0, 0).Grid(column: 2)))
            .AutomationName(S("Usage_Loading")),
            "UsageLoading");

    private static Element EmptyState(UsagePageController controller) =>
        Card(VStack(8,
                Image(UsageUi.SetupAssets + "OpenClawMascot.png").AccessibilityHidden().Size(112, 112).HAlign(HorizontalAlignment.Center)
                    .Enter(),
                TextBlock(S("Usage_Empty_Title")).FontSize(18).FontWeight(FontWeights.SemiBold).HAlign(HorizontalAlignment.Center)
                    .Margin(0, 8, 0, 0),
                TextBlock(S("Usage_Empty_Message")).Foreground(SecondaryText).TextWrapping(TextWrapping.Wrap).MaxWidth(420)
                    .TextAlignment(TextAlignment.Center).HAlign(HorizontalAlignment.Center),
                Button(S("Usage_Refresh"), controller.Refresh).HAlign(HorizontalAlignment.Center).Margin(0, 8, 0, 0).AutomationId("UsageEmptyRefresh"))
            .Padding(0, 28, 0, 28),
            "UsageEmptyState");
}
