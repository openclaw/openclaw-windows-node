using Microsoft.UI.Reactor.Core;
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
        var timeline = BuildTimeline(
        [
            Tool("tool-1", "powershell", "first output", ChatToolCallStatus.Success),
            Tool("tool-2", "read_file", "second output", ChatToolCallStatus.Success),
        ], generation: 1);
        var activityRow = Assert.Single(
            ChatToolActivityPresentation.Project(
                timeline.Entries,
                timeline.SessionId,
                timeline.TimelineGeneration,
                timeline.ShowToolCalls),
            row => row.IsActivityGroup);
        var expansionState = new ChatToolActivityExpansionState();
        ReactorHostControl? host = null;
        UIElement? root = null;
        Element? currentElement = null;
        var disposing = false;
        try
        {
            await _ui.RunOnUIAsync(() =>
            {
                TestApp.EnsureFluentBrushFallbacks(Application.Current.Resources);
                // Reconcile native controls without attaching them to the VSTest visual tree,
                // where applying the unpackaged Expander template crashes Microsoft.UI.Xaml.
                host = new ReactorHostControl();
                Action? reconcile = null;
                Action requestRender = () =>
                {
                    if (!disposing)
                        _ui.Dispatcher.TryEnqueue(() => reconcile!());
                };
                reconcile = () =>
                {
                    if (disposing)
                        return;

                    var nextElement = ToolCallCardRenderer.BuildActivity(
                        timeline,
                        activityRow,
                        expansionState);
                    root = host.Reconciler.Reconcile(
                        currentElement!,
                        nextElement,
                        root!,
                        requestRender);
                    currentElement = nextElement;
                    host.Content = root;
                };
                currentElement = ToolCallCardRenderer.BuildActivity(
                    timeline,
                    activityRow,
                    expansionState);
                root = host.Reconciler.Mount(currentElement, requestRender);
                host.Content = root;
            });

            await _ui.RunOnUIAsync(() =>
            {
                var activity = Assert.Single(
                    FindLogical<Expander>(root!),
                    expander => AutomationProperties.GetAutomationId(expander) == "ChatToolActivity_tool_1");
                Assert.False(activity.IsExpanded);
                var name = AutomationProperties.GetName(activity);
                Assert.Contains("Activity:", name, StringComparison.Ordinal);
                Assert.Contains("2 tools", name, StringComparison.Ordinal);
                Assert.Contains("Collapsed", name, StringComparison.Ordinal);
                Assert.DoesNotContain(
                    FindLogical<Expander>(root!),
                    expander => AutomationProperties.GetAutomationId(expander).StartsWith(
                        "ChatToolCall_",
                        StringComparison.Ordinal));

                activity.IsExpanded = true;
            });
            await DrainReactorQueueAsync();

            await _ui.RunOnUIAsync(() =>
            {
                Assert.True(expansionState.IsExpanded(
                    activityRow.Key,
                    activityRow.Summary!,
                    timeline.ToolCallsCollapseVersion));
                var expanders = FindLogical<Expander>(root!).ToArray();
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
                Assert.DoesNotContain(
                    FindExpandedLogical<RichTextBlock>(root!),
                    text => text.IsTextSelectionEnabled);
                Assert.All(nested, expander =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(expander)));
                    expander.IsExpanded = true;
                });

                var selectableOutputs = FindExpandedLogical<RichTextBlock>(root!)
                    .Where(text => text.IsTextSelectionEnabled)
                    .Select(CollectText)
                    .ToArray();
                Assert.Contains("first output", selectableOutputs);
                Assert.Contains("second output", selectableOutputs);
                activity.IsExpanded = false;
            });
            await DrainReactorQueueAsync();

            await _ui.RunOnUIAsync(() =>
            {
                Assert.False(expansionState.IsExpanded(
                    activityRow.Key,
                    activityRow.Summary!,
                    timeline.ToolCallsCollapseVersion));
                var activity = Assert.Single(
                    FindLogical<Expander>(root!),
                    expander => AutomationProperties.GetAutomationId(expander) == "ChatToolActivity_tool_1");
                Assert.False(activity.IsExpanded);
                Assert.Contains("Collapsed", AutomationProperties.GetName(activity), StringComparison.Ordinal);
                Assert.DoesNotContain(
                    FindLogical<Expander>(root!),
                    expander => AutomationProperties.GetAutomationId(expander).StartsWith(
                        "ChatToolCall_",
                        StringComparison.Ordinal));
            });
        }
        finally
        {
            if (host is not null)
            {
                await _ui.RunOnUIAsync(() =>
                {
                    disposing = true;
                    if (root is not null && currentElement is not null)
                    {
                        var empty = Empty();
                        root = host.Reconciler.Reconcile(
                            currentElement,
                            empty,
                            root,
                            static () => { });
                        currentElement = empty;
                    }
                    host.Content = null;
                    host.Dispose();
                });
            }
        }
    }

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

    private static IEnumerable<T> FindExpandedLogical<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T self)
            yield return self;

        IEnumerable<DependencyObject> children = root switch
        {
            Panel panel => panel.Children,
            Border border when border.Child is not null => [border.Child],
            ScrollViewer scrollViewer when scrollViewer.Content is DependencyObject content => [content],
            Expander expander when expander.IsExpanded && expander.Content is DependencyObject content => [content],
            Microsoft.UI.Xaml.Controls.Expander => [],
            ContentControl contentControl when contentControl.Content is DependencyObject content => [content],
            _ => [],
        };

        foreach (var child in children)
        {
            foreach (var descendant in FindExpandedLogical<T>(child))
                yield return descendant;
        }
    }

    private async Task DrainReactorQueueAsync()
    {
        for (var pass = 0; pass < 3; pass++)
            await _ui.YieldToRenderAsync();
    }

}
