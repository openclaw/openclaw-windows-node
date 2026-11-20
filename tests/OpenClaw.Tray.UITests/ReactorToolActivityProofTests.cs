using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Animation;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

/// <summary>Offscreen real-runtime proof for the production Reactor tool activity renderer.</summary>
[Collection(UICollection.Name)]
public sealed class ReactorToolActivityProofTests
{
    private const string DocumentGlyph = "\uE8A5";
    private const string ListGlyph = "\uE8FD";
    private const string ChevronRightGlyph = "\uE76C";
    private const string ChevronDownGlyph = "\uE70D";

    private static int ReadElapsedSeconds(string text)
    {
        var separator = text.LastIndexOf('\u00b7');
        Assert.True(separator >= 0 && separator < text.Length - 1, $"Elapsed time is missing from '{text}'.");
        var totalSeconds = 0;
        foreach (var part in text[(separator + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var value = int.Parse(part.AsSpan(0, part.Length - 1), CultureInfo.InvariantCulture);
            totalSeconds += part[^1] switch
            {
                'h' => value * 3600,
                'm' => value * 60,
                's' => value,
                _ => throw new InvalidOperationException($"Unexpected elapsed-time unit in '{part}'."),
            };
        }
        return totalSeconds;
    }

    private static int WorkDividerCount(DependencyObject root) =>
        FindLogical<Border>(root).Count(border => border.Height == 1);

    // One header line, outside the rounded card whose corners would otherwise clip its ends.
    private static void AssertWorkDividerOutsideCard(DependencyObject root)
    {
        var divider = Assert.Single(FindLogical<Border>(root), border => border.Height == 1);
        Assert.Contains(divider, Assert.IsType<StackPanel>(root).Children);
    }

    private readonly UIThreadFixture _ui;

    public ReactorToolActivityProofTests(UIThreadFixture ui) => _ui = ui;

    [Theory]
    [InlineData(false, 43, true, "exec", "+31 more lines", false)]
    [InlineData(false, 13, true, "read_file", "+1 more line", false)]
    [InlineData(true, 14, true, "exec", "More output available", false)]
    [InlineData(false, 12, true, "exec", null, false)]
    [InlineData(false, 43, false, "exec", null, false)]
    [InlineData(true, 14, false, "exec", "Full output requires the saved result identity and a verified current account.", false)]
    [InlineData(false, 43, true, "exec", null, true)]
    public async Task ToolPreview_ShowsPassiveLineCountInsideCardAndKeepsTopPanelAction(
        bool truncated, int lines, bool canOpen, string tool, string? label, bool whitespace)
    {
        await _ui.RunOnUIAsync(() =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl();
            var entry = Tool("preview", tool,
                whitespace ? new string('\n', lines) :
                    string.Join("\n", Enumerable.Range(1, lines).Select(index => index == 1 ? "Notes" : $"line {index}")),
                ChatToolCallStatus.Success) with
            {
                ToolResultReference = new("result", truncated, SessionId: "saved", CallId: "call"),
            };
            ChatToolDetailRequest? opened = null;
            var reads = 0;
            var profile = new ChatToolDetailsProfileState(1, true);
            var context = BuildTimeline([entry], 1) with
            {
                OnOpenToolDetails = canOpen ? request => opened = request : null,
                ToolDetailsProfileState = profile,
                ReadToolDetailsProfileState = () => profile,
                ToolOutputConnectionEpoch = 1,
                LoadToolOutputAsync = (_, _) =>
                {
                    reads++;
                    return Task.FromResult(new ChatToolOutputResult(ChatToolOutputStatus.Loaded, "full"));
                },
            };
            var element = ToolCallCardRenderer.BuildStandalone(context, entry);
            var root = host.Reconciler.Mount(element, static () => { });
            host.Content = root;
            try
            {
                Assert.Single(FindLogical<Expander>(root!)).IsExpanded = true;
                var outputs = FindLogical<RichTextBlock>(root!).ToArray();
                if (whitespace)
                {
                    Assert.Empty(outputs);
                    Assert.DoesNotContain(FindLogical<TextBlock>(root!), text =>
                        AutomationProperties.GetAutomationId(text) == "ChatToolMoreLines_preview");
                    return;
                }
                var output = Assert.Single(outputs);
                Assert.Contains("line 12", CollectText(output));
                if (canOpen)
                    Assert.DoesNotContain("line 13", CollectText(output));
                else
                    Assert.Contains($"line {lines}", CollectText(output));
                var card = Assert.Single(FindLogical<Border>(root!), border =>
                    AutomationProperties.GetAutomationId(border) == "ChatToolDetailsCard_preview");
                var markers = FindLogical<TextBlock>(card).Where(text =>
                    AutomationProperties.GetAutomationId(text) == "ChatToolMoreLines_preview").ToArray();
                if (label is null)
                    Assert.Empty(markers);
                else
                {
                    var more = Assert.Single(markers);
                    Assert.Equal(label, more.Text);
                    var sections = Assert.Single(FindLogical<StackPanel>(card),
                        stack => stack.Children.Contains(more));
                    var index = sections.Children.IndexOf(more);
                    Assert.True(index > 0);
                    Assert.Same(output, Assert.Single(FindLogical<RichTextBlock>(sections.Children[index - 1])));
                    Assert.DoesNotContain(FindLogical<Button>(root!), button =>
                        AutomationProperties.GetName(button) == label);
                }
                Assert.Null(opened);
                var actions = FindLogical<Button>(root!).Where(button =>
                    AutomationProperties.GetAutomationId(button) == "ChatToolDetails_preview").ToArray();
                if (canOpen)
                {
                    var action = Assert.Single(actions);
                    var invoke = Assert.IsAssignableFrom<Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider>(
                        new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(action)
                            .GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke));
                    invoke.Invoke();
                    Assert.Equal(ChatToolDetailRequest.Capture(
                        context.SessionId!, context.TimelineGeneration, context.HistoryRevision, entry), opened);
                }
                else
                    Assert.DoesNotContain(actions, action => action.Visibility == Visibility.Visible);
                Assert.Equal(0, reads);
            }
            finally
            {
                host.Reconciler.Reconcile(element, Empty(), root!, static () => { });
                host.Content = null;
            }
        });
    }

