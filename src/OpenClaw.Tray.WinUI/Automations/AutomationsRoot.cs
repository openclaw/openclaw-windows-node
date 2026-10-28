using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using OpenClaw.Shared.Cron;
using static Microsoft.UI.Reactor.Factories;
using static OpenClawTray.Automations.AutomationsVisuals;

namespace OpenClawTray.Automations;

internal sealed record AutomationsRootProps(AutomationsHost Host);

internal enum EditorMode { Create, Edit, Clone }

/// <summary>Page-owned editor state survives child remounts during reconnect or layout changes.</summary>
internal sealed record EditorSession(EditorMode Mode, CronJobForm Form, CronJob? Source, long Nonce)
{
    public bool Submitted { get; init; }
    public bool Saving { get; init; }
    public IReadOnlyDictionary<CronFormField, CronFormError> Errors { get; init; } =
        ImmutableDictionary<CronFormField, CronFormError>.Empty;
    public (string Title, string Message, InfoBarSeverity Severity)? Banner { get; init; }
}

/// <summary>
/// Native Automations page: status strip, agent-scoped list and a detail/editor
/// pane. Stacks into a single pane below <see cref="AutomationsVisuals.NarrowBreakpoint"/>.
/// </summary>
internal sealed class AutomationsRoot : Component<AutomationsRootProps>
{
    private static long s_editorNonce;
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan NoticeLifetime = TimeSpan.FromSeconds(8);

