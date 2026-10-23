using System.Text.Json;
using OpenClaw.TestSupport.Gateway;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayFixtureProtocolTests
{
    [Fact]
    public async Task CronProductionReaders_PublishListStatusAndExactJobRuns()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateCronManagement(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var list = Signal<JsonElement>();
        var status = Signal<JsonElement>();
        var runs = Signal<JsonElement>();
        client.CronListUpdated += (_, data) => list.TrySetResult(data);
        client.CronStatusUpdated += (_, data) => status.TrySetResult(data);
        client.CronRunsUpdated += (_, data) => runs.TrySetResult(data);
        await client.RequestCronListAsync();
        await client.RequestCronStatusAsync();
        await client.RequestCronRunsAsync("listed", limit: 1, offset: 0);
        Assert.Equal(4, (await list.Task.WaitAsync(Deadline)).GetProperty("jobs").GetArrayLength());
        var scheduler = await status.Task.WaitAsync(Deadline);
        Assert.False(scheduler.GetProperty("enabled").GetBoolean());
        Assert.Equal(4, scheduler.GetProperty("jobs").GetInt32());
        var history = await runs.Task.WaitAsync(Deadline);
        Assert.Equal(1, history.GetProperty("limit").GetInt32());
        Assert.Equal(GatewayScenario.CronFixtureRunId,
            Assert.Single(history.GetProperty("entries").EnumerateArray()).GetProperty("runId").GetString());
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task CronProductionMutations_AwaitAcceptanceRefreshAndRejectInvalidJobs()
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateCronManagement();
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        Assert.True(await client.AddCronJobAsync(new
        {
            name = "Synthetic created job", enabled = true,
            schedule = new { kind = "every", everyMs = 60000 },
            sessionTarget = "isolated", wakeMode = "next-heartbeat",
            payload = new { kind = "agentTurn", message = "Do not execute." }, delivery = new { mode = "none" }
        }));
        Assert.Equal(5, scenario.GetCronJobs().Length);
        Assert.Equal("next-heartbeat", scenario.GetCronJobs().Single(job => job.GetProperty("id").GetString() == "created-4")
            .GetProperty("wakeMode").GetString());
        Assert.True(await client.UpdateCronJobAsync("created-4", new
        {
            enabled = false, deleteAfterRun = true,
            schedule = new { kind = "at", at = "2030-01-01T00:00:00.000Z" }
        }));
        var updated = scenario.GetCronJobs().Single(job => job.GetProperty("id").GetString() == "created-4");
        Assert.False(updated.GetProperty("enabled").GetBoolean());
        Assert.True(updated.GetProperty("deleteAfterRun").GetBoolean());
        Assert.Equal("2030-01-01T00:00:00.000Z", updated.GetProperty("schedule").GetProperty("at").GetString());
        Assert.True(await client.RemoveCronJobAsync("created-4"));
        Assert.Equal(4, scenario.GetCronJobs().Length);
        Assert.False(await client.UpdateCronJobAsync("unknown", new { enabled = true }));
        Assert.False(await client.RemoveCronJobAsync("unknown"));
        Assert.False(await client.AddCronJobAsync(new { name = "Missing schedule" }));
        var listRefreshes = server.Requests.Count(request => request.Method == "cron.list");
        Assert.Equal(3, listRefreshes);
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task CronProductionRun_DistinguishesEnqueueFromNotDue()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateCronManagement(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var accepted = await connected.Client.RunCronJobDetailedAsync("listed");
        Assert.True(accepted.Accepted);
        Assert.True(accepted.Enqueued);
        Assert.Equal("fixture:listed:manual", accepted.RunId);
        var notDue = await connected.Client.RunCronJobDetailedAsync("listed", force: false);
        Assert.True(notDue.Accepted);
        Assert.False(notDue.Enqueued);
        Assert.Equal("not-due", notDue.Reason);
        var missing = await connected.Client.RunCronJobDetailedAsync("unknown");
        Assert.False(missing.Accepted);
        Assert.Equal(3, server.Requests.Count(request => request.Method == "cron.run"));
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task CronProductionDetails_ReadExactJobAndCompareAndSwapPrivateScratch()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateCronManagement(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var epoch = client.SessionMutationConnectionEpoch!.Value;
        var job = await client.GetCronJobAsync("listed", epoch);
        Assert.Equal("listed", job.GetProperty("id").GetString());
        Assert.False(job.TryGetProperty("scratch", out _));
        var empty = await client.GetCronScratchAsync("listed", epoch);
        Assert.Null(empty.Content);
        Assert.Equal(0, empty.CurrentRevision);
        Assert.Equal(262144, empty.MaxBytes);
        var saved = await client.SetCronScratchAsync("listed", "Synthetic state \u00e9", 0, epoch);
        Assert.True(saved.Ok);
        Assert.Equal(1, saved.CurrentRevision);
        var conflict = await client.SetCronScratchAsync("listed", "Must not overwrite", 0, epoch);
        Assert.False(conflict.Ok);
        Assert.Equal(1, conflict.CurrentRevision);
        Assert.Equal("Synthetic state \u00e9", (await client.GetCronScratchAsync("listed", epoch)).Content);
        Assert.True((await client.SetCronScratchAsync("listed", null, 1, epoch)).Ok);
        var cleared = await client.GetCronScratchAsync("listed", epoch);
        Assert.Null(cleared.Content);
        Assert.Equal(2, cleared.CurrentRevision);
        Assert.False((await client.SetCronScratchAsync("listed", "Stale recreation", 0, epoch)).Ok);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetCronScratchAsync(
            "listed", new string('\u00e9', 131073), 2, epoch));
        Assert.Equal(2, (await client.GetCronScratchAsync("listed", epoch)).CurrentRevision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCronJobAsync("unknown", epoch));
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task CronProductionTranscript_BindsExactRunAndOpaqueCursorWithoutSessionSelection()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateCronManagement(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var epoch = client.SessionMutationConnectionEpoch!.Value;
        var first = await client.GetCronHistoryAsync("listed", GatewayScenario.CronFixtureRunId, null, null, epoch);
        Assert.Equal("Synthetic cron request.", Assert.Single(first.Messages).GetProperty("content").GetString());
        Assert.Equal("fixture-listed-page-2", first.NextCursor);
        var next = await client.GetCronHistoryAsync("listed", GatewayScenario.CronFixtureRunId, null, first.NextCursor, epoch);
        Assert.Equal("Synthetic cron response.", Assert.Single(next.Messages).GetProperty("content").GetString());
        Assert.Null(next.NextCursor);
        var byTimestamp = await client.GetCronHistoryAsync("listed", null, 1_787_227_200_000, null, epoch);
        Assert.Equal("Synthetic cron request.", Assert.Single(byTimestamp.Messages).GetProperty("content").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetCronHistoryAsync("listed", "unknown-run", null, null, epoch));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetCronHistoryAsync("every", GatewayScenario.CronFixtureRunId, null, first.NextCursor, epoch));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetCronHistoryAsync("listed", GatewayScenario.CronFixtureRunId, null, "wrong-cursor", epoch));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetCronHistoryAsync("listed", null, null, null, epoch));
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task CronBrowse_RemainsReadOnlyForEveryProductionMutation()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateBrowse(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        Assert.False(await client.AddCronJobAsync(new { name = "Rejected" }));
        Assert.False(await client.UpdateCronJobAsync("listed", new { enabled = false }));
        Assert.False(await client.RemoveCronJobAsync("listed"));
        Assert.False((await client.RunCronJobDetailedAsync("listed")).Accepted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetCronScratchAsync("listed", "Rejected", 0,
            client.SessionMutationConnectionEpoch!.Value));
        Assert.Equal(5, server.Requests.Count(request => request.Method.StartsWith("cron.", StringComparison.Ordinal)
            && request.Outcome == "error:FIXTURE_READ_ONLY"));
        Assert.Empty(server.UnexpectedRequests);
    }

    [Fact]
    public async Task CronProductionDetails_RejectRetiredConnectionBeforeSending()
    {
        var token = CreateToken();
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateCronManagement(), token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var stale = client.SessionMutationConnectionEpoch!.Value - 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCronJobAsync("listed", stale));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCronScratchAsync("listed", stale));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetCronScratchAsync("listed", "No send", 0, stale));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCronHistoryAsync("listed", "run", null, null, stale));
        Assert.DoesNotContain(server.Requests, request => request.Method.StartsWith("cron.", StringComparison.Ordinal));
    }
}
