using System.Text.Json;
using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task DashboardsRenderNativeReportsTabsAndFailureStates()
    {
        var previousTheme = Environment.GetEnvironmentVariable("OPENCLAW_FIXTURE_APP_THEME");
        try
        {
            foreach (var theme in new[] { "Light", "Dark" })
            {
                Environment.SetEnvironmentVariable("OPENCLAW_FIXTURE_APP_THEME", theme);
                await WithAppAsync(async run =>
                {
                    await run.InvokeAsync("app.navigate", new { page = "dashboards" });
                    await WaitUiAsync(run, () => FindById(run, $"DashboardCard_{FixtureBoards.OpsSessionKey}") is not null,
                        "dashboard gallery populated");
                    var hub = AppWindows(run).Single(window => window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "DashboardsTitle")) is not null);
                    ((WindowPattern)hub.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Maximized);
                    await WaitUiAsync(run, () => ((WindowPattern)hub.GetCurrentPattern(WindowPattern.Pattern))
                            .Current.WindowVisualState == WindowVisualState.Maximized,
                        "maximized dashboard layout");
                    System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(DwmFlush());
                    AxeHelper.AssertNoAccessibilityErrors(new IntPtr(hub.Current.NativeWindowHandle),
                        context: $"Dashboards gallery ({theme})", targetProcessId: run.AppProcessId);
                    OwnedWindowCapture.Save(run, hub, $"dashboards-{theme}-gallery.png");

                    AutomationElement? opsCard = null;
                    await WaitUiAsync(run, () => (opsCard = FindById(run, $"DashboardCard_{FixtureBoards.OpsSessionKey}"))
                        is { Current.IsOffscreen: false }, "visible Ops dashboard card");
                    Invoke(opsCard!);
                    // This host's UIA provider can stop at the WebView pane.
                    // Verify the visible HTML and native report through owned-window pixels.
                    await WaitForDashboardPixelsAsync(run, hub, $"dashboards-{theme}-overview.png",
                        ["99.97", "Uptime", "Tickets closed", "Deployments this week"],
                        requireDeploymentPlot: true);
                    AxeHelper.AssertNoAccessibilityErrors(new IntPtr(hub.Current.NativeWindowHandle),
                        context: $"Dashboards widgets ({theme})", targetProcessId: run.AppProcessId);
                    ScrollAncestorToBottom(FindById(run, "BoardWidget_broken-widget")!);
                    await WaitForDashboardPixelsAsync(run, hub, $"dashboards-{theme}-approval-error.png",
                        ["Needs approval", "contained to this card"]);

                    var metrics = FindInApp(run, new AndCondition(
                        new PropertyCondition(AutomationElement.NameProperty, "Metrics"),
                        new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true)));
                    Assert.NotNull(metrics);
                    Invoke(metrics!);
                    await WaitForDashboardPixelsAsync(run, hub, $"dashboards-{theme}-tabs.png",
                        ["MCP app", "Access rejected", "disabled plugin"]);

                    AutomationElement? integrations = null;
                    await WaitUiAsync(run, () => (integrations = FindInApp(run, new AndCondition(
                        new PropertyCondition(AutomationElement.NameProperty, "Integrations"),
                        new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true)))) is not null,
                        "website tab");
                    Invoke(integrations!);
                    await WaitForDashboardPixelsAsync(run, hub, $"dashboards-{theme}-website.png",
                        ["documentation examples", "Open website"]);

                    // Reopening the same hub route must return from an in-page board to the gallery.
                    await run.InvokeAsync("app.navigate", new { page = "dashboards" });
                    AutomationElement? emptyCard = null;
                    await WaitUiAsync(run, () => (emptyCard = FindById(run, $"DashboardCard_{FixtureBoards.EmptySessionKey}"))
                            is { Current.IsOffscreen: false },
                        "gallery after same-route navigation");
                    Invoke(emptyCard!);
                    await WaitUiAsync(run, () => FindById(run, "DashboardEmptyOpenChat") is { Current.IsOffscreen: false },
                        "empty dashboard action");
                    OwnedWindowCapture.Save(run, hub, $"dashboards-{theme}-empty.png");

                    await run.InvokeAsync("app.navigate", new { page = "dashboards" });
                    AutomationElement? search = null;
                    await WaitUiAsync(run, () => (search = FindById(run, "DashboardsSearch")) is not null, "gallery search");
                    ((ValuePattern)search!.GetCurrentPattern(ValuePattern.Pattern))
                        .SetValue("no matching synthetic dashboard");
                    await WaitUiAsync(run, () => FindById(run, "DashboardsNoResultsTitle") is not null, "no-results state");
                    await WaitForDashboardPixelsAsync(run, hub, $"dashboards-{theme}-no-results.png", ["No matching dashboards"]);

                    // Stop only this synthetic Gateway, then observe the real connection/UI boundary.
                    await run.Gateway.DisposeAsync();
                    await WaitUiAsync(run, () => FindById(run, "DashboardsOpenConnection") is { Current.IsOffscreen: false },
                        "disconnected dashboard recovery action");
                    OwnedWindowCapture.Save(run, hub, $"dashboards-{theme}-disconnected.png");

                    Assert.DoesNotContain(run.Gateway.Requests, request =>
                        request.Method is "board.widget.grant" or "board.update" or "board.action");
                    await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "dashboard-contract.json"),
                        JsonSerializer.Serialize(new
                        {
                            theme,
                            proof = "Real isolated app, Gateway WebSocket, native UIA navigation and Windows OCR over owned-window pixels",
                            html = "99.97%",
                            report = "Tickets closed",
                            mutations = 0,
                        }));
                }, scenario: GatewayScenario.CreateDashboards());
            }

        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_FIXTURE_APP_THEME", previousTheme);
        }
    }

    private static async Task WaitForDashboardPixelsAsync(GatewayFixtureRun run, AutomationElement hub,
        string imageName, string[] expectedText, bool requireDeploymentPlot = false)
    {
        var engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
            ?? throw new InvalidOperationException("English Windows OCR is required to verify the sandbox's rendered pixels.");
        await run.WaitForAsync(async () =>
        {
            OwnedWindowCapture.Save(run, hub, imageName);
            var path = Path.Combine(run.ArtifactsDirectory, imageName);
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap);
            File.WriteAllText(Path.ChangeExtension(path, ".ocr.txt"), result.Text);
            return expectedText.All(text => result.Text.Contains(text, StringComparison.OrdinalIgnoreCase))
                && (!requireDeploymentPlot || DeploymentPlotHasVisibleInk(path, result));
        }, $"visible dashboard pixels: {string.Join(", ", expectedText)}", TimeSpan.FromSeconds(30));
    }

    private static bool DeploymentPlotHasVisibleInk(string imagePath, OcrResult content)
    {
        var title = content.Lines.FirstOrDefault(line => line.Text.Contains("Deployments this week", StringComparison.OrdinalIgnoreCase));
        var caption = content.Lines.FirstOrDefault(line => line.Text.Contains("Mon to Sun", StringComparison.OrdinalIgnoreCase));
        if (title is null || caption is null)
            return false;
        var left = title.Words.Min(word => word.BoundingRect.Left);
        var top = title.Words.Max(word => word.BoundingRect.Bottom);
        var height = caption.Words.Min(word => word.BoundingRect.Top) - top;
        using var image = new System.Drawing.Bitmap(imagePath);
        // This fixture chart spans all 12 columns, with no sibling to its right.
        var width = image.Width - left;
        // The middle of this fixture's SVG contains only the plotted curve,
        // not its header, caption or card border. A flat crop cannot prove it rendered.
        var area = System.Drawing.Rectangle.Intersect(
            new System.Drawing.Rectangle((int)(left + width * 0.15), (int)(top + height * 0.2),
                (int)(width * 0.7), (int)(height * 0.6)),
            new System.Drawing.Rectangle(0, 0, image.Width, image.Height));
        var minimum = 255;
        var maximum = 0;
        for (var y = area.Top; y < area.Bottom; y += 2)
        {
            for (var x = area.Left; x < area.Right; x += 2)
            {
                var color = image.GetPixel(x, y);
                var luminance = (299 * color.R + 587 * color.G + 114 * color.B) / 1000;
                minimum = Math.Min(minimum, luminance);
                maximum = Math.Max(maximum, luminance);
            }
        }
        const int MinimumPlotLuminanceRange = 64;
        File.WriteAllText(Path.ChangeExtension(imagePath, ".plot.json"),
            JsonSerializer.Serialize(new { minimum, maximum, requiredRange = MinimumPlotLuminanceRange }));
        return maximum - minimum >= MinimumPlotLuminanceRange;
    }
}