    [Fact]
    public async Task ToolPanel_HiddenReaderDefersRevalidationAndCancelsPendingRead()
    {
        await _ui.RunOnUIAsync(async () =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl();
            var entry = Tool("active-reader", "exec", "preview", ChatToolCallStatus.Success) with
            {
                ToolResultReference = new("result", true, SessionId: "saved", CallId: "call"),
            };
            var profile = new ChatToolDetailsProfileState(1, true);
            var reads = 0;
            CancellationToken firstRead = default;
            var delayed = new TaskCompletionSource<ChatToolOutputResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<ChatToolOutputResult> Load(ChatToolOutputRequest request, CancellationToken token)
            {
                reads++;
                if (reads != 1)
                    return Task.FromResult(new ChatToolOutputResult(ChatToolOutputStatus.Loaded, "full output"));
                firstRead = token;
                return delayed.Task;
            }
            Element Render(bool active) => ToolCallCardRenderer.BuildPanelDetails(BuildTimeline([entry], 1) with
            {
                ToolDetailsProfileState = profile,
                ReadToolDetailsProfileState = () => profile,
                ToolOutputConnectionEpoch = 1,
                LoadToolOutputAsync = Load,
            }, entry, active);
            var element = Render(false);
            var root = host.Reconciler.Mount(element, static () => { });
            host.Content = root;
            void Update(bool active)
            {
                var next = Render(active);
                root = host.Reconciler.Reconcile(element, next, root!, static () => { });
                element = next;
                host.Content = root;
            }
            try
            {
                Assert.Equal(0, reads);
                Update(true);
                await WaitForRenderedConditionAsync(() => reads == 1, "visible Details starts the saved-result read");
                Update(false);
                Assert.True(firstRead.IsCancellationRequested);
                profile = new(2, true);
                Update(false);
                delayed.SetResult(new(ChatToolOutputStatus.Loaded, "retired output"));
                await _ui.YieldToRenderAsync();
                Assert.Equal(1, reads);
                Assert.DoesNotContain(FindLogical<RichTextBlock>(root!), text => CollectText(text).Contains("retired output"));
                Update(true);
                await WaitForRenderedConditionAsync(() => reads == 2, "reopened Details reads under the new profile revision");
            }
            finally
            {
                host.Reconciler.Reconcile(element, Empty(), root!, static () => { });
                host.Content = null;
            }
        });
    }

    [Fact]
    public async Task DisclosureCloseTimer_RunsOnUiThreadAndCanBeCancelled()
    {
        await _ui.RunOnUIAsync(async () =>
        {
            var timers = new ChatDisclosureCloseTimerSet();
            var cancelledCallbackRan = false;
            var callbackRan = false;
            var callbackRanOnUiThread = false;
            var delay = TimeSpan.FromMilliseconds(ChatMotion.DisclosureCloseHoldMilliseconds);

            try
            {
                timers.Schedule("cancelled", delay, () => cancelledCallbackRan = true);
                timers.Cancel("cancelled");
                timers.Schedule("active", delay, () =>
                {
                    callbackRanOnUiThread = _ui.Dispatcher.HasThreadAccess;
                    callbackRan = true;
                });
                await WaitForRenderedConditionAsync(() => callbackRan, "the disclosure close timer fires");

                Assert.True(callbackRanOnUiThread);
                Assert.False(cancelledCallbackRan);
            }
            finally
            {
                timers.CancelAll();
            }
        });
    }

    [Fact]
    public async Task ToolActivityIdentityChange_ClearsPendingDisclosureClose()
    {
        await _ui.ResetContainerAsync();
        using var motion = new ChatMotionScope(enabled: true, reducedMotion: false);
        await _ui.RunOnUIAsync(async () =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl();
            var timeline = BuildTimeline(
                [
                    Tool("tool-1", "read_file", "first output", ChatToolCallStatus.Success),
                    Tool("tool-2", "read_file", "second output", ChatToolCallStatus.Success),
                ],
                generation: 1) with { SessionId = "agent:first" };
            var activity = Assert.Single(ChatToolActivityPresentation.Project(
                timeline.Entries, timeline.SessionId, timeline.TimelineGeneration), row => row.IsActivityGroup);
            var expansion = new ChatToolActivityExpansionState();
            var disposing = false;
            UIElement? root = null;
            Element? element = null;
            Action? reconcile = null;
            Element CreateElement() => Component<ToolActivityCard, ToolActivityCardProps>(
                new(timeline, activity, expansion, entry => TextBlock(entry.Text)))
                .WithKey("reused-activity");
            Action requestRender = () => _ui.Dispatcher.TryEnqueue(() => reconcile?.Invoke());
            reconcile = () =>
            {
                if (disposing)
                    return;
                var next = CreateElement();
                root = host.Reconciler.Reconcile(element!, next, root!, requestRender);
                element = next;
                host.Content = root;
            };
            element = CreateElement();
            root = host.Reconciler.Mount(element, requestRender);
            host.Content = root;
            try
            {
                Assert.Single(FindLogical<Expander>(root!)).IsExpanded = true;
                await DrainReactorQueueAsync();

                var activityDisclosure = Assert.Single(FindLogical<Expander>(root!),
                    expander => AutomationProperties.GetAutomationId(expander) == "ChatToolActivity_tool_1");
                Assert.Contains(FindLogical<Expander>(root!), expander =>
                    AutomationProperties.GetAutomationId(expander).StartsWith("ChatToolCall_", StringComparison.Ordinal));

                activityDisclosure.IsExpanded = false;
                reconcile();
                Assert.Contains(FindLogical<Expander>(root!), expander =>
                    AutomationProperties.GetAutomationId(expander).StartsWith("ChatToolCall_", StringComparison.Ordinal));

                timeline = timeline with { SessionId = "agent:second", TimelineGeneration = 2 };
                activity = Assert.Single(ChatToolActivityPresentation.Project(
                    timeline.Entries, timeline.SessionId, timeline.TimelineGeneration), row => row.IsActivityGroup);
                reconcile();

                Assert.DoesNotContain(FindLogical<Expander>(root!), expander =>
                    AutomationProperties.GetAutomationId(expander).StartsWith("ChatToolCall_", StringComparison.Ordinal));
            }
            finally
            {
                disposing = true;
                host.Reconciler.Reconcile(element!, Empty(), root!, static () => { });
                host.Content = null;
            }
        });
    }

    [Fact]
    public async Task ToolResultCardIdentityChange_ResetsDisclosureStateWhenEntryIsReused()
    {
        await _ui.ResetContainerAsync();
        using var motion = new ChatMotionScope(enabled: true, reducedMotion: false);
        await _ui.RunOnUIAsync(async () =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl();
            var entry = Tool("reused-tool", "read_file", "first output", ChatToolCallStatus.Success);
            var timeline = BuildTimeline([entry], generation: 1) with
            {
                SessionId = "agent:first",
                ToolExpansionState = new ChatToolActivityExpansionState(),
            };
            var disposing = false;
            UIElement? root = null;
            Element? element = null;
            Action? reconcile = null;
            Element CreateElement() => ToolCallCardRenderer.BuildStandalone(timeline, entry);
            Action requestRender = () => _ui.Dispatcher.TryEnqueue(() => reconcile?.Invoke());
            reconcile = () =>
            {
                if (disposing)
                    return;
                var next = CreateElement();
                root = host.Reconciler.Reconcile(element!, next, root!, requestRender);
                element = next;
                host.Content = root;
            };
            element = CreateElement();
            root = host.Reconciler.Mount(element, requestRender);
            host.Content = root;
            bool OutputDetailsVisible()
            {
                var detailsHost = FindLogical<ChatMotionHost>(root!).FirstOrDefault(motionHost =>
                    FindLogical<RichTextBlock>(motionHost).Any(text => CollectText(text) == "first output"));
                return detailsHost?.Visibility == Visibility.Visible;
            }
            try
            {
                var disclosure = Assert.Single(FindLogical<Expander>(root!),
                    expander => AutomationProperties.GetAutomationId(expander) == "ChatToolCall_reused_tool");
                disclosure.IsExpanded = true;
                await WaitForRenderedConditionAsync(OutputDetailsVisible, "standalone tool details open");

                disclosure = Assert.Single(FindLogical<Expander>(root!),
                    expander => AutomationProperties.GetAutomationId(expander) == "ChatToolCall_reused_tool");
                disclosure.IsExpanded = false;
                reconcile();
                Assert.True(OutputDetailsVisible());

                timeline = timeline with { SessionId = "agent:second", TimelineGeneration = 2 };
                reconcile();
                timeline = timeline with { SessionId = "agent:first", TimelineGeneration = 1 };
                reconcile();

                Assert.False(OutputDetailsVisible());
            }
            finally
            {
                disposing = true;
                host.Reconciler.Reconcile(element!, Empty(), root!, static () => { });
                host.Content = null;
            }
        });
    }

    [Fact]
    public async Task ToolResultCardClose_HidesImmediatelyWhenReducedMotionIsOverriddenOn()
    {
        await _ui.ResetContainerAsync();
        using var motion = new ChatMotionScope(enabled: true, reducedMotion: true);
        await _ui.RunOnUIAsync(async () =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl();
            var entry = Tool("reduced-motion-tool", "read_file", "tool output", ChatToolCallStatus.Success);
            var timeline = BuildTimeline([entry], generation: 1) with
            {
                ToolExpansionState = new ChatToolActivityExpansionState(),
            };
            var disposing = false;
            UIElement? root = null;
            Element? element = null;
            Action? reconcile = null;
            Element CreateElement() => ToolCallCardRenderer.BuildStandalone(timeline, entry);
            Action requestRender = () => _ui.Dispatcher.TryEnqueue(() => reconcile?.Invoke());
            reconcile = () =>
            {
                if (disposing)
                    return;
                var next = CreateElement();
                root = host.Reconciler.Reconcile(element!, next, root!, requestRender);
                element = next;
                host.Content = root;
            };
            element = CreateElement();
            root = host.Reconciler.Mount(element, requestRender);
            host.Content = root;
            bool OutputDetailsVisible() =>
                FindLogical<ChatMotionHost>(root!).FirstOrDefault(motionHost =>
                    FindLogical<RichTextBlock>(motionHost).Any(text => CollectText(text) == "tool output"))
                    ?.Visibility == Visibility.Visible;
            try
            {
                var disclosure = Assert.Single(FindLogical<Expander>(root!),
                    expander => AutomationProperties.GetAutomationId(expander) == "ChatToolCall_reduced_motion_tool");
                disclosure.IsExpanded = true;
                await WaitForRenderedConditionAsync(OutputDetailsVisible, "tool details open");

                disclosure = Assert.Single(FindLogical<Expander>(root!),
                    expander => AutomationProperties.GetAutomationId(expander) == "ChatToolCall_reduced_motion_tool");
                disclosure.IsExpanded = false;
                reconcile();

                Assert.False(OutputDetailsVisible());
            }
            finally
            {
                disposing = true;
                host.Reconciler.Reconcile(element!, Empty(), root!, static () => { });
                host.Content = null;
            }
        });
    }

    [Fact]
    public async Task ToolActivityDisclosure_OpensAndClosesWithoutMotionWhenReducedMotionIsOverriddenOn()
    {
        await _ui.ResetContainerAsync();
        using var motion = new ChatMotionScope(enabled: true, reducedMotion: true);
        await _ui.RunOnUIAsync(async () =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl();
            var timeline = BuildTimeline(
                [
                    Tool("tool-1", "read_file", "first output", ChatToolCallStatus.Success),
                    Tool("tool-2", "edit_file", "second output", ChatToolCallStatus.Success),
                ],
                generation: 1) with { SessionId = "agent:activity" };
            var activity = Assert.Single(ChatToolActivityPresentation.Project(
                timeline.Entries, timeline.SessionId, timeline.TimelineGeneration), row => row.IsActivityGroup);
            var expansion = new ChatToolActivityExpansionState();
            var disposing = false;
            UIElement? root = null;
            Element? element = null;
            Action? reconcile = null;
            Element CreateElement() => Component<ToolActivityCard, ToolActivityCardProps>(
                new(timeline, activity, expansion, entry => TextBlock(entry.Text)))
                .WithKey("reduced-motion-activity");
            Action requestRender = () => _ui.Dispatcher.TryEnqueue(() => reconcile?.Invoke());
            reconcile = () =>
            {
                if (disposing)
                    return;
                var next = CreateElement();
                root = host.Reconciler.Reconcile(element!, next, root!, requestRender);
                element = next;
                host.Content = root;
            };
            element = CreateElement();
            root = host.Reconciler.Mount(element, requestRender);
            host.Content = root;
            try
            {
                Assert.Single(FindLogical<Expander>(root!)).IsExpanded = true;
                reconcile();
                await WaitForRenderedConditionAsync(() => FindLogical<Expander>(root!).Any(expander =>
                    AutomationProperties.GetAutomationId(expander).StartsWith("ChatToolCall_", StringComparison.Ordinal)),
                    "nested tool rows appear");
                Assert.DoesNotContain(FindLogical<ChatMotionHost>(root!), motionHost => motionHost.Height == 0);

                var activityDisclosure = Assert.Single(FindLogical<Expander>(root!),
                    expander => AutomationProperties.GetAutomationId(expander) == "ChatToolActivity_tool_1");
                activityDisclosure.IsExpanded = false;
                reconcile();

                Assert.DoesNotContain(FindLogical<Expander>(root!), expander =>
                    AutomationProperties.GetAutomationId(expander).StartsWith("ChatToolCall_", StringComparison.Ordinal));
            }
            finally
            {
                disposing = true;
                host.Reconciler.Reconcile(element!, Empty(), root!, static () => { });
                host.Content = null;
            }
        });
    }

    [Fact]
    public async Task ToolRow_RecyclingBetweenActivityAndStandaloneDoesNotReuseIncompatibleProps()
    {
        await _ui.RunOnUIAsync(() =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl();
            var first = Tool("first", "read", "alpha", ChatToolCallStatus.Success);
            var second = Tool("second", "edit", "saved", ChatToolCallStatus.Success);
            var timeline = BuildTimeline([first, second], 1);
            var activity = Assert.Single(ChatToolActivityPresentation.Project(
                timeline.Entries, timeline.SessionId, timeline.TimelineGeneration), row => row.IsActivityGroup);
            var expansion = new ChatToolActivityExpansionState();
            Element Group() => Grid([GridSize.Star()], [GridSize.Auto],
                ToolCallCardRenderer.BuildActivity(timeline, activity, expansion));
            Element Single() => Grid([GridSize.Star()], [GridSize.Auto],
                ToolCallCardRenderer.BuildStandalone(timeline, first));
            var element = Group();
            var root = host.Reconciler.Mount(element, static () => { });
            host.Content = root;
            try
            {
                foreach (var next in new[] { Single(), Group(), Single() })
                {
                    root = host.Reconciler.Reconcile(element, next, root!, static () => { });
                    element = next;
                    host.Content = root;
                    Assert.NotEmpty(FindLogical<Expander>(root!));
                }
            }
            finally
            {
                host.Reconciler.Reconcile(element, Empty(), root!, static () => { });
                host.Content = null;
            }
        });
    }

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
                var status = Assert.Single(FindLogical<TextBlock>(root!),
                    block => AutomationProperties.GetAutomationId(block) == "ChatRunProgressStatus");
                var text = Assert.Single(FindLogical<TextBlock>(root!),
                    block => AutomationProperties.GetAutomationId(block) == "ChatRunProgressText");
                Assert.Contains(label, status.Text);
                Assert.Contains("1m", text.Text);
                Assert.IsNotType<LinearGradientBrush>(text.Foreground);
            }
            finally
            {
                host.Reconciler.Reconcile(element, Empty(), root!, static () => { });
                host.Content = null;
            }
        });
    }

    [Fact]
    public async Task RunProgress_OutputTokensPrecedeATickingClockWithoutTextShimmer()
    {
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(async () =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl();
            var startedAt = DateTimeOffset.UtcNow.AddSeconds(-65);
            host.Mount(_ => Component<ReactorRunProgress, ReactorRunProgressProps>(new(new(
                startedAt, null, "Working", true, false, OutputTokens: 12345))));
            _ui.Container.Children.Add(host);
            try
            {
                _ui.Container.UpdateLayout();
                await WaitForRenderedConditionAsync(() => FindLogical<TextBlock>(host).Any(block =>
                    AutomationProperties.GetAutomationId(block) == "ChatRunProgressText"), "the run progress mounts");
                var text = Assert.Single(FindLogical<TextBlock>(host),
                    block => AutomationProperties.GetAutomationId(block) == "ChatRunProgressText");
                var row = Assert.Single(FindLogical<Grid>(host),
                    grid => AutomationProperties.GetAutomationId(grid) == "ChatRunProgress");
                Assert.Equal(1, row.Opacity);
                Assert.StartsWith(" \u00b7 12,345 output tokens \u00b7 ", text.Text);
                Assert.Contains("1m", text.Text);
                Assert.IsNotType<LinearGradientBrush>(text.Foreground);
                var initialElapsedSeconds = ReadElapsedSeconds(text.Text);
                await WaitForRenderedConditionAsync(
                    () => FindLogical<TextBlock>(host).Any(block =>
                        AutomationProperties.GetAutomationId(block) == "ChatRunProgressText" &&
                        ReadElapsedSeconds(block.Text) > initialElapsedSeconds),
                    "the running clock advances",
                    () => string.Join(" | ", FindLogical<TextBlock>(host)
                        .Where(block => AutomationProperties.GetAutomationId(block) == "ChatRunProgressText")
                        .Select(block => block.Text)));
                var updatedText = Assert.Single(FindLogical<TextBlock>(host),
                    block => AutomationProperties.GetAutomationId(block) == "ChatRunProgressText");
                var updatedRow = Assert.Single(FindLogical<Grid>(host),
                    grid => AutomationProperties.GetAutomationId(grid) == "ChatRunProgress");
                Assert.Same(text, updatedText);
                Assert.Same(row, updatedRow);
                Assert.Equal(1, updatedRow.Opacity);
                Assert.StartsWith(" \u00b7 12,345 output tokens \u00b7 ", updatedText.Text);
                Assert.True(ReadElapsedSeconds(updatedText.Text) > initialElapsedSeconds);
                Assert.IsNotType<LinearGradientBrush>(updatedText.Foreground);
            }
            finally
            {
                _ui.Container.Children.Clear();
            }
        });
    }

    [Fact]
    public async Task TextShimmer_FollowsTheThemeOfTheTreeItJoinsAndLaterThemeChanges()
    {
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(async () =>
        {
            using var theme = new ChatThemeProofScope("Light");
            var text = new TextBlock { Text = "Running" };
            ChatVisuals.TextShimmer(text, true);
            if (!new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled ||
                new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
            {
                Assert.IsNotType<Microsoft.UI.Xaml.Media.LinearGradientBrush>(text.Foreground);
                return;
            }

            // Reactor configures the label before it joins the tree, when it still reports the
            // application theme. Join a tree that renders the opposite theme, then switch it back.
            var applicationIsDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
            var stops = Assert.IsType<Microsoft.UI.Xaml.Media.LinearGradientBrush>(text.Foreground).GradientStops;
            var reference = (TextBlock)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                """<TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Foreground="{ThemeResource TextFillColorPrimaryBrush}" Width="0" Height="0" />""");
            var secondary = (TextBlock)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                """<TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Foreground="{ThemeResource TextFillColorSecondaryBrush}" Width="0" Height="0" />""");
            void AssertSweepColors(bool dark)
            {
                var primaryInk = Assert.IsType<SolidColorBrush>(reference.Foreground).Color;
                var softerInk = Assert.IsType<SolidColorBrush>(secondary.Foreground).Color;
                Assert.All(stops.Where((_, index) => index != 2), stop => Assert.Equal(softerInk, stop.Color));
                var peak = stops[2].Color;
                Assert.Equal(primaryInk, peak);
                Assert.Equal((softerInk.R, softerInk.G, softerInk.B), (peak.R, peak.G, peak.B));
                Assert.True(peak.A - softerInk.A is >= 40 and <= 100,
                    $"The flare must remain visible: base {softerInk}, flare {peak}, theme {text.ActualTheme}.");
                foreach (var stop in stops)
                    Assert.True(ShimmerContrast(stop.Color, dark) >= 4.5,
                        $"The full running label must remain readable: {stop.Color} in {(dark ? "Dark" : "Light")}.");
            }

            var panel = new StackPanel();
            panel.Children.Add(text);
            panel.Children.Add(reference);
            panel.Children.Add(secondary);
            var host = new Border { Child = panel, RequestedTheme = applicationIsDark ? ElementTheme.Light : ElementTheme.Dark };
            try
            {
                _ui.Container.Children.Add(host);
                await _ui.YieldToRenderAsync();
                AssertSweepColors(!applicationIsDark);

                host.RequestedTheme = applicationIsDark ? ElementTheme.Dark : ElementTheme.Light;
                await _ui.YieldToRenderAsync();
                AssertSweepColors(applicationIsDark);

                ChatVisuals.TextShimmer(text, false);
                Assert.IsNotType<Microsoft.UI.Xaml.Media.LinearGradientBrush>(text.Foreground);
            }
            finally
            {
                ChatVisuals.TextShimmer(text, false);
                _ui.Container.Children.Clear();
            }
        });
    }

    [Fact]
    public async Task TextShimmer_KeepsTheWholeLabelPaintedThroughoutItsSweep()
    {
        await _ui.ResetContainerAsync();
        foreach (var themeName in new[] { "Light", "Dark" })
        {
            await _ui.RunOnUIAsync(async () =>
            {
                using var theme = new ChatThemeProofScope(themeName);
                var text = (TextBlock)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                    """<TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Text="Inspect the complete sentence while this operation is running." FontSize="13" HorizontalAlignment="Left" Foreground="{ThemeResource ChatSecondaryTextBrush}" />""");
                var primary = (TextBlock)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                    """<TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Foreground="{ThemeResource ChatTextBrush}" Width="0" Height="0" />""");
                var content = new Grid();
                content.Children.Add(text);
                content.Children.Add(primary);
                var host = new Border
                {
                    Child = content, RequestedTheme = theme.ElementTheme, Padding = new Thickness(12),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                };
                _ui.Container.Children.Add(host);
                try
                {
                    _ui.Container.UpdateLayout();
                    await _ui.YieldToRenderAsync();
                    // Use the same native gradient rasterizer without a highlight or motion.
                    // Solid-color text can use different glyph antialiasing from gradient text.
                    var resting = Assert.IsType<SolidColorBrush>(text.Foreground);
                    Assert.True(Assert.IsType<SolidColorBrush>(primary.Foreground).Color.A > resting.Color.A);
                    var reference = new LinearGradientBrush();
                    reference.GradientStops.Add(new GradientStop { Color = resting.Color, Offset = 0 });
                    reference.GradientStops.Add(new GradientStop { Color = resting.Color, Offset = 1 });
                    text.Foreground = reference;
                    var bitmap = new RenderTargetBitmap();
                    await bitmap.RenderAsync(text);
                    var baseline = (await bitmap.GetPixelsAsync()).ToArray();
                    Assert.Contains(Enumerable.Range(0, baseline.Length / 4), pixel => baseline[pixel * 4 + 3] >= 64);
                    ChatVisuals.TextShimmer(text, true);
                    if (!new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled ||
                        new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
                    {
                        Assert.Same(reference, text.Foreground);
                        return;
                    }
                    var brush = Assert.IsType<LinearGradientBrush>(text.Foreground);
                    foreach (var stop in brush.GradientStops)
                        Assert.True(ShimmerContrast(stop.Color, themeName == "Dark") >= 4.5);
                    Assert.Equal(GradientSpreadMethod.Pad, brush.SpreadMethod);
                    Assert.Equal(0, brush.GradientStops[0].Offset);
                    Assert.Equal(1, brush.GradientStops[^1].Offset);
                    double? previousFlareX = null;
                    foreach (var phase in new[] { 0.2, 0.5, 0.9 })
                    {
                        await WaitForRenderedConditionAsync(() => brush.GradientStops[2].Offset >= phase, "the running-label shine crosses the label");
                        await bitmap.RenderAsync(text);
                        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
                        Assert.Equal(baseline.Length, pixels.Length);
                        var weight = 0.0;
                        var weightedX = 0.0;
                        for (var offset = 3; offset < baseline.Length; offset += 4)
                            if (baseline[offset] >= 64)
                            {
                                Assert.True(pixels[offset] >= baseline[offset] - 8,
                                    $"{themeName}, phase {phase}: the shine erased part of the label at pixel {(offset - 3) / 4}: {baseline[offset]} -> {pixels[offset]}; theme {text.ActualTheme}, base {brush.GradientStops[0].Color}.");
                                var baseAlpha = baseline[offset] * brush.GradientStops[0].Color.A / (double)resting.Color.A;
                                var flare = pixels[offset] - baseAlpha;
                                if (flare >= 3)
                                {
                                    weight += flare;
                                    weightedX += flare * (((offset - 3) / 4) % bitmap.PixelWidth);
                                }
                            }
                        Assert.True(weight > 0, $"{themeName}: no visible flare at phase {phase}.");
                        var flareX = weightedX / weight;
                        if (previousFlareX is { } previousX)
                            Assert.True(flareX > previousX + bitmap.PixelWidth * 0.1,
                                $"{themeName}: the flare did not move across the label ({previousX} -> {flareX}).");
                        previousFlareX = flareX;
                        await VisualTestCapture.CaptureAsync(host, $"RunningFlare-{themeName}-{phase:0.0}");
                    }

                }
                finally
                {
                    ChatVisuals.TextShimmer(text, false);
                    _ui.Container.Children.Clear();
                }
            });
        }
    }

    private static double ShimmerContrast(global::Windows.UI.Color ink, bool dark)
    {
        var background = dark ? 32.0 : 255.0;
        var alpha = ink.A / 255.0;
        double Linear(double value)
        {
            var channel = value / 255.0;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Linear(alpha * ink.R + (1 - alpha) * background)
            + 0.7152 * Linear(alpha * ink.G + (1 - alpha) * background)
            + 0.0722 * Linear(alpha * ink.B + (1 - alpha) * background);
        var backdrop = Linear(background);
        return (Math.Max(luminance, backdrop) + 0.05) / (Math.Min(luminance, backdrop) + 0.05);
    }

    [Fact]
    public async Task RunningTool_UnmountStopsShimmerBeforeItsTextControlCanBeReused()
    {
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(async () =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl();
            var entry = Tool("running", "read", "partial output", ChatToolCallStatus.InProgress);
            var element = ToolCallCardRenderer.BuildStandalone(BuildTimeline([entry], 1), entry);
            var root = host.Reconciler.Mount(element, static () => { });
            host.Content = root;
            await DrainReactorQueueAsync();
            var labels = FindLogical<TextBlock>(root!).ToArray();
            try
            {
                if (new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled &&
                    !new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
                    Assert.Contains(labels, label => label.Foreground is Microsoft.UI.Xaml.Media.LinearGradientBrush);
            }
            finally
            {
                host.Reconciler.Reconcile(element, Empty(), root!, static () => { });
                host.Content = null;
            }
            Assert.All(labels, label =>
                Assert.IsNotType<Microsoft.UI.Xaml.Media.LinearGradientBrush>(label.Foreground));
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
    public async Task ChatMotion_UnmountRestoresNeutralStateBeforeAControlCanBeReused()
    {
        await _ui.ResetContainerAsync();
        using var motion = new ChatMotionScope(enabled: true);
        await _ui.RunOnUIAsync(async () =>
        {
            using var host = new ReactorHostControl();
            var element = ChatMotion.Host(TextBlock("motion"))
                .EnterOnMount(8, 180, fade: true)
                .DeferredRevealTransitions(180)
                .Set(border => border.Scale = new System.Numerics.Vector3(.9f, .9f, 1));
            var root = host.Reconciler.Mount(element, static () => { });
            host.Content = root;
            await DrainReactorQueueAsync();
            // Motion lives on a dedicated host type that Reactor's layout-control pool never recycles.
            var border = Assert.IsType<ChatMotionHost>(root);
            ChatMotion.RotateDisclosure(border, "\uE76C");
            ChatMotion.RotateDisclosure(border, "\uE70D");

            host.Reconciler.Reconcile(element, Empty(), root!, static () => { });
            host.Content = null;

            // Pooled controls must not carry a rise, fade, turn, scale or implicit transition to another row.
            Assert.Equal(1, border.Opacity);
            Assert.Equal(System.Numerics.Vector3.Zero, border.Translation);
            Assert.Equal(System.Numerics.Vector3.One, border.Scale);
            Assert.Equal(0, border.Rotation);
            Assert.Null(border.OpacityTransition);
            Assert.Null(border.ScaleTransition);
            Assert.Null(border.TranslationTransition);
            Assert.Null(border.RotationTransition);
        });
    }

    [Fact]
    public void TranscriptReflow_OnlyMovesRowsThatWereAlreadyRealized()
    {
        (double Now, double? Was)[] rows = [(100, 160), (-20, 40), (240, null), (-90, null)];
        Assert.Equal(new float?[] { 60, 60, null, null }, ChatTranscriptReflow.PlanOffsets(rows));
    }

    [Fact]
    public async Task TranscriptReflow_ReaderInputStopsAnInFlightGlide()
    {
        await _ui.ResetContainerAsync();
        using var motion = new ChatMotionScope(enabled: true);
        await _ui.RunOnUIAsync(async () =>
        {
            var panel = new StackPanel();
            var row = new ChatMotionHost { Content = new Border { Height = 40 } };
            panel.Children.Add(new Border { Child = row });
            var root = new Grid { Height = 400 };
            root.Children.Add(panel);
            _ui.Container.Children.Add(root);
            var reflow = new ChatTranscriptReflow();
            try
            {
                await WaitForRenderedConditionAsync(() => row.IsLoaded, "the row loads");
                root.UpdateLayout();
                reflow.Attach(root);
                reflow.Track(row, "row");
                ChatTranscriptReflow.CaptureAll();
                panel.Children.Insert(0, new Border { Height = 100 });
                root.UpdateLayout();

                await WaitForRenderedConditionAsync(() => Math.Abs(row.Translation.Y) > 10, "the reader reflow starts");
                reflow.CancelForReaderInput();
                Assert.Equal(System.Numerics.Vector3.Zero, row.Translation);
            }
            finally
            {
                reflow.Detach();
                _ui.Container.Children.Remove(root);
            }
        });
    }

    [Fact]
    public async Task TranscriptReflow_ExpiredCaptureDoesNotResetAnInFlightGlide()
    {
        await _ui.ResetContainerAsync();
        using var motion = new ChatMotionScope(enabled: true);
        await _ui.RunOnUIAsync(async () =>
        {
            long now = 0;
            var panel = new StackPanel();
            var row = new ChatMotionHost { Content = new Border { Height = 40 } };
            panel.Children.Add(new Border { Child = row });
            var root = new Grid { Height = 400 };
            root.Children.Add(panel);
            _ui.Container.Children.Add(root);
            var reflow = new ChatTranscriptReflow(() => now);
            try
            {
                await WaitForRenderedConditionAsync(() => row.IsLoaded, "the row loads");
                root.UpdateLayout();
                reflow.Attach(root);
                reflow.Track(row, "row");
                ChatTranscriptReflow.CaptureAll();
                panel.Children.Insert(0, new Border { Height = 100 });
                root.UpdateLayout();

                await WaitForRenderedConditionAsync(() => Math.Abs(row.Translation.Y) > 10, "the reader reflow starts");
                now = 601;
                ChatTranscriptReflow.CaptureAll();
                Assert.NotEqual(System.Numerics.Vector3.Zero, row.Translation);
            }
            finally
            {
                reflow.Detach();
                _ui.Container.Children.Remove(root);
            }
        });
    }

    [Fact]
    public async Task TranscriptReflow_DisablingAnimationsStopsAnInFlightGlide()
    {
        await _ui.ResetContainerAsync();
        using var motion = new ChatMotionScope(enabled: true);
        await _ui.RunOnUIAsync(async () =>
        {
            var panel = new StackPanel();
            var row = new ChatMotionHost { Content = new Border { Height = 40 } };
            panel.Children.Add(new Border { Child = row });
            var root = new Grid { Height = 400 };
            root.Children.Add(panel);
            _ui.Container.Children.Add(root);
            var reflow = new ChatTranscriptReflow();
            try
            {
                await WaitForRenderedConditionAsync(() => row.IsLoaded, "the row loads");
                root.UpdateLayout();
                reflow.Attach(root);
                reflow.Track(row, "row");
                ChatTranscriptReflow.CaptureAll();
                panel.Children.Insert(0, new Border { Height = 100 });
                root.UpdateLayout();

                await WaitForRenderedConditionAsync(() => Math.Abs(row.Translation.Y) > 10, "the reader reflow starts");
                ChatMotion.AnimationsOverride = false;
                await WaitForRenderedConditionAsync(
                    () => row.Translation == System.Numerics.Vector3.Zero,
                    "the disabled animation resets the row");
            }
            finally
            {
                reflow.Detach();
                _ui.Container.Children.Remove(root);
            }
            Assert.Null(row.TranslationTransition);
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
            var timeline = BuildTimeline(entries, 1) with
            {
                SessionId = "agent:a:dashboard:d",
                ToolExpansionState = new ChatToolActivityExpansionState(),
            };
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
                // Like the web work group: no leading glyph, the label first, and a line under the header.
                // The trailing chevron keeps its slot while hidden, so showing it never re-trims the label.
                var restTexts = FindLogical<TextBlock>(header).ToList();
                Assert.Equal(["Worked", ChevronRightGlyph], restTexts.Select(text => text.Text));
                var chevron = restTexts[1];
                Assert.Equal(0, chevron.Opacity);
                Assert.Equal(0, Microsoft.UI.Xaml.Controls.Grid.GetColumn(restTexts[0]));
                Assert.Equal(1, Microsoft.UI.Xaml.Controls.Grid.GetColumn(chevron));
                AssertWorkDividerOutsideCard(root!);
                var hovered = ToolCallCardRenderer.BuildActivityCore(timeline, work, false, _ => { },
                    entry => TextBlock(entry.Text), showChevron: true);
                root = host.Reconciler.Reconcile(element, hovered, root!, static () => { });
                element = hovered;
                host.Content = root;
                Assert.Same(disclosure, Assert.Single(FindLogical<Expander>(root!)));
                var hoverTexts = FindLogical<TextBlock>(Assert.IsAssignableFrom<DependencyObject>(disclosure.Header)).ToList();
                Assert.Equal(["Worked", ChevronRightGlyph], hoverTexts.Select(text => text.Text));
                Assert.Same(chevron, hoverTexts[1]);
                Assert.Equal(1, chevron.Opacity);
                var expanded = ToolCallCardRenderer.BuildActivityCore(timeline, work, true, _ => { },
                    entry => TextBlock(entry.Text), showChevron: true);
                root = host.Reconciler.Reconcile(element, expanded, root!, static () => { });
                element = expanded;
                host.Content = root;
                Assert.Contains(disclosure, FindLogical<Expander>(root!));
                Assert.True(disclosure.IsExpanded);
                Assert.Equal(["Worked · 1 tool call", ChevronDownGlyph],
                    FindLogical<TextBlock>(Assert.IsAssignableFrom<DependencyObject>(disclosure.Header)).Select(text => text.Text));
                // The body's separator replaces the header line, so the open disclosure still shows one line.
                Assert.Equal(1, WorkDividerCount(root!));
                Assert.DoesNotContain(FindLogical<Border>(root!).Single(border => border.Height == 1),
                    Assert.IsType<StackPanel>(root).Children);
                Assert.Contains(FindLogical<TextBlock>(root!), text => text.Text == "Checking");
                // A lone tool keeps its own step row, as the web Worked body renders single-call groups.
                // Its output is built only once the row opens.
                var row = Assert.Single(FindLogical<Expander>(root!), expander => !ReferenceEquals(expander, disclosure));
                Assert.False(row.IsExpanded);
                Assert.Equal("ChatToolCall_read", AutomationProperties.GetAutomationId(row));
                Assert.Empty(FindLogical<RichTextBlock>(root!));
                Assert.DoesNotContain(FindLogical<TextBlock>(root!), text => text.Text == "Preserved answer");

                // Closing again reuses the same disclosure, so its keyboard focus survives, and the header line returns.
                var collapsed = ToolCallCardRenderer.BuildActivityCore(timeline, work, false, _ => { },
                    entry => TextBlock(entry.Text));
                root = host.Reconciler.Reconcile(element, collapsed, root!, static () => { });
                element = collapsed;
                host.Content = root;
                Assert.Same(disclosure, Assert.Single(FindLogical<Expander>(root!)));
                Assert.False(disclosure.IsExpanded);
                Assert.Same(chevron, FindLogical<TextBlock>(Assert.IsAssignableFrom<DependencyObject>(disclosure.Header)).Last());
                Assert.Equal(ChevronRightGlyph, chevron.Text);
                Assert.Equal(0, chevron.Opacity);
                AssertWorkDividerOutsideCard(root!);
            }
            finally
            {
                host.Reconciler.Reconcile(element, Empty(), root!, static () => { });
                host.Content = null;
            }
        });
    }

    [Theory]
    [InlineData(true, true, ChatToolCallStatus.InProgress, 1, false)]
    [InlineData(true, true, ChatToolCallStatus.Success, 2, false)]
    [InlineData(true, true, ChatToolCallStatus.Error, 2, false)]
    [InlineData(true, false, ChatToolCallStatus.InProgress, 0, false)]
    [InlineData(false, true, ChatToolCallStatus.Success, 1, false)]
    [InlineData(false, false, ChatToolCallStatus.InProgress, 0, false)]
    [InlineData(true, true, ChatToolCallStatus.Success, 2, true)]
    [InlineData(true, true, ChatToolCallStatus.Error, 2, true)]
    [InlineData(false, true, ChatToolCallStatus.InProgress, 0, true)]
    public async Task ToolDetails_UseDividersInsteadOfVisibleSectionHeadings(
        bool hasInput, bool hasOutput, ChatToolCallStatus status, int expectedDividers, bool panel)
    {
        await _ui.RunOnUIAsync(() =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl();
            var entry = Tool("sections", "exec", hasOutput ? "Synthetic output" : "", status) with
            {
                ToolArgs = hasInput ? new System.Text.Json.Nodes.JsonObject { ["command"] = "echo synthetic" } : null,
            };
            var context = BuildTimeline([entry], 1);
            var element = panel
                ? ToolCallCardRenderer.BuildPanelDetails(context, entry)
                : ToolCallCardRenderer.BuildStandalone(context, entry);
            var root = host.Reconciler.Mount(element, static () => { });
            host.Content = root;
            try
            {
                if (!panel)
                    Assert.Single(FindLogical<Expander>(root!)).IsExpanded = true;
                Assert.DoesNotContain(FindLogical<TextBlock>(root!),
                    text => text.Text is "TOOL INPUT" or "TOOL OUTPUT" or "TOOL ERROR");
                var sections = FindLogical<ScrollViewer>(root!).ToArray();
                var expectedLabels = new List<string>();
                var expectedContent = new List<string>();
                if (hasInput)
                {
                    expectedLabels.Add("Tool input");
                    expectedContent.Add("command: echo synthetic");
                }
                if (hasOutput)
                {
                    expectedLabels.Add(status == ChatToolCallStatus.Error ? "Tool error" : "Tool output");
                    expectedContent.Add("Synthetic output");
                }
                if (panel)
                {
                    Assert.Equal("ConversationToolDetailsReader",
                        AutomationProperties.GetAutomationId(Assert.Single(sections)));
                    Assert.True(double.IsPositiveInfinity(sections[0].MaxHeight));
                    var text = FindLogical<RichTextBlock>(root!).ToArray();
                    Assert.Equal(expectedLabels, text.Select(AutomationProperties.GetName));
                    Assert.Equal(expectedContent, text.Select(CollectText));
                    Assert.All(text, block => Assert.True(block.IsTextSelectionEnabled));
                }
                else
                {
                    Assert.Equal(expectedLabels, sections.Select(AutomationProperties.GetName));
                    Assert.Equal(expectedContent, sections.Select(section =>
                        CollectText(Assert.IsType<RichTextBlock>(section.Content))));
                    Assert.All(sections, section =>
                        Assert.True(Assert.IsType<RichTextBlock>(section.Content).IsTextSelectionEnabled));
                }
                Assert.Equal(expectedDividers, FindLogical<Border>(root!).Count(border => border.Height == 1));
            }
            finally
            {
                host.Reconciler.Reconcile(element, Empty(), root!, static () => { });
                host.Content = null;
            }
        });
    }

    [Theory]
    [InlineData(ChatToolCallStatus.Success, false, "", false, false)]
    [InlineData(ChatToolCallStatus.Error, false, "", false, false)]
    [InlineData(ChatToolCallStatus.InProgress, false, "", false, false)]
    [InlineData(ChatToolCallStatus.InProgress, true, "", false, true)]
    [InlineData(ChatToolCallStatus.InProgress, false, "Live output", false, true)]
    [InlineData(ChatToolCallStatus.Success, false, "Completed output", false, true)]
    [InlineData(ChatToolCallStatus.Success, false, "", true, true)]
    public async Task ToolWrapper_OffersOwnDetailsOnlyForDisplayContentOrTruncatedOutput(
        ChatToolCallStatus status, bool input, string output, bool truncated, bool hasOwnDetails)
    {
        await _ui.RunOnUIAsync(() =>
        {
            using var theme = new ChatThemeProofScope("Light");
            using var host = new ReactorHostControl();
            var wrapper = Tool("wrapper", "task", output, status) with
            {
                ToolInputDetails = input ? new("", "Inspect the file") : null,
                ToolResultReference = truncated ? new("result", true) : null,
            };
            var child = Tool("child", "read_file", "Child output", ChatToolCallStatus.Success);
            var context = BuildTimeline([wrapper, child], 1) with { OnOpenToolDetails = _ => { } };
            var element = ToolCallCardRenderer.BuildStandalone(context, wrapper, children: [new(child, [])]);
            var root = host.Reconciler.Mount(element, static () => { });
            host.Content = root;
            try
            {
                Assert.Contains(FindLogical<Expander>(root!), expander =>
                    AutomationProperties.GetAutomationId(expander) == "ChatToolCall_child");
                Assert.Equal(hasOwnDetails, FindLogical<Expander>(root!).Any(expander =>
                    AutomationProperties.GetName(expander) == "Tool input"));
                Assert.Equal(hasOwnDetails, FindLogical<Button>(root!).Any(button =>
                    AutomationProperties.GetAutomationId(button) == "ChatToolDetails_wrapper"));
                Assert.DoesNotContain(FindLogical<TextBlock>(root!), text =>
                    AutomationProperties.GetAutomationId(text) == "ChatToolOutputRecoveryStatus");
                Assert.DoesNotContain(FindLogical<Button>(root!), button =>
                    AutomationProperties.GetAutomationId(button) == "ChatToolOutputLoad");
                if (input)
                    Assert.Contains(FindLogical<RichTextBlock>(root!), text => CollectText(text) == "Inspect the file");
                if (output.Length > 0)
                    Assert.Contains(FindLogical<RichTextBlock>(root!), text => CollectText(text) == output);
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
                var output = Assert.Single(FindLogical<RichTextBlock>(root));
                var section = Assert.Single(FindLogical<Border>(root),
                    border => border.Child is ScrollViewer);
                Assert.Equal(new Thickness(12, 8, 12, 8), section.Padding);
                var scroll = Assert.IsType<ScrollViewer>(section.Child);
                Assert.Equal("Tool output", AutomationProperties.GetName(scroll));
                Assert.Equal(240, scroll.MaxHeight);
                Assert.Empty(FindLogical<TextBlock>(section));
                Assert.DoesNotContain(FindLogical<Border>(root), border => border.Height == 1);
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
                Assert.Contains(FindLogical<TextBlock>(header), text => text.Text == "Read main.cs");
                Assert.Contains(FindLogical<TextBlock>(header), text => text.Text == "src");
                Assert.Contains(FindLogical<TextBlock>(header), text => text.Text == DocumentGlyph);
                Assert.Null(ToolTipService.GetToolTip(expander));
                Assert.Contains(output, FindLogical<RichTextBlock>(root));
                Assert.Equal("final <literal> output", CollectText(output));
                Assert.True(output.IsTextSelectionEnabled);
                Assert.Equal(2, FindLogical<Border>(root).Count(border => border.Height == 1));

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
