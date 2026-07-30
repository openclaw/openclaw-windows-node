using System;
using System.Runtime.InteropServices;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Presents the approval dialog only when there is an interactive input desktop the node owner
/// can actually see and answer on. When the workstation is locked, on the secure desktop, or the
/// process has no attended session, <c>OpenInputDesktop</c> fails and this reports "cannot
/// present" so the coordinator fails closed to the ask fallback instead of posting a dialog
/// nobody can respond to. Replaces <see cref="AlwaysCannotPresentEvaluator"/> in production.
/// </summary>
public sealed class DesktopCanPresentEvaluator : ICanPresentEvaluator
{
    private readonly Func<bool> _isDesktopInteractive;

    public DesktopCanPresentEvaluator(Func<bool>? isDesktopInteractive = null)
        => _isDesktopInteractive = isDesktopInteractive ?? IsInputDesktopInteractive;

    public bool CanPresent(string? requestSessionKey)
    {
        try
        {
            return _isDesktopInteractive();
        }
        catch
        {
            // Fail closed: without a reliable "is a user watching" signal, do not present.
            return false;
        }
    }

    private static bool IsInputDesktopInteractive()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        // OpenInputDesktop opens the desktop currently receiving user input. On the locked/
        // secure (Winlogon) desktop it fails for a normal-rights process, which is exactly when
        // there is no attended user desktop to present on.
        var desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (desktop == IntPtr.Zero)
            return false;

        try
        {
            return true;
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    private const uint DESKTOP_READOBJECTS = 0x0001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr hDesktop);
}
