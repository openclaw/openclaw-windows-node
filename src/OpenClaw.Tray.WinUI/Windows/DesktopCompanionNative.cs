using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinUIEx.Messaging;

namespace OpenClawTray.Windows;

internal static class DesktopCompanionNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr target, IntPtr first, IntPtr second, int mode);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, in int value, int size);

    internal static PointInt32 CursorPosition()
    {
        if (!GetCursorPos(out var point))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(point.X, point.Y);
    }

    private static void SetStyle(IntPtr hwnd, int index, int value)
    {
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLong(hwnd, index, value) == 0 && Marshal.GetLastWin32Error() != 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static void ConfigureOverlay(IntPtr hwnd)
    {
        const int extendedStyle = -20;
        const int toolWindow = 0x80, appWindow = 0x40000;
        SetStyle(hwnd, extendedStyle, (GetWindowLong(hwnd, extendedStyle) | toolWindow) & ~appWindow);

        const int nonClientRenderingPolicy = 2, renderingDisabled = 1;
        const int windowCornerPreference = 33, doNotRound = 1;
        const int borderColor = 34, colorNone = unchecked((int)0xFFFFFFFE);
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(hwnd, nonClientRenderingPolicy, renderingDisabled, sizeof(int)));
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(hwnd, windowCornerPreference, doNotRound, sizeof(int)));
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(hwnd, borderColor, colorNone, sizeof(int)));
        const uint noSize = 1, noMove = 2, noZOrder = 4, noActivate = 0x10, frameChanged = 0x20;
        if (!SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, noSize | noMove | noZOrder | noActivate | frameChanged))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static WindowMessageMonitor CreateFrameMonitor(IntPtr hwnd)
    {
        var monitor = new WindowMessageMonitor(hwnd);
        monitor.WindowMessageReceived += (_, args) =>
        {
            // WinUI's presenter retains dialog-frame styles. Make the entire HWND
            // client area instead of letting DefWindowProc paint a classic border.
            const uint nonClientCalculateSize = 0x0083; // WM_NCCALCSIZE
            if (args.Message.MessageId != nonClientCalculateSize) return;
            args.Result = 0;
            args.Handled = true;
        };
        return monitor;
    }

    /// <summary>
    /// A native region excludes the unused bubble area from both paint and hit testing.
    /// XAML transparency alone would leave an invisible rectangle blocking other apps.
    /// </summary>
    internal static void SetRegion(IntPtr hwnd, int width, int height, double scale, int? bubbleTop)
    {
        var mascotSize = (int)Math.Ceiling(180 * scale);
        var region = CreateEllipticRgn(width - mascotSize, height - mascotSize, width, height);
        if (region == IntPtr.Zero)
            throw new InvalidOperationException("Could not create the desktop lobster region.");
        try
        {
            if (bubbleTop is { } top)
            {
                var bubble = CreateRectRgn(0, top, width, height - (int)(174 * scale));
                if (bubble == IntPtr.Zero)
                    throw new InvalidOperationException("Could not create the notification bubble region.");
                try
                {
                    if (CombineRgn(region, region, bubble, 2 /* RGN_OR */) == 0)
                        throw new InvalidOperationException("Could not combine the desktop lobster regions.");
                }
                finally { DeleteObject(bubble); }
            }
            if (SetWindowRgn(hwnd, region, true) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            region = IntPtr.Zero; // USER owns the region after a successful SetWindowRgn.
        }
        finally
        {
            if (region != IntPtr.Zero) DeleteObject(region);
        }
    }
}
