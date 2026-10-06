using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI.Controls;

namespace OpenClaw.Tray.UITests;

public sealed partial class OnboardingAiPageTests
{
    [Theory]
    [InlineData(ElementTheme.Light)]
    [InlineData(ElementTheme.Dark)]
    public async Task AiShell_BreakpointsKeepRuntimeChoicesKeyFocusAndFixedActions(ElementTheme theme)
    {
        var local = new PageLocalAiHost(LocalAiOnboardingState.Use);
        await WithPageAsync(async (page, transport, completed) =>
        {
            var shell = Find<SetupPageShell>(page, "AiShell");
            var mascot = Find<OnboardingMascot>(page, "MascotHero");
            var timeline = Find<SetupStepIndicator>(page, "FlowProgress");
            timeline.Update([OnboardingStage.Welcome, OnboardingStage.Capabilities,
                OnboardingStage.AiSetup, OnboardingStage.Ready], OnboardingStage.AiSetup);
            Assert.Equal(AutomationHeadingLevel.Level1,
                AutomationProperties.GetHeadingLevel(Find<TextBlock>(page, "TitleText")));
            Assert.Equal(40, Find<TextBlock>(page, "TitleText").FontSize);
            Assert.Equal(18, Find<TextBlock>(page, "StatusText").FontSize);
            var more = Find<SettingsExpander>(page, "MoreExpander");
            more.IsExpanded = true;
            await TestSupport.WaitForSettingsExpanderSettledAsync(ui, more, true);
            await InvokeCardAsync(Find<SettingsCard>(page, "ApiKeysButton"));
            var picker = Find<ComboBox>(page, "ApiProviderPicker");
            picker.SelectedIndex = 0;
            var selection = picker.SelectedItem;
            var input = Find<PasswordBox>(page, "ApiKeyInput");
            input.Password = "dummy";
            Assert.Equal(PasswordRevealMode.Hidden, input.PasswordRevealMode);
            Assert.True(input.Focus(FocusState.Keyboard));
            foreach (var size in new[] { (1120, 720), (960, 600), (959, 600), (960, 599), (720, 560), (1120, 720) })
            {
                page.Width = size.Item1;
                page.Height = size.Item2;
                page.UpdateLayout();
                await ui.YieldToRenderAsync();
                Assert.Equal(size.Item1 >= 960 && size.Item2 >= 600, shell.IsWideLayout);
                Assert.Same(mascot, page.FindName("MascotHero"));
                Assert.Same(timeline, page.FindName("FlowProgress"));
                Assert.Same(selection, picker.SelectedItem);
                Assert.Same(input, FocusManager.GetFocusedElement(page.XamlRoot));
                Assert.Equal("dummy", input.Password);
                Assert.True(more.IsExpanded);
                Assert.Equal(0, shell.Scroller.ScrollableWidth);
                OnboardingNativeProof.AssertFullyVisible(Find<Grid>(page, "AiFooter"), shell);
                shell.Scroller.ChangeView(null, shell.Scroller.ScrollableHeight, null, disableAnimation: true);
                await ui.YieldToRenderAsync();
                await OnboardingNativeProof.NextCompositionAsync();
                var position = input.TransformToVisual(shell.Scroller).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, input.ActualWidth, input.ActualHeight));
                output.WriteLine($"Measured viewport={size}; input={position}; offset={shell.Scroller.VerticalOffset}; extent={shell.Scroller.ExtentHeight}; viewportHeight={shell.Scroller.ViewportHeight}; scrollable={shell.Scroller.ScrollableHeight}; bringOnFocus={shell.Scroller.BringIntoViewOnFocusChange}");
                await TestSupport.WaitForRenderedConditionAsync(() =>
                {
                    var bounds = input.TransformToVisual(shell.Scroller).TransformBounds(
                        new Windows.Foundation.Rect(0, 0, input.ActualWidth, input.ActualHeight));
                    return bounds.Top >= 0 && bounds.Bottom <= shell.Scroller.ActualHeight &&
                        Math.Abs(shell.Scroller.VerticalOffset - shell.Scroller.ScrollableHeight) < 0.5;
                }, "API key field scroll visibility");
                OnboardingNativeProof.AssertFullyVisible(input, shell.Scroller);
                var textScale = new Windows.UI.ViewManagement.UISettings().TextScaleFactor;
                var horizontal = Find<Grid>(page, "ApiKeyFields").ActualWidth >= 560 * Math.Min(textScale, 1.5);
                Assert.Equal(horizontal ? 1 : 0, Grid.GetColumn(input));
                Assert.Equal(horizontal ? 0 : 1, Grid.GetRow(input));
            }
            var cards = TestSupport.FindDescendants<SettingsCard>(Find<ItemsControl>(page, "FeaturedSignInChoices")).ToArray();
            Assert.Contains(TestSupport.FindDescendants<TextBlock>(cards[0]),
                text => text.Text.Contains("deliberately long organization") && !text.IsTextTrimmed);
            Assert.Equal(["openclaw.setup.detect"], transport.MethodCalls);
            Assert.Equal(0, local.Actions);
            Assert.Equal(0, completed());
            await page.CloseAsync();
            Assert.Empty(input.Password);
        }, theme: theme, width: 1120, height: 720, localAiHost: local,
            configure: transport =>
            {
                transport.IncludeDensityChoices = true;
                transport.IncludeMoreProvider = true;
                transport.LongProviderLabels = true;
            });
    }
}
