using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClaw.SetupEngine.UI.Pages;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class ProgressPageShellTests(UIThreadFixture ui)
{
    [Fact]
    public async Task InstallationEvents_RenderActualCountsAndFailureCancellationAndRetryStatus()
    {
        await WithPreviewAsync("progress", async page =>
        {
            var steps = OnboardingFlowPolicy.BuildInstallationSteps(false);
            var progress = new SetupInstallationProgress(steps, false);
            Field("_installationProgress").SetValue(page, progress);
            Render(page);
            AssertCount(page, 0, steps.Count);
            Assert.Equal("Waiting", PhaseText(page, "PrepareStatus"));
            Assert.Equal("Waiting", PhaseText(page, "InstallStatus"));
            Assert.Equal("Waiting", PhaseText(page, "ConnectStatus"));
            var step = steps[0];
            void Apply(StepOutcome? outcome) => typeof(ProgressPage).GetMethod("OnStepProgress",
                BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page,
                [null, new StepProgressEvent(step.Id, step.DisplayName, outcome, null)]);
            Apply(null);
            await ui.YieldToRenderAsync();
            Assert.Equal("In progress", PhaseText(page, "PrepareStatus"));
            Assert.Equal(step.DisplayName, Find<TextBlock>(page, "CurrentActivity").Text);
            AssertCount(page, 0, steps.Count);
            Apply(StepOutcome.FailedTerminal);
            await ui.YieldToRenderAsync();
            Assert.Equal("Needs attention", PhaseText(page, "PrepareStatus"));
            AssertCount(page, 0, steps.Count);
            Apply(null);
            await ui.YieldToRenderAsync();
            Assert.Equal("In progress", PhaseText(page, "PrepareStatus"));
            progress.Cancel();
            Render(page);
            Assert.Equal("Cancelled", PhaseText(page, "PrepareStatus"));
            AssertCount(page, 0, steps.Count);
            Apply(StepOutcome.Success);
            await ui.YieldToRenderAsync();
            AssertCount(page, 1, steps.Count);
            Assert.Equal("Waiting", PhaseText(page, "InstallStatus"));
            Assert.Equal("Waiting", PhaseText(page, "ConnectStatus"));
            Assert.Null(Field("_pipeline").GetValue(page));
            Assert.Null(Field("_runCts").GetValue(page));
            Assert.Null(Field("_logger").GetValue(page));
        });
    }

    [Theory]
    [InlineData("progress", ElementTheme.Light, 1120, 720)]
    [InlineData("progress", ElementTheme.Dark, 1120, 720)]
    [InlineData("progress", ElementTheme.Light, 720, 560)]
    [InlineData("progress", ElementTheme.Dark, 720, 560)]
    [InlineData("progress-local-ai", ElementTheme.Light, 1120, 720)]
    [InlineData("progress-local-ai", ElementTheme.Dark, 1120, 720)]
    [InlineData("progress-local-ai", ElementTheme.Light, 720, 560)]
    [InlineData("progress-local-ai", ElementTheme.Dark, 720, 560)]
    [InlineData("milestone", ElementTheme.Light, 1120, 720)]
    [InlineData("milestone", ElementTheme.Dark, 1120, 720)]
    [InlineData("milestone", ElementTheme.Light, 720, 560)]
    [InlineData("milestone", ElementTheme.Dark, 720, 560)]
    public async Task ProgressShell_KeepsRealDownloadsLogsAndResumeActionsReachable(
        string mode, ElementTheme theme, int width, int height)
    {
        await WithPreviewAsync(mode, async page =>
        {
            await OnboardingNativeProof.ApplyThemeSurfaceAsync(ui.Container, theme);
            page.Width = width;
            page.Height = height;
            page.UpdateLayout();
            await OnboardingNativeProof.NextCompositionAsync();
            var shell = Assert.IsType<SetupPageShell>(page.Content);
            Assert.Equal(width >= 960 && height >= 600, shell.IsWideLayout);
            Assert.Equal(0, shell.Scroller.ScrollableWidth);
            var title = Find<TextBlock>(page, "TitleText");
            Assert.Equal(40, title.FontSize);
            Assert.Equal(AutomationHeadingLevel.Level1, AutomationProperties.GetHeadingLevel(title));
            var progress = Find<SetupStepIndicator>(page, "FlowProgress");
            progress.Update(OnboardingFlowPolicy.GetStages(SetupGatewayRoute.ManagedWsl, new SetupConfig()), OnboardingStage.Install);
            Assert.Equal("Step 5 of 7", AutomationProperties.GetName(progress));
            var mascot = Find<OnboardingMascot>(page, "ProgressMascot");
            var primary = Find<Button>(page, "OnboardButton");
            if (mode == "milestone")
            {
                Assert.Equal("Gateway installed", title.Text);
                Assert.Equal(OnboardingMascotMood.Happy, mascot.Mood);
                Assert.Equal(Visibility.Collapsed, Find<ScrollViewer>(page, "InstallContent").Visibility);
                Assert.Equal(Visibility.Visible, Find<StackPanel>(page, "MilestonePanel").Visibility);
                Assert.Equal(Visibility.Visible, primary.Visibility);
                OnboardingNativeProof.AssertFullyVisible(primary, shell);
                Assert.True(primary.Focus(FocusState.Keyboard));
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(primary);
                Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
                var warning = Find<TextBlock>(page, "MilestoneStatusText");
                Assert.Equal("Another setup task is still active. Wait for it to finish, then start OpenClaw onboard.", warning.Text);
                await BringAsync(warning, page);
                OnboardingNativeProof.AssertFullyVisible(warning, shell.Scroller);
                OnboardingNativeProof.AssertFullyVisible(primary, shell);
            }
            else
            {
                Assert.Equal("Making room for your agent", title.Text);
                Assert.Equal(Visibility.Collapsed, primary.Visibility);
                Assert.Equal(ScrollMode.Disabled, Find<ScrollViewer>(page, "InstallContent").VerticalScrollMode);
                var details = Find<Expander>(page, "DetailedActivity");
                Assert.False(details.IsExpanded);
                Assert.True(Find<TextBlock>(page, "LogText").IsTextSelectionEnabled);
                if (mode == "progress-local-ai")
                {
                    var download = Find<ProgressBar>(page, "DownloadProgress");
                    Assert.Equal(Visibility.Visible, download.Visibility);
                    Assert.Equal(6_322_405_376d / 16_464_440_224, download.Value, 8);
                    Assert.Contains("6.3 GB of 16.5 GB", Find<TextBlock>(page, "DownloadActivity").Text);
                }
                details.IsExpanded = true;
                page.UpdateLayout();
                await OnboardingNativeProof.NextCompositionAsync(TimeSpan.FromMilliseconds(400));
                var logAction = Find<HyperlinkButton>(page, "OpenLogButton");
                await BringAsync(logAction, page);
                OnboardingNativeProof.AssertFullyVisible(logAction, shell.Scroller);
                Assert.Equal(0, Find<ScrollViewer>(page, "InstallContent").ScrollableHeight);
                var overview = Find<ProgressBar>(page, "OverallProgress");
                Assert.InRange(overview.Value, 0, 1);
            }
            page.Width = width == 1120 ? 720 : 1120;
            page.Height = height == 720 ? 560 : 720;
            page.UpdateLayout();
            await OnboardingNativeProof.NextCompositionAsync();
            Assert.Same(mascot, page.FindName("ProgressMascot"));
            if (mode == "milestone")
                OnboardingNativeProof.AssertFullyVisible(primary, shell);
            else
                Assert.True(Find<Expander>(page, "DetailedActivity").IsExpanded);
            Assert.Null(Field("_pipeline").GetValue(page));
            Assert.Null(Field("_runCts").GetValue(page));
            Assert.Null(Field("_logger").GetValue(page));
        });
    }

    private async Task BringAsync(FrameworkElement target, ProgressPage page)
    {
        target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.5 });
        await ui.YieldToRenderAsync();
        page.UpdateLayout();
        await OnboardingNativeProof.NextCompositionAsync();
        page.UpdateLayout();
    }

    private static void AssertCount(ProgressPage page, int completed, int total)
    {
        Assert.Equal($"{completed} of {total} steps complete", Find<TextBlock>(page, "StepCount").Text);
        Assert.Equal((double)completed / total, Find<ProgressBar>(page, "OverallProgress").Value, 8);
    }

    private static string PhaseText(ProgressPage page, string name) =>
        Assert.IsType<TextBlock>(Find<SetupPhaseStatus>(page, name).FindName("StatusText")).Text;

    private static void Render(ProgressPage page) => typeof(ProgressPage).GetMethod(
        "RenderInstallationOverview", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);

    private static FieldInfo Field(string name) => typeof(ProgressPage).GetField(
        name, BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static T Find<T>(ProgressPage page, string name) => Assert.IsType<T>(page.FindName(name));

    private async Task WithPreviewAsync(string previewPage, Func<ProgressPage, Task> assertion)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        using var preview = new EnvironmentScope("OPENCLAW_SETUP_PREVIEW_PAGE", previewPage);
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(
                Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            var originalSize = ui.TestWindow.AppWindow.Size;
            ui.TestWindow.AppWindow.Resize(new(1800, 1400));
            var frame = new Frame { Width = 1120, Height = 720 };
            ui.Container.Children.Add(frame);
            try
            {
                Assert.True(frame.Navigate(typeof(ProgressPage), new SetupConfig { LocalAi = new() { Enabled = previewPage == "progress-local-ai" } }));
                await TestSupport.WaitForRenderedConditionAsync(
                    () => frame.Content is ProgressPage { IsLoaded: true }, "inert progress page mounted");
                var page = Assert.IsType<ProgressPage>(frame.Content);
                Find<OnboardingMascot>(page, "ProgressMascot").IsAnimationEnabled = false;
                await assertion(page);
            }
            finally
            {
                if (frame.Content is ProgressPage page) await page.DisposeAsync();
                frame.Navigate(typeof(Page));
                ui.Container.Children.Remove(frame);
                ui.TestWindow.AppWindow.Resize(originalSize);
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }
}
