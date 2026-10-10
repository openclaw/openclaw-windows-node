using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClaw.SetupEngine.UI;
using OpenClaw.SetupEngine.UI.Pages;
using OpenClaw.TestSupport;
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using CommunityToolkit.WinUI.Controls;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class SetupLoadingViewRenderingTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData(ElementTheme.Light, 600, 380)]
    [InlineData(ElementTheme.Dark, 720, 820)]
    [InlineData(ElementTheme.Light, 1000, 820)]
    [InlineData(ElementTheme.Dark, 360, 260)]
    public async Task LoadingContentStaysCenteredThroughProgressAndResize(ElementTheme theme, double width, double height)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(
                Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            using var source = new SetupLoadingProgress();
            using var view = new SetupLoadingView { Width = width, Height = height, RequestedTheme = theme };
            var originalSize = ui.TestWindow.AppWindow.Size;
            try
            {
                var scale = ui.Container.XamlRoot.RasterizationScale;
                ui.TestWindow.AppWindow.Resize(new(
                    NativeProofLayout.PhysicalPixels(width + 48, scale),
                    NativeProofLayout.PhysicalPixels(height + 80, scale)));
                view.Bind(source);
                ui.Container.Children.Add(view);
                var scroll = Assert.Single(TestSupport.FindDescendants<ScrollViewer>(view));
                foreach (var step in new[]
                {
                    SetupLoadingStep.SaveSettings,
                    SetupLoadingStep.ApplyCapabilities,
                    SetupLoadingStep.CheckConfiguration,
                    SetupLoadingStep.RestartCompanion,
                })
                {
                    using var scope = source.Begin(SetupLoadingGroup.Finishing, step);
                    await TestSupport.WaitForRenderedConditionAsync(() => view.IsLoaded, "loading surface mounted");
                    view.UpdateLayout();
                    await ui.YieldToRenderAsync();
                    AssertCentered();
                    view.Width = width - 80;
                    view.UpdateLayout();
                    await ui.YieldToRenderAsync();
                    AssertCentered();
                    view.Width = width;
                    if (step == SetupLoadingStep.ApplyCapabilities)
                        await ui.PauseAsync("Finishing setup alignment");
                    if (height == 260 && step == SetupLoadingStep.RestartCompanion)
                    {
                        Assert.True(scroll.ScrollableHeight > 0);
                        scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
                        await TestSupport.WaitForRenderedConditionAsync(
                            () => Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) <= 1,
                            "short loading viewport scrolls to the final detail");
                    }
                }

                void AssertCentered()
                {
                    Assert.InRange(scroll.ScrollableWidth, 0, 0.5);
                    var progress = Assert.IsType<ProgressBar>(view.FindName("ActivityProgress"));
                    Assert.InRange(Math.Abs(progress.RenderSize.Width - Math.Min(320, scroll.ViewportWidth)), 0, 1);
                    foreach (var name in new[] { "Mascot", "Title", "Detail", "ActivityProgress" })
                    {
                        var element = Assert.IsAssignableFrom<FrameworkElement>(view.FindName(name));
                        // TextBlock.ActualWidth reports ink width; alignment uses its arranged render box.
                        var bounds = element.TransformToVisual(view).TransformBounds(
                            new Windows.Foundation.Rect(0, 0, element.RenderSize.Width, element.RenderSize.Height));
                        Assert.True(element.ActualWidth > 0, $"{name} must be laid out.");
                        if (element is TextBlock text) Assert.Equal(TextAlignment.Center, text.TextAlignment);
                        Assert.True(Math.Abs(bounds.X + bounds.Width / 2 - view.ActualWidth / 2) <= 1,
                            $"{name} center {bounds.X + bounds.Width / 2} must match viewport center {view.ActualWidth / 2}.");
                    }
                }
            }
            finally
            {
                ui.Container.Children.Clear();
                ui.TestWindow.AppWindow.Resize(originalSize);
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }

    [Fact]
    public async Task InstallationSubstepsRenderUnderTheirOwningPhaseAndClearOnCompletion()
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(
                Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            ProgressPage? page = null;
            try
            {
                page = new ProgressPage();
                var steps = OnboardingFlowPolicy.BuildInstallationSteps(false);
                var progress = new SetupInstallationProgress(steps, false);
                typeof(ProgressPage).GetField("_installationProgress",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, progress);
                var render = typeof(ProgressPage).GetMethod("RenderInstallationOverview",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                ui.Container.Children.Add(page);
                await ui.YieldToRenderAsync();
                var rows = new[]
                {
                    (Phase: SetupInstallationPhase.Prepare, Prefix: "Prepare"),
                    (Phase: SetupInstallationPhase.Install, Prefix: "Install"),
                    (Phase: SetupInstallationPhase.Connect, Prefix: "Connect"),
                };
                Assert.Null(page.FindName("CurrentActivity"));
                Assert.Null(page.FindName("StepCount"));
                foreach (var step in steps)
                {
                    progress.Apply(new(step.Id, step.DisplayName, null, null));
                    render.Invoke(page, null);
                    foreach (var row in rows)
                    {
                        var card = Assert.IsType<SettingsCard>(page.FindName(row.Prefix + "Phase"));
                        var subtitle = Assert.IsType<TextBlock>(page.FindName(row.Prefix + "Activity"));
                        Assert.Same(subtitle, card.Description);
                        var active = row.Phase == SetupInstallationProgress.PhaseFor(step.Id, false);
                        Assert.Equal(active ? step.DisplayName : "", subtitle.Text);
                        Assert.Equal(active ? Visibility.Visible : Visibility.Collapsed, subtitle.Visibility);
                        Assert.Equal(TextWrapping.Wrap, subtitle.TextWrapping);
                    }
                    progress.Apply(new(step.Id, step.DisplayName, StepOutcome.Success, TimeSpan.Zero));
                    render.Invoke(page, null);
                    foreach (var row in rows)
                        Assert.Equal(Visibility.Collapsed,
                            Assert.IsType<TextBlock>(page.FindName(row.Prefix + "Activity")).Visibility);
                }
            }
            finally
            {
                if (page is not null) await page.DisposeAsync();
                ui.Container.Children.Clear();
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }

    [Fact]
    public async Task HostedLocalAiMilestoneHasNoPipelineOrOverlayAndOnboardRemainsActionable()
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        using var temp = new TempDirectory("loading-milestone-");
        var config = new SetupConfig();
        config.LocalAi.Enabled = true;
        config.WindowsNodeContext.Enabled = false;
        var path = temp.Combine("config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config));
        await ui.RunOnUIAsync(async () =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(
                Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            SetupWindow? window = null;
            try
            {
                window = OnboardingNativeProof.CreateWindow(() => new SetupWindow(configPath: path,
                    startAtGatewayInstalledMilestone: true, dataDir: temp.Combine("data"),
                    localDataDir: temp.Combine("local"), commandLineArgs: []));
                var root = Assert.IsType<Grid>(window.Content);
                var frame = Assert.IsType<Frame>(root.FindName("RootFrame"));
                var page = Assert.IsType<ProgressPage>(frame.Content);
                var overlay = Assert.IsType<SetupLoadingView>(root.FindName("LoadingOverlay"));
                window.Activate();
                var button = Assert.IsType<Button>(page.FindName("OnboardButton"));
                await TestSupport.WaitForRenderedConditionAsync(() => button.IsLoaded, "hosted milestone action");
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                Assert.Null(typeof(ProgressPage).GetField("_runCts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page));
                Assert.Null(typeof(ProgressPage).GetField("_pipeline", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page));
                var loading = Assert.IsType<SetupLoadingProgress>(typeof(SetupWindow)
                    .GetProperty("LoadingProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
                using (var unrelated = loading.Begin(SetupLoadingGroup.Finishing, SetupLoadingStep.Drain))
                {
                    typeof(ProgressPage).GetMethod("ShowGatewayInstalledMilestone",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
                    Assert.True(unrelated.IsCurrent);
                }
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                Assert.Equal(Visibility.Visible, button.Visibility);
                Assert.True(button.IsEnabled);
                var peer = new ButtonAutomationPeer(button);
                Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
                await TestSupport.WaitForRenderedConditionAsync(() => frame.Content is AiSetupPage,
                    "milestone button advances to AI setup");
            }
            finally
            {
                if (window is not null) { window.Close(); await window.CleanupCompleted; }
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }

    [Fact]
    public async Task PassiveAndEarlyReplacementShellsOwnPresentationOnly()
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        await ui.RunOnUIAsync(async () =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(
                Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            SetupLoadingWindow? old = null;
            SetupReadyWindow? replacement = null;
            var verifies = 0;
            try
            {
                old = OnboardingNativeProof.CreateWindow(() => new SetupLoadingWindow());
                old.Activate();
                old.Report(SetupLoadingStep.StopLocalAi);
                await ui.YieldToRenderAsync();
                var view = Assert.IsType<SetupLoadingView>(old.Content);
                Assert.Equal("Finishing setup", Assert.IsType<TextBlock>(view.FindName("Title")).Text);
                Assert.Contains("old Local AI", Assert.IsType<TextBlock>(view.FindName("Detail")).Text);
                old.Close();
                old = null;
                replacement = OnboardingNativeProof.CreateWindow(() => new SetupReadyWindow(
                    _ =>
                    {
                        verifies++;
                        return Task.FromException<SetupVerifiedNativeRoute>(new InvalidOperationException("No receipt admitted"));
                    },
                    (_, _) => throw new InvalidOperationException("No destination admitted"),
                    _ => throw new InvalidOperationException("No observation admitted"),
                    () => throw new InvalidOperationException("No runtime admitted"), () => { }));
                replacement.Activate();
                await ui.YieldToRenderAsync();
                var frame = Assert.IsType<Frame>(replacement.Content);
                var page = Assert.IsType<OpenClaw.SetupEngine.UI.Pages.AiCompletionPage>(frame.Content);
                Assert.Contains("Starting Companion", Assert.IsType<TextBlock>(page.FindName("Detail")).Text);
                Assert.Equal(0, verifies);
                replacement.Close();
                await replacement.CleanupCompleted;
                Assert.Equal(0, verifies);
            }
            finally
            {
                old?.Close();
                if (replacement is { IsClosed: false }) replacement.Close();
                if (replacement is not null) await replacement.CleanupCompleted;
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }

    [Fact]
    public async Task LocalAiMajorSurfaceSurvivesArtifactToRuntimeTransferWithRealMeasurement()
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(
                Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            using var source = new SetupLoadingProgress();
            using var view = new SetupLoadingView();
            try
            {
                view.Bind(source);
                ui.Container.Children.Add(view);
                using var acquisition = source.Begin(SetupLoadingGroup.LocalAi, SetupLoadingStep.CheckArtifacts);
                acquisition.ReportActivity("Downloading selected model");
                acquisition.ReportDetail(new("model.gguf", 512, 1024, true));
                await ui.YieldToRenderAsync();
                var title = Assert.IsType<TextBlock>(view.FindName("Title"));
                Assert.Equal("Setting up Local AI", title.Text);
                var progress = Assert.IsType<ProgressBar>(view.FindName("ActivityProgress"));
                Assert.False(progress.IsIndeterminate);
                Assert.Equal(0.5, progress.Value);
                var original = ui.Container.Children.Single();
                using var runtime = source.Begin(SetupLoadingGroup.LocalAi, SetupLoadingStep.PrepareLocalAi);
                acquisition.ReportActivity("late artifact report");
                acquisition.Dispose();
                foreach (var step in new[] { SetupLoadingStep.StartLocalAi, SetupLoadingStep.PublishProvider, SetupLoadingStep.VerifyModel })
                {
                    runtime.Report(step);
                    await ui.YieldToRenderAsync();
                    Assert.Same(original, ui.Container.Children.Single());
                    Assert.Same(title, view.FindName("Title"));
                    Assert.Equal("Setting up Local AI", title.Text);
                    Assert.DoesNotContain("late artifact", Assert.IsType<TextBlock>(view.FindName("Detail")).Text);
                    Assert.True(progress.IsIndeterminate);
                }
                using var finishing = source.Begin(SetupLoadingGroup.Finishing, SetupLoadingStep.SaveSettings);
                Assert.Same(original, ui.Container.Children.Single());
                Assert.Equal("Finishing setup", title.Text);
                Assert.DoesNotContain("ready", title.Text, StringComparison.OrdinalIgnoreCase);
                source.Clear();
                finishing.Report(SetupLoadingStep.VerifyModel);
                Assert.Equal(Visibility.Collapsed, view.Visibility);
            }
            finally
            {
                ui.Container.Children.Clear();
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }

    [Fact]
    public async Task ReboundLoadingViewDoesNotRenderOldOrClosedScope()
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(
                Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            using var source = new SetupLoadingProgress();
            using var view = new SetupLoadingView();
            view.Bind(source);
            ui.Container.Children.Add(view);
            try
            {
                using var old = source.Begin(SetupLoadingGroup.GatewayPreparation, SetupLoadingStep.ConnectGateway);
                using var current = source.Begin(SetupLoadingGroup.Finishing, SetupLoadingStep.RecoverLocalAi);
                old.Report(SetupLoadingStep.PairGateway);
                await ui.YieldToRenderAsync();
                Assert.Equal("Finishing setup", Assert.IsType<TextBlock>(view.FindName("Title")).Text);
                view.Dispose();
                var detail = Assert.IsType<TextBlock>(view.FindName("Detail")).Text;
                current.Report(SetupLoadingStep.VerifyModel);
                await ui.YieldToRenderAsync();
                Assert.Equal(detail, Assert.IsType<TextBlock>(view.FindName("Detail")).Text);
            }
            finally
            {
                ui.Container.Children.Clear();
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }
}
