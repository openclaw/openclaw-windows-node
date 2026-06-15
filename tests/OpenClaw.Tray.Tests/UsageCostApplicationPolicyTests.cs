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
