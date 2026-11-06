// Native tab strip beside a stable conversation subtree. Use with the root-owned
// ChatConversationPanelState; tab bodies stay mounted while selection changes.
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Helpers;
using Windows.System;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ReactorConversationPanelHostProps(
    Element Conversation, ChatConversationPanelState State, Action<bool>? ConversationVisibilityChanged = null,
    ChatTimelinePresentationContext? Timeline = null);

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
        var presented = open && (!narrow || showCompact);
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

        var tabStrip = TabView((int?)Array.IndexOf(tabs, selected), index =>
            {
                if (index >= 0 && index < tabs.Length) state.Select(tabs[index]);
            }, tabs.Select(tab => Tab(Text("Details"), Empty()) with { IsClosable = true }).ToArray())
            .IsAddTabButtonVisible(false)
            .CanDragTabs(false).CanReorderTabs(false)
            .TabWidthMode(TabViewWidthMode.SizeToContent)
            .CloseButtonOverlayMode(TabViewCloseButtonOverlayMode.Always)
            .TabCloseRequested(index =>
            {
                if (index < 0 || index >= tabs.Length) return;
                state.Close(tabs[index]);
                focus.Current?.AfterClose();
            })
            .AutomationName(Text("Details"))
            .AutomationId("ConversationPanelTabs");

        var bodies = tabs.Select(slot =>
            Border(state.Details is { } request && state.Resolve(request) is { } entry
                    ? ToolCallCardRenderer.BuildPanelDetails(
                        Props.Timeline ?? new(request.SessionKey, [], false, null), entry)
                    : TextBlock(Text("Unavailable")).TextWrapping(TextWrapping.Wrap).Margin(16))
                .WithKey(state.Details?.ToString() ?? slot)
                .IsVisible(slot == selected))
            .Cast<Element>().ToArray();
        var panel = Grid([GridSize.Star()], [GridSize.Auto, GridSize.Star()],
                tabStrip.Grid(row: 0),
                Grid([GridSize.Star()], [GridSize.Star()], bodies).Grid(row: 1))
            .Background(Theme.Ref("LayerFillColorDefaultBrush"))
            .AutomationId("ConversationPanel")
            .IsVisible(presented)
            .Set(control => focus.Current?.Update(control, narrow, presented, () => setShowCompact(!showCompact)))
            .WithKey("conversation-panel");
        var panelWidth = open && !narrow ? state.EffectiveWidth(width) : 0;
        var resize = Button(
                Border(null).Background(Theme.Ref("ControlStrokeColorDefaultBrush")).Width(4).Height(40), () => { })
            .AutomationName(Text("Resize"))
            .AutomationId("ConversationPanelResize")
            .ToolTip(Text("Resize"))
            .Resources(ChatVisuals.ToolbarButtonResources)
            .Padding(0).MinWidth(0).Width(ChatConversationPanelState.DividerWidth)
            .IsVisible(open && !narrow)
            .OnMount(control => PanelResize.Attach((Button)control, state, () => viewportWidth.Current))
            .OnUnmount(control => PanelResize.Detach((Button)control));

        return Grid([GridSize.Star(), GridSize.Px(open && !narrow ? ChatConversationPanelState.DividerWidth : 0), GridSize.Px(panelWidth)],
                [GridSize.Auto, GridSize.Star()],
                Button(TextBlock(Text(presented ? "Back" : "Show")), () =>
                    {
                        setShowCompact(!showCompact);
                        if (presented) focus.Current?.Restore();
                    })
                    .AutomationId("ConversationPanelToggle")
                    .AutomationName(Text(presented ? "Back" : "Show"))
                    .IsVisible(open && narrow)
                    .HAlign(HorizontalAlignment.Left).Margin(8)
                    .Grid(row: 0),
                Border(Props.Conversation).WithKey("chat-conversation")
                    .AutomationId("ChatConversation")
                    .IsVisible(conversationVisible)
                    .Set(control => focus.Current?.SetConversation(control))
                    .Grid(row: 1),
                resize.Grid(column: 1, row: 1),
                panel.Grid(column: narrow ? 0 : 2, row: 1))
            .OnMount(control =>
            {
                ChatVisuals.Observe(control, setWidth);
                focus.Current = new PanelInteraction((Grid)control, state);
            })
            .OnUnmount(control =>
            {
                ChatVisuals.StopObserving(control);
                focus.Current?.Dispose();
                focus.Current = null;
            });
    }

    private static string Text(string name) => LocalizationHelper.GetString("Chat_Panel_" + name);

    private sealed class PanelInteraction : IDisposable
    {
        private readonly Grid _root;
        private readonly ChatConversationPanelState _state;
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

        internal PanelInteraction(Grid root, ChatConversationPanelState state)
        {
            _root = root;
            _state = state;
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
            if (presented && (!_presented || _selected != _state.Selected || _openVersion != _state.OpenVersion))
            {
                QueueFocus(() => FocusManager.FindFirstFocusableElement(panel) as Control);
            }
            _presented = presented;
            _selected = _state.Selected;
            _openVersion = _state.OpenVersion;
            panel.TabFocusNavigation = narrow ? KeyboardNavigationMode.Cycle : KeyboardNavigationMode.Local;
        }

        private void QueueFocus(Func<Control?> target, Func<Control?>? fallback = null)
        {
            var revision = ++_focusRevision;
            var generation = _state.Revision;
            _root.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (revision != _focusRevision || generation != _state.Revision || !_root.IsLoaded)
                    return;
                if (!TryFocus(target())) TryFocus(fallback?.Invoke());
            });
        }

        private bool TryFocus(Control? control)
        {
            if (control is not { IsLoaded: true, IsEnabled: true } || control.XamlRoot != _root.XamlRoot)
                return false;
            for (DependencyObject? element = control; element is not null; element = VisualTreeHelper.GetParent(element))
                if (element is UIElement { Visibility: Visibility.Collapsed }) return false;
            return control.Focus(FocusState.Programmatic);
        }

        internal void Restore() => QueueFocus(
            () => _opener is { } opener &&
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(opener) == _openerId
                    ? opener : null,
            () => _conversation is { } conversation
                ? FocusManager.FindFirstFocusableElement(conversation) as Control : null);
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
                _state.Close(selected);
                AfterClose();
                args.Handled = true;
            }
            else if (args.Key == VirtualKey.F6)
            {
                if (_narrow) _toggle?.Invoke();
                if (within) Restore();
                else if (_panel is { } current) QueueFocus(() => FocusManager.FindFirstFocusableElement(current) as Control);
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
