using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared.Cron;
using OpenClawTray.Presentation;
using static Microsoft.UI.Reactor.Factories;
using static OpenClawTray.Automations.AutomationsVisuals;

namespace OpenClawTray.Automations;

internal sealed record AutomationsRootProps(AutomationsHost Host);
internal enum EditorMode { Create, Edit, Clone }

/// <summary>Root-owned draft lifetime, independent of connection, layout and dialog mounts.</summary>
internal sealed record EditorSession(EditorMode Mode, CronJobForm Form, CronJob? Source, long Nonce)
{
    public string DraftKey => Mode == EditorMode.Create ? "new" : Mode == EditorMode.Clone ? $"copy:{Source!.Id}" : Source!.Id;
    public CronJobForm InitialForm { get; init; } = Form;
    public string Scope { get; init; } = "";
    public bool RestoredDraft { get; init; }
    public bool Submitted { get; init; }
    public bool Saving { get; init; }
    public IReadOnlyDictionary<CronFormField, CronFormError> Errors { get; init; } =
        ImmutableDictionary<CronFormField, CronFormError>.Empty;
    public (string Title, string Message, InfoBarSeverity Severity)? Banner { get; init; }
}

/// <summary>Answer, attention, agenda and inline inventory. The only modal surface is the editor.</summary>
internal sealed class AutomationsRoot : Component<AutomationsRootProps>
{
    private static long s_editorNonce;
    private static readonly ConditionalWeakTable<Application, Dictionary<string, EditorSession>> SessionDrafts = new();
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(300);
    private const int MaxVisibleToasts = 3;

