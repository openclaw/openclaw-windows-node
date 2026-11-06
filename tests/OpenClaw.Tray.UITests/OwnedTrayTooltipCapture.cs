using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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
            MoveCursorTo((int)appBounds.Left + 100, (int)appBounds.Top + 100);
            await run.WaitForAsync(() => Task.FromResult(FindTooltip(run) is null), "previous tray tooltip dismissed");

            Icon? icon = null;
            await run.WaitForAsync(() => Task.FromResult((icon = FindIcon(run)) is not null),
                "notification icon registered by the owned app");
            WriteShellObservation(run, icon!.Value);
            if (!IsIconVisible(icon!.Value))
            {
                var tray = AutomationElement.FromHandle(FindWindow("Shell_TrayWnd", null));
                chevron = tray.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new OrCondition(
                        new PropertyCondition(AutomationElement.NameProperty, "Show hidden icons", PropertyConditionFlags.IgnoreCase),
                        new PropertyCondition(AutomationElement.NameProperty, "Notification Chevron", PropertyConditionFlags.IgnoreCase))));
                Assert.NotNull(chevron);
                ((InvokePattern)chevron.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                WriteOverflowDiagnostics(run, "after-overflow-invoke");
                try
                {
                    await run.WaitForAsync(() => Task.FromResult(
                        (icon = FindIcon(run)) is { } found && IsIconVisible(found)), "owned icon in notification overflow");
                }
                catch (TimeoutException)
                {
                    WriteOverflowDiagnostics(run, "overflow-timeout");
                    throw;
                }
                WriteShellObservation(run, icon!.Value, "notification-area-open.json");
            }

            var ownedIcon = icon!.Value;
            MoveCursorTo(ownedIcon.Bounds.CenterX, ownedIcon.Bounds.CenterY);
            AutomationElement? tooltip = null;
            try
            {
                await run.WaitForAsync(() => Task.FromResult((tooltip = FindTooltip(run, topology)) is not null),
                    "Shell tooltip for the hovered owned icon");
            }
            catch (TimeoutException)
            {
                WriteOverflowDiagnostics(run, "tooltip-timeout");
                throw;
            }
            Assert.Contains($"; {topology};", tooltip!.Current.Name);
            GetWindowThreadProcessId(FindWindow("Shell_TrayWnd", null), out var shellPid);
            Assert.True(tooltip.Current.ProcessId == shellPid || tooltip.Current.ProcessId == run.AppProcessId,
                "Tooltip must belong to this app or its Windows Shell.");
            Assert.Equal(ownedIcon, FindIcon(run));
            Assert.True(GetPhysicalCursorPos(out var cursor));
            Assert.InRange(cursor.X, ownedIcon.Bounds.Left, ownedIcon.Bounds.Right - 1);
            Assert.InRange(cursor.Y, ownedIcon.Bounds.Top, ownedIcon.Bounds.Bottom - 1);

            if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
                Capture(run, ownedIcon, tooltip, fileName);
        }
        finally
        {
            MoveCursorTo(previousCursor.X, previousCursor.Y);
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
        using var dpi = new PhysicalDpiScope();
        var shellPid = GetShellProcessId();
        foreach (var window in FindVisibleWindows(shellPid))
        {
            var buttons = AutomationElement.FromHandle(window).FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement button in buttons)
            {
                var current = button.Current;
                var bounds = current.BoundingRectangle;
                if (current.ProcessId == shellPid && !current.IsOffscreen
                    && current.Name.StartsWith("OpenClaw", StringComparison.Ordinal)
                    && Math.Abs(bounds.Left - icon.Bounds.Left) <= 1
                    && Math.Abs(bounds.Top - icon.Bounds.Top) <= 1
                    && Math.Abs(bounds.Right - icon.Bounds.Right) <= 1
                    && Math.Abs(bounds.Bottom - icon.Bounds.Bottom) <= 1)
                    return true;
            }
        }
        return false;
    }

    private static void WriteShellObservation(GatewayFixtureRun run, Icon icon, string fileName = "notification-area.json")
    {
        using var dpi = new PhysicalDpiScope();
        var taskbarHandle = FindWindow("Shell_TrayWnd", null);
        Assert.NotEqual(IntPtr.Zero, taskbarHandle);
        var taskbar = AutomationElement.FromHandle(taskbarHandle);
        var hit = AutomationElement.FromPoint(new System.Windows.Point(icon.Bounds.CenterX, icon.Bounds.CenterY));
        var buttons = taskbar.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>().Select(element => new
            {
                element.Current.Name, element.Current.AutomationId, element.Current.ProcessId,
                element.Current.IsOffscreen
            }).ToArray();
        File.WriteAllText(Path.Combine(run.ArtifactsDirectory, fileName),
            JsonSerializer.Serialize(new
            {
                run.AppProcessId, iconWindow = $"0x{icon.Window:X}",
                x = icon.Bounds.CenterX, y = icon.Bounds.CenterY,
                hit = new { hit.Current.Name, hit.Current.AutomationId, hit.Current.ProcessId,
                    hit.Current.IsOffscreen }, buttons
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteOverflowDiagnostics(GatewayFixtureRun run, string phase)
    {
        // Diagnostic-only CI collection. Never capture a developer's Shell surface.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            return;
        using var dpi = new PhysicalDpiScope();
        GetWindowThreadProcessId(FindWindow("Shell_TrayWnd", null), out var shellPid);
        var windows = new List<(IntPtr Handle, string ClassName, uint ProcessId)>();
        EnumWindows((handle, _) =>
        {
            var className = new StringBuilder(256);
            GetWindowThreadProcessId(handle, out var pid);
            GetClassName(handle, className, className.Capacity);
            var name = className.ToString();
            if (name != "Shell_TrayWnd" && !name.Contains("Overflow", StringComparison.OrdinalIgnoreCase))
                return true;
            windows.Add((handle, name, pid));
            return true;
        }, IntPtr.Zero);
        var observations = new List<object>();
        foreach (var (handle, className, pid) in windows)
        {
            var hasBounds = GetWindowRect(handle, out var bounds);
            var visible = IsWindowVisible(handle);
            var elements = new List<object>();
            string? uiaError = null;
            string? imagePath = null;
            bool? printWindowSucceeded = null;
            if (pid == shellPid && visible)
            {
                try
                {
                    foreach (AutomationElement element in AutomationElement.FromHandle(handle)
                        .FindAll(TreeScope.Subtree, Condition.TrueCondition).Cast<AutomationElement>().Take(200))
                    {
                        var current = element.Current;
                        elements.Add(new { current.Name, current.AutomationId, current.ProcessId,
                            current.IsOffscreen, type = current.ControlType.ProgrammaticName,
                            bounds = current.BoundingRectangle.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                    }
                }
                catch (ElementNotAvailableException ex) { uiaError = ex.Message; }
                catch (COMException ex) { uiaError = ex.Message; }
                if (hasBounds && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top)
                {
                    using var bitmap = new Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        var dc = graphics.GetHdc();
                        try { printWindowSucceeded = PrintWindow(handle, dc, 2); }
                        finally { graphics.ReleaseHdc(dc); }
                    }
                    imagePath = $"{phase}-{handle:X}.png";
                    bitmap.Save(Path.Combine(run.ArtifactsDirectory, imagePath), ImageFormat.Png);
                }
            }
            observations.Add(new { hwnd = $"0x{handle:X}", className, pid, visible, hasBounds, bounds,
                elements, uiaError, imagePath, printWindowSucceeded,
                imageSha256 = imagePath is null ? null : Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(Path.Combine(run.ArtifactsDirectory, imagePath)))) });
        }
        var icon = FindIcon(run);
        File.WriteAllText(Path.Combine(run.ArtifactsDirectory, $"{phase}.json"),
            JsonSerializer.Serialize(new
            {
                diagnosticOnly = true, phase, capturedAtUtc = DateTimeOffset.UtcNow, shellPid,
                iconWindow = icon is { } found ? $"0x{found.Window:X}" : null,
                iconBounds = icon?.Bounds, observations
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static AutomationElement? FindTooltip(GatewayFixtureRun run, string? topology = null)
    {
        using var dpi = new PhysicalDpiScope();
        var shellPid = GetShellProcessId();
        var appPid = checked((uint)run.AppProcessId);
        foreach (var window in FindVisibleWindows(shellPid, appPid))
        {
            var elements = AutomationElement.FromHandle(window).FindAll(TreeScope.Subtree,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolTip));
            foreach (AutomationElement element in elements)
            {
                var current = element.Current;
                if (!current.IsOffscreen
                    && current.Name.StartsWith("OpenClaw", StringComparison.Ordinal)
                    && (topology is null || current.Name.Contains($"; {topology};", StringComparison.Ordinal)))
                    return element;
            }
        }
        return null;
    }

    private static uint GetShellProcessId()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        Assert.NotEqual(IntPtr.Zero, taskbar);
        GetWindowThreadProcessId(taskbar, out var shellPid);
        Assert.NotEqual(0u, shellPid);
        return shellPid;
    }

    private static void MoveCursorTo(int x, int y)
    {
        using var dpi = new PhysicalDpiScope();
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78);
        var height = GetSystemMetrics(79);
        Assert.True(width > 1 && height > 1);
        var absoluteX = (int)Math.Round((x - left) * 65535d / (width - 1));
        var absoluteY = (int)Math.Round((y - top) * 65535d / (height - 1));
        Assert.InRange(absoluteX, 0, 65535);
        Assert.InRange(absoluteY, 0, 65535);
        var input = new NativeInput
        {
            Type = 0,
            Mouse = new MouseInput
            {
                X = absoluteX,
                Y = absoluteY,
                Flags = 0x0001 | 0x4000 | 0x8000
            }
        };
        Assert.Equal(1u, SendInput(1, [input], Marshal.SizeOf<NativeInput>()));
        Assert.True(GetPhysicalCursorPos(out var cursor));
        Assert.InRange(cursor.X, x - 1, x + 1);
        Assert.InRange(cursor.Y, y - 1, y + 1);
    }

    private static IReadOnlyList<IntPtr> FindVisibleWindows(params uint[] processIds)
    {
        var owners = processIds.ToHashSet();
        var windows = new List<IntPtr>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window))
                return true;
            GetWindowThreadProcessId(window, out var processId);
            if (owners.Contains(processId))
                windows.Add(window);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static void Capture(GatewayFixtureRun run, Icon icon, AutomationElement tooltip, string fileName)
    {
        using var dpi = new PhysicalDpiScope();
        var area = tooltip.Current.BoundingRectangle;
        Assert.True(area.Width > 0 && area.Height > 0);
        var point = new NativePoint
        {
            X = (int)(area.Left + area.Width / 2),
            Y = (int)(area.Top + area.Height / 2)
        };
        var handle = GetAncestor(WindowFromPoint(point), 2);
        Assert.True(handle != IntPtr.Zero, "Could not resolve the visible Shell tooltip window.");
        GetWindowThreadProcessId(handle, out var processId);
        Assert.True(processId == GetShellProcessId() || processId == run.AppProcessId,
            "Captured window must belong to this app or its Windows Shell.");
        Assert.True(GetWindowRect(handle, out var rect));
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
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct NativeInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public nuint ExtraInfo;
    }
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? name);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
}
