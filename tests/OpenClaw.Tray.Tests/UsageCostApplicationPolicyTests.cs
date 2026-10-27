using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class UsageCostApplicationPolicyTests
{
    [Theory]
    [InlineData(30, 2, false)]
    [InlineData(7, 0, false)]
    [InlineData(7, 2, true)]
    public void CompleteRefresh_RejectedSummaryTerminatesLoadingAndPreservesPriorContent(
        int responseDays, int responseRevision, bool accepted)
    {
        var loading = new AsyncListLoadingState();
        loading.Complete(3);
        loading.BeginRefresh();
        var applied = UsageCostApplicationPolicy.CompleteRefresh(
            responseDays, 7, DateTime.UnixEpoch.AddMinutes(responseRevision),
            DateTime.UnixEpoch.AddMinutes(1), 5, loading);
        Assert.Equal(accepted, applied);
        Assert.False(loading.IsRefreshing);
        Assert.True(loading.CanEdit);
        Assert.Equal(accepted ? 5 : 3, loading.ItemCount);
    }

    [Fact]
    public void CompleteRefresh_FirstCorrelatedFallbackStillLoads()
    {
        var loading = new AsyncListLoadingState();
        loading.BeginInitialRefresh();
        Assert.True(UsageCostApplicationPolicy.CompleteRefresh(
            30, 7, DateTime.UnixEpoch, DateTime.MinValue, 2, loading));
        Assert.False(loading.IsRefreshing);
        Assert.True(loading.HasLoaded);
    }

    [Theory]
    [InlineData(1, 1, 7, 7, true, true, 4, 4, true)]
    [InlineData(1, 2, 7, 7, true, true, 4, 4, false)]
    [InlineData(1, 1, 7, 30, true, true, 4, 4, false)]
    [InlineData(1, 1, 7, 7, false, true, 4, 4, false)]
    [InlineData(1, 1, 7, 7, true, false, 4, 4, false)]
    [InlineData(1, 1, 7, 7, true, true, 4, 5, false)]
    public void IsCurrentRefresh_FencesSelectionConnectionAndUnloadedPage(
        long revision, long currentRevision, int days, int selectedDays,
        bool sameClient, bool connected, long epoch, long currentEpoch, bool expected)
    {
        Assert.Equal(expected, UsageCostApplicationPolicy.IsCurrentRefresh(
            revision, currentRevision, days, selectedDays, sameClient, connected, epoch, currentEpoch));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ShouldApply_AcceptsUnspecifiedGatewayPeriod(int responseDays)
    {
        Assert.True(UsageCostApplicationPolicy.ShouldApply(
            responseDays,
            selectedPeriodDays: 7,
            hasLoaded: true));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void ShouldApply_AcceptsInclusiveRangeAroundSelectedPeriod(int responseDays)
    {
        Assert.True(UsageCostApplicationPolicy.ShouldApply(
            responseDays,
            selectedPeriodDays: 7,
            hasLoaded: true));
    }

    [Fact]
    public void ShouldApply_AcceptsFirstMismatchedResponseToAvoidPermanentSpinner()
    {
        Assert.True(UsageCostApplicationPolicy.ShouldApply(
            responseDays: 30,
            selectedPeriodDays: 7,
            hasLoaded: false));
    }

    [Fact]
    public void ShouldApply_RejectsLaterMismatchedResponse()
    {
        Assert.False(UsageCostApplicationPolicy.ShouldApply(
            responseDays: 30,
            selectedPeriodDays: 7,
            hasLoaded: true));
    }
}
