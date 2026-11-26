// Native web-aligned floating task card. Rendering never changes task custody, session selection or composer drafts.
using System.Numerics;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Elements;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Helpers;
using Windows.ApplicationModel.DataTransfer;
using static Microsoft.UI.Reactor.Factories;
using NativeMenu = Microsoft.UI.Xaml.Controls.MenuFlyout;
using NativeMenuItem = Microsoft.UI.Xaml.Controls.MenuFlyoutItem;

namespace OpenClawTray.Chat;

internal sealed record ReactorTaskSuggestionPanelProps(ChatTaskSuggestionCoordinator Coordinator,
    string SessionKey, string? AgentId, double ViewportWidth, double ViewportHeight, double ReservedHeight = 108, Action? EnsureComposerVisible = null);

/// <summary>One active proposal in the composer gutter, with bounded native stack navigation.</summary>
internal sealed class ReactorTaskSuggestionPanel : Component<ReactorTaskSuggestionPanelProps>
{
    public override Element Render()
    {
        var props = Props;
        var (snapshot, setSnapshot) = UseState(props.Coordinator.Snapshot(), threadSafe: true);
        var (activeId, setActiveId) = UseState<string?>(null);
        var navigationFocus = UseRef<TaskNavigationFocus?>(null);
        var navigationGeneration = UseRef(0L);
        var drafts = UseRef(new TaskSuggestionPresentationCache());
        var usableMode = UseRef<bool?>(null);
        var shortageMode = UseRef<bool?>(null);
        var panelRef = this.UseElementRef<Grid>();
        var navigationScope = UseRef((props.Coordinator, props.SessionKey, props.AgentId));
        var currentScope = (props.Coordinator, props.SessionKey, props.AgentId);
        if (navigationScope.Current != currentScope)
        {
            navigationFocus.Current = null;
            drafts.Current.Clear();
            snapshot = props.Coordinator.Snapshot();
            navigationScope.Current = currentScope;
        }
        UseEffect((Func<Action>)(() =>
        {
            props.Coordinator.ObserveSession(props.SessionKey);
            EventHandler changed = (_, _) =>
            {
                if (!ReferenceEquals(Props.Coordinator, props.Coordinator)) return;
                var next = props.Coordinator.Snapshot();
                // Revision admission, retirement and state publication share one
                // lock; concurrent notifications cannot publish an older snapshot last.
                drafts.Current.Publish(next, accepted =>
                {
                    navigationFocus.Current = null;
                    setSnapshot(accepted);
                });
            };
            props.Coordinator.Changed += changed;
            drafts.Current.Publish(props.Coordinator.Snapshot(), setSnapshot);
            return () => props.Coordinator.Changed -= changed;
        }), props.Coordinator);
        // All-session membership is bounded by the coordinator. Below-floor hiding
        // does not change it, but authoritative absence and confirmed starts retire UI drafts.
        snapshot = drafts.Current.Observe(snapshot);
        var cards = snapshot.Cards.Where(card => card.Suggestion.SessionKey == props.SessionKey
            && (card.Suggestion.AgentId is null || props.AgentId is null || card.Suggestion.AgentId == props.AgentId)).ToArray();
        var identityUnavailable = snapshot.IdentityUnavailable && snapshot.IdentityNoticeSession == props.SessionKey;
        if (cards.Length == 0 && !identityUnavailable) return Empty();
        // Reserve usable native controls in short panes without exceeding the retained composer budget.
        var maximumHeight = Math.Min(ChatVisuals.TaskCardMaximumHeight,
            Math.Max(0, Math.Min(Math.Max(260, props.ViewportHeight * .42), props.ViewportHeight - props.ReservedHeight - 20)));
        // Do not leave clipped descendants reachable when there is not even a usable
        // header/action-sized viewport. State remains subscribed so space recovery remounts it.
        // Four-DIP deadbands prevent layout rounding at either threshold from
        // repeatedly detaching/reconfiguring focused native controls.
        usableMode.Current = usableMode.Current is null ? maximumHeight >= 80
            : usableMode.Current.Value ? maximumHeight >= 76 : maximumHeight >= 84;
        shortageMode.Current = shortageMode.Current is null ? maximumHeight < 260
            : shortageMode.Current.Value ? maximumHeight < 260 : maximumHeight < 256;
        if (!usableMode.Current.Value)
        {
            HandoffTaskFocus(panelRef.Current);
            return Empty();
        }
        var shortage = shortageMode.Current.Value;
        const double decorationHeight = 8;
        var contentHeight = maximumHeight - decorationHeight;
        var stacked = cards.Length > 1 && !identityUnavailable;
        Element content;
        if (identityUnavailable)
            content = Border(VStack(8,
                TextBlock(L("IdentityUnavailable", "Suggested tasks need a verified Gateway operator identity. Check your Gateway access, then retry."))
                    .Set(ChatVisuals.StyleSecondaryText).TextWrapping(TextWrapping.Wrap).LiveRegion(AutomationLiveSetting.Polite),
                HyperlinkButton(L("Refresh", "Refresh tasks"), null, () => _ = props.Coordinator.RefreshAsync())
                    .Padding(4, 2).MinHeight(32).Set(ReactorTaskSuggestionCard.KeepFocusInside).AutomationName(L("Refresh", "Refresh tasks"))
                    .HAlign(HorizontalAlignment.Left).IsEnabled(snapshot.Connected)))
                .Padding(16).CornerRadius(ChatVisuals.TaskCardRadius).BorderThickness(1).Set(ReactorTaskSuggestionCard.StyleCard);
        else
        {
            var index = Array.FindIndex(cards, card => card.Suggestion.Id == activeId);
            if (index < 0) index = 0;
            void Navigate(int direction)
            {
                var next = (index + direction + cards.Length) % cards.Length;
                navigationFocus.Current = new(cards[next].Suggestion.Id, direction < 0 ? "ChatTaskPrevious" : "ChatTaskNext",
                    ++navigationGeneration.Current, props.SessionKey);
                setActiveId(cards[next].Suggestion.Id);
            }
            var card = cards[index];
            var draft = card.StartedKey is null ? drafts.Current.Get(card.Suggestion.Id) : new TaskSuggestionPresentationState();
            var active = Component<ReactorTaskSuggestionCard, ReactorTaskSuggestionCardProps>(new(
                props.Coordinator, card, draft, snapshot.Connected && !card.Busy
                    && (card.Attempt is null || (card.CanRetry && card.AttemptEpoch == snapshot.ConnectionEpoch)),
                snapshot.CanAccept, snapshot.CanDismiss, snapshot.AdministratorRequired, snapshot.CanHideLocally,
                snapshot.CloudSupported, snapshot.RefreshFailed, shortage, shortage ? 320 : contentHeight,
                Math.Min(ChatVisuals.TaskCardWidth, props.ViewportWidth - 2 * ChatVisuals.Gutter(props.ViewportWidth)),
                index, cards.Length, () => Navigate(-1), () => Navigate(1), navigationFocus.Current, request =>
                {
                    if (!ReferenceEquals(navigationFocus.Current, request) || request.SessionKey != props.SessionKey) return false;
                    navigationFocus.Current = null;
                    return true;
                })).WithKey(card.StartedKey is null ? draft.MountKey : drafts.Current.SettledKey(card.Suggestion.Id));
            content = active;
        }
        // The native ancestry is identical on both sides of the shortage boundary.
        content = ScrollViewer(content).Height(shortage ? contentHeight : double.NaN).MinHeight(0).MaxHeight(contentHeight).IsTabStop(false)
                .Margin(0, 0, 0, decorationHeight)
                .Set(scroll => { scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled; scroll.HorizontalScrollMode = ScrollMode.Disabled; scroll.HorizontalContentAlignment = HorizontalAlignment.Stretch; scroll.VerticalScrollBarVisibility = shortage ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled; scroll.VerticalScrollMode = shortage ? ScrollMode.Enabled : ScrollMode.Disabled; scroll.VerticalContentAlignment = VerticalAlignment.Top; })
                .AutomationId("ChatTaskWholeCardViewport").OnMountAdd(ObserveShortageFocus).OnUnmountAdd(StopObservingShortageFocus);
        // Decoration is a stable non-interactive sibling, never a descendant of
        // the clipping viewport. Its eight-DIP extent is included in the budget.
        return Grid([GridSize.Star()], [GridSize.Auto],
                Border(null).Margin(0, 0, 0, decorationHeight).CornerRadius(ChatVisuals.TaskCardRadius)
                    .Set(ReactorTaskSuggestionCard.StyleCard).IsHitTestVisible(false).AccessibilityHidden()
                    .Set(border => { border.Shadow = new ThemeShadow(); border.Translation = new Vector3(0, 0, 24); })
                    .AutomationId("ChatTaskElevation"),
                Border(null).Height(18).Margin(14, 0, 14, 0).VAlign(VerticalAlignment.Bottom)
                    .CornerRadius(0, 0, ChatVisuals.TaskCardRadius, ChatVisuals.TaskCardRadius).BorderThickness(1)
                    .Set(ReactorTaskSuggestionCard.StyleCard).IsHitTestVisible(false).AccessibilityHidden().IsVisible(stacked)
                    .AutomationId("ChatTaskStack"),
                content).MaxHeight(maximumHeight)
            .Width(Math.Min(ChatVisuals.TaskCardWidth, props.ViewportWidth - 2 * ChatVisuals.Gutter(props.ViewportWidth)))
            .HAlign(HorizontalAlignment.Right)
            .Margin(ChatVisuals.Gutter(props.ViewportWidth), 8, ChatVisuals.Gutter(props.ViewportWidth), 12)
            .AutomationId("ChatTaskSuggestions").Ref(panelRef)
            .OnMountAdd(_ => props.EnsureComposerVisible?.Invoke())
            .OnSizeChanged((_, _) => Props.EnsureComposerVisible?.Invoke());
    }

