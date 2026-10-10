using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference;
using OpenClaw.SetupEngine.UI.Pages;
using OpenClaw.SetupEngine.UI.Controls;
using System.Runtime.InteropServices;

namespace OpenClaw.SetupEngine.UI;

public sealed partial class SetupWindow : Window
{
    private SetupConfig _config = null!;
    internal GatewaySetupChoice? WelcomeGatewayChoice { get; set; }
    internal NativeGatewaySetupDraft? NativeSetupDraft { get; set; }
    internal NativeGatewaySetupSession? NativeSetupSession { get; private set; }
    public SetupAccessDraft AccessDraft { get; private set; } = null!;
    private bool _isWelcomeInstallSelected = true;
    private SetupRunLock? _setupLock;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Task<StepResult>? _contextApplyTask;
    private Task? _completionTask;
    private bool _completionDispatched;
    private string? _expectedConfiguredModelRef;
    private readonly TaskCompletionSource<bool> _initialContentReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _cleanupCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isClosed;
    private XamlRoot? _minimumSizeRoot;
    private bool _persistStartupPreferenceOnComplete = true;
    private bool _showStartupPreferenceOnComplete = true;
    private readonly bool _startupRegistrationAllowed;
    private bool _autoStartAfterSetup = true;
    private readonly string _dataDir;
    private readonly string _localDataDir;
    private readonly LocalAiHardwareProbeCache _localAiHardwareProbe = new(() => new CudaHostHardwareProbe().Probe());
    private readonly ISetupNativeConnectionHost? _nativeConnectionHost;
    private readonly ISetupLocalAiHost? _localAiHost;
    private readonly GatewayConnectionManager? _connectionManager;
    private LocalAiOnboardingSnapshot? _localAiReviewSelection;
    private LocalAiInstallAndUseIntent? _localAiInstallAndUse;
    private Task _aiPageCleanupTask = Task.CompletedTask;
    private SetupCompletionPreparation? _completionPreparation;
    private Task _preparationTask = Task.CompletedTask;
    private readonly SetupLoadingProgress _loadingProgress = new();
    private SetupLoadingProgress.Scope? _finishingLoading;
    internal SetupLoadingProgress LoadingProgress => _loadingProgress;
    internal SetupLoadingProgress.Scope BeginLoading(SetupLoadingGroup group, SetupLoadingStep step) =>
        _loadingProgress.Begin(group, step);
    internal void SetLoadingCancellation(SetupLoadingProgress.Scope scope, Action cancel) =>
        LoadingOverlay.SetCancellation(scope, cancel);
    private readonly Func<SetupNativeCompletion, CancellationToken, Task>? _publishNativeCompletion;
    private readonly Func<SetupNativePreparation, CancellationToken, Task>? _publishNativePreparation;
    private readonly Func<bool, CancellationToken, Task>? _applyNativeStartup;
    private readonly Action<TraySettingsConfig, bool?, bool>? _persistChoices;
    private bool _nativeContextFinalized;
    private bool _nativeSettingsSaved;
    private bool _nativeStartupApplied;
    private Task? _localAiTransitionTask;
    private Task _nativePageCleanupTask = Task.CompletedTask;
    private int _nativeNavigationGeneration;
    private readonly WslViabilityProbe _wslViabilityProbe = new(InspectWslViabilityAsync);
    private bool _startAtLocalAiRecoveryReview;
    private bool _pinLocalAiRecoveryModel;
    private LocalAiRecoveryConfigurationBaseline _localAiRecoveryBaseline = null!;

    public static SetupWindow? Active { get; private set; }

