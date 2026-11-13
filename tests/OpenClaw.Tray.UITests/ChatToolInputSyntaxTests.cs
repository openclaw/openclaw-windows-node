// Native proof for inert, theme-aware input syntax and uncolored output.
// Run with --filter FullyQualifiedName~ChatToolInputSyntaxTests.
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
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
    public async Task ToolCard_ColorsPowerShellInputButKeepsOutputLiteralWithPrimaryBlend(string themeName)
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            using var theme = new ChatThemeProofScope(themeName);
            using var host = new ReactorHostControl { RequestedTheme = theme.ElementTheme, Width = 680 };
            var secondary = new RichTextBlock
            {
                Style = (Style)Application.Current.Resources["ChatSecondaryRichTextStyle"],
                Width = 0, Height = 0, IsHitTestVisible = false,
                RequestedTheme = theme.ElementTheme,
            };
            var content = new Grid();
            content.Children.Add(host);
            content.Children.Add(secondary);
            var surface = new Border
            {
                Child = content, Width = 712, Padding = new Thickness(16),
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
                AssertCommandTone(input, output, secondary);

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

                // Native text controls can arrive from another renderer with local
                // foreground, opacity or inline-code highlights. Reuse must retire them.
                output.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
                output.Opacity = 0.25;
                output.TextHighlighters.Add(new TextHighlighter());
                var generic = entry with { ToolName = "read_file", ToolResult = ChatToolCallStatus.Error };
                var next = ToolCallCardRenderer.BuildStandalone(context, generic);
                root = host.Reconciler.Reconcile(element, next, root!, static () => { });
                element = next;
                host.Content = root;
                await ui.YieldToRenderAsync();
                Assert.Same(output, FindLogical<RichTextBlock>(root!).Last());
                Assert.Equal(1, output.Opacity);
                Assert.Empty(output.TextHighlighters);
                Assert.Equal(Assert.IsType<SolidColorBrush>(input.Foreground).Color,
                    Assert.IsType<SolidColorBrush>(output.Foreground).Color);
                Assert.Equal(1, output.Foreground.Opacity);
                Assert.Equal(Command, string.Concat(Runs(output).Select(run => run.Text)));
                Assert.All(Runs(output), run =>
                    Assert.Equal(DependencyProperty.UnsetValue, run.ReadLocalValue(TextElement.ForegroundProperty)));

                next = ToolCallCardRenderer.BuildStandalone(context, entry);
                root = host.Reconciler.Reconcile(element, next, root!, static () => { });
                element = next;
                host.Content = root;
                await ui.YieldToRenderAsync();
                Assert.Same(output, FindLogical<RichTextBlock>(root!).Last());
                AssertCommandTone(input, output, secondary);
                if (themeName == "Light")
                {
                    commandRun = Runs(input).Single(run => run.Text == "Get-ChildItem");
                    var original = Assert.IsType<SolidColorBrush>(commandRun.Foreground).Color;
                    host.RequestedTheme = ElementTheme.Dark;
                    secondary.RequestedTheme = ElementTheme.Dark;
                    await ui.YieldToRenderAsync();
                    Assert.Same(commandRun, Runs(input).Single(run => run.Text == "Get-ChildItem"));
                    Assert.NotEqual(original, Assert.IsType<SolidColorBrush>(commandRun.Foreground).Color);
                    Assert.Equal(Command, string.Concat(Runs(input).Select(run => run.Text)));
                    AssertCommandTone(input, output, secondary);
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

    private static void AssertCommandTone(RichTextBlock input, RichTextBlock output, RichTextBlock secondary)
    {
        var primaryBrush = Assert.IsType<SolidColorBrush>(input.Foreground);
        var secondaryBrush = Assert.IsType<SolidColorBrush>(secondary.Foreground);
        var outputBrush = Assert.IsType<SolidColorBrush>(output.Foreground);
        Assert.Equal(primaryBrush.Color.R, outputBrush.Color.R);
        Assert.Equal(primaryBrush.Color.G, outputBrush.Color.G);
        Assert.Equal(primaryBrush.Color.B, outputBrush.Color.B);
        var expectedAlpha = 0.82 * primaryBrush.Color.A * primaryBrush.Opacity +
            0.18 * secondaryBrush.Color.A * secondaryBrush.Opacity;
        Assert.InRange(outputBrush.Color.A * outputBrush.Opacity, expectedAlpha - 0.5, expectedAlpha + 0.5);
        Assert.Equal(1, output.Opacity);
    }

    [Theory]
    [InlineData("PowerShell")]
    [InlineData("read_file")]
    public async Task ToolDetails_AvailabilityChangesPreserveMountedReaderSelectionAndScroll(string toolName)
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl { Width = 680 };
            var surface = new Border { Child = host, Width = 712, Padding = new Thickness(16) };
            var text = string.Join("\n", Enumerable.Range(1, 100).Select(index => $"Literal line {index}"));
            var entry = new ChatTimelineItem("continuity", ChatTimelineItemKind.ToolCall, toolName,
                ToolName: toolName, ToolCallId: "call", ToolResult: ChatToolCallStatus.Success,
                ToolOutput: text, ToolInputDetails: new("", text));
            var opened = 0;
            var context = new ChatTimelinePresentationContext("reader-proof", [entry], false, null,
                OnOpenToolDetails: _ => opened++);
            var element = ToolCallCardRenderer.BuildStandalone(context, entry);
            var root = host.Reconciler.Mount(element, static () => { });
            host.Content = root;
            var window = new Window { Content = surface };
            window.AppWindow.MoveAndResize(new(-32000, -32000, 1150, 850));
            window.Activate();
            // Reactor does not keep managed wrappers for mounted controls. Collect them before the
            // reader is captured so retention cannot depend on a managed RichTextBlock wrapper.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            try
            {
                Assert.Single(FindLogical<Expander>(root!)).IsExpanded = true;
                surface.UpdateLayout();
                await WaitForRenderedConditionAsync(() =>
                    FindLogical<ScrollViewer>(root!).Count(scroll => scroll.ScrollableHeight > 120) == 2,
                    "both inline readers have selectable, scrollable content");
                var readers = FindLogical<ScrollViewer>(root!).ToArray();
                var blocks = readers.Select(reader => Assert.IsType<RichTextBlock>(reader.Content)).ToArray();
                var paragraphs = blocks.Select(block => Assert.Single(block.Blocks)).ToArray();
                blocks[1].SelectAll();
                Assert.Equal(text, blocks[1].SelectedText);
                foreach (var reader in readers)
                    Assert.True(reader.ChangeView(null, 80, null, disableAnimation: true));
                await WaitForRenderedConditionAsync(() => readers.All(reader => reader.VerticalOffset == 80),
                    "both inline readers retain a nonzero offset");
                var action = Assert.Single(FindLogical<Button>(root!), button =>
                    AutomationProperties.GetAutomationId(button) == "ChatToolDetails_continuity");
                foreach (var available in new[] { false, true })
                {
                    var next = ToolCallCardRenderer.BuildStandalone(
                        context with { OnOpenToolDetails = available ? context.OnOpenToolDetails : null }, entry);
                    root = host.Reconciler.Reconcile(element, next, root!, static () => { });
                    element = next;
                    host.Content = root;
                    await ui.YieldToRenderAsync();
                    Assert.Equal(readers, FindLogical<ScrollViewer>(root!).ToArray());
                    Assert.Equal(blocks, FindLogical<RichTextBlock>(root!).ToArray());
                    for (var index = 0; index < blocks.Length; index++)
                    {
                        Assert.Same(paragraphs[index], Assert.Single(blocks[index].Blocks));
                        Assert.Equal(80, readers[index].VerticalOffset);
                    }
                    Assert.Equal(text, blocks[1].SelectedText);
                    Assert.Same(action, Assert.Single(FindLogical<Button>(root!), button =>
                        AutomationProperties.GetAutomationId(button) == "ChatToolDetails_continuity"));
                    Assert.Equal(available, action.IsEnabled);
                    Assert.Equal(available ? Visibility.Visible : Visibility.Collapsed, action.Visibility);
                    var invoke = Assert.IsAssignableFrom<IInvokeProvider>(
                        new ButtonAutomationPeer(action).GetPattern(PatternInterface.Invoke));
                    try { invoke.Invoke(); }
                    catch (Exception ex) when (!available && ex.HResult == unchecked((int)0x80040200))
                    {
                        // UIA may reject a disabled native button before its guarded action runs.
                    }
                    await ui.YieldToRenderAsync();
                    Assert.Equal(available ? 1 : 0, opened);
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
