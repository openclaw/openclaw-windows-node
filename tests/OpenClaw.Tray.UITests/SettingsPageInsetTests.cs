using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Controls;
using OpenClawTray.Pages;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.UITests;

// Exercises the native shell applicator with a page-owned viewport, without app services/navigation.
[Collection(UICollection.Name)]
public sealed class SettingsPageInsetTests(UIThreadFixture ui)
{
    [Fact]
    public async Task DesktopLobster_HasNamedDisclosureAndIndependentToggleWithNoneditableSizePicker()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(() =>
        {
            using var resources = new ChatThemeProofScope("Light");
            var page = new SettingsPage();
            ui.Container.Children.Add(page);
            try
            {
                ui.Container.UpdateLayout();
                var card = Assert.IsType<SettingsExpander>(page.FindName("DesktopCompanionExpander"));
                var disclosure = Assert.Single(Descendants(card).OfType<Expander>());
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(disclosure);
                Assert.Equal("Desktop lobster", peer.GetName());
                var expansion = Assert.IsAssignableFrom<IExpandCollapseProvider>(
                    peer.GetPattern(PatternInterface.ExpandCollapse));
                Assert.False(card.IsExpanded);
                expansion.Expand();
                Assert.True(card.IsExpanded);

                var toggle = Assert.IsType<ToggleSwitch>(card.Content);
                var togglePeer = FrameworkElementAutomationPeer.CreatePeerForElement(toggle);
                var toggling = Assert.IsAssignableFrom<IToggleProvider>(
                    togglePeer.GetPattern(PatternInterface.Toggle));
                toggling.Toggle();
                Assert.True(toggle.IsOn);
                Assert.True(card.IsExpanded);
                expansion.Collapse();
                Assert.False(card.IsExpanded);
                Assert.True(toggle.IsOn);

                var sizeCard = Assert.IsType<SettingsCard>(Assert.Single(card.Items));
                Assert.False(Assert.IsType<ComboBox>(sizeCard.Content).IsEditable);
            }
            finally
            {
                ui.Container.Children.Remove(page);
            }
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

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
            var notifications = Assert.IsType<Grid>(shell.FindName("AppNotificationOverlay"));
            var banner = Assert.IsType<InfoBar>(shell.FindName("AppNotificationInfoBar"));
            banner.IsOpen = true;
            banner.Visibility = Visibility.Visible;
            banner.Title = "Session action failed";
            var page = new InsetPage();
            // Frame.Content avoids app service activation; shell allocation remains production code.
            frame.Content = page;
            ui.Container.Children.Add(shell);
            navigation.IsPaneOpen = true;
            shell.SetHostContentTopInset(0);
            ui.Container.UpdateLayout();
            Assert.Equal(56, page.Viewport.Margin.Top);
            Assert.Equal(56, notifications.Margin.Top);
            Assert.Equal(64, banner.TransformToVisual(shell).TransformPoint(new()).Y);
            Assert.Equal(new Thickness(12, 8, 12, 0), banner.Margin);
            navigation.IsPaneOpen = false;
            Assert.Equal(56, page.Viewport.Margin.Top);
            Assert.Equal(56, notifications.Margin.Top);
            navigation.IsPaneOpen = true;
            Assert.Equal(56, page.Viewport.Margin.Top);
            Assert.Equal(56, notifications.Margin.Top);
            shell.ContentOnly = true;
            shell.SetHostContentTopInset(56);
            Assert.Equal(56, page.Viewport.Margin.Top);
            Assert.Equal(56, notifications.Margin.Top);
            shell.SetHostContentTopInset(0);
            Assert.Equal(0, page.Viewport.Margin.Top);
            Assert.Equal(0, notifications.Margin.Top);
            Assert.Same(page, frame.Content);
            Assert.Equal(new Thickness(0), page.Margin);
            Assert.Equal(new Thickness(0), frame.Margin);

            var ordinary = new Border { Margin = new Thickness(12, 24, 12, 24) };
            frame.Content = ordinary;
            shell.SetHostContentTopInset(56);
            Assert.Equal(56, notifications.Margin.Top);
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
