using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClaw.Shared.Usage;
using OpenClawTray.Presentation;
using OpenClawTray.Presentation.Usage;
using static Microsoft.UI.Reactor.Factories;
using static OpenClawTray.Pages.Usage.UsageUi;

namespace OpenClawTray.Pages.Usage;

/// <summary>Retained page width and shell content inset, published independently without remounting the page.</summary>
internal sealed class UsageViewport
{
    public double Width { get; private set; } = PageResponsiveLayout.WideMin;
    public double TopInset { get; private set; }

    public event Action? Changed;

    public void Update(double width)
    {
        var snapped = PageResponsiveLayout.Snap(width);
        if (snapped <= 0 || snapped == Width) return;
        Width = snapped;
        Changed?.Invoke();
    }

    public void UpdateTopInset(double topInset)
    {
        if (topInset == TopInset) return;
        TopInset = topInset;
        Changed?.Invoke();
    }
}

/// <summary>
/// Page inputs. <see cref="OpenWebUsage"/> opens the Gateway's web Usage page through the app's Dashboard launcher;
/// <see cref="OpenConversation"/> opens a session key in native chat.
/// </summary>
internal sealed record UsagePageProps(
    UsagePageController Controller,
    UsageViewport Viewport,
    Action OpenConnection,
    Action OpenWebUsage,
    Action<string> OpenConversation,
    Func<UsageExportFormat, Task> Export,
    Action OpenChat,
    Func<string, Task> OpenExportFolder);

/// <summary>
/// Reactor root of the native Usage page. Renders <see cref="UsagePageController.State"/>; all data loading,
/// query state and Gateway contracts live in the controller and WinUI-free projections.
/// </summary>
internal sealed class ReactorUsagePage : Component<UsagePageProps>
{
    public override Element Render()
    {
        var props = Props;
        var controller = props.Controller;
        var (state, setState) = UseState(controller.State);
        var (width, setWidth) = UseState(props.Viewport.Width);
        var (topInset, setTopInset) = UseState(props.Viewport.TopInset);
        var announce = this.UseAnnounce();
        UseEffect((Func<Action>)(() =>
        {
            var cancelled = false;
            if (state.ExportNotice is { } notice)
                // Render effects can still be inside a native UIA/menu activation.
                controller.PostUi(() =>
                {
                    if (!cancelled)
                        announce.Announce(UsageExportFeedback.Message(notice), assertive: notice.Error is not null);
                });
            return () => cancelled = true;
        }), state.ExportNotice);

        UseEffect((Func<Action>)(() =>
        {
            void Changed() => setState(controller.State);
            controller.Changed += Changed;
            setState(controller.State);
            return () => controller.Changed -= Changed;
        }), controller);

        UseEffect((Func<Action>)(() =>
        {
            void Resized()
            {
                setWidth(props.Viewport.Width);
                setTopInset(props.Viewport.TopInset);
            }
            props.Viewport.Changed += Resized;
            // The host can publish a size between the first render and this subscription.
            Resized();
            return () => props.Viewport.Changed -= Resized;
        }), props.Viewport);

        var layout = new UsagePageLayout(width);
        var header = UsageHeader.Render(state, props, layout, UseHighContrast());
        // Sections render without entrance transitions: Reactor reconciles a keyed section against a transient
        // transition host if data or a selection arrives mid-animation, which throws during the update.
        var sections = Banners(state, props).Concat(Body(state, props, layout));
        var body = VStack(PageResponsiveLayout.GapStack, sections.ToArray())
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationId("UsageBody");

        var scroll = PageScroll(Grid([GridSize.Star()], [GridSize.Auto],
                    VStack(PageResponsiveLayout.GapStack, header, body)
                        .Padding(layout.Padding, PageResponsiveLayout.PaddingWide, layout.Padding, PageResponsiveLayout.PaddingWide)
                        .Width(layout.ColumnWidth).HAlign(HorizontalAlignment.Center).AutomationId("UsageContentColumn")
                        .Grid(row: 0).WithKey("content"))
                .Width(width).HAlign(HorizontalAlignment.Left))
            .AutomationId("UsagePageRoot");
        return Grid([GridSize.Star()], [GridSize.Star()],
            scroll.Margin(0, topInset, 0, 0).WithKey("scroll"),
            UsageExportFeedback.Toast(state, props)?.WithKey("export-toast"),
            UsageExportFeedback.Dialog(state, controller, props.Viewport)?.WithKey("export-dialog"),
            announce.Region.LiveRegion(state.ExportNotice?.Error is not null
                    ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite)
                .AutomationId("UsageResultAnnouncements").WithKey("announcements")).AutomationId("UsageSurface");
    }

    // Reactor's ScrollViewer scrolls horizontally by default; the page must measure content at the viewport width
    // so wrapping rows and star-sized charts fit instead of extending past the window.
    private static ScrollViewerElement PageScroll(Element child) =>
        ScrollViewer(child) with
        {
            HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled,
            HorizontalScrollMode = Microsoft.UI.Xaml.Controls.ScrollMode.Disabled,
        };

