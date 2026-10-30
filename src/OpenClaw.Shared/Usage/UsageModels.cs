namespace OpenClaw.Shared.Usage;

// Immutable mirrors of the Gateway usage payloads read by the Usage page.
// Upstream sources (openclaw/openclaw@5089aa71): src/shared/usage-types.ts,
// src/infra/session-cost-usage.types.ts, src/shared/session-usage-timeseries-types.ts,
// src/config/sessions/session-system-prompt-report.ts and src/infra/provider-usage.types.ts.

/// <summary>Completeness of a usage subtotal. <see cref="Unavailable"/> means the numbers are a lower bound.</summary>
public enum UsageReportState
{
    Unknown,
    Available,
    Unavailable,
}

public sealed record UsageTotals
{
    public static readonly UsageTotals Empty = new();

    public UsageReportState Report { get; init; }
    public long Input { get; init; }
    public long Output { get; init; }
    public long CacheRead { get; init; }
    public long CacheWrite { get; init; }
    public long TotalTokens { get; init; }
    public double TotalCost { get; init; }
    public double InputCost { get; init; }
    public double OutputCost { get; init; }
    public double CacheReadCost { get; init; }
    public double CacheWriteCost { get; init; }
    public int MissingCostEntries { get; init; }

    /// <summary>True when a response ended before its final usage arrived, so values are known subtotals.</summary>
    public bool IsLowerBound => Report == UsageReportState.Unavailable;
}

public sealed record UsageDailyTotals(string Date, UsageTotals Totals);

public sealed record UsageMessageCounts(int Total, int User, int Assistant, int ToolCalls, int ToolResults, int Errors)
{
    public static readonly UsageMessageCounts Empty = new(0, 0, 0, 0, 0, 0);
}

public sealed record UsageToolCount(string Name, int Count);

public sealed record UsageToolSummary(int TotalCalls, int UniqueTools, IReadOnlyList<UsageToolCount> Tools)
{
    public static readonly UsageToolSummary Empty = new(0, 0, Array.Empty<UsageToolCount>());
}

public sealed record UsageModelBreakdown(string? Provider, string? Model, int Count, UsageTotals Totals);

/// <summary>A labelled totals bucket, used for agents and channels.</summary>
public sealed record UsageNamedTotals(string Name, UsageTotals Totals);

public sealed record UsageLatencyStats(int Count, double AvgMs, double P95Ms);

public sealed record UsageDailyModel(
    string Date, string? Provider, string? Model, long Tokens, double Cost, int Count, UsageReportState Report);

public sealed record UsageDailyActivity(
    string Date, long Tokens, double Cost, int Messages, int ToolCalls, int Errors, UsageReportState Report);

/// <summary>Immutable creator of a session (<c>SessionCreatedActor</c>). Not per-turn billing attribution.</summary>
public sealed record UsageCreatedActor(string Type, string? Id, string? Label);

public sealed record UsageCreator(string Key, UsageCreatedActor? Actor);

public sealed record UsageCreatorAggregate(
    UsageCreator Creator,
    UsageTotals Totals,
    int SessionCount,
    IReadOnlyList<UsageDailyTotals> Daily);

public sealed record UsageAggregates
{
    public static readonly UsageAggregates Empty = new();

    public int? SessionCount { get; init; }
    public UsageMessageCounts Messages { get; init; } = UsageMessageCounts.Empty;
    public UsageToolSummary Tools { get; init; } = UsageToolSummary.Empty;
    public IReadOnlyList<UsageModelBreakdown> ByModel { get; init; } = Array.Empty<UsageModelBreakdown>();
    public IReadOnlyList<UsageModelBreakdown> ByProvider { get; init; } = Array.Empty<UsageModelBreakdown>();
    public IReadOnlyList<UsageNamedTotals> ByAgent { get; init; } = Array.Empty<UsageNamedTotals>();
    public IReadOnlyList<UsageNamedTotals> ByChannel { get; init; } = Array.Empty<UsageNamedTotals>();
    public IReadOnlyList<UsageCreatorAggregate> ByCreator { get; init; } = Array.Empty<UsageCreatorAggregate>();
    public IReadOnlyList<UsageDailyTotals> CostDaily { get; init; } = Array.Empty<UsageDailyTotals>();
    public IReadOnlyList<UsageDailyModel> ModelDaily { get; init; } = Array.Empty<UsageDailyModel>();
    public IReadOnlyList<UsageDailyActivity> Daily { get; init; } = Array.Empty<UsageDailyActivity>();
}

