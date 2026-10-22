using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClawTray.Helpers;
using Windows.System;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ReactorConversationPanelHostProps(
    Element Conversation,
    ChatConversationPanelState State,
    string? SessionKey,
    Func<string, Element> RenderContent,
    Func<string, string> Title);

/// <summary>One fixed native conversation branch, with an independently scrollable companion panel.</summary>
internal sealed class ReactorConversationPanelHost : Component<ReactorConversationPanelHostProps>
{
    public override Element Render()
    {
        var props = Props;
        var state = props.State;
        var (_, setRevision) = UseState(0L, threadSafe: true);
        var (width, setWidth) = UseState(800d);
        var (compactPanelPresented, setCompactPanelPresented) = UseState(true);
        var toggleCompactPanel = UseRef<Action>(() => { });
        toggleCompactPanel.Current = () => setCompactPanelPresented(!compactPanelPresented);
        var announcedAccount = UseRef<string?>(null);
        var announcementRevision = UseRef(0L);
        var pendingAnnouncementLoad = UseRef<RoutedEventHandler?>(null);
        var focus = UseRef<PanelFocusBinding?>(null);
        var grip = UseRef<PanelResizeBinding?>(null);
        UseEffect((Func<Action>)(() =>
        {
            long revision = 0;
            void Changed() => setRevision(Interlocked.Increment(ref revision));
            state.Changed += Changed;
            return () => state.Changed -= Changed;
        }), state);

        var layout = state.Layout;
        var selected = layout.SelectedPanel;
        var open = selected is not null;
        var narrow = width < ChatConversationPanelState.MinimumConversationWidth +
            ChatConversationPanelState.MinimumPanelWidth + 12;
        var presented = open && (!narrow || compactPanelPresented);
        UseEffect((Func<Action>)(() =>
        {
            setCompactPanelPresented(true);
            return static () => { };
        }), selected ?? string.Empty);
        var panelWidth = open && !narrow
            ? Math.Min(layout.Width, width - ChatConversationPanelState.MinimumConversationWidth - 12)
            : 0;
        var contextReady = state.IsReady && state.Context?.Identity.SessionKey == props.SessionKey;
        var accountMessage = Text(state.AccountStatus switch
        {
            ChatConversationAccountStatus.Loading => "AccountLoading",
            ChatConversationAccountStatus.Profileless or ChatConversationAccountStatus.Unavailable => "AccountUnavailable",
            ChatConversationAccountStatus.Failed => "AccountFailed",
            ChatConversationAccountStatus.RequiresReconnect => "AccountChanged",
            _ => "Waiting"
        });
        var showAccountNotice = !open && props.SessionKey is not null &&
            state.AccountStatus != ChatConversationAccountStatus.Known;

        void Close()
        {
            if (selected is not null) state.Close(selected);
            focus.Current?.Restore();
        }

        var panel = Border(
            Grid([GridSize.Star()], [GridSize.Auto, GridSize.Star()],
                Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    TextBlock(selected is null ? string.Empty : props.Title(selected))
                        .Set(text => text.Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"])
                        .TextTrimming(TextTrimming.CharacterEllipsis)
                        .VAlign(VerticalAlignment.Center)
                        .Margin(12, 8)
                        .Grid(column: 0),
                    Button(Icon(FluentIconCatalog.Exit), Close)
                        .AutomationName(Text("Close"))
                        .AutomationId("ConversationPanelClose")
                        .ToolTip(Text("Close"))
                        .Resources(ChatVisuals.ToolbarButtonResources)
                        .MinWidth(40).MinHeight(40).Padding(8)
                        .Grid(column: 1))
                    .Grid(row: 0),
                (open && contextReady
                    ? props.RenderContent(selected!)
                    : TextBlock(accountMessage)
                        .TextWrapping(TextWrapping.Wrap)
                        .Margin(16)
                        .VAlign(VerticalAlignment.Top))
                    .WithKey("conversation-panel-content")
                    .Grid(row: 1)))
            .Background(Theme.Ref("LayerFillColorDefaultBrush"))
            .AutomationId("ConversationPanel")
            .AutomationName(selected is null ? string.Empty : props.Title(selected))
            .IsVisible(presented)
            .Set(border =>
            {
                border.TabFocusNavigation = narrow
                    ? KeyboardNavigationMode.Cycle : KeyboardNavigationMode.Local;
                focus.Current?.SetPanel(border, narrow, presented);
            })
            .WithKey("conversation-panel");

        var resize = Button(
                Border(null).Background(Theme.Ref("ControlStrokeColorDefaultBrush")).Width(4).Height(40),
                () => { })
            .AutomationName(Text("Resize"))
            .AutomationId("ConversationPanelResize")
            .ToolTip(Text("Resize"))
            .Resources(ChatVisuals.ToolbarButtonResources)
            .Padding(0).MinWidth(0).Width(12)
            .IsVisible(open && !narrow)
            .Set(button =>
            {
                AutomationProperties.SetItemStatus(button,
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, Text("WidthFormat"), layout.Width));
            })
            .OnMount(control => grip.Current = new PanelResizeBinding((Button)control, state))
            .OnUnmount(_ =>
            {
                grip.Current?.Dispose();
                grip.Current = null;
            })
            .Grid(column: 1);

