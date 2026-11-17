using OpenClaw.Shared.Usage;

namespace OpenClawTray.Presentation.Usage;

internal readonly record struct UsagePageLayout(double HostWidth)
{
    public const double StatMinWidth = 160;
    public static int DailyFactColumns(double innerWidth)
    {
        var width = PageResponsiveLayout.Snap(innerWidth);
        if (width >= 3 * StatMinWidth + 2 * PageResponsiveLayout.GapGroup) return 3;
        return width >= 2 * StatMinWidth + PageResponsiveLayout.GapGroup ? 2 : 1;
    }
    public const double ActivityTileMinWidth = 200;
    public const double FactStackWidth = 400;
    public const double DialogMaxWidth = 560;
    public const double DialogWindowInset = 48;
    public PageWidthTier Tier => PageResponsiveLayout.TierFor(HostWidth);
    public bool Narrow => Tier == PageWidthTier.Narrow;
    public double Padding => PageResponsiveLayout.HorizontalPadding(Tier);
    public double ContentWidth => PageResponsiveLayout.ContentWidth(HostWidth);
    public double ColumnWidth => ContentWidth + Padding * 2;
    public bool TwoColumns => PageResponsiveLayout.UseTwoColumns(ContentWidth);
    public double DailyWidth => TwoColumns ? (ContentWidth - PageResponsiveLayout.GapStack) * 1.7 / 2.7 : ContentWidth;
    public static int ActivityColumns(double width) =>
        PageResponsiveLayout.TileColumns(width, ActivityTileMinWidth) >= 4 ? 4 : 2;
}

internal sealed record UsageChartDay(
    DateOnly Date, string Iso, double Value, long Tokens, double Cost, bool Pending, bool LowerBound, bool MissingPrice = false,
    bool TokensKnown = true, bool CostKnown = true);

internal sealed record UsageDailyChart(UsageMetric Metric, IReadOnlyList<UsageChartDay> Days, double Ceiling, bool CoversRange = true)
{
    public bool IsEmpty => Days.Count == 0 || Days.All(d => d.Value <= 0);
}

/// <summary>
/// Message, tool and error counts plus the cache hit rate. <see cref="ErrorRate"/> is errors divided by messages,
/// not a measure of Gateway or provider reliability. Ratios are null when an input is unknown or a lower bound.
/// </summary>
internal sealed record UsageActivitySummary(
    UsageMessageCounts Messages, int ToolCalls, double? ErrorRate, double? CacheHitRate, long CacheRead, long CacheBase,
    bool LowerBound, bool ToolCallsKnown = true);

internal enum UsageContributorKind
{
    Agent,
    Model,
    Provider,
}

/// <summary>One contributor's reported cost and tokens.</summary>
internal sealed record UsageContributor(
    string Label, string? Provider, long Tokens, double Cost, bool Unpriced, bool LowerBound,
    bool TokensKnown = true, bool CostKnown = true, bool CostIncomplete = false);

internal sealed record UsageSessionRowView(
    string Key, string Title, string? AgentId, string? Provider, string? Model,
    int Errors, long Tokens, double Cost,
    bool LowerBound, bool Computing, bool Selected, DateTimeOffset? LastActivity,
    bool TokensKnown = true, bool CostKnown = true, bool CostIncomplete = false);

/// <summary>
/// The session list. <see cref="Rows"/> is the loaded page. <see cref="Total"/> counts every matching session when the
/// Gateway reports that count; otherwise it is the loaded count. Without the server overview, Gateways return at most
/// <see cref="SessionsUsageQuery.FullRowLimit"/> rows in one read: <see cref="Truncated"/> reports a known total beyond
/// the loaded rows, and <see cref="MayHaveMore"/> a full read with no reported total, so the loaded count is a floor.
/// </summary>
internal sealed record UsageSessionsView(
    IReadOnlyList<UsageSessionRowView> Rows, int Total, int Offset, int Limit,
    bool ServerPaged, bool MayHaveMore = false, bool CountKnown = true)
{
    public bool Truncated => !ServerPaged && Total > Rows.Count;
}

/// <summary>
/// Range totals. <see cref="SessionsAtLeast"/> marks a count that is only the loaded rows of a read that reached its limit.
/// </summary>
internal sealed record UsageSummary(
    long Tokens, double Cost, int Sessions, bool LowerBound, bool Partial, int MissingCostEntries, bool SessionsAtLeast = false,
    bool TokensKnown = true, bool CostKnown = true, bool SessionsKnown = true);

internal sealed record UsageDashboard(
    UsageSummary Summary,
    UsageDailyChart Chart,
    UsageActivitySummary Activity,
    IReadOnlyDictionary<UsageContributorKind, IReadOnlyList<UsageContributor>> Contributors,
    UsageSessionsView Sessions,
    IReadOnlyList<string> QueryWarnings,
    IReadOnlyDictionary<string, IReadOnlyList<string>> FilterOptions,
    bool IsEmpty);

/// <summary>
/// WinUI-free projection of a <see cref="SessionsUsageReport"/> into the Usage page sections. Labels are resolved by
/// the view. Session-derived costs are Gateway estimates; provider plans and quotas are projected separately.
/// </summary>
internal static class UsageDashboardProjection
{
    public const int MaxChartDays = 366;

