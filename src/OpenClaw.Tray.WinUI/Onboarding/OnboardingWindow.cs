using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Infrastructure;
using OpenClawTray.Infrastructure.Hosting;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using WinUIEx;

namespace OpenClawTray.Onboarding;

/// <summary>
/// Host window for the Reactor-based onboarding wizard.
/// Supports visual test capture via OPENCLAW_VISUAL_TEST env var.
/// </summary>
public sealed class OnboardingWindow : WindowEx
{
    public event EventHandler? OnboardingCompleted;
    public bool Completed { get; private set; }

    private readonly SettingsManager _settings;
    private readonly ReactorHostControl _host;
    private readonly string? _visualTestDir;
    private readonly DispatcherQueue _dispatcherQueue;
    private int _captureIndex;

    public OnboardingWindow(SettingsManager settings)
    {
        _settings = settings;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _visualTestDir = Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST") == "1"
            ? Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR")
              ?? Path.Combine(Path.GetTempPath(), "openclaw-visual-test")
            : null;

        Title = LocalizationHelper.GetString("Onboarding_Title");
        this.SetWindowSize(720, 752);
        this.CenterOnScreen();
        this.SetIcon("Assets\\openclaw.ico");
        SystemBackdrop = new MicaBackdrop();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        var state = new OnboardingState(settings);
        state.Finished += OnOnboardingFinished;

        _host = new ReactorHostControl();
        _host.Mount(ctx =>
        {
            var (s, _) = ctx.UseState(state);
            return Factories.Component<OnboardingApp, OnboardingState>(s);
        });
        Content = _host;

        // Auto-capture in visual test mode
        if (_visualTestDir != null)
        {
            Directory.CreateDirectory(_visualTestDir);

            // Capture on initial load
            _host.Loaded += (_, _) =>
            {
                DispatcherQueue.GetForCurrentThread().TryEnqueue(
                    DispatcherQueuePriority.Low,
                    () => _ = CaptureCurrentPageAsync());
            };

            // Capture on every page navigation
            state.PageChanged += (_, _) =>
            {
                Task.Delay(500).ContinueWith(_ =>
                    _dispatcherQueue.TryEnqueue(() => _ = CaptureCurrentPageAsync()),
                    TaskScheduler.Default);
            };
        }
    }

    /// <summary>
    /// Captures the current window content to a PNG file.
    /// Called automatically on page navigation when OPENCLAW_VISUAL_TEST=1.
    /// Can also be triggered externally via a file signal.
    /// </summary>
    public async Task CaptureCurrentPageAsync()
    {
        if (_visualTestDir == null) return;
        try
        {
            // Small delay to ensure render is complete
            await Task.Delay(300);

            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(_host);
            var pixels = await rtb.GetPixelsAsync();
            var pixelBytes = pixels.ToArray();

            var fileName = $"page-{_captureIndex:D2}.png";
            var filePath = Path.Combine(_visualTestDir, fileName);

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth, (uint)rtb.PixelHeight,
                96, 96, pixelBytes);
            await encoder.FlushAsync();

            // Write stream to file
            stream.Seek(0);
            var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
            await File.WriteAllBytesAsync(filePath, bytes);

            Logger.Info($"[VisualTest] Captured {fileName} ({rtb.PixelWidth}x{rtb.PixelHeight})");
            _captureIndex++;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[VisualTest] Capture failed: {ex.Message}");
        }
    }

    private void OnOnboardingFinished(object? sender, EventArgs e)
    {
        _settings.Save();
        Completed = true;
        OnboardingCompleted?.Invoke(this, EventArgs.Empty);
        Close();
    }
}
