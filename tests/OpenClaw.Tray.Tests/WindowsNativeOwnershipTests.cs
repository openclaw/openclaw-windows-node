using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenClawTray.Services;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// NATIVE PHYSICAL OWNERSHIP controls. These drive the ACTUAL WindowsUiThreadStackSource through the injected
/// native-operation seam, so its real admission / timeout / release / cross-instance ownership logic runs on
/// Linux without calling Windows or any private app. No fabricated native frames or acceptance.
/// </summary>
[Collection("native-capture")]
public class WindowsNativeOwnershipTests
{
    // ---- Adopted verbatim from Mini-Actual-U4-Host-bf9-Counterexample. ----
    [Fact]
    public async Task MiniReview_HostMustWaitForNestedPhysicalDrainBeforeMainCanExit()
    {
        using var release = new ManualResetEventSlim(false);
        var ops = new GatedOperations(release);
        var source = new WindowsUiThreadStackSource(ops);
        var root = Directory.CreateTempSubdirectory("mini-u4-host-drain-").FullName;
        using var output = new StringWriter();
        var host = Task.Run(() => HangCaptureHost.Run(new[] {"--pid","4321","--birth","638000000000000000","--thread","77","--dir",root,"--budget-ms","50"},source,output));
        bool returnedWhileNativeHeld;
        try
        {
            var complete = await Task.WhenAny(host,Task.Delay(500));
            returnedWhileNativeHeld = ReferenceEquals(complete,host) && WindowsUiThreadStackSource.PhysicalWorkers > 0;
        }
        finally { release.Set(); }
        _ = await host;
        Assert.True(WindowsUiThreadStackSource.WaitForPhysicalIdle(TimeSpan.FromSeconds(10)));
        Assert.False(returnedWhileNativeHeld,"Host returned to Main while an actual source nested physical worker was still held");
    }

    private static UiThreadTarget Target(int pid = 4321, int tid = 77)
        => new(pid, 638_000_000_000_000_000L, tid);

    // Adapted from Mini-Actual-U4-Nested-cb76-Counterexample to exercise the ACTUAL native class: two source
    // INSTANCES against the same exact target, each outer run timing out while its native worker drains.
    [Fact]
    public async Task MiniReview_NestedPhysicalWorkersMustRemainExclusiveAcrossInstances()
    {
        using var release = new ManualResetEventSlim(false);
        var ops = new GatedOperations(release);
        var instances = Enumerable.Range(0, 2).Select(_ => new WindowsUiThreadStackSource(ops)).ToArray();
        var root = Directory.CreateTempSubdirectory("mini-u4-native-nested-").FullName;
        int started;
        try
        {
            foreach (var source in instances)
                CompanionHangCapture.Run(source, Target(), root, budget: TimeSpan.FromMilliseconds(150));
            started = ops.Started;
        }
        finally
        {
            release.Set();
            await ops.FirstDrained.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(WindowsUiThreadStackSource.WaitForPhysicalIdle(TimeSpan.FromSeconds(10)));
        }
        Assert.True(started <= 1, $"Outer collectors returned but {started} nested native workers remained held across source instances");
    }

    // A second instance is REFUSED while the first native worker physically drains, then admitted after drain.
    [Fact]
    public async Task NativeOwnership_SecondInstanceRefusedWhilePhysicalWorkerDrains()
    {
        using var release = new ManualResetEventSlim(false);
        var ops = new GatedOperations(release);
        var first = new WindowsUiThreadStackSource(ops);
        var second = new WindowsUiThreadStackSource(ops);

        Assert.False(first.TryCaptureWaitChain(Target(), 4, TimeSpan.FromMilliseconds(100), out _, out _, out var firstFail));
        // The timed-out call must NOT have disposed the handle its native worker still uses.
        Assert.Equal(0, ops.HandlesDisposed);
        // While the first native worker drains, a fresh instance must NOT start another native worker.
        Assert.False(second.TryCaptureWaitChain(Target(), 4, TimeSpan.FromMilliseconds(100), out _, out _, out var busy));
        Assert.Contains("busy", busy ?? string.Empty);
        Assert.True(ops.Started == 1, $"started={ops.Started} opened={ops.HandlesOpened} first={firstFail} second={busy}");

        release.Set();
        await ops.FirstDrained.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(WindowsUiThreadStackSource.WaitForPhysicalIdle(TimeSpan.FromSeconds(10)));
        // Now drained: the held handle was disposed WITH the ownership release (plus the refused open above).
        Assert.Equal(2, ops.HandlesDisposed);
    }

