using System.Globalization;
using OpenClaw.Shared.Usage;

namespace OpenClawTray.Presentation.Usage;

internal enum UsageSeriesKind
{
    Total,
    Output,
    Input,
    CacheWrite,
    CacheRead,
    Provider,
    Other,
}

internal sealed record UsageSeries(string Key, UsageSeriesKind Kind, string? Label, int PaletteIndex);

internal sealed record UsageChartSegment(string SeriesKey, double Value);

internal sealed record UsageChartDay(
    DateOnly Date, string Iso, double Total, IReadOnlyList<UsageChartSegment> Segments,
    long Tokens, double Cost, bool Pending, bool LowerBound, bool Selected);

internal sealed record UsageDailyChart(
    UsageMetric Metric, UsageDailyChartMode Mode, IReadOnlyList<UsageSeries> Series,
    IReadOnlyList<UsageChartDay> Days, double Ceiling)
{
    public bool IsEmpty => Days.Count == 0 || Days.All(d => d.Total <= 0);
}

internal sealed record UsageCostWindow(int Days, DateOnly EndDate, bool IsSelectedRange, UsageTotals Totals);

internal sealed record UsageBreakdownPart(UsageSeriesKind Kind, long Tokens, double Cost, double Share);

internal sealed record UsageCreatorRow(
    string Key, string? Label, string ActorType, int Sessions, long Tokens, double Cost, double Weight, bool Selected,
    bool LowerBound);

/// <summary>One ranked insight row. <see cref="Weight"/> is 0..1 relative to the list leader.</summary>
internal sealed record UsageInsightItem(
    string Label, string? Source, double Cost, long Tokens, int? Count, double Weight, bool Unpriced, bool LowerBound,
    double? Rate = null, int? Errors = null, int? Messages = null, string? AgentId = null);

internal enum UsageInsightList
{
    TopModels,
    TopProviders,
    TopTools,
    TopAgents,
    TopChannels,
    PeakErrorDays,
    PeakErrorHours,
}

internal sealed record UsageInsights(
    UsageMessageCounts Messages,
    UsageToolSummary Tools,
    double? ThroughputTokensPerMinute,
    double? ThroughputCostPerMinute,
    long? AverageTokensPerMessage,
    double? AverageCostPerMessage,
    double? CacheHitRate,
    long CacheRead,
    long CacheBase,
    double ErrorRate,
    double? AverageSessionDurationMs,
    int Sessions,
    int TotalSessions,
    double TotalCost,
    bool Partial,
    bool MissingCost,
    IReadOnlyDictionary<UsageInsightList, IReadOnlyList<UsageInsightItem>> Lists);

internal sealed record UsageActivityCell(int Index, long Tokens, double Intensity, bool Selected);

internal sealed record UsageActivity(
    IReadOnlyList<UsageActivityCell> Weekdays, IReadOnlyList<UsageActivityCell> Hours, bool HasTimelineData,
    int? PeakHour, DayOfWeek? PeakWeekday);

internal sealed record UsageHeatmapCell(DateOnly Date, long Tokens, int Level, bool InRange, bool LowerBound);

internal sealed record UsageHeatmap(IReadOnlyList<IReadOnlyList<UsageHeatmapCell>> Weeks);

internal sealed record UsageSessionRowView(
    string Key, string Title, string? AgentId, string? Channel, string? Provider, string? Model,
    int Messages, int ToolCalls, int Errors, long? DurationMs, long Tokens, double Cost,
    bool LowerBound, bool Computing, int? InstanceCount, bool Selected, DateTimeOffset? LastActivity);

internal sealed record UsageSessionsView(
    IReadOnlyList<UsageSessionRowView> Rows, int Total, int Offset, int Limit, long TableTokens, double TableCost,
    int TableErrors, IReadOnlyList<UsageSessionRowView> SelectedRows)
{
    /// <summary>Sessions the table totals cover. "Recently viewed" lists fewer rows than the totals count.</summary>
    public int TableSessions { get; init; } = Total;
}

internal sealed record UsageSummary(long Tokens, double Cost, int Sessions, bool LowerBound, bool Partial);

