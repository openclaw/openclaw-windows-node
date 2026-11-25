using System.Runtime.InteropServices;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClaw.Shared.Themes;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using OpenClawTray.Theming;
using OpenClawTray.Windows;
using FunctionalTheme = OpenClawTray.FunctionalUI.Core.Theme;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class DesktopCompanionWindowTests(UIThreadFixture ui)
{
    [Fact]
    public Task AutomaticHide_ClosesBubbleButRetainsMountedMascotAndDesktopAnchor() =>
        WithWindowAsync(async window =>
        {
            var host = Assert.IsType<ReactorHostControl>(window.Content);
            var mascot = Mascot(host);
            var view = (IDesktopCompanionView)window;
            var hideRequests = 0;
            window.HideRequested += (_, _) => hideRequests++;
            window.Present(new() { Title = "Visible", Message = "Hide with the lobster." });
            await TestSupport.WaitForRenderedConditionAsync(
                () => VisualTreeHelper.GetOpenPopupsForXamlRoot(host.XamlRoot).Any(popup => popup.IsOpen),
                "lobster notification popup");
            var anchor = new PointInt32(window.AppWindow.Position.X + window.AppWindow.Size.Width,
                window.AppWindow.Position.Y + window.AppWindow.Size.Height);
            view.Hide();
            await TestSupport.WaitForRenderedConditionAsync(
                () => !window.AppWindow.IsVisible &&
                    !VisualTreeHelper.GetOpenPopupsForXamlRoot(host.XamlRoot).Any(popup => popup.IsOpen),
                "lobster and popup hidden");
            view.Show();
            await TestSupport.WaitForRenderedConditionAsync(
                () => window.AppWindow.IsVisible && Assert.IsType<TranslateTransform>(host.RenderTransform).Y == 0,
                "same lobster restored through the bottom frame");
            Assert.Same(host, window.Content);
            Assert.Same(mascot, Mascot(host));
            Assert.Equal(anchor, new PointInt32(window.AppWindow.Position.X + window.AppWindow.Size.Width,
                window.AppWindow.Position.Y + window.AppWindow.Size.Height));
            Assert.Equal(0, hideRequests);
            Assert.DoesNotContain(VisualTreeHelper.GetOpenPopupsForXamlRoot(host.XamlRoot), popup => popup.IsOpen);
        });

    [Fact]
    [Trait("Category", "AnimationPreferenceEnabled")]
    public Task AnimatedRestore_ReversesAProgressedExitWithoutALateHide() =>
        WithWindowAsync(async window =>
        {
            Assert.True(new Windows.UI.ViewManagement.UISettings().AnimationsEnabled,
                "This regression requires native animations enabled.");
            var host = Assert.IsType<ReactorHostControl>(window.Content);
            var mascot = Mascot(host);
            var translation = Assert.IsType<TranslateTransform>(host.RenderTransform);
            var view = (IDesktopCompanionView)window;
            await TestSupport.WaitForRenderedConditionAsync(
                () => translation.Y == 0, "initial entrance completes");
            view.Hide();
            await TestSupport.WaitForRenderedConditionAsync(
                () => window.AppWindow.IsVisible && translation.Y > 0 && translation.Y < mascot.ActualHeight,
                "exit has progressed before reversing it",
                () => $"Y={translation.Y}, MascotHeight={mascot.ActualHeight}, Visible={window.AppWindow.IsVisible}");
            Assert.True(window.AppWindow.IsVisible);
            Assert.True(translation.Y > 0 && translation.Y < mascot.ActualHeight,
                "Restore must interrupt an exit, not show an already hidden window.");
            view.Show();
            await TestSupport.WaitForRenderedConditionAsync(
                () => window.AppWindow.IsVisible && translation.Y == 0,
                "reversed entrance completes");
            var observation = System.Diagnostics.Stopwatch.StartNew();
            await TestSupport.WaitForRenderedConditionAsync(() =>
            {
                Assert.True(window.AppWindow.IsVisible);
                Assert.Equal(0, translation.Y);
                return observation.Elapsed >= TestSupport.NegativeObservationTimeout;
            }, "superseded exit cannot hide the restored lobster");
        });

    [Fact]
    public Task QuickChat_AndNotificationsUseTheSameCompactBubbleInset() =>
        WithWindowAsync(async window =>
        {
            var root = (FrameworkElement)window.Content;
            window.Present(new() { Title = "Notification", Message = "Use the compact inset." });
            var bubble = Control<StackPanel>(root, "DesktopCompanionBubble");
            await TestSupport.WaitForRenderedConditionAsync(
                () => bubble.Visibility == Visibility.Visible, "notification bubble");
            var frame = Assert.IsType<Border>(bubble.Children[0]);
            Assert.Equal(new Thickness(4), frame.Padding);
            Assert.Equal(new Thickness(8, 4, 0, 0),
                Control<TextBlock>(root, "DesktopCompanionTitle").Margin);
            var message = Control<TextBlock>(root, "DesktopCompanionMessage");
            var messageScroller = TestSupport.FindDescendants<ScrollViewer>(BubbleRoot)
                .Single(scroll => ReferenceEquals(scroll.Content, message));
            Assert.Equal(new Thickness(8, 0, 8, 0), messageScroller.Margin);
            var dismiss = Control<Button>(root, "DesktopCompanionDismiss");
            Assert.Equal(new Thickness(0, 4, 0, 0), dismiss.Margin);
            Assert.Equal(new Thickness(4), dismiss.Padding);
            var actions = Assert.IsType<StackPanel>(
                Control<HyperlinkButton>(root, "DesktopCompanionNotifications").Parent);
            Assert.Equal(new Thickness(8, 0, 0, 8), actions.Margin);
            Assert.Equal(8, Assert.IsType<Grid>(frame.Child).RowSpacing);

            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(Control<Button>(root, "DesktopCompanionMascot"));
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await TestSupport.WaitForRenderedConditionAsync(
                () => Control<TextBlock>(root, "DesktopCompanionTitle").Visibility == Visibility.Collapsed, "quick chat");
            Assert.Equal(new Thickness(4), frame.Padding);
            Assert.Same(frame, bubble.Children[0]);
            Assert.Equal(new Thickness(0), dismiss.Margin);
            Assert.Equal(new Thickness(6), dismiss.Padding);
            var loading = Assert.Single(TestSupport.FindLogical<ProgressRing>(BubbleRoot));
            Assert.Equal(Visibility.Collapsed, loading.Visibility);
            var recovery = Assert.IsType<StackPanel>(loading.Parent);
            var status = Assert.Single(recovery.Children.OfType<TextBlock>());
            var recoveryActions = Assert.Single(recovery.Children.OfType<StackPanel>());
            var retry = Assert.Single(recoveryActions.Children.OfType<Button>());
            var connection = Assert.Single(recoveryActions.Children.OfType<HyperlinkButton>());
            Assert.Equal(Visibility.Visible, connection.Visibility);
            await TestSupport.WaitForRenderedConditionAsync(() => status.ActualHeight > 0, "chat recovery layout");
            Assert.InRange(status.TransformToVisual(dismiss).TransformPoint(new(0, 0)).Y - dismiss.ActualHeight, 0, 8.01);
            Assert.Equal(new Thickness(8), recovery.Margin);
            var retryCenter = retry.TransformToVisual(BubbleRoot).TransformPoint(new(0, 0)).Y + retry.ActualHeight / 2;
            var connectionCenter = connection.TransformToVisual(BubbleRoot).TransformPoint(new(0, 0)).Y + connection.ActualHeight / 2;
            Assert.InRange(Math.Abs(retryCenter - connectionCenter), 0, 1);
            Assert.Equal(new Thickness(0), connection.Padding);

            window.Present(new() { Title = "Next notification", Message = "Keep the same inset." });
            await TestSupport.WaitForRenderedConditionAsync(
                () => Control<TextBlock>(root, "DesktopCompanionTitle").Visibility == Visibility.Visible, "replacement notification");
            Assert.Same(frame, bubble.Children[0]);
            Assert.Equal(new Thickness(4), frame.Padding);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MascotScale_UpdatesLayoutAndHitRegionWithoutScalingOrReplacingPopup(bool chatOpen) =>
        WithWindowAsync(async window =>
        {
            var root = (FrameworkElement)window.Content;
            var target = Control<Button>(root, "DesktopCompanionMascot");
            var mascot = Mascot(root);
            var bubble = Control<StackPanel>(root, "DesktopCompanionBubble");
            window.Present(new() { Title = "Same popup", Message = "Keep this text at its normal size." });
            if (chatOpen)
            {
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(target);
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            }
            // The popup and mascot reconcile on separate Reactor hosts; settle the arrival mood
            // before counting mood changes during scaling.
            await TestSupport.WaitForRenderedConditionAsync(
                () => bubble.ActualHeight > 0 && mascot.Mood == OnboardingMascotMood.Happy,
                "popup layout and arrival mood before resizing",
                () => $"Mood: {mascot.Mood}; bubble height: {bubble.ActualHeight}");
            Assert.Same(mascot, Mascot(root));
            var frame = Assert.IsType<Border>(bubble.Children[0]);
            var popupContent = frame.Child;
            var originalWidth = frame.ActualWidth;
            var originalHeight = frame.ActualHeight;
            var originalFontSize = Control<TextBlock>(root, "DesktopCompanionMessage").FontSize;
            var originalMood = mascot.Mood;
            var moodChanges = 0;
            var token = mascot.RegisterPropertyChangedCallback(OnboardingMascot.MoodProperty, (_, _) => moodChanges++);
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var region = CreateRectRgn(0, 0, 0, 0);
            var work = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var anchor = new PointInt32(window.AppWindow.Position.X + window.AppWindow.Size.Width,
                window.AppWindow.Position.Y + window.AppWindow.Size.Height);
            try
            {
                foreach (var (percent, expectedSize) in new[] { (50, 90d), (100, 180d), (200, 360d), (75, 135d) })
                {
                    window.ApplyScale(percent);
                    await TestSupport.WaitForRenderedConditionAsync(
                        () => NearSize(root, target.ActualWidth, expectedSize) && NearSize(root, mascot.ActualWidth, expectedSize),
                        $"mascot at {percent}%");
                    Assert.True(NearSize(root, target.ActualHeight, expectedSize), $"target height {target.ActualHeight}");
                    Assert.True(NearSize(root, mascot.ActualHeight, expectedSize), $"mascot height {mascot.ActualHeight}");
                    Assert.Same(mascot, Mascot(root));
                    Assert.Same(popupContent, frame.Child);
                    Assert.Equal(originalWidth, frame.ActualWidth);
                    Assert.Equal(originalHeight, frame.ActualHeight);
                    Assert.Equal(originalFontSize, Control<TextBlock>(root, "DesktopCompanionMessage").FontSize);
                    Assert.Equal(anchor, new PointInt32(window.AppWindow.Position.X + window.AppWindow.Size.Width,
                        window.AppWindow.Position.Y + window.AppWindow.Size.Height));
                    Assert.InRange(window.AppWindow.Position.X, work.X, work.X + work.Width - window.AppWindow.Size.Width);
                    Assert.InRange(window.AppWindow.Position.Y, work.Y, work.Y + work.Height - window.AppWindow.Size.Height);
                    Assert.NotEqual(0, GetWindowRgn(hwnd, region));
                    var dpi = root.XamlRoot.RasterizationScale;
                    var width = window.AppWindow.Size.Width;
                    var height = window.AppWindow.Size.Height;
                    Assert.True(PtInRegion(region, width - (int)(expectedSize * dpi / 2),
                        height - (int)(expectedSize * dpi / 2)));
                    Assert.False(PtInRegion(region, width - (int)(expectedSize * dpi) - 2,
                        height - (int)(expectedSize * dpi / 2)));
                    Assert.False(PtInRegion(region, (int)(20 * dpi), (int)(20 * dpi)),
                        "The bubble popup owns its own window, so the overlay region must stay click-through there.");
                }
                Assert.Equal(originalMood, mascot.Mood);
                Assert.Equal(0, moodChanges);
            }
            finally
            {
                mascot.UnregisterPropertyChangedCallback(OnboardingMascot.MoodProperty, token);
                DeleteObject(region);
            }
        });

    [Fact]
    public Task OptionsMenu_SettingsInvokesNavigationWithoutHidingLobster()
    {
        var settingsRequests = 0;
        return WithWindowAsync(async window =>
        {
            var root = (FrameworkElement)window.Content;
            var hideRequests = 0;
            window.HideRequested += (_, _) => hideRequests++;
            typeof(DesktopCompanionWindow).GetMethod("ShowMenu",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [null]);
            await TestSupport.WaitForRenderedConditionAsync(
                () => VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Count > 0, "lobster options");
            var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot);
            try
            {
                await TestSupport.WaitForRenderedConditionAsync(
                    () => popups.SelectMany(popup => TestSupport.FindDescendants<MenuFlyoutItem>(popup.Child)).Any(),
                    "rendered lobster options");
                var items = popups.SelectMany(popup => TestSupport.FindDescendants<MenuFlyoutItem>(popup.Child)).ToArray();
                Assert.Equal(new[]
                {
                    OpenClawTray.Helpers.LocalizationHelper.GetString("DesktopCompanion_Open"),
                    OpenClawTray.Helpers.LocalizationHelper.GetString("DesktopCompanion_Notifications"),
                    OpenClawTray.Helpers.LocalizationHelper.GetString("DesktopCompanion_Settings")
                }, items.Select(item => item.Text));
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(items[2]);
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                Assert.Equal(1, settingsRequests);
                Assert.Equal(0, hideRequests);
                Assert.Same(root, window.Content);
            }
            finally { foreach (var popup in popups) popup.IsOpen = false; }
        }, openSettings: () => settingsRequests++);
    }

    [Fact]
    public Task NewNotificationReplacesOpenChat_ButClearingNotificationsDoesNot() =>
        WithWindowAsync(async window =>
        {
            var root = (FrameworkElement)window.Content;
            var mascot = Control<Button>(root, "DesktopCompanionMascot");
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(mascot);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await TestSupport.WaitForRenderedConditionAsync(
                () => Control<TextBlock>(root, "DesktopCompanionTitle").Visibility == Visibility.Collapsed &&
                    Control<StackPanel>(root, "DesktopCompanionBubble").Visibility == Visibility.Visible, "quick chat");
            Assert.Empty(OpenPopups(root));
            window.Present(null);
            Assert.Equal(Visibility.Visible, Control<StackPanel>(root, "DesktopCompanionBubble").Visibility);
            Assert.Equal(Visibility.Collapsed, Control<TextBlock>(root, "DesktopCompanionTitle").Visibility);
            var chatSize = window.AppWindow.Size;
            window.Present(new() { Title = "A new notification", Message = "New arrival" });
            await TestSupport.WaitForRenderedConditionAsync(
                () => Control<TextBlock>(root, "DesktopCompanionTitle").Text == "A new notification" &&
                    Control<TextBlock>(root, "DesktopCompanionTitle").Visibility == Visibility.Visible, "replacement notification");
            Assert.Equal("New arrival", Control<TextBlock>(root, "DesktopCompanionMessage").Text);
            Assert.True(window.AppWindow.Size.Height < chatSize.Height);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await TestSupport.WaitForRenderedConditionAsync(
                () => Control<TextBlock>(root, "DesktopCompanionTitle").Visibility == Visibility.Collapsed, "reopened chat");
            Assert.Equal(chatSize, window.AppWindow.Size);
            var dismiss = FrameworkElementAutomationPeer.CreatePeerForElement(Control<Button>(root, "DesktopCompanionDismiss"));
            ((IInvokeProvider)dismiss.GetPattern(PatternInterface.Invoke)).Invoke();
            await TestSupport.WaitForRenderedConditionAsync(
                () => Control<StackPanel>(root, "DesktopCompanionBubble").Visibility == Visibility.Collapsed, "chat dismissed");
        });

    [Theory]
    [InlineData(null, false)]
    [InlineData("connection", false)]
    [InlineData("chat:", false)]
    [InlineData("chat:agent%3Amain%3Areply", true)]
    public Task OpenChat_OnlySuppressesRoutedChatNotifications(string? actionRoute, bool staysOpen) =>
        WithWindowAsync(async window =>
        {
            var root = (FrameworkElement)window.Content;
            var mascot = Control<Button>(root, "DesktopCompanionMascot");
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(mascot);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            var title = Control<TextBlock>(root, "DesktopCompanionTitle");
            await TestSupport.WaitForRenderedConditionAsync(
                () => title.Visibility == Visibility.Collapsed, "open quick chat");
            var chatSize = window.AppWindow.Size;
            window.Present(new() { Title = "Incoming", Message = "Response", ActionRoute = actionRoute });
            await TestSupport.WaitForRenderedConditionAsync(
                () => title.Visibility == (staysOpen ? Visibility.Collapsed : Visibility.Visible),
                "notification routing");
            if (staysOpen)
                Assert.Equal(chatSize, window.AppWindow.Size);
            else
                Assert.Equal("Incoming", title.Text);
        });

    [Theory]
    [InlineData(null, false)]
    [InlineData("connection", false)]
    [InlineData("chat:", false)]
    [InlineData("chat:agent%3Amain%3Areply", true)]
    public Task OnlyChatNotificationsOfferQuickReply(string? actionRoute, bool expected) =>
        WithWindowAsync(async window =>
        {
            var root = (FrameworkElement)window.Content;
            window.Present(new() { Title = "Notification", Message = "Reply routing", ActionRoute = actionRoute });
            await TestSupport.WaitForRenderedConditionAsync(
                () => Control<HyperlinkButton>(root, "DesktopCompanionReply").Visibility ==
                    (expected ? Visibility.Visible : Visibility.Collapsed), "reply availability");
            if (!expected) return;
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(Control<HyperlinkButton>(root, "DesktopCompanionReply"));
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await TestSupport.WaitForRenderedConditionAsync(
                () => Control<TextBlock>(root, "DesktopCompanionTitle").Visibility == Visibility.Collapsed &&
                    Control<StackPanel>(root, "DesktopCompanionBubble").Visibility == Visibility.Visible, "reply chat");
        });

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
            var root = (FrameworkElement)window.Content;
            var mascot = Mascot(root);
            var bubble = Control<StackPanel>(root, "DesktopCompanionBubble");
            var message = Control<TextBlock>(root, "DesktopCompanionMessage");
            window.ApplyTheme("Dark");
            window.Present(new()
            {
                Title = "OpenClaw notification",
                Message = "<b>Literal text</b> & a lobster",
                Severity = severity
            });
            // The popup and mascot reconcile on separate Reactor hosts.
            await TestSupport.WaitForRenderedConditionAsync(
                () => bubble.ActualHeight > 0 && bubble.Visibility == Visibility.Visible &&
                    mascot.Mood == mood && message.Text == "<b>Literal text</b> & a lobster",
                "notification layout, mood and literal text",
                () => $"Mood: {mascot.Mood}; bubble: {bubble.Visibility}, height: {bubble.ActualHeight}; text: {message.Text}");
            Assert.Equal(ElementTheme.Dark, root.RequestedTheme);
            Assert.Same(mascot, Mascot(root));
            Assert.Equal(mood, mascot.Mood);
            Assert.Equal("<b>Literal text</b> & a lobster", Control<TextBlock>(root, "DesktopCompanionMessage").Text);
            Assert.Equal(Visibility.Visible, Control<StackPanel>(root, "DesktopCompanionBubble").Visibility);
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(Control<Button>(root, "DesktopCompanionDismiss"));
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await TestSupport.WaitForRenderedConditionAsync(
                () => bubble.Visibility == Visibility.Collapsed &&
                    mascot.Mood == OnboardingMascotMood.Idle && message.Text.Length == 0,
                "bubble dismissal, idle mood and cleared text",
                () => $"Mood: {mascot.Mood}; bubble: {bubble.Visibility}; text: {message.Text}");
            Assert.Same(mascot, Mascot(root));
            Assert.Equal(OnboardingMascotMood.Idle, mascot.Mood);
            Assert.Empty(message.Text);
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
    public Task InputPrompt_UsesTheWholeOverlayRegion_AndRestoresClickThroughWhenDismissed() =>
        WithWindowAsync(async window =>
        {
            var root = (FrameworkElement)window.Content;
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(Control<Button>(root, "DesktopCompanionMascot"));
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await TestSupport.WaitForRenderedConditionAsync(
                () => Control<StackPanel>(root, "DesktopCompanionBubble").Visibility == Visibility.Visible, "quick chat");
            var presentation = Assert.IsType<DesktopCompanionPresentation>(
                typeof(DesktopCompanionWindow).GetField("_presentation",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window));
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var region = CreateRectRgn(0, 0, 0, 0);
            try
            {
                Assert.NotEqual(0, GetWindowRgn(hwnd, region));
                Assert.False(PtInRegion(region, 10, 10));
                presentation.ShowInputPrompt(new("Voice Input Is Off", "Enable speech-to-text in Permissions.",
                    null, _ => presentation.ShowInputPrompt(null)));
                await TestSupport.WaitForRenderedConditionAsync(
                    () => OpenPopups(root).Count > 0 &&
                        GetWindowRgn(hwnd, region) != 0 && PtInRegion(region, 10, 10), "unclipped input prompt");
                var dialog = Assert.Single(OpenPopups(root)
                    .SelectMany(popup => TestSupport.FindLogical<ContentDialog>(popup.Child)));
                Assert.Equal("Voice Input Is Off", dialog.Title);
                var dismiss = FrameworkElementAutomationPeer.CreatePeerForElement(
                    Assert.Single(TestSupport.FindDescendants<Button>(dialog), button => button.Name == "PrimaryButton"));
                ((IInvokeProvider)dismiss.GetPattern(PatternInterface.Invoke)).Invoke();
                await TestSupport.WaitForRenderedConditionAsync(
                    () => OpenPopups(root).Count == 0 &&
                        GetWindowRgn(hwnd, region) != 0 && !PtInRegion(region, 10, 10), "restored click-through region");
            }
            finally { DeleteObject(region); }
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

    // App reconfigures BackdropPolicy on every appearance, theme and High Contrast change; the open bubble must follow
    // without reopening. A Gateway theme paints the dashboard toast (--popover, --border) opaque with the acrylic removed,
    // whatever the Mica setting, like Gateway-themed windows. Clearing the theme restores Fluent's acrylic look and the
    // same backdrop. High Contrast keeps Fluent's colors and acrylic. With OPENCLAW_UI_PROOF_DIR set on an interactive
    // desktop, each state is captured over a stand-in canvas in the theme's shell color.
    [Fact]
    public Task OpenBubble_FollowsGatewayThemeMicaAndLightDarkLive() =>
        WithWindowAsync(async window =>
        {
            var rose = BuiltinGatewayThemes.All.First(theme => theme.Id == "rose");
            var highContrast = new AccessibilitySettings().HighContrast;
            var faults = new List<Exception>();
            void OnUnhandled(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
            {
                faults.Add(args.Exception);
                args.Handled = true;
            }
            Application.Current.UnhandledException += OnUnhandled;
            using var canvas = ProofCanvas.Create(window);
            try
            {
                var root = (FrameworkElement)window.Content;
                window.ApplyTheme("Dark");
                window.Present(new() { Title = "Rosé", Message = "The bubble follows the dashboard theme." });
                var bubble = Control<StackPanel>(root, "DesktopCompanionBubble");
                await TestSupport.WaitForRenderedConditionAsync(() => bubble.ActualHeight > 0, "notification bubble");
                var frame = Assert.IsType<Border>(bubble.Children[0]);
                var popup = Assert.Single(VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot),
                    candidate => ReferenceEquals(candidate.Child, BubbleRoot));
                var acrylic = Assert.IsType<PopoverAcrylicBackdrop>(popup.SystemBackdrop);
                // The acrylic takes the bubble's theme, not the Windows app mode the application theme follows.
                Assert.Equal(ElementTheme.Dark, acrylic.Theme);
                var windowsFill = ColorOf(frame.Background);
                var windowsStroke = ColorOf(frame.BorderBrush);
                await canvas.CaptureAsync("windows-default-acrylic", Rgb(0x20, 0x20, 0x20));

                BackdropPolicy.Configure(useMica: true, rose);
                if (highContrast)
                {
                    await WaitForFillAsync(frame, windowsFill, "High Contrast keeps the Fluent fill");
                    Assert.Same(acrylic, popup.SystemBackdrop);
                }
                else
                {
                    await TestSupport.WaitForRenderedConditionAsync(() => popup.SystemBackdrop is null, "acrylic removed under the theme");
                    await WaitForFillAsync(frame, Rgb(0x26, 0x23, 0x3A), "opaque dark popover");
                    Assert.Equal(Rgb(0x29, 0x26, 0x3C), ColorOf(frame.BorderBrush));
                }
                await canvas.CaptureAsync("rose-dark-mica-on", Rgb(0x19, 0x17, 0x24));

                // Light and dark palettes differ; switching the mode re-paints the open bubble.
                window.ApplyTheme("Light");
                await WaitForFillAsync(frame, highContrast ? ColorOf(frame.Background) : Rgb(0xFF, 0xFA, 0xF3),
                    "opaque light popover");
                if (!highContrast)
                    Assert.Equal(Rgb(0xE3, 0xD9, 0xCB), ColorOf(frame.BorderBrush));
                await TestSupport.WaitForRenderedConditionAsync(() => acrylic.Theme == ElementTheme.Light, "light acrylic");
                await canvas.CaptureAsync("rose-light-mica-on", Rgb(0xF6, 0xEF, 0xE6));

                window.ApplyTheme("Dark");
                BackdropPolicy.Configure(useMica: false, rose);
                await TestSupport.WaitForRenderedConditionAsync(() => popup.SystemBackdrop is null, "acrylic removed");
                if (!highContrast)
                {
                    await WaitForFillAsync(frame, Rgb(0x26, 0x23, 0x3A), "opaque dark popover");
                    Assert.Equal(Rgb(0x29, 0x26, 0x3C), ColorOf(frame.BorderBrush));
                }
                await canvas.CaptureAsync("rose-dark-mica-off", Rgb(0x19, 0x17, 0x24));

                BackdropPolicy.Configure(useMica: true, null);
                await WaitForFillAsync(frame, windowsFill, "Windows acrylic fill");
                Assert.Same(acrylic, popup.SystemBackdrop);
                Assert.Equal(windowsStroke, ColorOf(frame.BorderBrush));
                await canvas.CaptureAsync("windows-default-restored", Rgb(0x20, 0x20, 0x20));

                // With Mica off the Windows look paints Fluent's opaque flyout fill in the bubble's own mode, not the
                // app's: a dark Companion on light Windows (or the reverse) must not get the other mode's fill.
                if (!highContrast)
                {
                    var (otherTheme, otherSetting, otherShell) = Application.Current.RequestedTheme == ApplicationTheme.Dark
                        ? (ElementTheme.Light, "Light", Rgb(0xF3, 0xF3, 0xF3))
                        : (ElementTheme.Dark, "Dark", Rgb(0x20, 0x20, 0x20));
                    window.ApplyTheme(otherSetting);
                    BackdropPolicy.Configure(useMica: false, null);
                    await TestSupport.WaitForRenderedConditionAsync(() => popup.SystemBackdrop is null, "opaque Windows look");
                    await WaitForFillAsync(frame, FunctionalTheme.ResolveColor("SolidBackgroundFillColorQuarternary", otherTheme),
                        "opaque Windows fill in the bubble's mode");
                    Assert.Equal(FunctionalTheme.ResolveColor("SurfaceStrokeColorFlyout", otherTheme), ColorOf(frame.BorderBrush));
                    await canvas.CaptureAsync("windows-default-mica-off-other-mode", otherShell);
                    window.ApplyTheme("Dark");
                    BackdropPolicy.Configure(useMica: true, null);
                    await WaitForFillAsync(frame, windowsFill, "Windows acrylic fill");
                }

                // Removing and restoring the popup's backdrop while it is open must not fault the framework later.
                foreach (var (useMica, theme) in new (bool, ResolvedTheme?)[]
                    { (false, rose), (true, rose), (false, null), (true, null), (false, rose), (true, null) })
                {
                    BackdropPolicy.Configure(useMica, theme);
                    await OnboardingNativeProof.NextCompositionAsync();
                }
                await OnboardingNativeProof.NextCompositionAsync(TimeSpan.FromSeconds(1));
                Assert.Empty(faults);
                Assert.Same(acrylic, popup.SystemBackdrop);
            }
            finally
            {
                BackdropPolicy.Configure(useMica: true, null);
                Application.Current.UnhandledException -= OnUnhandled;
            }
        });

    [Fact]
    public Task ThemedBubbleActions_KeepContrastAndRestoreNativeResources() =>
        WithWindowAsync(async window =>
        {
            using var resources = new ChatThemeProofScope("Light");
            var applicator = new AppThemeApplicator();
            var highContrast = new AccessibilitySettings().HighContrast;
            var rose = BuiltinGatewayThemes.All.First(theme => theme.Id == "rose");
            var imported = rose with
            {
                Dark = null,
                Light = rose.Light! with
                {
                    Canvas = ThemeColor.White, Content = ThemeColor.White, Card = ThemeColor.White, Panel = ThemeColor.White,
                    Text = ThemeColor.Black, Muted = ThemeColor.Black, Popover = ThemeColor.FromHex("#808080"),
                    Primary = ThemeColor.FromHex("#808080"), PrimaryForeground = ThemeColor.Black,
                    Accent = ThemeColor.FromHex("#808080"), AccentHover = ThemeColor.FromHex("#696969"),
                },
            };
            Assert.True(ThemeLegibility.IsReadable(imported));
            var root = (FrameworkElement)window.Content;
            window.ApplyTheme("Light", refreshThemeResources: true);
            window.Present(new() { Title = "Action contrast", Message = "The action remains readable." });
            var action = Control<HyperlinkButton>(root, "DesktopCompanionNotifications");
            await TestSupport.WaitForRenderedConditionAsync(() => action.ActualHeight > 0, "native action");
            var nativeForeground = ColorOf(action.Foreground);
            var content = Assert.Single(TestSupport.FindDescendants<ContentPresenter>(action));
            var nativeStates = new[]
            {
                (State: "PointerOver", Color: ColorOf(Assert.IsAssignableFrom<Brush>(action.Resources["HyperlinkButtonForegroundPointerOver"]))),
                (State: "Pressed", Color: ColorOf(Assert.IsAssignableFrom<Brush>(action.Resources["HyperlinkButtonForegroundPressed"]))),
                (State: "Normal", Color: nativeForeground),
            };
            try
            {
                applicator.Apply(imported);
                BackdropPolicy.Configure(true, imported);
                window.ApplyTheme("Light", refreshThemeResources: true);
                var frame = Assert.IsType<Border>(Control<StackPanel>(root, "DesktopCompanionBubble").Children[0]);
                if (!highContrast)
                {
                    await WaitForFillAsync(frame, Rgb(0x80, 0x80, 0x80), "theme popover");
                    await TestSupport.WaitForRenderedConditionAsync(
                        () => ColorOf(action.Foreground) == Rgb(0, 0, 0), "readable local action foreground");
                    foreach (var state in new[] { "PointerOver", "Pressed", "Normal" })
                    {
                        Assert.True(VisualStateManager.GoToState(action, state, false));
                        await TestSupport.WaitForRenderedConditionAsync(
                            () => ColorOf(content.Foreground) == Rgb(0, 0, 0), $"readable {state} action");
                    }
                }
                else
                {
                    Assert.DoesNotContain("HyperlinkButtonForeground", action.Resources.Keys.OfType<string>());
                }

                applicator.Apply(null);
                BackdropPolicy.Configure(true, null);
                window.ApplyTheme("Light", refreshThemeResources: true);
                await TestSupport.WaitForRenderedConditionAsync(
                    () => !action.Resources.Keys.OfType<string>().Contains("HyperlinkButtonForeground") &&
                        ColorOf(action.Foreground) == nativeForeground, "restored native action");
                Assert.DoesNotContain("HyperlinkButtonForegroundPointerOver", action.Resources.Keys.OfType<string>());
                Assert.DoesNotContain("HyperlinkButtonForegroundPressed", action.Resources.Keys.OfType<string>());
                foreach (var (state, expected) in nativeStates)
                {
                    Assert.True(VisualStateManager.GoToState(action, state, false));
                    await TestSupport.WaitForRenderedConditionAsync(
                        () => ColorOf(content.Foreground) == expected, $"restored native {state} action");
                }
            }
            finally
            {
                applicator.Apply(null);
                BackdropPolicy.Configure(true, null);
            }
        });

    private static global::Windows.UI.Color ColorOf(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;
    private static global::Windows.UI.Color Rgb(byte r, byte g, byte b) => Argb(0xFF, r, g, b);
    private static global::Windows.UI.Color Argb(byte a, byte r, byte g, byte b) => global::Windows.UI.Color.FromArgb(a, r, g, b);

    private static Task WaitForFillAsync(Border frame, global::Windows.UI.Color expected, string description) =>
        TestSupport.WaitForRenderedConditionAsync(
            () => frame.Background is SolidColorBrush brush && brush.Color == expected, description,
            () => $"Fill: {(frame.Background as SolidColorBrush)?.Color}; expected {expected}.");

    /// <summary>
    /// With OPENCLAW_UI_PROOF_DIR set, a plain window in a shell color behind the overlay stands in for the themed
    /// Workspace, and each capture saves the overlay's screen rectangle (the bubble popup included).
    /// </summary>
    private sealed class ProofCanvas : IDisposable
    {
        private readonly DesktopCompanionWindow _overlay;
        private readonly Window? _canvas;
        private readonly Grid? _fill;

        private ProofCanvas(DesktopCompanionWindow overlay, Window? canvas, Grid? fill)
        {
            _overlay = overlay;
            _canvas = canvas;
            _fill = fill;
        }

        private static string? Directory => Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_DIR");

        public static ProofCanvas Create(DesktopCompanionWindow overlay)
        {
            if (string.IsNullOrWhiteSpace(Directory))
                return new(overlay, null, null);
            var fill = new Grid();
            var canvas = new Window { Content = fill };
            var position = overlay.AppWindow.Position;
            var size = overlay.AppWindow.Size;
            canvas.AppWindow.MoveAndResize(new(position.X - 40, position.Y - 40, size.Width + 80, size.Height + 80));
            canvas.Activate();
            return new(overlay, canvas, fill);
        }

        public async Task CaptureAsync(string name, global::Windows.UI.Color shell)
        {
            if (_canvas is null || _fill is null)
                return;
            _fill.Background = new SolidColorBrush(shell);
            await OnboardingNativeProof.NextCompositionAsync(TimeSpan.FromSeconds(1));
            var position = _overlay.AppWindow.Position;
            var size = _overlay.AppWindow.Size;
            using var bitmap = new System.Drawing.Bitmap(size.Width, size.Height);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(position.X, position.Y, 0, 0, new System.Drawing.Size(size.Width, size.Height));
            System.IO.Directory.CreateDirectory(Directory!);
            var suffix = new AccessibilitySettings().HighContrast ? "-high-contrast" : "";
            bitmap.Save(Path.Combine(Directory!, $"desktop-lobster-{name}{suffix}.png"), System.Drawing.Imaging.ImageFormat.Png);
        }

        public void Dispose() => _canvas?.Close();
    }

    [Fact]
    public Task LongMessage_RemainsScrollable_AndClearRemovesSensitiveText() =>
        WithWindowAsync(async window =>
        {
            window.Present(new() { Title = "Long message", Message = string.Join("\n", Enumerable.Repeat("Lobster news", 100)) });
            var root = (FrameworkElement)window.Content;
            await TestSupport.WaitForRenderedConditionAsync(() =>
                TestSupport.FindDescendants<ScrollViewer>(BubbleRoot).Any(scroll => scroll.ScrollableHeight > 0),
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
            // The popup and mascot reconcile on separate Reactor hosts.
            await TestSupport.WaitForRenderedConditionAsync(
                () => message.Text == "First arrival" && mascot.Mood == OnboardingMascotMood.Happy,
                "first Reactor render and arrival mood",
                () => $"Mood: {mascot.Mood}; text: {message.Text}");
            Assert.Equal(OnboardingMascotMood.Happy, mascot.Mood);
            var bubble = Control<StackPanel>(host, "DesktopCompanionBubble");
            var frame = Assert.IsType<Border>(bubble.Children[0]);
            var darkFill = Assert.IsType<SolidColorBrush>(frame.Background).Color;
            var moodChanges = 0;
            var token = mascot.RegisterPropertyChangedCallback(OnboardingMascot.MoodProperty, (_, _) => moodChanges++);
            try
            {
                window.ApplyTheme("Light");
                await TestSupport.WaitForRenderedConditionAsync(() => host.ActualTheme == ElementTheme.Light, "theme render");
                await Task.Delay(100); // test-timing-exception: negative probe: a theme change must not replay the mascot mood
                Assert.NotEqual(darkFill, Assert.IsType<SolidColorBrush>(frame.Background).Color);
                Assert.Equal(0, moodChanges);
                window.Present(new() { Title = "Second", Message = "Second arrival" });
                // The arrival replays the entrance (Idle, then Happy) in the mascot host's own render.
                await TestSupport.WaitForRenderedConditionAsync(
                    () => message.Text == "Second arrival" && moodChanges == 2 && mascot.Mood == OnboardingMascotMood.Happy,
                    "second Reactor render and replayed arrival mood",
                    () => $"Mood: {mascot.Mood}; mood changes: {moodChanges}; text: {message.Text}");
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
            await Task.Delay(100); // test-timing-exception: negative probe: a late Present after Close must not render
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
    public Task BubbleEditor_ClickTakesTypingFocusFromAnotherProcess(bool otherEditorFocused) =>
        WithWindowAsync(async window =>
        {
            await PositionForPointerProofAsync(window, activate: false);
            var root = (FrameworkElement)window.Content;
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(Control<Button>(root, "DesktopCompanionMascot"));
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            var chat = Assert.IsType<DesktopCompanionChatHost>(
                typeof(DesktopCompanionWindow).GetField("_chat",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window));
            // Exercise the popup's native input boundary independently of Gateway readiness.
            var input = new TextBox { Height = 32, VerticalAlignment = VerticalAlignment.Top };
            var inputPressed = false;
            var inputGotFocus = false;
            input.GotFocus += (_, _) => inputGotFocus = true;
            input.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler((_, _) => inputPressed = true), true);
            chat.Content.Children.Add(input);
            using var other = OwnedFocusProcess.Create(otherEditorFocused);
            Assert.True(GetCursorPos(out var saved));
            Assert.True(other.Start());
            System.Windows.Automation.AutomationElement? otherEditor = null;
            var pressed = false;
            try
            {
                IntPtr otherHwnd = default;
                await TestSupport.WaitForRenderedConditionAsync(
                    () =>
                    {
                        Assert.False(other.HasExited, "The owned focus proof process exited before showing its window.");
                        other.Refresh();
                        otherHwnd = other.MainWindowHandle;
                        if (otherHwnd == IntPtr.Zero || !input.IsLoaded || input.ActualWidth <= 0) return false;
                        otherEditor = System.Windows.Automation.AutomationElement.FromHandle(otherHwnd).FindFirst(
                            System.Windows.Automation.TreeScope.Descendants,
                            new System.Windows.Automation.PropertyCondition(
                                System.Windows.Automation.AutomationElement.AutomationIdProperty,
                                "OwnedFocusControl"));
                        return otherEditor is not null && !otherEditor.Current.IsOffscreen &&
                            otherEditor.Current.BoundingRectangle.Width > 0;
                    },
                    "other window and bubble editor loaded",
                    () => $"other HWND={otherHwnd}; editor found={otherEditor is not null}; input loaded={input.IsLoaded}");
                var otherControl = otherEditor!;
                var otherBounds = otherControl.Current.BoundingRectangle;
                var otherPoint = new NativePoint
                {
                    X = (int)Math.Round(otherBounds.Left + otherBounds.Width / 2),
                    Y = (int)Math.Round(otherBounds.Top + otherBounds.Height / 2),
                };
                var otherAt = WindowFromPoint(otherPoint);
                GetWindowThreadProcessId(otherAt, out var otherProcess);
                Assert.True(otherProcess == (uint)other.Id,
                    $"The owned external editor is obscured by process {otherProcess}; expected {other.Id}. No input was sent.");
                otherHwnd = GetAncestor(otherAt, 2);
                Assert.True(SetCursorPos(otherPoint.X, otherPoint.Y));
                SendMouseButton(0x0002);
                pressed = true;
                SendMouseButton(0x0004);
                pressed = false;
                await TestSupport.WaitForRenderedConditionAsync(
                    () => GetForegroundWindow() == otherHwnd, "other window foreground from owned click");
                if (otherEditorFocused)
                    await TestSupport.WaitForRenderedConditionAsync(
                        () => otherControl.Current.HasKeyboardFocus, "other process editor keyboard focus");
                var popup = Assert.Single(VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot),
                    popup => ReferenceEquals(popup.Child, BubbleRoot));
                NativePoint point = default;
                NativePoint? previousPoint = null;
                var stableFrames = 0;
                await TestSupport.WaitForRenderedConditionAsync(() =>
                {
                    var location = input.TransformToVisual(BubbleRoot).TransformPoint(
                        new(input.ActualWidth / 2, input.ActualHeight / 2));
                    var scale = root.XamlRoot.RasterizationScale;
                    point = new()
                    {
                        X = window.AppWindow.Position.X + (int)Math.Round((popup.HorizontalOffset + location.X) * scale),
                        Y = window.AppWindow.Position.Y + (int)Math.Round((popup.VerticalOffset + location.Y) * scale),
                    };
                    stableFrames = previousPoint is { } previous && point.X == previous.X && point.Y == previous.Y
                        ? stableFrames + 1 : 0;
                    previousPoint = point;
                    return stableFrames >= 3;
                }, "stable bubble input position");
                var at = WindowFromPoint(point);
                GetWindowThreadProcessId(at, out var process);
                Assert.True(process == (uint)Environment.ProcessId,
                    $"The bubble input at ({point.X},{point.Y}) is obscured by process {process}; no input was sent.");
                Assert.True(SetCursorPos(point.X, point.Y));
                SendMouseButton(0x0002);
                pressed = true;
                await TestSupport.WaitForRenderedConditionAsync(
                    () => inputPressed, "pointer press on bubble editor");
                SendMouseButton(0x0004);
                pressed = false;
                await TestSupport.WaitForRenderedConditionAsync(
                    () => inputGotFocus && ReferenceEquals(input, FocusManager.GetFocusedElement(input.XamlRoot)) &&
                        GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(window),
                    "bubble editor keyboard focus",
                    () => $"focus={FocusManager.GetFocusedElement(input.XamlRoot)?.GetType().Name}; " +
                        $"foreground={GetForegroundWindow()}; owner={WinRT.Interop.WindowNative.GetWindowHandle(window)}");
                var expected = "";
                foreach (var character in "lobster")
                {
                    expected += character;
                    await Task.Run(() =>
                    {
                        GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundProcess);
                        Assert.Equal((uint)Environment.ProcessId, foregroundProcess);
                        System.Windows.Forms.SendKeys.SendWait(character.ToString());
                    });
                    await TestSupport.WaitForRenderedConditionAsync(
                        () => input.Text == expected, "typed character in the bubble editor",
                        () => $"input length={input.Text.Length}; expected length={expected.Length}");
                }
                if (otherEditorFocused)
                    Assert.Empty(((System.Windows.Automation.ValuePattern)otherControl.GetCurrentPattern(
                        System.Windows.Automation.ValuePattern.Pattern)).Current.Value);
            }
            finally
            {
                try
                {
                    if (pressed) SendMouseButton(0x0004);
                    input.IsEnabled = false;
                    chat.Content.Children.Remove(input);
                    Assert.True(SetCursorPos(saved.X, saved.Y));
                }
                finally
                {
                    if (!other.HasExited)
                    {
                        other.Kill();
                        await other.WaitForExitAsync().WaitAsync(TestSupport.RenderedConditionTimeout);
                    }
                }
            }
        });

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
            Assert.Empty(OpenPopups(root));
            Assert.True(target.PointerCaptures is not { Count: > 0 });

            await DragAsync(window, target, 60, 30);
            Assert.Equal(new PointInt32(origin.X - 60, origin.Y - 30), window.AppWindow.Position);
            Assert.Empty(OpenPopups(root));
            Assert.Equal(showBubble ? Visibility.Visible : Visibility.Collapsed,
                Control<StackPanel>(root, "DesktopCompanionBubble").Visibility);

            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(target);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await TestSupport.WaitForRenderedConditionAsync(
                () => Control<TextBlock>(root, "DesktopCompanionTitle").Visibility ==
                    Visibility.Collapsed &&
                    Control<StackPanel>(root, "DesktopCompanionBubble").Visibility == Visibility.Visible,
                "chat after dragging");
        });

    [NativePointerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Click_OpensChatOrContextMenu_WithoutMovingTheMascot(bool rightClick) =>
        WithWindowAsync(async window =>
        {
            await PositionForPointerProofAsync(window);
            var root = (FrameworkElement)window.Content;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var origin = MascotCenter(window);
            var point = MascotCenter(window);
            Assert.True(GetCursorPos(out var saved));
            var release = rightClick ? 0x0010u : 0x0004u;
            var pressed = false;
            try
            {
                MoveOwnedPointer(hwnd, point);
                await Task.Delay(50); // test-timing-exception: real pointer input needs OS delivery time, no observable signal exists
                SendMouseButton(rightClick ? 0x0008u : 0x0002u);
                pressed = true;
                await Task.Delay(50); // test-timing-exception: real pointer input needs OS delivery time, no observable signal exists
                point.X += 2;
                point.Y += 2;
                MoveOwnedPointer(hwnd, point);
                await Task.Delay(50); // test-timing-exception: real pointer input needs OS delivery time, no observable signal exists
                SendMouseButton(release);
                pressed = false;
                await TestSupport.WaitForRenderedConditionAsync(
                    () => rightClick
                        ? VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Count > 0
                        : Control<TextBlock>(root, "DesktopCompanionTitle").Visibility == Visibility.Collapsed &&
                            Control<StackPanel>(root, "DesktopCompanionBubble").Visibility == Visibility.Visible,
                    "pointer action");
                Assert.Equal(origin, MascotCenter(window));
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
                await Task.Delay(50); // test-timing-exception: real pointer input needs OS delivery time, no observable signal exists
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
                await Task.Delay(100); // test-timing-exception: negative probe: dropped capture must stop the drag
                var stopped = window.AppWindow.Position;
                point.X -= 12;
                MoveOwnedPointer(hwnd, point);
                await Task.Delay(100); // test-timing-exception: negative probe: moves after capture loss must not drag
                Assert.Equal(stopped, window.AppWindow.Position);
                SendMouseButton(0x0004);
                pressed = false;
                await Task.Delay(100); // test-timing-exception: negative probe: release after capture loss must not open a popup
                Assert.Empty(VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot));
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(target);
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                await TestSupport.WaitForRenderedConditionAsync(
                    () => Control<TextBlock>(root, "DesktopCompanionTitle").Visibility == Visibility.Collapsed &&
                        Control<StackPanel>(root, "DesktopCompanionBubble").Visibility == Visibility.Visible,
                    "chat after canceled drag");
            }
            finally
            {
                if (pressed) SendMouseButton(0x0004);
                Assert.True(SetCursorPos(saved.X, saved.Y));
            }
        });

    // Layout rounds to whole physical pixels, so a fractional display scale shifts sizes by up to one pixel.
    private static bool NearSize(FrameworkElement root, double actual, double expected) =>
        Math.Abs(actual - expected) <= 1d / root.XamlRoot.RasterizationScale + 0.01;

    private static async Task PositionForPointerProofAsync(DesktopCompanionWindow window, bool activate = true)
    {
        if (activate) OnboardingNativeProof.ActivateOwned(window);
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
            await Task.Delay(50); // test-timing-exception: real pointer input needs OS delivery time, no observable signal exists
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
            await Task.Delay(150); // test-timing-exception: negative probe: capture is released after the final button up
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

    private Task WithWindowAsync(Func<DesktopCompanionWindow, Task> test, Action? openNotifications = null,
        Action? openSettings = null) =>
        ui.RunOnUIAsync(async () =>
        {
            var window = new DesktopCompanionWindow(openNotifications ?? (() => { }), () => { },
                openSettings ?? (() => { }));
            try
            {
                s_bubbleRoot = window.BubbleHostForTests;
                var foreground = GetForegroundWindow();
                ((IDesktopCompanionView)window).Show();
                await TestSupport.WaitForRenderedConditionAsync(() => ((FrameworkElement)window.Content).IsLoaded,
                    "desktop lobster Loaded");
                Assert.Equal(foreground, GetForegroundWindow());
                await test(window);
            }
            finally { ((IDesktopCompanionView)window).Close(); }
        });

    // The bubble lives in a windowed popup, outside the overlay window's visual tree.
    [ThreadStatic] private static FrameworkElement? s_bubbleRoot;

    private static FrameworkElement BubbleRoot => s_bubbleRoot!;

    private static IReadOnlyList<Microsoft.UI.Xaml.Controls.Primitives.Popup> OpenPopups(FrameworkElement root) =>
        VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
            .Where(popup => !ReferenceEquals(popup.Child, BubbleRoot)).ToArray();

    private static T Control<T>(FrameworkElement root, string id) where T : FrameworkElement =>
        Assert.Single(TestSupport.FindLogical<T>(root).Concat(TestSupport.FindLogical<T>(BubbleRoot)),
            element => AutomationProperties.GetAutomationId(element) == id);

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
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
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