    // Different exact targets do not serialize each other.
    [Fact]
    public async Task NativeOwnership_DifferentTargetsAreIndependent()
    {
        using var releaseA = new ManualResetEventSlim(false);
        using var releaseB = new ManualResetEventSlim(false);
        var opsA = new GatedOperations(releaseA);
        var opsB = new GatedOperations(releaseB);
        var sourceA = new WindowsUiThreadStackSource(opsA);
        var sourceB = new WindowsUiThreadStackSource(opsB);

        try
        {
            Assert.False(sourceA.TryCaptureWaitChain(Target(pid: 5001, tid: 61), 4, TimeSpan.FromMilliseconds(100), out _, out _, out var failA));
            Assert.False(sourceB.TryCaptureWaitChain(Target(pid: 5002, tid: 62), 4, TimeSpan.FromMilliseconds(100), out _, out _, out var failB));
            Assert.DoesNotContain("busy", failA ?? string.Empty);
            Assert.DoesNotContain("busy", failB ?? string.Empty);   // a different target is not blocked
        }
        finally
        {
            releaseA.Set(); releaseB.Set();
            await Task.WhenAll(opsA.FirstDrained.Task, opsB.FirstDrained.Task).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(WindowsUiThreadStackSource.WaitForPhysicalIdle(TimeSpan.FromSeconds(10)));
        }
        Assert.Equal(1, opsA.Started);
        Assert.Equal(1, opsB.Started);
    }

    // The host drain seam: Main waits for outstanding outer workers within a bounded grace.
    [Fact]
    public void HostDrainWaitsForOutstandingWorkers()
    {
        CaptureDrain.Register();
        try
        {
            Assert.False(CaptureDrain.WaitForIdle(TimeSpan.FromMilliseconds(50)));   // still outstanding
        }
        finally
        {
            CaptureDrain.Complete();
        }
        Assert.True(CaptureDrain.WaitForIdle(TimeSpan.FromSeconds(2)));
    }

    // The REAL native ops never fabricate frames: without the real exact-target handle (or on an unsupported
    // architecture) they return an explicit failure receipt instead of inventing a stack.
    [Fact]
    public void RealNativeStack_ReportsFailureReceiptWithoutRealHandle()
    {
        var ops = WindowsThreadStackNativeOperations.Instance;
        Assert.False(ops.TryCaptureStack(new StubHandle(), 8, out var frames, out var truncated, out var failure));
        Assert.Empty(frames);
        Assert.False(truncated);
        Assert.False(string.IsNullOrWhiteSpace(failure));   // an explicit receipt, not a fabricated stack
    }

    // The exact-target RESUME obligation stays visible until confirmed, and the host drain cannot confirm while it
    // is outstanding - so the process cannot exit silently with a target possibly still suspended.
    [Fact]
    public void ResumeObligation_MustBeClearedBeforeHostDrainConfirms()
    {
        var reservation = CaptureResumeObligations.TryReserve();
        Assert.NotNull(reservation);
        CaptureResumeObligations.PromoteToOwned(reservation!);   // a real suspension now exists
        try
        {
            Assert.Equal(1, CaptureResumeObligations.Outstanding);
            Assert.False(CaptureHostLifetime.DrainBeforeExit(TimeSpan.FromMilliseconds(50)));
        }
        finally
        {
            CaptureResumeObligations.ReleaseOwnedAfterConfirmedResume(reservation!);   // only after a real resume
        }
        Assert.Equal(0, CaptureResumeObligations.Outstanding);
        Assert.True(CaptureHostLifetime.DrainBeforeExit(TimeSpan.FromSeconds(2)));
    }

