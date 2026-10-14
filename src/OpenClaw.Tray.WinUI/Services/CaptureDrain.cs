using System;
using System.Threading;

namespace OpenClawTray.Services;

/// <summary>
/// PROCESS-LOCAL drain accounting for outstanding outer capture workers. The external host calls
/// <see cref="WaitForIdle"/> before returning from Main so a still-draining worker cannot have its cleanup
/// (e.g. a future exact-target resume) abandoned at process exit. This is a bounded GRACE, not a guarantee: a
/// native syscall that never returns cannot be forced, and this is process-local (it does not coordinate with a
/// separate external host process).
/// </summary>
internal static class CaptureDrain
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(5);

    private static readonly object Gate = new();
    private static readonly ManualResetEventSlim Idle = new(true);
    private static int _outstanding;

    public static int Outstanding { get { lock (Gate) return _outstanding; } }

    public static void Register()
    {
        lock (Gate)
        {
            if (++_outstanding == 1) Idle.Reset();
        }
    }

    public static void Complete()
    {
        lock (Gate)
        {
            if (_outstanding > 0 && --_outstanding == 0) Idle.Set();
        }
    }

    public static bool WaitForIdle(TimeSpan grace)
    {
        try { return Idle.Wait(grace); }
        catch (ObjectDisposedException) { return true; }
    }
}