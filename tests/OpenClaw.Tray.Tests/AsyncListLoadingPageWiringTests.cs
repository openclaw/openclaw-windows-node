using System.IO;

namespace OpenClaw.Tray.Tests;

public sealed class AsyncListLoadingPageWiringTests
{
    [Theory]
    [InlineData("SessionsPage.xaml", "Loading sessions")]
    [InlineData("CronPage.xaml", "Loading cron jobs")]
    [InlineData("UsagePage.xaml", "Loading daily costs")]
    [InlineData("BindingsPage.xaml", "Loading bindings")]
    public void BigListPages_HaveFirstLoadPlaceholders(string fileName, string loadingText)
    {
        var source = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", fileName);

        Assert.Contains("<ProgressRing", source);
        Assert.Contains(loadingText, source);
    }

    [Theory]
    [InlineData("SessionsPage.xaml.cs")]
    [InlineData("CronPage.xaml.cs")]
    [InlineData("UsagePage.xaml.cs")]
    [InlineData("BindingsPage.xaml.cs")]
    public void BigListPages_DisableListInteractionsDuringRefresh(string fileName)
    {
        var source = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", fileName);

        Assert.Contains("AsyncListLoadingState", source);
        Assert.Contains(".BeginRefresh()", source);
        Assert.Contains(".CanEdit", source);
    }

    [Theory]
    [InlineData("SessionsPage.xaml.cs")]
    [InlineData("CronPage.xaml.cs")]
    [InlineData("UsagePage.xaml.cs")]
    [InlineData("BindingsPage.xaml.cs")]
    public void BigListPages_SurfaceDisconnectedState(string fileName)
    {
        var source = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", fileName);

        Assert.Contains("ShowDisconnected", source);
        Assert.Contains("Navigate(\"connection\")", source);
        Assert.Contains(".Fail()", source);
    }

    [Fact]
    public void UsagePage_GuardsStalePeriodResponses()
    {
        var source = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "UsagePage.xaml.cs");

        Assert.Contains("ShouldApplyUsageCost", source);
        Assert.Contains("Math.Abs(cost.Days - _currentPeriodDays) <= 1", source);
        Assert.Contains("return !_dailyCostLoading.HasLoaded", source);
        Assert.Contains("cost.UpdatedAt < _lastAppliedUsageCostUpdatedAtUtc", source);
        Assert.Contains("_dailyCostLoading.BeginInitialRefresh()", source);
    }

    [Fact]
    public void CronPage_DefaultsToLoadingUntilFirstListResponse()
    {
        var xaml = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "CronPage.xaml");
        var source = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "CronPage.xaml.cs");

        Assert.Contains("x:Name=\"LoadingState\" Grid.Row=\"4\"", xaml);
        Assert.Contains("Visibility=\"Visible\"", xaml);
        Assert.Contains("x:Name=\"EmptyState\" Grid.Row=\"4\"", xaml);
        Assert.Contains("Visibility=\"Collapsed\"", xaml);
        Assert.Contains("_cronLoading.Complete(jobs.Count)", source);
    }

    private static string ReadSource(params string[] relativePathParts)
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(relativePathParts).ToArray()));
    }
}
