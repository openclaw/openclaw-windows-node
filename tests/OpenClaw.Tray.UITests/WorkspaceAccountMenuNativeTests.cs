// Exercises account-menu metadata rendering and native shortcut automation on the hidden WinUI fixture.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using OpenClawTray.Controls;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

/// <summary>Verifies the native account-menu template keeps metadata separate from real shortcuts.</summary>
[Collection(UICollection.Name)]
public sealed class WorkspaceAccountMenuNativeTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData("gateway.example:18789", ElementTheme.Light)]
    [InlineData("2026.11.2", ElementTheme.Light)]
    [InlineData("Connected", ElementTheme.Light)]
    [InlineData("gateway.example:18789", ElementTheme.Dark)]
    [InlineData("2026.11.2", ElementTheme.Dark)]
    [InlineData("Connected", ElementTheme.Dark)]
    public async Task Metadata_RendersWithoutBecomingAnAccessibleShortcut(string metadata, ElementTheme theme)
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")
                ?? throw new InvalidOperationException("OPENCLAW_REPO_ROOT is required for native account-menu tests.");
            var resources = Assert.IsType<ResourceDictionary>(XamlReader.Load(File.ReadAllText(
                Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Themes", "AccountMenuResources.xaml"))));
            Application.Current.Resources.MergedDictionaries.Add(resources);
            try
            {
                var item = new AccountMenuItem
                {
                    Text = "Gateway",
                    Metadata = metadata,
                    Style = AccountMenuItem.ItemStyle,
                    RequestedTheme = theme,
                };
                ui.Container.Children.Add(item);
                await WaitForRenderedConditionAsync(() => item.IsLoaded, "account-menu item Loaded");
                item.ApplyTemplate();
                ui.Container.UpdateLayout();
                await ui.YieldToRenderAsync();

                var text = FindDescendants<TextBlock>(item).Single(block => block.Name == "MetadataTextBlock");
                Assert.Equal(metadata, text.Text);
                Assert.Equal(Visibility.Visible, text.Visibility);
                Assert.True(text.ActualWidth > 0);
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(item);
                Assert.Empty(peer.GetAcceleratorKey());
                Assert.NotNull(peer.GetPattern(PatternInterface.Invoke));

                item.Metadata = null;
                Assert.Equal("", text.Text);
                Assert.Equal(Visibility.Collapsed, text.Visibility);
                item.KeyboardAcceleratorTextOverride = "Ctrl+,";
                Assert.Equal("Ctrl+,", peer.GetAcceleratorKey());
            }
            finally
            {
                ui.Container.Children.Clear();
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }
}
