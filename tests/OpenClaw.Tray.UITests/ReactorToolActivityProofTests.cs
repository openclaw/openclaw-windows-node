using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Animation;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

/// <summary>Offscreen real-runtime proof for the production Reactor tool activity renderer.</summary>
[Collection(UICollection.Name)]
public sealed class ReactorToolActivityProofTests
{
    private const string DocumentGlyph = "\uE8A5";
    private const string ListGlyph = "\uE8FD";

    private readonly UIThreadFixture _ui;

    public ReactorToolActivityProofTests(UIThreadFixture ui) => _ui = ui;

    [Theory]
    [InlineData("Waiting for approval", true)]
    [InlineData("Retrying the model request", false)]
    [InlineData("Compacting context", false)]
    public async Task RunProgress_PreservesVisibleExceptionalStatusBesideElapsedTime(string label, bool waiting)
    {
        await _ui.RunOnUIAsync(() =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl();
            var element = Component<ReactorRunProgress, ReactorRunProgressProps>(new(new(
                DateTimeOffset.UtcNow.AddSeconds(-65), null, label, true, waiting)));
            var root = host.Reconciler.Mount(element, static () => { });
            host.Content = root;
            try
            {
                var text = Assert.Single(FindLogical<TextBlock>(root!),
                    block => AutomationProperties.GetAutomationId(block) == "ChatRunProgressText");
                Assert.Contains(label, text.Text);
                Assert.Contains("1m", text.Text);
            }
            finally
            {
                host.Reconciler.Reconcile(element, Empty(), root!, static () => { });
                host.Content = null;
            }
        });
    }

    [Fact]
    public async Task WorkMotion_UsesEnterOnlyTransitionAndRespectsReducedMotion()
    {
        await _ui.RunOnUIAsync(() =>
        {
            using var theme = new ChatThemeProofScope("Light");
            var work = Tool("t", "read", "output", ChatToolCallStatus.Success);
            var activity = new ChatToolActivityRow(
                "work", null, [work], ChatToolActivityPresentation.Summarize([work]), [work]);
            var timeline = BuildTimeline([work], 1);
            var animated = ToolCallCardRenderer.BuildActivityCore(
                timeline, activity, false, _ => { }, animateEntrance: true, reducedMotion: false);
            Assert.NotNull(animated.ElementTransition);
            Assert.Equal(Transition.Enter(Transition.Fade), animated.ElementTransition.Transition);
            Assert.Equal(Curve.Ease(200, Easing.EaseOut), animated.ElementTransition.Curve);
            var reduced = ToolCallCardRenderer.BuildActivityCore(
                timeline, activity, false, _ => { }, animateEntrance: true, reducedMotion: true);
            Assert.Null(reduced.ElementTransition);
            var history = ToolCallCardRenderer.BuildActivityCore(
                timeline, activity, false, _ => { }, animateEntrance: false, reducedMotion: false);
            Assert.Null(history.ElementTransition);
        });
    }

    [Fact]
    public async Task CompletedWork_ReusesNativeDisclosureAndToolDetailsWithoutHidingAnswer()
    {
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(() =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl();
            var entries = new ChatTimelineItem[]
            {
                new("commentary", ChatTimelineItemKind.Assistant, "Checking",
                    Presentation: new(Phase: "commentary", RunId: "r")),
                Tool("read", "read_file", "<literal output>", ChatToolCallStatus.Success),
                new("answer", ChatTimelineItemKind.Assistant, "Preserved answer",
                    Presentation: new(Phase: "final_answer", RunId: "r")),
            };
            var timeline = BuildTimeline(entries, 1) with { SessionId = "agent:a:dashboard:d" };
            var rows = ChatToolActivityPresentation.Project(entries, timeline.SessionId, 1);
            Assert.Equal("Preserved answer", rows[1].Entry!.Text);
            var work = rows[0];
            var element = ToolCallCardRenderer.BuildActivityCore(timeline, work, false, _ => { },
                entry => TextBlock(entry.Text));
            var root = host.Reconciler.Mount(element, static () => { });
            host.Content = root;
            try
            {
                var disclosure = Assert.Single(FindLogical<Expander>(root!));
                Assert.False(disclosure.IsExpanded);
                Assert.Contains("Worked", AutomationProperties.GetName(disclosure));
                Assert.Empty(FindLogical<RichTextBlock>(root!));
                var header = Assert.IsAssignableFrom<DependencyObject>(disclosure.Header);
                Assert.Contains(FindLogical<TextBlock>(header),
                    text => text.Text == ListGlyph);
                Assert.Contains(FindLogical<TextBlock>(header), text => text.Text == "Worked");
                var expanded = ToolCallCardRenderer.BuildActivityCore(timeline, work, true, _ => { },
                    entry => TextBlock(entry.Text));
                root = host.Reconciler.Reconcile(element, expanded, root!, static () => { });
                element = expanded;
                host.Content = root;
                Assert.Contains(disclosure, FindLogical<Expander>(root!));
                Assert.True(disclosure.IsExpanded);
                Assert.Contains(FindLogical<TextBlock>(Assert.IsAssignableFrom<DependencyObject>(disclosure.Header)),
                    text => text.Text == "Worked · 1 tool call");
                Assert.Contains(FindLogical<TextBlock>(root!), text => text.Text == "Checking");
                // The tool sits in its own collapsed group layer, exactly like the web Worked body.
                var group = Assert.Single(FindLogical<Expander>(root!), expander => !ReferenceEquals(expander, disclosure));
                Assert.False(group.IsExpanded);
                Assert.Contains("1 read", AutomationProperties.GetName(group));
                Assert.Empty(FindLogical<RichTextBlock>(root!));
                Assert.DoesNotContain(FindLogical<TextBlock>(root!), text => text.Text == "Preserved answer");
            }
            finally
            {
                host.Reconciler.Reconcile(element, Empty(), root!, static () => { });
                host.Content = null;
            }
        });
    }

    [Fact]
    public async Task ToolRow_ExposesTargetAndKeepsOpenOutputWhenInputArrivesAndRunFinishes()
    {
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(() =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl();
            var entry = Tool("stream", "read_file", "partial output", ChatToolCallStatus.InProgress)
                with { Text = string.Empty };
            var timeline = BuildTimeline([entry], generation: 1);
            var element = ToolCallCardRenderer.BuildStandalone(timeline, entry);
            var root = host.Reconciler.Mount(element, static () => { });
            Assert.NotNull(root);
            host.Content = root;
            try
            {
                var expander = Assert.Single(FindLogical<Expander>(root));
                Assert.False(expander.IsExpanded);
                expander.IsExpanded = true;
                var details = Assert.IsType<Border>(expander.Content);
                Assert.Equal(new Thickness(12, 8, 12, 8), details.Padding);
                Assert.Equal(4, Assert.IsType<StackPanel>(details.Child).Spacing);
                var output = Assert.Single(FindLogical<RichTextBlock>(root));
                Assert.Equal("partial output", CollectText(output));

                entry = entry with
                {
                    ToolArgs = new System.Text.Json.Nodes.JsonObject { ["filePath"] = "src\\main.cs" },
                    ToolOutput = "final <literal> output",
                    ToolResult = ChatToolCallStatus.Success,
                    ToolRunId = "observed-run",
                    ToolLegacyTurn = 2,
                };
                var next = ToolCallCardRenderer.BuildStandalone(timeline, entry);
                root = host.Reconciler.Reconcile(element, next, root, static () => { });
                Assert.NotNull(root);
                element = next;
                host.Content = root;

                Assert.Same(expander, Assert.Single(FindLogical<Expander>(root)));
                Assert.True(expander.IsExpanded);
                Assert.Equal("src\\main.cs", AutomationProperties.GetHelpText(expander));
                Assert.Contains("Done", AutomationProperties.GetName(expander), StringComparison.Ordinal);
                Assert.DoesNotContain(FindLogical<TextBlock>(root), text => text.Text == "Done");
                var header = Assert.IsAssignableFrom<DependencyObject>(expander.Header);
                Assert.Contains(FindLogical<TextBlock>(header), text => text.Text == "src\\main.cs");
                Assert.Contains(FindLogical<TextBlock>(header), text => text.Text == DocumentGlyph);
                Assert.Null(ToolTipService.GetToolTip(expander));
                Assert.Contains(output, FindLogical<RichTextBlock>(root));
                Assert.Equal("final <literal> output", CollectText(output));
                Assert.True(output.IsTextSelectionEnabled);

                var collapsed = ToolCallCardRenderer.BuildStandalone(
                    timeline with { ToolCallsCollapseVersion = 1 }, entry);
                root = host.Reconciler.Reconcile(element, collapsed, root, static () => { });
                Assert.NotNull(root);
                element = collapsed;
                host.Content = root;
                Assert.False(Assert.Single(FindLogical<Expander>(root)).IsExpanded);
            }
            finally
            {
                host.Reconciler.Reconcile(element, Empty(), root, static () => { });
                host.Content = null;
            }
        });
    }

    [Fact]
    public async Task RunningActivity_UsesSuppliedTitleCategoryIconAndSourceMetadata()
    {
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(() =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl();
            var first = Tool("first", "exec", "first output", ChatToolCallStatus.Success)
                with { ToolRunId = "run" };
            var current = Tool("current", "exec", "partial output", ChatToolCallStatus.InProgress)
                with
                {
                    ToolRunId = "run",
                    ToolInputDetails = new ChatToolInputDetails(
                        """{"command":"dotnet test","title":"Build identical reviewer input"}""",
                        "dotnet test",
                        "Build identical reviewer input"),
                };
            var entries = new[] { first, current };
            var activity = Assert.Single(
                ChatToolActivityPresentation.Project(entries, "session", 1),
                row => row.IsActivityGroup);
            var timestamp = new DateTimeOffset(2026, 1, 2, 17, 5, 0,
                TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 1, 2, 17, 5, 0)));
            var metadata = new ChatEntryMetadata(timestamp, null)
            {
                Presentation = new ChatMessagePresentation(SenderLabel: "RoboClaw"),
            };
            var timeline = BuildTimeline(entries, 1) with
            {
                EntryMetadata = new Dictionary<string, ChatEntryMetadata>
                {
                    [current.Id] = metadata,
                },
            };
            var element = ToolCallCardRenderer.BuildActivityCore(
                timeline, activity, false, _ => { });
            var root = host.Reconciler.Mount(element, static () => { });
            host.Content = root;
            try
            {
                var expander = Assert.Single(FindLogical<Expander>(root!));
                var header = Assert.IsAssignableFrom<DependencyObject>(expander.Header);
                var headerText = FindLogical<TextBlock>(header).Select(text => text.Text).ToArray();
                Assert.Contains("Build identical reviewer input", headerText);
                Assert.Contains("Running", headerText);
                Assert.Contains(ListGlyph, headerText);

                // Web tool rows carry no hover tooltip.
                Assert.Null(ToolTipService.GetToolTip(expander));
            }
            finally
            {
                host.Reconciler.Reconcile(element, Empty(), root!, static () => { });
                host.Content = null;
            }
        });
    }

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
        ChatThemeProofScope? themeScope = null;
        try
        {
            await _ui.RunOnUIAsync(() =>
            {
                themeScope = new ChatThemeProofScope("Light");
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
                    themeScope?.Dispose();
                });
            }
        }
    }

    private static ChatTimelinePresentationContext BuildTimeline(
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
