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
    Func<ChatWorkspaceFileRequest, CancellationToken, Task<ChatWorkspaceFileResult>>? LoadWorkspaceFile = null);

/// <summary>Applies native tab selection, split sizing, compact presentation and focus.</summary>
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
        var open = selected is not null;
        var narrow = width < ChatConversationPanelState.MinimumConversationWidth +
            ChatConversationPanelState.MinimumPanelWidth + ChatConversationPanelState.DividerWidth;
        var presented = open && (!narrow || showCompact && !state.Passive);
        var conversationVisible = !narrow || !presented;
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
            })
            .OnUnmount(control =>
            {
                ((TabView)control).TabItemsChanged -= adoptNativeOrder;
                tabView.Current = null;
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
        var panel = Grid([GridSize.Star()], [GridSize.Auto, GridSize.Star()],
                tabStrip.Grid(row: 0),
                Grid([GridSize.Star()], [GridSize.Star()], bodies).Grid(row: 1))
            .Background(Theme.Ref("ChatCanvasBrush"))
            .AutomationId("ConversationPanel")
            .IsVisible(presented)
            .Set(control =>
            {
                control.Style = (Style)Application.Current.Resources["ChatCanvasStyle"];
                control.ClearValue(Microsoft.UI.Xaml.Controls.Panel.BackgroundProperty);
                focus.Current?.Update(control, narrow, presented, TogglePresented);
            })
            .WithKey("conversation-panel");
        var panelWidth = open && !narrow ? state.EffectiveWidth(width) : 0;
        var resize = Button(
                Border(null).Background(Theme.Ref("ControlStrokeColorDefaultBrush")).Width(4).Height(40), () => { })
            .AutomationName(Text("Resize"))
            .HelpText(Text("ResizeHelp"))
            .AutomationId("ConversationPanelResize")
            .ToolTip(Text("Resize"))
            .Resources(ChatVisuals.ToolbarButtonResources)
            .Padding(0).MinWidth(0).Width(ChatConversationPanelState.DividerWidth)
            .IsVisible(open && !narrow)
            .OnMount(control => PanelResize.Attach((Button)control, state, () => viewportWidth.Current))
            .OnUnmount(control => PanelResize.Detach((Button)control));

        var toggleText = presented ? "Back" : "Show";
        return Grid([GridSize.Star(), GridSize.Px(open && !narrow ? ChatConversationPanelState.DividerWidth : 0), GridSize.Px(panelWidth)],
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
                    .IsHitTestVisible(false)
                    .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw)
                    .AutomationId("ConversationPanelDivider")
                    .IsVisible(open && !narrow)
                    .WithKey("conversation-panel-divider")
                    .Grid(column: 1, row: 1),
                resize.Grid(column: 1, row: 1),
                panel.Grid(column: narrow ? 0 : 2, row: 1))
            .OnMount(control =>
            {
                ChatVisuals.Observe(control, setWidth);
                focus.Current = new PanelInteraction((Grid)control, state,
                    index => tabView.Current is { } view && index >= 0 && index < view.TabItems.Count
                        ? view.TabItems[index] as Control : null);
            })
            .OnUnmount(control =>
            {
                ChatVisuals.StopObserving(control);
                focus.Current?.Dispose();
                focus.Current = null;
            });
    }

    private static string Text(string name) => LocalizationHelper.GetString("Chat_Panel_" + name);

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
        private Action? _toggle;
        private long _focusRevision;
        private long _openVersion;
        private long _closeRevision;
        private bool _closing;

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
            if (_state.Selected is not null && _state.OpenVersion != _openVersion &&
                _root.XamlRoot is { } root &&
                FocusManager.GetFocusedElement(root) is Control opener &&
                (_panel is null || !IsWithin(opener, _panel)))
            {
                _opener = opener;
                _openerId = Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(opener);
            }
        }

        internal void SetConversation(FrameworkElement conversation) => _conversation = conversation;

        internal void Update(FrameworkElement panel, bool narrow, bool presented, Action toggle)
        {
            _panel = panel;
            _narrow = narrow;
            _toggle = toggle;
            if (presented && !_state.Passive &&
                (!_presented || _selected != _state.Selected || _openVersion != _state.OpenVersion))
            {
                QueueFocus(SelectedTab, () => FocusManager.FindFirstFocusableElement(panel) as Control);
            }
            _presented = presented;
            _selected = _state.Selected;
            _openVersion = _state.OpenVersion;
            panel.TabFocusNavigation = KeyboardNavigationMode.Local;
            _root.TabFocusNavigation = narrow && presented ? KeyboardNavigationMode.Cycle : KeyboardNavigationMode.Local;
        }

        private void QueueFocus(Func<Control?> target, Func<Control?>? fallback = null)
        {
            var revision = ++_focusRevision;
            var generation = _state.Revision;
            void Attempt(int remaining) => _root.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (revision != _focusRevision || generation != _state.Revision || !_root.IsLoaded)
                        return;
                    if (TryFocus(target())) return;
                    // A newly presented tab item can load a layout pass after the panel; retry before falling back.
                    if (remaining > 1) Attempt(remaining - 1);
                    else TryFocus(fallback?.Invoke());
                });
            Attempt(FocusAttempts);
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
            if (_state.Selected is not { } selected || _root.XamlRoot is not { } root) return;
            var within = _panel is { } panel && IsWithin(FocusManager.GetFocusedElement(root) as DependencyObject, panel);
            if (args.Key == VirtualKey.Escape && (within || _narrow && _presented))
            {
                BeforeClose(selected);
                _state.Close(selected);
                AfterClose();
                args.Handled = true;
            }
            else if (args.Key == VirtualKey.F6)
            {
                if (_narrow) _toggle?.Invoke();
                if (within) Restore();
                else if (_panel is { } current)
                    QueueFocus(SelectedTab, () => FocusManager.FindFirstFocusableElement(current) as Control);
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