internal sealed record UsageDashboard(
    UsageSummary Summary,
    UsageDailyChart Chart,
    IReadOnlyList<UsageBreakdownPart> Breakdown,
    IReadOnlyList<UsageCostWindow> CostWindows,
    IReadOnlyList<UsageCreatorRow> Creators,
    UsageInsights Insights,
    UsageActivity Activity,
    UsageHeatmap? Heatmap,
    UsageSessionsView Sessions,
    IReadOnlyList<string> QueryWarnings,
    IReadOnlyDictionary<string, IReadOnlyList<string>> FilterOptions,
    bool IsEmpty);

/// <summary>
/// WinUI-free projection of a <see cref="SessionsUsageReport"/> into the Usage page sections. Formulas follow
/// openclaw/openclaw@5089aa71 ui/src/pages/usage (view.tsx, view-overview.tsx, view-chart.tsx,
/// metrics.ts, view-heatmap.tsx). Labels are resolved by the view.
/// </summary>
internal static class UsageDashboardProjection
{
    public const int TopListSize = 5;
    public const int TopToolsSize = 6;
    public const int MaxProviderSeries = 5;
    public const int HeatmapThresholdDays = 62;
    public const int HeatmapMaxWeeks = 52;
    public const int MaxChartDays = 366;

    public static UsageDashboard Project(
        SessionsUsageReport report, UsageFilters filters, UsageMetric metric, UsageDailyChartMode chartMode,
        DateOnly today)
    {
        var totals = report.Totals;
        var overview = report.Overview;
        var sessionCount = report.Aggregates.SessionCount ?? overview?.Total ?? report.Sessions.Count;
        var summary = new UsageSummary(totals.TotalTokens, totals.TotalCost, sessionCount, totals.IsLowerBound,
            report.Partial is not null || report.CacheStatus?.IsIncomplete == true);

        var isEmpty = report.Sessions.Count == 0
            && (overview?.UnfilteredSessionCount ?? 0) == 0
            && totals.TotalTokens == 0
            && report.CacheStatus?.IsIncomplete != true;

        return new UsageDashboard(
            summary,
            DailyChart(report, filters, metric, chartMode),
            Breakdown(totals, metric),
            CostWindows(report.Aggregates.CostDaily, filters),
            Creators(report.Aggregates.ByCreator, report.CreatorOptions, filters.CreatorKey, metric),
            Insights(report, sessionCount),
            Activity(overview, filters.SelectedHours),
            filters.RangeDays > HeatmapThresholdDays ? Heatmap(report.Aggregates.CostDaily, filters, today) : null,
            Sessions(report, filters),
            overview?.QueryWarnings ?? Array.Empty<string>(),
            overview?.FilterOptions ?? new Dictionary<string, IReadOnlyList<string>>(),
            isEmpty);
    }

    // ── daily chart ──