    private static void HandoffTaskFocus(FrameworkElement? panel)
    {
        if (panel?.XamlRoot is not { } xamlRoot
            || Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(xamlRoot) is not DependencyObject focused) return;
        var ancestor = focused;
        while (ancestor is not null && !ReferenceEquals(ancestor, panel)) ancestor = VisualTreeHelper.GetParent(ancestor);
        if (ancestor is null) return;
        // A question can share a nearer task dock while the composer is a sibling
        // of that dock. Keep walking until THIS chat's composer is found.
        DependencyObject root = panel;
        FrameworkElement? questions = null;
        while (VisualTreeHelper.GetParent(root) is { } parent)
        {
            root = parent;
            questions ??= ChatMotion.FindByAutomationId(root, "ChatQuestionPanel") as FrameworkElement;
            if (ChatMotion.FindByAutomationId(root, "ChatComposerInput") is not null) break;
        }
        if (ChatMotion.FindByAutomationId(root, "ChatComposerInput") is Control composer && TryFocus(composer)) return;
        if (questions is not null && FocusFirst(questions)) return;
        // Result-only questions have no actionable descendants. Keep focus in a
        // retained composer host, or another retained control of the owning chat.
        if (ChatMotion.FindByAutomationId(root, "ChatComposerArea") is Control composerRoot
            && FocusFallback(composerRoot)) return;
        if (FocusFirst(root, panel)) return;
        for (DependencyObject? fallback = root; fallback is not null; fallback = VisualTreeHelper.GetParent(fallback))
            if (fallback is Control control && FocusFallback(control)) return;
    }
    private static bool FocusFallback(Control control)
    {
        if (!control.IsLoaded || !control.IsEnabled || control.ActualHeight <= 0) return false;
        for (DependencyObject? ancestor = control; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
            if (ancestor is UIElement { Visibility: Visibility.Collapsed }) return false;
        return control.Focus(FocusState.Programmatic);
    }

    private static bool TryFocus(Control control)
    {
        if (!control.IsLoaded || !control.IsEnabled || !control.IsTabStop || control.ActualHeight <= 0) return false;
        for (DependencyObject? ancestor = control; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
            if (ancestor is UIElement { Visibility: Visibility.Collapsed }) return false;
        return control.Focus(FocusState.Keyboard);
    }
    private static bool FocusFirst(DependencyObject root, DependencyObject? excluded = null)
    {
        if (ReferenceEquals(root, excluded)) return false;
        if (root is Control control && control is not Microsoft.UI.Xaml.Controls.ScrollViewer && TryFocus(control)) return true;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FocusFirst(VisualTreeHelper.GetChild(root, index), excluded)) return true;
        return false;
    }

    private sealed record ShortageFocusHandlers(RoutedEventHandler Focus, SizeChangedEventHandler Size);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, ShortageFocusHandlers> ShortageFocus = new();
    private static void ObserveShortageFocus(FrameworkElement viewport)
    {
        RoutedEventHandler handler = (_, _) =>
        {
            // SizeChanged/GotFocus can run while the old viewport is detaching.
            // WinUI rejects GetFocusedElement(null/disconnected XamlRoot).
            if (!viewport.IsLoaded || viewport.XamlRoot is not { Content: not null } root
                || Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is not FrameworkElement focused) return;
            DependencyObject? ancestor = focused;
            while (ancestor is not null && !ReferenceEquals(ancestor, viewport)) ancestor = VisualTreeHelper.GetParent(ancestor);
            if (ancestor is null) return;
            viewport.DispatcherQueue.TryEnqueue(() =>
            {
                if (!viewport.IsLoaded || !focused.IsLoaded || !ShortageFocus.TryGetValue(viewport, out _)
                    || !ReferenceEquals(viewport.XamlRoot, root) || root.Content is null
                    || !ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root), focused)) return;
                if (viewport is not Microsoft.UI.Xaml.Controls.ScrollViewer scroll || scroll.ViewportHeight <= 0) return;
                var top = focused.TransformToVisual(viewport).TransformPoint(new global::Windows.Foundation.Point()).Y;
                var bottom = top + focused.ActualHeight;
                var delta = top < 0 ? top : bottom > scroll.ViewportHeight ? bottom - scroll.ViewportHeight : 0;
                if (Math.Abs(delta) > .5)
                {
                    focused.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                    scroll.ChangeView(null, Math.Clamp(scroll.VerticalOffset + delta, 0, scroll.ScrollableHeight), null, true);
                }
            });
        };
        SizeChangedEventHandler size = (_, _) => handler(viewport, new RoutedEventArgs());
        ShortageFocus.Add(viewport, new(handler, size));
        viewport.GotFocus += handler;
        viewport.Loaded += handler;
        viewport.SizeChanged += size;
    }
    private static void StopObservingShortageFocus(FrameworkElement viewport)
    {
        if (!ShortageFocus.TryGetValue(viewport, out var handler)) return;
        viewport.GotFocus -= handler.Focus;
        viewport.Loaded -= handler.Focus;
        viewport.SizeChanged -= handler.Size;
        ShortageFocus.Remove(viewport);
    }

    internal static string L(string key, string fallback) => ChatMarkdownPresentation.Localized("Chat_Tasks_" + key, fallback);
}

