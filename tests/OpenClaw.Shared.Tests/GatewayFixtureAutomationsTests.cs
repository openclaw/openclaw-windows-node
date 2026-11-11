using System.Text.Json.Nodes;
using OpenClaw.Shared.Cron;
using OpenClaw.TestSupport.Gateway;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Automations over the real <see cref="OpenClawGatewayClient"/> and the synthetic
/// fixture Gateway. Nothing is scheduled or executed by the fixture.
/// </summary>
public sealed partial class GatewayFixtureProtocolTests
{
    [Fact]
    public async Task HostGlobalOverviewSurvivesPageDisposalAndScopedReadsDoNotInferMissingOwners()
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateAutomations(additionalJobs: 205);
        scenario.SetCronJobOwner("morning-brief", null);
        scenario.OmitCronEffectiveAgentId = true;
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var ui = new SingleThreadDispatcher();
        using var overview = new AutomationsOverviewSource(() => connected.Client, ui.Post);
        using var store = new AutomationsStore("main", () => connected.Client, ui.Post, overviewSource: overview);
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.OverviewLoaded && !store.State.ListLoading);
        Assert.Equal(213, store.State.OverviewJobs.Count);
        Assert.Contains(store.State.OverviewJobs, job => job.Id == "morning-brief" && job.Owner is null);
        Assert.Null(overview.State.AttentionCount);
        Assert.All(server.Requests.Where(request => request.Method == "cron.list"), request => Assert.Equal("main", request.AgentId));

        await ui.InvokeAsync(() => store.SetAgentFilter(null));
        await WaitForAsync(() => overview.State.IsCurrent && store.State.OverviewLoaded);
        Assert.Equal(214, overview.State.Jobs.Length);
        Assert.Equal(1, overview.State.AttentionCount);
        var global = overview.State;
        await ui.InvokeAsync(() => store.SetAgentFilter("ops"));
        await WaitForAsync(() => store.State.OverviewLoaded && !store.State.ListLoading);
        Assert.Equal(GatewayScenario.AutomationsOtherAgentJobId, Assert.Single(store.State.OverviewJobs).Id);
        Assert.Same(global, overview.State);
        await ui.InvokeAsync(store.Dispose);
        Assert.Equal(1, overview.State.AttentionCount);
    }

    [Fact]
    public async Task AutomationsStore_ExcludedAgentSelectionRetiresLateReadsButPreservesQueuedRun()
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateAutomations();
        scenario.QueueCronRunsOnly = true;
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var ui = new SingleThreadDispatcher();
        CancellationToken watcher = default;
        using var store = new AutomationsStore("main", () => connected.Client, ui.Post, (_, ct) =>
        {
            watcher = ct;
            return Task.Delay(Timeout.Infinite, ct);
        });
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.ListLoaded && !store.State.ListLoading);
        await ui.InvokeAsync(() => store.Select("morning-brief"));
        await WaitForAsync(() => store.State.RunsLoaded && !store.State.RunsLoading);
        await await ui.InvokeAsync(() => store.RunNowAsync("morning-brief"));
        await WaitForAsync(() => store.State.IsRunning("morning-brief") && watcher.CanBeCanceled
            && !store.State.ListLoading && server.Requests.All(request => request.Outcome != "pending"));

        server.HoldResponses("cron.get");
        server.HoldResponses("cron.runs");
        await ui.InvokeAsync(() => { _ = store.LoadSelectedAsync("morning-brief"); _ = store.LoadRunsAsync(); });
        await WaitForAsync(() => server.Requests.Any(request => request.Method == "cron.get" && request.Outcome == "pending")
            && server.Requests.Any(request => request.Method == "cron.runs" && request.Outcome == "pending"));
        await ui.InvokeAsync(() => store.SetAgentFilter("ops"));
        Assert.Null(store.State.SelectedJobId);
        Assert.Null(store.State.SelectedJob);
        Assert.Empty(store.State.Runs);
        Assert.Null(store.State.RunsQuery.JobId);
        Assert.Equal("ops", store.State.RunsQuery.AgentId);
        Assert.True(store.State.IsRunning("morning-brief"));
        Assert.False(watcher.IsCancellationRequested);

        server.ReleaseResponses("cron.get");
        server.ReleaseResponses("cron.runs");
        await WaitForAsync(() => store.State.ListLoaded && !store.State.ListLoading
            && server.Requests.All(request => request.Outcome != "pending"));
        await ui.InvokeAsync(() => { });
        Assert.Null(store.State.SelectedJobId);
        Assert.Empty(store.State.Runs);
        var priorReads = server.Requests.Count(request => request.Method is "cron.get" or "cron.runs");
        await ui.InvokeAsync(() => { store.Refresh(); store.OnGatewayCronEvent(); });
        await WaitForAsync(() => !store.State.ListLoading && server.Requests.All(request => request.Outcome != "pending"));
        Assert.Equal(priorReads, server.Requests.Count(request => request.Method is "cron.get" or "cron.runs"));
        Assert.True(store.State.IsRunning("morning-brief"));
        Assert.False(watcher.IsCancellationRequested);
    }

    [Fact]
    public async Task AutomationsStore_ExactDefinitionRetainsTheListedResolvedOwnerWithoutPinningIt()
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateAutomations();
        scenario.SetCronJobOwner("morning-brief", null);
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var ui = new SingleThreadDispatcher();
        using var store = new AutomationsStore("main", () => connected.Client, ui.Post);
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.ListLoaded);
        await ui.InvokeAsync(() => store.Select("morning-brief"));
        await await ui.InvokeAsync(() => store.LoadSelectedAsync("morning-brief"));
        await ui.InvokeAsync(() => { });
        Assert.Null(store.State.SelectedJob!.AgentId);
        Assert.Equal("main", store.State.SelectedJob.Owner);
        Assert.False(CronJobForm.FromJob(store.State.SelectedJob).BuildDefinition(store.State.SelectedJob).ContainsKey("agentId"));
    }

    [Fact]
    public async Task AutomationsStore_AgendaPagesTheWholeScopeAndRevealLoadsPastTheBrowsePage()
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateAutomations(additionalJobs: 205);
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var ui = new SingleThreadDispatcher();
        using var store = new AutomationsStore("main", () => connected.Client, ui.Post);
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.OverviewLoaded && store.State.ListLoaded);
        Assert.Equal(213, store.State.OverviewJobs.Count);
        Assert.True(store.State.HasMore);
        Assert.DoesNotContain(store.State.Jobs, j => j.Id == "scheduled-204");
        Assert.Contains(store.State.OverviewJobs, j => j.Id == "scheduled-204");
        Assert.DoesNotContain(store.State.OverviewJobs, j => j.Id == GatewayScenario.AutomationsOtherAgentJobId);
        Assert.Equal(AutomationRevealOutcome.Revealed, await await ui.InvokeAsync(() => store.RevealAsync("scheduled-204")));
        await WaitForAsync(() => store.State.SelectedJobId == "scheduled-204");
        Assert.Contains(store.State.Jobs, j => j.Id == "scheduled-204");
        Assert.Equal("", store.State.ListQuery.Query);
        Assert.NotNull(store.State.LastSeenAtMs);
        scenario.CronStatusReadError = "Synthetic scheduler status request failure";
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.StatusError is not null && !store.State.ListLoading);
        Assert.Null(store.State.Status);
        Assert.Equal(213, store.State.OverviewJobs.Count);
        scenario.CronStatusReadError = null;
        scenario.CronReadError = "Synthetic refresh failure";
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.OverviewError is not null && store.State.ListError is not null);
        Assert.Equal(213, store.State.OverviewJobs.Count);
        Assert.Contains(store.State.Jobs, j => j.Id == "scheduled-204");
    }

    [Fact]
    public async Task AutomationsStore_RevealOfAMissingJobSaysSoWithoutSelectingOrWidening()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateAutomations(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var ui = new SingleThreadDispatcher();
        using var store = new AutomationsStore("main", () => connected.Client, ui.Post, (_, _) => Task.CompletedTask);
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.ListLoaded && !store.State.ListLoading);

        Assert.Equal(AutomationRevealOutcome.NotFound, await await ui.InvokeAsync(() => store.RevealAsync("deleted-elsewhere", widenToAllAgents: true)));
        await WaitForAsync(() => store.State.Notice?.Kind == AutomationsNoticeKind.JobUnavailable && !store.State.ListLoading);

        var notice = store.State.Notice!;
        Assert.Equal("deleted-elsewhere", notice.JobName);
        Assert.True(notice.IsInformational);
        Assert.False(notice.IsError);
        // Nothing is selected for a job the list does not hold, and a miss does not leave the filtered agent.
        Assert.Null(store.State.SelectedJobId);
        Assert.Null(store.State.SelectedJob);
        Assert.Equal("main", store.State.AgentFilter);
        Assert.Contains(store.State.Jobs, job => job.Id == "morning-brief");
        Assert.DoesNotContain(store.State.Jobs, job => job.Id == GatewayScenario.AutomationsOtherAgentJobId);
    }

    [Fact]
    public async Task AutomationsStore_RevealStaysInTheFilteredAgentUnlessWidened()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateAutomations(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var ui = new SingleThreadDispatcher();
        using var store = new AutomationsStore("main", () => connected.Client, ui.Post, (_, _) => Task.CompletedTask);
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.ListLoaded && store.State.OverviewLoaded && !store.State.ListLoading);

        // An agenda link is scoped: another agent's job is not reachable from this filter.
        Assert.Equal(AutomationRevealOutcome.NotFound, await await ui.InvokeAsync(() => store.RevealAsync(GatewayScenario.AutomationsOtherAgentJobId)));
        await WaitForAsync(() => store.State.Notice?.Kind == AutomationsNoticeKind.JobUnavailable && !store.State.ListLoading);
        Assert.Null(store.State.SelectedJobId);
        Assert.Equal("main", store.State.AgentFilter);
        Assert.DoesNotContain(server.Requests, r => r.Method == "cron.list" && r.AgentId is null && r.Outcome == "ok");

        // A search result is not: the owning agent is shown instead of a dead end.
        Assert.Equal(AutomationRevealOutcome.Revealed,
            await await ui.InvokeAsync(() => store.RevealAsync(GatewayScenario.AutomationsOtherAgentJobId, widenToAllAgents: true)));
        await WaitForAsync(() => store.State.SelectedJobId == GatewayScenario.AutomationsOtherAgentJobId
            && store.State.OverviewLoaded && !store.State.ListLoading);
        Assert.True(store.State.IsAllAgents);
        Assert.Contains(store.State.Jobs, job => job.Id == GatewayScenario.AutomationsOtherAgentJobId);
        Assert.Contains(store.State.Jobs, job => job.Id == "morning-brief");
        Assert.Contains(store.State.OverviewJobs, job => job.Id == GatewayScenario.AutomationsOtherAgentJobId);
        // New automations still belong to the route agent, not the revealed job's owner.
        Assert.Equal("main", store.State.DefaultAgentId);
        Assert.Equal(GatewayScenario.AutomationsOtherAgentJobId, store.State.SelectedJob?.Id);
    }

    [Fact]
    public async Task AutomationsStore_AJobSavedForAnotherAgentIsRevealedByWideningTheFilter()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateAutomations(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var ui = new SingleThreadDispatcher();
        using var store = new AutomationsStore("main", () => connected.Client, ui.Post, (_, _) => Task.CompletedTask);
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.ListLoaded && !store.State.ListLoading);

        var created = await await ui.InvokeAsync(() => store.SaveAsync(CronJobForm.CreateDefault("ops") with { Name = "Ops nightly", PayloadText = "Check the backups" }, null));
        var saved = Assert.IsType<AutomationsSaveResult.Saved>(created).Job;
        Assert.Equal("ops", saved.AgentId);
        await WaitForAsync(() => store.State.Notice?.Kind == AutomationsNoticeKind.Created);

        Assert.Equal(AutomationRevealOutcome.Revealed, await await ui.InvokeAsync(() => store.RevealAsync(saved.Id, widenToAllAgents: true)));
        await WaitForAsync(() => store.State.SelectedJobId == saved.Id && store.State.IsAllAgents && !store.State.ListLoading
            && store.State.Jobs.Any(job => job.Id == saved.Id));
        Assert.Equal("main", store.State.DefaultAgentId);
    }

    [Fact]
    public async Task AutomationsStore_RevealNeedsAConnectedGatewayAndYieldsToANewerLoad()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateAutomations(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var ui = new SingleThreadDispatcher();
        using var store = new AutomationsStore("main", () => connected.Client, ui.Post, (_, _) => Task.CompletedTask);

        // Before the first Refresh the store has not confirmed a connection, so it asks the Gateway for nothing.
        Assert.Equal(AutomationRevealOutcome.Failed, await await ui.InvokeAsync(() => store.RevealAsync("morning-brief")));
        Assert.DoesNotContain(server.Requests, r => r.Method == "cron.list");
        Assert.Null(store.State.SelectedJobId);

        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.ListLoaded && store.State.OverviewLoaded && !store.State.ListLoading);
        server.HoldResponses("cron.list");
        var reveal = await ui.InvokeAsync(() => store.RevealAsync("weekly-digest"));
        await WaitForAsync(() => server.Requests.Any(r => r.Method == "cron.list" && r.AgentId == "main" && r.Outcome == "pending"));
        await ui.InvokeAsync(() => store.SetAgentFilter("ops"));
        server.ReleaseResponses("cron.list");

        Assert.Equal(AutomationRevealOutcome.Superseded, await reveal);
        await WaitForAsync(() => store.State.ListLoaded && store.State.OverviewLoaded && !store.State.ListLoading
            && server.Requests.All(r => r.Method != "cron.list" || r.Outcome != "pending"));
        await Task.Delay(100);
        // The request that lost has no say: no selection, no notice and the newer agent's rows stand.
        Assert.Null(store.State.SelectedJobId);
        Assert.Null(store.State.Notice);
        Assert.Equal("ops", store.State.AgentFilter);
        Assert.Equal(GatewayScenario.AutomationsOtherAgentJobId, Assert.Single(store.State.Jobs).Id);
    }

    [Fact]
    public async Task Automations_ListIsScopedToTheRequestedAgent()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateAutomations(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        IOperatorGatewayClient client = connected.Client;

        var main = await client.ListCronJobsAsync(new CronListQuery { AgentId = "main" });
        var research = await client.ListCronJobsAsync(new CronListQuery { AgentId = "research" });
        var unscoped = await client.ListCronJobsAsync(new CronListQuery());

        Assert.NotEmpty(main.Jobs);
        Assert.All(main.Jobs, job => Assert.Equal("main", job.AgentId));
        Assert.DoesNotContain(main.Jobs, job => job.Id == GatewayScenario.AutomationsOtherAgentJobId);
        Assert.Empty(research.Jobs);
        Assert.Equal(0, research.Total);
        Assert.Contains(unscoped.Jobs, job => job.Id == GatewayScenario.AutomationsOtherAgentJobId);
        var request = server.Requests.Last(r => r.Method == "cron.list" && r.AgentId == "research");
        Assert.Equal("ok", request.Outcome);
    }

    [Fact]
    public async Task Automations_RequestMetadataSeparatesInboxCatalogFromTypedPageScopes()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateAutomations(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        IOperatorGatewayClient client = connected.Client;

        using var deadline = new CancellationTokenSource(Deadline);
        // The same catalog entrypoint and parameters as InboxGatewaySource, then both typed scopes.
        var catalog = await client.ReadSearchCatalogAsync("cron.list", new { includeDisabled = true }, CancellationToken.None);
        Assert.Contains(catalog.GetProperty("jobs").EnumerateArray(), job =>
            job.GetProperty("id").GetString() == GatewayScenario.AutomationsOtherAgentJobId);
        await client.ListCronJobsAsync(new CronListQuery { AgentId = "main" });
        await client.ListCronJobsAsync(new CronListQuery());
        // An extra query field must not gain the catalog exception just because agentId is absent.
        await connected.Client.SendWizardRequestAsync("cron.list", new { includeDisabled = true, offset = 0, limit = 200, query = "morning" });

        // A client response can arrive before the server publishes its completed diagnostic record.
        await server.WaitForCompletedRequestAsync("cron.list", occurrence: 4, cancellationToken: deadline.Token);
        var reads = server.Requests.Where(request => request.Method == "cron.list").ToArray();
        Assert.Equal(4, reads.Length);
        Assert.True(reads[0].IsUnscopedCronCatalogRead);
        Assert.Null(reads[0].AgentId);
        Assert.All(reads.Skip(1), request => Assert.False(request.IsUnscopedCronCatalogRead));
        Assert.Equal("main", reads[1].AgentId);
        Assert.Null(reads[2].AgentId);
        Assert.Null(reads[3].AgentId);
        Assert.All(reads, request => Assert.Equal("ok", request.Outcome));
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task Automations_UpdateIsGuardedByConfigRevision()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateAutomations(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        IOperatorGatewayClient client = connected.Client;

        var job = await client.GetCronJobAsync("morning-brief");
        var form = CronJobForm.FromJob(job) with { CronTz = "Europe/Paris" };
        var updated = await client.UpdateCronJobDetailedAsync(job.Id, job.ConfigRevision, form.BuildDefinition(job));
        Assert.Equal("Europe/Paris", updated.Schedule.Tz);
        Assert.Equal(300_000, updated.Schedule.StaggerMs);
        Assert.NotEqual(job.ConfigRevision, updated.ConfigRevision);

        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.UpdateCronJobDetailedAsync(job.Id, job.ConfigRevision, new JsonObject { ["enabled"] = false }));
        Assert.True(CronGatewayRequests.IsRevisionConflict(stale));
        Assert.True((await client.GetCronJobAsync(job.Id)).Enabled);
    }

    [Fact]
    public async Task Automations_RemoveConfirmsTheLoadedRevisionAndSendsOnlyTheId()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateAutomations(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        IOperatorGatewayClient client = connected.Client;

        var job = await client.GetCronJobAsync("weekly-digest");
        var edited = await client.UpdateCronJobDetailedAsync(job.Id, job.ConfigRevision, new JsonObject { ["enabled"] = false });
        Assert.NotEqual(job.ConfigRevision, edited.ConfigRevision);

        // A definition changed since it was loaded is reported without sending the removal.
        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RemoveCronJobDetailedAsync(job.Id, job.ConfigRevision));
        Assert.True(CronGatewayRequests.IsRevisionConflict(stale));
        Assert.DoesNotContain(server.Requests, request => request.Method == "cron.remove");
        Assert.Equal(edited.ConfigRevision, (await client.GetCronJobAsync(job.Id)).ConfigRevision);

        await client.RemoveCronJobDetailedAsync(job.Id, edited.ConfigRevision);
        var removal = Assert.Single(server.Requests, request => request.Method == "cron.remove");
        Assert.Equal("ok", removal.Outcome);
        var gone = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCronJobAsync(job.Id));
        Assert.True(CronGatewayRequests.IsJobNotFound(gone));
    }

    [Fact]
    public async Task AutomationsStore_RemovingAJobChangedElsewhereReportsTheConflictAndKeepsIt()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateAutomations(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        IOperatorGatewayClient client = connected.Client;
        using var ui = new SingleThreadDispatcher();
        using var store = new AutomationsStore("main", () => connected.Client, ui.Post, (_, ct) => Task.Delay(Timeout.Infinite, ct));
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.ListLoaded);

        var loaded = store.State.Jobs.Single(job => job.Id == "weekly-digest");
        Assert.False(string.IsNullOrEmpty(loaded.ConfigRevision));
        await client.UpdateCronJobDetailedAsync(loaded.Id, loaded.ConfigRevision, new JsonObject { ["enabled"] = false });

        await await ui.InvokeAsync(() => store.RemoveAsync(loaded.Id));
        await WaitForAsync(() => store.State.Notice is not null);
        Assert.Equal(AutomationsNoticeKind.Conflict, store.State.Notice!.Kind);
        Assert.DoesNotContain(server.Requests, request => request.Method == "cron.remove");
        await WaitForAsync(() => !store.State.ListLoading);
        Assert.False(store.State.Jobs.Single(job => job.Id == loaded.Id).Enabled);
        Assert.Equal(loaded.Id, (await client.GetCronJobAsync(loaded.Id)).Id);
    }

    [Theory]
    [InlineData("cron.runs", """{"scope":"all","view":"summary"}""")]
    [InlineData("cron.remove", """{"id":"weekly-digest","expectedConfigRevision":"rev"}""")]
    public async Task AutomationsFixture_RejectsPropertiesTheReleasedGatewayDoesNotAccept(string method, string json)
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateAutomations(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var parameters = System.Text.Json.JsonDocument.Parse(json);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => connected.Client.SendWizardRequestAsync(method, parameters.RootElement));

        var request = Assert.Single(server.Requests, r => r.Method == method);
        Assert.Equal("error:INVALID_PARAMS", request.Outcome);
        Assert.Equal("weekly-digest", (await connected.Client.GetCronJobAsync("weekly-digest")).Id);
    }

    [Fact]
    public async Task Automations_BasicEditPreservesInheritedTimingOnAStrictGatewayAndBlocksCopies()
    {
        var scenario = GatewayScenario.CreateAutomations();
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        IOperatorGatewayClient client = connected.Client;
        var original = await client.GetCronJobAsync("morning-brief");
        var withTiming = await client.UpdateCronJobDetailedAsync(original.Id, original.ConfigRevision,
            JsonNode.Parse("""{"activeHours":{"start":"08:00","end":"17:00","timezone":"user"},"idleOnly":true}""")!.AsObject());
        scenario.RejectUnsupportedCronTimingFields = true;
        using var ui = new SingleThreadDispatcher();
        using var store = new AutomationsStore("main", () => connected.Client, ui.Post, (_, ct) => Task.Delay(Timeout.Infinite, ct));
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.ListLoaded);

        var saved = await await ui.InvokeAsync(() => store.SaveAsync(
            CronJobForm.FromJob(withTiming) with { Name = "Basic rename" }, withTiming));
        Assert.IsType<AutomationsSaveResult.Saved>(saved);
        var after = await client.GetCronJobAsync(original.Id);
        Assert.Equal("Basic rename", after.Name);
        Assert.Equal(new CronActiveHours("08:00", "17:00", "user"), after.ActiveHours);
        Assert.True(after.IdleOnly);

        var count = server.Requests.Count(request => request.Method is "cron.add" or "cron.update");
        var copyForm = CronJobForm.FromClone(after, "Copied");
        var copy = await await ui.InvokeAsync(() => store.SaveAsync(copyForm, null, after));
        Assert.Equal(CronFormError.TimingWebManaged,
            Assert.IsType<AutomationsSaveResult.Invalid>(copy).Errors[CronFormField.Timing]);
        var changed = await await ui.InvokeAsync(() => store.SaveAsync(
            CronJobForm.FromJob(after) with { IdleOnly = false }, after));
        Assert.IsType<AutomationsSaveResult.Invalid>(changed);
        Assert.Equal(count, server.Requests.Count(request => request.Method is "cron.add" or "cron.update"));
        Assert.True(copyForm.ActiveHoursEnabled);
        Assert.True(copyForm.IdleOnly);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.UpdateCronJobDetailedAsync(after.Id, after.ConfigRevision,
                JsonNode.Parse("""{"activeHours":null}""")!.AsObject()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.AddCronJobDetailedAsync(JsonNode.Parse("""{"idleOnly":true}""")!.AsObject()));
        Assert.True((await client.GetCronJobAsync(after.Id)).IdleOnly);
    }

    [Fact]
    public async Task Automations_RunHistoryFiltersByJobStatusAndAgent()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateAutomations(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        IOperatorGatewayClient client = connected.Client;

        var runs = await client.ListCronRunsAsync(new CronRunsQuery { JobId = "morning-brief", Limit = 5 });
        Assert.Equal(5, runs.Entries.Count);
        Assert.True(runs.HasMore);
        Assert.All(runs.Entries, run => Assert.Equal("morning-brief", run.JobId));
        var errors = await client.ListCronRunsAsync(new CronRunsQuery { JobId = "morning-brief", Statuses = ["error"] });
        var failed = Assert.Single(errors.Entries);
        Assert.Equal("not-delivered", failed.DeliveryStatus);
        var all = await client.ListCronRunsAsync(new CronRunsQuery { AgentId = "main", Limit = 200 });
        Assert.DoesNotContain(all.Entries, run => run.JobId == GatewayScenario.AutomationsOtherAgentJobId);

        var everyAgent = await client.ListCronRunsAsync(new CronRunsQuery { Limit = 200 });
        Assert.Contains(everyAgent.Entries, run => run.JobId == GatewayScenario.AutomationsOtherAgentJobId);
        // Each run links to its chat session so "Open in chat" can show the full conversation.
        Assert.All(runs.Entries, run => Assert.StartsWith("agent:main:cron:morning-brief:run:", run.SessionKey));
    }

    [Fact]
    public async Task Automations_RunIdsDistinguishSuccessiveInvocationsWithStaleInventory()
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateAutomations(sparse: true);
        scenario.QueueCronRunsOnly = true;
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var before = await client.GetCronJobAsync("morning-brief");
        var at = before.State.LastRunAtMs!.Value + 60_000;
        var first = await client.RunCronJobDetailedAsync("morning-brief");
        Assert.True(first.Accepted && first.Enqueued);
        Assert.Equal(first.RunId, Assert.Single(scenario.GetQueuedCronRunIds("morning-brief")));
        scenario.CompleteQueuedCronRun("morning-brief", first.RunId!, at, updateListState: false);
        var second = await client.RunCronJobDetailedAsync("morning-brief");
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.Equal(before.State.LastRunAtMs, (await client.GetCronJobAsync("morning-brief")).State.LastRunAtMs);
        Assert.Empty((await client.ListCronRunsAsync(new CronRunsQuery { JobId = "morning-brief", RunId = second.RunId })).Entries);
        Assert.Equal(first.RunId, Assert.Single((await client.ListCronRunsAsync(
            new CronRunsQuery { JobId = "morning-brief", RunId = first.RunId })).Entries).RunId);
        // Equal timestamps still describe distinct invocations. Only the accepted ID is authoritative.
        scenario.CompleteQueuedCronRun("morning-brief", second.RunId!, at);
        Assert.Equal(second.RunId, Assert.Single((await client.ListCronRunsAsync(
            new CronRunsQuery { JobId = "morning-brief", RunId = second.RunId })).Entries).RunId);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task AutomationsStore_ScopesRunsAndLatchesRunNowUntilHistoryReportsIt()
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateAutomations();
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var ui = new SingleThreadDispatcher();
        using var store = new AutomationsStore("main", () => connected.Client, ui.Post, (_, _) => Task.CompletedTask);

        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.ListLoaded && store.State.ScopedTotal is not null);
        Assert.True(store.State.CanManage);
        Assert.All(store.State.Jobs, job => Assert.Equal("main", job.AgentId));
        Assert.Equal(store.State.Jobs.Count, store.State.ScopedTotal);
        Assert.Equal(1, store.State.FailingCount);
        Assert.DoesNotContain(server.Requests, r => r.Method == "cron.list" && r.AgentId is null);

        await ui.InvokeAsync(() => store.Select("inbox-sweep"));
        await WaitForAsync(() => store.State.RunsLoaded && store.State.SelectedJob?.ConfigRevision is not null);
        Assert.All(store.State.Runs, run => Assert.Equal("inbox-sweep", run.JobId));

        var run = await ui.InvokeAsync(() => store.RunNowAsync("inbox-sweep"));
        Assert.True(store.State.IsRunning("inbox-sweep"));
        await run;
        await WaitForAsync(() => !store.State.IsRunning("inbox-sweep"));
        await WaitForAsync(() => store.State.Runs.Any(r => r.Summary?.Contains("Nothing was executed", StringComparison.Ordinal) == true));

        await await ui.InvokeAsync(() => store.RunNowAsync("inbox-sweep", CronRunMode.Due));
        await WaitForAsync(() => store.State.Notice?.Kind == AutomationsNoticeKind.RunNotStarted);
        Assert.Equal("not-due", store.State.Notice?.Reason);
        Assert.False(store.State.IsRunning("inbox-sweep"));
    }

    [Fact]
    public async Task AutomationsStore_MainSessionCreateAndUpdateUseReleasedPayloadShape()
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateAutomations(sparse: true);
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var ui = new SingleThreadDispatcher();
        using var store = new AutomationsStore("main", () => connected.Client, ui.Post);
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.ListLoaded);
        var form = (CronJobForm.CreateDefault("main") with
            { Name = "Main timeline message", PayloadText = "Keep the entered task", SessionTarget = "main" }).Normalize(true);
        var saved = Assert.IsType<AutomationsSaveResult.Saved>(await await ui.InvokeAsync(() => store.SaveAsync(form, null))).Job;
        Assert.Equal("main", saved.SessionTarget);
        Assert.Equal(CronPayloadKind.SystemEvent, saved.Payload.Kind);
        Assert.Equal("Keep the entered task", saved.Payload.Text);
        var retargeted = (CronJobForm.FromJob(saved) with { SessionTarget = "isolated" }).Normalize(true);
        var updated = Assert.IsType<AutomationsSaveResult.Saved>(await await ui.InvokeAsync(() => store.SaveAsync(retargeted, saved))).Job;
        Assert.Equal(CronPayloadKind.AgentTurn, updated.Payload.Kind);
        Assert.Equal("Keep the entered task", updated.Payload.Text);
        var returnToMain = (CronJobForm.FromJob(updated) with { SessionTarget = "main" }).Normalize(true);
        var restored = Assert.IsType<AutomationsSaveResult.Saved>(await await ui.InvokeAsync(() => store.SaveAsync(returnToMain, updated))).Job;
        Assert.Equal("main", restored.SessionTarget);
        Assert.Equal(CronPayloadKind.SystemEvent, restored.Payload.Kind);
        Assert.Equal("Keep the entered task", restored.Payload.Text);
        // The fixture rejects the pre-fix request rather than supplying a successful result.
        var invalid = retargeted.BuildDefinition();
        invalid["sessionTarget"] = "main";
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => connected.Client.AddCronJobDetailedAsync(invalid));
        var successfulWrites = server.Requests.Where(r => r.Method is "cron.add" or "cron.update").Take(3).ToArray();
        Assert.Equal(3, successfulWrites.Length);
        Assert.All(successfulWrites, r => Assert.Equal("ok", r.Outcome));
    }

    [Fact]
    public async Task AutomationsStore_OwnerDeliveryKeepsSavedRoutingAndBlocksUnsupportedWrites()
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateAutomations(sparse: true);
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        // Seed a job saved by a newer Gateway, then enforce the published stable write contract.
        var saved = await client.UpdateCronJobDetailedAsync("morning-brief", "rev-1", new JsonObject
        {
            ["delivery"] = new JsonObject { ["target"] = "owner", ["threadId"] = "preserved", ["futureSetting"] = "kept" },
        });
        var originalDelivery = Assert.Single(scenario.GetCronJobs()).GetProperty("delivery").GetRawText();
        scenario.RejectUnsupportedCronDeliveryTarget = true;
        using var ui = new SingleThreadDispatcher();
        using var store = new AutomationsStore("main", () => client, ui.Post);
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.ListLoaded && !store.State.ListLoading);
        var form = CronJobForm.FromJob(saved) with { Name = "Renamed Owner job" };
        var result = await await ui.InvokeAsync(() => store.SaveAsync(form, saved));
        var updated = Assert.IsType<AutomationsSaveResult.Saved>(result).Job;
        Assert.Equal(originalDelivery, Assert.Single(scenario.GetCronJobs()).GetProperty("delivery").GetRawText());
        Assert.Equal("Renamed Owner job", updated.Name);

        var mutations = server.Requests.Count(request => request.Method is "cron.add" or "cron.update");
        var copy = CronJobForm.FromClone(updated, "Owner copy");
        Assert.IsType<AutomationsSaveResult.Invalid>(await await ui.InvokeAsync(() => store.SaveAsync(copy, null, updated)));
        Assert.IsType<AutomationsSaveResult.Invalid>(await await ui.InvokeAsync(() => store.SaveAsync(
            CronJobForm.FromJob(updated) with { DeliveryChannel = "telegram" }, updated)));
        Assert.IsType<AutomationsSaveResult.Invalid>(await await ui.InvokeAsync(() => store.SaveAsync(copy, null)));
        Assert.Equal(mutations, server.Requests.Count(request => request.Method is "cron.add" or "cron.update"));
        Assert.Equal("owner", copy.DeliveryTarget);
        // The fixture guard itself rejects the historical request, even if native validation is bypassed.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => client.UpdateCronJobDetailedAsync(updated.Id,
            updated.ConfigRevision, new JsonObject { ["delivery"] = new JsonObject { ["target"] = "owner" } }));
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task AutomationsStore_SaveRejectsInvalidFormsAndRecoversFromRevisionConflicts()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateAutomations(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var ui = new SingleThreadDispatcher();
        using var store = new AutomationsStore("main", () => connected.Client, ui.Post, (_, _) => Task.CompletedTask);
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.ListLoaded);

        var invalid = await await ui.InvokeAsync(() => store.SaveAsync(CronJobForm.CreateDefault("main"), null));
        var errors = Assert.IsType<AutomationsSaveResult.Invalid>(invalid).Errors;
        Assert.Equal(CronFormError.NameRequired, errors[CronFormField.Name]);
        Assert.Equal(CronFormError.AgentMessageRequired, errors[CronFormField.PayloadText]);

        var created = await await ui.InvokeAsync(() => store.SaveAsync(CronJobForm.CreateDefault(null) with { Name = "Created", PayloadText = "Say hi" }, null));
        var job = Assert.IsType<AutomationsSaveResult.Saved>(created).Job;
        Assert.Equal("main", job.AgentId);

        var stale = await connected.Client.GetCronJobAsync("weekly-digest");
        await ((IOperatorGatewayClient)connected.Client).UpdateCronJobDetailedAsync(stale.Id, stale.ConfigRevision, new JsonObject { ["description"] = "changed elsewhere" });
        var conflict = await await ui.InvokeAsync(() => store.SaveAsync(CronJobForm.FromJob(stale) with { Name = "Mine" }, stale));
        var latest = Assert.IsType<AutomationsSaveResult.Conflict>(conflict).Latest;
        Assert.Equal("changed elsewhere", latest?.Description);
        // Rebasing onto the latest definition (what the editor does) lets the retry succeed.
        var retried = await await ui.InvokeAsync(() => store.SaveAsync(CronJobForm.FromJob(latest!) with { Name = "Mine" }, latest));
        Assert.Equal("Mine", Assert.IsType<AutomationsSaveResult.Saved>(retried).Job.Name);
    }

    [Fact]
    public async Task AutomationsStore_AllAgentsListsEveryOwnerAndAgentFilterNarrowsIt()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateAutomations(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var ui = new SingleThreadDispatcher();
        using var store = new AutomationsStore("main", () => connected.Client, ui.Post, (_, _) => Task.CompletedTask);
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.ListLoaded && store.State.ScopedTotal is not null);
        Assert.Equal("main", store.State.AgentFilter);
        Assert.DoesNotContain(store.State.Jobs, job => job.Id == GatewayScenario.AutomationsOtherAgentJobId);
        var mainTotal = store.State.ScopedTotal!.Value;

        await ui.InvokeAsync(() => store.SetAgentFilter(null));
        await WaitForAsync(() => !store.State.ListLoading && store.State.ScopedTotal > mainTotal
            && store.State.Jobs.Any(job => job.Id == GatewayScenario.AutomationsOtherAgentJobId));
        Assert.True(store.State.IsAllAgents);
        // "All" status still includes paused jobs, and new automations still belong to the route agent.
        Assert.Contains(store.State.Jobs, job => !job.Enabled);
        Assert.Equal("main", store.State.DefaultAgentId);
        Assert.Contains(server.Requests, r => r.Method == "cron.list" && r.AgentId is null && r.Outcome == "ok");

        await ui.InvokeAsync(() => store.Select(GatewayScenario.AutomationsOtherAgentJobId));
        await WaitForAsync(() => store.State.RunsLoaded && !store.State.RunsLoading
            && store.State.Runs.Any(run => run.JobId == GatewayScenario.AutomationsOtherAgentJobId));

        await ui.InvokeAsync(() => store.SetAgentFilter("ops"));
        await WaitForAsync(() => !store.State.ListLoading && store.State.ScopedTotal == 1
            && store.State.Jobs.Count == 1 && store.State.Jobs[0].Id == GatewayScenario.AutomationsOtherAgentJobId);
        Assert.Equal("ops", store.State.DefaultAgentId);
        await WaitForAsync(() => store.State.RunsLoaded && !store.State.RunsLoading && store.State.Runs.Count > 0
            && store.State.Runs.All(run => run.JobId == GatewayScenario.AutomationsOtherAgentJobId));
    }

    [Fact]
    public async Task AutomationsStore_ReadOnlyOperatorsBrowseEverythingButChangeNothing()
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateAutomations();
        scenario.CronReadOnly = true;
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var ui = new SingleThreadDispatcher();
        using var store = new AutomationsStore("main", () => connected.Client, ui.Post, (_, _) => Task.CompletedTask);
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.ListLoaded && !store.State.ListLoading);

        Assert.False(store.State.CanManage);
        Assert.False(store.State.ReadDenied);
        Assert.Contains(store.State.Jobs, job => !job.Enabled);
        await ui.InvokeAsync(() => store.Select("morning-brief"));
        await WaitForAsync(() => store.State.RunsLoaded && !store.State.RunsLoading && store.State.SelectedJob?.ConfigRevision is not null);
        Assert.NotEmpty(store.State.Runs);

        // Without operator.admin the store sends no change at all.
        await await ui.InvokeAsync(() => store.RunNowAsync("morning-brief"));
        await await ui.InvokeAsync(() => store.SetEnabledAsync("morning-brief", false));
        await await ui.InvokeAsync(() => store.RemoveAsync("morning-brief"));
        var job = store.State.SelectedJob!;
        var save = await await ui.InvokeAsync(() => store.SaveAsync(CronJobForm.FromJob(job) with { Name = "Mine" }, job));
        Assert.IsType<AutomationsSaveResult.Failed>(save);
        Assert.DoesNotContain(server.Requests, r => r.Method is "cron.run" or "cron.update" or "cron.remove" or "cron.add");
        Assert.True(store.State.Jobs.Single(j => j.Id == "morning-brief").Enabled);
    }

    [Fact]
    public async Task AutomationsStore_ForbiddenReadIsAnAccessErrorNotAnEmptyList()
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateAutomations();
        scenario.CronReadDenied = true;
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var ui = new SingleThreadDispatcher();
        using var store = new AutomationsStore("main", () => connected.Client, ui.Post, (_, _) => Task.CompletedTask);
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.ListLoaded && !store.State.ListLoading);

        Assert.True(store.State.ReadDenied);
        Assert.NotNull(store.State.ListError);
        Assert.Empty(store.State.Jobs);
        Assert.Empty(store.State.OverviewJobs);
        Assert.False(store.State.OverviewLoaded);
        Assert.Contains(server.Requests, r => r.Method == "cron.list" && r.Outcome == "error:FORBIDDEN");
    }

    [Fact]
    public async Task AutomationsStore_AJobWithoutAnEnabledStateIsAReadErrorNotAPausedRow()
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateAutomations();
        scenario.RemoveCronJobField("morning-brief", "enabled");
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var ui = new SingleThreadDispatcher();
        using var store = new AutomationsStore("main", () => connected.Client, ui.Post, (_, _) => Task.CompletedTask);
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => !store.State.ListLoading && store.State.ListError is not null && store.State.OverviewError is not null);

        Assert.Equal(CronJsonReader.UnreadableJobMessage, store.State.ListError);
        Assert.Equal(CronJsonReader.UnreadableJobMessage, store.State.OverviewError);
        Assert.False(store.State.ListLoaded);
        Assert.Empty(store.State.Jobs);
        Assert.Null(store.State.Total);
        Assert.False(store.State.OverviewLoaded);
        Assert.Null(store.State.ActiveCount);
        Assert.Null(store.State.PausedCount);
    }

    [Fact]
    public async Task AutomationsStore_AgentChangeRetiresTheInFlightPageOfThePreviousAgent()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateAutomations(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var ui = new SingleThreadDispatcher();
        using var store = new AutomationsStore("main", () => connected.Client, ui.Post, (_, _) => Task.CompletedTask);
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.ListLoaded && !store.State.ListLoading && store.State.Jobs.Count > 1);

        server.HoldResponses("cron.list");
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => server.Requests.Any(r => r.Method == "cron.list" && r.AgentId == "main" && r.Outcome == "pending"));
        await ui.InvokeAsync(() => store.SetAgentFilter("ops"));
        // The previous agent's rows are gone at once and the new page is requested without waiting.
        Assert.Empty(store.State.Jobs);
        Assert.Empty(store.State.OverviewJobs);
        Assert.False(store.State.OverviewLoaded);
        Assert.False(store.State.ListLoaded);
        await WaitForAsync(() => server.Requests.Any(r => r.Method == "cron.list" && r.AgentId == "ops" && r.Outcome == "pending"));

        server.ReleaseResponses("cron.list");
        await WaitForAsync(() => store.State.ListLoaded && store.State.OverviewLoaded && !store.State.ListLoading
            && server.Requests.All(r => r.Method != "cron.list" || r.Outcome != "pending"));
        await Task.Delay(100);
        var job = Assert.Single(store.State.Jobs);
        Assert.Equal(GatewayScenario.AutomationsOtherAgentJobId, job.Id);
        Assert.Equal("ops", job.Owner);
        Assert.Equal(GatewayScenario.AutomationsOtherAgentJobId, Assert.Single(store.State.OverviewJobs).Id);
    }

    [Fact]
    public async Task AutomationsStore_RunningStateSurvivesFiltersAndSelectionKeepsRecentResults()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateAutomations(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        using var ui = new SingleThreadDispatcher();
        // The run watcher never polls, so only list changes could clear the running state.
        using var store = new AutomationsStore("main", () => connected.Client, ui.Post, (_, ct) => Task.Delay(Timeout.Infinite, ct));
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.ListLoaded);

        await ui.InvokeAsync(() => store.Select("inbox-sweep"));
        await await ui.InvokeAsync(() => store.RunNowAsync("inbox-sweep"));
        await ui.InvokeAsync(() => store.SetQuery(store.State.ListQuery with { Query = "dentist" }));
        await WaitForAsync(() => !store.State.ListLoading && store.State.Jobs.All(j => j.Id != "inbox-sweep"));
        Assert.True(store.State.IsRunning("inbox-sweep"));

        await ui.InvokeAsync(() => store.Select("dentist"));
        await WaitForAsync(() => store.State.RunsLoaded && store.State.RunsQuery.JobId == "dentist");
        await ui.InvokeAsync(() => store.Select("inbox-sweep"));
        await WaitForAsync(() => store.State.RunsLoaded && store.State.RunsQuery.JobId == "inbox-sweep");
        Assert.All(store.State.Runs, run => Assert.Equal("inbox-sweep", run.JobId));
        Assert.True(store.State.IsRunning("inbox-sweep"));
    }

    [Fact]
    public async Task AutomationsStore_ReconcilesRunsRemovalsAndRevisionsFromElsewhere()
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateAutomations();
        scenario.SetCronJobRunning("inbox-sweep", true);
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        IOperatorGatewayClient client = connected.Client;
        using var ui = new SingleThreadDispatcher();
        using var store = new AutomationsStore("main", () => connected.Client, ui.Post, (_, ct) => Task.Delay(Timeout.Infinite, ct));
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.ListLoaded);
        Assert.True(store.State.IsRunning("inbox-sweep"));

        // A run observed only in the list (no watcher here) cannot outlive its visible row.
        await ui.InvokeAsync(() => store.SetQuery(store.State.ListQuery with { Query = "dentist" }));
        await WaitForAsync(() => !store.State.ListLoading && store.State.Jobs.All(j => j.Id != "inbox-sweep"));
        Assert.False(store.State.IsRunning("inbox-sweep"));

        // The selected job's exact definition settles it too.
        await ui.InvokeAsync(() => store.SetQuery(store.State.ListQuery with { Query = "" }));
        await WaitForAsync(() => !store.State.ListLoading && store.State.IsRunning("inbox-sweep"));
        scenario.SetCronJobRunning("inbox-sweep", false);
        await ui.InvokeAsync(() => store.Select("inbox-sweep"));
        await WaitForAsync(() => !store.State.IsRunning("inbox-sweep"));

        // A delete-after-run job removed elsewhere is confirmed with cron.get (CRON_JOB_NOT_FOUND).
        var dentist = await client.GetCronJobAsync("dentist");
        await client.RemoveCronJobDetailedAsync(dentist.Id, dentist.ConfigRevision);
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.Notice is { Kind: AutomationsNoticeKind.JobCompleted, JobName: "Dentist reminder" });

        // Without a revision an edit would skip the Gateway conflict check, so nothing is sent.
        var updates = server.Requests.Count(r => r.Method == "cron.update");
        var weekly = await client.GetCronJobAsync("weekly-digest");
        var result = await await ui.InvokeAsync(() => store.SaveAsync(CronJobForm.FromJob(weekly) with { Name = "Mine" }, weekly with { ConfigRevision = null }));
        Assert.IsType<AutomationsSaveResult.MissingRevision>(result);
        Assert.Equal(updates, server.Requests.Count(r => r.Method == "cron.update"));
    }

    [Fact]
    public async Task AutomationsStore_LocalRunsSurviveReloadsAndRemovingAGoneJobIsQuiet()
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateAutomations();
        scenario.QueueCronRunsOnly = true;
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        IOperatorGatewayClient client = connected.Client;
        using var ui = new SingleThreadDispatcher();
        using var store = new AutomationsStore("main", () => connected.Client, ui.Post, (_, ct) => Task.Delay(Timeout.Infinite, ct));
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => store.State.ListLoaded);

        // Queued, not yet in history: a reload whose row is idle must not end a run started here.
        await await ui.InvokeAsync(() => store.RunNowAsync("inbox-sweep"));
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => !store.State.ListLoading);
        Assert.True(store.State.IsRunning("inbox-sweep"));

        // Removing a one-shot that is already gone succeeds quietly, without a false completion.
        var dentist = await client.GetCronJobAsync("dentist");
        await client.RemoveCronJobDetailedAsync(dentist.Id, dentist.ConfigRevision);
        await await ui.InvokeAsync(() => store.RemoveAsync("dentist"));
        await WaitForAsync(() => store.State.Notice?.Kind == AutomationsNoticeKind.Removed);
        await WaitForAsync(() => !store.State.ListLoading && store.State.Jobs.All(j => j.Id != "dentist"));
        await ui.InvokeAsync(store.Refresh);
        await WaitForAsync(() => !store.State.ListLoading);
        await Task.Delay(200);
        Assert.Equal(AutomationsNoticeKind.Removed, store.State.Notice?.Kind);
    }

    [Fact]
    public void OnlyTheJobNotFoundDetailProvesAJobIsGone()
    {
        Assert.True(CronGatewayRequests.IsJobNotFound(GatewayRequestErrors.Create("gone", "INVALID_REQUEST", CronProtocol.JobNotFoundCode)));
        Assert.False(CronGatewayRequests.IsJobNotFound(GatewayRequestErrors.Create("denied", "FORBIDDEN", null)));
        Assert.False(CronGatewayRequests.IsJobNotFound(GatewayRequestErrors.Create("down", "UNAVAILABLE", "UNAVAILABLE")));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met.");
            await Task.Delay(25);
        }
    }

    /// <summary>One thread owns store state, like the WinUI dispatcher in production.</summary>
    private sealed class SingleThreadDispatcher : IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;

        public SingleThreadDispatcher()
        {
            _thread = new Thread(() =>
            {
                foreach (var action in _queue.GetConsumingEnumerable())
                    action();
            }) { IsBackground = true };
            _thread.Start();
        }

        public void Post(Action action)
        {
            if (!_queue.IsAddingCompleted) _queue.Add(action);
        }

        public Task InvokeAsync(Action action) => InvokeAsync(() => { action(); return true; });

        public Task<T> InvokeAsync<T>(Func<T> func)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                try { result.SetResult(func()); }
                catch (Exception ex) { result.SetException(ex); }
            });
            return result.Task;
        }

        public void Dispose() => _queue.CompleteAdding();
    }
}