    public static UsageDailyChart DailyChart(
        SessionsUsageReport report, UsageFilters filters, UsageMetric metric, UsageDailyChartMode mode)
    {
        var byDate = new Dictionary<DateOnly, UsageDailyTotals>();
        foreach (var day in report.Aggregates.CostDaily)
        {
            if (UsageFormat.TryParseIso(day.Date, out var date)) byDate[date] = day;
        }

        // Ranges up to a year show every calendar day; longer ranges (All) show days with data.
        IEnumerable<DateOnly> dates = filters.RangeDays <= MaxChartDays
            ? Enumerable.Range(0, filters.RangeDays).Select(i => filters.Start.AddDays(i))
            : byDate.Keys.Order().TakeLast(MaxChartDays);
        var complete = report.CacheStatus?.IsIncomplete != true && report.Partial is null;
        var selected = filters.SelectedDays.ToHashSet(StringComparer.Ordinal);

        var series = mode switch
        {
            UsageDailyChartMode.ByType => new List<UsageSeries>
            {
                new("output", UsageSeriesKind.Output, null, 0),
                new("input", UsageSeriesKind.Input, null, 1),
                new("cacheWrite", UsageSeriesKind.CacheWrite, null, 2),
                new("cacheRead", UsageSeriesKind.CacheRead, null, 3),
            },
            UsageDailyChartMode.ByProvider => ProviderSeries(report, metric),
            _ => [new UsageSeries("total", UsageSeriesKind.Total, null, 0)],
        };

        var providerValues = mode == UsageDailyChartMode.ByProvider ? ProviderDayValues(report, metric) : null;
        var days = new List<UsageChartDay>();
        foreach (var date in dates)
        {
            var iso = UsageFilters.Iso(date);
            byDate.TryGetValue(date, out var entry);
            var t = entry?.Totals ?? UsageTotals.Empty;
            var total = metric == UsageMetric.Cost ? t.TotalCost : t.TotalTokens;
            var segments = mode switch
            {
                UsageDailyChartMode.ByType => metric == UsageMetric.Cost
                    ? new[] { Seg("output", t.OutputCost), Seg("input", t.InputCost), Seg("cacheWrite", t.CacheWriteCost), Seg("cacheRead", t.CacheReadCost) }
                    : [Seg("output", t.Output), Seg("input", t.Input), Seg("cacheWrite", t.CacheWrite), Seg("cacheRead", t.CacheRead)],
                UsageDailyChartMode.ByProvider => ProviderSegments(series, providerValues!, iso, total),
                _ => [Seg("total", total)],
            };
            days.Add(new UsageChartDay(date, iso, total, segments, t.TotalTokens, t.TotalCost,
                Pending: entry is null && !complete, LowerBound: t.IsLowerBound, Selected: selected.Contains(iso)));
        }

        var max = days.Count == 0 ? 0 : days.Max(d => d.Segments.Sum(s => s.Value));
        return new UsageDailyChart(metric, mode, series, days, UsageFormat.NiceCeiling(max));
    }

    private static UsageChartSegment Seg(string key, double value) => new(key, Math.Max(0, value));

    private static List<UsageSeries> ProviderSeries(SessionsUsageReport report, UsageMetric metric)
    {
        var ranked = report.Aggregates.ModelDaily
            .GroupBy(d => d.Provider ?? "")
            .Select(g => (Provider: g.Key, Value: g.Sum(d => metric == UsageMetric.Cost ? d.Cost : d.Tokens)))
            .Where(p => p.Value > 0 && p.Provider.Length > 0)
            .OrderByDescending(p => p.Value).ThenBy(p => p.Provider, StringComparer.Ordinal)
            .Take(MaxProviderSeries)
            .Select((p, i) => new UsageSeries("provider:" + p.Provider, UsageSeriesKind.Provider, p.Provider, i))
            .ToList();
        ranked.Add(new UsageSeries("other", UsageSeriesKind.Other, null, ranked.Count));
        return ranked;
    }

    private static Dictionary<(string Date, string Provider), double> ProviderDayValues(SessionsUsageReport report, UsageMetric metric) =>
        report.Aggregates.ModelDaily
            .GroupBy(d => (d.Date, d.Provider ?? ""))
            .ToDictionary(g => g.Key, g => g.Sum(d => metric == UsageMetric.Cost ? d.Cost : d.Tokens));

    private static UsageChartSegment[] ProviderSegments(
        IReadOnlyList<UsageSeries> series, Dictionary<(string, string), double> values, string iso, double total)
    {
        var segments = new List<UsageChartSegment>();
        var attributed = 0d;
        foreach (var s in series.Where(s => s.Kind == UsageSeriesKind.Provider))
        {
            var value = values.GetValueOrDefault((iso, s.Label!));
            attributed += value;
            segments.Add(Seg(s.Key, value));
        }
        // Unattributed usage and providers beyond the top list become "Other".
        segments.Add(Seg("other", total - attributed));
        return segments.ToArray();
    }

    // ── cost breakdown and windows ──