// Native presentation only: no acceptance options, custody or identity adoption.
// Notifications can arrive off-thread and can coalesce before Render; observe every
// authoritative revision/epoch transition so the same ID never revives a retired draft.
internal sealed class TaskSuggestionPresentationCache
{
    private readonly Dictionary<string, TaskSuggestionPresentationState> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _settledKeys = new(StringComparer.Ordinal);
    private long? _epoch;
    private long _presentationRevision = -1;
    private ChatTaskSnapshot? _latest;
    internal void Clear()
    {
        lock (_entries) { _entries.Clear(); _settledKeys.Clear(); _latest = null; _presentationRevision = -1; }
    }
    internal void Publish(ChatTaskSnapshot snapshot, Action<ChatTaskSnapshot> publish)
    {
        lock (_entries)
        {
            if (_latest is not null && snapshot.SnapshotRevision <= _latest.SnapshotRevision) return;
            Apply(snapshot);
            publish(snapshot); // Keep admission/pruning/publication atomic across notifications.
        }
    }
    internal ChatTaskSnapshot Observe(ChatTaskSnapshot snapshot)
    {
        lock (_entries)
        {
            if (_latest is null || snapshot.SnapshotRevision > _latest.SnapshotRevision) Apply(snapshot);
            return _latest!; // A captured older hook value cannot prune or render older ownership.
        }
    }
    private void Apply(ChatTaskSnapshot snapshot)
    {
        if (_epoch != snapshot.ConnectionEpoch || _presentationRevision != snapshot.PresentationRevision)
        {
            _entries.Clear(); _settledKeys.Clear();
            _epoch = snapshot.ConnectionEpoch; _presentationRevision = snapshot.PresentationRevision;
        }
        var present = snapshot.Cards.Where(card => card.StartedKey is null)
            .Select(card => card.Suggestion.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _entries.Keys.Where(id => !present.Contains(id)).ToArray()) _entries.Remove(id);
        var settled = snapshot.Cards.Where(card => card.StartedKey is not null).Select(card => card.Suggestion.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _settledKeys.Keys.Where(id => !settled.Contains(id)).ToArray()) _settledKeys.Remove(id);
        _latest = snapshot;
    }
    internal string SettledKey(string id)
    {
        lock (_entries)
        {
            if (!_settledKeys.TryGetValue(id, out var key)) _settledKeys.Add(id, key = Guid.NewGuid().ToString("N"));
            return key;
        }
    }
    internal TaskSuggestionPresentationState Get(string id)
    {
        lock (_entries)
        {
            if (!_entries.TryGetValue(id, out var draft)) _entries.Add(id, draft = new());
            return draft;
        }
    }
}

