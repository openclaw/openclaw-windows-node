using System.Globalization;
using OpenClaw.Shared.Usage;

namespace OpenClawTray.Presentation.Usage;

internal enum UsagePreset
{
    Today,
    Last7Days,
    Last30Days,
    Custom,
}

internal enum UsageMetric
{
    Tokens,
    Cost,
}

/// <summary>
/// Immutable Usage page query state: a calendar range in the reporting time zone, the session scope, and the
/// search, sort and page that only Gateways with the server-side overview apply.
/// </summary>
internal sealed record UsageFilters
{
    public UsagePreset Preset { get; init; } = UsagePreset.Last7Days;
    public DateOnly Start { get; init; }
    public DateOnly End { get; init; }
    public UsageTimeZoneMode TimeZone { get; init; } = UsageTimeZoneMode.Local;
    public UsageScope Scope { get; init; } = UsageScope.Instance;
    public string Query { get; init; } = "";
    public UsageSort Sort { get; init; } = UsageSort.Cost;
    public bool SortDescending { get; init; } = true;
    public int Offset { get; init; }

    public static UsageFilters Default(DateOnly today) => ForPreset(new UsageFilters(), UsagePreset.Last7Days, today);

    public static UsageFilters ForPreset(UsageFilters current, UsagePreset preset, DateOnly today)
    {
        var (start, end) = preset switch
        {
            UsagePreset.Today => (today, today),
            UsagePreset.Last7Days => (today.AddDays(-6), today),
            UsagePreset.Last30Days => (today.AddDays(-29), today),
            _ => (current.Start, current.End),
        };
        return current with { Preset = preset, Start = start, End = end, Offset = 0 };
    }

    /// <summary>Custom ranges are normalized so start never exceeds end.</summary>
    public UsageFilters WithRange(DateOnly start, DateOnly end) =>
        this with
        {
            Preset = UsagePreset.Custom,
            Start = start <= end ? start : end,
            End = start <= end ? end : start,
            Offset = 0,
        };

    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);

    /// <summary>The filters every Gateway with <c>sessions.usage</c> applies: range, time zone and scope.</summary>
    public UsageFilters WithoutServerFilters() => this with { Query = "", Offset = 0 };

    public int RangeDays => End.DayNumber - Start.DayNumber + 1;

    public SessionsUsageQuery ToOverviewQuery() => new()
    {
        StartDate = Iso(Start),
        EndDate = Iso(End),
        TimeZone = TimeZone,
        Scope = Scope,
        Query = HasQuery ? Query.Trim() : null,
        Offset = Offset,
        Sort = Sort,
        SortDescending = SortDescending,
        OverviewProjection = true,
    };

    public SessionsUsageQuery ToDetailQuery(string key, string? agentId) => new()
    {
        StartDate = Iso(Start),
        EndDate = Iso(End),
        TimeZone = TimeZone,
        Scope = Scope,
        AgentId = agentId,
        Key = key,
    };

    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
