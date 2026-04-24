using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Infrastructure.Hosting.Devtools;

/// <summary>
/// PNG capture of a WinUI window (or a selector-scoped region of one) via Win32
/// <c>PrintWindow</c>. Same capture backend as <see cref="PreviewCaptureServer"/>;
/// kept here as a separate helper so the MCP <c>screenshot</c> tool doesn't take
/// a hard dependency on the VS Code capture server.
/// </summary>
internal static class ScreenshotCapture
{
    public readonly record struct Capture(byte[] Png, int X, int Y, int Width, int Height);

    public static Capture CaptureWindow(Window window, bool includeChrome, (double x, double y, double w, double h)? crop = null)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

        if (!GetClientRect(hwnd, out var clientRect))
            throw new InvalidOperationException("GetClientRect failed on window.");

        var clientOrigin = new POINT { X = 0, Y = 0 };
        ClientToScreen(hwnd, ref clientOrigin);
        GetWindowRect(hwnd, out var windowRect);

        int offsetX = clientOrigin.X - windowRect.Left;
        int offsetY = clientOrigin.Y - windowRect.Top;
        int windowWidth = windowRect.Right - windowRect.Left;
        int windowHeight = windowRect.Bottom - windowRect.Top;
        int clientWidth = clientRect.Right - clientRect.Left;
        int clientHeight = clientRect.Bottom - clientRect.Top;

        if (windowWidth <= 0 || windowHeight <= 0 || clientWidth <= 0 || clientHeight <= 0)
            throw new InvalidOperationException("Window has zero-size bounds.");

        using var windowBmp = new Bitmap(windowWidth, windowHeight, PixelFormat.Format32bppArgb);
        using (var g = global::System.Drawing.Graphics.FromImage(windowBmp))
        {
            IntPtr hdc = g.GetHdc();
            PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
            g.ReleaseHdc(hdc);
        }

        // Source rect inside the window bitmap.
        int srcX = includeChrome ? 0 : offsetX;
        int srcY = includeChrome ? 0 : offsetY;
        int srcW = includeChrome ? windowWidth : clientWidth;
        int srcH = includeChrome ? windowHeight : clientHeight;

        if (crop is var (cx, cy, cw, ch) && cw > 0 && ch > 0)
        {
            // The crop rect comes in from TransformToVisual in device-independent
            // pixels (DIPs). PrintWindow hands us a bitmap in *physical* pixels.
            // Without scaling, the crop is correct only at 100% DPI; at 125% a
            // rect that should cover a button up at y=240 lands at y=177 (the
            // "Cour" of "Current count" above it). Scale DIPs to physical px
            // using the window's effective DPI before stacking the client offset.
            double dpi = GetDpiForWindow(hwnd);
            if (dpi <= 0) dpi = 96;
            double scale = dpi / 96.0;

            int cropX = (int)Math.Round(cx * scale) + srcX;
            int cropY = (int)Math.Round(cy * scale) + srcY;
            int cropW = (int)Math.Round(cw * scale);
            int cropH = (int)Math.Round(ch * scale);
            cropX = Math.Max(0, Math.Min(cropX, windowWidth - 1));
            cropY = Math.Max(0, Math.Min(cropY, windowHeight - 1));
            cropW = Math.Max(1, Math.Min(cropW, windowWidth - cropX));
            cropH = Math.Max(1, Math.Min(cropH, windowHeight - cropY));

            srcX = cropX; srcY = cropY; srcW = cropW; srcH = cropH;
        }

        using var outBmp = new Bitmap(srcW, srcH, PixelFormat.Format32bppArgb);
        using (var g = global::System.Drawing.Graphics.FromImage(outBmp))
        {
            g.DrawImage(windowBmp,
                new Rectangle(0, 0, srcW, srcH),
                new Rectangle(srcX, srcY, srcW, srcH),
                GraphicsUnit.Pixel);
        }

        using var ms = new MemoryStream();
        outBmp.Save(ms, ImageFormat.Png);
        return new Capture(ms.ToArray(), srcX, srcY, srcW, srcH);
    }

    // -- Win32 interop -----------------------------------------------------------

    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT lpPoint);

    // GetDpiForWindow is Windows 10+; we target Windows 10 18362 so it's always
    // available at runtime. Returns the DPI the window currently renders at —
    // 96 is 100%, 120 is 125%, 144 is 150%.
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
