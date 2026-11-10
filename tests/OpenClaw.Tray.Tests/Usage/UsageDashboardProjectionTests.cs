using System.Globalization;
using OpenClaw.Shared.Usage;
using OpenClawTray.Presentation;
using OpenClawTray.Presentation.Usage;

namespace OpenClaw.Tray.Tests.Usage;

/// <summary>
/// Pins the Usage page's derived numbers to the upstream web formulas (openclaw/openclaw@5089aa71
/// ui/src/pages/usage). Inputs are small hand-built reports; expected values are computed by hand.
/// </summary>
public sealed class UsageDashboardProjectionTests
{
    [Theory]
    [InlineData(331.99, 1)]
    [InlineData(332, 2)]
    [InlineData(503.99, 2)]
    [InlineData(504, 3)]
    [InlineData(504.01, 3)]
    [InlineData(503.994, 2)]
    [InlineData(503.996, 3)]
    [InlineData(1200, 3)]
    public void DailyFacts_KeepAllThreeInOneColumnUntilThreeMinimumWidthColumnsFit(double innerWidth, int expected) =>
        Assert.Equal(expected, UsagePageLayout.DailyFactColumns(innerWidth));

    [Theory]
    [InlineData(408, 2)]
    [InlineData(664, 2)]
    [InlineData(713, 2)]
    [InlineData(834, 2)]
    [InlineData(835.99, 2)]
    [InlineData(835.994, 2)]
    [InlineData(835.996, 4)]
    [InlineData(836, 4)]
    [InlineData(1200, 4)]
    public void ActivityTiles_UseOnlyCompleteOneOrTwoRowStrips(double width, int expected)
    {
        Assert.Equal(expected, UsagePageLayout.ActivityColumns(width));
        if (expected == 4)
            Assert.True(PageResponsiveLayout.TileWidth(PageResponsiveLayout.Snap(width), expected) >= 200);
    }

    [Theory]
    [InlineData(1400, PageWidthTier.Wide, 1200, 4)]
    [InlineData(1000, PageWidthTier.Wide, 936, 3)]
    [InlineData(999.99994, PageWidthTier.Wide, 936, 3)]
    [InlineData(760, PageWidthTier.Medium, 696, 2)]
    [InlineData(720, PageWidthTier.Medium, 656, 2)]
    [InlineData(719.99994, PageWidthTier.Medium, 656, 2)]
    [InlineData(520, PageWidthTier.Narrow, 480, 1)]
    [InlineData(480, PageWidthTier.Narrow, 440, 1)]
    public void ResponsiveContract_UsesHostWidthsSnappedBoundariesAndEqualTiles(
        double host, PageWidthTier tier, double content, int tiles)
    {
        Assert.Equal(tier, PageResponsiveLayout.TierFor(host));
        Assert.Equal(content, PageResponsiveLayout.ContentWidth(host));
        Assert.Equal(tiles, PageResponsiveLayout.TileColumns(content, 240));
        var cell = PageResponsiveLayout.TileWidth(content, tiles);
        Assert.Equal(content, cell * tiles + (tiles - 1) * 12, 6);
        Assert.Equal(tier == PageWidthTier.Wide, new UsagePageLayout(host).TwoColumns);
    }

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

    [Theory]
    [InlineData(false, false, 0, true, 0.46)]
    [InlineData(true, false, 0, true, null)]
    [InlineData(false, true, 0, true, null)]
    [InlineData(false, false, 1, true, null)]
    [InlineData(false, false, 0, false, null)]
    public void HeroCostShare_RequiresCompletePricedTotalsWithoutClaimingAgentModelAssociation(
        bool partial, bool lowerBound, int missingPrices, bool costKnown, double? expectedShare)
    {
        var dashboard = UsageDashboardProjection.Project(new SessionsUsageReport(), Week, UsageMetric.Cost) with
        {
            Summary = new UsageSummary(1000, 100, 63, lowerBound, partial, missingPrices, CostKnown: costKnown),
            Contributors = new Dictionary<UsageContributorKind, IReadOnlyList<UsageContributor>>
            {
                [UsageContributorKind.Agent] = [new("Atlas", null, 460, 46, false, false)],
                [UsageContributorKind.Model] = [new("Independent model", "provider", 600, 60, false, false)],
            },
        };
        var facts = UsageInsights.HeroContributors(dashboard);
        Assert.Equal("Atlas", facts.Agent);
        Assert.Equal(expectedShare, facts.CostShare);
        Assert.Equal("Independent model", facts.Model);
    }

