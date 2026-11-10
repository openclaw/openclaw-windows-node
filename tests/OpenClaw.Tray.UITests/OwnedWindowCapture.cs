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
    private const int MaximumConfigTreeDiagnosticElements = 64;

    /// <summary>Reads only the synthetic app's Config tree, never the desktop or another app.</summary>
    internal static string ReadConfigTreeAutomation(GatewayFixtureRun run, AutomationElement window)
    {
        ValidateOwnedWindow(run, window);
        var tree = window.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, run.AppProcessId),
            new PropertyCondition(AutomationElement.AutomationIdProperty, "ConfigTree")));
        if (tree is null)
            return JsonSerializer.Serialize(new { diagnostic = "ConfigTreeUIA", status = "tree-unavailable" });

        var rows = new List<object>();
        var pending = new Stack<AutomationElement>();
        var walker = TreeWalker.RawViewWalker;
        var truncated = false;
        pending.Push(tree);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            var current = element.Current;
            Assert.Equal(run.AppProcessId, current.ProcessId);
            rows.Add(new { ControlType = current.ControlType.ProgrammaticName,
                current.Name, current.AutomationId, current.IsOffscreen });
            var child = walker.GetFirstChild(element);
            while (child is not null)
            {
                if (rows.Count + pending.Count >= MaximumConfigTreeDiagnosticElements)
                {
                    truncated = true;
                    break;
                }
                Assert.Equal(run.AppProcessId, child.Current.ProcessId);
                pending.Push(child);
                child = walker.GetNextSibling(child);
            }
        }
        return JsonSerializer.Serialize(new { diagnostic = "ConfigTreeUIA", status = "captured",
            truncated, rows });
    }

    private static IntPtr ValidateOwnedWindow(GatewayFixtureRun run, AutomationElement window)
    {
        run.EnsureRunning();
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        Assert.Equal(run.AppProcessId, window.Current.ProcessId);
        GetWindowThreadProcessId(handle, out var processId);
        Assert.Equal((uint)run.AppProcessId, processId);
        Assert.True(IsWindowVisible(handle) && !IsIconic(handle), "Owned window must be visible and not minimized.");
        return handle;
    }

    internal static void Save(GatewayFixtureRun run, AutomationElement window, string name,
        params AutomationElement[] assertedElements) => Save(run, window, name, CancellationToken.None, assertedElements);

    internal static void Save(GatewayFixtureRun run, AutomationElement window, string name,
        CancellationToken cancellationToken, params AutomationElement[] assertedElements)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handle = ValidateOwnedWindow(run, window);
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
            // A timed-out PrintWindow can return after owned termination. Do not publish
            // its late image. The failure owner also joins this worker before disposal.
            cancellationToken.ThrowIfCancellationRequested();
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
