// Native regression for a tray crash: Reactor writes the non-nullable
// RichTextBlockElement.IsTextSelectionEnabled (default false) on every mount, before
// element setters run, and its pool does not reset selection on a returned
// RichTextBlock. Chat surfaces that enabled selection only through a setter therefore
// flipped a recycled control true -> false -> true on remount, and WinUI faulted in
// CRichTextBlock::OnSelectionEnabledChanged -> TextSelectionManager::ReleaseGrippers.
// Selection must be declared on the record so no mount writes false.
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
    // The chat list recycles RichTextBlocks through the Reactor pool, which does not
    // reset selection on them. Seed the pool with a selectable control like the one the
    // crashing session reused, then mount the chat element onto it.
    private static (RichTextBlock Block, Func<int> OffWrites, Action Release) SeedSelectablePooledBlock(
        ReactorHostControl host)
    {
        var block = new RichTextBlock { IsTextSelectionEnabled = true };
        host.Reconciler.ReturnControl(block);
        var offWrites = 0;
        var token = block.RegisterPropertyChangedCallback(
            RichTextBlock.IsTextSelectionEnabledProperty,
            (sender, _) => { if (!((RichTextBlock)sender).IsTextSelectionEnabled) offWrites++; });
        return (block, () => offWrites,
            () => block.UnregisterPropertyChangedCallback(RichTextBlock.IsTextSelectionEnabledProperty, token));
    }

    [Fact]
    public async Task CodeBlock_RecycledControlNeverTurnsSelectionOff()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(() =>
        {
            using var host = new ReactorHostControl { Width = 480 };
            var (block, offWrites, release) = SeedSelectablePooledBlock(host);
            var root = host.Reconciler.Mount(ChatMarkdownPresentation.CodeBlock("print(x)", "python"), static () => { });
            host.Content = root;
            try
            {
                Assert.Same(block, Assert.Single(FindLogical<RichTextBlock>(root!)));
                Assert.Equal(0, offWrites());
                Assert.True(block.IsTextSelectionEnabled);
            }
            finally
            {
                release();
                host.Content = null;
                host.Reconciler.UnmountChild(root!);
            }
        });
    }

    [Fact]
    public async Task ToolCardDetails_RecycledControlNeverTurnsSelectionOff()
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
            var entry = new ChatTimelineItem("select", ChatTimelineItemKind.ToolCall, "",
                ToolName: "PowerShell", ToolResult: ChatToolCallStatus.Success, ToolOutput: "output",
                ToolInputDetails: new("", "Get-Date"));
            var context = new ChatTimelinePresentationContext("selection-proof", [entry], false, null);
            var (block, offWrites, release) = SeedSelectablePooledBlock(host);
            var root = host.Reconciler.Mount(ToolCallCardRenderer.BuildStandalone(context, entry), static () => { });
            host.Content = root;
            var window = new Window { Content = surface };
            window.AppWindow.MoveAndResize(new(-32000, -32000, 1150, 550));
            window.Activate();
            try
            {
                Assert.Single(FindLogical<Expander>(root!)).IsExpanded = true;
                surface.UpdateLayout();
                await WaitForRenderedConditionAsync(
                    () => FindLogical<RichTextBlock>(root!).Count() == 2,
                    "expanded tool card mounts its input and output sections");
                Assert.Contains(block, FindLogical<RichTextBlock>(root!));
                Assert.Equal(0, offWrites());
                Assert.All(FindLogical<RichTextBlock>(root!), text => Assert.True(text.IsTextSelectionEnabled));
            }
            finally
            {
                release();
                host.Content = null;
                host.Reconciler.UnmountChild(root!);
                window.Close();
            }
        });
    }
}
