using System.Globalization;
using OpenClaw.Shared.Usage;
using OpenClawTray.Presentation.Usage;

namespace OpenClaw.Tray.Tests.Usage;

/// <summary>
/// Pins the Usage page's derived numbers to the upstream web formulas (openclaw/openclaw@5089aa71
/// ui/src/pages/usage). Inputs are small hand-built reports; expected values are computed by hand.
/// </summary>
public sealed class UsageDashboardProjectionTests
{
    private static readonly DateOnly Today = new(2026, 10, 26);

    private static UsageTotals T(long input, long output, long read, long write, double cost = 0,
        double inputCost = 0, double outputCost = 0, double readCost = 0, double writeCost = 0,
        UsageReportState report = UsageReportState.Available, int missing = 0) => new()
    {
        Input = input, Output = output, CacheRead = read, CacheWrite = write,
        TotalTokens = input + output + read + write, TotalCost = cost,
        InputCost = inputCost, OutputCost = outputCost, CacheReadCost = readCost, CacheWriteCost = writeCost,
        Report = report, MissingCostEntries = missing,
    };

    private static UsageFilters Week => UsageFilters.Default(Today);

    [Fact]
    public void Presets_ResolveInclusiveRangesEndingToday()
    {
        Assert.Equal((new DateOnly(2026, 10, 20), Today), Range(UsagePreset.Last7Days));
        Assert.Equal((Today, Today), Range(UsagePreset.Today));
        Assert.Equal((new DateOnly(2026, 7, 29), Today), Range(UsagePreset.Last90Days));
        Assert.Equal((UsageFilters.AllStart, Today), Range(UsagePreset.All));

        static (DateOnly, DateOnly) Range(UsagePreset preset)
        {
            var f = UsageFilters.ForPreset(new UsageFilters(), preset, Today);
            return (f.Start, f.End);
        }
    }

    [Fact]
    public void CustomRange_IsNormalizedAndClearsDaySelection()
    {
        var filters = Week with { SelectedDays = ["2026-10-21"], Offset = 50 };
        var custom = filters.WithRange(new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 1));

