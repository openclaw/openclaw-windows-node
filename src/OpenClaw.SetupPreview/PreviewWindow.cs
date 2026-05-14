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

    // Theme-aware chrome elements (mutated by ApplyTheme when the user / system theme changes).
    private Grid? _titleBar;
    private TextBlock? _titleText;

    // System-theme tracking. Stored as fields (not locals) so the
    // ColorValuesChanged subscription can be unhooked on window close —
    // otherwise the COM event holds a strong reference to the lambda
    // (and via it the window), preventing GC.
    private Windows.UI.ViewManagement.UISettings? _themeUiSettings;
    private Windows.Foundation.TypedEventHandler<Windows.UI.ViewManagement.UISettings, object>? _themeColorValuesChangedHandler;

    public PreviewWindow()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _state = new OnboardingV2State();

        // Non-functional preview defaults: show the Node-Mode-Active warning
        // on the All Set page (the design's default state). Env vars below
        // can override this for capture scenarios that test the no-node variant.
        _state.NodeModeActive = true;

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

        // Force Windows 11 rounded corners on the window. Setting
        // SystemBackdrop = null above unhooks the default Mica path that
        // normally rounds the frame, leaving square corners. DWM's
        // WINDOW_CORNER_PREFERENCE attribute (Windows 11 build 22000+)
        // restores the rounded look without bringing back the Mica fill.
        TryApplyRoundedCorners();

        // Make the system min/max/close buttons follow the current theme below;
        // the actual button colours are applied in ApplyTheme().

        _host = new FunctionalHostControl();
        _host.Mount(ctx =>
        {
            var (s, _) = ctx.UseState(_state);
            return Factories.Component<OnboardingV2App, OnboardingV2State>(s);
        });

        _rootGrid = new Grid
        {
            Background = V2Theme.WindowBackground(_state.EffectiveTheme)
        };
        _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleBarHeight) });
        _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Custom title bar: small lobster icon + "OpenClaw Setup"
        // text. Reserve the right-hand inset for the system caption
        // buttons. AppWindow.TitleBar.RightInset is in physical pixels;
        // convert to DIPs using XamlRoot.RasterizationScale (set after
        // the host has loaded). Fall back to a sensible default at 100%
        // DPI (~138 DIP) until the first SizeChanged.
        _titleBar = new Grid { Padding = new Thickness(14, 0, 138, 0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_titleBar, "OpenClaw Setup title bar");

        var titleBar = _titleBar;
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
        _titleText = new TextBlock
        {
            Text = Title,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_titleText, "OpenClaw Setup");
        var titleStack = new StackPanel { Orientation = Orientation.Horizontal };
        titleStack.Children.Add(lobster);
        titleStack.Children.Add(_titleText);
        _titleBar.Children.Add(titleStack);
        Grid.SetRow(_titleBar, 0);
        _rootGrid.Children.Add(_titleBar);
        SetTitleBar(_titleBar);

        Grid.SetRow(_host, 1);
        _rootGrid.Children.Add(_host);
        Content = _rootGrid;

        _host.Loaded += (_, _) => UpdateTitleBarPadding();

        // Wire theme resolution and re-application. The state's ThemeMode
        // (System / Light / Dark) is the user's preference; EffectiveTheme
        // is what the V2 pages actually render against. ApplyResolvedTheme
        // reads the preference, picks Light or Dark (System => follow the
        // host Application.RequestedTheme), and pushes the result back
        // onto the state + the chrome.
        ApplyResolvedTheme();
        if (Application.Current is { } app)
        {
            // No app-level RequestedTheme change event is reliably surfaced
            // in unpackaged WinUI 3 apps, but UISettings raises ColorValuesChanged
            // when Windows app-mode flips. Forward it.
            //
            // Both the UISettings instance and the handler delegate are
            // stored as fields so we can unhook in Closed — without this,
            // the COM event keeps a strong reference to the lambda (and
            // via it, this window), preventing GC of the window when it
            // closes.
            try
            {
                _themeUiSettings = new Windows.UI.ViewManagement.UISettings();
                _themeColorValuesChangedHandler = (_, _) =>
                    _dispatcherQueue.TryEnqueue(() => ApplyResolvedTheme());
                _themeUiSettings.ColorValuesChanged += _themeColorValuesChangedHandler;
            }
            catch { /* non-fatal */ }
        }

        Closed += (_, _) =>
        {
            if (_themeUiSettings is not null && _themeColorValuesChangedHandler is not null)
            {
                try { _themeUiSettings.ColorValuesChanged -= _themeColorValuesChangedHandler; }
                catch { /* ignore */ }
            }
            _themeColorValuesChangedHandler = null;
            _themeUiSettings = null;
        };

        // F2 cycles theme mode (System -> Light -> Dark -> System) for live design feedback.
        // Only honoured in interactive mode; capture mode never sees keyboard input.
        if (!_captureMode)
        {
            _host.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.F2)
                {
                    _state.ThemeMode = (V2ThemeMode)(((int)_state.ThemeMode + 1) % 3);
                    ApplyResolvedTheme();
                    e.Handled = true;
                }
            };
        }

        if (_captureMode)
        {
            _host.Loaded += async (_, _) =>
            {
                await CaptureAndExitAsync();
            };
        }
        else
        {
            // Interactive preview: drive a fake stage progression so designers
            // can walk through the LocalSetupProgress checklist without a real
            // gateway install. Skipped if a frozen / failed stage env override
            // is set (those are deterministic capture scenarios).
            var hasFrozen = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCLAW_PREVIEW_PROGRESS_FROZEN_STAGE"));
            var hasFail = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCLAW_PREVIEW_FAIL_STAGE"));
            if (!hasFrozen && !hasFail)
            {
                _host.Loaded += (_, _) => StartFakeStageProgression();
            }
        }
    }

    /// <summary>
    /// Resolves <see cref="OnboardingV2State.ThemeMode"/> to a concrete
    /// <see cref="ElementTheme"/> and pushes it onto state + chrome.
    /// Called on startup, on F2 cycle, and on Windows app-mode change.
    /// </summary>
    private void ApplyResolvedTheme()
    {
        var resolved = ResolveEffectiveTheme(_state.ThemeMode);
        _state.EffectiveTheme = resolved;

        // Window background — V2 pages re-render through StateChanged, but the
        // root Grid background is owned here so update it directly too.
        _rootGrid.Background = V2Theme.WindowBackground(resolved);

        // Push the same theme into the visual tree so WinUI's built-in controls
        // (ToggleSwitch thumb, ProgressRing, focus visuals) pick up matching defaults.
        _rootGrid.RequestedTheme = resolved;

        if (_titleText is not null)
        {
            _titleText.Foreground = V2Theme.TextPrimary(resolved);
        }

        if (AppWindow?.TitleBar is { } systemTitleBar)
        {
            // The system caption buttons (min/max/close) live above our XAML
            // and need their colours set explicitly. Match the window bg so
            // they blend into the chrome rather than showing a dark bar above
            // a light window (or vice versa).
            var bg = ((SolidColorBrush)V2Theme.WindowBackground(resolved)).Color;
            var hover = ((SolidColorBrush)V2Theme.CardBackground(resolved)).Color;
            var pressed = ((SolidColorBrush)V2Theme.CardBackgroundPressed(resolved)).Color;
            var fg = ((SolidColorBrush)V2Theme.TextPrimary(resolved)).Color;
            var inactiveFg = ((SolidColorBrush)V2Theme.TextSubtle(resolved)).Color;
            systemTitleBar.ButtonBackgroundColor = bg;
            systemTitleBar.ButtonInactiveBackgroundColor = bg;
            systemTitleBar.ButtonForegroundColor = fg;
            systemTitleBar.ButtonInactiveForegroundColor = inactiveFg;
            systemTitleBar.ButtonHoverBackgroundColor = hover;
            systemTitleBar.ButtonHoverForegroundColor = fg;
            systemTitleBar.ButtonPressedBackgroundColor = pressed;
            systemTitleBar.ButtonPressedForegroundColor = fg;
        }
    }

    /// <summary>
    /// Resolves a user theme preference to a concrete <see cref="ElementTheme"/>.
    /// <see cref="V2ThemeMode.System"/> uses <see cref="V2SystemTheme.IsDark"/>
    /// (UISettings-based) since <see cref="Application.RequestedTheme"/>
    /// returns Light on unpackaged WinUI 3 apps regardless of system setting.
    /// </summary>
    private static ElementTheme ResolveEffectiveTheme(V2ThemeMode mode) => V2SystemTheme.Resolve(mode);

    /// <summary>
    /// Apply Windows 11 rounded-corner preference via DWM. No-op (and silent)
    /// on Windows 10 — the attribute simply isn't recognised.
    /// </summary>
    private void TryApplyRoundedCorners()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            int pref = DWMWCP_ROUND;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch
        {
            // Non-fatal: square corners are an acceptable fallback.
        }
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    private DispatcherQueueTimer? _fakeStageTimer;

    /// <summary>
    /// Walk the LocalSetupProgress checklist by promoting one row at a time:
    /// the current spinner row turns into a checkmark, then the next idle
    /// row spins. Loops at the end so a designer can keep watching without
    /// restarting the app. ~900 ms per transition feels close to a real WSL
    /// install but fast enough for design feedback.
    /// </summary>
    private void StartFakeStageProgression()
    {
        if (_fakeStageTimer is not null) return;

        // Seed: first stage spinning, the rest idle.
        var stages = Enum.GetValues<OnboardingV2State.LocalSetupStage>();
        var seed = new Dictionary<OnboardingV2State.LocalSetupStage, OnboardingV2State.LocalSetupRowState>();
        for (int i = 0; i < stages.Length; i++)
        {
            seed[stages[i]] = i == 0
                ? OnboardingV2State.LocalSetupRowState.Running
                : OnboardingV2State.LocalSetupRowState.Idle;
        }
        _state.LocalSetupRows = seed;

        _fakeStageTimer = _dispatcherQueue.CreateTimer();
        _fakeStageTimer.Interval = TimeSpan.FromMilliseconds(900);
        _fakeStageTimer.IsRepeating = true;
        _fakeStageTimer.Tick += (_, _) => AdvanceFakeStage();
        _fakeStageTimer.Start();
    }

    private void AdvanceFakeStage()
    {
        var stages = Enum.GetValues<OnboardingV2State.LocalSetupStage>();
        var rows = new Dictionary<OnboardingV2State.LocalSetupStage, OnboardingV2State.LocalSetupRowState>(_state.LocalSetupRows);

        // Find the currently-running row (if any) and promote it to Done; then
        // start the next idle row spinning. If there's no idle row left, loop
        // by resetting back to "stage 0 spinning, rest idle" after a brief pause.
        int runningIndex = -1;
        for (int i = 0; i < stages.Length; i++)
        {
            if (rows[stages[i]] == OnboardingV2State.LocalSetupRowState.Running)
            {
                runningIndex = i;
                break;
            }
        }

        if (runningIndex == -1)
        {
            // Loop back to the start so the demo keeps going.
            for (int i = 0; i < stages.Length; i++)
            {
                rows[stages[i]] = i == 0
                    ? OnboardingV2State.LocalSetupRowState.Running
                    : OnboardingV2State.LocalSetupRowState.Idle;
            }
            _state.LocalSetupRows = rows;
            return;
        }

        rows[stages[runningIndex]] = OnboardingV2State.LocalSetupRowState.Done;
        var nextIndex = runningIndex + 1;
        if (nextIndex < stages.Length)
        {
            rows[stages[nextIndex]] = OnboardingV2State.LocalSetupRowState.Running;
        }
        _state.LocalSetupRows = rows;
    }

    private static void ApplyEnvOverrides(OnboardingV2State state)
    {
        var page = Environment.GetEnvironmentVariable("OPENCLAW_PREVIEW_PAGE");
        if (!string.IsNullOrWhiteSpace(page) &&
            Enum.TryParse<V2Route>(page, ignoreCase: true, out var route))
        {
            state.CurrentRoute = route;
        }

        var theme = Environment.GetEnvironmentVariable("OPENCLAW_PREVIEW_THEME");
        if (!string.IsNullOrWhiteSpace(theme) &&
            Enum.TryParse<V2ThemeMode>(theme, ignoreCase: true, out var mode))
        {
            state.ThemeMode = mode;
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
