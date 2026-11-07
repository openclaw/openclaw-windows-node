using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;

namespace OpenClaw.Tray.UITests;

/// <summary>Captures only an owned external app HWND, including its complete native bounds.</summary>
internal static class OwnedWindowCapture
{
    internal static void Save(GatewayFixtureRun run, AutomationElement window, string name,
        params AutomationElement[] assertedElements)
    {
        run.EnsureRunning();
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        Assert.Equal(run.AppProcessId, window.Current.ProcessId);
        GetWindowThreadProcessId(handle, out var processId);
        Assert.Equal((uint)run.AppProcessId, processId);
        Assert.True(IsWindowVisible(handle) && !IsIconic(handle), "Owned window must be visible and not minimized.");
        var previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            Assert.True(GetWindowRect(handle, out var rect));
            var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            Assert.True(bounds.Width >= 400 && bounds.Height >= 300);
            var assertions = assertedElements.Select(element =>
            {
                Assert.Equal(run.AppProcessId, element.Current.ProcessId);
                Assert.False(element.Current.IsOffscreen);
                var area = element.Current.BoundingRectangle;
                Assert.True(area.Width > 0 && area.Height > 0);
                Assert.True(area.Left >= bounds.Left && area.Top >= bounds.Top &&
                    area.Right <= bounds.Right && area.Bottom <= bounds.Bottom,
                    $"Asserted {element.Current.AutomationId} is cropped.");
                return new { element.Current.AutomationId, element.Current.Name, element.Current.IsEnabled,
                    left = area.Left - bounds.Left, top = area.Top - bounds.Top, area.Width, area.Height };
            }).ToArray();
            using var image = new Bitmap(bounds.Width, bounds.Height);
            using (var graphics = Graphics.FromImage(image))
            {
                var dc = graphics.GetHdc();
                try { Assert.True(PrintWindow(handle, dc, 2), "Owned-HWND PrintWindow failed; no desktop fallback."); }
                finally { graphics.ReleaseHdc(dc); }
            }
            Assert.True(GetWindowRect(handle, out var after));
            Assert.Equal(rect, after);
            // Exclude the title bar and native border: a rendered caption around blank content is not proof.
            var colors = new HashSet<int>();
            for (var y = 70; y < image.Height - 20; y += Math.Max(1, image.Height / 100))
                for (var x = 20; x < image.Width - 20; x += Math.Max(1, image.Width / 100))
                    colors.Add(image.GetPixel(x, y).ToArgb());
            Assert.True(colors.Count >= 16, $"Rejected blank native content ({colors.Count} sampled colors).");
            var path = Path.Combine(run.ArtifactsDirectory, name);
            image.Save(path, ImageFormat.Png);
            File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(new
            {
                run.Profile.RunId, run.AppProcessId, hwnd = $"0x{handle:X}",
                capturedAtUtc = DateTimeOffset.UtcNow, bounds, sampledContentColors = colors.Count,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                assertions, method = "PrintWindow(PW_RENDERFULLCONTENT); owned HWND only; synthetic Gateway"
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { SetThreadDpiAwarenessContext(previousDpi); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
