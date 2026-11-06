namespace OpenClaw.Shared.Usage;

// Immutable mirrors of the Gateway usage payloads read by the Usage page. Only the fields the page renders are kept;
// unknown or unused fields are ignored by UsageJsonParser.
// Upstream sources (openclaw/openclaw@5089aa71): src/shared/usage-types.ts,
// src/infra/session-cost-usage.types.ts and src/infra/provider-usage.types.ts.

/// <summary>Completeness of a usage subtotal. <see cref="Unavailable"/> means the numbers are a lower bound.</summary>
public enum UsageReportState
{
    Unknown,
    Available,
    Unavailable,
}

public sealed record UsageTotals
{
    public static readonly UsageTotals Empty = new() { ReportedFields = new HashSet<string>() };

    /// <summary>Wire counters present with valid numeric values. Null denotes a complete typed subtotal.</summary>
    public IReadOnlySet<string>? ReportedFields { get; init; }
    public bool HasValue(string field) => ReportedFields is null || ReportedFields.Contains(field);

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
    /// <summary>Usage entries the Gateway could not price. Their cost is unknown, not zero.</summary>
    public int MissingCostEntries { get; init; }

    /// <summary>True when a response ended before its final usage arrived, so values are known subtotals.</summary>
    public bool IsLowerBound => Report == UsageReportState.Unavailable;
}

public sealed record UsageDailyTotals(string Date, UsageTotals Totals);

public sealed record UsageMessageCounts(int Total, int User, int Assistant, int ToolCalls, int ToolResults, int Errors)
{
    public static readonly UsageMessageCounts Empty = new(0, 0, 0, 0, 0, 0) { HasData = false };
    public bool HasData { get; init; } = true;
}

public sealed record UsageToolSummary(int TotalCalls, int UniqueTools)
{
    public static readonly UsageToolSummary Empty = new(0, 0) { HasData = false };
    public bool HasData { get; init; } = true;
}

public sealed record UsageModelBreakdown(string? Provider, string? Model, int Count, UsageTotals Totals);

/// <summary>A labelled totals bucket, used for agents.</summary>
public sealed record UsageNamedTotals(string Name, UsageTotals Totals);

public sealed record UsageAggregates
{
    public static readonly UsageAggregates Empty = new();

    public int? SessionCount { get; init; }
    public UsageMessageCounts Messages { get; init; } = UsageMessageCounts.Empty;
    public UsageToolSummary Tools { get; init; } = UsageToolSummary.Empty;
    public IReadOnlyList<UsageModelBreakdown> ByModel { get; init; } = Array.Empty<UsageModelBreakdown>();
    public IReadOnlyList<UsageModelBreakdown> ByProvider { get; init; } = Array.Empty<UsageModelBreakdown>();
    public IReadOnlyList<UsageNamedTotals> ByAgent { get; init; } = Array.Empty<UsageNamedTotals>();
    public IReadOnlyList<UsageDailyTotals> CostDaily { get; init; } = Array.Empty<UsageDailyTotals>();
}

/// <summary>Time-budget or aggregate-boundary truncation reported by the Gateway.</summary>
public sealed record UsagePartial(string Reason, string Message);

/// <summary>Usage cache freshness (<c>fresh|partial|stale|refreshing</c>).</summary>
public sealed record UsageCacheStatus(string Status, int PendingFiles, int StaleFiles, UsagePartial? Partial)
{
    public bool IsIncomplete => Status is "partial" or "stale" or "refreshing" || PendingFiles > 0 || StaleFiles > 0;
}

/// <summary>Per-session usage (<c>SessionCostSummary</c>): totals, activity and counts.</summary>
public sealed record SessionUsageSummary
{
    public UsageTotals Totals { get; init; } = UsageTotals.Empty;
    public DateTimeOffset? FirstActivity { get; init; }
    public DateTimeOffset? LastActivity { get; init; }
    public long? DurationMs { get; init; }
    public UsageMessageCounts? MessageCounts { get; init; }
    public UsageToolSummary? ToolUsage { get; init; }
}

public sealed record SessionUsageRow
{
    public string Key { get; init; } = "";
    public string? Label { get; init; }
    public string? Scope { get; init; }
    public int? HistoricalInstanceCount { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? AgentId { get; init; }
    public string? Channel { get; init; }
    public string? ModelOverride { get; init; }
    public string? ProviderOverride { get; init; }
    public string? ModelProvider { get; init; }
    public string? Model { get; init; }
    /// <summary>Null when the Gateway has not computed this session's usage yet.</summary>
    public SessionUsageSummary? Usage { get; init; }
    public bool Computing { get; init; }

    public string? EffectiveProvider => ProviderOverride ?? ModelProvider;
    public string? EffectiveModel => ModelOverride ?? Model;
}

/// <summary>Server-computed overview projection (<c>projection: "overview"</c>), returned by newer Gateways only.</summary>
public sealed record UsageOverview
{
    public UsageReportState Report { get; init; }
    public int Total { get; init; }
    public int UnfilteredSessionCount { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; }
    public IReadOnlyList<string> QueryWarnings { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, IReadOnlyList<string>> FilterOptions { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();
}

/// <summary><c>sessions.usage</c> result.</summary>
public sealed record SessionsUsageReport
{
    public bool SessionsKnown { get; init; } = true;
    public DateTimeOffset? UpdatedAt { get; init; }
    public string StartDate { get; init; } = "";
    public string EndDate { get; init; } = "";
    public IReadOnlyList<SessionUsageRow> Sessions { get; init; } = Array.Empty<SessionUsageRow>();
    public UsageTotals Totals { get; init; } = UsageTotals.Empty;
    public UsageAggregates Aggregates { get; init; } = UsageAggregates.Empty;
    public UsageCacheStatus? CacheStatus { get; init; }
    public UsageOverview? Overview { get; init; }
    public UsagePartial? Partial { get; init; }
}
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
