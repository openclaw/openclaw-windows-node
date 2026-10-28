using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;
using MenuFlyoutItemBase = Microsoft.UI.Reactor.Core.MenuFlyoutItemBase;

namespace OpenClawTray.Sessions;

/// <summary>Sessions tab: filter toolbar, grouped roster, selection, pager, details, and transcript search.</summary>
internal static class SessionsHubSessionsView
{
    private const string ChevronLeftGlyph = "\uE76B"; // ChevronLeft
    private static string T(string key) => SessionsHubPresentation.T(key);
    private static string F(string key, params object?[] args) => SessionsHubPresentation.F(key, args);

    private static readonly SessionsGroupBy[] GroupModes =
        [SessionsGroupBy.None, SessionsGroupBy.Category, SessionsGroupBy.Person, SessionsGroupBy.Channel,
         SessionsGroupBy.Kind, SessionsGroupBy.Agent, SessionsGroupBy.Date];

    public static Element Render(SessionsHubViewContext context)
    {
        var state = context.State;
        var titles = SessionsHubPresentation.Titles(state.Rows);
        var details = state.DetailsRow;
        var main = state.Transcript.IsOpen
            ? SessionsHubSearchView.Render(context, titles)
            : Roster(context, titles);

        Element content;
        if (details is null)
            content = main;
        else if (context.IsWide)
            content = Grid([GridSize.Star(), GridSize.Px(380)], [GridSize.Star()],
                main.Grid(column: 0),
                SessionsHubDetailsView.Render(context, details, titles).Margin(16, 0, 0, 0).Grid(column: 1));
        else
            content = SessionsHubDetailsView.Render(context, details, titles);

        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Star()],
            Toolbar(context).Grid(row: 0),
            SelectionBar(context).Grid(row: 1),
            content.Grid(row: 2));
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
            .AutomationId("SessionsHubAgentFilter");

        var search = TextBox(filters.Search, controller.SetSearch, T("SearchPlaceholder"))
            .IsEnabled(enabled && !state.Transcript.IsOpen)
            .AutomationName(T("SearchPlaceholder"))
            .MinWidth(220)
            .AutomationId("SessionsHubSearch");

        var status = SelectorBar(
                [SelectorBarItem(T("StatusActive")), SelectorBarItem(T("StatusArchived")), SelectorBarItem(T("StatusAll"))],
                (int)filters.Status,
                index =>
                {
                    if (index is >= 0 and <= 2)
                        controller.SetFilters(filters with { Status = (SessionArchiveFilter)index });
                })
            .Set(bar => SessionsHubView.TagItems(bar, "SessionsPageStatus", ["Active", "Archived", "All"]))
            .IsEnabled(enabled)
            .AutomationName(T("StatusFilter"))
            .AutomationId("SessionsHubStatus");

        var groupBy = ComboBox(GroupModes.Select(SessionsHubPresentation.GroupByLabel).ToArray(),
                Array.IndexOf(GroupModes, state.GroupBy),
                index =>
                {
                    if (index >= 0 && index < GroupModes.Length)
                        controller.SetGroupBy(GroupModes[index]);
                })
            .AutomationName(T("GroupBy"))
            .ToolTip(T("GroupBy"))
            .MinWidth(150)
            .AutomationId("SessionsHubGroupBy");

        var sort = DropDownButton(SortLabel(state), MenuItems(
                SortItem(context, SessionsSortColumn.Updated, true, T("SortNewest")),
                SortItem(context, SessionsSortColumn.Updated, false, T("SortOldest")),
                SortItem(context, SessionsSortColumn.Name, false, T("SortNameAsc")),
                SortItem(context, SessionsSortColumn.Name, true, T("SortNameDesc"))))
            .AutomationName(T("Sort"))
            .ToolTip(T("Sort"))
            .AutomationId("SessionsHubSort");

        var filterCount = FilterCount(filters);
        var filtersButton = DropDownButton(filterCount > 0 ? F("FiltersCount", filterCount) : T("Filters"),
                ContentFlyout(FiltersPanel(context)))
            .IsEnabled(enabled)
            .AutomationName(T("Filters"))
            .AutomationId("SessionsHubFilters");

        var transcript = ToggleButton(T("SearchTranscripts"), state.Transcript.IsOpen, controller.SetTranscriptOpen)
            .IsEnabled(enabled && state.Permissions.Supports("sessions.search"))
            .ToolTip(state.Permissions.Supports("sessions.search") ? T("SearchTranscriptsTip") : T("Unsupported"))
            .AutomationId("SessionsHubTranscriptToggle");

        var select = ToggleButton(T("Select"), state.SelectionMode, controller.SetSelectionMode)
            .IsEnabled(enabled && !state.Transcript.IsOpen)
            .ToolTip(T("SelectTip"))
            .AutomationId("SessionsHubSelectToggle");

        var trailing = new List<Element> { transcript, select };
        if (state.GroupBy == SessionsGroupBy.Category)
            trailing.Add(SessionsHubView.CommandButton(FluentIconCatalog.Add, T("NewGroup"), () => _ = controller.NewGroupAsync(),
                enabled && state.Permissions.CanCreateGroup && !state.IsBusy, "SessionsHubNewGroup",
                tooltip: !state.Permissions.SupportsGroupCreation ? T("Unsupported")
                    : state.Permissions.CanCreateGroup ? T("NewGroup") : T("NeedsWrite")));
        if (filters.Status == SessionArchiveFilter.Archived)
            trailing.Add(SessionsHubView.CommandButton(FluentIconCatalog.Delete, T("DeleteAllArchived"),
                () => _ = controller.DeleteAllArchivedAsync(),
                enabled && state.Permissions.CanWrite && state.Rows.Any(row => row.Archived) && !state.IsBusy,
                "SessionsHubDeleteAllArchived",
                tooltip: state.Permissions.CanWrite ? T("DeleteAllArchivedTip") : T("NeedsWrite")));

        return VStack(8,
                (FlexRow(search.Flex(grow: 1, basis: 220), agent, status) with
                    { Wrap = FlexWrap.Wrap, ColumnGap = 8, RowGap = 8, AlignItems = FlexAlign.Center }),
                (FlexRow([groupBy, sort, filtersButton, Border(Empty()).Flex(grow: 1, basis: 0), .. trailing]) with
                    { Wrap = FlexWrap.Wrap, ColumnGap = 8, RowGap = 8, AlignItems = FlexAlign.Center }))
            .Margin(0, 0, 0, 8);
    }

    private static MenuFlyoutItemBase SortItem(SessionsHubViewContext context, SessionsSortColumn column, bool descending, string label) =>
        new RadioMenuFlyoutItemData(label, "sessions-sort",
            context.State.SortColumn == column && context.State.SortDescending == descending,
            () => context.Controller.SetSort(column, descending));

    private static string SortLabel(SessionsHubState state) => (state.SortColumn, state.SortDescending) switch
    {
        (SessionsSortColumn.Updated, true) => T("SortNewest"),
        (SessionsSortColumn.Updated, false) => T("SortOldest"),
        (SessionsSortColumn.Name, false) => T("SortNameAsc"),
        _ => T("SortNameDesc"),
    };

    private static int FilterCount(SessionsHubFilters filters) =>
        (SessionListQuery.ParsePositiveInteger(filters.ActiveMinutes) is not null && filters.Status == SessionArchiveFilter.Active ? 1 : 0)
        + (!filters.IncludeGlobal ? 1 : 0) + (filters.IncludeUnknown ? 1 : 0)
        + (filters.ShowBackground ? 1 : 0)
        + (SessionListQuery.ParsePositiveInteger(filters.Limit) is { } limit && limit != SessionListQuery.DefaultLimit ? 1 : 0);

    private static Element FiltersPanel(SessionsHubViewContext context)
    {
        var controller = context.Controller;
        var filters = context.State.Filters;
        var activeOnly = filters.Status == SessionArchiveFilter.Active;
        return VStack(12,
                TextBlock(T("Filters")).FontWeight(FontWeights.SemiBold),
                TextBox(filters.ActiveMinutes, text => controller.SetFilters(filters with { ActiveMinutes = text }), T("ActiveMinutesPlaceholder"), T("ActiveMinutes"))
                    .NumericInput()
                    .AutomationName(T("ActiveMinutes"))
                    .Description(activeOnly ? T("ActiveMinutesHelp") : T("ActiveMinutesDisabled"))
                    .IsEnabled(activeOnly)
                    .AutomationId("SessionsHubActiveMinutes"),
                TextBox(filters.Limit, text => controller.SetFilters(filters with { Limit = text }), SessionListQuery.DefaultLimit.ToString(), T("Limit"))
                    .NumericInput()
                    .AutomationName(T("Limit"))
                    .Description(T("LimitHelp"))
                    .AutomationId("SessionsHubLimit"),
                ToggleSwitch(filters.IncludeGlobal, value => controller.SetFilters(filters with { IncludeGlobal = value }), header: T("IncludeGlobal"))
                    .AutomationId("SessionsHubIncludeGlobal"),
                ToggleSwitch(filters.IncludeUnknown, value => controller.SetFilters(filters with { IncludeUnknown = value }), header: T("IncludeUnknown"))
                    .AutomationId("SessionsHubIncludeUnknown"),
                ToggleSwitch(filters.ShowCompleted, value => controller.SetFilters(filters with { ShowCompleted = value }),
                        header: LocalizationHelper.GetString("SessionsPage_ShowCompleted.Header"))
                    .IsEnabled(activeOnly)
                    .AutomationId("SessionsPageShowCompleted"),
                ToggleSwitch(filters.ShowBackground, value => controller.SetFilters(filters with { ShowBackground = value }),
                        header: LocalizationHelper.GetString("SessionsPage_ShowBackground.Header"))
                    .AutomationId("SessionsPageShowBackground"),
                Button(T("ResetFilters"), controller.ClearFilters).AutomationName(T("ResetFilters")).AutomationId("SessionsHubResetFilters"))
            .Width(280)
            .AutomationId("SessionsHubFiltersPanel");
    }

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
        return SessionsHubView.Card(Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto, GridSize.Auto], [GridSize.Auto],
                    CheckBox(pageState, selected => controller.SetSelected(pageKeys, selected), T("SelectPage"))
                        .AutomationName(T("SelectPage"))
                        .AutomationId("SessionsHubSelectPage").Grid(column: 0),
                    TextBlock(state.Selection.Count == 1 ? T("SelectedOne") : F("SelectedMany", state.Selection.Count))
                        .VAlign(VerticalAlignment.Center).Margin(12, 0).LiveRegion().AutomationId("SessionsHubSelectedCount")
                        .Grid(column: 1),
                    SessionsHubView.CommandButton(FluentIconCatalog.Delete, T("DeleteSelected"), () => _ = controller.DeleteSelectedAsync(),
                            deletable > 0 && !state.IsBusy && !state.IsReloading, "SessionsHubDeleteSelected",
                            tooltip: deletable > 0 ? T("DeleteSelected") : T("DeleteSelectedNoneAllowed"))
                        .Margin(0, 0, 8, 0).Grid(column: 2),
                    Button(T("ClearSelection"), () => controller.SetSelected(state.Selection.Keys.ToArray(), false)).AutomationName(T("ClearSelection"))
                        .IsEnabled(state.Selection.Count > 0).AutomationId("SessionsHubClearSelection").Grid(column: 3))
                , 12, 8)
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
            return HStack(12, ProgressRing().IsActive(true).Size(20, 20), TextBlock(T("Loading")).VAlign(VerticalAlignment.Center))
                .HAlign(HorizontalAlignment.Center).Margin(0, 48).AutomationId("SessionsPageLoading");

        var ordered = Ordered(context, titles);
        if (ordered.Count == 0)
            return EmptyRoster(context);

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

        var list = ItemsView(items, static item => item.Key, (item, _) => RosterContainer(context, item, titles)) with
        {
            LayoutKind = ItemsViewLayoutKind.StackLayout,
            SelectionMode = ItemsViewSelectionMode.None,
            IsItemInvokedEnabled = false,
        };
        return Grid([GridSize.Star()], [GridSize.Star(), GridSize.Auto],
            list.AutomationName(T("ListLabel")).AutomationId("SessionsHubList").Grid(row: 0),
            Pager(context, ordered.Count, pageIndex, pageCount).Grid(row: 1));
    }

    private sealed record RosterItem(string Key, SessionRowGroup? Group, SessionInfo? Row, bool IsEmptyGroup = false);

    private static Element RosterContainer(SessionsHubViewContext context, RosterItem item, IReadOnlyDictionary<string, string> titles)
    {
        Element content;
        string name;
        if (item.Row is { } row)
        {
            var title = titles.TryGetValue(row.Key, out var known) ? known : row.Key;
            name = RowAccessibleName(context, row, title);
            content = Row(context, row, title);
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
        return ItemContainer(content.Margin(0, 0, 12, 4).HAlign(HorizontalAlignment.Stretch))
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
            title, statusLabel, row.Unread ? T("Unread") : null, row.Archived ? T("Archived") : null,
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

    private static Element Row(SessionsHubViewContext context, SessionInfo row, string title)
    {
        var state = context.State;
        var controller = context.Controller;
        var (statusLabel, tone) = SessionsHubPresentation.Status(row);
        var selected = state.DetailsKey == row.Key;
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
        if (row.Pinned)
            titleLine.Add(SessionsHubView.Glyph(FluentIconCatalog.Pin, 12, SessionsHubView.Strong("yellow")).VAlign(VerticalAlignment.Center));
        if (row.Unread)
            titleLine.Add(SessionsHubView.Badge(T("Unread"), Theme.Accent, Theme.Ref("TextOnAccentFillColorPrimaryBrush"), "SessionsHubUnread:" + row.Key));
        if (tone == SessionsHubPresentation.StatusTone.Working)
            titleLine.Add(SessionsHubView.Pill(statusLabel, "green", FluentIconCatalog.Sync));
        else if (tone == SessionsHubPresentation.StatusTone.Attention)
            titleLine.Add(SessionsHubView.Pill(statusLabel, "red", FluentIconCatalog.StatusWarn));
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

        var primary = Button(
                Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                    Leading(row, agent, display.AgentId ?? row.Key, tone).Grid(column: 0),
                    VStack(3,
                            HStack(8, titleLine.ToArray()),
                            HStack(6, meta.ToArray()))
                        .VAlign(VerticalAlignment.Center).Margin(12, 0, 0, 0).Grid(column: 1)),
                () => controller.ShowDetails(row.Key))
            .SubtleButton()
            .HAlign(HorizontalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Left)
            .Padding(10, 8)
            .AutomationName(F("ShowDetailsNamed", title))
            .HelpText(T("RowHelp"))
            .AutomationId("SessionsHubRow:" + row.Key);

        var menu = MenuItems(SessionsHubMenus.SessionMenu(context, row, title));
        var actions = new List<Element>
        {
            TextBlock(SessionsHubPresentation.RelativeTime(row.UpdatedAt, context.NowUtc))
                .FontSize(12).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center).Margin(8, 0)
                .ToolTip(SessionsHubPresentation.AbsoluteTime(row.UpdatedAt)),
        };
        if (row.Archived)
            actions.Add(SessionsHubView.IconButton(FluentIconCatalog.Unarchive, F("UnarchiveNamed", title),
                () => _ = controller.UnarchiveAsync(row, title), state.Permissions.CanWrite && !state.IsBusy,
                "SessionsPageUnarchive:" + row.Key));
        actions.Add(SessionsHubView.IconButton(FluentIconCatalog.Chat, F("OpenInChatNamed", title),
            () => controller.OpenInChat(row.Key), row.Kind != "global", "SessionsHubOpen:" + row.Key));
        actions.Add(SessionsHubView.IconButton(FluentIconCatalog.MoreOverflow, F("MoreNamed", title), () => { },
                automationId: "SessionsHubMore:" + row.Key)
            .WithFlyout(menu));

        var columns = new List<GridSize> { GridSize.Px(4) };
        var cells = new List<Element>
        {
            // Session color stripe; an uncolored row keeps the column so titles stay aligned.
            (colorId is null
                ? Empty()
                : Border(Empty()).Width(4).CornerRadius(2).Background(SessionsHubView.Strong(colorId))
                    .VAlign(VerticalAlignment.Stretch).Margin(0, 10, 0, 10)
                    .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw))
                .Grid(column: 0),
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

        return Border(Grid(columns.ToArray(), [GridSize.Auto], cells.ToArray()))
            .Background(selected ? Theme.Ref("SessionsHubSelectedFillBrush") : Theme.CardBackground)
            .WithBorder(selected ? Theme.Accent : Theme.CardStroke, 1)
            .CornerRadius(8)
            .Opacity(row.Archived && state.Filters.Status == SessionArchiveFilter.All ? 0.75 : 1)
            .WithContextFlyout(menu);
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
                .WithBorder(Theme.CardBackground, 2)
                .HAlign(HorizontalAlignment.Right)
                .VAlign(VerticalAlignment.Bottom)
                .Margin(0, 0, -2, -2)
                .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw));
    }
    private static Element Pager(SessionsHubViewContext context, int total, int pageIndex, int pageCount)
    {
        var state = context.State;
        var controller = context.Controller;
        var start = total == 0 ? 0 : pageIndex * state.PageSize + 1;
        var end = Math.Min(total, (pageIndex + 1) * state.PageSize);
        var sizes = SessionsHubProjection.PageSizes;
        var info = state.TotalCount is { } server && server > state.Rows.Count
            ? F("PageRangeOfServer", start, end, total, server)
            : F("PageRange", start, end, total);
        return Border(Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                TextBlock(info).FontSize(12).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center)
                    .AutomationId("SessionsHubPageInfo").Grid(column: 0),
                HStack(8,
                        ComboBox(sizes.Select(size => F("RowsPerPage", size)).ToArray(),
                                Math.Max(0, sizes.ToList().IndexOf(state.PageSize)),
                                index =>
                                {
                                    if (index >= 0 && index < sizes.Count)
                                        controller.SetPageSize(sizes[index]);
                                })
                            .AutomationName(T("PageSize")).AutomationId("SessionsHubPageSize"),
                        state.HasMore
                            ? Button(T("LoadMore"), () => _ = controller.LoadMoreAsync()).AutomationName(T("LoadMore")).IsEnabled(!state.IsBusy && !state.IsReloading).AutomationId("SessionsHubLoadMore")
                            : Empty(),
                        SessionsHubView.IconButton(ChevronLeftGlyph, T("PreviousPage"), () => controller.SetPage(pageIndex - 1),
                            pageIndex > 0, "SessionsHubPrevPage"),
                        TextBlock(F("PageOf", pageIndex + 1, pageCount)).FontSize(12).VAlign(VerticalAlignment.Center),
                        SessionsHubView.IconButton(FluentIconCatalog.ChevronR, T("NextPage"), () => controller.SetPage(pageIndex + 1),
                            pageIndex < pageCount - 1, "SessionsHubNextPage"))
                    .Grid(column: 1)))
            .Padding(0, 8, 12, 0);
    }
}
