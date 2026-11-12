using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Win32;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using Windows.Graphics;
using Windows.System;
using WinUIEx;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Windows;

public sealed class DesktopCompanionWindow : WindowEx, IDesktopCompanionView
{
    private readonly Action _openNotifications;
    private readonly Action _openChat;
    private readonly Action _openSettings;
    private int _scalePercent = OpenClaw.Shared.DesktopCompanionScale.DefaultPercent;
    private readonly DispatcherTimer _bubbleTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DesktopCompanionPresentation _presentation = new();
    private readonly ReactorHostControl _host = new();
    private readonly DesktopCompanionChatHost _chat;
    private readonly IntPtr _hwnd;
    private readonly WinUIEx.Messaging.WindowMessageMonitor _frameMonitor;
    private StackPanel? _bubble;
    private Button? _mascotTarget;
    private XamlRoot? _xamlRoot;
    private PointInt32? _dragStart;
    private PointInt32 _windowStart;
    private string? _notificationGatewayId;
    private (int Width, int Height, double Dpi, double MascotSize, int? BubbleTop, bool Modal)? _regionLayout;
    private bool _closed, _positioning, _dragged, _bubbleHovered;

    public event EventHandler? HideRequested;

    internal DesktopCompanionWindow(Action openNotifications, Action openChat, Action openSettings)
    {
        _openNotifications = openNotifications;
        _openChat = openChat;
        _openSettings = openSettings;
        _chat = new DesktopCompanionChatHost(this, _presentation);
        Title = LocalizationHelper.GetString("DesktopCompanion_Name");
        SystemBackdrop = new TransparentTintBackdrop();
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _frameMonitor = DesktopCompanionNative.CreateFrameMonitor(_hwnd);
        _bubbleTimer.Tick += OnBubbleTimeout;
        _host.Loaded += OnLoaded;
        _host.LayoutUpdated += OnHostLayoutUpdated;
        _host.KeyDown += OnKeyDown;
        _host.OnRenderComplete = (_, _, _) =>
        {
            if (_closed) return;
            _host.UpdateLayout();
            UpdateRegion();
        };
        XamlInterop.Register(_host.Reconciler);
        var props = new DesktopCompanionSurfaceProps(
            _presentation, DismissBubble, OnOpenNotifications, OnReply, _chat.Content, OnBubbleMounted, OnMascotMounted);
        _host.Mount(_ => Component<DesktopCompanionSurface, DesktopCompanionSurfaceProps>(props));
        Content = _host;
        DesktopCompanionNative.ConfigureOverlay(_hwnd);
        Closed += OnClosed;
    }

    void IDesktopCompanionView.Show()
    {
        PositionInWorkArea(initial: true);
        UpdateRegion();
        AppWindow.Show(activateWindow: false);
    }

    public void ApplyTheme(string theme) => ThemeHelper.ApplyTheme(this, theme);

    public void ApplyScale(int percent)
    {
        if (_closed) return;
        var normalized = OpenClaw.Shared.DesktopCompanionScale.Normalize(percent);
        if (_scalePercent == normalized) return;
        _scalePercent = normalized;
        PositionInWorkArea();
    }

    void IDesktopCompanionView.Close()
    {
        if (!_closed) Close();
    }

