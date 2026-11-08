using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace OpenClaw.Tray.UITests;

internal static class MountedControlCapture
{
    internal static async Task RequiredUsageAsync(Window window, string state)
    {
        var directory = Environment.GetEnvironmentVariable("OPENCLAW_MOUNTED_CONTROL_CAPTURE_DIR");
        Assert.False(string.IsNullOrWhiteSpace(directory), "Required Usage capture root is missing.");
        foreach (var extension in new[] { ".png", ".json", ".attempt.json" })
            Assert.False(File.Exists(Path.Combine(directory!, state + extension)), "Required capture must be fresh.");
        await OptionalAsync(window, state);
        foreach (var extension in new[] { ".png", ".json", ".attempt.json" })
        {
            var path = Path.Combine(directory!, state + extension);
            Assert.True(File.Exists(path) && new FileInfo(path).Length > 0, $"Required capture artifact missing: {state + extension}");
        }
        using var image = Image.FromFile(Path.Combine(directory!, state + ".png"));
        Assert.True(image.Width > 0 && image.Height > 0, "Required owned-HWND PNG must decode.");
    }

    internal static async Task<Action> ArrangeUsageSceneAsync(Window window, FrameworkElement scene, string state)
    {
        var directory = Environment.GetEnvironmentVariable("OPENCLAW_MOUNTED_CONTROL_CAPTURE_DIR");
        Assert.False(string.IsNullOrWhiteSpace(directory), "Paired capture requires an explicit artifact root.");
        OnboardingNativeProof.AssertIsolatedRoots();
        var desktop = new StringBuilder(256);
        Assert.True(GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, desktop, 512, out _));
        Assert.NotEqual("Default", desktop.ToString());
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        GetWindowThreadProcessId(handle, out var owner);
        Assert.Equal((uint)Environment.ProcessId, owner);
        var position = window.AppWindow.Position;
        var size = window.AppWindow.Size;
        Directory.CreateDirectory(directory!);
        Action restore = () =>
        {
            window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(position.X, position.Y, size.Width, size.Height));
            Assert.Equal(position, window.AppWindow.Position);
            Assert.Equal(size, window.AppWindow.Size);
            File.WriteAllText(Path.Combine(directory!, state + ".restored.json"), JsonSerializer.Serialize(new
            {
                ownerProcessId = owner, hwnd = handle.ToInt64(), position, size, restoredUtc = DateTimeOffset.UtcNow,
            }));
        };
        try
        {
            var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            var scale = scene.XamlRoot.RasterizationScale;
            Assert.True(scene.ActualWidth > 0 && scene.ActualHeight > 0 && scale > 0);
            var expected = new Windows.Graphics.SizeInt32(
                checked((int)Math.Ceiling(scene.ActualWidth * scale)),
                checked((int)Math.Ceiling(scene.ActualHeight * scale)));
            var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(window.AppWindow.Id,
                Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
            window.AppWindow.ResizeClient(expected);
            window.AppWindow.Move(new Windows.Graphics.PointInt32(work.X, work.Y));
            await OnboardingNativeProof.NextCompositionAsync();
            Assert.Equal(0, DwmFlush());
            root.UpdateLayout();
            Assert.True(GetClientRect(handle, out var client));
            Assert.InRange(Math.Abs(client.Right - client.Left - expected.Width), 0, 1);
            Assert.InRange(Math.Abs(client.Bottom - client.Top - expected.Height), 0, 1);
            var bounds = scene.TransformToVisual(root).TransformBounds(
                new Windows.Foundation.Rect(0, 0, scene.ActualWidth, scene.ActualHeight));
            Assert.True(bounds.Left >= 0 && bounds.Top >= 0 &&
                bounds.Right * scale <= expected.Width + 1 && bounds.Bottom * scale <= expected.Height + 1,
                "Complete mounted scene must fit the owned HWND client.");
            File.WriteAllText(Path.Combine(directory!, state + ".arranged.json"), JsonSerializer.Serialize(new
            {
                ownerProcessId = owner, hwnd = handle.ToInt64(),
                processStartUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                desktop = desktop.ToString(), scale, expected,
                clientWidth = client.Right - client.Left, clientHeight = client.Bottom - client.Top,
                sceneBounds = bounds,
                arrangedUtc = DateTimeOffset.UtcNow,
            }));
            return restore;
        }
        catch (Exception failure)
        {
            try { restore(); }
            catch (Exception restoration)
            {
                throw new AggregateException("Scene arrangement and independent restoration failed.", failure, restoration);
            }
            throw;
        }
    }

    internal static async Task OptionalAsync(Window window, string state)
    {
        var directory = Environment.GetEnvironmentVariable("OPENCLAW_MOUNTED_CONTROL_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            return;

        OnboardingNativeProof.AssertIsolatedRoots();
        var desktop = new StringBuilder(256);
        Assert.True(GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2,
            desktop, 512, out _));
        Assert.NotEqual("Default", desktop.ToString());
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        GetWindowThreadProcessId(handle, out var owner);
        Assert.Equal((uint)Environment.ProcessId, owner);
        var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(window.AppWindow.Id,
            Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        // Mounted fixtures normally live offscreen. Requested capture stays on the private desktop.
        window.AppWindow.Move(new Windows.Graphics.PointInt32(work.X, work.Y));
        await OnboardingNativeProof.NextCompositionAsync();
        Assert.Equal(0, DwmFlush());
        Assert.True(IsWindowVisible(handle));
        Assert.False(IsIconic(handle));
        Assert.True(GetWindowRect(handle, out var bounds));
        Assert.True(bounds.Right > bounds.Left && bounds.Bottom > bounds.Top);
        using var bitmap = new Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            try
            {
                Assert.True(PrintWindow(handle, dc, 2), "Owned-window capture failed. No fallback is permitted.");
            }
            finally
            {
                graphics.ReleaseHdc(dc);
            }
        }
        var colors = new HashSet<int>();
        for (var y = 0; y < bitmap.Height; y += Math.Max(1, bitmap.Height / 40))
            for (var x = 0; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 40))
                colors.Add(bitmap.GetPixel(x, y).ToArgb());
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, state + ".attempt.json"), JsonSerializer.Serialize(new
        {
            state,
            qualification = "Capture attempt only. Not evidence of rendered content.",
            method = "PrintWindow(PW_RENDERFULLCONTENT); no fallback",
            head = Environment.GetEnvironmentVariable("OPENCLAW_MOUNTED_CONTROL_CAPTURE_HEAD"),
            assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MountedControlCapture).Assembly.Location))),
            desktop = desktop.ToString(),
            ownerProcessId = owner,
            left = bounds.Left,
            top = bounds.Top,
            right = bounds.Right,
            bottom = bounds.Bottom,
            workLeft = work.X,
            workTop = work.Y,
            workWidth = work.Width,
            workHeight = work.Height,
            sampledColors = colors.Count,
            attemptedUtc = DateTimeOffset.UtcNow,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(bounds.Left >= work.X && bounds.Top >= work.Y &&
            bounds.Right <= work.X + work.Width && bounds.Bottom <= work.Y + work.Height,
            "Owned capture window must fit within its display work area.");
        Assert.True(colors.Count >= 8, "Mounted production-control frame is blank or near-uniform.");
        var image = Path.Combine(directory, state + ".png");
        bitmap.Save(image, ImageFormat.Png);
        File.WriteAllText(Path.Combine(directory, state + ".json"), JsonSerializer.Serialize(new
        {
            state,
            qualification = "Synthetic dependencies; private owned production window. Not native compositor, keyboard, installed-app, or live Gateway proof.",
            method = "PrintWindow(PW_RENDERFULLCONTENT); no fallback",
            head = Environment.GetEnvironmentVariable("OPENCLAW_MOUNTED_CONTROL_CAPTURE_HEAD"),
            assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MountedControlCapture).Assembly.Location))),
            imageSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(image))),
            width = bitmap.Width,
            height = bitmap.Height,
            sampledColors = colors.Count,
            capturedUtc = DateTimeOffset.UtcNow,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Bounds { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Bounds bounds);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Bounds bounds);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder value,
        uint length, out uint needed);
}