    public event EventHandler? AdvancedSetupRequested;
    internal bool HasClassicSettingsHost => AdvancedSetupRequested is not null;
    internal bool HasNativeConnectionHost => _nativeConnectionHost is not null;
    public event EventHandler<SetupCompletedEventArgs>? SetupCompleted;
    public bool IsClosed => _isClosed;
    public bool IsLocalAiRecovery => _startAtLocalAiRecoveryReview;
    internal bool IsAiLocalReview => _localAiReviewSelection is not null;
    public Task CleanupCompleted => _cleanupCompleted.Task;
    public bool ShowStartupPreference => _startupRegistrationAllowed && _showStartupPreferenceOnComplete;
    public bool AutoStartAfterSetup
    {
        get => _startupRegistrationAllowed && _autoStartAfterSetup;
        set => _autoStartAfterSetup = value;
    }
    internal string DataDir => _dataDir;
    internal string LocalDataDir => _localDataDir;
    public bool CanNavigateToWizard =>
        !_isClosed &&
        _completionPreparation is null &&
        _setupLock is not null &&
        RootFrame.Content is not NativeGatewaySetupPage { IsBusy: true } &&
        RootFrame.Content is not WizardPage and not AiSetupPage and not AiReadyPage &&
        RootFrame.Content is not ProgressPage { IsPipelineRunning: true };
    public bool CanNavigateToGatewayInstalledMilestone =>
        !_isClosed &&
        _completionPreparation is null &&
        _setupLock is not null &&
        RootFrame.Content is not ProgressPage { IsPipelineRunning: true } &&
        RootFrame.Content is not NativeGatewaySetupPage { IsBusy: true } &&
        RootFrame.Content is not WizardPage and not AiSetupPage and not AiReadyPage;
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void ApplyWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "openclaw.ico");
        if (!File.Exists(iconPath))
        {
            System.Diagnostics.Debug.WriteLine($"Setup window icon was not found at '{iconPath}'.");
            return;
        }

        try
        {
            AppWindow.SetIcon(iconPath);
        }
        catch (COMException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Setup window icon could not be applied: {ex}");
        }
    }

    public SetupWindow(
        string? configPath = null,
        bool startAtGatewayInstalledMilestone = false,
        bool startAtLocalAiRecoveryReview = false,
        string? dataDir = null,
        string? localDataDir = null,
        string? distroNameOverride = null,
        int? gatewayPortOverride = null,
        string? localAiRecoveryGatewayId = null,
        string? localAiRecoveryDistroName = null,
        int? localAiRecoveryGatewayPort = null,
        string? localAiRecoveryModelId = null,
        int? localAiRecoveryRequestedPort = null,
        string[]? commandLineArgs = null,
        ISetupNativeConnectionHost? nativeConnectionHost = null,
        ISetupLocalAiHost? localAiHost = null,
        Func<SetupNativeCompletion, CancellationToken, Task>? publishNativeCompletion = null,
        Func<bool, CancellationToken, Task>? applyNativeStartup = null,
        bool startupRegistrationAllowed = true,
        Action<TraySettingsConfig, bool?, bool>? persistChoices = null,
        GatewayConnectionManager? connectionManager = null,
        Func<SetupNativePreparation, CancellationToken, Task>? publishNativePreparation = null)
    {
        _startupRegistrationAllowed = startupRegistrationAllowed;
        _dataDir = dataDir ?? SetupContext.ResolveDataDir();
        _localDataDir = localDataDir ?? SetupContext.ResolveLocalDataDir();
        _nativeConnectionHost = nativeConnectionHost;
        _localAiHost = localAiHost;
        _connectionManager = connectionManager;
        _publishNativeCompletion = publishNativeCompletion;
        _publishNativePreparation = publishNativePreparation;
        _applyNativeStartup = applyNativeStartup;
        _persistChoices = persistChoices;
        _startAtLocalAiRecoveryReview = startAtLocalAiRecoveryReview;
        InitializeComponent();
        LoadingOverlay.Bind(_loadingProgress);
        ApplyWindowIcon();
        Active = this;
        RootFrame.Navigated += (_, _) => RefreshFlowProgress();
        RootFrame.PointerMoved += (_, args) =>
        {
            if (ActiveMascot is not { ActualWidth: > 0, ActualHeight: > 0 } mascot)
                return;
            var position = args.GetCurrentPoint(mascot).Position;
            mascot.SetPointerGaze(
                (position.X - mascot.ActualWidth / 2) / mascot.ActualWidth,
                (position.Y - mascot.ActualHeight / 2) / mascot.ActualHeight);
        };
        RootFrame.PointerExited += (_, _) => ActiveMascot?.SetPointerGaze(null, null);

        Closed += async (_, _) =>
        {
            _isClosed = true;
            LoadingOverlay.Dispose();
            _loadingProgress.Dispose();
            RootFrame.Loaded -= AttachMinimumSizeRoot;
            AppWindow.Changed -= MinimumSizeWindowChanged;
            if (_minimumSizeRoot is { } sizingRoot)
                sizingRoot.Changed -= MinimumSizeRootChanged;
            _minimumSizeRoot = null;
            _initialContentReady.TrySetResult(true);
            try
            {
                _lifetimeCts.Cancel();
                _completionPreparation?.Dispose();
                var nativeCleanup = RootFrame.Content switch
                {
                    NativeGatewaySetupPage nativePage => nativePage.CancelAndWaitAsync(),
                    WizardPage wizardPage => wizardPage.CancelAndWaitAsync(),
                    _ => Task.CompletedTask,
                };
                var pageCleanup = RootFrame.Content is IAsyncDisposable pageLifetime
                    ? pageLifetime.DisposeAsync().AsTask()
                    : Task.CompletedTask;
                try
                {
                    try
                    {
                        await nativeCleanup;
                    }
                    finally
                    {
                        if (_contextApplyTask is { } contextApplyTask)
                            await contextApplyTask;
                    }
                }
                finally
                {
                    try
                    {
                        await pageCleanup;
                    }
                    finally
                    {
                        try
                        {
                            await Task.WhenAll(
                                _nativePageCleanupTask, _aiPageCleanupTask, _preparationTask,
                                _completionPreparation?.CleanupCompleted ?? Task.CompletedTask,
                                _localAiTransitionTask ?? Task.CompletedTask);
                        }
                        finally
                        {
                            AccessDraft?.ClearNativeConnectionSecrets();
                            await ReleaseNativeSetupAsync();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Window teardown owns this cancellation; cleanup still must finish.
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Setup cleanup failed: {ex}");
            }
            finally
            {
                _setupLock?.Dispose();
                _setupLock = null;
                if (ReferenceEquals(Active, this))
                    Active = null;
                _cleanupCompleted.TrySetResult(true);
            }
        };

        RootFrame.Loaded += AttachMinimumSizeRoot;
        AppWindow.Changed += MinimumSizeWindowChanged;
        ApplyMinimumWindowSize();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var initialSize = SetupWindowSizing.InitialPixels(dpi);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(initialSize.Width, initialSize.Height));

        // Extend into title bar for modern look
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDrag);

        // Mica backdrop — the signature Windows 11 material (native).
        SystemBackdrop = new MicaBackdrop();

        // Load config: explicit --config arg, or bundled default-config.json (required)
        commandLineArgs ??= Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (!SetupWindowCommandLine.TryParse(
                commandLineArgs,
                out var setupArguments,
                out var argumentError))
        {
            ShowConfigurationError($"Invalid setup arguments: {argumentError}");
            return;
        }

        var explicitConfigPath = configPath ?? setupArguments.ConfigPath;
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

        if (configPath == null)
        {
            var missingPath = Path.Combine(AppContext.BaseDirectory, "default-config.json");
            ShowConfigurationError(
                $"No setup configuration file was found at '{missingPath}'. " +
                "Place default-config.json next to the executable or pass --config <path>.");
            return;
        }

        if (!SetupConfig.TryLoadFromFile(configPath, out var loadedConfig, out var configError))
        {
            ShowConfigurationError(
                $"The setup configuration file '{configPath}' could not be loaded. {configError}");
            return;
        }

        _config = loadedConfig;
        _config.UsesBundledDefaultConfig = explicitConfigPath == null;
        _config = SetupConfig.FromEnvironment(_config);
        if (!_startupRegistrationAllowed)
            _config.Settings.AutoStart = false;
        if (!string.IsNullOrWhiteSpace(distroNameOverride))
            _config.DistroName = distroNameOverride;
        if (gatewayPortOverride is > 0 and <= 65535)
        {
            _config.GatewayPort = gatewayPortOverride.Value;
            _config.GatewayUrl = null;
        }
        try
        {
            GatewayInstallPolicy.ValidateAndApply(_config);
        }
        catch (GatewayCompatibilityException ex)
        {
            ShowConfigurationError(ex.Message);
            return;
        }
        _config.ApplyUiDefaults(rollbackOnFailure: setupArguments.RollbackOnFailure);
        _localAiRecoveryBaseline = LocalAiRecoveryConfigurationBaseline.Capture(_config);
        AccessDraft = new SetupAccessDraft(_config);
        if (startAtLocalAiRecoveryReview)
        {
            _config.LocalAiRecoveryGatewayId = localAiRecoveryGatewayId;
            if (!string.IsNullOrWhiteSpace(localAiRecoveryDistroName))
                _config.DistroName = localAiRecoveryDistroName;
            if (localAiRecoveryGatewayPort is > 0 and <= 65535)
            {
                _config.GatewayPort = localAiRecoveryGatewayPort.Value;
                _config.GatewayUrl = null;
            }
            if (!string.IsNullOrWhiteSpace(localAiRecoveryModelId))
            {
                _config.LocalAi.SelectedModelId = localAiRecoveryModelId;
                _config.LocalAi.InstalledReceiptModelId = localAiRecoveryModelId;
                _pinLocalAiRecoveryModel = true;
            }
            if (localAiRecoveryRequestedPort is { } requestedPort &&
                LocalAiPortPolicy.TryValidate(requestedPort, out _))
            {
                _config.LocalAi.Port = requestedPort;
            }
            _config.LocalAi.Enabled = true;
            _config.SkipWizard = true;
            _config.RollbackOnFailure = true;
        }
        if (startAtGatewayInstalledMilestone || startAtLocalAiRecoveryReview)
        {
            _persistStartupPreferenceOnComplete = false;
            _showStartupPreferenceOnComplete = false;
        }

        var previewPage = SetupPreview.RequestedPage;
        if (previewPage != null)
        {
            NavigatePreview(previewPage);
            return;
        }

        if (!SetupRunLock.TryAcquire(_dataDir, out _setupLock, out var lockMessage))
        {
            NavigateTo(typeof(CompletePage), new CompletePageArgs(false, TimeSpan.Zero, null, lockMessage ?? "Another setup run is active."));
            return;
        }

        if (startAtGatewayInstalledMilestone)
            NavigateToGatewayInstalledMilestone();
        else if (startAtLocalAiRecoveryReview)
            NavigateToLocalAiSetup();
        else
            NavigateTo(typeof(SecurityNoticePage), _config);
    }

    public void NavigateToSecurityNotice(bool back = false) => NavigateTo(typeof(SecurityNoticePage), _config, back);

    private void AttachMinimumSizeRoot(object sender, RoutedEventArgs args)
    {
        if (_isClosed) return;
        var root = RootFrame.XamlRoot;
        if (!ReferenceEquals(root, _minimumSizeRoot))
        {
            if (_minimumSizeRoot is { } previous)
                previous.Changed -= MinimumSizeRootChanged;
            _minimumSizeRoot = root;
            if (root is not null)
                root.Changed += MinimumSizeRootChanged;
        }
        ApplyMinimumWindowSize();
    }

    private void MinimumSizeRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ApplyMinimumWindowSize();

    private void MinimumSizeWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidPresenterChange)
            ApplyMinimumWindowSize();
    }

    private void ApplyMinimumWindowSize()
    {
        if (_isClosed || AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var minimum = SetupWindowSizing.MinimumPixels(dpi);
        if (presenter.PreferredMinimumWidth != minimum.Width)
            presenter.PreferredMinimumWidth = minimum.Width;
        if (presenter.PreferredMinimumHeight != minimum.Height)
            presenter.PreferredMinimumHeight = minimum.Height;
    }

    public void NavigateToWelcome(bool back = false)
    {
        ResetLocalAiRecoveryMode();
        NavigateTo(typeof(WelcomePage), _config, back);
    }
    internal async Task<NativeGatewayEligibility> GetNativeGatewayEligibilityAsync()
    {
        using var logger = new SetupLogger(filePath: null);
        return await Task.Run(() => NativeGatewaySetupEligibility.Probe(new SetupOpenClawLogger(logger)));
    }
    internal void NavigateToNativeGatewaySetup() => NavigateTo(typeof(NativeGatewaySetupPage), _config);
    internal void NavigateToNativeCapabilities() =>
        NavigateToCapabilities(back: true);
    internal void NavigateToNativeAiSetup(NativeGatewaySetupSession session, GatewayAiPreparation? preparation = null)
    {
        NativeSetupSession = session;
        NavigateTo(typeof(AiSetupPage), CreateAiSetupArgs() with { Preparation = preparation });
    }

    internal async Task CancelNativeAiSetupAsync()
    {
        if (RootFrame.Content is AiSetupPage page)
            await page.CloseAsync();
        await ReleaseNativeSetupAsync();
        if (!_isClosed)
            NavigateToNativeCapabilities();
    }

    internal async Task ReleaseNativeSetupAsync()
    {
        var session = NativeSetupSession;
        if (session is null)
            return;
        try
        {
            if (!session.IsPublished && _localAiHost is INativeSetupLocalAiHost { HasNativeSelection: true } localAi)
            {
                await using var connection = await NativeGatewaySetupConnection.ConnectAsync(session, CancellationToken.None);
                localAi.ConfigureNative(session.Record, connection, session.AuthorizeAsync);
                try { await localAi.WithdrawNativeAsync(CancellationToken.None); }
                finally { localAi.ReleaseNative(connection); }
            }
        }
        finally
        {
            await session.DisposeAsync();
            if (ReferenceEquals(NativeSetupSession, session))
                NativeSetupSession = null;
        }
    }

    internal void NavigateToNativeComplete(string gatewayUrl)
    {
        _persistStartupPreferenceOnComplete = false;
        _showStartupPreferenceOnComplete = false;
        NavigateTo(typeof(CompletePage), new CompletePageArgs(
            Success: true, Elapsed: TimeSpan.Zero, LogPath: null, ShowStartupPreference: false)
        {
            NativeGatewayUrl = gatewayUrl,
            NativeCapabilitySummary = string.Join(", ", SetupCapabilityProfiles.Ordered
                .Where(capability => AccessDraft.GetCapability(capability))
                .Select(capability => capability.ToString())),
        });
    }

    internal void SaveNativeCapabilities()
    {
        _config.Settings.ApplyCapabilities(_config.Capabilities);
        _persistStartupPreferenceOnComplete = false;
        SaveSetupChoices(AutoStartAfterSetup);
    }

    public bool IsWelcomeInstallSelected => _isWelcomeInstallSelected;
    public void SetWelcomeInstallSelected(bool installSelected)
    {
        _isWelcomeInstallSelected = installSelected;
        if (installSelected)
            AccessDraft.SelectRoute(SetupGatewayRoute.ManagedWsl);
        else if (AccessDraft.Route == SetupGatewayRoute.ManagedWsl)
            AccessDraft.SelectRoute(SetupGatewayRoute.Existing);
        RefreshFlowProgress();
    }

    internal Task<HostHardwareInfo> GetLocalAiHardwareAsync(bool forceRefresh = false) =>
        _localAiHardwareProbe.GetAsync(forceRefresh);

    internal Task<WslViabilityResult> GetWslViabilityAsync(bool refresh = false) =>
        _wslViabilityProbe.GetAsync(refresh);

    private static async Task<WslViabilityResult> InspectWslViabilityAsync()
    {
        using var logger = new SetupLogger(filePath: null);
        return await WslViabilityInspector.InspectAsync(
            new CommandRunner(logger),
            logger,
            CancellationToken.None);
    }

    public void NavigateToAdvancedSetup() => NavigateTo(typeof(AdvancedSetupPage), _config);
    internal void NavigateToNativeConnection(SetupGatewayRoute route)
    {
        if (route is not (SetupGatewayRoute.Existing or SetupGatewayRoute.Remote))
            throw new ArgumentOutOfRangeException(nameof(route));
        if (_nativeConnectionHost is null)
        {
            NavigateTo(typeof(AdvancedSetupPage), true);
            return;
        }

        SelectGatewayRoute(route);
        int generation = ++_nativeNavigationGeneration;
        NavigateTo(typeof(SetupNativeConnectionPage), new SetupNativeConnectionNavigationArgs(
            _nativeConnectionHost,
            AccessDraft.NativeConnectionRequest,
            request =>
            {
                if (!_isClosed && generation == _nativeNavigationGeneration)
                    AccessDraft.NativeConnectionRequest = request;
            },
            result =>
            {
                var accepted = AccessDraft.TryAcceptNativeConnection(route, result);
                if (_isClosed || generation != _nativeNavigationGeneration)
                    return;
                if (!accepted)
                {
                    NavigateToComplete(false, TimeSpan.Zero, _config.LogPath,
                        result.Error ?? SetupLocalization.GetString("Onboarding_NativeConnection_Failed"));
                    return;
                }
                SelectGatewayRoute(route, gatewayAvailable: true);
                _expectedConfiguredModelRef = null;
                NavigateToCapabilities();
            },
            () => NavigateToWelcome(back: true),
            _lifetimeCts.Token));
    }
    public void SelectGatewayRoute(SetupGatewayRoute route, bool gatewayAvailable = false)
    {
        AccessDraft.SelectRoute(route, gatewayAvailable);
        _isWelcomeInstallSelected = route == SetupGatewayRoute.ManagedWsl;
        RefreshFlowProgress();
    }
    public void NavigateToCapabilities() => NavigateToCapabilities(back: false);
    public void NavigateToCapabilities(bool back) =>
        NavigateTo(typeof(CapabilitiesPage), AccessDraft, back);
    public void NavigateAfterCapabilities()
    {
        switch (OnboardingFlowPolicy.GetAccessDestination(AccessDraft.Route))
        {
            case OnboardingAccessDestination.NativeGatewaySetup:
                NavigateToNativeGatewaySetup();
                break;
            case OnboardingAccessDestination.GatewayReview:
                NavigateToGatewaySetup();
                break;
            case OnboardingAccessDestination.AiSetup:
                if (AccessDraft.GatewayAvailable)
                    TryNavigateToWizard();
                else
                    NavigateTo(typeof(AdvancedSetupPage), true);
                break;
            case OnboardingAccessDestination.CompleteWithoutGateway:
                AsyncEventHandlerGuard.Run(CompleteSetupAsync, NullLogger.Instance, nameof(CompleteSetupAsync));
                break;
        }
    }
    public void NavigateToGatewaySetup(bool back = false) =>
        NavigateTo(typeof(GatewaySetupPage), AccessDraft, back);
    public void NavigateToLocalAiSetup(bool back = false) => NavigateToDetail(GatewaySetupDetail.LocalAi, back);
    public void NavigateToTailscaleSetup() => NavigateToDetail(GatewaySetupDetail.Tailscale);
    public void NavigateToWslNetworking(bool returnToReview = false) =>
        NavigateToDetail(GatewaySetupDetail.Networking, returnToReview: returnToReview);
    private void NavigateToDetail(GatewaySetupDetail detail, bool back = false, bool returnToReview = false) =>
        NavigateTo(typeof(GatewaySetupDetailPage),
            new GatewaySetupDetailArgs(AccessDraft, detail, _startAtLocalAiRecoveryReview, _pinLocalAiRecoveryModel, returnToReview), back);

    internal Task ReviewLocalAiAsync(LocalAiOnboardingSnapshot selection) =>
        _localAiTransitionTask = ReviewLocalAiCoreAsync(selection);

    private async Task ReviewLocalAiCoreAsync(LocalAiOnboardingSnapshot selection)
    {
        if (_isClosed || _localAiHost is null || RootFrame.Content is not AiSetupPage)
            return;
        var target = await _localAiHost.RevalidateReviewAsync(selection, _lifetimeCts.Token);
        if (_isClosed || RootFrame.Content is not AiSetupPage)
            return;
        _localAiRecoveryBaseline = LocalAiRecoveryConfigurationBaseline.Capture(_config);
        _localAiReviewSelection = selection;
        _startAtLocalAiRecoveryReview = true;
        _pinLocalAiRecoveryModel = target.ModelCatalogId is not null;
        _config.LocalAiRecoveryGatewayId = target.GatewayId;
        _config.NativeLocalAiAcquisition = target.IsNative;
        if (!target.IsNative)
            _config.DistroName = target.DistroName;
        _config.GatewayPort = target.GatewayPort;
        _config.GatewayUrl = null;
        _config.LocalAi.SelectedModelId = target.ModelCatalogId;
        _config.LocalAi.InstalledReceiptModelId = target.ModelCatalogId;
        if (target.RequestedLocalAiPort is { } port)
            _config.LocalAi.Port = port;
        _config.LocalAi.Enabled = true;
        _config.SkipWizard = true;
        _config.RollbackOnFailure = true;
        AccessDraft.LocalAiReady = false;
        NavigateToLocalAiSetup();
    }

    internal Task InstallReviewedLocalAiAsync() =>
        _localAiTransitionTask = InstallReviewedLocalAiCoreAsync();

    private async Task InstallReviewedLocalAiCoreAsync()
    {
        if (_isClosed || _localAiHost is null || _localAiReviewSelection is not { } selection ||
            !AccessDraft.CanInstall(localAiRecovery: true))
            return;
        var modelId = _config.LocalAi.SelectedModelId;
        var requestedPort = _config.LocalAi.Port;
        await _aiPageCleanupTask;
        var target = await _localAiHost.RevalidateReviewAsync(selection, _lifetimeCts.Token);
        if (_config.LocalAi.SelectedModelId != modelId || _config.LocalAi.Port != requestedPort)
            throw new LocalAiSelectionRejectedException("The reviewed Local AI model changed. Review it again before installing.");
        if (!_isClosed && AccessDraft.CanInstall(localAiRecovery: true))
        {
            _localAiInstallAndUse = target.IsNative
                ? new(target, modelId ??
                    throw new LocalAiSelectionRejectedException("Select a Local AI model before installing."),
                    requestedPort)
                : null;
            NavigateToProgress();
        }
    }

    internal void ContinueInstalledNativeLocalAi()
    {
        if (_isClosed || !_config.NativeLocalAiAcquisition || _localAiInstallAndUse is not { } intent)
            throw new InvalidOperationException("The reviewed native Local AI installation is no longer available.");
        _localAiInstallAndUse = null;
        NavigateTo(typeof(AiSetupPage), CreateAiSetupArgs() with
        {
            ExpectedGatewayId = intent.Target.GatewayId,
            ExpectedEndpointBinding = intent.Target.EndpointBinding,
            InstallAndUse = intent
        });
    }

    internal void CancelLocalAiReview()
    {
        if (_localAiReviewSelection is null)
            return;
        _localAiRecoveryBaseline.Restore(_config);
        _config.LocalAiRecoveryGatewayId = null;
        _config.NativeLocalAiAcquisition = false;
        _localAiReviewSelection = null;
        _localAiInstallAndUse = null;
        _startAtLocalAiRecoveryReview = false;
        _pinLocalAiRecoveryModel = false;
        AccessDraft.LocalAiReady = false;
        _expectedConfiguredModelRef = null;
        TryNavigateToWizard(back: true);
    }
    public void NavigateToProgress()
    {
        if (!AccessDraft.CanInstall(_startAtLocalAiRecoveryReview))
            return;
        _expectedConfiguredModelRef = null;
        NavigateTo(typeof(ProgressPage), CreateProgressPageArgs(showMilestoneOnly: false));
    }
    private string? _expectedConfiguredGatewayId;
    private SetupCompletionIntent _configuredCompletionIntent = SetupCompletionIntent.Dashboard;
    internal OpenClaw.Connection.GatewayRegistrySnapshot? BeginGatewaySetup() => _localAiHost?.BeginGatewaySetup();
    internal Task SettleGatewaySetupAsync(OpenClaw.Connection.GatewayRegistrySnapshot? expectedOutput, string? completedGatewayId) =>
        _localAiHost?.ReconcileGatewaySetupAsync(
            expectedOutput ?? throw new InvalidOperationException("The setup registry output is unavailable."), completedGatewayId)
        ?? Task.CompletedTask;
    internal void SetExpectedConfiguredModelRef(string modelRef, string gatewayId)
    {
        _expectedConfiguredModelRef = modelRef;
        _expectedConfiguredGatewayId = gatewayId;
        _configuredCompletionIntent = _startAtLocalAiRecoveryReview && _pinLocalAiRecoveryModel
            ? SetupCompletionIntent.Dashboard : SetupCompletionIntent.CustodianOnboarding;
    }
    public void NavigateToGatewayInstalledMilestone() =>
        NavigateTo(typeof(ProgressPage), CreateProgressPageArgs(showMilestoneOnly: true));

    private ProgressPageArgs CreateProgressPageArgs(bool showMilestoneOnly) =>
        new(_config, showMilestoneOnly, _startAtLocalAiRecoveryReview, _dataDir, _localDataDir);

    public bool TryNavigateToGatewayInstalledMilestone()
    {
        if (!CanNavigateToGatewayInstalledMilestone)
            return false;

        ResetLocalAiRecoveryMode();
        _persistStartupPreferenceOnComplete = false;
        _showStartupPreferenceOnComplete = false;
        NavigateToGatewayInstalledMilestone();
        return true;
    }

    private void ResetLocalAiRecoveryMode()
    {
        if (!_startAtLocalAiRecoveryReview)
            return;

        _startAtLocalAiRecoveryReview = false;
        _localAiReviewSelection = null;
        _localAiInstallAndUse = null;
        _pinLocalAiRecoveryModel = false;
        _config.LocalAiRecoveryGatewayId = null;
        _localAiRecoveryBaseline.Restore(_config);
        _config.NativeLocalAiAcquisition = false;
        AccessDraft.LocalAiReady = false;
        AccessDraft.TailscaleReady = false;
        _persistStartupPreferenceOnComplete = true;
        _showStartupPreferenceOnComplete = true;
    }

    public bool TryNavigateToExistingNativeLocalAi(OpenClaw.Connection.GatewayRecord record)
    {
        if (!CanNavigateToWizard)
            return false;
        AccessDraft.SelectExistingNativeGateway(record);
        _persistStartupPreferenceOnComplete = false;
        _showStartupPreferenceOnComplete = false;
        return TryNavigateToWizard();
    }

    public bool TryNavigateToWizard(bool back = false)
    {
        if (!CanNavigateToWizard)
            return false;

        NavigateTo(typeof(AiSetupPage), CreateAiSetupArgs(), back);
        return true;
    }

    private AiSetupPageArgs CreateAiSetupArgs() =>
        new AiSetupPageArgs(_config, _dataDir, _localDataDir,
                TryNavigateToLegacyWizard, CompleteSetupAsync, _expectedConfiguredModelRef,
                LocalAiHost: _localAiHost, ReviewLocalAi: ReviewLocalAiAsync,
                ExpectedGatewayId: NativeSetupSession?.Record.Id ??
                    (_expectedConfiguredModelRef is null ? AccessDraft.NativeGatewayId : _expectedConfiguredGatewayId),
                ConfiguredCompletionIntent: _configuredCompletionIntent,
                CompleteVerifiedSetup: CompleteVerifiedAiSetupAsync, NativeSession: NativeSetupSession,
                CancelNativeSetup: NativeSetupSession is null ? null : CancelNativeAiSetupAsync,
                ConnectionManager: _connectionManager,
                CloseSetupWindow: Close,
                Loading: _loadingProgress,
                ExpectedEndpointBinding: _expectedConfiguredModelRef is null ? AccessDraft.NativeEndpointBinding : null);

    public bool TryNavigateToLegacyWizard()
    {
        if (_isClosed || _setupLock is null || RootFrame.Content is WizardPage)
            return false;

        NavigateTo(typeof(WizardPage), _config);
        return true;
    }

    private Task CompleteVerifiedAiSetupAsync(GatewayAiSetupCompletion completion)
    {
        if (_isClosed || completion.ModelTarget is not null || completion.VerifiedGeneration <= 0 ||
            !OnboardingFlowPolicy.RequiresAiSetup(AccessDraft.Route, _config))
            throw new InvalidOperationException("Native completion requires this setup's verified primary AI.");
        RequireVerifiedGateway(completion);
        if (_completionPreparation is not null)
            throw new InvalidOperationException("Setup completion has already been admitted.");
        _finishingLoading = BeginLoading(SetupLoadingGroup.Finishing, SetupLoadingStep.Drain);
        _completionPreparation = new(completion,
            _ => _aiPageCleanupTask,
            (proof, ct) => NativeSetupSession is { } native
                ? native.VerifyAsync(proof, ct, _finishingLoading)
                : SetupNativeCompletionVerifier.VerifyAsync(_dataDir, proof, ct, _connectionManager, progress: _finishingLoading),
            FinalizeNativeChoiceAsync,
            async (preparation, ct) =>
            {
                RequireVerifiedGateway(preparation.Verification);
                await (_publishNativePreparation?.Invoke(preparation, ct) ??
                    Task.FromException(new InvalidOperationException("The setup preparation host is unavailable.")));
                _completionDispatched = true;
            }, canResumeCommittedFinalization: () => _nativeContextFinalized);
        NavigateTo(typeof(AiCompletionPage), null);
        _completionPreparation.StateChanged += PreparationStateChanged;
        // The tracked continuation yields before draining the originating active request.
        _preparationTask = ObservePreparationAsync(_completionPreparation);
        return Task.CompletedTask;
    }

    private void PreparationStateChanged()
    {
        if (_completionPreparation is { } current && current.Stage != SetupNativeCompletionStage.Finalizing)
            _finishingLoading?.Report(current.Stage switch
            {
                SetupNativeCompletionStage.Draining => SetupLoadingStep.Drain,
                SetupNativeCompletionStage.Opening => SetupLoadingStep.RestartCompanion,
                _ => SetupLoadingStep.ConnectGateway
            });
        if (!_isClosed && RootFrame.Content is AiCompletionPage page && _completionPreparation is { } owner)
            page.ShowStage(owner.Stage);
    }

    private async Task ObservePreparationAsync(SetupCompletionPreparation owner, bool retry = false)
    {
        if (retry) _finishingLoading = BeginLoading(SetupLoadingGroup.Finishing, SetupLoadingStep.Drain);
        try { await (retry ? owner.RetryAsync() : owner.StartAsync()); }
        catch (Exception error)
        {
            _finishingLoading?.Dispose();
            System.Diagnostics.Trace.TraceWarning("Setup completion failed ({0}).", error.GetType().Name);
            if (!_isClosed && RootFrame.Content is AiCompletionPage page)
                page.ShowFailure(
                    owner.CanRetry ? () => _preparationTask = ObservePreparationAsync(owner, retry: true) : null,
                    CanReturnFromFinishing ? ReturnFromFinishing : null);
        }
    }

    private bool CanReturnFromFinishing => !_nativeContextFinalized && NativeSetupSession?.IsPublished != true;

    private void ReturnFromFinishing()
    {
        if (_isClosed || !CanReturnFromFinishing || _completionPreparation is not { ActiveTask.IsCompleted: true } owner)
            return;
        if (RootFrame.Content is AiCompletionPage page) page.ShowStage(SetupNativeCompletionStage.Draining);
        _preparationTask = ReturnFromFinishingAsync(owner);
    }

    private async Task ReturnFromFinishingAsync(SetupCompletionPreparation owner)
    {
        owner.StateChanged -= PreparationStateChanged;
        owner.Dispose();
        try
        {
            await owner.CleanupCompleted;
            try { await _aiPageCleanupTask; }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceWarning("Prior AI page cleanup settled with failure ({0}).", error.GetType().Name);
            }
            await ReleaseNativeSetupAsync();
            if (_isClosed) return;
            _completionPreparation = null;
            NavigateToCapabilities(back: true);
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning("Returning from setup completion failed ({0}).", error.GetType().Name);
            if (!_isClosed && RootFrame.Content is AiCompletionPage page) page.ShowFailure();
        }
    }
    private void RequireVerifiedGateway(GatewayAiSetupCompletion completion)
    {
        if (_isClosed) throw new OperationCanceledException(_lifetimeCts.Token);
        if (NativeSetupSession is { } native)
            native.RequireCompletion(completion);
        else
            SetupGatewaySession.RequireCompletionGateway(_dataDir, completion);
    }

    private async Task FinalizeNativeChoiceAsync(GatewayAiSetupCompletion proof, CancellationToken ct)
    {
        if (_publishNativePreparation is null || _applyNativeStartup is null)
            throw new InvalidOperationException("The native completion host is unavailable.");
        RequireVerifiedGateway(proof);
        if (!_nativeContextFinalized)
        {
            if (NativeSetupSession is { } native)
            {
                await native.CompleteVerifiedAsync(proof, _config.Capabilities, ct,
                    afterVerification: proof.RequiresManagedLocalAi &&
                        _localAiHost is INativeSetupLocalAiHost { HasNativeSelection: true } localAi
                        ? (transport, token) => localAi.ReconcileNativeAsync(transport, proof.ModelRef, token)
                        : null, progress: _finishingLoading);
            }
            else
            {
                _finishingLoading?.Report(SetupLoadingStep.CheckConfiguration);
                var result = await ApplyWindowsNodeContextAsync();
                if (!result.IsSuccess) throw new InvalidOperationException(result.Message);
                if (proof.RequiresManagedLocalAi &&
                    _localAiHost is INativeSetupLocalAiHost { HasNativeSelection: true } localAi &&
                    _connectionManager is { } manager)
                {
                    _finishingLoading?.Report(SetupLoadingStep.ReconcileLocalAi);
                    var transport = await GatewayAiSetupTransport.BorrowNativeAsync(
                        _dataDir, manager, proof.GatewayId, ct, proof.EndpointBinding);
                    await localAi.ReconcileNativeAsync(transport, proof.ModelRef, ct);
                }
            }
            ct.ThrowIfCancellationRequested();
            _nativeContextFinalized = true;
        }
        RequireVerifiedGateway(proof);
        var startup = _persistStartupPreferenceOnComplete && AutoStartAfterSetup;
        if (!_nativeSettingsSaved)
        {
            _finishingLoading?.Report(SetupLoadingStep.SaveSettings);
            SaveSetupChoices(startup);
            _nativeSettingsSaved = true;
        }
        if (!_nativeStartupApplied)
        {
            if (_startupRegistrationAllowed && _persistStartupPreferenceOnComplete)
            {
                _finishingLoading?.Report(SetupLoadingStep.ApplyStartup);
                await _applyNativeStartup(startup, ct);
            }
            _nativeStartupApplied = true;
        }
        RequireVerifiedGateway(proof);
    }

    public async Task CompleteSetupAsync()
    {
        if (_completionPreparation is not null)
            throw new InvalidOperationException("Setup completion is already in progress.");
        if (_completionDispatched || _isClosed)
            return;
        if (_completionTask is { } pending)
        {
            await pending;
            return;
        }

        _completionTask = CompleteSetupCoreAsync();
        try
        {
            await _completionTask;
        }
        finally
        {
            if (!_completionDispatched)
                _completionTask = null;
        }
    }

    private async Task CompleteSetupCoreAsync()
    {
        var result = await ApplyWindowsNodeContextAsync();
        if (_isClosed)
            return;
        if (!result.IsSuccess)
        {
            NavigateToComplete(false, TimeSpan.Zero, _config.LogPath, result.Message);
            return;
        }
        if (!RequestSetupCompleted(_persistStartupPreferenceOnComplete && AutoStartAfterSetup))
        {
            NavigateToComplete(false, TimeSpan.Zero, _config.LogPath,
                SetupLocalization.GetString("Onboarding_Flow_CompletionUnavailable"));
        }
    }

    internal void RefreshFlowProgress()
    {
        if (RootFrame.Content is not FrameworkElement page ||
            page.FindName("FlowProgress") is not SetupProgressIndicator progress)
            return;

        var stage = page switch
        {
            SecurityNoticePage => OnboardingStage.Welcome,
            WelcomePage or AdvancedSetupPage or SetupNativeConnectionPage => OnboardingStage.Gateway,
            CapabilitiesPage => OnboardingStage.Capabilities,
            GatewaySetupPage or GatewaySetupDetailPage => OnboardingStage.GatewayReview,
            ProgressPage or NativeGatewaySetupPage => OnboardingStage.Install,
            AiReadyPage => OnboardingStage.Ready,
            _ => OnboardingStage.AiSetup,
        };
        progress.Update(
            OnboardingFlowPolicy.GetStages(AccessDraft.Route, _config, _startAtLocalAiRecoveryReview,
                includeReadyChoice: page is not WizardPage),
            stage);
    }

    private OnboardingMascot? ActiveMascot =>
        RootFrame.Content is FrameworkElement page
            ? page.FindName("MascotHero") as OnboardingMascot ??
              page.FindName("ProgressMascot") as OnboardingMascot
            : null;

    internal async Task<StepResult> ApplyWindowsNodeContextAsync()
    {
        if (_contextApplyTask is { } existingTask)
            return await existingTask;

        _contextApplyTask = ApplyWindowsNodeContextCoreAsync();
        try
        {
            return await _contextApplyTask;
        }
        finally
        {
            _contextApplyTask = null;
        }
    }

    private async Task<StepResult> ApplyWindowsNodeContextCoreAsync()
    {
        if (!OnboardingFlowPolicy.UsesWslWorkspaceFinalization(AccessDraft.Route))
            return StepResult.Skip("This setup route does not modify a WSL workspace.");
        if (!_config.WindowsNodeContext.Enabled)
            return StepResult.Skip("Windows node context injection disabled");

        var ct = _lifetimeCts.Token;
        using var logger = new SetupLogger(filePath: null);
        using var journal = new TransactionJournal(filePath: null, logger);
        var context = new SetupContext(
            _config,
            logger,
            journal,
            new CommandRunner(logger),
            ct,
            _dataDir,
            _localDataDir);
        // This is an idempotent refresh after onboarding, not a transactional
        // install. A failed refresh must not remove a valid block from an earlier run.
        var pipeline = new SetupPipeline(
            [new WindowsNodeBootstrapContextStep()],
            rollbackOnFailureOverride: false);
        var result = await pipeline.RunAsync(context);
        return result.Outcome switch
        {
            PipelineOutcome.Success => StepResult.Ok("Windows node context injected"),
            PipelineOutcome.Cancelled => StepResult.Fail("Windows node context injection was cancelled"),
            _ => StepResult.Fail(result.Message ?? "Windows node context injection failed")
        };
    }

    public void NavigateToComplete(
        bool success,
        TimeSpan elapsed,
        string? logPath,
        string? errorMessage = null,
        GatewayCompatibilityFailureKind? compatibilityFailure = null,
        LocalAiFailureDetail? detail = null,
        bool restartRequired = false)
    {
        var canRetryFallback =
            compatibilityFailure is { } failureKind &&
            GatewayInstallPolicy.CanRetryWithFallback(_config, failureKind);
        NavigateTo(
            typeof(CompletePage),
            new CompletePageArgs(
                success,
                elapsed,
                logPath,
                errorMessage,
                DefaultAutoStart: AutoStartAfterSetup,
                ShowStartupPreference: ShowStartupPreference,
                ReviewSummary: SetupReviewSummaryBuilder.Build(_config, _dataDir, _localDataDir),
                CanRetryGatewayFallback: canRetryFallback,
                GatewayFallbackVersion: canRetryFallback
                    ? _config.Gateway.FallbackVersion
                    : null,
                Detail: detail)
            {
                RequiresRestart = restartRequired,
            });
    }

    public bool TryRetryWithGatewayFallback(out string? error)
    {
        if (!GatewayInstallPolicy.TryApplyFallback(_config, out error))
            return false;

        NavigateToProgress();
        return true;
    }

    private void ShowConfigurationError(string errorMessage)
    {
        _persistStartupPreferenceOnComplete = false;
        _showStartupPreferenceOnComplete = false;
        NavigateTo(
            typeof(CompletePage),
            new CompletePageArgs(
                Success: false,
                Elapsed: TimeSpan.Zero,
                LogPath: null,
                ErrorMessage: errorMessage,
                ShowStartupPreference: false));
    }

    // Directional page transition: forward steps slide in from the right, Back from the left.
    private void NavigateTo(Type page, object? parameter, bool back = false)
    {
        if (page != typeof(AiSetupPage) && page != typeof(ProgressPage) &&
            page != typeof(AiCompletionPage) && page != typeof(NativeGatewaySetupPage))
            _loadingProgress.Clear();
        if (RootFrame.Content is SetupNativeConnectionPage nativePage)
        {
            // A replacement native page already advanced the generation before capturing its callbacks.
            if (page != typeof(SetupNativeConnectionPage))
                _nativeNavigationGeneration++;
            _nativePageCleanupTask = Task.WhenAll(
                _nativePageCleanupTask, nativePage.DisposeAsync().AsTask());
        }
        if (RootFrame.Content is AiSetupPage aiPage)
            _aiPageCleanupTask = Task.WhenAll(_aiPageCleanupTask, aiPage.CloseAsync());
        NavigationTransitionInfo transition = new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled
            ? new SlideNavigationTransitionInfo
            {
                Effect = back ? SlideNavigationTransitionEffect.FromLeft : SlideNavigationTransitionEffect.FromRight,
            }
            : new SuppressNavigationTransitionInfo();
        RootFrame.Navigate(page, parameter, transition);
    }

    private void NavigatePreview(string page) => RootFrame.Navigate(
        page switch
        {
            "welcome" => typeof(WelcomePage),
            "native" => typeof(NativeGatewaySetupPage),
            "advanced" => typeof(AdvancedSetupPage),
            "capabilities" => typeof(CapabilitiesPage),
            "gateway-review" => typeof(GatewaySetupPage),
            "capabilities-review" => typeof(GatewaySetupDetailPage),
            "capabilities-review-consent" => typeof(GatewaySetupDetailPage),
            "progress" => typeof(ProgressPage),
            "progress-local-ai" => typeof(ProgressPage),
            "milestone" => typeof(ProgressPage),
            "wizard" => typeof(WizardPage),
            "wizard-error" => typeof(WizardPage),
            "complete" => typeof(CompletePage),
            "complete-error" => typeof(CompletePage),
            _ => typeof(SecurityNoticePage),
        },
        page switch
        {
            "complete" => new CompletePageArgs(
                true,
                TimeSpan.FromMinutes(3),
                null,
                ReviewSummary: SetupReviewSummaryBuilder.Build(_config, _dataDir, _localDataDir)),
            "complete-error" => new CompletePageArgs(false, TimeSpan.FromMinutes(3), null, "Setup could not finish. Review the details, then retry setup when you are ready."),
            "progress" => CreateProgressPageArgs(showMilestoneOnly: false),
            "progress-local-ai" => CreateProgressPageArgs(showMilestoneOnly: false),
            "milestone" => CreateProgressPageArgs(showMilestoneOnly: true),
            "capabilities" or "gateway-review" => AccessDraft,
            "capabilities-review" or "capabilities-review-consent" =>
                new GatewaySetupDetailArgs(AccessDraft, GatewaySetupDetail.LocalAi, false, false),
            _ => _config,
        });

    public bool RequestAdvancedSetup()
    {
        if (AdvancedSetupRequested is { } handler)
        {
            handler.Invoke(this, EventArgs.Empty);
            return true;
        }
        return false;
    }

    public bool RequestSetupCompleted(bool enableAutoStart, bool preserveStartupPreference = false)
    {
        enableAutoStart &= _startupRegistrationAllowed;
        if (_completionPreparation is not null)
        {
            System.Diagnostics.Trace.TraceWarning("Legacy setup completion refused while verified setup is finishing.");
            return false;
        }
        var handler = SetupCompleted;
        if (handler == null)
            return false;

        try
        {
            SaveSetupChoices(enableAutoStart);
        }
        catch (Exception ex)
        {
            NavigateToComplete(false, TimeSpan.Zero, null,
                SetupLocalization.Format("Onboarding_V2_SaveChoicesFailed", ex.Message));
            return true;
        }

        handler.Invoke(this, new SetupCompletedEventArgs(enableAutoStart, AccessDraft.Route,
            ApplyStartupPreference: _startupRegistrationAllowed && _persistStartupPreferenceOnComplete));
        _completionDispatched = true;
        return true;
    }

    private void SaveSetupChoices(bool enableAutoStart)
    {
        if (AccessDraft.IsExistingNativeLocalAi)
            return;
        enableAutoStart &= _startupRegistrationAllowed;
        if (_persistChoices is not null)
        {
            _persistChoices(_config.Settings,
                _persistStartupPreferenceOnComplete || !_startupRegistrationAllowed ? enableAutoStart : null,
                AccessDraft.Route == SetupGatewayRoute.ManagedWsl);
            return;
        }
        if (_persistStartupPreferenceOnComplete || !_startupRegistrationAllowed)
        {
            _config.Settings.AutoStart = enableAutoStart;
            if (AccessDraft.Route == SetupGatewayRoute.ManagedWsl)
                TraySettingsConfig.UpdateAutoStartInSettingsFile(Path.Combine(_dataDir, "settings.json"), enableAutoStart);
        }
        if (AccessDraft.Route != SetupGatewayRoute.ManagedWsl)
            _config.Settings.MergeIntoSettingsFile(Path.Combine(_dataDir, "settings.json"),
                includeAutoStart: _persistStartupPreferenceOnComplete || !_startupRegistrationAllowed,
                defaultManagedAutoRepair: AccessDraft.Route != SetupGatewayRoute.Native);
    }

    internal void PersistPipelineSettings(TraySettingsConfig settings)
    {
        if (_persistChoices is not null) _persistChoices(settings, null, false);
        else settings.MergeIntoSettingsFile(Path.Combine(_dataDir, "settings.json"),
            includeAutoStart: _persistStartupPreferenceOnComplete);
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

}

public sealed record CompletePageArgs(
    bool Success,
    TimeSpan Elapsed,
    string? LogPath,
    string? ErrorMessage = null,
    bool DefaultAutoStart = true,
    bool ShowStartupPreference = true,
    SetupReviewSummary? ReviewSummary = null,
    bool CanRetryGatewayFallback = false,
    string? GatewayFallbackVersion = null,
    LocalAiFailureDetail? Detail = null)
{
    public bool RequiresRestart { get; init; }
    public string? NativeGatewayUrl { get; init; }
    public string? NativeCapabilitySummary { get; init; }
}
public sealed record SetupCompletedEventArgs(
    bool EnableAutoStart,
    SetupGatewayRoute Route = SetupGatewayRoute.ManagedWsl,
    bool ApplyStartupPreference = true);
