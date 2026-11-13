// Exercises native ownership with two windows on the existing WinUI dispatcher, not another app boot.
// Run with the NativeUiOwnershipTests filter on an isolated Windows desktop.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.GatewayFixtureHost;
using System.Windows.Automation;

namespace OpenClaw.Tray.UITests;

/// <summary>Proves that native ancestry admits owned XAML and rejects foreign or unverifiable elements.</summary>
[Collection(UICollection.Name)]
public sealed class NativeUiOwnershipTests(UIThreadFixture ui)
{
    [Fact]
    public async Task OwnedXaml_RejectsAnotherWindowAndWrongOrUnverifiableOwners()
    {
        Window? first = null;
        Window? second = null;
        try
        {
            var handles = await ui.RunOnUIAsync(() =>
            {
                first = CreateWindow();
                second = CreateWindow();
                return Task.FromResult((
                    First: WinRT.Interop.WindowNative.GetWindowHandle(first),
                    Second: WinRT.Interop.WindowNative.GetWindowHandle(second)));
            });
            await ui.YieldToRenderAsync();
            Assert.NotEqual(IntPtr.Zero, handles.First);
            Assert.NotEqual(IntPtr.Zero, handles.Second);
            var owner = AutomationElement.FromHandle(handles.First);
            var other = AutomationElement.FromHandle(handles.Second);
            var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, "OwnershipProofControl");
            var ownedControl = owner.FindFirst(TreeScope.Descendants, condition);
            var otherControl = other.FindFirst(TreeScope.Descendants, condition);
            Assert.NotNull(ownedControl);
            Assert.NotNull(otherControl);
            Assert.Equal(0, ownedControl.Current.NativeWindowHandle);

            Assert.True(NativeUiOwnership.IsOwnedDescendant(owner, ownedControl, Environment.ProcessId));
            Assert.False(NativeUiOwnership.IsOwnedDescendant(owner, otherControl, Environment.ProcessId));
            Assert.False(NativeUiOwnership.IsOwnedDescendant(other, ownedControl, Environment.ProcessId));
            Assert.False(NativeUiOwnership.IsOwnedDescendant(owner, ownedControl, Environment.ProcessId + 1));
            Assert.False(NativeUiOwnership.IsOwnedDescendant(owner, AutomationElement.RootElement, Environment.ProcessId));
            Assert.False(NativeUiOwnership.IsOwnedDescendant(ownedControl, ownedControl, Environment.ProcessId));
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                second?.Close();
                first?.Close();
            });
        }
    }

    [Fact]
    public async Task ViewportRead_WaitsForTargetWindowAndRejectsChangedSnapshot()
    {
        await WithViewportWindowsAsync(async (target, owner, decoy) =>
        {
            var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, "CaptureViewport");
            var decoyViewport = decoy.FindFirst(TreeScope.Descendants, condition);
            Assert.NotNull(decoyViewport);
            Assert.Null(owner.FindFirst(TreeScope.Descendants, condition));
            var decoyId = decoyViewport.GetRuntimeId();
            var firstObservation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseMount = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var mount = ui.RunOnUIAsync(async () =>
            {
                await releaseMount.Task;
                target.Content = CreateViewport();
                await TestSupport.WaitForRenderedConditionAsync(() =>
                    target.Content is ScrollViewer { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 },
                    "target viewport mounted after admission was withheld");
            });
            OwnedWindowCapture.ViewportSnapshot? observed = null;
            var wait = GatewayFixtureRun.WaitForConditionAsync(() =>
            {
                try
                {
                    observed = OwnedWindowCapture.TryReadViewport(owner, Environment.ProcessId, "CaptureViewport");
                    return Task.FromResult(observed is not null);
                }
                finally { firstObservation.TrySetResult(); }
            }, "viewport in target window", System.IO.Path.GetTempPath(), () => { });
            try
            {
                await Task.WhenAny(firstObservation.Task, wait);
                Assert.False(wait.IsCompleted, "A viewport in another window must not complete target admission.");
                Assert.Null(observed);
                Assert.False(mount.IsCompleted);
                await ui.RunOnUIAsync(() => Assert.IsType<Grid>(target.Content));
                releaseMount.SetResult();
                await mount;
                await wait;
                Assert.NotNull(observed);
                var actualTarget = owner.FindFirst(TreeScope.Descendants, condition);
                Assert.NotNull(actualTarget);
                Assert.Equal(actualTarget.GetRuntimeId(), observed.Element.GetRuntimeId());
                Assert.False(decoyId.SequenceEqual(observed.Element.GetRuntimeId()));
                Assert.Equal(actualTarget.Current.BoundingRectangle, observed.Bounds);
                Assert.True(NativeUiOwnership.IsOwnedDescendant(owner, observed.Element, Environment.ProcessId));
                observed.AssertUnchanged();

                await ui.RunOnUIAsync(async () =>
                {
                    var viewport = Assert.IsType<ScrollViewer>(target.Content);
                    var widthBefore = viewport.ActualWidth;
                    viewport.Margin = new Thickness(24);
                    await TestSupport.WaitForRenderedConditionAsync(() =>
                        viewport.ActualWidth < widthBefore - 24,
                        "native target viewport geometry changed");
                });
                Assert.NotEqual(observed.Bounds, actualTarget.Current.BoundingRectangle);
                Assert.ThrowsAny<Xunit.Sdk.XunitException>(observed.AssertUnchanged);
            }
            finally
            {
                releaseMount.TrySetResult();
                await mount;
                // Drain the same bounded read before closing its windows, including baseline failures.
                await wait;
            }
        });
    }

    [Fact]
    public async Task ViewportRead_MissingTargetTimesOutWithoutAdmittingOtherWindow()
    {
        await WithViewportWindowsAsync(async (target, owner, decoy) =>
        {
            var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, "CaptureViewport");
            Assert.NotNull(decoy.FindFirst(TreeScope.Descendants, condition));
            Assert.Null(owner.FindFirst(TreeScope.Descendants, condition));
            OwnedWindowCapture.ViewportSnapshot? observed = null;
            // Both windows are settled and only the decoy hosts the viewport, so a short bounded
            // observation proves the read never admits the other window without the 20 s default.
            await Assert.ThrowsAsync<TimeoutException>(() => GatewayFixtureRun.WaitForConditionAsync(() =>
            {
                observed = OwnedWindowCapture.TryReadViewport(owner, Environment.ProcessId, "CaptureViewport");
                return Task.FromResult(observed is not null);
            }, "never-mounted viewport in target window", System.IO.Path.GetTempPath(), () => { },
                TestSupport.NegativeObservationTimeout));
            Assert.Null(observed);
            await ui.RunOnUIAsync(() => Assert.IsType<Grid>(target.Content));
        });
    }

    private async Task WithViewportWindowsAsync(Func<Window, AutomationElement, AutomationElement, Task> test)
    {
        Window? target = null;
        Window? decoy = null;
        try
        {
            var handles = await ui.RunOnUIAsync(async () =>
            {
                target = new Window { Content = new Grid() };
                decoy = new Window { Content = CreateViewport() };
                target.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(20, 20, 400, 400));
                decoy.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(440, 20, 400, 400));
                decoy.Activate();
                target.Activate();
                await TestSupport.WaitForRenderedConditionAsync(() =>
                    target.Content is Grid { IsLoaded: true, ActualWidth: > 0 } &&
                    decoy.Content is ScrollViewer { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 },
                    "two real native viewport-observer windows ready");
                return (Target: WinRT.Interop.WindowNative.GetWindowHandle(target),
                    Decoy: WinRT.Interop.WindowNative.GetWindowHandle(decoy));
            });
            Assert.NotEqual(handles.Target, handles.Decoy);
            var owner = AutomationElement.FromHandle(handles.Target);
            var other = AutomationElement.FromHandle(handles.Decoy);
            Assert.Equal(Environment.ProcessId, owner.Current.ProcessId);
            Assert.Equal(Environment.ProcessId, other.Current.ProcessId);
            Assert.False(owner.Current.IsOffscreen);
            Assert.False(other.Current.IsOffscreen);
            await test(target!, owner, other);
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                decoy?.Close();
                target?.Close();
            });
        }
    }

    private static ScrollViewer CreateViewport()
    {
        var viewport = new ScrollViewer { Content = new Border { Height = 800 } };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(viewport, "CaptureViewport");
        return viewport;
    }

    private static Window CreateWindow()
    {
        var button = new Button { Content = "Ownership fixture" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, "OwnershipProofControl");
        var window = new Window { Content = button };
        window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-32000, -32000, 400, 300));
        window.Activate();
        return window;
    }
}
