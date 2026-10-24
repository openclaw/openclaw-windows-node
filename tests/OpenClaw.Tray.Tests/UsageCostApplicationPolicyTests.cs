using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class UsageCostApplicationPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ShouldApply_AcceptsUnspecifiedGatewayPeriod(int responseDays)
    {
        Assert.True(UsageCostApplicationPolicy.ShouldApply(
            responseDays,
            selectedPeriodDays: 7));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void ShouldApply_AcceptsInclusiveRangeAroundSelectedPeriod(int responseDays)
    {
        Assert.True(UsageCostApplicationPolicy.ShouldApply(
            responseDays,
            selectedPeriodDays: 7));
    }

    [Fact]
    public void ShouldApply_RejectsFirstMismatchedResponse()
    {
        Assert.False(UsageCostApplicationPolicy.ShouldApply(
            responseDays: 30,
            selectedPeriodDays: 7));
    }

    [Fact]
    public void Decide_StopsLoadingWhenTheFirstResponseUsesAnotherRange()
    {
        Assert.Equal(
            UsageCostApplicationPolicy.Decision.StopForUnsupportedRange,
            UsageCostApplicationPolicy.Decide(responseDays: 30, selectedPeriodDays: 7, hasLoaded: false));
    }

    [Fact]
    public void RangeWarning_StaysUpWhenProviderStatusArrivesAfterTheCost()
    {
        var warning = new UsageRangeWarning();
        warning.Show();
        Assert.False(warning.ProviderStatusMayCloseTheBar());
    }

    [Fact]
    public void RangeWarning_LetsProviderStatusCloseTheBarBeforeTheCostArrives()
    {
        var warning = new UsageRangeWarning();
        Assert.True(warning.ProviderStatusMayCloseTheBar());
        warning.Show();
        Assert.True(warning.IsVisible);
    }

    [Fact]
    public void RangeWarning_ClearLetsTheNextProviderStatusCloseTheBar()
    {
        var warning = new UsageRangeWarning();
        warning.Show();
        warning.Clear();
        Assert.False(warning.IsVisible);
        Assert.True(warning.ProviderStatusMayCloseTheBar());
    }

    [Fact]
    public void Decide_IgnoresALaterMismatchedResponse()
    {
        Assert.Equal(
            UsageCostApplicationPolicy.Decision.Ignore,
            UsageCostApplicationPolicy.Decide(responseDays: 30, selectedPeriodDays: 7, hasLoaded: true));
    }

    [Fact]
    public void UsagePage_DoesNotRequestFixed30DayCostOrMoveTheSelector()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Pages", "UsagePage.xaml.cs"));
        var start = source.IndexOf("private void RequestRefresh(", StringComparison.Ordinal);
        var end = source.IndexOf("private void OnOpenConnectionClick", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var refresh = source[start..end];
        Assert.DoesNotContain("RequestUsageAsync(", refresh, StringComparison.Ordinal);
        Assert.Contains("RequestUsageCostAsync(_currentPeriodDays)", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncSelectorToServerDays", source, StringComparison.Ordinal);
        var statusStart = source.IndexOf("public void UpdateUsageStatus(", StringComparison.Ordinal);
        var statusEnd = source.IndexOf("private void OnPeriodSelectionChanged", statusStart, StringComparison.Ordinal);
        Assert.True(statusStart >= 0 && statusEnd > statusStart);
        var status = source[statusStart..statusEnd];
        Assert.Contains("_rangeWarning.ProviderStatusMayCloseTheBar()", status, StringComparison.Ordinal);
        var costStart = source.IndexOf("public void UpdateUsageCost(", StringComparison.Ordinal);
        var costEnd = source.IndexOf("private bool ShouldApplyUsageCost(", costStart, StringComparison.Ordinal);
        Assert.True(costStart >= 0 && costEnd > costStart);
        var cost = source[costStart..costEnd];
        Assert.Contains("Decide(cost.Days, _currentPeriodDays, _hasMatchingUsageCost)", cost, StringComparison.Ordinal);
        Assert.DoesNotContain("_dailyCostLoading.HasLoaded", cost, StringComparison.Ordinal);
        Assert.Contains("_hasMatchingUsageCost = false", source, StringComparison.Ordinal);
    }
}
