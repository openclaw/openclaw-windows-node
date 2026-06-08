using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using OpenClaw.SetupEngine.UI.Pages;
using System.Runtime.InteropServices;

namespace OpenClaw.SetupEngine.UI;

public sealed partial class SetupWindow : Window
{
    private SetupConfig _config = null!;
    private SetupRunLock? _setupLock;
    private readonly TaskCompletionSource<bool> _initialContentReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isClosed;

    public static SetupWindow? Active { get; private set; }

    public event EventHandler? AdvancedSetupRequested;
    public event EventHandler<SetupCompletedEventArgs>? SetupCompleted;
    public bool IsClosed => _isClosed;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public SetupWindow(string? configPath = null)
    {
        InitializeComponent();
        Active = this;

        // Size window accounting for DPI
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(720 * scale), (int)(820 * scale)));

        // Extend into title bar for modern look
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDrag);

        // Mica backdrop
        SystemBackdrop = new MicaBackdrop();

        // Load config: explicit --config arg, or bundled default-config.json (required)
        var args = Environment.GetCommandLineArgs();
        configPath ??= GetArg(args, "--config");
        if (configPath == null)
        {
            var defaultPath = Path.Combine(AppContext.BaseDirectory, "default-config.json");
            if (File.Exists(defaultPath))
                configPath = defaultPath;
            else
            {
                var libraryDefaultPath = Path.Combine(AppContext.BaseDirectory, "OpenClaw.SetupEngine.UI", "default-config.json");
                if (File.Exists(libraryDefaultPath))
                    configPath = libraryDefaultPath;
            }
        }

        if (configPath == null || !File.Exists(configPath))
        {
            throw new FileNotFoundException(
                "No config file found. Place default-config.json next to the executable or pass --config <path>.",
                configPath ?? Path.Combine(AppContext.BaseDirectory, "default-config.json"));
        }

        _config = SetupConfig.LoadFromFile(configPath);
        _config = SetupConfig.FromEnvironment(_config);
        GatewayLkgVersion.ApplyToConfig(_config);
        _config.ApplyUiDefaults(rollbackOnFailure: !HasFlag(args, "--no-rollback-on-failure"));

        Closed += (_, _) =>
        {
            _isClosed = true;
            _initialContentReady.TrySetResult(true);
            _setupLock?.Dispose();
            _setupLock = null;
            if (ReferenceEquals(Active, this))
                Active = null;
        };

        if (!SetupRunLock.TryAcquire(SetupContext.ResolveDataDir(), out _setupLock, out var lockMessage))
        {
            RootFrame.Navigate(typeof(CompletePage), new CompletePageArgs(false, TimeSpan.Zero, null, lockMessage ?? "Another setup run is active."));
            return;
        }

        RootFrame.Navigate(typeof(WelcomePage), _config);
    }

    public void NavigateToCapabilities() => RootFrame.Navigate(typeof(CapabilitiesPage), _config);
    public void NavigateToProgress() => RootFrame.Navigate(typeof(ProgressPage), _config);
    public void NavigateToWizard() => RootFrame.Navigate(typeof(WizardPage), _config);
    public void NavigateToPermissions() => RootFrame.Navigate(typeof(PermissionsPage), _config);
    public void NavigateToComplete(bool success, TimeSpan elapsed, string? logPath, string? errorMessage = null)
        => RootFrame.Navigate(typeof(CompletePage), new CompletePageArgs(success, elapsed, logPath, errorMessage));

    public void RequestAdvancedSetup()
    {
        AdvancedSetupRequested?.Invoke(this, EventArgs.Empty);
    }

    public bool RequestSetupCompleted(bool enableAutoStart)
    {
        var handler = SetupCompleted;
        if (handler == null)
            return false;

        handler.Invoke(this, new SetupCompletedEventArgs(enableAutoStart));
        return true;
    }

    public async Task WaitForInitialContentReadyAsync()
    {
        var completed = await Task.WhenAny(_initialContentReady.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        if (completed == _initialContentReady.Task)
            await _initialContentReady.Task;
        else
            _initialContentReady.TrySetResult(true);
    }

    public void BringToFrontForSetupLaunch()
    {
        Activate();

        if (AppWindow.Presenter is not OverlappedPresenter presenter)
            return;

        if (presenter.State == OverlappedPresenterState.Minimized)
            presenter.Restore();

        var wasAlwaysOnTop = presenter.IsAlwaysOnTop;
        presenter.IsAlwaysOnTop = true;
        Activate();

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(750);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!wasAlwaysOnTop && AppWindow.Presenter is OverlappedPresenter p)
                p.IsAlwaysOnTop = false;
        };
        timer.Start();
    }

    private void RootFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (e.Content is FrameworkElement element)
        {
            if (element.IsLoaded)
            {
                CompleteInitialContentReady();
                return;
            }

            RoutedEventHandler? loaded = null;
            loaded = (_, _) =>
            {
                element.Loaded -= loaded;
                CompleteInitialContentReady();
            };
            element.Loaded += loaded;
            return;
        }

        CompleteInitialContentReady();
    }

    private void RootFrame_NavigationFailed(object sender, Microsoft.UI.Xaml.Navigation.NavigationFailedEventArgs e)
    {
        _initialContentReady.TrySetResult(true);
    }

    private void CompleteInitialContentReady()
    {
        RootFrame.Navigated -= RootFrame_Navigated;
        DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () => _initialContentReady.TrySetResult(true));
    }

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static bool HasFlag(string[] args, string name)
        => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
}

public sealed record CompletePageArgs(bool Success, TimeSpan Elapsed, string? LogPath, string? ErrorMessage = null);
public sealed record SetupCompletedEventArgs(bool EnableAutoStart);
