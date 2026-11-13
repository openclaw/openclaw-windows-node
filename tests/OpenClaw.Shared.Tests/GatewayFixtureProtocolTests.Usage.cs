using OpenClaw.Shared.Usage;
using OpenClaw.TestSupport.Gateway;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed partial class GatewayFixtureProtocolTests
{
    private static SessionsUsageQuery UsageWeek(bool overview = true) => new()
    {
        StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-6).ToString("yyyy-MM-dd"),
        EndDate = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
        TimeZone = UsageTimeZoneMode.Utc,
        OverviewProjection = overview,
    };

    [Theory]
    [InlineData(false, 6)]
    [InlineData(true, 7)]
    public async Task RealClient_LayoutProofCoverageIsExplicitAndDefaultFixtureKeepsMissingDates(bool complete, int expectedDates)
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateUsageAnalytics(UsageAnalyticsMode.Released, completeDailyCoverage: complete);
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var result = await connected.Client.GetSessionsUsageAsync(new()
        {
            StartDate = "2026-11-03", EndDate = "2026-11-09", TimeZone = UsageTimeZoneMode.Utc,
        });
        Assert.Equal(UsageReadStatus.Ok, result.Status);
        var days = result.Value!.Aggregates.CostDaily;
        Assert.Equal(expectedDates, days.Count);
        Assert.Equal(complete, days.Any(day => day.Date == "2026-11-08"));
        Assert.All(days, day => Assert.True(day.Totals.TotalTokens > 0));
        if (complete)
            Assert.Equal(new[] { "2026-11-03", "2026-11-04", "2026-11-05", "2026-11-06", "2026-11-07", "2026-11-08", "2026-11-09" },
                days.Select(day => day.Date));
    }

    [Theory]
    [InlineData("systems-inventory", false)]
    [InlineData("systems-empty", true)]
    public async Task RealClient_SystemsScenariosRetainInventoryAndIndependentUsageReads(string name, bool empty)
    {
        var token = CreateToken();
        var scenario = GatewayScenario.LoadBuiltin(name);
        Assert.Equal(name, scenario.Name);
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;

        var inventory = await client.SendWizardRequestAsync("node.list", new { });
        var nodes = inventory.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Equal(empty, nodes.Length == 0);
        if (!empty)
            Assert.Contains(nodes, node => node.GetProperty("nodeId").GetString() == SystemsFixture.StudioNodeId);

        var usage = await client.GetSessionsUsageAsync(UsageWeek(overview: false));
        Assert.Equal(UsageReadStatus.Ok, usage.Status);
        Assert.NotEmpty(usage.Value!.Sessions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendWizardRequestAsync(
            "node.rename", new { nodeId = SystemsFixture.StudioNodeId, displayName = "Not applied" }));
    }

    [Fact]
    public async Task RealClient_UsageReadsReturnTypedOverviewSessionDetailAndProviderPlans()
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateUsageAnalytics();
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;

        var overview = await client.GetSessionsUsageAsync(UsageWeek() with { Sort = UsageSort.Cost, SortDescending = true });
        Assert.Equal(UsageReadStatus.Ok, overview.Status);
        var report = overview.Value!;
        Assert.NotEmpty(report.Sessions);
        Assert.NotNull(report.Overview);
        Assert.Contains("agent", report.Overview!.FilterOptions.Keys);
        Assert.True(report.Totals.TotalTokens > 0);
        Assert.Equal(report.Totals.TotalTokens, report.Aggregates.CostDaily.Sum(d => d.Totals.TotalTokens));
        var costs = report.Sessions.Select(s => s.Usage!.Totals.TotalCost).ToArray();
        Assert.Equal(costs.OrderByDescending(c => c), costs);
        Assert.Equal("fresh", report.CacheStatus!.Status);

        var key = report.Sessions[0].Key;
        var detail = await client.GetSessionsUsageAsync(UsageWeek(overview: false) with { Key = key, AgentId = report.Sessions[0].AgentId });
        var row = Assert.Single(detail.Value!.Sessions);
        Assert.Equal(key, row.Key);

        var providers = await client.GetProviderUsageAsync();
        Assert.Contains(providers.Value!.Providers, p => p.Billing.Count > 0);
        Assert.Contains(providers.Value.Providers, p => p.Error is not null);
    }

    [Fact]
    public async Task RealClient_ReleasedGatewayRetriesWithoutOverviewParametersOncePerConnection()
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateUsageAnalytics(UsageAnalyticsMode.Released);
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;
        var query = UsageWeek() with { Query = "agent:main", Sort = UsageSort.Cost };

        var first = await client.GetSessionsUsageAsync(query);

        Assert.Equal(UsageReadStatus.Ok, first.Status);
        var report = first.Value!;
        Assert.Null(report.Overview);
        Assert.NotEmpty(report.Sessions);
        Assert.Equal(report.Aggregates.SessionCount ?? report.Sessions.Count, report.Sessions.Count);
        Assert.True(report.Totals.TotalTokens > 0);
        Assert.NotEmpty(report.Aggregates.ByModel);
        Assert.Equal(2, server.Requests.Count(r => r.Method == "sessions.usage"));

        // Later reads on the same connection skip the rejected parameters.
        Assert.Equal(UsageReadStatus.Ok, (await client.GetSessionsUsageAsync(query)).Status);
        Assert.Equal(3, server.Requests.Count(r => r.Method == "sessions.usage"));

        var key = report.Sessions[0].Key;
        var detail = await client.GetSessionsUsageAsync(UsageWeek(overview: false) with { Key = key });
        Assert.Equal(key, Assert.Single(detail.Value!.Sessions).Key);
    }

    [Fact]
    public async Task RealClient_SchemaOlderThanTheInstalledGatewaysIsReportedUnsupportedOnce()
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateUsageAnalytics(UsageAnalyticsMode.Released);
        scenario.UsageAnalytics.ReleasedParameters = ["key", "agentId", "agentScope", "startDate", "endDate", "mode", "utcOffset", "timeZone", "limit"];
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;

        Assert.Equal(UsageReadStatus.Unsupported, (await client.GetSessionsUsageAsync(UsageWeek())).Status);
        Assert.Equal(UsageReadStatus.Unsupported, (await client.GetSessionsUsageAsync(UsageWeek())).Status);
        Assert.Equal(2, server.Requests.Count(r => r.Method == "sessions.usage"));
    }

    [Fact]
    public async Task RealClient_LegacyGatewayReportsUnsupportedOnceAndStaysOffline()
    {
        var token = CreateToken();
        var scenario = GatewayScenario.CreateUsageAnalytics(UsageAnalyticsMode.Legacy);
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        await using var connected = await ConnectedClient.OpenAsync(server, token);
        var client = connected.Client;

        Assert.Equal(UsageReadStatus.Unsupported, (await client.GetSessionsUsageAsync(UsageWeek())).Status);

        // The unsupported answer is cached for the connection, even if the Gateway later changes.
        scenario.UsageAnalytics.Mode = UsageAnalyticsMode.Populated;
        Assert.Equal(UsageReadStatus.Unsupported, (await client.GetSessionsUsageAsync(UsageWeek())).Status);
        Assert.Equal(UsageReadStatus.Ok, (await client.GetProviderUsageAsync()).Status);
    }
}