    [Theory]
    [InlineData(0, false, false, true, null)]
    [InlineData(101, false, false, true, null)]
    [InlineData(46, true, false, true, null)]
    [InlineData(46, false, true, true, null)]
    [InlineData(46, false, false, false, null)]
    [InlineData(100, false, false, true, 1d)]
    public void HeroCostShare_RejectsUnpricedUnknownOrInconsistentAgentCosts(
        double agentCost, bool unpriced, bool lowerBound, bool known, double? expected)
    {
        var dashboard = UsageDashboardProjection.Project(new SessionsUsageReport(), Week, UsageMetric.Cost) with
        {
            Summary = new UsageSummary(1000, 100, 63, false, false, 0),
            Contributors = new Dictionary<UsageContributorKind, IReadOnlyList<UsageContributor>>
            {
                [UsageContributorKind.Agent] = [new("Atlas", null, 460, agentCost, unpriced, lowerBound, CostKnown: known)],
            },
        };
        Assert.Equal(expected, UsageInsights.HeroContributors(dashboard).CostShare);
    }

    [Fact]
    public void Presets_ResolveInclusiveRangesEndingToday()
    {
        Assert.Equal((new DateOnly(2026, 10, 20), Today), Range(UsagePreset.Last7Days));
        Assert.Equal((Today, Today), Range(UsagePreset.Today));
        Assert.Equal((new DateOnly(2026, 9, 27), Today), Range(UsagePreset.Last30Days));

        static (DateOnly, DateOnly) Range(UsagePreset preset)
        {
            var f = UsageFilters.ForPreset(new UsageFilters(), preset, Today);
            return (f.Start, f.End);
        }
    }

    [Fact]
    public void CustomRange_IsNormalizedAndResetsPaging()
    {
        var custom = (Week with { Offset = 50 }).WithRange(new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 1));

