using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Presentation;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
[Trait("Category", "ProductionResources")]
public sealed class PageHeroSummaryTilesTests(UIThreadFixture ui)
{
    private static FrameworkElement? Find(DependencyObject root, string id)
    {
        var matches = FindDescendants<FrameworkElement>(root)
            .Where(element => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(element) == id).ToArray();
        return matches.Length == 0 ? null : Assert.Single(matches);
    }

    private static global::Windows.Foundation.Rect Bounds(FrameworkElement element, UIElement root) =>
        element.TransformToVisual(root).TransformBounds(
            new global::Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

    [Theory]
    [InlineData(760, 3, 1, 0, 2, 1)]
    [InlineData(759, 2, 2, 1, 0, 2)]
    [InlineData(380, 2, 2, 1, 0, 2)]
    [InlineData(379, 1, 3, 2, 0, 1)]
    public async Task SummaryTiles_UsesExactResponsiveBoundariesAndDisabledButton(
        double width,
        int expectedColumns,
        int expectedRows,
        int expectedLastRow,
        int expectedLastColumn,
        int expectedLastColumnSpan)
    {
        RequireProductionResources();
        await ui.ResetContainerAsync();
        var tiles = new[]
        {
            new PageHeroTile("First", "1", "\uE8A5", "HeroBoundaryTile1", OnInvoked: static () => { }),
            new PageHeroTile("Second", "2", "\uE8A5", "HeroBoundaryTile2", OnInvoked: static () => { }),
            new PageHeroTile("Third", "0", "\uE8A5", "HeroBoundaryTile3", OnInvoked: static () => { }, IsEnabled: false),
        };
        ReactorHostControl? renderer = null;
        Border? surface = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                renderer = new ReactorHostControl { RequestedTheme = ElementTheme.Light };
                renderer.Mount(_ => PageHero.SummaryTiles(
                    "PageHeroBoundaryTiles", "Boundary", tiles, width, reducedMotion: true));
                surface = new Border { Width = width, Height = 360, Child = renderer };
                ui.Container.Children.Add(surface);
                ui.Container.UpdateLayout();
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => surface!.IsLoaded && surface.XamlRoot is not null
                    && Find(surface!, "PageHeroBoundaryTiles") is Grid grid
                    && grid.ActualWidth > 0 && grid.RowDefinitions.Count == expectedRows,
                "mounted summary tiles at the requested width"));
            await ui.RunOnUIAsync(() =>
            {
                var grid = Assert.IsType<Grid>(Find(surface!, "PageHeroBoundaryTiles"));
                Assert.Equal(expectedColumns, grid.ColumnDefinitions.Count);
                Assert.Equal(expectedRows, grid.RowDefinitions.Count);
                var third = Assert.IsType<Button>(Find(surface!, "HeroBoundaryTile3"));
                Assert.Equal(expectedLastRow, Grid.GetRow(third));
                Assert.Equal(expectedLastColumn, Grid.GetColumn(third));
                Assert.Equal(expectedLastColumnSpan, Grid.GetColumnSpan(third));
                if (expectedLastColumnSpan == 2)
                {
                    Assert.Equal(0, third.Margin.Left);
                    Assert.Equal(0, third.Margin.Right);
                }
                Assert.False(third.IsEnabled);
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (surface is not null) ui.Container.Children.Remove(surface);
                renderer?.Dispose();
            });
        }
    }

    [Theory]
    [InlineData(1400, 4)]
    [InlineData(1000, 4)]
    [InlineData(760, 4)]
    [InlineData(759, 2)]
    [InlineData(520, 2)]
    [InlineData(480, 2)]
    public async Task SummaryTiles_FillsTracksAndKeepsIconsClearOfLabels(double width, int expectedColumns)
    {
        RequireProductionResources();
        await ui.ResetContainerAsync();
        var tiles = new[]
        {
            new PageHeroTile("Scheduler", "On", "\uE787", "HeroLayoutScheduler", PageHeroTone.Success, StatusDot: true),
            new PageHeroTile("Running now", "0", "\uE768", "HeroLayoutRunning"),
            new PageHeroTile("Failing", "1", "\uEA39", "HeroLayoutFailing", OnInvoked: static () => { }),
            new PageHeroTile("Paused or off", "2", "\uE769", "HeroLayoutPaused"),
        };
        ReactorHostControl? renderer = null;
        Border? surface = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                renderer = new ReactorHostControl { RequestedTheme = ElementTheme.Light };
                renderer.Mount(_ => PageHero.SummaryTiles(
                    "PageHeroLayoutTiles", "Layout", tiles, width, reducedMotion: true));
                surface = new Border { Width = width, Height = 240, Child = renderer };
                ui.Container.Children.Add(surface);
                ui.Container.UpdateLayout();
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => surface!.IsLoaded && surface.XamlRoot is not null
                    && Find(surface!, "PageHeroLayoutTiles") is Grid grid
                    && grid.ActualWidth > 0 && grid.ColumnDefinitions.Count == expectedColumns
                    && tiles.All(tile => Find(surface!, tile.AutomationId) is { ActualWidth: > 0 }),
                "summary tiles arranged at the requested width"));
            await ui.RunOnUIAsync(() =>
            {
                var grid = Assert.IsType<Grid>(Find(surface!, "PageHeroLayoutTiles"));
                Assert.Equal(expectedColumns, grid.ColumnDefinitions.Count);
                var scale = surface!.XamlRoot!.RasterizationScale;
                Assert.True(scale > 0);
                Assert.InRange(Math.Abs(grid.ActualWidth * scale - width * scale), 0, scale);

                foreach (var tileModel in tiles)
                {
                    var tile = Assert.IsAssignableFrom<FrameworkElement>(Find(surface!, tileModel.AutomationId));
                    var column = Grid.GetColumn(tile);
                    var trackWidth = width / expectedColumns;
                    var expectedLeft = column * trackWidth + (column == 0 ? 0 : 6);
                    var expectedWidth = trackWidth - (column == 0 ? 0 : 6)
                        - (column == expectedColumns - 1 ? 0 : 6);
                    Assert.InRange(Math.Abs(tile.ActualWidth * scale - expectedWidth * scale), 0, scale);
                    var tileBounds = Bounds(tile, grid);
                    Assert.InRange(Math.Abs(tileBounds.Left - expectedLeft), 0, 1);

                    var iconWell = Assert.IsType<Border>(Find(surface!, $"{tileModel.AutomationId}:IconWell"));
                    Assert.InRange(Math.Abs(iconWell.ActualWidth * scale - 36 * scale), 0, scale);
                    Assert.InRange(Math.Abs(iconWell.ActualHeight * scale - 36 * scale), 0, scale);
                    var iconGrid = Assert.IsType<Grid>(VisualTreeHelper.GetParent(iconWell));
                    Assert.InRange(Math.Abs(iconGrid.ActualWidth * scale - 36 * scale), 0, scale);
                    var tileContent = Assert.IsType<Grid>(VisualTreeHelper.GetParent(iconGrid));

                    var iconBounds = Bounds(iconWell, tileContent);
                    Assert.InRange(iconBounds.Left, 0, 1);
                    Assert.InRange(iconBounds.Right, 35, 37);
                    var statusDot = Find(surface!, $"{tileModel.AutomationId}:StatusDot");
                    if (statusDot is not null)
                    {
                        var statusBounds = Bounds(statusDot, iconGrid);
                        Assert.InRange(statusBounds.Left, 9, 11);
                        Assert.InRange(statusBounds.Top, 9, 11);
                        Assert.InRange(statusBounds.Right, 25, 27);
                        Assert.InRange(statusBounds.Bottom, 25, 27);
                    }
                    else
                    {
                        var glyph = Assert.Single(FindDescendants<TextBlock>(iconGrid),
                            text => text.Text == tileModel.Glyph);
                        var glyphBounds = Bounds(glyph, iconGrid);
                        Assert.True(glyphBounds.Left >= -1 && glyphBounds.Right <= 37,
                            $"{tileModel.Label} glyph escapes its icon well: {glyphBounds}");
                    }
                    var label = Assert.Single(FindDescendants<TextBlock>(tile),
                        text => text.Text == tileModel.Label);
                    var labelBounds = Bounds(label, tileContent);
                    Assert.InRange(Math.Abs((labelBounds.Left - iconBounds.Right) * scale - 12 * scale), 0, scale);
                }

                var failing = Assert.IsType<Button>(Find(surface!, "HeroLayoutFailing"));
                var running = Assert.IsType<Border>(Find(surface!, "HeroLayoutRunning"));
                Assert.InRange(Math.Abs(failing.ActualWidth * scale - running.ActualWidth * scale), 0, scale);
                Assert.InRange(Math.Abs(surface.ActualWidth * scale - width * scale), 0, scale);
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (surface is not null) ui.Container.Children.Remove(surface);
                renderer?.Dispose();
            });
        }
    }

    private void RequireProductionResources() =>
        Assert.True(ui.UsesProductionResources,
            $"Run Category=ProductionResources in its own test process with {TestAppResourceLoader.ProfileVariable}=Production.");
}