        return Grid([GridSize.Star(), GridSize.Px(open && !narrow ? 12 : 0), GridSize.Px(panelWidth)],
                [GridSize.Auto, GridSize.Star()],
                Border(props.Conversation)
                    .WithKey("chat-conversation")
                    .IsVisible(!presented || !narrow)
                    .Grid(column: 0, row: 1),
                resize.Grid(row: 1),
                panel.Grid(column: narrow ? 0 : 2, row: 1),
                Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    TextBlock(accountMessage)
                        .TextWrapping(TextWrapping.Wrap)
                        .AutomationId("ConversationPanelAccountStatus")
                        .AutomationName(accountMessage)
                        .Set(text =>
                        {
                            // Do not add Theme.Ref here: the consumed Reactor build replaces the native caption style.
                            text.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
                            text.ClearValue(Microsoft.UI.Xaml.Controls.TextBlock.FontSizeProperty);
                            text.ClearValue(Microsoft.UI.Xaml.Controls.TextBlock.FontWeightProperty);
                            AutomationProperties.SetLiveSetting(text, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
                            var announcement = showAccountNotice ? accountMessage : null;
                            if (announcedAccount.Current == announcement) return;
                            if (pendingAnnouncementLoad.Current is { } previous)
                                text.Loaded -= previous;
                            pendingAnnouncementLoad.Current = null;
                            announcedAccount.Current = announcement;
                            var revision = ++announcementRevision.Current;
                            if (announcement is null) return;
                            void Announce()
                            {
                                if (revision == announcementRevision.Current && text.IsLoaded &&
                                    text.Visibility == Visibility.Visible)
                                    Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(text)
                                        ?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
                            }
                            if (text.IsLoaded) text.DispatcherQueue.TryEnqueue(Announce);
                            else
                            {
                                RoutedEventHandler? loaded = null;
                                loaded = (_, _) =>
                                {
                                    text.Loaded -= loaded;
                                    pendingAnnouncementLoad.Current = null;
                                    text.DispatcherQueue.TryEnqueue(Announce);
                                };
                                pendingAnnouncementLoad.Current = loaded;
                                text.Loaded += loaded;
                            }
                        })
                        .OnUnmount(control =>
                        {
                            if (pendingAnnouncementLoad.Current is { } loaded)
                                ((TextBlock)control).Loaded -= loaded;
                            pendingAnnouncementLoad.Current = null;
                            announcementRevision.Current++;
                            announcedAccount.Current = null;
                        })
                        .IsVisible(showAccountNotice)
                        .Margin(12, 8)
                        .Grid(column: 0),
                    Button(TextBlock(Text("Show")), () => setCompactPanelPresented(true))
                        .AutomationName(Text("Show"))
                        .AutomationId("ConversationPanelShow")
                        .IsVisible(open && narrow && !compactPanelPresented)
                        .HAlign(HorizontalAlignment.Right)
                        .Margin(8)
                        .Grid(column: 1))
                    .IsVisible(showAccountNotice || open && narrow && !compactPanelPresented)
                    .Grid(row: 0))
            .OnMount(control =>
            {
                ChatVisuals.Observe(control, setWidth);
                focus.Current = new PanelFocusBinding((Grid)control, state,
                    () => toggleCompactPanel.Current());
            })
            .OnUnmount(control =>
            {
                ChatVisuals.StopObserving(control);
                focus.Current?.Dispose();
                focus.Current = null;
            });
    }

    private static string Text(string key) => LocalizationHelper.GetString("Chat_Panel_" + key);

    private static Element Icon(string glyph) => TextBlock(glyph)
        .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
        .Set(text => text.IsTextScaleFactorEnabled = false)
        .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

    private sealed class PanelFocusBinding : IDisposable
    {
        private readonly Grid _root;
        private readonly ChatConversationPanelState _state;
        private FrameworkElement? _panel;
        private Control? _opener;
        private string? _selected;
        private bool _narrow;
        private bool _presented;
        private readonly Action _toggleCompact;
        private long _focusRevision;

        internal PanelFocusBinding(Grid root, ChatConversationPanelState state, Action toggleCompact)
        {
            _root = root;
            _state = state;
            _toggleCompact = toggleCompact;
            _root.KeyDown += KeyDown;
        }

        internal void SetPanel(FrameworkElement panel, bool narrow, bool presented)
        {
            _panel = panel;
            _narrow = narrow;
            var selected = _state.Layout.SelectedPanel;
            if (selected == _selected && presented == _presented) return;
            var opening = presented && (!_presented || selected != _selected);
            if (!_presented && opening)
                _opener = FocusManager.GetFocusedElement(_root.XamlRoot) as Control;
            _selected = selected;
            _presented = presented;
            if (opening)
            {
                var revision = ++_focusRevision;
                var generation = _state.Generation;
                _root.DispatcherQueue.TryEnqueue(() =>
                {
                    if (revision == _focusRevision && generation == _state.Generation &&
                        _selected == selected && _presented && _panel.IsLoaded &&
                        FocusManager.FindFirstFocusableElement(_panel) is Control first)
                        first.Focus(FocusState.Programmatic);
                });
            }
        }

        internal void Restore()
        {
            var opener = _opener;
            var revision = ++_focusRevision;
            var generation = _state.Generation;
            _root.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (revision == _focusRevision && generation == _state.Generation &&
                    _root.IsLoaded && opener is { IsLoaded: true, IsEnabled: true })
                    opener.Focus(FocusState.Programmatic);
            });
        }

        private void KeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (_state.Layout.SelectedPanel is not { } selected) return;
            var focused = FocusManager.GetFocusedElement(_root.XamlRoot) as DependencyObject;
            if (args.Key == VirtualKey.Escape && ChatConversationPanelNavigation.ShouldCloseOnEscape(
                _narrow, _presented, _panel is { } currentPanel && IsWithin(focused, currentPanel)))
            {
                _state.Close(selected);
                Restore();
                args.Handled = true;
            }
            else if (args.Key == VirtualKey.F6 && _panel is { IsLoaded: true } panel)
            {
                if (_narrow)
                {
                    _toggleCompact();
                    if (_presented) Restore();
                }
                else if (IsWithin(focused, panel)) Restore();
                else if (FocusManager.FindFirstFocusableElement(panel) is Control first)
                {
                    _focusRevision++;
                    first.Focus(FocusState.Programmatic);
                }
                args.Handled = true;
            }
        }

        private static bool IsWithin(DependencyObject? item, DependencyObject parent)
        {
            for (; item is not null; item = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(item))
                if (ReferenceEquals(item, parent)) return true;
            return false;
        }

        public void Dispose()
        {
            _focusRevision++;
            _root.KeyDown -= KeyDown;
        }
    }

    private sealed class PanelResizeBinding : IDisposable
    {
        private readonly Button _button;
        private readonly ChatConversationPanelState _state;
        private double? _origin;
        private double _width;

        internal PanelResizeBinding(Button button, ChatConversationPanelState state)
        {
            _button = button;
            _state = state;
            button.PointerPressed += Pressed;
            button.PointerMoved += Moved;
            button.PointerReleased += Released;
            button.PointerCaptureLost += Released;
            button.KeyDown += KeyDown;
        }

        private void Pressed(object sender, PointerRoutedEventArgs args)
        {
            if (!args.GetCurrentPoint(_button).Properties.IsLeftButtonPressed) return;
            _origin = args.GetCurrentPoint(null).Position.X;
            _width = _state.Layout.Width;
            _button.CapturePointer(args.Pointer);
            args.Handled = true;
        }

        private void Moved(object sender, PointerRoutedEventArgs args)
        {
            if (_origin is { } origin)
                _state.Resize(_width + origin - args.GetCurrentPoint(null).Position.X);
        }

        private void Released(object sender, PointerRoutedEventArgs args)
        {
            _origin = null;
            _button.ReleasePointerCapture(args.Pointer);
        }

        private void KeyDown(object sender, KeyRoutedEventArgs args)
        {
            var width = args.Key switch
            {
                VirtualKey.Left => _state.Layout.Width + Direction * ChatConversationPanelState.ResizeStep,
                VirtualKey.Right => _state.Layout.Width - Direction * ChatConversationPanelState.ResizeStep,
                VirtualKey.Home => ChatConversationPanelState.MinimumPanelWidth,
                VirtualKey.End => ChatConversationPanelState.MaximumPanelWidth,
                _ => (double?)null
            };
            if (width is null) return;
            _state.Resize(width.Value);
            args.Handled = true;
        }

        private int Direction => _button.FlowDirection == FlowDirection.RightToLeft ? -1 : 1;

        public void Dispose()
        {
            _button.PointerPressed -= Pressed;
            _button.PointerMoved -= Moved;
            _button.PointerReleased -= Released;
            _button.PointerCaptureLost -= Released;
            _button.KeyDown -= KeyDown;
        }
    }
}
