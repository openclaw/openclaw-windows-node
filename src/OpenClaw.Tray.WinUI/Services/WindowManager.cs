using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Windows;
using SetupCompletedEventArgs = OpenClaw.SetupEngine.UI.SetupCompletedEventArgs;
using SetupWindow = OpenClaw.SetupEngine.UI.SetupWindow;

namespace OpenClawTray.Services;

internal sealed record WindowManagerCallbacks(
    Func<AppState?> GetAppState,
    Func<AppNotificationService?> GetAppNotificationService,
    Func<GatewayConnectionManager?> GetConnectionManager,
    Func<GatewayRegistry?> GetGatewayRegistry,
    Func<SettingsManager?> GetSettings,
    Func<GatewayDirectConnectService?> GetGatewayDirectConnectService,
    Func<ILocalAiRuntime?> GetLocalAiRuntime,
    Func<NodeService?> GetNodeService,
    Func<VoiceService?> GetVoiceService,
    Func<IPageActivator?> GetPageActivator,
    Func<string?> GetPendingChatSessionKey,
    Func<string[]?> GetStartupArgs,
    Func<string, bool> IsDeepLinkArg,
    Func<bool> RequiresSetup,
    Action Connect,
    Action Disconnect,
    EventHandler SettingsSaved,
    EventHandler AdvancedSetupRequested,
    EventHandler<SetupCompletedEventArgs> SetupCompleted,
    Action<Window?> ApplyTheme,
    Func<SetupNativeCompletion, CancellationToken, Task>? PublishNativeCompletion = null,
    Func<bool, CancellationToken, Task>? ApplyNativeStartup = null,
    Func<ISettingsStore?>? GetSettingsStore = null,
    Func<LocalAiGatewayLifecycle?>? GetLocalAiGatewayLifecycle = null);