// Unsent UI state survives below-floor presentation removal, scoped to panel/task ID.
internal sealed class TaskSuggestionPresentationState
{
    internal readonly string MountKey = Guid.NewGuid().ToString("N");
    internal bool Instructions, RepositoryOpen, CloudOpen, Copied, CopyFailed;
    internal string Cwd = "", Profile = "";
}

internal sealed record TaskNavigationFocus(string TargetTaskId, string Direction, long Generation, string SessionKey);

internal sealed record ReactorTaskSuggestionCardProps(ChatTaskSuggestionCoordinator Coordinator, ChatTaskCard Card, TaskSuggestionPresentationState Draft,
    bool Enabled, bool CanAccept, bool CanDismiss, bool AdministratorRequired, bool CanHideLocally,
    bool CloudSupported, bool RefreshFailed, bool WholeCardScroll, double MaximumHeight, double Width, int Index, int Count,
    Action Previous, Action Next, TaskNavigationFocus? NavigationFocus, Func<TaskNavigationFocus, bool> ConsumeNavigationFocus);

/// <summary>Plain native text, header actions and a Windows-owned launch menu; the coordinator owns every mutation.</summary>
internal sealed class ReactorTaskSuggestionCard : Component<ReactorTaskSuggestionCardProps>
{
    public override Element Render()
    {
        var props = Props;
        var card = props.Card;
        var task = card.Suggestion;
        var (instructions, updateInstructions) = UseState(props.Draft.Instructions);
        void setInstructions(bool value) { props.Draft.Instructions = value; updateInstructions(value); }
        var (repositoryOpen, updateRepositoryOpen) = UseState(props.Draft.RepositoryOpen);
        void setRepositoryOpen(bool value) { props.Draft.RepositoryOpen = value; updateRepositoryOpen(value); }
        var (cloudOpen, updateCloudOpen) = UseState(props.Draft.CloudOpen);
        void setCloudOpen(bool value) { props.Draft.CloudOpen = value; updateCloudOpen(value); }
        var (cwd, updateCwd) = UseState(props.Draft.Cwd);
        void setCwd(string value) { props.Draft.Cwd = value; updateCwd(value); }
        var (profile, updateProfile) = UseState(props.Draft.Profile);
        void setProfile(string value) { props.Draft.Profile = value; updateProfile(value); }
        var (copied, updateCopied) = UseState(props.Draft.Copied);
        void setCopied(bool value) { props.Draft.Copied = value; updateCopied(value); }
        var (copyFailed, updateCopyFailed) = UseState(props.Draft.CopyFailed);
        void setCopyFailed(bool value) { props.Draft.CopyFailed = value; updateCopyFailed(value); }
        var summaryObserver = UseRef<SummaryViewportObserver?>(null);
        var anchor = UseRef<Button?>(null);
        var menu = UseMemo(() =>
        {
            var value = new NativeMenu { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
            ChatVisuals.StylePicker(value);
            void Add(string mode, string label)
            {
                var item = new NativeMenuItem { Text = label };
                AutomationProperties.SetAutomationId(item, "ChatTaskMode_" + mode);
                item.Click += (_, _) =>
                {
                    var current = Props;
                    if (!current.Enabled || !current.CanAccept || current.Card.Attempt is not null) return;
                    if (mode == "cloud") { if (current.CloudSupported) setCloudOpen(true); }
                    else _ = current.Coordinator.AcceptAsync(new(current.Card.Suggestion.Id, mode));
                };
                value.Items.Add(item);
            }
            Add("local", L("Local", "Start in a new session"));
            Add("worktree", L("Worktree", "Start in a new worktree"));
            Add("session", L("Session", "Start in this session"));
            Add("cloud", L("Cloud", "Start in cloud"));
            return value;
        }, Array.Empty<object>());
        UseEffect((Func<Action>)(() => () => { menu.Hide(); anchor.Current = null; }), Array.Empty<object>());
        UseEffect(() => { if (card.CloudProfileRequired) setCloudOpen(true); }, card.CloudProfileRequired);
        foreach (var item in menu.Items)
        {
            item.IsEnabled = props.Enabled && props.CanAccept && card.Attempt is null;
            if (AutomationProperties.GetAutomationId(item) == "ChatTaskMode_cloud")
                item.Visibility = props.CloudSupported ? Visibility.Visible : Visibility.Collapsed;
        }
        if (!props.Enabled || card.Attempt is not null) menu.Hide();
        var copyName = copied ? L("Copied", "Instructions copied") : L("Copy", "Copy instructions");
        var headerActions = new List<Element>
        {
            IconButton(copied ? FluentIconCatalog.StatusOk : FluentIconCatalog.Copy, copyName, "ChatTaskCopy", () =>
            {
                try { var data = new DataPackage(); data.SetText(task.Prompt); Clipboard.SetContent(data); setCopied(true); setCopyFailed(false); }
                catch { setCopyFailed(true); }
            })
        };
        if (props.Count > 1)
        {
            headerActions.Add(IconButton(FluentIconCatalog.TaskPrevious, L("Previous", "Previous suggested task"), "ChatTaskPrevious", props.Previous)
                .OnMountAdd(control => RestoreNavigationFocus((Button)control, "ChatTaskPrevious")));
            headerActions.Add(IconButton(FluentIconCatalog.ChevronR, L("Next", "Next suggested task"), "ChatTaskNext", props.Next)
                .OnMountAdd(control => RestoreNavigationFocus((Button)control, "ChatTaskNext")));
        }
        if (props.CanDismiss && card.Attempt is null && card.StartedKey is null)
            headerActions.Add(IconButton(FluentIconCatalog.Exit, string.Format(L("DismissTitle", "Dismiss {0}"), task.Title), "ChatTaskDismiss",
                () => _ = props.Coordinator.DismissAsync(task.Id)).IsEnabled(props.Enabled));
        var repositoryName = task.Cwd.TrimEnd('\\', '/').Split('\\', '/').LastOrDefault() ?? task.Cwd;
        var eyebrow = string.Format(L("Eyebrow", "Suggested task · in {0}"), repositoryName);
        var header = Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
            HStack(6,
                TextBlock(eyebrow.ToUpper(EffectiveUiCulture())).AutomationName(eyebrow).AutomationId("ChatTaskEyebrow").ToolTip(task.Cwd)
                    .Set(text => text.Style = (Style)Application.Current.Resources["ChatTaskEyebrowStyle"])
                    .TextTrimming(TextTrimming.CharacterEllipsis).MaxWidth(Math.Max(48, props.Width - (props.Count > 1 ? 240 : 148))),
                Border(TextBlock($"{props.Index + 1} / {props.Count}").FontSize(11).Set(ChatVisuals.StyleSecondaryText))
                    .Padding(6, 2).CornerRadius(4).Background(Theme.Ref("ChatTaskPathBrush")).IsVisible(props.Count > 1).AutomationId("ChatTaskPosition"))
                .ToolTip(task.Cwd).VAlign(VerticalAlignment.Center),
            HStack(0, headerActions.ToArray()).Grid(column: 1));
        // Title stays anchored. Summary has an independent bounded scroll viewport, so even
        // multiline/large text cannot consume the disclosure, recovery viewport or action row.
        var identity = VStack(4,
            TextBlock(task.Title).Set(ChatVisuals.StyleFlyoutHeading).TextWrapping(TextWrapping.Wrap)
                .MaxLines(2).TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(task.Title).AutomationName(task.Title).AutomationId("ChatTaskTitle"),
            ScrollViewer(TextBlock(task.Tldr).Set(text => text.Style = (Style)Application.Current.Resources["ChatTaskSummaryStyle"])
                .TextWrapping(TextWrapping.Wrap).IsTextSelectionEnabled(true).AutomationId("ChatTaskSummary").Margin(0, 0, 12, 0))
                .AutomationName(L("Summary", "Suggested task summary")).AutomationId("ChatTaskSummaryViewport")
                .Set(scroll => { ConfigureSummaryViewport(scroll, props.MaximumHeight); summaryObserver.Current?.Refresh(); })
                .OnMountAdd(control =>
                {
                    var observer = new SummaryViewportObserver((Microsoft.UI.Xaml.Controls.ScrollViewer)control, () => Props.MaximumHeight);
                    SummaryObservers.Add(control, observer);
                    summaryObserver.Current = observer;
                })
                .OnUnmountAdd(control =>
                {
                    if (!SummaryObservers.TryGetValue(control, out var observer)) return;
                    observer.Dispose();
                    SummaryObservers.Remove(control);
                    if (ReferenceEquals(summaryObserver.Current, observer)) summaryObserver.Current = null;
                }));
        var instructionLabel = instructions ? L("HideInstructions", "Hide instructions") : L("Instructions", "Show instructions");
        var disclosure = ToggleButton(HStack(6, Glyph(instructions ? FluentIconCatalog.ChevronDown : FluentIconCatalog.ChevronR, 12),
                    TextBlock(instructionLabel).FontSize(12).FontWeight(Microsoft.UI.Text.FontWeights.Normal)), instructions, setInstructions)
                .AutomationId("ChatTaskInstructions").AutomationName(instructions ? L("HideInstructions", "Hide instructions") : L("Instructions", "Show instructions"))
                .Set(control => { control.Style = (Style)Application.Current.Resources["ChatTaskDisclosureStyle"]; KeepFocusInside(control); SizeDisclosure(control, instructionLabel); })
                .HAlign(HorizontalAlignment.Left).WithKey("task-instructions-toggle");
        var body = new List<Element>
        {
            Border(ScrollViewer(TextBlock(task.Cwd).FontFamily("Cascadia Mono").FontSize(11).FontWeight(Microsoft.UI.Text.FontWeights.Normal).IsTextSelectionEnabled(true).TextWrapping(TextWrapping.NoWrap)
                    .Set(ChatVisuals.StylePrimaryText).Padding(10, 7))
                .Set(scroll => { scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto; scroll.HorizontalScrollMode = ScrollMode.Enabled; scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled; }))
                .CornerRadius(9, 9, 0, 0).Background(Theme.Ref("ChatTaskPathBrush")).IsVisible(instructions).AutomationId("ChatTaskInstructionPath"),
            Border(ScrollViewer(TextBlock(task.Prompt).Set(ChatVisuals.StylePrimaryText).FontFamily("Cascadia Mono").FontSize(11).FontWeight(Microsoft.UI.Text.FontWeights.Normal)
                    .TextWrapping(TextWrapping.Wrap).IsTextSelectionEnabled(true).IsVisible(instructions).Padding(12))
                .MaxHeight(144).Set(scroll => scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled))
                .CornerRadius(8).Background(Theme.Ref("ChatTaskPromptBrush")).Margin(0, -4, 0, 0).IsVisible(instructions)
        };
        void Alert(string text) => body.Add(Border(TextBlock(text).Set(ChatVisuals.StylePrimaryText).TextWrapping(TextWrapping.Wrap)
            .LiveRegion(AutomationLiveSetting.Assertive)).Padding(8).CornerRadius(8).Background(Theme.Ref("InfoBarErrorSeverityBackgroundBrush")));
        if (copyFailed) Alert(L("CopyFailed", "Instructions could not be copied."));
        if (card.DismissFailed) Alert(L("DismissFailed", "The task could not be dismissed. Try again."));
        if (card.CloudProfileRequired && !cloudOpen) Alert(L("CloudProfileRequired", "Choose a configured cloud profile. The task has not started."));
        if (props.RefreshFailed)
            body.Add(VStack(4, TextBlock(L("RefreshFailed", "Suggested tasks could not be refreshed.")).Set(ChatVisuals.StyleSecondaryText).TextWrapping(TextWrapping.Wrap),
                HyperlinkButton(L("Refresh", "Refresh tasks"), null, () => _ = props.Coordinator.RefreshAsync()).Padding(4, 2).MinHeight(32)
                    .AutomationName(L("Refresh", "Refresh tasks")).Set(KeepFocusInside).HAlign(HorizontalAlignment.Left)));
        Element actions;
        if (card.StartedKey is not null)
            actions = TextBlock(L("Started", "Task started. Find it in Sessions.")).Set(ChatVisuals.StylePrimaryText).TextWrapping(TextWrapping.Wrap).LiveRegion(AutomationLiveSetting.Polite);
        else if (card.Busy && card.Attempt is not null)
            actions = TextBlock(L("Starting", "Starting task...")).Set(ChatVisuals.StylePrimaryText).TextWrapping(TextWrapping.Wrap)
                .AutomationId("ChatTaskStarting").AutomationName(L("Starting", "Starting task...")).LiveRegion(AutomationLiveSetting.Polite);
        else if (card.Attempt is not null && !card.Busy)
        {
            Alert(L("Unconfirmed", "The task start was not confirmed. Retrying checks the same task, not a new task. After reconnecting, check Sessions or web chat."));
            body.Add(TextBlock(L("HideHelp", "Hide this notice locally. The task outcome remains unknown.")).Set(ChatVisuals.StyleSecondaryText).TextWrapping(TextWrapping.Wrap));
            actions = HStack(6,
                Button(L("Hide", "Hide locally"), () => props.Coordinator.HideLocally(task.Id)).SubtleButton().Set(KeepFocusInside).IsEnabled(props.CanHideLocally),
                Primary(L("Retry", "Retry same task"), () => _ = props.Coordinator.AcceptAsync(card.Attempt)).IsEnabled(props.Enabled && props.CanAccept));
        }
        else if (card.RepositoryRequired)
        {
            Alert(L("RepositoryRequired", "Enter an absolute repository path on the Gateway with a commit. The task has not started."));
            if (repositoryOpen)
                body.Add(VStack(6, TextBox(cwd, setCwd).Header(L("Repository", "Repository path on Gateway"))
                        .AutomationName(L("Repository", "Repository path on Gateway")).MaxLength(4096),
                    Button(L("Cancel", "Cancel"), () => setRepositoryOpen(false)).SubtleButton().Set(KeepFocusInside).HAlign(HorizontalAlignment.Left)));
            actions = repositoryOpen
                ? Primary(L("Worktree", "Start in a new worktree"), () => _ = props.Coordinator.AcceptAsync(new(task.Id, "worktree", Cwd: cwd.Trim())))
                    .IsEnabled(props.Enabled && props.CanAccept && !string.IsNullOrWhiteSpace(cwd))
                : Primary(L("ChooseRepository", "Choose repository"), () => setRepositoryOpen(true)).IsEnabled(props.Enabled && props.CanAccept);
        }
        else if (cloudOpen)
        {
            if (card.CloudProfileRequired) Alert(L("CloudProfileRequired", "Choose a configured cloud profile. The task has not started."));
            body.Add(VStack(6, TextBox(profile, setProfile).Header(L("CloudProfile", "Configured cloud profile ID"))
                    .AutomationName(L("CloudProfile", "Configured cloud profile ID")).MaxLength(128),
                Button(L("Cancel", "Cancel"), () => setCloudOpen(false)).SubtleButton().Set(KeepFocusInside).HAlign(HorizontalAlignment.Left)));
            actions = Primary(L("Cloud", "Start in cloud"), () => _ = props.Coordinator.AcceptAsync(new(task.Id, "cloud", profile.Trim())))
                .IsEnabled(props.Enabled && props.CanAccept && props.CloudSupported && !string.IsNullOrWhiteSpace(profile));
        }
        else
        {
            var reason = props.AdministratorRequired ? L("AdministratorRequired", "Administrator access to the Gateway is required to start suggested tasks.")
                : L("Compatibility", "Update the Gateway or use web chat to launch this task.");
            if (!props.CanAccept) body.Add(TextBlock(reason).Set(ChatVisuals.StyleSecondaryText).TextWrapping(TextWrapping.Wrap));
            var primary = Primary(L("Local", "Start in a new session"),
                () => _ = props.Coordinator.AcceptAsync(new(task.Id, "local")), play: true, accent: props.Enabled && props.CanAccept).CornerRadius(10, 0, 0, 10)
                .IsEnabled(props.Enabled && props.CanAccept).Set(button => SetLaunchTooltip(button, props.CanAccept, reason));
            var options = PrimaryGlyph(FluentIconCatalog.ChevronDown, L("Options", "Choose where to start the task"), "ChatTaskLaunchOptions", props.Enabled && props.CanAccept)
                .CornerRadius(0, 10, 10, 0).Width(32).IsEnabled(props.Enabled && props.CanAccept)
                .Set(button => button.Flyout = menu).Set(button => SetLaunchTooltip(button, props.CanAccept, reason))
                .OnMountAdd(control => anchor.Current = (Button)control);
            actions = HStack(0, primary, options);
        }
        var bodyContent = VStack(6, body.ToArray()).Margin(0, 0, 12, 0);
        Element bodyViewport = ScrollViewer(bodyContent).Set(scroll =>
        {
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.VerticalScrollBarVisibility = props.WholeCardScroll ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            scroll.VerticalScrollMode = props.WholeCardScroll ? ScrollMode.Disabled : ScrollMode.Enabled;
        });
        return Border(Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Auto, props.WholeCardScroll ? GridSize.Auto : GridSize.Star(), GridSize.Auto],
                header.Grid(row: 0),
                identity.Margin(0, 6, 0, 2).Grid(row: 1),
                disclosure.Grid(row: 2),
                bodyViewport.Margin(0, 2, 0, 6).AutomationId("ChatTaskBody").Grid(row: 3),
                actions.HAlign(HorizontalAlignment.Right).Grid(row: 4)).MaxHeight(props.WholeCardScroll ? double.PositiveInfinity : Math.Max(80, props.MaximumHeight - 30)))
            .Padding(16, 14, 16, 14).CornerRadius(ChatVisuals.TaskCardRadius).BorderThickness(1)
            .Set(StyleCard)
            .AutomationId("ChatTaskSuggestion_" + task.Id);
    }

    private void RestoreNavigationFocus(Button button, string id)
    {
        var request = Props.NavigationFocus;
        if (request is null || request.Direction != id || request.TargetTaskId != Props.Card.Suggestion.Id) return;
        void Restore()
        {
            button.DispatcherQueue.TryEnqueue(() =>
            {
                var current = Props;
                if (!button.IsLoaded || current.Card.Suggestion.Id != request.TargetTaskId
                    || current.NavigationFocus?.Generation != request.Generation || !current.ConsumeNavigationFocus(request)) return;
                button.Focus(FocusState.Keyboard);
            });
        }
        if (button.IsLoaded) Restore();
        else
        {
            void Loaded(object sender, RoutedEventArgs args) { button.Loaded -= Loaded; Restore(); }
            button.Loaded += Loaded;
        }
    }
    private static Element Glyph(string glyph, double size = ChatVisuals.ActionIconSize) => TextBlock(glyph)
        .FontFamily(FluentIconCatalog.SymbolThemeFontFamily).FontSize(size).AccessibilityHidden();
    private static ButtonElement IconButton(string glyph, string label, string id, Action click) => Button(Glyph(glyph), click)
        .SubtleButton().Width(32).Height(32).Padding(0).CornerRadius(8).AutomationId(id).AutomationName(label)
        .Set(KeepFocusInside).ToolTip(label);
    private static ButtonElement Primary(string label, Action click, bool play = false, bool accent = true) => Button(play ? HStack(5, Glyph(FluentIconCatalog.TaskStart, 12), TextBlock(label)) : TextBlock(label), click)
        .AutomationName(label).MinHeight(34).Padding(12, 0).Set(button => StylePrimary(button, accent)).Set(KeepFocusInside);
    private static ButtonElement PrimaryGlyph(string glyph, string label, string id, bool accent) => Button(Glyph(glyph, 12), null)
        .AutomationName(label).AutomationId(id).MinHeight(34).Padding(0).Set(button => StylePrimary(button, accent)).Set(KeepFocusInside);
    private static void SizeDisclosure(Microsoft.UI.Xaml.Controls.Primitives.ToggleButton control, string label)
    {
        // Measure the localized label with the rendered native font, not a language-specific width.
        var text = new Microsoft.UI.Xaml.Controls.TextBlock { Text = label, FontFamily = control.FontFamily,
            FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Normal };
        text.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        control.MinWidth = Math.Ceiling(text.DesiredSize.Width) + 12 + 6 + 12;
    }
    private static void ConfigureSummaryViewport(Microsoft.UI.Xaml.Controls.ScrollViewer scroll, double maximumHeight)
    {
        scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        scroll.VerticalScrollMode = ScrollMode.Enabled;
        if (scroll.Content is not TextBlock summary) return;
        // Measure the rendered font instead of assuming a DIP line height. Fixed line boxes
        // keep the independent viewport on whole lines, including native text scaling.
        var probe = new TextBlock { Text = "Mg", Style = summary.Style, FontFamily = summary.FontFamily,
            FontSize = summary.FontSize, FontWeight = summary.FontWeight, CharacterSpacing = summary.CharacterSpacing,
            TextWrapping = TextWrapping.NoWrap, LineStackingStrategy = LineStackingStrategy.MaxHeight,
            IsTextScaleFactorEnabled = summary.IsTextScaleFactorEnabled, Language = summary.Language };
        probe.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var lineHeight = Math.Max(1, Math.Ceiling(probe.DesiredSize.Height));
        if (summary.LineHeight != lineHeight) summary.LineHeight = lineHeight;
        if (summary.LineStackingStrategy != LineStackingStrategy.BlockLineHeight) summary.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        var available = maximumHeight * (maximumHeight < 290 ? .10 : .25);
        var height = Math.Clamp(Math.Floor(available / lineHeight), 1, 5) * lineHeight;
        if (scroll.MaxHeight != height) scroll.MaxHeight = height;
        UpdateSummaryTabStop(scroll);
        KeepFocusInside(scroll);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, SummaryViewportObserver> SummaryObservers = new();
    private static System.Globalization.CultureInfo EffectiveUiCulture()
    {
        // SetLanguageOverride changes MRT qualifiers, not CurrentUICulture.
        try { return System.Globalization.CultureInfo.GetCultureInfo(LocalizationHelper.CurrentLanguage); }
        catch (System.Globalization.CultureNotFoundException) { return System.Globalization.CultureInfo.CurrentUICulture; }
    }

    private static void UpdateSummaryTabStop(Microsoft.UI.Xaml.Controls.ScrollViewer scroll)
    {
        var overflow = scroll.ScrollableHeight > .5;
        if (scroll.IsTabStop == overflow) return;
        scroll.IsTabStop = overflow;
        if (!overflow && scroll.XamlRoot is not null
            && ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(scroll.XamlRoot), scroll))
        {
            DependencyObject? card = scroll;
            while (card is not null && !Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(card).StartsWith("ChatTaskSuggestion_", StringComparison.Ordinal))
                card = VisualTreeHelper.GetParent(card);
            if (card is FrameworkElement root && ChatMotion.FindByAutomationId(root, "ChatTaskInstructions") is Control disclosure)
                disclosure.Focus(FocusState.Keyboard);
        }
    }

    // One mounted owner for geometry and the process-global scale event. No event survives unmount.
    private sealed class SummaryViewportObserver : IDisposable
    {
        private readonly Microsoft.UI.Xaml.Controls.ScrollViewer _scroll;
        private readonly Func<double> _maximumHeight;
        private readonly global::Windows.UI.ViewManagement.UISettings _settings = new();
        private TextBlock? _summary;
        private bool _disposed, _queued;
        public SummaryViewportObserver(Microsoft.UI.Xaml.Controls.ScrollViewer scroll, Func<double> maximumHeight)
        {
            _scroll = scroll;
            _maximumHeight = maximumHeight;
            scroll.Loaded += OnLoaded;
            scroll.SizeChanged += OnSizeChanged;
            scroll.ViewChanged += OnViewChanged;
            _settings.TextScaleFactorChanged += OnTextScaleChanged;
            Refresh();
        }
        public void Refresh()
        {
            if (_disposed) return;
            var text = _scroll.Content as TextBlock;
            if (!ReferenceEquals(text, _summary))
            {
                if (_summary is not null) _summary.SizeChanged -= OnSizeChanged;
                _summary = text;
                if (_summary is not null) _summary.SizeChanged += OnSizeChanged;
            }
            ConfigureSummaryViewport(_scroll, _maximumHeight());
        }
        private void QueueRefresh()
        {
            if (_disposed || _queued) return;
            _queued = true;
            if (!_scroll.DispatcherQueue.TryEnqueue(() => { _queued = false; if (!_disposed) Refresh(); })) _queued = false;
        }
        private void OnLoaded(object sender, RoutedEventArgs args) => QueueRefresh();
        private void OnSizeChanged(object sender, SizeChangedEventArgs args) => QueueRefresh();
        private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
        {
            if (!_disposed) UpdateSummaryTabStop(_scroll);
        }
        private void OnTextScaleChanged(global::Windows.UI.ViewManagement.UISettings sender, object args)
        {
            // UISettings raises outside the XAML thread; all metrics and hook state stay on its dispatcher.
            _scroll.DispatcherQueue.TryEnqueue(QueueRefresh);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _settings.TextScaleFactorChanged -= OnTextScaleChanged;
            _scroll.Loaded -= OnLoaded;
            _scroll.SizeChanged -= OnSizeChanged;
            _scroll.ViewChanged -= OnViewChanged;
            if (_summary is not null) _summary.SizeChanged -= OnSizeChanged;
            _summary = null;
        }
    }

    private static void SetLaunchTooltip(Button button, bool canAccept, string reason)
    {
        if (canAccept) button.ClearValue(ToolTipService.ToolTipProperty);
        else ToolTipService.SetToolTip(button, reason);
    }
    private static void StylePrimary(Button button, bool accent)
    {
        button.Style = (Style)Application.Current.Resources[accent ? "AccentButtonStyle" : "DefaultButtonStyle"];
        // Keep the native stock template when disabled; clearing Reactor's style also clears its template.
    }
    internal static void StyleCard(Border border) => border.Style = (Style)Application.Current.Resources["ChatTaskCardStyle"];
    internal static void KeepFocusInside(Control control) => control.FocusVisualMargin = new Thickness(0);
    private static string L(string key, string fallback) => ReactorTaskSuggestionPanel.L(key, fallback);
}