    // A gated stack capture that leaves the resume obligation outstanding is observable and keeps the drain
    // unconfirmed (no late publication of success).
    [Fact]
    public async Task GatedStack_LeavesResumeObligationVisibleUntilConfirmed()
    {
        var ops = new UnconfirmedResumeOperations();
        var reservation = CaptureResumeObligations.TryReserve();
        Assert.NotNull(reservation);
        CaptureResumeObligations.PromoteToOwned(reservation!);   // the gated stack models a target left suspended
        var source = new WindowsUiThreadStackSource(ops);
        try
        {
            Assert.False(source.TryCaptureStack(Target(), 4, TimeSpan.FromMilliseconds(100), out var frames, out _, out var failure));
            Assert.Empty(frames);
            Assert.False(string.IsNullOrWhiteSpace(failure));
            Assert.Equal(1, CaptureResumeObligations.Outstanding);          // visible, not hidden
            Assert.False(CaptureHostLifetime.DrainBeforeExit(TimeSpan.FromMilliseconds(50)));
        }
        finally
        {
            CaptureResumeObligations.ReleaseOwnedAfterConfirmedResume(reservation!);
        }
        Assert.True(await Task.Run(() => CaptureHostLifetime.DrainBeforeExit(TimeSpan.FromSeconds(2))));
    }

    // DWORD_MAX suspend failure: NO owned suspension exists, so the reservation is disposed with no leak.
    [Fact]
    public void SuspendFailure_DisposesReservationWithoutLeak()
    {
        var reservation = CaptureResumeObligations.TryReserve();
        Assert.NotNull(reservation);
        CaptureResumeObligations.ReleaseReservationOnly(reservation!);   // SuspendThread returned DWORD_MAX
        Assert.Equal(0, CaptureResumeObligations.Outstanding);
        Assert.Equal(0, CaptureResumeObligations.Reservations);
        Assert.True(CaptureHostLifetime.DrainBeforeExit(TimeSpan.FromSeconds(2)));
    }

    // Resume failure: the owned suspension is RETAINED (never blindly decremented) and stays exclusive.
    [Fact]
    public void ResumeFailure_RetainsOwnedSuspension()
    {
        var reservation = CaptureResumeObligations.TryReserve();
        Assert.NotNull(reservation);
        CaptureResumeObligations.PromoteToOwned(reservation!);
        // ResumeThread failed (DWORD_MAX): release is NOT called here.
        Assert.Equal(1, CaptureResumeObligations.Outstanding);
        Assert.Null(CaptureResumeObligations.TryReserve());            // exclusive while unresolved
        Assert.False(CaptureHostLifetime.DrainBeforeExit(TimeSpan.FromMilliseconds(50)));
        CaptureResumeObligations.ReleaseOwnedAfterConfirmedResume(reservation!);
        Assert.True(CaptureHostLifetime.DrainBeforeExit(TimeSpan.FromSeconds(2)));
    }

    // Mini-c825 counterexample 1: exclusivity must include RESERVATIONS, not only owned slots.
    [Fact]
    public void Reservation_IsExclusiveIncludingReservations()
    {
        var first = CaptureResumeObligations.TryReserve();
        Assert.NotNull(first);
        try
        {
            Assert.Null(CaptureResumeObligations.TryReserve());   // a SECOND reservation must be refused
        }
        finally
        {
            CaptureResumeObligations.ReleaseReservationOnly(first!);
        }
        Assert.Equal(0, CaptureResumeObligations.Reservations);
    }

    // Mini-c825 counterexample 2: a RELEASED token can never be promoted into a leaked owned obligation.
    [Fact]
    public void ReleasedToken_CannotBePromotedIntoLeakedObligation()
    {
        var token = CaptureResumeObligations.TryReserve();
        Assert.NotNull(token);
        CaptureResumeObligations.ReleaseReservationOnly(token!);   // DWORD_MAX path: terminal release
        CaptureResumeObligations.PromoteToOwned(token!);           // invalid promotion must NOT mutate counters
        Assert.Equal(0, CaptureResumeObligations.Outstanding);
        Assert.Equal(0, CaptureResumeObligations.Reservations);
        CaptureResumeObligations.ReleaseOwnedAfterConfirmedResume(token!);   // no-op on the terminal token
        Assert.Equal(0, CaptureResumeObligations.Outstanding);
        Assert.True(CaptureHostLifetime.DrainBeforeExit(TimeSpan.FromSeconds(2)));   // no leaked obligation
    }

