using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Hosting;
using OpenClawTray.Onboarding.V2;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinUIEx;

namespace OpenClaw.SetupPreview;

/// <summary>
/// Standalone preview window for the V2 onboarding redesign.
///
/// Two modes of operation, selected by env vars:
///
///  * Interactive (default): a normal window, intended for live design
///    iteration. Future work in the fake-services todo wires up the F1
///    debug overlay (start page, locale, scenarios, replay).
///
///  * Headless capture: when OPENCLAW_PREVIEW_CAPTURE=1, the window
///    appears at the requested size, mounts the V2 tree against the
///    requested page (OPENCLAW_PREVIEW_PAGE), waits for first composition
///    plus a quiescent frame, captures the root grid via
///    RenderTargetBitmap, writes the PNG to OPENCLAW_PREVIEW_CAPTURE_PATH,
///    and exits with code 0. On failure the exit code is 1 and a JSON
///    error file is written next to the requested PNG path. This is the
///    same RenderTargetBitmap mechanism the existing OnboardingWindow uses
///    for OPENCLAW_VISUAL_TEST=1, factored to fit a one-shot exe.
///
/// The window is intentionally fixed-size so that the captured PNG always
/// has the same pixel dimensions for a given DPI — the visual-diff tool
/// relies on this stability.
/// </summary>
internal sealed class PreviewWindow : WindowEx
{
    /// <summary>
    /// Logical preview window size in DIPs. Picked to closely match the
    /// designer mocks (which are exported at 1568×2106; aspect 0.745).
    /// 720 × 966 → aspect 0.745, identical to the design canvas.
    /// </summary>
    private const int PreviewWidthDip = 720;
    private const int PreviewHeightDip = 966;

    private readonly Grid _rootGrid;
    private readonly FunctionalHostControl _host;
    private readonly OnboardingV2State _state;
    private readonly DispatcherQueue _dispatcherQueue;

    // Capture-mode configuration.
    private readonly bool _captureMode;
    private readonly string? _capturePath;
    private bool _captureCompleted;

    public PreviewWindow()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _state = new OnboardingV2State();

        ApplyEnvOverrides(_state);
        _captureMode = Environment.GetEnvironmentVariable("OPENCLAW_PREVIEW_CAPTURE") == "1";
        _capturePath = Environment.GetEnvironmentVariable("OPENCLAW_PREVIEW_CAPTURE_PATH");

        Title = "OpenClaw Setup (preview)";
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new MicaBackdrop();
        this.SetWindowSize(PreviewWidthDip, PreviewHeightDip);
        this.CenterOnScreen();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        _host = new FunctionalHostControl();
        _host.Mount(ctx =>
        {
            var (s, _) = ctx.UseState(_state);
            return Factories.Component<OnboardingV2App, OnboardingV2State>(s);
        });

        _rootGrid = new Grid();
        _rootGrid.Children.Add(_host);
        Content = _rootGrid;

        if (_captureMode)
        {
            _host.Loaded += async (_, _) =>
            {
                await CaptureAndExitAsync();
            };
        }
    }

    private static void ApplyEnvOverrides(OnboardingV2State state)
    {
        var page = Environment.GetEnvironmentVariable("OPENCLAW_PREVIEW_PAGE");
        if (!string.IsNullOrWhiteSpace(page) &&
            Enum.TryParse<V2Route>(page, ignoreCase: true, out var route))
        {
            state.CurrentRoute = route;
        }

        var nodeMode = Environment.GetEnvironmentVariable("OPENCLAW_PREVIEW_NODE_MODE");
        if (!string.IsNullOrWhiteSpace(nodeMode))
        {
            state.NodeModeActive =
                nodeMode.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                nodeMode.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task CaptureAndExitAsync()
    {
        if (_captureCompleted) return;
        _captureCompleted = true;

        try
        {
            // Two layout passes + a short delay so any first-render UseEffect
            // mutations have time to land before we snapshot.
            await Task.Yield();
            await Task.Delay(250);

            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(_rootGrid);
            var pixels = await rtb.GetPixelsAsync();
            var pixelBytes = pixels.ToArray();

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth,
                (uint)rtb.PixelHeight,
                96, 96,
                pixelBytes);
            await encoder.FlushAsync();

            stream.Seek(0);
            var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);

            var path = !string.IsNullOrWhiteSpace(_capturePath)
                ? _capturePath
                : Path.Combine(Path.GetTempPath(), "openclaw-preview-capture.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);

            Console.Out.WriteLine($"[preview] captured {rtb.PixelWidth}x{rtb.PixelHeight} -> {path}");
            ExitWithCode(0);
        }
        catch (Exception ex)
        {
            try
            {
                var errPath = (_capturePath ?? Path.Combine(Path.GetTempPath(), "openclaw-preview-capture.png")) + ".error.json";
                Directory.CreateDirectory(Path.GetDirectoryName(errPath)!);
                var json = $"{{\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)},\"type\":{System.Text.Json.JsonSerializer.Serialize(ex.GetType().FullName ?? "")}}}";
                File.WriteAllText(errPath, json);
            }
            catch { /* best effort */ }
            Console.Error.WriteLine($"[preview] capture failed: {ex}");
            ExitWithCode(1);
        }
    }

    private void ExitWithCode(int code)
    {
        // WinUI doesn't expose a clean Application.Exit(int) — the Win32
        // ExitProcess avoids racing with the dispatcher loop teardown that
        // a managed Application.Exit() can leave hanging.
        try { Close(); } catch { /* ignore */ }
        ExitProcess((uint)code);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ExitProcess(uint uExitCode);
}
