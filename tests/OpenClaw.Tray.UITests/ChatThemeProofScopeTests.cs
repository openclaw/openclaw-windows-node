using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class ChatThemeProofScopeTests(UIThreadFixture ui)
{
    [Fact]
    public async Task LightScopeAfterHighContrastRendersLightReactorThemeBackground()
    {
        await ui.ResetContainerAsync();
        Assert.NotEqual(0, await RenderedCanvasAlphaAsync("HighContrast"));
        Assert.Equal(0, await RenderedCanvasAlphaAsync("Light"));
    }

    private async Task<byte> RenderedCanvasAlphaAsync(string themeName)
    {
        ChatThemeProofScope? theme = null;
        ReactorHostControl? host = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                theme = new ChatThemeProofScope(themeName);
                host = new ReactorHostControl { Width = 40, Height = 40, RequestedTheme = theme.ElementTheme };
                host.Mount(_ => Border(null).Background(Theme.Ref("ChatCanvasBrush")).AutomationId("ThemeProbe"));
                ui.Container.Children.Add(host);
            });
            byte alpha = 0;
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
            {
                if (FindDescendants<Border>(host!).FirstOrDefault(border =>
                        Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(border) == "ThemeProbe")
                    is not { Background: SolidColorBrush brush })
                    return false;
                alpha = brush.Color.A;
                return true;
            }, "rendered theme probe"));
            return alpha;
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                host?.Dispose();
                if (host is not null)
                    ui.Container.Children.Remove(host);
                theme?.Dispose();
            });
        }
    }
    [Fact]
    public async Task RepeatedScopesReuseStableProductionResourceDictionaries()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(() =>
        {
            var original = Application.Current.Resources;
            using (var light = new ChatThemeProofScope("Light"))
            {
                var standard = Application.Current.Resources;
                Assert.Equal(ElementTheme.Light, light.ElementTheme);

                using (var dark = new ChatThemeProofScope("Dark"))
                {
                    Assert.Same(standard, Application.Current.Resources);
                    Assert.Equal(ElementTheme.Dark, dark.ElementTheme);
                }
                Assert.Same(standard, Application.Current.Resources);

                using (var highContrast = new ChatThemeProofScope("HighContrast"))
                {
                    var contrast = Application.Current.Resources;
                    Assert.NotSame(standard, contrast);

                    using var repeatedHighContrast = new ChatThemeProofScope("HighContrast");
                    Assert.Same(contrast, Application.Current.Resources);
                }
                Assert.Same(standard, Application.Current.Resources);
            }
            Assert.Same(original, Application.Current.Resources);
        });
    }
}
