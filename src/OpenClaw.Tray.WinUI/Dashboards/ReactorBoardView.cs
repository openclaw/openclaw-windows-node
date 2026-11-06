using System.Collections.Immutable;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared.Dashboards;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using static Microsoft.UI.Reactor.Factories;
using static OpenClawTray.Presentation.PageResponsiveLayout;

namespace OpenClawTray.Dashboards;

internal sealed record BoardViewProps(
    DashboardsPageState State,
    IBoardGatewayClient Client,
    BoardSessionTarget Target,
    int ConnectionRevision,
    double ViewportWidth = 0);

/// <summary>
/// One open session dashboard: header, tab strip, and the 12-column widget
/// grid. Widgets of visited tabs stay mounted (hidden) so switching tabs
/// keeps their live content, like the upstream board view.
/// </summary>
internal sealed class ReactorBoardView : Component<BoardViewProps>
{
    /// <summary>Tabs shown in the strip before the overflow menu (upstream shows six).</summary>
    private const int VisibleTabLimit = 6;

    public override Element Render()
    {
        var props = Props;
        var (snapshot, setSnapshot) = UseState(BoardDocumentSnapshot.Initial, threadSafe: true);
        var (contentHeights, setContentHeights) = UseState(ImmutableDictionary<string, double>.Empty, threadSafe: true);
        var latestHeights = UseRef(ImmutableDictionary<string, double>.Empty);
        latestHeights.Current = contentHeights;
        var (measuredWidth, setWidth) = UseState(MinWidth);
        var (backdropHeight, setBackdropHeight) = UseState(0d);
        var width = props.ViewportWidth > 0 ? props.ViewportWidth : measuredWidth;
        var model = UseRef<BoardDocumentModel?>(null);

        UseEffect((Func<Action>)(() =>
        {
            var document = new BoardDocumentModel(props.Client, props.Target, props.State.Post, props.State.Schedule);
            model.Current = document;
            document.Changed += next => setSnapshot(next);
            _ = document.LoadAsync();
            return () =>
            {
                model.Current = null;
                document.Dispose();
            };
        }), props.Client, props.Target, props.ConnectionRevision);

        var title = props.State.OpenTitle ?? LocalizationHelper.GetString("Dashboards_BoardLabel");
        var reducedMotion = UseReducedMotion();
        var highContrast = UseHighContrast();
        var refresh = () => { _ = model.Current?.LoadAsync(); };
        var actions = new List<Element>();
        if (props.State.OpenChat is { } openChat)
        {
            actions.Add(DashboardsStates.ChatButton(title, () => openChat(props.Target.SessionKey), "DashboardOpenChat"));
        }
        actions.Add(DashboardsStates.IconButton(DashboardsGlyphs.Refresh, LocalizationHelper.GetString("Dashboards_Refresh"), refresh, "DashboardRefresh"));
        if (MoreActions(props) is { } more)
            actions.Add(more);
        var header = BoardHeader(props, title, reducedMotion, actions.ToArray(), width, highContrast)
            .OnSizeChanged((sender, args) =>
            {
                var element = (FrameworkElement)sender;
                var height = args.NewSize.Height + element.Margin.Top + element.Margin.Bottom + 24;
                if (Snap(height) != Snap(backdropHeight))
                    setBackdropHeight(height);
            });

        Element body = snapshot.Status switch
        {
            DashboardLoadStatus.Unavailable => DashboardsStates.Unavailable(),
            DashboardLoadStatus.Disconnected => DashboardsStates.Disconnected(props.State),
            DashboardLoadStatus.Error => DashboardsStates.Status(
                "DashboardLoadError",
                DashboardsGlyphs.Error,
                LocalizationHelper.GetString("Dashboards_BoardLoadError_Title"),
                LocalizationHelper.Format("Dashboards_LoadErrorFormat", snapshot.Error ?? ""),
                Button(LocalizationHelper.GetString("Dashboards_Retry"), refresh).AutomationName(LocalizationHelper.GetString("Dashboards_Retry")).AutomationId("DashboardRetry"),
                alert: true),
            DashboardLoadStatus.Ready when snapshot.Board is not null =>
                Board(props, snapshot, model, latestHeights, setContentHeights, width, reducedMotion),
            _ => ProgressRing().IsActive(true).Size(32, 32).Center()
                .AutomationName(LocalizationHelper.GetString("Dashboards_Loading"))
                .AutomationId("DashboardLoading"),
        };

        var children = new List<Element> { header.Grid(row: 0) };
        if (snapshot.Status == DashboardLoadStatus.Ready && snapshot.Error is { } refreshError)
        {
            children.Add(InfoBar(LocalizationHelper.GetString("Dashboards_BoardLoadError_Title"),
                    LocalizationHelper.Format("Dashboards_LoadErrorFormat", refreshError))
                .Warning().IsClosable(false).Margin(0, 0, 0, 12)
                .AutomationId("DashboardStaleError")
                .Grid(row: 1));
        }
        children.Add(body.Grid(row: 2));
        var column = Border(Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Auto], children.ToArray()))
            .Width(ContentWidth(width) + 2 * HorizontalPadding(TierFor(width)))
            .MaxWidth(ContentMaxWidth + 2 * HorizontalPadding(TierFor(width)))
            .Padding(HorizontalPadding(TierFor(width)), 24, HorizontalPadding(TierFor(width)), 0)
            .HAlign(HorizontalAlignment.Center)
            .AutomationName(LocalizationHelper.GetString("Dashboards_BoardLabel"))
            .AutomationId("DashboardView");
        return Grid([GridSize.Star()], [GridSize.Star()],
                Border(Empty()).Background(Theme.Ref(DashboardHues.Hero))
                    .Height(backdropHeight).HAlign(HorizontalAlignment.Stretch).VAlign(VerticalAlignment.Top)
                    .Set(border => border.IsHitTestVisible = false)
                    .AccessibilityView(AccessibilityView.Raw).AutomationId("DashboardBackdrop"),
                DashboardsStates.VerticalScroll(column)
                    .AutomationName(LocalizationHelper.GetString("Dashboards_GridLabel"))
                    .AutomationId("DashboardPageScroll"))
            .OnSizeChanged((_, args) =>
            {
                if (props.ViewportWidth <= 0 && Snap(args.NewSize.Width) != Snap(measuredWidth))
                    setWidth(args.NewSize.Width);
            });
    }

    /// <summary>
    /// Web handoff for work Windows only displays. The browser opens the
    /// Gateway's Dashboards gallery, not this board, so the menu also copies
    /// the session key to find it there.
    /// </summary>
    private static Element? MoreActions(BoardViewProps props)
    {
        var state = props.State;
        if (state.OpenWebDashboards is null && state.CopySessionKey is null)
            return null;
        var menu = new MenuFlyout();
        if (state.OpenWebDashboards is { } openWeb)
        {
            var item = new MenuFlyoutItem
            {
                Text = LocalizationHelper.GetString("Dashboards_OpenWebGallery"),
                Icon = new FontIcon { Glyph = DashboardsGlyphs.OpenInBrowser },
            };
            ToolTipService.SetToolTip(item, LocalizationHelper.GetString("Dashboards_OpenWebGallery_BoardHint"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, "DashboardOpenWeb");
            item.Click += (_, _) => openWeb();
            menu.Items.Add(item);
        }
        if (state.CopySessionKey is { } copy)
        {
            var sessionKey = props.Target.SessionKey;
            var item = new MenuFlyoutItem
            {
                Text = LocalizationHelper.GetString("Dashboards_CopySessionKey"),
                Icon = new FontIcon { Glyph = DashboardsGlyphs.Copy },
            };
            ToolTipService.SetToolTip(item, sessionKey);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, "DashboardCopySessionKey");
            item.Click += (_, _) => copy(sessionKey);
            menu.Items.Add(item);
        }
        return DashboardsStates.IconButton(DashboardsGlyphs.More, LocalizationHelper.GetString("Dashboards_MoreActions"), () => { }, "DashboardMoreActions")
            .Set(button => button.Flyout = menu);
    }

    private static Element BoardHeader(BoardViewProps props, string title, bool reducedMotion, Element[] actions, double width, bool highContrast)
    {
        var narrow = TierFor(width) == PageWidthTier.Narrow;
        var row = props.State.OpenRow;
        Element meta;
        if (row is not null)
        {
            var author = DashboardGalleryPolicy.ResolveAuthor(row, props.Target.AgentId ?? props.State.AgentId);
            var authorLine = Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                DashboardsArt.Avatar(author.Initial, DashboardHues.For(author.Id), 18).VAlign(VerticalAlignment.Center),
                TextBlock(LocalizationHelper.Format("Dashboards_ByAuthorFormat", author.Label)).FontSize(13)
                    .Foreground(Theme.Ref("TextFillColorSecondaryBrush")).VAlign(VerticalAlignment.Center)
                    .TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(author.Label).Margin(GapInner, 0, 0, 0).Grid(column: 1));
            var updated = DashboardsPresentation.UpdatedText(row, DateTimeOffset.UtcNow);
            var updateLine = TextBlock(updated).FontSize(13).TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(updated)
                .Foreground(Theme.Ref("TextFillColorSecondaryBrush"));
            meta = Grid(narrow ? [GridSize.Star()] : [GridSize.Star(), GridSize.Auto, GridSize.Auto],
                    narrow ? [GridSize.Auto, GridSize.Auto] : [GridSize.Auto],
                    authorLine.Grid(row: 0),
                    updateLine.Margin(narrow ? 0 : GapGroup, narrow ? GapInner : 0, 0, 0)
                        .Grid(row: narrow ? 1 : 0, column: narrow ? 0 : 1),
                    row.IsLive && !narrow ? DashboardsArt.LiveBadge(reducedMotion).Margin(GapInner, 0, 0, 0).Grid(column: 2) : Empty())
                .AutomationId("DashboardsSubtitle");
        }
        else
        {
            meta = Empty();
        }

        return Border(Grid(
                narrow ? [GridSize.Star()] : [GridSize.Star(), GridSize.Auto],
                narrow ? [GridSize.Auto, GridSize.Auto] : [GridSize.Auto],
                VStack(6,
                        DashboardsStates.Eyebrow(highContrast),
                        TextBlock(title).FontSize(32).SemiBold()
                            .TextWrapping(TextWrapping.WrapWholeWords).MaxLines(2).ToolTip(title)
                            .TextTrimming(TextTrimming.CharacterEllipsis)
                            .HeadingLevel(AutomationHeadingLevel.Level1)
                            .AutomationId("DashboardsTitle"),
                        meta)
                    .VAlign(VerticalAlignment.Top)
                    .Grid(column: 0),
                HStack(GapInner, actions).VAlign(VerticalAlignment.Top)
                    .Margin(narrow ? 0 : GapStack, narrow ? GapGroup : 0, 0, 0)
                    .AutomationId("DashboardMastActions").Grid(row: narrow ? 1 : 0, column: narrow ? 0 : 1)))
            .Padding(0, 8, 0, 20)
            .Margin(0, 0, 0, 20);
    }

    private static Element Board(
        BoardViewProps props,
        BoardDocumentSnapshot snapshot,
        Ref<BoardDocumentModel?> model,
        Ref<ImmutableDictionary<string, double>> contentHeights,
        Action<ImmutableDictionary<string, double>> setContentHeights,
        double width,
        bool reducedMotion)
    {
        var board = snapshot.Board!;
        var tabs = board.OrderedTabs;
        var stacked = TierFor(width) == PageWidthTier.Narrow;
        var grids = snapshot.VisitedTabs
            .OrderBy(tab => tab, StringComparer.Ordinal)
            .Select(tabId => TabGrid(props, snapshot, tabId, model, contentHeights, setContentHeights, stacked, reducedMotion)
                .IsVisible(tabId == snapshot.ActiveTabId)
                .Grid(row: 0)
                .WithKey("tab:" + tabId))
            .ToArray();
        var empty = board.WidgetsForTab(snapshot.ActiveTabId).IsEmpty;
        // The grid host stays mounted (only hidden) on an empty tab, so widgets on
        // visited tabs keep their loaded documents when the user switches back.
        var gridHost = Border(Grid([GridSize.Star()], [GridSize.Auto], grids)
                .Margin(0, 0, 0, 24))
            .IsVisible(!empty)
            .AutomationName(LocalizationHelper.GetString("Dashboards_GridLabel"))
            .AutomationId("DashboardGrid");
        var emptyState = empty
            ? DashboardsStates.Status(
                "DashboardEmpty",
                DashboardsGlyphs.Dashboard,
                LocalizationHelper.GetString("Dashboards_BoardEmpty_Title"),
                LocalizationHelper.GetString("Dashboards_BoardEmpty_Detail"),
                props.State.OpenChat is { } openChat
                    ? Button(LocalizationHelper.GetString("Dashboards_OpenChat"), () => openChat(props.Target.SessionKey)).AccentButton()
                        .AutomationName(LocalizationHelper.GetString("Dashboards_OpenChat"))
                        .AutomationId("DashboardEmptyOpenChat")
                    : null,
                art: DashboardsArt.Mosaic(1.2, reducedMotion))
            : Empty();

        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto],
            Tabs(tabs, snapshot.ActiveTabId, tabId => model.Current?.SelectTab(tabId), width).Grid(row: 0),
            gridHost.Grid(row: 1),
            emptyState.Grid(row: 1));
    }

    private static Element Tabs(ImmutableArray<BoardTab> tabs, string activeTabId, Action<string> select, double width)
    {
        if (tabs.Length <= 1)
            return Empty();
        var limit = TierFor(width) switch { PageWidthTier.Narrow => 2, PageWidthTier.Medium => 4, _ => VisibleTabLimit };
        var visible = tabs.Take(limit).ToList();
        var active = tabs.FirstOrDefault(tab => tab.TabId == activeTabId);
        if (active is not null && !visible.Contains(active))
            visible[^1] = active;
        var overflow = tabs.Where(tab => !visible.Contains(tab)).ToArray();
        var selectedIndex = visible.FindIndex(tab => tab.TabId == activeTabId);
        var strip = SelectorBar(
                visible.Select(tab => SelectorBarItem(tab.Title)).ToArray(),
                selectedIndex,
                index =>
                {
                    if (index >= 0 && index < visible.Count)
                        select(visible[index].TabId);
                })
            .Set(bar =>
            {
                foreach (var item in bar.Items)
                {
                    item.MaxWidth = Math.Max(40, (ContentWidth(width) - 120) / Math.Max(1, visible.Count));
                    ToolTipService.SetToolTip(item, item.Text);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, item.Text);
                }
            })
            .AutomationName(LocalizationHelper.GetString("Dashboards_TabsLabel"))
            .AutomationId("DashboardTabs");
        if (overflow.Length == 0)
            return strip.Margin(0, 0, 0, 12);
        var menu = new MenuFlyout();
        foreach (var tab in overflow)
        {
            var item = new MenuFlyoutItem { Text = tab.Title };
            var tabId = tab.TabId;
            item.Click += (_, _) => select(tabId);
            menu.Items.Add(item);
        }
        return HStack(8,
                strip,
                DropDownButton(LocalizationHelper.GetString("Dashboards_MoreTabs"))
                    .Set(button => button.Flyout = menu)
                    .AutomationId("DashboardMoreTabs")
                    .VAlign(VerticalAlignment.Center))
            .Margin(0, 0, 0, 12);
    }

    private static Element TabGrid(
        BoardViewProps props,
        BoardDocumentSnapshot snapshot,
        string tabId,
        Ref<BoardDocumentModel?> model,
        Ref<ImmutableDictionary<string, double>> contentHeights,
        Action<ImmutableDictionary<string, double>> setContentHeights,
        bool stacked,
        bool reducedMotion)
    {
        var widgets = snapshot.Board!.WidgetsForTab(tabId);
        var byName = widgets.ToDictionary(widget => widget.Name, StringComparer.Ordinal);
        var rects = BoardGridLayout.Layout(widgets.Select(widget => new BoardGridItem(
            widget.Name,
            stacked ? BoardGridLayout.Columns : widget.SizeW,
            BoardGridLayout.EffectiveRows(widget, contentHeights.Current.TryGetValue(widget.Name, out var height) ? height : null),
            widget.Position)));
        var rowCount = Math.Max(1, BoardGridLayout.RowCount(rects));
        var kinds = snapshot.PluginWidgetKinds;
        var cells = rects.Select((rect, index) =>
        {
            var widget = byName[rect.Name];
            var body = BoardWidgetBodyPolicy.Resolve(widget, kinds);
            return Component<ReactorBoardWidgetCell, BoardWidgetCellProps>(new BoardWidgetCellProps(
                    widget,
                    body,
                    DashboardsPresentation.KindLabel(widget, kinds),
                    snapshot.GatewayOrigin,
                    snapshot.TicketFailures.TryGetValue(widget.Name, out var failure) ? failure : null,
                    name => model.Current?.ReportWidgetUnauthorized(name),
                    name => model.Current?.ReportWidgetReady(name),
                    name => model.Current?.RetryWidget(name),
                    (name, contentHeight) => props.State.Post(() =>
                    {
                        var current = contentHeights.Current;
                        if (current.TryGetValue(name, out var previous) && Math.Abs(previous - contentHeight) < 1)
                            return;
                        contentHeights.Current = current.SetItem(name, contentHeight);
                        setContentHeights(contentHeights.Current);
                    }),
                    reducedMotion,
                    props.State.OpenWebDashboards))
                .PositionInSet(index + 1, rects.Length)
                .Grid(row: rect.Y, column: rect.X, rowSpan: rect.H, columnSpan: rect.W)
                .WithKey(widget.Name);
        }).ToArray();
        return Grid(
                Enumerable.Repeat(GridSize.Star(), BoardGridLayout.Columns).ToArray(),
                Enumerable.Repeat(GridSize.Px(BoardGridLayout.RowHeight), rowCount).ToArray(),
                cells)
            .Set(grid =>
            {
                grid.ColumnSpacing = BoardGridLayout.Gap;
                grid.RowSpacing = BoardGridLayout.Gap;
            })
            .Stagger(TimeSpan.FromMilliseconds(reducedMotion ? 0 : 45))
            .AutomationId("DashboardTabGrid_" + tabId);
    }
}
