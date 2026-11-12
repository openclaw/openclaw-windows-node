using OpenClawTray.Systems;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Systems;

public sealed class SystemsLayoutTests
{
    [Theory]
    [InlineData(1400, PageWidthTier.Wide, 1200)]
    [InlineData(1000, PageWidthTier.Wide, 936)]
    [InlineData(999.99994, PageWidthTier.Wide, 936)]
    [InlineData(760, PageWidthTier.Medium, 696)]
    [InlineData(520, PageWidthTier.Narrow, 480)]
    public void CanonicalHelper_UsesHostTierCappedContentAndEqualTiles(double width, PageWidthTier tier, double content)
    {
        Assert.Equal(tier, PageResponsiveLayout.TierFor(width));
        Assert.Equal(content, PageResponsiveLayout.ContentWidth(width));
        var count = PageResponsiveLayout.TileColumns(content, SystemsLayout.StatTileMinWidth);
        Assert.True(PageResponsiveLayout.TileWidth(content, count) >= SystemsLayout.StatTileMinWidth);
        Assert.True(PageResponsiveLayout.TileWidth(content, count + 1) < SystemsLayout.StatTileMinWidth);
    }

    [Theory]
    [InlineData(1400, false, false, 4)]
    [InlineData(1000, false, false, 3)]
    [InlineData(999.99994, false, false, 3)]
    [InlineData(999, true, false, 5)]
    [InlineData(760, true, false, 3)]
    [InlineData(720, true, false, 3)]
    [InlineData(719, true, true, 3)]
    [InlineData(520, true, true, 2)]
    [InlineData(480, true, true, 2)]
    public void HostWidth_SelectsSharedTiersAndTilesWithoutViolatingMinimums(
        double width, bool stacked, bool compact, int stats)
    {
        var layout = SystemsLayout.For(width, 900);
        Assert.Equal(stacked, layout.Stacked);
        Assert.Equal(compact, layout.CompactHeader);
        Assert.Equal(PageResponsiveLayout.ContentWidth(width), layout.ContentWidth);
        Assert.Equal(width, layout.HostWidth);
        Assert.Equal(stats, layout.StatColumns);
    }

    [Fact]
    public void WideDetails_KeepAFixedCapabilitiesColumnAndTheDesignGap()
    {
        Assert.Equal(340, SystemsLayout.CapabilitiesWidth);
        Assert.Equal(32, SystemsLayout.DetailsColumnGap);

        // Statistic tiles fit what is left beside the fixed Capabilities column, never the old half split.
        foreach (var width in new[] { 1000d, 1100d, 1280d, 1400d, 1920d })
        {
            var layout = SystemsLayout.For(width, 900);
            Assert.False(layout.Stacked);
            var rightColumn = PageResponsiveLayout.ContentWidth(width) - 34
                - SystemsLayout.CapabilitiesWidth - SystemsLayout.DetailsColumnGap;
            Assert.True(PageResponsiveLayout.TileWidth(rightColumn, layout.StatColumns) >= SystemsLayout.StatTileMinWidth);
            Assert.True(PageResponsiveLayout.TileWidth(rightColumn, layout.StatColumns + 1) < SystemsLayout.StatTileMinWidth);
        }
    }

    [Fact]
    public void NarrowWindows_StackTheListAndReduceStatisticColumns()
    {
        Assert.True(SystemsLayout.Default.Stacked);
        Assert.Equal(1, SystemsLayout.Default.StatColumns);
        var stacked = SystemsLayout.For(560, 900);
        Assert.True(stacked.Stacked);
        Assert.Equal(2, stacked.StatColumns);
        Assert.Equal(2, SystemsLayout.For(440, 900).StatColumns);
        Assert.Equal(SystemsLayout.For(560, 900).StatColumns, SystemsLayout.For(561, 900).StatColumns);
        Assert.Equal(520, SystemsLayout.For(560, 900).ContentWidth);
        Assert.Equal(521, SystemsLayout.For(561, 900).ContentWidth);
    }

    [Fact]
    public void ShortWindows_KeepTheSingleScrollPageLayout()
    {
        // The Hub allows a 400px minimum height; the machines must keep most of it.
        Assert.Equal(SystemsLayout.For(1600, 1200), SystemsLayout.For(1600, 400));
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

        // Hiding or closing Settings clears the frame without raising OnNavigatedFrom.
        var shell = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Controls", "SettingsShellView.xaml.cs"));
        var dispose = shell.IndexOf("internal void Dispose()", StringComparison.Ordinal);
        Assert.True(dispose >= 0);
        Assert.Contains("Deactivate();",
            shell[dispose..shell.IndexOf("private void OnAppNotificationChanged", dispose, StringComparison.Ordinal)],
            StringComparison.Ordinal);
        var deactivate = shell.IndexOf("internal void Deactivate()", StringComparison.Ordinal);
        Assert.True(deactivate >= 0);
        var clearFrame = shell.IndexOf("ContentFrame.Content = null;", deactivate, StringComparison.Ordinal);
        Assert.True(clearFrame > deactivate);
        Assert.Contains("(ContentFrame.Content as SystemsPage)?.Dispose();", shell[deactivate..clearFrame], StringComparison.Ordinal);

        var workspace = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("Closed += OnClosed;", workspace, StringComparison.Ordinal);
        var closed = workspace.IndexOf("private void OnClosed(", StringComparison.Ordinal);
        Assert.True(closed >= 0);
        Assert.Contains("_preparedSettings?.Dispose();", workspace[closed..], StringComparison.Ordinal);
        Assert.Contains("settings.Dispose();", workspace[closed..], StringComparison.Ordinal);
    }
}
