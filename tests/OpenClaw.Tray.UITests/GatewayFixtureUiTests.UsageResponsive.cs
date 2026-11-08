using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    private static readonly TimeSpan UsageResizeSampleInterval = TimeSpan.FromMilliseconds(300);

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task UsagePinned_ToggleOwnsHitPointAndTabOrderWithVisibleOperationFeedback()
    {
        foreach (var theme in new[] { "Light", "Dark" })
        {
            await WithAppAsync(async run =>
            {
                await run.InvokeAsync("app.navigate", new { page = "settings" });
                await WaitUiAsync(run, () => FindById(run, "SettingsPageAppTheme") is { Current.IsOffscreen: false },
                    "existing app theme selector");
                var selector = FindById(run, "SettingsPageAppTheme")!;
                var themePattern = (ExpandCollapsePattern)selector.GetCurrentPattern(ExpandCollapsePattern.Pattern);
                themePattern.Expand();
                var themeCondition = new AndCondition(
                    new PropertyCondition(AutomationElement.NameProperty, theme),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                await WaitUiAsync(run, () => FindInApp(run, themeCondition) is not null, $"theme {theme}");
                ((SelectionItemPattern)FindInApp(run, themeCondition)!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                themePattern.Collapse();
                await run.InvokeAsync("app.navigate", new { page = "chat" });
                await WaitUiAsync(run, () => FindById(run, "WorkspacePagesCustomize") is { Current.IsOffscreen: false },
                    "Chat pin customization");
                var window = FindHub(run);
                var handle = new IntPtr(window.Current.NativeWindowHandle);
                await NativeKeyboardProof.ActivateAsync(handle);
                var scale = UsageGetDpiForWindow(handle) / 96d;
                var expected = ResizeUsageWindow(handle, 1000, scale, capToDisplay: false);
                await WaitUiAsync(run, () => UsageWindowBounds(handle).Width == expected.Width &&
                    UsageWindowBounds(handle).Height == expected.Height, "1000-DIP owned pinned window");
                Invoke(FindById(run, "WorkspacePagesCustomize")!);
                await WaitUiAsync(run, () => FindById(run, "WorkspacePagesToggle:usage") is not null,
                    "existing Usage pin toggle");
                Invoke(FindById(run, "WorkspacePagesToggle:usage")!);
                await WaitUiAsync(run, () => FindById(run, "WorkspacePinnedPage:usage") is not null,
                    "existing pinned Usage row");
                Invoke(FindById(run, "WorkspacePinnedPage:usage")!);
                await WaitUiAsync(run, () => FindById(run, "UsageSummaryCostValue") is { Current.IsOffscreen: false },
                    "populated pinned Usage");

                bool IsWithin(AutomationElement element, AutomationElement ancestor)
                {
                    for (AutomationElement? current = element; current is not null;
                         current = TreeWalker.RawViewWalker.GetParent(current))
                        if (current.GetRuntimeId().SequenceEqual(ancestor.GetRuntimeId()))
                            return true;
                    return false;
                }
                object Describe(AutomationElement element) => new
                {
                    runtimeId = element.GetRuntimeId(), pid = element.Current.ProcessId,
                    hwnd = element.Current.NativeWindowHandle, automationId = element.Current.AutomationId,
                    className = element.Current.ClassName, controlType = element.Current.ControlType.ProgrammaticName,
                    bounds = element.Current.BoundingRectangle,
                };
                var hitSequence = 0;
                void AssertToggleHit(string id)
                {
                    var button = FindById(run, id)
                        ?? throw new InvalidOperationException($"Missing pinned Usage pane button {id}.");
                    var bounds = button.Current.BoundingRectangle;
                    var point = new System.Windows.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                    var receipt = Path.Combine(run.ArtifactsDirectory, $"pane-hit-{++hitSequence:D2}-{id}.json");
                    var target = Describe(button);
                    var targetIdentity = button.GetRuntimeId();
                    var windowState = Describe(window);
                    var ancestry = new List<object>();
                    var owned = false;
                    string? error = null;
                    void WriteReceipt(string phase) =>
                        File.WriteAllText(receipt, JsonSerializer.Serialize(new
                        {
                            utc = DateTimeOffset.UtcNow, phase, point, expectedPid = run.AppProcessId,
                            window = windowState, target, ancestry, owned, error,
                        }, new JsonSerializerOptions { WriteIndented = true }));
                    WriteReceipt("before-point-query");
                    try
                    {
                        Assert.True(bounds.Width > 0 && bounds.Height > 0);
                        var hit = AutomationElement.FromPoint(point);
                        for (AutomationElement? current = hit; current is not null;
                             current = TreeWalker.RawViewWalker.GetParent(current))
                        {
                            ancestry.Add(Describe(current));
                            owned |= current.GetRuntimeId().SequenceEqual(targetIdentity);
                            WriteReceipt("raw-ancestor");
                        }
                    }
                    catch (Exception exception)
                    {
                        error = exception.ToString();
                        throw;
                    }
                    finally
                    {
                        WriteReceipt(error is null ? "complete" : "failed");
                    }
                    Assert.Equal(run.AppProcessId, button.Current.ProcessId);
                    Assert.False(button.Current.IsOffscreen);
                    Assert.True(button.Current.IsEnabled);
                    Assert.True(owned, $"{id} center hit is not the button or its RawView descendant. Receipt: {receipt}");
                }
                async Task StableGeometry(bool collapsed, string stateName)
                {
                    string? previous = null;
                    var consecutive = 0;
                    var samples = new List<object>();
                    await run.WaitForAsync(async () =>
                    {
                        var geometry = new
                        {
                            host = FindById(run, "WorkspaceSettingsHost")!.Current.BoundingRectangle,
                            surface = FindById(run, "UsageSurface")!.Current.BoundingRectangle,
                            viewport = FindById(run, "UsagePageRoot")!.Current.BoundingRectangle,
                            marker = FindById(run, "UsagePageMarker")!.Current.BoundingRectangle,
                            button = FindById(run, collapsed ? "WorkspaceReopenPane" : "WorkspaceTogglePane")!
                                .Current.BoundingRectangle,
                            feedback = FindById(run, "WorkspaceEmbeddedOperationFeedback")!.Current.BoundingRectangle,
                        };
                        var current = JsonSerializer.Serialize(geometry);
                        consecutive = previous == current ? consecutive + 1 : 0;
                        previous = current;
                        samples.Add(new { utc = DateTimeOffset.UtcNow, geometry, consecutive });
                        File.WriteAllText(Path.Combine(run.ArtifactsDirectory, $"pane-layout-{stateName}.json"),
                            JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
                        if (consecutive >= 3) return true;
                        await Task.Delay(UsageResizeSampleInterval);
                        return false;
                    }, "settled pinned Usage content, pane button and visible feedback");
                }

                async Task TriggerError()
                {
                    await WaitUiAsync(run, () => FindById(run, "WorkspaceSessionsAdd") is { Current.IsOffscreen: false },
                        "available sidebar session creation while Usage is pinned");
                    Invoke(FindById(run, "WorkspaceSessionsAdd")!);
                    await WaitUiAsync(run, () => FindById(run, "WorkspaceEmbeddedOperationFeedback") is { Current.IsOffscreen: false },
                        "explicit read-only operation error on pinned Usage");
                    Assert.Contains("Fixture Gateway is read-only", FindText(run, "Fixture Gateway is read-only")!.Current.Name);
                }
                // Existing fixture rejects session creation through the ordinary sidebar action.
                await TriggerError();
                var contentIdentity = FindById(run, "UsagePageRoot")!.GetRuntimeId();
                foreach (var (collapsed, stateName) in new[] { (false, "open"), (true, "collapsed"), (false, "reopened") })
                {
                    if (collapsed)
                    {
                        await NativeKeyboardProof.ActivateAsync(handle);
                        AssertToggleHit("WorkspaceTogglePane");
                        Invoke(FindById(run, "WorkspaceTogglePane")!);
                    }
                    else if (FindById(run, "WorkspaceReopenPane") is { Current.IsOffscreen: false } reopen)
                    {
                        await NativeKeyboardProof.ActivateAsync(handle);
                        AssertToggleHit("WorkspaceReopenPane");
                        Invoke(reopen);
                    }
                    var buttonId = collapsed ? "WorkspaceReopenPane" : "WorkspaceTogglePane";
                    await WaitUiAsync(run, () => FindById(run, buttonId) is { Current.IsOffscreen: false },
                        "observed pinned Usage pane state");
                    await StableGeometry(collapsed, stateName);
                    await NativeKeyboardProof.ActivateAsync(handle);
                    AssertToggleHit(buttonId);
                    var content = FindById(run, "UsagePageRoot")!;
                    Assert.Equal(contentIdentity, content.GetRuntimeId());
                    var titleBottom = FindById(run, "WorkspaceTitleBar")!.Current.BoundingRectangle.Bottom;
                    var host = FindById(run, "WorkspaceSettingsHost")!;
                    var surface = FindById(run, "UsageSurface")!;
                    Assert.InRange(Math.Abs((host.Current.BoundingRectangle.Top - titleBottom) / scale), 0, 2);
                    Assert.InRange(Math.Abs((surface.Current.BoundingRectangle.Top - host.Current.BoundingRectangle.Top) / scale), 0, 2);
                    Assert.InRange(Math.Abs((content.Current.BoundingRectangle.Top - titleBottom) / scale
                        - (collapsed ? 56 : 0)), 0, 2);
                    var button = FindById(run, buttonId)!;
                    var feedback = FindById(run, "WorkspaceEmbeddedOperationFeedback")!;
                    Assert.False(feedback.Current.IsOffscreen);
                    Assert.False(button.Current.BoundingRectangle.IntersectsWith(feedback.Current.BoundingRectangle),
                        "Open pinned Usage feedback overlaps pane chrome.");
                    if (collapsed)
                    {
                        Assert.True(button.Current.BoundingRectangle.Bottom <= feedback.Current.BoundingRectangle.Top,
                            "Collapsed pinned Usage feedback is not below pane chrome.");
                        Assert.True(FindById(run, "SettingsBack")!.Current.IsEnabled,
                            "The Chat-to-Usage navigation must supply enabled history for Tab proof.");
                        // The actual InfoBar close peer must not become an extra Tab stop for this separate contract.
                        var closeButtons = feedback.FindAll(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                        var close = Assert.Single(closeButtons.Cast<AutomationElement>());
                        Assert.True(IsWithin(close, feedback));
                        Assert.Equal(ControlType.Button, close.Current.ControlType);
                        Invoke(close);
                        await WaitUiAsync(run, () => FindById(run, "WorkspaceEmbeddedOperationFeedback") is null or { Current.IsOffscreen: true },
                            "operation owner dismissed before history Tab proof");
                        button.SetFocus();
                        await WaitUiAsync(run, () => button.Current.HasKeyboardFocus, "pinned reopen focus");
                        foreach (var id in new[] { "SettingsBack", "SettingsForward" })
                        {
                            var history = FindById(run, id)!;
                            Assert.False(history.Current.IsOffscreen);
                            Assert.True(button.Current.BoundingRectangle.Right <= history.Current.BoundingRectangle.Left,
                                $"Pinned Usage toggle overlaps {id}.");
                            if (!history.Current.IsEnabled) continue;
                            NativeKeyboardProof.Press(handle, Windows.System.VirtualKey.Tab);
                            await WaitUiAsync(run, () => history.Current.HasKeyboardFocus, $"real Tab to enabled {id}");
                        }
                        NativeKeyboardProof.Press(handle, Windows.System.VirtualKey.Tab);
                        await WaitUiAsync(run, () => IsWithin(AutomationElement.FocusedElement, content),
                            "real Tab from history into Usage content");
                        await NativeKeyboardProof.ActivateAsync(handle);
                        AssertToggleHit("WorkspaceReopenPane");
                        Invoke(FindById(run, "WorkspaceReopenPane")!);
                        await WaitUiAsync(run, () => FindById(run, "WorkspaceSessionsAdd") is { Current.IsOffscreen: false },
                            "sidebar reopened for actual error retrigger");
                        await TriggerError();
                        await NativeKeyboardProof.ActivateAsync(handle);
                        AssertToggleHit("WorkspaceTogglePane");
                        Invoke(FindById(run, "WorkspaceTogglePane")!);
                        await WaitUiAsync(run, () => FindById(run, "WorkspaceReopenPane") is { Current.IsOffscreen: false },
                            "collapsed error recapture");
                        await StableGeometry(true, "collapsed-retriggered");
                        Assert.Equal(contentIdentity, FindById(run, "UsagePageRoot")!.GetRuntimeId());
                        Assert.InRange(Math.Abs((host.Current.BoundingRectangle.Top - titleBottom) / scale), 0, 2);
                        Assert.InRange(Math.Abs((surface.Current.BoundingRectangle.Top - titleBottom) / scale), 0, 2);
                        Assert.InRange(Math.Abs((content.Current.BoundingRectangle.Top - titleBottom) / scale - 56), 0, 2);
                        feedback = FindById(run, "WorkspaceEmbeddedOperationFeedback")!;
                        Assert.False(feedback.Current.IsOffscreen);
                        Assert.True(button.Current.BoundingRectangle.Bottom <= feedback.Current.BoundingRectangle.Top);
                    }
                    var name = $"Usage-{theme}-pinned-{stateName}-error";
                    OwnedWindowCapture.Save(run, window, name + ".png", button, feedback,
                        FindById(run, "UsagePageMarker")!);
                    await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, name + "-layout.json"),
                        JsonSerializer.Serialize(new
                        {
                            theme, collapsed, scale, expectedInsetDip = collapsed ? 56 : 0,
                            host = host.Current.BoundingRectangle,
                            surface = surface.Current.BoundingRectangle,
                            viewport = content.Current.BoundingRectangle,
                            button = button.Current.BoundingRectangle,
                            feedback = feedback.Current.BoundingRectangle,
                            errorTrigger = "WorkspaceSessionsAdd UI invocation; fixture rejects sessions.create",
                            expectedFeedbackId = "WorkspaceEmbeddedOperationFeedback",
                        }, new JsonSerializerOptions { WriteIndented = true }));
                }
            }, scenario: GatewayScenario.CreateUsageAnalytics(UsageAnalyticsMode.Released));
        }
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task UsageHero_ViewportRemainsAnchoredWhileTheCardScrollsAway()
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
            var hero = FindById(run, "UsageMast") ?? throw new InvalidOperationException("Missing Usage hero card.");
            var filter = FindById(run, "UsageFilterRow") ?? throw new InvalidOperationException("Missing Usage range bar.");
            Assert.True(filter.Current.BoundingRectangle.Top >= hero.Current.BoundingRectangle.Bottom);
            OwnedWindowCapture.Save(run, window, "Usage-Light-hero-top.png", FindById(run, "UsagePageMarker")!);
            var top = scrollElement.Current.BoundingRectangle;
            var previousTitle = FindById(run, "UsagePageMarker")!.Current.BoundingRectangle;
            scroll.SetScrollPercent(ScrollPattern.NoScroll, 45);
            await WaitUiAsync(run, () => scroll.Current.VerticalScrollPercent >= 44 &&
                (FindById(run, "UsagePageMarker")!.Current.IsOffscreen ||
                    FindById(run, "UsagePageMarker")!.Current.BoundingRectangle.Top < previousTitle.Top - 100),
                "ordinary Usage header scrolled away");
            Assert.Equal(top, scrollElement.Current.BoundingRectangle);
            Assert.True(hero.Current.IsOffscreen || hero.Current.BoundingRectangle.Top < top.Top);
            OwnedWindowCapture.Save(run, window, "Usage-Light-hero-scrolled.png");
            File.WriteAllText(Path.Combine(run.ArtifactsDirectory, "viewport-scroll-proof.json"), JsonSerializer.Serialize(new
            {
                observedHostWidthDip = top.Width / scale,
                viewportTopPx = top.Top,
                observedScrollPercent = scroll.Current.VerticalScrollPercent,
                headerOffscreen = FindById(run, "UsagePageMarker")!.Current.IsOffscreen,
                scale,
                syntheticGateway = true,
                contract = "The hero card and its accent tint scroll inside an anchored viewport; no sticky header or page-wide wash."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }, scenario: GatewayScenario.CreateUsageAnalytics(UsageAnalyticsMode.Released));
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task UsageHero_RealShellCapturesCardAndRangeInLightAndDark()
    {
        foreach (var theme in new[] { "Light", "Dark" })
        {
            await WithAppAsync(async run =>
            {
                await run.InvokeAsync("app.navigate", new { page = "usage" });
                await WaitUiAsync(run, () => FindById(run, "UsageSummaryCostValue") is { Current.IsOffscreen: false },
                    "loaded Usage hero");
                var window = AppWindows(run).Single(window => window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "UsagePageRoot")) is not null);
                var handle = new IntPtr(window.Current.NativeWindowHandle);
                var scale = UsageGetDpiForWindow(handle) / 96d;
                Assert.True(scale > 0);
                var manifests = new List<object>();
                var failures = new List<string>();
                foreach (var target in theme == "Light" ? new[] { 1400, 0 } : new[] { 1400 })
                {
                    var name = $"Usage-{theme}-hero-{(target == 0 ? "shell-minimum" : "1400")}";
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
                        }, "exact hero capture window bounds");
                        double? previous = null;
                        await run.WaitForAsync(async () =>
                        {
                            await Task.Delay(UsageResizeSampleInterval);
                            var width = FindById(run, "UsagePageRoot")!.Current.BoundingRectangle.Width;
                            var stable = previous is { } value && Math.Abs(value - width) < 0.01;
                            previous = width;
                            return stable;
                        }, "stable hero capture host width");
                        var frame = UsageWindowBounds(handle);
                        var page = FindById(run, "UsagePageRoot")!.Current.BoundingRectangle;
                        var host = page.Width / scale;
                        var hero = FindById(run, "UsageMast")!.Current.BoundingRectangle;
                        var range = FindById(run, "UsageFilterRow")!.Current.BoundingRectangle;
                        Assert.True(hero.Left >= page.Left && hero.Right <= page.Right);
                        Assert.True(range.Top >= hero.Bottom, "Range controls overlap the Usage hero.");
                        foreach (var id in new[] { "UsageOpenWeb", "UsageExport" })
                        {
                            var action = FindById(run, id)!.Current.BoundingRectangle;
                            Assert.True(action.Left >= hero.Left && action.Right <= hero.Right);
                        }
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
                            var session = FindById(run, "UsageSession_agent:main:webchat:main")
                                ?? throw new InvalidOperationException("Missing populated main Usage session.");
                            ((InvokePattern)session.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                            await WaitUiAsync(run, () => FindById(run, "UsageDetailCostValue") is not null,
                                "expanded Usage session detail");
                            (System.Windows.Rect Header, System.Windows.Rect Cost, System.Windows.Rect Tokens)? previousDetail = null;
                            await run.WaitForAsync(async () =>
                            {
                                await Task.Delay(UsageResizeSampleInterval);
                                var current = (Header: session.Current.BoundingRectangle,
                                    Cost: FindById(run, "UsageDetailCostValue")!.Current.BoundingRectangle,
                                    Tokens: FindById(run, "UsageDetailTokensValue")!.Current.BoundingRectangle);
                                var stable = previousDetail == current;
                                previousDetail = current;
                                return stable && current.Cost.Top >= current.Header.Bottom
                                    && current.Tokens.Top >= current.Header.Bottom;
                            }, "settled session animation with detail below its header");
                            OwnedWindowCapture.Save(run, window, "Usage-Light-1400-expanded-session.png");
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
                        File.WriteAllText(Path.Combine(run.ArtifactsDirectory, "hero-manifest.json"),
                            JsonSerializer.Serialize(manifests, new JsonSerializerOptions { WriteIndented = true }));
                    }
                }
                Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
                if (FindById(run, "SettingsTogglePane")!.Current.Name == "Collapse navigation")
                    Invoke(FindById(run, "SettingsTogglePane")!);
                foreach (var width in new[] { 800, 1000 })
                {
                    var expected = ResizeUsageWindow(handle, width, scale, capToDisplay: false);
                    await WaitUiAsync(run, () => UsageWindowBounds(handle).Width == expected.Width,
                        $"collapsed Settings at {width} DIP");
                    await WaitUiAsync(run, () => FindById(run, "UsagePageMarker")!.Current.BoundingRectangle.Top >=
                        Math.Max(FindById(run, "SettingsBack")!.Current.BoundingRectangle.Bottom,
                            FindById(run, "SettingsForward")!.Current.BoundingRectangle.Bottom),
                        "Usage header below collapsed history controls");
                    Assert.InRange(Math.Abs(UsageWindowBounds(handle).Width / scale - width), 0, 1);
                    OwnedWindowCapture.Save(run, window, $"Usage-{theme}-collapsed-{width}.png");
                }
                var collapsedScroll = (ScrollPattern)FindById(run, "UsagePageRoot")!.GetCurrentPattern(ScrollPattern.Pattern);
                collapsedScroll.SetScrollPercent(ScrollPattern.NoScroll, 45);
                await WaitUiAsync(run, () => collapsedScroll.Current.VerticalScrollPercent >= 44 &&
                    FindById(run, "UsagePageMarker") is { Current.IsOffscreen: true },
                    "collapsed content scrolled below the retained history band");
                OwnedWindowCapture.Save(run, window, $"Usage-{theme}-collapsed-1000-scrolled.png");
                collapsedScroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
                await WaitUiAsync(run, () => collapsedScroll.Current.VerticalScrollPercent == 0,
                    "collapsed Usage restored to top");
                Invoke(FindById(run, "SettingsTogglePane")!);
                await WaitUiAsync(run, () => FindById(run, "SettingsSearch") is { Current.IsOffscreen: false },
                    "expanded Settings chrome");
                await CaptureSettledUsageShellAsync(run, window, $"Usage-{theme}-expanded-1000",
                    "UsagePageMarker", paneOpen: true);
                Invoke(FindById(run, "SettingsTogglePane")!);
                await run.InvokeAsync("app.navigate", new { page = "permissions" });
                await WaitUiAsync(run, () => FindById(run, "PermissionsPageMarker") is { Current.IsOffscreen: false },
                    "no-backdrop Permissions page under collapsed Settings");
                await CaptureSettledUsageShellAsync(run, window, $"Permissions-{theme}-collapsed-1000",
                    "PermissionsPageMarker", paneOpen: false);
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

    private static async Task CaptureSettledUsageShellAsync(GatewayFixtureRun run, AutomationElement window,
        string name, string markerId, bool paneOpen)
    {
        var samples = new List<object>();
        string? previous = null;
        var consecutive = 0;
        await run.WaitForAsync(async () =>
        {
            var marker = FindById(run, markerId)!;
            var pane = FindById(run, "SettingsTogglePane")!;
            var content = TreeWalker.ControlViewWalker.GetParent(marker);
            var state = new
            {
                marker = marker.Current.AutomationId,
                markerBounds = marker.Current.BoundingRectangle,
                contentClass = content.Current.ClassName,
                contentBounds = content.Current.BoundingRectangle,
                paneLabel = pane.Current.Name,
                paneBounds = pane.Current.BoundingRectangle,
                navigationBounds = FindById(run, "SettingsNavChat")!.Current.BoundingRectangle,
                searchVisible = FindById(run, "SettingsSearch") is { Current.IsOffscreen: false },
                usageVisible = FindById(run, "UsagePageMarker") is { Current.IsOffscreen: false },
                permissionsVisible = FindById(run, "PermissionsPageMarker") is { Current.IsOffscreen: false },
            };
            var current = JsonSerializer.Serialize(state);
            consecutive = previous == current ? consecutive + 1 : 0;
            previous = current;
            var frame = $"{name}-transition-{samples.Count:D2}.png";
            OwnedWindowCapture.Save(run, window, frame, marker);
            samples.Add(new { capturedAtUtc = DateTimeOffset.UtcNow, frame, consecutive, state });
            File.WriteAllText(Path.Combine(run.ArtifactsDirectory, name + "-settling.json"),
                JsonSerializer.Serialize(new
                {
                    requestedRoute = markerId == "PermissionsPageMarker" ? "permissions" : "usage",
                    expectedPaneOpen = paneOpen,
                    inferredContentOnly = false,
                    inferredPageType = markerId == "PermissionsPageMarker" ? "PermissionsPage" : "UsagePage",
                    inferredFrameTopMargin = markerId == "PermissionsPageMarker" && !paneOpen ? 56 : 0,
                    inferenceLimit = "Page type and margin follow source plus observed markers, not runtime field reads. Stable UIA bounds do not prove compositor transition continuity.",
                    samples,
                }, new JsonSerializerOptions { WriteIndented = true }));
            var ready = !marker.Current.IsOffscreen
                && state.paneLabel == (paneOpen ? "Collapse navigation" : "Expand navigation")
                && (markerId == "PermissionsPageMarker" ? !state.usageVisible : !state.permissionsVisible);
            if (ready && consecutive >= 3)
            {
                OwnedWindowCapture.Save(run, window, name + ".png", marker);
                return true;
            }
            await Task.Delay(UsageResizeSampleInterval);
            return false;
        }, $"settled {name} marker, content, pane and navigation bounds with retained transition frames");
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
            ["average"] = FindById(run, "UsageDailyAverageLabel")
                ?? throw new InvalidOperationException("Missing daily average text peer."),
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
