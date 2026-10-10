using System;
using System.Threading;

namespace OpenClawTray.Services;

/// <summary>
/// Process-lifetime gate for the external host. CLR background threads are abandoned when Main returns, so a
/// FOREGROUND drain thread holds the process open until BOTH the capture workers drain AND every resume obligation
/// clears - or the bounded grace expires. This makes the exact-resume obligation visible at the external Main
/// boundary; it is a bounded, process-local guarantee, not a hard cancellation of a hung native syscall.
/// </summary>
internal static class CaptureHostLifetime
{
    /// <summary>
    /// Wait (on a foreground thread) for workers + resume obligations to clear. Returns true only when BOTH are
    /// confirmed clear within the grace; false means the process is about to exit with work still outstanding.
    /// </summary>
    public static bool DrainBeforeExit(TimeSpan grace)
    {
        var workersClear = false;
        var resumeClear = false;
        var thread = new Thread(() =>
        {
            workersClear = CaptureDrain.WaitForIdle(grace);
            resumeClear = CaptureResumeObligations.WaitForClear(grace);
        })
        {
            IsBackground = false,   // foreground: the CLR keeps the process alive until this thread finishes
            Name = "hang-capture-drain",
        };
        thread.Start();
        thread.Join(grace + TimeSpan.FromSeconds(1));
        return workersClear && resumeClear;
    }
}