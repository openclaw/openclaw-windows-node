using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;

namespace OpenClawTray.Services;

/// <summary>
/// Screen capture service using GDI+ and Win32 APIs
/// </summary>
public class ScreenCaptureService
{
    private readonly IOpenClawLogger _logger;
    
    public ScreenCaptureService(IOpenClawLogger logger)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Capture a screenshot
    /// </summary>
    public Task<ScreenCaptureResult> CaptureAsync(ScreenCaptureArgs args)
    {
        _logger.Info($"Capturing screen {args.MonitorIndex}, maxWidth={args.MaxWidth}");
        
        // Get screen bounds
        var screens = new System.Collections.Generic.List<(Rectangle bounds, bool isPrimary)>();
        
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
        {
            var info = new MONITORINFOEX();
            info.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            
            if (GetMonitorInfo(hMonitor, ref info))
            {
                var bounds = new Rectangle(
                    info.rcMonitor.left,
                    info.rcMonitor.top,
                    info.rcMonitor.right - info.rcMonitor.left,
                    info.rcMonitor.bottom - info.rcMonitor.top);
                var isPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
                screens.Add((bounds, isPrimary));
            }
            return true;
        }, IntPtr.Zero);
        
        if (screens.Count == 0)
        {
            throw new InvalidOperationException("No screens available");
        }
        
        // Select screen - validate index
        Rectangle targetBounds;
        if (args.MonitorIndex >= 0 && args.MonitorIndex < screens.Count)
        {
            targetBounds = screens[args.MonitorIndex].bounds;
        }
        else
        {
            // Default to primary
            targetBounds = screens.Find(s => s.isPrimary).bounds;
            if (targetBounds.IsEmpty && screens.Count > 0)
            {
                targetBounds = screens[0].bounds;
            }
        }
        
        // Capture screen using GDI with proper resource management
        var width = targetBounds.Width;
        var height = targetBounds.Height;
        
        IntPtr hdcScreen = IntPtr.Zero;
        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr hOld = IntPtr.Zero;
        Bitmap? bitmap = null;
        Bitmap? scaledBitmap = null;
        
        try
        {
            hdcScreen = GetDC(IntPtr.Zero);
            hdcMem = CreateCompatibleDC(hdcScreen);
            hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
            hOld = SelectObject(hdcMem, hBitmap);
            
            BitBlt(hdcMem, 0, 0, width, height, hdcScreen, targetBounds.X, targetBounds.Y, SRCCOPY);
            
            // Optionally include mouse cursor
            if (args.IncludePointer)
            {
                DrawCursor(hdcMem, targetBounds);
            }
            
            SelectObject(hdcMem, hOld);
            hOld = IntPtr.Zero; // Mark as restored
            
            // Convert to Bitmap
            bitmap = Image.FromHbitmap(hBitmap);
            
            // Scale if needed
            Bitmap finalBitmap = bitmap;
            if (width > args.MaxWidth)
            {
                var scale = (double)args.MaxWidth / width;
                var newWidth = args.MaxWidth;
                var newHeight = (int)(height * scale);
                
                scaledBitmap = new Bitmap(bitmap, new Size(newWidth, newHeight));
                finalBitmap = scaledBitmap;
                width = newWidth;
                height = newHeight;
            }
            
            // Encode to base64
            string base64;
            var format = args.Format.ToLowerInvariant() == "jpeg" ? ImageFormat.Jpeg : ImageFormat.Png;
            
            using (var ms = new MemoryStream())
            {
                if (format == ImageFormat.Jpeg)
                {
                    var encoder = GetEncoder(ImageFormat.Jpeg);
                    if (encoder != null)
                    {
                        using var encoderParams = new EncoderParameters(1);
                        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)args.Quality);
                        finalBitmap.Save(ms, encoder, encoderParams);
                    }
                    else
                    {
                        finalBitmap.Save(ms, ImageFormat.Jpeg);
                    }
                }
                else
                {
                    finalBitmap.Save(ms, ImageFormat.Png);
                }
                
                base64 = Convert.ToBase64String(ms.ToArray());
            }
            
            _logger.Info($"Screen captured: {width}x{height}, {base64.Length} bytes");
            
            return Task.FromResult(new ScreenCaptureResult
            {
                Format = args.Format,
                Width = width,
                Height = height,
                Base64 = base64
            });
        }
        finally
        {
            // Always clean up GDI resources
            scaledBitmap?.Dispose();
            bitmap?.Dispose();
            
            if (hOld != IntPtr.Zero && hdcMem != IntPtr.Zero)
                SelectObject(hdcMem, hOld);
            if (hBitmap != IntPtr.Zero)
                DeleteObject(hBitmap);
            if (hdcMem != IntPtr.Zero)
                DeleteDC(hdcMem);
            if (hdcScreen != IntPtr.Zero)
                ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }
    
    private static void DrawCursor(IntPtr hdc, Rectangle bounds)
    {
        try
        {
            CURSORINFO cursorInfo;
            cursorInfo.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            
            if (GetCursorInfo(out cursorInfo) && cursorInfo.flags == CURSOR_SHOWING)
            {
                var iconHandle = CopyIcon(cursorInfo.hCursor);
                if (iconHandle != IntPtr.Zero)
                {
                    try
                    {
                        if (GetIconInfo(iconHandle, out ICONINFO iconInfo))
                        {
                            var x = cursorInfo.ptScreenPos.x - bounds.X - iconInfo.xHotspot;
                            var y = cursorInfo.ptScreenPos.y - bounds.Y - iconInfo.yHotspot;
                            
                            DrawIconEx(hdc, x, y, iconHandle, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
                            
                            if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
                            if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
                        }
                    }
                    finally
                    {
                        DestroyIcon(iconHandle);
                    }
                }
            }
        }
        catch
        {
            // Ignore cursor drawing errors
        }
    }
    
    private static ImageCodecInfo? GetEncoder(ImageFormat format)
    {
        foreach (var codec in ImageCodecInfo.GetImageDecoders())
        {
            if (codec.FormatID == format.Guid)
            {
                return codec;
            }
        }
        return null;
    }
    
    #region Native methods
    
    private const int CURSOR_SHOWING = 1;
    private const int SRCCOPY = 0x00CC0020;
    private const int MONITORINFOF_PRIMARY = 1;
    private const int DI_NORMAL = 0x0003;
    
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }
    
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }
    
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);
    
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);
    
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);
    
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
    
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height, IntPtr hdcSrc, int xSrc, int ySrc, int rop);
    
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);
    
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
    
    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(out CURSORINFO pci);
    
    [DllImport("user32.dll")]
    private static extern IntPtr CopyIcon(IntPtr hIcon);
    
    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
    
    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyHeight, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);
    
    #endregion
}
