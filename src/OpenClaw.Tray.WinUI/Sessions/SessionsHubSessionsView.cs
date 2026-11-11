using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using static Microsoft.UI.Reactor.Factories;
using MenuFlyoutItemBase = Microsoft.UI.Reactor.Core.MenuFlyoutItemBase;

namespace OpenClawTray.Sessions;

/// <summary>Sessions tab: filter toolbar, grouped roster, selection, pager, details, and transcript search.</summary>
internal static class SessionsHubSessionsView
{
    private const string ChevronLeftGlyph = "\uE76B"; // ChevronLeft
    private static string T(string key) => SessionsHubPresentation.T(key);
    private static string F(string key, params object?[] args) => SessionsHubPresentation.F(key, args);

    public static Element Render(SessionsHubViewContext context)
    {
        var state = context.State;
        var titles = SessionsHubPresentation.Titles(state.Rows);
        var main = state.Transcript.IsOpen
            ? SessionsHubSearchView.Render(context, titles)
            : Roster(context, titles);

        return Grid([GridSize.Star()],
                [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Auto],
                InventoryTitle(context).Margin(0, 4, 0, 10).Grid(row: 0),
                Toolbar(context).Grid(row: 1),
                FilterNote(state).Grid(row: 2),
                TranscriptPrompt(context).Grid(row: 3),
                SelectionBar(context).Grid(row: 4),
                main.Grid(row: 5))
            .Padding(0, 0, 12, 12);
    }

