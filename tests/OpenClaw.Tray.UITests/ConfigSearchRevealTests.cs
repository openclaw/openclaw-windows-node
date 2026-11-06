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
    private const int DefaultUnloadTimeoutSeconds = 10;
    private static readonly TimeSpan UnloadTimeout = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_UI_UNLOAD_TIMEOUT_SECONDS"), out var seconds) && seconds > 0
            ? seconds : DefaultUnloadTimeoutSeconds);

    [Fact]
    public async Task UninitializedPageUnloadsWithoutResolvingProductionApp()
    {
        await ui.RunOnUIAsync(async () =>
        {
            Assert.IsType<TestApp>(Application.Current);
            var page = new ConfigPage();
            var unloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            page.Unloaded += (_, _) => unloaded.TrySetResult();
            ui.Container.Children.Add(page);
            try
            {
                await ui.YieldToRenderAsync();
                ui.Container.Children.Remove(page);
                await unloaded.Task.WaitAsync(UnloadTimeout);
                await ui.YieldToRenderAsync();
                Assert.True(unloaded.Task.IsCompletedSuccessfully);
            }
            finally
            {
                ui.Container.Children.Remove(page);
            }
        });
    }

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
