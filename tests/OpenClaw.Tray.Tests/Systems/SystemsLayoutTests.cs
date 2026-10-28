using OpenClawTray.Systems;

namespace OpenClaw.Tray.Tests.Systems;

public sealed class SystemsLayoutTests
{
    [Fact]
    public void WideWindows_ShowSideBySidePanesWithThreeStatisticColumns()
    {
        var layout = SystemsLayout.For(1600, 900);
        Assert.False(layout.Stacked);
        Assert.Equal(320, layout.InventoryWidth);
        Assert.Equal(3, layout.StatColumns);
        Assert.Equal(5, layout.SummaryColumns);
        Assert.Equal(16, layout.CoresPerRow);
    }

    [Fact]
    public void DefaultHubWidth_KeepsPanesSideBySideWithACompactList()
    {
        // A 1000px hub window with its navigation pane open leaves about 740px for the page.
        var layout = SystemsLayout.For(740, 900);
        Assert.False(layout.Stacked);
        Assert.Equal(260, layout.InventoryWidth);
        Assert.Equal(1, layout.StatColumns);
        Assert.InRange(layout.CoresPerRow, 4, 16);
        Assert.Equal(2, SystemsLayout.For(980, 900).StatColumns);
    }

    [Fact]
    public void NarrowWindows_StackTheListAndReduceStatisticColumns()
    {
        var stacked = SystemsLayout.For(560, 900);
        Assert.True(stacked.Stacked);
        Assert.Equal(2, stacked.StatColumns);
        Assert.Equal(2, stacked.SummaryColumns);
        Assert.Equal(1, SystemsLayout.For(440, 900).StatColumns);
        Assert.Equal(SystemsLayout.For(560, 900), SystemsLayout.For(561, 900));
    }

    [Fact]
    public void ShortWindows_HideTheOverviewAndCapApprovalsLower()
    {
        // The Hub allows a 400px minimum height; the machines must keep most of it.
        var tall = SystemsLayout.For(1600, 1200);
        var medium = SystemsLayout.For(1600, 700);
        var shortPage = SystemsLayout.For(1600, 400);
        Assert.True(tall.ShowOverview);
        Assert.Equal(260, tall.ApprovalsMaxHeight);
        Assert.Equal(160, medium.ApprovalsMaxHeight);
        Assert.False(shortPage.ShowOverview);
        Assert.Equal(72, shortPage.ApprovalsMaxHeight);
    }

    [Fact]
    public void Page_DisposesItsReactorTreeAndControllerWhenLeft()
    {
        // Reactor keeps the tree across Unloaded, so the page must dispose it when navigation leaves.
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Pages", "SystemsPage.xaml.cs"));
        var start = source.IndexOf("protected override void OnNavigatedFrom", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var body = source[start..source.IndexOf("public void Initialize()", start, StringComparison.Ordinal)];
        Assert.Contains("host.Dispose();", body, StringComparison.Ordinal);
        Assert.Contains("_controller.Dispose();", body, StringComparison.Ordinal);
        Assert.Contains("ReactorHost.Child = null;", body, StringComparison.Ordinal);

        // Closing the Hub does not navigate the frame, so the window disposes its page.
        var hub = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml.cs"));
        Assert.Contains("(ContentFrame.Content as IDisposable)?.Dispose();", hub, StringComparison.Ordinal);
    }
}
