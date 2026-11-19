// Covers the new fixture transport boundary, not the product's disclosure policy.
// Run GatewayFixtureProtocolTests.PluginApprovalFixture_* alongside the mounted Inbox proofs.
using OpenClaw.TestSupport.Gateway;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayFixtureProtocolTests
{
    [Fact]
    public async Task PluginApprovalFixture_CanonicalEventAndOneShotResolveKeepTheNarrowGrant()
    {
        var scenario = GatewayScenario.CreateApprovalReview();
        Assert.Contains("plugin.approval.resolve", scenario.ReadMethods);
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var observed = new TaskCompletionSource<GatewayPendingApproval>(TaskCreationOptions.RunContinuationsAsynchronously);
        connected.Client.PendingApprovalRequested += (_, approval) => observed.TrySetResult(approval);
        const string id = "fixture-inbox-plugin-wire";
        const string detail = "Review every line.\nCRITICAL END: synthetic fixture deletion.";

        await server.PublishPluginApprovalRequestedAsync(id, "Fixture plugin operation", detail, "Fixture warning");
        var approval = await observed.Task.WaitAsync(Deadline);

        Assert.Equal(["operator.read", "operator.approvals"], connected.Client.GrantedOperatorScopes);
        Assert.Equal(GatewayApprovalKind.Plugin, approval.Kind);
        Assert.Equal(id, approval.Id);
        Assert.Equal(GatewayScenario.MainSessionKey, approval.SessionKey);
        Assert.Equal("Fixture plugin operation", approval.Title);
        Assert.Equal(detail, approval.Detail);
        Assert.Equal("Fixture warning", approval.WarningText);
        Assert.Equal(["allow-once", "deny"], approval.AllowedDecisions);
        Assert.True(approval.AllowedDecisionsSpecified);
        Assert.True(approval.ExpiresAtMs > approval.CreatedAtMs);

        // An unsupported decision must not consume the issued identity.
        await Assert.ThrowsAsync<InvalidOperationException>(() => connected.Client.SendWizardRequestAsync(
            "plugin.approval.resolve", new { id, decision = "allow-always" }));
        await connected.Client.ResolveApprovalAsync(GatewayApprovalKind.Plugin, id, "allow-once",
            connected.Client.SessionMutationConnectionEpoch!.Value);
        await server.WaitForCompletedRequestAsync("plugin.approval.resolve", occurrence: 2).WaitAsync(Deadline);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connected.Client.SendWizardRequestAsync(
            "plugin.approval.resolve", new { id, decision = "deny" }));

        var requests = server.Requests.Where(request => request.Method == "plugin.approval.resolve").ToArray();
        Assert.Equal(["error:INVALID_PARAMS", "ok", "error:INVALID_PARAMS"], requests.Select(request => request.Outcome));
        Assert.All(requests, request => Assert.Equal(id, request.ApprovalId));
        Assert.Equal("allow-once", requests[1].Decision);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task PluginApprovalFixture_RejectsUnissuedWrongKindAndUndeliveredIds()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateApprovalReview(), token);
        const string undelivered = "fixture-inbox-plugin-undelivered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.PublishPluginApprovalRequestedAsync(
            undelivered, "Fixture plugin", "Not delivered"));
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connected.Client.SendWizardRequestAsync(
            "plugin.approval.resolve", new { id = undelivered, decision = "deny" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connected.Client.SendWizardRequestAsync(
            "plugin.approval.resolve", new { id = "fixture-inbox-plugin-unissued", decision = "deny" }));

        const string execId = "fixture-inbox-plugin-wrong-kind";
        await server.PublishExecApprovalRequestedAsync(GatewayScenario.MainSessionKey, execId, "echo fixture");
        await Assert.ThrowsAsync<InvalidOperationException>(() => connected.Client.SendWizardRequestAsync(
            "plugin.approval.resolve", new { id = execId, decision = "deny" }));
        await connected.Client.ResolveExecApprovalAsync(execId, "deny");
        await server.WaitForCompletedRequestAsync("exec.approval.resolve").WaitAsync(Deadline);
        Assert.All(server.Requests.Where(request => request.Method == "plugin.approval.resolve"),
            request => Assert.Equal("error:INVALID_PARAMS", request.Outcome));
        Assert.Equal("ok", Assert.Single(server.Requests, request => request.Method == "exec.approval.resolve").Outcome);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task PluginApprovalFixture_DoesNotBroadenBrowseOrAcceptArbitraryIds()
    {
        var token = CreateToken();
        var browse = GatewayScenario.CreateBrowse();
        Assert.DoesNotContain("plugin.approval.resolve", browse.ReadMethods);
        await using (var server = await FixtureGatewayServer.StartAsync(browse, token))
        {
            await using var connected = await ConnectedClient.OpenAsync(server, token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => server.PublishPluginApprovalRequestedAsync(
                "fixture-inbox-plugin-browse", "Fixture plugin", "Not admitted"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => connected.Client.SendWizardRequestAsync(
                "plugin.approval.resolve", new { id = "fixture-inbox-plugin-browse", decision = "deny" }));
            Assert.Equal(["operator.read"], connected.Client.GrantedOperatorScopes);
            Assert.Empty(server.UnexpectedRequests);
        }

        await using var review = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateApprovalReview(), token);
        foreach (var invalid in new[] { "external-approval", "fixture-other-purpose", "fixture-inbox-plugin-" + new string('x', 65) })
            await Assert.ThrowsAsync<ArgumentException>(() => review.PublishPluginApprovalRequestedAsync(
                invalid, "Fixture plugin", "Not admitted"));
    }
}
