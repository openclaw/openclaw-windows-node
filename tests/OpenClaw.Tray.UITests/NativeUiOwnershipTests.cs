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
            // Every rejection above carries positive contrary evidence; polls must fail on these, not wait.
            Assert.Equal(NativeUiOwnership.Evidence.Foreign, NativeUiOwnership.Classify(owner, otherControl, Environment.ProcessId));
            Assert.Equal(NativeUiOwnership.Evidence.Foreign, NativeUiOwnership.Classify(other, ownedControl, Environment.ProcessId));
            Assert.Equal(NativeUiOwnership.Evidence.Foreign, NativeUiOwnership.Classify(owner, ownedControl, Environment.ProcessId + 1));
            Assert.Equal(NativeUiOwnership.Evidence.Foreign, NativeUiOwnership.Classify(owner, AutomationElement.RootElement, Environment.ProcessId));
            Assert.Equal(NativeUiOwnership.Evidence.Foreign, NativeUiOwnership.Classify(ownedControl, ownedControl, Environment.ProcessId));
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

/// <summary>
/// Deterministic Owned/Unproven/Foreign rules over synthetic ancestry. SystemsDaily failed on CI when a
/// re-rendered peer was unavailable mid-walk; polls must keep observing on Unproven, but any positive
/// evidence of another process or window must stay Foreign so it fails immediately.
/// </summary>
public sealed class NativeUiOwnershipClassificationTests
{
    private const int AppPid = 4242;
    private const int OtherPid = 777;
    private const int OwnerHwnd = 0x100;
    private const int OtherAppWindowHwnd = 0x200;
    private const int ForeignHwnd = 0x300;
    private const int DesktopHwnd = 0x10010;
    private const int DestroyedHwnd = 0x400;

    private sealed class Node(int processId, string framework = "XAML", int hwnd = 0, bool isWindow = false, Node? parent = null)
    {
        private static int nextId = 1;
        public int ProcessId = processId;
        public int Hwnd = hwnd;
        public bool Unavailable;
        public bool PartialRead;
        public Node? Parent = parent;
        public readonly int[] RuntimeId = [42, Interlocked.Increment(ref nextId)];
        public NativeUiOwnership.NodeFacts Facts => Unavailable
            ? throw new ElementNotAvailableException()
            : PartialRead ? new(ProcessId, null, Hwnd, false, null)
            : new(ProcessId, framework, Hwnd, isWindow, RuntimeId);
    }

    private static uint WindowProcess(int hwnd) => hwnd switch
    {
        OwnerHwnd or OtherAppWindowHwnd => AppPid,
        ForeignHwnd or DesktopHwnd => OtherPid,
        _ => 0,
    };

    private static NativeUiOwnership.Evidence Classify(Node window, Node element) =>
        NativeUiOwnership.Classify(window, element, AppPid, node => node.Facts, node => node.Parent, WindowProcess);

    private static Node Owner() => new(AppPid, "Win32", OwnerHwnd, isWindow: true);

    [Fact]
    public void WindowlessXamlReachingTheOwner_IsOwned()
    {
        var owner = Owner();
        var control = new Node(0, parent: new Node(AppPid, parent: owner));
        Assert.Equal(NativeUiOwnership.Evidence.Owned, Classify(owner, control));
    }

    [Fact]
    public void MissingEvidence_IsUnproven()
    {
        var owner = Owner();
        var pane = new Node(AppPid, parent: owner);
        Assert.Equal(NativeUiOwnership.Evidence.Unproven, Classify(owner, new Node(AppPid)));
        Assert.Equal(NativeUiOwnership.Evidence.Unproven, Classify(owner, new Node(0, "Win32", parent: pane)));
        Assert.Equal(NativeUiOwnership.Evidence.Unproven, Classify(owner, new Node(AppPid, hwnd: DestroyedHwnd, parent: pane)));
        var torn = new Node(AppPid, parent: pane) { Unavailable = true };
        Assert.Equal(NativeUiOwnership.Evidence.Unproven, Classify(owner, new Node(AppPid, parent: torn)));
        Assert.Equal(NativeUiOwnership.Evidence.Unproven, Classify(owner, torn));
        // The peer vanished after its process id was read: no contrary evidence, so not proven yet.
        Assert.Equal(NativeUiOwnership.Evidence.Unproven, Classify(owner, new Node(AppPid, parent: pane) { PartialRead = true }));
    }

    [Fact]
    public void PositiveContraryEvidence_IsForeign()
    {
        var owner = Owner();
        var pane = new Node(AppPid, parent: owner);
        var desktop = new Node(OtherPid, "Win32", DesktopHwnd);
        Assert.Equal(NativeUiOwnership.Evidence.Foreign, Classify(owner, new Node(OtherPid, parent: pane)));
        Assert.Equal(NativeUiOwnership.Evidence.Foreign, Classify(owner,
            new Node(AppPid, parent: new Node(AppPid, "Win32", OtherAppWindowHwnd, isWindow: true, parent: desktop))));
        // Missing evidence never hides contrary evidence, at the same node or higher up.
        Assert.Equal(NativeUiOwnership.Evidence.Foreign, Classify(owner, new Node(0, "Win32", ForeignHwnd, parent: pane)));
        Assert.Equal(NativeUiOwnership.Evidence.Foreign, Classify(owner, new Node(0, "Win32", parent: new Node(OtherPid))));
        // Ownership facts read before the peer vanished are kept, so a foreign process still wins.
        Assert.Equal(NativeUiOwnership.Evidence.Foreign, Classify(owner, new Node(OtherPid, parent: pane) { PartialRead = true }));
        Assert.Equal(NativeUiOwnership.Evidence.Foreign, Classify(owner, new Node(AppPid, hwnd: ForeignHwnd, parent: pane) { PartialRead = true }));
        Assert.Equal(NativeUiOwnership.Evidence.Foreign, Classify(new Node(AppPid, "Win32", OwnerHwnd), pane));
        Assert.Equal(NativeUiOwnership.Evidence.Foreign, Classify(new Node(OtherPid, "Win32", ForeignHwnd, isWindow: true), pane));
    }
    [Fact]
    public void OwnerIdentityChangingDuringTheWalk_IsForeign()
    {
        Assert.Equal(NativeUiOwnership.Evidence.Foreign, ClassifyWithOwnerChangedOnRecheck(partialRecheck: false));
        // A partial recheck still keeps the changed window handle it read before the owner vanished.
        Assert.Equal(NativeUiOwnership.Evidence.Foreign, ClassifyWithOwnerChangedOnRecheck(partialRecheck: true));
    }

    private static NativeUiOwnership.Evidence ClassifyWithOwnerChangedOnRecheck(bool partialRecheck)
    {
        var owner = Owner();
        var control = new Node(AppPid, parent: new Node(AppPid, parent: owner));
        var reads = 0;
        return NativeUiOwnership.Classify(owner, control, AppPid, node =>
        {
            if (ReferenceEquals(node, owner) && ++reads == 3)
            {
                owner.Hwnd = OtherAppWindowHwnd;
                owner.PartialRead = partialRecheck;
            }
            return node.Facts;
        }, node => node.Parent, WindowProcess);
    }
}