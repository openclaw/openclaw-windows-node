using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenClawTray.Presentation;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class PageHeroIconTests(UIThreadFixture ui)
{
    private static FrameworkElement? Find(DependencyObject root, string id) =>
        FindDescendants<FrameworkElement>(root)
            .SingleOrDefault(element => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(element) == id);

    private async Task<(ReactorHostControl Renderer, Border Surface)> MountAsync(string? pageTag, bool highContrast)
    {
        ReactorHostControl? renderer = null;
        Border? surface = null;
        await ui.RunOnUIAsync(() =>
        {
            renderer = new ReactorHostControl();
            renderer.Mount(_ => PageHero.Header(
                "IconHero", "\uE713", "Automations", "IconHeroTitle",
                TextBlock("All jobs"), null, Empty(), narrow: false, reducedMotion: true,
                highContrast: highContrast, glyphAutomationId: "CronPageMarker", pageTag: pageTag));
            surface = new Border { Width = 800, Child = renderer };
            ui.Container.Children.Add(surface);
            ui.Container.UpdateLayout();
        });
        return (renderer!, surface!);
    }

    [Theory]
    [InlineData("cron", "ms-appx:///Assets/SidebarIcons/Cron.svg")]
    [InlineData("instances", "ms-appx:///Assets/SidebarIcons/Instances.svg")]
    [InlineData("usage", "ms-appx:///Assets/SidebarIcons/Usage.svg")]
    public async Task KnownPageTag_RendersDecorativeSidebarSvgWithStableHost(string tag, string expectedUri)
    {
        await ui.ResetContainerAsync();
        var (renderer, surface) = await MountAsync(tag, highContrast: false);
        try
        {
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => Find(surface, "CronPageMarker") is Border,
                "page hero SVG host mounted"));
            await ui.RunOnUIAsync(() =>
            {
                Assert.False(Assert.IsType<TextBlock>(Find(surface, "IconHeroTitle")).IsTabStop);
                var host = Assert.IsType<Border>(Find(surface, "CronPageMarker"));
                var image = Assert.IsType<Image>(host.Child);
                var svg = Assert.IsType<SvgImageSource>(image.Source);
                Assert.Equal(expectedUri, svg.UriSource.AbsoluteUri);
                Assert.Null(host.Background);
                Assert.Equal(48, host.Width);
                Assert.Equal(48, host.Height);
                Assert.Equal(48, image.Width);
                Assert.Equal(48, image.Height);
                Assert.Equal(AccessibilityView.Raw,
                    Microsoft.UI.Xaml.Automation.AutomationProperties.GetAccessibilityView(image));
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                ui.Container.Children.Remove(surface);
                renderer.Dispose();
            });
        }
    }

    [Theory]
    [InlineData("cron", true)]
    [InlineData("unknown-page", false)]
    public async Task HighContrastOrUnknownPageTag_KeepsGlyphTileAndAutomationId(string? tag, bool highContrast)
    {
        await ui.ResetContainerAsync();
        var (renderer, surface) = await MountAsync(tag, highContrast);
        try
        {
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => Find(surface, "CronPageMarker") is Border,
                "page hero glyph host mounted"));
            await ui.RunOnUIAsync(() =>
            {
                var host = Assert.IsType<Border>(Find(surface, "CronPageMarker"));
                Assert.NotNull(host.Background);
                Assert.NotNull(host.Child);
                Assert.IsNotType<Image>(host.Child);
                Assert.Empty(FindDescendants<Image>(host));
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                ui.Container.Children.Remove(surface);
                renderer.Dispose();
            });
        }
    }
}
