using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Pages;

namespace OpenClaw.Tray.UITests;

/// <summary>Exercises the real Config page's pending detail cancellation without connecting a Gateway.</summary>
[Collection(UICollection.Name)]
public sealed class ConfigSearchRevealTests(UIThreadFixture ui)
{
    [Fact]
    public async Task SearchRevealDismissesTheCancelledDetailOverlayBeforeItsContinuation()
    {
        await ui.RunOnUIAsync(async () =>
        {
            var page = new ConfigPage();
            ui.Container.Children.Add(page);
            using var json = JsonDocument.Parse("{}");
            var render = (Task)typeof(ConfigPage).GetMethod("ShowDetailWithProgress",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, ["", json.RootElement])!;
            var overlay = Assert.IsType<Border>(page.FindName("DetailLoadingOverlay"));
            Assert.Equal(Visibility.Visible, overlay.Visibility);

            typeof(ConfigPage).GetMethod("RevealSearchPath", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(page, ["optionalNavigation.field8"]);
            try
            {
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            }
            finally
            {
                await render;
                ui.Container.Children.Clear();
            }
        });
    }
}