    public static UsageDashboard Project(
        SessionsUsageReport report, UsageFilters filters, UsageMetric metric, string? selectedKey = null)
    {
        var totals = report.Totals;
        var overview = report.Overview;
        var sessions = Sessions(report, selectedKey);
        var sessionCount = report.Aggregates.SessionCount ?? overview?.Total ?? sessions.Total;
        var summary = new UsageSummary(totals.TotalTokens, totals.TotalCost, sessionCount, totals.IsLowerBound,
            report.Partial is not null || report.CacheStatus?.IsIncomplete == true, totals.MissingCostEntries,
            SessionsAtLeast: report.Aggregates.SessionCount is null && sessions.MayHaveMore,
            TokensKnown: totals.HasValue("totalTokens"), CostKnown: totals.HasValue("totalCost"),
            SessionsKnown: report.Aggregates.SessionCount is not null || overview is not null || report.SessionsKnown);

        var isEmpty = report.SessionsKnown && report.Sessions.Count == 0 && sessionCount == 0
            && (overview?.UnfilteredSessionCount ?? 0) == 0
            && totals.HasValue("totalTokens") && totals.HasValue("totalCost")
            && totals.TotalTokens == 0 && totals.TotalCost == 0
            && report.CacheStatus?.IsIncomplete != true;

        return new UsageDashboard(
            summary,
            DailyChart(report, filters, metric),
            Activity(report),
            Contributors(report.Aggregates),
            sessions,
            overview?.QueryWarnings ?? Array.Empty<string>(),
            overview?.FilterOptions ?? new Dictionary<string, IReadOnlyList<string>>(),
            isEmpty);
    }

    // ── daily trend ──

    public static UsageDailyChart DailyChart(SessionsUsageReport report, UsageFilters filters, UsageMetric metric)
    {
        var byDate = new Dictionary<DateOnly, UsageDailyTotals>();
        foreach (var day in report.Aggregates.CostDaily)
        {
            // A repeated date keeps its last value instead of throwing during render.
            if (UsageFormat.TryParseIso(day.Date, out var date)) byDate[date] = day;
        }

        // Missing calendar days are not zero-usage days. Both views contain only the Gateway's reported rows.
        var dates = byDate.Keys.Where(d => d >= filters.Start && d <= filters.End).Order().TakeLast(MaxChartDays);

        var days = dates.Select(date =>
        {
            byDate.TryGetValue(date, out var entry);
            var t = entry?.Totals ?? UsageTotals.Empty;
            var value = metric == UsageMetric.Cost ? t.TotalCost : t.TotalTokens;
            return new UsageChartDay(date, UsageFilters.Iso(date), Math.Max(0, value), t.TotalTokens, t.TotalCost,
                Pending: entry is null || !t.HasValue(metric == UsageMetric.Cost ? "totalCost" : "totalTokens"),
                LowerBound: t.IsLowerBound, MissingPrice: t.MissingCostEntries > 0,
                TokensKnown: t.HasValue("totalTokens"), CostKnown: t.HasValue("totalCost"));
        }).ToList();

        var max = days.Count == 0 ? 0 : days.Max(d => d.Value);
        return new UsageDailyChart(metric, days, UsageFormat.NiceCeiling(max), days.Count == filters.RangeDays);
    }

    // ── activity ──

    public static UsageActivitySummary Activity(SessionsUsageReport report)
    {
        var totals = report.Totals;
        var messages = report.Aggregates.Messages;
        // Ratios of partial counters are neither exact nor lower bounds, so they are withheld.
        var lowerBound = totals.IsLowerBound || report.Partial is not null || report.CacheStatus?.IsIncomplete == true;
        var cacheBase = totals.Input + totals.CacheRead + totals.CacheWrite;
        return new UsageActivitySummary(
            messages,
            report.Aggregates.Tools.HasData ? report.Aggregates.Tools.TotalCalls : messages.ToolCalls,
            messages.HasData && messages.Total > 0 && !lowerBound ? (double)messages.Errors / messages.Total : null,
            cacheBase > 0 && !lowerBound && new[] { "input", "cacheRead", "cacheWrite" }.All(totals.HasValue)
                ? (double)totals.CacheRead / cacheBase : null,
            totals.CacheRead,
            cacheBase,
            lowerBound, report.Aggregates.Tools.HasData || messages.HasData);
    }

    // ── contributors ──

    public static IReadOnlyDictionary<UsageContributorKind, IReadOnlyList<UsageContributor>> Contributors(UsageAggregates aggregates) =>
        new Dictionary<UsageContributorKind, IReadOnlyList<UsageContributor>>
        {
            [UsageContributorKind.Agent] = Ranked(aggregates.ByAgent
                .Select(a => (Label: a.Name, Provider: (string?)null, a.Totals))),
            [UsageContributorKind.Model] = Ranked(aggregates.ByModel
                .Select(m => (Label: m.Model ?? "", m.Provider, m.Totals))),
            // Provider rows can repeat per model upstream, so they are merged by name.
            [UsageContributorKind.Provider] = Ranked(aggregates.ByProvider
                .GroupBy(p => p.Provider ?? "")
                .Select(g => (Label: g.Key, Provider: (string?)g.Key, Totals: g.Select(p => p.Totals).Aggregate(Add)))),
        };

