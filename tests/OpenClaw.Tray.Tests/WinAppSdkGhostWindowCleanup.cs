using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using Xunit.Sdk;

[assembly: OpenClaw.Tray.Tests.WinAppSdkGhostWindowCleanupAttribute]

namespace OpenClaw.Tray.Tests;

internal sealed class WinAppSdkGhostWindowCleanupAttribute : BeforeAfterTestAttribute
{
    public override void After(MethodInfo methodUnderTest) =>
        WinAppSdkGhostWindowCleanup.CleanupBlankFrames();
}

/// <summary>
/// Cleans up Windows App SDK shell-frame ghosts created during tray validation.
/// Some WinUI/AppWindow surfaces can leave an explorer-owned, blank
/// <c>ApplicationFrameWindow</c> behind when the testhost exits. Those windows are
/// visually disruptive on a developer workstation even though all tests pass.
/// This test-only hook hides and closes blank shell frames during validation and
/// again when the test process exits.
/// </summary>
internal static class WinAppSdkGhostWindowCleanup
{
    private const uint WM_CLOSE = 0x0010;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const int SW_HIDE = 0;

    [ModuleInitializer]
    public static void Initialize()
    {
        if (!OperatingSystem.IsWindows())
            return;

        CleanupBlankFrames();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupBlankFrames();
        AssemblyLoadContext.Default.Unloading += _ => CleanupBlankFrames();
    }

    public static void CleanupBlankFrames()
    {
        if (!OperatingSystem.IsWindows())
            return;

        foreach (var hwnd in EnumerateBlankApplicationFrameWindows())
        {
            _ = ShowWindow(hwnd, SW_HIDE);
            _ = SendMessageTimeout(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out _);
        }
    }

    private static IEnumerable<IntPtr> EnumerateBlankApplicationFrameWindows()
    {
        var windows = new List<IntPtr>();
        _ = EnumWindows((hwnd, lParam) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;

            var className = new StringBuilder(256);
            _ = GetClassName(hwnd, className, className.Capacity);
            if (!string.Equals(className.ToString(), "ApplicationFrameWindow", StringComparison.Ordinal))
                return true;

            var title = new StringBuilder(512);
            _ = GetWindowText(hwnd, title, title.Capacity);
            if (!string.IsNullOrWhiteSpace(title.ToString()))
                return true;

            _ = GetWindowThreadProcessId(hwnd, out var pid);
            try
            {
                using var owner = System.Diagnostics.Process.GetProcessById((int)pid);
                if (!string.Equals(owner.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }

            if (!GetWindowRect(hwnd, out var rect))
                return true;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width > 100 && height > 100)
                windows.Add(hwnd);

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hwnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