    public override Element Render()
    {
        var baseHost = Props.Host;
        var (confirm, setConfirm) = UseState<ConfirmRequest?>(null, threadSafe: true);
        var host = baseHost with
        {
            Confirm = (title, body, primary) =>
            {
                var request = new ConfirmRequest(title, body, primary);
                setConfirm(request);
                return request.Result.Task;
            },
        };
        var store = host.Store;
        var t = host.Text;
        var (state, setState) = UseState(store.State, threadSafe: true);
        var (width, setWidth) = UseState(1200d);
        var (editor, setEditor) = UseState<EditorSession?>(null);
        var editorRef = UseRef<EditorSession?>(null);
        editorRef.Current = editor;
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var (search, setSearch) = UseState(state.ListQuery.Query);
        var searchToken = UseRef(0);
        var look = new AutomationsLook(UseReducedMotion(), UseHighContrast());

        UseEffect((Func<Action>)(() =>
        {
            EventHandler<AutomationsSnapshot> changed = (_, snapshot) => setState(snapshot);
            store.Changed += changed;
            setState(store.State);
            return () =>
            {
                store.Changed -= changed;
                editorRef.Current = null;
            };
        }), store);

        var noticeId = state.Notice?.Id ?? 0;
        UseEffect((Func<Action>)(() =>
        {
            if (state.Notice is not { IsError: false } notice)
                return static () => { };
            var cts = new CancellationTokenSource();
            _ = Task.Delay(NoticeLifetime, cts.Token).ContinueWith(task =>
            {
                if (!task.IsCanceled && store.State.Notice?.Id == notice.Id)
                    store.DismissNotice();
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return () => cts.Cancel();
        }), noticeId);

        void OnSearchChanged(string text)
        {
            setSearch(text);
            var token = ++searchToken.Current;
            _ = Task.Delay(SearchDebounce).ContinueWith(_ =>
            {
                if (token == searchToken.Current)
                    store.SetQuery(store.State.ListQuery with { Query = text });
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void SetEditorSession(EditorSession? next)
        {
            editorRef.Current = next;
            setEditor(next);
        }

        void OpenEditor(EditorMode mode, CronJobForm form, CronJob? source) =>
            SetEditorSession(new EditorSession(mode, form, source, Interlocked.Increment(ref s_editorNonce)));

        void NewAutomation() => OpenEditor(EditorMode.Create, CronJobForm.CreateDefault(state.AgentId), null);

        void OnEditorDispatcher(Action action)
        {
            if (dispatcher.HasThreadAccess) action();
            else dispatcher.TryEnqueue(() => action());
        }

        void UpdateEditor(long nonce, Func<EditorSession, EditorSession> change) =>
            OnEditorDispatcher(() =>
            {
                if (editorRef.Current is { } current && current.Nonce == nonce)
                    SetEditorSession(change(current));
            });

        void CloseEditor(long nonce, string? savedJobId)
        {
            OnEditorDispatcher(() =>
            {
                if (editorRef.Current?.Nonce != nonce) return;
                SetEditorSession(null);
                if (savedJobId is not null)
                    store.Select(savedJobId);
            });
        }

        var narrow = width < NarrowBreakpoint;
        var actions = new AutomationsActions(
            NewAutomation,
            job => OpenEditor(EditorMode.Edit, CronJobForm.FromJob(job), job),
            job => OpenEditor(EditorMode.Clone, CronJobForm.FromClone(job, t.Format("CloneName", job.Name)), job),
            suggestion => OpenEditor(EditorMode.Create, CronJobForm.FromSuggestion(
                suggestion,
                t[$"Suggestion_{suggestion.Id}_Name"],
                t[$"Suggestion_{suggestion.Id}_Prompt"],
                state.AgentId), null));

        Element body;
        if (!state.Connected)
        {
            body = Hero(GlyphDisconnected, t["Disconnected_Title"], t["Disconnected_Body"], "AutomationsDisconnected",
                    GlyphButton(GlyphPlug, t["OpenConnection"], host.OpenConnection, "AutomationsOpenConnection", accent: true))
                .VAlign(VerticalAlignment.Center)
                .Margin(0, 48, 0, 48);
        }
        else
        {
            var detail = RenderDetailPane(host, state, editor, actions, UpdateEditor, CloseEditor, narrow, look);
            var showDetailOnly = narrow && (editor is not null || state.SelectedJobId is not null
                || state.View == AutomationsView.RunHistory || state.ShowSuggestions);
            var list = RenderListPane(host, state, search, OnSearchChanged, narrow, look);
            body = narrow
                ? (showDetailOnly ? detail : list)
                : Grid([GridSize.Px(ListPaneWidth), GridSize.Px(1), GridSize.Star()], [GridSize.Star()],
                    list.Grid(column: 0),
                    Border(Empty()).Background(Theme.DividerStroke).Grid(column: 1),
                    detail.Grid(column: 2));
        }

        var dialog = confirm is { } pending
            ? (ContentDialog(pending.Title, TextBlock(pending.Body).TextWrapping(TextWrapping.Wrap).MaxWidth(420), pending.Primary) with
            {
                IsOpen = true,
                CloseButtonText = t["Cancel"],
                DefaultButton = ContentDialogButton.Close,
                OnClosed = result =>
                {
                    pending.Result.TrySetResult(result == ContentDialogResult.Primary);
                    setConfirm(null);
                },
            }).AutomationId("AutomationsConfirmDialog").WithKey(pending.Id)
            : Empty();

        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Star()],
                RenderHeader(host, state, actions, narrow, look).Grid(row: 0),
                RenderBanners(host, state).Grid(row: 1),
                body.Grid(row: 2),
                dialog.Grid(row: 0))
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch)
            .OnSizeChanged((_, args) =>
            {
                if (args.NewSize.Width > 0 && Math.Abs(args.NewSize.Width - width) > 1)
                    setWidth(args.NewSize.Width);
            })
            .AutomationId("AutomationsRoot");
    }

    // ─── Header and status strip ───

    private static Element RenderHeader(AutomationsHost host, AutomationsSnapshot state, AutomationsActions actions, bool narrow, AutomationsLook look)
    {
        var t = host.Text;
        var nextUp = state.Jobs
            .Where(j => j.Enabled && j.State.NextRunAtMs is not null)
            .OrderBy(j => j.State.NextRunAtMs)
            .FirstOrDefault();
        var headline = !state.Connected || !state.ListLoaded
            ? null
            : nextUp is { State.NextRunAtMs: { } at }
                ? t.Format("Header_NextUp", nextUp.Title, t.Relative(at))
                : t["Header_AllQuiet"];

        var scope = Border(HStack(6,
                Glyph(GlyphPerson, 12, Theme.AccentText),
                TextBlock(t.Format("AgentScope", state.AgentId)).FontSize(12).FontWeight(FontWeights.SemiBold)
                    .Foreground(Theme.AccentText).AutomationId("AutomationsAgentScope")))
            .Padding(10, 3)
            .CornerRadius(12)
            .Background(Theme.CardBackground)
            .BorderBrush(Theme.Ref("AutomationsHeroStrokeBrush"))
            .BorderThickness(1)
            .VAlign(VerticalAlignment.Center);

        var title = VStack(6,
            TextBlock(t["Title"]).FontSize(28).FontWeight(FontWeights.SemiBold).AutomationId("CronPageMarker"),
            HStack(10,
                    scope,
                    headline is null ? Empty() : Secondary(headline, 13).TextWrapping(TextWrapping.NoWrap)
                        .TextTrimming(TextTrimming.CharacterEllipsis).VAlign(VerticalAlignment.Center).AutomationId("AutomationsHeadline")));
        var buttons = HStack(Gap,
            IconButton(GlyphRefresh, t["Refresh"], host.Store.Refresh, "AutomationsRefresh", state.Connected),
            state.CanManage
                ? GlyphButton(GlyphAdd, t["NewAutomation"], actions.New, "AutomationsNew", state.Connected, accent: true)
                : Empty());
        var header = Wash(Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                (narrow ? Empty() : AppIcon(look.HighContrast, 52).Margin(0, 0, 16, 0).VAlign(VerticalAlignment.Center)).Grid(column: 0),
                title.VAlign(VerticalAlignment.Center).Grid(column: 1),
                buttons.VAlign(VerticalAlignment.Center).Margin(12, 0, 0, 0).Grid(column: 2))
            .Margin(20, 16, 20, 16));

        Element stats = state.Connected ? RenderStats(t, state, narrow, look) : Empty();
        return VStack(SectionGap, header, stats)
            .Padding(PagePadding, 16, PagePadding, 12);
    }

    private static Element RenderStats(AutomationsText t, AutomationsSnapshot state, bool narrow, AutomationsLook look)
    {
        var schedulerOn = state.Status?.Enabled;
        var nextWake = state.ScopedNextWakeAtMs ?? state.Status?.NextWakeAtMs;
        var failing = state.FailingCount;

        Element Disc(string glyph, ThemeRef brush, ThemeRef fill) =>
            Border(Glyph(glyph, 16, brush).Center()).Size(36, 36).CornerRadius(18).Background(fill)
                .BorderBrush(Theme.Ref("AutomationsTileStrokeBrush")).BorderThickness(1);

        Element Tile(Element leading, string label, Element value, string id, string? tooltip = null)
        {
            var tile = Surface(Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                    leading.VAlign(VerticalAlignment.Center).Margin(0, 0, 12, 0).Grid(column: 0),
                    VStack(2, Secondary(label), value).Grid(column: 1)), padding: 14)
                .AutomationId(id);
            return tooltip is null ? tile : tile.ToolTip(tooltip);
        }

        TextBlockElement Big(string text) => TextBlock(text).FontSize(20).FontWeight(FontWeights.SemiBold);

        var schedulerBrush = schedulerOn == false ? Theme.SystemNeutral : Theme.SystemSuccess;
        var tiles = new[]
        {
            Tile(
                Border(BreathingDot(schedulerBrush, animate: schedulerOn == true && !look.ReduceMotion, size: 12).Center())
                    .Size(36, 36).CornerRadius(18)
                    .Background(schedulerOn == false ? Theme.SystemNeutralBackground : Theme.SystemSuccessBackground),
                t["Stat_Scheduler"],
                Big(schedulerOn == false ? t["Scheduler_Off"] : schedulerOn == true ? t["Scheduler_On"] : t["Unknown"]),
                "AutomationsStatScheduler"),
            Tile(Disc(GlyphSparkle, Theme.AccentText, Theme.SystemAttentionBackground),
                t["Stat_Total"],
                Big(state.ScopedTotal?.ToString() ?? "—"),
                "AutomationsStatTotal"),
            Tile(failing is > 0
                    ? Disc(GlyphError, Theme.SystemCritical, Theme.SystemCriticalBackground)
                    : Disc(GlyphHeart, Theme.SystemSuccess, Theme.SystemSuccessBackground),
                t["Stat_Failing"],
                HStack(Gap,
                    Big(failing?.ToString() ?? "—").Foreground(failing is > 0 ? Theme.SystemCritical : Theme.PrimaryText),
                    failing == 0 ? Secondary(t["Stat_Healthy"], 13).VAlign(VerticalAlignment.Center) : Empty()),
                "AutomationsStatFailing"),
            Tile(Disc(GlyphClock, Theme.Ref("AutomationsKindCronBrush"), Theme.Ref("AutomationsKindCronFillBrush")),
                t["Stat_NextWake"],
                Big(nextWake is { } wake ? t.Relative(wake) : t["NextWake_None"]),
                "AutomationsStatNextWake",
                nextWake is { } w ? t.DateTime(w) : null),
        };
        return narrow
            ? Grid([GridSize.Star(), GridSize.Star()], [GridSize.Auto, GridSize.Auto],
                tiles[0].Margin(0, 0, 4, 4).Grid(row: 0, column: 0), tiles[1].Margin(4, 0, 0, 4).Grid(row: 0, column: 1),
                tiles[2].Margin(0, 4, 4, 0).Grid(row: 1, column: 0), tiles[3].Margin(4, 4, 0, 0).Grid(row: 1, column: 1))
            : Grid([GridSize.Star(), GridSize.Star(), GridSize.Star(), GridSize.Star()], [GridSize.Auto],
                tiles[0].Margin(0, 0, 6, 0).Grid(column: 0), tiles[1].Margin(6, 0, 6, 0).Grid(column: 1),
                tiles[2].Margin(6, 0, 6, 0).Grid(column: 2), tiles[3].Margin(6, 0, 0, 0).Grid(column: 3));
    }

