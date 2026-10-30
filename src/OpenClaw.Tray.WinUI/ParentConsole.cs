using System.Runtime.InteropServices;

namespace OpenClawTray;

/// <summary>
/// Attaches the WinUI process to its parent console so headless CLI modes
/// (<c>--uninstall</c>, <c>--headless-setup</c>) can write to the invoking terminal.
/// </summary>
internal static class ParentConsole
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    private const int AttachParentProcess = -1;

    internal static void Attach() => AttachConsole(AttachParentProcess);
}
