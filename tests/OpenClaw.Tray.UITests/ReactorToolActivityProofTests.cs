using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using OpenClaw.Chat;
using OpenClawTray.Chat;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

/// <summary>Offscreen real-runtime proof for the production Reactor tool activity renderer.</summary>
[Collection(UICollection.Name)]
public sealed class ReactorToolActivityProofTests
{
    private readonly UIThreadFixture _ui;

    public ReactorToolActivityProofTests(UIThreadFixture ui) => _ui = ui;

    [Fact]
    public async Task ConsecutiveTools_RenderCompactActivityWithAccessibleExpansion()
    {
        await _ui.ResetContainerAsync();
        ReactorHostControl? host = null;

        await _ui.RunOnUIAsync(() =>
        {
            TestApp.EnsureFluentBrushFallbacks(Application.Current.Resources);
            _ui.Container.Width = 900;
            _ui.Container.Height = 640;
            host = new ReactorHostControl { Width = 860, Height = 560 };
            _ui.Container.Children.Add(host);
            Mount(host, BuildTimeline(
            [
                Tool("tool-1", "powershell", "first output", ChatToolCallStatus.Success),
                Tool("tool-2", "read_file", "second output", ChatToolCallStatus.Success),
            ], generation: 1));
        });
        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            var activity = Assert.Single(
                FindDescendants<Expander>(host!),
                expander => AutomationProperties.GetAutomationId(expander) == "ChatToolActivity_tool_1");
            Assert.False(activity.IsExpanded);
            var name = AutomationProperties.GetName(activity);
            Assert.Contains("Activity:", name, StringComparison.Ordinal);
            Assert.Contains("2 tools", name, StringComparison.Ordinal);
            Assert.Contains("Collapsed", name, StringComparison.Ordinal);
            activity.IsExpanded = true;
        });
        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            var expanders = FindDescendants<Expander>(host!).ToArray();
            var activity = Assert.Single(
                expanders,
                expander => AutomationProperties.GetAutomationId(expander) == "ChatToolActivity_tool_1");
            Assert.True(activity.IsExpanded);
            Assert.Contains("Expanded", AutomationProperties.GetName(activity), StringComparison.Ordinal);

            var nested = expanders
                .Where(expander => AutomationProperties.GetAutomationId(expander).StartsWith(
                    "ChatToolCall_",
                    StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(2, nested.Length);
            Assert.All(nested, expander =>
            {
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(expander)));
                expander.IsExpanded = true;
            });
        });
        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            var selectableOutputs = FindDescendants<RichTextBlock>(host!)
                .Where(text => text.IsTextSelectionEnabled)
                .Select(CollectText)
                .ToArray();
            Assert.Contains("first output", selectableOutputs);
            Assert.Contains("second output", selectableOutputs);
            host!.Dispose();
        });
    }

    private static void Mount(ReactorHostControl host, OpenClawChatTimelineProps timeline) =>
        host.Mount(_ => Component<ReactorChatTimeline, ReactorChatTimelineProps>(
            new ReactorChatTimelineProps(ReactorChatTimelineMode.Timeline, timeline)));

    private static OpenClawChatTimelineProps BuildTimeline(
        IReadOnlyList<ChatTimelineItem> entries,
        long generation) =>
        new(
            SessionId: "reactor-tool-activity-proof",
            Entries: entries,
            HasMoreHistory: false,
            OnLoadMoreHistory: null,
            TimelineGeneration: generation,
            ShowToolCalls: true);

    private static ChatTimelineItem Tool(
        string id,
        string name,
        string output,
        ChatToolCallStatus result) =>
        new(
            id,
            ChatTimelineItemKind.ToolCall,
            name,
            ToolName: name,
            ToolResult: result,
            ToolOutput: output);

    private static string CollectText(RichTextBlock richTextBlock)
    {
        var text = new System.Text.StringBuilder();
        foreach (var paragraph in richTextBlock.Blocks.OfType<Paragraph>())
        {
            foreach (var inline in paragraph.Inlines)
                AppendInlineText(inline, text);
        }

        return text.ToString();
    }

    private static void AppendInlineText(Inline inline, System.Text.StringBuilder text)
    {
        switch (inline)
        {
            case Run run:
                text.Append(run.Text);
                break;
            case Span span:
                foreach (var child in span.Inlines)
                    AppendInlineText(child, text);
                break;
            case LineBreak:
                text.Append('\n');
                break;
        }
    }

    private async Task DrainRenderQueueAsync()
    {
        for (var pass = 0; pass < 3; pass++)
        {
            await _ui.RunOnUIAsync(() => _ui.Container.UpdateLayout());
            await _ui.YieldToRenderAsync();
            await Task.Delay(40);
        }
    }
}
