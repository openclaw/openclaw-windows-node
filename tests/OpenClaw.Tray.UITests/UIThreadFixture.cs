using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.DynamicDependency;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Bootstraps a single WinUI3 <see cref="Application"/> + hidden <see cref="Window"/>
/// on a dedicated STA thread for the lifetime of the test process. Tests marshal
/// onto that thread via <see cref="RunOnUIAsync"/>.
///
/// Why this shape:
///   - WinUI3 Application is a process singleton; only one Application.Start may
///     run per process. So the fixture is a collection fixture (single instance
///     across all UI tests).
///   - Application.Start blocks the calling thread (it pumps the message loop),
///     so we spin a dedicated thread and call Application.Start there.
///   - Renderers like <see cref="OpenClawTray.A2UI.Rendering.Renderers.TextRenderer"/>
///     call Application.Current.Resources["BodyTextBlockStyle"], so a real
///     Application instance must exist.
///   - A hidden Window provides a XamlRoot for elements that need one (theme
///     resource resolution, focus state, etc.). We host the surface under
///     <see cref="Container"/>, a Grid that is the Window's Content.
///
/// Tests must NOT touch WinUI types from xUnit's worker thread directly — always
/// marshal via <see cref="RunOnUIAsync{T}"/> or <see cref="RunOnUIAsync"/>.
/// </summary>
public sealed class UIThreadFixture : IDisposable
{
    // Match OpenClaw.Tray.WinUI.csproj's Microsoft.WindowsAppSDK package version
    // (1.8). The bootstrapper resolves a system-installed
    // Microsoft.WindowsAppRuntime.1.8 framework MSIX (stable channel = empty version
    // tag). On dev machines and on CI the runtime is installed out-of-band — see
    // .github/workflows/ci.yml ("Install WindowsAppRuntime") and the README setup
    // notes. Self-contained deployment was tried but doesn't survive the xunit
    // testhost: the testhost.exe lives in the .NET SDK directory, so the SDK's
    // P/Invoke-based auto-initializer can't probe the test bin folder.
    private const uint WinAppSdkMajorMinor = 0x00010008;
    private const string WinAppSdkVersionTag = "";

    private static int s_bootstrapInitialized; // 0 = no, 1 = yes

    private readonly Thread _uiThread;
    private readonly ManualResetEventSlim _ready = new(false);
    private Exception? _startupError;

    /// <summary>
    /// True when env var SLOW_UI_TESTS=1. Window stays visible, tests insert
    /// deliberate pauses via <see cref="PauseAsync"/> so a human can watch.
    /// </summary>
    public bool IsSlow { get; }

    /// <summary>Default pause length when running in slow mode.</summary>
    public int SlowStepMs { get; }

    /// <summary>Dispatcher attached to the UI thread. Use to enqueue work.</summary>
    public DispatcherQueue Dispatcher { get; private set; } = null!;

    /// <summary>The hidden top-level Window owned by the fixture.</summary>
    public Window TestWindow { get; private set; } = null!;

    /// <summary>
    /// Top-level Grid that tests can drop content into. Cleared between tests
    /// by the test code (call <see cref="ResetContainerAsync"/>).
    /// </summary>
    public Grid Container { get; private set; } = null!;

    public UIThreadFixture()
    {
        IsSlow = string.Equals(
            Environment.GetEnvironmentVariable("SLOW_UI_TESTS"), "1", StringComparison.Ordinal);
        SlowStepMs = int.TryParse(
            Environment.GetEnvironmentVariable("SLOW_UI_STEP_MS"), out var ms) && ms > 0 ? ms : 800;

        _uiThread = new Thread(UIThreadProc)
        {
            IsBackground = true,
            Name = "UIThreadFixture",
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();

        // Wait up to 30s for Application.Start + Window setup to signal ready.
        if (!_ready.Wait(TimeSpan.FromSeconds(30)))
            throw new InvalidOperationException("UIThreadFixture failed to initialize within 30s");
        if (_startupError != null)
            throw new InvalidOperationException("UIThreadFixture initialization failed", _startupError);
    }

    /// <summary>Run an async lambda on the UI thread, awaiting its completion.</summary>
    public Task<T> RunOnUIAsync<T>(Func<Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Dispatcher.TryEnqueue(async () =>
        {
            try { tcs.SetResult(await work().ConfigureAwait(true)); }
            catch (Exception ex) { tcs.SetException(ex); }
        }))
        {
            tcs.SetException(new InvalidOperationException("Dispatcher rejected enqueue (shutting down?)"));
        }
        return tcs.Task;
    }

