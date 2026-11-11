using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Chat;
using OpenClawTray.Services;
using Windows.Foundation;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

public sealed partial class ReactorChatLayoutProofTests
{
    [Fact]
    public async Task HistoryLoading_StopsPulsingBeforeCompletedMessagesReplaceIt()
    {
        await WithChatAsync(1100, async (surface, _, _, provider) =>
        {
            StackPanel? skeleton = null;
            await ui.RunOnUIAsync(() =>
                skeleton = FindControl<StackPanel>(surface, "ChatTimelineLoadingSkeleton"));
            var snapshot = await provider.LoadAsync();
            await ui.RunOnUIAsync(() => provider.Publish(snapshot with
            {
                Timelines = new Dictionary<string, ChatTimelineState>
                {
                    ["proof"] = ChatTimelineState.Initial() with
                    {
                        HistoryLoaded = true,
                        Entries = System.Collections.Immutable.ImmutableList.Create(
                            new ChatTimelineItem("user", ChatTimelineItemKind.User, "Completed user content remains readable."),
                            new ChatTimelineItem("assistant", ChatTimelineItemKind.Assistant,
                                "Completed assistant content remains readable.\n\n| Item | Status |\n|---|---|\n| Contrast | Stable |")),
                    },
                },
                CurrentHistoryThreads = new HashSet<string> { "proof" },
            }));
            await SettleAsync();
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => RealizedTimelineElements<TextBlock>(surface).Any(block =>
                    block.Text == "Completed user content remains readable.") &&
                    !RealizedTimelineElements<StackPanel>(surface).Any(panel =>
                        Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(panel) == "ChatTimelineLoadingSkeleton"),
                "completed history replaces the loading skeleton"));
            for (var frame = 0; frame < 9; frame++)
            {
                await CaptureAsync(surface, $"HistoryOpacity-{frame}");
                await ui.RunOnUIAsync(() =>
                {
                    Assert.True(skeleton!.Opacity == 1,
                        $"Retired skeleton: opacity={skeleton.Opacity}, loaded={skeleton.IsLoaded}, parent={VisualTreeHelper.GetParent(skeleton)?.GetType().Name}, children={skeleton.Children.Count}.");
                    foreach (var block in RealizedTimelineElements<TextBlock>(surface))
                        foreach (var ancestor in Ancestors(block).OfType<UIElement>())
                            if (ancestor is not FrameworkElement element ||
                                !Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(element)
                                    .StartsWith("ChatMessageFooter_", StringComparison.Ordinal))
                                Assert.True(ancestor.Opacity is 0 or 1,
                                    $"{block.Text}: ancestor {ancestor.GetType().Name} opacity {ancestor.Opacity}.");
                });
                // Sample two complete seconds of the old skeleton's animation.
                await Task.Delay(250);
            }
        }, scenario: "loading");
    }

    [Theory]
    [InlineData(480)]
    [InlineData(1100)]
    public async Task ConversationFeedback_ThinkingToolReviewsAndHistoryRetainComposerWithoutSearch(int width)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        if (Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            await File.WriteAllTextAsync(Path.Combine(directory, "owned-process.json"), JsonSerializer.Serialize(new
            {
                pid = process.Id, startedUtc = process.StartTime.ToUniversalTime(),
                executable = process.MainModule!.FileName, command = Environment.CommandLine,
                data = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR"),
                appdata = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_APPDATA_DIR"),
                localappdata = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR"),
                mcpPort = Environment.GetEnvironmentVariable("OPENCLAW_MCP_PORT"),
            }));
        }
        await WithChatAsync(width, async (surface, host, _, provider) =>
        {
            var snapshot = await provider.LoadAsync();
            snapshot = snapshot with { Threads = [snapshot.Threads[0] with { ReasoningLevel = "on" }] };
            TextBox? composer = null;
            void ResizeProofWindow()
            {
                var scale = surface.XamlRoot.RasterizationScale;
                _captureWindow!.AppWindow.Resize(new((int)(surface.Width * scale) + 32, (int)(surface.Height * scale) + 80));
            }
            await ui.RunOnUIAsync(() =>
            {
                ResizeProofWindow();
                composer = FindControl<TextBox>(surface, "ChatComposerInput");
                OpenClawReactorChatRoot.SetThinkingVisible(true);
                OpenClawReactorChatRoot.SetToolCallsVisible(true);
            });
            var state = ChatTimelineState.Initial();
            var publication = 0;
            void RecordPhase(string phase)
            {
                if (Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR") is { Length: > 0 } output)
                    File.AppendAllText(Path.Combine(output, "feedback-phases.log"),
                        $"{DateTimeOffset.UtcNow:O} {phase}\n");
            }
            async Task PublishAsync(bool historyFailed = false)
            {
                var sequence = ++publication;
                RecordPhase($"publication {sequence}: dispatch");
                await ui.RunOnUIAsync(() => provider.Publish(snapshot with
                {
                    Timelines = new Dictionary<string, ChatTimelineState> { ["proof"] = state },
                    CurrentHistoryThreads = state.HistoryLoaded ? new HashSet<string> { "proof" } : [],
                    FailedHistoryThreads = historyFailed ? new HashSet<string> { "proof" } : [],
                }));
                RecordPhase($"publication {sequence}: drain");
                await SettleAsync();
                RecordPhase($"publication {sequence}: settled");
            }
            await PublishAsync();
            var opacity = new List<double>();
            for (var frame = 0; frame < 5; frame++)
            {
                opacity.Add(await ui.RunOnUIAsync(() => Task.FromResult(
                    FindControl<StackPanel>(surface, "ChatTimelineLoadingSkeleton").Opacity)));
                await CaptureAsync(surface, $"Feedback-loading-{frame}");
                // Sample the documented two-second animation, not an asynchronous readiness delay.
                await Task.Delay(250);
            }
            var animations = await ui.RunOnUIAsync(() => Task.FromResult(
                new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled));
            if (animations)
                Assert.True(opacity.Max() - opacity.Min() > .1, "The mounted history skeleton did not animate.");
            await PublishAsync(historyFailed: true);
            await ui.RunOnUIAsync(() => Assert.NotNull(
                FindControl<StackPanel>(surface, "ChatHistoryUnavailable")));
            await CaptureAsync(surface, "Feedback-history-unavailable");

            state = ChatTimelineReducer.AddLocalUser(state, "Inspect the synthetic project", "visible", DateTimeOffset.UtcNow);
            state = ChatTimelineReducer.Apply(state, new ChatThinkingEvent("", "proof-run"));
            await PublishAsync();
            await ui.RunOnUIAsync(() => Assert.Contains("Working",
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(
                    FindControl<Grid>(host, "ChatRunProgress"))));
            await CaptureAsync(surface, "Feedback-before-first-event");
            state = ChatTimelineReducer.Apply(state, new ChatReasoningStreamEvent("proof-run", "reason",
                "I will inspect the supplied files before editing.", 1000));
            await PublishAsync();
            await ui.RunOnUIAsync(() =>
                Assert.Contains("I will inspect the supplied files before editing.", ReadToolText(surface)));
            await CaptureAsync(surface, "Feedback-supplied-thinking");
            await ui.RunOnUIAsync(() => Assert.Contains("Thinking",
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(FindControl<Grid>(host, "ChatRunProgress"))));
            for (var frame = 0; frame < 5; frame++)
            {
                await CaptureAsync(surface, $"Feedback-working-{frame}");
                await Task.Delay(250);
            }
            state = ChatTimelineReducer.Apply(state, new ChatProgressEvent(
                "Retrying the model request", DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds()));
            await PublishAsync();
            await ui.RunOnUIAsync(() => Assert.Contains("Retrying in", FindControl<TextBlock>(host, "ChatRunProgressText").Text));
            await CaptureAsync(surface, "Feedback-retry");

            void Tool(string data, int sequence) => state = ChatTimelineReducer.Apply(state,
                ChatEventMapper.Map(new AgentEventInfo { RunId = "proof-run", Seq = sequence, Stream = "tool",
                    Data = JsonDocument.Parse(data).RootElement.Clone() }).Event!);
            Tool("""{"phase":"input_delta","name":"edit","toolCallId":"c","diff":{"added":3,"removed":1}}""", 1);
            await PublishAsync();
            await CaptureAsync(surface, "Feedback-preparing-input");
            Tool("""
                {"phase":"review","name":"edit","toolCallId":"c",
                 "review":{"id":"g","label":"Guardian","status":"in_progress","rationale":"Checking the supplied edit"}}
                """, 2);
            await PublishAsync();
            await ui.RunOnUIAsync(() => Assert.Contains(
                FindDescendants<TextBlock>(FindControl<StackPanel>(surface, "ChatToolReview")),
                text => text.Text == "Checking the supplied edit"));
            await CaptureAsync(surface, "Feedback-tool-review");
            Tool("""{"phase":"start","name":"edit","toolCallId":"c","args":{"path":"example.cs"}}""", 3);
            Tool("""{"phase":"update","toolCallId":"c","partialResult":"Partial needle output"}""", 4);
            await PublishAsync();
            var toolId = state.Entries.Single(entry => entry.ToolCallId == "c").Id;
            await ui.RunOnUIAsync(() => FindControl<Expander>(surface, $"ChatToolCall_{toolId}").IsExpanded = true);
            await SettleAsync();
            await AwaitDisclosureAnimationsAsync(surface);
            await CaptureAsync(surface, "Feedback-partial-output");
            Tool("""
                {"phase":"review","name":"edit","toolCallId":"c",
                 "review":{"id":"g","label":"Guardian","status":"approved"}}
                """, 5);
            Tool("""{"phase":"result","toolCallId":"c","result":"Complete needle output"}""", 6);
            state = ChatTimelineReducer.Apply(state, new ChatMessageEvent("The edit is complete."));
            state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent(), DateTimeOffset.UtcNow);
            await PublishAsync();
            await CaptureAsync(surface, "Feedback-complete");
            await ui.RunOnUIAsync(() =>
            {
                Assert.True(FindControl<Expander>(surface, $"ChatToolCall_{toolId}").IsExpanded);
                Assert.DoesNotContain(FindDescendants<Button>(surface),
                    button => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(button) is
                        "ChatSearchOpen" or "ChatSearchClose");
                Assert.DoesNotContain(FindDescendants<TextBox>(surface),
                    text => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(text) == "ChatTranscriptSearch");
                Assert.Same(composer, FindControl<TextBox>(surface, "ChatComposerInput"));
                Assert.Equal(Draft, composer!.Text);
            });
            snapshot = snapshot with { Threads = [
                snapshot.Threads[0] with { WaitingForSubagents = true,
                    ProviderReview = new("provider-review", "proof-run", "Synthetic review explanation",
                        "Continue only after reviewing the request.", false) },
                new ChatThread { Id = "child", Title = "Inspecting results", ParentSessionKey = "proof",
                    Activity = ChatActivity.Working, CurrentActivity = "Checking the synthetic change" }] };
            await PublishAsync();
            await ResumeFollowingAsync(surface);
            await SettleAsync();
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
            {
                var progress = FindDescendants<TextBlock>(surface).SingleOrDefault(text =>
                    Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(text) == "ChatRunProgressText");
                if (progress is null || !progress.Text.Contains("Waiting for subagents", StringComparison.Ordinal))
                    return false;
                var scroll = Assert.Single(FindDescendants<ItemsView>(surface)).ScrollView;
                var bounds = progress.TransformToVisual(scroll).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, progress.ActualWidth, progress.ActualHeight));
                return bounds.Top >= 0 && bounds.Bottom <= scroll.ActualHeight;
            }, "visible waiting-for-subagents feedback"));
            await CaptureAsync(surface, "Feedback-child-wait");
            await ui.RunOnUIAsync(() => FindControl<Expander>(surface, "ChatChildActivity").IsExpanded = true);
            await ui.RunOnUIAsync(() => FindControl<Expander>(surface, "ChatProviderReview").IsExpanded = true);
            await SettleAsync();
            await AwaitDisclosureAnimationsAsync(surface);
            await CaptureAsync(surface, "Feedback-provider-review");
            await ui.RunOnUIAsync(() =>
            {
                var details = FindControl<ScrollViewer>(surface, "ChatActivityDetails");
                details.ChangeView(null, details.ScrollableHeight, null, disableAnimation: true);
            });
            await SettleAsync();
            await CaptureAsync(surface, "Feedback-child-details");
            await ui.RunOnUIAsync(() => Assert.Contains("Waiting for subagents",
                FindControl<TextBlock>(host, "ChatRunProgressText").Text));

            RecordPhase("append transcript");
            snapshot = snapshot with { Threads = [snapshot.Threads[0] with
                { WaitingForSubagents = false, ProviderReview = null }] };
            for (var index = 0; index < 12; index++)
            {
                state = ChatTimelineReducer.Apply(state, new ChatUserMessageEvent($"Earlier question {index}"));
                state = ChatTimelineReducer.Apply(state, new ChatMessageEvent($"Earlier answer {index}"));
                state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());
            }
            await PublishAsync();
            // Disclosure interaction relinquished following. Resume it explicitly.
            await ResumeFollowingAsync(surface);
            await AwaitInitialTailAsync(surface, "proof", state.Entries.Last().Id);
            var transcriptScroll = await ui.RunOnUIAsync(() => Task.FromResult(
                Assert.Single(FindDescendants<ItemsView>(surface)).ScrollView));
            Task AwaitBottomAsync() => ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => transcriptScroll.State == ScrollingInteractionState.Idle &&
                    Math.Abs(transcriptScroll.ScrollableHeight - transcriptScroll.VerticalOffset) <= 1,
                "jump reaches the exact transcript bottom",
                () => $"offset={transcriptScroll.VerticalOffset}; maximum={transcriptScroll.ScrollableHeight}; state={transcriptScroll.State}"));
            async Task ScrollAwayAsync()
            {
                // Establish reader intent through the normal transcript keyboard-focus path.
                await ui.RunOnUIAsync(async () =>
                {
                    var items = Assert.Single(FindDescendants<ItemsView>(surface));
                    var repeater = Assert.Single(FindDescendants<ItemsRepeater>(transcriptScroll));
                    // Enter an earlier realized item, not the last physical child, which may be recycled.
                    var reader = Enumerable.Range(0, repeater.ItemsSourceView.Count)
                        .Select(index => repeater.TryGetElement(index)).OfType<ItemContainer>()
                        .SelectMany(row => FindDescendants<Button>(row))
                        .First(button => button.IsLoaded && button.IsEnabled && button.IsTabStop &&
                            Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(button)
                                .StartsWith("ChatCopy_", StringComparison.Ordinal));
                    var entered = false;
                    TypedEventHandler<UIElement, GettingFocusEventArgs> onReaderFocus = (_, args) =>
                    {
                        if (args.FocusState == FocusState.Keyboard)
                            entered = true;
                    };
                    items.AddHandler(UIElement.GettingFocusEvent, onReaderFocus, true);
                    try
                    {
                        // A successful Focus call on the already focused control raises no new reader-input event.
                        Assert.True(composer!.Focus(FocusState.Programmatic));
                        Assert.True(reader.Focus(FocusState.Keyboard));
                        await WaitForRenderedConditionAsync(() => entered,
                            "transcript keyboard focus reaches the navigation owner");
                    }
                    finally
                    {
                        items.RemoveHandler(UIElement.GettingFocusEvent, onReaderFocus);
                    }
                });
                await SettleAsync();
                // Animated ScrollToTop is a separate, preserved WinUI platform reproducer.
                await ui.RunOnUIAsync(() =>
                {
                    Assert.True(composer!.Focus(FocusState.Programmatic));
                    transcriptScroll.ScrollTo(0, 0,
                        new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
                });
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                    () => transcriptScroll.State == ScrollingInteractionState.Idle &&
                        transcriptScroll.VerticalOffset <= 1 &&
                        FindControl<Button>(surface, "ChatJumpLatest").Opacity == 1,
                    "reader at the top with the jump control visible",
                    () => $"offset={transcriptScroll.VerticalOffset}; maximum={transcriptScroll.ScrollableHeight}; " +
                        $"viewport={transcriptScroll.ViewportHeight}; state={transcriptScroll.State}; " +
                        $"jump={FindControl<Button>(surface, "ChatJumpLatest").Opacity}"));
            }
            var viewportBeforeJump = await ui.RunOnUIAsync(() => Task.FromResult(
                transcriptScroll.ViewportHeight));
            await ScrollAwayAsync();
            var readerOffset = await ui.RunOnUIAsync(() => Task.FromResult(transcriptScroll.VerticalOffset));
            await ui.RunOnUIAsync(() =>
            {
                var jump = FindControl<Button>(surface, "ChatJumpLatest");
                Assert.Equal(1, jump.Opacity);
                Assert.True(Bounds(jump, surface).Bottom <= Bounds(transcriptScroll, surface).Bottom);
                Assert.Equal(viewportBeforeJump, transcriptScroll.ViewportHeight, precision: 1);
            });
            state = ChatTimelineReducer.Apply(state, new ChatUserMessageEvent("New activity while reading"));
            state = ChatTimelineReducer.Apply(state, new ChatMessageEvent("New reply while reading"));
            state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());
            await PublishAsync();
            await CaptureAsync(surface, "Feedback-new-message");
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(readerOffset, transcriptScroll.VerticalOffset, precision: 1);
                Assert.Equal(viewportBeforeJump, transcriptScroll.ViewportHeight, precision: 1);
                var jump = FindControl<Button>(surface, "ChatJumpLatest");
                Assert.Equal(1, jump.Opacity);
                Assert.Contains("New messages", Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(jump));
                AssertJumpIcon(jump);
                Invoke(jump);
            });
            await AwaitInitialTailAsync(surface, "proof", state.Entries.Last().Id);
            await AwaitBottomAsync();
            await ui.RunOnUIAsync(() =>
            {
                var jump = FindControl<Button>(surface, "ChatJumpLatest");
                Assert.Equal(0, jump.Opacity);
                Assert.False(jump.IsEnabled);
                Assert.False(jump.IsHitTestVisible);
                Assert.False(jump.IsTabStop);
            });
            await CaptureAsync(surface, "Feedback-jump-latest");

            state = ChatTimelineReducer.Apply(state, new ChatMessageEvent("Streaming reply.", IsStreaming: true));
            await PublishAsync();
            for (var index = 0; index < 4; index++)
            {
                state = ChatTimelineReducer.Apply(state, new ChatMessageDeltaEvent(
                    "\n\n" + string.Join("\n\n", Enumerable.Repeat($"Streamed section {index}.", 5))));
                await PublishAsync();
                await AwaitBottomAsync();
            }
            state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());
            await PublishAsync();
            await AwaitInitialTailAsync(surface, "proof", state.Entries.Last().Id);
            await ui.RunOnUIAsync(() =>
            {
                surface.Width = width == 480 ? 1100 : 480;
                ResizeProofWindow();
            });
            await SettleAsync();
            await AwaitBottomAsync();
            await ScrollAwayAsync();
            await ui.RunOnUIAsync(() =>
            {
                surface.Width = width;
                ResizeProofWindow();
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.True(transcriptScroll.ScrollableHeight - transcriptScroll.VerticalOffset >
                    transcriptScroll.ViewportHeight);
                Invoke(FindControl<Button>(surface, "ChatJumpLatest"));
            });
            await AwaitInitialTailAsync(surface, "proof", state.Entries.Last().Id);
            await AwaitBottomAsync();
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => transcriptScroll.VerticalAnchorRatio == 1,
                "bottom anchoring restored after the corrected jump"));
            await CaptureAsync(surface, "Feedback-resized-jump-latest");

            state = ChatTimelineReducer.Apply(state, new ChatMessageEvent("Reply after resized jump.", IsStreaming: true));
            await PublishAsync();
            await AwaitBottomAsync();
            state = ChatTimelineReducer.Apply(state, new ChatMessageDeltaEvent(
                "\n\n" + string.Join("\n\n", Enumerable.Repeat("Keep following after the corrected jump.", 20))));
            await PublishAsync();
            await AwaitBottomAsync();
            state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());
            await PublishAsync();
            await AwaitInitialTailAsync(surface, "proof", state.Entries.Last().Id);
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(composer, FindControl<TextBox>(surface, "ChatComposerInput"));
                Assert.Equal(Draft, composer!.Text);
            });

            var older = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ReactorHostControl? historyHost = null;
            try
            {
                await ui.RunOnUIAsync(() =>
                {
                    historyHost = new ReactorHostControl();
                    var timeline = new ChatTimelinePresentationContext("paging", [state.Entries.Last()],
                        true, () => older.Task);
                    historyHost.Mount(_ => Microsoft.UI.Reactor.Factories.Component<ReactorChatTimeline, ReactorChatTimelineProps>(
                        new(ReactorChatTimelineMode.Timeline, timeline)));
                    surface.Child = historyHost;
                });
                await SettleAsync();
                await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(surface, "ChatLoadEarlier")));
                await SettleAsync();
                await ui.RunOnUIAsync(() =>
                {
                    var button = FindControl<Button>(surface, "ChatLoadEarlier");
                    Assert.False(button.IsEnabled);
                    Assert.Contains("Loading messages", Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button));
                });
                await CaptureAsync(surface, "Feedback-older-loading");
                older.SetResult();
                await SettleAsync();
                await ui.RunOnUIAsync(() => Assert.True(FindControl<Button>(surface, "ChatLoadEarlier").IsEnabled));
            }
            finally
            {
                older.TrySetResult();
                await ui.RunOnUIAsync(() => { surface.Child = host; historyHost?.Dispose(); });
            }
        }, "empty", interactiveKeyboard: true);
    }

    [Theory]
    [InlineData(480, "load")]
    [InlineData(1100, "load")]
    [InlineData(480, "cancel")]
    [InlineData(480, "close")]
    [InlineData(480, "profile-same")]
    [InlineData(480, "profile-different")]
    public async Task ConversationParity_RecoveryReadsTheSavedResultOnlyAfterTheDetailsAction(int width, string action)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        Assert.Equal(Path.Combine(Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR")!, "openclaw-tray.log"),
            Logger.LogFilePath, StringComparer.OrdinalIgnoreCase);
        using var temp = new TempDirectory("tool-recovery-proof-");
        using var release = new ManualResetEventSlim(false);
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(
            Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!,
            "tests", "OpenClaw.Tray.UITests", "Fixtures", "tool-output-recovery.json")))!;
        var key = fixture["sessionKey"]!.GetValue<string>();
        var sessionId = fixture["sessionId"]!.GetValue<string>();
        var resultId = fixture["resultMessageId"]!.GetValue<string>();
        var callId = fixture["callId"]!.GetValue<string>();
        var runId = fixture["runId"]!.GetValue<string>();
        var full = string.Concat(Enumerable.Repeat(fixture["line"]!.GetValue<string>(), fixture["lineCount"]!.GetValue<int>()))
            + fixture["tail"]!.GetValue<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var profileId = GatewayScenario.OwnerProfileId;
        var profileName = "Before";
        object Result(string text, bool truncated) => new
        {
            role = "toolResult", toolCallId = callId, toolName = "exec",
            __openclaw = new { id = resultId, runId, truncated, reason = "display-cap" },
            content = new[] { new { type = "text", text } },
        };
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        server.OverrideProfileRead(_ => new
        {
            profile = new { id = Volatile.Read(ref profileId), displayName = Volatile.Read(ref profileName) },
        });
        server.OverrideChatRead("chat.history", parameters =>
        {
            Assert.Equal(key, parameters.GetProperty("sessionKey").GetString());
            Assert.False(parameters.TryGetProperty("toolResultMaxChars", out _));
            var preview = full[..fixture["genericHistoryMaxChars"]!.GetValue<int>()] + "\n...(truncated)...";
            return new
            {
                sessionKey = key, sessionId,
                messages = new object[]
                {
                    new { role = "assistant", __openclaw = new { id = "input-message", runId },
                        content = new[] { new { type = "toolCall", id = callId, name = "exec", arguments = new { command = "synthetic read" } } } },
                    Result(preview, true),
                    new { role = "assistant", content = "Recovery fixture final reply.", __openclaw = new { id = "answer", runId } },
                },
            };
        });
        server.OverrideChatRead("chat.message.get", parameters =>
        {
            Assert.Equal(key, parameters.GetProperty("sessionKey").GetString());
            Assert.Equal(sessionId, parameters.GetProperty("sessionId").GetString());
            Assert.Equal(resultId, parameters.GetProperty("messageId").GetString());
            Assert.Equal(OpenClawChatDataProvider.MaxEntryTextBytes, parameters.GetProperty("maxChars").GetInt32());
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("The proof did not release its full-output response.");
            return new { ok = true, message = Result(full, false) };
        });
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SessionsUpdated += (_, sessions) =>
        {
            if (sessions.Any(session => session.Key == key && session.SessionId == sessionId))
                ready.TrySetResult();
        };
        await client.ConnectAsync();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var bridge = new GatewayClientChatBridge(client);
        var profileChanges = new System.Collections.Concurrent.ConcurrentQueue<ChatToolDetailsProfileState>();
        bridge.ToolDetailsInvalidated += (_, _) => profileChanges.Enqueue(bridge.ToolDetailsProfileState);
        await using var source = new OpenClawChatDataProvider(bridge, null,
            temp.Combine("tools.json"), temp.Combine("attachments.json"), temp.Combine("last-state.json"));
        await source.LoadHistoryAsync(key);
        var initial = await source.LoadAsync();
        var entry = Assert.Single(initial.Timelines[key].Entries, item => item.ToolCallId == callId);
        Assert.True(entry.ToolResultReference!.IsTruncated);
        async Task ProfileSettled(long previousRevision)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (profileChanges.Count(state => state.Revision > previousRevision) < 2)
            {
                Assert.True(DateTime.UtcNow < deadline, "Profile verification did not complete through the bridge.");
                await SettleAsync();
            }
            await SettleAsync();
        }
        try
        {
            await WithChatAsync(width, async (surface, _, session, _) =>
            {
                await ui.RunOnUIAsync(() =>
                {
                    session.ViewModel.SetDraft("Preserve this draft.");
                    var scale = surface.XamlRoot.RasterizationScale;
                    _captureWindow!.AppWindow.Resize(new((int)Math.Ceiling(width * scale) + 32,
                        (int)Math.Ceiling(surface.Height * scale) + 64));
                });
                await SettleAsync();
                await AwaitInitialTailAsync(surface, key, initial.Timelines[key].Entries.Last().Id);
                await ui.RunOnUIAsync(() =>
                    FindControl<Expander>(surface, $"ChatToolActivity_{entry.Id}").IsExpanded = true);
                await SettleAsync();
                var expander = await ui.RunOnUIAsync(() =>
                {
                    var control = FindControl<Expander>(surface, $"ChatToolCall_{entry.Id}");
                    control.IsExpanded = true;
                    return Task.FromResult(control);
                });
                await SettleAsync();
                await RevealHeaderAsync(surface, expander);
                Assert.DoesNotContain(server.Requests, request => request.Method == "chat.message.get");
                var output = await ui.RunOnUIAsync(() => Task.FromResult(FindDescendants<RichTextBlock>(expander).Last()));
                await CaptureAsync(surface, $"ToolRecovery-{width}-{action}-preview");
                await ui.RunOnUIAsync(() =>
                {
                    var load = FindControl<Button>(expander, "ChatToolOutputLoad");
                    Invoke(load);
                    Invoke(load);
                });
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await SettleAsync();
                await CaptureAsync(surface, $"ToolRecovery-{width}-{action}-loading");
                var previousProfile = bridge.ToolDetailsProfileState;
                if (action == "profile-different")
                {
                    Volatile.Write(ref profileId, "different-account");
                    await server.PublishSelfProfileChangedAsync();
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (bridge.ToolDetailsProfileState.Revision == previousProfile.Revision)
                    {
                        Assert.True(DateTime.UtcNow < deadline, "Profile invalidation was not observed.");
                        await SettleAsync();
                    }
                    Assert.False(bridge.ToolDetailsProfileState.IsAvailable);
                }
                if (action is "cancel" or "close")
                {
                    await ui.RunOnUIAsync(() =>
                    {
                        if (action == "cancel")
                            Invoke(FindControl<Button>(expander, "ChatToolOutputCancel"));
                        else
                            expander.IsExpanded = false;
                    });
                    await SettleAsync();
                }
                release.Set();
                await server.WaitForCompletedRequestAsync("chat.message.get", key).WaitAsync(TimeSpan.FromSeconds(10));
                if (action == "profile-different")
                {
                    await ProfileSettled(previousProfile.Revision);
                    Assert.False(bridge.ToolDetailsProfileState.IsAvailable);
                    await ui.RunOnUIAsync(() =>
                    {
                        Assert.DoesNotContain("RECOVERED OUTPUT TAIL", ReadToolText(expander));
                        Assert.DoesNotContain(FindDescendants<Button>(expander), button =>
                            Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(button) == "ChatToolOutputLoad");
                    });
                    Assert.Single(server.Requests, request => request.Method == "chat.message.get");
                    await CaptureAsync(surface, $"ToolRecovery-{width}-{action}-retired");
                    return;
                }
                if (action is "cancel" or "close")
                {
                    await SettleAsync();
                    await ui.RunOnUIAsync(() =>
                    {
                        Assert.DoesNotContain("RECOVERED OUTPUT TAIL", ReadToolText(expander));
                        expander.IsExpanded = true;
                    });
                    await SettleAsync();
                    Assert.Single(server.Requests, request => request.Method == "chat.message.get");
                    await ui.RunOnUIAsync(() => Invoke(FindControl<Button>(expander, "ChatToolOutputLoad")));
                }
                await WaitForRecoveryAsync(expander);
                if (action == "profile-same")
                {
                    previousProfile = bridge.ToolDetailsProfileState;
                    Volatile.Write(ref profileName, "After");
                    await server.PublishSelfProfileChangedAsync();
                    await ProfileSettled(previousProfile.Revision);
                    Assert.True(bridge.ToolDetailsProfileState.IsAvailable);
                    Assert.NotEqual(previousProfile, bridge.ToolDetailsProfileState);
                    await ui.RunOnUIAsync(() =>
                    {
                        Assert.DoesNotContain("RECOVERED OUTPUT TAIL", ReadToolText(expander));
                        Invoke(FindControl<Button>(expander, "ChatToolOutputLoad"));
                    });
                    await WaitForRecoveryAsync(expander);
                }
                await ui.RunOnUIAsync(() =>
                {
                    Assert.Contains(output, FindDescendants<RichTextBlock>(expander));
                    Assert.Contains("RECOVERED OUTPUT TAIL", ReadToolText(expander));
                    Assert.Equal("Preserve this draft.", FindControl<TextBox>(surface, "ChatComposerInput").Text);
                    var scroll = FindDescendants<ScrollViewer>(expander).Last();
                    scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
                });
                await SettleAsync();
                await CaptureAsync(surface, $"ToolRecovery-{width}-{action}-recovered");
                Assert.Equal(action == "load" ? 1 : 2, server.Requests.Count(request => request.Method == "chat.message.get"));
                Assert.DoesNotContain("RECOVERED OUTPUT TAIL", (await source.LoadAsync()).Timelines[key].Entries
                    .Single(item => item.ToolCallId == callId).ToolOutput);
                if (Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR") is { Length: > 0 } directory)
                    await File.WriteAllTextAsync(Path.Combine(directory, $"recovery-{width}-{action}.json"),
                        JsonSerializer.Serialize(new
                        {
                            fullOutputSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(full))),
                            initialBudget = fixture["genericHistoryMaxChars"]!.GetValue<int>(),
                            resultMessageId = resultId, sessionId, callId,
                            fullReads = server.Requests.Count(request => request.Method == "chat.message.get"),
                        }));
            }, initialThreadId: key, renderProvider: source, interactiveKeyboard: true);
        }
        finally { release.Set(); }
    }

    private async Task WaitForRecoveryAsync(Expander expander)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await ui.RunOnUIAsync(() => Task.FromResult(ReadToolText(expander).Contains("RECOVERED OUTPUT TAIL"))))
        {
            Assert.True(DateTime.UtcNow < deadline, "The recovered result was not displayed.");
            await SettleAsync();
        }
    }

    [Theory]
    [InlineData(480)]
    [InlineData(1100)]
    public async Task ConversationParity_RetainsSuppliedToolInformationInNativeDisclosures(int width)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        Assert.Equal(Path.Combine(Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR")!, "openclaw-tray.log"),
            Logger.LogFilePath, StringComparer.OrdinalIgnoreCase);
        var fixturePath = Path.Combine(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!,
            "tests", "OpenClaw.Tray.UITests", "Fixtures", "conversation-parity.json");
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(fixturePath))!;
        var fullOutput = string.Concat(Enumerable.Repeat(
            fixture["longOutput"]!["line"]!.GetValue<string>(),
            fixture["longOutput"]!["count"]!.GetValue<int>())) + fixture["longOutput"]!["tail"]!.GetValue<string>();
        foreach (var message in fixture["messages"]!.AsArray())
            if (message!["content"] is JsonArray content)
                foreach (var block in content)
                    if (block?["text"]?.GetValue<string>() == "__LONG_OUTPUT__")
                        block!["text"] = fullOutput;
        var key = fixture["sessionKey"]!.GetValue<string>();
        using var wire = JsonDocument.Parse(new JsonObject
        {
            ["sessionKey"] = key, ["messages"] = fixture["messages"]!.DeepClone(),
            ["session"] = fixture["session"]!.DeepClone(),
        }.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        var parser = typeof(OpenClawGatewayClient).GetMethod("ParseChatHistory", BindingFlags.NonPublic | BindingFlags.Static)!;
        var history = (ChatHistoryInfo)parser.Invoke(null, [wire.RootElement, key])!;
        using var data = new TempDirectory("conversation-parity-");
        using var parseClient = new OpenClawGatewayClient("ws://127.0.0.1:1", "synthetic-proof",
            identityPath: Path.Combine(data.Path, "identity"), persistHandshakeDeviceTokens: false);
        var sessionRow = new SessionInfo { Key = key, IsMain = true };
        typeof(OpenClawGatewayClient).GetMethod("PopulateSessionFromObject", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(parseClient, [sessionRow, wire.RootElement.GetProperty("session"), true]);
        using var bridge = new ConversationParityBridge(history, sessionRow);
        await using var source = new OpenClawChatDataProvider(bridge, null,
            Path.Combine(data.Path, "tools.json"), Path.Combine(data.Path, "attachments.json"),
            Path.Combine(data.Path, "last-state.json"));
        await source.LoadHistoryAsync(key);
        var snapshot = await source.LoadAsync();
        var command = Assert.Single(snapshot.Timelines[key].Entries, entry => entry.ToolCallId == "command-1");
        var generic = Assert.Single(snapshot.Timelines[key].Entries, entry => entry.ToolCallId == "generic-1");
        await WithChatAsync(width, async (surface, _, session, _) =>
        {
            await ui.RunOnUIAsync(() =>
            {
                session.ViewModel.SetDraft("");
                Assert.True(FindControl<TextBox>(surface, "ChatComposerInput").Focus(FocusState.Programmatic));
                var scale = surface.XamlRoot.RasterizationScale;
                _captureWindow!.AppWindow.Resize(new(
                    (int)Math.Ceiling(width * scale) + 32, (int)Math.Ceiling(surface.Height * scale) + 64));
            });
            await SettleAsync();
            await AwaitInitialTailAsync(surface, key, snapshot.Timelines[key].Entries.Last().Id);
            await ui.RunOnUIAsync(() =>
            {
                Assert.Single(FindDescendants<TextBlock>(surface),
                    block => block.Text.StartsWith("Assistant ·", StringComparison.Ordinal));
                Assert.Contains("PARITY THINKING", ReadToolText(surface));
                var group = Assert.Single(FindDescendants<Expander>(surface), control =>
                    Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(control).StartsWith("ChatToolActivity_"));
                Assert.False(group.IsExpanded);
                Assert.InRange(group.ActualHeight, 32, 40);
            });
            await CaptureAsync(surface, $"ConversationParity-{width}-collapsed");
            await ui.RunOnUIAsync(() =>
            {
                foreach (var activity in FindDescendants<Expander>(surface).Where(control =>
                    Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(control).StartsWith("ChatToolActivity_")))
                    activity.IsExpanded = true;
            });
            await SettleAsync();
            var commandControl = await ui.RunOnUIAsync(() =>
            {
                var control = FindControl<Expander>(surface, $"ChatToolCall_{command.Id}");
                control.IsExpanded = true;
                return Task.FromResult(control);
            });
            await SettleAsync();
            await RevealHeaderAsync(surface, commandControl);
            var commandText = await ui.RunOnUIAsync(() => Task.FromResult(ReadToolText(commandControl)));
            await CaptureAsync(surface, $"ConversationParity-{width}-command-expanded");
            await ui.RunOnUIAsync(() =>
            {
                var output = FindDescendants<ScrollViewer>(commandControl).LastOrDefault();
                output?.ChangeView(null, output.ScrollableHeight, null, true);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() => FindDescendants<ScrollViewer>(commandControl).Last()
                .StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 }));
            await SettleAsync();
            await CaptureAsync(surface, $"ConversationParity-{width}-command-tail");
            var genericControl = await ui.RunOnUIAsync(() =>
            {
                commandControl.IsExpanded = false;
                var control = FindControl<Expander>(surface, $"ChatToolCall_{generic.Id}");
                control.IsExpanded = true;
                return Task.FromResult(control);
            });
            await SettleAsync();
            await RevealHeaderAsync(surface, genericControl);
            var genericText = await ui.RunOnUIAsync(() => Task.FromResult(ReadToolText(genericControl)));
            await CaptureAsync(surface, $"ConversationParity-{width}-generic-expanded");
            if (Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR") is { Length: > 0 } directory)
                await File.WriteAllTextAsync(Path.Combine(directory, $"conversation-{width}.json"), JsonSerializer.Serialize(new
                {
                    qualification = "Synthetic same-input capture. Assertions below determine information parity.",
                    inputSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(wire.RootElement.GetRawText()))),
                    commandText, genericText,
                    hasCommandTail = commandText.Contains("PARITY COMMAND TAIL"),
                    hasOutputTail = commandText.Contains("PARITY OUTPUT TAIL"),
                    hasGenericOptions = genericText.Contains("includeDocs"),
                }, new JsonSerializerOptions { WriteIndented = true }));
            Assert.Contains("PARITY COMMAND TAIL", commandText);
            Assert.Contains("PARITY OUTPUT TAIL", commandText);
            Assert.Contains("includeDocs", genericText);
        }, initialThreadId: key, renderProvider: source, interactiveKeyboard: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversationParity_ProgrammaticOrComposerFocusRetainsFollowing(bool transcriptFocus)
    {
        await WithChatAsync(480, async (surface, _, _, provider) =>
        {
            var snapshot = await provider.LoadAsync();
            await AwaitInitialTailAsync(surface, "proof", snapshot.Timelines["proof"].Entries.Last().Id);
            await ui.RunOnUIAsync(() =>
            {
                Control target = transcriptFocus
                    ? FindDescendants<Button>(Assert.Single(FindDescendants<ItemsView>(surface))).Last()
                    : FindControl<TextBox>(surface, "ChatComposerInput");
                Assert.True(target.Focus(FocusState.Programmatic));
            });
            var state = ChatTimelineReducer.AddLocalUser(snapshot.Timelines["proof"], "Continue", "focus-follow");
            state = ChatTimelineReducer.Apply(state,
                new ChatMessageEvent(string.Join("\n\n", Enumerable.Repeat("New visible reply.", 20))));
            state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());
            await ui.RunOnUIAsync(() => provider.Publish(snapshot with
            {
                Timelines = new Dictionary<string, ChatTimelineState> { ["proof"] = state },
            }));
            await SettleAsync();
            await AwaitInitialTailAsync(surface, "proof", state.Entries.Last().Id);
        }, interactiveKeyboard: true);
    }

    [Fact]
    public void ConversationParity_ReadinessRejectsClippedMissingAndWrongLogicalTails()
    {
        const string tail = "thread:expected-session|generation:7|kind:Assistant|id:last";
        Assert.True(TailVisible(tail, tail, tail, 499, 0, 500));
        Assert.False(TailVisible(tail, tail, null, 499, 0, 500));
        Assert.False(TailVisible(tail, tail, tail, null, 0, 500));
        Assert.False(TailVisible(tail, tail, tail, 520, 0, 500));
        Assert.False(TailVisible(tail, tail, "earlier-realized-row", 499, 0, 500));
        Assert.False(TailVisible(tail, tail.Replace("expected-session", "other"), tail, 499, 0, 500));
        Assert.False(TailVisible(tail, tail.Replace("generation:7", "generation:8"), tail, 499, 0, 500));
    }

    private static bool TailVisible(string requiredKey, string? expectedKey, string? realizedKey,
        double? bottom, double viewportTop, double viewportBottom) =>
        expectedKey == requiredKey && realizedKey == requiredKey &&
        bottom.HasValue && bottom.Value >= viewportTop && bottom.Value <= viewportBottom + 1;

    private static void AssertJumpIcon(Button jump)
    {
        Assert.Equal("\uE74B", Assert.IsType<TextBlock>(jump.Content).Text);
        Assert.Equal(32, jump.ActualWidth, precision: 1);
        Assert.Equal(32, jump.ActualHeight, precision: 1);
        Assert.Equal(new CornerRadius(16), jump.CornerRadius);
        Assert.Equal(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(jump),
            ToolTipService.GetToolTip(jump));
        Assert.True(jump.IsEnabled);
        Assert.True(jump.IsHitTestVisible);
        Assert.True(jump.IsTabStop);
    }

    private Task ResumeFollowingAsync(FrameworkElement surface) => ui.RunOnUIAsync(async () =>
    {
        var items = Assert.Single(FindDescendants<ItemsView>(surface));
        var scroll = items.ScrollView;
        Button? jump = null;
        await WaitForRenderedConditionAsync(() =>
        {
            jump = FindDescendants<Button>(surface).SingleOrDefault(button =>
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(button) == "ChatJumpLatest"
                && button.Visibility == Visibility.Visible && button.Opacity > 0 && button.ActualHeight > 0);
            if (jump is not null)
                return true;
            var repeater = FindDescendants<ItemsRepeater>(items).FirstOrDefault();
            var source = repeater?.ItemsSourceView;
            var count = source?.Count ?? 0;
            var last = count > 0 ? repeater?.TryGetElement(count - 1) as ItemContainer : null;
            if (last?.Child is not Border { Child: FrameworkElement content }
                || content.ActualHeight <= 0
                || !ReferenceEquals(last.DataContext, source!.GetAt(count - 1)))
                return false;
            var bounds = content.TransformToVisual(scroll).TransformBounds(
                new Windows.Foundation.Rect(0, 0, content.ActualWidth, content.ActualHeight));
            return bounds.Bottom >= 0 && bounds.Bottom <= scroll.ActualHeight + 1;
        }, "tail landing or the Jump to latest command",
            () => $"offset={scroll.VerticalOffset}; maximum={scroll.ScrollableHeight}; jump={jump is not null}");
        if (jump is not null)
            Invoke(jump);
    });

    private async Task AwaitInitialTailAsync(FrameworkElement surface, string expectedSession, string expectedEntryId,
        long expectedGeneration = 0)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settledFrames = 0;
        var observedFrames = 0;
        var lastGeometry = "";
        (double Extent, double Viewport, double Offset)? previous = null;
        ScrollView? scroll = null;
        ItemsView? items = null;
        var requiredKey = $"thread:{expectedSession}|generation:{expectedGeneration}|kind:Assistant|id:{expectedEntryId}";
        static string? CarrierKey(object? carrier) =>
            carrier?.GetType().GetProperty("Key", BindingFlags.Instance | BindingFlags.Public)?.GetValue(carrier) as string;
        void ObserveTail()
        {
            var geometry = (scroll!.ExtentHeight, scroll.ViewportHeight, scroll.VerticalOffset);
            observedFrames++;
            var geometryDescription = $"extent={geometry.Item1}; viewport={geometry.Item2}; offset={geometry.Item3}; maximum={scroll.ScrollableHeight}; state={scroll.State}";
            var repeater = FindDescendants<ItemsRepeater>(items!).FirstOrDefault();
            var source = repeater?.ItemsSourceView;
            var count = source?.Count ?? 0;
            var expected = count > 0 ? source!.GetAt(count - 1) : null;
            var last = count > 0 ? repeater?.TryGetElement(count - 1) as FrameworkElement : null;
            var viewport = scroll.TransformToVisual(surface).TransformBounds(
                new Windows.Foundation.Rect(0, 0, scroll.ActualWidth, scroll.ActualHeight));
            // The container includes trailing row margin; its child content must be visible, not that spacing.
            var content = (last as ItemContainer)?.Child is Border row ? row.Child as FrameworkElement : null;
            var lastBottom = content?.TransformToVisual(surface).TransformBounds(
                new Windows.Foundation.Rect(0, 0, content.ActualWidth, content.ActualHeight)).Bottom;
            lastGeometry = geometryDescription + $"; count={count}; expectedKey={CarrierKey(expected)}; realizedKey={CarrierKey(last?.DataContext)}; bottom={lastBottom}; viewportBottom={viewport.Bottom}";
            settledFrames = previous == geometry && scroll.State == ScrollingInteractionState.Idle &&
                last is not null && repeater!.GetElementIndex(last) == count - 1 &&
                ReferenceEquals(expected, last.DataContext) &&
                TailVisible(requiredKey, CarrierKey(expected), CarrierKey(last.DataContext), lastBottom, viewport.Top, viewport.Bottom)
                    ? settledFrames + 1 : 0;
            previous = geometry;
            if (settledFrames >= InitialTailConfirmation.SettledFramesBeforeDecision)
                ready.TrySetResult();
        }
        EventHandler<object> rendering = (_, _) =>
        {
            try { ObserveTail(); }
            catch (Exception ex) { ready.TrySetException(ex); }
        };
        await ui.RunOnUIAsync(() =>
        {
            items = Assert.Single(FindDescendants<ItemsView>(surface));
            scroll = items.ScrollView;
            CompositionTarget.Rendering += rendering;
        });
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Initial tail did not settle: frames={observedFrames}; settled={settledFrames}; {lastGeometry}", ex);
        }
        finally { await ui.RunOnUIAsync(() => CompositionTarget.Rendering -= rendering); }
    }

    private async Task AwaitDisclosureAnimationsAsync(FrameworkElement surface)
    {
        var animations = new List<(Storyboard Storyboard, EventHandler<object> Handler, Task Completion)>();
        await ui.RunOnUIAsync(() =>
        {
            foreach (var group in FindDescendants<FrameworkElement>(surface)
                .SelectMany(element => VisualStateManager.GetVisualStateGroups(element))
                .Where(group => group.Name == "ExpandStates"))
            {
                if (group.CurrentState?.Storyboard is not { } storyboard ||
                    storyboard.GetCurrentState() != ClockState.Active)
                    continue;
                var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<object> handler = (_, _) => finished.TrySetResult();
                storyboard.Completed += handler;
                animations.Add((storyboard, handler, finished.Task));
            }
        });
        try { await Task.WhenAll(animations.Select(animation => animation.Completion)).WaitAsync(TimeSpan.FromSeconds(5)); }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                foreach (var animation in animations)
                    animation.Storyboard.Completed -= animation.Handler;
            });
        }
    }

    private async Task RevealHeaderAsync(FrameworkElement surface, Expander control)
    {
        await ui.RunOnUIAsync(() =>
        {
            var header = FindDescendants<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>(control)
                .First(button => button.Name == "ExpanderHeader");
            Assert.True(header.Focus(FocusState.Keyboard));
        });
        await AwaitDisclosureAnimationsAsync(surface);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ScrollView? scroll = null;
        var requestId = -1;
        var targetOffset = 0d;
        void Completed(ScrollView sender, ScrollingScrollCompletedEventArgs args)
        {
            if (args.CorrelationId == requestId)
                completion.TrySetResult();
        }
        await ui.RunOnUIAsync(() =>
        {
            Assert.Same(control, FindControl<Expander>(surface,
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(control)));
            scroll = Assert.Single(FindDescendants<ItemsView>(surface)).ScrollView;
            var top = control.TransformToVisual(surface).TransformPoint(default).Y -
                scroll.TransformToVisual(surface).TransformPoint(default).Y;
            targetOffset = Math.Clamp(scroll.VerticalOffset + top - 8, 0, scroll.ScrollableHeight);
            scroll.ScrollCompleted += Completed;
            requestId = scroll.ScrollTo(scroll.HorizontalOffset, targetOffset,
                new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
            if (requestId == -1)
                completion.TrySetResult();
        });
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { await ui.RunOnUIAsync(() => scroll!.ScrollCompleted -= Completed); }
        await SettleAsync();
        await ui.RunOnUIAsync(() =>
        {
            var top = control.TransformToVisual(surface).TransformPoint(default).Y;
            Assert.True(top >= 0 && top <= surface.ActualHeight - 40,
                $"Header top={top}; requested={targetOffset}; scroll offset={scroll!.VerticalOffset}; maximum={scroll.ScrollableHeight}; request={requestId}");
        });
    }

    private static string ReadToolText(DependencyObject root) => string.Join("\n",
        FindDescendants<RichTextBlock>(root).SelectMany(block => block.Blocks.OfType<Paragraph>())
            .Select(paragraph => string.Concat(paragraph.Inlines.Select(ReadInline))));

    private static string ReadInline(Inline inline) => inline switch
    {
        Run run => run.Text,
        Span span => string.Concat(span.Inlines.Select(ReadInline)),
        LineBreak => "\n",
        _ => "",
    };

    private sealed class ConversationParityBridge(ChatHistoryInfo history, SessionInfo session) : IChatGatewayBridge
    {
        public bool IsConnected => true;
        public ConnectionStatus CurrentStatus => ConnectionStatus.Connected;
        public string MainSessionKey => history.SessionKey;
        public bool HasHandshakeSnapshot => true;
        public SessionInfo[] GetSessionList() => [session.Clone()];
        public ModelsListInfo? GetCurrentModelsList() => null;
        public void StartProactiveBootstrap() => SessionsUpdated?.Invoke(this, GetSessionList());
        public Task<ChatHistoryInfo> RequestChatHistoryAsync(string? sessionKey) => Task.FromResult(history);
        public Task<CommandCatalog> ListCommandsAsync(CommandCatalogQuery? query = null) =>
            Task.FromResult(new CommandCatalog { IsSupported = false });
        public Task SendChatMessageAsync(string message, string? sessionKey, string? sessionId,
            IReadOnlyList<ChatAttachment>? attachments = null) => throw new NotSupportedException();
        public Task<ChatSendResult> SendChatMessageForRunAsync(string message, string? sessionKey, string? sessionId,
            IReadOnlyList<ChatAttachment>? attachments = null, string? idempotencyKey = null) => throw new NotSupportedException();
        public Task PatchSessionModelAsync(string sessionKey, string model) => throw new NotSupportedException();
        public Task ClearSessionModelAsync(string sessionKey) => throw new NotSupportedException();
        public Task PatchSessionThinkingLevelAsync(string sessionKey, string thinkingLevel) => throw new NotSupportedException();
        public Task ClearSessionThinkingLevelAsync(string sessionKey) => throw new NotSupportedException();
        public Task SendChatAbortAsync(string runId, string? sessionKey = null) => throw new NotSupportedException();
        public Task ResolveExecApprovalAsync(string approvalId, string decision) => throw new NotSupportedException();
        public void Dispose() { }
#pragma warning disable CS0067
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<SessionInfo[]>? SessionsUpdated;
        public event EventHandler<SessionUsageSnapshot>? SessionUsageSnapshotUpdated;
        public event EventHandler<SessionCommandResult>? SessionCommandCompleted;
        public event EventHandler<ChatMessageInfo>? ChatMessageReceived;
        public event EventHandler<AgentEventInfo>? AgentEventReceived;
        public event EventHandler<ModelsListInfo>? ModelsListUpdated;
#pragma warning restore CS0067
    }
}
