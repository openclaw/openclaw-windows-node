// Real-app lost-ack proof: the server consumes a canonical approval but drops its response frame.
// Run GatewayFixtureUiTests.InboxApprovalLostAck_* with the standard fixture desktop/app opt-in.
using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    private const string LostAckNote = "The decision could not be confirmed. Further decisions are blocked while its outcome is unknown.";

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task InboxApprovalLostAck_QuarantineSurvivesReopenAndRemovesChatActions()
    {
        await WithAppAsync(async run =>
        {
            const string command = "echo fixture-lost-ack";
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitHistoryAsync(run, GatewayScenario.MainSessionKey);
            await run.Gateway.PublishExecApprovalRequestedAsync(GatewayScenario.MainSessionKey,
                FixtureGatewayServer.InboxLostAckApprovalId, command);
            await WaitUiAsync(run, () => FindButton(run, "Allow once") is not null, "canonical approval in native chat");
            await OpenInboxReviewAsync(run, flyout: false);
            await ExpandInboxReviewAsync(run, command);
            await RevealInboxDecisionAsync(run, "InboxApprovalAllowOnce");
            var staleAction = (InvokePattern)FindInboxReviewAction(run, "InboxApprovalAllowOnce")!.GetCurrentPattern(InvokePattern.Pattern);
            var connects = run.Gateway.Requests.Count(request => request.Method == "connect");
            run.Gateway.DropInboxApprovalAcknowledgement();
            staleAction.Invoke();
            await run.WaitForAsync(() => Task.FromResult(run.Gateway.Requests.Any(request =>
                request.Method == "exec.approval.resolve" && request.Outcome == "accepted:ack-dropped")), "server accepted decision and dropped acknowledgement");
            var accepted = Assert.Single(run.Gateway.Requests, request => request.Method == "exec.approval.resolve");
            Assert.Equal("fixture-inbox-lost-ack", accepted.ApprovalId);
            Assert.Equal("allow-once", accepted.Decision);
            // ResolveApprovalCoreAsync has a real 15-second deadline. The existing 20-second UI
            // wait observes that timeout; no sleep, product flag or injected uncertain state is used.
            await AssertLostAckInboxAsync(run, command);
            await CaptureIfRequestedAsync(run, "inbox-lost-ack-quarantined.png");

            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindTimeline(run) is not null, "native chat timeline after lost acknowledgement");
            await WaitUiAsync(run, () =>
            {
                var timeline = FindTimeline(run)!;
                return timeline.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>()
                    .Any(text => IsVisibleInTimeline(timeline, text) &&
                        string.Join(" ", ReadInboxReviewText(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                            .Contains(LostAckNote, StringComparison.Ordinal));
            }, "shared quarantine explanation in native chat");
            foreach (var label in new[] { "Allow once", "Always allow", "Deny once" }) Assert.Null(FindButton(run, label));
            await CaptureIfRequestedAsync(run, "chat-lost-ack-quarantined.png");

            foreach (var flyout in new[] { false, true })
            {
                await OpenInboxReviewAsync(run, flyout);
                await AssertLostAckInboxAsync(run, command);
                // Attempt the pre-quarantine native action after reopening. A removed/disabled
                // UIA provider may reject it; if it dispatches, the shared owner must still refuse.
                try { staleAction.Invoke(); }
                catch (ElementNotAvailableException) { }
                catch (ElementNotEnabledException) { }
                await run.InvokeAsync("app.status", new { });
                await CaptureIfRequestedAsync(run, flyout ? "inbox-lost-ack-reopened-flyout.png" : "inbox-lost-ack-reopened-page.png");
            }
            // An ordinary completed read is a transport-liveness barrier, not an arbitrary delay.
            var reads = run.Gateway.Requests.Count(request => request.Method == "sessions.list");
            await run.Gateway.NotifySessionsChangedAsync();
            await run.WaitForAsync(() => Task.FromResult(run.Gateway.Requests.Count(request =>
                request.Method == "sessions.list" && request.Outcome == "ok") > reads), "healthy Gateway after stale action attempts");
            Assert.Single(run.Gateway.Requests, request => request.Method == "exec.approval.resolve");
            Assert.Equal(connects, run.Gateway.Requests.Count(request => request.Method == "connect"));
        }, scenario: GatewayScenario.CreateApprovalReview());
    }

    private static async Task AssertLostAckInboxAsync(GatewayFixtureRun run, string command)
    {
        AutomationElement? expander = null;
        await WaitUiAsync(run, () => (expander = FindInboxReviewExpander(run, command)) is not null, "original approval retained in Inbox");
        ((ExpandCollapsePattern)expander!.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        await WaitUiAsync(run, () => InboxReviewTextElements(run).Any(text =>
            !text.Current.IsOffscreen && ReadInboxReviewText(text) == LostAckNote), "visible uncertainty without another decision");
        foreach (var id in new[] { "InboxApprovalAllowOnce", "InboxApprovalAllowAlways", "InboxApprovalDeny" })
            Assert.Null(FindInboxReviewAction(run, id));
    }
}
