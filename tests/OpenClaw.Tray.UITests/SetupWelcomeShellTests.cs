using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClaw.SetupEngine.UI.Pages;
using OpenClaw.TestSupport;
using Xunit.Abstractions;
using Uia = System.Windows.Automation;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class SetupWelcomeShellTests(UIThreadFixture ui, ITestOutputHelper output)
{
    [Fact]
    public async Task WelcomeAction_PreservesDraftAndUsesExistingGatewayChooser()
    {
        await WithWindowAsync(async (window, frame, data) =>
        {
            var page = Assert.IsType<SecurityNoticePage>(frame.Content);
            Assert.IsType<OnboardingMascot>(page.FindName("MascotHero"));
            Assert.IsType<SetupStepIndicator>(page.FindName("FlowProgress"));
            var safety = Assert.Single(TestSupport.FindDescendants<HyperlinkButton>(page));
            Assert.Equal("https://trust.openclaw.ai/", safety.NavigateUri.AbsoluteUri);
            var before = JsonSerializer.Serialize(window.AccessDraft.Config);
            Assert.False(File.Exists(Path.Combine(data, "settings.json")));
            Assert.False(File.Exists(Path.Combine(data, "gateways.json")));

            var button = Assert.IsType<Button>(page.FindName("ContinueButton"));
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button);
            Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await TestSupport.WaitForRenderedConditionAsync(
                () => frame.Content is WelcomePage { IsLoaded: true }, "existing gateway chooser");
            Assert.Equal(before, JsonSerializer.Serialize(window.AccessDraft.Config));
            window.AccessDraft.ApplyProfile(SetupCapabilityProfile.ReadOnly);
            var reviewed = JsonSerializer.Serialize(window.AccessDraft.Config);
            window.NavigateToSecurityNotice(back: true);
            await TestSupport.WaitForRenderedConditionAsync(
                () => frame.Content is SecurityNoticePage { IsLoaded: true }, "return to welcome");
            Assert.Equal(reviewed, JsonSerializer.Serialize(window.AccessDraft.Config));
            Assert.Equal(SetupCapabilityProfile.ReadOnly, window.AccessDraft.Profile);
            Assert.False(File.Exists(Path.Combine(data, "settings.json")));
            Assert.False(File.Exists(Path.Combine(data, "gateways.json")));
        });
    }

    [Theory]
    [InlineData(SetupGatewayRoute.Native, 4, "Step 1 of 6")]
    [InlineData(SetupGatewayRoute.ManagedWsl, 4, "Step 1 of 7")]
    [InlineData(SetupGatewayRoute.Existing, 3, "Step 1 of 5")]
    [InlineData(SetupGatewayRoute.Remote, 3, "Step 1 of 5")]
    [InlineData(SetupGatewayRoute.McpOnly, 1, "Step 1 of 3")]
    [InlineData(SetupGatewayRoute.Deferred, 1, "Step 1 of 3")]
    public async Task Milestones_ReflectOnlySuppliedRoute(SetupGatewayRoute route, int count, string announcement)
    {
        await WithWindowAsync((window, frame, _) =>
        {
            var page = Assert.IsType<SecurityNoticePage>(frame.Content);
            var progress = Assert.IsType<SetupStepIndicator>(page.FindName("FlowProgress"));
            var stages = OnboardingFlowPolicy.GetStages(route, window.AccessDraft.Config);
            progress.Update(stages, OnboardingStage.Welcome);
            Assert.Equal(count, Labels(progress).Length);
            Assert.Equal(announcement, AutomationProperties.GetName(progress));
            var names = Labels(progress);
            Assert.Equal("Choose your PC tools", names[0].Text);
            Assert.Equal("Choose your PC tools, current step", AutomationProperties.GetName(names[0]));
            Assert.DoesNotContain(TestSupport.FindDescendants<Button>(progress), _ => true);
            if (route is SetupGatewayRoute.Existing or SetupGatewayRoute.Remote)
                Assert.Equal(new[] { "Choose your PC tools", "Connect your AI", "Start chatting" }, names.Select(label => label.Text));
            if (route is SetupGatewayRoute.McpOnly or SetupGatewayRoute.Deferred)
                Assert.Equal(new[] { "Choose your PC tools" }, names.Select(label => label.Text));
            progress.Update(stages, OnboardingStage.Capabilities);
            Assert.Equal(count, Labels(progress).Length);
            Assert.EndsWith("current step", AutomationProperties.GetName(Labels(progress)[0]));
            if (stages.Contains(OnboardingStage.AiSetup))
            {
                progress.Update(stages, OnboardingStage.AiSetup);
                Assert.Equal("Choose your PC tools, completed", AutomationProperties.GetName(Labels(progress)[0]));
                Assert.Equal("Connect your AI, current step",
                    AutomationProperties.GetName(Assert.Single(Labels(progress), label => label.Text == "Connect your AI")));
            }
            progress.Update(stages, (OnboardingStage)999);
            Assert.Equal(Visibility.Collapsed, progress.Visibility);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DefaultDots_KeepCanonicalStageCountAndCurrentWidth()
    {
        await ui.RunOnUIAsync(() =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(
                Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            try
            {
                var progress = new SetupProgressIndicator();
                progress.Update([OnboardingStage.Welcome, OnboardingStage.Capabilities, OnboardingStage.Ready],
                    OnboardingStage.Capabilities);
                Assert.Equal(Orientation.Horizontal, progress.Orientation);
                Assert.Equal(new double[] { 8, 20, 8 }, progress.Children.Cast<Border>().Select(dot => dot.Width));
                Assert.Equal("Step 2 of 3", AutomationProperties.GetName(progress));
            }
            finally { Application.Current.Resources.MergedDictionaries.Remove(resources); }
        });
    }

    [Theory]
    [InlineData(1120, 720, true)]
    [InlineData(960, 600, true)]
    [InlineData(959, 600, false)]
    [InlineData(960, 599, false)]
    [InlineData(720, 560, false)]
    public async Task ActualShellThresholds_KeepControlsFocusAndDraftMounted(int width, int height, bool wide)
    {
        await WithWindowAsync(async (window, frame, _) =>
        {
            window.AppWindow.Resize(new(1800, 1400));
            var page = Assert.IsType<SecurityNoticePage>(frame.Content);
            var shell = Assert.IsType<SetupPageShell>(page.Content);
            var mascot = Assert.IsType<OnboardingMascot>(page.FindName("MascotHero"));
            var button = Assert.IsType<Button>(page.FindName("ContinueButton"));
            var config = JsonSerializer.Serialize(window.AccessDraft.Config);
            Assert.True(button.Focus(FocusState.Keyboard));
            foreach (var size in new[] { (Width: width, Height: height), (Width: 720, Height: 560), (Width: width, Height: height) })
            {
                page.Width = size.Width;
                page.Height = size.Height;
                page.UpdateLayout();
                await ui.YieldToRenderAsync();
                Assert.Equal(size.Width >= 960 && size.Height >= 600, shell.IsWideLayout);
                Assert.Same(mascot, page.FindName("MascotHero"));
                Assert.True(mascot.IsLoaded);
                Assert.Same(button, FocusManager.GetFocusedElement(page.XamlRoot));
                Assert.Equal(config, JsonSerializer.Serialize(window.AccessDraft.Config));
                Assert.Equal(0, shell.Scroller.ScrollableWidth);
                OnboardingNativeProof.AssertFullyVisible(button, shell);
            }
            Assert.Equal(wide, shell.IsWideLayout);
            output.WriteLine($"Measured shell viewport={shell.ActualWidth}x{shell.ActualHeight}; wide={shell.IsWideLayout}. This is layout threshold proof, not monitor DPI.");
        });
    }

    [Theory]
    [InlineData(ElementTheme.Light, false)]
    [InlineData(ElementTheme.Dark, false)]
    [InlineData(ElementTheme.Light, true)]
    [InlineData(ElementTheme.Dark, true)]
    public async Task NativeDefaultAndMinimum_FooterAndEntireSafetyNoticeStayReachable(ElementTheme theme, bool minimum)
    {
        await WithWindowAsync(async (window, frame, _) =>
        {
            var root = Assert.IsType<Grid>(window.Content);
            await OnboardingNativeProof.ApplyThemeSurfaceAsync(root, theme);
            if (minimum)
                await SetupWindowMinimumSizeTests.ResizeBelowMinimumAsync(window, ui, output, $"welcome-{theme}");
            var page = Assert.IsType<SecurityNoticePage>(frame.Content);
            var shell = Assert.IsType<SetupPageShell>(page.Content);
            root.UpdateLayout();
            await ui.YieldToRenderAsync();
            var action = Assert.IsType<Button>(page.FindName("ContinueButton"));
            Assert.Equal("Get started", action.Content);
            var title = Assert.IsType<TextBlock>(page.FindName("WelcomeTitle"));
            var labels = Labels(Assert.IsType<SetupStepIndicator>(page.FindName("FlowProgress")));
            Assert.Equal(Assert.IsType<SolidColorBrush>(title.Foreground).Color,
                Assert.IsType<SolidColorBrush>(labels[0].Foreground).Color);
            Assert.Equal(Assert.IsType<SolidColorBrush>(
                    Assert.IsType<TextBlock>(page.FindName("WelcomeDescription")).Foreground).Color,
                Assert.IsType<SolidColorBrush>(labels[1].Foreground).Color);
            Assert.Equal(0, shell.Scroller.ScrollableWidth);
            Assert.True(shell.Scroller.ViewportHeight >= 100);
            OnboardingNativeProof.AssertFullyVisible(action, root);
            foreach (var name in new[] { "WelcomeTitle", "PcFeature", "GatewayFeature", "ChatFeature", "SafetyNotice", "SafetyLink" })
            {
                var control = Assert.IsAssignableFrom<FrameworkElement>(page.FindName(name));
                control.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.5 });
                await ui.YieldToRenderAsync();
                root.UpdateLayout();
                await OnboardingNativeProof.NextCompositionAsync();
                OnboardingNativeProof.AssertFullyVisible(control, shell.Scroller);
                OnboardingNativeProof.AssertFullyVisible(action, root);
            }
            var warning = Assert.IsType<InfoBar>(page.FindName("SafetyNotice"));
            Assert.Contains("change or delete files", warning.Message);
            Assert.Contains("expose private information", warning.Message);
            Assert.All(TestSupport.FindDescendants<TextBlock>(warning), text => Assert.False(text.IsTextTrimmed));
            AssertKeyboardActions(page);
        });
    }

    [Fact]
    [Trait("Category", "NativeOnboardingProof")]
    public async Task NativeWelcomeAccessibility_ExposesHeadingMilestoneAndKeyboardActions()
    {
        await WithWindowAsync(async (window, frame, _) =>
        {
            var page = Assert.IsType<SecurityNoticePage>(frame.Content);
            var heading = Assert.IsType<TextBlock>(page.FindName("WelcomeTitle"));
            Assert.Equal(AutomationHeadingLevel.Level1, AutomationProperties.GetHeadingLevel(heading));
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            await Task.Run(() =>
            {
                var native = Uia.AutomationElement.FromHandle(hwnd);
                Assert.Equal(Environment.ProcessId, native.Current.ProcessId);
                foreach (var label in new[] { "Hi. Let's get you set up.", "Meet your new sidekick.",
                             "Choose your PC tools, current step", "Get started" })
                {
                    var item = native.FindFirst(Uia.TreeScope.Descendants,
                        new Uia.PropertyCondition(Uia.AutomationElement.NameProperty, label));
                    Assert.NotNull(item);
                    Assert.False(item.Current.IsOffscreen);
                }
                AxeHelper.Initialize(Environment.ProcessId);
                AxeHelper.AssertNoAccessibilityErrors(hwnd, context: "Setup Welcome");
            });
            var shell = Assert.IsType<SetupPageShell>(page.Content);
            var safety = Assert.IsType<HyperlinkButton>(page.FindName("SafetyLink"));
            safety.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            await ui.YieldToRenderAsync();
            OnboardingNativeProof.AssertFullyVisible(safety, shell.Scroller);
            AssertKeyboardActions(page);
            using (await OnboardingNativeProof.CaptureAsync(window, "welcome-accessibility-safety", output,
                ["Before you get started", "Learn more about risks and safety", "Get started"])) { }
        }, nativeProof: true);
    }

    private static TextBlock[] Labels(SetupStepIndicator progress) =>
        TestSupport.FindDescendants<TextBlock>(progress).Where(label => label.Name == "StepTitle").ToArray();

    private static void AssertKeyboardActions(Page page)
    {
        var safety = Assert.IsType<HyperlinkButton>(page.FindName("SafetyLink"));
        var action = Assert.IsType<Button>(page.FindName("ContinueButton"));
        Assert.Same(safety, FocusManager.FindFirstFocusableElement(page));
        Assert.Same(action, FocusManager.FindLastFocusableElement(page));
        foreach (var control in new Control[] { safety, action })
        {
            Assert.True(control.Focus(FocusState.Keyboard));
            Assert.Same(control, FocusManager.GetFocusedElement(page.XamlRoot));
        }
    }

    private async Task WithWindowAsync(Func<SetupWindow, Frame, string, Task> assertion, bool nativeProof = false)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        if (nativeProof) OnboardingNativeProof.RequireProofDirectory();
        using var temp = new TempDirectory("welcome-shell-");
        var config = temp.Combine("setup.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new SetupConfig()));
        await ui.RunOnUIAsync(async () =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(
                Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            SetupWindow? window = null;
            try
            {
                window = OnboardingNativeProof.CreateWindow(() => new SetupWindow(configPath: config,
                    dataDir: temp.Combine("data"), localDataDir: temp.Combine("local"),
                    commandLineArgs: [], startupRegistrationAllowed: false));
                var root = Assert.IsType<Grid>(window.Content);
                var frame = Assert.IsType<Frame>(root.FindName("RootFrame"));
                window.Activate();
                if (nativeProof) OnboardingNativeProof.ActivateOwned(window);
                else window.AppWindow.Move(new(-32000, -32000));
                await TestSupport.WaitForRenderedConditionAsync(
                    () => frame.Content is SecurityNoticePage { IsLoaded: true }, "welcome mounted");
                Assert.IsType<OnboardingMascot>(((Page)frame.Content).FindName("MascotHero")).IsAnimationEnabled = false;
                await assertion(window, frame, temp.Combine("data"));
            }
            finally
            {
                if (window is not null) { window.Close(); await window.CleanupCompleted; }
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }
}
