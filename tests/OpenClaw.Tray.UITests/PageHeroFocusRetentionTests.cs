using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Presentation;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

/// <summary>Candidate regression for native action identity across the hero layout breakpoint.</summary>
[Collection(UICollection.Name)]
[Trait("Category", "ProductionResources")]
public sealed class PageHeroFocusRetentionTests(UIThreadFixture ui)
{
    private static FrameworkElement? Find(DependencyObject root, string id)
    {
        var matches = FindDescendants<FrameworkElement>(root)
            .Where(element => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(element) == id).ToArray();
        return matches.Length == 0 ? null : Assert.Single(matches);
    }

    [Fact]
    public async Task Header_PreservesActionAndParentIdentityAndFocusAcrossBothDirections()
    {
        Assert.True(ui.UsesProductionResources, "Requires OPENCLAW_UI_TEST_RESOURCE_PROFILE=Production.");
        await ui.ResetContainerAsync();
        ReactorHostControl? renderer = null;
        Border? surface = null;
        Button? originalAction = null;
        DependencyObject? originalParent = null;
        Grid? originalContentGrid = null;
        var narrow = false;
        Microsoft.UI.Reactor.Core.Element Render() => PageHero.Header(
            "FocusHero", "\uE713", "Automations", "FocusTitle",
            TextBlock("All jobs"), null,
            Button("Add", () => { }).AutomationId("FocusAdd"),
            narrow, reducedMotion: false);
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                renderer = new ReactorHostControl();
                renderer.Mount(_ => Render());
                surface = new Border { Width = 800, Child = renderer };
                ui.Container.Children.Add(surface);
                ui.Container.UpdateLayout();
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => Find(surface!, "FocusAdd") is Button { ActualWidth: > 0 },
                "hero action mounted"));
            await ui.RunOnUIAsync(() =>
            {
                originalAction = Assert.IsType<Button>(Find(surface!, "FocusAdd"));
                originalParent = VisualTreeHelper.GetParent(originalAction);
                originalContentGrid = Assert.IsType<Grid>(Find(surface!, "FocusHeroGrid"));
                Assert.Same(originalContentGrid, VisualTreeHelper.GetParent(originalParent));
                Assert.True(originalAction.Focus(FocusState.Programmatic));
                Assert.Same(originalAction, FocusManager.GetFocusedElement(surface!.XamlRoot));
            });
            foreach (var width in new[] { 560, 800 })
            {
                await ui.RunOnUIAsync(() =>
                {
                    narrow = width == 560;
                    surface!.Width = width;
                    renderer!.Mount(_ => Render());
                    ui.Container.UpdateLayout();
                });
                // Mount queues reconciliation. Width can already match before the retained
                // action host receives its new attached properties; wait for that actual boundary.
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                    () => Find(surface!, "FocusHero") is Border { ActualWidth: > 0 } hero &&
                        Math.Abs(hero.ActualWidth - width) < 1 &&
                        Find(surface!, "FocusAdd") is Button { ActualWidth: > 0 } &&
                        originalParent is Grid actionHost &&
                        Microsoft.UI.Xaml.Controls.Grid.GetRow(actionHost) == (narrow ? 1 : 0) &&
                        Microsoft.UI.Xaml.Controls.Grid.GetColumn(actionHost) == (narrow ? 1 : 2) &&
                        Microsoft.UI.Xaml.Controls.Grid.GetColumnSpan(actionHost) == (narrow ? 2 : 1),
                    "responsive action placement committed on the retained host",
                    () => $"Width={surface!.ActualWidth}; narrow={narrow}; " +
                        $"row={Microsoft.UI.Xaml.Controls.Grid.GetRow((Grid)originalParent!)}; " +
                        $"column={Microsoft.UI.Xaml.Controls.Grid.GetColumn((Grid)originalParent!)}; " +
                        $"span={Microsoft.UI.Xaml.Controls.Grid.GetColumnSpan((Grid)originalParent!)}"));
                await ui.YieldToRenderAsync();
                await ui.RunOnUIAsync(() =>
                {
                    Assert.Same(originalAction, Find(surface!, "FocusAdd"));
                    Assert.Same(originalParent, VisualTreeHelper.GetParent(originalAction!));
                    var host = Assert.IsType<Grid>(originalParent);
                    Assert.Same(originalContentGrid, Find(surface!, "FocusHeroGrid"));
                    Assert.Same(originalContentGrid, VisualTreeHelper.GetParent(host));
                    Assert.Equal(narrow ? 1 : 0, Microsoft.UI.Xaml.Controls.Grid.GetRow(host));
                    Assert.Equal(narrow ? 1 : 2, Microsoft.UI.Xaml.Controls.Grid.GetColumn(host));
                    Assert.Equal(narrow ? 2 : 1, Microsoft.UI.Xaml.Controls.Grid.GetColumnSpan(host));
                    var glyph = Assert.IsType<Border>(Find(surface!, "FocusHero:Glyph"));
                    var title = Assert.IsType<TextBlock>(Find(surface!, "FocusTitle"));
                    var copy = Assert.IsType<StackPanel>(VisualTreeHelper.GetParent(title));
                    Assert.Same(originalContentGrid, VisualTreeHelper.GetParent(copy));
                    Assert.Equal(narrow ? 2 : 1, Microsoft.UI.Xaml.Controls.Grid.GetColumnSpan(copy));
                    var glyphBounds = glyph.TransformToVisual(originalContentGrid).TransformPoint(new Windows.Foundation.Point());
                    var copyBounds = copy.TransformToVisual(originalContentGrid).TransformPoint(new Windows.Foundation.Point());
                    Assert.InRange(copyBounds.X - glyphBounds.X - glyph.ActualWidth, 15.5, 16.5);
                    var actionBounds = host.TransformToVisual(originalContentGrid).TransformPoint(new Windows.Foundation.Point());
                    if (narrow)
                    {
                        Assert.InRange(actionBounds.X - copyBounds.X, -0.5, 0.5);
                        Assert.InRange(actionBounds.Y - copyBounds.Y - copy.ActualHeight, 11.5, 12.5);
                    }
                    Assert.Same(originalAction, FocusManager.GetFocusedElement(surface!.XamlRoot));
                    Assert.Equal(1, originalAction!.Opacity);
                });
            }
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
}
