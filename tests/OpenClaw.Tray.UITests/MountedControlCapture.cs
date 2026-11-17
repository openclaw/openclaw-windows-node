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
    internal static async Task<Action?> ArrangeSceneAsync(Window window, FrameworkElement scene, string state)
    {
        var directory = Environment.GetEnvironmentVariable("OPENCLAW_MOUNTED_CONTROL_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            return null;
        OnboardingNativeProof.AssertIsolatedRoots();
        var desktop = new StringBuilder(256);
        Assert.True(GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, desktop, 512, out _));
        Assert.NotEqual("Default", desktop.ToString());
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        GetWindowThreadProcessId(handle, out var owner);
        Assert.Equal((uint)Environment.ProcessId, owner);
        using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
        var processCreationUtc = currentProcess.StartTime.ToUniversalTime();
        var position = window.AppWindow.Position;
        var size = window.AppWindow.Size;
        var failures = new List<Exception>();
        Windows.Graphics.SizeInt32? requestedClient = null;
        Windows.Graphics.RectInt32? selectedWork = null;
        FrameworkElement? root = null;
        void RecordStage(string stage)
        {
            Assert.Equal(handle, WinRT.Interop.WindowNative.GetWindowHandle(window));
            Assert.NotEqual(0u, GetWindowThreadProcessId(handle, out var currentOwner));
            Assert.Equal(owner, currentOwner);
            Assert.True(GetWindowRect(handle, out var windowBounds));
            Assert.True(GetClientRect(handle, out var clientBounds));
            var dpi = GetDpiForWindow(handle);
            Assert.NotEqual(0u, dpi);
            var currentWork = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(window.AppWindow.Id,
                Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
            object Extent(FrameworkElement element) => new
            {
                type = element.GetType().Name,
                element.ActualWidth, element.ActualHeight,
                desiredWidth = element.DesiredSize.Width, desiredHeight = element.DesiredSize.Height,
                element.IsLoaded, hasXamlRoot = element.XamlRoot is not null,
                rasterizationScale = element.XamlRoot?.RasterizationScale,
            };
            FrameworkElement? FindHost(DependencyObject parent)
            {
                if (parent is Microsoft.UI.Reactor.Hosting.ReactorHostControl element)
                    return element;
                for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
                    if (FindHost(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index)) is { } child)
                        return child;
                return null;
            }
            var host = FindHost(scene);
            var overlapped = window.AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, state + "." + stage + ".arrangement.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, state, stage, recordedUtc = DateTimeOffset.UtcNow,
                    ownerProcessId = currentOwner, processCreationUtc, hwnd = $"0x{handle:X}", desktop = desktop.ToString(),
                    nativeThreadId = GetCurrentThreadId(),
                    visible = IsWindowVisible(handle), iconic = IsIconic(handle),
                    presenterType = window.AppWindow.Presenter.GetType().Name,
                    presenterState = overlapped?.State.ToString(),
                    windowX = window.AppWindow.Position.X, windowY = window.AppWindow.Position.Y,
                    windowWidth = window.AppWindow.Size.Width, windowHeight = window.AppWindow.Size.Height,
                    appClientWidth = window.AppWindow.ClientSize.Width,
                    appClientHeight = window.AppWindow.ClientSize.Height,
                    windowLeft = windowBounds.Left, windowTop = windowBounds.Top,
                    windowRight = windowBounds.Right, windowBottom = windowBounds.Bottom,
                    clientLeft = clientBounds.Left, clientTop = clientBounds.Top,
                    clientRight = clientBounds.Right, clientBottom = clientBounds.Bottom,
                    nativeDpi = dpi, sceneRasterizationScale = scene.XamlRoot?.RasterizationScale,
                    root = root is null ? null : Extent(root), border = Extent(scene),
                    hostType = typeof(Microsoft.UI.Reactor.Hosting.ReactorHostControl).FullName,
                    hostPresent = host is not null, host = host is null ? null : Extent(host),
                    requestedClientWidth = requestedClient?.Width, requestedClientHeight = requestedClient?.Height,
                    selectedWorkX = selectedWork?.X, selectedWorkY = selectedWork?.Y,
                    selectedWorkWidth = selectedWork?.Width, selectedWorkHeight = selectedWork?.Height,
                    currentWorkX = currentWork.X, currentWorkY = currentWork.Y,
                    currentWorkWidth = currentWork.Width, currentWorkHeight = currentWork.Height,
                    monitorSelection = "DisplayArea.GetFromWindowId(owned AppWindow.Id, Nearest)",
                    qualification = "Test-window arrangement diagnostics. Not pixel or compositor acceptance.",
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Action restore = () =>
        {
            var restoreFailures = new List<Exception>();
            try { RecordStage("beforeRestoration"); }
            catch (Exception failure) { restoreFailures.Add(failure); }
            try
            {
                Assert.Equal(handle, WinRT.Interop.WindowNative.GetWindowHandle(window));
                Assert.NotEqual(0u, GetWindowThreadProcessId(handle, out var restoreOwner));
                Assert.Equal(owner, restoreOwner);
                window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(position.X, position.Y, size.Width, size.Height));
                Assert.Equal(position, window.AppWindow.Position);
                Assert.Equal(size, window.AppWindow.Size);
                File.WriteAllText(Path.Combine(directory, state + ".restored.json"), JsonSerializer.Serialize(new
                {
                    ownerProcessId = owner, processCreationUtc,
                    position.X, position.Y, size.Width, size.Height, restoredUtc = DateTimeOffset.UtcNow,
                }));
            }
            catch (Exception failure) { restoreFailures.Add(failure); }
            try { RecordStage("afterRestoration"); }
            catch (Exception failure) { restoreFailures.Add(failure); }
            if (restoreFailures.Count > 0)
                throw new AggregateException("Owned capture restoration or diagnostic persistence failed.", restoreFailures);
        };
        try
        {
            Directory.CreateDirectory(directory);
            root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            RecordStage("beforeMeasure");
            root.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            RecordStage("afterMeasure");
            var scale = scene.XamlRoot.RasterizationScale;
            Assert.True(scene.ActualWidth > 0 && scene.ActualHeight > 0 && scale > 0);
            var expected = new Windows.Graphics.SizeInt32(
                checked((int)Math.Ceiling(root.DesiredSize.Width * scale)),
                checked((int)Math.Ceiling(root.DesiredSize.Height * scale)));
            var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(window.AppWindow.Id,
                Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
            requestedClient = expected;
            selectedWork = work;
            RecordStage("beforeResize");
            window.AppWindow.ResizeClient(expected);
            RecordStage("afterResize");
            window.AppWindow.Move(new Windows.Graphics.PointInt32(work.X, work.Y));
            RecordStage("afterMove");
            await OnboardingNativeProof.NextCompositionAsync();
            Assert.Equal(0, DwmFlush());
            RecordStage("afterComposition");
            root.UpdateLayout();
            RecordStage("afterUpdateLayout");
            Assert.True(GetClientRect(handle, out var client));
            Assert.InRange(Math.Abs(client.Right - client.Left - expected.Width), 0, 1);
            Assert.InRange(Math.Abs(client.Bottom - client.Top - expected.Height), 0, 1);
            var sceneBounds = scene.TransformToVisual(root).TransformBounds(
                new Windows.Foundation.Rect(0, 0, scene.ActualWidth, scene.ActualHeight));
            Assert.InRange(sceneBounds.Left * scale, 0, expected.Width);
            Assert.InRange(sceneBounds.Top * scale, 0, expected.Height);
            Assert.InRange(sceneBounds.Right * scale, 0, expected.Width);
            Assert.InRange(sceneBounds.Bottom * scale, 0, expected.Height);
            File.WriteAllText(Path.Combine(directory, state + ".arranged.json"), JsonSerializer.Serialize(new
            {
                ownerProcessId = owner, desktop = desktop.ToString(),
                sceneWidth = scene.ActualWidth, sceneHeight = scene.ActualHeight,
                rasterizationScale = scene.XamlRoot.RasterizationScale,
                expectedClientWidth = expected.Width, expectedClientHeight = expected.Height,
                actualClientWidth = client.Right - client.Left, actualClientHeight = client.Bottom - client.Top,
                rootDesiredWidth = root.DesiredSize.Width, rootDesiredHeight = root.DesiredSize.Height,
                sceneClientBounds = sceneBounds.ToString(),
                sceneClientLeft = sceneBounds.Left * scale, sceneClientTop = sceneBounds.Top * scale,
                sceneClientRight = sceneBounds.Right * scale, sceneClientBottom = sceneBounds.Bottom * scale,
                originalX = position.X, originalY = position.Y, originalWidth = size.Width, originalHeight = size.Height,
                arrangedUtc = DateTimeOffset.UtcNow,
            }));
        }
        catch (Exception failure)
        {
            failures.Add(failure);
            try { File.WriteAllText(Path.Combine(directory, state + ".arrange-exception.txt"), failure.ToString()); }
            catch (Exception persistenceFailure) { failures.Add(persistenceFailure); }
        }
        finally
        {
            try { RecordStage("finally"); }
            catch (Exception diagnosticFailure) { failures.Add(diagnosticFailure); }
        }
        if (failures.Count > 0)
        {
            try { restore(); }
            catch (Exception restoreFailure)
            {
                failures.Add(restoreFailure);
                try { File.WriteAllText(Path.Combine(directory, state + ".restore-exception.txt"), restoreFailure.ToString()); }
                catch (Exception persistenceFailure) { failures.Add(persistenceFailure); }
            }
            throw new AggregateException("Owned capture arrangement or diagnostics failed.", failures);
        }
        return restore;
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
        Assert.True(GetClientRect(handle, out var clientBounds));
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        Assert.InRange(Math.Abs(content.ActualWidth * content.XamlRoot.RasterizationScale -
            (clientBounds.Right - clientBounds.Left)), 0, 1);
        Assert.InRange(Math.Abs(content.ActualHeight * content.XamlRoot.RasterizationScale -
            (clientBounds.Bottom - clientBounds.Top)), 0, 1);
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
            clientWidth = clientBounds.Right - clientBounds.Left,
            clientHeight = clientBounds.Bottom - clientBounds.Top,
            contentWidth = content.ActualWidth,
            contentHeight = content.ActualHeight,
            rasterizationScale = content.XamlRoot.RasterizationScale,
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
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder value,
        uint length, out uint needed);
}
