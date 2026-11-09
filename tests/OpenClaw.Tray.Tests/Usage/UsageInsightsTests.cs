using OpenClaw.Shared.Usage;
using OpenClawTray.Presentation.Usage;
using System.Globalization;

namespace OpenClaw.Tray.Tests.Usage;

public sealed class UsageInsightsTests
{
    [Fact]
    public void MixedPricing_InContributorPreventsExactHeroShareEvenWhenRangeOmitsMissingCount()
    {
        var report = new SessionsUsageReport
        {
            Totals = new() { TotalCost = 10, TotalTokens = 100 },
            Aggregates = new()
            {
                ByAgent = [new("main", new() { TotalCost = 5, TotalTokens = 50, MissingCostEntries = 1 })],
            },
        };
        var dashboard = UsageDashboardProjection.Project(report, UsageFilters.Default(new(2026, 10, 30)), UsageMetric.Cost);
        Assert.Null(UsageInsights.HeroContributors(dashboard).CostShare);
    }

    [Theory]
    [InlineData(null, 10, "session0999")]
    [InlineData("session0999", 10, "session0999")]
    [InlineData("session0000", 11, "session0000")]
    [InlineData("missing", 10, "session0999")]
    public void BasicSessionDisclosure_KeepsTenRowsPlusAtMostTheSelectedLoadedRow(string? selected, int count, string first)
    {
        var sessions = UsageDashboardProjection.Sessions(new()
        {
            Sessions = Enumerable.Range(0, 1000).Select(index => new SessionUsageRow
            {
                Key = $"session{index:D4}", UpdatedAt = DateTimeOffset.UnixEpoch.AddMinutes(index),
            }).ToArray(),
        });

        var visible = UsageInsights.VisibleSessions(sessions, 10, selected);

        Assert.Equal(count, visible.Count);
        Assert.Equal(first, visible[0].Key);
        Assert.Equal(count, visible.Select(row => row.Key).Distinct().Count());
        Assert.Contains(visible, row => row.Key == "session0990");
        Assert.DoesNotContain(visible, row => row.Key == "session0500");
        Assert.Equal(21, UsageInsights.VisibleSessions(sessions, 20, "session0000").Count);
        Assert.Equal(10, UsageInsights.VisibleSessions(sessions, 10, null).Count);
    }

    [Fact]
    public void ServerPagedSessionDisclosure_PreservesTheServerPage()
    {
        var sessions = UsageDashboardProjection.Sessions(new()
        {
            Overview = new UsageOverview { Total = 100, Offset = 50, Limit = 50 },
            Sessions = Enumerable.Range(50, 50).Select(index => new SessionUsageRow { Key = index.ToString() }).ToArray(),
        });
        var visible = UsageInsights.VisibleSessions(sessions, 10, "1");
        Assert.Equal(50, visible.Count);
        Assert.Equal("50", visible[0].Key);
        Assert.Equal("99", visible[^1].Key);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void MixedPricing_QualifiesCostWithoutDiscardingCompleteTokenCounts(int missing, bool costComplete)
    {
        var partial = new UsageTotals { TotalTokens = 50, TotalCost = 5, MissingCostEntries = missing };
        var report = new SessionsUsageReport
        {
            Totals = new() { TotalTokens = 100, TotalCost = 10, MissingCostEntries = missing },
            Aggregates = new() { ByAgent = [new("partial", partial), new("priced", new() { TotalCost = 5, TotalTokens = 50 })] },
            Sessions =
            [
                new() { Key = "partial", Usage = new() { Totals = partial } },
                new() { Key = "priced", Usage = new() { Totals = new() { TotalCost = 10, TotalTokens = 50 } } },
            ],
        };
        var dashboard = UsageDashboardProjection.Project(report, UsageFilters.Default(new(2026, 10, 30)), UsageMetric.Cost);
        var contributors = dashboard.Contributors[UsageContributorKind.Agent];
        var contributor = Assert.Single(contributors, row => row.Label == "partial");
        Assert.Equal(!costComplete, contributor.CostIncomplete);
        Assert.False(contributor.Unpriced);
        Assert.False(contributor.LowerBound);
        Assert.True(contributor.TokensKnown);
        Assert.Equal(costComplete, UsageInsights.ContributorSharesKnown(dashboard.Summary, contributors, UsageMetric.Cost));
        Assert.True(UsageInsights.ContributorSharesKnown(dashboard.Summary, contributors, UsageMetric.Tokens));
        Assert.Equal(!costComplete, Assert.Single(dashboard.Sessions.Rows, row => row.Key == "partial").CostIncomplete);
        Assert.Equal(costComplete ? "priced" : null, UsageInsights.CostliestLoadedSession(dashboard)?.Key);
        Assert.Null(UsageInsights.CostliestLoadedSession(dashboard with
        {
            Sessions = dashboard.Sessions with { Rows = [dashboard.Sessions.Rows[0] with { Computing = true }] },
        }));
        Assert.False(UsageInsights.ContributorSharesKnown(dashboard.Summary with { Partial = true }, contributors, UsageMetric.Tokens));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(74.99, false)]
    [InlineData(75, true)]
    [InlineData(75.01, true)]
    [InlineData(100, true)]
    public void NearLimit_UsesInclusiveReportedAllowancePercentage(double percent, bool expected) =>
        Assert.Equal(expected, UsageProviderAttention.HighestWindow(new()
        {
            Windows = [new("Week", null, percent, null)],
        }, DateTimeOffset.UnixEpoch) is not null);

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(100d)]
    public void NearLimit_DoesNotInventAllowanceFromMissingZeroOrReportedBillingLimit(double? limit) =>
        Assert.Null(UsageProviderAttention.HighestWindow(new()
        {
            Billing = [new("budget", "Budget", 90, limit, "USD", null)],
        }, DateTimeOffset.UnixEpoch));