    public void Present(AppNotification? notification)
    {
        if (_closed) return;
        if (_presentation.IsChatOpen)
        {
            if (notification is null) return;
            _chat.Hide();
            _bubbleHovered = false;
        }
        _bubbleTimer.Stop();
        if (notification is null) _bubbleHovered = false;
        _notificationGatewayId = (Application.Current as App)?.Registry?.GetActive()?.Id;
        _presentation.Present(notification);
        PositionInWorkArea();
        if (notification is not null && !_bubbleHovered)
            _bubbleTimer.Start();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_closed || _xamlRoot is not null) return;
        _xamlRoot = _host.XamlRoot;
        _xamlRoot.Changed += OnXamlRootChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        PositionInWorkArea();
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => PositionInWorkArea();
    private void OnDisplaySettingsChanged(object? sender, EventArgs args) => Reposition();
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs args) => Reposition();
    private void Reposition()
    {
        if (!DispatcherQueue.TryEnqueue(() => { if (!_closed) PositionInWorkArea(); }))
            Logger.Warn("Desktop lobster could not refresh its display placement.");
    }

    private void PositionInWorkArea(bool initial = false)
    {
        if (_closed || _positioning) return;
        _positioning = true;
        try
        {
            var scale = DesktopCompanionNative.GetDpiForWindow(_hwnd) / 96d;
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var mascotSize = Math.Min(DesktopCompanionPresentation.DefaultMascotSize * _scalePercent / 100,
                Math.Min(work.Width, work.Height) / scale);
            _presentation.SetMascotSize(mascotSize);
            var width = Math.Min(work.Width, (int)Math.Ceiling(Math.Max(_presentation.IsChatOpen ? 440 : 360, mascotSize) * scale));
            var height = Math.Min(work.Height, (int)Math.Ceiling(((_presentation.IsChatOpen ? 640 : 420) +
                mascotSize - DesktopCompanionPresentation.DefaultMascotSize) * scale));
            _chat.Content.MaxHeight = Math.Max(0, height / scale - mascotSize - 84);
            var pos = AppWindow.Position;
            var bounds = DesktopCompanionBounds.Clamp(
                initial ? work.X + work.Width - width - 16 : pos.X + AppWindow.Size.Width - width,
                initial ? work.Y + work.Height - height - 8 : pos.Y + AppWindow.Size.Height - height,
                width, height, new(work.X, work.Y, work.Width, work.Height));
            if (pos.X != bounds.X || pos.Y != bounds.Y ||
                AppWindow.Size.Width != width || AppWindow.Size.Height != height)
                AppWindow.MoveAndResize(new(bounds.X, bounds.Y, width, height));
            UpdateRegion();
        }
        finally { _positioning = false; }
    }

    private void UpdateRegion()
    {
        if (_closed) return;
        var scale = _host.XamlRoot?.RasterizationScale ?? DesktopCompanionNative.GetDpiForWindow(_hwnd) / 96d;
        int? bubbleTop = _bubble is { Visibility: Visibility.Visible, ActualHeight: > 0 }
            ? (int)Math.Floor(_bubble.TransformToVisual(_host).TransformPoint(new(0, 0)).Y * scale)
            : null;
        var layout = (Width: AppWindow.Size.Width, Height: AppWindow.Size.Height, Dpi: scale,
            MascotSize: _presentation.MascotSize, BubbleTop: bubbleTop, Modal: _presentation.InputPrompt is not null);
        if (_regionLayout == layout) return;
        DesktopCompanionNative.SetRegion(_hwnd, layout.Width, layout.Height, layout.Dpi, layout.MascotSize, layout.BubbleTop,
            modal: layout.Modal);
        _regionLayout = layout;
    }

    private void OnHostLayoutUpdated(object? sender, object args) => UpdateRegion();
    private void OnBubbleSizeChanged(object sender, SizeChangedEventArgs args) => UpdateRegion();
    private void OnBubbleTimeout(object? sender, object args) => Present(null);
    private void OnBubbleEntered(object sender, PointerRoutedEventArgs args)
    {
        _bubbleHovered = true;
        _bubbleTimer.Stop();
    }
    private void OnBubbleExited(object sender, PointerRoutedEventArgs args)
    {
        _bubbleHovered = false;
        if (!_presentation.IsChatOpen && _presentation.Notification is not null) _bubbleTimer.Start();
    }
    private void OnOpenNotifications()
    {
        DismissBubble();
        _openNotifications();
    }

    private void DismissBubble()
    {
        _bubbleTimer.Stop();
        _chat.Hide();
        _presentation.ShowChat(false);
        PositionInWorkArea();
        _mascotTarget?.Focus(FocusState.Programmatic);
    }

    private void OnReply()
    {
        if (_notificationGatewayId != (Application.Current as App)?.Registry?.GetActive()?.Id)
        {
            DismissBubble();
            (Application.Current as App)?.ShowHub("connection");
            return;
        }
        if (AppNotificationActionRoutes.TryGetChatSessionKey(_presentation.Notification?.ActionRoute, out var key))
            OpenQuickChat(key);
    }

    private void OpenQuickChat(string? sessionKey = null)
    {
        if (_closed) return;
        _bubbleTimer.Stop();
        _presentation.ShowChat(true);
        PositionInWorkArea();
        Activate();
        _ = _chat.OpenAsync(sessionKey);
    }

    private void OnBubbleMounted(FrameworkElement element)
    {
        _bubble = (StackPanel)element;
        _bubble.SizeChanged += OnBubbleSizeChanged;
        _bubble.PointerEntered += OnBubbleEntered;
        _bubble.PointerExited += OnBubbleExited;
    }

    private void OnMascotMounted(FrameworkElement element)
    {
        _mascotTarget = (Button)element;
        _mascotTarget.Click += OnMascotClick;
        // Button handles these before routed instance handlers. Observe handled events
        // while retaining its native keyboard, accessibility and pointer capture behavior.
        _mascotTarget.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnMascotPressed), true);
        _mascotTarget.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMascotMoved), true);
        _mascotTarget.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnMascotReleased), true);
        _mascotTarget.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnMascotCaptureLost), true);
        _mascotTarget.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnMascotCaptureLost), true);
        _mascotTarget.RightTapped += OnMascotRightTapped;
    }

    private void OnMascotPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!args.GetCurrentPoint(_mascotTarget).Properties.IsLeftButtonPressed) return;
        _dragStart = DesktopCompanionNative.CursorPosition();
        _windowStart = AppWindow.Position;
        _dragged = false;
        _mascotTarget?.CapturePointer(args.Pointer);
    }

    private void OnMascotMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_dragStart is not { } start) return;
        var cursor = DesktopCompanionNative.CursorPosition();
        var dx = cursor.X - start.X;
        var dy = cursor.Y - start.Y;
        if (!_dragged && Math.Abs(dx) + Math.Abs(dy) < 6) return;
        _dragged = true;
        AppWindow.Move(new(_windowStart.X + dx, _windowStart.Y + dy));
    }

    private void OnMascotReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_dragStart is null) return;
        _dragStart = null;
        _mascotTarget?.ReleasePointerCapture(args.Pointer);
        PositionInWorkArea();
        args.Handled = _dragged;
    }

    private void OnMascotCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        _dragStart = null;
        PositionInWorkArea();
        // Button can raise Click after releasing capture. Suppress only that gesture,
        // then allow keyboard/UI Automation invocation and subsequent ordinary clicks.
        if (!DispatcherQueue.TryEnqueue(() => { if (_dragStart is null) _dragged = false; }))
            Logger.Warn("Desktop lobster could not finish its drag gesture.");
    }

    private void OnMascotRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        ShowMenu(args.GetPosition(_mascotTarget));
        args.Handled = true;
    }

    private void OnMascotClick(object sender, RoutedEventArgs args)
    {
        if (!_dragged) OpenQuickChat();
    }

    private void ShowMenu(global::Windows.Foundation.Point? position)
    {
        if (_closed || _mascotTarget is null) return;
        var menu = new MenuFlyout();
        AddMenuItem(menu, "DesktopCompanion_Open", _openChat);
        AddMenuItem(menu, "DesktopCompanion_Notifications", _openNotifications);
        menu.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(menu, "DesktopCompanion_Settings", _openSettings);
        if (position is { } point) menu.ShowAt(_mascotTarget, point);
        else menu.ShowAt(_mascotTarget);
    }

    private static void AddMenuItem(MenuFlyout menu, string key, Action action)
    {
        var item = new MenuFlyoutItem { Text = LocalizationHelper.GetString(key) };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Application or VirtualKey.F10)
        {
            ShowMenu(null);
            args.Handled = true;
            return;
        }
        if (args.Key != VirtualKey.Escape) return;
        DismissBubble();
        args.Handled = true;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_closed) return;
        _closed = true;
        _bubbleTimer.Stop();
        _bubbleTimer.Tick -= OnBubbleTimeout;
        _frameMonitor.Dispose();
        if (_xamlRoot is { } root) root.Changed -= OnXamlRootChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _host.Loaded -= OnLoaded;
        _host.LayoutUpdated -= OnHostLayoutUpdated;
        _host.KeyDown -= OnKeyDown;
        _host.OnRenderComplete = null;
        if (_bubble is { } bubble)
        {
            bubble.SizeChanged -= OnBubbleSizeChanged;
            bubble.PointerEntered -= OnBubbleEntered;
            bubble.PointerExited -= OnBubbleExited;
        }
        if (_mascotTarget is { } target)
        {
            target.Click -= OnMascotClick;
            target.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnMascotPressed));
            target.RemoveHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMascotMoved));
            target.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnMascotReleased));
            target.RemoveHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnMascotCaptureLost));
            target.RemoveHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnMascotCaptureLost));
            target.RightTapped -= OnMascotRightTapped;
        }
        _chat.Dispose();
        _host.Dispose();
        Content = null;
        _presentation.Present(null);
        _bubble = null;
        _mascotTarget = null;
        HideRequested?.Invoke(this, EventArgs.Empty);
    }
}
