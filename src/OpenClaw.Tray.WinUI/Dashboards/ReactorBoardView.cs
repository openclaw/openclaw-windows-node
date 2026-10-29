using System.Collections.Immutable;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared.Dashboards;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Dashboards;

internal sealed record BoardViewProps(
    DashboardsPageState State,
    IBoardGatewayClient Client,
    BoardSessionTarget Target,
    int ConnectionRevision);

/// <summary>
/// One open session dashboard: header, tab strip, and the 12-column widget
/// grid. Widgets of visited tabs stay mounted (hidden) so switching tabs
/// keeps their live content, like the upstream board view.
/// </summary>
internal sealed class ReactorBoardView : Component<BoardViewProps>
{
    /// <summary>Below this width every widget spans the full row, like the web's stacked layout.</summary>
    private const double StackedLayoutWidth = 720;

    /// <summary>Tabs shown in the strip before the overflow menu (upstream shows six).</summary>
    private const int VisibleTabLimit = 6;

    public override Element Render()
    {
        var props = Props;
        var (snapshot, setSnapshot) = UseState(BoardDocumentSnapshot.Initial, threadSafe: true);
        var (contentHeights, setContentHeights) = UseState(ImmutableDictionary<string, double>.Empty, threadSafe: true);
        var latestHeights = UseRef(ImmutableDictionary<string, double>.Empty);
        latestHeights.Current = contentHeights;
        var (width, setWidth) = UseState(0d);
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

        var title = props.State.OpenTitle ?? props.Target.SessionKey;
        var reducedMotion = UseReducedMotion();
        var refresh = () => { _ = model.Current?.LoadAsync(); };
        var actions = new List<Element>();
        if (props.State.OpenChat is { } openChat)
        {
            actions.Add(Button(
                    HStack(8,
                        DashboardsStates.Glyph(DashboardsGlyphs.Chat, 14, "TextFillColorPrimaryBrush").VAlign(VerticalAlignment.Center),
                        TextBlock(LocalizationHelper.GetString("Dashboards_OpenChat"))),
                    () => openChat(props.Target.SessionKey))
                .AutomationName(LocalizationHelper.GetString("Dashboards_OpenChat"))
                .AutomationId("DashboardOpenChat"));
        }
        actions.Add(DashboardsStates.IconButton(DashboardsGlyphs.Refresh, LocalizationHelper.GetString("Dashboards_Refresh"), refresh, "DashboardRefresh"));
        var header = BoardHeader(props, title, reducedMotion, actions.ToArray());

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
        return Border(Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Star()], children.ToArray())
            .OnSizeChanged((_, args) =>
            {
                if (Math.Abs(args.NewSize.Width - width) > 1)
                    setWidth(args.NewSize.Width);
            }))
            .Padding(32, 24, 32, 0)
            .AutomationName(LocalizationHelper.GetString("Dashboards_BoardLabel"))
            .AutomationId("DashboardView");
    }

    private static Element BoardHeader(BoardViewProps props, string title, bool reducedMotion, Element[] actions)
    {
        var hue = DashboardHues.For(props.Target.SessionKey);
        var row = props.State.OpenRow;
        Element meta;
        if (row is not null)
        {
            var author = DashboardGalleryPolicy.ResolveAuthor(row, props.Target.AgentId ?? props.State.AgentId);
            var parts = new List<Element>
            {
                DashboardsArt.Avatar(author.Initial, DashboardHues.For(author.Id), 18).VAlign(VerticalAlignment.Center),
                TextBlock(LocalizationHelper.Format("Dashboards_ByAuthorFormat", author.Label)).FontSize(13)
                    .Foreground(Theme.Ref("TextFillColorSecondaryBrush")).VAlign(VerticalAlignment.Center),
                DashboardsStates.Glyph(DashboardsGlyphsExtra.Clock, 12, "TextFillColorTertiaryBrush")
                    .Margin(8, 0, 0, 0).VAlign(VerticalAlignment.Center),
                TextBlock(DashboardsPresentation.UpdatedText(row, DateTimeOffset.UtcNow)).FontSize(13)
                    .Foreground(Theme.Ref("TextFillColorSecondaryBrush")).VAlign(VerticalAlignment.Center),
            };
            if (row.IsLive)
                parts.Add(DashboardsArt.LiveBadge(reducedMotion).Margin(8, 0, 0, 0));
            meta = HStack(6, parts.ToArray()).AutomationId("DashboardsSubtitle");
        }
        else
        {
            meta = TextBlock(props.Target.SessionKey).FontSize(13)
                .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                .TextTrimming(TextTrimming.CharacterEllipsis)
                .AutomationId("DashboardsSubtitle");
        }

        return Grid(
                [GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                DashboardsStates.IconButton(DashboardsGlyphs.Back, LocalizationHelper.GetString("Dashboards_Back"), () => props.State.Open(null), "DashboardBack")
                    .VAlign(VerticalAlignment.Center).Margin(0, 0, 8, 0).Grid(column: 0),
                DashboardsArt.Monogram(title, hue, 52).VAlign(VerticalAlignment.Center).Margin(0, 0, 16, 0).Grid(column: 1),
                VStack(6,
                        TextBlock(title).FontSize(28).SemiBold()
                            .TextTrimming(TextTrimming.CharacterEllipsis)
                            .HeadingLevel(AutomationHeadingLevel.Level1)
                            .AutomationId("DashboardsTitle"),
                        meta)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(column: 2),
                HStack(8, actions).VAlign(VerticalAlignment.Center).Grid(column: 3))
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
        var stacked = width > 0 && width < StackedLayoutWidth;
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
        var gridHost = ScrollView(Grid([GridSize.Star()], [GridSize.Auto], grids)
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
                    ? Button(LocalizationHelper.GetString("Dashboards_AskInChat"), () => openChat(props.Target.SessionKey)).AccentButton()
                        .AutomationName(LocalizationHelper.GetString("Dashboards_AskInChat"))
                        .AutomationId("DashboardEmptyOpenChat")
                    : null,
                art: DashboardsArt.Mosaic(1.2, reducedMotion))
            : Empty();

        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Star()],
            Tabs(tabs, snapshot.ActiveTabId, tabId => model.Current?.SelectTab(tabId)).Grid(row: 0),
            gridHost.Grid(row: 1),
            emptyState.Grid(row: 1));
    }

    private static Element Tabs(ImmutableArray<BoardTab> tabs, string activeTabId, Action<string> select)
    {
        if (tabs.Length <= 1)
            return Empty();
        var visible = tabs.Take(VisibleTabLimit).ToList();
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
                    reducedMotion))
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
