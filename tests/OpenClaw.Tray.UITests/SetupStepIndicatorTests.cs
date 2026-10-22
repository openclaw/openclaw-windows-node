using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI.Controls;
using Windows.UI.ViewManagement;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class SetupStepIndicatorTests(UIThreadFixture ui)
{
    private static SetupStepItem[] FirstState() =>
    [
        new("Choose your PC tools", SetupStepState.Current, "Start with the access that suits you."),
        new("Set up", SetupStepState.Upcoming),
        new("Connect your AI", SetupStepState.Upcoming),
        new("Start chatting", SetupStepState.Upcoming),
    ];

    private static SetupStepItem[] SecondState() =>
    [
        new("Choose your PC tools", SetupStepState.Completed, "Start with the access that suits you."),
        new("Set up", SetupStepState.Current, "OpenClaw installs. You can keep using your PC."),
        new("Connect your AI", SetupStepState.Upcoming),
        new("Start chatting", SetupStepState.Upcoming),
    ];

    [Theory]
    [InlineData(ElementTheme.Light)]
    [InlineData(ElementTheme.Dark)]
    public async Task FigmaStates_RenderNativeNodesConnectorsAndHelperText(ElementTheme theme)
    {
        await WithIndicatorAsync(async (indicator, root) =>
        {
            indicator.IsAnimationEnabled = false;
            root.RequestedTheme = theme;
            indicator.Update(FirstState());
            await ui.YieldToRenderAsync();
            var titles = Named<TextBlock>(indicator, "StepTitle");
            Assert.Equal(4, titles.Length);
            Assert.All(titles, title => { Assert.Equal(13, title.FontSize); Assert.Equal(18, title.LineHeight); });
            var current = Named<Ellipse>(indicator, "CurrentDot");
            var future = Named<Ellipse>(indicator, "UpcomingDot");
            Assert.Equal(new[] { Visibility.Visible, Visibility.Collapsed, Visibility.Collapsed, Visibility.Collapsed },
                current.Select(dot => dot.Visibility));
            Assert.Equal(12, current[0].Width);
            Assert.All(future, dot => Assert.Equal(10, dot.Width));
            Assert.Equal("Choose your PC tools, current step", AutomationProperties.GetName(titles[0]));
            Assert.Equal("Set up, upcoming", AutomationProperties.GetName(titles[1]));
            Assert.Equal("Start with the access that suits you.", Named<TextBlock>(indicator, "StepDescription")[0].Text);
            Assert.Equal(Visibility.Collapsed, Named<Border>(indicator, "BottomConnector")[3].Visibility);
            Assert.Equal(Visibility.Collapsed, Named<Border>(indicator, "TopConnector")[0].Visibility);
            Assert.All(Named<Border>(indicator, "TopConnector").Skip(1),
                line => Assert.Equal(Visibility.Visible, line.Visibility));
            Assert.All(Named<Border>(indicator, "FilledConnector"), line => Assert.Equal(Visibility.Collapsed, line.Visibility));

            indicator.Update(SecondState());
            await ui.YieldToRenderAsync();
            Assert.Same(titles[0], Named<TextBlock>(indicator, "StepTitle")[0]);
            Assert.Equal(Visibility.Visible, Named<Grid>(indicator, "CompletedDot")[0].Visibility);
            Assert.Equal(Visibility.Visible, current[1].Visibility);
            Assert.Equal(Visibility.Collapsed, future[0].Visibility);
            Assert.Equal(Visibility.Collapsed, future[1].Visibility);
            Assert.Equal(Visibility.Visible, Named<Border>(indicator, "FilledConnector")[0].Visibility);
            Assert.Equal(Visibility.Visible, Named<Border>(indicator, "FilledTopConnector")[1].Visibility);
            Assert.Equal(Visibility.Collapsed, Named<Border>(indicator, "TopConnector")[0].Visibility);
            Assert.Equal("Choose your PC tools, completed", AutomationProperties.GetName(titles[0]));
            Assert.Equal("Set up, current step", AutomationProperties.GetName(titles[1]));
            Assert.Equal("OpenClaw installs. You can keep using your PC.", Named<TextBlock>(indicator, "StepDescription")[1].Text);
            Assert.Empty(TestSupport.FindDescendants<Button>(indicator));
            Assert.All(Named<TextBlock>(indicator, "StepDescription"), caption => Assert.Equal(1, caption.Opacity));
            Assert.Equal(theme, current[1].ActualTheme);
        });
    }

    [Fact]
    public async Task NarrowLocalizedSteps_WrapAndGrowWithoutHorizontalOverflow()
    {
        await WithIndicatorAsync(async (indicator, root) =>
        {
            root.Width = 190;
            indicator.Update([
                new("Choose your PC tools with a longer localized heading", SetupStepState.Current,
                    "A longer helper explanation that must wrap rather than clip or shrink its text."),
                new("Connect your AI", SetupStepState.Upcoming),
            ]);
            await ui.YieldToRenderAsync();
            root.UpdateLayout();
            var titles = Named<TextBlock>(indicator, "StepTitle");
            Assert.True(titles[0].ActualHeight > 18);
            Assert.True(Named<TextBlock>(indicator, "StepDescription")[0].ActualHeight > 16);
            Assert.All(titles, title => Assert.False(title.IsTextTrimmed));
            foreach (var title in titles)
            {
                var bounds = title.TransformToVisual(root).TransformBounds(new(0, 0, title.ActualWidth, title.ActualHeight));
                Assert.True(bounds.Right <= root.ActualWidth + 0.1);
            }
        });
    }

    [Fact]
    public async Task PauseAndUnload_StopFiniteTransitionsAndReleaseGlow()
    {
        await WithIndicatorAsync(async (indicator, root) =>
        {
            indicator.Update(FirstState());
            await ui.YieldToRenderAsync();
            indicator.Update(SecondState());
            await ui.YieldToRenderAsync();
            var transform = Assert.IsType<ScaleTransform>(Named<Ellipse>(indicator, "CurrentDot")[1].RenderTransform);
            var glow = Named<Grid>(indicator, "GlowHost")[1];
            Assert.Equal(!new AccessibilitySettings().HighContrast,
                ElementCompositionPreview.GetElementChildVisual(glow) is not null);
            if (new UISettings().AnimationsEnabled)
                Assert.InRange(transform.ScaleX, 1, 4d / 3 + 0.01);
            else
                Assert.Equal(1, transform.ScaleX);
            indicator.IsAnimationEnabled = false;
            await ui.YieldToRenderAsync();
            Assert.Equal(1, transform.ScaleX);
            Assert.Equal(1, Assert.IsType<ScaleTransform>(Named<Border>(indicator, "FilledConnector")[0].RenderTransform).ScaleY);
            root.Children.Clear();
            await ui.YieldToRenderAsync();
            Assert.Null(ElementCompositionPreview.GetElementChildVisual(glow));
        });
    }

    [Fact]
    public async Task NewIndicator_AnimatesFromPreviousPageMilestone()
    {
        await WithIndicatorAsync(async (indicator, root) =>
        {
            var previous = new SetupStepIndicator();
            root.Children.Add(previous);
            previous.Update(
                [OnboardingStage.Welcome, OnboardingStage.Gateway, OnboardingStage.Capabilities,
                    OnboardingStage.GatewayReview, OnboardingStage.Install, OnboardingStage.AiSetup,
                    OnboardingStage.Ready],
                OnboardingStage.Install,
                OnboardingStage.GatewayReview);
            root.UpdateLayout();
            await ui.YieldToRenderAsync();
            if (new UISettings().AnimationsEnabled)
                await TestSupport.WaitForRenderedConditionAsync(
                    () => Assert.IsType<ScaleTransform>(
                        Named<Ellipse>(previous, "CurrentDot")[1].RenderTransform).ScaleX > 1,
                    "cross-page step transition starts");
            else
                Assert.Equal(1, Assert.IsType<ScaleTransform>(
                    Named<Ellipse>(previous, "CurrentDot")[1].RenderTransform).ScaleX);
            root.Children.Remove(previous);
        });
    }

    [Fact]
    public async Task InvalidStateOrder_IsRejectedBeforeChangingMountedContent()
    {
        await WithIndicatorAsync((indicator, _) =>
        {
            indicator.Update(FirstState());
            var title = Named<TextBlock>(indicator, "StepTitle")[0];
            Assert.Throws<ArgumentException>(() => indicator.Update([
                new("One", SetupStepState.Current), new("Two", SetupStepState.Current),
            ]));
            Assert.Throws<ArgumentException>(() => indicator.Update([
                new("One", SetupStepState.Upcoming), new("Two", SetupStepState.Completed),
            ]));
            Assert.Throws<ArgumentOutOfRangeException>(() => indicator.Update([new("One", (SetupStepState)99)]));
            Assert.Same(title, Named<TextBlock>(indicator, "StepTitle")[0]);
            indicator.Update(Array.Empty<SetupStepItem>());
            Assert.Equal(Visibility.Collapsed, indicator.Visibility);
            return Task.CompletedTask;
        });
    }

    private static T[] Named<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        TestSupport.FindDescendants<T>(root).Where(element => element.Name == name).ToArray();

    private async Task WithIndicatorAsync(Func<SetupStepIndicator, Grid, Task> assertion)
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var originalSize = ui.TestWindow.AppWindow.Size;
            var scale = ui.Container.XamlRoot.RasterizationScale;
            ui.TestWindow.AppWindow.Resize(new((int)(600 * scale), (int)(600 * scale)));
            var indicator = new SetupStepIndicator();
            var root = new Grid { Width = 320, Height = 320 };
            root.Children.Add(indicator);
            ui.Container.Children.Add(root);
            try
            {
                indicator.Update(FirstState());
                await TestSupport.WaitForRenderedConditionAsync(() => indicator.IsLoaded, "standalone step indicator loaded");
                await assertion(indicator, root);
            }
            finally
            {
                root.Children.Clear();
                ui.Container.Children.Clear();
                await ui.YieldToRenderAsync();
                ui.TestWindow.AppWindow.Resize(originalSize);
            }
        });
    }
}
