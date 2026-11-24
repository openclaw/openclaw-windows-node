using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace OpenClaw.Tray.UITests;

public sealed class NativeThreadStackProbeTests
{
    private static readonly TimeSpan CaptureBudget = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_TEST_STACK_PROBE_BUDGET_SECONDS"), out var seconds) && seconds > 0 ? seconds : 30);

    private const long NoGcBudgetBytes = 64L * 1024 * 1024;

    // Module file name plus offset per frame, or the fixed JIT placeholder: no paths, values or text.
    private static readonly Regex ContentFreeStack = new(
        @"^[A-Za-z0-9._-]+\+0x[0-9A-F]+( <- ([A-Za-z0-9._-]+\+0x[0-9A-F]+|<jit-or-unmapped>))*$", RegexOptions.CultureInvariant);

    [Fact]
    public void Capture_NamesNativeFramesOfABlockedThreadAndLeavesItRunning()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Assert.Equal("unavailable: x64 only", NativeThreadStackProbe.Capture(NativeThreadStackProbe.GetCurrentThreadId()));
            return;
        }

        using var target = new ParkedThread();
        var stack = CaptureWhenParked(target.NativeId);

        Assert.Matches(ContentFreeStack, stack);
        Assert.Contains("coreclr.dll+", stack, StringComparison.OrdinalIgnoreCase);
        target.Release();
        Assert.True(target.Join(CaptureBudget), "The captured thread must keep running after the snapshot.");
    }

    [Fact]
    public void Capture_CompletesWhileTheTargetHoldsTheLoaderLock()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        using var target = new ParkedThread();
        // Take the lock right after Start, before the worker's warm-up can finish: run alone in a
        // fresh testhost, this covers a watched thread that stalls under the lock at once.
        NativeThreadStackProbe.Start();
        // The loader lock is process-wide: no GC may run while it is held, because a GC that needs
        // a new thread would wait for the lock forever.
        Assert.True(GC.TryStartNoGCRegion(NoGcBudgetBytes), "Could not reserve a no-GC region for the loader-lock hold.");
        var stack = string.Empty;
        bool unlocked;
        try
        {
            try
            {
                target.TakeLoaderLockAndPark();
                stack = CaptureWhenParked(target.NativeId, assertParked: false);
            }
            finally
            {
                // Confirm the unlock before leaving the no-GC region.
                target.Release();
                unlocked = target.Join(CaptureBudget);
            }
        }
        finally
        {
            if (System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion)
                GC.EndNoGCRegion();
        }

        Assert.True(unlocked, "The target must release the loader lock and exit.");
        Assert.True(target.LoaderLockHeld, "The target must have held the loader lock during the capture.");
        Assert.StartsWith("ntdll.dll+", stack, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(ContentFreeStack, stack);
    }

    private static string CaptureWhenParked(uint nativeId, bool assertParked = true)
    {
        NativeThreadStackProbe.Start();
        // Bounded wait for the thread to park inside the kernel wait, observed through the probe itself.
        var deadline = DateTime.UtcNow + CaptureBudget;
        string stack;
        do stack = NativeThreadStackProbe.Capture(nativeId);
        while (!stack.StartsWith("ntdll.dll+", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow < deadline);
        if (assertParked)
            Assert.StartsWith("ntdll.dll+", stack, StringComparison.OrdinalIgnoreCase);
        return stack;
    }

    /// <summary>A thread parked in a kernel wait, optionally re-parked while holding the loader lock.</summary>
    private sealed class ParkedThread : IDisposable
    {
        private readonly ManualResetEvent _lockAndPark = new(false);
        private readonly ManualResetEvent _parkedUnderLock = new(false);
        private readonly ManualResetEvent _release = new(false);
        private readonly Thread _thread;
        private uint _nativeId;
        private volatile bool _loaderLockHeld;

        public ParkedThread()
        {
            // Bind the native calls now: binding under the loader lock could need it.
            NativeThreadStackProbe.PrelinkDeclaredImports(typeof(ParkedThread));
            _thread = new Thread(Run) { IsBackground = true };
            _thread.Start();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref _nativeId) != 0, CaptureBudget));
        }

        public uint NativeId => Volatile.Read(ref _nativeId);

        public bool LoaderLockHeld => _loaderLockHeld;

        public void TakeLoaderLockAndPark()
        {
            _lockAndPark.Set();
            Assert.True(_parkedUnderLock.WaitOne(CaptureBudget), "The target did not take the loader lock.");
        }

        public void Release()
        {
            _release.Set();
            _lockAndPark.Set();
        }

        public bool Join(TimeSpan timeout) => _thread.Join(timeout);

        public void Dispose()
        {
            Release();
            if (_thread.Join(CaptureBudget))
            {
                _lockAndPark.Dispose();
                _parkedUnderLock.Dispose();
                _release.Dispose();
            }
        }

        private void Run()
        {
            Volatile.Write(ref _nativeId, NativeThreadStackProbe.GetCurrentThreadId());
            _lockAndPark.WaitOne();
            if (_release.WaitOne(0))
                return;
            // Only already-bound native calls run under the lock: no JIT, stub generation or managed wait.
            var parked = _parkedUnderLock.SafeWaitHandle.DangerousGetHandle();
            var release = _release.SafeWaitHandle.DangerousGetHandle();
            if (LdrLockLoaderLock(0, IntPtr.Zero, out var cookie) != 0)
                return;
            _loaderLockHeld = true;
            SetEvent(parked);
            // Bounded, so an unforeseen loader-lock deadlock fails the test instead of hanging the shard.
            WaitForSingleObject(release, (uint)(CaptureBudget.TotalMilliseconds * 2));
            LdrUnlockLoaderLock(0, cookie);
        }

        [DllImport("ntdll.dll")]
        private static extern int LdrLockLoaderLock(uint flags, IntPtr disposition, out IntPtr cookie);

        [DllImport("ntdll.dll")]
        private static extern int LdrUnlockLoaderLock(uint flags, IntPtr cookie);

        [DllImport("kernel32.dll")]
        private static extern bool SetEvent(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    }
}
