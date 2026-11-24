using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace OpenClaw.Tray.UITests;

public sealed class NativeThreadStackProbeTests
{
    private static readonly TimeSpan CaptureBudget = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_TEST_STACK_PROBE_BUDGET_SECONDS"), out var seconds) && seconds > 0 ? seconds : 30);

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
        // A first capture proves the worker is warm before the loader lock is taken.
        CaptureWhenParked(target.NativeId);

        string stack;
        var elapsed = new System.Diagnostics.Stopwatch();
        target.TakeLoaderLockAndPark();
        elapsed.Start();
        try
        {
            stack = NativeThreadStackProbe.Capture(target.NativeId);
        }
        finally
        {
            target.Release();
        }

        Assert.True(target.LoaderLockHeld, "The target must have held the loader lock during the capture.");
        Assert.Matches(ContentFreeStack, stack);
        Assert.True(elapsed.Elapsed < CaptureBudget, $"Capture took {elapsed.Elapsed} under the loader lock.");
        Assert.True(target.Join(CaptureBudget), "The target must release the loader lock and exit.");
    }

    private static string CaptureWhenParked(uint nativeId)
    {
        NativeThreadStackProbe.Start();
        // Bounded wait for the thread to park inside the kernel wait, observed through the probe itself.
        var deadline = DateTime.UtcNow + CaptureBudget;
        string stack;
        do stack = NativeThreadStackProbe.Capture(nativeId);
        while (!stack.StartsWith("ntdll.dll+", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow < deadline);
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

        public ParkedThread()
        {
            _thread = new Thread(Run) { IsBackground = true };
            _thread.Start();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref _nativeId) != 0, CaptureBudget));
        }

        public uint NativeId => Volatile.Read(ref _nativeId);

        public bool LoaderLockHeld { get; private set; }

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
            if (LdrLockLoaderLock(0, IntPtr.Zero, out var cookie) != 0)
                return;
            try
            {
                LoaderLockHeld = true;
                _parkedUnderLock.Set();
                _release.WaitOne();
            }
            finally
            {
                LdrUnlockLoaderLock(0, cookie);
            }
        }

        [DllImport("ntdll.dll")]
        private static extern int LdrLockLoaderLock(uint flags, IntPtr disposition, out IntPtr cookie);

        [DllImport("ntdll.dll")]
        private static extern int LdrUnlockLoaderLock(uint flags, IntPtr cookie);
    }
}
