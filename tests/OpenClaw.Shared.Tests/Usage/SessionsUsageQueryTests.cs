using System.Text.Json;
using OpenClaw.Shared.Usage;
using Xunit;

namespace OpenClaw.Shared.Tests.Usage;

/// <summary>
/// Pins sessions.usage request parameters to the upstream web client's requestSessionUsage
/// (openclaw/openclaw@5089aa71 ui/src/lib/sessions/usage.ts), so Windows reads the same projection.
/// </summary>
public sealed class SessionsUsageQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OverviewQuery_MatchesUpstreamWireParameters()
    {
        var query = new SessionsUsageQuery
        {
            StartDate = "2026-10-20",
            EndDate = "2026-10-26",
            TimeZone = UsageTimeZoneMode.Utc,
            Scope = UsageScope.Family,
            CreatorKey = "profile:ada",
            Query = "model:gpt-5.1 has:errors",
            SelectedDays = ["2026-10-25"],
            SelectedHours = [9, 10],
            SelectedSessions = ["agent:main:main"],
            RecentKeys = ["a", "b", "c", "d", "e", "f", "g", "h", "i"],
            Offset = 50,
            Sort = UsageSort.Tokens,
            SortDescending = false,
            OverviewProjection = true,
        };

        Assert.Equal("""
            {"startDate":"2026-10-20","endDate":"2026-10-26","agentScope":"all","mode":"utc","creatorKey":"profile:ada","groupBy":"family","limit":50,"projection":"overview","query":"model:gpt-5.1 has:errors","selectedDays":["2026-10-25"],"selectedHours":[9,10],"selectedSessions":["agent:main:main"],"offset":50,"recentKeys":["a","b","c","d","e","f","g","h"],"sort":"tokens","sortDirection":"asc","includeContextWeight":false}
            """, Serialize(query.ToParameters(TimeZoneInfo.Utc, Now)));
    }

    [Fact]
    public void DefaultQuery_OmitsEmptyFiltersAndUsesTheFullRowLimit()
    {
        var query = new SessionsUsageQuery { StartDate = "2026-10-26", EndDate = "2026-10-26", TimeZone = UsageTimeZoneMode.Utc };

        Assert.Equal("""
            {"startDate":"2026-10-26","endDate":"2026-10-26","agentScope":"all","mode":"utc","groupBy":"instance","limit":1000,"includeContextWeight":false}
            """, Serialize(query.ToParameters(TimeZoneInfo.Utc, Now)));
    }

    [Fact]
    public void DetailQuery_ReadsOneKeyWithContextWeightAndIgnoresTablePaging()
    {
        var query = new SessionsUsageQuery
        {
            StartDate = "2026-10-20",
            EndDate = "2026-10-26",
            TimeZone = UsageTimeZoneMode.Utc,
            AgentId = "research",
            Key = "agent:research:main",
            Offset = 50,
            Query = "ignored",
            IncludeContextWeight = true,
        };

        Assert.Equal("""
            {"startDate":"2026-10-20","endDate":"2026-10-26","agentId":"research","mode":"utc","groupBy":"instance","key":"agent:research:main","limit":1,"includeContextWeight":true}
            """, Serialize(query.ToParameters(TimeZoneInfo.Utc, Now)));
    }

    [Fact]
    public void LocalTimeZone_SendsIanaZoneWithTheCurrentOffset()
    {
        var india = TimeZoneInfo.CreateCustomTimeZone("Asia/Kolkata", TimeSpan.FromMinutes(330), "India", "India");
        var parameters = new SessionsUsageQuery { StartDate = "2026-10-26", EndDate = "2026-10-26" }.ToParameters(india, Now);

        Assert.Equal("specific", parameters["mode"]);
        Assert.Equal("Asia/Kolkata", parameters["timeZone"]);
        Assert.Equal("UTC+5:30", parameters["utcOffset"]);
    }

    [Fact]
    public void LocalTimeZone_WithoutIanaMappingSendsOnlyTheOffset()
    {
        var custom = TimeZoneInfo.CreateCustomTimeZone("Fixture Custom Zone", TimeSpan.FromHours(-4), "Custom", "Custom");
        var parameters = new SessionsUsageQuery { StartDate = "2026-10-26", EndDate = "2026-10-26" }.ToParameters(custom, Now);

        Assert.Equal("specific", parameters["mode"]);
        Assert.False(parameters.ContainsKey("timeZone"));
        Assert.Equal("UTC-4", parameters["utcOffset"]);
    }

    [Theory]
    [InlineData(0, "UTC+0")]
    [InlineData(330, "UTC+5:30")]
    [InlineData(-570, "UTC-9:30")]
    [InlineData(840, "UTC+14")]
    [InlineData(-720, "UTC-12")]
    public void FormatUtcOffset_UsesTheGatewayOffsetGrammar(int minutes, string expected) =>
        Assert.Equal(expected, SessionsUsageQuery.FormatUtcOffset(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void WithoutServerOverview_SendsOnlyParametersReleasedGatewaysAccept()
    {
        var query = new SessionsUsageQuery
        {
            StartDate = "2026-10-20", EndDate = "2026-10-26", TimeZone = UsageTimeZoneMode.Utc,
            Scope = UsageScope.Family, CreatorKey = "human:ada", OverviewProjection = true, Query = "has:errors",
            SelectedDays = ["2026-10-21"], SelectedHours = [9], SelectedSessions = ["agent:main:a"],
            RecentKeys = ["agent:main:a"], Offset = 50, Sort = UsageSort.Tokens, SortDescending = false,
        };
        Assert.True(query.UsesServerOverview);

        var basic = query.WithoutServerOverview();

        Assert.False(basic.UsesServerOverview);
        // SessionsUsageParamsSchema in the published openclaw@2026.10.7 package.
        string[] released = ["key", "agentId", "agentScope", "creatorKey", "startDate", "endDate", "mode", "range",
            "groupBy", "includeHistorical", "utcOffset", "timeZone", "limit", "includeContextWeight"];
        var parameters = basic.ToParameters();
        Assert.All(parameters.Keys, name => Assert.Contains(name, released));
        Assert.Equal(SessionsUsageQuery.FullRowLimit, parameters["limit"]);
        Assert.Equal("family", parameters["groupBy"]);
        Assert.Equal("human:ada", parameters["creatorKey"]);
        Assert.False(new SessionsUsageQuery { StartDate = "2026-10-26", EndDate = "2026-10-26", Key = "k", Limit = 1 }.UsesServerOverview);
    }

    private static string Serialize(Dictionary<string, object?> parameters) => JsonSerializer.Serialize(parameters);
}
