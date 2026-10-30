using System.Text.Json;
using OpenClaw.Shared.Usage;
using Xunit;

namespace OpenClaw.Shared.Tests.Usage;

/// <summary>
/// Pins the sessions.usage / timeseries / logs / usage.status projection against payloads shaped like
/// openclaw/openclaw@5089aa71 src/shared/usage-types.ts. Expected values are written out by hand.
/// </summary>
public sealed class UsageJsonParserTests
{
    private const string OverviewPayload = """
    {
      "updatedAt": 1792540800000,
      "startDate": "2026-10-20",
      "endDate": "2026-10-26",
      "sessions": [
        {
          "key": "agent:main:webchat:main",
          "label": "Main conversation",
          "sessionId": "s-1",
          "scope": "family",
          "sessionFamilyKey": "agent:main:webchat:main",
          "includedSessionIds": ["s-1", "s-0"],
          "historicalInstanceCount": 2,
          "updatedAt": 1792540000000,
          "agentId": "main",
          "createdActor": { "type": "human", "id": "ada", "label": "Ada", "identity": { "type": "profile", "id": "ada" } },
          "creatorKey": "profile:ada",
          "channel": "webchat",
          "origin": { "label": "Ada", "threadId": 42 },
          "providerOverride": "anthropic",
          "modelProvider": "openai",
          "model": "claude-sonnet-4.5",
          "hasContextWeight": true,
          "usage": {
            "input": 1000, "output": 200, "cacheRead": 300, "cacheWrite": 50, "totalTokens": 1550,
            "totalCost": 0.0125, "inputCost": 0.003, "outputCost": 0.003, "cacheReadCost": 0.0001, "cacheWriteCost": 0.0002,
            "missingCostEntries": 1, "missingCostByModel": { "openai/gpt-x": 1 },
            "usageReport": { "state": "unavailable" },
            "firstActivity": 1792450000000, "lastActivity": 1792540000000, "durationMs": 5400000,
            "activityDates": ["2026-10-24", "2026-10-25"],
            "dailyBreakdown": [{ "date": "2026-10-25", "input": 1000, "output": 200, "cacheRead": 300, "cacheWrite": 50, "totalTokens": 1550, "totalCost": 0.0125, "tokens": 1550, "cost": 0.0125 }],
            "messageCounts": { "total": 12, "user": 6, "assistant": 6, "toolCalls": 4, "toolResults": 4, "errors": 1 },
            "toolUsage": { "totalCalls": 4, "uniqueTools": 2, "tools": [{ "name": "read", "count": 3 }, { "name": "exec", "count": 1 }] },
            "modelUsage": [{ "provider": "anthropic", "model": "claude-sonnet-4.5", "count": 6, "totals": { "totalTokens": 1550, "totalCost": 0.0125 } }],
            "latency": { "count": 6, "avgMs": 2400, "p95Ms": 7000, "minMs": 600, "maxMs": 9000 }
          }
        },
        { "key": "agent:ops:cron:backup", "usage": null, "computing": true },
        { "label": "row without a key is not a session" },
        "not an object"
      ],
      "totals": {
        "input": 1000, "output": 200, "cacheRead": 300, "cacheWrite": 50, "totalTokens": 1550, "totalCost": 0.0125,
        "inputCost": 0.003, "outputCost": 0.003, "cacheReadCost": 0.0001, "cacheWriteCost": 0.0002, "missingCostEntries": 1,
        "usageReport": { "state": "unavailable" }
      },
      "aggregates": {
        "sessionCount": 2,
        "longestSessionDurationMs": 5400000,
        "messages": { "total": 12, "user": 6, "assistant": 6, "toolCalls": 4, "toolResults": 4, "errors": 1 },
        "tools": { "totalCalls": 4, "uniqueTools": 2, "tools": [{ "name": "read", "count": 3 }, { "name": "exec", "count": 1 }] },
        "byModel": [{ "provider": "anthropic", "model": "claude-sonnet-4.5", "count": 6, "totals": { "totalTokens": 1550, "totalCost": 0.0125 } }],
        "byProvider": [{ "provider": "anthropic", "count": 6, "totals": { "totalTokens": 1550, "totalCost": 0.0125 } }],
        "byAgent": [{ "agentId": "main", "totals": { "totalTokens": 1550, "totalCost": 0.0125 } }],
        "byChannel": [{ "channel": "webchat", "totals": { "totalTokens": 1550, "totalCost": 0.0125 } }],
        "byCreator": [{ "key": "profile:ada", "actor": { "type": "human", "label": "Ada" }, "totals": { "totalCost": 0.0125 }, "sessionCount": 1,
          "daily": [{ "date": "2026-10-25", "totalCost": 0.0125 }], "sessionActivity": [{ "dates": ["2026-10-25"], "sessionCount": 1 }] }],
        "costDaily": [{ "date": "2026-10-25", "input": 1000, "output": 200, "totalTokens": 1550, "totalCost": 0.0125, "outputCost": 0.003 }],
        "latency": { "count": 6, "avgMs": 2400, "p95Ms": 7000, "minMs": 600, "maxMs": 9000 },
        "dailyLatency": [{ "date": "2026-10-25", "count": 6, "avgMs": 2400, "p95Ms": 7000, "minMs": 600, "maxMs": 9000 }],
        "modelDaily": [{ "date": "2026-10-25", "provider": "anthropic", "model": "claude-sonnet-4.5", "tokens": 1550, "cost": 0.0125, "count": 6, "usageReport": { "state": "unavailable" } }],
        "daily": [{ "date": "2026-10-25", "tokens": 1550, "cost": 0.0125, "messages": 12, "toolCalls": 4, "errors": 1 }]
      },
      "creatorOptions": [{ "key": "profile:ada", "actor": { "type": "human", "label": "Ada" } }, { "key": "system" }],
      "cacheStatus": { "status": "partial", "cachedFiles": 9, "pendingFiles": 3, "staleFiles": 0, "refreshedAt": 1792540700000 },
      "partial": { "reason": "time-budget", "message": "Totals include transcripts read so far." },
      "overview": {
        "total": 2, "unfilteredSessionCount": 5, "selectedSessionCount": 1, "selectedRowCount": 1,
        "selectedUsageComplete": false,
        "selectedRows": [{ "key": "agent:main:webchat:main", "usage": null }],
        "tableSessionCount": 2,
        "tableTotals": { "tokens": 1550, "cost": 0.0125, "errors": 1 },
        "offset": 0, "limit": 50,
        "queryWarnings": ["Unsupported filter: foo:bar"],
        "hourTokens": [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 99],
        "weekdayTokens": [10, 20],
        "hasTimelineData": true,
        "durationMs": 5400000, "durationCount": 1,
        "hourlyMessages": [], "hourlyErrors": [],
        "filterOptions": { "agent": ["main", "ops"], "tool": ["exec", "read"] }
      }
    }
    """;