/// <summary>Time-budget or aggregate-boundary truncation reported by the Gateway.</summary>
public sealed record UsagePartial(string Reason, string Message);

/// <summary>Usage cache freshness (<c>fresh|partial|stale|refreshing</c>).</summary>
public sealed record UsageCacheStatus(string Status, int PendingFiles, int StaleFiles, UsagePartial? Partial)
{
    public bool IsIncomplete => Status is "partial" or "stale" or "refreshing" || PendingFiles > 0 || StaleFiles > 0;
}

/// <summary>Per-session usage (<c>SessionCostSummary</c>): totals plus activity, counts and breakdowns.</summary>
public sealed record SessionUsageSummary
{
    public UsageTotals Totals { get; init; } = UsageTotals.Empty;
    public bool Refreshing { get; init; }
    public DateTimeOffset? FirstActivity { get; init; }
    public DateTimeOffset? LastActivity { get; init; }
    public long? DurationMs { get; init; }
    public UsageMessageCounts? MessageCounts { get; init; }
    public UsageToolSummary? ToolUsage { get; init; }
    public IReadOnlyList<UsageModelBreakdown> ModelUsage { get; init; } = Array.Empty<UsageModelBreakdown>();
    public UsageLatencyStats? Latency { get; init; }
}

public sealed record UsageContextFile(string Name, long? InjectedChars);

public sealed record UsageContextSkill(string Name, long BlockChars);

public sealed record UsageContextTool(string Name, string? Source, long SummaryChars, long SchemaChars);

/// <summary>Size of the assembled system prompt (<c>SessionSystemPromptReport</c>), in characters.</summary>
public sealed record UsageContextWeight
{
    public string Source { get; init; } = "";
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public long SystemPromptChars { get; init; }
    public long SkillsPromptChars { get; init; }
    public IReadOnlyList<UsageContextSkill> Skills { get; init; } = Array.Empty<UsageContextSkill>();
    public long ToolListChars { get; init; }
    public long ToolSchemaChars { get; init; }
    public IReadOnlyList<UsageContextTool> Tools { get; init; } = Array.Empty<UsageContextTool>();
    public IReadOnlyList<UsageContextFile> Files { get; init; } = Array.Empty<UsageContextFile>();
}

