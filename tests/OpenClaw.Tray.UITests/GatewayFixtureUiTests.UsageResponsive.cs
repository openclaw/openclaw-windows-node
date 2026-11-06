using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    private static readonly TimeSpan UsageResizeSampleInterval = TimeSpan.FromMilliseconds(300);

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task UsageBackdrop_ViewportWashRemainsAnchoredWhileTheHeaderScrollsAway()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "usage" });
            await WaitUiAsync(run, () => FindById(run, "UsageSummaryCostValue") is { Current.IsOffscreen: false },
                "populated Usage before scrolling");
            var window = AppWindows(run).Single(window => window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "UsagePageRoot")) is not null);
            var handle = new IntPtr(window.Current.NativeWindowHandle);
            var scale = UsageGetDpiForWindow(handle) / 96d;
            var before = UsageWindowBounds(handle);
            var beforeHost = FindById(run, "UsagePageRoot")!.Current.BoundingRectangle.Width / scale;
            var expected = ResizeUsageWindow(handle, 1400 + before.Width / scale - beforeHost, scale, capToDisplay: true);
            await WaitUiAsync(run, () => UsageWindowBounds(handle).Width == expected.Width, "wide Usage viewport");
            var scrollElement = FindById(run, "UsagePageRoot")!;
            var scroll = (ScrollPattern)scrollElement.GetCurrentPattern(ScrollPattern.Pattern);
            await WaitUiAsync(run, () => scroll.Current.VerticallyScrollable, "scrollable Usage content");
            scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
            await WaitUiAsync(run, () => scroll.Current.VerticalScrollPercent == 0 &&
                FindById(run, "UsagePageMarker") is { Current.IsOffscreen: false }, "Usage at top");
            OwnedWindowCapture.Save(run, window, "Usage-Light-viewport-gradient-top.png", FindById(run, "UsagePageMarker")!);
            var top = scrollElement.Current.BoundingRectangle;
            var previousTitle = FindById(run, "UsagePageMarker")!.Current.BoundingRectangle;
            scroll.SetScrollPercent(ScrollPattern.NoScroll, 45);
            await WaitUiAsync(run, () => scroll.Current.VerticalScrollPercent >= 44 &&
                (FindById(run, "UsagePageMarker")!.Current.IsOffscreen ||
                    FindById(run, "UsagePageMarker")!.Current.BoundingRectangle.Top < previousTitle.Top - 100),
                "ordinary Usage header scrolled away");
            OwnedWindowCapture.Save(run, window, "Usage-Light-viewport-gradient-scrolled.png");
            File.WriteAllText(Path.Combine(run.ArtifactsDirectory, "viewport-scroll-proof.json"), JsonSerializer.Serialize(new
            {
                observedHostWidthDip = top.Width / scale,
                viewportTopPx = top.Top,
                observedScrollPercent = scroll.Current.VerticalScrollPercent,
                headerOffscreen = FindById(run, "UsagePageMarker")!.Current.IsOffscreen,
                scale,
                syntheticGateway = true,
                contract = "The decorative viewport wash is behind ordinary scrolling content; no sticky header."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }, scenario: GatewayScenario.CreateUsageAnalytics(UsageAnalyticsMode.Released));
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task UsageBackdrop_RealShellCapturesFullPageWashInLightAndDark()
    {
        foreach (var theme in new[] { "Light", "Dark" })
        {
            await WithAppAsync(async run =>
            {
                await run.InvokeAsync("app.navigate", new { page = "usage" });
                await WaitUiAsync(run, () => FindById(run, "UsageSummaryCostValue") is { Current.IsOffscreen: false },
                    "loaded Usage backdrop");
                var window = AppWindows(run).Single(window => window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "UsagePageRoot")) is not null);
                var handle = new IntPtr(window.Current.NativeWindowHandle);
                var scale = UsageGetDpiForWindow(handle) / 96d;
                Assert.True(scale > 0);
                var manifests = new List<object>();
                var failures = new List<string>();
                foreach (var target in theme == "Light" ? new[] { 1400, 0 } : new[] { 1400 })
                {
                    var name = $"Usage-{theme}-backdrop-{(target == 0 ? "shell-minimum" : "1400")}";
                    try
                    {
                        var before = UsageWindowBounds(handle);
                        var beforeHost = FindById(run, "UsagePageRoot")!.Current.BoundingRectangle.Width / scale;
                        var expected = ResizeUsageWindow(handle, target == 0 ? 1000 : target + before.Width / scale - beforeHost,
                            scale, capToDisplay: target == 1400);
                        await WaitUiAsync(run, () =>
                        {
                            var bounds = UsageWindowBounds(handle);
                            return bounds.Width == expected.Width && bounds.Height == expected.Height;
                        }, "exact backdrop capture window bounds");
                        double? previous = null;
                        await run.WaitForAsync(async () =>
                        {
                            await Task.Delay(UsageResizeSampleInterval);
                            var width = FindById(run, "UsagePageRoot")!.Current.BoundingRectangle.Width;
                            var stable = previous is { } value && Math.Abs(value - width) < 0.01;
                            previous = width;
                            return stable;
                        }, "stable backdrop capture host width");
                        var frame = UsageWindowBounds(handle);
                        var page = FindById(run, "UsagePageRoot")!.Current.BoundingRectangle;
                        var host = page.Width / scale;
                        var file = name + ".png";
                        OwnedWindowCapture.Save(run, window, file, FindById(run, "UsagePageMarker")!);
                        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(run.ArtifactsDirectory, name + ".json")));
                        var manifest = new
                        {
                            theme, pageHostWidthDip = host, contentWidthDip = PageResponsiveLayout.ContentWidth(host),
                            tier = PageResponsiveLayout.TierFor(host).ToString(), scale, file,
                            pageLeftPx = page.Left, pageRightPx = page.Right,
                            windowRectPx = new { frame.Left, frame.Top, frame.Width, frame.Height },
                            windowRectDip = new { width = frame.Width / scale, height = frame.Height / scale },
                            sha256 = capture.RootElement.GetProperty("sha256").GetString(),
                        };
                        manifests.Add(manifest);
                        File.WriteAllText(Path.Combine(run.ArtifactsDirectory, name + "-layout.json"),
                            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
                        Assert.Equal(target == 0 ? PageWidthTier.Medium : PageWidthTier.Wide, PageResponsiveLayout.TierFor(host));
                        if (theme == "Light" && target == 1400)
                        {
                            var scroll = (ScrollPattern)FindById(run, "UsagePageRoot")!.GetCurrentPattern(ScrollPattern.Pattern);
                            scroll.SetScrollPercent(ScrollPattern.NoScroll, 75);
                            await WaitUiAsync(run, () => scroll.Current.VerticalScrollPercent >= 74,
                                "Usage session cards scrolled into view");
                            OwnedWindowCapture.Save(run, window, "Usage-Light-separated-session-cards.png");
                            scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
                            await WaitUiAsync(run, () => scroll.Current.VerticalScrollPercent == 0 &&
                                FindById(run, "UsagePageMarker") is { Current.IsOffscreen: false }, "Usage top restored");
                        }
                    }
                    catch (Exception error)
                    {
                        failures.Add($"{name}: {error}");
                        manifests.Add(new { name, error = error.ToString() });
                        try { OwnedWindowCapture.Save(run, window, name + "-failure.png"); }
                        catch (Exception captureError) { failures.Add(captureError.ToString()); }
                    }
                    finally
                    {
                        File.WriteAllText(Path.Combine(run.ArtifactsDirectory, "backdrop-manifest.json"),
                            JsonSerializer.Serialize(manifests, new JsonSerializerOptions { WriteIndented = true }));
                    }
                }
                Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
            }, scenario: GatewayScenario.CreateUsageAnalytics(UsageAnalyticsMode.Released), prepareSetupHandoff: profile =>
            {
                var file = Path.Combine(profile.DataDirectory, "settings.json");
                var settings = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
                settings["AppTheme"] = theme;
                File.WriteAllText(file, settings.ToJsonString());
                return "";
            });
        }
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task UsageResponsive_RealShellCapturesMeasuredWidthsAndCollapsedNavigation()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "usage" });
            await WaitUiAsync(run, () => FindById(run, "UsageSummaryCostValue") is { Current.IsOffscreen: false },
                "populated Usage page");
            var window = AppWindows(run).Single(window => window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "UsagePageRoot")) is not null);
            var handle = new IntPtr(window.Current.NativeWindowHandle);
            var scale = UsageGetDpiForWindow(handle) / 96d;
            Assert.True(scale > 0);
            double HostWidth() => FindById(run, "UsagePageRoot")!.Current.BoundingRectangle.Width / scale;
            double PaneItemWidth() => FindById(run, "SettingsNavChat")!.Current.BoundingRectangle.Width / scale;
            async Task StableHost()
            {
                var previous = (Host: HostWidth(), Pane: PaneItemWidth());
                await run.WaitForAsync(async () =>
                {
                    await Task.Delay(UsageResizeSampleInterval);
                    var current = (Host: HostWidth(), Pane: PaneItemWidth());
                    var stable = Math.Abs(previous.Host - current.Host) < 0.01 && Math.Abs(previous.Pane - current.Pane) < 0.01;
                    previous = current;
                    return stable;
                }, "stable measured Usage host and navigation widths sampled 300 ms apart");
            }
            await StableHost();
            var manifests = new List<object>();
            var errors = new List<string>();
            var hashes = new HashSet<string>();
            var sizes = new HashSet<string>();
            var collapsed = false;
            try
            {
                foreach (var target in new[] { 1400, 1000, 760, 0, -1 })
                {
                    var name = target > 0 ? $"host-{target}" : target == 0 ? "shell-minimum" : "shell-minimum-collapsed";
                    try
                    {
                        var before = UsageWindowBounds(handle);
                        var beforeHost = HostWidth();
                        if (target == -1)
                        {
                            Invoke(FindById(run, "SettingsTogglePane")!);
                            collapsed = true;
                        }
                        else
                        {
                            var chrome = before.Width / scale - beforeHost;
                            var hostTarget = target == 1000 ? target + PageResponsiveLayout.GapStack : target;
                            var expected = ResizeUsageWindow(handle, target == 0 ? 1000 : hostTarget + chrome, scale, capToDisplay: target == 1400);
                            await WaitUiAsync(run, () =>
                            {
                                var rect = UsageWindowBounds(handle);
                                return rect.Width == expected.Width && rect.Height == expected.Height;
                            }, $"exact {name} window pixel bounds");
                        }
                        await WaitUiAsync(run, () => Math.Abs(HostWidth() - beforeHost) > 1, $"changed {name} host width");
                        await StableHost();
                        System.Windows.Rect? previousAnchor = null;
                        await WaitUiAsync(run, () =>
                        {
                            var anchor = FindById(run, "UsageExport")!.Current.BoundingRectangle;
                            var frame = UsageWindowBounds(handle);
                            var stable = previousAnchor is { } previous && previous == anchor;
                            previousAnchor = anchor;
                            return stable && anchor.Left >= frame.Left && anchor.Top >= frame.Top &&
                                anchor.Right <= frame.Right && anchor.Bottom <= frame.Bottom;
                        }, "stable mast action fully inside the resized Hub");
                        var bounds = UsageWindowBounds(handle);
                        var hostWidth = HostWidth();
                        var sizeKey = $"{bounds.Width}x{bounds.Height}:{hostWidth:F2}";
                        var file = $"Usage-Light-responsive-{name}.png";
                        OwnedWindowCapture.Save(run, window, file, FindById(run, "UsagePageMarker")!);
                        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(run.ArtifactsDirectory,
                            Path.ChangeExtension(file, ".json"))));
                        var sha = capture.RootElement.GetProperty("sha256").GetString()!;
                        var state = new
                        {
                            name, requestedHostWidthDip = target > 0 ? (int?)target : null,
                            pageHostWidthDip = hostWidth, contentWidthDip = PageResponsiveLayout.ContentWidth(hostWidth),
                            navigationItemWidthDip = PaneItemWidth(),
                            tier = PageResponsiveLayout.TierFor(hostWidth).ToString(), scale,
                            windowRectPx = new { bounds.Left, bounds.Top, bounds.Width, bounds.Height },
                            windowRectDip = new { left = bounds.Left / scale, top = bounds.Top / scale,
                                width = bounds.Width / scale, height = bounds.Height / scale },
                            file, sha256 = sha, status = "captured", syntheticGateway = true,
                        };
                        File.WriteAllText(Path.Combine(run.ArtifactsDirectory, name + "-layout.json"),
                            JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
                        manifests.Add(state);
                        if (target is 1400 or -1)
                        {
                            var ink = UsageHeaderInkBounds(run, window, Path.Combine(run.ArtifactsDirectory, file), scale);
                            File.WriteAllText(Path.Combine(run.ArtifactsDirectory, name + "-header-ink.json"),
                                JsonSerializer.Serialize(ink, new JsonSerializerOptions { WriteIndented = true }));
                        }
                        if (target != -1) Assert.NotEqual(before.Width, bounds.Width);
                        if (target <= 0) Assert.InRange(Math.Abs(bounds.Width / scale - 1000), 0, 1);
                        Assert.True(sizes.Add(sizeKey), "The capture must have distinct measured bounds.");
                        Assert.True(hashes.Add(sha), "The capture is byte-identical to another width.");
                        Assert.Equal(target >= 1000 ? PageWidthTier.Wide : PageWidthTier.Medium,
                            PageResponsiveLayout.TierFor(hostWidth));
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{name}: {ex}");
                        manifests.Add(new { name, status = "failed", error = ex.ToString() });
                        try { OwnedWindowCapture.Save(run, window, $"Usage-{name}-failure.png"); }
                        catch (Exception captureError) { errors.Add($"{name} failure capture: {captureError}"); }
                        await run.InvokeAsync("app.navigate", new { page = "usage" });
                    }
                    finally
                    {
                        File.WriteAllText(Path.Combine(run.ArtifactsDirectory, "responsive-manifest.json"),
                            JsonSerializer.Serialize(manifests, new JsonSerializerOptions { WriteIndented = true }));
                    }
                }
            }
            finally
            {
                if (collapsed) Invoke(FindById(run, "SettingsTogglePane")!);
            }
            Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        }, scenario: GatewayScenario.CreateUsageAnalytics(UsageAnalyticsMode.Released));
    }

    private static System.Drawing.Rectangle UsageWindowBounds(IntPtr handle)
    {
        var old = UsageSetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            Assert.True(UsageGetWindowRect(handle, out var rect));
            return System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }
        finally { UsageSetThreadDpiAwarenessContext(old); }
    }

    private sealed record UsageHeaderInk(double TopPx, double BottomPx, double CentreDip);

    private static Dictionary<string, UsageHeaderInk> UsageHeaderInkBounds(
        OpenClaw.GatewayFixtureHost.GatewayFixtureRun run, AutomationElement window, string file, double scale)
    {
        using var image = new System.Drawing.Bitmap(file);
        var frame = UsageWindowBounds(new IntPtr(window.Current.NativeWindowHandle));
        var metric = FindById(run, "UsageMetric")!;
        var view = FindById(run, "UsageChartView")!;
        AutomationElement Label(AutomationElement parent, string name) =>
            parent.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name))
            ?? throw new InvalidOperationException($"Header label '{name}' is not exposed.");
        var regions = new Dictionary<string, AutomationElement>
        {
            ["pivot"] = Label(metric, "Estimated cost"),
            ["title"] = FindById(run, "UsagePerDayTitle")!,
            ["chart"] = Label(view, "Chart"),
            ["table"] = Label(view, "Table"),
            ["legend"] = FindById(run, "UsageDailyAverageLegend")!,
        };
        var result = new Dictionary<string, UsageHeaderInk>();
        foreach (var (name, element) in regions)
        {
            var area = element.Current.BoundingRectangle;
            var top = image.Height;
            var bottom = -1;
            for (var y = Math.Max(0, (int)Math.Ceiling(area.Top - frame.Top)); y < Math.Min(image.Height, (int)Math.Floor(area.Bottom - frame.Top)); y++)
                for (var x = Math.Max(0, (int)Math.Ceiling(area.Left - frame.Left)); x < Math.Min(image.Width, (int)Math.Floor(area.Right - frame.Left)); x++)
                {
                    var color = image.GetPixel(x, y);
                    var maximum = Math.Max(color.R, Math.Max(color.G, color.B));
                    var minimum = Math.Min(color.R, Math.Min(color.G, color.B));
                    if (maximum < 150 && maximum - minimum <= 25)
                    {
                        top = Math.Min(top, y);
                        bottom = Math.Max(bottom, y);
                    }
                }
            Assert.True(bottom >= top, $"No foreground ink found for the {name} header label.");
            result[name] = new(top, bottom + 1, (top + bottom + 1) / (2 * scale));
        }
        return result;
    }

    private static System.Drawing.Size ResizeUsageWindow(IntPtr handle, double widthDip, double scale, bool capToDisplay)
    {
        var old = UsageSetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            var work = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            var width = (int)Math.Round(widthDip * scale);
            if (capToDisplay) width = Math.Min(width, work.Width);
            var height = Math.Min((int)Math.Round(1000 * scale), work.Height);
            Assert.True(UsageSetWindowPos(handle, IntPtr.Zero, work.Left, work.Top, width, height, 0x0014),
                "Owned Hub resize failed.");
            return new(width, height);
        }
        finally { UsageSetThreadDpiAwarenessContext(old); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UsageNativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static extern bool UsageGetWindowRect(IntPtr window, out UsageNativeRect rect);
    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint UsageGetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "SetThreadDpiAwarenessContext")]
    private static extern IntPtr UsageSetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    private static extern bool UsageSetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
