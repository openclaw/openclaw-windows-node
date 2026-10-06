using System.Reflection;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClaw.SetupEngine.UI.Pages;

namespace OpenClaw.Tray.UITests;

public sealed partial class AiReadyPageRenderingTests
{
    [Theory]
    [InlineData(ElementTheme.Light)]
    [InlineData(ElementTheme.Dark)]
    public async Task ReadyShell_ChoicesLoadingAndFailureStayReachableAcrossBreakpoints(ElementTheme theme)
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(
                Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            var page = new AiReadyPage { Width = 1120, Height = 720, RequestedTheme = theme };
            var originalSize = ui.TestWindow.AppWindow.Size;
            try
            {
                ui.TestWindow.AppWindow.Resize(new(1800, 1400));
                ui.Container.Children.Add(page);
                await TestSupport.WaitForRenderedConditionAsync(() => page.IsLoaded, "ready shell mounted");
                var shell = Assert.IsType<SetupPageShell>(page.FindName("ReadyShell"));
                var choices = Assert.IsType<StackPanel>(page.FindName("Choices"));
                var chat = Assert.IsType<SettingsCard>(page.FindName("ChatChoice"));
                var model = Assert.IsType<TextBlock>(page.FindName("ModelSummary"));
                model.Text = "Synthetic verified model " + new string('m', 150);
                var timeline = Assert.IsType<SetupStepIndicator>(page.FindName("FlowProgress"));
                timeline.Update([OnboardingStage.Welcome, OnboardingStage.Capabilities,
                    OnboardingStage.AiSetup, OnboardingStage.Ready], OnboardingStage.Ready);
                var error = Assert.IsType<InfoBar>(page.FindName("ErrorBar"));
                var recovery = Assert.IsType<Button>(page.FindName("RecoveryButton"));
                Assert.Equal(Visibility.Collapsed, recovery.Visibility);
                Assert.Equal(["Chat", "Channels", "Skills"],
                    choices.Children.Cast<FrameworkElement>().Select(choice => choice.Tag));
                Assert.Equal(AutomationHeadingLevel.Level1, AutomationProperties.GetHeadingLevel(
                    Assert.IsType<TextBlock>(page.FindName("HeadingText"))));
                Assert.True(chat.Focus(FocusState.Keyboard));
                foreach (var size in new[] { (1120, 720), (960, 600), (959, 600), (960, 599), (720, 560), (1120, 720) })
                {
                    page.Width = size.Item1;
                    page.Height = size.Item2;
                    page.UpdateLayout();
                    await ui.YieldToRenderAsync();
                    Assert.Equal(size.Item1 >= 960 && size.Item2 >= 600, shell.IsWideLayout);
                    Assert.Same(chat, FocusManager.GetFocusedElement(page.XamlRoot));
                    Assert.Same(timeline, page.FindName("FlowProgress"));
                    Assert.Equal(0, shell.Scroller.ScrollableWidth);
                    shell.Scroller.ChangeView(null, shell.Scroller.ScrollableHeight, null, true);
                    await TestSupport.WaitForRenderedConditionAsync(
                        () => Math.Abs(shell.Scroller.VerticalOffset - shell.Scroller.ScrollableHeight) < 0.5,
                        "ready choice viewport");
                    OnboardingNativeProof.AssertFullyVisible(
                        Assert.IsType<SettingsCard>(page.FindName("SkillsChoice")), shell.Scroller);
                    error.Message = string.Join(" ", Enumerable.Repeat("Synthetic verification failed. Retry or return to AI setup.", 8));
                    error.IsOpen = true;
                    page.UpdateLayout();
                    await ui.YieldToRenderAsync();
                    Assert.Equal(Visibility.Visible, recovery.Visibility);
                    OnboardingNativeProof.AssertFullyVisible(recovery, shell);
                    var busy = typeof(AiReadyPage).GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    busy.Invoke(page, [true]);
                    Assert.All(choices.Children, choice => Assert.False(Assert.IsAssignableFrom<Control>(choice).IsEnabled));
                    Assert.False(recovery.IsEnabled);
                    Assert.Equal(Visibility.Visible, Assert.IsType<ProgressBar>(page.FindName("BusyProgress")).Visibility);
                    busy.Invoke(page, [false]);
                    Assert.All(choices.Children, choice => Assert.True(Assert.IsAssignableFrom<Control>(choice).IsEnabled));
                    error.IsOpen = false;
                    chat.Focus(FocusState.Keyboard);
                }
                Assert.Null(page.FindName("FinishButton"));
                Assert.Null(page.FindName("SkipButton"));
            }
            finally
            {
                await page.DisposeAsync();
                ui.Container.Children.Clear();
                ui.TestWindow.AppWindow.Resize(originalSize);
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }
}