        Assert.Equal(UsagePreset.Custom, custom.Preset);
        Assert.Equal(new DateOnly(2026, 10, 1), custom.Start);
        Assert.Equal(new DateOnly(2026, 10, 10), custom.End);
        Assert.Equal(0, custom.Offset);
    }

    [Fact]
    public void OverviewQuery_SendsSearchSortAndPageWithTheProjection()
    {
        var query = (Week with { Query = " model:gpt ", Sort = UsageSort.Errors, SortDescending = false, Offset = 50 }).ToOverviewQuery();

        Assert.True(query.OverviewProjection);
        Assert.Equal(("model:gpt", UsageSort.Errors, false, 50), (query.Query, query.Sort, query.SortDescending, query.Offset));
        Assert.Null(Week.ToOverviewQuery().Query);
    }

    [Fact]
    public void DailyChart_ShowsOnlyReportedDaysEvenWhenFreshOrIncomplete()
    {
        var report = new SessionsUsageReport
        {
            Aggregates = new UsageAggregates
            {
                CostDaily = [new UsageDailyTotals("2026-10-22", T(100, 20, 0, 0, cost: 0.5))],
            },
        };

        var complete = UsageDashboardProjection.DailyChart(report, Week, UsageMetric.Tokens);
        var day = Assert.Single(complete.Days);
        Assert.Equal(new DateOnly(2026, 10, 22), day.Date);
        Assert.Equal(120, day.Value);
        Assert.False(day.Pending);
        Assert.False(complete.CoversRange);
        Assert.Equal(200, complete.Ceiling);

        var incomplete = UsageDashboardProjection.DailyChart(
            report with { CacheStatus = new UsageCacheStatus("refreshing", 2, 0, null) }, Week, UsageMetric.Cost);
        Assert.False(Assert.Single(incomplete.Days).Pending);
        Assert.False(incomplete.CoversRange);
        Assert.Equal(0.5, incomplete.Days[0].Value);
    }

    [Fact]
    public void DailyChart_RepeatedDatesDoNotThrowAndLongRangesShowRecentDaysWithData()
    {
        var report = new SessionsUsageReport
        {
            Aggregates = new UsageAggregates
            {
                CostDaily =
                [
                    new UsageDailyTotals("2024-01-02", T(5, 0, 0, 0)),
                    new UsageDailyTotals("2026-10-20", T(10, 0, 0, 0)),
                    new UsageDailyTotals("2026-10-20", T(30, 0, 0, 0)),
                ],
            },
        };
        var twoYears = Week.WithRange(new DateOnly(2024, 1, 1), Today);

        var chart = UsageDashboardProjection.DailyChart(report, twoYears, UsageMetric.Tokens);

        Assert.Equal([new DateOnly(2024, 1, 2), new DateOnly(2026, 10, 20)], chart.Days.Select(d => d.Date));
        Assert.Equal(30, chart.Days[1].Value);
    }

    [Fact]
    public void Activity_DefinesErrorRateOverMessagesAndCacheHitOverPromptTokens()
    {
        var report = new SessionsUsageReport
        {
            Totals = T(600, 100, 300, 100, cost: 2.2),
            Aggregates = new UsageAggregates
            {
                Messages = new UsageMessageCounts(100, 50, 50, 10, 10, 4),
                Tools = new UsageToolSummary(12, 2),
            },
        };

        var activity = UsageDashboardProjection.Activity(report);

        // 4 errors / 100 messages; 300 cache read / (600 input + 300 read + 100 write).
        Assert.Equal(0.04, activity.ErrorRate);
        Assert.Equal(0.3, activity.CacheHitRate);
        Assert.Equal(1000, activity.CacheBase);
        Assert.Equal(12, activity.ToolCalls);
        Assert.Null(UsageDashboardProjection.Activity(new SessionsUsageReport()).ErrorRate);
    }

    [Theory]
    [InlineData("subtotal")]
    [InlineData("partial")]
    [InlineData("cache")]
    public void Activity_WithholdsRatiosForIncompleteCounters(string reason)
    {
        var report = new SessionsUsageReport
        {
            Totals = T(600, 100, 300, 100, report: reason == "subtotal" ? UsageReportState.Unavailable : UsageReportState.Available),
            Partial = reason == "partial" ? new("time-budget", "Incomplete") : null,
            CacheStatus = reason == "cache" ? new("stale", 0, 1, null) : null,
            Aggregates = new UsageAggregates { Messages = new UsageMessageCounts(10, 5, 5, 0, 0, 1) },
        };

        var activity = UsageDashboardProjection.Activity(report);

        Assert.True(activity.LowerBound);
        Assert.Null(activity.CacheHitRate);
        Assert.Null(activity.ErrorRate);
        Assert.Equal(300, activity.CacheRead);
    }

    [Fact]
    public void Activity_KeepsReportedZeroToolsRatherThanReplacingItWithAnotherCounter()
    {
        var activity = UsageDashboardProjection.Activity(new SessionsUsageReport
        {
            Aggregates = new UsageAggregates
            {
                Tools = new UsageToolSummary(0, 0),
                Messages = new UsageMessageCounts(10, 5, 5, 9, 9, 0),
            },
        });

        Assert.True(activity.ToolCallsKnown);
        Assert.Equal(0, activity.ToolCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MergedProviderCounters_AreKnownOnlyWhenEveryContributingSubtotalReportedThem(bool typedSubtotal)
    {
        using var json = System.Text.Json.JsonDocument.Parse("""{"totals":{"totalTokens":20}}""");
        var incomplete = UsageJsonParser.ParseSessionsUsage(json.RootElement).Totals;
        using var completeJson = System.Text.Json.JsonDocument.Parse("""{"totals":{"totalTokens":10,"totalCost":1}}""");
        var complete = typedSubtotal ? T(10, 0, 0, 0, cost: 1) : UsageJsonParser.ParseSessionsUsage(completeJson.RootElement).Totals;
        var contributors = UsageDashboardProjection.Contributors(new UsageAggregates
        {
            ByProvider = [new("provider", null, 1, complete), new("provider", null, 1, incomplete)],
        });

        var provider = Assert.Single(contributors[UsageContributorKind.Provider]);
        Assert.Equal(30, provider.Tokens);
        Assert.True(provider.TokensKnown);
        Assert.False(provider.CostKnown);
    }

    [Fact]
    public void MissingSessionUsage_DoesNotInventAGatewayComputingStatus()
    {
        var row = UsageDashboardProjection.Row(new SessionUsageRow { Key = "missing" }, selected: false);

        Assert.False(row.Computing);
        Assert.False(row.CostKnown);
        Assert.False(row.TokensKnown);
        Assert.True(UsageDashboardProjection.Row(new SessionUsageRow { Key = "pending", Computing = true }, selected: false).Computing);
    }

    [Fact]
    public void Contributors_RankByCostThenTokensSkipUnusedMergeProvidersAndFlagUnpriced()
    {
        var aggregates = new UsageAggregates
        {
            ByModel =
            [
                new UsageModelBreakdown("local", "llama", 2, T(500_000, 0, 0, 0, missing: 3)),
                new UsageModelBreakdown("openai", "gpt", 4, T(100, 0, 0, 0, cost: 2)),
                new UsageModelBreakdown("mirror", "none", 1, T(0, 0, 0, 0)),
            ],
            ByProvider =
            [
                new UsageModelBreakdown("openai", null, 2, T(50, 0, 0, 0, cost: 1)),
                new UsageModelBreakdown("openai", null, 2, T(50, 0, 0, 0, cost: 1)),
                new UsageModelBreakdown("anthropic", null, 1, T(10, 0, 0, 0, cost: 3)),
            ],
            ByAgent = Enumerable.Range(1, 7).Select(i => new UsageNamedTotals("agent" + i, T(i, 0, 0, 0, cost: i))).ToList(),
        };

        var contributors = UsageDashboardProjection.Contributors(aggregates);

        var models = contributors[UsageContributorKind.Model];
        Assert.Equal(["gpt", "llama"], models.Select(m => m.Label));
        Assert.Equal("openai", models[0].Provider);
        Assert.True(models[1].Unpriced);
        var providers = contributors[UsageContributorKind.Provider];
        Assert.Equal(["anthropic", "openai"], providers.Select(p => p.Label));
        Assert.Equal((2d, 100L), (providers[1].Cost, providers[1].Tokens));
        Assert.Equal(["agent7", "agent6", "agent5", "agent4", "agent3", "agent2", "agent1"],
            contributors[UsageContributorKind.Agent].Select(a => a.Label));
    }

    [Fact]
    public void SessionsView_UsesServerPagingWhenTheOverviewIsPresent()
    {
        var report = new SessionsUsageReport
        {
            Sessions = [new SessionUsageRow { Key = "a" }],
            Overview = new UsageOverview { Total = 120, Offset = 50, Limit = 50 },
        };

        var sessions = UsageDashboardProjection.Sessions(report);

        Assert.True(sessions.ServerPaged);
        Assert.False(sessions.Truncated);
        Assert.Equal((120, 50, 50), (sessions.Total, sessions.Offset, sessions.Limit));
    }

    [Fact]
    public void SessionRows_UseLabelFallbackOverridesPendingStateAndTheOpenSession()
    {
        var rows = UsageDashboardProjection.Sessions(new SessionsUsageReport
        {
            Sessions =
            [
                new SessionUsageRow { Key = "agent:main:a", ModelProvider = "openai", ProviderOverride = "anthropic", Model = "m", HistoricalInstanceCount = 3,
                    Usage = new SessionUsageSummary { Totals = T(1, 1, 0, 0, cost: 0.5, report: UsageReportState.Unavailable),
                        MessageCounts = new UsageMessageCounts(4, 2, 2, 1, 1, 1), ToolUsage = new UsageToolSummary(5, 1) } },
                new SessionUsageRow { Key = "agent:main:b", Label = "Planning", Computing = true },
            ],
            Overview = new UsageOverview { Total = 2 },
        }, selectedKey: "agent:main:b");

        Assert.Equal("agent:main:a", rows.Rows[0].Title);
        Assert.Equal("anthropic", rows.Rows[0].Provider);
        Assert.True(rows.Rows[0].LowerBound);
        Assert.False(rows.Rows[0].Selected);
        Assert.Equal("Planning", rows.Rows[1].Title);
        Assert.True(rows.Rows[1].Computing);
        Assert.True(rows.Rows[1].Selected);
    }

    [Fact]
    public void ReleasedGatewayReport_ListsRowsNewestFirstAndReportsRowsBeyondTheReadLimit()
    {
        SessionUsageRow Row(string key, int day) => new()
        {
            Key = key,
            Usage = new SessionUsageSummary
            {
                Totals = T(10, 0, 0, 0, cost: 1),
                LastActivity = new DateTimeOffset(2026, 10, day, 12, 0, 0, TimeSpan.Zero),
                MessageCounts = new UsageMessageCounts(2, 1, 1, 0, 0, 1),
            },
        };
        var report = new SessionsUsageReport
        {
            Sessions = [Row("agent:main:old", 20), Row("agent:main:new", 25), Row("agent:main:mid", 22)],
            Totals = T(30, 0, 0, 0),
        };

        var sessions = UsageDashboardProjection.Sessions(report);
        var capped = UsageDashboardProjection.Sessions(report with { Aggregates = new UsageAggregates { SessionCount = 1500 } });

        Assert.Equal(["agent:main:new", "agent:main:mid", "agent:main:old"], sessions.Rows.Select(r => r.Key));
        Assert.False(sessions.ServerPaged);
        Assert.False(sessions.Truncated);
        Assert.True(capped.Truncated);
        Assert.Equal((1500, 3), (capped.Total, capped.Rows.Count));
    }

    [Fact]
    public void SessionTotals_UseTheReportedCountAndNeverTreatARowLimitAsAnExactTotal()
    {
        SessionsUsageReport Basic(int rows, int? count) => new()
        {
            Sessions = Enumerable.Range(0, rows).Select(i => new SessionUsageRow { Key = "k" + i }).ToList(),
            Aggregates = new UsageAggregates { SessionCount = count },
        };
        var limit = SessionsUsageQuery.FullRowLimit;

        // A full read with no reported count may have stopped at the limit: the loaded count is only a floor.
        var full = UsageDashboardProjection.Project(Basic(limit, null), Week, UsageMetric.Tokens);
        Assert.True(full.Sessions.MayHaveMore);
        Assert.False(full.Sessions.Truncated);
        Assert.Equal((limit, true), (full.Summary.Sessions, full.Summary.SessionsAtLeast));

        // Fewer rows than the limit is the whole population.
        var partialPage = UsageDashboardProjection.Project(Basic(limit - 1, null), Week, UsageMetric.Tokens);
        Assert.False(partialPage.Sessions.MayHaveMore);
        Assert.Equal((limit - 1, false), (partialPage.Summary.Sessions, partialPage.Summary.SessionsAtLeast));

        // A reported count is exact, so the list says how many of them are shown.
        var counted = UsageDashboardProjection.Project(Basic(limit, 1500), Week, UsageMetric.Tokens);
        Assert.True(counted.Sessions.Truncated);
        Assert.False(counted.Sessions.MayHaveMore);
        Assert.Equal((1500, limit), (counted.Sessions.Total, counted.Sessions.Rows.Count));
        Assert.Equal((1500, false), (counted.Summary.Sessions, counted.Summary.SessionsAtLeast));
    }

    [Fact]
    public void Summary_KeepsMissingPricesDistinctFromZeroCost()
    {
        var unpriced = new SessionsUsageReport { Totals = T(100, 0, 0, 0, missing: 2) };
        var free = new SessionsUsageReport { Totals = T(100, 0, 0, 0) };

        Assert.Equal(2, UsageDashboardProjection.Project(unpriced, Week, UsageMetric.Cost).Summary.MissingCostEntries);
        Assert.Equal(0, UsageDashboardProjection.Project(free, Week, UsageMetric.Cost).Summary.MissingCostEntries);
    }

    [Fact]
    public void EmptyReport_IsTheEmptyStateButAnIncompleteOneIsNot()
    {
        var empty = new SessionsUsageReport { Totals = T(0, 0, 0, 0) };

        Assert.True(UsageDashboardProjection.Project(empty, Week, UsageMetric.Tokens).IsEmpty);
        Assert.False(UsageDashboardProjection.Project(empty with { CacheStatus = new UsageCacheStatus("refreshing", 4, 0, null) },
            Week, UsageMetric.Tokens).IsEmpty);
    }

    [Fact]
    public void MissingGatewayFields_AreNotAZeroSummaryEmptyStateOrExportableReport()
    {
        using var json = System.Text.Json.JsonDocument.Parse("{}");
        var report = UsageJsonParser.ParseSessionsUsage(json.RootElement);
        var dashboard = UsageDashboardProjection.Project(report, Week, UsageMetric.Cost);
        Assert.False(dashboard.Summary.CostKnown);
        Assert.False(dashboard.Summary.TokensKnown);
        Assert.False(dashboard.Summary.SessionsKnown);
        Assert.False(dashboard.IsEmpty);
        Assert.False(dashboard.Activity.Messages.HasData);
        Assert.False(dashboard.Activity.ToolCallsKnown);
        Assert.Null(dashboard.Activity.CacheHitRate);
        Assert.Empty(dashboard.Chart.Days);
        Assert.False(UsageExport.IsExportable(report));
    }

    [Fact]
    public void ExplicitGatewayZeroDailyTotal_RemainsKnownRatherThanUnavailable()
    {
        using var json = System.Text.Json.JsonDocument.Parse("""{"sessions":[],"totals":{"totalTokens":0,"totalCost":0},"aggregates":{"costDaily":[{"date":"2026-10-22","totalTokens":0,"totalCost":0}]}}""");
        var report = UsageJsonParser.ParseSessionsUsage(json.RootElement);
        var chart = UsageDashboardProjection.DailyChart(report, Week, UsageMetric.Cost);
        Assert.False(Assert.Single(chart.Days).Pending);
        Assert.Equal(0, chart.Days[0].Value);
        Assert.True(UsageDashboardProjection.Project(report, Week, UsageMetric.Cost).IsEmpty);
    }

    [Fact]
    public void ReportedSessionsWithoutLoadedRows_AreNotAnEmptySuccess()
    {
        using var json = System.Text.Json.JsonDocument.Parse(
            """{"sessions":[],"totals":{"totalTokens":0,"totalCost":0},"overview":{"total":3}}""");
        var dashboard = UsageDashboardProjection.Project(UsageJsonParser.ParseSessionsUsage(json.RootElement), Week, UsageMetric.Cost);

        Assert.Equal(3, dashboard.Summary.Sessions);
        Assert.True(dashboard.Summary.SessionsKnown);
        Assert.False(dashboard.IsEmpty);
    }

    [Theory]
    [InlineData("totalCost", false, false, true)]
    [InlineData("totalTokens", true, true, false)]
    public void DailyChart_PreservesTheMissingCounterBesideAKnownSelectedMetric(
        string field, bool tokensMetric, bool tokensKnown, bool costKnown)
    {
        using var json = System.Text.Json.JsonDocument.Parse(
            $$$"""{"aggregates":{"costDaily":[{"date":"2026-10-22","{{{field}}}":12}]}}""");
        var report = UsageJsonParser.ParseSessionsUsage(json.RootElement);
        var day = Assert.Single(UsageDashboardProjection.DailyChart(report, Week, tokensMetric ? UsageMetric.Tokens : UsageMetric.Cost).Days);

        Assert.False(day.Pending);
        Assert.Equal(12, day.Value);
        Assert.Equal(tokensKnown, day.TokensKnown);
        Assert.Equal(costKnown, day.CostKnown);
    }

    [Theory]
    [InlineData(999, "999")]
    [InlineData(1_000, "1K")]
    [InlineData(740_000, "740K")]
    [InlineData(185_000, "185K")]
    [InlineData(1_550, "1.6K")]
    [InlineData(2_340_000, "2.3M")]
    [InlineData(7_000_000_000, "7B")]
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
    [InlineData("en-US", "2026-10-01", "2026-10-30", "{0} to {1}", "Oct 1 to Oct 30")]
    [InlineData("en-US", "2026-10-30", "2026-11-05", "{0} to {1}", "Oct 30 to Nov 5")]
    [InlineData("en-US", "2025-12-30", "2026-01-02", "{0} to {1}", "12/30/2025 to 1/2/2026")]
    [InlineData("fr-FR", "2026-10-30", "2026-11-05", "du {0} au {1}", "du 30 oct. au 5 nov.")]
    public void CompactRange_UsesLocalizedMonthDaysAndPreservesCrossYearIdentity(
        string locale, string start, string end, string format, string expected) =>
        Assert.Equal(expected, UsageFormat.CompactRange(
            DateOnly.ParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateOnly.ParseExact(end, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            CultureInfo.GetCultureInfo(locale), format));

    [Theory]
    [InlineData(45_000, "45s")]
    [InlineData(90_000, "1m 30s")]
    [InlineData(5_400_000, "1h 30m")]
    [InlineData(183_600_000, "2d 3h")]
    [InlineData(double.MaxValue, "36500d")]
    public void Duration_ShowsTwoUnits(double ms, string expected) => Assert.Equal(expected, UsageFormat.Duration(ms));
}
