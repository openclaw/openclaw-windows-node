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
    internal static T WithPhysicalCoordinates<T>(Func<T> read)
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try { return read(); }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    internal sealed record WindowGeometry(string Hwnd, uint ProcessId, DateTime ProcessStartedUtc, uint Dpi,
        Rectangle WindowPixels, Rectangle ClientPixels, Size SystemMaxTrackPixels);

    internal static WindowGeometry ReadCurrentProcessGeometry(IntPtr handle) => WithPhysicalCoordinates(() =>
    {
        GetWindowThreadProcessId(handle, out var owner);
        Assert.Equal((uint)Environment.ProcessId, owner);
        Assert.True(GetWindowRect(handle, out var window), "Cannot measure the owned window rectangle.");
        Assert.True(GetClientRect(handle, out var client), "Cannot measure the owned client rectangle.");
        var dpi = GetDpiForWindow(handle);
        Assert.True(dpi > 0);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        const int smCxMaxTrack = 59, smCyMaxTrack = 60;
        return new WindowGeometry($"0x{handle:X}", owner, process.StartTime.ToUniversalTime(), dpi,
            Rectangle.FromLTRB(window.Left, window.Top, window.Right, window.Bottom),
            Rectangle.FromLTRB(client.Left, client.Top, client.Right, client.Bottom),
            new Size(GetSystemMetricsForDpi(smCxMaxTrack, dpi), GetSystemMetricsForDpi(smCyMaxTrack, dpi)));
    });

    internal static global::System.Windows.Rect ReadOwnedBounds(GatewayFixtureRun run, AutomationElement window)
    {
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        Assert.Equal(run.AppProcessId, window.Current.ProcessId);
        GetWindowThreadProcessId(handle, out var processId);
        Assert.Equal((uint)run.AppProcessId, processId);
        Assert.True(GetWindowRect(handle, out var rect));
        return new global::System.Windows.Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    internal static double OwnedDpiScale(GatewayFixtureRun run, AutomationElement window)
    {
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        GetWindowThreadProcessId(handle, out var owner);
        Assert.Equal((uint)run.AppProcessId, owner);
        var dpi = GetDpiForWindow(handle);
        Assert.True(dpi > 0);
        return dpi / 96d;
    }

    internal static global::System.Windows.Rect ReadOwnedClientBounds(GatewayFixtureRun run, AutomationElement window)
    {
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        Assert.Equal(run.AppProcessId, window.Current.ProcessId);
        GetWindowThreadProcessId(handle, out var owner);
        Assert.Equal((uint)run.AppProcessId, owner);
        Assert.True(GetClientRect(handle, out var client));
        var origin = new System.Drawing.Point();
        Assert.True(ClientToScreen(handle, ref origin));
        return new global::System.Windows.Rect(origin.X, origin.Y,
            client.Right - client.Left, client.Bottom - client.Top);
    }

    internal static void ResizeOwnedPixels(GatewayFixtureRun run, AutomationElement window, int width, int height)
    {
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        GetWindowThreadProcessId(handle, out var owner);
        Assert.Equal((uint)run.AppProcessId, owner);
        Assert.True(SetWindowPos(handle, IntPtr.Zero, 0, 0, width, height, 0x0006),
            $"SetWindowPos failed for owned HWND 0x{handle:X}.");
    }

    internal static int CorrectedWindowWidthPixels(int currentPixels, double desiredHostDip, double measuredHostDip, double scale) =>
        Math.Max((int)Math.Ceiling(1000 * scale),
            (int)Math.Round(currentPixels + (desiredHostDip - measuredHostDip) * scale, MidpointRounding.AwayFromZero));

    internal static void Save(GatewayFixtureRun run, AutomationElement window, string name,
        params AutomationElement[] assertedElements) => Save(run, window, name, CancellationToken.None, assertedElements);

    internal static void Save(GatewayFixtureRun run, AutomationElement window, string name,
        CancellationToken cancellationToken, params AutomationElement[] assertedElements)
    {
        cancellationToken.ThrowIfCancellationRequested();
        run.EnsureRunning();
        SaveCore(run.AppProcessId, run.Profile.RunId, run.ArtifactsDirectory, window, name, assertedElements, cancellationToken);
    }

    internal static void SaveCurrentProcessWindow(IntPtr handle, string runId, string artifactsDirectory, string name,
        WindowGeometry? qualifiedGeometry = null)
    {
        GetWindowThreadProcessId(handle, out var owner);
        Assert.Equal((uint)Environment.ProcessId, owner);
        Assert.Equal(handle, GetForegroundWindow());
        SaveCore(Environment.ProcessId, runId, artifactsDirectory, AutomationElement.FromHandle(handle), name, [],
            qualifiedGeometry: qualifiedGeometry);
    }

    private static void SaveCore(int appProcessId, string runId, string artifactsDirectory, AutomationElement window,
        string name, AutomationElement[] assertedElements, CancellationToken cancellationToken = default,
        WindowGeometry? qualifiedGeometry = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        Assert.Equal(appProcessId, window.Current.ProcessId);
        GetWindowThreadProcessId(handle, out var processId);
        Assert.Equal((uint)appProcessId, processId);
        Assert.True(IsWindowVisible(handle) && !IsIconic(handle), "Owned window must be visible and not minimized.");
        var previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            Assert.True(GetWindowRect(handle, out var rect));
            var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            AssertCaptureGeometry(bounds, qualifiedGeometry,
                qualifiedGeometry is null ? null : ReadCurrentProcessGeometry(handle));
            var assertions = assertedElements.Select(element =>
            {
                Assert.Equal(appProcessId, element.Current.ProcessId);
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
            if (qualifiedGeometry is not null)
                AssertCaptureGeometry(bounds, qualifiedGeometry, ReadCurrentProcessGeometry(handle));
            // Exclude the title bar and native border: a rendered caption around blank content is not proof.
            var colors = new HashSet<int>();
            for (var y = 70; y < image.Height - 20; y += Math.Max(1, image.Height / 100))
                for (var x = 20; x < image.Width - 20; x += Math.Max(1, image.Width / 100))
                    colors.Add(image.GetPixel(x, y).ToArgb());
            Assert.True(colors.Count >= 16, $"Rejected blank native content ({colors.Count} sampled colors).");
            // Do not publish an image that returns after owned termination.
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(artifactsDirectory, name);
            image.Save(path, ImageFormat.Png);
            File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(new
            {
                RunId = runId, AppProcessId = appProcessId, hwnd = $"0x{handle:X}",
                capturedAtUtc = DateTimeOffset.UtcNow, bounds, qualifiedGeometry, sampledContentColors = colors.Count,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                assertions, method = "PrintWindow(PW_RENDERFULLCONTENT); owned HWND only; synthetic Gateway"
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { SetThreadDpiAwarenessContext(previousDpi); }
    }

    /// <summary>
    /// Ordinary captures keep their minimum bounds. A caller that already proved an exact
    /// native viewport may instead pin that HWND/process/DPI/client/frame geometry for capture.
    /// This admits a narrow image, not its layout, foreground, pixels or accessibility.
    /// </summary>
    internal static void AssertCaptureGeometry(Rectangle bounds, WindowGeometry? qualifiedGeometry = null,
        WindowGeometry? measuredGeometry = null)
    {
        Assert.True(bounds.Width > 0 && bounds.Height >= 300);
        if (qualifiedGeometry is null)
        {
            Assert.True(bounds.Width >= 400);
            return;
        }
        Assert.NotNull(measuredGeometry);
        Assert.Equal(qualifiedGeometry, measuredGeometry);
        Assert.Equal(qualifiedGeometry.WindowPixels, bounds);
        Assert.True(qualifiedGeometry.Dpi > 0 && qualifiedGeometry.ProcessId > 0);
        Assert.True(qualifiedGeometry.ClientPixels.Width > 0 && qualifiedGeometry.ClientPixels.Height > 0);
        Assert.True(bounds.Width >= qualifiedGeometry.ClientPixels.Width
            && bounds.Height >= qualifiedGeometry.ClientPixels.Height);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref System.Drawing.Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
