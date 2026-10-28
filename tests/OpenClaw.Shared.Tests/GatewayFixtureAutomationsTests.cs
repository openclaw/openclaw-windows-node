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
    public async Task Automations_RunsHistoryScratchEventsAndConversationsRoundTrip()
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

        var transcript = await client.GetCronRunTranscriptAsync(runs.Entries[0]);
        Assert.Contains(transcript.Messages, m => m.Role == "user");
        Assert.Contains(transcript.Messages, m => m.Role == "assistant" && m.Text.Length > 0);
        Assert.Contains(transcript.Messages, m => m.Role == "tool" && m.ToolName == "calendar.list");
        Assert.DoesNotContain(transcript.Messages, m => m.Role == "toolResult");

        var scratch = await client.GetCronScratchAsync("morning-brief");
        Assert.True(scratch.Exists);
        var saved = await client.SetCronScratchAsync("morning-brief", "- new note\n", scratch.CurrentRevision);
        Assert.True(saved.Ok);
        Assert.Equal("- new note\n", saved.Snapshot!.Content);
        var conflict = await client.SetCronScratchAsync("morning-brief", "lost", scratch.CurrentRevision);
        Assert.True(conflict.Conflict);
        var removed = await client.SetCronScratchAsync("morning-brief", null, saved.Snapshot.CurrentRevision);
        Assert.True(removed.Ok);
        Assert.False((await client.GetCronScratchAsync("morning-brief")).Exists);
        // Compare-and-swap continues from the revision the removal returned.
        Assert.True((await client.SetCronScratchAsync("morning-brief", "again", removed.CurrentRevision)).Ok);

        var events = await client.ListMcpEventsAsync("main", "github");
        Assert.Contains(events, e => e.Name == "pull_request.opened" && e.InputSchemaJson is not null);
        var conversations = await client.ListConversationsAsync("main", "slack");
        Assert.All(conversations, c => Assert.Equal("slack", c.Channel));
        Assert.Contains(conversations, c => c.Target == "#daily");
        var hints = await CronGatewayRequests.EditorHintsAsync(client);
        Assert.Equal(["github", "linear"], hints.McpServers);
        Assert.Contains("fixture/browse", hints.Models);
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

        // A filter change drops old rows so a concurrent Load more cannot mix two filters.
        await ui.InvokeAsync(() => store.Select("morning-brief"));
        await WaitForAsync(() => store.State.RunsLoaded && store.State.RunsQuery.JobId == "morning-brief" && !store.State.RunsLoading);
        await ui.InvokeAsync(() => store.SetRunsFilter(store.State.RunsQuery with { Limit = 5 }));
        await WaitForAsync(() => store.State.RunsHasMore && !store.State.RunsLoading);
        await ui.InvokeAsync(() => store.SetRunsFilter(store.State.RunsQuery with { Statuses = ["error"] }));
        Assert.Empty(store.State.Runs);
        await await ui.InvokeAsync(() => store.LoadRunsAsync(append: true));
        await WaitForAsync(() => store.State.RunsLoaded && !store.State.RunsLoading);
        Assert.All(store.State.Runs, run => Assert.Equal("error", run.Status));
        Assert.Single(store.State.Runs);
    }

    [Fact]
    public async Task AutomationsStore_RunningStateSurvivesFiltersAndReselectLeavesRunHistory()
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

        await ui.InvokeAsync(() => store.SetView(AutomationsView.RunHistory));
        await ui.InvokeAsync(() => store.Select("inbox-sweep"));
        Assert.Equal(AutomationsView.Jobs, store.State.View);
        Assert.Equal("inbox-sweep", store.State.RunsQuery.JobId);
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