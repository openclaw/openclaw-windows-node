using System.Windows.Automation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Security.Cryptography;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task SystemsHero_EmptyFixtureHasHonestCountsInLightAndDark()
    {
        foreach (var theme in new[] { "Light", "Dark" })
        {
            await WithAppAsync(async run =>
            {
                await run.InvokeAsync("app.navigate", new { page = "systems" });
                await WaitUiAsync(run, () => FindById(run, "SystemsEmpty") is { Current.IsOffscreen: false } &&
                    FindById(run, "SystemsOnlineTile") is { Current.IsOffscreen: false } &&
                    FindById(run, "SystemsIssuesTile") is { Current.IsOffscreen: false, Current.IsEnabled: false },
                    "reported empty inventory renders an honest zero and disabled issue action");
                Assert.Null(FindById(run, "SystemsPairingRows"));
                Assert.Null(FindById(run, "SystemsIssuesView"));
                Assert.Null(FindById(run, "SystemsBackups"));
                var tile = FindById(run, "SystemsOnlineTile")!;
                Assert.Contains(tile.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>(),
                    element => element.Current.Name == "0 of 0");
                var window = AppWindows(run).Single(window => window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemsPageMarker")) is not null);
                OwnedWindowCapture.Save(run, window, $"{theme}-systems-hero-empty.png",
                    FindById(run, "SystemsEmpty")!, tile);
            }, scenario: GatewayScenario.CreateSystems(empty: true), appTheme: theme);
        }
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task SystemsPage_OmitsConnectionMapInLightAndDark()
    {
        var app = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_APP")
            ?? throw new InvalidOperationException("Set OPENCLAW_GATEWAY_FIXTURE_APP to the current build.");
        foreach (var theme in new[] { "Light", "Dark" })
        {
            await using var run = await GatewayFixtureRun.StartAsync(app,
                Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS"),
                scenario: GatewayScenario.CreateSystems(empty: false), appTheme: theme);
            await run.InvokeAsync("app.navigate", new { page = "systems" });
            await WaitUiAsync(run, () => FindById(run, "SystemsDevicesAndGatewayHeading") is not null,
                "reported Systems inventory loaded");
            Assert.Null(FindById(run, "SystemsConstellation"));
            Assert.Null(FindById(run, "SystemsMap_gateway"));
            var window = AppWindows(run).Single(w => w.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemsPageMarker")) is not null);
            OwnedWindowCapture.Save(run, window, $"{theme}-systems-inventory.png",
                FindById(run, "SystemsDevicesAndGatewayHeading")!);
            await run.WriteReportAsync("passed");
            output.WriteLine($"{theme} Systems inventory without a connection map: {run.ArtifactsDirectory}");
        }
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task SystemsInventory_ExistingSeparatedCards()
    {
        var app = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_APP")
            ?? throw new InvalidOperationException("Set OPENCLAW_GATEWAY_FIXTURE_APP to the current build.");
        await using var run = await GatewayFixtureRun.StartAsync(app,
            Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS"),
            scenario: GatewayScenario.CreateSystems(empty: false), appTheme: "Light");
        await run.InvokeAsync("app.navigate", new { page = "systems" });
        await WaitUiAsync(run, () => FindById(run, "SystemsRow_node:fixture-studio") is not null, "Systems inventory loaded");
        var scroll = FindById(run, "SystemsPageScroll")!;
        var pattern = (ScrollPattern)scroll.GetCurrentPattern(ScrollPattern.Pattern);
        var row = FindById(run, "SystemsRow_node:fixture-macbook")!;
        var viewport = scroll.Current.BoundingRectangle;
        var range = viewport.Height * (100 / pattern.Current.VerticalViewSize - 1);
        var desired = Math.Clamp((row.Current.BoundingRectangle.Top - viewport.Top - 60) / range * 100, 0, 100);
        pattern.SetScrollPercent(ScrollPattern.NoScroll, desired);
        await WaitUiAsync(run, () => FindById(run, "SystemsRow_node:fixture-macbook") is { Current.IsOffscreen: false } &&
            FindById(run, "SystemsRow_node:fixture-studio") is { Current.IsOffscreen: false }, "multiple native separated rows visible");
        var window = AppWindows(run).Single(w => w.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemsPageMarker")) is not null);
        OwnedWindowCapture.Save(run, window, "Light-systems-separated-cards.png",
            FindById(run, "SystemsRow_node:fixture-macbook")!, FindById(run, "SystemsRow_node:fixture-studio")!);
        await run.InvokeAsync("app.navigate", new { page = "systems:gateway" });
        AutomationElement? GatewayDetail(string id) => FindById(run, "SystemsRow_gateway")?.FindFirst(
            TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
        // Section panels have no UIA peer. Observe the shipped accessible heading and real row state.
        await WaitUiAsync(run, () => FindById(run, "SystemsRow_gateway") is { } gateway &&
            gateway.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expansion) &&
            ((ExpandCollapsePattern)expansion).Current.ExpandCollapseState == ExpandCollapseState.Expanded &&
            GatewayDetail("SystemsSessionsHeading") is not null && GatewayDetail("SystemsDiagnostics") is not null,
            "deep-linked Gateway is expanded with accessible sessions and diagnostics");
        scroll = FindById(run, "SystemsPageScroll")!;
        pattern = (ScrollPattern)scroll.GetCurrentPattern(ScrollPattern.Pattern);
        viewport = scroll.Current.BoundingRectangle;
        range = viewport.Height * (100 / pattern.Current.VerticalViewSize - 1);
        Assert.True(range > 0, "The populated Systems fixture must expose its expanded Gateway details through ordinary scrolling.");
        var currentOffset = pattern.Current.VerticalScrollPercent / 100 * range;
        var targetOffset = currentOffset + GatewayDetail("SystemsSessionsHeading")!.Current.BoundingRectangle.Top - viewport.Top - 16;
        pattern.SetScrollPercent(ScrollPattern.NoScroll, Math.Clamp(targetOffset / range * 100, 0, 100));
        await WaitUiAsync(run, () => GatewayDetail("SystemsSessionsHeading") is { Current.IsOffscreen: false } &&
            GatewayDetail("SystemsDiagnostics") is { Current.IsOffscreen: false },
            "Gateway sessions and diagnostics scrolled into the current Settings viewport");
        OwnedWindowCapture.Save(run, window, "Light-systems-gateway-details.png",
            GatewayDetail("SystemsSessionsHeading")!, GatewayDetail("SystemsDiagnostics")!);
        await run.WriteReportAsync("passed");
        output.WriteLine($"Existing card separation: {run.ArtifactsDirectory}\\Light-systems-separated-cards.png");
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task SystemsHero_TopAndScrolled()
    {
        var app = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_APP")
            ?? throw new InvalidOperationException("Set OPENCLAW_GATEWAY_FIXTURE_APP to the current build.");
        await using var run = await GatewayFixtureRun.StartAsync(app,
            Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS"),
            scenario: GatewayScenario.CreateSystems(empty: false), appTheme: "Light");
        await run.InvokeAsync("app.navigate", new { page = "systems" });
        await WaitUiAsync(run, () => FindById(run, "SystemsDevicesAndGatewayHeading") is not null, "populated Systems viewport");
        var window = AppWindows(run).Single(w => w.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemsPageMarker")) is not null);
        var scroll = FindById(run, "SystemsPageScroll")!;
        var pattern = (ScrollPattern)scroll.GetCurrentPattern(ScrollPattern.Pattern);
        pattern.SetScrollPercent(ScrollPattern.NoScroll, 0);
        await WaitUiAsync(run, () => pattern.Current.VerticalScrollPercent == 0 &&
            FindById(run, "SystemsPageMarker") is { Current.IsOffscreen: false }, "Systems mast at top");
        OwnedWindowCapture.Save(run, window, "Light-viewport-top.png", FindById(run, "SystemsPageMarker")!);
        var before = scroll.Current.BoundingRectangle;
        pattern.SetScrollPercent(ScrollPattern.NoScroll, 35);
        await WaitUiAsync(run, () => pattern.Current.VerticalScrollPercent > 20 &&
            FindById(run, "SystemsPageMarker") is { Current.IsOffscreen: true }, "Systems mast scrolled out normally");
        Assert.Equal(before, scroll.Current.BoundingRectangle);
        OwnedWindowCapture.Save(run, window, "Light-viewport-scrolled.png");
        await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "viewport-proof.json"),
            JsonSerializer.Serialize(new { run.AppProcessId, verticalScrollPercent = pattern.Current.VerticalScrollPercent,
                viewportBounds = before, mastIsOffscreen = true,
                qualification = "Real isolated app, synthetic Gateway. Hero scrolls with inventory; viewport remains fixed." },
                new JsonSerializerOptions { WriteIndented = true }));
        await run.WriteReportAsync("passed");
        output.WriteLine($"Hero scroll proof: {run.ArtifactsDirectory}");
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task SystemsHero_FullHostLightAndDark()
    {
        var app = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_APP")
            ?? throw new InvalidOperationException("Set OPENCLAW_GATEWAY_FIXTURE_APP to the freshness-verified build.");
        foreach (var theme in new[] { "Light", "Dark" })
        {
            await using var run = await GatewayFixtureRun.StartAsync(app,
                Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS"),
                scenario: GatewayScenario.CreateSystems(empty: false), appTheme: theme);
            await run.InvokeAsync("app.navigate", new { page = "systems" });
            await WaitUiAsync(run, () => FindById(run, "SystemsDevicesAndGatewayHeading") is not null, "loaded Systems hero");
            var window = AppWindows(run).Single(w => w.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemsPageMarker")) is not null);
            var hwnd = new IntPtr(window.Current.NativeWindowHandle);
            var scale = SystemsWindowDpi(hwnd) / 96d;
            var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd),
                Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
            var windowPattern = (WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern);
            windowPattern.SetWindowVisualState(WindowVisualState.Normal);
            await WaitUiAsync(run, () => windowPattern.Current.WindowVisualState == WindowVisualState.Normal &&
                FindById(run, "SystemsPageScroll") is { Current.BoundingRectangle.Width: > 0 },
                "normal owned window and current Systems scroll viewport");
            var chrome = window.Current.BoundingRectangle.Width - FindById(run, "SystemsPageScroll")!.Current.BoundingRectangle.Width;
            var records = new List<object>();
            foreach (var request in new[] { 1400, 760 })
            {
                var target = Math.Min(work.Width, request == 520 ? 1000 * scale : request * scale + chrome);
                var height = Math.Min(work.Height, window.Current.BoundingRectangle.Height);
                Assert.True(SystemsSetWindowPos(hwnd, IntPtr.Zero, work.X, work.Y, (int)Math.Round(target), (int)Math.Round(height), 0x0014));
                var lastWidth = -1d;
                await WaitUiAsync(run, () =>
                {
                    var width = FindById(run, "SystemsPageScroll")!.Current.BoundingRectangle.Width / scale;
                    var stable = Math.Abs(width - lastWidth) < .1;
                    lastWidth = width;
                    return stable && Math.Abs(window.Current.BoundingRectangle.Width - target) <= 2 &&
                        OpenClawTray.Presentation.PageResponsiveLayout.TierFor(width) ==
                        (request == 760 ? OpenClawTray.Presentation.PageWidthTier.Medium : OpenClawTray.Presentation.PageWidthTier.Wide);
                }, "native hero width settled");
                var scroll = FindById(run, "SystemsPageScroll")!;
                if (scroll.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern))
                    ((ScrollPattern)pattern).SetScrollPercent(ScrollPattern.NoScroll, 0);
                await WaitUiAsync(run, () => FindById(run, "SystemsPageMarker") is { Current.IsOffscreen: false }, "hero mast visible");
                Assert.True(SystemsGetWindowRect(hwnd, out var rect));
                Assert.InRange(Math.Abs(rect.Right - rect.Left - target), 0, 2);
                var hostWidth = scroll.Current.BoundingRectangle.Width / scale;
                var name = $"{theme}-hero-{request}-host-{Math.Round(hostWidth)}.png";
                OwnedWindowCapture.Save(run, window, name, FindById(run, "SystemsPageMarker")!);
                records.Add(new { theme, requestedHost = request, measuredHostDip = hostWidth,
                    contentDip = FindById(run, "SystemsContentColumn")?.Current.BoundingRectangle.Width / scale,
                    tier = OpenClawTray.Presentation.PageResponsiveLayout.TierFor(hostWidth).ToString(),
                    windowPx = new { rect.Left, rect.Top, rect.Right, rect.Bottom }, scale, image = name,
                    sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(run.ArtifactsDirectory, name)))),
                    qualification = "Native owned shell. Narrow widths use the separate mounted viewport route." });
                await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "hero-manifest.json"),
                    JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            }
            await run.WriteReportAsync("passed");
            output.WriteLine($"{theme} hero: {run.ArtifactsDirectory}");
        }
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task SystemsResponsive_ReachableShellWidths()
    {
        var app = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_APP")
            ?? throw new InvalidOperationException("Set OPENCLAW_GATEWAY_FIXTURE_APP to the freshness-verified build.");
        await using var run = await GatewayFixtureRun.StartAsync(app,
            Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS"),
            scenario: GatewayScenario.CreateSystems(empty: false), appTheme: "Light");
        output.WriteLine($"Responsive captures: {run.ArtifactsDirectory}");
        var records = new List<Dictionary<string, object?>>();
        AutomationElement Window() => AppWindows(run).Single(w => w.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemsPageMarker")) is not null);
        await run.InvokeAsync("app.navigate", new { page = "systems" });
        await WaitUiAsync(run, () => FindById(run, "SystemsDevicesAndGatewayHeading") is not null, "populated Systems page");
        var window = Window();
        var hwnd = new IntPtr(window.Current.NativeWindowHandle);
        var scale = SystemsWindowDpi(hwnd) / 96d;
        var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd),
            Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Normal);
        var baseline = window.Current.BoundingRectangle;
        var baselineHost = FindById(run, "SystemsPageScroll")!.Current.BoundingRectangle;
        var chrome = baseline.Width - baselineHost.Width;
        var baselineWidth = Math.Min(baseline.Width, display.Width);
        var baselineHeight = Math.Min(baseline.Height, display.Height);

        var previousWidths = new HashSet<int>();
        var capturedHashes = new HashSet<string>();
        foreach (var (requested, collapsed) in new[] { (1400, false), (1000, false), (760, false), (520, false), (950, true) })
        {
            var record = new Dictionary<string, object?> { ["requestedHostWidth"] = requested, ["theme"] = "Light", ["paneCollapsed"] = collapsed };
            records.Add(record);
            var paneToggled = false;
            try
            {
                await run.InvokeAsync("app.navigate", new { page = "systems" });
                await WaitUiAsync(run, () => FindById(run, "SystemsPageScroll") is not null, "baseline Systems scroll");
                var target = requested == 520 || collapsed ? 1000 * scale : requested * scale + chrome;
                var targetWindow = Math.Min(display.Width, Math.Max(1000 * scale, target));
                Assert.True(SystemsSetWindowPos(hwnd, IntPtr.Zero, display.X, display.Y,
                    (int)Math.Round(targetWindow), (int)Math.Round(baselineHeight), 0x0014));
                await WaitUiAsync(run, () => Math.Abs(Window().Current.BoundingRectangle.Width - targetWindow) <= 2,
                    $"native shell resize for requested host {requested}");
                Assert.True(SystemsGetWindowRect(hwnd, out var nativeBounds));
                Assert.InRange(Math.Abs(nativeBounds.Right - nativeBounds.Left - targetWindow), 0, 2);
                var previousHostWidth = -1d;
                await WaitUiAsync(run, () =>
                {
                    var width = FindById(run, "SystemsPageScroll")!.Current.BoundingRectangle.Width;
                    // UIA bounds are clipped to native non-client/display bounds. Do not
                    // assume their chrome delta stays pixel-identical after moving the HWND.
                    var inRange = targetWindow > baseline.Width + 2 ? width > baselineHost.Width + 2
                        : width > 0 && width <= targetWindow;
                    var stable = Math.Abs(width - previousHostWidth) < .1;
                    previousHostWidth = width;
                    return inRange && stable;
                }, "page-host layout settled after native resize");
                if (collapsed)
                {
                    // Compare pane states at the same native window size, not the initial wider window.
                    var expandedHostWidth = FindById(run, "SystemsPageScroll")!.Current.BoundingRectangle.Width;
                    record["expandedPaneHostWidthDip"] = expandedHostWidth / scale;
                    Invoke(FindById(run, "SettingsTogglePane")!);
                    paneToggled = true;
                    previousHostWidth = -1;
                    await WaitUiAsync(run, () =>
                    {
                        var width = FindById(run, "SystemsPageScroll")!.Current.BoundingRectangle.Width;
                        var stable = Math.Abs(width - previousHostWidth) < .1;
                        previousHostWidth = width;
                        return width > expandedHostWidth + 100 * scale && width <= targetWindow && stable &&
                            Math.Abs(Window().Current.BoundingRectangle.Width - targetWindow) <= 2;
                    }, "collapsed pane releases host width at the same native window size");
                }
                var measuredBeforeTierCheck = FindById(run, "SystemsPageScroll")!.Current.BoundingRectangle.Width / scale;
                var expectedTier = requested == 520 || collapsed ? OpenClawTray.Presentation.PageWidthTier.Medium
                    : OpenClawTray.Presentation.PageResponsiveLayout.TierFor(requested);
                // Calibrate the exact Wide boundary using measured host width rather than
                // assuming native non-client clipping equals the initial chrome estimate.
                if (requested == 1000 && OpenClawTray.Presentation.PageResponsiveLayout.TierFor(measuredBeforeTierCheck) != expectedTier)
                {
                    targetWindow += Math.Ceiling((1000 - measuredBeforeTierCheck) * scale) + 1;
                    Assert.True(targetWindow <= display.Width);
                    Assert.True(SystemsSetWindowPos(hwnd, IntPtr.Zero, display.X, display.Y,
                        (int)Math.Round(targetWindow), (int)Math.Round(baselineHeight), 0x0014));
                    var last = -1d;
                    await WaitUiAsync(run, () =>
                    {
                        var width = FindById(run, "SystemsPageScroll")!.Current.BoundingRectangle.Width / scale;
                        var stable = Math.Abs(width - last) < .1;
                        last = width;
                        return stable && OpenClawTray.Presentation.PageResponsiveLayout.TierFor(width) == expectedTier;
                    }, "measured Wide boundary settled");
                    Assert.True(SystemsGetWindowRect(hwnd, out nativeBounds));
                    Assert.InRange(Math.Abs(nativeBounds.Right - nativeBounds.Left - targetWindow), 0, 2);
                }
                var scroll = FindById(run, "SystemsPageScroll")!;
                if (scroll.Current.IsKeyboardFocusable)
                    scroll.SetFocus();
                if (scroll.TryGetCurrentPattern(ScrollPattern.Pattern, out var scrollPattern))
                    ((ScrollPattern)scrollPattern).SetScrollPercent(ScrollPattern.NoScroll, 0);
                await WaitUiAsync(run, () => FindById(run, "SystemsPageMarker") is { Current.IsOffscreen: false },
                    "mast visible after resize");
                var pageBounds = scroll.Current.BoundingRectangle;
                var windowBounds = Window().Current.BoundingRectangle;
                var measured = pageBounds.Width / scale;
                Assert.Equal(expectedTier, OpenClawTray.Presentation.PageResponsiveLayout.TierFor(measured));
                Assert.True(previousWidths.Add((int)Math.Round(pageBounds.Width)), "Page-host width did not change from another capture.");
                var content = FindById(run, "SystemsContentColumn");
                var name = $"Light-host-{requested}-measured-{Math.Round(measured)}{(collapsed ? "-pane-collapsed" : "")}.png";
                record["measuredHostWidthDip"] = measured;
                record["measuredWindowWidthDip"] = windowBounds.Width / scale;
                record["windowRectPx"] = new { nativeBounds.Left, nativeBounds.Top, nativeBounds.Right, nativeBounds.Bottom };
                record["windowRectDip"] = new { Left = nativeBounds.Left / scale, Top = nativeBounds.Top / scale,
                    Right = nativeBounds.Right / scale, Bottom = nativeBounds.Bottom / scale };
                record["contentWidthDip"] = content?.Current.BoundingRectangle.Width / scale
                    ?? OpenClawTray.Presentation.PageResponsiveLayout.ContentWidth(measured);
                record["contentWidthSource"] = content is null ? "Canonical helper applied to measured host" : "UIA SystemsContentColumn";
                record["windowWidthPixels"] = windowBounds.Width;
                record["scale"] = scale;
                record["tier"] = OpenClawTray.Presentation.PageResponsiveLayout.TierFor(measured).ToString();
                record["image"] = name;
                record["qualification"] = requested == 520
                    ? "Shipped shell minimum (1000 DIP window), not a 520 DIP page. Narrow is mounted-geometry-only."
                    : "Measured real page-host width; the largest request is limited by the display work area.";
                OwnedWindowCapture.Save(run, Window(), name, FindById(run, "SystemsPageMarker")!);
                var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(run.ArtifactsDirectory, name))));
                Assert.True(capturedHashes.Add(hash), "Capture is byte-identical to another width.");
                record["sha256"] = hash;
                record["status"] = "captured";
                await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, Path.GetFileNameWithoutExtension(name) + "-layout.json"),
                    JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception error)
            {
                record["status"] = "failed";
                record["error"] = error.ToString();
                try
                {
                    var failedWindow = Window().Current.BoundingRectangle;
                    var failedHost = FindById(run, "SystemsPageScroll")!.Current.BoundingRectangle;
                    record["failureMeasuredHostWidthDip"] = failedHost.Width / scale;
                    record["failureWindowRectPx"] = new { failedWindow.X, failedWindow.Y, failedWindow.Width, failedWindow.Height };
                    record["failureTier"] = OpenClawTray.Presentation.PageResponsiveLayout.TierFor(failedHost.Width / scale).ToString();
                }
                catch (ElementNotAvailableException unavailable) { record["failureGeometryError"] = unavailable.Message; }
                try { OwnedWindowCapture.Save(run, Window(), $"failure-host-{requested}.png"); }
                catch (Exception captureError) { record["failureCaptureError"] = captureError.ToString(); }
            }
            finally
            {
                await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "responsive-manifest.json"),
                    JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
                // Recovery is scoped to this run's HWND and precedes the next independent state.
                if (paneToggled && FindById(run, "SettingsTogglePane") is { } toggle)
                    Invoke(toggle);
                Assert.True(SystemsSetWindowPos(hwnd, IntPtr.Zero, display.X, display.Y,
                    (int)Math.Round(baselineWidth), (int)Math.Round(baselineHeight), 0x0014));
            }
        }
        var failures = records.Count(record => Equals(record["status"], "failed"));
        await run.WriteReportAsync(failures == 0 ? "passed" : "failed");
        Assert.True(failures == 0, $"{failures} responsive captures failed. See {run.ArtifactsDirectory}\\responsive-manifest.json.");
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task SystemsDaily_InsetAndExpandedNativeFrames()
    {
        var app = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_APP")
            ?? throw new InvalidOperationException("Set OPENCLAW_GATEWAY_FIXTURE_APP to the current build.");
        foreach (var theme in new[] { "Light", "Dark" })
        {
            await using var run = await GatewayFixtureRun.StartAsync(app,
                Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS"),
                scenario: GatewayScenario.CreateSystems(empty: false), appTheme: theme);
            await run.InvokeAsync("app.navigate", new { page = "systems" });
            await WaitUiAsync(run, () => FindById(run, "SystemsPageScroll") is not null &&
                FindById(run, "SystemsRow_node:fixture-studio") is not null, "daily Systems inventory");
            var window = AppWindows(run).Single(w => w.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemsPageMarker")) is not null);
            var hwnd = new IntPtr(window.Current.NativeWindowHandle);
            var scale = SystemsWindowDpi(hwnd) / 96d;
            var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd),
                Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
            var windowPattern = (WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern);
            windowPattern.SetWindowVisualState(WindowVisualState.Normal);
            await WaitUiAsync(run, () => windowPattern.Current.WindowVisualState == WindowVisualState.Normal &&
                FindById(run, "SystemsPageScroll") is { Current.BoundingRectangle.Width: > 0 }, "normal Systems window");
            var records = new List<object>();
            async Task Capture(string state, params AutomationElement[] requiredVisible)
            {
                var name = $"{theme}-{state}.png";
                OwnedWindowCapture.Save(run, window, name, requiredVisible);
                var bounds = window.Current.BoundingRectangle;
                var viewport = FindById(run, "SystemsPageScroll")!.Current.BoundingRectangle;
                records.Add(new
                {
                    state, theme, run.AppProcessId, hwnd = hwnd.ToInt64(), scale,
                    windowDip = new { width = bounds.Width / scale, height = bounds.Height / scale },
                    viewportDip = new { width = viewport.Width / scale, height = viewport.Height / scale },
                    viewportTopRelativeToWindowDip = (viewport.Top - bounds.Top) / scale,
                    image = name,
                    sha256 = Convert.ToHexString(SHA256.HashData(
                        await File.ReadAllBytesAsync(Path.Combine(run.ArtifactsDirectory, name)))),
                    qualification = "Current isolated app with synthetic Gateway. No real node invocation or provider proof.",
                });
                await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "daily-frames.json"),
                    JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            }
            Invoke(FindById(run, "SettingsTogglePane")!);
            await WaitUiAsync(run, () => FindById(run, "SettingsSearch") is null or { Current.IsOffscreen: true },
                "Settings navigation pane collapsed");
            foreach (var width in new[] { 800, 1000 })
            {
                var requestedWidth = width * scale;
                Assert.True(requestedWidth <= work.Width, $"Display cannot contain the requested {width} DIP owned window.");
                Assert.True(SystemsSetWindowPos(hwnd, IntPtr.Zero, work.X, work.Y,
                    (int)Math.Round(requestedWidth), Math.Min(work.Height, (int)Math.Round(900 * scale)), 0x0014));
                var scroll = FindById(run, "SystemsPageScroll")!;
                var pattern = (ScrollPattern)scroll.GetCurrentPattern(ScrollPattern.Pattern);
                pattern.SetScrollPercent(ScrollPattern.NoScroll, 0);
                await WaitUiAsync(run, () => Math.Abs(window.Current.BoundingRectangle.Width / scale - width) <= 2 &&
                    FindById(run, "SystemsPageMarker") is { Current.IsOffscreen: false } &&
                    scroll.Current.BoundingRectangle.Top >=
                        FindById(run, "SettingsTogglePane")!.Current.BoundingRectangle.Bottom,
                    $"collapsed {width} DIP Systems content remains below navigation chrome");
                await Capture($"collapsed-{width}-top", FindById(run, "SystemsPageMarker")!);
                if (width == 1000)
                {
                    pattern.SetScrollPercent(ScrollPattern.NoScroll, 35);
                    await WaitUiAsync(run, () => pattern.Current.VerticalScrollPercent > 20 &&
                        FindById(run, "SystemsPageMarker") is { Current.IsOffscreen: true }, "collapsed Systems scrolled");
                    await Capture("collapsed-1000-scrolled");
                    pattern.SetScrollPercent(ScrollPattern.NoScroll, 0);
                }
            }
            var collapsedViewport = System.Windows.Rect.Empty;
            var previousCollapsedWidth = -1d;
            await WaitUiAsync(run, () =>
            {
                if (FindById(run, "SystemsPageScroll") is not { Current.IsOffscreen: false } scroll ||
                    FindById(run, "SystemsPageMarker") is not { Current.IsOffscreen: false } marker)
                {
                    previousCollapsedWidth = -1d;
                    return false;
                }
                Assert.Equal(run.AppProcessId, scroll.Current.ProcessId);
                Assert.Equal(run.AppProcessId, marker.Current.ProcessId);
                var viewport = scroll.Current.BoundingRectangle;
                var pattern = (ScrollPattern)scroll.GetCurrentPattern(ScrollPattern.Pattern);
                if (viewport.IsEmpty || viewport.Width <= 0 || viewport.Height <= 0 ||
                    !double.IsFinite(viewport.X) || !double.IsFinite(viewport.Y) ||
                    !double.IsFinite(viewport.Width) || !double.IsFinite(viewport.Height) ||
                    pattern.Current.VerticalScrollPercent is not (>= 0 and <= .1))
                {
                    previousCollapsedWidth = -1d;
                    return false;
                }
                var stable = Math.Abs(viewport.Width - previousCollapsedWidth) < .1;
                previousCollapsedWidth = viewport.Width;
                if (stable) collapsedViewport = viewport;
                return stable;
            }, "collapsed Systems viewport settles at the top after scroll reset");
            var collapsedTop = collapsedViewport.Top;
            var collapsedWidth = collapsedViewport.Width;
            var previousExpandedWidth = -1d;
            Invoke(FindById(run, "SettingsTogglePane")!);
            await WaitUiAsync(run, () =>
            {
                // An absent viewport during the pane transition is not settled geometry.
                if (FindById(run, "SystemsPageScroll") is not
                    { Current.BoundingRectangle.Width: > 0, Current.BoundingRectangle.Height: > 0 } scroll)
                {
                    previousExpandedWidth = -1d;
                    return false;
                }
                var viewport = scroll.Current.BoundingRectangle;
                var stable = Math.Abs(viewport.Width - previousExpandedWidth) < .1;
                previousExpandedWidth = viewport.Width;
                return stable && viewport.Width < collapsedWidth - 100 * scale &&
                    FindById(run, "SettingsSearch") is { Current.IsOffscreen: false } && viewport.Top < collapsedTop;
            },
                "expanding navigation removes the Systems scroll-only inset");
            await Capture("expanded-1000-top", FindById(run, "SystemsPageMarker")!);
            var chromeWidth = window.Current.BoundingRectangle.Width -
                FindById(run, "SystemsPageScroll")!.Current.BoundingRectangle.Width;
            var statsWindowWidth = 1000 * scale + chromeWidth;
            Assert.True(statsWindowWidth <= work.Width, "Display must contain a 1000 DIP Systems host and its native navigation pane.");
            Assert.True(SystemsSetWindowPos(hwnd, IntPtr.Zero, work.X, work.Y,
                (int)Math.Round(statsWindowWidth), Math.Min(work.Height, (int)Math.Round(900 * scale)), 0x0014));
            var previousStatsHostWidth = -1d;
            await WaitUiAsync(run, () =>
            {
                var hostWidth = FindById(run, "SystemsPageScroll")!.Current.BoundingRectangle.Width;
                var stable = Math.Abs(hostWidth - previousStatsHostWidth) < .1;
                previousStatsHostWidth = hostWidth;
                return stable && Math.Abs(window.Current.BoundingRectangle.Width - statsWindowWidth) <= 2;
            }, "native window and resource viewport finish their initial width allocation");
            var measuredStatsHostWidth = FindById(run, "SystemsPageScroll")!.Current.BoundingRectangle.Width / scale;
            if (Math.Abs(measuredStatsHostWidth - 1000) > 2)
            {
                statsWindowWidth += Math.Round((1000 - measuredStatsHostWidth) * scale);
                Assert.True(statsWindowWidth <= work.Width, "The measured host-width correction must still fit the display.");
                Assert.True(SystemsSetWindowPos(hwnd, IntPtr.Zero, work.X, work.Y,
                    (int)Math.Round(statsWindowWidth), Math.Min(work.Height, (int)Math.Round(900 * scale)), 0x0014));
            }
            try
            {
                await WaitUiAsync(run, () => Math.Abs(FindById(run, "SystemsPageScroll")!.Current.BoundingRectangle.Width / scale - 1000) <= 2,
                    "1000 DIP Systems page host for the resource-tile minimum");
            }
            catch (TimeoutException)
            {
                await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "tile-host-failure.json"),
                    JsonSerializer.Serialize(new { statsWindowWidth, chromeWidth, scale,
                        window = window.Current.BoundingRectangle,
                        viewport = FindById(run, "SystemsPageScroll")!.Current.BoundingRectangle,
                        work = new { work.Width, work.Height } }, new JsonSerializerOptions { WriteIndented = true }));
                OwnedWindowCapture.Save(run, window, $"{theme}-tile-host-failure.png");
                throw;
            }
            await run.InvokeAsync("app.navigate", new { page = "systems:node:fixture-studio" });
            var gatewayDisclosure = (ExpandCollapsePattern)FindById(run, "SystemsRow_gateway")!
                .GetCurrentPattern(ExpandCollapsePattern.Pattern);
            if (gatewayDisclosure.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
                gatewayDisclosure.Collapse();
            var studioDisclosure = (ExpandCollapsePattern)FindById(run, "SystemsRow_node:fixture-studio")!
                .GetCurrentPattern(ExpandCollapsePattern.Pattern);
            await WaitUiAsync(run, () => gatewayDisclosure.Current.ExpandCollapseState == ExpandCollapseState.Collapsed &&
                studioDisclosure.Current.ExpandCollapseState == ExpandCollapseState.Expanded &&
                FindById(run, "SystemsSelectedName")?.Current.Name == "Fixture Studio PC",
                "Studio resource proof has one explicitly expanded native machine");
            AutomationElement? Studio(string id) => window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemsRow_node:fixture-studio"))?.FindFirst(
                    TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            await WaitUiAsync(run, () => Studio("SystemsStat_cpu") is not null &&
                Studio("SystemsStat_memory") is not null && Studio("SystemsStat_disk") is not null, "reported Studio resource tiles");
            var stats = Studio("SystemsStat_cpu")!;
            var statsScroll = FindById(run, "SystemsPageScroll")!;
            var statsPattern = (ScrollPattern)statsScroll.GetCurrentPattern(ScrollPattern.Pattern);
            var viewportBounds = statsScroll.Current.BoundingRectangle;
            var range = viewportBounds.Height * (100 / statsPattern.Current.VerticalViewSize - 1);
            Assert.True(range > 0, "Expanded machine details must remain reachable through ordinary scrolling.");
            var offset = statsPattern.Current.VerticalScrollPercent / 100 * range;
            var target = offset + stats.Current.BoundingRectangle.Top - viewportBounds.Top - 16;
            statsPattern.SetScrollPercent(ScrollPattern.NoScroll, Math.Clamp(target / range * 100, 0, 100));
            AutomationElement? cpuTile = null, memoryTile = null, diskTile = null;
            try
            {
                await WaitUiAsync(run, () =>
                {
                    cpuTile = Studio("SystemsStat_cpu");
                    memoryTile = Studio("SystemsStat_memory");
                    diskTile = Studio("SystemsStat_disk");
                    return cpuTile is { Current.IsOffscreen: false } && memoryTile is { Current.IsOffscreen: false } &&
                        diskTile is { Current.IsOffscreen: false } &&
                        cpuTile.Current.BoundingRectangle.Width / scale >= 160 &&
                        memoryTile.Current.BoundingRectangle.Width / scale >= 160 &&
                        diskTile.Current.BoundingRectangle.Width / scale >= 160;
                },
                    "Studio resource tiles visible with the independent 160 DIP minimum");
            }
            catch (TimeoutException)
            {
                await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "tile-visibility-failure.json"),
                    JsonSerializer.Serialize(new { scale, viewport = statsScroll.Current.BoundingRectangle,
                        scrollPercent = statsPattern.Current.VerticalScrollPercent,
                        viewSize = statsPattern.Current.VerticalViewSize,
                        tiles = new[] { "SystemsStat_cpu", "SystemsStat_memory", "SystemsStat_disk" }.Select(id => new
                        { id, bounds = Studio(id)?.Current.BoundingRectangle, offscreen = Studio(id)?.Current.IsOffscreen }) },
                        new JsonSerializerOptions { WriteIndented = true }));
                OwnedWindowCapture.Save(run, window, $"{theme}-tile-visibility-failure.png");
                throw;
            }
            Assert.NotNull(cpuTile);
            Assert.NotNull(memoryTile);
            Assert.NotNull(diskTile);
            await Capture("expanded-1000-host-stat-tiles", cpuTile, memoryTile, diskTile);
            gatewayDisclosure.Expand();
            studioDisclosure.Collapse();
            studioDisclosure.Expand();
            await WaitUiAsync(run, () => gatewayDisclosure.Current.ExpandCollapseState == ExpandCollapseState.Expanded &&
                studioDisclosure.Current.ExpandCollapseState == ExpandCollapseState.Expanded &&
                FindById(run, "SystemsSelectedName")?.Current.Name == "Fixture Studio PC",
                "independent native Gateway and Studio disclosures both remain expanded");
            Assert.Equal(ExpandCollapseState.Expanded, ((ExpandCollapsePattern)FindById(run, "SystemsRow_gateway")!
                .GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState);
            Assert.Equal(ExpandCollapseState.Expanded, ((ExpandCollapsePattern)FindById(run, "SystemsRow_node:fixture-studio")!
                .GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState);
            var twoRowRange = viewportBounds.Height * (100 / statsPattern.Current.VerticalViewSize - 1);
            var twoRowOffset = statsPattern.Current.VerticalScrollPercent / 100 * twoRowRange;
            var twoRowTarget = twoRowOffset + FindById(run, "SystemsRow_node:fixture-studio")!.Current.BoundingRectangle.Top -
                viewportBounds.Top - viewportBounds.Height / 3;
            statsPattern.SetScrollPercent(ScrollPattern.NoScroll, Math.Clamp(twoRowTarget / twoRowRange * 100, 0, 100));
            await WaitUiAsync(run, () => FindById(run, "SystemsSelectedName") is { Current.IsOffscreen: false } &&
                FindById(run, "SystemsRow_gateway") is { Current.IsOffscreen: false },
                "both expanded machine rows occupy the viewport");
            await Capture("two-open-rows", FindById(run, "SystemsSelectedName")!);
            statsPattern.SetScrollPercent(ScrollPattern.NoScroll, 0);
            await WaitUiAsync(run, () => FindById(run, "SystemsIssuesTile") is { Current.IsOffscreen: false, Current.IsEnabled: true },
                "Issues tile retains access to badged rows");
            Invoke(FindById(run, "SystemsIssuesTile")!);
            await WaitUiAsync(run, () => window.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                .Cast<AutomationElement>().Any(element =>
                    element.Current.AutomationId.StartsWith("SystemsPairing_", StringComparison.Ordinal) &&
                    !element.Current.IsOffscreen), "Issues jump reveals the pending pairing row");
            Assert.Null(FindById(run, "SystemsIssuesView"));
            Assert.NotNull(FindById(run, "SystemsRow_node:fixture-studio"));
            await Capture("issues-jump-in-full-inventory");
            statsPattern.SetScrollPercent(ScrollPattern.NoScroll, 0);
            await WaitUiAsync(run, () => FindById(run, "SystemsRefreshButton") is { Current.IsOffscreen: false },
                "inventory refresh remains reachable after Issues jump");
            run.Gateway.SetSystemsInventoryUnavailable(true);
            Invoke(FindById(run, "SystemsRefreshButton")!);
            await WaitUiAsync(run, () => FindById(run, "SystemsRetry") is { Current.IsOffscreen: false } &&
                FindById(run, "SystemsInventoryConnection") is { Current.IsOffscreen: false },
                "inventory transport failure exposes Retry and Connection");
            Assert.Contains(run.Gateway.Requests, request => request.Method == "environments.list" && request.Outcome == "error:UNAVAILABLE");
            await Capture("inventory-error-recovery", FindById(run, "SystemsRetry")!, FindById(run, "SystemsInventoryConnection")!);
            run.Gateway.SetSystemsInventoryUnavailable(false);
            Invoke(FindById(run, "SystemsRetry")!);
            await WaitUiAsync(run, () => FindById(run, "SystemsRetry") is null &&
                FindById(run, "SystemsRow_node:fixture-studio") is not null, "successful retry restores inventory");
            await run.Gateway.DisposeAsync();
            await WaitUiAsync(run, () => FindById(run, "SystemsDisconnectedBanner") is not null &&
                FindById(run, "SystemsDisconnectedConnection") is not null, "disconnected last-known Systems state");
            var disconnectedGatewayDisclosure = (ExpandCollapsePattern)FindById(run, "SystemsRow_gateway")!
                .GetCurrentPattern(ExpandCollapsePattern.Pattern);
            var disconnectedStudioDisclosure = (ExpandCollapsePattern)FindById(run, "SystemsRow_node:fixture-studio")!
                .GetCurrentPattern(ExpandCollapsePattern.Pattern);
            disconnectedGatewayDisclosure.Collapse();
            disconnectedStudioDisclosure.Collapse();
            var disconnectedScroll = (ScrollPattern)FindById(run, "SystemsPageScroll")!
                .GetCurrentPattern(ScrollPattern.Pattern);
            disconnectedScroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
            var inventoryIds = new[] { "SystemsRow_gateway", "SystemsRow_node:fixture-studio",
                "SystemsRow_node:fixture-macbook", "SystemsRow_node:fixture-pi" };
            await WaitUiAsync(run, () => FindById(run, "SystemsDisconnectedConnection") is { Current.IsOffscreen: false } &&
                inventoryIds.All(id => FindById(run, id) is { Current.IsOffscreen: false } row &&
                    ((ExpandCollapsePattern)row.GetCurrentPattern(ExpandCollapsePattern.Pattern))
                        .Current.ExpandCollapseState == ExpandCollapseState.Collapsed &&
                    row.Current.BoundingRectangle.Height / scale is > 20 and < 120 &&
                    row.Current.Name.Contains("Last known", StringComparison.Ordinal)),
                "settled disconnected inventory retains all four collapsed last-known machines");
            await Capture("disconnected-last-known",
                inventoryIds.Select(id => FindById(run, id)!).Prepend(FindById(run, "SystemsDisconnectedConnection")!).ToArray());
            disconnectedGatewayDisclosure.Expand();
            var knownGateway = FindById(run, "SystemsRow_gateway")!;
            AutomationElement? GatewayContent(string id) => knownGateway.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            await WaitUiAsync(run, () => GatewayContent("SystemsCapabilitiesHeading") is { Current.BoundingRectangle.Height: > 0 } &&
                GatewayContent("SystemsStatisticsHeading") is { Current.BoundingRectangle.Height: > 0 } &&
                GatewayContent("SystemsStatsSampled") is { Current.BoundingRectangle.Height: > 0 } &&
                knownGateway.FindAll(TreeScope.Descendants, new PropertyCondition(
                    AutomationElement.AutomationIdProperty, "SystemsStatLastKnown")).Count == 5,
                "expanded last-known Gateway retains capabilities, resource heading, sample age and all five labelled metrics");
            var knownScroll = FindById(run, "SystemsPageScroll")!;
            var knownPattern = (ScrollPattern)knownScroll.GetCurrentPattern(ScrollPattern.Pattern);
            var knownViewport = knownScroll.Current.BoundingRectangle;
            var knownRange = knownViewport.Height * (100 / knownPattern.Current.VerticalViewSize - 1);
            var knownOffset = knownPattern.Current.VerticalScrollPercent / 100 * knownRange;
            var knownTarget = knownOffset + knownGateway.Current.BoundingRectangle.Top - knownViewport.Top - 16 * scale;
            knownPattern.SetScrollPercent(ScrollPattern.NoScroll, Math.Clamp(knownTarget / knownRange * 100, 0, 100));
            await WaitUiAsync(run, () => GatewayContent("SystemsCapabilitiesHeading") is { Current.IsOffscreen: false } &&
                GatewayContent("SystemsStatisticsHeading") is { Current.IsOffscreen: false } &&
                GatewayContent("SystemsStatsSampled") is { Current.IsOffscreen: false } &&
                GatewayContent("SystemsStat_disk:/data") is { Current.IsOffscreen: false },
                "last-known expanded resource heading, report age and trailing Disk are visible together");
            await Capture("disconnected-expanded-resources", GatewayContent("SystemsCapabilitiesHeading")!,
                GatewayContent("SystemsStatisticsHeading")!, GatewayContent("SystemsStatsSampled")!,
                GatewayContent("SystemsStat_disk:/data")!);
            await run.WriteReportAsync("passed");
            output.WriteLine($"{theme} daily Systems frames: {run.ArtifactsDirectory}");
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint SystemsWindowDpi(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemsNativeBounds { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static extern bool SystemsGetWindowRect(IntPtr window, out SystemsNativeBounds bounds);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    private static extern bool SystemsSetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task SystemsDesign_RealAppLightAndDark()
    {
        var app = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_APP")
            ?? throw new InvalidOperationException("Set OPENCLAW_GATEWAY_FIXTURE_APP to the current build.");
        foreach (var theme in new[] { "Light", "Dark" })
        {
            await using var run = await GatewayFixtureRun.StartAsync(app,
                Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS"),
                scenario: GatewayScenario.CreateSystems(empty: false), appTheme: theme);
            output.WriteLine($"{theme}: {run.ArtifactsDirectory}");
            await run.InvokeAsync("app.navigate", new { page = "systems" });
            await WaitUiAsync(run, () => FindById(run, "SystemsDevicesAndGatewayHeading") is not null, "populated Systems page");
            AutomationElement Window() => AppWindows(run).Single(w => w.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemsPageMarker")) is not null);
            async Task Capture(string name)
            {
                await run.WaitForAsync(() =>
                {
                    try
                    {
                        OwnedWindowCapture.Save(run, Window(), $"{theme}-{name}.png");
                        return Task.FromResult(true);
                    }
                    catch (Xunit.Sdk.XunitException ex) when (ex.Message.StartsWith("Rejected blank native content", StringComparison.Ordinal))
                    {
                        return Task.FromResult(false);
                    }
                }, "painted native frame: " + name);
            }
            if (Environment.GetEnvironmentVariable("OPENCLAW_SYSTEMS_DETAILS_ONLY") != "1")
            {
                await Capture("loaded");
                Invoke(FindById(run, "SystemsAddSystem")!);
                await WaitUiAsync(run, () => FindText(run, "1. Install a supported OpenClaw node app") is not null, "Add system dialog");
                await Capture("add");
                var close = FindButton(run, "Close")!;
                Invoke(close);
                await WaitUiAsync(run, () => FindText(run, "1. Install a supported OpenClaw node app") is null, "Add dialog dismissed");
            }

            await run.InvokeAsync("app.navigate", new { page = "systems:node:fixture-studio" });
            await WaitUiAsync(run, () => FindById(run, "SystemsCommandBar") is { Current.IsOffscreen: false }, "expanded machine command bar");
            await Capture("expanded");
            var bar = FindById(run, "SystemsCommandBar")!;
            var more = bar.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "MoreButton"));
            Assert.NotNull(more);
            Invoke(more);
            await WaitUiAsync(run, () => FindById(run, "SystemsForgetNode") is { Current.IsOffscreen: false }, "Forget overflow command");
            Invoke(FindById(run, "SystemsForgetNode")!);
            await WaitUiAsync(run, () => FindText(run, "Forget Fixture Studio PC?") is not null, "Forget node dialog");
            await Capture("forget");
            Invoke(FindButton(run, "Cancel")!);
            await WaitUiAsync(run, () => FindText(run, "Forget Fixture Studio PC?") is null, "Forget cancelled without mutation");
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "node.pair.remove");
            await run.WriteReportAsync("passed");
        }
    }
}
