using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using OpenClawTray.Presentation;
using Windows.UI.ViewManagement;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class PageHeroMotionTests(UIThreadFixture ui)
{
    private static FrameworkElement? Find(DependencyObject root, string id)
    {
        var matches = FindDescendants<FrameworkElement>(root)
            .Where(element => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(element) == id).ToArray();
        return matches.Length == 0 ? null : Assert.Single(matches);
    }

    [Fact]
    public async Task WithMotion_PutsRepositionOnContainersAndEntranceExitOnPanelChildren()
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? renderer = null;
        Border? surface = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                renderer = new ReactorHostControl();
                renderer.Mount(_ => RenderMotionHosts(reducedMotion: false));
                surface = new Border { Width = 480, Height = 220, Child = renderer };
                ui.Container.Children.Add(surface);
                ui.Container.UpdateLayout();
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => surface!.IsLoaded && surface.XamlRoot is not null
                    && Find(surface!, "PageHeroMotionActions") is StackPanel actions
                    && Find(surface!, "PageHeroMotionTiles") is Grid tiles
                    && actions.ActualWidth > 0 && tiles.ActualWidth > 0,
                "motion hosts mounted"));
            await ui.RunOnUIAsync(() =>
            {
                var enabled = new UISettings().AnimationsEnabled;
                AssertTransitions(Assert.IsType<StackPanel>(Find(surface!, "PageHeroMotionActions")), enabled);
                AssertTransitions(Assert.IsType<Grid>(Find(surface!, "PageHeroMotionTiles")), enabled);
                var actions = Assert.IsType<StackPanel>(Find(surface!, "PageHeroMotionActions"));
                var tiles = Assert.IsType<Grid>(Find(surface!, "PageHeroMotionTiles"));
                AssertPanelChildTransitions(actions, enabled);
                AssertPanelChildTransitions(tiles, enabled);
            });

            await ui.RunOnUIAsync(() => renderer!.Mount(_ => RenderMotionHosts(reducedMotion: true)));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => Find(surface!, "PageHeroMotionActions") is StackPanel { Transitions.Count: 0, ChildrenTransitions.Count: 0 }
                    && Find(surface!, "PageHeroMotionTiles") is Grid { Transitions.Count: 0, ChildrenTransitions.Count: 0 },
                "reduced motion clears position and child transitions"));
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

    private static Element RenderMotionHosts(bool reducedMotion)
    {
        Element actions = HStack(TextBlock("Action")).AutomationId("PageHeroMotionActions");
        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto],
            PageHero.WithMotion(actions, reducedMotion).Grid(row: 0),
            PageHero.WithMotion(Grid([GridSize.Star()], [GridSize.Auto], TextBlock("Tile"))
                .AutomationId("PageHeroMotionTiles"), reducedMotion).Grid(row: 1));
    }

    private static void AssertTransitions(FrameworkElement element, bool enabled)
    {
        Assert.Equal(enabled ? 1 : 0, element.Transitions?.Count ?? 0);
        if (enabled)
            Assert.IsType<RepositionThemeTransition>(element.Transitions![0]);
    }

    private static void AssertPanelChildTransitions(Panel panel, bool enabled)
    {
        Assert.Equal(enabled ? 2 : 0, panel.ChildrenTransitions?.Count ?? 0);
        if (!enabled) return;
        Assert.IsType<EntranceThemeTransition>(panel.ChildrenTransitions![0]);
        Assert.True(((EntranceThemeTransition)panel.ChildrenTransitions[0]).IsStaggeringEnabled);
        Assert.IsType<AddDeleteThemeTransition>(panel.ChildrenTransitions[1]);
    }
}
