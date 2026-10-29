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
    public void Decide_IgnoresALaterMismatchedResponse()
    {
        Assert.Equal(
            UsageCostApplicationPolicy.Decision.Ignore,
            UsageCostApplicationPolicy.Decide(responseDays: 30, selectedPeriodDays: 7, hasLoaded: true));
    }

    [Fact]
    public void Decide_AppliesTheGatewayRangeAfterTheUserSelectsIt()
    {
        Assert.Equal(
            UsageCostApplicationPolicy.Decision.StopForUnsupportedRange,
            UsageCostApplicationPolicy.Decide(responseDays: 30, selectedPeriodDays: 7, hasLoaded: false));
        Assert.Equal(
            UsageCostApplicationPolicy.Decision.Apply,
            UsageCostApplicationPolicy.Decide(responseDays: 30, selectedPeriodDays: 30, hasLoaded: false));
    }

    [Fact]
    public void Decide_AppliesOlderMatchingRangeAfterNewerUnsupportedRange()
    {
        var lastApplied = DateTime.MinValue;

        Assert.Equal(
            UsageCostApplicationPolicy.Decision.StopForUnsupportedRange,
            UsageCostApplicationPolicy.Decide(
                responseDays: 30,
                selectedPeriodDays: 7,
                hasLoaded: false,
                updatedAt: new DateTime(2026, 1, 21),
                lastAppliedUpdatedAt: lastApplied));
        Assert.Equal(
            UsageCostApplicationPolicy.Decision.Apply,
            UsageCostApplicationPolicy.Decide(
                responseDays: 7,
                selectedPeriodDays: 7,
                hasLoaded: false,
                updatedAt: new DateTime(2026, 1, 20),
                lastAppliedUpdatedAt: lastApplied));
    }

    [Fact]
    public void Decide_CompletesRefreshForOlderMismatchedRangeAfterMatchingCache()
    {
        Assert.Equal(
            UsageCostApplicationPolicy.Decision.Ignore,
            UsageCostApplicationPolicy.Decide(
                responseDays: 30,
                selectedPeriodDays: 7,
                hasLoaded: true,
                updatedAt: new DateTime(2026, 1, 19),
                lastAppliedUpdatedAt: new DateTime(2026, 1, 20)));
    }

    [Fact]
    public void Decide_IgnoresOlderMatchingRange()
    {
        Assert.Equal(
            UsageCostApplicationPolicy.Decision.IgnoreStale,
            UsageCostApplicationPolicy.Decide(
                responseDays: 7,
                selectedPeriodDays: 7,
                hasLoaded: true,
                updatedAt: new DateTime(2026, 1, 19),
                lastAppliedUpdatedAt: new DateTime(2026, 1, 20)));
    }

    [Fact]
    public void GatewayClientSwap_ClearsGatewayCacheAndUsagePageSnapshot()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var gatewayService = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Services", "GatewayService.cs"));
        var attachStart = gatewayService.IndexOf("public void AttachClient(", StringComparison.Ordinal);
        var attachEnd = gatewayService.IndexOf("// ── Dispatcher helper", attachStart, StringComparison.Ordinal);
        Assert.True(attachStart >= 0 && attachEnd > attachStart);
        var attach = gatewayService[attachStart..attachEnd];
        Assert.Contains("if (!ReferenceEquals(oldClient, newClient))", attach, StringComparison.Ordinal);
        Assert.Contains("_state.ClearCachedData();", attach, StringComparison.Ordinal);

        var usagePage = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Pages", "UsagePage.xaml.cs"));
        var changedStart = usagePage.IndexOf("private void OnOperatorClientChanged(", StringComparison.Ordinal);
        var changedEnd = usagePage.IndexOf("private void ResetUsageCostState(", changedStart, StringComparison.Ordinal);
        Assert.True(changedStart >= 0 && changedEnd > changedStart);
        var changed = usagePage[changedStart..changedEnd];
        Assert.Contains("if (!ReferenceEquals(e.OldClient, e.NewClient))", changed, StringComparison.Ordinal);
        Assert.Contains("ResetUsageCostState();", changed, StringComparison.Ordinal);
        Assert.Contains("else ResetUsageCostState();", usagePage, StringComparison.Ordinal);
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
        Assert.Contains("_appState?.UsageCost is { } latestCost", source, StringComparison.Ordinal);
        Assert.Contains("ShouldApplyUsageCost(latestCost)", source, StringComparison.Ordinal);
        Assert.Contains("_appState?.GetUsageCostForPeriod(_currentPeriodDays)", source, StringComparison.Ordinal);
        Assert.Contains("_appState?.GetUsageCostForPeriod(days)", source, StringComparison.Ordinal);
        var statusStart = source.IndexOf("public void UpdateUsageStatus(", StringComparison.Ordinal);
        var statusEnd = source.IndexOf("private void OnPeriodSelectionChanged", statusStart, StringComparison.Ordinal);
        Assert.True(statusStart >= 0 && statusEnd > statusStart);
        var status = source[statusStart..statusEnd];
        Assert.Contains("if (!_isUnsupportedRangeWarningVisible)", status, StringComparison.Ordinal);
        var costStart = source.IndexOf("public void UpdateUsageCost(", StringComparison.Ordinal);
        var costEnd = source.IndexOf("public void UpdateUsageStatus(", costStart, StringComparison.Ordinal);
        Assert.True(costStart >= 0 && costEnd > costStart);
        var cost = source[costStart..costEnd];
        Assert.Contains("cost.UpdatedAt", cost, StringComparison.Ordinal);
        Assert.Contains("_lastAppliedUsageCostUpdatedAtUtc", cost, StringComparison.Ordinal);
        Assert.Contains("_isUnsupportedRangeWarningVisible = true", cost, StringComparison.Ordinal);
        Assert.Contains("OpenConnectionButton.Visibility = Visibility.Collapsed", cost, StringComparison.Ordinal);
        Assert.Contains("_dailyCostLoading.Complete(_dailyCostLoading.ItemCount)", cost, StringComparison.Ordinal);
        Assert.Contains("Decision.IgnoreStale", cost, StringComparison.Ordinal);
        Assert.Contains("_hasMatchingUsageCost = false", source, StringComparison.Ordinal);
        Assert.Contains(
            "bool showEmpty = !_isUnsupportedRangeWarningVisible",
            source,
            StringComparison.Ordinal);
        var unsupportedStart = source.IndexOf("private void StopForUnsupportedRange(", StringComparison.Ordinal);
        var unsupportedEnd = source.IndexOf("public void UpdateUsageStatus(", unsupportedStart, StringComparison.Ordinal);
        Assert.True(unsupportedStart >= 0 && unsupportedEnd > unsupportedStart);
        Assert.DoesNotContain(
            "_lastAppliedUsageCostUpdatedAtUtc",
            source[unsupportedStart..unsupportedEnd],
            StringComparison.Ordinal);
        Assert.Contains("OpenConnectionButton.Visibility = Visibility.Visible", source, StringComparison.Ordinal);
        var recoveryStart = source.IndexOf("private void RecoverConnectedClient(", StringComparison.Ordinal);
        var recoveryEnd = source.IndexOf("private void RequestRefresh(", recoveryStart, StringComparison.Ordinal);
        Assert.True(recoveryStart >= 0 && recoveryEnd > recoveryStart);
        Assert.DoesNotContain(
            "_hasMatchingUsageCost = false",
            source[recoveryStart..recoveryEnd],
            StringComparison.Ordinal);
    }
}