        Assert.Equal(UsagePreset.Custom, custom.Preset);
        Assert.Equal(new DateOnly(2026, 10, 1), custom.Start);
        Assert.Equal(new DateOnly(2026, 10, 10), custom.End);
        Assert.Empty(custom.SelectedDays);
        Assert.Equal(0, custom.Offset);
    }

    [Fact]
    public void RecentOnly_SendsTheRecentRosterAndOtherwiseOmitsIt()
    {
        Assert.Null(Week.ToOverviewQuery(["a"]).RecentKeys);
        Assert.Equal(["a"], (Week with { RecentOnly = true }).ToOverviewQuery(["a"]).RecentKeys);
        Assert.True(Week.ToOverviewQuery(null).OverviewProjection);
    }

    [Fact]
    public void DailyChart_FillsEveryCalendarDayAndMarksPendingOnlyWhenIncomplete()
    {
        var report = new SessionsUsageReport
        {
            Aggregates = new UsageAggregates
            {
                CostDaily = [new UsageDailyTotals("2026-10-22", T(100, 20, 0, 0, cost: 0.5))],
            },
        };

        var complete = UsageDashboardProjection.DailyChart(report, Week, UsageMetric.Tokens, UsageDailyChartMode.Total);
        Assert.Equal(7, complete.Days.Count);
        Assert.Equal(120, complete.Days[2].Total);
        Assert.All(complete.Days, d => Assert.False(d.Pending));
        Assert.Equal(200, complete.Ceiling);

        var incomplete = UsageDashboardProjection.DailyChart(
            report with { CacheStatus = new UsageCacheStatus("refreshing", 2, 0, null) },
            Week, UsageMetric.Cost, UsageDailyChartMode.Total);
        Assert.True(incomplete.Days[0].Pending);
        Assert.False(incomplete.Days[2].Pending);
        Assert.Equal(0.5, incomplete.Days[2].Total);
    }

    [Fact]
    public void DailyChart_ByTypeUsesPerTypeCostsInUpstreamStackOrder()
    {
        var report = new SessionsUsageReport
        {
            Aggregates = new UsageAggregates
            {
                CostDaily = [new UsageDailyTotals("2026-10-26", T(10, 20, 30, 40, cost: 1, inputCost: 0.1, outputCost: 0.2, readCost: 0.3, writeCost: 0.4))],
            },
        };

        var chart = UsageDashboardProjection.DailyChart(report, Week, UsageMetric.Cost, UsageDailyChartMode.ByType);

        Assert.Equal(["output", "input", "cacheWrite", "cacheRead"], chart.Series.Select(s => s.Key));
        Assert.Equal([0.2, 0.1, 0.4, 0.3], chart.Days[^1].Segments.Select(s => s.Value));
    }

    [Fact]
    public void DailyChart_ByProviderRanksProvidersAndPutsTheRemainderInOther()
    {
        var report = new SessionsUsageReport
        {
            Aggregates = new UsageAggregates
            {
                CostDaily = [new UsageDailyTotals("2026-10-26", T(500, 500, 0, 0))],
                ModelDaily =
                [
                    new UsageDailyModel("2026-10-26", "openai", "a", 300, 0, 1, UsageReportState.Available),
                    new UsageDailyModel("2026-10-26", "anthropic", "b", 600, 0, 1, UsageReportState.Available),
                ],
            },
        };

        var chart = UsageDashboardProjection.DailyChart(report, Week, UsageMetric.Tokens, UsageDailyChartMode.ByProvider);

        Assert.Equal(["provider:anthropic", "provider:openai", "other"], chart.Series.Select(s => s.Key));
        Assert.Equal([600, 300, 100], chart.Days[^1].Segments.Select(s => s.Value));
    }

    [Fact]
    public void Breakdown_SharesFollowTheShownMetric()
    {
        var totals = T(600, 200, 150, 50, cost: 10, inputCost: 2, outputCost: 6, readCost: 1, writeCost: 1);

        var tokens = UsageDashboardProjection.Breakdown(totals, UsageMetric.Tokens);
        var cost = UsageDashboardProjection.Breakdown(totals, UsageMetric.Cost);

        Assert.Equal([UsageSeriesKind.Output, UsageSeriesKind.Input, UsageSeriesKind.CacheWrite, UsageSeriesKind.CacheRead], tokens.Select(p => p.Kind));
        Assert.Equal([0.2, 0.6, 0.05, 0.15], tokens.Select(p => Math.Round(p.Share, 6)));
        Assert.Equal([0.6, 0.2, 0.1, 0.1], cost.Select(p => Math.Round(p.Share, 6)));
    }

    [Fact]
    public void CostWindows_StartWithTheRangeThenShorterUpstreamWindows()
    {
        var filters = UsageFilters.ForPreset(new UsageFilters(), UsagePreset.Last30Days, Today);
        var daily = new[]
        {
            new UsageDailyTotals("2026-10-26", T(1, 0, 0, 0, cost: 1)),
            new UsageDailyTotals("2026-10-22", T(1, 0, 0, 0, cost: 2)),
            new UsageDailyTotals("2026-10-01", T(1, 0, 0, 0, cost: 4)),
        };

        var windows = UsageDashboardProjection.CostWindows(daily, filters);

        Assert.Equal([30, 1, 7], windows.Select(w => w.Days));
        Assert.True(windows[0].IsSelectedRange);
        Assert.Equal([7d, 1d, 3d], windows.Select(w => w.Totals.TotalCost));
    }

    [Fact]
    public void Insights_FollowUpstreamRatios()
    {
        var report = new SessionsUsageReport
        {
            Totals = T(600, 100, 300, 100, cost: 2.2),
            Aggregates = new UsageAggregates
            {
                Messages = new UsageMessageCounts(100, 50, 50, 10, 10, 4),
                Tools = new UsageToolSummary(10, 2, [new UsageToolCount("read", 7), new UsageToolCount("exec", 3)]),
            },
            Overview = new UsageOverview { DurationMs = 600_000, DurationCount = 2, UnfilteredSessionCount = 9 },
        };

        var insights = UsageDashboardProjection.Insights(report, sessionCount: 3);

        Assert.Equal(300d / 1000, insights.CacheHitRate);
        Assert.Equal(1000, insights.CacheBase);
        Assert.Equal(11, insights.AverageTokensPerMessage);
        Assert.Equal(0.022, insights.AverageCostPerMessage!.Value, 10);
        Assert.Equal(0.04, insights.ErrorRate);
        Assert.Equal(110, insights.ThroughputTokensPerMinute);
        Assert.Equal(300_000, insights.AverageSessionDurationMs);
        Assert.Equal(9, insights.TotalSessions);
        Assert.Equal([1, 3d / 7], insights.Lists[UsageInsightList.TopTools].Select(t => t.Weight));
    }

    [Fact]
    public void Insights_WithholdRatiosForLowerBoundTotals()
    {
        var report = new SessionsUsageReport
        {
            Totals = T(600, 100, 300, 100, cost: 2.2, report: UsageReportState.Unavailable),
            Aggregates = new UsageAggregates { Messages = new UsageMessageCounts(100, 50, 50, 0, 0, 0) },
            Overview = new UsageOverview { DurationMs = 600_000, DurationCount = 1 },
        };

        var insights = UsageDashboardProjection.Insights(report, 1);

        Assert.True(insights.Partial);
        Assert.Null(insights.CacheHitRate);
        Assert.Null(insights.AverageTokensPerMessage);
        Assert.Null(insights.ThroughputTokensPerMinute);
    }

    [Fact]
    public void TopLists_RankByCostSkipUnusedRowsMergeProvidersAndFlagUnpriced()
    {
        var report = new SessionsUsageReport
        {
            Aggregates = new UsageAggregates
            {
                ByModel =
                [
                    new UsageModelBreakdown("openai", "gpt", 4, T(100, 0, 0, 0, cost: 2)),
                    new UsageModelBreakdown("local", "llama", 2, T(500, 0, 0, 0, missing: 3)),
                    new UsageModelBreakdown("mirror", "none", 1, T(0, 0, 0, 0)),
                ],
                ByProvider =
                [
                    new UsageModelBreakdown("openai", null, 2, T(50, 0, 0, 0, cost: 1)),
                    new UsageModelBreakdown("openai", null, 2, T(50, 0, 0, 0, cost: 1)),
                    new UsageModelBreakdown("anthropic", null, 1, T(10, 0, 0, 0, cost: 3)),
                ],
            },
        };

        var lists = UsageDashboardProjection.Insights(report, 1).Lists;

        var models = lists[UsageInsightList.TopModels];
        Assert.Equal(["gpt", "llama"], models.Select(m => m.Label));
        Assert.True(models[1].Unpriced);
        Assert.Equal("openai", models[0].Source);
        var providers = lists[UsageInsightList.TopProviders];
        Assert.Equal(["anthropic", "openai"], providers.Select(p => p.Label));
        Assert.Equal(2, providers[1].Cost);
        Assert.Equal(4, providers[1].Count);
    }

    [Fact]
    public void PeakErrorHours_RankByErrorRate()
    {
        var messages = new long[24];
        var errors = new long[24];
        (messages[9], errors[9]) = (100, 2);
        (messages[14], errors[14]) = (10, 1);
        (messages[20], errors[20]) = (5, 0);
        var report = new SessionsUsageReport { Overview = new UsageOverview { HourlyMessages = messages, HourlyErrors = errors } };

        var hours = UsageDashboardProjection.Insights(report, 1).Lists[UsageInsightList.PeakErrorHours];

        Assert.Equal(["14", "9"], hours.Select(h => h.Label));
        Assert.Equal(0.1, hours[0].Rate);
    }

    [Fact]
    public void Activity_NormalizesIntensityAndFindsPeaks()
    {
        var hours = new long[24];
        hours[10] = 400;
        hours[11] = 100;
        var overview = new UsageOverview { HourTokens = hours, WeekdayTokens = [0, 50, 200, 0, 0, 0, 0], HasTimelineData = true };

        var activity = UsageDashboardProjection.Activity(overview, [11]);

        Assert.Equal(10, activity.PeakHour);
        Assert.Equal(DayOfWeek.Tuesday, activity.PeakWeekday);
        Assert.Equal(0.25, activity.Hours[11].Intensity);
        Assert.True(activity.Hours[11].Selected);
        Assert.Equal(0.25, activity.Weekdays[1].Intensity);
    }

    [Fact]
    public void Heatmap_AppearsOnlyBeyondSixtyTwoDaysWithQuartileLevels()
    {
        var report = new SessionsUsageReport
        {
            Aggregates = new UsageAggregates
            {
                CostDaily =
                [
                    new UsageDailyTotals("2026-10-20", T(10, 0, 0, 0)),
                    // A repeated date must not throw while the page renders.
                    new UsageDailyTotals("2026-10-20", T(10, 0, 0, 0)),
                    new UsageDailyTotals("2026-10-21", T(20, 0, 0, 0)),
                    new UsageDailyTotals("2026-10-22", T(30, 0, 0, 0)),
                    new UsageDailyTotals("2026-10-23", T(40, 0, 0, 0)),
                ],
            },
        };
        var quarter = UsageFilters.ForPreset(new UsageFilters(), UsagePreset.Last90Days, Today);

        Assert.Null(UsageDashboardProjection.Project(report, UsageFilters.ForPreset(new UsageFilters(), UsagePreset.Last30Days, Today),
            UsageMetric.Tokens, UsageDailyChartMode.Total, Today).Heatmap);
        var heatmap = UsageDashboardProjection.Project(report, quarter, UsageMetric.Tokens, UsageDailyChartMode.Total, Today).Heatmap!;

        var cells = heatmap.Weeks.SelectMany(w => w).ToDictionary(c => c.Date);
        Assert.All(heatmap.Weeks, w => Assert.Equal(DayOfWeek.Sunday, w[0].Date.DayOfWeek));
        Assert.Equal([1, 2, 3, 4], new[] { 20, 21, 22, 23 }.Select(d => cells[new DateOnly(2026, 10, d)].Level));
        Assert.Equal(0, cells[new DateOnly(2026, 10, 24)].Level);
        Assert.False(cells[quarter.Start.AddDays(-1)].InRange);
    }

    [Fact]
    public void SessionsView_AveragesOverEverySessionTheTableTotalsCover()
    {
        var report = new SessionsUsageReport
        {
            Sessions = [new SessionUsageRow { Key = "recent" }],
            Overview = new UsageOverview { Total = 1, TableSessionCount = 10, TableTotals = new UsageTableTotals(1000, 5, 0, UsageReportState.Available) },
        };

        var sessions = UsageDashboardProjection.Sessions(report, UsageFilters.Default(Today) with { RecentOnly = true });

        // "Recently viewed" lists one row, but the totals cover all ten matching sessions, as upstream averages them.
        Assert.Equal(1, sessions.Total);
        Assert.Equal(10, sessions.TableSessions);
    }

    [Fact]
    public void SessionRows_UseLabelFallbackOverridesAndPendingState()
    {
        var rows = UsageDashboardProjection.Sessions(new SessionsUsageReport
        {
            Sessions =
            [
                new SessionUsageRow { Key = "agent:main:a", ModelProvider = "openai", ProviderOverride = "anthropic", Model = "m", HistoricalInstanceCount = 3,
                    Usage = new SessionUsageSummary { Totals = T(1, 1, 0, 0, cost: 0.5, report: UsageReportState.Unavailable),
                        MessageCounts = new UsageMessageCounts(4, 2, 2, 1, 1, 1), ToolUsage = new UsageToolSummary(5, 1, []) } },
                new SessionUsageRow { Key = "agent:main:b", Label = "Planning", Computing = true },
            ],
        }, Week with { SelectedSessions = ["agent:main:b"] });

        Assert.Equal("agent:main:a", rows.Rows[0].Title);
        Assert.Equal("anthropic", rows.Rows[0].Provider);
        Assert.Equal(5, rows.Rows[0].ToolCalls);
        Assert.True(rows.Rows[0].LowerBound);
        Assert.Equal(3, rows.Rows[0].InstanceCount);
        Assert.Equal("Planning", rows.Rows[1].Title);
        Assert.True(rows.Rows[1].Computing);
        Assert.True(rows.Rows[1].Selected);
        Assert.Equal("agent:main:b", Assert.Single(rows.SelectedRows).Key);
    }

    [Fact]
    public void ReleasedGatewayReport_ListsEveryRowNewestFirstAndTakesDurationsFromRows()
    {
        SessionUsageRow Row(string key, int day, long durationMs) => new()
        {
            Key = key,
            Usage = new SessionUsageSummary
            {
                Totals = T(10, 0, 0, 0),
                LastActivity = new DateTimeOffset(2026, 10, day, 12, 0, 0, TimeSpan.Zero),
                DurationMs = durationMs,
            },
        };
        var report = new SessionsUsageReport
        {
            Sessions = [Row("agent:main:old", 20, 60_000), Row("agent:main:new", 25, 120_000), Row("agent:main:mid", 22, 0)],
            Totals = T(30, 0, 0, 0),
        };

        var sessions = UsageDashboardProjection.Sessions(report, Week);
        var insights = UsageDashboardProjection.Insights(report, 3);

        Assert.Equal(["agent:main:new", "agent:main:mid", "agent:main:old"], sessions.Rows.Select(r => r.Key));
        Assert.Equal(3, sessions.Limit);
        Assert.Equal(0, sessions.Offset);
        Assert.Equal(90_000, insights.AverageSessionDurationMs);
        Assert.Equal(10, insights.ThroughputTokensPerMinute!.Value, 6);
    }

    [Fact]
    public void EmptyReport_IsTheEmptyStateButAnIncompleteOneIsNot()
    {
        var empty = new SessionsUsageReport();

        Assert.True(UsageDashboardProjection.Project(empty, Week, UsageMetric.Tokens, UsageDailyChartMode.Total, Today).IsEmpty);
        Assert.False(UsageDashboardProjection.Project(empty with { CacheStatus = new UsageCacheStatus("refreshing", 4, 0, null) },
            Week, UsageMetric.Tokens, UsageDailyChartMode.Total, Today).IsEmpty);
    }

    [Theory]
    [InlineData(999, "999")]
    [InlineData(1_000, "1.0K")]
    [InlineData(1_550, "1.6K")]
    [InlineData(2_340_000, "2.3M")]
    [InlineData(7_000_000_000, "7.0B")]
    public void Tokens_UseCompactUnits(long tokens, string expected) =>
        Assert.Equal(expected, UsageFormat.Tokens(tokens, CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(1.234, "$1.23")]
    [InlineData(0.0042, "$0.0042")]
    [InlineData(0.00001234, "$0.000012")]
    [InlineData(0, "$0.00")]
    public void AnalysisCost_KeepsSmallAmountsReadable(double value, string expected) =>
        Assert.Equal(expected, UsageFormat.AnalysisCost(value, CultureInfo.GetCultureInfo("en-US")));

    [Fact]
    public void Cost_UsesTheUserCultureWithDollars() =>
        Assert.Equal("1 234,50 $", UsageFormat.Cost(1234.5, CultureInfo.GetCultureInfo("fr-FR")).Replace('\u202F', ' ').Replace('\u00A0', ' '));

    [Theory]
    [InlineData(45_000, "45s")]
    [InlineData(90_000, "1m 30s")]
    [InlineData(5_400_000, "1h 30m")]
    [InlineData(183_600_000, "2d 3h")]
    [InlineData(double.MaxValue, "36500d")]
    public void Duration_ShowsTwoUnits(double ms, string expected) => Assert.Equal(expected, UsageFormat.Duration(ms));
}