    [Fact]
    public void ParseSessionsUsage_ReadsOverviewRowsAggregatesAndCompleteness()
    {
        var report = UsageJsonParser.ParseSessionsUsage(Parse(OverviewPayload));

        Assert.Equal("2026-10-20", report.StartDate);
        Assert.Equal("2026-10-26", report.EndDate);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1792540800000), report.UpdatedAt);
        Assert.Equal(["agent:main:webchat:main", "agent:ops:cron:backup"], report.Sessions.Select(s => s.Key));

        var main = report.Sessions[0];
        Assert.Equal("anthropic", main.EffectiveProvider);
        Assert.Equal(2, main.HistoricalInstanceCount);
        Assert.Equal("human", main.CreatedActor!.Type);
        Assert.True(main.Usage!.Totals.IsLowerBound);
        Assert.Equal(1550, main.Usage.Totals.TotalTokens);
        Assert.Equal(0.0125, main.Usage.Totals.TotalCost, 6);
        Assert.Equal(5_400_000, main.Usage.DurationMs);
        Assert.Equal(new UsageMessageCounts(12, 6, 6, 4, 4, 1), main.Usage.MessageCounts);
        Assert.Equal(["read", "exec"], main.Usage.ToolUsage!.Tools.Select(t => t.Name));
        Assert.Equal(7000, main.Usage.Latency!.P95Ms);

        var pending = report.Sessions[1];
        Assert.Null(pending.Usage);
        Assert.True(pending.Computing);

        Assert.True(report.Totals.IsLowerBound);
        Assert.Equal("time-budget", report.Partial!.Reason);
        Assert.True(report.CacheStatus!.IsIncomplete);
        Assert.Equal(3, report.CacheStatus.PendingFiles);

        var aggregates = report.Aggregates;
        Assert.Equal(2, aggregates.SessionCount);
        Assert.Equal("claude-sonnet-4.5", Assert.Single(aggregates.ByModel).Model);
        Assert.Equal("main", Assert.Single(aggregates.ByAgent).Name);
        Assert.Equal("webchat", Assert.Single(aggregates.ByChannel).Name);
        Assert.Equal("profile:ada", Assert.Single(aggregates.ByCreator).Creator.Key);
        Assert.Equal(0.003, Assert.Single(aggregates.CostDaily).Totals.OutputCost, 6);
        Assert.Equal(UsageReportState.Unavailable, Assert.Single(aggregates.ModelDaily).Report);
        Assert.Equal(12, Assert.Single(aggregates.Daily).Messages);
        Assert.Equal(["profile:ada", "system"], report.CreatorOptions.Select(c => c.Key));
    }

    [Fact]
    public void ParseSessionsUsage_NormalizesOverviewVectorsToFixedLengths()
    {
        var overview = UsageJsonParser.ParseSessionsUsage(Parse(OverviewPayload)).Overview!;

        Assert.Equal(24, overview.HourTokens.Count);
        Assert.Equal(23, overview.HourTokens[23]);
        Assert.Equal([10, 20, 0, 0, 0, 0, 0], overview.WeekdayTokens);
        Assert.Equal(24, overview.HourlyMessages.Count);
        Assert.False(overview.SelectedUsageComplete);
        Assert.Equal(["Unsupported filter: foo:bar"], overview.QueryWarnings);
        Assert.Equal(["exec", "read"], overview.FilterOptions["tool"]);
        Assert.Equal(1550, overview.TableTotals.Tokens);
        Assert.Equal("agent:main:webchat:main", Assert.Single(overview.SelectedRows).Key);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("""{"sessions":"nope","totals":[],"aggregates":7,"overview":"x","cacheStatus":{},"partial":{}}""")]
    public void ParseSessionsUsage_MalformedShapesYieldAnEmptyReport(string json)
    {
        var report = UsageJsonParser.ParseSessionsUsage(Parse(json));

        Assert.Empty(report.Sessions);
        Assert.Equal(0, report.Totals.TotalTokens);
        Assert.Empty(report.Aggregates.ByModel);
        Assert.Null(report.Overview);
        Assert.Null(report.CacheStatus);
        Assert.Null(report.Partial);
    }

    [Fact]
    public void ParseSessionsUsage_WrongScalarKindsFallBackWithoutThrowing()
    {
        var report = UsageJsonParser.ParseSessionsUsage(Parse("""
            { "updatedAt": "soon", "totals": { "totalTokens": "12", "totalCost": true },
              "sessions": [{ "key": "k", "updatedAt": 1e300, "usage": { "durationMs": "long" } }] }
            """));

        Assert.Null(report.UpdatedAt);
        Assert.Equal(0, report.Totals.TotalTokens);
        Assert.Equal(0, report.Totals.TotalCost);
        var row = Assert.Single(report.Sessions);
        Assert.Null(row.UpdatedAt);
        Assert.Null(row.Usage!.DurationMs);
    }

    [Fact]
    public void ParseTimeseries_ReadsPointsAndCapsAtTwoHundred()
    {
        var points = string.Join(",", Enumerable.Range(0, 205).Select(i =>
            $$"""{"timestamp":{{1792450000000L + i * 60_000}},"input":10,"output":2,"cacheRead":3,"cacheWrite":1,"totalTokens":16,"cost":0.001,"cumulativeTokens":{{(i + 1) * 16}},"cumulativeCost":0.5}"""));
        var series = UsageJsonParser.ParseTimeseries(Parse(
            $$"""{"sessionId":"s-1","usageReport":{"state":"unavailable"},"points":[{{points}},{"input":5}]}"""));

        Assert.Equal("s-1", series.SessionId);
        Assert.Equal(UsageReportState.Unavailable, series.Report);
        Assert.Equal(200, series.Points.Count);
        Assert.Equal(16, series.Points[0].TotalTokens);
        Assert.Equal(3200, series.Points[^1].CumulativeTokens);
    }

    [Fact]
    public void ParseLogs_KeepsValidEntriesInOrder()
    {
        var logs = UsageJsonParser.ParseLogs(Parse("""
            { "logs": [
              { "timestamp": 1792450000000, "role": "user", "content": "Hi" },
              { "timestamp": 1792450001000, "role": "assistant", "content": "[Tool: read]\nDone", "tokens": 42, "cost": 0.002 },
              { "role": "assistant", "content": "missing timestamp" },
              { "timestamp": 1792450002000, "content": "missing role" }
            ] }
            """));

        Assert.Equal(["user", "assistant"], logs.Select(l => l.Role));
        Assert.Equal(42, logs[1].Tokens);
        Assert.Null(logs[0].Tokens);
    }

    [Fact]
    public void ParseProviderUsage_ReadsWindowsBillingCostHistoryAndErrors()
    {
        var summary = UsageJsonParser.ParseProviderUsage(Parse("""
            { "updatedAt": 1792540800000, "refreshing": true, "providers": [
              { "provider": "anthropic", "displayName": "Claude", "plan": "Max", "accountEmail": "a@example.test",
                "windows": [{ "label": "Week", "groupLabel": "Opus", "usedPercent": 61.5, "resetAt": 1792627200000 }] },
              { "provider": "openai", "windows": [],
                "billing": [
                  { "type": "balance", "amount": 37.42, "unit": "USD" },
                  { "type": "budget", "label": "Monthly", "used": 62.18, "limit": 150, "unit": "USD", "period": "month" },
                  { "type": "mystery", "amount": 1, "unit": "USD" }
                ],
                "costHistory": { "unit": "USD", "periodDays": 30, "daily": [{ "date": "2026-10-25", "amount": 1.5, "requests": 3, "totalTokens": 900 }],
                  "models": [{ "name": "gpt-5.1", "totalTokens": 900 }], "categories": [{ "name": "API", "amount": 1.5 }] } },
              { "provider": "zai", "error": "Usage endpoint returned 401." },
              { "displayName": "No provider id" }
            ] }
            """));

        Assert.True(summary.Refreshing);
        Assert.Equal(["anthropic", "openai", "zai"], summary.Providers.Select(p => p.Provider));
        var window = Assert.Single(summary.Providers[0].Windows);
        Assert.Equal("Opus", window.GroupLabel);
        Assert.Equal(61.5, window.UsedPercent);
        Assert.Equal("openai", summary.Providers[1].DisplayName);
        Assert.Equal(["balance", "budget"], summary.Providers[1].Billing.Select(b => b.Type));
        Assert.Equal(150, summary.Providers[1].Billing[1].Limit);
        Assert.Equal(62.18, summary.Providers[1].Billing[1].Amount);
        Assert.Equal(30, summary.Providers[1].CostHistory!.PeriodDays);
        Assert.Equal("Usage endpoint returned 401.", summary.Providers[2].Error);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