    private static Element InventoryTitle(SessionsHubViewContext context)
    {
        var state = context.State;
        var title = state.Filters.Search.Trim().Length > 0 ? T("MatchingSessions") : state.Filters.Status switch
        {
            SessionArchiveFilter.Archived => T("ArchivedSessions"),
            SessionArchiveFilter.All => T("StatAll"),
            _ => T("CurrentSessions"),
        };
        var shown = SessionsHubProjection.Page(Ordered(context), state.PageIndex, state.PageSize).Rows.Count;
        return HStack(8,
                SessionsHubView.SectionTitle(title),
                TextBlock(F("ShownOfLoaded", shown, state.Rows.Count)).FontSize(12)
                    .Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center)
                    .AutomationId("SessionsHubInventoryCount"))
            .AutomationId("SessionsHubAllSessions");
    }

    private static Element FilterNote(SessionsHubState state)
    {
        var filters = state.Filters;
        return TextBlock(F("ViewFilterNote", filters.ApplyClientFilters(state.Rows).Count(),
                T(filters.ShowBackground ? "BackgroundShown" : "BackgroundHidden"),
                T(filters.ShowCompleted || filters.Status != SessionArchiveFilter.Active ? "CompletedShown" : "CompletedHidden"),
                T(filters.IncludeUnknown ? "UnroutedShown" : "UnroutedHidden")))
            .FontSize(12).Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.Wrap)
            .Margin(0, 0, 0, PageResponsiveLayout.GapInner)
            .AutomationId("SessionsHubFilterNote");
    }

    private static Element TranscriptPrompt(SessionsHubViewContext context)
    {
        var state = context.State;
        var query = state.Filters.Search.Trim();
        if (state.Transcript.IsOpen || query.Length == 0 || !state.IsConnected
            || !state.Permissions.Supports("sessions.search"))
            return Empty();
        return Border(Button(
                    HStack(8,
                        SessionsHubView.Glyph("\uE721", 14, Theme.Accent),
                        TextBlock(F("SearchMessagesFor", query)).Foreground(Theme.Accent)
                            .FontWeight(FontWeights.SemiBold),
                        SessionsHubView.Glyph(FluentIconCatalog.ChevronR, 12, Theme.Accent)),
                    context.Controller.SearchTranscriptsForCurrentQuery)
                .SubtleButton()
                .HAlign(HorizontalAlignment.Stretch)
                .HorizontalContentAlignment(HorizontalAlignment.Left)
                .AutomationName(F("SearchMessagesFor", query)))
            .Background(Theme.Ref("AccentFillColorSecondaryBrush"))
            .WithBorder(Theme.Accent, 1)
            .CornerRadius(8)
            .Padding(4)
            .Margin(0, 0, 0, 8)
            .AutomationId("SessionsHubTranscriptPrompt");
    }

    /// <summary>Reveals a loaded session in its roster page and group before expanding it.</summary>
    public static void RevealDetails(SessionsHubViewContext context, string key, bool focus = false)
    {
        var controller = context.Controller;
        var state = controller.State;
        var row = state.Rows.FirstOrDefault(row => row.Key == key);
        if (row is null) return;
        controller.SetFilters(state.Filters with
        {
            ShowCompleted = state.Filters.ShowCompleted
                || (state.Filters.Status == SessionArchiveFilter.Active && SessionRunState.IsCompleted(row)),
            ShowBackground = state.Filters.ShowBackground || SessionDisplayResolver.IsBackground(row),
        }, persist: false);
        controller.OpenDetails(key);
        var current = context with { State = controller.State };
        var ordered = Ordered(current);
        var index = ordered.ToList().FindIndex(row => row.Key == key);
        if (index >= 0)
            controller.SetPage(index / current.State.PageSize);
        var group = SessionsHubProjection.Group(ordered, current.State.GroupBy, controller.KnownGroups())
            .FirstOrDefault(group => group.Rows.Any(row => row.Key == key));
        if (group is not null && context.CollapsedGroups.Contains(group.Id))
        {
            var collapsed = new HashSet<string>(context.CollapsedGroups, StringComparer.Ordinal);
            collapsed.Remove(group.Id);
            context.SetCollapsedGroups(collapsed);
        }
        controller.SetTranscriptOpen(false);
        controller.RequestRowReveal(key, focus);
    }

    // ── Toolbar ──

    private static Element Toolbar(SessionsHubViewContext context)
    {
        var state = context.State;
        var controller = context.Controller;
        var filters = state.Filters;
        var enabled = state.IsConnected;

        var agentNames = new[] { T("AllAgents") }.Concat(state.Agents.Select(agent => agent.Name)).ToArray();
        var agentIndex = filters.AgentId is null ? 0 : state.Agents.ToList().FindIndex(agent => agent.Id == filters.AgentId) + 1;
        if (filters.AgentId is not null && agentIndex == 0)
        {
            agentNames = [.. agentNames, filters.AgentId];
            agentIndex = agentNames.Length - 1;
        }
        var agent = ComboBox(agentNames, agentIndex, index =>
            {
                if (index < 0) return;
                var id = index <= 0 ? null : index <= state.Agents.Count ? state.Agents[index - 1].Id : filters.AgentId;
                controller.SetFilters(filters with { AgentId = id });
            })
            .IsEnabled(enabled)
            .AutomationName(T("AgentFilter"))
            .ToolTip(T("AgentFilter"))
            .MinWidth(160)
            // WinUI's ComboBox style defaults to Top; center it with the toolbar buttons.
            .VAlign(VerticalAlignment.Center)
            .AutomationId("SessionsHubAgentFilter")
            .WithKey("agent");

        var search = TextBox(filters.Search, controller.SetSearch, T("SearchPlaceholder"))
            .AcceptsReturn(false)
            .TextWrapping(TextWrapping.NoWrap)
            .VAlign(VerticalAlignment.Center)
            .IsEnabled(enabled && !state.Transcript.IsOpen)
            .AutomationName(T("SearchPlaceholder"))
            .MinWidth(220)
            .AutomationId("SessionsHubSearch")
            .WithKey("search");

        var status = SelectorBar(
                [SelectorBarItem(T("StatusActive")), SelectorBarItem(T("StatusArchived")), SelectorBarItem(T("StatusAll"))],
                (int)filters.Status,
                index =>
                {
                    if (index is >= 0 and <= 2)
                        controller.SetFilters(filters with { Status = (SessionArchiveFilter)index });
                })
            .Set(bar => SessionsHubView.TagItems(bar, "SessionsPageStatus", ["Active", "Archived", "All"]))
            .Set(bar =>
            {
                if (bar.Items.Count > 0)
                    ToolTipService.SetToolTip(bar.Items[0], T("StatusCurrentHelp"));
            })
            .IsEnabled(enabled)
            .AutomationName(T("StatusFilter"))
            .AutomationId("SessionsHubStatus")
            .WithKey("status");

        var optionsLabel = T("Options");
        var options = DropDownButton(optionsLabel, MenuItems(OptionItems(context)))
            .IsEnabled(enabled)
            .AutomationName(optionsLabel)
            .ToolTip(optionsLabel)
            .AutomationId("SessionsHubOptions")
            .WithKey("options");

        var canSelect = state.Filters.ApplyClientFilters(state.Rows).Any();
        var select = ToggleButton(T("Select"), state.SelectionMode, controller.SetSelectionMode)
            .IsEnabled(enabled && !state.Transcript.IsOpen && (canSelect || state.SelectionMode))
            .ToolTip(T(canSelect || state.SelectionMode ? "SelectTip" : "NoMatchingSelection"))
            .AutomationId("SessionsHubSelectToggle")
            .WithKey("select");

        var deleteAllArchivedHelp = state.CanMutateRows
            ? state.Permissions.CanWrite ? T("DeleteAllArchivedTip") : T("NeedsWrite")
            : T("RosterNotCurrent");
        Element archiveDelete = filters.Status == SessionArchiveFilter.Archived
            ? SessionsHubView.CommandButton(FluentIconCatalog.Delete, T("DeleteAllArchived"),
                () => _ = controller.DeleteAllArchivedAsync(),
                enabled && state.Permissions.CanWrite && state.Rows.Any(row => row.Archived) && state.CanMutateRows,
                "SessionsHubDeleteAllArchived",
                tooltip: deleteAllArchivedHelp)
                .HelpText(deleteAllArchivedHelp)
            : Empty();
        archiveDelete = archiveDelete.WithKey("archive-delete");

        Element toolbar = context.Tier switch
        {
            PageWidthTier.Wide => Grid(
                [GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Px(360), GridSize.Auto, GridSize.Auto, GridSize.Auto],
                [GridSize.Auto],
                status.Grid(column: 0),
                agent.Margin(PageResponsiveLayout.GapInner, 0, 0, 0).Grid(column: 1),
                Border(Empty()).Grid(column: 2),
                search.Grid(column: 3),
                options.Margin(PageResponsiveLayout.GapInner, 0, 0, 0).Grid(column: 4),
                select.Margin(PageResponsiveLayout.GapInner, 0, 0, 0).Grid(column: 5),
                archiveDelete.Margin(PageResponsiveLayout.GapInner, 0, 0, 0).Grid(column: 6)),
            PageWidthTier.Medium => Grid(
                [GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto, GridSize.Auto, GridSize.Auto],
                [GridSize.Auto, GridSize.Auto],
                status.Grid(row: 0, column: 0),
                agent.Margin(PageResponsiveLayout.GapInner, 0, 0, 0).Grid(row: 0, column: 1),
                Border(Empty()).Grid(row: 0, column: 2),
                options.Grid(row: 0, column: 3),
                select.Margin(PageResponsiveLayout.GapInner, 0, 0, 0).Grid(row: 0, column: 4),
                archiveDelete.Margin(PageResponsiveLayout.GapInner, 0, 0, 0).Grid(row: 0, column: 5),
                search.Margin(0, PageResponsiveLayout.GapGroup, 0, 0).HAlign(HorizontalAlignment.Stretch)
                    .Grid(row: 1, column: 0, columnSpan: 6)),
            _ => Grid(
                [GridSize.Star(), GridSize.Auto, GridSize.Auto, GridSize.Auto],
                [GridSize.Auto, GridSize.Auto, GridSize.Auto],
                status.Grid(row: 0, column: 0, columnSpan: 4),
                agent.Margin(0, PageResponsiveLayout.GapGroup, 0, 0).Grid(row: 1, column: 0),
                options.Margin(PageResponsiveLayout.GapInner, PageResponsiveLayout.GapGroup, 0, 0).Grid(row: 1, column: 1),
                select.Margin(PageResponsiveLayout.GapInner, PageResponsiveLayout.GapGroup, 0, 0).Grid(row: 1, column: 2),
                archiveDelete.Margin(PageResponsiveLayout.GapInner, PageResponsiveLayout.GapGroup, 0, 0).Grid(row: 1, column: 3),
                search.Margin(0, PageResponsiveLayout.GapGroup, 0, 0).HAlign(HorizontalAlignment.Stretch)
                    .Grid(row: 2, column: 0, columnSpan: 4)),
        };
        return toolbar.Margin(0, 0, 0, PageResponsiveLayout.GapInner)
            .AutomationId("SessionsHubToolbar");
    }

    private static MenuFlyoutItemBase[] OptionItems(SessionsHubViewContext context)
    {
        var state = context.State;
        var controller = context.Controller;
        var filters = state.Filters;
        return
        [
            MenuHeader(T("GroupBy")),
            GroupItem(context, SessionsGroupBy.Date),
            GroupItem(context, SessionsGroupBy.Agent),
            GroupItem(context, SessionsGroupBy.Category),
            GroupItem(context, SessionsGroupBy.None),
            MenuSeparator(),
            MenuHeader(T("Sort")),
            SortItem(context, SessionsSortColumn.Updated, true, T("SortNewest")),
            SortItem(context, SessionsSortColumn.Name, false, T("SortNameAsc")),
            MenuSeparator(),
            MenuHeader(T("Filters")),
            ToggleItem(LocalizationHelper.GetString("SessionsPage_ShowBackground.Header"), filters.ShowBackground,
                () => controller.SetFilters(filters with { ShowBackground = !filters.ShowBackground })),
            ToggleItem(LocalizationHelper.GetString("SessionsPage_ShowCompleted.Header"), filters.ShowCompleted,
                () => controller.SetFilters(filters with { ShowCompleted = !filters.ShowCompleted })),
            ToggleItem(T("IncludeGlobal"), filters.IncludeGlobal,
                () => controller.SetFilters(filters with { IncludeGlobal = !filters.IncludeGlobal })),
            ToggleItem(T("IncludeUnknown"), filters.IncludeUnknown,
                () => controller.SetFilters(filters with { IncludeUnknown = !filters.IncludeUnknown })),
            MenuSeparator(),
            SessionsHubView.Item(T("NewGroupUnsupported"), FluentIconCatalog.Add, () => { }, enabled: false),
        ];
    }

    private static MenuFlyoutItemBase MenuHeader(string text) =>
        SessionsHubView.Item(text, null, () => { }, enabled: false);

    private static MenuFlyoutItemBase GroupItem(SessionsHubViewContext context, SessionsGroupBy mode) =>
        new RadioMenuFlyoutItemData(SessionsHubPresentation.GroupByLabel(mode), "sessions-group",
            context.State.GroupBy == mode, () => context.Controller.SetGroupBy(mode));

    private static MenuFlyoutItemBase ToggleItem(string label, bool isChecked, Action toggle) =>
        SessionsHubView.Item(label, isChecked ? FluentIconCatalog.Check : null, toggle);

    private static MenuFlyoutItemBase SortItem(SessionsHubViewContext context, SessionsSortColumn column, bool descending, string label) =>
        new RadioMenuFlyoutItemData(label, "sessions-sort",
            context.State.SortColumn == column && context.State.SortDescending == descending,
            () => context.Controller.SetSort(column, descending));

    // ── Selection ──

    private static Element SelectionBar(SessionsHubViewContext context)
    {
        var state = context.State;
        if (!state.SelectionMode || state.Transcript.IsOpen)
            return Empty();
        var controller = context.Controller;
        var pageKeys = VisiblePageKeys(context);
        var pageState = state.Selection.PageState(pageKeys);
        var selectedRows = state.Filters.ApplyClientFilters(state.Rows).Where(row => state.Selection.Contains(row.Key)).ToArray();
        var deletable = selectedRows.Count(row => state.Permissions.CanDelete(row) && !controller.IsMain(row));
        return Border(Grid([GridSize.Star(), GridSize.Auto, GridSize.Auto, GridSize.Auto], [GridSize.Auto],
                    TextBlock(state.Selection.Count == 1 ? T("SelectedOne") : F("SelectedMany", state.Selection.Count))
                        .VAlign(VerticalAlignment.Center).LiveRegion().AutomationId("SessionsHubSelectedCount")
                        .Grid(column: 0),
                    Button(T("SelectPage"), () => controller.SetSelected(pageKeys, pageState != true))
                        .SubtleButton().AutomationName(T("SelectPage")).AutomationId("SessionsHubSelectPage")
                        .Margin(8, 0).Grid(column: 1),
                    SessionsHubView.CommandButton(FluentIconCatalog.Delete, T("DeleteSelected"), () => _ = controller.DeleteSelectedAsync(),
                            deletable > 0 && state.CanMutateRows, "SessionsHubDeleteSelected",
                            tooltip: !state.CanMutateRows ? T("RosterNotCurrent")
                                : deletable > 0 ? T("DeleteSelected") : T("DeleteSelectedNoneAllowed"))
                        .HelpText(!state.CanMutateRows ? T("RosterNotCurrent")
                            : deletable > 0 ? T("DeleteSelected") : T("DeleteSelectedNoneAllowed"))
                        .Margin(0, 0, 8, 0).Grid(column: 2),
                    Button(T("Cancel"), () => controller.SetSelectionMode(false)).SubtleButton()
                        .AutomationName(T("Cancel")).AutomationId("SessionsHubClearSelection").Grid(column: 3)))
            .Padding(12, 8)
            .Background(Theme.Ref("AccentFillColorSecondaryBrush"))
            .CornerRadius(8)
            .Margin(0, 0, 0, 8)
            .AutomationId("SessionsHubSelectionBar");
    }

    // ── Roster ──

    private static IReadOnlyList<SessionInfo> Ordered(SessionsHubViewContext context, IReadOnlyDictionary<string, string>? titles = null)
    {
        var state = context.State;
        titles ??= SessionsHubPresentation.Titles(state.Rows);
        var visible = state.Filters.ApplyClientFilters(state.Rows).ToArray();
        var sorted = SessionsHubProjection.Sort(visible, state.SortColumn, state.SortDescending,
            row => titles.TryGetValue(row.Key, out var title) ? title : row.Key);
        return SessionsHubProjection.Group(sorted, state.GroupBy, context.Controller.KnownGroups())
            .SelectMany(group => group.Rows).ToArray();
    }

    private static IReadOnlyList<string> VisiblePageKeys(SessionsHubViewContext context)
    {
        var state = context.State;
        var ordered = Ordered(context);
        var pageRows = SessionsHubProjection.Page(ordered, state.PageIndex, state.PageSize).Rows;
        if (state.GroupBy == SessionsGroupBy.None)
            return pageRows.Select(row => row.Key).ToArray();
        var groups = SessionsHubProjection.Group(ordered, state.GroupBy, context.Controller.KnownGroups());
        return SessionsHubSelection.ExpandedKeys(pageRows, groups, context.CollapsedGroups);
    }

    private static Element Roster(SessionsHubViewContext context, IReadOnlyDictionary<string, string> titles)
    {
        var state = context.State;
        var controller = context.Controller;
        if (!state.IsConnected)
            return SessionsHubView.EmptyState(FluentIconCatalog.Sessions, T("DisconnectedTitle"), T("DisconnectedBody"),
                automationId: "SessionsHubDisconnectedEmpty");
        if (state.Load == SessionsHubLoad.Unsupported)
            return SessionsHubView.EmptyState(FluentIconCatalog.StatusWarn, T("UnsupportedTitle"), T("Unsupported"));
        if (state.Load == SessionsHubLoad.Failed && state.Rows.Count == 0)
            return SessionsHubView.EmptyState(FluentIconCatalog.StatusErr, T("LoadFailedTitle"), state.LoadError ?? "",
                Button(T("Retry"), () => _ = controller.ReloadAsync()).AutomationName(T("Retry")).AutomationId("SessionsHubRetry"), "SessionsHubLoadFailed");
        if (state.Load is SessionsHubLoad.Loading or SessionsHubLoad.Idle && state.Rows.Count == 0)
            return LoadingRows();

        var ordered = Ordered(context, titles);

        var (pageRows, pageIndex, pageCount) = SessionsHubProjection.Page(ordered, state.PageIndex, state.PageSize);
        var pageKeys = pageRows.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        var groups = SessionsHubProjection.Group(
            SessionsHubProjection.Sort(state.Filters.ApplyClientFilters(state.Rows), state.SortColumn, state.SortDescending,
                row => titles.TryGetValue(row.Key, out var title) ? title : row.Key),
            state.GroupBy, controller.KnownGroups());

        var items = new List<RosterItem>();
        foreach (var group in groups)
        {
            var rows = group.Rows.Where(row => pageKeys.Contains(row.Key)).ToArray();
            // Empty custom groups are drop targets worth showing, but only while nothing narrows the list.
            var showEmptyCategory = state.GroupBy == SessionsGroupBy.Category && group.Rows.Count == 0 && pageIndex == 0
                && !state.Filters.IsNarrowed;
            if (rows.Length == 0 && !showEmptyCategory)
                continue;
            if (state.GroupBy != SessionsGroupBy.None)
                items.Add(new RosterItem("group:" + group.Id, group, null));
            if (context.CollapsedGroups.Contains(group.Id) && state.GroupBy != SessionsGroupBy.None)
                continue;
            if (showEmptyCategory)
                items.Add(new RosterItem("group-empty:" + group.Id, group, null, IsEmptyGroup: true));
            items.AddRange(rows.Select(row => new RosterItem("row:" + row.Key, null, row)));
        }

        if (items.Count == 0)
            return EmptyRoster(context);

        return Grid([GridSize.Star()], [GridSize.Star(), GridSize.Auto],
            Component<RosterList, RosterListProps>(new(context, items, titles)).Grid(row: 0),
            Footer(context, ordered.Count, pageRows.Count).Grid(row: 1));
    }

    private static Element LoadingRows()
    {
        var rows = Enumerable.Range(0, 5).Select(index =>
            SessionsHubView.Card(
                Grid([GridSize.Auto, GridSize.Star(), GridSize.Px(120)], [GridSize.Auto],
                    Border(Empty()).Size(36, 36).CornerRadius(18)
                        .Background(Theme.Ref("SubtleFillColorSecondaryBrush")).Grid(column: 0),
                    VStack(7,
                            Border(Empty()).Height(12).MaxWidth(320)
                                .Background(Theme.Ref("SubtleFillColorSecondaryBrush")).CornerRadius(4),
                            Border(Empty()).Height(9).MaxWidth(220)
                                .Background(Theme.Ref("SubtleFillColorTertiaryBrush")).CornerRadius(4))
                        .Margin(12, 0, 0, 0).Grid(column: 1),
                    Border(Empty()).Height(12)
                        .Background(Theme.Ref("SubtleFillColorSecondaryBrush")).CornerRadius(4)
                        .Grid(column: 2)),
                12, 10).Opacity(0.75).WithKey("skeleton:" + index)).ToArray();
        return VStack(8,
                TextBlock(T("Loading")).AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content)
                    .LiveRegion(),
                VStack(6, rows))
            .AutomationId("SessionsPageLoading");
    }

    private sealed record RosterListProps(
        SessionsHubViewContext Context, IReadOnlyList<RosterItem> Items, IReadOnlyDictionary<string, string> Titles);

    private sealed class RosterList : Component<RosterListProps>
    {
        public override Element Render()
        {
            var reference = this.UseElementRef<ItemsView>();
            var request = Props.Context.State.RowReveal;
            UseEffect((Func<Action>)(() =>
            {
                var view = reference.Current;
                if (view is null || request is null) return () => { };
                void FocusRestored(object? sender, object args)
                {
                    var controller = Props.Context.Controller;
                    if (!ReferenceEquals(controller.State.RowReveal, request))
                        return;
                    if (controller.State.Dialog is not null)
                    {
                        controller.CompleteRowReveal(request);
                        return;
                    }
                    var row = FindRow(view, request.Key);
                    if (row is not null && row.Focus(FocusState.Programmatic))
                        controller.CompleteRowReveal(request);
                }
                void Reveal(object sender, RoutedEventArgs args)
                {
                    view.Loaded -= Reveal;
                    var controller = Props.Context.Controller;
                    if (!ReferenceEquals(controller.State.RowReveal, request))
                        return;
                    var key = request.Key;
                    var index = Props.Items.ToList().FindIndex(item => item.Row?.Key == key);
                    if (index >= 0)
                    {
                        view.StartBringItemIntoView(index, new BringIntoViewOptions
                        {
                            AnimationDesired = false,
                            VerticalAlignmentRatio = 0,
                        });
                        if (!request.Focus)
                            controller.CompleteRowReveal(request);
                    }
                }
                if (view.IsLoaded)
                    view.DispatcherQueue.TryEnqueue(() => Reveal(view, new RoutedEventArgs()));
                else view.Loaded += Reveal;
                if (request.Focus)
                    view.LayoutUpdated += FocusRestored;
                return () =>
                {
                    view.Loaded -= Reveal;
                    view.LayoutUpdated -= FocusRestored;
                };
            }), request, Props.Items);
            return (ItemsView(Props.Items, static item => item.Key,
                    (item, _) => RosterContainer(Props.Context, item, Props.Titles)) with
                {
                    LayoutKind = ItemsViewLayoutKind.StackLayout,
                    SelectionMode = ItemsViewSelectionMode.None,
                    IsItemInvokedEnabled = false,
                })
                .Ref(reference).AutomationName(T("ListLabel")).AutomationId("SessionsHubList");
        }

        private static Expander? FindRow(DependencyObject parent, string key)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is Expander row && AutomationProperties.GetAutomationId(row) == "SessionsHubRow:" + key)
                    return row;
                if (FindRow(child, key) is { } found)
                    return found;
            }
            return null;
        }
    }

    private sealed record RosterItem(string Key, SessionRowGroup? Group, SessionInfo? Row, bool IsEmptyGroup = false);

    private static Element RosterContainer(
        SessionsHubViewContext context, RosterItem item, IReadOnlyDictionary<string, string> titles)
    {
        Element content;
        string name;
        if (item.Row is { } row)
        {
            var title = titles.TryGetValue(row.Key, out var known) ? known : row.Key;
            name = RowAccessibleName(context, row, title);
            content = Row(context, row, title, titles);
        }
        else if (item.IsEmptyGroup)
        {
            name = T("EmptyGroup");
            content = TextBlock(T("EmptyGroup")).FontSize(12).Foreground(Theme.TertiaryText).Margin(36, 0, 0, 8);
        }
        else
        {
            name = SessionsHubPresentation.GroupLabel(item.Group!, context.State.GroupBy, context.State.Agents);
            content = GroupHeader(context, item.Group!);
        }
        var bottomMargin = item.Row is null ? 4 : PageResponsiveLayout.GapInner;
        return ItemContainer(content.Margin(0, 0, 12, bottomMargin).HAlign(HorizontalAlignment.Stretch))
            .Resources(resources => resources
                .Set("ItemContainerPointerOverBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                .Set("ItemContainerPressedBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                .Set("ItemContainerSelectedBackground", Theme.Ref("SubtleFillColorTransparentBrush")))
            .IsTabStop(false)
            .Set(container =>
            {
                container.IsSelected = false;
                container.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            })
            .AutomationName(name)
            .HAlign(HorizontalAlignment.Stretch)
            .WithKey(item.Key);
    }

    private static string RowAccessibleName(SessionsHubViewContext context, SessionInfo row, string title)
    {
        var (statusLabel, _) = SessionsHubPresentation.Status(row);
        return string.Join(", ", new[]
        {
            title, statusLabel,
            SessionsHubPresentation.HasStoppedWithError(row) ? T("StoppedWithError") : null,
            row.Unread ? T("Unread") : null, row.Archived ? T("Archived") : null,
            SessionsHubPresentation.RelativeTime(row.UpdatedAt, context.NowUtc),
        }.Where(part => !string.IsNullOrEmpty(part)));
    }

    private static Element EmptyRoster(SessionsHubViewContext context)
    {
        var state = context.State;
        var filters = state.Filters;
        // Client-only filters can hide every row of a loaded page; later pages may still match.
        Element? loadMore = state.HasMore
            ? Button(T("LoadMore"), () => _ = context.Controller.LoadMoreAsync()).AutomationName(T("LoadMore"))
                .IsEnabled(!state.IsBusy && !state.IsReloading).AutomationId("SessionsHubLoadMore")
            : null;
        // Ahead of IsNarrowed: a search that matches only completed sessions is not fixed by clearing it.
        var hiddenCompleted = filters.CountHiddenCompleted(state.Rows);
        if (hiddenCompleted > 0)
        {
            var showCompletedLabel = LocalizationHelper.GetString("SessionsPage_ShowCompleted.Header");
            var showCompleted = Button(showCompletedLabel, () => context.Controller.SetFilters(filters with { ShowCompleted = true }))
                .AutomationName(showCompletedLabel).AutomationId("SessionsHubShowCompleted");
            return SessionsHubView.EmptyState(FluentIconCatalog.StatusOk, T("EmptyCompletedTitle"),
                hiddenCompleted == 1 ? T("EmptyCompletedOne") : F("EmptyCompletedMany", hiddenCompleted),
                loadMore is null ? showCompleted : HStack(8, showCompleted, loadMore).HAlign(HorizontalAlignment.Center), "SessionsPageEmpty");
        }
        if (filters.IsNarrowed)
        {
            var showAll = Button(T("ShowAll"), context.Controller.ClearFilters).AutomationName(T("ShowAll")).AutomationId("SessionsHubShowAll");
            return SessionsHubView.EmptyState(FluentIconCatalog.Clear, T("EmptyFilteredTitle"), T("EmptyFilteredBody"),
                loadMore is null ? showAll : HStack(8, showAll, loadMore).HAlign(HorizontalAlignment.Center), "SessionsPageEmpty");
        }
        return filters.Status switch
        {
            SessionArchiveFilter.Archived => SessionsHubView.EmptyState(FluentIconCatalog.Archive, T("EmptyArchivedTitle"), T("EmptyArchivedBody"),
                loadMore, "SessionsPageEmpty"),
            SessionArchiveFilter.All => SessionsHubView.EmptyState(FluentIconCatalog.Sessions, T("EmptyAllTitle"), T("EmptyAllBody"),
                loadMore, "SessionsPageEmpty"),
            _ => SessionsHubView.EmptyState(FluentIconCatalog.Sessions, T("EmptyActiveTitle"), T("EmptyActiveBody"),
                loadMore, "SessionsPageEmpty"),
        };
    }

    private static Element GroupHeader(SessionsHubViewContext context, SessionRowGroup group)
    {
        var state = context.State;
        var label = SessionsHubPresentation.GroupLabel(group, state.GroupBy, state.Agents);
        var collapsed = context.CollapsedGroups.Contains(group.Id);
        var count = group.Rows.Count == 1 ? T("CountOne") : F("CountMany", group.Rows.Count);
        var colorId = GroupColor(state.GroupBy, group);
        void Toggle()
        {
            var next = new HashSet<string>(context.CollapsedGroups, StringComparer.Ordinal);
            if (!next.Remove(group.Id)) next.Add(group.Id);
            context.SetCollapsedGroups(next);
        }
        var header = Button(
                HStack(10,
                    SessionsHubView.Glyph(collapsed ? FluentIconCatalog.ChevronR : FluentIconCatalog.ChevronDown, 12, Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center),
                    GroupLeading(state, group, colorId),
                    TextBlock(label).FontSize(15).FontWeight(FontWeights.SemiBold).VAlign(VerticalAlignment.Center),
                    colorId is null
                        ? SessionsHubView.NeutralBadge(count)
                        : SessionsHubView.Pill(count, colorId))
                    .Padding(0, 2),
                Toggle)
            .SubtleButton()
            .HAlign(HorizontalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Left)
            .AutomationName($"{label}, {count}")
            .HeadingLevel(Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2)
            .AutomationId("SessionsHubGroup:" + (group.IsUngrouped ? "(none)" : group.Id));
        var canManage = state.GroupBy == SessionsGroupBy.Category && !group.IsUngrouped;
        return Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                header.Grid(column: 0),
                canManage
                    ? SessionsHubView.IconButton(FluentIconCatalog.MoreOverflow, F("GroupOptions", label), () => { },
                            automationId: "SessionsHubGroupMenu:" + group.Id)
                        .WithFlyout(MenuItems(SessionsHubMenus.GroupHeaderMenu(context, group.Id)))
                        .Grid(column: 1)
                    : Empty())
            .Margin(0, 12, 0, 4);
    }

    /// <summary>The hue a group header wears; null keeps it neutral (dates, people, ungrouped).</summary>
    private static string? GroupColor(SessionsGroupBy mode, SessionRowGroup group) => group.IsUngrouped ? null : mode switch
    {
        SessionsGroupBy.Category or SessionsGroupBy.Agent or SessionsGroupBy.Channel => SessionAppearance.PaletteColor(group.Id),
        SessionsGroupBy.Kind => SessionsHubPresentation.KindColor(group.Id),
        _ => null,
    };

    private static Element GroupLeading(SessionsHubState state, SessionRowGroup group, string? colorId) => state.GroupBy switch
    {
        SessionsGroupBy.Agent when !group.IsUngrouped =>
            SessionsHubView.AgentAvatar(state.Agents.FirstOrDefault(agent => agent.Id == group.Id), group.Id, 24),
        SessionsGroupBy.Channel => SessionsHubView.IconTile(SessionsHubPresentation.ChannelGlyph(group.Id), colorId ?? "blue", 24),
        SessionsGroupBy.Person => SessionsHubView.IconTile(FluentIconCatalog.Operator, "purple", 24),
        SessionsGroupBy.Date => SessionsHubView.IconTile(FluentIconCatalog.Cron, "cyan", 24),
        _ when colorId is not null => SessionsHubView.ColorDot(colorId, 12),
        _ => SessionsHubView.Glyph(FluentIconCatalog.Folder, 14, Theme.SecondaryText).VAlign(VerticalAlignment.Center),
    };

    private static Element Row(
        SessionsHubViewContext context, SessionInfo row, string title, IReadOnlyDictionary<string, string> titles)
    {
        var state = context.State;
        var controller = context.Controller;
        var (statusLabel, tone) = SessionsHubPresentation.Status(row);
        var selected = state.ExpandedKeys.Contains(row.Key);
        var display = SessionDisplayResolver.Resolve(row);
        var agent = display.AgentId is { } agentId ? state.Agents.FirstOrDefault(entry => entry.Id == agentId) : null;
        var colorId = SessionAppearance.NormalizeColor(row.Color);
        var iconId = SessionAppearance.NormalizeIcon(row.Icon);
        var kind = SessionsHubProjection.DisplayKind(row);

        var titleLine = new List<Element>();
        if (iconId is not null)
            titleLine.Add(SessionsHubView.Glyph(SessionsHubPresentation.IconGlyph(iconId), 14,
                colorId is null ? Theme.SecondaryText : SessionsHubView.Strong(colorId)).VAlign(VerticalAlignment.Center));
        titleLine.Add(TextBlock(title).FontSize(14).FontWeight(row.Unread ? FontWeights.SemiBold : FontWeights.Normal)
            .TextTrimming(TextTrimming.CharacterEllipsis).VAlign(VerticalAlignment.Center));
        Element? stoppedBadge = null;
        if (SessionsHubPresentation.HasStoppedWithError(row))
        {
            var error = string.IsNullOrWhiteSpace(row.LastRunError) ? T("NeedsAttentionFallback") : row.LastRunError!;
            stoppedBadge = PageHero.AttentionBadge("SessionsHubStoppedWithError:" + row.Key, T("StoppedWithError"),
                    error, FluentIconCatalog.StatusErr, PageHeroTone.Critical, context.ReducedMotion)
                .VAlign(VerticalAlignment.Center);
            // Below Wide, the title shares its line with status and actions, so the badge gets its own line.
            if (context.IsWide)
            {
                titleLine.Add(stoppedBadge);
                stoppedBadge = null;
            }
        }
        if (row.Pinned)
            titleLine.Add(SessionsHubView.Glyph(FluentIconCatalog.Pin, 12, SessionsHubView.Strong("yellow")).VAlign(VerticalAlignment.Center));
        if (row.Unread)
            titleLine.Add(SessionsHubView.Badge(T("Unread"), Theme.Accent, Theme.Ref("TextOnAccentFillColorPrimaryBrush"), "SessionsHubUnread:" + row.Key));
        if (row.Category is { } category && state.GroupBy != SessionsGroupBy.Category)
            titleLine.Add(SessionsHubView.Pill(category, SessionAppearance.PaletteColor(category), FluentIconCatalog.Folder));
        if (SessionsHubProjection.IsCloudWorker(row))
            titleLine.Add(SessionsHubView.Pill(SessionsHubPresentation.Placement(row.PlacementState), "purple", CloudGlyph));
        if (row.Archived && state.Filters.Status == SessionArchiveFilter.All)
            titleLine.Add(SessionsHubView.NeutralBadge(T("Archived")));

        var meta = new List<Element>();
        void AddMeta(string? glyph, string text, ThemeRef? brush = null)
        {
            if (meta.Count > 0)
                meta.Add(TextBlock("·").FontSize(12).Foreground(Theme.TertiaryText).VAlign(VerticalAlignment.Center));
            var foreground = brush ?? Theme.SecondaryText;
            var parts = new List<Element>();
            if (glyph is not null)
                parts.Add(SessionsHubView.Glyph(glyph, 11, foreground).VAlign(VerticalAlignment.Center));
            parts.Add(TextBlock(text).FontSize(12).Foreground(foreground).TextTrimming(TextTrimming.CharacterEllipsis)
                .VAlign(VerticalAlignment.Center));
            meta.Add(HStack(4, parts.ToArray()));
        }
        // Direct conversations are the norm; only call out the other kinds.
        if (kind is not ("direct" or "main") && SessionsHubPresentation.KindColor(kind) is { } kindColor)
            AddMeta(null, SessionsHubPresentation.KindLabel(kind), SessionsHubView.Strong(kindColor));
        if (state.Filters.AgentId is null && display.AgentId is { } shownAgent)
            AddMeta(null, agent?.Name ?? shownAgent);
        if (display.Channel is { } channel)
            AddMeta(SessionsHubPresentation.ChannelGlyph(channel), SessionsHubPresentation.ChannelLabel(channel));
        if (row.Owner is { } owner)
            AddMeta(FluentIconCatalog.Operator, F("OwnedBy", SessionsHubPresentation.OwnerLabel(owner, state.Agents)));
        if (!string.IsNullOrWhiteSpace(row.Model))
            AddMeta(null, row.Model!);
        if (meta.Count == 0)
            AddMeta(null, statusLabel);

        var primary = Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                    Leading(row, agent, display.AgentId ?? row.Key, tone).Grid(column: 0),
                    VStack(3,
                            HStack(8, titleLine.ToArray()),
                            stoppedBadge is null ? Empty() : stoppedBadge.HAlign(HorizontalAlignment.Left).Margin(0, 2, 0, 2),
                            HStack(6, meta.ToArray()))
                        .VAlign(VerticalAlignment.Center).Margin(12, 0, 0, 0).Grid(column: 1))
            .HAlign(HorizontalAlignment.Stretch)
            .Padding(10, 8);

        var menu = MenuItems(SessionsHubMenus.SessionMenu(context, row, title));
        var actions = new List<Element>
        {
            HStack(5,
                SessionsHubView.Glyph(tone == SessionsHubPresentation.StatusTone.Working ? "\uE915"
                    : tone == SessionsHubPresentation.StatusTone.Attention ? FluentIconCatalog.StatusWarn
                    : tone == SessionsHubPresentation.StatusTone.Ready ? FluentIconCatalog.StatusOk : FluentIconCatalog.About,
                    11, tone == SessionsHubPresentation.StatusTone.Working ? SessionsHubView.Strong("green")
                        : tone == SessionsHubPresentation.StatusTone.Attention ? SessionsHubView.Strong("red") : Theme.SecondaryText),
                TextBlock(statusLabel).FontSize(12).Foreground(Theme.SecondaryText))
                .VAlign(VerticalAlignment.Center),
            TextBlock(SessionsHubPresentation.RelativeTime(row.UpdatedAt, context.NowUtc))
                .FontSize(12).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center).Margin(8, 0)
                .ToolTip(SessionsHubPresentation.AbsoluteTime(row.UpdatedAt)),
        };
        if (row.Archived)
            actions.Add(SessionsHubView.WithMutationHelp(
                SessionsHubView.IconButton(FluentIconCatalog.Unarchive, F("UnarchiveNamed", title),
                    () => _ = controller.UnarchiveAsync(row, title), state.Permissions.CanWrite && state.CanMutateRows,
                    "SessionsPageUnarchive:" + row.Key),
                state, F("UnarchiveNamed", title)));
        if (!state.SelectionMode)
            actions.Add(SessionsHubView.IconButton(FluentIconCatalog.Chat, F("OpenInChatNamed", title),
                () => controller.OpenInChat(row.Key), row.Kind != "global", "SessionsHubOpen:" + row.Key));

        var columns = new List<GridSize> { GridSize.Px(4) };
        var cells = new List<Element>
        {
            // Session color stripe; an uncolored row keeps the column so titles stay aligned.
            Empty().Grid(column: 0),
        };
        if (state.SelectionMode)
        {
            columns.Add(GridSize.Auto);
            cells.Add(CheckBox(state.Selection.Contains(row.Key), _ => controller.ToggleSelected(row.Key))
                .AutomationName(F("SelectNamed", title)).MinWidth(0).Margin(8, 0, 0, 0)
                .AutomationId("SessionsHubSelect:" + row.Key).Grid(column: columns.Count - 1));
        }
        columns.Add(GridSize.Star());
        cells.Add(primary.Grid(column: columns.Count - 1));
        columns.Add(GridSize.Auto);
        cells.Add(HStack(2, actions.ToArray()).VAlign(VerticalAlignment.Center).Margin(0, 0, 8, 0).Grid(column: columns.Count - 1));

        return (Expander("", selected ? SessionsHubDetailsView.Render(context, row, titles) : Empty(),
                selected, expanded =>
                {
                    if (expanded && !controller.State.ExpandedKeys.Contains(row.Key))
                        controller.OpenDetails(row.Key);
                    else if (!expanded && controller.State.ExpandedKeys.Contains(row.Key))
                        controller.CloseDetails(row.Key);
                }) with
                {
                    // Archived rows in All are dimmed to tell them apart, but only in the header:
                    // opacity on the Expander would also fade the editable body.
                    HeaderTemplate = Grid(columns.ToArray(), [GridSize.Auto], cells.ToArray())
                        .Opacity(row.Archived && state.Filters.Status == SessionArchiveFilter.All ? 0.75 : 1),
                })
            .HAlign(HorizontalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            // Native settings-expander surfaces: the body sits one layer below the header card.
            .Background(Theme.Ref("CardBackgroundFillColorSecondaryBrush"))
            .WithBorder(Theme.CardStroke, 1)
            .CornerRadius(8)
            .WithContextFlyout(menu)
            .AutomationName(RowAccessibleName(context, row, title))
            .HelpText(T("RowHelp"))
            .AutomationId("SessionsHubRow:" + row.Key);
    }

    private const string CloudGlyph = "\uE753";

    /// <summary>Agent avatar with a small presence badge for working or attention sessions.</summary>
    private static Element Leading(SessionInfo row, SessionsHubAgent? agent, string fallbackName, SessionsHubPresentation.StatusTone tone)
    {
        var avatar = SessionsHubView.AgentAvatar(agent, fallbackName, 36);
        var badgeColor = tone switch
        {
            SessionsHubPresentation.StatusTone.Working => "green",
            SessionsHubPresentation.StatusTone.Attention => "red",
            _ => null,
        };
        if (badgeColor is null)
            return avatar;
        return Grid([GridSize.Auto], [GridSize.Auto],
            avatar,
            Border(Empty())
                .Size(12, 12)
                .CornerRadius(6)
                .Background(SessionsHubView.Strong(badgeColor))
                .WithBorder(Theme.Ref("CardBackgroundFillColorDefaultBrush"), 2)
                .HAlign(HorizontalAlignment.Right)
                .VAlign(VerticalAlignment.Bottom)
                .Margin(0, 0, -2, -2)
                .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw));
    }
    private static Element Footer(SessionsHubViewContext context, int total, int shown)
    {
        var state = context.State;
        var controller = context.Controller;
        var info = F("FooterLoaded", shown, total, state.Rows.Count) + (state.HasMore ? " · " + T("MoreAvailableShort") : "");
        return Border(Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                TextBlock(info).FontSize(12).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center)
                    .AutomationId("SessionsHubPageInfo").Grid(column: 0),
                HStack(8,
                        total > shown
                            ? Button(T("ShowMore"), () => controller.SetPageSize(state.PageSize + 25))
                                .AutomationName(T("ShowMore")).AutomationId("SessionsHubShowMore")
                            : Empty(),
                        state.HasMore
                            ? Button(T("LoadMore"), () => _ = controller.LoadMoreAsync()).AutomationName(T("LoadMore")).IsEnabled(!state.IsBusy && !state.IsReloading).AutomationId("SessionsHubLoadMore")
                            : Empty())
                    .Grid(column: 1)))
            .Padding(0, 8, 12, 0);
    }
}