public sealed record SessionUsageRow
{
    public string Key { get; init; } = "";
    public string? Label { get; init; }
    public string? Scope { get; init; }
    public int? HistoricalInstanceCount { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? AgentId { get; init; }
    public UsageCreatedActor? CreatedActor { get; init; }
    public string? CreatorKey { get; init; }
    public string? Channel { get; init; }
    public string? ModelOverride { get; init; }
    public string? ProviderOverride { get; init; }
    public string? ModelProvider { get; init; }
    public string? Model { get; init; }
    /// <summary>Null when the Gateway has not computed this session's usage yet.</summary>
    public SessionUsageSummary? Usage { get; init; }
    public bool Computing { get; init; }
    public UsageContextWeight? ContextWeight { get; init; }

    public string? EffectiveProvider => ProviderOverride ?? ModelProvider;
    public string? EffectiveModel => ModelOverride ?? Model;
}

public sealed record UsageTableTotals(long Tokens, double Cost, int Errors, UsageReportState Report);

/// <summary>Server-computed overview projection (<c>projection: "overview"</c>).</summary>
public sealed record UsageOverview
{
    public UsageReportState Report { get; init; }
    public int Total { get; init; }
    public int UnfilteredSessionCount { get; init; }
    public bool? SelectedUsageComplete { get; init; }
    public IReadOnlyList<SessionUsageRow> SelectedRows { get; init; } = Array.Empty<SessionUsageRow>();
    public int TableSessionCount { get; init; }
    public UsageTableTotals TableTotals { get; init; } = new(0, 0, 0, UsageReportState.Unknown);
    public int Offset { get; init; }
    public int Limit { get; init; }
    public IReadOnlyList<string> QueryWarnings { get; init; } = Array.Empty<string>();
    /// <summary>24 token totals by hour of day in the requested time zone.</summary>
    public IReadOnlyList<long> HourTokens { get; init; } = Array.Empty<long>();
    /// <summary>7 token totals by weekday, Sunday first.</summary>
    public IReadOnlyList<long> WeekdayTokens { get; init; } = Array.Empty<long>();
    public bool HasTimelineData { get; init; }
    public long DurationMs { get; init; }
    public int DurationCount { get; init; }
    public IReadOnlyList<long> HourlyMessages { get; init; } = Array.Empty<long>();
    public IReadOnlyList<long> HourlyErrors { get; init; } = Array.Empty<long>();
    public IReadOnlyDictionary<string, IReadOnlyList<string>> FilterOptions { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();
}

/// <summary><c>sessions.usage</c> result.</summary>
public sealed record SessionsUsageReport
{
    public DateTimeOffset? UpdatedAt { get; init; }
    public string StartDate { get; init; } = "";
    public string EndDate { get; init; } = "";
    public IReadOnlyList<SessionUsageRow> Sessions { get; init; } = Array.Empty<SessionUsageRow>();
    public UsageTotals Totals { get; init; } = UsageTotals.Empty;
    public UsageAggregates Aggregates { get; init; } = UsageAggregates.Empty;
    public IReadOnlyList<UsageCreator> CreatorOptions { get; init; } = Array.Empty<UsageCreator>();
    public UsageCacheStatus? CacheStatus { get; init; }
    public UsageOverview? Overview { get; init; }
    public UsagePartial? Partial { get; init; }

    /// <summary>The unmodified payload, kept so a user-requested JSON export matches the Gateway result.</summary>
    public System.Text.Json.JsonElement? Raw { get; init; }
}

public sealed record UsageTimePoint(
    DateTimeOffset Timestamp, long Input, long Output, long CacheRead, long CacheWrite, long TotalTokens,
    double Cost, long CumulativeTokens, double CumulativeCost);

/// <summary><c>sessions.usage.timeseries</c> result (at most 200 points).</summary>
public sealed record SessionUsageTimeseries(UsageReportState Report, string? SessionId, IReadOnlyList<UsageTimePoint> Points);

/// <summary>One <c>sessions.usage.logs</c> entry. Contains conversation content; never log or export it.</summary>
public sealed record SessionUsageLogEntry(DateTimeOffset Timestamp, string Role, string Content, long? Tokens, double? Cost);

public sealed record ProviderUsageWindow(string Label, string? GroupLabel, double UsedPercent, DateTimeOffset? ResetAt);

/// <summary>Provider billing fact: <c>balance</c>, <c>spend</c> or <c>budget</c>.</summary>
public sealed record ProviderUsageBilling(
    string Type, string? Label, double Amount, double? Limit, string Unit, DateTimeOffset? ResetAt);

public sealed record ProviderUsageCostDay(string Date, double Amount);

public sealed record ProviderUsageCostHistory(string Unit, int PeriodDays, IReadOnlyList<ProviderUsageCostDay> Daily);

public sealed record ProviderUsageSnapshot
{
    public string Provider { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public IReadOnlyList<ProviderUsageWindow> Windows { get; init; } = Array.Empty<ProviderUsageWindow>();
    public IReadOnlyList<ProviderUsageBilling> Billing { get; init; } = Array.Empty<ProviderUsageBilling>();
    public ProviderUsageCostHistory? CostHistory { get; init; }
    public string? Summary { get; init; }
    public string? Plan { get; init; }
    public string? AccountEmail { get; init; }
    public string? Error { get; init; }
}

/// <summary><c>usage.status</c> result: provider plans, quotas and billing.</summary>
public sealed record ProviderUsageSummary(
    DateTimeOffset? UpdatedAt, IReadOnlyList<ProviderUsageSnapshot> Providers, bool Refreshing);