    // RUNTIME type-load + Marshal.SizeOf/OffsetOf controls on the ACTUAL private structs (Mini-834a control was a
    // TypeLoadException: an explicit-layout struct with char[] fields has managed references, not inline WCHARs).
    [Fact]
    public void NativeStructs_LoadAndMarshalWithExactSdkLayout()
    {
        var module = typeof(WindowsThreadStackNativeOperations)
            .GetNestedType("ImagehlpModuleW64", System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(3264, Marshal.SizeOf(module));   // SDK sizeof(IMAGEHLP_MODULEW64); would throw on a bad layout
        Assert.Equal(8, Marshal.OffsetOf(module, "BaseOfImage").ToInt32());
        Assert.Equal(16, Marshal.OffsetOf(module, "ImageSize").ToInt32());
        Assert.Equal(20, Marshal.OffsetOf(module, "TimeDateStamp").ToInt32());
        Assert.Equal(24, Marshal.OffsetOf(module, "CheckSum").ToInt32());
        Assert.Equal(28, Marshal.OffsetOf(module, "NumSyms").ToInt32());
        Assert.Equal(32, Marshal.OffsetOf(module, "SymType").ToInt32());
        Assert.Equal(36, Marshal.OffsetOf(module, "ModuleName").ToInt32());
        Assert.Equal(100, Marshal.OffsetOf(module, "ImageName").ToInt32());
        Assert.Equal(612, Marshal.OffsetOf(module, "LoadedImageName").ToInt32());

        var symbol = typeof(WindowsThreadStackNativeOperations)
            .GetNestedType("SymbolInfo", System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(84, Marshal.OffsetOf(symbol, "Name").ToInt32());   // SYMBOL_INFOW header Name@84 (sizeof 88)
    }

    private sealed class StubHandle : IExactTargetHandle
    {
        public bool IsStillExact() => true;
        public void Dispose() { }
    }

    private sealed class UnconfirmedResumeOperations : IWindowsThreadStackNativeOperations
    {
        public bool TryOpenVerified(UiThreadTarget target, out IExactTargetHandle? handle, out string? failure)
        {
            handle = new StubHandle();
            failure = null;
            return true;
        }

        public bool TryCaptureWaitChain(
            IExactTargetHandle handle, int maxNodes,
            out IReadOnlyList<WaitChainNodeView> nodes, out bool truncated, out string? failure)
        {
            nodes = Array.Empty<WaitChainNodeView>(); truncated = false; failure = "synthetic unavailable"; return false;
        }

        public bool TryCaptureStack(
            IExactTargetHandle handle, int maxFrames,
            out IReadOnlyList<StackFrameView> frames, out bool truncated, out string? failure)
        {
            // Models an owned suspension whose resume was NOT confirmed: the obligation stays owned.
            frames = Array.Empty<StackFrameView>();
            truncated = false;
            failure = "synthetic suspended target; resume not confirmed";
            return false;
        }
    }

    private sealed class GatedOperations : IWindowsThreadStackNativeOperations
    {
        private readonly ManualResetEventSlim _release;
        public int Started;
        public int HandlesOpened;
        public int HandlesDisposed;
        public readonly TaskCompletionSource<bool> FirstDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GatedOperations(ManualResetEventSlim release) { _release = release; }

        public bool TryOpenVerified(UiThreadTarget target, out IExactTargetHandle? handle, out string? failure)
        {
            Interlocked.Increment(ref HandlesOpened);
            handle = new FakeHandle(this);
            failure = null;
            return true;
        }

        public bool TryCaptureWaitChain(
            IExactTargetHandle handle, int maxNodes,
            out IReadOnlyList<WaitChainNodeView> nodes, out bool truncated, out string? failure)
        {
            // Model the REAL synchronous native call: the physical worker entry blocks until drained.
            Interlocked.Increment(ref Started);
            try { _release.Wait(); }
            finally { FirstDrained.TrySetResult(true); }
            nodes = Array.Empty<WaitChainNodeView>();
            truncated = false;
            failure = "synthetic native capture timed out and still draining";
            return false;
        }

        public bool TryCaptureStack(
            IExactTargetHandle handle, int maxFrames,
            out IReadOnlyList<StackFrameView> frames, out bool truncated, out string? failure)
        {
            frames = Array.Empty<StackFrameView>();
            truncated = false;
            failure = "synthetic unavailable";
            return false;
        }

        private sealed class FakeHandle : IExactTargetHandle
        {
            private readonly GatedOperations _owner;
            public FakeHandle(GatedOperations owner) { _owner = owner; }
            public bool IsStillExact() => true;
            public void Dispose() => Interlocked.Increment(ref _owner.HandlesDisposed);
        }
    }
}