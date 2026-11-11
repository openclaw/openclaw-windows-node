using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Controls;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.UITests;

// Exercises the native shell applicator with a page-owned viewport, without app services/navigation.
[Collection(UICollection.Name)]
public sealed class SettingsPageInsetTests(UIThreadFixture ui)
{
    [Fact]
    public async Task Shell_DeliversFullAndEmbeddedInsetsWithoutMovingBackdropOrReplacingContent()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(() =>
        {
            using var resources = new ChatThemeProofScope("Light");
            var shell = new SettingsShellView();
            var frame = Assert.IsType<Frame>(shell.FindName("ContentFrame"));
            var navigation = Assert.IsType<NavigationView>(shell.FindName("NavView"));
            var page = new InsetPage();
            // Frame.Content avoids app service activation; shell allocation remains production code.
            frame.Content = page;
            ui.Container.Children.Add(shell);
            navigation.IsPaneOpen = true;
            shell.SetHostContentTopInset(0);
            ui.Container.UpdateLayout();
            Assert.Equal(0, page.Viewport.Margin.Top);
            navigation.IsPaneOpen = false;
            Assert.Equal(56, page.Viewport.Margin.Top);
            navigation.IsPaneOpen = true;
            Assert.Equal(0, page.Viewport.Margin.Top);
            shell.ContentOnly = true;
            shell.SetHostContentTopInset(56);
            Assert.Equal(56, page.Viewport.Margin.Top);
            shell.SetHostContentTopInset(0);
            Assert.Equal(0, page.Viewport.Margin.Top);
            Assert.Same(page, frame.Content);
            Assert.Equal(new Thickness(0), page.Margin);
            Assert.Equal(new Thickness(0), frame.Margin);

            var ordinary = new Border { Margin = new Thickness(12, 24, 12, 24) };
            frame.Content = ordinary;
            shell.SetHostContentTopInset(56);
            Assert.Equal(new Thickness(12, 24, 12, 24), ordinary.Margin);
            Assert.Equal(new Thickness(0), frame.Margin);
            ui.Container.Children.Remove(shell);
        });
    }

    private sealed class InsetPage : Grid, ISettingsPageContentInset
    {
        internal Border Viewport { get; } = new() { Child = new Button { Content = "Page action" } };
        public InsetPage() => Children.Add(Viewport);
        public void ApplyTopInset(double topInset) => Viewport.Margin = new Thickness(0, topInset, 0, 0);
    }
}
