using System.Runtime.InteropServices;

namespace OpenClawTray.Services;

internal enum NativeRestartWaitFailure { PreviousInstance, RecoveryStorage }

/// <summary>Runs synchronously so acquiring a .NET mutex never changes threads across an await.</summary>
internal static class NativeRestartAdmission
{
    internal static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(60);

    public static bool Acquire(string handle, Action<string> preserve, Func<TimeSpan, bool> wait,
        Func<NativeRestartWaitFailure, bool> retry)
    {
        while (true)
        {
            try { preserve(handle); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                if (!retry(NativeRestartWaitFailure.RecoveryStorage)) return false;
                continue;
            }
            try { if (wait(WaitBudget)) return true; }
            catch (AbandonedMutexException) { return true; }
            if (!retry(NativeRestartWaitFailure.PreviousInstance)) return false;
        }
    }

    public static bool PromptRetry(string text, string title)
    {
        var result = MessageBoxW(IntPtr.Zero, text, title, 0x00000005 | 0x00000030 | 0x00010000);
        if (result == 0) throw new InvalidOperationException("The setup restart recovery prompt could not be shown.");
        return result == 4;
    }

    public static void Notify(string text, string title) =>
        MessageBoxW(IntPtr.Zero, text, title, 0x00000030 | 0x00010000);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