    /// <summary>
    /// Token types in upstream stack order. Shares use the shown metric, like upstream: token counts over total
    /// tokens, or per-type cost over total cost.
    /// </summary>
    public static IReadOnlyList<UsageBreakdownPart> Breakdown(UsageTotals totals, UsageMetric metric)
    {
        var tokenTotal = (double)(totals.TotalTokens > 0
            ? totals.TotalTokens
            : totals.Output + totals.Input + totals.CacheWrite + totals.CacheRead);
        var costTotal = totals.TotalCost;
        UsageBreakdownPart Part(UsageSeriesKind kind, long tokens, double cost) =>
            new(kind, tokens, cost, metric == UsageMetric.Cost
                ? costTotal > 0 ? cost / costTotal : 0
                : tokenTotal > 0 ? tokens / tokenTotal : 0);
        return
        [
            Part(UsageSeriesKind.Output, totals.Output, totals.OutputCost),
            Part(UsageSeriesKind.Input, totals.Input, totals.InputCost),
            Part(UsageSeriesKind.CacheWrite, totals.CacheWrite, totals.CacheWriteCost),
            Part(UsageSeriesKind.CacheRead, totals.CacheRead, totals.CacheReadCost),
        ];
    }

    /// <summary>The selected range, then each of 1/7/30/90 days that is shorter than the range.</summary>
    public static IReadOnlyList<UsageCostWindow> CostWindows(IReadOnlyList<UsageDailyTotals> daily, UsageFilters filters)
    {
        if (daily.Count == 0) return Array.Empty<UsageCostWindow>();
        var rangeDays = filters.RangeDays;
        var windows = new List<UsageCostWindow>();
        foreach (var days in new[] { rangeDays }.Concat(new[] { 1, 7, 30, 90 }.Where(d => d < rangeDays)))
        {
            var start = filters.End.AddDays(-(days - 1));
            var sum = daily
                .Where(d => UsageFormat.TryParseIso(d.Date, out var date) && date >= start && date <= filters.End)
                .Select(d => d.Totals)
                .Aggregate(UsageTotals.Empty, Add);
            windows.Add(new UsageCostWindow(days, filters.End, windows.Count == 0, sum));
        }
        return windows;
    }

