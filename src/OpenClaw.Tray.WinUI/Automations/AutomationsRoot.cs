using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(300);
    private const int MaxVisibleToasts = 3;

    public override Element Render()
    {
        var scrollRef = this.UseElementRef<ScrollView>();
        var pageRef = this.UseElementRef<Microsoft.UI.Xaml.Controls.Grid>();
        var announce = this.UseAnnounce();
        var confirmDialogRef = UseRef<ContentDialog?>(null);
        var measuredRows = UseRef(new Dictionary<string, WeakReference<FrameworkElement>>(StringComparer.Ordinal));
        var revealedSelection = UseRef<string?>(null);
        var (confirm, setConfirm) = UseState<ConfirmRequest?>(null, threadSafe: true);
        var host = Props.Host with
        {
            RevealElement = target => target.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!target.IsLoaded || scrollRef.Current is not { IsLoaded: true, Content: UIElement content } scroll) return;
                var top = target.TransformToVisual(content).TransformPoint(new global::Windows.Foundation.Point()).Y;
                scroll.ScrollTo(scroll.HorizontalOffset, Math.Clamp(top, 0, Math.Max(0, scroll.ScrollableHeight)),
                    new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
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
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var (search, setSearch) = UseState("");
        var searchToken = UseRef(0);
        var (expandedGroups, setExpandedGroups) = UseState(ImmutableHashSet<string>.Empty);
        var look = new AutomationsLook(UseReducedMotion(), UseHighContrast());
        var (hostSize, setHostSize) = UseState((Page: PageResponsiveLayout.WideMin, Window: PageResponsiveLayout.WideMin));
        // The viewport backdrop matches the header band's top-of-page height.
        var (bandHeight, setBandHeight) = UseState(0.0);
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
                SetEditorSession(new(mode, form, source, Interlocked.Increment(ref s_editorNonce)));
            }, operationName: "Open automation editor");
        void OnDispatcher(Action action)
        {
            if (dispatcher.HasThreadAccess) action(); else dispatcher.TryEnqueue(() => action());
        }
        void UpdateEditor(long nonce, Func<EditorSession, EditorSession> change) => OnDispatcher(() =>
        {
            if (editorRef.Current is { } current && current.Nonce == nonce) SetEditorSession(change(current));
        });
        void CloseEditor(long nonce, string? savedId) => OnDispatcher(() =>
        {
            if (editorRef.Current?.Nonce != nonce) return;
            SetEditorSession(null);
            if (savedId is not null) Reveal(savedId);
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
        void Reveal(string id)
        {
            ++searchToken.Current;
            setSearch("");
            revealedSelection.Current = null;
            if (measuredRows.Current.TryGetValue(id, out var reference) && reference.TryGetTarget(out var row) && row.IsLoaded)
                host.RevealElement?.Invoke(row);
            _ = store.RevealAsync(id);
        }
        void RegisterRow(FrameworkElement row, string id)
        {
            measuredRows.Current[id] = new(row);
            if (store.State.SelectedJobId != id || revealedSelection.Current == id) return;
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
            content = Hero(GlyphDisconnected, t["Disconnected_Title"], t["Disconnected_Body"], "AutomationsDisconnected",
                GlyphButton(GlyphPlug, t["OpenConnection"], host.OpenConnection, "AutomationsOpenConnection"));
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
                Hero(GlyphCalendar, t["Empty_Title"], t[state.CanManage ? "Empty_Body" : "Empty_ReadOnlyBody"], "AutomationsEmpty",
                    state.CanManage ? GlyphButton(GlyphAdd, t["NewAutomation"], actions.New, "AutomationsEmptyNew", state.Connected, accent: true) : Empty(),
                    GlyphButton(GlyphChat, t["Review_OpenChat"], () => host.OpenChat($"agent:{state.DefaultAgentId}:main"), "AutomationsEmptyChat")),
                Secondary(t["Review_EmptyWebCaption"], 13).TextAlignment(TextAlignment.Center).HAlign(HorizontalAlignment.Center));
        else
            content = VStack(32,
                RenderAttention(host, state, !twoColumns, Reveal),
                RenderAgenda(host, state, expandedGroups, ToggleGroup, Reveal, look, !twoColumns),
                RenderInventory(host, state, search, OnSearch, actions, look, tier, twoColumns, RegisterRow));

        Element dialog = Empty().WithKey("dialog-empty");
        // ContentDialog is side-mounted. Native setters target the modal rather than its tree placeholder.
        if (editor is { } currentEditor)
        {
            var nonce = currentEditor.Nonce;
            dialog = (ContentDialog("", Component<AutomationEditor, AutomationEditorProps>(
                new(host, currentEditor, state.DefaultAgentId, state.CanManage && state.Connected,
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
                    : t["Editor_NewTitle"]);
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

        return Grid([GridSize.Star()], [GridSize.Star()],
            PageBackdrop(bandHeight).WithKey("page-backdrop"),
            ScrollView(VStack(24,
                Grid([GridSize.Star()], [GridSize.Auto],
                    RenderHeader(host, state, actions, look, tier)
                        .Width(contentWidth).HAlign(HorizontalAlignment.Center))
                    .Width(hostSize.Page).Padding(0, 24, 0, 0)
                    .OnSizeChanged((_, args) => setBandHeight(args.NewSize.Height))
                    .WithKey("page-header-band"),
                VStack(24, RenderBanners(host, state), content)
                    .Width(contentWidth).HAlign(HorizontalAlignment.Center)
                    .Margin(PageResponsiveLayout.HorizontalPadding(tier), 0, PageResponsiveLayout.HorizontalPadding(tier), 24))
                .Width(hostSize.Page).HAlign(HorizontalAlignment.Stretch))
                .AutomationId("AutomationsList").Ref(scrollRef).WithKey("page-scroll"),
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
        var attention = all.Count(j => j.IsFailing || j.IsAutoDisabled);
        var next = all.Where(j => j.Enabled && !j.IsAutoDisabled && j.State.NextRunAtMs is not null)
            .OrderBy(j => j.State.NextRunAtMs).FirstOrDefault();
        var title = !state.Connected && state.LastSeenAtMs is { } seen ? t.Format("Redesign_LastSeen", t.DateTime(seen))
            : !state.OverviewLoaded ? t["Title"] : all.Count == 0 ? t["Review_NothingScheduled"] : state.Status?.Enabled == false ? t["SchedulerOff_Title"]
            : attention == 1 ? t["Redesign_NeedYouOne"]
            : attention > 0 ? t.Format("Redesign_NeedYou", attention) : t["Redesign_OnSchedule"];
        var agentCount = all.Select(j => j.Owner ?? state.DefaultAgentId).Distinct().Count();
        var subtitle = !state.OverviewLoaded ? t.AgentScope(state.AgentFilter)
            : string.Join(" ", next is { State.NextRunAtMs: { } at } ? t.Format("Redesign_Next", next.Title, t.Clock(at)) : "",
                t.InventorySummary(all.Count(j => j.Enabled && !j.IsAutoDisabled),
                    all.Count(j => !j.Enabled || j.IsAutoDisabled), agentCount));
        var narrow = tier == PageWidthTier.Narrow;
        var text = VStack(12,
                HStack(PageResponsiveLayout.GapInner, AppIcon(look.HighContrast, 20).VAlign(VerticalAlignment.Center),
                    TextBlock(t["Title"]).FontWeight(FontWeights.SemiBold).Foreground(Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center).AutomationId("CronPageMarker")),
                TextBlock(title).FontSize(28).FontWeight(FontWeights.SemiBold).TextWrapping(TextWrapping.WrapWholeWords)
                    .MaxLines(2).TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(title).AutomationId("AutomationsHeadline"),
                Secondary(subtitle, 14).AutomationId("AutomationsAgentScope"));
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
                .HAlign(HorizontalAlignment.Left).VAlign(VerticalAlignment.Top).AutomationId("AutomationsHeaderActions");
        return Border(Grid(narrow ? [GridSize.Star()] : [GridSize.Star(), GridSize.Auto],
            narrow ? [GridSize.Auto, GridSize.Auto] : [GridSize.Auto],
            text.Margin(0, 0, narrow ? 0 : 16, 0).Grid(column: 0),
            commands.Margin(0, narrow ? 12 : 0, 0, 0).Grid(column: narrow ? 0 : 1, row: narrow ? 1 : 0))
            .Padding(0, 8, 0, 16).AutomationId("AutomationsMastGrid")).AutomationId("AutomationsMast");
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
            count is { } known ? CountPill(known, key == "Redesign_NeedsYou") : Empty());

    private static Element CountPill(int count, bool critical = false) =>
        Border(TextBlock(count.ToString()).FontSize(12).FontWeight(FontWeights.SemiBold).TextWrapping(TextWrapping.NoWrap)
                .Foreground(critical ? Theme.SystemCritical : Theme.SecondaryText).Center())
            .Padding(8, 2).CornerRadius(10).VAlign(VerticalAlignment.Center)
            .Background(critical ? Theme.SystemCriticalBackground : Theme.SystemNeutralBackground);

    private static Element GroupHeading(AutomationsText text, string key, int count) =>
        HStack(8, TextBlock(text[key]).FontSize(14).FontWeight(FontWeights.SemiBold), CountPill(count));

    private static Element RenderAttention(AutomationsHost host, AutomationsSnapshot state, bool narrow, Action<string> reveal)
    {
        var t = host.Text;
        var jobs = state.OverviewJobs.Where(j => j.IsAutoDisabled || j.IsFailing).ToArray();
        if (jobs.Length == 0) return Empty();
        var cards = jobs.Take(jobs.Length > 3 ? 2 : 3).Select(job =>
            (Element)Border(Grid([GridSize.Px(4), GridSize.Star()], [GridSize.Auto],
                Border(Empty()).Background(Theme.SystemCritical).Grid(column: 0),
                Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                    Border(Glyph(job.IsAutoDisabled ? GlyphLock : GlyphError, 16, Theme.SystemCritical).Center())
                        .Size(DialogBadgeSize, DialogBadgeSize).CornerRadius(DialogBadgeSize / 2)
                        .Background(Theme.SystemCriticalBackground).Margin(0, 0, 12, 0).VAlign(VerticalAlignment.Top).Grid(column: 0),
                    VStack(8,
                    TextBlock(job.IsAutoDisabled ? t.Format("Review_TurnedOff", job.Title) : job.Title)
                        .FontSize(14).FontWeight(FontWeights.SemiBold).TextWrapping(TextWrapping.Wrap).AutomationId($"AutomationAttentionTitle:{job.Id}"),
                    Secondary(string.Join(" ", new[]
                    {
                        (job.State.AutoDisabled?.ConsecutiveErrors ?? job.State.ConsecutiveErrors) is { } count
                            ? t.Format("Redesign_FailureStreak", count) + "." : t["Redesign_FailedLast"] + ".",
                        job.State.LastError,
                        job.IsAutoDisabled ? t["Review_SchedulerStopped"] : null,
                    }.Where(value => !string.IsNullOrWhiteSpace(value)))).AutomationId($"AutomationAttentionBody:{job.Id}"),
                    HStack(8,
                        job.IsAutoDisabled ? Empty() : state.CanManage
                            ? GlyphButton(GlyphPlay, t["Redesign_RetryNow"], () => _ = host.Store.RunNowAsync(job.Id),
                                $"AutomationRetry:{job.Id}", state.Connected && !state.IsBusy(job.Id) && !state.IsRunning(job.Id) && !job.IsRunning, accent: true) : Empty(),
                        Button(t["Redesign_SeeRuns"], () => reveal(job.Id)).AutomationName(t["Redesign_SeeRuns"]).AutomationId($"AutomationSeeRuns:{job.Id}"),
                        job.IsAutoDisabled && state.CanManage ? Button(t["Redesign_TurnOn"], () => _ = host.Store.SetEnabledAsync(job.Id, true))
                            .AutomationName(t["Redesign_TurnOn"]).ApplyStyle("SubtleButtonStyle")
                            .IsEnabled(state.Connected && !state.IsBusy(job.Id)).WithKey($"AutomationTurnOn:{job.Id}") : Empty())).Grid(column: 1))
                    .Padding(16).Grid(column: 1)))
                .CornerRadius(CardRadius).Background(Theme.CardBackground).BorderBrush(Theme.CardStroke).BorderThickness(1)
                .AutomationId($"AutomationAttention:{job.Id}").WithKey(job.Id)).ToList();
        if (jobs.Length > 3)
            cards.Add(Surface(Button(t.Format("Redesign_MoreAttention", jobs.Length - 2),
                () => reveal(jobs[2].Id)).AutomationName(t.Format("Redesign_MoreAttention", jobs.Length - 2))));
        Element layout = narrow ? VStack(PageResponsiveLayout.GapInner, cards.ToArray()).HAlign(HorizontalAlignment.Stretch)
            : Grid([GridSize.Star(), GridSize.Star()], Enumerable.Repeat(GridSize.Auto, (cards.Count + 1) / 2).ToArray(),
                cards.Select((card, i) => card.Margin(i % 2 == 0 ? 0 : 6, 0, i % 2 == 0 ? 6 : 0, 8)
                    .Grid(column: i % 2, row: i / 2).WithKey(jobs.Length > 3 && i == 2 ? "more-attention" : jobs[i].Id))
                    .Append(Empty().Grid(column: 1)).ToArray());
        return VStack(12, Heading(t, "Redesign_NeedsYou", jobs.Length), layout.AutomationId("AutomationsAttentionLayout")).AutomationId("AutomationsAttention");
    }

    private static Element RenderAgenda(AutomationsHost host, AutomationsSnapshot state, ImmutableHashSet<string> expanded, Action<string> toggle, Action<string> reveal, AutomationsLook look, bool narrow)
    {
        var t = host.Text;
        if (!state.OverviewLoaded) return Empty();
        var agenda = CronAgenda.Create(state.OverviewJobs, DateTimeOffset.Now, TimeZoneInfo.Local);
        if (agenda.Groups.Sum(g => g.Jobs.Count) == 1 && agenda.Repeating.Count == 0
            && agenda.WaitingForEvents.Count == 0 && agenda.EarlierToday.Count == 0)
        {
            var next = agenda.Groups.SelectMany(g => g.Jobs).Single();
            return Surface(VStack(12,
                HStack(12, Glyph(GlyphClock, 24, Theme.AccentText),
                    VStack(4, Secondary(next.IsRunning ? t["Running"] : t["NextRun"]),
                        TextBlock(next.Title).FontSize(20).FontWeight(FontWeights.SemiBold),
                        Secondary(next.State.NextRunAtMs is { } at && !next.IsRunning
                            ? $"{t.FriendlyDateTime(at)} · {t.Schedule(next.Schedule)}" : t.Schedule(next.Schedule), 13))),
                Button(t["Redesign_SeeRuns"], () => reveal(next.Id)).AutomationName(t["Redesign_SeeRuns"])
                    .HAlign(HorizontalAlignment.Left).AutomationId($"AutomationAgenda:{next.Id}"),
                Empty()))
                .AutomationId("AutomationsAgenda");
        }
        var rows = new List<Element>();
        var budget = 6;
        foreach (var group in agenda.Groups)
        {
            var count = expanded.Contains(group.Key) ? group.Jobs.Count : Math.Min(group.Jobs.Count, Math.Min(4, budget));
            budget = Math.Max(0, budget - count);
            rows.Add(GroupHeading(t, "Redesign_Group_" + group.Key, group.Jobs.Count).Margin(0, 12, 0, 4)
                .AutomationId($"AutomationAgendaGroup:{group.Key}"));
            string? previous = null;
            foreach (var job in group.Jobs.Take(count))
            {
                var clock = group.Key == "Running" ? t["Running"] : t.Clock(job.State.NextRunAtMs!.Value);
                var time = previous == clock ? "┊" : clock;
                previous = clock;
                rows.Add(Button(Grid([GridSize.Px(100), GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    (group.Key == "Running" ? (Element)HStack(4, BreathingDot(Theme.Accent, !look.ReduceMotion, 6), Secondary(clock, 13))
                        : Secondary(time, 13)).VAlign(VerticalAlignment.Center).Grid(column: 0),
                    AgentAvatar(job.Owner ?? state.DefaultAgentId).Margin(0, 0, 12, 0).Grid(column: 1),
                    VStack(2, SingleLine(job.Title, 14).FontWeight(FontWeights.SemiBold),
                        SingleLine($"{job.Owner ?? state.DefaultAgentId} · {t.Schedule(job.Schedule)}").Foreground(Theme.SecondaryText)).Grid(column: 2),
                    job.IsFailing ? Secondary(t["Redesign_FailedLast"]).Foreground(Theme.SystemCritical).Grid(column: 3) : Empty().Grid(column: 3)),
                    () => reveal(job.Id))
                    .HorizontalContentAlignment(HorizontalAlignment.Stretch).HAlign(HorizontalAlignment.Stretch)
                    .BorderThickness(0).Background(Theme.Ref("SubtleFillColorTransparentBrush")).Padding(8)
                    .AutomationName($"{job.Title}. {clock}").AutomationId($"AutomationAgenda:{job.Id}").WithKey("agenda-" + job.Id));
            }
            if (count < group.Jobs.Count || expanded.Contains(group.Key) && group.Jobs.Count > 4)
                rows.Add(HyperlinkButton(count < group.Jobs.Count ? t.Format("Redesign_ShowMore", group.Jobs.Count - count) : t["Runs_ShowLess"],
                    null, () => toggle(group.Key)).AutomationId($"AutomationAgendaMore:{group.Key}"));
        }
        if (agenda.Groups.Count == 0) rows.Add(Secondary(t["Redesign_NothingSoon"], 14));
        if (agenda.Repeating.Count > 0)
            rows.Add(Secondary(agenda.Repeating.Count <= 2
                ? string.Join("; ", agenda.Repeating.Select(j => $"{j.Title}: {t.Schedule(j.Schedule)}"))
                : t.Format("Redesign_Repeating", agenda.Repeating.Count), 13).Margin(0, 8, 0, 0));
        if (agenda.WaitingForEvents.Count > 0)
            rows.Add(Secondary(t.Format("Redesign_WaitEvents", agenda.WaitingForEvents.Count), 13));
        var earlierRows = new List<Element>();
        var recentCount = expanded.Contains("Earlier") ? agenda.EarlierToday.Count : Math.Min(5, agenda.EarlierToday.Count);
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
                    () => reveal(job.Id)).BorderThickness(0).Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                    .HAlign(HorizontalAlignment.Stretch).HorizontalContentAlignment(HorizontalAlignment.Left)
                    .AutomationName($"{job.Title}. {t.RunStatus(job.State.LastRunStatus)}").AutomationId($"AutomationEarlier:{job.Id}").WithKey(job.Id));
            }
            if (recentCount < agenda.EarlierToday.Count)
                earlierRows.Add(HyperlinkButton(t.Format("Redesign_ShowMore", agenda.EarlierToday.Count - recentCount), null, () => toggle("Earlier")));
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
        Element agendaBody = earlierRows.Count == 0 ? list : narrow
            ? VStack(PageResponsiveLayout.GapStack, list, VStack(8, earlierRows.ToArray())).AutomationId("AutomationsAgendaBody")
            : Grid([GridSize.Star(), GridSize.Star()], [GridSize.Auto],
                list.MinWidth(PageResponsiveLayout.TwoColumnMinColumnWidth).Margin(0, 0, 8, 0).Grid(column: 0),
                VStack(8, earlierRows.ToArray()).MinWidth(PageResponsiveLayout.TwoColumnMinColumnWidth).Margin(8, 0, 0, 0).Grid(column: 1))
                .AutomationId("AutomationsAgendaBody");
        return Surface(VStack(12,
            Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                TextBlock(t[agenda.Groups.Sum(g => g.Jobs.Count) == 1 && agenda.Repeating.Count == 0 ? "NextRun" : "Redesign_ComingUp"])
                    .FontSize(20).FontWeight(FontWeights.SemiBold).Grid(column: 0),
                HStack(6, StatusDot(state.Connected && state.Status?.Enabled == true ? Theme.SystemSuccess
                        : state.Connected && state.Status?.Enabled == false ? Theme.SystemCritical : Theme.SecondaryText, 8),
                    Secondary(health, 13)).VAlign(VerticalAlignment.Center).Grid(column: 1)),
            agendaBody))
            .AutomationId("AutomationsAgenda");
    }

    private static Element RenderInventory(AutomationsHost host, AutomationsSnapshot state, string search, Action<string> onSearch,
        AutomationsActions actions, AutomationsLook look, PageWidthTier tier, bool twoColumns, Action<FrameworkElement, string> registerRow)
    {
        var t = host.Text;
        var store = host.Store;
        var agents = state.FilterAgents(host.KnownAgents());
        var labels = new[] { t["Filter_AllAgents"] }.Concat(agents).ToArray();
        var agentIndex = state.AgentFilter is null ? 0 : Array.IndexOf(agents.ToArray(), state.AgentFilter) + 1;
        var narrow = tier == PageWidthTier.Narrow;
        var status = SelectorBar([
                    SelectorBarItem(t.CountLabel("Redesign_All", state.InventoryTotal)),
                    SelectorBarItem(t.CountLabel("Redesign_On", state.ActiveCount)),
                    SelectorBarItem(t.CountLabel("Redesign_Off", state.PausedCount))],
                    state.ListQuery.Enabled switch { CronEnabledFilter.Enabled => 1, CronEnabledFilter.Disabled => 2, _ => 0 },
                    index => store.SetQuery(state.ListQuery with { Enabled = index switch { 1 => CronEnabledFilter.Enabled, 2 => CronEnabledFilter.Disabled, _ => CronEnabledFilter.All } }))
                    .AutomationId("AutomationsStatusFilter");
        var agent = ComboBox(labels, agentIndex, i => { if (i >= 0) store.SetAgentFilter(i == 0 ? null : agents[i - 1]); })
                        .MinWidth(140).MaxWidth(240).HAlign(HorizontalAlignment.Left)
                        .AutomationName(t.Format("AgentFilterFor", t.AgentScope(state.AgentFilter)))
                        .AutomationId("AutomationsAgentFilter");
        var searchBox = (AutoSuggestBox(search, onSearch, onSearch) with { PlaceholderText = t["Search_Placeholder"], QueryIcon = FontIcon(GlyphSearch) })
                        .MinWidth(240).MaxWidth(tier == PageWidthTier.Wide ? 360 : double.PositiveInfinity)
                        .HAlign(tier == PageWidthTier.Wide ? HorizontalAlignment.Right : HorizontalAlignment.Stretch)
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
                        searchBox.Margin(0, 12, 0, 0).Grid(row: 1).Set(native => Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(native, 2)))
            : (Element)Empty();
        var rows = new List<Element>();
        foreach (var enabled in new[] { true, false })
        {
            var jobs = state.Jobs.Where(j => (j.Enabled && !j.IsAutoDisabled) == enabled).ToArray();
            if (jobs.Length == 0) continue;
            rows.Add(Heading(t, enabled ? "Redesign_Scheduled" : "Redesign_Paused", state.HasMore ? null : jobs.Length).Margin(0, 12, 0, 8));
            // Each automation is its own card, separated by spacing step 2; expanded detail stays inside its card.
            rows.Add(VStack(PageResponsiveLayout.GapInner, jobs.Select(job =>
            {
                var selected = state.SelectedJobId == job.Id;
                var exact = selected ? state.SelectedJob ?? job : job;
                return (Element)Surface(VStack(0,
                    RenderRow(host, state, job, selected, narrow, tier == PageWidthTier.Wide),
                    selected ? Component<AutomationDetail, AutomationDetailProps>(new(host, state, exact, actions) { Narrow = !twoColumns })
                        .WithKey("detail-" + job.Id) : Empty()), 0)
                    .OnSizeChanged((sender, args) =>
                    {
                        if (args.NewSize.Height > 0 && args.NewSize.Width > 0) registerRow((FrameworkElement)sender, job.Id);
                    }).AutomationId($"AutomationCard:{job.Id}").WithKey("row-" + job.Id);
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

    private static Element RenderRow(AutomationsHost host, AutomationsSnapshot state, CronJob job, bool selected, bool narrow, bool wide)
    {
        var t = host.Text;
        var running = job.IsRunning;
        var queued = !running && state.IsRunning(job.Id);
        var accessibleState = queued ? t["Toast_RunQueued"] : t.JobState(job, running);
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
        var chatSession = state.RunsQuery.JobId == job.Id
            ? state.Runs.FirstOrDefault(run => !string.IsNullOrWhiteSpace(run.SessionKey))?.SessionKey ?? job.SessionKey
            : job.SessionKey;
        return Grid([GridSize.Star(), GridSize.Auto, GridSize.Auto], [GridSize.Auto],
            Button(Grid(narrow ? [GridSize.Auto, GridSize.Star()] : [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                narrow ? [GridSize.Auto, GridSize.Auto] : [GridSize.Auto],
                AgentAvatar(job.Owner ?? state.DefaultAgentId).Margin(0, 0, 12, 0).Grid(column: 0),
                Component<AutomationRowLabels, AutomationRowLabelsProps>(new(job.Title,
                    $"{job.Owner ?? state.DefaultAgentId} · {t.Schedule(job.Schedule)}",
                    wide && IsAdvanced(job) ? t["Redesign_Advanced"] : null, job.Id)).Grid(column: 1),
                VStack(4, SingleLine(when).TextAlignment(narrow ? TextAlignment.Left : TextAlignment.Right),
                    HStack(4, Glyph(statusGlyph, 11, statusBrush), SingleLine(last).Foreground(statusBrush))
                        .HAlign(narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right))
                    .Margin(narrow ? 0 : 12, narrow ? 8 : 0, 0, 0)
                    .AutomationId($"AutomationRowMeta:{job.Id}").Grid(column: narrow ? 1 : 2, row: narrow ? 1 : 0)),
                () => host.Store.Select(selected ? null : job.Id))
                .HorizontalContentAlignment(HorizontalAlignment.Stretch).HAlign(HorizontalAlignment.Stretch)
                .Padding(16).BorderThickness(0).Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                .AutomationName($"{job.Title}. {t.AgentScope(job.Owner)}. {accessibleState}. {t.Schedule(job.Schedule)}")
                .ItemStatus(selected ? t["Selected"] : accessibleState)
                .AutomationId($"AutomationRow:{job.Id}").Grid(column: 0),
            string.IsNullOrWhiteSpace(chatSession) ? Empty().Grid(column: 1)
                : ChatButton(host, chatSession!, job.Title, $"AutomationRowChat:{job.Id}").VAlign(VerticalAlignment.Center).Grid(column: 1),
            IconButton(selected ? "\uE70E" : "\uE70D", $"{t["Tab_Overview"]}: {job.Title}",
                () => host.Store.Select(selected ? null : job.Id), $"AutomationExpand:{job.Id}")
                .VAlign(VerticalAlignment.Center).Margin(0, 0, 12, 0).Grid(column: 2))
            .CornerRadius(CardRadius - 1, CardRadius - 1, 0, 0)
            .Background(selected ? Theme.SubtleFill : Theme.Ref("SubtleFillColorTransparentBrush"));
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
