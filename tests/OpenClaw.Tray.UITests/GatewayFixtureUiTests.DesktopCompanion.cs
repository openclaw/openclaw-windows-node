// Proves native mascot-click typing through the real retained composer.
// Run with the fixture/keyboard opt-ins and --filter FullyQualifiedName~DesktopLobster_Click.
using System.Runtime.InteropServices;
using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.Shared;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    [Trait("KeyboardProof", "Required")]
    public Task DesktopLobster_ClickTypesIntoRealComposer_AfterExternalEditorFocus_AndRetainsDraft() =>
        WithAppAsync(async run =>
        {
            if (Environment.GetEnvironmentVariable("OPENCLAW_RUN_KEYBOARD_PROOF") != "1")
                throw new InvalidOperationException("Explicit native keyboard proof is required.");
            await WaitForElementByIdAsync(run, "DesktopCompanionMascot", "desktop lobster");
            using var other = OwnedFocusProcess.Create(editor: true);
            var saved = WithPhysicalCursorCoordinates(() =>
            {
                Assert.True(GetCursorPos(out var point));
                return point;
            });
            Assert.True(other.Start());
            try
            {
                AutomationElement? external = null;
                await WaitUiAsync(run, () =>
                {
                    Assert.False(other.HasExited, "The owned external editor exited before readiness.");
                    other.Refresh();
                    if (other.MainWindowHandle == IntPtr.Zero) return false;
                    external = AutomationElement.FromHandle(other.MainWindowHandle).FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "OwnedFocusControl"));
                    return external is { Current.IsOffscreen: false, Current.IsEnabled: true };
                }, "owned external textbox");
                var externalEditor = external!;
                int[]? editorIdentity = null;
                var expected = "";
                foreach (var text in new[] { "lobster", " again" })
                {
                    await ClickFocusTargetWithoutActivationAsync(run, externalEditor, other.Id);
                    await WaitUiAsync(run, () => ForegroundBelongsTo(other.Id) &&
                        externalEditor.Current.HasKeyboardFocus, "external textbox owns native input");
                    var mascot = await WaitForElementByIdAsync(run, "DesktopCompanionMascot", "clickable lobster");
                    // Do not activate the app or focus the editor on behalf of the production click path.
                    await ClickFocusTargetWithoutActivationAsync(run, mascot, run.AppProcessId);
                    AutomationElement? input = null;
                    AutomationElement? popup = null;
                    await WaitUiAsync(run, () =>
                    {
                        popup = AppWindows(run).FirstOrDefault(window => window.FindFirst(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.AutomationIdProperty, "DesktopCompanionDismiss"))
                            is { Current.IsOffscreen: false });
                        input = popup?.FindFirst(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.AutomationIdProperty, "ChatComposerInput"));
                        return input is { Current.IsOffscreen: false, Current.IsEnabled: true, Current.HasKeyboardFocus: true }
                            && ForegroundBelongsTo(run.AppProcessId);
                    }, "real lobster composer owns input after clicking only the mascot");
                    var editor = input!;
                    var bubble = popup!;
                    if (editorIdentity is null) editorIdentity = editor.GetRuntimeId();
                    else Assert.Equal(editorIdentity, editor.GetRuntimeId());
                    Assert.Equal(expected, ((ValuePattern)editor.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
                    var foreground = GetForegroundWindow();
                    GetWindowThreadProcessId(foreground, out var foregroundProcess);
                    Assert.Equal((uint)run.AppProcessId, foregroundProcess);
                    NativeKeyboardProof.TypeText(foreground, text);
                    expected += text;
                    await WaitUiAsync(run, () =>
                        ((ValuePattern)editor.GetCurrentPattern(ValuePattern.Pattern)).Current.Value == expected,
                        "native keystrokes in the retained production composer");
                    Assert.Empty(((ValuePattern)externalEditor.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
                    if (text == "lobster")
                    {
                        var dismiss = bubble.FindFirst(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.AutomationIdProperty, "DesktopCompanionDismiss"));
                        Assert.NotNull(dismiss);
                        Invoke(dismiss);
                        await WaitUiAsync(run, () => editor.Current.IsOffscreen, "lobster chat dismissed with draft retained");
                    }
                }
            }
            finally
            {
                try
                {
                    WithPhysicalCursorCoordinates(() =>
                    {
                        Assert.True(SetCursorPos(saved.X, saved.Y));
                        return true;
                    });
                }
                finally
                {
                    if (!other.HasExited)
                    {
                        other.Kill();
                        await other.WaitForExitAsync().WaitAsync(TestSupport.RenderedConditionTimeout);
                    }
                }
            }
        }, allowSessionMutations: true, configureProfile: profile =>
        {
            var path = Path.Combine(profile.DataDirectory, "settings.json");
            var settings = SettingsData.FromJson(File.ReadAllText(path));
            Assert.NotNull(settings);
            settings.ShowDesktopCompanion = true;
            File.WriteAllText(path, settings.ToJson());
        });

    private static async Task ClickFocusTargetWithoutActivationAsync(
        GatewayFixtureRun run, AutomationElement target, int processId)
    {
        System.Windows.Rect? previous = null;
        FixturePoint point = default;
        await WaitUiAsync(run, () => WithPhysicalCursorCoordinates(() =>
        {
            var bounds = target.Current.BoundingRectangle;
            var stable = previous == bounds;
            previous = bounds;
            if (!stable || target.Current.IsOffscreen || !target.Current.IsEnabled || bounds.Width <= 0 || bounds.Height <= 0)
                return false;
            point = new() { X = (int)(bounds.Left + bounds.Width / 2), Y = (int)(bounds.Top + bounds.Height / 2) };
            GetWindowThreadProcessId(WindowFromPoint(point), out var owner);
            if (owner == 0) return false;
            Assert.Equal((uint)processId, owner);
            Assert.True(SetCursorPos(point.X, point.Y));
            return true;
        }), "stable owned click target");
        await WaitUiAsync(run, () => WithPhysicalCursorCoordinates(() =>
            GetCursorPos(out var current) && current.Equals(point)), "cursor reaches owned click target");
        WithPhysicalCursorCoordinates(() =>
        {
            Assert.True(GetCursorPos(out var current));
            Assert.Equal(point, current);
            Assert.True(target.Current.BoundingRectangle.Contains(new System.Windows.Point(point.X, point.Y)));
            GetWindowThreadProcessId(WindowFromPoint(current), out var owner);
            Assert.Equal((uint)processId, owner);
            var inputs = new[]
            {
                new FixtureInput { Mouse = new FixtureMouse { Flags = 2 } },
                new FixtureInput { Mouse = new FixtureMouse { Flags = 4 } },
            };
            var sent = SendInput(2, inputs, Marshal.SizeOf<FixtureInput>());
            if (sent == 1) SendInput(1, [inputs[1]], Marshal.SizeOf<FixtureInput>());
            Assert.Equal(2u, sent);
            return true;
        });
    }
}
