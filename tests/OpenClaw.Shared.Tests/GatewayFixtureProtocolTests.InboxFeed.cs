using OpenClaw.TestSupport.Gateway;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayFixtureProtocolTests
{
    [Fact]
    public async Task InboxFeed_AuthenticatedListsAndTopLevelEventRemainReadOnlyAcrossReconnect()
    {
        var scenario = GatewayScenario.CreateInboxFeed(seedApproval: true);
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.PublishInboxFeedApprovalAsync());
        await using (var connected = await ConnectedClient.OpenAsync(server, token))
        {
            var client = connected.Client;
            Assert.Equal(new[] { "operator.read", "operator.approvals" }, client.GrantedOperatorScopes);
            Assert.Contains("exec.approval.list", client.AdvertisedServerMethods);
            Assert.DoesNotContain("exec.approval.resolve", client.AdvertisedServerMethods);
            Assert.DoesNotContain("cron.run", client.AdvertisedServerMethods);
            var initial = await client.SendWizardRequestAsync("exec.approval.list", new { }).WaitAsync(Deadline);
            Assert.Equal("fixture-inbox-initial", Assert.Single(initial.EnumerateArray()).GetProperty("id").GetString());

            using var deadline = new CancellationTokenSource(Deadline);
            var cron = await client.ReadSearchCatalogAsync("cron.list", new { includeDisabled = true }, deadline.Token);
            Assert.Equal(1, cron.GetProperty("total").GetInt32());
            Assert.False(cron.GetProperty("hasMore").GetBoolean());
            Assert.Equal(200, cron.GetProperty("limit").GetInt32());
            Assert.True(scenario.InboxFeedPagedReadCount > 0, "Real reader must supply bounded canonical pagination parameters.");
            Assert.Matches("^[A-F0-9]{64}$", cron.GetProperty("snapshotRevision").GetString()!);
            var sameInventory = await client.ReadSearchCatalogAsync("cron.list", new { includeDisabled = true }, deadline.Token);
            Assert.Equal(cron.GetProperty("snapshotRevision").GetString(), sameInventory.GetProperty("snapshotRevision").GetString());
            var job = Assert.Single(cron.GetProperty("jobs").EnumerateArray());
            Assert.Equal("Fixture nightly archive", job.GetProperty("name").GetString());
            Assert.Equal("error", job.GetProperty("state").GetProperty("lastRunStatus").GetString());
            var auth = await client.SendWizardRequestAsync("models.authStatus", new { }).WaitAsync(Deadline);
            var provider = Assert.Single(auth.GetProperty("providers").EnumerateArray());
            Assert.Equal("expired", provider.GetProperty("status").GetString());
            Assert.Equal("token", Assert.Single(provider.GetProperty("profiles").EnumerateArray()).GetProperty("type").GetString());

            var requested = Signal<GatewayPendingApproval>();
            client.PendingApprovalRequested += (_, approval) => requested.TrySetResult(approval);
            await server.PublishInboxFeedApprovalAsync();
            var observed = await requested.Task.WaitAsync(Deadline);
            Assert.Equal("fixture-inbox-live", observed.Id);
            Assert.Equal(GatewayApprovalKind.Exec, observed.Kind);
            Assert.Equal("curl https://example.test/run?token=fixture-secret ; echo fixture-end", observed.Command);
            Assert.All(observed.Command, character => Assert.InRange(character, ' ', '~'));
            Assert.Equal(@"C:\fixture", observed.Cwd);
            Assert.Equal(GatewayScenario.OtherSessionKey, observed.SessionKey);
            Assert.Equal(45_000, observed.ExpiresAtMs - observed.CreatedAtMs);
            Assert.True(observed.CreatedAtMs > DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds());
            var live = await client.SendWizardRequestAsync("exec.approval.list", new { }).WaitAsync(Deadline);
            Assert.Equal("fixture-inbox-live", Assert.Single(live.EnumerateArray()).GetProperty("id").GetString());
            Assert.Equal(1, Assert.Single(server.Events).DeliveredConnections);
            await Assert.ThrowsAsync<InvalidOperationException>(() => server.PublishInboxFeedApprovalAsync());

            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => client.SendWizardRequestAsync(
                "exec.approval.resolve", new { id = "fixture-inbox-live", decision = "allow-once" }));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => client.SendWizardRequestAsync(
                "cron.run", new { id = "fixture-inbox-cron" }));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => client.SendWizardRequestAsync(
                "cron.list", new { includeDisabled = true, limit = 201, offset = 0 }));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => client.SendWizardRequestAsync(
                "exec.approval.list", new { unexpected = true }));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => client.SendWizardRequestAsync(
                "chat.send", new { sessionKey = GatewayScenario.MainSessionKey, message = "not allowed", idempotencyKey = "fixture-blocked" }));
            foreach (var method in new[] { "cron.run", "exec.approval.resolve", "chat.send" })
                Assert.StartsWith("error:", (await server.WaitForCompletedRequestAsync(method, cancellationToken: deadline.Token)).Outcome);
            // Even the pre-existing agent-stream exception cannot grant this new
            // read-only scenario resolution authority over an issued fixture ID.
            await server.PublishAgentEventAsync(GatewayScenario.MainSessionKey, new
            { phase = "requested", approvalId = "fixture-inbox-legacy", command = "echo fixture-legacy" });
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => client.SendWizardRequestAsync(
                "exec.approval.resolve", new { id = "fixture-inbox-legacy", decision = "allow-once" }));
            Assert.Equal("error:FIXTURE_READ_ONLY",
                (await server.WaitForCompletedRequestAsync("exec.approval.resolve", occurrence: 2, cancellationToken: deadline.Token)).Outcome);
        }
        scenario.ReplaceInboxApprovalForReconnect();
        await using var reconnected = await ConnectedClient.OpenAsync(server, token);
        var current = await reconnected.Client.SendWizardRequestAsync("exec.approval.list", new { }).WaitAsync(Deadline);
        var row = Assert.Single(current.EnumerateArray());
        Assert.Equal("fixture-inbox-reconnected", row.GetProperty("id").GetString());
        Assert.Equal("echo fixture-reconnected", row.GetProperty("request").GetProperty("command").GetString());
        Assert.Single(server.Events); // Reconnect input changed only the read inventory.
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task InboxFeed_NativeAutomationQueriesKeepFiltersPagingAndReadOnlyAuthority()
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateInboxFeed();
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var query = new Cron.CronListQuery { AgentId = "main", Query = "nightly", Limit = 1, SortBy = Cron.CronSortBy.Name };
        var page = await client.ListCronJobsAsync(query).WaitAsync(Deadline);
        Assert.Equal("fixture-inbox-cron", Assert.Single(page.Jobs).Id);
        Assert.Empty((await client.ListCronJobsAsync(query with { AgentId = "research" }).WaitAsync(Deadline)).Jobs);
        Assert.Empty((await client.ListCronJobsAsync(query with { Enabled = Cron.CronEnabledFilter.Disabled }).WaitAsync(Deadline)).Jobs);
        Assert.Empty((await client.ListCronJobsAsync(query with { Offset = 1 }).WaitAsync(Deadline)).Jobs);
        Assert.Equal("fixture-inbox-cron", (await client.GetCronJobAsync("fixture-inbox-cron").WaitAsync(Deadline)).Id);
        Assert.Empty((await client.ListCronRunsAsync(new Cron.CronRunsQuery { JobId = "fixture-inbox-cron" }).WaitAsync(Deadline)).Entries);
        foreach (var method in new[] { "cron.add", "cron.update", "cron.remove", "cron.run" })
        {
            Assert.DoesNotContain(method, client.AdvertisedServerMethods);
            var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => client.SendWizardRequestAsync(method, new { id = "fixture-inbox-cron" }));
            Assert.Equal("FORBIDDEN", GatewayRequestErrors.Code(error));
        }
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => client.SendWizardRequestAsync("cron.list", new { unknown = true }));
        Assert.Single(scenario.GetCronJobs());
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task InboxFeed_ExplainPromptIsOptInAndRestrictedToCanonicalMainConversation()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateInboxFeed(allowExplainPrompts: true), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        const string question = "This automation failed: Fixture nightly archive. Explain why it failed and how to fix it.";
        var sent = await connected.Client.SendChatMessageForRunAsync(question, GatewayScenario.MainSessionKey).WaitAsync(Deadline);
        Assert.Equal("fixture-chat-1", sent.RunId);
        using var deadline = new CancellationTokenSource(Deadline);
        Assert.Equal("ok", (await server.WaitForCompletedRequestAsync("chat.send", cancellationToken: deadline.Token)).Outcome);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => connected.Client.SendWizardRequestAsync(
            "chat.send", new { sessionKey = GatewayScenario.OtherSessionKey, message = question, idempotencyKey = "fixture-wrong-session" }));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => connected.Client.SendWizardRequestAsync(
            "chat.send", new { sessionKey = GatewayScenario.MainSessionKey, message = "arbitrary input", idempotencyKey = "fixture-wrong-prompt" }));
        Assert.Equal("error:FIXTURE_READ_ONLY",
            (await server.WaitForCompletedRequestAsync("chat.send", occurrence: 3, cancellationToken: deadline.Token)).Outcome);
        Assert.Single(server.Requests, request => request.Method == "chat.send" && request.Outcome == "ok");
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task InboxFeed_DoesNotChangeBrowseScenarioOrUnknownMethodRejection()
    {
        var browse = GatewayScenario.CreateBrowse();
        Assert.DoesNotContain("exec.approval.list", browse.ReadMethods);
        Assert.Throws<InvalidOperationException>(browse.ReplaceInboxApprovalForReconnect);
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(browse, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        Assert.Equal(new[] { "operator.read" }, connected.Client.GrantedOperatorScopes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.PublishInboxFeedApprovalAsync());
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            connected.Client.SendWizardRequestAsync("exec.approval.list", new { }));
        using var deadline = new CancellationTokenSource(Deadline);
        Assert.Equal("error:METHOD_NOT_FOUND",
            (await server.WaitForCompletedRequestAsync("exec.approval.list", cancellationToken: deadline.Token)).Outcome);
        Assert.Equal("exec.approval.list", Assert.Single(server.UnexpectedRequests).Method);
        Assert.Empty(server.Events);
    }
}
