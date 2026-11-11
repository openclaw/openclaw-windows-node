// Native regression for a tray crash: Reactor writes the non-nullable
// RichTextBlockElement.IsTextSelectionEnabled (default false) on every mount and
// update. Chat surfaces that enabled selection only through a setter flipped the live
// control true -> false -> true on each pass, and WinUI faulted in
// CRichTextBlock::OnSelectionEnabledChanged -> TextSelectionManager::ReleaseGrippers
// on a reused control. Selection must be declared on the record so no pass writes false.
// Run with --filter FullyQualifiedName~ChatRichTextSelectionStabilityTests.
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Chat;
using OpenClawTray.Chat;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class ChatRichTextSelectionStabilityTests(UIThreadFixture ui)
{
    [Fact]
    public async Task CodeBlock_UpdateNeverTurnsSelectionOff()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(() =>
        {
            using var host = new ReactorHostControl { Width = 480 };
            var first = ChatMarkdownPresentation.CodeBlock("x = 1", "python");
            var root = host.Reconciler.Mount(first, static () => { });
            host.Content = root;
            var block = Assert.Single(FindLogical<RichTextBlock>(root!));
            Assert.True(block.IsTextSelectionEnabled);
            var offWrites = 0;
            var token = block.RegisterPropertyChangedCallback(
                RichTextBlock.IsTextSelectionEnabledProperty,
                (sender, _) => { if (!((RichTextBlock)sender).IsTextSelectionEnabled) offWrites++; });
            var current = first;
            try
            {
                foreach (var code in new[] { "x = 2", "x = 2", "print(x)" })
                {
                    var next = ChatMarkdownPresentation.CodeBlock(code, "python");
                    Assert.Same(root, host.Reconciler.Reconcile(current, next, root, static () => { }));
                    current = next;
                }
                Assert.Same(block, Assert.Single(FindLogical<RichTextBlock>(root!)));
                Assert.Equal(0, offWrites);
                Assert.True(block.IsTextSelectionEnabled);
            }
            finally
            {
                block.UnregisterPropertyChangedCallback(RichTextBlock.IsTextSelectionEnabledProperty, token);
                host.Reconciler.Reconcile(current, Microsoft.UI.Reactor.Factories.Empty(), root!, static () => { });
                host.Content = null;
            }
        });
    }

    [Fact]
    public async Task ToolCardDetails_UpdateNeverTurnsSelectionOff()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            using var host = new ReactorHostControl { Width = 680 };
            var surface = new Border
            {
                Child = host, Width = 712, Padding = new Thickness(16),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            };
            static Microsoft.UI.Reactor.Core.Element Card(string output)
            {
                var entry = new ChatTimelineItem("select", ChatTimelineItemKind.ToolCall, "",
                    ToolName: "PowerShell", ToolResult: ChatToolCallStatus.Success, ToolOutput: output,
                    ToolInputDetails: new("", "Get-Date"));
                var context = new ChatTimelinePresentationContext("selection-proof", [entry], false, null);
                return ToolCallCardRenderer.BuildStandalone(context, entry);
            }
            var current = Card("first output");
            var root = host.Reconciler.Mount(current, static () => { });
            host.Content = root;
            var window = new Window { Content = surface };
            window.AppWindow.MoveAndResize(new(-32000, -32000, 1150, 550));
            window.Activate();
            var tokens = new List<(RichTextBlock Block, long Token)>();
            try
            {
                var expander = Assert.Single(FindLogical<Expander>(root!));
                expander.IsExpanded = true;
                surface.UpdateLayout();
                await WaitForRenderedConditionAsync(
                    () => FindLogical<RichTextBlock>(root!).Count() == 2,
                    "expanded tool card mounts its input and output sections");
                var offWrites = 0;
                foreach (var block in FindLogical<RichTextBlock>(root!))
                {
                    Assert.True(block.IsTextSelectionEnabled);
                    tokens.Add((block, block.RegisterPropertyChangedCallback(
                        RichTextBlock.IsTextSelectionEnabledProperty,
                        (sender, _) => { if (!((RichTextBlock)sender).IsTextSelectionEnabled) offWrites++; })));
                }
                foreach (var output in new[] { "second output", "third output" })
                {
                    var next = Card(output);
                    host.Reconciler.Reconcile(current, next, root, static () => { });
                    current = next;
                    await ui.YieldToRenderAsync();
                }
                Assert.Equal(0, offWrites);
                Assert.All(FindLogical<RichTextBlock>(root!), block => Assert.True(block.IsTextSelectionEnabled));
            }
            finally
            {
                foreach (var (block, token) in tokens)
                    block.UnregisterPropertyChangedCallback(RichTextBlock.IsTextSelectionEnabledProperty, token);
                host.Reconciler.Reconcile(current, Microsoft.UI.Reactor.Factories.Empty(), root!, static () => { });
                host.Content = null;
                window.Close();
            }
        });
    }
}
