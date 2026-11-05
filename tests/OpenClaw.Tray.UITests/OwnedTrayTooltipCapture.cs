using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Observes the Shell tooltip for this app's registered notification icon.
/// Use after navigating the owned app; an app-name match alone cannot establish icon ownership.
/// </summary>
internal static class OwnedTrayTooltipCapture
{
    internal static async Task AssertAndCaptureAsync(
        GatewayFixtureRun run, AutomationElement appWindow, string topology, string fileName)
    {
        run.EnsureRunning();
        Assert.True(GetPhysicalCursorPos(out var previousCursor));
        AutomationElement? chevron = null;
        try
        {
            var appBounds = appWindow.Current.BoundingRectangle;
            Assert.Equal(run.AppProcessId, appWindow.Current.ProcessId);
            Assert.True(SetPhysicalCursorPos((int)appBounds.Left + 100, (int)appBounds.Top + 100));
            await run.WaitForAsync(() => Task.FromResult(FindTooltip() is null), "previous tray tooltip dismissed");

            Icon? icon = null;
            await run.WaitForAsync(() => Task.FromResult((icon = FindIcon(run)) is not null),
                "notification icon registered by the owned app");
            if (!IsIconVisible(icon!.Value))
            {
                var tray = AutomationElement.FromHandle(FindWindow("Shell_TrayWnd", null));
                chevron = tray.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new OrCondition(
                        new PropertyCondition(AutomationElement.NameProperty, "Show hidden icons"),
                        new PropertyCondition(AutomationElement.NameProperty, "Notification Chevron"))));
                Assert.NotNull(chevron);
                ((InvokePattern)chevron.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                await run.WaitForAsync(() => Task.FromResult(
                    (icon = FindIcon(run)) is { } found && IsIconVisible(found)), "owned icon in notification overflow");
            }

            var ownedIcon = icon!.Value;
            Assert.True(SetPhysicalCursorPos(ownedIcon.Bounds.CenterX, ownedIcon.Bounds.CenterY));
            AutomationElement? tooltip = null;
            await run.WaitForAsync(() => Task.FromResult((tooltip = FindTooltip()) is not null),
                "Shell tooltip for the hovered owned icon");
            Assert.Contains($"; {topology};", tooltip!.Current.Name);
            GetWindowThreadProcessId(FindWindow("Shell_TrayWnd", null), out var shellPid);
            Assert.True(tooltip.Current.ProcessId == shellPid || tooltip.Current.ProcessId == run.AppProcessId,
                "Tooltip must belong to this app or its Windows Shell.");
            Assert.Equal(ownedIcon, FindIcon(run));
            Assert.True(GetPhysicalCursorPos(out var cursor));
            Assert.Equal(ownedIcon.Bounds.CenterX, cursor.X);
            Assert.Equal(ownedIcon.Bounds.CenterY, cursor.Y);

            if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
                Capture(run, ownedIcon, tooltip, fileName);
        }
        finally
        {
            Assert.True(SetPhysicalCursorPos(previousCursor.X, previousCursor.Y));
            if (chevron is not null)
                ((InvokePattern)chevron.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        }
    }

    private static Icon? FindIcon(GatewayFixtureRun run)
    {
        using var dpi = new PhysicalDpiScope();
        var icons = new List<Icon>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (pid != run.AppProcessId) return true;
            // TrayController registers WinUIEx icon 1 on an owned, hidden top-level window.
            var identifier = new IconIdentifier
            {
                Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = window, Id = 1
            };
            if (Shell_NotifyIconGetRect(ref identifier, out var bounds) == 0
                && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top)
                icons.Add(new Icon(window, bounds));
            return true;
        }, IntPtr.Zero);
        return icons.Count == 0 ? null : Assert.Single(icons);
    }

    private static bool IsIconVisible(Icon icon)
    {
        var element = AutomationElement.FromPoint(new System.Windows.Point(icon.Bounds.CenterX, icon.Bounds.CenterY));
        return !element.Current.IsOffscreen && element.Current.Name.StartsWith("OpenClaw", StringComparison.Ordinal);
    }

    private static AutomationElement? FindTooltip() =>
        AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolTip))
            .Cast<AutomationElement>()
            .SingleOrDefault(element => !element.Current.IsOffscreen
                && element.Current.Name.StartsWith("OpenClaw", StringComparison.Ordinal));

    private static void Capture(GatewayFixtureRun run, Icon icon, AutomationElement tooltip, string fileName)
    {
        using var dpi = new PhysicalDpiScope();
        var handle = new IntPtr(tooltip.Current.NativeWindowHandle);
        Assert.True(GetWindowRect(handle, out var rect));
        var area = tooltip.Current.BoundingRectangle;
        Assert.True(area.Width > 0 && area.Height > 0);
        Assert.True(area.Left >= rect.Left && area.Top >= rect.Top
            && area.Right <= rect.Right && area.Bottom <= rect.Bottom, "Tooltip is cropped.");
        using var image = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
        using (var graphics = Graphics.FromImage(image))
        {
            var dc = graphics.GetHdc();
            try { Assert.True(PrintWindow(handle, dc, 2), "Shell tooltip capture failed; no substituted image."); }
            finally { graphics.ReleaseHdc(dc); }
        }
        var colors = new HashSet<int>();
        for (var y = 2; y < image.Height - 2; y++)
            for (var x = 2; x < image.Width - 2; x++)
                colors.Add(image.GetPixel(x, y).ToArgb());
        Assert.True(colors.Count >= 16, "Rejected blank tooltip pixels.");
        Assert.True(GetWindowRect(handle, out var after));
        Assert.Equal(rect, after);
        Assert.Equal(icon, FindIcon(run));
        var path = Path.Combine(run.ArtifactsDirectory, fileName);
        image.Save(path, ImageFormat.Png);
        File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(new
        {
            run.Profile.RunId, run.AppProcessId,
            iconOwnerWindow = $"0x{icon.Window:X}", iconId = 1,
            tooltipProcessId = tooltip.Current.ProcessId, tooltip = tooltip.Current.Name,
            capturedAtUtc = DateTimeOffset.UtcNow, method = "Shell_NotifyIconGetRect + owned icon hover + PrintWindow",
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    // Thread-local state must be restored synchronously, never across an await.
    private sealed class PhysicalDpiScope : IDisposable
    {
        private readonly IntPtr _previous = SetThreadDpiAwarenessContext(new IntPtr(-4));

        public PhysicalDpiScope() => Assert.NotEqual(IntPtr.Zero, _previous);

        public void Dispose() =>
            Assert.NotEqual(IntPtr.Zero, SetThreadDpiAwarenessContext(_previous));
    }

    private readonly record struct Icon(IntPtr Window, NativeRect Bounds);
    [StructLayout(LayoutKind.Sequential)]
    private struct IconIdentifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)]
    private record struct NativeRect(int Left, int Top, int Right, int Bottom)
    {
        public readonly int CenterX => (Left + Right) / 2;
        public readonly int CenterY => (Top + Bottom) / 2;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? name);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
}
