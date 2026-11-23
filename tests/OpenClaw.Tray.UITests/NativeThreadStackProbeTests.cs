namespace OpenClaw.Tray.UITests;

public sealed class NativeThreadStackProbeTests
{
    private static readonly TimeSpan CaptureBudget = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_TEST_STACK_PROBE_BUDGET_SECONDS"), out var seconds) && seconds > 0 ? seconds : 30);

    [Fact]
    public void Capture_NamesNativeFramesOfABlockedThreadAndLeavesItRunning()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
        {
            Assert.Equal("unavailable: x64 only", NativeThreadStackProbe.Capture(NativeThreadStackProbe.GetCurrentThreadId()));
            return;
        }

        NativeThreadStackProbe.Start();
        var release = new ManualResetEvent(false);
        uint nativeId = 0;
        var blocked = new Thread(() =>
        {
            Volatile.Write(ref nativeId, NativeThreadStackProbe.GetCurrentThreadId());
            release.WaitOne();
        }) { IsBackground = true };
        blocked.Start();
        try
        {
            // Bounded wait for the thread to park inside the kernel wait, observed through the probe itself.
            var deadline = DateTime.UtcNow + CaptureBudget;
            string stack;
            do stack = NativeThreadStackProbe.Capture(Volatile.Read(ref nativeId));
            while (!stack.StartsWith("ntdll.dll+", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow < deadline);

            Assert.StartsWith("ntdll.dll+", stack, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("coreclr.dll+", stack, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), stack);
        }
        finally
        {
            release.Set();
        }
        Assert.True(blocked.Join(CaptureBudget), "The captured thread must keep running after the snapshot.");
        release.Dispose();
    }
}
