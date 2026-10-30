using System.Globalization;
using OpenClaw.Shared.Usage;

namespace OpenClawTray.Presentation.Usage;

internal enum UsagePreset
{
    Today,
    Last7Days,
    Last30Days,
    Last90Days,
    LastYear,
    All,
    Custom,
}

internal enum UsageMetric
{
    Tokens,
    Cost,
}

internal enum UsageDailyChartMode
{
    Total,
    ByType,
    ByProvider,
}

/// <summary>
/// Immutable Usage page query state. Mirrors the upstream web filter state
/// (openclaw/openclaw@5089aa71 ui/src/pages/usage/usage-state-types.ts).
/// </summary>
internal sealed record UsageFilters
{
    /// <summary>Upstream "All" sends this start date.</summary>
    public static readonly DateOnly AllStart = new(1970, 1, 1);

    public UsagePreset Preset { get; init; } = UsagePreset.Last7Days;
    public DateOnly Start { get; init; }
    public DateOnly End { get; init; }
    public UsageTimeZoneMode TimeZone { get; init; } = UsageTimeZoneMode.Local;
    public UsageScope Scope { get; init; } = UsageScope.Instance;
    public string? CreatorKey { get; init; }
    public string Query { get; init; } = "";
    public IReadOnlyList<string> SelectedDays { get; init; } = Array.Empty<string>();
    public IReadOnlyList<int> SelectedHours { get; init; } = Array.Empty<int>();
    public IReadOnlyList<string> SelectedSessions { get; init; } = Array.Empty<string>();
    public UsageSort Sort { get; init; } = UsageSort.Cost;
    public bool SortDescending { get; init; } = true;
    public int Offset { get; init; }
    public bool RecentOnly { get; init; }

    public static UsageFilters Default(DateOnly today) => ForPreset(new UsageFilters(), UsagePreset.Last7Days, today);

    public static UsageFilters ForPreset(UsageFilters current, UsagePreset preset, DateOnly today)
    {
        var (start, end) = preset switch
        {
            UsagePreset.Today => (today, today),
            UsagePreset.Last7Days => (today.AddDays(-6), today),
            UsagePreset.Last30Days => (today.AddDays(-29), today),
            UsagePreset.Last90Days => (today.AddDays(-89), today),
            UsagePreset.LastYear => (today.AddDays(-364), today),
            UsagePreset.All => (AllStart, today),
            _ => (current.Start, current.End),
        };
        return current with { Preset = preset, Start = start, End = end, SelectedDays = [], Offset = 0 };
    }

    /// <summary>Custom ranges are normalized so start never exceeds end.</summary>
    public UsageFilters WithRange(DateOnly start, DateOnly end) =>
        this with
        {
            Preset = UsagePreset.Custom,
            Start = start <= end ? start : end,
            End = start <= end ? end : start,
            SelectedDays = [],
            Offset = 0,
        };

    public bool HasPopulationFilter =>
        !string.IsNullOrWhiteSpace(Query) || SelectedDays.Count > 0 || SelectedHours.Count > 0 || CreatorKey is not null;

    /// <summary>The filters released Gateways (2026.10.7) apply: range, time zone, scope and creator.</summary>
    public UsageFilters WithoutServerFilters() =>
        this with { Query = "", SelectedDays = [], SelectedHours = [], Offset = 0, RecentOnly = false };

    public int RangeDays => End.DayNumber - Start.DayNumber + 1;

    public SessionsUsageQuery ToOverviewQuery(IReadOnlyList<string>? recentKeys) => new()
    {
        StartDate = Iso(Start),
        EndDate = Iso(End),
        TimeZone = TimeZone,
        Scope = Scope,
        CreatorKey = CreatorKey,
        Query = string.IsNullOrWhiteSpace(Query) ? null : Query.Trim(),
        SelectedDays = SelectedDays,
        SelectedHours = SelectedHours,
        SelectedSessions = SelectedSessions,
        RecentKeys = RecentOnly ? recentKeys ?? Array.Empty<string>() : null,
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
        CreatorKey = CreatorKey,
        AgentId = agentId,
        Key = key,
        IncludeContextWeight = true,
    };

    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Plain click replaces the selection; append (Ctrl) toggles membership.</summary>
    public static IReadOnlyList<T> Toggle<T>(IReadOnlyList<T> selected, T value, bool append)
    {
        if (append)
        {
            return selected.Contains(value)
                ? selected.Where(v => !EqualityComparer<T>.Default.Equals(v, value)).ToArray()
                : [.. selected, value];
        }
        return selected.Count == 1 && EqualityComparer<T>.Default.Equals(selected[0], value) ? [] : [value];
    }
}
