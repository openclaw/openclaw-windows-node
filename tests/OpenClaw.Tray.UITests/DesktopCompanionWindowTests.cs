using System.Runtime.InteropServices;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
}
