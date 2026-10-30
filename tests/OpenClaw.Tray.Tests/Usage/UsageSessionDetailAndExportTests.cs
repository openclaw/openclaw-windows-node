using System.Text.Json;
using OpenClaw.Shared.Usage;
using OpenClawTray.Presentation.Usage;

namespace OpenClaw.Tray.Tests.Usage;

public sealed class UsageSessionDetailAndExportTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Context_SubtractsInjectedPartsFromTheSystemPromptAndEstimatesTokens()
    {
        var row = new SessionUsageRow
        {
            Key = "k",
            Usage = new SessionUsageSummary { Totals = new UsageTotals { Input = 3_000, CacheRead = 1_000, TotalTokens = 4_000, Report = UsageReportState.Available } },
            ContextWeight = new UsageContextWeight
            {
                SystemPromptChars = 20_000,
                SkillsPromptChars = 2_000,
                Skills = [new UsageContextSkill("small", 400), new UsageContextSkill("big", 1_600)],
                ToolListChars = 1_000,
                ToolSchemaChars = 3_000,
                Files = [new UsageContextFile("AGENTS.md", 4_000), new UsageContextFile("native", null)],
            },
        };

        var context = UsageSessionDetailProjection.Context(row)!;

        // System = 20000 - 4000 files - 2000 skills - 1000 tool list = 13000 chars = 3250 tokens.
        Assert.Equal([3250, 500, 1000, 1000], context.Groups.Select(g => g.Tokens));
        Assert.Equal(5750, context.TotalTokens);
        Assert.Equal(1, context.InputShare);
        Assert.Equal(["big", "small"], context.Groups[1].Entries.Select(e => e.Name));
        Assert.Equal(["AGENTS.md", "native"], context.Groups[3].Entries.Select(e => e.Name));
    }

    [Fact]
    public void Context_IsAbsentWithoutAReport() =>
        Assert.Null(UsageSessionDetailProjection.Context(new SessionUsageRow { Key = "k" }));

    [Fact]
    public void Timeline_CumulativeUsesRunningTotalsAndPerTurnByTypeStacksTokens()
    {
        var series = new SessionUsageTimeseries(UsageReportState.Unavailable, "s",
        [
            new UsageTimePoint(At, 10, 5, 3, 2, 20, 0.1, 20, 0.1),
            new UsageTimePoint(At.AddMinutes(5), 30, 10, 0, 0, 40, 0.3, 60, 0.4),
        ]);

        var cumulative = UsageSessionDetailProjection.Timeline(series, UsageMetric.Tokens, UsageTimelineMode.Cumulative, byType: true);
        Assert.Equal([20, 60], cumulative.Bars.Select(b => b.Total));
        Assert.True(cumulative.LowerBound);
        Assert.Equal(100, cumulative.Ceiling);

        var perTurn = UsageSessionDetailProjection.Timeline(series, UsageMetric.Tokens, UsageTimelineMode.PerTurn, byType: true);
        Assert.Equal([5, 10, 2, 3], perTurn.Bars[0].Segments.Select(s => s.Value));
        var cost = UsageSessionDetailProjection.Timeline(series, UsageMetric.Cost, UsageTimelineMode.PerTurn, byType: false);
        Assert.Equal([0.1, 0.3], cost.Bars.Select(b => b.Total));
    }

    [Fact]
    public void Logs_StripToolMarkersCountToolsAndFilter()
    {
        var logs = new[]
        {
            new SessionUsageLogEntry(At, "user", "Find the report", 10, null),
            new SessionUsageLogEntry(At.AddSeconds(5), "assistant", "Looking.\n[Tool: read]\n[Tool: read]\n[Tool: exec]", 20, 0.01),
            new SessionUsageLogEntry(At.AddSeconds(9), "toolResult", "[Tool: read]\n[Tool Result]", null, null),
        }.Select(UsageSessionDetailProjection.ParseLog).ToList();

        Assert.Equal("Looking.", logs[1].Content);
        Assert.Equal([("read", 2), ("exec", 1)], logs[1].Tools);
        Assert.Equal("[Tool: read]\n[Tool Result]", logs[2].Content);

        Assert.Equal(2, UsageSessionDetailProjection.FilterLogs(logs, new UsageLogFilter(HasToolsOnly: true)).Count);
        Assert.Single(UsageSessionDetailProjection.FilterLogs(logs, new UsageLogFilter(Role: "assistant", Tool: "exec")));
        Assert.Equal("user", Assert.Single(UsageSessionDetailProjection.FilterLogs(logs, new UsageLogFilter(Search: "REPORT"))).Role);
    }

    [Fact]
    public void ModelMix_SharesCostAcrossModels()
    {
        var row = new SessionUsageRow
        {
            Key = "k",
            Usage = new SessionUsageSummary
            {
                ModelUsage =
                [
                    new UsageModelBreakdown("openai", "small", 3, new UsageTotals { TotalCost = 1, TotalTokens = 900 }),
                    new UsageModelBreakdown("anthropic", "large", 1, new UsageTotals { TotalCost = 3, TotalTokens = 100 }),
                ],
            },
        };

        var mix = UsageSessionDetailProjection.ModelMix(row);

        Assert.Equal(["large", "small"], mix.Select(m => m.Model));
        Assert.Equal(new double?[] { 0.75, 0.25 }, mix.Select(m => m.Share));
    }

    [Fact]
    public void FilteredSummary_DoesNotSubstituteWholeRangeUsageWhenTheSessionIsAbsent()
    {
        var detail = new SessionUsageRow
        {
            Key = "k",
            Usage = new SessionUsageSummary { Totals = new UsageTotals { TotalTokens = 900 } },
            ContextWeight = new UsageContextWeight { SystemPromptChars = 400 },
        };
        var overview = detail with { Usage = new SessionUsageSummary { Totals = new UsageTotals { TotalTokens = 50 } }, ContextWeight = null };

        Assert.Null(UsageSessionDetailProjection.SummaryRow(null, detail, filtered: true));
        var filtered = UsageSessionDetailProjection.SummaryRow(overview, detail, filtered: true)!;
        Assert.Equal(50, filtered.Usage!.Totals.TotalTokens);
        Assert.Equal(400, filtered.ContextWeight!.SystemPromptChars);
        Assert.Same(detail, UsageSessionDetailProjection.SummaryRow(overview, detail, filtered: false));
    }

    [Fact]
    public void ModelMix_PreservesLowerBoundsAndWithholdsSharesWhenAnySubtotalIsIncomplete()
    {
        var row = new SessionUsageRow
        {
            Key = "k",
            Usage = new SessionUsageSummary
            {
                ModelUsage =
                [
                    new UsageModelBreakdown("openai", "partial", 1,
                        new UsageTotals { TotalTokens = 100, TotalCost = 3, Report = UsageReportState.Unavailable }),
                    new UsageModelBreakdown("anthropic", "complete", 2,
                        new UsageTotals { TotalTokens = 300, TotalCost = 1, Report = UsageReportState.Available }),
                ],
            },
        };

        var mix = UsageSessionDetailProjection.ModelMix(row);

        Assert.All(mix, m => Assert.Null(m.Share));
        Assert.True(Assert.Single(mix, m => m.Model == "partial").LowerBound);
        Assert.False(Assert.Single(mix, m => m.Model == "complete").LowerBound);
        Assert.Equal(100, Assert.Single(mix, m => m.Model == "partial").Tokens);
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
    public void Json_KeepsRawGatewayFieldsAndFiltersSelectedSessions()
    {
        var payload = JsonDocument.Parse("""
            { "sessions": [{ "key": "a", "extra": 1 }, { "key": "b" }, { "key": 7 }], "totals": { "totalTokens": 3 },
              "aggregates": { "costDaily": [{ "date": "2026-10-26", "totalCost": 1 }] } }
            """).RootElement;
        var report = UsageJsonParser.ParseSessionsUsage(payload);

        using var json = JsonDocument.Parse(UsageExport.Build(UsageExportFormat.Json, report, ["a"]));

        var session = Assert.Single(json.RootElement.GetProperty("sessions").EnumerateArray());
        Assert.Equal(1, session.GetProperty("extra").GetInt32());
        Assert.Equal(3, json.RootElement.GetProperty("totals").GetProperty("totalTokens").GetInt32());
        Assert.Equal("2026-10-26", json.RootElement.GetProperty("daily")[0].GetProperty("date").GetString());
    }

    [Fact]
    public void Export_RefusesIncompleteDataUnlessSelectionIsConfirmedComplete()
    {
        var incomplete = new SessionsUsageReport { CacheStatus = new UsageCacheStatus("partial", 2, 0, null) };

        Assert.False(UsageExport.IsExportable(incomplete));
        Assert.True(UsageExport.IsExportable(incomplete with { Overview = new UsageOverview { SelectedUsageComplete = true } }));
        Assert.Equal("openclaw-usage-sessions-2026-10-26.csv", UsageExport.FileName(UsageExportFormat.SessionsCsv, new DateOnly(2026, 10, 26)));
        Assert.Equal("openclaw-usage-2026-10-26.json", UsageExport.FileName(UsageExportFormat.Json, new DateOnly(2026, 10, 26)));
    }
}
