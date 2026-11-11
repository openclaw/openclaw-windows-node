// Tests the native pointer foreground gate without injecting input.
// Run with dotnet test --filter FullyQualifiedName~ForegroundActivationGate.

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    private const uint QsSendMessage = 0x0040;
    private const uint MwmoInputAvailable = 0x0004;

    [Fact]
    public void ForegroundActivationGate_AcknowledgesBeforeReadingForeground()
    {
        var target = new IntPtr(42);
        var acknowledged = false;
        ConfirmForegroundAfterRequest(target,
            () => { acknowledged = true; return true; },
            () =>
            {
                Assert.True(acknowledged);
                return target;
            });
        Assert.True(acknowledged);
    }

    [Fact]
    public void ForegroundActivationGate_RejectsAnUnresponsiveWindow()
    {
        var readForeground = false;
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConfirmForegroundAfterRequest(new IntPtr(42),
                () => false,
                () => { readForeground = true; return new IntPtr(42); }));
        Assert.Contains("did not acknowledge activation", exception.Message);
        Assert.False(readForeground);
    }

    [Fact]
    public void ForegroundActivationGate_RejectsAnotherForegroundWindow()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConfirmForegroundAfterRequest(new IntPtr(42), () => true, () => new IntPtr(43), TimeSpan.Zero));
        Assert.Contains("0x2A", exception.Message);
        Assert.Contains("0x2B", exception.Message);
        Assert.Contains("refusing pointer input", exception.Message);
    }

    [Fact]
    public async Task ForegroundActivationGate_WaitsForTheExactOwnedWindow()
    {
        using var otherWindowObserved = new ManualResetEventSlim();
        var foreground = 43;
        var target = new IntPtr(42);
        var gate = Task.Run(() => ConfirmForegroundAfterRequest(target, () => true, () =>
        {
            var observed = Volatile.Read(ref foreground);
            if (observed == 43) otherWindowObserved.Set();
            return new IntPtr(observed);
        }));
        try
        {
            Assert.True(otherWindowObserved.Wait(FixtureForegroundTimeout), "The other foreground window was not observed.");
            Assert.False(gate.IsCompleted, "The gate accepted a different foreground window.");
            Volatile.Write(ref foreground, 42);
            await gate.WaitAsync(FixtureForegroundTimeout);
        }
        finally
        {
            Volatile.Write(ref foreground, 42);
            await gate.WaitAsync(FixtureForegroundTimeout);
        }
    }

    [Fact]
    public async Task ForegroundActivationGate_WaitsForNativeQueueBeforeRejectingWrongForeground()
    {
        using var targetReady = new ManualResetEventSlim();
        using var messagePending = new ManualResetEventSlim();
        using var releaseTarget = new EventWaitHandle(false, EventResetMode.ManualReset);
        using var gateFinished = new EventWaitHandle(false, EventResetMode.ManualReset);
        var threadTimeout = FixtureForegroundTimeout + FixtureForegroundTimeout;
        var threadTimeoutMs = checked((uint)threadTimeout.TotalMilliseconds);
        var targetWindow = IntPtr.Zero;
        Exception? targetError = null;

        var targetThread = new Thread(() =>
        {
            try
            {
                targetWindow = CreateMessageOnlyWindow(0, "STATIC", "", 0, 0, 0, 0, 0,
                    new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (targetWindow == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the message-only window.");
                targetReady.Set();

                var wait = MsgWaitForMultipleObjectsEx(1, [releaseTarget.SafeWaitHandle.DangerousGetHandle()],
                    threadTimeoutMs, QsSendMessage, MwmoInputAvailable);
                if (wait == 0) return;
                if (wait != 1 || (GetQueueStatus(QsSendMessage) & (QsSendMessage << 16)) == 0)
                    throw new InvalidOperationException($"No sent message became pending (wait result {wait}).");
                messagePending.Set();

                if (WaitForSingleObject(releaseTarget.SafeWaitHandle.DangerousGetHandle(), threadTimeoutMs) != 0)
                    throw new TimeoutException("The message-only target was not released.");
                // PeekMessage dispatches sent messages even when there is no posted message to return.
                PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
                if (WaitForSingleObject(gateFinished.SafeWaitHandle.DangerousGetHandle(), threadTimeoutMs) != 0)
                    throw new TimeoutException("The foreground gate did not finish after pumping.");
            }
            catch (Exception error) { targetError = error; }
            finally
            {
                if (targetWindow != IntPtr.Zero && !DestroyMessageOnlyWindow(targetWindow) && targetError is null)
                    targetError = new Win32Exception(Marshal.GetLastWin32Error(), "Could not destroy the owned window.");
                targetReady.Set();
                messagePending.Set();
            }
        }) { IsBackground = true };

        Task<Exception?>? gate = null;
        try
        {
            targetThread.Start();
            Assert.True(targetReady.Wait(FixtureForegroundTimeout), "The owned message-only window did not start.");
            Assert.Null(targetError);
            Assert.NotEqual(IntPtr.Zero, targetWindow);
            Assert.NotEqual(targetWindow, GetForegroundWindow());

            gate = Task.Run<Exception?>(() =>
            {
                try { return Record.Exception(() => ConfirmForegroundAfterRequest(targetWindow, TimeSpan.Zero)); }
                finally { gateFinished.Set(); }
            });
            Assert.True(messagePending.Wait(FixtureForegroundTimeout), "The gate did not send a pending message.");
            Assert.Null(targetError);
            Assert.False(gate.IsCompleted, "The gate completed before the target processed its message.");
            releaseTarget.Set();

            var error = Assert.IsType<InvalidOperationException>(await gate.WaitAsync(threadTimeout));
            Assert.Contains("Expected owned window", error.Message);
            Assert.Contains("refusing pointer input", error.Message);
        }
        finally
        {
            releaseTarget.Set();
            if (targetThread.IsAlive)
                Assert.True(targetThread.Join(threadTimeout), "The owned message-only window thread did not stop.");
            if (gate is not null)
                await gate.WaitAsync(threadTimeout);
        }
        Assert.Null(targetError);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateMessageOnlyWindow(uint extendedStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance,
        IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    private static extern bool DestroyMessageOnlyWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[] handles, uint timeout,
        uint wakeMask, uint flags);
    [DllImport("user32.dll")]
    private static extern uint GetQueueStatus(uint flags);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint minimum,
        uint maximum, uint remove);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
}