internal sealed class WindowManager : IWindowManager
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly WindowManagerCallbacks _callbacks;
    private Window? _keepAliveWindow;
    private HubWindow? _hubWindow;
    private WorkspaceWindow? _workspaceWindow;
    private Window? _lastActiveMainWindow;
    private ChatWindow? _chatWindow;
    private ConnectionStatusWindow? _connectionStatusWindow;
    private SetupWindow? _setupWindow;
    private bool _isShuttingDown;
    private Task? _closeForShutdownTask;
    private bool _nativeSetupFailureVisible;

    internal WindowManager(
        DispatcherQueue dispatcherQueue,
        WindowManagerCallbacks callbacks)
    {
        _dispatcherQueue = dispatcherQueue;
        _callbacks = callbacks;
    }

    public Window? ActiveHubWindow =>
        _isShuttingDown ? null :
            _lastActiveMainWindow is WorkspaceWindow { IsClosed: false } workspace ? workspace :
            _lastActiveMainWindow is HubWindow { IsClosed: false } hub ? hub :
            _hubWindow is { IsClosed: false } ? _hubWindow :
            _workspaceWindow is { IsClosed: false } ? _workspaceWindow : null;

    public bool IsHubOpen => !_isShuttingDown &&
        (_workspaceWindow is { IsClosed: false } || _hubWindow is { IsClosed: false });

    public bool IsChatVisible => ChatVisibilityPolicy.IsChatVisible(
        _isShuttingDown,
        !_isShuttingDown && _workspaceWindow is { IsChatVisible: true },
        _chatWindow is { IsClosed: false, Visible: true });

    public XamlRoot? DialogXamlRoot =>
        _isShuttingDown
            ? null
            : (ActiveHubWindow?.Content as FrameworkElement)?.XamlRoot
              ?? (_hubWindow is { IsClosed: false } hub
                ? (hub.Content as FrameworkElement)?.XamlRoot
                : null)
              ?? (_workspaceWindow?.Content as FrameworkElement)?.XamlRoot
              ?? (_keepAliveWindow?.Content as FrameworkElement)?.XamlRoot;

    public XamlRoot? RuntimeAnchorXamlRoot =>
        _isShuttingDown ? null : (_keepAliveWindow?.Content as FrameworkElement)?.XamlRoot;

    public XamlRoot? SetupXamlRoot =>
        _isShuttingDown ? null : (_setupWindow?.Content as FrameworkElement)?.XamlRoot;

    public bool CanNavigateHubBack() => ActiveHubWindow switch
    {
        WorkspaceWindow workspace => workspace.CanGoBack,
        HubWindow hub => hub.CanGoBack,
        _ => false
    };

    public void NavigateHubBack()
    {
        if (ActiveHubWindow is WorkspaceWindow workspace)
            workspace.NavigateBack();
        else if (ActiveHubWindow is HubWindow hub)
        {
            hub.NavigateBack();
        }
    }

    public void InitializeRuntimeAnchor()
    {
        if (_keepAliveWindow is not null || _isShuttingDown)
        {
            return;
        }

        _keepAliveWindow = new Window
        {
            Content = new Grid(),
        };
        _callbacks.ApplyTheme(_keepAliveWindow);
        _keepAliveWindow.AppWindow.IsShownInSwitchers = false;
        _keepAliveWindow.AppWindow.MoveAndResize(
            new global::Windows.Graphics.RectInt32(-32000, -32000, 1, 1));
    }

    public void BeginShutdown() => _isShuttingDown = true;

    private bool _dashboardFailureVisible;

    public async Task ShowDashboardLaunchFailureAsync(Action? retry)
    {
        if (_isShuttingDown || _dashboardFailureVisible)
            return;
        var message = LocalizationHelper.GetString("Onboarding_DashboardLaunchFailed");
        _callbacks.GetAppNotificationService()?.Show(new AppNotification
        {
            Id = GatewayDashboardLauncher.FailureNotificationId,
            Title = LocalizationHelper.GetString("ChatDashboardButton.Content"),
            Message = message,
            Source = "connection",
            Severity = AppNotificationSeverity.Error,
            DedupeKey = GatewayDashboardLauncher.FailureNotificationId,
            ActionLabel = LocalizationHelper.GetString("HubWindow_NavigationViewItem_88.Content"),
            ActionRoute = "connection",
        });
        ShowHub("connection");
        _dashboardFailureVisible = true;
        for (var attempt = 0; DialogXamlRoot is null && !_isShuttingDown && attempt < 10; attempt++)
            await Task.Delay(50);
        if (DialogXamlRoot is not { } root)
        {
            _dashboardFailureVisible = false;
            return;
        }
        var retryRequested = false;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = LocalizationHelper.GetString("ChatDashboardButton.Content"),
                Content = message,
                PrimaryButtonText = retry is null ? "" : LocalizationHelper.GetString("Onboarding_Retry"),
                CloseButtonText = LocalizationHelper.GetString("ActivityCloseButton.Content"),
            };
            retryRequested = await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally { _dashboardFailureVisible = false; }
        if (retryRequested && !_isShuttingDown)
            retry?.Invoke();
    }

    public void PrewarmChat(ChatWindowRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_chatWindow is not null || _isShuttingDown)
        {
            return;
        }

        _chatWindow = new ChatWindow(request.GatewayUrl, request.GatewayToken);
        _callbacks.ApplyTheme(_chatWindow);
    }

    public void ShowChat(ChatWindowRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_isShuttingDown)
        {
            return;
        }

        if (_chatWindow is null)
        {
            _chatWindow = new ChatWindow(request.GatewayUrl, request.GatewayToken);
            _callbacks.ApplyTheme(_chatWindow);
        }

        _chatWindow.RefreshCredentials(request.GatewayUrl, request.GatewayToken);

        if (_chatWindow.Visible)
        {
            _chatWindow.HideNearTray();
            return;
        }

        var window = _chatWindow;
        _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!_isShuttingDown && ReferenceEquals(_chatWindow, window))
            {
                try
                {
                    window.ShowNearTrayAnimated();
                }
                catch (Exception ex)
                {
                    Logger.Warn($"ShowChat deferred show failed: {ex.Message}");
                }
            }
        });
    }

    public void ResetChatForCredentialChange()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _chatWindow?.ForceClose();
        _chatWindow = null;
    }

    public void ShowCanvas(CanvasWindowRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_isShuttingDown)
        {
            request.Dispatch(tag => ShowHub(tag));
        }
    }

    public void ShowHub(string? navigateTo = null, bool activate = true)
        => ShowHubCore(navigateTo, activate);

    public async Task ShowNativeSetupAsync(SetupNativeCompletion completion, CancellationToken ct)
    {
        var request = new SetupNativeNavigationRequest(completion);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var active = _callbacks.GetGatewayRegistry()?.GetActive();
            if (active is null || active.Id != completion.Verification.GatewayId ||
                GatewayDashboardBinding.Capture(active) != completion.Verification.EndpointBinding)
                throw new SetupNativeOwnershipException();
            var manager = _callbacks.GetConnectionManager();
            var client = manager?.OperatorClient;
            if (client?.IsConnectedToGateway == true &&
                manager?.CurrentSnapshot.OperatorState == RoleConnectionState.Connected)
            {
                request.GetConnectedClient(_callbacks.GetGatewayRegistry(), manager);
                break;
            }
            await Task.Delay(100, ct);
        }
        ct.ThrowIfCancellationRequested();
        if (_isShuttingDown)
            throw new InvalidOperationException("The application is shutting down.");
        Window window;
        if (request.WorkspaceDestination is { } destination)
        {
            ShowWorkspace(destination, activate: false, preserveCurrent: false, nativeRequest: request);
            if (_workspaceWindow is not { } workspace)
                throw new InvalidOperationException("The native window is unavailable.");
            await workspace.WaitForNativeSetupAsync(request, ct);
            window = workspace;
        }
        else
        {
            ShowCompanion(request.PageTag, activate: false, nativeRequest: request);
            if (_hubWindow is not { } hub)
                throw new InvalidOperationException("The native window is unavailable.");
            await hub.WaitForNativeSetupAsync(request, ct);
            window = hub;
        }
        ct.ThrowIfCancellationRequested();
        request.GetConnectedClient(_callbacks.GetGatewayRegistry(), _callbacks.GetConnectionManager());
        if (SetupDashboardLiveFacts.Capture(_callbacks.GetConnectionManager()) is { } facts &&
            !facts.Matches(completion.Verification))
            throw new SetupNativeOwnershipException();
        if (_isShuttingDown || window is WorkspaceWindow { IsClosed: true } or HubWindow { IsClosed: true })
            throw new InvalidOperationException("The native window closed.");
        ct.ThrowIfCancellationRequested();
        _lastActiveMainWindow = window;
        window.Activate();
    }

    public async Task ShowNativeSetupFailureAsync(SetupNativeLaunchFailure failure, Action? retry)
    {
        var title = LocalizationHelper.GetString("Onboarding_Ready_LaunchFailedTitle");
        var message = LocalizationHelper.GetString("Onboarding_Ready_Launch" + failure);
        _callbacks.GetAppNotificationService()?.Show(new AppNotification
        {
            Id = SetupNativeHandoffLauncher.FailureNotificationId,
            Title = title,
            Message = message,
            Severity = AppNotificationSeverity.Error,
            Source = "setup",
            DedupeKey = SetupNativeHandoffLauncher.FailureNotificationId,
            ActionRoute = "connection",
        });
        if (_nativeSetupFailureVisible) return;
        _nativeSetupFailureVisible = true;
        var retryRequested = false;
        try
        {
            ShowHub("connection");
            for (var attempt = 0; DialogXamlRoot is null && !_isShuttingDown && attempt < 10; attempt++)
                await Task.Delay(50);
            if (DialogXamlRoot is not { } root)
            {
                Logger.Error("Native setup destination failed; the persistent notification is available in Connection.");
                return;
            }
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = title,
                Content = message,
                CloseButtonText = LocalizationHelper.GetString("Onboarding_Ready_Close"),
                PrimaryButtonText = failure == SetupNativeLaunchFailure.Unavailable && retry is not null
                    ? LocalizationHelper.GetString("Onboarding_Ready_Retry") : "",
            };
            retryRequested = await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally { _nativeSetupFailureVisible = false; }
        if (retryRequested && !_isShuttingDown) retry?.Invoke();
    }

    private void ShowHubCore(string? navigateTo, bool activate)
    {
        if (_isShuttingDown)
        {
            return;
        }

        WorkspaceNavigation.Dispatch(navigateTo, destination =>
        {
            if (_callbacks.RequiresSetup())
            {
                AsyncEventHandlerGuard.Run(
                    ShowOnboardingAsync,
                    new AppLogger(),
                    nameof(ShowOnboardingAsync));
                return;
            }

            ShowWorkspace(destination, activate, preserveCurrent: navigateTo is null or "hub");
        }, tag => ShowCompanion(tag, activate));
    }

    private void ShowCompanion(string navigateTo, bool activate, SetupNativeNavigationRequest? nativeRequest = null)
    {
        if (_hubWindow is null || _hubWindow.IsClosed)
        {
            var appState = _callbacks.GetAppState();
            var notificationService = _callbacks.GetAppNotificationService();
            if (appState is null || notificationService is null)
            {
                return;
            }

            var settings = _callbacks.GetSettings();
            if (settings is null)
            {
                return;
            }

            _hubWindow = new HubWindow();
            _callbacks.ApplyTheme(_hubWindow);
            _hubWindow.AppModel = appState;
            _hubWindow.BindAppNotifications(notificationService);
            _hubWindow.ApplyNavPaneState(settings);
            _hubWindow.OpenSetupAction = () => _ = ShowOnboardingAsync();
            _hubWindow.OpenConnectionStatusAction = ShowConnectionStatus;
            _hubWindow.OpenVoiceAction = () => ShowHub("voice");
            _hubWindow.ConnectionManager = _callbacks.GetConnectionManager();
            _hubWindow.GatewayRegistry = _callbacks.GetGatewayRegistry();
            _hubWindow.ConnectAction = _callbacks.Connect;
            _hubWindow.DisconnectAction = _callbacks.Disconnect;
            _hubWindow.ReconnectAction = _callbacks.Connect;
            var nodeService = _callbacks.GetNodeService();
            if (nodeService is not null)
            {
                _hubWindow.NodeIsConnected = nodeService.IsConnected;
                _hubWindow.NodeIsPaired = nodeService.IsPaired;
                _hubWindow.NodeIsPendingApproval = nodeService.IsPendingApproval;
                _hubWindow.NodeShortDeviceId = nodeService.ShortDeviceId;
                _hubWindow.NodeFullDeviceId = nodeService.FullDeviceId;
            }
            _hubWindow.VoiceServiceInstance = _callbacks.GetVoiceService();
            _hubWindow.SettingsSaved += _callbacks.SettingsSaved;
            _hubWindow.PendingChatSessionKey = _callbacks.GetPendingChatSessionKey();
            _hubWindow.Closed += OnHubClosed;
            _hubWindow.Activated += OnMainWindowActivated;
            _hubWindow.BindToAppState();
            if (nativeRequest is null) _hubWindow.NavigateToDefault();
        }

        if (nativeRequest is not null)
        {
            _hubWindow.NavigateTo(nativeRequest);
        }
        else if (navigateTo == "command-center")
        {
            _hubWindow.OpenCommandCenter();
        }
        else if (navigateTo is not null)
        {
            _hubWindow.NavigateTo(navigateTo);
        }

        if (activate)
        {
            _lastActiveMainWindow = _hubWindow;
            _ = ActivateHubWhenReadyAsync(_hubWindow);
        }
        else
        {
            try
            {
                if (_hubWindow.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter &&
                    presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
                {
                    presenter.Restore(activateWindow: false);
                }
                _hubWindow.AppWindow.Show(activateWindow: false);
            }
            catch (Exception ex)
            {
                Logger.Debug($"WindowManager: Failed to show hub window without activation before tray menu: {ex.Message}");
            }
        }
    }

    private void ShowWorkspace(
        WorkspaceDestination destination, bool activate, bool preserveCurrent,
        SetupNativeNavigationRequest? nativeRequest = null)
    {
        if (_workspaceWindow is null || _workspaceWindow.IsClosed)
        {
            if (_callbacks.GetAppState() is not { } state ||
                _callbacks.GetAppNotificationService() is not { } notifications)
            {
                Logger.Warn("[WindowManager] Workspace cannot open before application services are ready.");
                return;
            }

            _workspaceWindow = new WorkspaceWindow(state, notifications,
                tag => ShowHub(tag), ShowConnectionStatus);
            _callbacks.ApplyTheme(_workspaceWindow);
            _workspaceWindow.Closed += OnWorkspaceClosed;
            _workspaceWindow.Activated += OnMainWindowActivated;
        }

        if (nativeRequest is not null)
        {
            nativeRequest.RequireWorkspaceDestination(destination);
            _workspaceWindow.NavigateNativeSetup(nativeRequest);
        }
        else if (!preserveCurrent)
        {
            if (destination.Page == WorkspacePageId.Home &&
                _callbacks.GetPendingChatSessionKey() is { Length: > 0 } sessionKey)
                _workspaceWindow.SelectSession(sessionKey);
            else
                _workspaceWindow.Navigate(destination);
        }
        if (_workspaceWindow.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter &&
            presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
            presenter.Restore(activate);
        if (activate)
        {
            _lastActiveMainWindow = _workspaceWindow;
            _workspaceWindow.Activate();
        }
        else
            _workspaceWindow.AppWindow.Show(activateWindow: false);
    }

    private void OnWorkspaceClosed(object sender, WindowEventArgs args)
    {
        if (sender is WorkspaceWindow workspace)
        {
            workspace.Closed -= OnWorkspaceClosed;
            workspace.Activated -= OnMainWindowActivated;
        }
        if (ReferenceEquals(sender, _lastActiveMainWindow))
            _lastActiveMainWindow = null;
        if (ReferenceEquals(sender, _workspaceWindow))
            _workspaceWindow = null;
    }

    private void OnMainWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated && sender is Window window)
            _lastActiveMainWindow = window;
    }

    private async Task ActivateHubWhenReadyAsync(HubWindow hub)
    {
        try
        {
            await hub.WaitForCurrentContentReadyAsync();
            if (!_isShuttingDown && ReferenceEquals(_hubWindow, hub) && !hub.IsClosed)
            {
                hub.Activate();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Hub window readiness activation failed: {ex.Message}");
        }
    }

    private void OnHubClosed(object sender, WindowEventArgs args)
    {
        if (sender is not HubWindow hub)
        {
            return;
        }

        hub.SettingsSaved -= _callbacks.SettingsSaved;
        hub.Closed -= OnHubClosed;
        hub.Activated -= OnMainWindowActivated;
        if (ReferenceEquals(hub, _lastActiveMainWindow))
            _lastActiveMainWindow = null;
        if (ReferenceEquals(_hubWindow, hub))
        {
            _hubWindow = null;
            ResetNavigationScope();
        }
    }

    private void ResetNavigationScope()
    {
        try
        {
            _callbacks.GetPageActivator()?.Reset();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[WindowManager] Navigation scope reset on hub close failed: {ex.Message}");
        }
    }

    public void ShowConnectionStatus()
    {
        if (_isShuttingDown)
        {
            return;
        }

        if (_connectionStatusWindow is { IsClosed: false })
        {
            if (_connectionStatusWindow.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter &&
                presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
                presenter.Restore();
            _connectionStatusWindow.Activate();
            return;
        }

        var registry = _callbacks.GetGatewayRegistry();
        var manager = _callbacks.GetConnectionManager();
        if (registry is null || manager is null)
        {
            return;
        }

        _connectionStatusWindow = new ConnectionStatusWindow(
            manager.Diagnostics,
            registry,
            manager);
        _connectionStatusWindow.Closed += OnConnectionStatusClosed;
        _callbacks.ApplyTheme(_connectionStatusWindow);
        if (ActiveHubWindow is { } owner)
        {
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                owner.AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
            var size = _connectionStatusWindow.AppWindow.Size;
            size.Width = Math.Min(size.Width, Math.Max(1, area.Width - 32));
            size.Height = Math.Min(size.Height, Math.Max(1, area.Height - 32));
            _connectionStatusWindow.AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(
                Math.Max(area.X, area.X + area.Width - size.Width - 16),
                Math.Clamp(owner.AppWindow.Position.Y + 48, area.Y, Math.Max(area.Y, area.Y + area.Height - size.Height)),
                size.Width, size.Height));
        }
        _connectionStatusWindow.Activate();
    }

    private void OnConnectionStatusClosed(object sender, WindowEventArgs args)
    {
        if (sender is not ConnectionStatusWindow window)
        {
            return;
        }

        window.Closed -= OnConnectionStatusClosed;
        if (ReferenceEquals(_connectionStatusWindow, window))
        {
            _connectionStatusWindow = null;
        }
    }

    public async Task ShowOnboardingAsync()
    {
        await EnsureSetupWindowAsync(
            startAtGatewayInstalledMilestone: false,
            localAiRecoveryTarget: null);
    }

    public Task ShowLocalAiModelSetupAsync() =>
        _callbacks.GetGatewayRegistry()?.GetActive() is
            { NativePackageFamilyName: not null,
              NativeRuntimeContract: OpenClaw.Connection.NativeGateway.NativeGatewayPackageClient.IsolatedContract }
                ? ShowLocalAiSetupAsync() : ShowOnboardingAsync();

    public async Task ShowLocalAiSetupAsync()
    {
        if (_callbacks.GetGatewayRegistry()?.GetActive() is
            { NativePackageFamilyName: not null,
              NativeRuntimeContract: OpenClaw.Connection.NativeGateway.NativeGatewayPackageClient.IsolatedContract } native)
        {
            var (window, created) = await EnsureSetupWindowAsync(
                startAtGatewayInstalledMilestone: true, localAiRecoveryTarget: null);
            if (created && window is { IsClosed: false })
                window.TryNavigateToExistingNativeLocalAi(native);
            return;
        }
        var resolution = await ResolveLocalAiSetupRouteAsync();
        if (resolution.Route == LocalAiSetupRoute.Provision)
        {
            Logger.Info("Local AI recovery requires an existing app-managed gateway; opening full setup");
            await ShowOnboardingAsync();
            return;
        }
        if (resolution.Route == LocalAiSetupRoute.Blocked ||
            resolution.RecoveryTarget is null)
        {
            Logger.Warn("Local AI setup could not safely identify one existing app-managed gateway");
            _callbacks.GetAppNotificationService()?.Show(new AppNotification
            {
                Id = $"local-ai-setup-owner-{Guid.NewGuid():N}",
                Title = "Local AI setup needs attention",
                Message = "OpenClaw could not safely identify the managed WSL gateway. Review Connection settings before retrying setup.",
                Severity = AppNotificationSeverity.Warning,
                Source = "local-ai",
                DedupeKey = "local-ai-setup-owner",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            ShowHub("connection");
            return;
        }

        await ShowLocalAiSetupRecoveryAsync(resolution.RecoveryTarget);
    }

    private async Task<LocalAiSetupResolution> ResolveLocalAiSetupRouteAsync()
    {
        try
        {
            return await new LocalAiSetupRouteResolver(_callbacks.GetGatewayRegistry,
                AppIdentity.ResolveRoamingDataDirectory(), AppIdentity.ResolveSetupLocalDataDirectory(),
                AppIdentity.SetupDistroName).ResolveAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Local AI recovery gateway inspection failed: {ex.Message}");
            return new(LocalAiSetupRoute.Blocked);
        }
    }

    private ISetupLocalAiHost CreateLocalAiSetupHost() => new SetupLocalAiHost(
        ResolveLocalAiSetupRouteAsync, _callbacks.GetGatewayRegistry, _callbacks.GetLocalAiRuntime,
        ct => new LocalAiManifestStore(new(AppIdentity.ResolveSetupLocalDataDirectory())).LoadAsync(ct),
        LocalAiInstallationObservation.InspectAsync,
        ct => Task.Run(() => new OpenClaw.Shared.Inference.CudaHostHardwareProbe().Probe(), ct).WaitAsync(ct),
        () => new LocalAiGatewayProviderCoordinator(new WslExeCommandRunner(new AppLogger()),
            new LocalAiGatewayDistroResolver(_callbacks.GetGatewayRegistry()), new AppLogger()),
          ReconcileSetupConnectionAsync, NotifySetupRegistryRecovery,
          _callbacks.GetLocalAiGatewayLifecycle?.Invoke());

    private async Task ReconcileSetupConnectionAsync(GatewayRecord? before, GatewayRecord? after)
    {
        var manager = _callbacks.GetConnectionManager();
        if (manager is null) return;
        var snapshot = manager.CurrentSnapshot;
        var currentRegistry = _callbacks.GetGatewayRegistry();
        var currentActive = currentRegistry?.GetActive();
        if (snapshot.GatewayId is null || before is null ||
            snapshot.GatewayId != before.Id || snapshot.GatewayUrl != before.Url ||
            (currentActive is null) != (after is null) ||
            currentActive is not null && after is not null &&
            (currentActive with { LastConnected = null }) != (after with { LastConnected = null }))
            return;
        var reset = after is null ||
            (before with { LastConnected = null }) != (after with { LastConnected = null }) ||
            snapshot.GatewayId != after.Id;
        if (!reset && after is not null && manager.OperatorClient?.AuthenticatedSigningDeviceId is { } signing &&
            currentRegistry is { } registry)
        {
            var path = registry.GetIdentityDirectory(after.Id);
            try { SetupCompletionAuthority.RequirePersistedIdentity(path, SetupCompletionAuthority.CaptureIdentity(path, signing)); }
            catch (SetupNativeOwnershipException) { reset = true; }
        }
        if (reset) await manager.DisconnectIfCurrentAsync(snapshot);
    }

    private void NotifySetupRegistryRecovery()
    {
        Logger.Error("Setup registry settlement needs attention. Reopen the app to reload saved gateways; unsaved changes were not committed.");
        _dispatcherQueue.TryEnqueue(() => _callbacks.GetAppNotificationService()?.Show(new AppNotification
        {
            Id = "setup-registry-recovery",
            Title = LocalizationHelper.GetString("Onboarding_RegistryRecovery_Title"),
            Message = LocalizationHelper.GetString("Onboarding_RegistryRecovery_Message"),
            Severity = AppNotificationSeverity.Error, Source = "setup", ActionRoute = "connection",
        }));
    }

    private async Task ShowLocalAiSetupRecoveryAsync(LocalAiRecoveryTarget target)
    {
        var (setupWindow, created) = await EnsureSetupWindowAsync(
            startAtGatewayInstalledMilestone: false,
            localAiRecoveryTarget: target);
        if (!_isShuttingDown && !created && setupWindow is { IsClosed: false })
        {
            Logger.Info("Setup window already open; leaving current setup page visible to avoid interrupting active setup");
        }
    }

    public async Task ShowGatewayWizardAsync()
    {
        var (setupWindow, created) = await EnsureSetupWindowAsync(
            startAtGatewayInstalledMilestone: true,
            localAiRecoveryTarget: null);
        if (!_isShuttingDown && !created && setupWindow is { IsClosed: false })
        {
            if (setupWindow.TryNavigateToGatewayInstalledMilestone())
            {
                Logger.Info("Setup window already open; switched to direct OpenClaw onboard handoff");
            }
            else
            {
                Logger.Info("Setup window already open; leaving current setup page visible to avoid interrupting active setup");
            }
        }
    }

    private void NotifyIncompleteNativeConnection()
    {
        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            _callbacks.GetAppNotificationService()?.Show(new AppNotification
            {
                Title = LocalizationHelper.GetString("Onboarding_NativeConnection_Title"),
                Message = LocalizationHelper.GetString("Onboarding_Native_Failed"),
                Severity = AppNotificationSeverity.Error,
                Source = "connection",
                DedupeKey = "native-setup-incomplete-commit",
                Persistence = AppNotificationPersistence.Persistent
            });
            if (!_isShuttingDown)
                ShowHub("connection");
        }))
            Logger.Error("Native setup connection needs attention, but its notification could not be dispatched.");
    }

    private async Task<(SetupWindow? Window, bool Created)> EnsureSetupWindowAsync(
        bool startAtGatewayInstalledMilestone,
        LocalAiRecoveryTarget? localAiRecoveryTarget)
    {
        if (_isShuttingDown || _callbacks.GetSettings() is null)
        {
            return (null, false);
        }

        while (_setupWindow is not null)
        {
            var existingSetupWindow = _setupWindow;
            await existingSetupWindow.WaitForInitialContentReadyAsync();
            if (_isShuttingDown)
            {
                return (null, false);
            }

            if (!existingSetupWindow.IsClosed)
            {
                if (ReferenceEquals(_setupWindow, existingSetupWindow))
                {
                    existingSetupWindow.BringToFrontForSetupLaunch();
                }
                return (existingSetupWindow, false);
            }

            await existingSetupWindow.CleanupCompleted;
            if (ReferenceEquals(_setupWindow, existingSetupWindow))
            {
                _setupWindow = null;
            }

            if (_isShuttingDown)
            {
                return (null, false);
            }
        }

        if (_isShuttingDown)
        {
            return (null, false);
        }

        SetupWindow? setupWindow = null;
        try
        {
            var settingsWriter = new SetupSettingsWriter(_callbacks.GetSettingsStore?.Invoke()
                ?? throw new InvalidOperationException("The settings owner is unavailable. Reopen the app before setup."));
            setupWindow = new SetupWindow(
                startAtGatewayInstalledMilestone: startAtGatewayInstalledMilestone,
                startAtLocalAiRecoveryReview: localAiRecoveryTarget is not null,
                dataDir: AppIdentity.ResolveRoamingDataDirectory(),
                localDataDir: AppIdentity.ResolveSetupLocalDataDirectory(),
                distroNameOverride: AppIdentity.SetupDistroName,
                gatewayPortOverride: AppIdentity.SetupGatewayPort,
                localAiRecoveryGatewayId: localAiRecoveryTarget?.GatewayId,
                localAiRecoveryDistroName: localAiRecoveryTarget?.DistroName,
                localAiRecoveryGatewayPort: localAiRecoveryTarget?.GatewayPort,
                localAiRecoveryModelId: localAiRecoveryTarget?.ModelCatalogId,
                localAiRecoveryRequestedPort: localAiRecoveryTarget?.RequestedLocalAiPort,
                localAiHost: CreateLocalAiSetupHost(),
                connectionManager: _callbacks.GetConnectionManager(),
                publishNativeCompletion: _callbacks.PublishNativeCompletion,
                applyNativeStartup: _callbacks.ApplyNativeStartup,
                startupRegistrationAllowed: !AppIdentity.IsIsolated,
                persistChoices: (config, startup, onlyStartup) => settingsWriter.Apply(config.CreatePatch(startup, onlyStartup)),
                nativeConnectionHost: _callbacks.GetGatewayDirectConnectService() is { } directConnect &&
                    _callbacks.GetGatewayRegistry() is { } registry
                    ? new SetupNativeConnectionHost(directConnect, registry, new AppLogger(), NotifyIncompleteNativeConnection)
                    : null,
                commandLineArgs: SetupWindowArgumentProjection.Project(
                    _callbacks.GetStartupArgs(),
                    _callbacks.IsDeepLinkArg,
                    Environment.ProcessId))
            {
                Title = AppIdentity.DecorateWindowTitle("OpenClaw Setup"),
            };
            _setupWindow = setupWindow;
            _callbacks.ApplyTheme(setupWindow);
            setupWindow.AdvancedSetupRequested += _callbacks.AdvancedSetupRequested;
            setupWindow.SetupCompleted += _callbacks.SetupCompleted;
            setupWindow.Closed += OnSetupClosed;
            await setupWindow.WaitForInitialContentReadyAsync();
            if (!_isShuttingDown && ReferenceEquals(_setupWindow, setupWindow) && !setupWindow.IsClosed)
            {
                setupWindow.BringToFrontForSetupLaunch();
                Logger.Info("Opened tray-hosted setup window");
            }

            return (setupWindow, true);
        }
        catch (Exception ex)
        {
            if (setupWindow is not null)
            {
                setupWindow.AdvancedSetupRequested -= _callbacks.AdvancedSetupRequested;
                setupWindow.SetupCompleted -= _callbacks.SetupCompleted;
                setupWindow.Closed -= OnSetupClosed;
                try
                {
                    if (!setupWindow.IsClosed)
                    {
                        setupWindow.Close();
                    }
                    await setupWindow.CleanupCompleted;
                }
                catch (Exception cleanupException)
                {
                    Logger.Warn($"Failed to clean up setup window after open failure: {cleanupException.Message}");
                }
                finally
                {
                    if (ReferenceEquals(_setupWindow, setupWindow))
                    {
                        _setupWindow = null;
                    }
                }
            }

            Logger.Error($"Failed to open setup window: {ex}");
            return (null, false);
        }
    }

    private void OnSetupClosed(object sender, WindowEventArgs args)
    {
        if (sender is not SetupWindow setupWindow)
        {
            return;
        }

        setupWindow.AdvancedSetupRequested -= _callbacks.AdvancedSetupRequested;
        setupWindow.SetupCompleted -= _callbacks.SetupCompleted;
        setupWindow.Closed -= OnSetupClosed;
        AsyncEventHandlerGuard.Run(
            () => CompleteSetupCloseAsync(setupWindow),
            new AppLogger(),
            nameof(OnSetupClosed));
    }

    private async Task CompleteSetupCloseAsync(SetupWindow setupWindow)
    {
        await setupWindow.CleanupCompleted;
        if (ReferenceEquals(_setupWindow, setupWindow))
        {
            _setupWindow = null;
        }
    }

    public void CloseSetup()
    {
        if (!_isShuttingDown)
        {
            _setupWindow?.Close();
        }
    }

    public void ApplyThemeToOpenWindows()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _callbacks.ApplyTheme(_keepAliveWindow);
        _callbacks.ApplyTheme(_hubWindow);
        _callbacks.ApplyTheme(_workspaceWindow);
        _callbacks.ApplyTheme(_chatWindow);
        _callbacks.ApplyTheme(_connectionStatusWindow);
        _callbacks.ApplyTheme(_setupWindow);
    }

    public void UpdateHubTitleBarStatus(
        GatewayConnectionSnapshot snapshot,
        ConnectionStatus status)
    {
        if (!_isShuttingDown)
        {
            _hubWindow?.UpdateTitleBarStatus(snapshot, status);
            _workspaceWindow?.UpdateConnectionStatus(snapshot, status);
        }
    }

    public void RefreshHubDiagnosticsNavigationVisibility()
    {
        if (!_isShuttingDown)
        {
            _hubWindow?.RefreshDiagnosticsNavVisibility();
        }
    }

    public void SetPendingChatSessionKey(string? sessionKey)
    {
        if (!_isShuttingDown)
            _workspaceWindow?.ChatPage.QueueSession(sessionKey);
    }

    public void ShowHubChatAndStartVoice()
    {
        if (_isShuttingDown)
        {
            return;
        }

        ShowHub("chat");
        _workspaceWindow?.ChatPage.TriggerAutoStartVoice();
    }

    public IntPtr GetHubWindowHandle() =>
        ActiveHubWindow is { } window
            ? WinRT.Interop.WindowNative.GetWindowHandle(window)
            : IntPtr.Zero;

    public IntPtr GetOnboardingWindowHandle() =>
        !_isShuttingDown && _setupWindow is { IsClosed: false }
            ? WinRT.Interop.WindowNative.GetWindowHandle(_setupWindow)
            : IntPtr.Zero;

    public Task CloseForShutdownAsync()
    {
        BeginShutdown();
        return _closeForShutdownTask ??= CloseOwnedWindowsAsync();
    }

    private async Task CloseOwnedWindowsAsync()
    {
        List<Exception>? failures = null;

        if (_workspaceWindow is not null)
        {
            _workspaceWindow.Closed -= OnWorkspaceClosed;
            _workspaceWindow.Activated -= OnMainWindowActivated;
            TryClose("Workspace window", _workspaceWindow.Close, ref failures);
            _workspaceWindow = null;
        }

        TryClose("Chat window", () => _chatWindow?.ForceClose(), ref failures);
        _chatWindow = null;

        var setupWindow = _setupWindow;
        if (setupWindow is not null)
        {
            setupWindow.AdvancedSetupRequested -= _callbacks.AdvancedSetupRequested;
            setupWindow.SetupCompleted -= _callbacks.SetupCompleted;
            setupWindow.Closed -= OnSetupClosed;
            if (!setupWindow.IsClosed)
            {
                try
                {
                    setupWindow.Close();
                }
                catch (Exception ex)
                {
                    (failures ??= []).Add(
                        new InvalidOperationException("Setup window shutdown failed.", ex));
                }
            }

            if (setupWindow.IsClosed)
            {
                try
                {
                    await setupWindow.CleanupCompleted;
                }
                catch (Exception ex)
                {
                    (failures ??= []).Add(
                        new InvalidOperationException("Setup window cleanup failed.", ex));
                }
            }

            if (ReferenceEquals(_setupWindow, setupWindow))
            {
                _setupWindow = null;
            }
        }

        if (_connectionStatusWindow is not null)
        {
            _connectionStatusWindow.Closed -= OnConnectionStatusClosed;
            TryClose("Connection status window", _connectionStatusWindow.Close, ref failures);
            _connectionStatusWindow = null;
        }

        if (_hubWindow is not null)
        {
            var hub = _hubWindow;
            hub.SettingsSaved -= _callbacks.SettingsSaved;
            hub.Closed -= OnHubClosed;
            hub.Activated -= OnMainWindowActivated;
            TryClose("Hub window", hub.Close, ref failures);
            _hubWindow = null;
            ResetNavigationScope();
        }

        _lastActiveMainWindow = null;
        TryClose("Runtime anchor window", () => _keepAliveWindow?.Close(), ref failures);
        _keepAliveWindow = null;

        if (failures is { Count: > 0 })
        {
            throw new AggregateException("One or more owned windows failed to close.", failures);
        }

        Logger.Info("[WindowManager] Closed owned windows");
    }

    private static void TryClose(
        string surface,
        Action close,
        ref List<Exception>? failures)
    {
        try
        {
            close();
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(
                new InvalidOperationException($"{surface} shutdown failed.", ex));
        }
    }
}
