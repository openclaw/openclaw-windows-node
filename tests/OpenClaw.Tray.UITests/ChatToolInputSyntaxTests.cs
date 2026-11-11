// Native proof for inert, theme-aware input syntax and uncolored output.
// Run with --filter FullyQualifiedName~ChatToolInputSyntaxTests.
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Chat;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

/// <summary>Protects syntax colors, exact selectable text and native control reuse.</summary>
[Collection(UICollection.Name)]
public sealed class ChatToolInputSyntaxTests(UIThreadFixture ui)
{
    private const string Command = "Get-ChildItem -Path $env:TEMP | Write-Output 'hello'\r\n# literal <xml> & text";

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public async Task ToolCard_ColorsPowerShellInputButKeepsOutputLiteralAndSecondary(string themeName)
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            using var theme = new ChatThemeProofScope(themeName);
            using var host = new ReactorHostControl { RequestedTheme = theme.ElementTheme, Width = 680 };
            var surface = new Border
            {
                Child = host, Width = 712, Padding = new Thickness(16),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                RequestedTheme = theme.ElementTheme,
            };
            var entry = new ChatTimelineItem("syntax", ChatTimelineItemKind.ToolCall, "",
                ToolName: "PowerShell", ToolResult: ChatToolCallStatus.Success, ToolOutput: Command,
                ToolInputDetails: new("", Command));
            var context = new ChatTimelinePresentationContext("syntax-proof", [entry], false, null);
            var element = ToolCallCardRenderer.BuildStandalone(context, entry);
            var root = host.Reconciler.Mount(element, static () => { });
            host.Content = root;
            var window = new Window { Content = surface };
            window.AppWindow.MoveAndResize(new(-32000, -32000, 1150, 550));
            window.Activate();
            try
            {
                var expander = Assert.Single(FindLogical<Expander>(root!));
                expander.IsExpanded = true;
                surface.UpdateLayout();
                await WaitForRenderedConditionAsync(() =>
                    FindDescendants<Border>(expander).Any(border => border.Name == "ExpanderContent" &&
                        border.Visibility == Visibility.Visible &&
                        (border.RenderTransform switch
                        {
                            CompositeTransform transform => transform.TranslateY,
                            TranslateTransform transform => transform.Y,
                            _ => 0,
                        }) == 0) &&
                    FindLogical<RichTextBlock>(root!).All(block => block.ActualHeight > 0) &&
                    FindDescendants<FrameworkElement>(expander)
                        .SelectMany(VisualStateManager.GetVisualStateGroups)
                        .Any(group => group.Name == "ExpandStates" && group.CurrentState is { } state &&
                            state.Storyboard?.GetCurrentState() != Microsoft.UI.Xaml.Media.Animation.ClockState.Active),
                    "expanded colored input and muted output become visible");
                var sections = FindLogical<RichTextBlock>(root!).ToArray();
                Assert.Equal(2, sections.Length);
                var input = sections[0];
                var output = sections[1];
                var runs = Runs(input).ToArray();
                Assert.Equal(Command, string.Concat(runs.Select(run => run.Text)));
                Assert.Equal(Command, string.Concat(Runs(output).Select(run => run.Text)));
                Assert.True(input.IsTextSelectionEnabled);
                Assert.True(output.IsTextSelectionEnabled);
                Assert.Equal(1, output.Opacity);
                Assert.All(Runs(output), run =>
                    Assert.Equal(DependencyProperty.UnsetValue, run.ReadLocalValue(TextElement.ForegroundProperty)));
                var ink = Assert.IsType<SolidColorBrush>(input.Foreground).Color;
                var muted = Assert.IsType<SolidColorBrush>(output.Foreground).Color;
                if (themeName != "HighContrast")
                    Assert.True(muted.A < ink.A, $"Output must use secondary ink: {ink} / {muted}.");

                var commandRun = Assert.Single(runs, run => run.Text == "Get-ChildItem");
                var operatorRun = Assert.Single(runs, run => run.Text == "|");
                var stringRun = Assert.Single(runs, run => run.Text == "'hello'");
                if (themeName == "HighContrast")
                    Assert.All(new[] { commandRun, operatorRun, stringRun }, run =>
                        Assert.Equal(ink, Assert.IsType<SolidColorBrush>(run.Foreground).Color));
                else
                {
                    Assert.NotEqual(ink, Assert.IsType<SolidColorBrush>(commandRun.Foreground).Color);
                    Assert.NotEqual(commandRun.Foreground, operatorRun.Foreground);
                    Assert.NotEqual(Assert.IsType<SolidColorBrush>(commandRun.Foreground).Color,
                        Assert.IsType<SolidColorBrush>(operatorRun.Foreground).Color);
                    Assert.NotEqual(Assert.IsType<SolidColorBrush>(operatorRun.Foreground).Color,
                        Assert.IsType<SolidColorBrush>(stringRun.Foreground).Color);
                }
                await VisualTestCapture.CaptureAsync(surface, $"ToolSyntax-{themeName}");
                if (themeName == "Light")
                {
                    var original = Assert.IsType<SolidColorBrush>(commandRun.Foreground).Color;
                    host.RequestedTheme = ElementTheme.Dark;
                    await ui.YieldToRenderAsync();
                    Assert.Same(commandRun, Runs(input).Single(run => run.Text == "Get-ChildItem"));
                    Assert.NotEqual(original, Assert.IsType<SolidColorBrush>(commandRun.Foreground).Color);
                    Assert.Equal(Command, string.Concat(Runs(input).Select(run => run.Text)));
                }
            }
            finally
            {
                host.Reconciler.Reconcile(element, Microsoft.UI.Reactor.Factories.Empty(), root!, static () => { });
                host.Content = null;
                window.Close();
            }
        });
    }

    [Fact]
    public async Task SyntaxRuns_PreserveJsonMarkupAndClearWhenTheSameTextBecomesOutput()
    {
        await ui.RunOnUIAsync(() =>
        {
            using var theme = new ChatThemeProofScope("Light");
            const string json = "{ \"path\": \"<tag>&value\", \"count\": 42, \"enabled\": true }";
            var target = new RichTextBlock();
            var entry = new ChatTimelineItem("json", ChatTimelineItemKind.ToolCall, "", ToolName: "read");
            ChatToolInputSyntax.Apply(target, json, entry);
            Assert.Equal(json, string.Concat(Runs(target).Select(run => run.Text)));
            Assert.Contains(Runs(target), run => run.Text == "42" &&
                run.ReadLocalValue(TextElement.ForegroundProperty) != DependencyProperty.UnsetValue);
            var paragraph = Assert.Single(target.Blocks);
            ChatToolInputSyntax.Apply(target, json, entry);
            Assert.Same(paragraph, Assert.Single(target.Blocks));
            ChatToolInputSyntax.Apply(target, json, null);
            Assert.Equal(json, Assert.Single(Runs(target)).Text);
            Assert.Equal(DependencyProperty.UnsetValue,
                Assert.Single(Runs(target)).ReadLocalValue(TextElement.ForegroundProperty));
        });
    }

    [Fact]
    public async Task UnknownInput_RemainsLiteralAndDoesNotGuessAShell()
    {
        await ui.RunOnUIAsync(() =>
        {
            using var theme = new ChatThemeProofScope("Light");
            var target = new RichTextBlock();
            var entry = new ChatTimelineItem("unknown", ChatTimelineItemKind.ToolCall, "", ToolName: "Bash");
            ChatToolInputSyntax.Apply(target, Command, entry);
            Assert.Equal(Command, Assert.Single(Runs(target)).Text);
            Assert.Equal(DependencyProperty.UnsetValue,
                Assert.Single(Runs(target)).ReadLocalValue(TextElement.ForegroundProperty));
        });
    }

    private static IEnumerable<Run> Runs(RichTextBlock block) =>
        block.Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines.OfType<Run>());
}
