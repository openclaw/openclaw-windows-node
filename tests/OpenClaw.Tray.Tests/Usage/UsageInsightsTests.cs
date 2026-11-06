using OpenClaw.Shared.Usage;
using OpenClawTray.Presentation.Usage;
using System.Globalization;

namespace OpenClaw.Tray.Tests.Usage;

public sealed class UsageInsightsTests
{
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