    [Fact]
    public void ProviderWarning_UsesTheLiveWindowRatherThanTheExpiredMaximumAndRejectsErrors()
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var provider = new ProviderUsageSnapshot
        {
            Provider = "mixed",
            Windows = [new("Expired", null, 99, now), new("Live", null, 80, now.AddHours(1))],
        };
        var warning = UsageProviderAttention.HighestWindow(provider, now);
        Assert.NotNull(warning);
        Assert.Equal("Live", warning.Label);
        Assert.Equal(80, warning.UsedPercent);
        Assert.Null(UsageProviderAttention.HighestWindow(provider with { Error = "unavailable" }, now));
        Assert.Null(UsageProviderAttention.HighestWindow(provider, now.AddHours(1)));
        Assert.Equal(2, provider.Windows.Count); // Historical display facts are not pruned.
    }

    [Theory]
    [InlineData("agent:main:cron:job:run:42", true)]
    [InlineData("agent:main:cron:job", true)]
    [InlineData("agent:cron:webchat:main", false)]
    [InlineData("agent:main:webchat:mentions-cron", false)]
    public void AutomationTag_FollowsTheSessionClassification(string key, bool expected) =>
        Assert.Equal(expected, UsageInsights.IsAutomation(key));

    [Fact]
    public void Attention_UsesReportedLiveWindowsAndNeverStaleOrFailedProviderData()
    {
        var high = new ProviderUsageSnapshot
        {
            Provider = "high", Windows = [new("week", null, 75, DateTimeOffset.UtcNow.AddDays(1))],
        };
        var low = high with { Provider = "low", Windows = [new("week", null, 74.9, null)] };
        var expired = high with { Provider = "expired", Windows = [new("week", null, 100, DateTimeOffset.UtcNow.AddDays(-1))] };
        var error = high with { Provider = "error", Error = "Unavailable" };
        var state = new UsagePageState
        {
            Filters = UsageFilters.Default(new(2026, 10, 30)),
            Availability = UsageAvailability.Connected,
            Providers = new(UsageLoadPhase.Loaded, new(null, [low, high, expired, error], false), null),
        };
        Assert.Equal(["high"], UsageInsights.Attention(state).Select(provider => provider.Provider));
        Assert.Empty(UsageInsights.Attention(state with { Availability = UsageAvailability.Disconnected }));
        Assert.Empty(UsageInsights.Attention(state with { Providers = state.Providers with { Phase = UsageLoadPhase.Failed } }));
        Assert.Empty(UsageInsights.Attention(state with
        {
            Providers = state.Providers with { Value = state.Providers.Value! with { Refreshing = true } },
        }));
    }

    [Fact]
    public void ModelDrilldown_DoesNotMixProvidersWithTheSameModelName()
    {
        var report = new SessionsUsageReport
        {
            Sessions =
            [
                new() { Key = "one", ModelProvider = "a", Model = "shared" },
                new() { Key = "two", ModelProvider = "b", Model = "shared" },
                new() { Key = "three", ModelProvider = "a", Model = "different" },
            ],
        };
        var model = new UsageContributor("shared", "b", 10, 1, false, false);
        Assert.Equal(["two"], UsageInsights.LoadedSessions(UsageDashboardProjection.Sessions(report), UsageContributorKind.Model, model)
            .Select(row => row.Key));
    }

    [Fact]
    public void DailyPricing_KeepsAnUnpricedDayDistinctFromAGenuineZero()
    {
        var filters = UsageFilters.Default(new(2026, 10, 30));
        var report = new SessionsUsageReport
        {
            Aggregates = new()
            {
                CostDaily =
                [
                    new("2026-10-29", new() { TotalTokens = 100, MissingCostEntries = 1 }),
                    new("2026-10-30", new() { TotalTokens = 100, TotalCost = 0 }),
                ],
            },
        };
        var chart = UsageDashboardProjection.DailyChart(report, filters, UsageMetric.Cost);
        Assert.True(chart.Days.Single(day => day.Iso == "2026-10-29").MissingPrice);
        Assert.False(chart.Days.Single(day => day.Iso == "2026-10-30").MissingPrice);
    }

    [Theory]
    [InlineData(30, 10, "Today 2:18 PM")]
    [InlineData(1, 11, "Sun 2:18 PM")]
    [InlineData(20, 10, "Oct 20")]
    public void FriendlyDates_UseTodayWeekdaysOrShortDates(int day, int month, string expected)
    {
        var result = UsageFormat.FriendlyDate(new(2026, month, day, 14, 18, 0), new(2026, 10, 30),
            CultureInfo.GetCultureInfo("en-US"), "Today");
        Assert.Equal(expected, result.Replace('\u202F', ' ').Replace('\u00A0', ' '));
    }
}
