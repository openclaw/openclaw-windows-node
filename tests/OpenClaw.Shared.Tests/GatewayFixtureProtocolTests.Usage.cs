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

    [Fact]
    public async Task RealClient_UsageReadsReturnTypedOverviewDetailsAndProviderPlans()
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
        Assert.Equal(24, report.Overview!.HourTokens.Count);
        Assert.Contains("tool", report.Overview.FilterOptions.Keys);
        Assert.True(report.Totals.TotalTokens > 0);
        Assert.Equal(report.Totals.TotalTokens, report.Aggregates.Daily.Sum(d => d.Tokens));
        var costs = report.Sessions.Select(s => s.Usage!.Totals.TotalCost).ToArray();
        Assert.Equal(costs.OrderByDescending(c => c), costs);
        Assert.Equal("fresh", report.CacheStatus!.Status);

        var key = report.Sessions[0].Key;
        var detail = await client.GetSessionsUsageAsync(UsageWeek(overview: false) with { Key = key, IncludeContextWeight = true });
        var row = Assert.Single(detail.Value!.Sessions);
        Assert.Equal(key, row.Key);
        Assert.True(row.ContextWeight!.SystemPromptChars > 0);

        var series = await client.GetSessionUsageTimeseriesAsync(key, row.AgentId);
        Assert.Equal(UsageReadStatus.Ok, series.Status);
        Assert.NotEmpty(series.Value!.Points);
        var logs = await client.GetSessionUsageLogsAsync(key, row.AgentId);
        Assert.Contains(logs.Value!, entry => entry.Role == "user");

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
        var query = UsageWeek() with { Query = "agent:main", SelectedDays = ["2026-10-20"], Sort = UsageSort.Cost };

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
        var detail = await client.GetSessionsUsageAsync(UsageWeek(overview: false) with { Key = key, IncludeContextWeight = true });
        Assert.Equal(key, Assert.Single(detail.Value!.Sessions).Key);
    }

    [Fact]
    public async Task RealClient_OlderSchemaThatRejectsTheReleasedParametersFallsBackToUsageCost()
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