    public override Element Render()
    {
        var scrollRef = this.UseElementRef<ScrollView>();
        var pageRef = this.UseElementRef<Microsoft.UI.Xaml.Controls.Grid>();
        var announce = this.UseAnnounce();
        var look = new AutomationsLook(UseReducedMotion(), UseHighContrast());
        var confirmDialogRef = UseRef<ContentDialog?>(null);
        var measuredRows = UseRef(new Dictionary<string, WeakReference<FrameworkElement>>(StringComparer.Ordinal));
        var revealedSelection = UseRef<string?>(null);
        var pendingReveal = UseRef<string?>(null);
        var revealRef = UseRef<Func<string, bool, Task<AutomationRevealOutcome>>?>(null);
        var (confirm, setConfirm) = UseState<ConfirmRequest?>(null, threadSafe: true);
        var host = Props.Host with
        {
            RevealElement = target => target.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (target.IsLoaded && scrollRef.Current is { IsLoaded: true })
                    PageHero.RevealAndHighlight(target, look.ReduceMotion);
            }),
            Confirm = async request =>
            {
                if (pageRef.Current is { } page) await CloseTransientSurfacesAsync(page);
                setConfirm(request);
                return await request.Result.Task;
            },
        };
        var store = host.Store;
        var t = host.Text;
        var (state, setState) = UseState(store.State, threadSafe: true);
        var (toasts, setToasts) = UseState(ImmutableArray<AutomationsNotice>.Empty, threadSafe: true);
        var toastRef = UseRef(toasts);
        toastRef.Current = toasts;
        if (state.SelectedJobId is null) revealedSelection.Current = null;
        var (editor, setEditor) = UseState<EditorSession?>(null);
        var editorRef = UseRef<EditorSession?>(null);
        editorRef.Current = editor;
        var drafts = SessionDrafts.GetValue(Application.Current, _ => new(StringComparer.Ordinal));
        var draftScope = host.DraftScope();
        string DraftKey(EditorSession session) => $"{session.Scope}/{session.DraftKey}";
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var (search, setSearch) = UseState("");
        var searchToken = UseRef(0);
        var (expandedGroups, setExpandedGroups) = UseState(ImmutableHashSet<string>.Empty);
        var (expandedJobs, setExpandedJobs) = UseState(ImmutableHashSet<string>.Empty);
        var expandedJobsRef = UseRef(expandedJobs);
        expandedJobsRef.Current = expandedJobs;
        var detailSnapshots = UseRef(new Dictionary<string, AutomationsSnapshot>(StringComparer.Ordinal));
        var visitedJobs = UseRef(new HashSet<string>(StringComparer.Ordinal));
        var previousScope = UseRef((state.AgentFilter, state.Connected, state.ReadDenied));
        if (previousScope.Current.AgentFilter != state.AgentFilter || state.ReadDenied
            || (!previousScope.Current.Connected && state.Connected))
            detailSnapshots.Current.Clear();
        previousScope.Current = (state.AgentFilter, state.Connected, state.ReadDenied);
        if (state.SelectedJobId is { } selected && state.RunsQuery.JobId == selected && state.RunsLoaded)
            detailSnapshots.Current[selected] = state;
        UseEffect(() =>
        {
            if (state.SelectedJobId is { } id)
            {
                expandedJobsRef.Current = expandedJobsRef.Current.Add(id);
                setExpandedJobs(expandedJobsRef.Current);
            }
        }, state.SelectedJobId ?? "");
        var (hostSize, setHostSize) = UseState((Page: PageResponsiveLayout.WideMin, Window: PageResponsiveLayout.WideMin));
        var viewport = host.Viewport;
        var (topInset, setTopInset) = UseState(viewport.TopInset);
        UseEffect((Func<Action>)(() =>
        {
            EventHandler<double> changed = (_, inset) => setTopInset(inset);
            viewport.Changed += changed;
            setTopInset(viewport.TopInset);
            return () => viewport.Changed -= changed;
        }), viewport);
        var tier = PageResponsiveLayout.TierFor(hostSize.Page);
        var narrow = tier == PageWidthTier.Narrow;
        var contentWidth = PageResponsiveLayout.ContentWidth(hostSize.Page);
        var twoColumns = PageResponsiveLayout.UseTwoColumns(contentWidth);
        var dialogWidth = Math.Min(DialogMaxWidth, Math.Max(0, hostSize.Window - DialogWindowMargin));

        UseEffect((Func<Action>)(() =>
        {
            EventHandler<AutomationsSnapshot> changed = (_, snapshot) => setState(snapshot);
            EventHandler<AutomationsNotice> noticed = (_, notice) =>
            {
                var next = toastRef.Current.Add(notice);
                // Persistent errors stay until explicitly closed.
                if (next.Length > MaxVisibleToasts)
                    for (var index = 0; index < next.Length; index++)
                        if (!next[index].IsError) { next = next.RemoveAt(index); break; }
                toastRef.Current = next;
                setToasts(next);
            };
            store.Changed += changed;
            store.NoticeRaised += noticed;
            setState(store.State);
            return () => { store.Changed -= changed; store.NoticeRaised -= noticed; editorRef.Current = null; };
        }), store);

        // Hub search reaches the root through the page's channel; the latest render serves it, widened to every agent.
        UseEffect((Func<Action>)(() =>
        {
            var attached = host.Reveals.Attach(id => revealRef.Current?.Invoke(id, true) ?? Task.FromResult(AutomationRevealOutcome.Superseded));
            return attached.Dispose;
        }), store);

        UseEffect(() =>
        {
            if (state.Notice is { } notice)
                announce.Announce(t.Notice(notice).Message, assertive: notice.IsError);
        }, state.Notice?.Id ?? 0);

        void DismissToast(long id)
        {
            var next = toastRef.Current.Where(notice => notice.Id != id).ToImmutableArray();
            toastRef.Current = next;
            setToasts(next);
            if (store.State.Notice?.Id == id) store.DismissNotice();
        }

        UseEffect(() =>
        {
            ++searchToken.Current;
            setSearch(state.ListQuery.Query);
        }, state.ListQuery.Query, state.AgentFilter ?? "");

        void SetEditorSession(EditorSession? next) { editorRef.Current = next; setEditor(next); }
        void OpenEditor(EditorMode mode, CronJobForm form, CronJob? source) =>
            OpenClaw.Shared.AsyncEventHandlerGuard.Run(async () =>
            {
                if (pageRef.Current is { } page) await CloseTransientSurfacesAsync(page);
                var next = new EditorSession(mode, form, source, Interlocked.Increment(ref s_editorNonce)) { Scope = draftScope };
                if (drafts.TryGetValue(DraftKey(next), out var kept))
                {
                    next = kept with { Nonce = next.Nonce, Saving = false, RestoredDraft = true };
                    drafts[DraftKey(next)] = next;
                }
                SetEditorSession(next);
            }, operationName: "Open automation editor");
        void OnDispatcher(Action action)
        {
            if (dispatcher.HasThreadAccess) action(); else dispatcher.TryEnqueue(() => action());
        }
        void UpdateEditor(long nonce, Func<EditorSession, EditorSession> change) => OnDispatcher(() =>
        {
            if (editorRef.Current is { } current && current.Nonce == nonce)
            {
                var next = change(current);
                if (next.Form == next.InitialForm) drafts.Remove(DraftKey(next));
                else drafts[DraftKey(next)] = next;
                SetEditorSession(next);
            }
        });
        void CloseEditor(long nonce, string? savedId) => OnDispatcher(() =>
        {
            if (savedId is not null)
            {
                var savedDraft = drafts.FirstOrDefault(entry => entry.Value.Nonce == nonce);
                if (savedDraft.Key is not null) drafts.Remove(savedDraft.Key);
            }
            if (editorRef.Current is not { } current || current.Nonce != nonce) return;
            if (savedId is not null) drafts.Remove(DraftKey(current));
            else if (current.Form != current.InitialForm) drafts[DraftKey(current)] = current with { Saving = false };
            SetEditorSession(null);
            if (savedId is not null) _ = RevealJob(savedId, widenToAllAgents: true);
        });
        void OnSearch(string value)
        {
            setSearch(value);
            var token = ++searchToken.Current;
            _ = Task.Delay(SearchDebounce).ContinueWith(_ =>
            {
                if (token == searchToken.Current) store.SetQuery(store.State.ListQuery with { Query = value });
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        void ToggleGroup(string key) => setExpandedGroups(expandedGroups.Contains(key) ? expandedGroups.Remove(key) : expandedGroups.Add(key));
        void ToggleJob(string id, bool open)
        {
            expandedJobsRef.Current = open ? expandedJobsRef.Current.Add(id) : expandedJobsRef.Current.Remove(id);
            setExpandedJobs(expandedJobsRef.Current);
            if (open) store.Select(id);
            else if (store.State.SelectedJobId == id) store.Select(null);
        }
        // Resets browse state so the row is reachable, then reveals through the store. A saved job or a
        // search result may belong to another agent and widens to All agents; the agenda stays in scope.
        async Task<AutomationRevealOutcome> RevealJob(string id, bool widenToAllAgents)
        {
            ++searchToken.Current;
            setSearch("");
            revealedSelection.Current = null;
            pendingReveal.Current = id;
            var outcome = await store.RevealAsync(id, widenToAllAgents);
            if (outcome == AutomationRevealOutcome.Revealed)
                OnDispatcher(() =>
                {
                    if (measuredRows.Current.TryGetValue(id, out var reference) && reference.TryGetTarget(out var row) && row.IsLoaded)
                        RegisterRow(row, id);
                });
            return outcome;
        }
        void Reveal(string id) => _ = RevealJob(id, widenToAllAgents: false);
        revealRef.Current = RevealJob;
        void RegisterRow(FrameworkElement row, string id)
        {
            measuredRows.Current[id] = new(row);
            if (store.State.SelectedJobId != id || pendingReveal.Current != id || revealedSelection.Current == id) return;
            revealedSelection.Current = id;
            host.RevealElement?.Invoke(row);
        }
        var actions = new AutomationsActions(
            () => OpenEditor(EditorMode.Create, CronJobForm.CreateDefault(state.DefaultAgentId), null),
            job => OpenEditor(EditorMode.Edit, CronJobForm.FromJob(job), job),
            job => OpenEditor(EditorMode.Clone, CronJobForm.FromClone(job, t.Format("CloneName", job.Name)), job));

        Element content;
        if (state.ReadDenied)
            content = Hero(GlyphLock, t["ReadDenied_Title"], t["Redesign_ReadDenied"], "AutomationsReadDenied");
        else if (!state.Connected && !state.ListLoaded)
            content = Empty();
        else if (!state.ListLoaded && state.ListError is null)
            content = VStack(PageResponsiveLayout.GapInner, Enumerable.Range(0, 4).Select(i => (Element)Surface(
                HStack(12, Border(Empty()).Size(40, 40).CornerRadius(8).Background(Theme.SubtleFill),
                    VStack(8, Border(Empty()).Height(16).Width(240).Background(Theme.SubtleFill),
                        Border(Empty()).Height(12).Width(160).Background(Theme.SubtleFill)))).WithKey(i.ToString())).ToArray())
                .AutomationName(t["Loading"]).AutomationId("AutomationsListLoading");
        else if (!state.ListLoaded && state.ListError is { } initialError)
            content = Hero(GlyphError, t["ListError_Title"], initialError, "AutomationsListError",
                GlyphButton(GlyphRefresh, t["Retry"], store.Refresh, "AutomationsRetry"));
        else if (state.ListLoaded && state.Jobs.Count == 0 && !state.IsFiltered)
            content = VStack(16,
                RenderAgentFilter(host, state),
                Hero(GlyphCalendar, t["Empty_Title"], t[state.CanManage ? "Empty_Body" : "Empty_ReadOnlyBody"], "AutomationsEmpty",
                    state.CanManage ? GlyphButton(GlyphAdd, t["NewAutomation"], actions.New, "AutomationsEmptyNew", state.Connected, accent: true) : Empty(),
                    GlyphButton(GlyphChat, t["Review_OpenChat"], () => host.OpenChat($"agent:{state.DefaultAgentId}:main"), "AutomationsEmptyChat")),
                Secondary(t["Review_EmptyWebCaption"], 13).TextAlignment(TextAlignment.Center).HAlign(HorizontalAlignment.Center));
        else
            content = VStack(32,
                RenderAgenda(host, state, expandedGroups, ToggleGroup, Reveal, look, !twoColumns),
                RenderInventory(host, state, search, OnSearch, actions, look, tier, twoColumns, RegisterRow,
                    expandedJobs, ToggleJob, detailSnapshots.Current, visitedJobs.Current));

        Element dialog = Empty().WithKey("dialog-empty");
        // ContentDialog is side-mounted. Native setters target the modal rather than its tree placeholder.
        if (editor is { } currentEditor)
        {
            var nonce = currentEditor.Nonce;
            dialog = (ContentDialog("", Component<AutomationEditor, AutomationEditorProps>(
                new(host, currentEditor, state.DefaultAgentId, state.CanManage && state.Connected,
                    () => editorRef.Current is { } active && active.Nonce == nonce ? active : null,
                    change => UpdateEditor(nonce, change), id => CloseEditor(nonce, id))
                { Narrow = narrow })
                .Height(EditorHeight).WithKey($"editor-{nonce}"), "") with
            {
                IsOpen = true,
                OnClosed = _ => CloseEditor(nonce, null),
            }).Set(native =>
            {
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(native, "AutomationEditorDialog");
                ConfigureDialogWidth(native, dialogWidth);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(native, currentEditor.Mode == EditorMode.Edit
                    ? t.Format("Editor_EditTitle", currentEditor.Source?.Title ?? currentEditor.Form.Name)
                    : currentEditor.Mode == EditorMode.Clone ? t.Format("Review_CopyAsNewTitle", currentEditor.Source?.Title ?? currentEditor.Form.Name)
                    : t["NewAutomation"]);
            }).WithKey($"editor-dialog-{nonce}");
        }
        else if (confirm is { } pending)
            dialog = (ContentDialog("", RenderConfirmation(pending, dialogWidth), pending.Primary) with
            {
                IsOpen = true,
                CloseButtonText = t["Cancel"],
                DefaultButton = ContentDialogButton.None,
                OnOpened = () => { if (confirmDialogRef.Current is { } native) FocusConfirmationCancel(native); },
                OnClosed = result => { confirmDialogRef.Current = null; pending.Result.TrySetResult(result == ContentDialogResult.Primary); setConfirm(null); },
            }).Set(native =>
            {
                confirmDialogRef.Current = native;
                ConfigureDialogWidth(native, dialogWidth);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(native, "AutomationsConfirmDialog");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(native, pending.Title);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(native, pending.Body);
                native.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
                if (pending.Destructive)
                {
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetLocalizedControlType(native, t["Popup_AlertType"]);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(native, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
                }
            })
                .WithKey(pending.Id);

        var hero = RenderHeader(host, state, actions, look, tier);
        var banner = RenderBanners(host, state);
        var headerContent = state.Connected
            ? VStack(12, hero, RenderSummaryTiles(host, state, look, hostSize.Page, Reveal))
            : VStack(12, hero, banner);
        var body = state.Connected
            ? VStack(24, banner, content)
            : VStack(24, content);
        // The page marker belongs to the named native scroll surface, not the decorative
        // icon Border (which has no automation peer). It remains exposed in every page state.
        return Grid([GridSize.Star()], [GridSize.Star()],
            ScrollView(VStack(24,
                PageHero.WithMotion(headerContent, look.ReduceMotion).Width(contentWidth).HAlign(HorizontalAlignment.Center)
                    .Padding(0, 24, 0, 0).WithKey("page-header"),
                PageHero.WithMotion(body, look.ReduceMotion).Width(contentWidth).HAlign(HorizontalAlignment.Center)
                    .Margin(PageResponsiveLayout.HorizontalPadding(tier), 0, PageResponsiveLayout.HorizontalPadding(tier), 24))
                .Width(hostSize.Page).HAlign(HorizontalAlignment.Stretch))
                .Margin(0, topInset, 0, 0)
                .AutomationName(t["Title"]).AutomationId("CronPageMarker").Ref(scrollRef).WithKey("page-scroll"),
            VStack(8, toasts.Select((notice, i) => (Element)Component<AutomationToast, AutomationToastProps>(
                    new(notice, t, i == toasts.Length - 1, DismissToast)).WithKey($"toast-{notice.Id}")).ToArray())
                .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Bottom).MaxWidth(ToastMaxWidth)
                .Margin(20).Set(native => native.RequestedTheme = host.ActualTheme()).WithKey("toasts"),
            dialog,
            announce.Region.LiveRegion(state.Notice?.IsError == true
                    ? Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive
                    : Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite)
                .AutomationId("AutomationAnnouncements").WithKey("announcements"))
            .OnSizeChanged((sender, args) =>
            {
                if (args.NewSize.Width > 0)
                    setHostSize((args.NewSize.Width, ((FrameworkElement)sender).XamlRoot?.Size.Width ?? args.NewSize.Width));
            })
            .AutomationId("AutomationsRoot").Ref(pageRef);
    }

    private static void ConfigureDialogWidth(ContentDialog dialog, double width)
    {
        // Constrain the dialog chrome, not ContentDialog's full-window popup host.
        dialog.Resources["ContentDialogMaxWidth"] = width;
        dialog.Resources["ContentDialogMinWidth"] = width;
    }

    private static Element RenderConfirmation(ConfirmRequest request, double width) =>
        VStack(16,
            DialogHeader(request.Glyph, request.Title, request.Destructive, "AutomationConfirmationTitle"),
            Secondary(request.Body, 14).AutomationId("AutomationConfirmationBody"),
            request.Facts.Count == 0 ? Empty() : Border(VStack(8, request.Facts.Select((fact, i) =>
                Fact(fact.Label, fact.Value, $"AutomationConfirmationFact:{i}").WithKey($"fact-{i}")).ToArray()))
                .Padding(12).CornerRadius(6).Background(Theme.SubtleFill))
            .MaxWidth(420);

    private static void FocusConfirmationCancel(ContentDialog sender) =>
        sender.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => FindCloseButton(sender, sender.CloseButtonText)?.Focus(FocusState.Programmatic));

    private static Button? FindCloseButton(DependencyObject parent, string label)
    {
        if (parent is Button button && (button.Name == "CloseButton" || string.Equals(button.Content as string, label, StringComparison.Ordinal)))
            return button;
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindCloseButton(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i), label) is { } close)
                return close;
        return null;
    }

    private static Element RenderHeader(AutomationsHost host, AutomationsSnapshot state, AutomationsActions actions, AutomationsLook look, PageWidthTier tier)
    {
        var t = host.Text;
        var all = state.OverviewJobs;
        var attention = all.Count(job => job.IsFailing || job.IsAutoDisabled);
        var next = all.Where(j => j.Enabled && !j.IsAutoDisabled && j.State.NextRunAtMs is not null)
            .OrderBy(j => j.State.NextRunAtMs).FirstOrDefault();
        var answer = !state.Connected && state.LastSeenAtMs is { } seen
            ? t.Format("Redesign_LastSeen", t.DateTime(seen))
            : !state.Connected ? t["Disconnected_BannerBody"]
            : !state.OverviewLoaded ? t["Redesign_SchedulerUnknown"]
            : state.Status?.Enabled == false ? t["SchedulerOff_Title"]
            : all.Count == 0 ? t["Review_NothingScheduled"]
            : next?.State.NextRunAtMs is { } at ? t.Format("Redesign_Next", next.Title, t.NextUpTime(at))
            : attention == 1 ? t["Redesign_NeedYouOne"]
            : attention > 1 ? t.Format("Redesign_NeedYou", attention)
            : t["Redesign_OnSchedule"];
        var agentCount = all.Select(j => j.Owner ?? state.DefaultAgentId).Distinct().Count();
        var detail = state.OverviewLoaded
            ? t.InventorySummary(
                all.Count(job => job.Enabled && !job.IsAutoDisabled),
                all.Count(job => !job.Enabled || job.IsAutoDisabled),
                agentCount)
            : t.AgentScope(state.AgentFilter);
        var detailLine = string.IsNullOrWhiteSpace(detail)
            ? null
            : (Element)Secondary(detail, 14).AutomationId("AutomationsAgentScope");
        var commands = HStack(8,
                    tier != PageWidthTier.Wide ? Empty() : GlyphButton(GlyphOpenInBrowser, t["OpenInWeb"], () => host.OpenWeb(null), "AutomationsOpenWeb", state.Connected),
                    state.CanManage && !state.ReadDenied ? GlyphButton(GlyphAdd, t["NewAutomation"], actions.New, "AutomationsNew", accent: true) : Empty(),
                    IconButton(GlyphRefresh, t["Refresh"], host.Store.Refresh, "AutomationsRefresh", state.Connected),
                    tier != PageWidthTier.Wide ? Button(Glyph(GlyphMore), () => { }).AutomationId("AutomationsHeaderMore")
                        .AutomationName(t.Format("MoreActionsFor", t["Title"])).ToolTip(t.Format("MoreActionsFor", t["Title"]))
                        .WithKey("AutomationsHeaderMore")
                        .WithFlyout(MenuItems(MenuItem(t["OpenInWeb"], () => host.OpenWeb(null))))
                        .Set(native =>
                        {
                            if (native.Flyout is not MenuFlyout menu) return;
                            var label = t.Format("MoreActionsFor", t["Title"]);
                            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(menu, label);
                            menu.MenuFlyoutPresenterStyle = new Style(typeof(MenuFlyoutPresenter))
                            {
                                Setters = { new Setter(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, label) },
                            };
                            foreach (var item in menu.Items.OfType<MenuFlyoutItem>()) item.IsEnabled = state.Connected;
                        }) : Empty())
                .HAlign(HorizontalAlignment.Left).AutomationId("AutomationsHeaderActions");
        return PageHero.Header("AutomationsMast", GlyphCalendar, t["Title"], "AutomationsHeadline",
            TextBlock(answer).FontSize(14).Foreground(Theme.SecondaryText)
                .TextWrapping(TextWrapping.WrapWholeWords).MaxLines(2).AutomationId("AutomationsHeroAnswer"),
            detailLine, commands, tier == PageWidthTier.Narrow, look.ReduceMotion, look.HighContrast,
            pageTag: "cron");
    }

    private static Element RenderSummaryTiles(AutomationsHost host, AutomationsSnapshot state, AutomationsLook look,
        double pageWidth, Action<string> reveal)
    {
        var t = host.Text;
        var jobs = state.OverviewJobs;
        var known = state.OverviewLoaded;
        var running = known ? jobs.Count(job => job.IsRunning || state.IsRunning(job.Id)) : (int?)null;
        var failing = known ? jobs.Where(job => job.IsFailing || job.IsAutoDisabled).ToArray() : [];
        var firstFailure = failing.FirstOrDefault();
        string Count(int? value) => value?.ToString("N0", CultureInfo.CurrentCulture) ?? AutomationsText.Unavailable;
        var failingName = $"{t["State_Failing"]}: {Count(known ? failing.Length : null)}";
        var schedulerTone = state.Status?.Enabled switch
        {
            true => PageHeroTone.Success,
            false => PageHeroTone.Critical,
            _ => PageHeroTone.Neutral,
        };
        var tiles = new[]
        {
            new PageHeroTile(t["Redesign_Scheduler"], state.Status?.Enabled switch
                {
                    true => t["Redesign_On"],
                    false => t["Scheduler_Off"],
                    _ => t["Redesign_SchedulerUnknown"],
                }, GlyphCalendar, "AutomationsTileScheduler", schedulerTone, StatusDot: true),
            new PageHeroTile(t["Review_RunningNow"], Count(running), GlyphPlay, "AutomationsTileRunning",
                running is > 0 ? PageHeroTone.Accent : PageHeroTone.Neutral),
            new PageHeroTile(t["State_Failing"], Count(known ? failing.Length : null), GlyphError,
                "AutomationsTileFailing", PageHeroTone.Critical,
                OnInvoked: () => { if (firstFailure is not null) reveal(firstFailure.Id); },
                AutomationName: failingName, IsEnabled: firstFailure is not null),
            new PageHeroTile(t["Redesign_Off"], Count(known ? state.PausedCount : null), GlyphPause,
                "AutomationsTilePaused", PageHeroTone.Neutral),
        };
        return PageHero.SummaryTiles("AutomationsSummaryTiles", t["Title"], tiles, pageWidth,
            look.ReduceMotion, look.HighContrast);
    }

    private static Element RenderBanners(AutomationsHost host, AutomationsSnapshot state)
    {
        var t = host.Text;
        var banners = new List<Element>();
        if (!state.Connected)
            banners.Add((InfoBar(t["Disconnected_BannerTitle"], t["Disconnected_BannerBody"]) with
            {
                Severity = InfoBarSeverity.Warning,
                IsClosable = false,
                ActionButtonContent = t["OpenConnection"],
                OnActionButtonClick = host.OpenConnection,
            }).AutomationId("AutomationsOpenConnection"));
        if (state.Connected && !state.CanManage && !state.ReadDenied)
            banners.Add((InfoBar(null, t["ReadOnly"]) with { IsClosable = false }).AutomationId("AutomationsReadOnlyBanner"));
        if (state.Connected && state.Status?.Enabled == false)
            banners.Add((InfoBar(t["SchedulerOff_Title"], t["SchedulerOff_Body"]) with
            { Severity = InfoBarSeverity.Error, IsClosable = false }).AutomationId("AutomationsSchedulerOffBanner"));
        var error = state.ListLoaded ? state.ListError ?? state.OverviewError ?? state.StatusError : null;
        if (error is not null && !state.ReadDenied)
            banners.Add((InfoBar(t["Redesign_RefreshFailed"], error) with
            {
                Severity = InfoBarSeverity.Warning,
                IsClosable = false,
                ActionButtonContent = t["Retry"],
                OnActionButtonClick = host.Store.Refresh,
            }).AutomationId("AutomationsRefreshWarning"));
        return banners.Count == 0 ? Empty() : VStack(8, banners.ToArray());
    }

    private static Element Heading(AutomationsText t, string key, int? count) =>
        HStack(12, TextBlock(t[key]).FontSize(20).FontWeight(FontWeights.SemiBold),
            count is { } known ? CountPill(known) : Empty());

    private static Element CountPill(int count) =>
        Border(TextBlock(count.ToString()).FontSize(12).FontWeight(FontWeights.SemiBold).TextWrapping(TextWrapping.NoWrap)
                .Foreground(Theme.SecondaryText).Center())
            .Padding(8, 2).CornerRadius(10).VAlign(VerticalAlignment.Center)
            .Background(Theme.SystemNeutralBackground);

    private static Element GroupHeading(AutomationsText text, string key, int count) =>
        HStack(8, TextBlock(text[key]).FontSize(14).FontWeight(FontWeights.SemiBold), CountPill(count));

    private static Element RenderAgenda(AutomationsHost host, AutomationsSnapshot state, ImmutableHashSet<string> expanded, Action<string> toggle, Action<string> reveal, AutomationsLook look, bool narrow)
        => Component<AutomationAgenda, AutomationAgendaProps>(new(host, state, expanded, toggle, reveal, look));

    private sealed record AutomationAgendaProps(AutomationsHost Host, AutomationsSnapshot State,
        ImmutableHashSet<string> Expanded, Action<string> Toggle, Action<string> Reveal, AutomationsLook Look);

    private sealed class AutomationAgenda : Component<AutomationAgendaProps>
    {
        public override Element Render()
        {
            var (width, setWidth) = UseState(0.0);
            var p = Props;
            return Border(RenderAgendaBody(p.Host, p.State, p.Expanded, p.Toggle, p.Reveal, p.Look, width))
                .OnSizeChanged((_, args) => { if (args.NewSize.Width > 0) setWidth(args.NewSize.Width); });
        }
    }

    private static Element RenderAgendaBody(AutomationsHost host, AutomationsSnapshot state, ImmutableHashSet<string> expanded,
        Action<string> toggle, Action<string> reveal, AutomationsLook look, double width)
    {
        var t = host.Text;
        if (!state.OverviewLoaded) return Empty();
        var asOf = !state.Connected && state.LastSeenAtMs is { } lastSeen
            ? DateTimeOffset.FromUnixTimeMilliseconds(lastSeen) : DateTimeOffset.Now;
        var agenda = CronAgenda.Create(state.OverviewJobs, asOf, TimeZoneInfo.Local);
        if (agenda.Groups.Sum(g => g.Jobs.Count) == 1 && agenda.Repeating.Count == 0
            && agenda.WaitingForEvents.Count == 0 && agenda.EarlierToday.Count == 0)
        {
            var next = agenda.Groups.SelectMany(g => g.Jobs).Single();
            return Surface(VStack(12,
                !state.Connected && state.LastSeenAtMs is { } singleJobSynced
                    ? TextBlock(t.Format("Designer_ScheduleAsOf", t.Clock(singleJobSynced))).FontSize(20).FontWeight(FontWeights.SemiBold)
                    : Empty(),
                HStack(12, Glyph(GlyphClock, 24, Theme.AccentText),
                    VStack(4, Secondary(next.IsRunning ? t["State_Running"] : t["NextRun"]),
                        TextBlock(next.Title).FontSize(20).FontWeight(FontWeights.SemiBold),
                        Secondary(next.State.NextRunAtMs is { } at && !next.IsRunning
                            ? $"{t.FriendlyDateTime(at)} · {t.Schedule(next.Schedule)}" : t.Schedule(next.Schedule), 13))),
                Button(t["Redesign_SeeRuns"], () => reveal(next.Id)).IsEnabled(state.Connected).AutomationName(t["Redesign_SeeRuns"])
                    .HAlign(HorizontalAlignment.Left).AutomationId($"AutomationAgenda:{next.Id}"),
                Empty()))
                .AutomationId("AutomationsAgenda");
        }
        var rows = new List<Element>();
        var compact = PageResponsiveLayout.Snap(width) < PageResponsiveLayout.WideMin;
        var perGroup = compact ? 3 : 4;
        var budget = compact ? 4 : 6;
        foreach (var group in agenda.Groups)
        {
            var count = expanded.Contains(group.Key) ? group.Jobs.Count : Math.Min(group.Jobs.Count, Math.Min(perGroup, budget));
            if (group.Jobs.Count == 1) count = 1;
            budget = Math.Max(0, budget - count);
            var groupLabel = group.Key == "Running" ? "Review_RunningNow" : "Redesign_Group_" + group.Key;
            rows.Add(GroupHeading(t, groupLabel, group.Jobs.Count).Margin(0, 12, 0, 4)
                .AutomationId($"AutomationAgendaGroup:{group.Key}"));
            string? previous = null;
            foreach (var job in group.Jobs.Take(count))
            {
                var clock = group.Key == "Running" ? t["State_Running"] : t.Clock(job.State.NextRunAtMs!.Value);
                var time = previous == clock ? "┊" : clock;
                previous = clock;
                rows.Add(Button(Grid([GridSize.Px(100), GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    (group.Key == "Running" ? (Element)HStack(4, BreathingDot(Theme.Accent, !look.ReduceMotion, 6), Secondary(clock, 13))
                        : Secondary(time, 13)).VAlign(VerticalAlignment.Center).Grid(column: 0),
                    AgentAvatar(job.Owner ?? state.DefaultAgentId).Margin(0, 0, 12, 0).Grid(column: 1),
                    VStack(2, SingleLine(job.Title, 14).FontWeight(FontWeights.SemiBold),
                        SingleLine($"{job.Owner ?? state.DefaultAgentId} · {t.Schedule(job.Schedule)}").Foreground(Theme.SecondaryText)).Grid(column: 2),
                    job.IsFailing ? Secondary(t["Redesign_FailedLast"]).Foreground(Theme.SystemCritical).Grid(column: 3) : Empty().Grid(column: 3)),
                    () => reveal(job.Id)).IsEnabled(state.Connected)
                    .HorizontalContentAlignment(HorizontalAlignment.Stretch).HAlign(HorizontalAlignment.Stretch)
                    .ApplyStyle("SubtleButtonStyle")
                    .AutomationName($"{job.Title}. {clock}").AutomationId($"AutomationAgenda:{job.Id}").WithKey("agenda-" + job.Id));
            }
            if (count < group.Jobs.Count || expanded.Contains(group.Key))
                rows.Add(HyperlinkButton(expanded.Contains(group.Key) ? t["Designer_ShowFewer"]
                        : count == 0 ? t.Format("Designer_ShowAll", group.Jobs.Count) : t["Designer_ShowMore"],
                    null, () => toggle(group.Key))
                    .ItemStatus(t[expanded.Contains(group.Key) ? "Designer_Expanded" : "Designer_Collapsed"])
                    .AutomationId($"AutomationAgendaMore:{group.Key}"));
        }
        if (agenda.Groups.Count == 0) rows.Add(Secondary(t["Redesign_NothingSoon"], 14));
        if (agenda.Repeating.Count > 0)
            rows.Add(Secondary(agenda.Repeating.Count <= 2
                ? string.Join("; ", agenda.Repeating.Select(j => $"{j.Title}: {t.Schedule(j.Schedule)}"))
                : t.Format("Redesign_Repeating", agenda.Repeating.Count), 13).Margin(0, 8, 0, 0));
        if (agenda.WaitingForEvents.Count > 0)
            rows.Add(Secondary(t.Format("Redesign_WaitEvents", agenda.WaitingForEvents.Count), 13));
        var earlierRows = new List<Element>();
        var recentCount = expanded.Contains("Earlier") ? agenda.EarlierToday.Count : Math.Min(compact ? 3 : 5, agenda.EarlierToday.Count);
        if (recentCount > 0)
        {
            earlierRows.Add(GroupHeading(t, "Redesign_Earlier", agenda.EarlierToday.Count).Margin(0, 0, 0, 8)
                .AutomationId("AutomationEarlierHeading"));
            foreach (var job in agenda.EarlierToday.Take(recentCount))
            {
                var (brush, glyph) = RunTone(job.State.LastRunStatus);
                earlierRows.Add(Button(VStack(4,
                    HStack(8, Glyph(glyph, 14, brush), TextBlock(job.Title).TextWrapping(TextWrapping.Wrap)),
                    Secondary(t.Clock(job.State.LastRunAtMs!.Value)).Margin(22, 0, 0, 0)),
                    () => reveal(job.Id)).IsEnabled(state.Connected).ApplyStyle("SubtleButtonStyle")
                    .HAlign(HorizontalAlignment.Stretch).HorizontalContentAlignment(HorizontalAlignment.Left)
                    .AutomationName($"{job.Title}. {t.RunStatus(job.State.LastRunStatus)}").AutomationId($"AutomationEarlier:{job.Id}").WithKey(job.Id));
            }
            if (recentCount < agenda.EarlierToday.Count || expanded.Contains("Earlier"))
                earlierRows.Add(HyperlinkButton(t[expanded.Contains("Earlier") ? "Designer_ShowFewer" : "Designer_ShowMore"], null, () => toggle("Earlier"))
                    .ItemStatus(t[expanded.Contains("Earlier") ? "Designer_Expanded" : "Designer_Collapsed"])
                    .AutomationId("AutomationAgendaMore:Earlier"));
        }
        var health = !state.Connected ? t["Redesign_SchedulerUnknown"] : state.Status?.Enabled switch
        { true => t["Review_SchedulerOn"], false => t["Scheduler_Off"], _ => t["Unknown"] };
        var list = (ListView(null, null, rows.ToArray()) with { SelectionMode = ListViewSelectionMode.None })
            .Set(native =>
            {
                native.Padding = new Thickness(0);
                native.ItemContainerStyle = new Style(typeof(ListViewItem))
                {
                    Setters = { new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
                        new Setter(Control.PaddingProperty, new Thickness(0)) },
                };
            })
            .AutomationId("AutomationsAgendaList");
        Element agendaBody = earlierRows.Count == 0 ? list : PageResponsiveLayout.Snap(width) < AgendaColumnsMinWidth
            ? VStack(PageResponsiveLayout.GapStack, list, VStack(8, earlierRows.ToArray())).AutomationId("AutomationsAgendaBody")
            : Grid([GridSize.Star(), GridSize.Star()], [GridSize.Auto],
                list.MinWidth(PageResponsiveLayout.TwoColumnMinColumnWidth).Margin(0, 0, 8, 0).Grid(column: 0),
                VStack(8, earlierRows.ToArray()).MinWidth(PageResponsiveLayout.TwoColumnMinColumnWidth).Margin(8, 0, 0, 0).Grid(column: 1))
                .AutomationId("AutomationsAgendaBody");
        return Surface(VStack(12,
            Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                TextBlock(!state.Connected && state.LastSeenAtMs is { } synced ? t.Format("Designer_ScheduleAsOf", t.Clock(synced))
                    : t[agenda.Groups.Sum(g => g.Jobs.Count) == 1 && agenda.Repeating.Count == 0 ? "NextRun" : "Redesign_ComingUp"])
                    .FontSize(20).FontWeight(FontWeights.SemiBold).Grid(column: 0),
                HStack(6, StatusDot(state.Connected && state.Status?.Enabled == true ? Theme.SystemSuccess
                        : state.Connected && state.Status?.Enabled == false ? Theme.SystemCritical : Theme.SecondaryText, 8),
                    Secondary(health, 13)).VAlign(VerticalAlignment.Center).Grid(column: 1)),
            agendaBody))
            .AutomationId("AutomationsAgenda");
    }

    private static Element RenderAgentFilter(AutomationsHost host, AutomationsSnapshot state)
    {
        var t = host.Text;
        var agents = state.FilterAgents(host.KnownAgents());
        var labels = new[] { t["Filter_AllAgents"] }.Concat(agents).ToArray();
        var agentIndex = state.AgentFilter is null ? 0 : Array.IndexOf(agents.ToArray(), state.AgentFilter) + 1;
        return ComboBox(labels, agentIndex, i => { if (i >= 0) host.Store.SetAgentFilter(i == 0 ? null : agents[i - 1]); })
            .MinWidth(140).MaxWidth(240).HAlign(HorizontalAlignment.Left)
            .VAlign(VerticalAlignment.Center)
            .AutomationName(t.Format("AgentFilterFor", t.AgentScope(state.AgentFilter)))
            .AutomationId("AutomationsAgentFilter");
    }

    private static Element RenderInventory(AutomationsHost host, AutomationsSnapshot state, string search, Action<string> onSearch,
        AutomationsActions actions, AutomationsLook look, PageWidthTier tier, bool twoColumns, Action<FrameworkElement, string> registerRow,
        ImmutableHashSet<string> expanded, Action<string, bool> toggle, IReadOnlyDictionary<string, AutomationsSnapshot> detailSnapshots,
        HashSet<string> visited)
    {
        var t = host.Text;
        var store = host.Store;
        var narrow = tier == PageWidthTier.Narrow;
        var status = SelectorBar([
                    SelectorBarItem(t.CountLabel("Redesign_All", state.InventoryTotal)),
                    SelectorBarItem(t.CountLabel("Redesign_On", state.ActiveCount)),
                    SelectorBarItem(t.CountLabel("Redesign_Off", state.PausedCount))],
                    state.ListQuery.Enabled switch { CronEnabledFilter.Enabled => 1, CronEnabledFilter.Disabled => 2, _ => 0 },
                    index => store.SetQuery(state.ListQuery with { Enabled = index switch { 1 => CronEnabledFilter.Enabled, 2 => CronEnabledFilter.Disabled, _ => CronEnabledFilter.All } }))
                    .AutomationId("AutomationsStatusFilter");
        var agent = RenderAgentFilter(host, state);
        var searchBox = (AutoSuggestBox(search, onSearch, onSearch) with { PlaceholderText = t["Search_Placeholder"], QueryIcon = FontIcon(GlyphSearch) })
                        .MinWidth(240).MaxWidth(tier == PageWidthTier.Wide ? 360 : double.PositiveInfinity)
                        .HAlign(tier == PageWidthTier.Wide ? HorizontalAlignment.Right : HorizontalAlignment.Stretch)
                        .VAlign(VerticalAlignment.Center)
                        .AutomationName(t["Search_Placeholder"]).AutomationId("AutomationsSearch");
        var filters = (state.ScopedTotal ?? state.Total) > 5 || state.IsFiltered || state.IsAllAgents
            ? tier == PageWidthTier.Wide
                ? (Element)Grid([GridSize.Auto, GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                    status.Grid(column: 0), agent.Margin(8, 0, 8, 0).Grid(column: 1), searchBox.Grid(column: 2))
                : narrow
                    ? Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Auto],
                        status.Grid(row: 0), agent.Margin(0, 12, 0, 0).Grid(row: 1), searchBox.Margin(0, 12, 0, 0).Grid(row: 2))
                    : Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto, GridSize.Auto],
                        status.Grid(column: 0), agent.Margin(8, 0, 0, 0).Grid(column: 1),
                        searchBox.Margin(0, 12, 0, 0).Grid(column: 0, row: 1, columnSpan: 2))
            : VStack(0, agent);
        var rows = new List<Element>();
        foreach (var enabled in new[] { true, false })
        {
            var jobs = state.Jobs.Where(j => (j.Enabled && !j.IsAutoDisabled) == enabled).ToArray();
            if (jobs.Length == 0) continue;
            rows.Add(Heading(t, enabled ? "Redesign_Scheduled" : "Redesign_Off", state.HasMore ? null : jobs.Length).Margin(0, 12, 0, 8));
            // Each automation uses the stock Expander surface with its row action beside it.
            rows.Add(VStack(PageResponsiveLayout.GapInner, jobs.Select(job =>
            {
                var open = expanded.Contains(job.Id);
                if (open) visited.Add(job.Id);
                var detailsState = state.SelectedJobId == job.Id ? state
                    : detailSnapshots.TryGetValue(job.Id, out var cached) ? cached with
                    {
                        Connected = state.Connected, CanManage = state.CanManage, LastSeenAtMs = state.LastSeenAtMs,
                        LastKnownCanManage = state.LastKnownCanManage,
                        BusyJobIds = state.BusyJobIds, RunningJobIds = state.RunningJobIds,
                    } : state with { RunsLoaded = false, RunsError = null, SelectedError = null };
                var exact = detailsState.SelectedJobId == job.Id ? detailsState.SelectedJob ?? job : job;
                var running = job.IsRunning;
                var queued = !running && state.IsRunning(job.Id);
                var accessibleState = queued ? t["Toast_RunQueued"] : t.JobState(job, running);
                var attentionState = job.IsAutoDisabled ? t["Scheduler_Off"] : job.IsFailing ? t["State_Failing"] : null;
                var expander = Expander(job.Title,
                    visited.Contains(job.Id) ? Component<AutomationDetail, AutomationDetailProps>(new(host, detailsState, exact, actions) { Narrow = !twoColumns })
                        .WithKey("detail-" + job.Id) : Empty(), open, value => toggle(job.Id, value))
                    .HAlign(HorizontalAlignment.Stretch)
                    .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                    .AutomationName($"{job.Title}. {t.AgentScope(job.Owner)}. {accessibleState}. "
                        + (attentionState is null ? "" : $"{attentionState}. {job.State.LastError}. ")
                        + t.Schedule(job.Schedule))
                    .ItemStatus(state.SelectedJobId == job.Id ? t["Selected"] : accessibleState)
                    .AutomationId($"AutomationExpander:{job.Id}");
                var chatSession = state.RunsQuery.JobId == job.Id
                    ? state.Runs.FirstOrDefault(run => !string.IsNullOrWhiteSpace(run.SessionKey))?.SessionKey ?? job.SessionKey
                    : job.SessionKey;
                return Component<AutomationInventoryRow, AutomationInventoryRowProps>(new(expander,
                    RenderRowHeader(host, state, job, narrow, tier == PageWidthTier.Wide, look),
                    string.IsNullOrWhiteSpace(chatSession) ? null
                        : ChatButton(host, chatSession!, job.Title, $"AutomationRowChat:{job.Id}"),
                    job.Id, registerRow)).WithKey("row-" + job.Id);
            }).ToArray()).AutomationId(enabled ? "AutomationsScheduledList" : "AutomationsPausedList"));
        }
        if (rows.Count == 0) rows.Add(Secondary(t["List_NoMatch"], 14).AutomationId("AutomationsListEmpty"));
        rows.Add(Secondary(t.Format("List_Count", state.Jobs.Count, (object?)state.Total ?? AutomationsText.Unavailable)).AutomationId("AutomationsListCount"));
        if (state.HasMore)
            rows.Add(Button(state.ListLoadingMore ? t["Loading"] : t["LoadMore"], () => _ = store.LoadListAsync(true))
                .AutomationName(t["LoadMore"]).IsEnabled(state.Connected && !state.ListLoadingMore).AutomationId("AutomationsLoadMore"));
        return VStack(12, Heading(t, "Redesign_AllAutomations", state.InventoryTotal), filters.AutomationId("AutomationsFilters"), VStack(8, rows.ToArray()))
            .AutomationId("AutomationsInventory");
    }

    private sealed record AutomationInventoryRowProps(ExpanderElement Expander, Element Header, Element? Chat,
        string JobId, Action<FrameworkElement, string> RegisterRow);

    /// <summary>Keeps the sibling action aligned to our measured header, independently of native expanded content.</summary>
    private sealed class AutomationInventoryRow : Component<AutomationInventoryRowProps>
    {
        private const double ChatButtonSize = 40;

        public override Element Render()
        {
            var rowRef = this.UseElementRef<Microsoft.UI.Xaml.Controls.Grid>();
            var headerRef = UseRef<FrameworkElement?>(null);
            var (headerCenter, setHeaderCenter) = UseState(0.0);
            void MeasureHeader()
            {
                if (rowRef.Current is { } row
                    && headerRef.Current is { ActualHeight: > 0, XamlRoot: { } xamlRoot } header)
                {
                    var scale = xamlRoot.RasterizationScale;
                    var center = header.TransformToVisual(row)
                        .TransformPoint(new global::Windows.Foundation.Point(0, header.ActualHeight / 2)).Y;
                    center = Math.Round(center * scale) / scale;
                    if (Math.Abs(center - headerCenter) > 0.5) setHeaderCenter(center);
                }
            }
            void HeaderArranged(object sender)
            {
                headerRef.Current = (FrameworkElement)sender;
                MeasureHeader();
            }
            var header = Props.Header.AutomationId($"AutomationRowHeader:{Props.JobId}")
                .OnSizeChanged((sender, _) => HeaderArranged(sender));
            return Grid([GridSize.Star(), GridSize.Px(ChatButtonSize + PageResponsiveLayout.GapInner)], [GridSize.Auto],
                    (Props.Expander with { HeaderTemplate = header }).Grid(column: 0),
                    Props.Chat is null ? Empty().Grid(column: 1)
                        : Props.Chat.Size(ChatButtonSize, ChatButtonSize)
                            .Margin(PageResponsiveLayout.GapInner, Math.Max(0, headerCenter - ChatButtonSize / 2), 0, 0)
                            .VAlign(VerticalAlignment.Top).Grid(column: 1))
                .Ref(rowRef)
                .OnSizeChanged((sender, args) =>
                {
                    if (args.NewSize.Height > 0 && args.NewSize.Width > 0)
                        Props.RegisterRow((FrameworkElement)sender, Props.JobId);
                    MeasureHeader();
                }).AutomationId($"AutomationCard:{Props.JobId}");
        }
    }

    private static Element RenderRowHeader(AutomationsHost host, AutomationsSnapshot state, CronJob job, bool narrow, bool wide,
        AutomationsLook look)
    {
        var t = host.Text;
        var running = job.IsRunning;
        var queued = !running && state.IsRunning(job.Id);
        var eventDriven = job.Schedule.Kind is CronScheduleKind.Event or CronScheduleKind.OnExit or CronScheduleKind.Stream;
        var when = eventDriven ? t["Review_OnEvent"]
            : job.Enabled && job.State.NextRunAtMs is { } next ? t.Clock(next) : AutomationsText.Unavailable;
        var last = queued ? t["Toast_RunQueued"] : running ? t["Review_RunningNow"]
            : job.IsAutoDisabled ? t["Redesign_AutoDisabled"] : job.IsFailing ? t["Review_Error"]
            : job.State.LastRunAtMs is { } at ? t.Format("Review_LastRun", t.Relative(at)) : t["NeverRun"];
        var (statusBrush, statusGlyph) = running || queued ? (Theme.AccentText, GlyphPlay)
            : job.IsFailing || job.IsAutoDisabled ? (Theme.SystemCritical, GlyphError)
            : job.State.LastRunAtMs is null ? (Theme.SecondaryText, GlyphClock)
            // The last run's own status picks the glyph, so a skipped or unknown run never shows as a success.
            : (job.State.LastRunStatus == "skipped" ? RunTone("skipped").Brush : Theme.SecondaryText, RunTone(job.State.LastRunStatus).Glyph);
        return Grid(narrow ? [GridSize.Auto, GridSize.Star()] : [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                narrow ? [GridSize.Auto, GridSize.Auto] : [GridSize.Auto],
                AgentAvatar(job.Owner ?? state.DefaultAgentId).Margin(0, 0, 12, 0).Grid(column: 0),
                Component<AutomationRowLabels, AutomationRowLabelsProps>(new(job.Title,
                    $"{job.Owner ?? state.DefaultAgentId} · {t.Schedule(job.Schedule)}",
                    wide && IsAdvanced(job) ? t["Redesign_Advanced"] : null, job.Id, job.IsFailing,
                    job.IsAutoDisabled, job.State.LastError,
                    job.State.AutoDisabled?.ConsecutiveErrors ?? job.State.ConsecutiveErrors, t, look.ReduceMotion)).Grid(column: 1),
                VStack(4, SingleLine(when).TextAlignment(narrow ? TextAlignment.Left : TextAlignment.Right),
                    HStack(4, Glyph(statusGlyph, 11, statusBrush), SingleLine(last).Foreground(statusBrush))
                        .HAlign(narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right))
                    .Margin(narrow ? 0 : 12, narrow ? 8 : 0, 0, 0)
                    .AutomationId($"AutomationRowMeta:{job.Id}").Grid(column: narrow ? 1 : 2, row: narrow ? 1 : 0));
    }
}

internal sealed record AutomationsActions(Action New, Action<CronJob> Edit, Action<CronJob> Duplicate);
internal sealed record ConfirmationFact(string Label, string Value);
internal sealed record ConfirmRequest(string Title, string Body, string Primary)
{
    public string Glyph { get; init; } = AutomationsVisuals.GlyphAlert;
    public bool Destructive { get; init; }
    public IReadOnlyList<ConfirmationFact> Facts { get; init; } = Array.Empty<ConfirmationFact>();
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public TaskCompletionSource<bool> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
