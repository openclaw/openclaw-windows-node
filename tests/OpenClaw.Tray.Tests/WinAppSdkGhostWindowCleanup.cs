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
    public override void Before(MethodInfo methodUnderTest) =>
        WinAppSdkGhostWindowCleanup.CleanupBlankFrames();

    public override void After(MethodInfo methodUnderTest) =>
        WinAppSdkGhostWindowCleanup.CleanupBlankFrames();
}

/// <summary>
/// Cleans up Windows App SDK shell-frame ghosts created during tray validation.
/// Some WinUI/AppWindow surfaces can leave an explorer-owned, blank
/// <c>ApplicationFrameWindow</c> behind when the testhost exits. Those windows
/// are visually disruptive on a developer workstation even though all tests pass.
/// This test-only hook hides and closes newly-created blank shell frames during
/// validation and again when the test process exits.
/// </summary>
internal static class WinAppSdkGhostWindowCleanup
{
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint SC_CLOSE = 0xF060;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const int SW_HIDE = 0;
    private static readonly object s_baselineLock = new();
    private static HashSet<IntPtr> s_baselineBlankFrames = [];
    private static int s_cleanupInProgress;
    private static System.Threading.Timer? s_cleanupTimer;

    [ModuleInitializer]
    public static void Initialize()
    {
        if (!OperatingSystem.IsWindows())
            return;

        RecordBaselineBlankFrames();
        CleanupBlankFrames();
        s_cleanupTimer = new System.Threading.Timer(
            _ => CleanupBlankFrames(),
            state: null,
            dueTime: System.TimeSpan.FromSeconds(1),
            period: System.TimeSpan.FromSeconds(1));

        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupBlankFramesRepeatedly();
        AssemblyLoadContext.Default.Unloading += _ => CleanupBlankFramesRepeatedly();
    }

    public static void CleanupBlankFrames()
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (System.Threading.Interlocked.Exchange(ref s_cleanupInProgress, 1) == 1)
            return;

        try
        {
            foreach (var hwnd in EnumerateBlankApplicationFrameWindows())
            {
                if (!IsBaselineBlankFrame(hwnd))
                    HideAndClose(hwnd);
            }
        }
        finally
        {
            System.Threading.Volatile.Write(ref s_cleanupInProgress, 0);
        }
    }

    private static void RecordBaselineBlankFrames()
    {
        lock (s_baselineLock)
        {
            s_baselineBlankFrames = EnumerateBlankApplicationFrameWindows().ToHashSet();
        }
    }

    private static bool IsBaselineBlankFrame(IntPtr hwnd)
    {
        lock (s_baselineLock)
        {
            return s_baselineBlankFrames.Contains(hwnd);
        }
    }

    private static void CleanupBlankFramesRepeatedly()
    {
        var deadline = System.DateTime.UtcNow.AddSeconds(5);
        while (System.DateTime.UtcNow < deadline)
        {
            CleanupBlankFrames();
            System.Threading.Thread.Sleep(250);
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
                if (!string.Equals(owner.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(owner.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
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

    private static void HideAndClose(IntPtr hwnd)
    {
        _ = ShowWindow(hwnd, SW_HIDE);
        _ = PostMessage(hwnd, WM_SYSCOMMAND, new IntPtr(SC_CLOSE), IntPtr.Zero);
        _ = SendMessageTimeout(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out _);
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