    /// <summary>Run a sync lambda on the UI thread, awaiting its completion.</summary>
    public Task RunOnUIAsync(Action work) => RunOnUIAsync(() => { work(); return Task.FromResult(0); });

    /// <summary>Run an async void lambda on the UI thread, awaiting its completion.</summary>
    public Task RunOnUIAsync(Func<Task> work) => RunOnUIAsync(async () => { await work().ConfigureAwait(true); return 0; });

    /// <summary>Clear the container so each test starts from an empty surface.</summary>
    public Task ResetContainerAsync() => RunOnUIAsync(() => Container.Children.Clear());

    /// <summary>
    /// In slow mode, sleep for <see cref="SlowStepMs"/> (or the override) and
    /// optionally update the window title with a step label so the watcher can
    /// follow what's being demonstrated. No-op when slow mode is off.
    /// </summary>
    public Task PauseAsync(string? label = null, int? ms = null)
    {
        if (!IsSlow) return Task.CompletedTask;
        if (label != null && Dispatcher != null)
        {
            Dispatcher.TryEnqueue(() =>
            {
                try { TestWindow.Title = $"A2UI render test — {label}"; } catch { }
            });
        }
        return Task.Delay(ms ?? SlowStepMs);
    }

    private void UIThreadProc()
    {
        try
        {
            // Initialize WinAppSDK runtime (unpackaged process).
            if (Interlocked.Exchange(ref s_bootstrapInitialized, 1) == 0)
            {
                Bootstrap.Initialize(WinAppSdkMajorMinor, WinAppSdkVersionTag);
            }

            // Application.Start blocks the calling thread until Application.Current.Exit().
            // The lambda runs once, on this same thread, with a live dispatcher.
            Application.Start(p =>
            {
                try
                {
                    var app = new TestApp(); // ctor stashes itself as Application.Current
                    // Application.Resources can only be touched once the COM object
                    // is fully wired; safe by the time we reach here (post-ctor).
                    app.MergeStandardResources();

                    Dispatcher = DispatcherQueue.GetForCurrentThread();

                    Container = new Grid { Padding = new Microsoft.UI.Xaml.Thickness(24) };
                    TestWindow = new Window
                    {
                        Title = "OpenClaw.Tray.UITests",
                        Content = Container,
                    };

                    if (IsSlow)
                    {
                        // Resize to something a human can watch comfortably.
                        try
                        {
                            TestWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 640));
                        }
                        catch { /* best-effort sizing */ }
                    }

                    TestWindow.Activate();

                    if (!IsSlow)
                    {
                        // Default: hide the window so CI runs are silent.
                        TryHide(TestWindow);
                    }

                    _ready.Set();
                }
                catch (Exception ex)
                {
                    _startupError = ex;
                    _ready.Set();
                }
            });
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _ready.Set();
        }
    }

    private static void TryHide(Window w)
    {
        try
        {
            // WindowsAppSDK 1.5+ supports AppWindow.Hide via WinUIEx, but to keep
            // deps minimal we just move the window off-screen. It's still a real
            // Window — XamlRoot is attached, the visual tree lays out — but the
            // user never sees it during tests.
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(w);
            // SW_HIDE = 0
            ShowWindow(hwnd, 0);
        }
        catch
        {
            // best-effort; tests still work even if the window briefly flashes.
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public void Dispose()
    {
        try
        {
            // Ask the UI thread to exit. Application.Current.Exit() unwinds
            // Application.Start; the thread then completes naturally.
            if (Dispatcher != null)
            {
                Dispatcher.TryEnqueue(() =>
                {
                    try { TestWindow?.Close(); } catch { }
                    try { Application.Current?.Exit(); } catch { }
                });
            }
            // Don't block the test process forever if the UI thread misbehaves.
            _uiThread.Join(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Disposal is best-effort.
        }
    }
}

/// <summary>
/// All UI tests share one Application/Window — declare them in this collection
/// so xUnit serializes them and reuses the fixture.
/// </summary>
[CollectionDefinition(Name)]
public sealed class UICollection : ICollectionFixture<UIThreadFixture>
{
    public const string Name = "UI";
}