    private static Element RenderBanners(AutomationsHost host, AutomationsSnapshot state)
    {
        var t = host.Text;
        var banners = new List<Element>();
        if (!state.Connected)
        {
            banners.Add((InfoBar(t["Disconnected_BannerTitle"], t["Disconnected_BannerBody"]) with
            {
                Severity = InfoBarSeverity.Warning,
                IsClosable = false,
                ActionButtonContent = t["OpenConnection"],
                OnActionButtonClick = host.OpenConnection,
            }).AutomationId("AutomationsConnectionBanner"));
        }
        else
        {
            if (!state.CanManage)
                banners.Add((InfoBar(null, t["ReadOnly"]) with { Severity = InfoBarSeverity.Informational, IsClosable = false })
                    .AutomationId("AutomationsReadOnlyBanner"));
            if (state.Status?.Enabled == false)
                banners.Add((InfoBar(t["SchedulerOff_Title"], t["SchedulerOff_Body"]) with { Severity = InfoBarSeverity.Warning, IsClosable = false })
                    .AutomationId("AutomationsSchedulerOffBanner"));
            if (state.Notice is { } notice)
            {
                var (title, message) = t.Notice(notice);
                banners.Add((InfoBar(title, message) with
                {
                    Severity = notice.IsError ? InfoBarSeverity.Error
                        : notice.Kind is AutomationsNoticeKind.Conflict or AutomationsNoticeKind.RunNotStarted ? InfoBarSeverity.Warning
                        : notice.Kind is AutomationsNoticeKind.RunQueued ? InfoBarSeverity.Informational
                        : InfoBarSeverity.Success,
                    OnClosed = host.Store.DismissNotice,
                }).AutomationId("AutomationsNotice").WithKey($"notice-{notice.Id}"));
            }
        }
        return banners.Count == 0 ? Empty() : VStack(Gap, banners.ToArray()).Padding(PagePadding, 0, PagePadding, 12);
    }

