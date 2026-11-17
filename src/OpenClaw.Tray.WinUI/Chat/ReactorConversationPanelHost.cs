// Native tab strip beside a stable conversation subtree. Use with the root-owned
// ChatConversationPanelState; tab bodies stay mounted while selection changes.
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared.Dashboards;
using OpenClawTray.Dashboards;
using OpenClawTray.Helpers;
using Windows.Foundation;
using Windows.System;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ReactorConversationPanelHostProps(
    Element Conversation, ChatConversationPanelState State, Action<bool>? ConversationVisibilityChanged = null,
    ChatTimelinePresentationContext? Timeline = null, Element? Subagents = null,
    DashboardsPageState? Dashboards = null, Element? Files = null,
    Func<ChatWorkspaceFileRequest, CancellationToken, Task<ChatWorkspaceFileResult>>? LoadWorkspaceFile = null,
    Element? Launcher = null);

/// <summary>Applies native tab selection, split or expanded sizing, compact presentation and focus.</summary>
internal sealed class ReactorConversationPanelHost : Component<ReactorConversationPanelHostProps>
{
    public override Element Render()
    {
        var state = Props.State;
        var (_, setRevision) = UseState(0L, threadSafe: true);
        var (width, setWidth) = UseState(800d);
        var viewportWidth = UseRef(width);
        viewportWidth.Current = width;
        var (showCompact, setShowCompact) = UseState(true);
        // Distance from the window's top edge, so a header that joins the caption row can reach it.
        var (topOffset, setTopOffset) = UseState(0d);
        var topOffsetRef = UseRef(0d);
        var layoutWatch = UseRef<EventHandler<object>?>(null);
        var focus = UseRef<PanelInteraction?>(null);
        var tabView = UseRef<TabView?>(null);
        // The native drag handler is attached once at mount; it must reach the current state.
        var latestState = UseRef(state);
        latestState.Current = state;
        // A native drag removes the moved tab and reinserts it; removing the selected tab makes the strip
        // select a neighbor, possibly after the reinsert. Selection events from the move start until its
        // order is adopted are not the user's.
        var movePending = UseRef(false);
        var renderedTabCount = UseRef(state.Tabs.Count);
        renderedTabCount.Current = state.Tabs.Count;
        // One delegate for the mount's lifetime, so unmount removes exactly what mount added.
        var adoptNativeOrderRef = UseRef<TypedEventHandler<TabView, global::Windows.Foundation.Collections.IVectorChangedEventArgs>?>(null);
        var adoptNativeOrder = adoptNativeOrderRef.Current ??= (view, args) =>
        {
            // Reactor removes a closed tab after state already dropped it; a native move removes a rendered one.
            if (args.CollectionChange == global::Windows.Foundation.Collections.CollectionChange.ItemRemoved &&
                view.TabItems.Count == renderedTabCount.Current - 1)
            {
                movePending.Current = true;
                return;
            }
            // A drag ends with an insert; Reactor's own inserts already match state order, so Reorder ignores them.
            if (args.CollectionChange != global::Windows.Foundation.Collections.CollectionChange.ItemInserted) return;
            var order = view.TabItems.Select(SlotOf).ToArray();
            if (order.Any(slot => slot is null)) return;
            var target = latestState.Current;
            var revision = target.Revision;
            // Applied later: reject it if the view unmounted or the conversation was rebound meanwhile.
            view.DispatcherQueue.TryEnqueue(() =>
            {
                movePending.Current = false;
                if (ReferenceEquals(tabView.Current, view) && ReferenceEquals(latestState.Current, target) &&
                    target.Revision == revision)
                    target.Reorder(order!);
            });
        };
        UseEffect((Func<Action>)(() =>
        {
            long revision = 0;
            void Changed() => setRevision(++revision);
            state.Changed += Changed;
            return () => state.Changed -= Changed;
        }), state);

        var tabs = state.Tabs.ToArray();
        var selected = state.Selected;
        var open = state.IsOpen;
        var launcher = open && tabs.Length == 0;
        var narrow = width < ChatConversationPanelState.MinimumConversationWidth +
            ChatConversationPanelState.MinimumPanelWidth + ChatConversationPanelState.DividerWidth;
        var presented = open && (!narrow || showCompact && !state.Passive);
        var expanded = presented && !narrow && state.Expanded;
        var split = open && !narrow && !expanded;
        var conversationVisible = !presented || !narrow && !expanded;
        // Workspace chrome offers its caption row: the header joins it and the panel and divider reach the
        // window top, unless the panel is too narrow to keep usable tabs beside the window controls.
        var joinsTitleRow = presented && !narrow &&
            state.CanJoinTitleRow(expanded ? width : state.EffectiveWidth(width), fillsArea: expanded);
        var lift = joinsTitleRow ? topOffset : 0;
        // A panel that fills the area starts at the window's leading edge, under its start controls.
        var startInset = joinsTitleRow && expanded ? state.TitleRowStartInset : 0;
        var endInset = joinsTitleRow ? state.TitleRowEndInset : 0;
        // Tabs center in the caption band; the rest of the covered chrome stays header space, never body.
        var captionBand = joinsTitleRow ? state.TitleRowHeight - state.TitleRowCaptionHeight : 0;
        UseEffect((Func<Action>)(() =>
        {
            state.SetPresentation(presented, narrow, joinsTitleRow);
            return static () => { };
        }), presented, narrow, joinsTitleRow, state);
        var updateVisibility = Props.ConversationVisibilityChanged;
        UseEffect((Func<Action>)(() =>
        {
            updateVisibility?.Invoke(conversationVisible);
            return () => updateVisibility?.Invoke(true);
        }), conversationVisible, updateVisibility);
        UseEffect((Func<Action>)(() =>
        {
            setShowCompact(true);
            return static () => { };
        }), state.OpenVersion);
        void TogglePresented()
        {
            if (presented)
            {
                setShowCompact(false);
                focus.Current?.Restore();
                return;
            }
            state.Engage();
            setShowCompact(true);
        }

        // Reactor pairs native tab items with this array by index and reports no drag reorder, so each
        // item carries its slot. Selection and close resolve the native item; a drag adopts its order.
        string? SlotAt(int index) =>
            tabView.Current is { } view && index >= 0 && index < view.TabItems.Count
                ? SlotOf(view.TabItems[index])
                : index >= 0 && index < tabs.Length ? tabs[index] : null;
        var tabStrip = TabView((int?)Array.IndexOf(tabs, selected), index =>
            {
                if (movePending.Current || tabView.Current is { } view && view.TabItems.Count < tabs.Length) return;
                if (SlotAt(index) is { } slot) state.Select(slot);
            }, tabs.Select(tab => Tab(TabTitle(tab, state),
                    Border(null).IsVisible(false).Set(marker => marker.Tag = tab)) with { IsClosable = true }).ToArray())
            .IsAddTabButtonVisible(false)
            .CanDragTabs(false).CanReorderTabs(true)
            .TabWidthMode(TabViewWidthMode.SizeToContent)
            .CloseButtonOverlayMode(TabViewCloseButtonOverlayMode.OnPointerOver)
            .TabCloseRequested(index =>
            {
                if (SlotAt(index) is not { } slot) return;
                focus.Current?.BeforeClose(slot);
                state.Close(slot);
                focus.Current?.AfterClose();
            })
            .AutomationName(Text("Tabs"))
            .AutomationId("ConversationPanelTabs")
            .OnMount(control =>
            {
                var view = (TabView)control;
                tabView.Current = view;
                view.TabItemsChanged += adoptNativeOrder;
                state.SetTabStrip(view);
            })
            .OnUnmount(control =>
            {
                ((TabView)control).TabItemsChanged -= adoptNativeOrder;
                tabView.Current = null;
                if (ReferenceEquals(state.TabStrip, control)) state.SetTabStrip(null);
            });

        var bodies = tabs.Select(slot =>
            Border(slot switch
                {
                    ChatConversationPanelState.SubagentsSlot => Props.Subagents ?? Empty(),
                    ChatConversationPanelState.DashboardSlot => DashboardBody(state.Dashboard, Props.Dashboards),
                    ChatConversationPanelState.FilesSlot => Props.Files ?? Empty(),
                    ChatConversationPanelState.FileSlot => state.File is { } file
                        ? Component<ReactorChatWorkspaceFile, ReactorChatWorkspaceFileProps>(new(file, Props.LoadWorkspaceFile))
                        : TextBlock(Text("Unavailable")).TextWrapping(TextWrapping.Wrap).Margin(16),
                    _ => state.Details is { } request && state.Resolve(request) is { } entry
                        ? ToolCallCardRenderer.BuildPanelDetails(
                            Props.Timeline ?? new(request.SessionKey, [], false, null), entry)
                        : TextBlock(Text("Unavailable")).TextWrapping(TextWrapping.Wrap).Margin(16),
                })
                .WithKey(slot switch
                {
                    ChatConversationPanelState.DetailsSlot => state.Details?.ToString() ?? slot,
                    ChatConversationPanelState.DashboardSlot => slot + ":" + state.Dashboard?.SessionKey,
                    ChatConversationPanelState.FileSlot => state.FileBodyKey ?? slot,
                    _ => slot,
                })
                .IsVisible(slot == selected))
            .Cast<Element>().ToArray();
        var headerMargin = joinsTitleRow
            ? new Thickness(ChatVisuals.BlockGap + startInset, 0, endInset, captionBand)
            : new Thickness(ChatVisuals.BlockGap, ChatVisuals.BlockGap, ChatVisuals.BlockGap, 0);
        var panel = Grid([GridSize.Star()], [joinsTitleRow ? GridSize.Px(state.TitleRowHeight) : GridSize.Auto, GridSize.Star()],
                launcher
                    ? TextBlock(Text("Launcher")).FontSize(ChatVisuals.BodySize).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                        .Foreground(Theme.Ref("ChatTextBrush"))
                        .Margin(headerMargin)
                        .VAlign(joinsTitleRow ? VerticalAlignment.Center : VerticalAlignment.Top)
                        .HeadingLevel(Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2)
                        .AutomationId("ConversationPanelLauncherTitle")
                        .Grid(row: 0)
                    : (joinsTitleRow
                        ? tabStrip.VAlign(VerticalAlignment.Center).Margin(0, 0, 0, captionBand)
                        : tabStrip.VAlign(VerticalAlignment.Top).Margin(0))
                        .Set(control => ReserveTabStripEdges((TabView)control, startInset, endInset))
                        .Grid(row: 0),
                launcher
                    ? Border(Props.Launcher ?? Empty()).WithKey("panel-launcher").Grid(row: 1)
                    : Grid([GridSize.Star()], [GridSize.Star()], bodies).Grid(row: 1))
            .Background(Theme.Ref("ChatCanvasBrush"))
            .Margin(0, -lift, 0, 0)
            .AutomationId("ConversationPanel")
            .IsVisible(presented)
            .Set(control =>
            {
                control.Style = (Style)Application.Current.Resources["ChatCanvasStyle"];
                control.ClearValue(Microsoft.UI.Xaml.Controls.Panel.BackgroundProperty);
                focus.Current?.Update(control, narrow, expanded, presented, TogglePresented);
            })
            .WithKey("conversation-panel");
        var panelWidth = split ? state.EffectiveWidth(width) : 0;
        var resize = Button(
                Border(null).Background(Theme.Ref("ControlStrokeColorDefaultBrush")).Width(4).Height(40), () => { })
            .AutomationName(Text("Resize"))
            .HelpText(Text("ResizeHelp"))
            .AutomationId("ConversationPanelResize")
            .ToolTip(Text("Resize"))
            .Resources(ChatVisuals.ToolbarButtonResources)
            .Padding(0).MinWidth(0).Width(ChatConversationPanelState.DividerWidth)
            .IsVisible(split)
            .OnMount(control => PanelResize.Attach((Button)control, state, () => viewportWidth.Current))
            .OnUnmount(control => PanelResize.Detach((Button)control));

        var toggleText = presented ? "Back" : "Show";
        return Grid([GridSize.Star(), GridSize.Px(split ? ChatConversationPanelState.DividerWidth : 0), GridSize.Px(panelWidth)],
                [GridSize.Auto, GridSize.Star()],
                Button(TextBlock(Text(toggleText)), TogglePresented)
                    .AutomationId("ConversationPanelToggle")
                    .AutomationName(Text(toggleText))
                    .IsVisible(open && narrow)
                    .HAlign(HorizontalAlignment.Left).Margin(8)
                    .Grid(row: 0),
                Border(Props.Conversation).WithKey("chat-conversation")
                    .AutomationId("ChatConversation")
                    .IsVisible(conversationVisible)
                    .Set(control => focus.Current?.SetConversation(control))
                    .Grid(row: 1),
                Border(Empty()).Width(1)
                    .Background(Theme.Ref("DividerStrokeColorDefaultBrush"))
                    .HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Stretch)
                    .Margin(0, -lift, 0, 0)
                    .IsHitTestVisible(false)
                    .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw)
                    .AutomationId("ConversationPanelDivider")
                    .IsVisible(split)
                    .WithKey("conversation-panel-divider")
                    .Grid(column: 1, row: 1),
                resize.Grid(column: 1, row: 1),
                panel.Grid(column: split ? 2 : 0, row: 1))
            .OnMount(control =>
            {
                ChatVisuals.Observe(control, setWidth);
                focus.Current = new PanelInteraction((Grid)control, state,
                    index => tabView.Current is { } view && index >= 0 && index < view.TabItems.Count
                        ? view.TabItems[index] as Control : null);
                var root = (FrameworkElement)control;
                layoutWatch.Current = (_, _) =>
                {
                    // Only a host offered a title row needs its window offset (never the companion window).
                    if (state.TitleRowHeight <= 0 || !root.IsLoaded || root.XamlRoot is null) return;
                    double y;
                    try { y = root.TransformToVisual(null).TransformPoint(default).Y; }
                    catch (ArgumentException) { return; }
                    if (Math.Abs(y - topOffsetRef.Current) < 0.5) return;
                    topOffsetRef.Current = y;
                    setTopOffset(y);
                };
                root.LayoutUpdated += layoutWatch.Current;
            })
            .OnUnmount(control =>
            {
                ChatVisuals.StopObserving(control);
                if (layoutWatch.Current is { } watch) ((FrameworkElement)control).LayoutUpdated -= watch;
                layoutWatch.Current = null;
                focus.Current?.Dispose();
                focus.Current = null;
                state.SetPresentation(false, false);
            });
    }

    private static string Text(string name) => LocalizationHelper.GetString("Chat_Panel_" + name);

    /// <summary>
    /// Keeps tabs clear of window controls in the caption row while the strip's bottom line
    /// still spans the panel: the native header and footer slots take the reserved widths.
    /// </summary>
    private static void ReserveTabStripEdges(TabView view, double start, double end)
    {
        Reserve(Microsoft.UI.Xaml.Controls.TabView.TabStripHeaderProperty, start);
        Reserve(Microsoft.UI.Xaml.Controls.TabView.TabStripFooterProperty, end);

        void Reserve(DependencyProperty slot, double width)
        {
            if (width <= 0)
            {
                view.ClearValue(slot);
                return;
            }
            if (view.GetValue(slot) is not Border spacer)
                view.SetValue(slot, spacer = new Border { IsHitTestVisible = false });
            spacer.Width = width;
        }
    }

    private static string? SlotOf(object item) =>
        item is TabViewItem { Content: FrameworkElement { Tag: string slot } } ? slot : null;

    private static string TabTitle(string slot, ChatConversationPanelState state) => slot switch
    {
        ChatConversationPanelState.SubagentsSlot => LocalizationHelper.GetString("Chat_Subagents_Title"),
        ChatConversationPanelState.DashboardSlot => Text("Dashboard"),
        ChatConversationPanelState.FilesSlot => LocalizationHelper.GetString("Chat_Files_Title"),
        ChatConversationPanelState.FileSlot => state.File is { } file &&
            NativeToolProjector.SanitizeToolDisplayValue(file.Name) is { Length: > 0 } name ? name : Text("File"),
        _ => Text("Details"),
    };

    // The board view owns loading, access and disconnected states once a client exists.
    private static Element DashboardBody(BoardSessionTarget? target, DashboardsPageState? dashboards) =>
        target is null || dashboards is null ? Empty()
        : dashboards.Client is { } client
            ? Component<ReactorBoardView, BoardViewProps>(
                new(dashboards, client, target, dashboards.ConnectionRevision, Embedded: true))
            : DashboardsStates.Disconnected(dashboards);

    private sealed class PanelInteraction : IDisposable
    {
        // Low-priority dispatcher passes allowed for a newly presented focus target to load.
        private const int FocusAttempts = 4;
        private readonly Grid _root;
        private readonly ChatConversationPanelState _state;
        private readonly Func<int, Control?> _tabAt;
        private readonly TypedEventHandler<UIElement, GettingFocusEventArgs> _redirectClosingFocus;
        private FrameworkElement? _panel;
        private FrameworkElement? _conversation;
        private Control? _opener;
        private string? _openerId;
        private string? _selected;
        private bool _presented;
        private bool _narrow;
        private bool _expanded;
        private Action? _toggle;
        private long _focusRevision;
        private long _openVersion;
        private long _closeRevision;
        private bool _closing;
        private EventHandler<object>? _pendingLayout;

        internal PanelInteraction(Grid root, ChatConversationPanelState state, Func<int, Control?> tabAt)
        {
            _root = root;
            _state = state;
            _tabAt = tabAt;
            _redirectClosingFocus = RedirectClosingFocus;
            root.AddHandler(UIElement.GettingFocusEvent, _redirectClosingFocus, true);
            root.KeyDown += KeyDown;
            state.Changed += CaptureOpener;
        }

        private void CaptureOpener()
        {
            if (_state.IsOpen && _state.OpenVersion != _openVersion &&
                _root.XamlRoot is { } root &&
                FocusManager.GetFocusedElement(root) is Control opener &&
                (_panel is null || !IsWithin(opener, _panel)))
            {
                _opener = opener;
                _openerId = Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(opener);
            }
        }

        internal void SetConversation(FrameworkElement conversation) => _conversation = conversation;

        internal void Update(FrameworkElement panel, bool narrow, bool expanded, bool presented, Action toggle)
        {
            _panel = panel;
            _narrow = narrow;
            _expanded = expanded;
            _toggle = toggle;
            if (presented && !_state.Passive &&
                (!_presented || _selected != _state.Selected || _openVersion != _state.OpenVersion))
            {
                QueueSelectedTabFocus(panel);
            }
            _presented = presented;
            _selected = _state.Selected;
            _openVersion = _state.OpenVersion;
            panel.TabFocusNavigation = KeyboardNavigationMode.Local;
            _root.TabFocusNavigation = narrow && presented ? KeyboardNavigationMode.Cycle : KeyboardNavigationMode.Local;
        }

        private void QueueFocus(Func<Control?> target, Func<Control?>? fallback = null, bool awaitTarget = false)
        {
            var revision = ++_focusRevision;
            var generation = _state.Revision;
            DetachPendingLayout();
            bool Current() => revision == _focusRevision && generation == _state.Revision && _root.IsLoaded;
            void Attempt(int remaining) => _root.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (!Current()) return;
                    var control = target();
                    if (TryFocus(control)) return;
                    // A newly presented tab item can load a layout pass after the panel; retry before falling back.
                    if (remaining > 1) Attempt(remaining - 1);
                    // On a slow layout pass the selected tab can exist without being focusable yet. Moving focus to the
                    // fallback would leave the requested tab unfocused, so wait for the layout that makes it focusable.
                    else if (awaitTarget && control is not null) FocusWhenUsable(target, Current);
                    else TryFocus(fallback?.Invoke());
                });
            Attempt(FocusAttempts);
        }

        private void QueueSelectedTabFocus(FrameworkElement panel) =>
            QueueFocus(SelectedTab, () => FocusManager.FindFirstFocusableElement(panel) as Control, awaitTarget: true);

        /// <summary>
        /// Focuses <paramref name="target"/> after the layout pass that makes it focusable. A newer focus request,
        /// a panel state change, keyboard focus the user moves meanwhile, or disposal cancels the wait.
        /// </summary>
        private void FocusWhenUsable(Func<Control?> target, Func<bool> current)
        {
            var focusedBefore = _root.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
            var requested = _state.Selected;
            // Closing the requested tab can select another one passively, without a revision change.
            bool Pending() => current() && _presented && !_state.Passive && _state.Selected == requested;
            void LayoutUpdated(object? sender, object args)
            {
                if (!Pending())
                {
                    DetachPendingLayout();
                    return;
                }
                if (!IsUsable(target())) return;
                DetachPendingLayout();
                // Focusing from inside a layout callback would start another layout; apply it on the next pass.
                _root.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (Pending() && _root.XamlRoot is { } xamlRoot &&
                        ReferenceEquals(FocusManager.GetFocusedElement(xamlRoot), focusedBefore))
                        TryFocus(target());
                });
            }
            _root.LayoutUpdated += LayoutUpdated;
            _pendingLayout = LayoutUpdated;
        }

        private void DetachPendingLayout()
        {
            if (_pendingLayout is { } handler) _root.LayoutUpdated -= handler;
            _pendingLayout = null;
        }

        private bool TryFocus(Control? control) => IsUsable(control) && control!.Focus(FocusState.Programmatic);

        private bool IsUsable(Control? control)
        {
            if (control is not { IsLoaded: true, IsEnabled: true } || control.XamlRoot != _root.XamlRoot)
                return false;
            for (DependencyObject? element = control; element is not null; element = VisualTreeHelper.GetParent(element))
            {
                if (element is UIElement { Visibility: Visibility.Collapsed }) return false;
                // Collapsing Expander content stays visible until its animation ends.
                if (element is Expander { IsExpanded: false, Content: DependencyObject content } &&
                    IsWithin(control, content)) return false;
            }
            return true;
        }

        internal void Restore() => QueueFocus(OpenerTarget, ConversationTarget);

        private Control? OpenerTarget() =>
            _opener is { } opener &&
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(opener) == _openerId
                ? opener : null;

        private Control? SelectedTab() =>
            _state.Selected is { } slot && _state.Tabs.ToList().IndexOf(slot) is >= 0 and var index ? _tabAt(index) : null;

        private Control? ConversationTarget() =>
            _conversation is { } conversation ? FocusManager.FindFirstFocusableElement(conversation) as Control : null;

        /// <summary>
        /// Removing a focused tab item lets WinUI choose the next focus target, which can be a transcript
        /// row whose bring-into-view moves the parent's reading position. Before closing, move focus to the
        /// tab that will be selected next, or to the opener when the last tab closes. Because
        /// that move can be refused while the tab strip is handling the close, automatic focus that still
        /// reaches the conversation during the close is redirected (see <see cref="RedirectClosingFocus"/>).
        /// </summary>
        internal void BeforeClose(string slot)
        {
            var index = _state.Tabs.ToList().IndexOf(slot);
            if (index < 0 || _panel is not { } panel || _root.XamlRoot is not { } root)
                return;
            var focused = FocusManager.GetFocusedElement(root) as DependencyObject;
            // No focus counts as at risk: WinUI assigns one when the focused item disappears.
            var closingFocus = focused is null ||
                _tabAt(index) is { } closingTab && IsWithin(focused, closingTab) ||
                slot == _state.Selected && IsWithin(focused, panel);
            if (!closingFocus) return;
            if (_state.Tabs.Count == 1)
                TryFocus(OpenerTarget());
            else if ((slot == _state.Selected
                    ? _state.Tabs[index + 1 < _state.Tabs.Count ? index + 1 : index - 1]
                    : _state.Selected) is { } next)
                TryFocus(_tabAt(_state.Tabs.ToList().IndexOf(next)));
            var closing = ++_closeRevision;
            _closing = true;
            // Two low-priority passes outlast the render that removes the tab item.
            _root.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                _root.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (closing == _closeRevision) _closing = false;
                }));
        }

        private void RedirectClosingFocus(UIElement sender, GettingFocusEventArgs args)
        {
            if (!_closing || _conversation is not { } conversation ||
                args.NewFocusedElement is not DependencyObject target || !IsWithin(target, conversation))
                return;
            var replacement = _state.Selected is null ? OpenerTarget() : SelectedTab();
            if (ReferenceEquals(replacement, target)) return;
            if (!IsUsable(replacement) || !args.TrySetNewFocusedElement(replacement))
                args.TryCancel();
        }

        internal void AfterClose()
        {
            if (_state.Selected is null) Restore();
        }

        private void KeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (!_state.IsOpen || _root.XamlRoot is not { } root) return;
            var within = _panel is { } panel && IsWithin(FocusManager.GetFocusedElement(root) as DependencyObject, panel);
            if (args.Key == VirtualKey.Escape && (within || _narrow && _presented))
            {
                if (_state.Selected is { } selected)
                {
                    BeforeClose(selected);
                    _state.Close(selected);
                }
                else
                {
                    _state.Hide();
                }
                AfterClose();
                args.Handled = true;
            }
            else if (args.Key == VirtualKey.F6)
            {
                if (_narrow) _toggle?.Invoke();
                // Leaving a panel that fills the area restores the split so the conversation can take focus.
                else if (_expanded && within) _state.ToggleExpanded();
                if (within) Restore();
                else if (_panel is { } current)
                    QueueSelectedTabFocus(current);
                args.Handled = true;
            }
        }

        private static bool IsWithin(DependencyObject? element, DependencyObject parent)
        {
            for (; element is not null; element = VisualTreeHelper.GetParent(element))
                if (ReferenceEquals(element, parent)) return true;
            return false;
        }

        public void Dispose()
        {
            _focusRevision++;
            DetachPendingLayout();
            _closing = false;
            _root.RemoveHandler(UIElement.GettingFocusEvent, _redirectClosingFocus);
            _root.KeyDown -= KeyDown;
            _state.Changed -= CaptureOpener;
            _opener = null;
            _conversation = null;
        }
    }

    private sealed class PanelResize
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Button, PanelResize> Bindings = new();
        private readonly Button _button;
        private readonly ChatConversationPanelState _state;
        private readonly Func<double> _viewportWidth;
        private readonly PointerEventHandler _pressed;
        private readonly PointerEventHandler _moved;
        private readonly PointerEventHandler _released;
        private double? _origin;
        private double _width;
        private PanelResize(Button button, ChatConversationPanelState state, Func<double> viewportWidth)
        {
            _button = button;
            _state = state;
            _viewportWidth = viewportWidth;
            _pressed = Pressed;
            _moved = Moved;
            _released = Released;
            button.AddHandler(UIElement.PointerPressedEvent, _pressed, true);
            button.AddHandler(UIElement.PointerMovedEvent, _moved, true);
            button.AddHandler(UIElement.PointerReleasedEvent, _released, true);
            button.AddHandler(UIElement.PointerCaptureLostEvent, _released, true);
            button.AddHandler(UIElement.PointerCanceledEvent, _released, true);
            button.KeyDown += KeyDown;
        }
        internal static void Attach(Button button, ChatConversationPanelState state, Func<double> viewportWidth) =>
            Bindings.Add(button, new(button, state, viewportWidth));
        internal static void Detach(Button button)
        {
            if (!Bindings.TryGetValue(button, out var binding)) return;
            button.RemoveHandler(UIElement.PointerPressedEvent, binding._pressed);
            button.RemoveHandler(UIElement.PointerMovedEvent, binding._moved);
            button.RemoveHandler(UIElement.PointerReleasedEvent, binding._released);
            button.RemoveHandler(UIElement.PointerCaptureLostEvent, binding._released);
            button.RemoveHandler(UIElement.PointerCanceledEvent, binding._released);
            button.KeyDown -= binding.KeyDown;
            button.ReleasePointerCaptures();
            Bindings.Remove(button);
        }
        private int Direction => _button.FlowDirection == FlowDirection.RightToLeft ? -1 : 1;
        private void Pressed(object sender, PointerRoutedEventArgs args)
        {
            if (!args.GetCurrentPoint(_button).Properties.IsLeftButtonPressed) return;
            _origin = args.GetCurrentPoint(null).Position.X;
            _width = _state.EffectiveWidth(_viewportWidth());
            _button.CapturePointer(args.Pointer);
            args.Handled = true;
        }
        private void Moved(object sender, PointerRoutedEventArgs args)
        {
            if (_origin is { } origin)
                Resize(_width + Direction * (origin - args.GetCurrentPoint(null).Position.X));
        }
        private void Released(object sender, PointerRoutedEventArgs args)
        {
            _origin = null;
            _button.ReleasePointerCapture(args.Pointer);
        }
        private void KeyDown(object sender, KeyRoutedEventArgs args)
        {
            var currentWidth = _state.EffectiveWidth(_viewportWidth());
            var width = args.Key switch
            {
                VirtualKey.Left => currentWidth + Direction * ChatConversationPanelState.ResizeStep,
                VirtualKey.Right => currentWidth - Direction * ChatConversationPanelState.ResizeStep,
                VirtualKey.Home => ChatConversationPanelState.MinimumPanelWidth,
                VirtualKey.End => ChatConversationPanelState.MaximumWidthForViewport(_viewportWidth()),
                _ => (double?)null
            };
            if (width is null) return;
            Resize(width.Value);
            args.Handled = true;
        }
        private void Resize(double width) =>
            _state.Resize(width, ChatConversationPanelState.MaximumWidthForViewport(_viewportWidth()));
    }
}
