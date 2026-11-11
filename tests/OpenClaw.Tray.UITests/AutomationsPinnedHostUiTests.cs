using System.Windows.Automation;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

/// <summary>Exercises pinned Automations chrome with native point ownership and Tab input in the isolated fixture.</summary>
public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [Trait("Category", "GatewayFixture")]
    [Trait("KeyboardProof", "Required")]
    public async Task Automations_PinnedHostToggleHitTabAndBanner(string theme)
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("OPENCLAW_RUN_KEYBOARD_PROOF"));
        await WithAppAsync(async run =>
        {
            await SelectAppThemeAsync(run, theme);
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "WorkspacePagesCustomize")?.Current.IsOffscreen == false,
                "Chat customization");
            var window = FindHub(run);
            ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Normal);
            var scale = OwnedWindowCapture.OwnedDpiScale(run, window);
            OwnedWindowCapture.ResizeOwnedPixels(run, window, (int)Math.Round(1000 * scale), (int)Math.Round(900 * scale));
            await WaitUiAsync(run, () => Math.Abs(OwnedWindowCapture.ReadOwnedBounds(run, window).Width / scale - 1000) <= 2,
                "1000-DIP pinned workspace");
            Invoke(FindById(run, "WorkspacePagesCustomize")!);
            await WaitUiAsync(run, () => FindById(run, "WorkspacePagesToggle:cron") is not null, "Automations pin toggle");
            Invoke(FindById(run, "WorkspacePagesToggle:cron")!);
            await WaitUiAsync(run, () => FindById(run, "WorkspacePinnedPage:cron") is not null, "Automations pin");
            Invoke(FindById(run, "WorkspacePinnedPage:cron")!);
            await WaitUiAsync(run, () => FindById(run, "AutomationExpander:morning-brief")?.Current.IsOffscreen == false,
                "populated pinned Automations");
            var scroll = PageScrollPeer(run);
            var scrollIdentity = scroll.GetRuntimeId();
            System.Windows.Rect Bounds(AutomationElement element) =>
                OwnedWindowCapture.WithPhysicalCoordinates(() => element.Current.BoundingRectangle);
            var openScrollTop = Bounds(scroll).Top;
            var openHostTop = Bounds(FindById(run, "WorkspaceSettingsHost")!).Top;

            bool IsWithin(AutomationElement element, AutomationElement ancestor)
            {
                for (AutomationElement? current = element; current is not null;
                     current = TreeWalker.RawViewWalker.GetParent(current))
                    if (current.GetRuntimeId().SequenceEqual(ancestor.GetRuntimeId())) return true;
                return false;
            }
            void AppendRawAncestry(AutomationElement element, List<object> ancestors, Action persist)
            {
                for (AutomationElement? current = element; current is not null;
                     current = TreeWalker.RawViewWalker.GetParent(current))
                {
                    var info = current.Current;
                    ancestors.Add(new { runtimeId = current.GetRuntimeId(), info.ProcessId, info.NativeWindowHandle,
                        info.ClassName, controlType = info.ControlType.ProgrammaticName, info.AutomationId,
                        bounds = info.BoundingRectangle.ToString() });
                    persist();
                }
            }
            var hitAttempt = 0;
            AutomationElement HitToggle(string id)
            {
                var receipt = new Dictionary<string, object?> { ["capturedUtc"] = DateTimeOffset.UtcNow,
                    ["theme"] = theme, ["targetId"] = id, ["appPid"] = run.AppProcessId,
                    ["runnerPid"] = Environment.ProcessId, ["phase"] = "before-target-query" };
                var receiptPath = Path.Combine(run.ArtifactsDirectory, $"pinned-toggle-hit-{++hitAttempt:D2}.json");
                void Persist() => File.WriteAllText(receiptPath, System.Text.Json.JsonSerializer.Serialize(receipt,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Persist();
                var button = FindById(run, id) ?? throw new InvalidOperationException($"Missing {id}.");
                Assert.False(button.Current.IsOffscreen);
                Assert.True(button.Current.IsEnabled);
                OwnedWindowCapture.WithPhysicalCoordinates(() =>
                {
                    var bounds = button.Current.BoundingRectangle;
                    Assert.True(bounds.Width > 0 && bounds.Height > 0);
                    var point = new FixturePoint { X = (int)(bounds.Left + bounds.Width / 2),
                        Y = (int)(bounds.Top + bounds.Height / 2) };
                    receipt["point"] = new { x = bounds.Left + bounds.Width / 2, y = bounds.Top + bounds.Height / 2 };
                    receipt["targetBounds"] = bounds.ToString();
                    receipt["windowBounds"] = window.Current.BoundingRectangle.ToString();
                    receipt["expectedHwnd"] = window.Current.NativeWindowHandle;
                    receipt["foregroundHwnd"] = GetForegroundWindow().ToInt64();
                    receipt["phase"] = "before-native-point-query";
                    Persist();
                    var hitHwnd = WindowFromPoint(point);
                    GetWindowThreadProcessId(hitHwnd, out var hitPid);
                    receipt["hitHwnd"] = hitHwnd.ToInt64();
                    receipt["hitRootHwnd"] = GetAncestor(hitHwnd, 2).ToInt64();
                    receipt["hitPid"] = hitPid;
                    receipt["phase"] = "before-uia-point-query";
                    Persist();
                    var hit = AutomationElement.FromPoint(new System.Windows.Point(
                        bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2));
                    var targetAncestors = new List<object>();
                    var hitAncestors = new List<object>();
                    receipt["target"] = targetAncestors;
                    receipt["hit"] = hitAncestors;
                    receipt["phase"] = "target-ancestry";
                    Persist();
                    AppendRawAncestry(button, targetAncestors, Persist);
                    receipt["phase"] = "hit-ancestry";
                    Persist();
                    AppendRawAncestry(hit, hitAncestors, Persist);
                    receipt["phase"] = "ownership-predicate";
                    Persist();
                    var ownsCenter = IsWithin(hit, button);
                    receipt["ownsCenter"] = ownsCenter;
                    receipt["phase"] = "completed";
                    Persist();
                    Assert.True(ownsCenter, $"{id} center ownership failed. See {Path.GetFileName(receiptPath)}.");
                    return true;
                });
                return button;
            }
            bool AllocationSettled(bool collapsed) =>
                Math.Abs((Bounds(scroll).Top - openScrollTop) / scale - (collapsed ? 56 : 0)) <= 1 &&
                Math.Abs((Bounds(FindById(run, "WorkspaceSettingsHost")!).Top - openHostTop) / scale) <= 1;
            void AssertAllocation(bool collapsed)
            {
                Assert.Equal(scrollIdentity, PageScrollPeer(run).GetRuntimeId());
                Assert.InRange(Math.Abs((Bounds(scroll).Top - openScrollTop) / scale -
                    (collapsed ? 56 : 0)), 0, 1);
                Assert.InRange(Math.Abs((Bounds(FindById(run, "WorkspaceSettingsHost")!).Top -
                    openHostTop) / scale), 0, 1);
            }
            void RecordFocus(string step)
            {
                var focused = AutomationElement.FocusedElement;
                var entry = new { capturedUtc = DateTimeOffset.UtcNow, step, focused.Current.AutomationId,
                    runtimeId = focused.GetRuntimeId(), focused.Current.ClassName,
                    controlType = focused.Current.ControlType.ProgrammaticName };
                File.AppendAllText(Path.Combine(run.ArtifactsDirectory, "pinned-tab-focus.jsonl"),
                    System.Text.Json.JsonSerializer.Serialize(entry) + Environment.NewLine);
            }
            bool BannerSettled(string toggleId)
            {
                var banner = FindById(run, "WorkspaceEmbeddedOperationFeedback");
                return banner is { Current.IsOffscreen: false } &&
                    Bounds(FindById(run, toggleId)!).Bottom <= Bounds(banner).Top &&
                    new[] { "SettingsBack", "SettingsForward" }.All(id =>
                        Bounds(FindById(run, id)!).Bottom <= Bounds(banner).Top);
            }

            Invoke(HitToggle("WorkspaceTogglePane"));
            await WaitUiAsync(run, () => FindById(run, "WorkspaceReopenPane")?.Current.IsOffscreen == false,
                "collapsed Chat");
            await WaitUiAsync(run, () => AllocationSettled(collapsed: true), "settled collapsed allocation");
            AssertAllocation(collapsed: true);
            var reopen = HitToggle("WorkspaceReopenPane");
            var toggleBounds = Bounds(reopen);
            foreach (var id in new[] { "SettingsBack", "SettingsForward" })
                Assert.True(toggleBounds.Right <= Bounds(FindById(run, id)!).Left,
                    $"Reopen overlaps {id}.");
            Capture(run, $"automations-pinned-{theme}-collapsed.png", "WorkspaceReopenPane",
                "SettingsBack", "AutomationExpander:morning-brief");

            reopen.SetFocus();
            await WaitUiAsync(run, () => reopen.Current.HasKeyboardFocus, "Reopen keyboard focus");
            RecordFocus("Reopen");
            foreach (var id in new[] { "SettingsBack", "SettingsForward" })
            {
                var history = FindById(run, id)!;
                if (!history.Current.IsEnabled) continue;
                AssertOwnedForeground(run, new IntPtr(window.Current.NativeWindowHandle), $"before-Tab-{id}");
                Send(run, window, "{TAB}");
                RecordFocus($"after-Tab-{id}");
                await WaitUiAsync(run, () => history.Current.HasKeyboardFocus, $"native Tab to {id}");
                RecordFocus($"settled-{id}");
            }
            AssertOwnedForeground(run, new IntPtr(window.Current.NativeWindowHandle), "before-Tab-page");
            Send(run, window, "{TAB}");
            RecordFocus("after-Tab-page");
            await WaitUiAsync(run, () => IsWithin(AutomationElement.FocusedElement, scroll),
                "native Tab from history into Automations scroll content");
            RecordFocus("settled-page");
            Invoke(HitToggle("WorkspaceReopenPane"));
            await WaitUiAsync(run, () => FindById(run, "WorkspaceTogglePane")?.Current.IsOffscreen == false,
                "Chat reopened");
            await WaitUiAsync(run, () => AllocationSettled(collapsed: false), "settled reopened allocation");
            AssertAllocation(collapsed: false);
            HitToggle("WorkspaceTogglePane");
            Capture(run, $"automations-pinned-{theme}-reopened.png", "WorkspaceTogglePane", "AutomationExpander:morning-brief");

            // Session creation is rejected by the fixture through the normal Workspace operation owner.
            Invoke(FindById(run, "WorkspaceSessionsAdd")!);
            await WaitUiAsync(run, () => FindById(run, "WorkspaceEmbeddedOperationFeedback")?.Current.IsOffscreen == false,
                "explicit operation error");
            Assert.Contains("Fixture Gateway is read-only", FindText(run, "Fixture Gateway is read-only")!.Current.Name);
            Invoke(HitToggle("WorkspaceTogglePane"));
            await WaitUiAsync(run, () => FindById(run, "WorkspaceReopenPane")?.Current.IsOffscreen == false,
                "collapsed Chat with banner");
            await WaitUiAsync(run, () => AllocationSettled(collapsed: true) && BannerSettled("WorkspaceReopenPane"),
                "settled collapsed allocation and banner");
            AssertAllocation(collapsed: true);
            reopen = HitToggle("WorkspaceReopenPane");
            var feedback = FindById(run, "WorkspaceEmbeddedOperationFeedback")!;
            Assert.False(feedback.Current.IsOffscreen);
            Assert.True(Bounds(reopen).Bottom <= Bounds(feedback).Top,
                "Banner overlaps Reopen.");
            foreach (var id in new[] { "SettingsBack", "SettingsForward" })
                Assert.True(Bounds(FindById(run, id)!).Bottom <= Bounds(feedback).Top,
                    $"Banner overlaps {id}.");
            Capture(run, $"automations-pinned-{theme}-collapsed-banner.png", "WorkspaceReopenPane",
                "WorkspaceEmbeddedOperationFeedback", "AutomationExpander:morning-brief");
            Invoke(HitToggle("WorkspaceReopenPane"));
            await WaitUiAsync(run, () => FindById(run, "WorkspaceTogglePane")?.Current.IsOffscreen == false,
                "Chat reopened with banner");
            await WaitUiAsync(run, () => AllocationSettled(collapsed: false) && BannerSettled("WorkspaceTogglePane"),
                "settled reopened allocation and banner");
            AssertAllocation(collapsed: false);
            Assert.False(FindById(run, "WorkspaceEmbeddedOperationFeedback")!.Current.IsOffscreen);
            Assert.True(BannerSettled("WorkspaceTogglePane"), "Reopened banner overlaps navigation chrome.");
            Capture(run, $"automations-pinned-{theme}-reopened-banner.png", "WorkspaceTogglePane",
                "WorkspaceEmbeddedOperationFeedback", "AutomationExpander:morning-brief");
        }, scenario: GatewayScenario.CreateAutomations());
    }
}
