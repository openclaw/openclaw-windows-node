using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClaw.SetupEngine.UI.Pages;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class CompleteShellRenderingTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData("success", ElementTheme.Light)]
    [InlineData("success", ElementTheme.Dark)]
    [InlineData("local-ai", ElementTheme.Light)]
    [InlineData("local-ai", ElementTheme.Dark)]
    [InlineData("native", ElementTheme.Light)]
    [InlineData("native", ElementTheme.Dark)]
    [InlineData("failure", ElementTheme.Light)]
    [InlineData("failure", ElementTheme.Dark)]
    [InlineData("fallback", ElementTheme.Light)]
    [InlineData("fallback", ElementTheme.Dark)]
    [InlineData("restart", ElementTheme.Light)]
    [InlineData("restart", ElementTheme.Dark)]
    [InlineData("diagnostics", ElementTheme.Light)]
    [InlineData("diagnostics", ElementTheme.Dark)]
    public async Task CompletionStates_PreserveStartupSummaryFailureFallbackAndExplicitRestart(
        string state, ElementTheme theme)
    {
        await WithPageAsync(state, theme, async (page, args) =>
        {
            var success = state is "success" or "local-ai" or "native";
            Assert.Equal(success ? Visibility.Visible : Visibility.Collapsed, Find<StackPanel>(page, "SummaryPanel").Visibility);
            Assert.Equal(success ? Visibility.Visible : Visibility.Collapsed, Find<Border>(page, "StartupRow").Visibility);
            Assert.False(Find<ToggleSwitch>(page, "StartupToggle").IsOn);
            Assert.Equal(state == "local-ai" ? Visibility.Visible : Visibility.Collapsed,
                Find<Border>(page, "LocalAiSummaryCard").Visibility);
            if (state == "native")
            {
                Assert.Equal(Visibility.Collapsed, Find<Border>(page, "DevicePairedSummaryCard").Visibility);
                Assert.Equal(Visibility.Visible, Find<TextBlock>(page, "NativeFeaturesNote").Visibility);
                Assert.Equal(args.NativeGatewayUrl, Find<TextBlock>(page, "GatewaySummaryText").Text);
            }
            if (state == "restart")
            {
                Assert.Equal(Visibility.Visible, Find<Grid>(page, "RestartIcon").Visibility);
                Assert.Equal(Visibility.Visible, Find<Button>(page, "RestartNowButton").Visibility);
                Assert.Equal(Visibility.Visible, Find<Button>(page, "RestartLaterButton").Visibility);
                Assert.Equal(Visibility.Collapsed, Find<Button>(page, "LaunchButton").Visibility);
                Assert.Equal(Visibility.Collapsed, Find<Border>(page, "ErrorCard").Visibility);
                Find<Button>(page, "RestartNowButton").IsEnabled = false;
                Find<Button>(page, "RestartLaterButton").IsEnabled = false;
                typeof(CompletePage).GetMethod("ShowRestartError", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(page, [new IOException("Synthetic restart request rejected.")]);
                Assert.True(Find<Button>(page, "RestartNowButton").IsEnabled);
                Assert.True(Find<Button>(page, "RestartLaterButton").IsEnabled);
                Assert.Contains("Synthetic restart request rejected.", Find<TextBlock>(page, "ErrorText").Text);
            }
            else if (!success)
            {
                Assert.Equal(Visibility.Collapsed, Find<TextBlock>(page, "SubtitleText").Visibility);
                Assert.Equal(Visibility.Visible, Find<Border>(page, "ErrorCard").Visibility);
                Assert.Equal(args.ErrorMessage, Find<TextBlock>(page, "ErrorText").Text);
                Assert.Equal("Close", Find<Button>(page, "LaunchButton").Content);
                Assert.Equal(state == "fallback" ? Visibility.Visible : Visibility.Collapsed,
                    Find<Button>(page, "FallbackButton").Visibility);
            }
            if (state == "diagnostics")
            {
                Assert.Equal(Visibility.Collapsed, Find<HyperlinkButton>(page, "HelpLink").Visibility);
                Assert.Equal(Visibility.Visible, Find<HyperlinkButton>(page, "ViewServerLogLink").Visibility);
                Assert.Contains("llama-server: Synthetic diagnostic", Find<TextBlock>(page, "ServerDiagnosticsText").Text);
                Assert.Equal(Visibility.Visible, Find<HyperlinkButton>(page, "ViewLogLink").Visibility);
            }
            Assert.Null(page.FindName("FlowProgress"));
            await ui.YieldToRenderAsync();
        });
    }

    [Theory]
    [InlineData("success", ElementTheme.Light)]
    [InlineData("success", ElementTheme.Dark)]
    [InlineData("local-ai", ElementTheme.Light)]
    [InlineData("local-ai", ElementTheme.Dark)]
    [InlineData("native", ElementTheme.Light)]
    [InlineData("native", ElementTheme.Dark)]
    [InlineData("failure", ElementTheme.Light)]
    [InlineData("failure", ElementTheme.Dark)]
    [InlineData("fallback", ElementTheme.Light)]
    [InlineData("fallback", ElementTheme.Dark)]
    [InlineData("restart", ElementTheme.Light)]
    [InlineData("restart", ElementTheme.Dark)]
    [InlineData("diagnostics", ElementTheme.Light)]
    [InlineData("diagnostics", ElementTheme.Dark)]
    public async Task CompletionShell_LongSummaryDiagnosticsAndActionsStayReachable(
        string state, ElementTheme theme)
    {
        await WithPageAsync(state, theme, async (page, _) =>
        {
            var shell = Find<SetupPageShell>(page, "CompleteShell");
            var action = Find<Button>(page, state == "restart" ? "RestartNowButton" : "LaunchButton");
            var title = Find<TextBlock>(page, "TitleText");
            var subtitle = Find<TextBlock>(page, "SubtitleText");
            var speech = Find<TextBlock>(page, "SpeechText");
            var mascotName = state is "success" or "local-ai" or "native" ? "MascotHero"
                : state == "restart" ? "RestartMascot" : "FailureMascot";
            var mascot = Find<OnboardingMascot>(page, mascotName);
            var startup = Find<ToggleSwitch>(page, "StartupToggle");
            var startupVisible = Find<Border>(page, "StartupRow").Visibility == Visibility.Visible;
            if (startupVisible) startup.IsOn = true;
            Assert.Equal(40, title.FontSize);
            Assert.Equal(18, subtitle.FontSize);
            Assert.Equal(AutomationHeadingLevel.Level1, AutomationProperties.GetHeadingLevel(title));
            Assert.Equal(TextAlignment.Left, subtitle.TextAlignment);
            Assert.Equal(state == "restart" ? "Almost there."
                : state is "success" or "local-ai" or "native" ? "All set." : "Let's sort this out.", speech.Text);
            Assert.True(action.Focus(FocusState.Keyboard));
            foreach (var size in new[] { (1120, 720), (960, 600), (959, 600), (960, 599), (720, 560), (1120, 720) })
            {
                page.Width = size.Item1;
                page.Height = size.Item2;
                page.UpdateLayout();
                await ui.YieldToRenderAsync();
                Assert.Equal(size.Item1 >= 960 && size.Item2 >= 600, shell.IsWideLayout);
                Assert.Same(action, FocusManager.GetFocusedElement(page.XamlRoot));
                Assert.Same(mascot, page.FindName(mascotName));
                if (startupVisible) Assert.True(startup.IsOn);
                Assert.Equal(0, shell.Scroller.ScrollableWidth);
                OnboardingNativeProof.AssertFullyVisible(Find<Grid>(page, "CompleteFooter"), shell);
                foreach (var button in new[] { "LaunchButton", "FallbackButton", "RestartNowButton", "RestartLaterButton" }
                    .Select(name => Find<Button>(page, name)).Where(button => button.Visibility == Visibility.Visible))
                {
                    OnboardingNativeProof.AssertFullyVisible(button, shell);
                    Assert.True(button.ActualHeight >= 40);
                }
                shell.Scroller.ChangeView(null, shell.Scroller.ScrollableHeight, null, true);
                await TestSupport.WaitForRenderedConditionAsync(
                    () => Math.Abs(shell.Scroller.VerticalOffset - shell.Scroller.ScrollableHeight) < 0.5,
                    "completion last viewport");
                var last = state == "diagnostics" ? (FrameworkElement)Find<HyperlinkButton>(page, "ViewServerLogLink")
                    : state is "failure" or "fallback" ? Find<HyperlinkButton>(page, "ViewLogLink")
                    : startupVisible ? Find<ToggleSwitch>(page, "StartupToggle") : subtitle;
                OnboardingNativeProof.AssertFullyVisible(last, shell.Scroller);
                Assert.Null(page.FindName("FlowProgress"));
            }
        });
    }

    private async Task WithPageAsync(string state, ElementTheme theme,
        Func<CompletePage, CompletePageArgs, Task> assertion)
    {
        using var temp = new TempDirectory("complete-shell-");
        var config = new SetupConfig();
        config.LocalAi.Enabled = state == "local-ai";
        var logDirectory = temp.Combine("server logs");
        Directory.CreateDirectory(logDirectory);
        var args = new CompletePageArgs(state is "success" or "local-ai" or "native", TimeSpan.FromMinutes(3),
            temp.Combine("setup.log"),
            "Synthetic setup failure. " + string.Join(" ", Enumerable.Repeat("Review the current diagnostics before retrying.", 12)) +
                " https://synthetic.invalid/help",
            DefaultAutoStart: false, ReviewSummary: SetupReviewSummaryBuilder.Build(config, temp.Path, temp.Path),
            CanRetryGatewayFallback: state == "fallback", GatewayFallbackVersion: "synthetic-version",
            Detail: state == "diagnostics" ? new LocalAiFailureDetail(
                Enumerable.Range(1, 32).Select(index => $"Synthetic diagnostic {index}. No server was started.").ToArray(),
                logDirectory) : null)
        {
            RequiresRestart = state == "restart",
            NativeGatewayUrl = state == "native" ? "wss://synthetic.invalid/current/" : null,
            NativeCapabilitySummary = state == "native" ? "Synthetic capability profile" : null,
        };
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(
                Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            var originalSize = ui.TestWindow.AppWindow.Size;
            var frame = new Frame { Width = 1120, Height = 720, RequestedTheme = theme };
            try
            {
                ui.TestWindow.AppWindow.Resize(new(1800, 1400));
                ui.Container.Children.Add(frame);
                Assert.True(frame.Navigate(typeof(CompletePage), args));
                await TestSupport.WaitForRenderedConditionAsync(
                    () => frame.Content is CompletePage { IsLoaded: true }, "completion page mounted");
                await assertion(Assert.IsType<CompletePage>(frame.Content), args);
            }
            finally
            {
                frame.Navigate(typeof(Page));
                ui.Container.Children.Clear();
                ui.TestWindow.AppWindow.Resize(originalSize);
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }

    private static T Find<T>(CompletePage page, string name) where T : FrameworkElement =>
        Assert.IsType<T>(page.FindName(name));
}
