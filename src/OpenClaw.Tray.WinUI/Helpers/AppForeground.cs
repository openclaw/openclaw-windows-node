// Reports whether one of OpenClaw's own windows is the active foreground window, so
// background-only alerts (actionable approval toasts) stay quiet while the user is in the app.
//
// Usage:
//   if (!AppForeground.IsActive()) ShowToast();

using System.Runtime.InteropServices;

namespace OpenClawTray.Helpers;

internal static class AppForeground
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static bool IsActive()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(window, out var processId);
        return processId == (uint)Environment.ProcessId;
    }
}
