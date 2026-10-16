using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
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
