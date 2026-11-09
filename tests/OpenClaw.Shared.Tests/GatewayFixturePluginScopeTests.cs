// Protects the optional plugin scope transport input, not product disclosure/reviewability policy.
// Run GatewayFixtureProtocolTests.PluginScopeFixture_* beside the existing PluginApprovalFixture_* guards.
using System.Text.Json;
using OpenClaw.TestSupport.Gateway;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayFixtureProtocolTests
{
    [Fact]
    public async Task PluginScopeFixture_RealClientPreservesSupportedScopesAndUsesPluginResolve()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateApprovalReview(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        Assert.Equal(["operator.read", "operator.approvals"], connected.Client.GrantedOperatorScopes);
        var scopes = new[]
        {
            JsonSerializer.SerializeToElement(new { kind = "standing-grant", automation = "nightly", command = "echo distinct scope tail", expiresInDays = 3 }),
            JsonSerializer.SerializeToElement(new { kind = "external-post", target = "newsroom", visibility = "public" }),
            JsonSerializer.SerializeToElement(new { kind = "external-post", target = "newsroom", visibility = "restricted" }),
            JsonSerializer.SerializeToElement(new { kind = "message-send", target = "fixture mailroom", recipientCount = 2,
                recipients = new[] { "first-reader@example.test", "last-reader@example.test" }, audience = "internal" }),
            JsonSerializer.SerializeToElement(new { kind = "message-send", target = "fixture mailroom", recipientCount = 2,
                recipients = new[] { "first-reader@example.test", "last-reader@example.test" }, audience = "external" }),
        };
        for (var index = 0; index < scopes.Length; index++)
        {
            var id = $"fixture-inbox-plugin-scope-client-{index}";
            var observed = Signal<GatewayPendingApproval>();
            void OnApproval(object? sender, GatewayPendingApproval approval)
            {
                if (approval.Id == id) observed.TrySetResult(approval);
            }
            connected.Client.PendingApprovalRequested += OnApproval;
            try
            {
                await server.PublishPluginApprovalRequestedAsync(id, "Fixture scope review", "Short plugin detail.", scope: scopes[index]);
                var approval = await observed.Task.WaitAsync(Deadline);
                Assert.Equal(GatewayApprovalKind.Plugin, approval.Kind);
                Assert.Equal("Fixture scope review", approval.Title);
                Assert.Equal("Fixture scope review", approval.Command); // Plugin command is its title, not the scoped operation.
                Assert.Equal("Review the complete fixture scope before deciding.", approval.Description);
                Assert.Equal("Short plugin detail.", approval.Detail);
                Assert.Equal(GatewayScenario.MainSessionKey, approval.SessionKey);
                Assert.Equal(["allow-once", "deny"], approval.AllowedDecisions);
                Assert.True(approval.ExpiresAtMs > approval.CreatedAtMs);
                Assert.NotNull(approval.Scope);
                if (index == 0)
                {
                    Assert.Equal(GatewayApprovalScopeKind.StandingGrant, approval.Scope.Kind);
                    Assert.Equal("nightly", approval.Scope.Automation);
                    Assert.Equal("echo distinct scope tail", approval.Scope.Command);
                    Assert.Equal(3, approval.Scope.ExpiresInDays);
                }
                else if (index < 3)
                {
                    Assert.Equal(GatewayApprovalScopeKind.ExternalPost, approval.Scope.Kind);
                    Assert.Equal("newsroom", approval.Scope.Target);
                    Assert.Equal(index == 1 ? "public" : "restricted", approval.Scope.Visibility);
                }
                else
                {
                    // Fails at the intended parser guard until ASTRA-S1 is integrated, not at a
                    // reference to a new model member. Mounted proof reads both complete recipients
                    // and exact audience from actual selectable text rather than a projection here.
                    Assert.Equal(GatewayApprovalScopeKind.MessageSend, approval.Scope.Kind);
                    Assert.Equal("fixture mailroom", approval.Scope.Target);
                    Assert.Equal(2, approval.Scope.RecipientCount);
                }
                await connected.Client.ResolveApprovalAsync(GatewayApprovalKind.Plugin, id, "allow-once",
                    connected.Client.SessionMutationConnectionEpoch!.Value);
                await server.WaitForCompletedRequestAsync("plugin.approval.resolve", occurrence: index + 1).WaitAsync(Deadline);
                var wire = Assert.Single(server.Requests, r => r.Method == "plugin.approval.resolve" && r.ApprovalId == id);
                Assert.Equal("allow-once", wire.Decision);
                Assert.Equal("ok", wire.Outcome);
            }
            finally { connected.Client.PendingApprovalRequested -= OnApproval; }
        }
        Assert.DoesNotContain(server.Requests, r => r.Method is "exec.approval.resolve" or "approval.resolve");
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task PluginScopeFixture_PreservesLegacyWireAndDoesNotSupplyTheProductReviewabilityResult()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateApprovalReview(), token);
        await using var peer = await FixturePeer.ConnectAsync(server, token);
        await server.HandshakeCompleted.WaitAsync(Deadline);
        const string secret = "s3cret42";
        // Defense-in-depth wire inputs, not claims that the sanitized/schema-checked producer
        // emits raw secrets, oversized text or malformed scope. The fixture must not fix them.
        var scopes = new JsonElement?[]
        {
            null,
            JsonSerializer.SerializeToElement(new { kind = "standing-grant", automation = "nightly", command = "Authorization: Bearer " + secret }),
            JsonSerializer.SerializeToElement(new { kind = "standing-grant", automation = "nightly", command = new string('x', 256 * 1024 + 1) }),
            JsonSerializer.SerializeToElement(new { kind = "external-post", target = "newsroom", visibility = "public", unexpected = secret }),
        };
        for (var index = 0; index < scopes.Length; index++)
        {
            var id = $"fixture-inbox-plugin-scope-wire-{index}";
            await server.PublishPluginApprovalRequestedAsync(id, "Fixture wire review", "Short plugin detail.", scope: scopes[index]);
            var frame = (await peer.ThroughAsync(e => e.GetProperty("event").GetString() == "plugin.approval.requested"))[^1];
            Assert.Equal(id, frame.GetProperty("payload").GetProperty("id").GetString());
            var request = frame.GetProperty("payload").GetProperty("request");
            Assert.Equal("Fixture wire review", request.GetProperty("title").GetString());
            Assert.Equal("Short plugin detail.", request.GetProperty("detail").GetString());
            Assert.Equal(["allow-once", "deny"], request.GetProperty("allowedDecisions").EnumerateArray().Select(value => value.GetString()));
            Assert.False(request.TryGetProperty("command", out _));
            if (scopes[index] is { } scope)
            {
                Assert.Equal(scope.GetRawText(), request.GetProperty("scope").GetRawText());
                Assert.Equal("Review the complete fixture scope before deciding.", request.GetProperty("description").GetString());
                Assert.Equal(["allowedDecisions", "description", "detail", "scope", "sessionKey", "title"],
                    request.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            }
            else
            {
                // Existing no-scope callers retain their exact property set and text.
                Assert.Equal(["allowedDecisions", "detail", "sessionKey", "title", "warningText"],
                    request.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            }
            // Only production may remove Allow. Accepting these raw-wire decisions proves the
            // fixture cannot make a reverted product's mounted Deny-only test pass for it.
            await peer.RequestAsync("plugin.approval.resolve", new { id, decision = "allow-once" });
        }
        await server.WaitForCompletedRequestAsync("plugin.approval.resolve", occurrence: scopes.Length).WaitAsync(Deadline);
        Assert.Equal(4, server.Requests.Count(r => r.Method == "plugin.approval.resolve" && r.Outcome == "ok"));
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(server.Requests));
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task PluginScopeFixture_OptionalScopeCannotBypassExistingPublicationKindOrOneShotGuards()
    {
        var token = CreateToken();
        var scope = JsonSerializer.SerializeToElement(new { kind = "external-post", target = "newsroom", visibility = "restricted" });
        await using (var browse = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token))
        {
            await using var connected = await ConnectedClient.OpenAsync(browse, token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => browse.PublishPluginApprovalRequestedAsync(
                "fixture-inbox-plugin-scope-browse", "Fixture scope", "Short detail.", scope: scope));
            Assert.Equal(["operator.read"], connected.Client.GrantedOperatorScopes);
            Assert.Empty(browse.UnexpectedRequests);
        }
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateApprovalReview(), token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.PublishPluginApprovalRequestedAsync(
            "fixture-inbox-plugin-scope-undelivered", "Fixture scope", "Short detail.", scope: scope));
        await using var review = await ConnectedClient.OpenAsync(server, token);
        const string id = "fixture-inbox-plugin-scope-guarded";
        await server.PublishPluginApprovalRequestedAsync(id, "Fixture scope", "Short detail.", scope: scope);
        await Assert.ThrowsAsync<InvalidOperationException>(() => review.Client.SendWizardRequestAsync(
            "exec.approval.resolve", new { id, decision = "deny" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => review.Client.SendWizardRequestAsync(
            "plugin.approval.resolve", new { id, decision = "allow-always" }));
        await review.Client.ResolveApprovalAsync(GatewayApprovalKind.Plugin, id, "allow-once",
            review.Client.SessionMutationConnectionEpoch!.Value);
        await Assert.ThrowsAsync<InvalidOperationException>(() => review.Client.SendWizardRequestAsync(
            "plugin.approval.resolve", new { id, decision = "deny" }));
        await server.WaitForCompletedRequestAsync("plugin.approval.resolve", occurrence: 3).WaitAsync(Deadline);
        Assert.Equal(["error:INVALID_PARAMS", "ok", "error:INVALID_PARAMS"],
            server.Requests.Where(r => r.Method == "plugin.approval.resolve").Select(r => r.Outcome));
        Assert.Equal("error:INVALID_PARAMS", Assert.Single(server.Requests, r => r.Method == "exec.approval.resolve").Outcome);
        Assert.Empty(server.UnexpectedRequests);
    }
}
