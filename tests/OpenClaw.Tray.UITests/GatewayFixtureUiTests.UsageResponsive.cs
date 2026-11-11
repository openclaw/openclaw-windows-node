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

    private static void AssertUsagePointOwner(IntPtr window, FixturePoint point) =>
        Assert.Equal(window, GetAncestor(WindowFromPoint(point), 2));

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr UsageCreateOccluder(uint extendedStyle, string className, string title,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    private static extern bool UsageDestroyOccluder(IntPtr window);

    private sealed record UsageDiagnosticBounds(bool IsEmpty, double? Left, double? Top, double? Width, double? Height);

    // UIA can return Rect.Empty for an offscreen peer. Keep that fact without serializing infinities.
    private static UsageDiagnosticBounds UsageBoundsForDiagnostic(System.Windows.Rect bounds)
    {
        static double? Finite(double value) => double.IsFinite(value) ? value : null;
        return new(bounds.IsEmpty, Finite(bounds.Left), Finite(bounds.Top), Finite(bounds.Width), Finite(bounds.Height));
    }

    [Fact]
    public void UsageScrollDiagnostics_SerializeEmptyAndMeasuredBoundsWithoutNonfiniteNumbers()
    {
        using var empty = JsonDocument.Parse(JsonSerializer.Serialize(UsageBoundsForDiagnostic(System.Windows.Rect.Empty)));
        Assert.True(empty.RootElement.GetProperty("IsEmpty").GetBoolean());
        foreach (var name in new[] { "Left", "Top", "Width", "Height" })
            Assert.Equal(JsonValueKind.Null, empty.RootElement.GetProperty(name).ValueKind);
        using var measured = JsonDocument.Parse(JsonSerializer.Serialize(UsageBoundsForDiagnostic(new(10, 20, 30, 40))));
        Assert.False(measured.RootElement.GetProperty("IsEmpty").GetBoolean());
        Assert.Equal(10, measured.RootElement.GetProperty("Left").GetDouble());
        Assert.Equal(20, measured.RootElement.GetProperty("Top").GetDouble());
        Assert.Equal(30, measured.RootElement.GetProperty("Width").GetDouble());
        Assert.Equal(40, measured.RootElement.GetProperty("Height").GetDouble());
    }

    private static System.Windows.Rect UsageRangeBounds(GatewayFixtureRun run)
    {
        // Layout-only Grids have no UIA peer. Measure the actual interactive controls.
        var bounds = System.Windows.Rect.Empty;
        foreach (var id in new[] { "UsagePresets", "UsageOptions", "UsageRefresh" })
        {
            var control = FindById(run, id)
                ?? throw new InvalidOperationException($"Missing Usage range control {id}.");
            Assert.False(control.Current.IsOffscreen);
            bounds.Union(control.Current.BoundingRectangle);
        }
        foreach (var id in new[] { "UsageFacet_agent", "UsageQuery", "UsageStartDate", "UsageEndDate" })
            if (FindById(run, id) is { } control)
                bounds.Union(control.Current.BoundingRectangle);
        return bounds;
    }

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
                    bounds = UsageBoundsForDiagnostic(element.Current.BoundingRectangle),
                };
                var hitSequence = 0;
                async Task ClickToggleAsync(string id, string expectedId)
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
                    object[] hitChildren = [];
                    var owned = false;
                    string? error = null;
                    void WriteReceipt(string phase) =>
                        File.WriteAllText(receipt, JsonSerializer.Serialize(new
                        {
                            utc = DateTimeOffset.UtcNow, phase, point, expectedPid = run.AppProcessId,
                            window = windowState, target, ancestry, hitChildren, owned, error,
                        }, new JsonSerializerOptions { WriteIndented = true }));
                    WriteReceipt("before-point-query");
                    try
                    {
                        Assert.True(bounds.Width > 0 && bounds.Height > 0);
                        var hit = AutomationElement.FromPoint(point);
                        hitChildren = hit.FindAll(TreeScope.Children, Condition.TrueCondition)
                            .Cast<AutomationElement>().Select(Describe).ToArray();
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
                    // WinAppSDK's bridge can stop a UIA point query above its InputSite child.
                    // The actual center click must change the pane; bridge ancestry alone never passes.
                    var nativePoint = new FixturePoint { X = (int)point.X, Y = (int)point.Y };
                    uint sent;
                    var previousDpi = UsageSetThreadDpiAwarenessContext(new IntPtr(-4));
                    try
                    {
                        AssertUsagePointOwner(handle, nativePoint);
                        Assert.True(SetCursorPos(nativePoint.X, nativePoint.Y));
                        Assert.True(GetCursorPos(out var cursor));
                        Assert.Equal(nativePoint, cursor);
                        AssertOwnedForeground(handle, GetForegroundWindow);
                        AssertUsagePointOwner(handle, cursor);
                        var input = new[]
                        {
                            new FixtureInput { Mouse = new FixtureMouse { Flags = 2 } },
                            new FixtureInput { Mouse = new FixtureMouse { Flags = 4 } },
                        };
                        sent = SendInput(2, input, Marshal.SizeOf<FixtureInput>());
                        if (sent == 1) SendInput(1, [input[1]], Marshal.SizeOf<FixtureInput>());
                        Assert.Equal(2u, sent);
                    }
                    finally { UsageSetThreadDpiAwarenessContext(previousDpi); }
                    await WaitUiAsync(run, () =>
                        FindById(run, id) is null or { Current.IsOffscreen: true } &&
                        FindById(run, expectedId) is { Current.IsOffscreen: false },
                        $"physical center click on {id} changed the pane to {expectedId}");
                    File.WriteAllText(Path.ChangeExtension(receipt, ".click.json"), JsonSerializer.Serialize(new
                    {
                        id, expectedId, point, expectedPid = run.AppProcessId,
                        uiaTargetOwned = owned, injectedEvents = sent, paneTransitionObserved = true,
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
                async Task StableGeometry(bool collapsed, string stateName)
                {
                    System.Windows.Rect? Bounds(string id)
                    {
                        var peer = FindById(run, id);
                        if (peer is null) return null;
                        var bounds = peer.Current.BoundingRectangle;
                        return !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0 ? bounds : null;
                    }
                    string? previous = null;
                    var consecutive = 0;
                    var samples = new List<object>();
                    await run.WaitForAsync(async () =>
                    {
                        var geometry = new
                        {
                            titleBar = Bounds("WorkspaceTitleBar"),
                            viewport = Bounds("UsagePageRoot"),
                            marker = Bounds("UsagePageMarker"),
                            button = Bounds(collapsed ? "WorkspaceReopenPane" : "WorkspaceTogglePane"),
                            feedback = Bounds("WorkspaceEmbeddedOperationFeedback"),
                        };
                        var ready = geometry.titleBar.HasValue && geometry.viewport.HasValue &&
                            geometry.marker.HasValue && geometry.button.HasValue && geometry.feedback.HasValue;
                        var current = JsonSerializer.Serialize(geometry);
                        consecutive = ready && previous == current ? consecutive + 1 : 0;
                        previous = ready ? current : null;
                        samples.Add(new { utc = DateTimeOffset.UtcNow, geometry, ready, consecutive });
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
                await NativeKeyboardProof.ActivateAsync(handle);
                var negativeBounds = FindById(run, "WorkspaceTogglePane")!.Current.BoundingRectangle;
                var negativePoint = new FixturePoint
                {
                    X = (int)(negativeBounds.Left + negativeBounds.Width / 2),
                    Y = (int)(negativeBounds.Top + negativeBounds.Height / 2),
                };
                // A real topmost test-owned window must make the exact-point owner gate reject.
                var previousDpi = UsageSetThreadDpiAwarenessContext(new IntPtr(-4));
                var occluder = IntPtr.Zero;
                try
                {
                    occluder = UsageCreateOccluder(0x08000008, "BUTTON", "Usage hit-test negative control",
                        0x90000000, negativePoint.X - 20, negativePoint.Y - 20, 40, 40,
                        IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    Assert.NotEqual(IntPtr.Zero, occluder);
                    Assert.Equal(occluder, GetAncestor(WindowFromPoint(negativePoint), 2));
                    Assert.Throws<Xunit.Sdk.EqualException>(() => AssertUsagePointOwner(handle, negativePoint));
                    Assert.False(FindById(run, "WorkspaceTogglePane")!.Current.IsOffscreen);
                    File.WriteAllText(Path.Combine(run.ArtifactsDirectory, "pane-hit-occlusion-negative.json"),
                        JsonSerializer.Serialize(new { point = new { negativePoint.X, negativePoint.Y }, rejected = true, injectedEvents = 0 }));
                }
                finally
                {
                    if (occluder != IntPtr.Zero) Assert.True(UsageDestroyOccluder(occluder));
                    UsageSetThreadDpiAwarenessContext(previousDpi);
                }
                foreach (var (collapsed, stateName) in new[] { (false, "open"), (true, "collapsed"), (false, "reopened") })
                {
                    if (collapsed)
                    {
                        await NativeKeyboardProof.ActivateAsync(handle);
                        await ClickToggleAsync("WorkspaceTogglePane", "WorkspaceReopenPane");
                    }
                    else if (FindById(run, "WorkspaceReopenPane") is { Current.IsOffscreen: false } reopen)
                    {
                        await NativeKeyboardProof.ActivateAsync(handle);
                        await ClickToggleAsync("WorkspaceReopenPane", "WorkspaceTogglePane");
                    }
                    var buttonId = collapsed ? "WorkspaceReopenPane" : "WorkspaceTogglePane";
                    await WaitUiAsync(run, () => FindById(run, buttonId) is { Current.IsOffscreen: false },
                        "observed pinned Usage pane state");
                    await StableGeometry(collapsed, stateName);
                    await NativeKeyboardProof.ActivateAsync(handle);
                    var oppositeId = collapsed ? "WorkspaceTogglePane" : "WorkspaceReopenPane";
                    await ClickToggleAsync(buttonId, oppositeId);
                    await ClickToggleAsync(oppositeId, buttonId);
                    await StableGeometry(collapsed, stateName + "-after-click");
                    var content = FindById(run, "UsagePageRoot")!;
                    Assert.Equal(contentIdentity, content.GetRuntimeId());
                    var titleBottom = FindById(run, "WorkspaceTitleBar")!.Current.BoundingRectangle.Bottom;
                    // Grids have no UIA peer. The mounted ContentInset test measures the actual
                    // surface against its host; this real-shell test measures the scrolling control
                    // against native title chrome and keeps the 0/56-DIP integration contract.
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

                        // History is the last Root child. Native Tab order wraps through the
                        // title bar and its visible actions before returning to page content.
                        void RecordTabFocus(string step)
                        {
                            int? focusedPid = null;
                            object? ownedFocus = null;
                            string? observationError = null;
                            try
                            {
                                var focused = AutomationElement.FocusedElement;
                                focusedPid = focused?.Current.ProcessId;
                                if (focused is not null && focusedPid == run.AppProcessId)
                                    ownedFocus = Describe(focused);
                            }
                            catch (ElementNotAvailableException error)
                            {
                                observationError = error.Message;
                            }
                            File.WriteAllText(Path.Combine(run.ArtifactsDirectory, $"tab-{theme}-{step}.json"),
                                JsonSerializer.Serialize(new
                                {
                                    step, expectedPid = run.AppProcessId,
                                    foregroundWindow = GetForegroundWindow().ToString("X"),
                                    focusedPid, ownedFocus, observationError,
                                }, new JsonSerializerOptions { WriteIndented = true }));
                        }
                        async Task TabToShellControl(string id, ControlType controlType, bool reverse = false)
                        {
                            var expectedFocus = FindById(run, id)
                                ?? throw new InvalidOperationException($"Missing title-bar focus target {id}.");
                            Assert.Equal(run.AppProcessId, expectedFocus.Current.ProcessId);
                            Assert.Equal(controlType, expectedFocus.Current.ControlType);
                            Assert.True(expectedFocus.Current.IsEnabled);
                            Assert.False(expectedFocus.Current.IsOffscreen);
                            NativeKeyboardProof.Press(handle, Windows.System.VirtualKey.Tab, shift: reverse);
                            try
                            {
                                await WaitUiAsync(run, () => expectedFocus.Current.HasKeyboardFocus,
                                    $"real {(reverse ? "Shift+Tab" : "Tab")} to {id}");
                            }
                            finally { RecordTabFocus((reverse ? "reverse-" : "") + id); }
                            AssertOwnedForeground(handle, GetForegroundWindow);
                            Assert.Equal(expectedFocus.GetRuntimeId(), AutomationElement.FocusedElement.GetRuntimeId());
                        }
                        bool HasVisibleContentFocus()
                        {
                            if (AutomationElement.FocusedElement is not { } focused) return false;
                            var bounds = focused.Current.BoundingRectangle;
                            return focused.Current.ProcessId == run.AppProcessId && focused.Current.HasKeyboardFocus
                                && !focused.Current.IsOffscreen && bounds.Width > 0 && bounds.Height > 0
                                && content.Current.BoundingRectangle.Contains(bounds) && IsWithin(focused, content);
                        }
                        Assert.True(FindById(run, "WorkspaceTitleBarOwner") is null or { Current.IsOffscreen: true });
                        await TabToShellControl("WorkspaceTitleBar", ControlType.TitleBar);
                        await TabToShellControl("WorkspaceTitleBarDashboard", ControlType.Button);
                        await TabToShellControl("WorkspaceTitleBarNotifications", ControlType.Button);
                        Assert.True(FindById(run, "WorkspaceNotificationsOpenPage") is null or { Current.IsOffscreen: true },
                            "Tab focus must not open the notifications flyout.");
                        NativeKeyboardProof.Press(handle, Windows.System.VirtualKey.Tab);
                        try
                        {
                            await WaitUiAsync(run, HasVisibleContentFocus,
                                "real Tab from title-bar actions into visible Usage content");
                        }
                        finally { RecordTabFocus("Usage-content"); }
                        AssertOwnedForeground(handle, GetForegroundWindow);
                        var firstContentFocus = AutomationElement.FocusedElement.GetRuntimeId();
                        OwnedWindowCapture.Save(run, window, $"Usage-{theme}-pinned-keyboard-content.png",
                            AutomationElement.FocusedElement);
                        await TabToShellControl("WorkspaceTitleBarNotifications", ControlType.Button, reverse: true);
                        NativeKeyboardProof.Press(handle, Windows.System.VirtualKey.Tab);
                        try
                        {
                            await WaitUiAsync(run, () => HasVisibleContentFocus() &&
                                firstContentFocus.SequenceEqual(AutomationElement.FocusedElement.GetRuntimeId()),
                                "real Tab returns to the same Usage control without trapping focus");
                        }
                        finally { RecordTabFocus("Usage-content-return"); }
                        await NativeKeyboardProof.ActivateAsync(handle);
                        await ClickToggleAsync("WorkspaceReopenPane", "WorkspaceTogglePane");
                        await WaitUiAsync(run, () => FindById(run, "WorkspaceSessionsAdd") is { Current.IsOffscreen: false },
                            "sidebar reopened for actual error retrigger");
                        await TriggerError();
                        await NativeKeyboardProof.ActivateAsync(handle);
                        await ClickToggleAsync("WorkspaceTogglePane", "WorkspaceReopenPane");
                        await WaitUiAsync(run, () => FindById(run, "WorkspaceReopenPane") is { Current.IsOffscreen: false },
                            "collapsed error recapture");
                        await StableGeometry(true, "collapsed-retriggered");
                        Assert.Equal(contentIdentity, FindById(run, "UsagePageRoot")!.GetRuntimeId());
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
                            titleBarBottom = titleBottom,
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
            Assert.True(UsageRangeBounds(run).Top >= hero.Current.BoundingRectangle.Bottom);
            OwnedWindowCapture.Save(run, window, "Usage-Light-hero-top.png", FindById(run, "UsagePageMarker")!);
            var top = scrollElement.Current.BoundingRectangle;
            var previousTitle = FindById(run, "UsagePageMarker")!.Current.BoundingRectangle;
            void RecordScrollObservation(string phase)
            {
                var title = FindById(run, "UsagePageMarker")
                    ?? throw new InvalidOperationException("Missing Usage title during scroll proof.");
                File.WriteAllText(Path.Combine(run.ArtifactsDirectory, "hero-scroll-" + phase + ".json"),
                    JsonSerializer.Serialize(new
                    {
                        utc = DateTimeOffset.UtcNow, phase, scrollPercent = scroll.Current.VerticalScrollPercent,
                        viewport = UsageBoundsForDiagnostic(scrollElement.Current.BoundingRectangle),
                        previousTitle = UsageBoundsForDiagnostic(previousTitle),
                        title = new { bounds = UsageBoundsForDiagnostic(title.Current.BoundingRectangle), offscreen = title.Current.IsOffscreen, runtimeId = title.GetRuntimeId() },
                        card = new { bounds = UsageBoundsForDiagnostic(hero.Current.BoundingRectangle), offscreen = hero.Current.IsOffscreen, runtimeId = hero.GetRuntimeId() },
                    }, new JsonSerializerOptions { WriteIndented = true }));
            }
            RecordScrollObservation("before");
            scroll.SetScrollPercent(ScrollPattern.NoScroll, 45);
            await WaitUiAsync(run, () =>
                {
                    RecordScrollObservation("after");
                    var title = FindById(run, "UsagePageMarker")!;
                    var bounds = title.Current.BoundingRectangle;
                    return scroll.Current.VerticalScrollPercent >= 44 &&
                        (title.Current.IsOffscreen || (!bounds.IsEmpty && bounds.Height > 0 &&
                            bounds.Top < top.Top && bounds.Top < previousTitle.Top - 100));
                },
                "ordinary Usage header scrolled away");
            Assert.Equal(top, scrollElement.Current.BoundingRectangle);
            // Border peers can expose clipped bounds. The mounted HeroContent test asserts the
            // actual card transform; here retain visible title movement and the real-shell capture.
            var scrolledTitle = FindById(run, "UsagePageMarker")!;
            Assert.True(scrolledTitle.Current.IsOffscreen ||
                (!scrolledTitle.Current.BoundingRectangle.IsEmpty && scrolledTitle.Current.BoundingRectangle.Height > 0 &&
                    scrolledTitle.Current.BoundingRectangle.Top < top.Top));
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
                        var client = OwnedWindowCapture.ReadOwnedClientBounds(run, window);
                        var samplingAperture = System.Windows.Rect.Intersect(page, client);
                        Assert.True(!samplingAperture.IsEmpty && samplingAperture.Width > 0 && samplingAperture.Height > 0);
                        var host = page.Width / scale;
                        var hero = FindById(run, "UsageMast")!.Current.BoundingRectangle;
                        var range = UsageRangeBounds(run);
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
                        if (target == 1400)
                        {
                            var scroll = (ScrollPattern)FindById(run, "UsagePageRoot")!.GetCurrentPattern(ScrollPattern.Pattern);
                            var requestedPercent = 0d;
                            scroll.SetScrollPercent(ScrollPattern.NoScroll, requestedPercent);
                            AutomationElement? expander = null;
                            System.Windows.Rect collapsedHeader = System.Windows.Rect.Empty;
                            await run.WaitForAsync(async () =>
                            {
                                await Task.Delay(UsageResizeSampleInterval);
                                var position = scroll.Current;
                                if (Math.Abs(position.VerticalScrollPercent - requestedPercent) > 0.01)
                                    return false;
                                var candidate = FindById(run, "UsageSessionExpander_agent:main:webchat:main");
                                if (candidate is { Current.IsOffscreen: false })
                                {
                                    var bounds = candidate.Current.BoundingRectangle;
                                    if (!bounds.IsEmpty && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) &&
                                        double.IsFinite(bounds.Top) && double.IsFinite(bounds.Left) &&
                                        bounds.Width > 0 && bounds.Height > 0 &&
                                        bounds.Top >= samplingAperture.Top && bounds.Bottom <= samplingAperture.Bottom)
                                    {
                                        expander = candidate;
                                        collapsedHeader = bounds;
                                        return true;
                                    }
                                }
                                Assert.True(requestedPercent < 100 && position.VerticalViewSize is > 0 and < 100,
                                    "Main Usage session header has no visible measured geometry at the end of the page.");
                                // Overlapping half-viewport steps reveal the intended row, not a calendar-dependent offset.
                                requestedPercent = Math.Min(100, requestedPercent +
                                    50 * position.VerticalViewSize / (100 - position.VerticalViewSize));
                                scroll.SetScrollPercent(ScrollPattern.NoScroll, requestedPercent);
                                return false;
                            }, "visible measured main Usage session header");
                            OwnedWindowCapture.Save(run, window, $"Usage-{theme}-separated-session-cards.png");
                            Assert.NotNull(expander);
                            var collapsedHeaderHeight = collapsedHeader.Height;
                            ((ExpandCollapsePattern)expander.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
                            await WaitUiAsync(run, () => FindById(run, "UsageDetailCostValue") is not null,
                                "expanded Usage session detail");
                            (System.Windows.Rect Header, System.Windows.Rect Cost, System.Windows.Rect Tokens)? previousDetail = null;
                            await run.WaitForAsync(async () =>
                            {
                                await Task.Delay(UsageResizeSampleInterval);
                                var expandedBounds = expander.Current.BoundingRectangle;
                                if (expandedBounds.IsEmpty || !double.IsFinite(expandedBounds.Width) ||
                                    !double.IsFinite(expandedBounds.Height) || expandedBounds.Width <= 0 ||
                                    expandedBounds.Height <= 0)
                                {
                                    previousDetail = null;
                                    return false;
                                }
                                var current = (Header: new System.Windows.Rect(expandedBounds.Left, expandedBounds.Top,
                                        expandedBounds.Width, collapsedHeaderHeight),
                                    Cost: FindById(run, "UsageDetailCostValue")!.Current.BoundingRectangle,
                                    Tokens: FindById(run, "UsageDetailTokensValue")!.Current.BoundingRectangle);
                                var stable = previousDetail == current;
                                previousDetail = current;
                                return stable && current.Cost.Top >= current.Header.Bottom
                                    && current.Tokens.Top >= current.Header.Bottom;
                            }, "settled session animation with detail below its header");
                            OwnedWindowCapture.Save(run, window, $"Usage-{theme}-expanded-before-inspection.png");
                            await PrepareUsageCaptureAsync(run, window, $"Usage-{theme}-expanded-inspection");
                            // Keep the unscrolled, client-clipped aperture; UIA aggregate bounds can grow after scrolling.
                            Assert.Equal(frame, UsageWindowBounds(handle));
                            Assert.Equal(client, OwnedWindowCapture.ReadOwnedClientBounds(run, window));
                            var viewport = samplingAperture;
                            var detailBounds = expander.Current.BoundingRectangle;
                            var scrollableHeight = viewport.Height * (100 / scroll.Current.VerticalViewSize - 1);
                            Assert.True(scrollableHeight > 0);
                            var detailPercent = Math.Clamp(scroll.Current.VerticalScrollPercent +
                                100 * (detailBounds.Top - viewport.Top) / scrollableHeight, 0, 100);
                            scroll.SetScrollPercent(ScrollPattern.NoScroll, detailPercent);
                            var detailIds = new[] { "UsageRowTitle_agent:main:webchat:main",
                                "UsageDetailCostValue", "UsageDetailCostShare", "UsageDetailModelValue",
                                "UsageDetailTokensValue", "UsageDetailCacheValue", "UsageDetailMessagesValue",
                                "UsageDetailDurationValue", "UsageDetailToolsValue", "UsageDetailErrorsValue" };
                            System.Windows.Rect[]? previousAnchors = null;
                            AutomationElement[]? visibleAnchors = null;
                            await run.WaitForAsync(async () =>
                            {
                                await Task.Delay(UsageResizeSampleInterval);
                                Assert.Equal(frame, UsageWindowBounds(handle));
                                Assert.Equal(client, OwnedWindowCapture.ReadOwnedClientBounds(run, window));
                                var anchors = detailIds.Select(id => FindById(run, id)).ToArray();
                                if (anchors.Any(anchor => anchor is null || anchor.Current.IsOffscreen))
                                {
                                    previousAnchors = null;
                                    return false;
                                }
                                var bounds = anchors.Select(anchor => anchor!.Current.BoundingRectangle).ToArray();
                                if (bounds.Any(box => box.IsEmpty || !double.IsFinite(box.Width) ||
                                    !double.IsFinite(box.Height) || !double.IsFinite(box.Left) ||
                                    !double.IsFinite(box.Top) || box.Width <= 0 || box.Height <= 0 ||
                                    box.Left < viewport.Left - 1 || box.Right > viewport.Right + 1 ||
                                    box.Top < viewport.Top - 1 || box.Bottom > viewport.Bottom + 1))
                                {
                                    previousAnchors = null;
                                    return false;
                                }
                                var stable = previousAnchors is not null && previousAnchors.SequenceEqual(bounds);
                                previousAnchors = bounds;
                                visibleAnchors = anchors.Select(anchor => anchor!).ToArray();
                                return stable;
                            }, "complete expanded-session header and all detail values fit the owned visible aperture");
                            Assert.NotNull(visibleAnchors);
                            Assert.NotNull(previousAnchors);
                            var contentBounds = previousAnchors.Aggregate(System.Windows.Rect.Union);
                            Assert.True(contentBounds.Height <= viewport.Height + 1,
                                $"Expanded content cannot fit the owned viewport: content={contentBounds}; aperture={viewport}.");
                            File.WriteAllText(Path.Combine(run.ArtifactsDirectory, $"Usage-{theme}-expanded-inspection.json"),
                                JsonSerializer.Serialize(new
                                {
                                    samplingAperture = UsageBoundsForDiagnostic(viewport),
                                    window = frame, client = UsageBoundsForDiagnostic(client),
                                    anchors = detailIds.Zip(previousAnchors, (id, bounds) =>
                                        new { id, bounds = UsageBoundsForDiagnostic(bounds) }),
                                    automaticExpansionCapture = $"Usage-{theme}-expanded-before-inspection.png",
                                }));
                            AssertUsageCaptureUnobstructed(run, name);
                            OwnedWindowCapture.Save(run, window, $"Usage-{theme}-1400-expanded-session.png", visibleAnchors);
                            AssertUsageCaptureUnobstructed(run, name);
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
                if (FindById(run, "SettingsSearch") is { Current.IsOffscreen: false })
                    Invoke(FindById(run, "SettingsTogglePane")!);
                await WaitUiAsync(run, () => FindById(run, "SettingsSearch") is not { Current.IsOffscreen: false },
                    "collapsed Settings before collecting history-band proof");
                foreach (var width in new[] { 800, 1000 })
                {
                    var expected = ResizeUsageWindow(handle, width, scale, capToDisplay: false);
                    await WaitUiAsync(run, () => UsageWindowBounds(handle).Width == expected.Width,
                        $"collapsed Settings at {width} DIP");
                    await WaitUiAsync(run, () => FindById(run, "UsagePageMarker") is { } marker &&
                        FindById(run, "SettingsBack") is { } back && FindById(run, "SettingsForward") is { } forward &&
                        marker.Current.BoundingRectangle.Top >=
                            Math.Max(back.Current.BoundingRectangle.Bottom, forward.Current.BoundingRectangle.Bottom),
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
            var marker = FindById(run, markerId);
            var pane = FindById(run, "SettingsTogglePane");
            var navigation = FindById(run, "SettingsNavConnection");
            if (marker is null || pane is null || navigation is null) return false;
            var content = TreeWalker.ControlViewWalker.GetParent(marker);
            if (content is null) return false;
            var state = new
            {
                marker = marker.Current.AutomationId,
                markerBounds = marker.Current.BoundingRectangle,
                contentClass = content.Current.ClassName,
                contentBounds = content.Current.BoundingRectangle,
                paneLabel = pane.Current.Name,
                paneBounds = pane.Current.BoundingRectangle,
                navigationBounds = navigation.Current.BoundingRectangle,
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
                    inferredFrameTopMargin = 0,
                    inferenceLimit = "Page type and margin follow source plus observed markers, not runtime field reads. Stable UIA bounds do not prove compositor transition continuity.",
                    samples,
                }, new JsonSerializerOptions { WriteIndented = true }));
            var ready = !marker.Current.IsOffscreen
                && state.searchVisible == paneOpen
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
        const double minimumWindowWidthDip = 800;
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
            double PaneItemWidth() => FindById(run, "SettingsNavConnection")!.Current.BoundingRectangle.Width / scale;
            (double Host, double Pane)? ReadWidths()
            {
                try
                {
                    // Responsive reconciliation can temporarily remove either UIA peer. Missing
                    // geometry is not a zero-width sample and cannot establish layout stability.
                    if (FindById(run, "UsagePageRoot") is not { Current.IsOffscreen: false } host ||
                        FindById(run, "SettingsNavConnection") is not { Current.IsOffscreen: false } pane) return null;
                    var hostWidth = host.Current.BoundingRectangle.Width / scale;
                    var paneWidth = pane.Current.BoundingRectangle.Width / scale;
                    return double.IsFinite(hostWidth) && hostWidth > 0 && double.IsFinite(paneWidth) && paneWidth > 0
                        ? (hostWidth, paneWidth) : null;
                }
                catch (ElementNotAvailableException) { return null; }
            }
            async Task StableHost()
            {
                var previous = ReadWidths();
                await run.WaitForAsync(async () =>
                {
                    await Task.Delay(UsageResizeSampleInterval);
                    var current = ReadWidths();
                    var stable = previous is { } before && current is { } after &&
                        Math.Abs(before.Host - after.Host) < 0.01 && Math.Abs(before.Pane - after.Pane) < 0.01;
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
                            var expected = ResizeUsageWindow(handle, target == 0 ? minimumWindowWidthDip : hostTarget + chrome, scale, capToDisplay: target == 1400);
                            await WaitUiAsync(run, () =>
                            {
                                var rect = UsageWindowBounds(handle);
                                return rect.Width == expected.Width && rect.Height == expected.Height;
                            }, $"exact {name} window pixel bounds");
                        }
                        await WaitUiAsync(run, () => ReadWidths() is { } measured &&
                            Math.Abs(measured.Host - beforeHost) > 1, $"changed {name} host width");
                        await StableHost();
                        System.Windows.Rect? previousAnchor = null;
                        await WaitUiAsync(run, () =>
                        {
                            if (FindById(run, "UsageExport") is not { Current.IsOffscreen: false } export)
                            {
                                previousAnchor = null;
                                return false;
                            }
                            var anchor = export.Current.BoundingRectangle;
                            var frame = UsageWindowBounds(handle);
                            var stable = previousAnchor is { } previous && previous == anchor;
                            previousAnchor = anchor;
                            return stable && !anchor.IsEmpty && anchor.Width > 0 && anchor.Height > 0 &&
                                anchor.Left >= frame.Left && anchor.Top >= frame.Top &&
                                anchor.Right <= frame.Right && anchor.Bottom <= frame.Bottom;
                        }, "stable mast action fully inside the resized Hub");
                        var bounds = UsageWindowBounds(handle);
                        var hostWidth = HostWidth();
                        var sizeKey = $"{bounds.Width}x{bounds.Height}:{hostWidth:F2}";
                        var file = $"Usage-Light-responsive-{name}.png";
                        await PrepareUsageCaptureAsync(run, window, name);
                        AssertUsageCaptureUnobstructed(run, name);
                        OwnedWindowCapture.Save(run, window, file, FindById(run, "UsagePageMarker")!);
                        // A late asynchronous probe notification invalidates the capture rather
                        // than supplying unrelated dark pixels to the label ink detector.
                        AssertUsageCaptureUnobstructed(run, name);
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
                        if (target <= 0) Assert.InRange(Math.Abs(bounds.Width / scale - minimumWindowWidthDip), 0, 1);
                        Assert.True(sizes.Add(sizeKey), "The capture must have distinct measured bounds.");
                        Assert.True(hashes.Add(sha), "The capture is byte-identical to another width.");
                        Assert.Equal(target >= 1000 ? PageWidthTier.Wide
                                : target == 0 ? PageWidthTier.Narrow : PageWidthTier.Medium,
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
        }, scenario: GatewayScenario.CreateUsageAnalytics(UsageAnalyticsMode.Released, completeDailyCoverage: true));
    }

    private static async Task PrepareUsageCaptureAsync(GatewayFixtureRun run, AutomationElement window, string stateName)
    {
        // The host probe publishes asynchronously. Observe the real banner at every capture,
        // not once at startup; absence here is not a claim that the probe has completed.
        // Close only the known host-warning presentation. The warning remains in the Inbox,
        // and containment policy/capability checks remain unchanged.
        const string title = "Windows containment unavailable: commands run uncontained";
        if (FindById(run, "AppNotificationInfoBar") is not { Current.IsOffscreen: false } notification) return;
        Assert.Equal(run.AppProcessId, notification.Current.ProcessId);
        var knownTitle = notification.Current.Name == title || notification.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, title)) is not null;
        Assert.True(knownTitle, "An unexpected notification obstructs Usage capture; it must not be silently dismissed.");
        var close = notification.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "CloseButton"));
        Assert.NotNull(close);
        Assert.Equal(run.AppProcessId, close.Current.ProcessId);
        Assert.True(close.Current.IsEnabled);
        OwnedWindowCapture.Save(run, window, stateName + "-host-warning-before-close.png", notification, close);
        Invoke(close);
        await WaitUiAsync(run, () => FindById(run, "AppNotificationInfoBar") is not { Current.IsOffscreen: false },
            "known host-warning banner closed through its ordinary presentation control");
    }

    private static void AssertUsageCaptureUnobstructed(GatewayFixtureRun run, string stateName) =>
        Assert.False(FindById(run, "AppNotificationInfoBar") is { Current.IsOffscreen: false },
            stateName + ": a notification appeared at the capture boundary; its pixels are not Usage ink proof.");

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