    // ─── List pane ───

    private static Element RenderListPane(AutomationsHost host, AutomationsSnapshot state, string search, Action<string> onSearch, bool narrow, AutomationsLook look)
    {
        var t = host.Text;
        var store = host.Store;
        var query = state.ListQuery;

        var viewBar = SelectorBar(
                [SelectorBarItem(t["View_Automations"]), SelectorBarItem(t["View_RunHistory"])],
                state.View == AutomationsView.Jobs ? 0 : 1,
                index => store.SetView(index == 0 ? AutomationsView.Jobs : AutomationsView.RunHistory))
            .AutomationId("AutomationsViewSelector");

        var searchBox = (AutoSuggestBox(search, onSearch, onSearch) with
        {
            PlaceholderText = t["Search_Placeholder"],
            QueryIcon = FontIcon(GlyphSearch),
        }).AutomationName(t["Search_Placeholder"]).AutomationId("AutomationsSearch");

        var statusFilter = SelectorBar(
                [SelectorBarItem(t["Filter_All"]), SelectorBarItem(t["Filter_Active"]), SelectorBarItem(t["Filter_Paused"])],
                query.Enabled switch { CronEnabledFilter.Enabled => 1, CronEnabledFilter.Disabled => 2, _ => 0 },
                index => store.SetQuery(query with
                {
                    Enabled = index switch { 1 => CronEnabledFilter.Enabled, 2 => CronEnabledFilter.Disabled, _ => CronEnabledFilter.All },
                }))
            .AutomationId("AutomationsStatusFilter");

        var sortMenu = DropDownButton(t["Sort"], MenuItems(
                RadioMenuItem(t["Sort_NextRun"], "sortBy", query.SortBy == CronSortBy.NextRunAtMs, () => store.SetQuery(query with { SortBy = CronSortBy.NextRunAtMs })),
                RadioMenuItem(t["Sort_Updated"], "sortBy", query.SortBy == CronSortBy.UpdatedAtMs, () => store.SetQuery(query with { SortBy = CronSortBy.UpdatedAtMs })),
                RadioMenuItem(t["Sort_Name"], "sortBy", query.SortBy == CronSortBy.Name, () => store.SetQuery(query with { SortBy = CronSortBy.Name })),
                MenuSeparator(),
                RadioMenuItem(t["Sort_Ascending"], "sortDir", !query.Descending, () => store.SetQuery(query with { Descending = false })),
                RadioMenuItem(t["Sort_Descending"], "sortDir", query.Descending, () => store.SetQuery(query with { Descending = true }))))
            .AutomationId("AutomationsSort");

        var filterMenu = DropDownButton(query.HasAdvancedFilters ? t["Filters_Active"] : t["Filters"], MenuItems(
                MenuSubItem(t["Filter_ScheduleKind"],
                    FilterItem(t["Filter_Any"], "kind", query.ScheduleKind == "all", () => store.SetQuery(query with { ScheduleKind = "all" })),
                    FilterItem(t["Kind_Every"], "kind", query.ScheduleKind == "every", () => store.SetQuery(query with { ScheduleKind = "every" })),
                    FilterItem(t["Kind_At"], "kind", query.ScheduleKind == "at", () => store.SetQuery(query with { ScheduleKind = "at" })),
                    FilterItem(t["Kind_Cron"], "kind", query.ScheduleKind == "cron", () => store.SetQuery(query with { ScheduleKind = "cron" })),
                    FilterItem(t["Kind_Event"], "kind", query.ScheduleKind == "event", () => store.SetQuery(query with { ScheduleKind = "event" })),
                    FilterItem(t["Kind_OnExit"], "kind", query.ScheduleKind == "on-exit", () => store.SetQuery(query with { ScheduleKind = "on-exit" })),
                    FilterItem(t["Kind_Stream"], "kind", query.ScheduleKind == "stream", () => store.SetQuery(query with { ScheduleKind = "stream" }))),
                MenuSubItem(t["Filter_LastRun"],
                    FilterItem(t["Filter_Any"], "last", query.LastRunStatus == "all", () => store.SetQuery(query with { LastRunStatus = "all" })),
                    FilterItem(t["RunStatus_Ok"], "last", query.LastRunStatus == "ok", () => store.SetQuery(query with { LastRunStatus = "ok" })),
                    FilterItem(t["RunStatus_Error"], "last", query.LastRunStatus == "error", () => store.SetQuery(query with { LastRunStatus = "error" })),
                    FilterItem(t["RunStatus_Skipped"], "last", query.LastRunStatus == "skipped", () => store.SetQuery(query with { LastRunStatus = "skipped" })),
                    FilterItem(t["Filter_NeverRun"], "last", query.LastRunStatus == "unknown", () => store.SetQuery(query with { LastRunStatus = "unknown" }))),
                MenuSubItem(t["Filter_Condition"],
                    FilterItem(t["Filter_Any"], "trigger", query.Trigger == "all", () => store.SetQuery(query with { Trigger = "all" })),
                    FilterItem(t["Filter_Conditional"], "trigger", query.Trigger == "conditional", () => store.SetQuery(query with { Trigger = "conditional" })),
                    FilterItem(t["Filter_Unconditional"], "trigger", query.Trigger == "unconditional", () => store.SetQuery(query with { Trigger = "unconditional" }))),
                MenuSeparator(),
                MenuItem(t["Filter_Reset"], () => store.SetQuery(query with { ScheduleKind = "all", LastRunStatus = "all", Trigger = "all" }))))
            .AutomationId("AutomationsFilters");

        var rows = new List<Element>();
        if (state.ListLoading && !state.ListLoaded)
        {
            rows.Add(HStack(Gap, ProgressRing().Size(16, 16), Secondary(t["Loading"], 13)).Margin(0, 12, 0, 0).AutomationId("AutomationsListLoading"));
        }
        else if (state.ListError is { } error)
        {
            rows.Add((InfoBar(state.ReadDenied ? t["ReadDenied_Title"] : t["ListError_Title"], error) with
            {
                Severity = InfoBarSeverity.Error,
                IsClosable = false,
                ActionButtonContent = state.ReadDenied ? null : t["Retry"],
                OnActionButtonClick = state.ReadDenied ? null : () => _ = store.LoadListAsync(),
            }).AutomationId("AutomationsListError"));
        }
        else if (state.ListLoaded && state.Jobs.Count == 0)
        {
            rows.Add(Secondary(state.IsFiltered ? t["List_NoMatch"] : t["List_Empty"], 13)
                .Margin(4, 12, 4, 0).AutomationId("AutomationsListEmpty"));
        }

        foreach (var job in state.Jobs)
            rows.Add(RenderRow(host, state, job, look).WithKey(job.Id));

        if (state.HasMore)
        {
            rows.Add(Button(state.ListLoadingMore ? t["Loading"] : t["LoadMore"], () => _ = store.LoadListAsync(append: true)).AutomationName(t["LoadMore"])
                .IsEnabled(!state.ListLoadingMore)
                .HAlign(HorizontalAlignment.Stretch)
                .AutomationId("AutomationsLoadMore"));
        }

        var countText = state.ListLoaded && state.ListError is null
            ? Secondary(t.Format("List_Count", state.Jobs.Count, state.Total)).AutomationId("AutomationsListCount")
            : (Element)Empty();

        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Star()],
                VStack(Gap,
                    viewBar,
                    searchBox,
                    Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                        statusFilter.Grid(column: 0),
                        HStack(4, filterMenu, sortMenu).Grid(column: 1)),
                    countText).Padding(PagePadding, 0, narrow ? PagePadding : 12, 8).Grid(row: 0),
                Empty().Grid(row: 1),
                ScrollView(VStack(4, rows.ToArray()).Stagger(StaggerStep).Padding(PagePadding - 4, 0, narrow ? PagePadding - 4 : 8, PagePadding))
                    .AutomationId("AutomationsList")
                    .Grid(row: 2))
            .AutomationId("AutomationsListPane");
    }

    private static Microsoft.UI.Reactor.Core.RadioMenuFlyoutItemData FilterItem(string text, string group, bool isChecked, Action onClick) =>
        RadioMenuItem(text, group, isChecked, onClick);

    private static Element RenderRow(AutomationsHost host, AutomationsSnapshot state, CronJob job, AutomationsLook look)
    {
        var t = host.Text;
        var running = state.IsRunning(job.Id);
        var selected = state.SelectedJobId == job.Id && state.View == AutomationsView.Jobs;
        var (fg, _, _) = StatusTone(job, running);
        var tone = Kind(job.Schedule.Kind);
        var when = running || job.IsRunning
            ? t["State_Running"]
            : job.State.NextRunAtMs is { } next && job.Enabled
                ? t.Format("Row_Next", t.Relative(next))
                : job.State.LastRunAtMs is { } last
                    ? t.Format("Row_Last", t.Relative(last))
                    : t.JobState(job, running);

        var paused = !job.Enabled && !running;
        var kindTile = KindTile(job.Schedule.Kind, 40, content: running || job.IsRunning ? ProgressRing().Size(18, 18).Center() : null)
            .Opacity(paused ? 0.55 : 1)
            .HAlign(HorizontalAlignment.Left).VAlign(VerticalAlignment.Top);
        // A short glow when the selected automation was just created, saved or resumed.
        if (selected && !paused && !look.ReduceMotion
            && state.Notice is { Kind: AutomationsNoticeKind.Created or AutomationsNoticeKind.Saved or AutomationsNoticeKind.Resumed } celebrated)
        {
            kindTile = kindTile.Keyframes("celebrate", celebrated.Id, kf => kf
                .Duration(900)
                .At(0f, opacity: 1f)
                .At(0.25f, opacity: 0.35f, easing: Microsoft.UI.Reactor.Animation.Easing.EaseOut)
                .At(0.5f, opacity: 1f, easing: Microsoft.UI.Reactor.Animation.Easing.EaseInOut)
                .At(0.75f, opacity: 0.5f, easing: Microsoft.UI.Reactor.Animation.Easing.EaseInOut)
                .At(1f, opacity: 1f, easing: Microsoft.UI.Reactor.Animation.Easing.Decelerate));
        }
        var tile = Grid([GridSize.Px(46)], [GridSize.Px(46)],
            kindTile,
            (paused ? Border(Glyph(GlyphPause, 8, Theme.SecondaryText).Center()).Size(16, 16).CornerRadius(8)
                    .Background(Theme.CardBackground).BorderBrush(Theme.CardStroke).BorderThickness(1)
                : StatusDot(fg, 14))
                .HAlign(HorizontalAlignment.Right).VAlign(VerticalAlignment.Bottom));
        var lines = VStack(2,
            HStack(6,
                TextBlock(job.Title).FontSize(14).FontWeight(FontWeights.SemiBold)
                    .Foreground(paused ? Theme.SecondaryText : Theme.PrimaryText)
                    .TextTrimming(TextTrimming.CharacterEllipsis).AutomationId($"CronJobTitle:{job.Id}"),
                job.HasTrigger ? Glyph(GlyphBranch, 12).ToolTip(t["TriggerConfigured"]) : Empty()),
            TextBlock(t.Schedule(job.Schedule)).FontSize(12).Foreground(Theme.SecondaryText).TextTrimming(TextTrimming.CharacterEllipsis),
            HStack(4,
                Glyph(job.IsFailing ? GlyphError : GlyphClock, 11, job.IsFailing ? Theme.SystemCritical : paused ? Theme.TertiaryText : tone.Brush),
                TextBlock(when).FontSize(12).Foreground(job.IsFailing ? Theme.SystemCritical : Theme.TertiaryText)
                    .TextTrimming(TextTrimming.CharacterEllipsis)));
        var content = Grid([GridSize.Px(3), GridSize.Auto, GridSize.Star()], [GridSize.Auto],
            Border(Empty()).CornerRadius(2).Background(selected ? Theme.Accent : Theme.Ref("SubtleFillColorTransparentBrush"))
                .Margin(0, 4, 9, 4).Grid(column: 0),
            tile.Margin(0, 0, 12, 0).Grid(column: 1),
            lines.VAlign(VerticalAlignment.Center).Grid(column: 2));

        // Selection is drawn with Background and an accent bar: lightweight-styling
        // resources only re-resolve on visual-state changes, so they cannot track selection.
        return Button(content, () => host.Store.Select(job.Id))
            .HAlign(HorizontalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .Padding(4, 8, 12, 8)
            .CornerRadius(8)
            .Background(selected ? Theme.SubtleFill : Theme.Ref("SubtleFillColorTransparentBrush"))
            .BorderThickness(0)
            .Resources(resources => resources
                .Set("ButtonBackgroundPointerOver", Theme.SubtleFill)
                .Set("ButtonBackgroundPressed", Theme.Ref("SubtleFillColorTertiaryBrush"))
                .Set("ButtonBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush"))
                .Set("ButtonBorderBrushPointerOver", Theme.Ref("SubtleFillColorTransparentBrush"))
                .Set("ButtonBorderBrushPressed", Theme.Ref("SubtleFillColorTransparentBrush")))
            .AutomationId($"AutomationRow:{job.Id}")
            .AutomationName($"{job.Title}. {t.JobState(job, running)}. {t.Schedule(job.Schedule)}")
            .ItemStatus(t.JobState(job, running))
            .Enter(look.ReduceMotion);
    }

    // ─── Detail pane ───

    private static Element RenderDetailPane(
        AutomationsHost host,
        AutomationsSnapshot state,
        EditorSession? editor,
        AutomationsActions actions,
        Action<long, Func<EditorSession, EditorSession>> updateEditor,
        Action<long, string?> closeEditor,
        bool narrow,
        AutomationsLook look)
    {
        var t = host.Text;
        Element content;
        if (editor is not null)
        {
            content = Component<AutomationEditor, AutomationEditorProps>(new(host, editor, state.AgentId, state.CanManage,
                    change => updateEditor(editor.Nonce, change), savedId => closeEditor(editor.Nonce, savedId)))
                .WithKey($"editor-{editor.Nonce}");
        }
        else if (state.View == AutomationsView.RunHistory)
        {
            content = Component<AutomationRuns, AutomationRunsProps>(new(host, state, null))
                .WithKey("runs-all");
        }
        else if (state.SelectedJobId is not null && (state.SelectedJob ?? state.Jobs.FirstOrDefault(j => j.Id == state.SelectedJobId)) is { } job)
        {
            content = Component<AutomationDetail, AutomationDetailProps>(new(host, state, job, actions))
                .WithKey($"detail-{job.Id}");
        }
        else if (state.SelectedJobId is not null && state.SelectedError is { } selectedError)
        {
            content = (InfoBar(t["Detail_LoadFailed"], selectedError) with { Severity = InfoBarSeverity.Error, IsClosable = false })
                .Margin(PagePadding);
        }
        else if (state.ShowSuggestions)
        {
            content = ScrollView(RenderEmptyHero(host, actions, look).Padding(PagePadding)).WithKey("suggestions");
        }
        else if (state.ListLoaded && state.Jobs.Count == 0 && !state.IsFiltered)
        {
            content = Hero(GlyphCalendar, t["Empty_Title"], t["Empty_ReadOnlyBody"], "AutomationsEmpty").VAlign(VerticalAlignment.Center);
        }
        else
        {
            content = Hero(GlyphCalendar, t["Placeholder_Title"], t["Placeholder_Body"], "AutomationsPlaceholder").VAlign(VerticalAlignment.Center);
        }
        var paneKey = editor is not null ? $"editor-{editor.Nonce}"
            : state.View == AutomationsView.RunHistory ? "runs-all"
            : state.SelectedJobId is { } selectedId ? $"detail-{selectedId}"
            : state.ShowSuggestions ? "suggestions"
            : "placeholder";
        content = Border(content).WithKey("pane-" + paneKey).Enter(look.ReduceMotion);

        if (!narrow || (editor is null && state.SelectedJobId is null && state.View == AutomationsView.Jobs))
            return content;

        var back = Button(HStack(Gap, Glyph(GlyphBack, 12, Theme.PrimaryText), TextBlock(t["Back"])), () =>
            {
                if (editor is not null) closeEditor(editor.Nonce, null);
                else if (state.View == AutomationsView.RunHistory) host.Store.SetView(AutomationsView.Jobs);
                else host.Store.Select(null);
            })
            .Margin(PagePadding, 0, PagePadding, 8)
            .AutomationName(t["Back"])
            .AutomationId("AutomationsBack");
        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Star()], back.Grid(row: 0), content.Grid(row: 1));
    }

    private static Element RenderEmptyHero(AutomationsHost host, AutomationsActions actions, AutomationsLook look)
    {
        var t = host.Text;
        // Starters are topics, not schedule kinds: rotate the palette so the gallery has variety.
        CronScheduleKind[] hues = [CronScheduleKind.Event, CronScheduleKind.Every, CronScheduleKind.Cron, CronScheduleKind.At];
        var cards = CronSuggestion.All.Select((s, index) =>
        {
            var hue = hues[index % hues.Length];
            var tone = Kind(hue);
            return Button(Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                    KindTile(hue, 44, s.Glyph).VAlign(VerticalAlignment.Top).Margin(0, 0, 14, 0).Grid(column: 0),
                    VStack(4,
                        TextBlock(t[$"Suggestion_{s.Id}_Name"]).FontSize(15).FontWeight(FontWeights.SemiBold),
                        Secondary(t[$"Suggestion_{s.Id}_Tagline"], 13),
                        HStack(4, Glyph(GlyphClock, 11, tone.Brush),
                            TextBlock(t[$"Suggestion_{s.Id}_Schedule"]).FontSize(12).FontWeight(FontWeights.SemiBold).Foreground(tone.Brush))
                            .Margin(0, 2, 0, 0)).Grid(column: 1)),
                    () => actions.Suggest(s))
                .HAlign(HorizontalAlignment.Stretch)
                .VAlign(VerticalAlignment.Stretch)
                .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .VerticalContentAlignment(VerticalAlignment.Top)
                .Padding(16, 14)
                .CornerRadius(10)
                .AutomationName(t[$"Suggestion_{s.Id}_Name"])
                .AutomationId($"AutomationSuggestion:{s.Id}")
                .WithKey(s.Id);
        }).ToArray();

        var grid = Grid([GridSize.Star(), GridSize.Star()], Enumerable.Repeat(GridSize.Auto, (cards.Length + 1) / 2).ToArray(),
            cards.Select((card, i) => card.Margin(i % 2 == 0 ? 0 : 6, 6, i % 2 == 0 ? 6 : 0, 6).Grid(row: i / 2, column: i % 2)
                .Enter(look.ReduceMotion).WithKey($"s{i}")).ToArray())
            .Stagger(TimeSpan.FromMilliseconds(45));

        var hero = VStack(14,
                Illustration(look).HAlign(HorizontalAlignment.Center),
                TextBlock(t["Empty_Headline"]).FontSize(28).FontWeight(FontWeights.SemiBold)
                    .TextAlignment(TextAlignment.Center).TextWrapping(TextWrapping.Wrap).HAlign(HorizontalAlignment.Center),
                Secondary(t["Empty_Body"], 14).TextAlignment(TextAlignment.Center).MaxWidth(460).HAlign(HorizontalAlignment.Center),
                GlyphButton(GlyphAdd, t["NewAutomation"], actions.New, "AutomationsEmptyNew", accent: true)
                    .HAlign(HorizontalAlignment.Center).Margin(0, 6, 0, 0))
            .AutomationId("AutomationsEmpty");

        return VStack(28,
                hero,
                VStack(Gap,
                    HStack(Gap, Glyph(GlyphSparkle, 16, Theme.AccentText), SectionTitle(t["Suggestions_Title"])),
                    grid).AutomationId("AutomationsSuggestions"))
            .MaxWidth(ReadingWidth)
            .HAlign(HorizontalAlignment.Center);
    }

    /// <summary>
    /// A small composition of the four schedule kinds orbiting the Automations
    /// artwork. Decorative only; sparkles twinkle unless motion is reduced.
    /// </summary>
    private static Element Illustration(AutomationsLook look)
    {
        Element Sparkle(double size, HorizontalAlignment h, VerticalAlignment v, Thickness margin, int periodMs, float low)
        {
            var star = Glyph("\uE735", size, Theme.Ref("AutomationsKindAtBrush")).HAlign(h).VAlign(v).Margin(margin);
            return look.ReduceMotion
                ? star.Opacity(0.7)
                : star.Keyframes("twinkle", true, kf => kf.Duration(periodMs).Loop()
                    .At(0f, opacity: 1f)
                    .At(0.5f, opacity: low, easing: Microsoft.UI.Reactor.Animation.Easing.EaseInOut)
                    .At(1f, opacity: 1f, easing: Microsoft.UI.Reactor.Animation.Easing.EaseInOut));
        }

        var center = Border(AppIcon(look.HighContrast, 56).Center())
            .Size(96, 96)
            .CornerRadius(28)
            .Background(Theme.CardBackground)
            .BorderBrush(Theme.Ref("AutomationsHeroStrokeBrush"))
            .BorderThickness(1)
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center);

        return Grid([GridSize.Px(300)], [GridSize.Px(156)],
                Border(Empty()).Size(240, 120).CornerRadius(60).Background(Theme.Ref("AutomationsHeroBrush"))
                    .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center),
                KindTile(CronScheduleKind.Every, 48).HAlign(HorizontalAlignment.Left).VAlign(VerticalAlignment.Top).Margin(34, 10, 0, 0),
                KindTile(CronScheduleKind.At, 38).HAlign(HorizontalAlignment.Left).VAlign(VerticalAlignment.Bottom).Margin(62, 0, 0, 6),
                KindTile(CronScheduleKind.Cron, 44).HAlign(HorizontalAlignment.Right).VAlign(VerticalAlignment.Top).Margin(0, 4, 52, 0),
                KindTile(CronScheduleKind.Event, 50).HAlign(HorizontalAlignment.Right).VAlign(VerticalAlignment.Bottom).Margin(0, 0, 28, 0),
                center,
                Sparkle(14, HorizontalAlignment.Left, VerticalAlignment.Center, new Thickness(12, 0, 0, 10), 2600, 0.25f),
                Sparkle(10, HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, 0, 18, 0), 1900, 0.15f),
                Sparkle(12, HorizontalAlignment.Center, VerticalAlignment.Bottom, new Thickness(90, 0, 0, 0), 3100, 0.3f))
            .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw)
            .AutomationId("AutomationsIllustration");
    }
}

/// <summary>Editor entry points shared by the list, detail pane and suggestions.</summary>
internal sealed record AutomationsActions(
    Action New,
    Action<CronJob> Edit,
    Action<CronJob> Duplicate,
    Action<CronSuggestion> Suggest);

/// <summary>A pending pause/remove confirmation rendered as a controlled ContentDialog.</summary>
internal sealed record ConfirmRequest(string Title, string Body, string Primary)
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public TaskCompletionSource<bool> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