    public static UsageTotals Add(UsageTotals a, UsageTotals b) => new()
    {
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

    // ── creators ──

    /// <summary>
    /// Creators ordered like upstream: recorded identities first, then by the shown metric. Each bar is that
    /// identity's share of the shown metric across all creators.
    /// </summary>
    public static IReadOnlyList<UsageCreatorRow> Creators(
        IReadOnlyList<UsageCreatorAggregate> byCreator, IReadOnlyList<UsageCreator> options, string? selectedKey,
        UsageMetric metric = UsageMetric.Tokens)
    {
        double Value(UsageTotals t) => metric == UsageMetric.Cost ? t.TotalCost : t.TotalTokens;
        var rows = byCreator
            .OrderBy(c => c.Creator.Actor is null)
            .ThenByDescending(c => Value(c.Totals))
            .ThenBy(c => c.Creator.Actor?.Label ?? c.Creator.Key, StringComparer.CurrentCulture)
            .ToList();
        // A selected identity with no usage in range still appears so it can be cleared.
        if (selectedKey is not null && rows.All(r => r.Creator.Key != selectedKey))
        {
            var option = options.FirstOrDefault(o => o.Key == selectedKey) ?? new UsageCreator(selectedKey, null);
            rows.Add(new UsageCreatorAggregate(option, UsageTotals.Empty, 0, []));
        }
        var total = rows.Sum(r => Value(r.Totals));
        return rows.Select(r => new UsageCreatorRow(
                r.Creator.Key, r.Creator.Actor?.Label, r.Creator.Actor?.Type ?? (r.Creator.Key == "system" ? "system" : "unknown"),
                r.SessionCount, r.Totals.TotalTokens, r.Totals.TotalCost,
                total > 0 ? Value(r.Totals) / total : 0, r.Creator.Key == selectedKey, r.Totals.IsLowerBound))
            .ToList();
    }

    // ── insights ──

    public static UsageInsights Insights(SessionsUsageReport report, int sessionCount)
    {
        var totals = report.Totals;
        var aggregates = report.Aggregates;
        var overview = report.Overview;
        // Ratios of partial counters are neither exact nor lower bounds, so they are withheld.
        var partial = totals.IsLowerBound;
        var messages = aggregates.Messages;
        // Without the server overview (released Gateways) every row is returned, so durations come from the rows.
        var rowDurations = overview is null
            ? report.Sessions.Select(s => s.Usage?.DurationMs ?? 0).Where(d => d > 0).ToList()
            : null;
        var durationMs = overview?.DurationMs ?? rowDurations!.Sum(d => (double)d);
        var durationCount = overview?.DurationCount ?? rowDurations!.Count;
        var cacheBase = totals.Input + totals.CacheRead + totals.CacheWrite;
        var minutes = durationMs / 60_000d;

        var lists = new Dictionary<UsageInsightList, IReadOnlyList<UsageInsightItem>>
        {
            [UsageInsightList.TopModels] = Ranked(aggregates.ByModel.Where(Used)
                .Select(m => (Label: m.Model ?? "", Source: m.Provider, m.Totals, Count: (int?)m.Count, Agent: (string?)null)), TopListSize),
            [UsageInsightList.TopProviders] = Ranked(aggregates.ByProvider
                .GroupBy(p => p.Provider ?? "")
                .Select(g => (Label: g.Key, Source: (string?)null, Totals: g.Select(p => p.Totals).Aggregate(UsageTotals.Empty, Add),
                    Count: (int?)g.Sum(p => p.Count), Agent: (string?)null))
                .Where(p => Used(p.Totals))
                .OrderByDescending(p => p.Totals.TotalCost).ThenByDescending(p => p.Totals.TotalTokens), TopListSize),
            [UsageInsightList.TopTools] = Tools(aggregates.Tools),
            [UsageInsightList.TopAgents] = Ranked(aggregates.ByAgent.Where(a => Used(a.Totals))
                .Select(a => (Label: a.Name, Source: (string?)null, a.Totals, Count: (int?)null, Agent: (string?)a.Name)), TopListSize),
            [UsageInsightList.TopChannels] = Ranked(aggregates.ByChannel.Where(c => Used(c.Totals))
                .Select(c => (Label: c.Name, Source: (string?)null, c.Totals, Count: (int?)null, Agent: (string?)null)), TopListSize),
            [UsageInsightList.PeakErrorDays] = PeakErrorDays(aggregates.Daily),
            [UsageInsightList.PeakErrorHours] = PeakErrorHours(overview),
        };

        return new UsageInsights(
            messages,
            aggregates.Tools,
            durationMs > 0 && !partial ? totals.TotalTokens / minutes : null,
            durationMs > 0 && !partial ? totals.TotalCost / minutes : null,
            messages.Total > 0 && !partial ? (long)Math.Round((double)totals.TotalTokens / messages.Total) : null,
            messages.Total > 0 && !partial ? totals.TotalCost / messages.Total : null,
            cacheBase > 0 && !partial ? (double)totals.CacheRead / cacheBase : null,
            totals.CacheRead,
            cacheBase,
            messages.Total > 0 ? (double)messages.Errors / messages.Total : 0,
            durationCount > 0 ? (double)durationMs / durationCount : null,
            sessionCount,
            overview?.UnfilteredSessionCount ?? sessionCount,
            totals.TotalCost,
            partial,
            totals.MissingCostEntries > 0,
            lists);
    }

    private static bool Used(UsageModelBreakdown entry) => Used(entry.Totals);

    // Rows with no tokens and no cost (for example delivery mirrors) carry no usage worth ranking.
    private static bool Used(UsageTotals totals) => totals.TotalTokens > 0 || totals.TotalCost > 0;

    // Bars rank by cost, which reflects model, speed tier and cache use better than tokens.
    private static double Weight(UsageTotals totals) => totals.TotalCost > 0 ? totals.TotalCost : totals.TotalTokens / 1e9;

    private static IReadOnlyList<UsageInsightItem> Ranked(
        IEnumerable<(string Label, string? Source, UsageTotals Totals, int? Count, string? Agent)> source, int take)
    {
        var items = source.Take(take).ToList();
        var leader = items.Count == 0 ? 0 : items.Max(i => Weight(i.Totals));
        return items.Select(i => new UsageInsightItem(
                i.Label, i.Source, i.Totals.TotalCost, i.Totals.TotalTokens, i.Count,
                leader > 0 ? Weight(i.Totals) / leader : 0,
                Unpriced: i.Totals.TotalCost == 0 && i.Totals.MissingCostEntries > 0,
                LowerBound: i.Totals.IsLowerBound, AgentId: i.Agent))
            .ToList();
    }

    private static IReadOnlyList<UsageInsightItem> Tools(UsageToolSummary tools)
    {
        var items = tools.Tools.Take(TopToolsSize).ToList();
        var leader = items.Count == 0 ? 0 : items.Max(t => t.Count);
        return items.Select(t => new UsageInsightItem(t.Name, null, 0, 0, t.Count,
            leader > 0 ? (double)t.Count / leader : 0, Unpriced: false, LowerBound: false)).ToList();
    }

    private static IReadOnlyList<UsageInsightItem> PeakErrorDays(IReadOnlyList<UsageDailyActivity> daily)
    {
        var items = daily.Where(d => d.Messages > 0 && d.Errors > 0)
            .Select(d => (Day: d, Rate: (double)d.Errors / d.Messages))
            .OrderByDescending(d => d.Rate).ThenBy(d => d.Day.Date, StringComparer.Ordinal)
            .Take(TopListSize).ToList();
        var leader = items.Count == 0 ? 0 : items.Max(i => i.Rate);
        return items.Select(i => new UsageInsightItem(i.Day.Date, null, i.Day.Cost, i.Day.Tokens, null,
            leader > 0 ? i.Rate / leader : 0, false, i.Day.Report == UsageReportState.Unavailable,
            Rate: i.Rate, Errors: i.Day.Errors, Messages: i.Day.Messages)).ToList();
    }

    private static IReadOnlyList<UsageInsightItem> PeakErrorHours(UsageOverview? overview)
    {
        if (overview is null) return Array.Empty<UsageInsightItem>();
        var items = Enumerable.Range(0, 24)
            .Select(hour => (Hour: hour, Messages: overview.HourlyMessages.ElementAtOrDefault(hour), Errors: overview.HourlyErrors.ElementAtOrDefault(hour)))
            .Where(h => h.Messages > 0 && h.Errors > 0)
            .Select(h => (h.Hour, h.Messages, h.Errors, Rate: (double)h.Errors / h.Messages))
            .OrderByDescending(h => h.Rate).ThenBy(h => h.Hour)
            .Take(TopListSize).ToList();
        var leader = items.Count == 0 ? 0 : items.Max(i => i.Rate);
        return items.Select(i => new UsageInsightItem(i.Hour.ToString(CultureInfo.InvariantCulture), null, 0, 0, null,
            leader > 0 ? i.Rate / leader : 0, false, false,
            Rate: i.Rate, Errors: (int)i.Errors, Messages: (int)i.Messages)).ToList();
    }

    // ── activity mosaic and heatmap ──

    public static UsageActivity Activity(UsageOverview? overview, IReadOnlyList<int> selectedHours)
    {
        var weekdays = Enumerable.Range(0, 7).Select(i => overview?.WeekdayTokens.ElementAtOrDefault(i) ?? 0).ToArray();
        var hours = Enumerable.Range(0, 24).Select(i => overview?.HourTokens.ElementAtOrDefault(i) ?? 0).ToArray();
        var weekdayMax = weekdays.Max();
        var hourMax = hours.Max();
        var selected = selectedHours.ToHashSet();
        return new UsageActivity(
            weekdays.Select((tokens, i) => new UsageActivityCell(i, tokens, weekdayMax > 0 ? (double)tokens / weekdayMax : 0, false)).ToList(),
            hours.Select((tokens, i) => new UsageActivityCell(i, tokens, hourMax > 0 ? (double)tokens / hourMax : 0, selected.Contains(i))).ToList(),
            overview?.HasTimelineData == true,
            hourMax > 0 ? Array.IndexOf(hours, hourMax) : null,
            weekdayMax > 0 ? (DayOfWeek)Array.IndexOf(weekdays, weekdayMax) : null);
    }

    /// <summary>Sunday-first daily token grid, up to 52 weeks ending at the range end. Levels are quartiles of active days.</summary>
    public static UsageHeatmap Heatmap(IReadOnlyList<UsageDailyTotals> daily, UsageFilters filters, DateOnly today)
    {
        var end = filters.End > today ? today : filters.End;
        var firstSunday = end.AddDays(-(int)end.DayOfWeek).AddDays(-7 * (HeatmapMaxWeeks - 1));
        var start = filters.Start > firstSunday ? filters.Start : firstSunday;
        var gridStart = start.AddDays(-(int)start.DayOfWeek);
        var byDate = new Dictionary<DateOnly, UsageTotals>();
        foreach (var day in daily)
        {
            // A repeated date keeps its last value, as the daily chart does, instead of throwing during render.
            if (UsageFormat.TryParseIso(day.Date, out var date)) byDate[date] = day.Totals;
        }
        var active = byDate.Where(kv => kv.Key >= start && kv.Key <= end && kv.Value.TotalTokens > 0)
            .Select(kv => kv.Value.TotalTokens).Order().ToArray();
        long Quantile(double q) => active.Length == 0 ? 0 : active[(int)Math.Floor(q * (active.Length - 1))];
        var (q1, q2, q3) = (Quantile(0.25), Quantile(0.5), Quantile(0.75));

        var weeks = new List<IReadOnlyList<UsageHeatmapCell>>();
        for (var weekStart = gridStart; weekStart <= end; weekStart = weekStart.AddDays(7))
        {
            var week = new List<UsageHeatmapCell>(7);
            for (var d = 0; d < 7; d++)
            {
                var date = weekStart.AddDays(d);
                var inRange = date >= start && date <= end;
                var totals = byDate.GetValueOrDefault(date) ?? UsageTotals.Empty;
                var tokens = inRange ? totals.TotalTokens : 0;
                var level = tokens <= 0 ? 0 : tokens <= q1 ? 1 : tokens <= q2 ? 2 : tokens <= q3 ? 3 : 4;
                week.Add(new UsageHeatmapCell(date, tokens, level, inRange, totals.IsLowerBound));
            }
            weeks.Add(week);
        }
        return new UsageHeatmap(weeks);
    }

    // ── sessions ──

    public static UsageSessionsView Sessions(SessionsUsageReport report, UsageFilters filters)
    {
        var selected = filters.SelectedSessions.ToHashSet(StringComparer.Ordinal);
        var overview = report.Overview;
        var rows = report.Sessions.Select(r => Row(r, selected.Contains(r.Key))).ToList();
        // Released Gateways return every row unsorted by the page's sort, so they are shown newest first in one page.
        if (overview is null)
            rows = rows.OrderByDescending(r => r.LastActivity ?? DateTimeOffset.MinValue).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
        var selectedRows = (overview?.SelectedRows ?? report.Sessions.Where(s => selected.Contains(s.Key)).ToList())
            .Select(r => Row(r, true)).ToList();
        return new UsageSessionsView(
            rows,
            overview?.Total ?? rows.Count,
            overview?.Offset ?? 0,
            overview?.Limit is > 0 ? overview.Limit : Math.Max(rows.Count, 1),
            overview?.TableTotals.Tokens ?? rows.Sum(r => r.Tokens),
            overview?.TableTotals.Cost ?? rows.Sum(r => r.Cost),
            overview?.TableTotals.Errors ?? rows.Sum(r => r.Errors),
            selectedRows)
        {
            TableSessions = overview is { TableSessionCount: > 0 } ? overview.TableSessionCount : overview?.Total ?? rows.Count,
        };
    }

    public static UsageSessionRowView Row(SessionUsageRow row, bool selected)
    {
        var usage = row.Usage;
        var counts = usage?.MessageCounts;
        return new UsageSessionRowView(
            row.Key,
            string.IsNullOrWhiteSpace(row.Label) ? row.Key : row.Label!,
            row.AgentId,
            row.Channel,
            row.EffectiveProvider,
            row.EffectiveModel,
            counts?.Total ?? 0,
            usage?.ToolUsage?.TotalCalls ?? counts?.ToolCalls ?? 0,
            counts?.Errors ?? 0,
            usage?.DurationMs,
            usage?.Totals.TotalTokens ?? 0,
            usage?.Totals.TotalCost ?? 0,
            usage?.Totals.IsLowerBound == true,
            row.Computing || usage is null,
            row.HistoricalInstanceCount is > 1 ? row.HistoricalInstanceCount : null,
            selected,
            usage?.LastActivity ?? row.UpdatedAt);
    }
}