    // Rows with no tokens and no cost (for example delivery mirrors) carry no usage worth ranking.
    private static bool Used(UsageTotals totals) => totals.TotalTokens > 0 || totals.TotalCost > 0;

    // Contributors rank by cost, which reflects model, speed tier and cache use better than tokens; unpriced usage
    // ranks by tokens below any priced usage.
    private static double Weight(UsageTotals totals) => totals.TotalCost > 0 ? totals.TotalCost : totals.TotalTokens / 1e12;

    private static IReadOnlyList<UsageContributor> Ranked(
        IEnumerable<(string Label, string? Provider, UsageTotals Totals)> source)
    {
        var items = source.Where(i => Used(i.Totals))
            .OrderByDescending(i => Weight(i.Totals)).ThenBy(i => i.Label, StringComparer.Ordinal)
            .ToList();
        return items.Select(i => new UsageContributor(
                i.Label, i.Provider, i.Totals.TotalTokens, i.Totals.TotalCost,
                Unpriced: i.Totals.TotalCost == 0 && i.Totals.MissingCostEntries > 0,
                LowerBound: i.Totals.IsLowerBound, TokensKnown: i.Totals.HasValue("totalTokens"), CostKnown: i.Totals.HasValue("totalCost"),
                CostIncomplete: i.Totals.MissingCostEntries > 0))
            .ToList();
    }

    public static UsageTotals Add(UsageTotals a, UsageTotals b) => new()
    {
        ReportedFields = a.ReportedFields is null ? b.ReportedFields : b.ReportedFields is null ? a.ReportedFields
            : a.ReportedFields.Intersect(b.ReportedFields).ToHashSet(StringComparer.Ordinal),
        Report = a.IsLowerBound || b.IsLowerBound ? UsageReportState.Unavailable
            : a.Report == UsageReportState.Available || b.Report == UsageReportState.Available ? UsageReportState.Available
            : UsageReportState.Unknown,
        Input = a.Input + b.Input,
        Output = a.Output + b.Output,
        CacheRead = a.CacheRead + b.CacheRead,
        CacheWrite = a.CacheWrite + b.CacheWrite,
        TotalTokens = a.TotalTokens + b.TotalTokens,
        TotalCost = a.TotalCost + b.TotalCost,
        InputCost = a.InputCost + b.InputCost,
        OutputCost = a.OutputCost + b.OutputCost,
        CacheReadCost = a.CacheReadCost + b.CacheReadCost,
        CacheWriteCost = a.CacheWriteCost + b.CacheWriteCost,
        MissingCostEntries = a.MissingCostEntries + b.MissingCostEntries,
    };

    // ── sessions ──

    public static UsageSessionsView Sessions(SessionsUsageReport report, string? selectedKey = null)
    {
        var overview = report.Overview;
        var rows = report.Sessions.Select(r => Row(r, r.Key == selectedKey)).ToList();
        if (overview is not null)
        {
            return new UsageSessionsView(
                rows, overview.Total, overview.Offset, overview.Limit > 0 ? overview.Limit : Math.Max(rows.Count, 1),
                ServerPaged: true);
        }

        // Without the server overview, rows are not sorted or paged by the Gateway; they are shown newest first.
        // A reported aggregates.sessionCount counts every matching session even when the read returned fewer rows.
        // Without it, a read that filled the row limit may have stopped early, so the loaded count is only a floor.
        rows = rows.OrderByDescending(r => r.LastActivity ?? DateTimeOffset.MinValue).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
        var known = report.Aggregates.SessionCount;
        var total = Math.Max(known ?? rows.Count, rows.Count);
        return new UsageSessionsView(
            rows, total, 0, Math.Max(rows.Count, 1),
            ServerPaged: false,
            MayHaveMore: known is null && rows.Count >= SessionsUsageQuery.FullRowLimit,
            CountKnown: known is not null || report.SessionsKnown);
    }

    public static UsageSessionRowView Row(SessionUsageRow row, bool selected)
    {
        var usage = row.Usage;
        var counts = usage?.MessageCounts;
        return new UsageSessionRowView(
            row.Key,
            string.IsNullOrWhiteSpace(row.Label) ? row.Key : row.Label!,
            row.AgentId,
            row.EffectiveProvider,
            row.EffectiveModel,
            counts?.Errors ?? 0,
            usage?.Totals.TotalTokens ?? 0,
            usage?.Totals.TotalCost ?? 0,
            usage?.Totals.IsLowerBound == true,
            row.Computing,
            selected,
            usage?.LastActivity ?? row.UpdatedAt,
            TokensKnown: usage?.Totals.HasValue("totalTokens") == true, CostKnown: usage?.Totals.HasValue("totalCost") == true,
            CostIncomplete: usage?.Totals.MissingCostEntries > 0);
    }
}
