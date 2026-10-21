using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task InteractiveQuestionKeepsComposerAndAcknowledgesNativeAnswer()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.EmptyTitle, GatewayScenario.EmptySessionKey);
            await WaitUiAsync(run, () =>
            {
                var input = FindById(run, "ChatComposerInput");
                if (input is null || string.IsNullOrEmpty(input.Current.ItemStatus)
                    || FindById(run, "ChatWelcomeAgent") is null)
                    return false;
                using var render = System.Text.Json.JsonDocument.Parse(input.Current.ItemStatus);
                return render.RootElement.GetProperty("selectedThreadId").GetString() == GatewayScenario.EmptySessionKey
                    && RenderConsumedHistory(run, GatewayScenario.EmptySessionKey);
            }, "mounted empty-session composer after navigation and history delivery");
            var composerId = FindById(run, "ChatComposerInput")!.GetRuntimeId();
            ((System.Windows.Automation.ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(
                System.Windows.Automation.ValuePattern.Pattern)).SetValue("Keep this unsent draft.");
            run.Gateway.Interactive!.Playback.Select("question-choice");
            await run.InvokeAsync("app.chat.send", new
            {
                threadId = GatewayScenario.EmptySessionKey,
                message = "Synthetic native question proof.",
            });
            await WaitUiAsync(run, () => FindById(run, "ChatQuestionOption_fixture_choice_0") is not null,
                "native question option");
            await WaitUiAsync(run, () => FindById(run, "ChatRunProgress")?.Current.Name
                .Contains("Waiting for your answer", StringComparison.Ordinal) == true,
                "visible waiting-for-answer progress");
            var option = FindById(run, "ChatQuestionOption_fixture_choice_0")!;
            Assert.False(option.Current.IsOffscreen);
            Assert.True(FindById(run, "ChatComposerInput") is null or { Current.IsOffscreen: true });
            Invoke(FindButton(run, "Collapse question")!);
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is { Current.IsOffscreen: false },
                "retained composer revealed by collapsing the question");
            Assert.Equal(composerId, FindById(run, "ChatComposerInput")!.GetRuntimeId());
            Assert.Equal("Keep this unsent draft.", ((System.Windows.Automation.ValuePattern)FindById(run, "ChatComposerInput")!
                .GetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern)).Current.Value);
            Invoke(FindButton(run, "Expand question")!);
            await WaitUiAsync(run, () => FindById(run, "ChatQuestionOption_fixture_choice_0") is { Current.IsOffscreen: false },
                "expanded native question");
            option = FindById(run, "ChatQuestionOption_fixture_choice_0")!;
            Invoke(option);
            await WaitUiAsync(run, () => FindButton(run, "Submit answer")?.Current.IsEnabled == true,
                "enabled native answer submission");
            await CaptureIfRequestedAsync(run, "interactive-native-question.png");
            Invoke(FindButton(run, "Submit answer")!);
            await run.Gateway.WaitForCompletedRequestAsync("question.resolve");
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, "Your answer was accepted."),
                "continued answer after native submission");
            await WaitUiAsync(run, () => FindById(run, "ChatRunProgress") is null,
                "completed native run indicator dismissed");
            await WaitUiAsync(run, () => FindById(run, "ChatQuestionPanel") is null
                && FindById(run, "ChatComposerInput") is { Current.IsOffscreen: false },
                "answered question dismissed and retained composer revealed");
            Assert.Null(FindText(run, "Question answered"));
            Assert.Equal(composerId, FindById(run, "ChatComposerInput")!.GetRuntimeId());
            Assert.Equal("Keep this unsent draft.", ((System.Windows.Automation.ValuePattern)FindById(run, "ChatComposerInput")!
                .GetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern)).Current.Value);
            await CaptureIfRequestedAsync(run, "interactive-native-question-answered.png");
        }, scenario: GatewayScenario.CreateInteractive());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task InteractiveActiveSessionForkShowsOnlyCompletedHistory()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            var key = GatewayScenario.OtherSessionKey;
            await SelectSessionAsync(run, GatewayScenario.OtherTitle, key);
            run.Gateway.Interactive!.Playback.Pause();
            const string pending = "Unfinished interactive fork proof";
            await run.InvokeAsync("app.chat.send", new { threadId = key, message = pending });
            var nextList = run.Gateway.Requests.Count(request => request.Method == "sessions.list") + 1;
            run.Gateway.Interactive.Playback.Step();
            await run.Gateway.WaitForCompletedRequestAsync("sessions.list", occurrence: nextList);
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, pending), "active parent turn");
            await OpenSessionMenuAsync(run, key);
            Invoke(FindById(run, "WorkspaceSessionMenuFork")!);
            await run.Gateway.WaitForCompletedRequestAsync("sessions.create");
            await WaitUiAsync(run, () => SessionSelected(run, "Fixture: new conversation"), "selected fork");
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.OtherSentinel), "completed history in fork");
            Assert.Null(FindText(run, pending));
            await CaptureIfRequestedAsync(run, "interactive-active-fork.png");
        }, scenario: GatewayScenario.CreateInteractive());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task InteractiveChatShowsStreamedAnswerAndGatewayApproval()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.EmptyTitle, GatewayScenario.EmptySessionKey);
            await run.InvokeAsync("app.chat.send", new { threadId = GatewayScenario.EmptySessionKey, message = "Synthetic interactive UI proof." });
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, "FIXTURE INTERACTIVE COMPLETE"), "interactive streamed final answer");
            await CaptureIfRequestedAsync(run, "interactive-chat-final.png");
            run.Gateway.Interactive!.Playback.Select("approval");
            await run.InvokeAsync("app.chat.send", new { threadId = GatewayScenario.EmptySessionKey, message = "Synthetic approval UI proof." });
            await WaitUiAsync(run, () => FindButton(run, "Allow once") is not null, "interactive approval prompt");
            await CaptureIfRequestedAsync(run, "interactive-chat-approval.png");
            Invoke(FindButton(run, "Allow once")!);
            await run.Gateway.WaitForCompletedRequestAsync("exec.approval.resolve");
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, "Approval decision: allow-once."), "answer after approval");
            await CaptureIfRequestedAsync(run, "interactive-chat-after-approval.png");
        }, scenario: GatewayScenario.CreateInteractive());
    }
}