    private static IEnumerable<Element> Banners(UsagePageState state, UsagePageProps props)
    {
        const Microsoft.UI.Xaml.Controls.InfoBarSeverity warning = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning;
        const Microsoft.UI.Xaml.Controls.InfoBarSeverity error = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
        const Microsoft.UI.Xaml.Controls.InfoBarSeverity info = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;
        var controller = props.Controller;

        if (state.Availability == UsageAvailability.Disconnected)
        {
            yield return Banner(S("UsagePage_GatewayDisconnected.Title"), S("UsagePage_GatewayDisconnected.Message"), error,
                "UsageDisconnectedBanner", S("UsagePage_OpenConnection.Content"), props.OpenConnection).WithKey("disconnected");
        }
        if (state.Availability is UsageAvailability.Forbidden or UsageAvailability.Unsupported)
        {
            yield return Card(HStack(16,
                    IconBadge("\uE72E", 0, 40),
                    Button(S("UsagePage_OpenConnection.Content"), props.OpenConnection)
                        .AutomationName(S("UsagePage_OpenConnection.Content")).AutomationId("UsageAccessOpenConnection")),
                state.Availability == UsageAvailability.Forbidden ? "UsageForbiddenPanel" : "UsageUnsupportedPanel")
                .MaxWidth(520).HAlign(HorizontalAlignment.Left).WithKey("access");
        }
        if (state.ShowLoadError)
        {
            var message = state.Error is { Length: > 0 } detail ? F("Usage_Error_MessageFormat", detail) : S("Usage_Error_Message");
            yield return Banner(S("Usage_Error_Title"), message, state.Report is null ? error : warning,
                "UsageErrorBanner", S("Usage_Retry"), controller.Refresh).WithKey("error");
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
            var pending = report.Sessions.Count(session => session.Computing);
            yield return Banner(pending > 0 ? F("Usage_ComputingFormat", Count(pending)) : S("Usage_Cache_Title"),
                state.AutoRefreshPaused ? S("Usage_Cache_Paused") : S("Usage_Cache_Checking"), info,
                "UsageCacheBanner", S("Usage_Refresh"), controller.Refresh).WithKey("cache");
        }
        if (state.Dashboard is { QueryWarnings.Count: > 0 } dashboard)
        {
            yield return Banner(S("Usage_QueryWarnings_Title"), string.Join(" ", dashboard.QueryWarnings), info, "UsageQueryWarningsBanner")
                .WithKey("query-warnings");
        }
    }

    private static IEnumerable<Element> Body(UsagePageState state, UsagePageProps props, UsagePageLayout layout)
    {
        if (state.Availability == UsageAvailability.Forbidden) yield break;
        var dashboard = state.Dashboard;
        if (dashboard is null)
        {
            if (state.Phase == UsageLoadPhase.Loading)
                yield return Loading().WithKey("loading");
            // Provider plans load separately, so they still show when the usage overview could not load.
            if (state.Providers.Phase != UsageLoadPhase.Idle || state.Providers.Value is not null)
                yield return UsageSections.Providers(state, props.Controller).WithKey("providers");
            yield break;
        }

        if (dashboard.IsEmpty && !state.Filters.HasQuery)
        {
            yield return EmptyState(props).WithKey("empty");
            yield return UsageSections.Providers(state, props.Controller).WithKey("providers");
            yield break;
        }

        yield return Grid(layout.TwoColumns ? [GridSize.Px(layout.DailyWidth + PageResponsiveLayout.GapStack), GridSize.Star()] : [GridSize.Star()],
                layout.TwoColumns ? [GridSize.Auto] : [GridSize.Auto, GridSize.Auto],
                UsageSections.Daily(state, dashboard, props, layout.DailyWidth).VAlign(VerticalAlignment.Top)
                    .Width(layout.TwoColumns ? layout.DailyWidth : layout.ContentWidth).HAlign(HorizontalAlignment.Left)
                    .Grid(row: 0, column: 0).WithKey("daily"),
                UsageSections.Providers(state, props.Controller).VAlign(VerticalAlignment.Top)
                    .Margin(0, layout.TwoColumns ? 0 : PageResponsiveLayout.GapStack, 0, 0)
                    .Grid(row: layout.TwoColumns ? 0 : 1, column: layout.TwoColumns ? 1 : 0).WithKey("providers"))
            .AutomationId("UsageStage").WithKey("stage");
        yield return UsageSections.Contributors(state, dashboard, props, layout).WithKey("contributors");
        yield return UsageSessionsSection.Render(state, dashboard, props, layout).WithKey("sessions");
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

    private static Element EmptyState(UsagePageProps props) =>
        Card(VStack(8,
                TextBlock(S("Usage_Empty_Title")).FontSize(18).FontWeight(FontWeights.SemiBold).HAlign(HorizontalAlignment.Center)
                    .Margin(0, 8, 0, 0),
                TextBlock(S("Usage_Empty_Message")).Foreground(SecondaryText).TextWrapping(TextWrapping.Wrap).MaxWidth(420)
                    .TextAlignment(TextAlignment.Center).HAlign(HorizontalAlignment.Center),
                Button(S("Usage_OpenChat"), props.OpenChat).HAlign(HorizontalAlignment.Center).Margin(0, 8, 0, 0)
                    .AutomationName(S("Usage_OpenChat")).AutomationId("UsageEmptyChat"))
            .Padding(0, 28, 0, 28),
            "UsageEmptyState");
}
