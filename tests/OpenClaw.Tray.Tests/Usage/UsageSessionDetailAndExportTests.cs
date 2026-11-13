using OpenClaw.Shared.Usage;
using OpenClawTray.Presentation.Usage;

namespace OpenClaw.Tray.Tests.Usage;

public sealed class UsageSessionDetailAndExportTests
{
    [Theory]
    [InlineData("complete", 1d)]
    [InlineData("forty-percent", 0.4d)]
    [InlineData("partial", null)]
    [InlineData("stale", null)]
    [InlineData("pending", null)]
    [InlineData("cache-partial", null)]
    [InlineData("overview-lower-bound", null)]
    [InlineData("computing", null)]
    [InlineData("missing-price", null)]
    [InlineData("lower-bound", null)]
    public void SessionCostShare_RequiresACompletePricedRange(string condition, double? expected)
    {
        var totals = new UsageTotals { TotalCost = 5, Report = UsageReportState.Available };
        var session = new SessionUsageRow
        {
            Key = "completed",
            Usage = new() { Totals = totals with { TotalCost = condition == "forty-percent" ? 2 : 5 } },
        };
        var report = new SessionsUsageReport { Totals = totals, Sessions = [session] };
        report = condition switch
        {
            "partial" => report with { Partial = new("time-budget", "Incomplete range") },
            "stale" => report with { CacheStatus = new("stale", 0, 0, null) },
            "pending" => report with { CacheStatus = new("fresh", 1, 0, null) },
            "cache-partial" => report with { CacheStatus = new("fresh", 0, 0, new("time-budget", "Incomplete cache")) },
            "overview-lower-bound" => report with { Overview = new() { Report = UsageReportState.Unavailable } },
            "computing" => report with { Sessions = [session, new() { Key = "pending", Computing = true }] },
            "missing-price" => report with { Totals = totals with { MissingCostEntries = 1 } },
            "lower-bound" => report with { Totals = totals with { Report = UsageReportState.Unavailable } },
            _ => report,
        };

        Assert.Equal(expected, UsageSessionDetailProjection.CostShare(report, session));
        Assert.Null(UsageSessionDetailProjection.CostShare(report, session with { Computing = true }));
        Assert.Null(UsageSessionDetailProjection.CostShare(report, session with
        {
            Usage = new() { Totals = totals with { MissingCostEntries = 1 } },
        }));
    }

    [Fact]
    public void SessionsCsv_UsesUpstreamColumnsBlanksLowerBoundsAndGuardsFormulas()
    {
        var csv = UsageExport.SessionsCsv(
        [
            new SessionUsageRow
            {
                Key = "agent:main:a", Label = "=SUM(A1), \"quoted\"", AgentId = "main", ModelProvider = "openai", Model = "gpt",
                UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(1792540800000),
                Usage = new SessionUsageSummary
                {
                    DurationMs = 60_000,
                    MessageCounts = new UsageMessageCounts(4, 2, 2, 1, 1, 0),
                    Totals = new UsageTotals { Input = 1, Output = 2, TotalTokens = 3, TotalCost = 0.5, Report = UsageReportState.Unavailable },
                },
            },
        ]);

        var lines = csv.Split('\n');
        Assert.Equal("key,label,agentId,channel,provider,model,updatedAt,durationMs,messages,errors,toolCalls,inputTokens,outputTokens,cacheReadTokens,cacheWriteTokens,totalTokens,totalCost", lines[0]);
        Assert.Equal("agent:main:a,\"'=SUM(A1), \"\"quoted\"\"\",main,,openai,gpt,2026-10-21T00:00:00.000Z,60000,4,0,1,,,,,,", lines[1]);
    }

    [Fact]
    public void DailyCsv_WritesInvariantNumbers()
    {
        var csv = UsageExport.DailyCsv([new UsageDailyTotals("2026-10-26",
            new UsageTotals { Input = 1000, Output = 10, TotalTokens = 1010, InputCost = 0.0025, TotalCost = 0.0025, Report = UsageReportState.Available })]);

        Assert.Equal("2026-10-26,1000,10,0,0,1010,0.0025,0,0,0,0.0025", csv.Split('\n')[1]);
    }

    [Fact]
    public void Export_RefusesIncompleteOrCutShortResults()
    {
        var complete = new SessionsUsageReport { Totals = new UsageTotals(), CacheStatus = new UsageCacheStatus("fresh", 0, 0, null) };

        Assert.True(UsageExport.IsExportable(complete));
        Assert.False(UsageExport.IsExportable(complete with { CacheStatus = new UsageCacheStatus("partial", 2, 0, null) }));
        Assert.False(UsageExport.IsExportable(complete with { Partial = new UsagePartial("time-budget", "") }));
        var filters = new UsageFilters { Start = new(2026, 10, 26), End = new(2026, 10, 26) };
        Assert.Equal("usage-sessions-2026-10-26.csv", UsageExport.FileName(UsageExportFormat.SessionsCsv, filters));
        Assert.Equal("usage-daily-2026-10-26.csv", UsageExport.FileName(UsageExportFormat.DailyCsv, filters));
        Assert.Equal("usage-daily-2026-10-20_to_2026-10-26.csv",
            UsageExport.FileName(UsageExportFormat.DailyCsv, filters with { Start = new(2026, 10, 20) }));
    }

    [Fact]
    public void SessionsExport_IsTruncatedWhenTheGatewayCountsMoreSessionsThanItReturned()
    {
        var rows = new SessionUsageRow[] { new() { Key = "a" }, new() { Key = "b" } };

        Assert.False(UsageExport.IsTruncated(new SessionsUsageReport { Sessions = rows, Aggregates = new UsageAggregates { SessionCount = 2 } }));
        Assert.True(UsageExport.IsTruncated(new SessionsUsageReport { Sessions = rows, Aggregates = new UsageAggregates { SessionCount = 3 } }));
        Assert.False(UsageExport.IsTruncated(new SessionsUsageReport { Sessions = rows, Overview = new UsageOverview { Total = 2 } }));
        Assert.True(UsageExport.IsTruncated(new SessionsUsageReport { Sessions = rows, Overview = new UsageOverview { Total = 3 } }));
        // Without a count, a full page at the read limit may have dropped rows.
        var full = Enumerable.Range(0, SessionsUsageQuery.FullRowLimit).Select(i => new SessionUsageRow { Key = "k" + i }).ToArray();
        Assert.True(UsageExport.IsTruncated(new SessionsUsageReport { Sessions = full }));
    }
}
