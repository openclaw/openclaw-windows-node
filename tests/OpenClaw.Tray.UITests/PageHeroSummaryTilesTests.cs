using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private void RequireProductionResources() =>
        Assert.True(ui.UsesProductionResources,
            $"Run Category=ProductionResources in its own test process with {TestAppResourceLoader.ProfileVariable}=Production.");
}
