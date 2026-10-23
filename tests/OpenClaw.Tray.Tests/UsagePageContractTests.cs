namespace OpenClaw.Tray.Tests;

public sealed class UsagePageContractTests
{
    [Fact]
    public void PageRefresh_UsesOneResponseBoundSelectedPeriodAndNotUncorrelatedPolling()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Pages", "UsagePage.xaml.cs"));

        Assert.Contains("await client.RefreshUsageAsync(days)", source);
        Assert.DoesNotContain("client.RequestUsageAsync(", source);
        Assert.DoesNotContain("client.RequestUsageCostAsync(", source);
        Assert.DoesNotContain("client.RequestUsageStatusAsync(", source);
        Assert.DoesNotContain("case nameof(AppState.UsageCost)", source);
        Assert.DoesNotContain("case nameof(AppState.UsageStatus)", source);
        Assert.Contains("UsageCostApplicationPolicy.IsCurrentRefresh(", source);
    }
}
