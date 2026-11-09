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
    public async Task DashboardsPinnedHost_RecordsBothThemesAcrossPaneAndScrollStates()
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
                    await WaitUiAsync(run, () => FindById(run, $"DashboardCardFresh_{FixtureBoards.OpsSessionKey}") is not null,
                        "pinned-host gallery ready");
                    var hub = AppWindows(run).Single(window => window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "DashboardsTitle")) is not null);
                    var hwnd = new IntPtr(hub.Current.NativeWindowHandle);
                    ((WindowPattern)hub.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Normal);
                    var scale = DashboardWindowDpi(hwnd) / 96d;
                    DashboardResize(hwnd, (int)Math.Round(1000 * scale), (int)Math.Round(900 * scale));
                    await WaitUiAsync(run, () => Math.Abs(DashboardRect(hwnd).Width / scale - 1000) <= 2, "1000-DIP pinned window");
                    bool IsWithin(AutomationElement element, AutomationElement ancestor)
                    {
                        for (AutomationElement? current = element; current is not null;
                             current = TreeWalker.RawViewWalker.GetParent(current))
                            if (current.GetRuntimeId().SequenceEqual(ancestor.GetRuntimeId()))
                                return true;
                        return false;
                    }
                    void AssertToggleHit(string id)
                    {
                        var button = FindById(run, id)
                            ?? throw new InvalidOperationException($"Pinned pane button {id} is missing.");
                        Assert.False(button.Current.IsOffscreen);
                        Assert.True(button.Current.IsEnabled);
                        var bounds = button.Current.BoundingRectangle;
                        Assert.True(bounds.Width > 0 && bounds.Height > 0);
                        var point = new System.Windows.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                        var hit = AutomationElement.FromPoint(point);
                        var belongsToButton = IsWithin(hit, button);
                        Assert.True(belongsToButton,
                            $"Pinned pane button {id} is occluded at its center by {hit.Current.AutomationId}.");
                    }
                    foreach (var board in new[] { false, true })
                    {
                        if (board)
                        {
                            Invoke(FindById(run, $"DashboardCardFresh_{FixtureBoards.OpsSessionKey}")!);
                            await WaitUiAsync(run, () => FindById(run, "DashboardView") is not null, "pinned board ready");
                        }
                        var surface = board ? "board" : "gallery";
                        double? openHostTop = null;
                        foreach (var paneState in new[] { "open", "collapsed", "reopened" })
                        {
                            var collapsed = paneState == "collapsed";
                            if (collapsed)
                            {
                                AssertToggleHit("WorkspaceTogglePane");
                                Invoke(FindById(run, "WorkspaceTogglePane")
                                    ?? throw new InvalidOperationException("Workspace collapse control is missing."));
                            }
                            else if (FindById(run, "WorkspaceReopenPane") is { Current.IsOffscreen: false } reopen)
                            {
                                AssertToggleHit("WorkspaceReopenPane");
                                Invoke(reopen);
                            }
                            await WaitUiAsync(run, () => FindById(run, collapsed ? "WorkspaceReopenPane" : "WorkspaceTogglePane")
                                is { Current.IsOffscreen: false }, "pinned pane state");
                            double? previousHostWidth = null;
                            var lastHostRead = DateTimeOffset.MinValue;
                            await WaitUiAsync(run, () =>
                            {
                                if ((DateTimeOffset.UtcNow - lastHostRead).TotalMilliseconds < 300)
                                    return false;
                                lastHostRead = DateTimeOffset.UtcNow;
                                if (!collapsed &&
                                    (FindById(run, "WorkspacePagesCustomize") is not { Current.IsOffscreen: false } ||
                                     FindById(run, "WorkspaceSessionsAdd") is not { Current.IsOffscreen: false }))
                                    return false;
                                var current = FindById(run, "DashboardsContentHost")!.Current.BoundingRectangle.Width / scale;
                                var settled = current > 0 && previousHostWidth is { } before && Math.Abs(before - current) < 0.5;
                                previousHostWidth = current;
                                return settled;
                            }, "settled pinned host and visible expanded pane actions");
                            var name = $"dashboards-{theme}-pinned-{surface}-{paneState}.png";
                            await WaitForDashboardPixelsAsync(run, hub, name,
                                board ? ["99.97", "Uptime", "Tickets closed"] : ["Dashboards"]);
                            var host = FindById(run, "DashboardsContentHost")!.Current.BoundingRectangle;
                            var viewport = FindById(run, board ? "DashboardPageScroll" : "DashboardsGalleryScroll")!.Current.BoundingRectangle;
                            if (!collapsed)
                                openHostTop = host.Top;
                            var measuredInset = (viewport.Top - host.Top) / scale;
                            var measuredHostMargin = (host.Top - openHostTop!.Value) / scale;
                            Assert.InRange(Math.Abs(measuredInset - (collapsed ? 56 : 0)), 0, 1);
                            Assert.InRange(Math.Abs(measuredHostMargin), 0, 1);
                            AssertToggleHit(collapsed ? "WorkspaceReopenPane" : "WorkspaceTogglePane");
                            if (collapsed)
                            {
                                var reopen = FindById(run, "WorkspaceReopenPane")!;
                                var toggleBounds = reopen.Current.BoundingRectangle;
                                foreach (var id in new[] { "SettingsBack", "SettingsForward" })
                                {
                                    var history = FindById(run, id)!;
                                    Assert.True(toggleBounds.Right <= history.Current.BoundingRectangle.Left,
                                        $"Pinned reopen overlaps {id}.");
                                }
                                reopen.SetFocus();
                                await WaitUiAsync(run, () => reopen.Current.HasKeyboardFocus, "pinned reopen keyboard focus");
                                foreach (var id in new[] { "SettingsBack", "SettingsForward" })
                                {
                                    var history = FindById(run, id)!;
                                    if (!history.Current.IsEnabled)
                                        continue;
                                    Send(run, hub, "{TAB}");
                                    await WaitUiAsync(run, () => history.Current.HasKeyboardFocus,
                                        $"pinned toggle/history tab order: {id}");
                                }
                                Send(run, hub, "{TAB}");
                                await WaitUiAsync(run, () => IsWithin(AutomationElement.FocusedElement,
                                    FindById(run, "DashboardsContentHost")!), "pinned history-to-page tab order");
                                if (FindById(run, "WorkspaceEmbeddedOperationFeedback") is { Current.IsOffscreen: false } feedback)
                                    Assert.True(toggleBounds.Bottom <= feedback.Current.BoundingRectangle.Top,
                                        "Pinned operation feedback overlaps the reopen button.");
                            }
                            if (!collapsed)
                                continue;
                            var scroll = FindById(run, board ? "DashboardPageScroll" : "DashboardsGalleryScroll")!;
                            var pattern = (ScrollPattern)scroll.GetCurrentPattern(ScrollPattern.Pattern);
                            Assert.True(pattern.Current.VerticallyScrollable);
                            pattern.SetScrollPercent(ScrollPattern.NoScroll, 25);
                            await WaitUiAsync(run, () => pattern.Current.VerticalScrollPercent > 0, "partial pinned scroll");
                            var scrolled = name.Replace(".png", "-scrolled.png", StringComparison.Ordinal);
                            await DismissDashboardContainmentNoticeAsync(run, hub, scrolled);
                            OwnedWindowCapture.Save(run, hub, scrolled);
                            pattern.SetScrollPercent(ScrollPattern.NoScroll, 0);
                            await WaitUiAsync(run, () => pattern.Current.VerticalScrollPercent <= 0.1, "restore pinned scroll");
                        }
                        if (board)
                        {
                            ((SelectionItemPattern)(FindById(run, "DashboardTab_mixed")
                                ?? throw new InvalidOperationException("Mixed content tab is missing."))
                                .GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                            await WaitUiAsync(run, () => FindById(run, "BoardWebsiteOpen_mixed-website") is not null,
                                "mixed report and website ready");
                            var reportBounds = FindById(run, "BoardWidget_mixed-report")!.Current.BoundingRectangle;
                            var websiteBounds = FindById(run, "BoardWidget_mixed-website")!.Current.BoundingRectangle;
                            Assert.True(reportBounds.Right <= websiteBounds.Left);
                            Assert.InRange(Math.Abs(reportBounds.Top - websiteBounds.Top) / scale, 0, 1);
                            Assert.InRange(Math.Abs(reportBounds.Bottom - websiteBounds.Bottom) / scale, 0, 1);
                            await WaitForDashboardPixelsAsync(run, hub, $"dashboards-{theme}-pinned-mixed.png",
                                ["A saved report line", "status.example.com"]);
                        }
                    }
                }, scenario: GatewayScenario.CreateDashboards());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_FIXTURE_APP_THEME", previousTheme);
        }
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task DashboardsBackdropSpansGalleryAndBoardInBothThemes()
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
                    await WaitUiAsync(run, () => FindById(run, $"DashboardCardFresh_{FixtureBoards.OpsSessionKey}") is not null,
                        "backdrop gallery loaded");
                    var hub = AppWindows(run).Single(window => window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "DashboardsTitle")) is not null);
                    var window = (WindowPattern)hub.GetCurrentPattern(WindowPattern.Pattern);
                    var hwnd = new IntPtr(hub.Current.NativeWindowHandle);
                    var scale = DashboardWindowDpi(hwnd) / 96d;
                    window.SetWindowVisualState(WindowVisualState.Maximized);
                    await WaitUiAsync(run, () => window.Current.WindowVisualState == WindowVisualState.Maximized, "backdrop work area");
                    var workArea = DashboardRect(hwnd);
                    window.SetWindowVisualState(WindowVisualState.Normal);
                    await WaitUiAsync(run, () => window.Current.WindowVisualState == WindowVisualState.Normal, "backdrop normal shell");
                    double HostWidth() => (FindById(run, "DashboardsContentHost")
                        ?? throw new InvalidOperationException("Named backdrop host is missing.")).Current.BoundingRectangle.Width / scale;
                    async Task StableHost()
                    {
                        double? previous = null;
                        var lastRead = DateTimeOffset.MinValue;
                        await WaitUiAsync(run, () =>
                        {
                            if ((DateTimeOffset.UtcNow - lastRead).TotalMilliseconds < 300)
                                return false;
                            var current = HostWidth();
                            var stable = current > 0 && previous is { } before &&
                                OpenClawTray.Presentation.PageResponsiveLayout.Snap(current) ==
                                OpenClawTray.Presentation.PageResponsiveLayout.Snap(before);
                            previous = current;
                            lastRead = DateTimeOffset.UtcNow;
                            return stable;
                        }, "stable backdrop host");
                    }
                    var states = new List<object>();
                    async Task Capture(string surface, bool minimum = false)
                    {
                        var name = $"dashboards-{theme}-backdrop-{surface}-{(minimum ? "minimum" : "wide")}.png";
                        try
                        {
                            await DismissDashboardContainmentNoticeAsync(run, hub, name);
                            await StableHost();
                            var before = DashboardRect(hwnd);
                            var chrome = before.Width - HostWidth() * scale;
                            var target = (int)Math.Round(minimum ? 1000 * scale : Math.Min(workArea.Width, 1400 * scale + chrome));
                            var height = (int)Math.Round(Math.Min(workArea.Height, 900 * scale));
                            var targets = new List<int>();
                            for (var correction = 0; correction <= 2; correction++)
                            {
                                targets.Add(target);
                                DashboardResize(hwnd, target, height);
                                await StableHost();
                                if (minimum || Math.Abs(HostWidth() - 1400) <= 2)
                                    break;
                                if (correction < 2)
                                    target = Math.Clamp(target + (int)Math.Round((1400 - HostWidth()) * scale),
                                        (int)Math.Round(1000 * scale), workArea.Width);
                            }
                            if (surface == "board")
                                await WaitForDashboardPixelsAsync(run, hub, name, ["99.97", "Uptime", "Tickets closed"]);
                            else
                                OwnedWindowCapture.Save(run, hub, name);
                            var rect = DashboardRect(hwnd);
                            var host = HostWidth();
                            var displayCapped = !minimum && DashboardBackdropIsDisplayCapped(host, target, workArea.Width);
                            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                                await File.ReadAllBytesAsync(Path.Combine(run.ArtifactsDirectory, name))));
                            states.Add(new { theme, surface, minimum, image = name, captured = true, measuredHostWidth = host,
                                requestedHostWidth = minimum ? (double?)null : 1400, displayCapped, workAreaWidthPx = workArea.Width,
                                calculatedContentWidth = OpenClawTray.Presentation.PageResponsiveLayout.ContentWidth(host),
                                tier = OpenClawTray.Presentation.PageResponsiveLayout.TierFor(host).ToString(),
                                windowWidthPx = rect.Width, windowHeightPx = rect.Height,
                                windowWidthDip = rect.Width / scale, windowHeightDip = rect.Height / scale,
                                scale, targets, sha256 = sha });
                            await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "backdrop-captures.json"),
                                JsonSerializer.Serialize(states));
                            Assert.InRange(Math.Abs(rect.Width - target), 0, 1);
                            Assert.InRange(Math.Abs(rect.Height - height), 0, 1);
                            Assert.True(host > 0);
                            Assert.Equal(minimum ? OpenClawTray.Presentation.PageWidthTier.Medium : OpenClawTray.Presentation.PageWidthTier.Wide,
                                OpenClawTray.Presentation.PageResponsiveLayout.TierFor(host));
                            if (!minimum && !displayCapped)
                                Assert.InRange(Math.Abs(host - 1400), 0, 2);
                        }
                        catch (Exception error)
                        {
                            string? captureError = null;
                            try { OwnedWindowCapture.Save(run, hub, name.Replace(".png", "-failed.png")); }
                            catch (Exception failure) { captureError = failure.ToString(); }
                            states.Add(new { theme, surface, minimum, captured = false, error = error.ToString(), captureError });
                            await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "backdrop-captures.json"),
                                JsonSerializer.Serialize(states));
                        }
                    }
                    async Task CaptureScroll(string surface)
                    {
                        var before = DashboardRect(hwnd);
                        DashboardResize(hwnd, before.Width, (int)Math.Round(Math.Min(workArea.Height, 600 * scale)));
                        await StableHost();
                        var scrollId = surface == "gallery" ? "DashboardsGalleryScroll" : "DashboardPageScroll";
                        ScrollPattern? scrolling = null;
                        await WaitUiAsync(run, () =>
                        {
                            var element = FindById(run, scrollId);
                            if (element is null || !element.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern))
                                return false;
                            scrolling = (ScrollPattern)pattern;
                            return scrolling.Current.VerticallyScrollable;
                        }, surface + " page scrollable");
                        scrolling!.SetScrollPercent(ScrollPattern.NoScroll, 0);
                        await WaitUiAsync(run, () => scrolling.Current.VerticalScrollPercent <= 0.1, surface + " scroll top");
                        var titleTop = FindById(run, "DashboardsTitle")!.Current.BoundingRectangle.Top;
                        var topName = $"dashboards-{theme}-viewport-{surface}-top.png";
                        OwnedWindowCapture.Save(run, hub, topName);
                        scrolling.Scroll(ScrollAmount.NoAmount, ScrollAmount.SmallIncrement);
                        await WaitUiAsync(run, () => scrolling.Current.VerticalScrollPercent > 0 &&
                            FindById(run, "DashboardsTitle")!.Current.BoundingRectangle.Top < titleTop - scale,
                            surface + " mast scrolls with page content");
                        var scrolledName = $"dashboards-{theme}-viewport-{surface}-scrolled.png";
                        OwnedWindowCapture.Save(run, hub, scrolledName);
                        await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, $"viewport-{surface}.json"),
                            JsonSerializer.Serialize(new
                            {
                                surface, theme, topName, scrolledName, beforePercent = 0,
                                afterPercent = scrolling.Current.VerticalScrollPercent,
                                titleBeforeTop = titleTop,
                                titleAfterTop = FindById(run, "DashboardsTitle")!.Current.BoundingRectangle.Top,
                            }));
                        scrolling.SetScrollPercent(ScrollPattern.NoScroll, 0);
                        await WaitUiAsync(run, () => scrolling.Current.VerticalScrollPercent <= 0.1, surface + " restore top");
                    }
                    await Capture("gallery");
                    if (theme == "Light")
                        await CaptureScroll("gallery");
                    if (theme == "Light")
                        await Capture("gallery", minimum: true);
                    Invoke(FindById(run, $"DashboardCardFresh_{FixtureBoards.OpsSessionKey}")!);
                    await Capture("board");
                    if (theme == "Light")
                        await CaptureScroll("board");
                    using var evidence = JsonDocument.Parse(JsonSerializer.Serialize(states));
                    Assert.All(evidence.RootElement.EnumerateArray(), state =>
                        Assert.True(state.GetProperty("captured").GetBoolean(), state.GetRawText()));
                }, scenario: GatewayScenario.CreateDashboards());
            }
        }
        finally { Environment.SetEnvironmentVariable("OPENCLAW_FIXTURE_APP_THEME", previousTheme); }
    }

    private static bool DashboardBackdropIsDisplayCapped(double measuredHostWidth, int targetWindowWidth, int workAreaWidth) =>
        measuredHostWidth < 1398 && targetWindowWidth == workAreaWidth;

    [Theory]
    [InlineData(1386.286, 2907, 2907, true)]
    [InlineData(1400, 2907, 2907, false)]
    [InlineData(1386.286, 2897, 2907, false)]
    public void DashboardBackdropCapture_DistinguishesDisplayCapFromReachableHost(double host, int target, int workArea, bool capped) =>
        Assert.Equal(capped, DashboardBackdropIsDisplayCapped(host, target, workArea));

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task DashboardsResponsiveLightWidthsRenderTheMeasuredPageHost()
    {
        var previousTheme = Environment.GetEnvironmentVariable("OPENCLAW_FIXTURE_APP_THEME");
        Environment.SetEnvironmentVariable("OPENCLAW_FIXTURE_APP_THEME", "Light");
        try
        {
            await WithAppAsync(async run =>
            {
                await run.InvokeAsync("app.navigate", new { page = "dashboards" });
                await WaitUiAsync(run, () => FindById(run, $"DashboardCardFresh_{FixtureBoards.OpsSessionKey}") is not null,
                    "responsive dashboard gallery loaded");
                var hub = AppWindows(run).Single(window => window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "DashboardsTitle")) is not null);
                var window = (WindowPattern)hub.GetCurrentPattern(WindowPattern.Pattern);
                window.SetWindowVisualState(WindowVisualState.Normal);
                await WaitUiAsync(run, () => window.Current.WindowVisualState == WindowVisualState.Normal, "normal shell state");
                var scale = DashboardWindowDpi(new IntPtr(hub.Current.NativeWindowHandle)) / 96d;
                var hwnd = new IntPtr(hub.Current.NativeWindowHandle);
                var baseline = DashboardRect(hwnd);
                window.SetWindowVisualState(WindowVisualState.Maximized);
                await WaitUiAsync(run, () => window.Current.WindowVisualState == WindowVisualState.Maximized, "maximized work area");
                var workArea = DashboardRect(hwnd);
                window.SetWindowVisualState(WindowVisualState.Normal);
                await WaitUiAsync(run, () => window.Current.WindowVisualState == WindowVisualState.Normal, "restored shell state");
                double HostWidth()
                {
                    var host = FindById(run, "DashboardsContentHost")
                        ?? throw new InvalidOperationException("The measured Reactor content host is missing from UIA.");
                    var bounds = host.Current.BoundingRectangle;
                    if (bounds.Width <= 0)
                        throw new InvalidOperationException("The Reactor content host has no positive UIA width.");
                    return bounds.Width / scale;
                }
                var preflightHost = FindById(run, "DashboardsContentHost");
                OwnedWindowCapture.Save(run, hub, "dashboards-Light-responsive-baseline.png");
                await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "responsive-baseline.json"),
                    JsonSerializer.Serialize(new
                    {
                        hostClass = preflightHost?.Current.ClassName,
                        automationId = preflightHost?.Current.AutomationId,
                        hostRectPx = preflightHost?.Current.BoundingRectangle,
                        hostDip = preflightHost is null ? (double?)null : preflightHost.Current.BoundingRectangle.Width / scale,
                        scale,
                    }));
                if (preflightHost is null || preflightHost.Current.BoundingRectangle.Width <= 0)
                {
                    await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "responsive-uia-preflight-dump.json"),
                        JsonSerializer.Serialize(DashboardUiaContext(preflightHost ?? hub, run.AppProcessId)));
                    throw new InvalidOperationException("The named page-host peer is missing or zero-width. Baseline frame and UIA context saved.");
                }
                async Task WaitStableHost(string description, double? priorHost = null, int direction = 0)
                {
                    double? previous = null;
                    double? previousNav = null;
                    var lastRead = DateTimeOffset.MinValue;
                    await WaitUiAsync(run, () =>
                    {
                        if ((DateTimeOffset.UtcNow - lastRead).TotalMilliseconds < 300)
                            return false;
                        var value = HostWidth();
                        var navElement = FindById(run, "WorkspacePinnedPage:dashboards")
                            ?? throw new InvalidOperationException("The persistent dashboard pin stability anchor is missing.");
                        var nav = navElement.Current.BoundingRectangle.Width;
                        var moved = priorHost is null || direction == 0 ||
                            (direction > 0 ? value > priorHost.Value : value < priorHost.Value);
                        var stable = value > 0 && moved && previous is { } before && previousNav is { } beforeNav &&
                            OpenClawTray.Presentation.PageResponsiveLayout.Snap(before) == OpenClawTray.Presentation.PageResponsiveLayout.Snap(value) &&
                            OpenClawTray.Presentation.PageResponsiveLayout.Snap(beforeNav) == OpenClawTray.Presentation.PageResponsiveLayout.Snap(nav);
                        previous = value;
                        previousNav = nav;
                        lastRead = DateTimeOffset.UtcNow;
                        return stable;
                    }, description);
                }
                var observations = new List<object>();
                var capturedBounds = new List<(double Host, int WindowWidth, int WindowHeight, string Sha)>();
                foreach (var (name, width, collapsed) in new[]
                {
                    ("largest", 1400, false), ("1000", 1000, false), ("760", 760, false),
                    ("shell-min", 0, false), ("shell-min-collapsed", 0, true),
                })
                {
                    var paneCollapsed = false;
                    var targets = new List<object>();
                    try
                    {
                        await WaitStableHost("settled page host measurement");
                        var current = HostWidth();
                        Assert.True(current > 0, "The page must report its actual Reactor host width.");
                        var outer = DashboardRect(hwnd);
                        var chrome = outer.Width - current * scale;
                        var aimHostWidth = width == 1000 ? 1008d : width;
                        var targetWindowWidth = (int)Math.Round(width == 0 ? 1000 * scale : Math.Min(workArea.Width, aimHostWidth * scale + chrome));
                        var targetWindowHeight = (int)Math.Round(Math.Min(workArea.Height, Math.Max(outer.Height, 900 * scale)));
                        var expectedTier = width >= 1000 ? OpenClawTray.Presentation.PageWidthTier.Wide : OpenClawTray.Presentation.PageWidthTier.Medium;
                        DashboardNativeRect resized = outer;
                        for (var correction = 0; correction <= 2; correction++)
                        {
                            var prior = HostWidth();
                            var beforeWindow = DashboardRect(hwnd);
                            DashboardResize(hwnd, targetWindowWidth, targetWindowHeight);
                            resized = DashboardRect(hwnd);
                            await WaitStableHost($"stable page host for {name}, correction {correction}", prior,
                                Math.Sign(resized.Width - beforeWindow.Width));
                            var value = HostWidth();
                            targets.Add(new { correction, targetWindowWidthPx = targetWindowWidth, targetWindowHeightPx = targetWindowHeight,
                                measuredHostWidth = value, aimHostWidth });
                            var tier = OpenClawTray.Presentation.PageResponsiveLayout.TierFor(value);
                            if (tier == expectedTier && (width == 0 || Math.Abs(value - aimHostWidth) <= 2))
                                break;
                            if (correction < 2 && width != 0)
                                targetWindowWidth = Math.Clamp(targetWindowWidth + (int)Math.Round((aimHostWidth - value) * scale),
                                    (int)Math.Round(1000 * scale), workArea.Width);
                        }
                        if (collapsed)
                        {
                            var openWidth = HostWidth();
                            Invoke(FindById(run, "WorkspaceTogglePane")
                                ?? throw new InvalidOperationException("Standalone Dashboard workspace collapse control is missing."));
                            paneCollapsed = true;
                            await WaitStableHost("stable collapsed-pane host and nav widths", openWidth, 1);
                        }
                        var measured = HostWidth();
                        var hostBounds = FindById(run, "DashboardsContentHost")!.Current.BoundingRectangle;
                        var hostElement = FindById(run, "DashboardsContentHost")!;
                        string? focusFailure = null;
                        try { if (hostElement.Current.IsKeyboardFocusable) hostElement.SetFocus(); }
                        catch (Exception focusError) { focusFailure = focusError.Message; }
                        System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(DwmFlush());
                        var image = $"dashboards-Light-responsive-{name}.png";
                        OwnedWindowCapture.Save(run, hub, image);
                        var imagePath = Path.Combine(run.ArtifactsDirectory, image);
                        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(imagePath)));
                        observations.Add(new
                        {
                            name, requestedHostWidth = width, measuredHostWidth = measured,
                            aimHostWidth, targets, focusFailure,
                            uiaHostWidth = hostBounds.Width / scale,
                            contentWidth = OpenClawTray.Presentation.PageResponsiveLayout.ContentWidth(measured),
                            windowRectPx = new { x = resized.Left, y = resized.Top, width = resized.Width, height = resized.Height },
                            windowRectDip = new { x = resized.Left / scale, y = resized.Top / scale, width = resized.Width / scale, height = resized.Height / scale },
                            tier = OpenClawTray.Presentation.PageResponsiveLayout.TierFor(measured).ToString(),
                            scale, image, sha256 = sha, paneCollapsed, captured = true,
                        });
                        await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "responsive-widths.json"),
                            JsonSerializer.Serialize(observations));
                        Assert.InRange(Math.Abs(resized.Width - targetWindowWidth), 0, 1);
                        Assert.InRange(Math.Abs(resized.Height - targetWindowHeight), 0, 1);
                        Assert.Equal(expectedTier, OpenClawTray.Presentation.PageResponsiveLayout.TierFor(measured));
                        if (width != 0)
                            Assert.InRange(Math.Abs(measured - aimHostWidth), 0, 2);
                        if (!collapsed)
                            Assert.True(Math.Abs(resized.Width - outer.Width) > 2 || Math.Abs(resized.Height - outer.Height) > 2,
                                "The native window did not change size.");
                        Assert.DoesNotContain(capturedBounds, previous => previous.Sha == sha ||
                            (Math.Abs(previous.Host - measured) < 1 && previous.WindowWidth == resized.Width && previous.WindowHeight == resized.Height));
                        capturedBounds.Add((measured, resized.Width, resized.Height, sha));
                    }
                    catch (Exception ex)
                    {
                        var failureImage = $"dashboards-Light-responsive-{name}-failed.png";
                        string? captureFailure = null;
                        try { OwnedWindowCapture.Save(run, hub, failureImage); }
                        catch (Exception captureError) { captureFailure = captureError.Message; }
                        DashboardNativeRect? geometry = null;
                        double? measured = null;
                        string? geometryFailure = null;
                        try { geometry = DashboardRect(hwnd); measured = HostWidth(); }
                        catch (Exception geometryError) { geometryFailure = geometryError.Message; }
                        string? recoveryFailure = null;
                        if (run.IsRunning)
                        {
                            try
                            {
                                window.SetWindowVisualState(WindowVisualState.Normal);
                                DashboardResize(hwnd, baseline.Width, baseline.Height);
                            }
                            catch (Exception recoveryError) { recoveryFailure = recoveryError.Message; }
                        }
                        observations.Add(new
                        {
                            name, requestedHostWidth = width, measuredHostWidth = measured,
                            contentWidth = measured is { } m ? (double?)OpenClawTray.Presentation.PageResponsiveLayout.ContentWidth(m) : null,
                            tier = measured is { } t ? OpenClawTray.Presentation.PageResponsiveLayout.TierFor(t).ToString() : null,
                            windowRectPx = geometry is { } px ? new { x = px.Left, y = px.Top, width = px.Width, height = px.Height } : null,
                            windowRectDip = geometry is { } dip ? new { x = dip.Left / scale, y = dip.Top / scale, width = dip.Width / scale, height = dip.Height / scale } : null,
                            targets, captured = false, error = ex.ToString(), failureImage, captureFailure, geometryFailure, recoveryFailure,
                        });
                    }
                    finally
                    {
                        if (paneCollapsed && run.IsRunning)
                        {
                            try
                            {
                                var closedWidth = HostWidth();
                                Invoke(FindById(run, "WorkspaceReopenPane")
                                    ?? throw new InvalidOperationException("Standalone Dashboard workspace reopen control is missing."));
                                await WaitUiAsync(run, () => HostWidth() < closedWidth - 100, "navigation pane reopened");
                            }
                            catch (Exception paneError)
                            {
                                observations.Add(new { name = name + "-pane-recovery", captured = false, error = paneError.ToString() });
                            }
                        }
                        await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "responsive-widths.json"),
                            JsonSerializer.Serialize(observations));
                    }
                }
                using var results = JsonDocument.Parse(JsonSerializer.Serialize(observations));
                Assert.All(results.RootElement.EnumerateArray(), entry => Assert.True(entry.GetProperty("captured").GetBoolean(), entry.GetRawText()));
                var scroller = FindById(run, "DashboardsGalleryScroll")
                    ?? throw new InvalidOperationException("The gallery scroll control is missing from UIA.");
                var scrolling = (ScrollPattern)scroller.GetCurrentPattern(ScrollPattern.Pattern);
                Assert.True(scrolling.Current.VerticallyScrollable);
                Assert.False(scrolling.Current.HorizontallyScrollable);
                Assert.Equal(run.AppProcessId, hub.Current.ProcessId);
                var wheelPoint = scroller.Current.BoundingRectangle;
                var wheelX = (int)Math.Round(wheelPoint.Left + wheelPoint.Width / 2);
                var wheelY = (int)Math.Round(wheelPoint.Top + Math.Min(100, wheelPoint.Height / 2));
                async Task ResetScroll()
                {
                    scrolling.SetScrollPercent(ScrollPattern.NoScroll, 0);
                    await WaitUiAsync(run, () => scrolling.Current.VerticalScrollPercent <= 0.1, "input-check scroll baseline");
                }
                DashboardInputCheck InputCheck(string name, Func<Task> perform)
                {
                    return new(name, ResetScroll, phase =>
                    {
                        var percent = scrolling.Current.VerticalScrollPercent;
                        OwnedWindowCapture.Save(run, hub, $"dashboards-input-{name}-{phase}.png");
                        return percent;
                    }, perform);
                }
                var inputResults = await RunDashboardInputChecks(
                    InputCheck("ScrollPattern", async () =>
                    {
                        scrolling.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
                        await WaitUiAsync(run, () => scrolling.Current.VerticalScrollPercent > 0.1, "ScrollPattern gallery scrolling");
                    }),
                    InputCheck("SendInputWheel", async () =>
                    {
                        ValidateDashboardWheelOptIn(Environment.GetEnvironmentVariable("OPENCLAW_RUN_KEYBOARD_PROOF"), hwnd);
                        await NativeKeyboardProof.ActivateAsync(hwnd);
                        ValidateDashboardWheelPreconditions(Environment.GetEnvironmentVariable("OPENCLAW_RUN_KEYBOARD_PROOF"),
                            hwnd, DashboardForegroundWindow());
                        Assert.True(DashboardGetCursorPos(out var originalCursor), "Cannot preserve the original cursor position.");
                        try
                        {
                            Assert.True(DashboardSetCursorPos(wheelX, wheelY), "Cannot move the cursor to the owned gallery.");
                            ValidateDashboardWheelPreconditions(Environment.GetEnvironmentVariable("OPENCLAW_RUN_KEYBOARD_PROOF"),
                                hwnd, DashboardForegroundWindow());
                            var inputs = new[]
                            {
                                new DashboardMouseInputEvent
                                {
                                    Type = 0,
                                    Mouse = new DashboardMouseInput { MouseData = unchecked((uint)-120), Flags = 0x0800 },
                                },
                            };
                            ValidateDashboardWheelInjection(DashboardSendInput(1, inputs,
                                System.Runtime.InteropServices.Marshal.SizeOf<DashboardMouseInputEvent>()));
                        }
                        finally
                        {
                            Assert.True(DashboardSetCursorPos(originalCursor.X, originalCursor.Y),
                                "Cannot restore the original cursor position.");
                        }
                        await WaitUiAsync(run, () => scrolling.Current.VerticalScrollPercent > 0.1, "mouse-wheel gallery scrolling");
                    }));
                await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "responsive-input.json"),
                    JsonSerializer.Serialize(new { attempts = inputResults,
                        delivery = "UIA ScrollPattern.LargeIncrement and foreground-gated SendInput MOUSEEVENTF_WHEEL(-120) at the owned gallery. Cursor restored; no keyboard input.",
                        appProcessId = run.AppProcessId }));
                Assert.All(inputResults, result => Assert.True(result.Error is null, JsonSerializer.Serialize(result)));
                Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "board.get");
            }, scenario: GatewayScenario.CreateDashboards());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_FIXTURE_APP_THEME", previousTheme);
        }
    }

    private static void ValidateDashboardWheelPreconditions(string? enabled, nint window, nint foreground)
    {
        ValidateDashboardWheelOptIn(enabled, window);
        if (foreground != window)
            throw new InvalidOperationException("The owned proof window must be foreground before wheel injection.");
    }

    private static void ValidateDashboardWheelOptIn(string? enabled, nint window)
    {
        if (enabled != "1" || window == 0)
            throw new InvalidOperationException("An explicitly enabled owned native-input proof window is required.");
    }

    private static void ValidateDashboardWheelInjection(uint count)
    {
        if (count != 1)
            throw new InvalidOperationException($"Native wheel proof injected {count}/1 events.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("true")]
    public void DashboardWheelProof_RejectsMissingOrInexactOptIn(string? enabled) =>
        Assert.Throws<InvalidOperationException>(() => ValidateDashboardWheelPreconditions(enabled, 1, 1));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    public void DashboardWheelProof_RejectsMissingOrNonForegroundTarget(int window, int foreground) =>
        Assert.Throws<InvalidOperationException>(() => ValidateDashboardWheelPreconditions("1", window, foreground));

    [Fact]
    public void DashboardWheelProof_RequiresOneInjectedEvent()
    {
        ValidateDashboardWheelPreconditions("1", 1, 1);
        ValidateDashboardWheelInjection(1);
        Assert.Throws<InvalidOperationException>(() => ValidateDashboardWheelInjection(0));
        Assert.Throws<InvalidOperationException>(() => ValidateDashboardWheelInjection(2));
    }

    private sealed record DashboardInputCheck(string Name, Func<Task> Reset, Func<string, double> ReadPercent, Func<Task> Perform);
    private sealed record DashboardInputResult(string Name, double? BeforePercent, double? AfterPercent, string? Error);

    private static async Task<DashboardInputResult[]> RunDashboardInputChecks(params DashboardInputCheck[] checks)
    {
        var results = new List<DashboardInputResult>();
        foreach (var check in checks)
        {
            double? before = null;
            double? after = null;
            Exception? error = null;
            try
            {
                await check.Reset();
                before = check.ReadPercent("before");
                await check.Perform();
            }
            catch (Exception failure)
            {
                error = failure;
            }
            try { after = check.ReadPercent("after"); }
            catch (Exception observationFailure)
            {
                error = error is null ? observationFailure : new AggregateException(error, observationFailure);
            }
            results.Add(new(check.Name, before, after, error?.ToString()));
        }
        return results.ToArray();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DashboardInputChecks_ReportBothAttemptsAndRetainEitherFailure(bool firstFails)
    {
        var percent = 0d;
        var calls = new List<string>();
        Task Reset() { percent = 0; return Task.CompletedTask; }
        DashboardInputCheck Check(string name, bool fail) => new(name, Reset, _ => percent, () =>
        {
            calls.Add(name);
            if (fail)
                throw new TimeoutException(name + " did not move.");
            percent = 25;
            return Task.CompletedTask;
        });
        var results = await RunDashboardInputChecks(Check("provider", firstFails), Check("wheel", !firstFails));
        Assert.Equal(new[] { "provider", "wheel" }, calls);
        Assert.Equal(2, results.Length);
        var failed = results[firstFails ? 0 : 1];
        var passed = results[firstFails ? 1 : 0];
        Assert.Equal(0d, failed.BeforePercent);
        Assert.Equal(0d, failed.AfterPercent);
        Assert.Contains("TimeoutException", failed.Error);
        Assert.Equal(0d, passed.BeforePercent);
        Assert.Equal(25d, passed.AfterPercent);
        Assert.Null(passed.Error);
        var report = JsonSerializer.Serialize(results);
        Assert.Contains("provider", report);
        Assert.Contains("wheel", report);
        Assert.Contains("did not move.", report);
        Assert.ThrowsAny<Exception>(() =>
            Assert.All(results, result => Assert.True(result.Error is null, JsonSerializer.Serialize(result))));
    }

    [Fact]
    public async Task DashboardInputChecks_ReportObservationErrorsWithoutSuppressingTheNextAttempt()
    {
        var reads = 0;
        var secondAttemptRan = false;
        var results = await RunDashboardInputChecks(
            new("unreadable", () => throw new InvalidOperationException("Reset failed."), _ =>
            {
                reads++;
                throw new InvalidOperationException("Percent unavailable.");
            }, () => throw new InvalidOperationException("Must not perform after failed reset.")),
            new("independent", () => Task.CompletedTask, _ => 0, () =>
            {
                secondAttemptRan = true;
                return Task.CompletedTask;
            }));
        Assert.True(secondAttemptRan);
        Assert.Equal(1, reads);
        Assert.Null(results[0].BeforePercent);
        Assert.Null(results[0].AfterPercent);
        Assert.Contains("Reset failed.", results[0].Error);
        Assert.Contains("Percent unavailable.", results[0].Error);
        Assert.Null(results[1].Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DashboardInputChecks_LabelAfterEvidenceExplicitlyWhenBaselineFails(bool resetFails)
    {
        var observations = new List<string>();
        var secondAttemptRan = false;
        var results = await RunDashboardInputChecks(
            new("failed", () => resetFails ? throw new InvalidOperationException("Reset failed.") : Task.CompletedTask,
                phase =>
                {
                    observations.Add("failed-" + phase);
                    if (phase == "before")
                        throw new InvalidOperationException("Before observation failed.");
                    return 12;
                }, () => throw new InvalidOperationException("Must not inject after baseline failure.")),
            new("independent", () => Task.CompletedTask, phase =>
            {
                observations.Add("independent-" + phase);
                return 0;
            }, () =>
            {
                secondAttemptRan = true;
                return Task.CompletedTask;
            }));
        Assert.Equal(resetFails
            ? ["failed-after", "independent-before", "independent-after"]
            : new[] { "failed-before", "failed-after", "independent-before", "independent-after" }, observations);
        Assert.True(secondAttemptRan);
        Assert.Null(results[0].BeforePercent);
        Assert.Equal(12d, results[0].AfterPercent);
        Assert.Contains(resetFails ? "Reset failed." : "Before observation failed.", results[0].Error);
        Assert.Null(results[1].Error);
        Assert.ThrowsAny<Exception>(() =>
            Assert.All(results, result => Assert.True(result.Error is null, JsonSerializer.Serialize(result))));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint DashboardWindowDpi(IntPtr window);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct DashboardMouseInputEvent
    {
        public uint Type;
        public DashboardMouseInput Mouse;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct DashboardMouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public nuint ExtraInfo;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct DashboardCursorPoint { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint DashboardSendInput(uint count, DashboardMouseInputEvent[] input, int size);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint DashboardForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetCursorPos", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DashboardGetCursorPos(out DashboardCursorPoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetCursorPos", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DashboardSetCursorPos(int x, int y);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct DashboardNativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DashboardGetWindowRect(IntPtr window, out DashboardNativeRect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DashboardSetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    private static DashboardNativeRect DashboardRect(IntPtr window)
    {
        if (!DashboardGetWindowRect(window, out var rect))
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        return rect;
    }

    private static void DashboardResize(IntPtr window, int width, int height)
    {
        const uint noMoveNoZOrderNoActivate = 0x0016;
        if (!DashboardSetWindowPos(window, IntPtr.Zero, 0, 0, width, height, noMoveNoZOrderNoActivate))
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
    }

    private static object DashboardUiaNode(AutomationElement element) => new
    {
        className = element.Current.ClassName,
        automationId = element.Current.AutomationId,
        processId = element.Current.ProcessId,
        rect = element.Current.BoundingRectangle,
        patterns = element.GetSupportedPatterns().Select(pattern => pattern.ProgrammaticName).ToArray(),
    };

    private static IReadOnlyList<object> DashboardUiaContext(AutomationElement anchor, int processId)
    {
        var entries = new List<object>();
        var current = anchor;
        for (var depth = 0; current is not null && depth < 12; depth++)
        {
            try
            {
                if (current.Current.ProcessId != processId && current.Current.ProcessId != 0)
                    break;
                var children = current.Current.ProcessId == processId
                    ? current.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>()
                        .Where(child => child.Current.ProcessId == processId).Take(40).Select(DashboardUiaNode).ToArray()
                    : [];
                entries.Add(new { depth, node = DashboardUiaNode(current), children });
                if (current.Current.ProcessId == 0)
                    break;
                current = TreeWalker.RawViewWalker.GetParent(current);
            }
            catch (Exception error)
            {
                entries.Add(new { depth, error = error.ToString() });
                break;
            }
        }
        return entries;
    }

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
                    await run.InvokeAsync("app.navigate", new { page = "settings" });
                    await WaitUiAsync(run, () => FindById(run, "SettingsNavChat") is { Current.IsOffscreen: false },
                        "Settings navigation visible");
                    Assert.Null(FindById(run, "HubNavDashboards"));
                    await run.InvokeAsync("app.navigate", new { page = "chat" });
                    await WaitUiAsync(run, () => FindById(run, "WorkspacePinnedPage:dashboards") is
                        { Current.IsOffscreen: false, Current.IsEnabled: true }, "default Dashboards page pin");
                    Invoke(FindById(run, "WorkspacePinnedPage:dashboards")!);
                    await WaitUiAsync(run, () => FindById(run, $"DashboardCardFresh_{FixtureBoards.OpsSessionKey}") is not null,
                        "dashboard gallery populated");
                    await WaitUiAsync(run, () =>
                        FindById(run, "DashboardsTitle")?.Current.Name == "Dashboards for Fixture Main" &&
                        FindById(run, "DashboardsSubtitle")?.Current.Name == "6 dashboards",
                        "Gateway agent name and saved-board count in gallery heading");
                    Assert.NotNull(FindById(run, "DashboardsOpenWeb"));
                    Assert.False(FindById(run, "WorkspacePagesCustomize")!.Current.IsOffscreen);
                    Assert.True(((SelectionItemPattern)FindById(run, "WorkspacePinnedPage:dashboards")!
                        .GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected);
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
                    // Gallery cards are static row metadata: no board, ticket, or widget load until one opens.
                    Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "board.get");

                    AutomationElement? opsCard = null;
                    await WaitUiAsync(run, () => (opsCard = FindById(run, $"DashboardCardFresh_{FixtureBoards.OpsSessionKey}"))
                        is { Current.IsOffscreen: false }, "visible Ops dashboard card");
                    Invoke(opsCard!);
                    // This host's UIA provider can stop at the WebView pane.
                    // Verify the visible HTML and native report through owned-window pixels.
                    await WaitForDashboardPixelsAsync(run, hub, $"dashboards-{theme}-overview.png",
                        ["99.97", "Uptime", "Tickets closed", "Deployments this week"],
                        requireDeploymentPlot: true);
                    AxeHelper.AssertNoAccessibilityErrors(new IntPtr(hub.Current.NativeWindowHandle),
                        context: $"Dashboards widgets ({theme})", targetProcessId: run.AppProcessId);
                    ScrollAncestorToBottom(run, FindById(run, "BoardWidget_broken-widget")!);
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
                    // Websites are browser links, not embedded pages: no outbound network is needed.
                    await WaitForDashboardPixelsAsync(run, hub, $"dashboards-{theme}-website.png",
                        ["example.com", "Open website", "opens in your browser"]);
                    Assert.NotNull(FindById(run, "BoardWebsiteOpen_docs-site"));

                    // Copy only the synthetic fixture session identity, never a production identifier.
                    Invoke(FindById(run, "DashboardMoreActions")!);
                    await WaitUiAsync(run, () => FindById(run, "DashboardCopySessionKey") is
                        { Current.IsOffscreen: false, Current.IsEnabled: true }, "visible copy session menu");
                    Invoke(FindById(run, "DashboardCopySessionKey")!);
                    bool HasCopyNotice() => FindInApp(run,
                        new PropertyCondition(AutomationElement.NameProperty, "Copied session key")) is not null ||
                        FindInApp(run, new PropertyCondition(AutomationElement.NameProperty, "Couldn't copy the session key.")) is not null;
                    await WaitUiAsync(run, HasCopyNotice, "localized clipboard result notice");
                    OwnedWindowCapture.Save(run, hub, $"dashboards-{theme}-clipboard-result.png");
                    await WaitUiAsync(run, () => !HasCopyNotice(),
                        "clipboard notice auto-dismissed");

                    // The titlebar Back button returns from an in-page board, even without a frame back-stack.
                    Invoke(FindById(run, "WorkspaceBack")!);
                    await WaitUiAsync(run, () => FindById(run, $"DashboardCard_{FixtureBoards.EmptySessionKey}")
                            is { Current.IsOffscreen: false },
                        "gallery after same-route navigation");
                    Assert.Equal("Dashboards for Fixture Main", FindById(run, "DashboardsTitle")!.Current.Name);
                    foreach (var pinned in new[] { false, true })
                    {
                        Invoke(FindById(run, "WorkspacePagesCustomize")!);
                        await WaitUiAsync(run, () => FindById(run, "WorkspacePagesToggle:dashboards") is not null,
                            "Dashboards pin toggle");
                        var toggle = FindById(run, "WorkspacePagesToggle:dashboards")!;
                        Assert.Equal(pinned ? ToggleState.Off : ToggleState.On,
                            ((TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState);
                        Invoke(toggle);
                        await WaitUiAsync(run, () => FindById(run, "WorkspacePagesToggle:dashboards") is null,
                            "Dashboards pin flyout closed");
                        await WaitUiAsync(run, () => (FindById(run, "WorkspacePinnedPage:dashboards") is not null) == pinned,
                            "Dashboards pin preference applied");
                        Assert.Equal("Dashboards for Fixture Main", FindById(run, "DashboardsTitle")!.Current.Name);
                    }
                    await run.InvokeAsync("app.navigate", new
                    {
                        page = "dashboards?session=" + Uri.EscapeDataString(FixtureBoards.EmptySessionKey) + "&agent=main",
                    });
                    await WaitUiAsync(run, () => FindById(run, "DashboardEmptyOpenChat") is { Current.IsOffscreen: false },
                        "empty dashboard action");
                    Assert.True(((SelectionItemPattern)FindById(run, "WorkspacePinnedPage:dashboards")!
                        .GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected);
                    Assert.Equal("No content was supplied for this section. Ask your agent in chat.", FindById(run, "DashboardEmptyDetail")!.Current.Name);
                    OwnedWindowCapture.Save(run, hub, $"dashboards-{theme}-empty.png");

                    await run.InvokeAsync("app.navigate", new { page = "dashboards" });
                    AutomationElement? search = null;
                    await WaitUiAsync(run, () => (search = FindById(run, "DashboardsSearch")) is not null, "gallery search");
                    var searchPattern = (ValuePattern)search!.GetCurrentPattern(ValuePattern.Pattern);
                    searchPattern.SetValue("no matching synthetic dashboard");
                    var valueAfterInput = searchPattern.Current.Value;
                    try
                    {
                        await WaitUiAsync(run, () => FindById(run, "DashboardsNoResultsTitle") is not null, "no-results state");
                    }
                    catch (TimeoutException)
                    {
                        var currentSearch = FindById(run, "DashboardsSearch");
                        await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "no-results-timeout.json"),
                            JsonSerializer.Serialize(new
                            {
                                valueAfterInput,
                                currentValue = currentSearch is null ? null :
                                    ((ValuePattern)currentSearch.GetCurrentPattern(ValuePattern.Pattern)).Current.Value,
                                sameSearchControl = currentSearch is not null && search.GetRuntimeId().SequenceEqual(currentSearch.GetRuntimeId()),
                                loadingPresent = FindById(run, "DashboardsLoading") is not null,
                                emptyPresent = FindById(run, "DashboardsEmpty") is not null,
                                cardsPresent = FindById(run, "DashboardsCards") is not null,
                                noResultsPresent = FindById(run, "DashboardsNoResults") is not null,
                                title = FindById(run, "DashboardsTitle")?.Current.Name,
                                count = FindById(run, "DashboardsSubtitle")?.Current.Name,
                                searchOffscreen = currentSearch?.Current.IsOffscreen,
                            }));
                        OwnedWindowCapture.Save(run, hub, $"dashboards-{theme}-no-results-timeout.png");
                        throw;
                    }
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
            await DismissDashboardContainmentNoticeAsync(run, hub, imageName);
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

    private static async Task DismissDashboardContainmentNoticeAsync(GatewayFixtureRun run, AutomationElement hub,
        string imageName)
    {
        var evidencePath = Path.Combine(run.ArtifactsDirectory, Path.GetFileNameWithoutExtension(imageName) + ".notice.json");
        var notice = FindById(run, "AppNotificationInfoBar");
        if (notice is null || notice.Current.IsOffscreen)
        {
            if (!File.Exists(evidencePath))
                await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(new { present = false, dismissed = false }));
            return;
        }
        const string title = "Windows containment unavailable: commands run uncontained";
        var hasKnownTitle = notice.Current.Name.Contains(title, StringComparison.Ordinal)
            || notice.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, title)) is not null;
        if (!hasKnownTitle)
        {
            if (!File.Exists(evidencePath))
                await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(new { present = false, dismissed = false }));
            return;
        }
        if (notice.Current.ProcessId != run.AppProcessId || hub.Current.ProcessId != run.AppProcessId)
            throw new InvalidOperationException("Containment notice is not owned by the fixture app.");
        // Native banner close hides all active banners. Refuse to hide another notice.
        var bell = FindById(run, "WorkspaceTitleBarNotifications")
            ?? throw new InvalidOperationException("Owned notification count is unavailable.");
        Assert.Equal(run.AppProcessId, bell.Current.ProcessId);
        Assert.Equal("1 active", bell.Current.HelpText);
        var close = notice.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "CloseButton"))
            ?? throw new InvalidOperationException("Known containment notice has no native close button.");
        var noticeImage = Path.GetFileNameWithoutExtension(imageName) + "-containment-notice.png";
        OwnedWindowCapture.Save(run, hub, noticeImage);
        await File.WriteAllTextAsync(evidencePath,
            JsonSerializer.Serialize(new { present = true, dismissed = false, noticeImage, title }));
        Invoke(close);
        await WaitUiAsync(run, () => FindById(run, "AppNotificationInfoBar") is not { Current.IsOffscreen: false },
            "owned containment notice dismissed before dashboard capture");
        await File.WriteAllTextAsync(evidencePath,
            JsonSerializer.Serialize(new { present = true, dismissed = true, noticeImage, title }));
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
