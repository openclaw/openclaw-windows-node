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
    /// designer mocks (which are exported at 2010×2472; aspect 0.813).
    /// 720 × 885 → aspect 0.813, identical to the design canvas, so the
    /// rendered PNG can be diffed pixel-for-pixel against the references.
    /// </summary>
    private const int PreviewWidthDip = 720;
    private const int PreviewHeightDip = 885;

    /// <summary>Height in DIPs of the custom XAML title bar (lobster + "OpenClaw Setup").</summary>
    private const int TitleBarHeight = 40;

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

        // In headless capture mode, suppress all V2 entrance/idle animations
        // so RenderTargetBitmap never snapshots an in-flight transform.
        OpenClawTray.Onboarding.V2.V2Animations.DisableForCapture = _captureMode;

        Title = "OpenClaw Setup";
        ExtendsContentIntoTitleBar = true;

        // Use a flat dark background that matches the designer mocks
        // (#202020) instead of MicaBackdrop. RenderTargetBitmap does not
        // see Mica composition (it lives below the XAML layer), so the
        // captures would otherwise show transparent/black behind the UI.
        // A solid color guarantees byte-identical captures across runs.
        SystemBackdrop = null;

        this.SetWindowSize(PreviewWidthDip, PreviewHeightDip);
        this.CenterOnScreen();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        // Make the system min/max/close buttons match our dark palette.
        if (AppWindow.TitleBar is { } systemTitleBar)
        {
            var dark = ColorHelper.FromArgb(255, 0x20, 0x20, 0x20);
            var hover = ColorHelper.FromArgb(255, 0x2C, 0x2C, 0x2C);
            var pressed = ColorHelper.FromArgb(255, 0x38, 0x38, 0x38);
            var fg = ColorHelper.FromArgb(255, 0xE0, 0xE0, 0xE0);
            systemTitleBar.ButtonBackgroundColor = dark;
            systemTitleBar.ButtonInactiveBackgroundColor = dark;
            systemTitleBar.ButtonForegroundColor = fg;
            systemTitleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, 0x70, 0x70, 0x70);
            systemTitleBar.ButtonHoverBackgroundColor = hover;
            systemTitleBar.ButtonHoverForegroundColor = fg;
            systemTitleBar.ButtonPressedBackgroundColor = pressed;
            systemTitleBar.ButtonPressedForegroundColor = fg;
        }

        _host = new FunctionalHostControl();
        _host.Mount(ctx =>
        {
            var (s, _) = ctx.UseState(_state);
            return Factories.Component<OnboardingV2App, OnboardingV2State>(s);
        });

        _rootGrid = new Grid
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 0x20, 0x20, 0x20))
        };
        _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleBarHeight) });
        _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Custom title bar: small lobster icon + "OpenClaw Setup"
        // text. Reserve the right-hand inset for the system caption
        // buttons. AppWindow.TitleBar.RightInset is in physical pixels;
        // convert to DIPs using XamlRoot.RasterizationScale (set after
        // the host has loaded). Fall back to a sensible default at 100%
        // DPI (~138 DIP) until the first SizeChanged.
        var titleBar = new Grid { Padding = new Thickness(14, 0, 138, 0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(titleBar, "OpenClaw Setup title bar");

        void UpdateTitleBarPadding()
        {
            try
            {
                var rightInsetPx = AppWindow?.TitleBar?.RightInset ?? 0;
                var scale = _host?.XamlRoot?.RasterizationScale ?? 1.0;
                if (scale <= 0) scale = 1.0;
                var rightInsetDip = rightInsetPx > 0 ? rightInsetPx / scale : 138;
                titleBar.Padding = new Thickness(14, 0, rightInsetDip, 0);
            }
            catch
            {
                // Non-fatal: leave the fallback padding.
            }
        }
        AppWindow.Changed += (_, _) => UpdateTitleBarPadding();
        var lobster = new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/Setup/Lobster.png")),
            Width = 14,
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Stretch = Stretch.Uniform
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(lobster, "OpenClaw");
        var titleText = new TextBlock
        {
            Text = Title,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 0xE0, 0xE0, 0xE0))
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(titleText, "OpenClaw Setup");
        var titleStack = new StackPanel { Orientation = Orientation.Horizontal };
        titleStack.Children.Add(lobster);
        titleStack.Children.Add(titleText);
        titleBar.Children.Add(titleStack);
        Grid.SetRow(titleBar, 0);
        _rootGrid.Children.Add(titleBar);
        SetTitleBar(titleBar);

        Grid.SetRow(_host, 1);
        _rootGrid.Children.Add(_host);
        Content = _rootGrid;

        _host.Loaded += (_, _) => UpdateTitleBarPadding();

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

        // OPENCLAW_PREVIEW_PROGRESS_FROZEN_STAGE freezes the LocalSetupProgress
        // page on a specific stage: every stage strictly before this one is
        // marked Done, the named stage is Running (spinner), and every stage
        // strictly after is Idle.
        //
        // OPENCLAW_PREVIEW_FAIL_STAGE additionally marks the named stage as
        // Failed (overrides the Running marking) and populates
        // LocalSetupErrorMessage so the inline error card renders.
        var frozen = Environment.GetEnvironmentVariable("OPENCLAW_PREVIEW_PROGRESS_FROZEN_STAGE");
        var failStage = Environment.GetEnvironmentVariable("OPENCLAW_PREVIEW_FAIL_STAGE");

        if (!string.IsNullOrWhiteSpace(frozen) &&
            Enum.TryParse<OnboardingV2State.LocalSetupStage>(frozen, ignoreCase: true, out var frozenStage))
        {
            var rows = new Dictionary<OnboardingV2State.LocalSetupStage, OnboardingV2State.LocalSetupRowState>();
            foreach (var stage in Enum.GetValues<OnboardingV2State.LocalSetupStage>())
            {
                if (stage < frozenStage)
                {
                    rows[stage] = OnboardingV2State.LocalSetupRowState.Done;
                }
                else if (stage == frozenStage)
                {
                    rows[stage] = OnboardingV2State.LocalSetupRowState.Running;
                }
                else
                {
                    rows[stage] = OnboardingV2State.LocalSetupRowState.Idle;
                }
            }
            state.LocalSetupRows = rows;
        }

        if (!string.IsNullOrWhiteSpace(failStage) &&
            Enum.TryParse<OnboardingV2State.LocalSetupStage>(failStage, ignoreCase: true, out var fStage))
        {
            var rows = new Dictionary<OnboardingV2State.LocalSetupStage, OnboardingV2State.LocalSetupRowState>(state.LocalSetupRows);
            // Mark every stage strictly before the failed one Done (in case
            // the frozen stage env var was unset or set to the same stage).
            foreach (var stage in Enum.GetValues<OnboardingV2State.LocalSetupStage>())
            {
                if (stage < fStage) rows[stage] = OnboardingV2State.LocalSetupRowState.Done;
                else if (stage == fStage) rows[stage] = OnboardingV2State.LocalSetupRowState.Failed;
                else rows[stage] = OnboardingV2State.LocalSetupRowState.Idle;
            }
            state.LocalSetupRows = rows;
            state.LocalSetupErrorMessage =
                "The OpenClaw gateway service started, but did not report ready status. Follow aka.ms/wsllogs for WSL diagnostic collection instructions.";
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

            // Clear keyboard focus so the system focus visual (cyan ring)
            // doesn't leak into deterministic captures. Re-enabling
            // UseSystemFocusVisuals on V2 buttons (a11y improvement) means
            // the first focusable in tab order would otherwise carry an
            // initial focus ring. Park focus on a hidden, zero-size sentinel
            // and let it settle for one more frame.
            var sentinel = new ContentControl
            {
                IsTabStop = true,
                Width = 0,
                Height = 0,
                Opacity = 0,
                IsHitTestVisible = false,
            };
            _rootGrid.Children.Add(sentinel);
            sentinel.Focus(FocusState.Programmatic);
            await Task.Delay(50);

            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(_rootGrid);
            var pixels = await rtb.GetPixelsAsync();
            var pixelBytes = pixels.ToArray();

            _rootGrid.Children.Remove(sentinel);

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
