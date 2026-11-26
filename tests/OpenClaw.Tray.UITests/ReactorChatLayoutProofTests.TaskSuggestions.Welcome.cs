// Real-root empty-history and space-shortage regressions; fixtures are in-memory, never live workers.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Chat;
using Windows.System;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

public sealed partial class ReactorChatLayoutProofTests
{
    [NativeKeyboardTheory]
    [InlineData(620, "late")]
    [InlineData(360, "late")]
    [InlineData(620, "at-mount")]
    [InlineData(360, "at-mount")]
    [InlineData(620, "identity-late")]
    [InlineData(360, "identity-late")]
    [InlineData(620, "reappear")]
    [InlineData(360, "reappear")]
    [InlineData(360, "pending")]
    [InlineData(360, "question")]
    [InlineData(620, "repository-boundaries")]
    [InlineData(620, "cloud-boundaries")]
    [InlineData(620, "question-collapsed-focus")]
    [InlineData(620, "question-terminal-focus")]
    [Trait("Category", "NativeKeyboard")]
    public async Task SuggestedTasks_RealWelcomeAndShortageLifecycle(int height, string state)
    {
        using var temp = new TempDirectory();
        var scenario = GatewayScenario.CreateTaskSuggestions(allowSyntheticChatSend: true);
        var tasks = scenario.TaskSuggestions!;
        tasks.TaskSessionKey = GatewayScenario.EmptySessionKey;
        tasks.PrimaryAvailable = state is "at-mount" or "pending" or "question" or "repository-boundaries" or "cloud-boundaries" or "question-collapsed-focus" or "question-terminal-focus";
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: temp.Combine("identity"), ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SessionsUpdated += (_, rows) => { if (rows.Any(row => row.Key == GatewayScenario.EmptySessionKey)) ready.TrySetResult(); };
        await client.ConnectAsync(); await ready.Task.WaitAsync(TaskProofStepTimeout);
        using var bridge = new GatewayClientChatBridge(client);
        var custody = new ChatTaskSuggestionCustody(); custody.SelectGateway("synthetic-welcome-gateway");
        await using var source = new OpenClawChatDataProvider(bridge, null, temp.Combine("tools.json"), temp.Combine("attachments.json"),
            temp.Combine("last.json"), taskCustody: custody, taskGatewayId: "synthetic-welcome-gateway");
        await source.LoadHistoryAsync(GatewayScenario.EmptySessionKey);
        Assert.Empty((await source.LoadAsync()).Timelines[GatewayScenario.EmptySessionKey].Entries);
        try
        {
            await WithChatAsync(480, async (surface, _, session, _) =>
            {
                nint handle = 0;
                await ui.RunOnUIAsync(() =>
                {
                    surface.Height = height; var scale = surface.XamlRoot.RasterizationScale;
                    _captureWindow!.AppWindow.ResizeClient(new((int)Math.Ceiling(480 * scale), (int)Math.Ceiling(height * scale)));
                    handle = WinRT.Interop.WindowNative.GetWindowHandle(_captureWindow);
                    FindControl<TextBox>(surface, "ChatComposerInput").Text = "Retain the welcome draft exactly.";
                });
                await NativeKeyboardProof.ActivateAsync(handle);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<FrameworkElement>(surface, "ChatWelcomeSecondary") is { IsLoaded: true, ActualHeight: > 0 }, "authoritative empty-history welcome settled"));
                await source.TaskSuggestions!.RefreshAsync();
                await ui.RunOnUIAsync(() => OnboardingNativeProof.NextCompositionAsync());
                await ReviewCaptureAsync(surface, handle, $"review-r5-{height}-{state}-before");
                if (height == 620 && state == "late")
                    await ui.RunOnUIAsync(() =>
                    {
                        var secondary = FindControl<FrameworkElement>(surface, "ChatWelcomeSecondary");
                        var content = Ancestors(secondary).OfType<Grid>().First();
                        Assert.Equal(VerticalAlignment.Center, content.VerticalAlignment);
                        var scroll = Ancestors(content).OfType<ScrollViewer>().First();
                        if (content.ActualHeight < scroll.ViewportHeight - 2)
                        {
                            var block = Bounds(content, scroll);
                            var centeredMarginBox = (block.Top + block.Bottom - content.Margin.Top + content.Margin.Bottom) / 2;
                            Assert.InRange(centeredMarginBox, scroll.ViewportHeight / 2 - 2, scroll.ViewportHeight / 2 + 2);
                        }
                    });
                if (state.EndsWith("-focus", StringComparison.Ordinal))
                {
                    await ProveQuestionComposerFloorFocusAsync(surface, handle, source, tasks, state == "question-terminal-focus");
                    return;
                }
                if (state.EndsWith("-boundaries", StringComparison.Ordinal))
                {
                    await ProveRealRootTaskFormBoundariesAsync(surface, handle, source, tasks, state.StartsWith("repository", StringComparison.Ordinal));
                    return;
                }
                if (state == "identity-late")
                {
                    server.ForbidProfileRead(); await server.PublishSelfProfileChangedAsync();
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => source.TaskSuggestions.Snapshot().IdentityUnavailable, "late unverified identity notice published"));
                    await ui.RunOnUIAsync(() => OnboardingNativeProof.NextCompositionAsync());
                    await ReviewCaptureAsync(surface, handle, $"review-r5-{height}-{state}-notice");
                    await ui.RunOnUIAsync(() =>
                    {
                        var panel = FindControl<FrameworkElement>(surface, "ChatTaskSuggestions");
                        Assert.True(panel.ActualHeight >= 80, "Late welcome identity notice cannot self-collapse");
                    });
                    await ReachWelcomeActionAsync(surface, handle, "Refresh tasks", activate: false);
                    await AssertWelcomeComposerVisibleAsync(surface);
                    return;
                }
                if (!tasks.PrimaryAvailable) { tasks.PrimaryAvailable = true; await source.TaskSuggestions.RefreshAsync(); }
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<Border>(surface, "ChatTaskSuggestion_" + FixtureTaskSuggestions.TaskId) is { IsLoaded: true }, "late or initial task mounted on welcome"));
                await ui.RunOnUIAsync(() => OnboardingNativeProof.NextCompositionAsync());
                await ReviewCaptureAsync(surface, handle, $"review-r5-{height}-{state}-card");
                await ui.RunOnUIAsync(() => Assert.True(TaskCard(surface).ActualHeight >= 80, "Welcome card must retain at least header/action-sized usable geometry"));
                await AssertWelcomeComposerVisibleAsync(surface);
                await ReachWelcomeActionAsync(surface, handle, "Start in a new session", activate: false);
                await ReachWelcomeActionAsync(surface, handle, "Choose where to start the task", activate: false);
                await ReachWelcomeActionAsync(surface, handle, "Show instructions", activate: true);
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindControl<ToggleButton>(surface, "ChatTaskInstructions").IsChecked == true, "welcome disclosure opened"));
                TextBlock? prompt = null;
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                {
                    prompt = FindDescendants<TextBlock>(surface).SingleOrDefault(text => text.Text == FixtureTaskSuggestions.Prompt && text.Visibility == Visibility.Visible);
                    if (prompt is not { IsLoaded: true, ActualHeight: > 0 }) return false;
                    prompt.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                    return true;
                }, "expanded prompt publication after native toggle state"));
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                {
                    var view = Bounds(FindControl<FrameworkElement>(surface, "ChatTaskSuggestions"), surface);
                    var text = Bounds(prompt!, surface);
                    return prompt!.ActualHeight > 0 && text.Bottom > view.Top && text.Top < view.Bottom - 10;
                }, "expanded prompt is revealable in welcome card"));
                await ReviewCaptureAsync(surface, handle, $"review-r5-{height}-{state}-expanded");
                await ReachWelcomeActionAsync(surface, handle, "Start in a new session", activate: false);
                await ReachWelcomeActionAsync(surface, handle, "Choose where to start the task", activate: false);
                if (state == "reappear")
                {
                    tasks.PrimaryAvailable = false; await source.TaskSuggestions.RefreshAsync();
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<FrameworkElement>(surface, "ChatTaskSuggestions") is null, "welcome task removed on same root"));
                    await AssertWelcomeComposerVisibleAsync(surface);
                    tasks.PrimaryAvailable = true; await source.TaskSuggestions.RefreshAsync();
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<Border>(surface, "ChatTaskSuggestion_" + FixtureTaskSuggestions.TaskId) is { IsLoaded: true, ActualHeight: >= 80 }, "welcome card reappears after reserved space grows"));
                    await ReachWelcomeActionAsync(surface, handle, "Start in a new session", activate: false);
                    await ReviewCaptureAsync(surface, handle, $"review-r5-{height}-{state}-returned");
                }
                if (state == "pending")
                {
                    await source.SendMessageAsync(GatewayScenario.EmptySessionKey, "Synthetic active run for pending-row proof.");
                    for (var index = 0; index < 10; index++) await source.SendMessageAsync(GatewayScenario.EmptySessionKey, "Synthetic queued follow-up " + index);
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<FrameworkElement>(surface, "ChatPendingMessages") is { ActualHeight: > 110 }, "pending queue reaches its 120-DIP body cap"));
                    Assert.Contains(source.TaskSuggestions.Snapshot().Cards, card => card.Suggestion.Id == FixtureTaskSuggestions.TaskId);
                    await VerifyTaskShortageAsync(surface, handle, state);
                    var snapshot = await source.LoadAsync();
                    foreach (var message in snapshot.QueuedMessagesByThread![GatewayScenario.EmptySessionKey]) await source.CancelQueuedMessageAsync(GatewayScenario.EmptySessionKey, message.Id);
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<FrameworkElement>(surface, "ChatPendingMessages") is null, "pending rows removed"));
                    await VerifyTaskRestoredAsync(surface, handle, state);
                }
                if (state == "question")
                {
                    await ReachWelcomeActionAsync(surface, handle, "Start in a new session", activate: false);
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    tasks.Questions = [new { id = "fixture-space-question", sessionKey = GatewayScenario.EmptySessionKey, agentId = "main", runId = "fixture-question-run", createdAtMs = now, expiresAtMs = now + 600000, status = "pending",
                        questions = new[] { new { questionId = "choice", header = "Choose a synthetic route", question = "Keep the task and composer reachable while answering this representative question.", options = new[] { new { label = "First route", value = "first" }, new { label = "Second route", value = "second" } }, allowOther = false } } }];
                    await source.Questions!.RefreshAsync();
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<FrameworkElement>(surface, "ChatQuestionPanel") is { IsLoaded: true, ActualHeight: > 0 }, "representative real question row mounted"));
                    Assert.Contains(source.TaskSuggestions.Snapshot().Cards, card => card.Suggestion.Id == FixtureTaskSuggestions.TaskId);
                    await VerifyTaskShortageAsync(surface, handle, state);
                    Button? collapseAction = null;
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                    {
                        collapseAction = FindOptional<Button>(surface, "ChatQuestionCollapse_fixture-space-question");
                        if (collapseAction is not { IsLoaded: true, IsEnabled: true, ActualHeight: > 0 }) return false;
                        collapseAction.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                        return collapseAction.Focus(FocusState.Keyboard);
                    }, "native question collapse ready after shortage reconciliation"));
                    await ui.RunOnUIAsync(() => NativeKeyboardProof.Press(handle, VirtualKey.Space));
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindControl<Border>(surface, "ChatWritingSurface") is { IsLoaded: true, ActualHeight: > 0 }
                        && FindControl<Button>(surface, "ChatQuestionCollapse_fixture-space-question") is { } collapse && TaskActionName(collapse) == "Expand question", "collapsed question retains its row and restores writing/send surface"));
                    await AssertWelcomeComposerVisibleAsync(surface);
                    await ReachWelcomeActionAsync(surface, handle, "Start in a new session", activate: false);
                    await ReachWelcomeActionAsync(surface, handle, "Choose where to start the task", activate: false);
                    await ReviewCaptureAsync(surface, handle, "review-r5-360-question-collapsed-send-visible");
                    tasks.Questions = []; await server.PublishTaskProofQuestionResolvedAsync("fixture-space-question"); await source.Questions.RefreshAsync();
                    await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<FrameworkElement>(surface, "ChatQuestionPanel") is null, "question row removed"));
                    await VerifyTaskRestoredAsync(surface, handle, state);
                }
                await AssertWelcomeComposerVisibleAsync(surface);
                await ui.RunOnUIAsync(() => Assert.Equal("Retain the welcome draft exactly.", FindControl<TextBox>(surface, "ChatComposerInput").Text));
                Assert.Empty(tasks.Acceptances);
            }, renderProvider: source, initialThreadId: GatewayScenario.EmptySessionKey, interactiveKeyboard: true);
        }
        finally { await client.DisconnectAsync(); }
    }

    private async Task VerifyTaskShortageAsync(Border surface, nint handle, string state)
    {
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
        {
            var composer = FindOptional<FrameworkElement>(surface, "ChatComposerArea");
            return composer is not null && (composer.Visibility != Visibility.Visible || Bounds(composer, surface).Bottom <= surface.ActualHeight + 1);
        }, "reserved-row geometry publishes the shortage budget"));
        await ReviewCaptureAsync(surface, handle, "review-r5-360-" + state + "-shortage");
        await AssertWelcomeComposerVisibleAsync(surface, requireWritingSurface: state != "question");
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
        {
            var panel = FindOptional<FrameworkElement>(surface, "ChatTaskSuggestions");
            if (state == "question") return panel is null;
            return panel is { IsLoaded: true } && FindOptional<ScrollViewer>(surface, "ChatTaskWholeCardViewport") is { VerticalScrollMode: ScrollMode.Enabled };
        }, state == "question" ? "expanded question pins task absence" : "capped pending queue pins usable whole-card shortage"));
        if (state == "question")
        {
            await ui.RunOnUIAsync(() =>
            {
                Assert.DoesNotContain(FindDescendants<Control>(surface), control => AutomationProperties.GetAutomationId(control).StartsWith("ChatTask", StringComparison.Ordinal) && control.IsTabStop);
                var question = FindControl<FrameworkElement>(surface, "ChatQuestionPanel");
                Assert.IsAssignableFrom<Control>(FocusManager.GetFocusedElement(surface.XamlRoot));
                Assert.Contains((DependencyObject)FocusManager.GetFocusedElement(surface.XamlRoot), FindDescendants<DependencyObject>(question));
                NativeKeyboardProof.Press(handle, VirtualKey.Tab);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FocusManager.GetFocusedElement(surface.XamlRoot) is Control focus
                && Ancestors(focus).Contains(FindControl<FrameworkElement>(surface, "ChatQuestionPanel")), "next Tab remains a meaningful question control"));
            await ReviewCaptureAsync(surface, handle, "review-r6-question-floor-focus-and-next-tab");
        }
        else
        {
            var expanded = await ui.RunOnUIAsync(() => Task.FromResult(FindControl<ToggleButton>(surface, "ChatTaskInstructions").IsChecked == true));
            if (expanded) await ReachWelcomeActionAsync(surface, handle, "Hide instructions", activate: true);
            await ReachWelcomeActionAsync(surface, handle, "Show instructions", activate: true);
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindControl<ToggleButton>(surface, "ChatTaskInstructions").IsChecked == true
                && FindDescendants<TextBlock>(surface).Any(t => t.Text == FixtureTaskSuggestions.Prompt && t.ActualHeight > 0), "pending shortage disclosure published"));
            await ReachWelcomeActionAsync(surface, handle, "Start in a new session", activate: false);
            await ReachWelcomeActionAsync(surface, handle, "Choose where to start the task", activate: false);
        }
    }
    private async Task VerifyTaskRestoredAsync(Border surface, nint handle, string state)
    {
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => FindOptional<Border>(surface, "ChatTaskSuggestion_" + FixtureTaskSuggestions.TaskId) is { IsLoaded: true, ActualHeight: >= 80 }, "shortage clears and task returns intact"));
        await ReachWelcomeActionAsync(surface, handle, "Start in a new session", activate: false);
        await ReachWelcomeActionAsync(surface, handle, "Choose where to start the task", activate: false);
        await AssertWelcomeComposerVisibleAsync(surface);
        await ReviewCaptureAsync(surface, handle, "review-r5-360-" + state + "-restored");
    }
    private Task AssertWelcomeComposerVisibleAsync(Border surface, bool requireWritingSurface = true) => ui.RunOnUIAsync(() =>
    {
        var composer = FindControl<FrameworkElement>(surface, "ChatComposerArea");
        var bounds = Bounds(composer, surface);
        if (composer.Visibility == Visibility.Visible)
            Assert.True(bounds.Bottom <= surface.ActualHeight + 1, $"Composer allocation must fit: {bounds}; surface={surface.ActualHeight}");
        else
        {
            Assert.False(requireWritingSurface, "Only the intentionally expanded question may replace the composer");
            var question = Bounds(FindControl<FrameworkElement>(surface, "ChatQuestionPanel"), surface);
            Assert.True(question.Bottom <= surface.ActualHeight + 1, "Visible expanded question fits the surface");
        }
        if (requireWritingSurface)
        {
            var writing = Bounds(FindControl<Border>(surface, "ChatWritingSurface"), surface);
            var send = Bounds(FindControl<Button>(surface, "ChatComposerPrimaryAction"), surface);
            Assert.True(writing.Bottom <= surface.ActualHeight + 1 && send.Bottom <= surface.ActualHeight + 1 && send.Top >= 0,
                $"Retained writing/send row must fit: writing={writing}; send={send}; surface={surface.ActualHeight}");
        }
        var panel = FindOptional<FrameworkElement>(surface, "ChatTaskSuggestions");
        if (panel is not null && composer.Visibility == Visibility.Visible) Assert.True(Bounds(panel, surface).Bottom <= bounds.Top + 1, "Card/notice must stay above composer allocation");
    });
    private async Task<bool> ReachWelcomeActionAsync(Border surface, nint handle, string label, bool activate, bool allowAbsent = false)
    {
        ButtonBase? action = null;
        bool Absent()
        {
            if (!allowAbsent || FindOptional<FrameworkElement>(surface, "ChatTaskSuggestions") is not null) return false;
            Assert.DoesNotContain(FindDescendants<Control>(surface), control => AutomationProperties.GetAutomationId(control).StartsWith("ChatTask", StringComparison.Ordinal) && control.IsTabStop);
            return true;
        }
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
        {
            if (Absent()) { action = null; return true; }
            action = FindDescendants<ButtonBase>(surface).SingleOrDefault(button => TaskActionName(button) == label && button.Visibility == Visibility.Visible);
            if (action is not { IsLoaded: true, IsEnabled: true, ActualHeight: > 0 }) return false;
            action.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            return action.Focus(FocusState.Keyboard);
        }, label + " usable in welcome/shortage"));
        if (action is null) return false;
        await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
        {
            if (Absent()) { action = null; return true; }
            var view = Bounds(FindControl<FrameworkElement>(surface, "ChatTaskSuggestions"), surface);
            var bounds = Bounds(action!, surface);
            return bounds.Top >= view.Top - 1 && bounds.Bottom <= view.Bottom + 1
                && ReferenceEquals(action, FocusManager.GetFocusedElement(surface.XamlRoot));
        }, label + " focus-driven task viewport reveal"));
        if (action is null) return false;
        if (activate) await ui.RunOnUIAsync(() => { if (!Absent()) NativeKeyboardProof.Press(handle, action is ToggleButton ? VirtualKey.Space : VirtualKey.Enter); });
        return true;
    }
}
