using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
    private bool _persistStartupPreferenceOnComplete = true;
    private bool _showStartupPreferenceOnComplete = true;

    public static SetupWindow? Active { get; private set; }

    public event EventHandler? AdvancedSetupRequested;
    public event EventHandler<SetupCompletedEventArgs>? SetupCompleted;
    public bool IsClosed => _isClosed;
    public bool CanNavigateToWizard =>
        !_isClosed &&
        _setupLock is not null &&
        RootFrame.Content is not WizardPage;
    public bool CanNavigateToGatewayInstalledMilestone =>
        !_isClosed &&
        _setupLock is not null &&
        RootFrame.Content is not ProgressPage { IsPipelineRunning: true } &&
        RootFrame.Content is not WizardPage;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public SetupWindow(string? configPath = null, bool startAtGatewayInstalledMilestone = false)
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

        // Mica backdrop — the signature Windows 11 material (native).
        SystemBackdrop = new MicaBackdrop();

        // Load config: explicit --config arg, or bundled default-config.json (required)
        var args = Environment.GetCommandLineArgs();
        var explicitConfigPath = configPath ?? GetArg(args, "--config");
        configPath = explicitConfigPath;
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
        _config.UsesBundledDefaultConfig = explicitConfigPath == null;
        _config = SetupConfig.FromEnvironment(_config);
        GatewayLkgVersion.ApplyToConfig(_config);
        _config.ApplyUiDefaults(rollbackOnFailure: !HasFlag(args, "--no-rollback-on-failure"));
        if (startAtGatewayInstalledMilestone)
        {
            _persistStartupPreferenceOnComplete = false;
            _showStartupPreferenceOnComplete = false;
        }

        Closed += (_, _) =>
        {
            _isClosed = true;
            _initialContentReady.TrySetResult(true);
            _setupLock?.Dispose();
            _setupLock = null;
            if (ReferenceEquals(Active, this))
                Active = null;
        };

        var previewPage = SetupPreview.RequestedPage;
        if (previewPage != null)
        {
            NavigatePreview(previewPage);
            return;
        }

        if (!SetupRunLock.TryAcquire(SetupContext.ResolveDataDir(), out _setupLock, out var lockMessage))
        {
            NavigateTo(typeof(CompletePage), new CompletePageArgs(false, TimeSpan.Zero, null, lockMessage ?? "Another setup run is active."));
            return;
        }

        if (startAtGatewayInstalledMilestone)
            NavigateToGatewayInstalledMilestone();
        else
            NavigateTo(typeof(SecurityNoticePage), _config);
    }

    public void NavigateToWelcome(bool back = false) => NavigateTo(typeof(WelcomePage), _config, back);
    public void NavigateToAdvancedSetup() => NavigateTo(typeof(AdvancedSetupPage), _config);
    public void NavigateToCapabilities() => NavigateTo(typeof(CapabilitiesPage), _config);
    public void NavigateToProgress() => NavigateTo(typeof(ProgressPage), _config);
    public void NavigateToGatewayInstalledMilestone() =>
        NavigateTo(typeof(ProgressPage), new ProgressPageArgs(_config, ShowMilestoneOnly: true));

    public bool TryNavigateToGatewayInstalledMilestone()
    {
        if (!CanNavigateToGatewayInstalledMilestone)
            return false;

        _persistStartupPreferenceOnComplete = false;
        _showStartupPreferenceOnComplete = false;
        NavigateToGatewayInstalledMilestone();
        return true;
    }

    public bool TryNavigateToWizard(bool back = false)
    {
        if (!CanNavigateToWizard)
            return false;

        NavigateTo(typeof(WizardPage), _config, back);
        return true;
    }

    public void NavigateToComplete(bool success, TimeSpan elapsed, string? logPath, string? errorMessage = null)
        => NavigateTo(
            typeof(CompletePage),
            new CompletePageArgs(
                success,
                elapsed,
                logPath,
                errorMessage,
                DefaultAutoStart: true,
                ShowStartupPreference: _showStartupPreferenceOnComplete,
                ReviewSummary: SetupReviewSummaryBuilder.Build(_config)));

    // Directional page transition: forward steps slide in from the right, Back from the left.
    private void NavigateTo(Type page, object? parameter, bool back = false) =>
        RootFrame.Navigate(page, parameter, new SlideNavigationTransitionInfo
        {
            Effect = back ? SlideNavigationTransitionEffect.FromLeft : SlideNavigationTransitionEffect.FromRight,
        });

    private void NavigatePreview(string page) => RootFrame.Navigate(
        page switch
        {
            "welcome" => typeof(WelcomePage),
            "advanced" => typeof(AdvancedSetupPage),
            "capabilities" => typeof(CapabilitiesPage),
            "progress" => typeof(ProgressPage),
            "milestone" => typeof(ProgressPage),
            "wizard" => typeof(WizardPage),
            "wizard-error" => typeof(WizardPage),
            "complete" => typeof(CompletePage),
            "complete-error" => typeof(CompletePage),
            _ => typeof(SecurityNoticePage),
        },
        page switch
        {
            "complete" => new CompletePageArgs(true, TimeSpan.FromMinutes(3), null),
            "complete-error" => new CompletePageArgs(false, TimeSpan.FromMinutes(3), null, "Setup could not finish. Review the details, then retry setup when you are ready."),
            "milestone" => new ProgressPageArgs(_config, ShowMilestoneOnly: true),
            _ => _config,
        });

    public void RequestAdvancedSetup()
    {
        AdvancedSetupRequested?.Invoke(this, EventArgs.Empty);
    }

    public bool RequestSetupCompleted(bool enableAutoStart)
    {
        var handler = SetupCompleted;
        if (handler == null)
            return false;

        try
        {
            if (_persistStartupPreferenceOnComplete)
            {
                _config.Settings.AutoStart = enableAutoStart;
                TraySettingsConfig.UpdateAutoStartInSettingsFile(
                    Path.Combine(SetupContext.ResolveDataDir(), "settings.json"),
                    enableAutoStart);
            }
        }
        catch (Exception ex)
        {
            NavigateToComplete(false, TimeSpan.Zero, null, $"Setup completed, but saving your startup preference failed: {ex.Message}");
            return true;
        }

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

public sealed record CompletePageArgs(
    bool Success,
    TimeSpan Elapsed,
    string? LogPath,
    string? ErrorMessage = null,
    bool DefaultAutoStart = true,
    bool ShowStartupPreference = true,
    SetupReviewSummary? ReviewSummary = null);
public sealed record SetupCompletedEventArgs(bool EnableAutoStart);
