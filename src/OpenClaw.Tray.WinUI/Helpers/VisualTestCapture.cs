using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenClawTray.Services;
using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace OpenClawTray.Helpers;

internal static class VisualTestCapture
{
    private static readonly ConcurrentDictionary<string, int> s_captureIndexes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> s_scheduledSignals = new(StringComparer.OrdinalIgnoreCase);

    public static void ScheduleSignalCapture(FrameworkElement root)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST") != "1"
            || Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_SIGNAL")
                is not { Length: > 0 } signalPath
            || Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_SURFACE")
                is not { Length: > 0 } surfaceName
            || !s_scheduledSignals.TryAdd(signalPath, 0))
        {
            return;
        }

        _ = CaptureWhenSignaledAsync(root, signalPath, surfaceName);
    }

    public static async Task CaptureAsync(FrameworkElement root, string surfaceName)
    {
        var rootDir = GetVisualTestDirectory();
        if (rootDir is null)
            return;

        await CaptureToDirectoryAsync(root, Path.Combine(rootDir, SanitizePathSegment(surfaceName)));
    }

    private static async Task CaptureWhenSignaledAsync(
        FrameworkElement root,
        string signalPath,
        string surfaceName)
    {
        for (var attempt = 0; attempt < 600; attempt++)
        {
            await Task.Delay(100);
            if (!File.Exists(signalPath))
                continue;

            await CaptureAsync(root, surfaceName);
            return;
        }

        Logger.Warn($"[VisualTest] Timed out waiting for capture signal for {surfaceName}.");
    }

    private static async Task CaptureToDirectoryAsync(FrameworkElement root, string surfaceDir)
    {
        try
        {
            Directory.CreateDirectory(surfaceDir);
            if (root.DispatcherQueue.HasThreadAccess)
            {
                await CaptureOnUiThreadAsync(root, surfaceDir);
                return;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var enqueued = root.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await CaptureOnUiThreadAsync(root, surfaceDir);
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            if (!enqueued)
                throw new InvalidOperationException("Dispatcher queue rejected capture work.");

            await tcs.Task;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[VisualTest] Capture failed for {surfaceDir}: {ex.Message}");
        }
    }

    private static async Task CaptureOnUiThreadAsync(FrameworkElement root, string surfaceDir)
    {
        Action restoreBackground = () => { };
        try
        {
            var fileName = $"capture-{s_captureIndexes.AddOrUpdate(surfaceDir, 0, (_, current) => current + 1):D2}.png";
            var filePath = Path.Combine(surfaceDir, fileName);
            restoreBackground = ApplyCaptureBackground(root);

            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(root);
            var pixels = await rtb.GetPixelsAsync();

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth,
                (uint)rtb.PixelHeight,
                96,
                96,
                pixels.ToArray());
            await encoder.FlushAsync();

            stream.Seek(0);
            var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
            await File.WriteAllBytesAsync(filePath, bytes);
        }
        finally
        {
            restoreBackground();
        }
    }

    private static Action ApplyCaptureBackground(FrameworkElement root)
    {
        var background = new SolidColorBrush(Microsoft.UI.Colors.White);

        switch (root)
        {
            case Panel panel when IsTransparent(panel.Background):
            {
                var original = panel.Background;
                panel.Background = background;
                return () => panel.Background = original;
            }
            case Border border when IsTransparent(border.Background):
            {
                var original = border.Background;
                border.Background = background;
                return () => border.Background = original;
            }
            case ScrollViewer scrollViewer when IsTransparent(scrollViewer.Background):
            {
                var original = scrollViewer.Background;
                scrollViewer.Background = background;
                return () => scrollViewer.Background = original;
            }
        }

        return () => { };
    }

    private static bool IsTransparent(Brush? brush) =>
        brush is null || brush is SolidColorBrush { Color.A: 0 };

    private static string? GetVisualTestDirectory()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST") != "1")
            return null;

        var path = Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR");
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
            return null;

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizePathSegment(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '-');
        return value;
    }
}
