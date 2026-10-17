using System.Runtime.InteropServices;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using OpenClawTray.Windows;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class DesktopCompanionWindowTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData(AppNotificationSeverity.Informational, OnboardingMascotMood.Happy)]
    [InlineData(AppNotificationSeverity.Success, OnboardingMascotMood.Happy)]
    [InlineData(AppNotificationSeverity.Warning, OnboardingMascotMood.Attentive)]
    [InlineData(AppNotificationSeverity.Error, OnboardingMascotMood.Sad)]
    public async Task Notification_UsesExistingMascot_ShowsLiteralText_AndDismissesLocally(
        AppNotificationSeverity severity, OnboardingMascotMood mood)
    {
        await WithWindowAsync(async window =>
        {
            window.ApplyTheme("Dark");
            window.Present(new()
            {
                Title = "OpenClaw notification",
                Message = "<b>Literal text</b> & a lobster",
                Severity = severity
            });
            var root = (FrameworkElement)window.Content;
            await TestSupport.WaitForRenderedConditionAsync(
                () => Control<StackPanel>(root, "DesktopCompanionBubble").ActualHeight > 0, "notification layout");
            Assert.Equal(ElementTheme.Dark, root.RequestedTheme);
            Assert.Equal(mood, Mascot(root).Mood);
            Assert.Equal("<b>Literal text</b> & a lobster", Control<TextBlock>(root, "DesktopCompanionMessage").Text);
            Assert.Equal(Visibility.Visible, Control<StackPanel>(root, "DesktopCompanionBubble").Visibility);
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(Control<Button>(root, "DesktopCompanionDismiss"));
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await TestSupport.WaitForRenderedConditionAsync(
                () => Control<StackPanel>(root, "DesktopCompanionBubble").Visibility == Visibility.Collapsed, "bubble dismissal");
            Assert.Equal(OnboardingMascotMood.Idle, Mascot(root).Mood);
            Assert.Empty(Control<TextBlock>(root, "DesktopCompanionMessage").Text);
        });
    }

    [Fact]
    public Task NotificationTimeout_ReturnsToIdle() =>
        WithWindowAsync(async window =>
        {
            var root = (FrameworkElement)window.Content;
            var bubble = Control<StackPanel>(root, "DesktopCompanionBubble");
            window.Present(new() { Title = "Temporary", Message = "This bubble will clear automatically." });
            var dismissed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var token = bubble.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) =>
            {
                if (bubble.Visibility == Visibility.Collapsed) dismissed.TrySetResult();
            });
            try { await dismissed.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { bubble.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, token); }
            await TestSupport.WaitForRenderedConditionAsync(
                () => Mascot(root).Mood == OnboardingMascotMood.Idle, "timeout mood");
        });

    [Fact]
    public Task Overlay_DoesNotActivate_IsTopmost_AndUnusedAreaIsOutsideNativeHitRegion() =>
        WithWindowAsync(window =>
        {
            Assert.False(window.AppWindow.IsShownInSwitchers);
            var presenter = Assert.IsType<OverlappedPresenter>(window.AppWindow.Presenter);
            Assert.True(presenter.IsAlwaysOnTop);
            Assert.False(presenter.IsResizable);
            var foreground = GetForegroundWindow();
            window.Present(new() { Title = "Hello", Message = "A new notification" });
            Assert.Equal(foreground, GetForegroundWindow());
            window.Present(null);
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var region = CreateRectRgn(0, 0, 0, 0);
            try
            {
                Assert.NotEqual(0, GetWindowRgn(hwnd, region));
                Assert.False(PtInRegion(region, 10, 10));
                Assert.True(PtInRegion(region,
                    window.AppWindow.Size.Width - (int)(90 * ((FrameworkElement)window.Content).XamlRoot.RasterizationScale),
                    window.AppWindow.Size.Height - (int)(90 * ((FrameworkElement)window.Content).XamlRoot.RasterizationScale)));
            }
            finally { DeleteObject(region); }
            return Task.CompletedTask;
        });

    [Fact]
    public Task Overlay_DisablesNativeFrameAndShadow_AcrossNotificationAndThemeUpdates() =>
        WithWindowAsync(async window =>
        {
            var root = (FrameworkElement)window.Content;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            AssertNoNativeFrame();
            window.Present(new() { Title = "Frameless", Message = "Only the speech bubble and lobster should be visible." });
            await TestSupport.WaitForRenderedConditionAsync(
                () => Control<StackPanel>(root, "DesktopCompanionBubble").ActualHeight > 0, "frameless bubble layout");
            AssertNoNativeFrame();
            window.ApplyTheme("Light");
            await TestSupport.WaitForRenderedConditionAsync(() => root.ActualTheme == ElementTheme.Light, "frameless light theme");
            AssertNoNativeFrame();
            window.Present(null);
            await TestSupport.WaitForRenderedConditionAsync(
                () => Control<StackPanel>(root, "DesktopCompanionBubble").Visibility == Visibility.Collapsed,
                "frameless idle");
            AssertNoNativeFrame();

            void AssertNoNativeFrame()
            {
                Assert.True(GetClientRect(hwnd, out var client));
                Assert.Equal(window.AppWindow.Size.Width, client.Right - client.Left);
                Assert.Equal(window.AppWindow.Size.Height, client.Bottom - client.Top);
                Assert.Equal(0, DwmGetWindowAttribute(hwnd, 1 /* DWMWA_NCRENDERING_ENABLED */, out var enabled, sizeof(int)));
                Assert.Equal(0, enabled);
                Assert.Equal(0, DwmGetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, out var corners, sizeof(int)));
                Assert.Equal(1, corners); // DWMWCP_DONOTROUND
                Assert.Equal(0, Assert.IsType<WinUIEx.TransparentTintBackdrop>(window.SystemBackdrop).TintColor.A);
            }
        });

    [Fact]
    public Task LongMessage_RemainsScrollable_AndClearRemovesSensitiveText() =>
        WithWindowAsync(async window =>
        {
            window.Present(new() { Title = "Long message", Message = string.Join("\n", Enumerable.Repeat("Lobster news", 100)) });
            var root = (FrameworkElement)window.Content;
            await TestSupport.WaitForRenderedConditionAsync(() =>
                TestSupport.FindDescendants<ScrollViewer>(root).Any(scroll => scroll.ScrollableHeight > 0),
                "long message scrolling");
            window.Present(null);
            await TestSupport.WaitForRenderedConditionAsync(
                () => Control<StackPanel>(root, "DesktopCompanionBubble").Visibility == Visibility.Collapsed,
                "sensitive text removal");
            Assert.Empty(Control<TextBlock>(root, "DesktopCompanionTitle").Text);
            Assert.Empty(Control<TextBlock>(root, "DesktopCompanionMessage").Text);
        });

    [Fact]
    public Task Reactor_ReconcilesNotifications_RetainsMascot_AndDoesNotReplayOnThemeChange() =>
        WithWindowAsync(async window =>
        {
            var host = Assert.IsType<ReactorHostControl>(window.Content);
            var mascot = Mascot(host);
            var message = Control<TextBlock>(host, "DesktopCompanionMessage");
            window.ApplyTheme("Dark");
            window.Present(new() { Title = "First", Message = "First arrival" });
            await TestSupport.WaitForRenderedConditionAsync(() => message.Text == "First arrival", "first Reactor render");
            Assert.Equal(OnboardingMascotMood.Happy, mascot.Mood);
            var bubble = Control<StackPanel>(host, "DesktopCompanionBubble");
            var tail = Assert.Single(bubble.Children.OfType<Microsoft.UI.Xaml.Shapes.Path>());
            var darkFill = Assert.IsType<SolidColorBrush>(tail.Fill).Color;
            var moodChanges = 0;
            var token = mascot.RegisterPropertyChangedCallback(OnboardingMascot.MoodProperty, (_, _) => moodChanges++);
            try
            {
                window.ApplyTheme("Light");
                await TestSupport.WaitForRenderedConditionAsync(() => host.ActualTheme == ElementTheme.Light, "theme render");
                await Task.Delay(100);
                Assert.NotEqual(darkFill, Assert.IsType<SolidColorBrush>(tail.Fill).Color);
                Assert.Equal(0, moodChanges);
                window.Present(new() { Title = "Second", Message = "Second arrival" });
                await TestSupport.WaitForRenderedConditionAsync(() => message.Text == "Second arrival", "second Reactor render");
                Assert.Same(mascot, Mascot(host));
                Assert.Same(message, Control<TextBlock>(host, "DesktopCompanionMessage"));
                Assert.Equal(2, moodChanges);
                Assert.Equal(OnboardingMascotMood.Happy, mascot.Mood);
            }
            finally { mascot.UnregisterPropertyChangedCallback(OnboardingMascot.MoodProperty, token); }
        });

    [Fact]
    public async Task OpenNotifications_InvokesHostActionOnce_AndClearsBubble()
    {
        var opened = 0;
        await WithWindowAsync(async window =>
        {
            var host = Assert.IsType<ReactorHostControl>(window.Content);
            window.Present(new() { Title = "Open notifications", Message = "Open the canonical notification history" });
            await TestSupport.WaitForRenderedConditionAsync(
                () => Control<StackPanel>(host, "DesktopCompanionBubble").ActualHeight > 0, "notification action");
            var action = Control<HyperlinkButton>(host, "DesktopCompanionNotifications");
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(action);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await TestSupport.WaitForRenderedConditionAsync(
                () => Control<StackPanel>(host, "DesktopCompanionBubble").Visibility == Visibility.Collapsed,
                "notification action dismissal");
            Assert.Equal(1, opened);
            Assert.Empty(Control<TextBlock>(host, "DesktopCompanionMessage").Text);
        }, () => opened++);
    }

    [Fact]
    public Task ClosingWithPendingRender_UnmountsReactor_StopsAnimator_AndIgnoresLatePresentation() =>
        WithWindowAsync(async window =>
        {
            var host = Assert.IsType<ReactorHostControl>(window.Content);
            var mascot = Mascot(host);
            window.Present(new() { Title = "Queued", Message = "Do not render after close" });
            var view = (IDesktopCompanionView)window;
            view.Close();
            view.Close();
            window.Present(new() { Title = "Late", Message = "Ignored" });
            await Task.Delay(100);
            Assert.Null(host.Content);
            Assert.False(host.IsLoaded);
            Assert.False(mascot.IsAnimationEnabled);
        });

    private sealed class NativePointerTheoryAttribute : TheoryAttribute
    {
        public NativePointerTheoryAttribute()
        {
            if (Environment.GetEnvironmentVariable("OPENCLAW_DESKTOP_COMPANION_POINTER_PROOF") != "1")
                Skip = "Requires an interactive desktop and explicitly enabled native pointer proof.";
        }
    }

    [NativePointerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Drag_MovesWithPointer_WithoutOpeningMenu_AndPreservesClick(bool showBubble) =>
        WithWindowAsync(async window =>
        {
            await PositionForPointerProofAsync(window);
            var root = (FrameworkElement)window.Content;
            var target = Control<Button>(root, "DesktopCompanionMascot");
            if (showBubble)
            {
                window.Present(new() { Title = "Move me", Message = "The bubble follows the lobster." });
                await TestSupport.WaitForRenderedConditionAsync(
                    () => Control<StackPanel>(root, "DesktopCompanionBubble").ActualHeight > 0, "draggable bubble");
            }
            var origin = window.AppWindow.Position;
            await DragAsync(window, target, -120, -60);
            Assert.Equal(new PointInt32(origin.X - 120, origin.Y - 60), window.AppWindow.Position);
            Assert.Empty(VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot));
            Assert.True(target.PointerCaptures is not { Count: > 0 });

            await DragAsync(window, target, 60, 30);
            Assert.Equal(new PointInt32(origin.X - 60, origin.Y - 30), window.AppWindow.Position);
            Assert.Empty(VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot));
            Assert.Equal(showBubble ? Visibility.Visible : Visibility.Collapsed,
                Control<StackPanel>(root, "DesktopCompanionBubble").Visibility);

            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(target);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await TestSupport.WaitForRenderedConditionAsync(
                () => VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Count > 0, "menu after dragging");
        });

    [NativePointerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Click_OpensMenu_WithoutRepositioning(bool rightClick) =>
        WithWindowAsync(async window =>
        {
            await PositionForPointerProofAsync(window);
            var root = (FrameworkElement)window.Content;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var origin = window.AppWindow.Position;
            var point = MascotCenter(window);
            Assert.True(GetCursorPos(out var saved));
            var release = rightClick ? 0x0010u : 0x0004u;
            var pressed = false;
            try
            {
                MoveOwnedPointer(hwnd, point);
                await Task.Delay(50);
                SendMouseButton(rightClick ? 0x0008u : 0x0002u);
                pressed = true;
                await Task.Delay(50);
                point.X += 2;
                point.Y += 2;
                MoveOwnedPointer(hwnd, point);
                await Task.Delay(50);
                SendMouseButton(release);
                pressed = false;
                await TestSupport.WaitForRenderedConditionAsync(
                    () => VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Count > 0, "pointer menu");
                Assert.Equal(origin, window.AppWindow.Position);
            }
            finally
            {
                if (pressed) SendMouseButton(release);
                Assert.True(SetCursorPos(saved.X, saved.Y));
            }
        });

    [NativePointerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LostPointerCapture_StopsMoving_AndPreservesClick(bool moveBeforeCaptureLoss) =>
        WithWindowAsync(async window =>
        {
            await PositionForPointerProofAsync(window);
            var root = (FrameworkElement)window.Content;
            var target = Control<Button>(root, "DesktopCompanionMascot");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            Assert.True(GetCursorPos(out var saved));
            var point = MascotCenter(window);
            var pressed = false;
            try
            {
                MoveOwnedPointer(hwnd, point);
                await Task.Delay(50);
                SendMouseButton(0x0002);
                pressed = true;
                await TestSupport.WaitForRenderedConditionAsync(() => target.PointerCaptures is { Count: > 0 }, "pointer capture");
                if (moveBeforeCaptureLoss)
                {
                    var origin = window.AppWindow.Position;
                    point.X -= 12;
                    MoveOwnedPointer(hwnd, point);
                    await TestSupport.WaitForRenderedConditionAsync(
                        () => window.AppWindow.Position.X == origin.X - 12, "drag before capture loss");
                }
                target.ReleasePointerCaptures();
                await Task.Delay(100);
                var stopped = window.AppWindow.Position;
                point.X -= 12;
                MoveOwnedPointer(hwnd, point);
                await Task.Delay(100);
                Assert.Equal(stopped, window.AppWindow.Position);
                SendMouseButton(0x0004);
                pressed = false;
                await Task.Delay(100);
                Assert.Empty(VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot));
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(target);
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                await TestSupport.WaitForRenderedConditionAsync(
                    () => VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Count > 0, "menu after canceled drag");
            }
            finally
            {
                if (pressed) SendMouseButton(0x0004);
                Assert.True(SetCursorPos(saved.X, saved.Y));
            }
        });

    private static async Task PositionForPointerProofAsync(DesktopCompanionWindow window)
    {
        OnboardingNativeProof.ActivateOwned(window);
        // Avoid the shell's bottom-right notification surface without interacting with it.
        var work = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        window.AppWindow.Move(new(work.X + (work.Width - window.AppWindow.Size.Width) / 2,
            work.Y + (work.Height - window.AppWindow.Size.Height) / 2));
        await OnboardingNativeProof.NextCompositionAsync(TimeSpan.FromMilliseconds(150));
    }

    private static async Task DragAsync(DesktopCompanionWindow window, Button target, int dx, int dy)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var origin = window.AppWindow.Position;
        var point = MascotCenter(window);
        Assert.True(GetCursorPos(out var saved));
        var pressed = false;
        try
        {
            MoveOwnedPointer(hwnd, point);
            await Task.Delay(50);
            SendMouseButton(0x0002);
            pressed = true;
            await TestSupport.WaitForRenderedConditionAsync(() => target.PointerCaptures is { Count: > 0 }, "drag capture");
            for (var step = 1; step <= 10; step++)
            {
                MoveOwnedPointer(hwnd, new() { X = point.X + dx * step / 10, Y = point.Y + dy * step / 10 });
                await TestSupport.WaitForRenderedConditionAsync(
                    () => window.AppWindow.Position == new PointInt32(origin.X + dx * step / 10, origin.Y + dy * step / 10),
                    $"drag step {step}");
            }
            SendMouseButton(0x0004);
            pressed = false;
            await Task.Delay(150);
            Assert.True(target.PointerCaptures is not { Count: > 0 });
        }
        finally
        {
            if (pressed) SendMouseButton(0x0004);
            Assert.True(SetCursorPos(saved.X, saved.Y));
        }
    }

    private static NativePoint MascotCenter(DesktopCompanionWindow window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        Assert.True(GetWindowRect(hwnd, out var rect));
        var half = (int)Math.Round(90 * ((FrameworkElement)window.Content).XamlRoot.RasterizationScale);
        return new() { X = rect.Right - half, Y = rect.Bottom - half };
    }

    private static void MoveOwnedPointer(IntPtr hwnd, NativePoint point)
    {
        var at = WindowFromPoint(point);
        Assert.True(hwnd == GetAncestor(at, 2 /* GA_ROOT */),
            $"The owned overlay is obscured at ({point.X},{point.Y}); no pointer input was sent.");
        Assert.True(SetCursorPos(point.X, point.Y));
    }

    private static void SendMouseButton(uint flags)
    {
        NativeInput[] inputs = [new() { Mouse = new() { Flags = flags } }];
        Assert.Equal(1u, SendInput(1, inputs, Marshal.SizeOf<NativeInput>()));
    }

    private Task WithWindowAsync(Func<DesktopCompanionWindow, Task> test, Action? openNotifications = null) =>
        ui.RunOnUIAsync(async () =>
        {
            var window = new DesktopCompanionWindow(openNotifications ?? (() => { }), () => { }, () => { });
            try
            {
                var foreground = GetForegroundWindow();
                ((IDesktopCompanionView)window).Show();
                await TestSupport.WaitForRenderedConditionAsync(() => ((FrameworkElement)window.Content).IsLoaded,
                    "desktop lobster Loaded");
                Assert.Equal(foreground, GetForegroundWindow());
                await test(window);
            }
            finally { ((IDesktopCompanionView)window).Close(); }
        });

    private static T Control<T>(FrameworkElement root, string id) where T : FrameworkElement =>
        Assert.Single(TestSupport.FindLogical<T>(root), element => AutomationProperties.GetAutomationId(element) == id);

    private static OnboardingMascot Mascot(FrameworkElement root) =>
        Assert.Single(TestSupport.FindLogical<OnboardingMascot>(root));

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr Extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput { public uint Type; public MouseInput Mouse; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
}
