using System;
using System.Reflection;
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
    // Match the Microsoft.WindowsAppSDK package's runtime major/minor. The
    // bootstrapper resolves a system-installed Microsoft.WindowsAppRuntime
    // framework MSIX (stable channel = empty version tag). On dev machines and on
    // CI the runtime is installed out-of-band — see
    // .github/workflows/ci.yml ("Install WindowsAppRuntime") and the README setup
    // notes. Self-contained deployment was tried but doesn't survive the xunit
    // testhost: the testhost.exe lives in the .NET SDK directory, so the SDK's
    // P/Invoke-based auto-initializer can't probe the test bin folder.
    private static readonly uint WinAppSdkMajorMinor = ResolveWinAppSdkMajorMinor();
    private const string WinAppSdkVersionTag = "";
    private const int DefaultStartupTimeoutSeconds = 90;

    private static int s_bootstrapInitialized; // 0 = no, 1 = yes

    private readonly Thread _uiThread;
    private readonly ManualResetEventSlim _ready = new(false);
    private Exception? _startupError;
    private volatile string _startupPhase = "not started";

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

        // Wait for Application.Start + Window setup to signal ready. CI can be
        // slow on first WinAppSDK activation, so keep the default generous but
        // overrideable when debugging.
        var timeoutSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("OPENCLAW_UI_FIXTURE_TIMEOUT_SECONDS"),
            out var configuredTimeoutSeconds) && configuredTimeoutSeconds > 0
            ? configuredTimeoutSeconds
            : DefaultStartupTimeoutSeconds;
        if (!_ready.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            var threadState = _uiThread.IsAlive ? _uiThread.ThreadState.ToString() : "stopped";
            throw new InvalidOperationException(
                $"UIThreadFixture failed to initialize within {timeoutSeconds}s; phase='{_startupPhase}', threadState='{threadState}'");
        }

        if (_startupError != null)
            throw new InvalidOperationException("UIThreadFixture initialization failed", _startupError);
    }

    private static uint ResolveWinAppSdkMajorMinor()
    {
        var assembly = typeof(UIThreadFixture).Assembly;
        foreach (var attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (!string.Equals(attribute.Key, "MicrosoftWindowsAppSDKVersion", StringComparison.Ordinal))
            {
                continue;
            }

            var stableVersion = attribute.Value?.Split(new[] { '-', '+' }, 2, StringSplitOptions.None)[0];
            if (Version.TryParse(stableVersion, out var version) && version.Major >= 0 && version.Minor >= 0)
            {
                return ((uint)version.Major << 16) | (ushort)version.Minor;
            }

            throw new InvalidOperationException(
                $"Assembly metadata MicrosoftWindowsAppSDKVersion '{attribute.Value}' is not a valid package version.");
        }

        throw new InvalidOperationException("Assembly metadata MicrosoftWindowsAppSDKVersion was not generated.");
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

    /// <summary>
    /// Await a single LOW-priority dispatcher callback. WinUI schedules layout/render and the
    /// realization callbacks they queue at higher priority, so by the time a Low-priority
    /// continuation runs, all currently-queued render/layout/realization work has already run.
    /// This is a deterministic drain primitive — a bounded alternative to sleeping a fixed
    /// interval — used by tests that need the render queue to settle. If the dispatcher is
    /// shutting down and rejects the enqueue, the task completes immediately so callers can
    /// never block indefinitely.
    /// </summary>
    public Task YieldToRenderAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => tcs.TrySetResult()))
        {
            tcs.TrySetResult();
        }
        return tcs.Task;
    }

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
                // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
                try { TestWindow.Title = $"A2UI render test — {label}"; } catch { }
            });
        }
        // slopwatch-ignore: SW004 Integration fixture polling delay is intentional and bounded while waiting for external process state.
        return Task.Delay(ms ?? SlowStepMs);
    }

    private void UIThreadProc()
    {
        try
        {
            _startupPhase = "initializing Windows App SDK bootstrap";

            // Initialize WinAppSDK runtime (unpackaged process).
            if (Interlocked.Exchange(ref s_bootstrapInitialized, 1) == 0)
            {
                Bootstrap.Initialize(WinAppSdkMajorMinor, WinAppSdkVersionTag);
            }

            // Application.Start blocks the calling thread until Application.Current.Exit().
            // The lambda runs once, on this same thread, with a live dispatcher.
            _startupPhase = "starting WinUI Application";
            Application.Start(p =>
            {
                try
                {
                    _startupPhase = "creating TestApp";
                    var app = new TestApp(); // ctor stashes itself as Application.Current
                    // Application.Resources can only be touched once the COM object
                    // is fully wired; safe by the time we reach here (post-ctor).
                    _startupPhase = "merging app resources";
                    app.MergeStandardResources();

                    _startupPhase = "getting dispatcher";
                    Dispatcher = DispatcherQueue.GetForCurrentThread();

                    _startupPhase = "creating hidden test window";
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
                        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
                        catch { /* best-effort sizing */ }
                    }

                    _startupPhase = "activating test window";
                    TestWindow.Activate();

                    if (!IsSlow)
                    {
                        // Default: keep the window live but off-screen so CI runs are silent.
                        _startupPhase = "moving test window off-screen";
                        MoveOffScreen(TestWindow);
                    }

                    _startupPhase = "ready";
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

    private static void MoveOffScreen(Window w)
    {
        try
        {
            // Move off-screen so CI runs are silent while keeping the window
            // live in the WinUI compositor. XamlRoot stays attached, the visual
            // tree lays out, but the user never sees it during tests.
            w.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-32000, -32000, 1, 1));
        }
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        catch
        {
            // best-effort; tests still work even if the window briefly flashes.
        }
    }

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
                    // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
                    try { TestWindow?.Close(); } catch { }
                    // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
                    try { Application.Current?.Exit(); } catch { }
                });
            }
            // Don't block the test process forever if the UI thread misbehaves.
            _uiThread.Join(TimeSpan.FromSeconds(5));
        }
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
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
