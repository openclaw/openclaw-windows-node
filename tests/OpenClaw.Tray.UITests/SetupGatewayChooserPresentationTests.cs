using System.Reflection;
using System.Text.Json;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClaw.SetupEngine.UI.Pages;
using OpenClaw.TestSupport;
using Xunit.Abstractions;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class SetupGatewayChooserPresentationTests(UIThreadFixture ui, ITestOutputHelper output)
{
    [Theory]
    [InlineData(ElementTheme.Light, 1120, 720)]
    [InlineData(ElementTheme.Dark, 1120, 720)]
    [InlineData(ElementTheme.Light, 720, 560)]
    [InlineData(ElementTheme.Dark, 720, 560)]
    public async Task ProductionChooser_KeepsNamedControlsFocusAndFixedActionsReachable(
        ElementTheme theme, int width, int height)
    {
        await WithChooserAsync(async (window, frame, page, _) =>
        {
            window.AppWindow.Resize(new(1800, 1400));
            page.Width = width;
            page.Height = height;
            await OnboardingNativeProof.ApplyThemeSurfaceAsync(Assert.IsType<Grid>(window.Content), theme);
            page.UpdateLayout();
            await ui.YieldToRenderAsync();
            var shell = Assert.IsType<SetupPageShell>(page.Content);
            Assert.Equal(width >= 960 && height >= 600, shell.IsWideLayout);
            var title = Find<TextBlock>(page, "GatewayTitle");
            Assert.Equal("Where should OpenClaw live?", title.Text);
            Assert.Equal(40, title.FontSize);
            Assert.Equal(AutomationHeadingLevel.Level1, AutomationProperties.GetHeadingLevel(title));
            Assert.IsType<SetupStepIndicator>(page.FindName("FlowProgress"));
            var selector = Find<ListView>(page, "GatewayChoiceSelector");
            Assert.Equal(new[] { "NativeChoice", "InstallChoice", "ConnectChoice" },
                selector.Items.Cast<ListViewItem>().Select(item => item.Name));
            var next = Find<Button>(page, "NextButton");
            var back = Find<Button>(page, "BackButton");
            var before = JsonSerializer.Serialize(window.AccessDraft.Config);
            foreach (var index in new[] { 1, 2 })
            {
                selector.SelectedIndex = index;
                Assert.True(next.IsEnabled);
                Assert.True(next.Focus(FocusState.Keyboard));
                page.Width = width == 1120 ? 720 : 1120;
                page.Height = height == 720 ? 560 : 720;
                page.UpdateLayout();
                await ui.YieldToRenderAsync();
                Assert.Same(next, FocusManager.GetFocusedElement(page.XamlRoot));
                Assert.Equal(index, selector.SelectedIndex);
                page.Width = width;
                page.Height = height;
                page.UpdateLayout();
                await ui.YieldToRenderAsync();
            }
            Assert.Equal(before, JsonSerializer.Serialize(window.AccessDraft.Config));
            Assert.Equal(0, shell.Scroller.ScrollableWidth);
            Assert.True(shell.Scroller.ViewportHeight >= 100);
            foreach (var name in new[] { "GatewayTitle", "NativeChoice", "InstallChoice", "ConnectChoice", "AlternativesButton" })
            {
                var control = Find<FrameworkElement>(page, name);
                control.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.5 });
                await ui.YieldToRenderAsync();
                await OnboardingNativeProof.NextCompositionAsync();
                OnboardingNativeProof.AssertFullyVisible(control, shell.Scroller);
                OnboardingNativeProof.AssertFullyVisible(back, shell);
                OnboardingNativeProof.AssertFullyVisible(next, shell);
            }
            Find<ListViewItem>(page, "NativeChoice").IsEnabled = true;
            Assert.True(VisualStateManager.GoToState(page, "NativeRecommendedState", false));
            Find<Border>(page, "NativeSupportCard").Visibility = Visibility.Collapsed;
            selector.SelectedIndex = 1;
            shell.Scroller.ChangeView(null, 0, null, disableAnimation: true);
            page.UpdateLayout();
            await ui.YieldToRenderAsync();
            await OnboardingArtworkRenderingTests.SaveProofAsync(page, $"gateway-chooser-{theme}-{width}-selected", output);
            Find<Border>(page, "NativeSupportCard").Visibility = Visibility.Visible;
            Find<StackPanel>(page, "NativeSupportStatusPanel").Visibility = Visibility.Visible;
            Find<TextBlock>(page, "NativeSupportStatus").Text = "Synthetic native support check failed.";
            Find<Button>(page, "WindowsUpdateButton").Visibility = Visibility.Visible;
            Find<InfoBar>(page, "ReadinessError").IsOpen = true;
            Find<InfoBar>(page, "ReadinessError").Message = "Synthetic WSL inspection failure.";
            Find<SettingsCard>(page, "LocalAiAvailabilityPanel").Visibility = Visibility.Visible;
            Find<TextBlock>(page, "LocalAiAvailabilityText").Text = "Synthetic eligible GPU.";
            page.UpdateLayout();
            await ui.YieldToRenderAsync();
            foreach (var name in new[] { "WindowsUpdateButton", "ReadinessError", "LocalAiAvailabilityPanel" })
            {
                var control = Find<FrameworkElement>(page, name);
                control.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.5 });
                await ui.YieldToRenderAsync();
                await OnboardingNativeProof.NextCompositionAsync();
                OnboardingNativeProof.AssertFullyVisible(control, shell.Scroller);
                OnboardingNativeProof.AssertFullyVisible(next, shell);
            }
            Assert.Equal(0, shell.Scroller.ScrollableWidth);
            await OnboardingArtworkRenderingTests.SaveProofAsync(page, $"gateway-chooser-{theme}-{width}-error", output);
            Assert.Same(page, frame.Content);
        });
    }

    [Fact]
    public async Task WslReadiness_ReusesPendingInspectionAndRetainsSelectionAfterFailure()
    {
        await WithChooserAsync(async (window, frame, page, pending) =>
        {
            var selector = Find<ListView>(page, "GatewayChoiceSelector");
            selector.SelectedIndex = 1;
            var before = JsonSerializer.Serialize(window.AccessDraft.Config);
            Invoke(Find<Button>(page, "NextButton"));
            await TestSupport.WaitForRenderedConditionAsync(
                () => Find<ProgressRing>(page, "InstallCheckProgress").IsActive, "WSL readiness pending");
            Assert.False(selector.IsEnabled);
            Assert.False(Find<Button>(page, "NextButton").IsEnabled);
            CompleteWsl(pending, "EnvironmentBlocked");
            await TestSupport.WaitForRenderedConditionAsync(
                () => Find<InfoBar>(page, "ReadinessError").IsOpen && selector.IsEnabled, "WSL failure restored");
            Assert.Equal(1, selector.SelectedIndex);
            Assert.True(Find<Button>(page, "NextButton").IsEnabled);
            Assert.False(Find<ProgressRing>(page, "InstallCheckProgress").IsActive);
            Assert.Equal(before, JsonSerializer.Serialize(window.AccessDraft.Config));
            Assert.Same(page, frame.Content);
        });
    }

    [Fact]
    public async Task ChoiceDraft_SurvivesBackAndAlternativesWithoutPersistingSettings()
    {
        await WithChooserAsync(async (window, frame, page, _) =>
        {
            window.AccessDraft.ApplyProfile(SetupCapabilityProfile.ReadOnly);
            var before = JsonSerializer.Serialize(window.AccessDraft.Config);
            Find<ListView>(page, "GatewayChoiceSelector").SelectedIndex = 2;
            Invoke(Find<Button>(page, "BackButton"));
            await TestSupport.WaitForRenderedConditionAsync(() => frame.Content is SecurityNoticePage { IsLoaded: true }, "Back");
            window.NavigateToWelcome();
            await TestSupport.WaitForRenderedConditionAsync(() => frame.Content is WelcomePage { IsLoaded: true }, "chooser return");
            var returned = Assert.IsType<WelcomePage>(frame.Content);
            Assert.Equal(2, Find<ListView>(returned, "GatewayChoiceSelector").SelectedIndex);
            Assert.Equal(before, JsonSerializer.Serialize(window.AccessDraft.Config));
            Invoke(Find<HyperlinkButton>(returned, "AlternativesButton"));
            await TestSupport.WaitForRenderedConditionAsync(() => frame.Content is AdvancedSetupPage { IsLoaded: true }, "alternatives");
            Assert.Equal(before, JsonSerializer.Serialize(window.AccessDraft.Config));
        });
    }

    private async Task WithChooserAsync(Func<SetupWindow, Frame, WelcomePage, object, Task> assertion)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        using var temp = new TempDirectory("gateway-chooser-");
        var config = temp.Combine("config.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new SetupConfig()));
        await ui.RunOnUIAsync(async () =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            SetupWindow? window = null;
            try
            {
                window = OnboardingNativeProof.CreateWindow(() => new SetupWindow(configPath: config,
                    dataDir: temp.Combine("data"), localDataDir: temp.Combine("local"),
                    commandLineArgs: [], startupRegistrationAllowed: false));
                var resultType = typeof(SetupConfig).Assembly.GetType("OpenClaw.SetupEngine.WslViabilityResult", throwOnError: true)!;
                var pending = Activator.CreateInstance(typeof(TaskCompletionSource<>).MakeGenericType(resultType),
                    TaskCreationOptions.RunContinuationsAsynchronously)!;
                var probe = Field(window, "_wslViabilityProbe");
                SetField(probe, "_inspectionTask", pending.GetType().GetProperty("Task")!.GetValue(pending)!);
                window.NavigateToWelcome();
                var root = Assert.IsType<Grid>(window.Content);
                var frame = Find<Frame>(root, "RootFrame");
                window.Activate();
                window.AppWindow.Move(new(-32000, -32000));
                await TestSupport.WaitForRenderedConditionAsync(() => frame.Content is WelcomePage { IsLoaded: true }, "chooser mounted");
                var page = Assert.IsType<WelcomePage>(frame.Content);
                Find<OnboardingMascot>(page, "MascotHero").IsAnimationEnabled = false;
                Find<ListView>(page, "GatewayChoiceSelector").SelectedIndex = 1;
                await TestSupport.WaitForRenderedConditionAsync(() => !Find<ProgressRing>(page, "NativeCheckProgress").IsActive,
                    "native support settled without replacing explicit WSL selection");
                Assert.Equal(1, Find<ListView>(page, "GatewayChoiceSelector").SelectedIndex);
                await assertion(window, frame, page, pending);
                Assert.False(File.Exists(temp.Combine("data", "settings.json")));
                Assert.False(File.Exists(temp.Combine("data", "gateways.json")));
            }
            finally
            {
                if (window is not null) { window.Close(); await window.CleanupCompleted; }
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }

    private static T Find<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        Assert.IsAssignableFrom<T>(root.FindName(name));
    private static object Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void CompleteWsl(object pending, string kind)
    {
        var assembly = typeof(SetupConfig).Assembly;
        var result = Activator.CreateInstance(assembly.GetType("OpenClaw.SetupEngine.WslViabilityResult", true)!,
            Enum.Parse(assembly.GetType("OpenClaw.SetupEngine.WslViabilityKind", true)!, kind),
            "Synthetic WSL readiness result.", "No WSL command was run.")!;
        pending.GetType().GetMethod("SetResult")!.Invoke(pending, [result]);
    }
    private static void Invoke(Control control) =>
        Assert.IsAssignableFrom<IInvokeProvider>(FrameworkElementAutomationPeer.CreatePeerForElement(control)
            .GetPattern(PatternInterface.Invoke)).Invoke();
}
