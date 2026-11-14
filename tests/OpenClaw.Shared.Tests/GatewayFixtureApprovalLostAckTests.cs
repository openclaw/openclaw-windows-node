// Verifies the fixture fault is after acceptance at the response transport boundary, not a product-state injection.
// Run GatewayFixtureProtocolTests.ApprovalLostAckFixture_*; the real client's existing timeout bounds the case.
using OpenClaw.TestSupport.Gateway;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayFixtureProtocolTests
{
    [Fact]
    public async Task ApprovalLostAckFixture_DropsOnlyAcceptedReplyAndKeepsOneShotAdmission()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateApprovalReview(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        Assert.Throws<InvalidOperationException>(server.DropInboxApprovalAcknowledgement);
        await server.PublishExecApprovalRequestedAsync(GatewayScenario.MainSessionKey,
            FixtureGatewayServer.InboxLostAckApprovalId, "echo fixture-lost-ack");
        server.DropInboxApprovalAcknowledgement();
        Assert.Throws<InvalidOperationException>(server.DropInboxApprovalAcknowledgement);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connected.Client.SendWizardRequestAsync(
            "exec.approval.resolve", new { id = FixtureGatewayServer.InboxLostAckApprovalId, decision = "invalid" }));

        var resolving = connected.Client.ResolveExecApprovalAsync(FixtureGatewayServer.InboxLostAckApprovalId, "allow-once");
        var receipt = await server.WaitForCompletedRequestAsync("exec.approval.resolve", occurrence: 2).WaitAsync(Deadline);
        Assert.Equal("accepted:ack-dropped", receipt.Outcome);
        Assert.Equal("fixture-inbox-lost-ack", receipt.ApprovalId);
        Assert.Equal("allow-once", receipt.Decision);
        await Assert.ThrowsAsync<TimeoutException>(() => resolving).WaitAsync(Deadline);
        Assert.True(connected.Client.IsConnectedToGateway);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connected.Client.ResolveExecApprovalAsync(FixtureGatewayServer.InboxLostAckApprovalId, "deny"));

        await server.PublishExecApprovalRequestedAsync(GatewayScenario.MainSessionKey, "fixture-after-lost-ack", "echo normal");
        await connected.Client.ResolveExecApprovalAsync("fixture-after-lost-ack", "deny");
        await server.WaitForCompletedRequestAsync("exec.approval.resolve", occurrence: 4).WaitAsync(Deadline);
        Assert.Equal(["error:INVALID_PARAMS", "accepted:ack-dropped", "error:INVALID_PARAMS", "ok"],
            server.Requests.Where(request => request.Method == "exec.approval.resolve").Select(request => request.Outcome));
        Assert.Equal(["operator.read", "operator.approvals"], connected.Client.GrantedOperatorScopes);
        Assert.Empty(server.UnexpectedRequests);
    }
}
