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
    double ViewportWidth = 0,
    double TopInset = 0);

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
        var (delivery, setDelivery) = UseState(
            (Snapshot: BoardDocumentSnapshot.Initial, Client: (IBoardGatewayClient?)null, Revision: 0), threadSafe: true);
        var snapshot = delivery.Snapshot;
        var (contentHeights, setContentHeights) = UseState(ImmutableDictionary<string, double>.Empty, threadSafe: true);
        var latestHeights = UseRef(ImmutableDictionary<string, double>.Empty);
        latestHeights.Current = contentHeights;
        var (measuredWidth, setWidth) = UseState(MinWidth);
        var width = props.ViewportWidth > 0 ? props.ViewportWidth : measuredWidth;
        var model = UseRef<BoardDocumentModel?>(null);
        var tabStrip = UseRef<SelectorBar?>(null);
        var overflowFocus = UseRef<(string TabId, bool FlyoutClosed)?>(null);
        var tabPanels = UseRef(new Dictionary<string, FrameworkElement>(StringComparer.Ordinal));
        UseEffect((Func<Action>)(() =>
        {
            var current = true;
            var queued = false;
            SelectorBar? focusOwner = null;
            void QueueOverflowFocus(object? sender, object args)
            {
                if (!current || queued || overflowFocus.Current is null)
                    return;
                queued = true;
                props.State.Post(() =>
                {
                    queued = false;
                    if (current)
                        RestoreOverflowFocus(tabStrip.Current, overflowFocus, tabPanels);
                });
            }
            props.State.Post(() =>
            {
                if (!current || tabStrip.Current is not { } strip)
                    return;
                if (overflowFocus.Current is not null)
                {
                    focusOwner = strip;
                    strip.LayoutUpdated += QueueOverflowFocus;
                    QueueOverflowFocus(null, EventArgs.Empty);
                }
                if (!strip.IsLoaded)
                    return;
                foreach (var item in strip.Items)
                {
                    var peers = Microsoft.UI.Xaml.Automation.AutomationProperties.GetControlledPeers(item);
                    peers.Clear();
                    var id = Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(item)["DashboardTab_".Length..];
                    if (tabPanels.Current.TryGetValue(id, out var panel) && panel.IsLoaded)
                        peers.Add(panel);
                }
            });
            return () =>
            {
                current = false;
                if (focusOwner is not null)
                    focusOwner.LayoutUpdated -= QueueOverflowFocus;
            };
        }), snapshot);

        UseEffect((Func<Action>)(() =>
        {
            var document = new BoardDocumentModel(props.Client, props.Target, props.State.Post, props.State.Schedule, snapshot);
            model.Current = document;
            document.Changed += next => setDelivery((next, props.Client, props.ConnectionRevision));
            _ = document.LoadAsync();
            return () =>
            {
                model.Current = null;
                tabStrip.Current = null;
                tabPanels.Current.Clear();
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
        var header = BoardHeader(props, title, reducedMotion, actions.ToArray(), width, highContrast);

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
                Board(props, snapshot, model, latestHeights, setContentHeights, width, reducedMotion, tabStrip, overflowFocus, tabPanels,
                    ReferenceEquals(delivery.Client, props.Client) && delivery.Revision == props.ConnectionRevision),
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
                DashboardsStates.VerticalScroll(column)
                    .Margin(0, props.TopInset, 0, 0)
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
            var updated = DashboardsPresentation.UpdatedText(row, DateTimeOffset.UtcNow);
            var authorLine = Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                DashboardsArt.Avatar(author.Initial, DashboardHues.For(author.Id), 18).VAlign(VerticalAlignment.Center),
                TextBlock(LocalizationHelper.Format("Dashboards_ByAuthorFormat", author.Label) + " · " + updated).FontSize(13)
                    .Foreground(Theme.Ref("TextFillColorSecondaryBrush")).VAlign(VerticalAlignment.Center)
                    .TextWrapping(TextWrapping.WrapWholeWords).Margin(GapInner, 0, 0, 0).Grid(column: 1))
                .MaxWidth(ContentWidth(width) / 2);
            meta = Grid([GridSize.Auto, GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                    authorLine.Grid(row: 0),
                    row.IsLive ? DashboardsArt.LiveBadge(reducedMotion).Margin(GapInner, 0, 0, 0)
                        .VAlign(VerticalAlignment.Center).Grid(column: 1) : Empty())
                .AutomationId("DashboardsSubtitle");
        }
        else
        {
            meta = Empty();
        }

        return PageHero.Header("DashboardHero", DashboardsGlyphs.Dashboard, title, "DashboardsTitle",
                meta, null, HStack(GapInner, actions).AutomationId("DashboardMastActions"),
                narrow, reducedMotion, highContrast, pageTag: DashboardsRoute.Tag)
            .Margin(0, 0, 0, 20);
    }

    private static Element Board(
        BoardViewProps props,
        BoardDocumentSnapshot snapshot,
        Ref<BoardDocumentModel?> model,
        Ref<ImmutableDictionary<string, double>> contentHeights,
        Action<ImmutableDictionary<string, double>> setContentHeights,
        double width,
        bool reducedMotion,
        Ref<SelectorBar?> tabStrip,
        Ref<(string TabId, bool FlyoutClosed)?> overflowFocus,
        Ref<Dictionary<string, FrameworkElement>> tabPanels,
        bool documentCurrent)
    {
        var board = snapshot.Board!;
        var tabs = board.OrderedTabs;
        if (tabs.IsEmpty && board.Widgets.IsEmpty)
            return EmptySection(props, props.State.OpenTitle ?? LocalizationHelper.GetString("Dashboards_BoardLabel"), "DashboardEmpty", reducedMotion);
        var stacked = TierFor(width) == PageWidthTier.Narrow;
        var grids = snapshot.VisitedTabs
            .OrderBy(tab => tab, StringComparer.Ordinal)
            .Select(tabId => TabGrid(props, snapshot, tabId, model, contentHeights, setContentHeights, stacked, reducedMotion, tabPanels, documentCurrent)
                .IsVisible(tabId == snapshot.ActiveTabId)
                .Grid(row: 0)
                .WithKey("tab:" + tabId))
            .ToArray();
        // Visited panels remain mounted, including when an empty section opens.
        var gridHost = Border(Grid([GridSize.Star()], [GridSize.Auto], grids)
                .Margin(0, 0, 0, 24))
            .AutomationName(LocalizationHelper.GetString("Dashboards_GridLabel"))
            .AutomationId("DashboardGrid");

        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto],
            Tabs(tabs, snapshot.ActiveTabId, tabId => model.Current?.SelectTab(tabId), width, tabStrip, overflowFocus, tabPanels).Grid(row: 0),
            gridHost.Grid(row: 1));
    }

    private static Element Tabs(ImmutableArray<BoardTab> tabs, string activeTabId, Action<string> select, double width,
        Ref<SelectorBar?> tabStrip, Ref<(string TabId, bool FlyoutClosed)?> overflowFocus,
        Ref<Dictionary<string, FrameworkElement>> tabPanels)
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
                tabStrip.Current = bar;
                for (var index = 0; index < bar.Items.Count; index++)
                {
                    var item = bar.Items[index];
                    item.MaxWidth = Math.Max(40, (ContentWidth(width) - 120) / Math.Max(1, visible.Count));
                    ToolTipService.SetToolTip(item, item.Text);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, item.Text);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, "DashboardTab_" + visible[index].TabId);
                }
            })
            .AutomationName(LocalizationHelper.GetString("Dashboards_TabsLabel"))
            .AutomationId("DashboardTabs");
        if (overflow.Length == 0)
            return strip.Margin(0, 0, 0, 12);
        var menu = new MenuFlyout();
        string? selectedFromMenu = null;
        menu.Closed += (_, _) =>
        {
            if (selectedFromMenu is { } tabId && tabStrip.Current is { } bar)
                bar.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (overflowFocus.Current?.TabId != tabId)
                        return;
                    // Closed returns before WinUI finishes restoring the flyout anchor's focus.
                    overflowFocus.Current = (tabId, true);
                    RestoreOverflowFocus(tabStrip.Current, overflowFocus, tabPanels);
                });
        };
        foreach (var tab in overflow)
        {
            var item = new MenuFlyoutItem { Text = tab.Title };
            var tabId = tab.TabId;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, "DashboardOverflowTab_" + tabId);
            item.Click += (_, _) =>
            {
                selectedFromMenu = tabId;
                overflowFocus.Current = (tabId, false);
                select(tabId);
            };
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

    private static void RestoreOverflowFocus(SelectorBar? strip,
        Ref<(string TabId, bool FlyoutClosed)?> pending, Ref<Dictionary<string, FrameworkElement>> tabPanels)
    {
        if (pending.Current is not { FlyoutClosed: true } request ||
            strip is not { IsLoaded: true, SelectedItem: { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } selected } ||
            Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(selected) != "DashboardTab_" + request.TabId ||
            !tabPanels.Current.TryGetValue(request.TabId, out var panel) || !panel.IsLoaded)
            return;
        var peers = Microsoft.UI.Xaml.Automation.AutomationProperties.GetControlledPeers(selected);
        peers.Clear();
        peers.Add(panel);
        if (selected.Focus(FocusState.Programmatic))
            pending.Current = null;
    }

    private static Element TabGrid(
        BoardViewProps props,
        BoardDocumentSnapshot snapshot,
        string tabId,
        Ref<BoardDocumentModel?> model,
        Ref<ImmutableDictionary<string, double>> contentHeights,
        Action<ImmutableDictionary<string, double>> setContentHeights,
        bool stacked,
        bool reducedMotion,
        Ref<Dictionary<string, FrameworkElement>> tabPanels,
        bool documentCurrent)
    {
        var widgets = snapshot.Board!.WidgetsForTab(tabId);
        var tabTitle = snapshot.Board.OrderedTabs.First(tab => tab.TabId == tabId).Title;
        var emptyId = tabId == snapshot.ActiveTabId ? "DashboardEmpty" : "DashboardEmpty_" + tabId;
        if (widgets.IsEmpty)
            return Grid([GridSize.Star()], [GridSize.Auto], EmptySection(props, tabTitle, emptyId, reducedMotion))
                .Set(grid => tabPanels.Current[tabId] = grid)
                .AutomationName(tabTitle).AutomationId("DashboardTabGrid_" + tabId);
        var byName = widgets.ToDictionary(widget => widget.Name, StringComparer.Ordinal);
        var rects = BoardGridLayout.Layout(widgets.Select(widget => new BoardGridItem(
            widget.Name,
            stacked ? BoardGridLayout.Columns : widget.SizeW,
            BoardGridLayout.EffectiveRows(widget, contentHeights.Current.TryGetValue(widget.Name, out var height) ? height : null),
            widget.Position)));
        var rowCount = Math.Max(1, BoardGridLayout.RowCount(rects));
        var kinds = snapshot.PluginWidgetKinds;
        var reportRows = rects
            .Where(rect => BoardWidgetBodyPolicy.Resolve(byName[rect.Name], kinds) == BoardWidgetBodyKind.Report)
            .SelectMany(rect => Enumerable.Range(rect.Y, rect.H))
            .ToHashSet();
        var cells = rects.Select((rect, index) =>
        {
            var widget = byName[rect.Name];
            var body = BoardWidgetBodyPolicy.Resolve(widget, kinds);
            var allocatedHeight = rect.H * BoardGridLayout.RowHeight + (rect.H - 1) * BoardGridLayout.Gap;
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
                    props.State.OpenWebDashboards,
                    allocatedHeight,
                    props.Client,
                    props.ConnectionRevision,
                    documentCurrent,
                    () => props.State.Connected && ReferenceEquals(props.State.Client, props.Client) &&
                        props.State.ConnectionRevision == props.ConnectionRevision))
                .MinHeight(allocatedHeight)
                .VAlign(VerticalAlignment.Stretch)
                .PositionInSet(index + 1, rects.Length)
                .Grid(row: rect.Y, column: rect.X, rowSpan: rect.H, columnSpan: rect.W)
                .WithKey(widget.Name);
        }).ToArray();
        return Grid(
                Enumerable.Repeat(GridSize.Star(), BoardGridLayout.Columns).ToArray(),
                Enumerable.Range(0, rowCount)
                    .Select(row => reportRows.Contains(row) ? GridSize.Auto : GridSize.Px(BoardGridLayout.RowHeight)).ToArray(),
                cells)
            .Set(grid =>
            {
                tabPanels.Current[tabId] = grid;
                grid.ColumnSpacing = BoardGridLayout.Gap;
                grid.RowSpacing = BoardGridLayout.Gap;
                // Native reports grow their shared rows; HTML retains its bounded row allocation.
                foreach (var row in reportRows)
                    grid.RowDefinitions[row].MinHeight = BoardGridLayout.RowHeight;
            })
            .Stagger(TimeSpan.FromMilliseconds(reducedMotion ? 0 : 45))
            .AutomationName(tabTitle)
            .AutomationId("DashboardTabGrid_" + tabId);
    }

    private static Element EmptySection(BoardViewProps props, string title, string automationId, bool reducedMotion) =>
        DashboardsStates.Status(
            automationId, DashboardsGlyphs.Dashboard,
            LocalizationHelper.GetString("Dashboards_BoardEmpty_Title"),
            LocalizationHelper.GetString("Dashboards_BoardEmpty_Detail"),
            props.State.OpenChat is { } openChat
                ? Button(LocalizationHelper.GetString("Dashboards_OpenChat"), () => openChat(props.Target.SessionKey)).AccentButton()
                    .AutomationName(LocalizationHelper.Format("Dashboards_OpenChatFor", title))
                    .AutomationId(automationId + "OpenChat")
                : null,
            art: DashboardsArt.Mosaic(1.2, reducedMotion));
}
