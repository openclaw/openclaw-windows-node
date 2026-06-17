using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Controls.Primitives;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClawTray.Dialogs;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using OpenClaw.Connection;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Updatum;
using WinUIEx;
using SetupCompletedEventArgs = OpenClaw.SetupEngine.UI.SetupCompletedEventArgs;
using SetupWindow = OpenClaw.SetupEngine.UI.SetupWindow;

namespace OpenClawTray;

public partial class App : Application, OpenClawTray.Services.IAppCommands
{
    internal static readonly UpdatumManager AppUpdater = new("openclaw", "openclaw-windows-node")
    {
        FetchOnlyLatestRelease = true,
        InstallUpdateSingleFileExecutableName = "OpenClaw.Tray.WinUI",
    };

    private TrayIcon? _trayIcon;
    private GatewayConnectionManager? _connectionManager;
    private GatewayRegistry? _gatewayRegistry;
    private OpenClawTray.Chat.OpenClawChatCoordinator? _chatCoordinator;
    /// <summary>
    /// Cached reference to the most recently constructed local-setup engine. Used by
    /// <see cref="OnPairingStatusChanged"/> to suppress the "copy pairing command" toast
    /// during Phase 14 auto-pair (Bug #2, manual test 2026-05-05). Null when no local
    /// setup has run in this app lifetime.
    /// </summary>
    /// <summary>The persistent gateway client. Used by the onboarding wizard for RPC calls.</summary>
    public IOperatorGatewayClient? GatewayClient => _connectionManager?.OperatorClient;
    public GatewayRegistry? Registry => _gatewayRegistry;
    public GatewayConnectionManager? ConnectionManager => _connectionManager;
    internal SettingsManager Settings => _settings ?? throw new InvalidOperationException("Settings are not initialized.");

    /// <summary>The active hub window, exposed so pages can obtain an HWND for file pickers.</summary>
    internal Microsoft.UI.Xaml.Window? ActiveHubWindow => _hubWindow;
    /// <summary>The current voice service instance (node or standalone).</summary>
    internal VoiceService? VoiceService => _nodeService?.VoiceService ?? _standaloneVoiceService;
    /// <summary>The full device ID of the local node service (if running).</summary>
    internal string? NodeFullDeviceId => _nodeService?.FullDeviceId;

    public OpenClawTray.Chat.OpenClawChatDataProvider? ChatProvider => _chatCoordinator?.Provider;

    /// <summary>
    /// Raised after the tray-wide settings have been saved (either via the
    /// SettingsPage Save button or a direct toggle from the tray menu).
    /// Subscribers can refresh UI that depends on a setting (e.g. switching
    /// the chat surface between native chat and WebView2).
    /// </summary>
    public event EventHandler? SettingsChanged;
    public event EventHandler? ChatProviderChanged;

    /// <summary>
    /// Ensures the managed SSH tunnel is started using the current settings.
    /// Used by connection settings when the user picks the SSH topology.
    /// </summary>
    public void EnsureSshTunnelStarted()
    {
        if (_sshTunnelService == null || _settings == null)
            return;

        if (!_settings.UseSshTunnel)
        {
            _sshTunnelService.ResetNotConfigured();
            return;
        }

        var includeBrowserProxyForward =
            _settings.NodeBrowserProxyEnabled &&
            SshTunnelCommandLine.CanForwardBrowserProxyPort(_settings.SshTunnelRemotePort, _settings.SshTunnelLocalPort);
        if (_settings.NodeBrowserProxyEnabled && !includeBrowserProxyForward)
        {
            Logger.Warn("SSH tunnel browser proxy forward disabled because the derived port would be invalid");
        }

        _sshTunnelService.EnsureStarted(
            _settings.SshTunnelUser,
            _settings.SshTunnelHost,
            _settings.SshTunnelRemotePort,
            _settings.SshTunnelLocalPort,
            includeBrowserProxyForward);
    }

    /// <summary>
    /// Returns the HWND of the active onboarding window, or IntPtr.Zero if none.
    /// Used by onboarding pages that need to host file pickers / dialogs.
    /// </summary>
    public IntPtr GetOnboardingWindowHandle()
        => _setupWindow is null
            ? IntPtr.Zero
            : WinRT.Interop.WindowNative.GetWindowHandle(_setupWindow);

    /// <summary>
    /// Returns the HWND of the Hub window, or IntPtr.Zero if it isn't open.
    /// Used by pages hosted in the Hub that need to parent a file picker
    /// or other Win32-style dialog. Pages should not hold a reference to
    /// the HubWindow directly (single-app-model rule); they call this
    /// when they need the handle and discard it afterwards.
    /// Guards against the close-window race where `_hubWindow != null`
    /// but the window is mid-teardown — every other call site in this
    /// file pairs the null check with `!IsClosed` (Hanselman v2 #4).
    /// </summary>
    public IntPtr GetHubWindowHandle()
        => _hubWindow != null && !_hubWindow.IsClosed
            ? WinRT.Interop.WindowNative.GetWindowHandle(_hubWindow)
            : IntPtr.Zero;

    private SettingsManager? _settings;
    private ConnectionSettingsSnapshot? _previousSettingsSnapshot;
    private SshTunnelService? _sshTunnelService;
    private GlobalHotkeyService? _globalHotkey;
    private Mutex? _mutex;
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    private AppState? _appState;
    internal AppState? AppState => _appState;
    private GatewayService? _gatewayService;
    private PairingApprovalCoordinator? _pairingApprovalCoordinator;
    private OpenClawTray.Dialogs.PairingApprovalDialog? _pairingApprovalDialog;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _pairingApprovalPollTimer;
    private CancellationTokenSource? _deepLinkCts;
    private bool _isExiting;
    
    /// <summary>
    /// Cached connection status — sole writer is OnManagerStateChanged.
    /// Reads are safe from any thread; derives from the connection manager's state machine.
    /// SSH tunnel errors in EnsureSshTunnelConfigured also write this temporarily (Phase 3 moves tunnel to manager).
    /// </summary>
    private WeakReference<ToggleSwitch>? _connectionToggleRef;
    private bool _suspendConnectionToggleEvent;

    // FrozenDictionary for O(1) case-insensitive notification type → setting lookup — no per-call allocation.
    private static readonly System.Collections.Frozen.FrozenDictionary<string, Func<SettingsManager, bool>> s_notifTypeMap =
        new Dictionary<string, Func<SettingsManager, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            ["health"]    = s => s.NotifyHealth,
            ["urgent"]    = s => s.NotifyUrgent,
            ["reminder"]  = s => s.NotifyReminder,
            ["email"]     = s => s.NotifyEmail,
            ["calendar"]  = s => s.NotifyCalendar,
            ["build"]     = s => s.NotifyBuild,
            ["stock"]     = s => s.NotifyStock,
            ["info"]      = s => s.NotifyInfo,
            ["error"]     = s => s.NotifyUrgent,  // errors follow urgent setting
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // Windows (created on demand)
    private HubWindow? _hubWindow;
    private TrayMenuWindow? _trayMenuWindow;
    private ChatWindow? _chatWindow;
    private ConnectionStatusWindow? _connectionStatusWindow;

    private DiagnosticsClipboardService? _diagnosticsClipboard;
    private ToastService? _toastService;
    
    // Node service (optional, enabled in settings)
    private NodeService? _nodeService;
    // MCP-only app capability — local testing/control, not exposed to gateway
    private AppCapability? _appCapability;
    
    // Keep-alive window to anchor WinUI runtime (prevents GC/threading issues)
    private Window? _keepAliveWindow;
    private SetupWindow? _setupWindow;

    private string[]? _startupArgs;
    private string? _pendingProtocolUri;
    private bool _isPostSetupRestart;
    private string? _postSetupLaunch;
    // OPENCLAW_TRAY_DATA_DIR isolates a test instance: settings, logs, run marker,
    // crash log, exec approvals, and the single-instance mutex name all derive from it.
    private static readonly string? DataDirOverride =
        Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") is { Length: > 0 } v ? v : null;
    private static readonly string DataPath = DataDirOverride
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenClawTray");
    private static readonly string DeepLinkPipeName =
        DeepLinkSecurityPolicy.BuildCurrentUserScopedPipeName(DataPath);
    // Operator/node identity store. In normal installs this is %APPDATA%\OpenClawTray.
    // Isolated test/dev runs set OPENCLAW_TRAY_DATA_DIR to the direct OpenClaw data
    // folder, and SetupEngine/GatewayRegistry write per-gateway identities there.
    private static readonly string IdentityDataPath = DataDirOverride
        ?? Path.Combine(
            Environment.GetEnvironmentVariable("OPENCLAW_TRAY_APPDATA_DIR")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenClawTray");
    private readonly AppCrashLogger _crashLogger = new(Path.Combine(DataPath, "crash.log"));
    private static readonly AppRunMarker s_runMarker = new(Path.Combine(DataPath, "run.marker"));

    public App()
    {
        WaitForRestartSourceIfRequested(Environment.GetCommandLineArgs());
        StartupInputConfigurator.Configure();

        // Language override for localization testing (e.g., OPENCLAW_LANGUAGE=zh-CN)
        var langOverride = Environment.GetEnvironmentVariable("OPENCLAW_LANGUAGE");
        if (!string.IsNullOrEmpty(langOverride))
        {
            // SECURITY: Whitelist known locale codes to prevent locale injection
            string[] allowedLocales = ["en-us", "fr-fr", "nl-nl", "zh-cn", "zh-tw"];
            if (allowedLocales.Contains(langOverride.ToLowerInvariant()))
                LocalizationHelper.SetLanguageOverride(langOverride);
            else
                Logger.Warn($"[App] Ignoring invalid OPENCLAW_LANGUAGE value: {langOverride}");
        }

        InitializeComponent();
        
        s_runMarker.Check();
        s_runMarker.MarkStarted();
        
        // Hook up crash handlers
        this.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    private static bool HasArg(string[] args, string name) =>
        args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    private static string? GetArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static void WaitForRestartSourceIfRequested(string[] args)
    {
        var pidValue = GetArgValue(args, "--wait-for-pid");
        if (!int.TryParse(pidValue, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
            pid <= 0 ||
            pid == Environment.ProcessId)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
                process.WaitForExit(TimeSpan.FromSeconds(60));
        }
        catch (ArgumentException)
        {
            // The source process already exited.
        }
        catch (Exception ex)
        {
            try { Logger.Warn($"Post-setup restart wait for PID {pid} failed: {ex.Message}"); } catch { }
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _crashLogger.Log("UnhandledException", e.Exception);
        e.Handled = true; // Try to prevent crash
    }

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        _crashLogger.Log("DomainUnhandledException", e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _crashLogger.Log("UnobservedTaskException", e.Exception);
        e.SetObserved(); // Prevent crash
    }
    
    private void OnProcessExit(object? sender, EventArgs e)
    {
        s_runMarker.MarkEnded();
        try
        {
            Logger.Info($"Process exiting (ExitCode={Environment.ExitCode})");
        }
        catch { }
    }

    private void OnUiThread(Microsoft.UI.Dispatching.DispatcherQueueHandler action) => _dispatcherQueue?.TryEnqueue(action);

    /// <summary>
    /// Check if the app was launched via protocol activation (MSIX deep link).
    /// In WinUI 3, protocol activation is retrieved via AppInstance, not OnActivated.
    /// </summary>
    private static string? GetProtocolActivationUri()
    {
        try
        {
            var activatedArgs = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activatedArgs.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Protocol
                && activatedArgs.Data is global::Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocolArgs)
            {
                return protocolArgs.Uri?.ToString();
            }
        }
        catch { /* Not activated via protocol, or not packaged */ }
        return null;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args) =>
        AsyncEventHandlerGuard.Run(
            () => OnLaunchedAsync(args),
            new AppLogger(),
            nameof(OnLaunched));

    private async Task OnLaunchedAsync(LaunchActivatedEventArgs args)
    {
        _startupArgs = Environment.GetCommandLineArgs();
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _isPostSetupRestart = HasArg(_startupArgs, "--post-setup-restart");
        _postSetupLaunch = GetArgValue(_startupArgs, "--post-setup-launch");

        // -----------------------------------------------------------------------
        // CLI uninstall path — headless; never shows tray or any windows.
        // Approach: detect in OnLaunched before any UI is created (WinUI3 Main
        // is auto-generated; earliest interception point is OnLaunched).
        // Bypasses the single-instance mutex so the Inno uninstaller can invoke
        // this even while the tray is running.
        // -----------------------------------------------------------------------
        if (_startupArgs.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            await CliUninstallHandler.RunAsync(_startupArgs);
            return; // Environment.Exit called inside; defensive return
        }

        // Check for protocol activation (MSIX packaged apps receive deep links this way)
        string? protocolUri = GetProtocolActivationUri();

        // Single instance check - keep mutex alive for app lifetime.
        // When running with an isolated data dir (tests), suffix the mutex name so
        // the test instance does not collide with the user's regular tray app.
        // String.GetHashCode() is randomized per process since .NET Core 2.1, so
        // two test runs against the same data dir would otherwise pick different
        // mutex names — and `Math.Abs(int.MinValue)` overflows. Use a stable
        // SHA-256 prefix instead.
        // NOTE: The bare "OpenClawTray" mutex name is also referenced by
        // installer.iss `AppMutex=` for install/uninstall race coordination
        // (round 2, Scott #5). The suffixed test-isolation variant is
        // intentionally not covered by AppMutex — production installs only
        // ever use the unsuffixed name.
        var mutexName = "OpenClawTray";
        if (DataDirOverride is not null)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(DataDirOverride));
            mutexName = $"OpenClawTray-{Convert.ToHexString(hash, 0, 4)}";
        }
        _mutex = new Mutex(true, mutexName, out bool createdNew);
        var ownsMutex = createdNew;
        if (!ownsMutex && _isPostSetupRestart)
        {
            try
            {
                Logger.Warn("Post-setup restart found an existing tray mutex after waiting for the old process; waiting briefly for mutex release.");
                ownsMutex = _mutex.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                Logger.Warn("Post-setup restart acquired abandoned tray mutex.");
                ownsMutex = true;
            }
        }

        if (!ownsMutex)
        {
            // Forward deep link args to running instance (command-line or protocol activation)
            var deepLink = protocolUri
                ?? (_startupArgs.Length > 1 && _startupArgs[1].StartsWith("openclaw://", StringComparison.OrdinalIgnoreCase)
                    ? _startupArgs[1] : null)
                ?? (string.Equals(_postSetupLaunch, "chat", StringComparison.OrdinalIgnoreCase)
                    ? "openclaw://chat" : null);
            if (deepLink != null)
            {
                SendDeepLinkToRunningInstance(deepLink);
            }
            Exit();
            return;
        }

        // Store protocol URI for processing after setup
        _pendingProtocolUri = protocolUri;

        // Initialize settings before update check so skip selections can be remembered.
        _settings = new SettingsManager();
        _previousSettingsSnapshot = _settings.ToSettingsData().ToConnectionSnapshot();
        _chatCoordinator = new OpenClawTray.Chat.OpenClawChatCoordinator(
            _settings,
            () => _nodeService,
            new AppLogger(),
            _dispatcherQueue is null
                ? null
                : OpenClawTray.Chat.FunctionalChatHostExtensions.AsPost(_dispatcherQueue));
        DiagnosticsJsonlService.Configure(DataPath);

        // Central observable model + gateway event handler.
        _appState = new AppState(_dispatcherQueue);
        _appState.UpdateInfo = BuildInitialUpdateInfo();
        _gatewayService = new GatewayService(_appState, _dispatcherQueue!);
        _gatewayService.ConnectionStatusChanged += OnGatewayConnectionStatusChanged;
        _gatewayService.AuthenticationFailed += OnGatewayAuthenticationFailed;
        _gatewayService.SessionCommandCompleted += OnGatewaySessionCommandCompleted;
        _gatewayService.NotificationReceived += OnGatewayNotificationReceived;
        _appState.PropertyChanged += OnAppStateChanged;

        _diagnosticsClipboard = new DiagnosticsClipboardService(BuildCommandCenterState);
        _toastService = new ToastService(() => _settings);

        // Inbound pairing approvals: surface a focused dialog + awareness toast when another
        // device/node requests pairing (Mac-parity). Getters are lazy so this can be created
        // before the connection manager / node service exist.
        _pairingApprovalCoordinator = new PairingApprovalCoordinator(
            getClient: () => _connectionManager?.OperatorClient,
            getOwnNodeDeviceId: () => _nodeService?.FullDeviceId,
            isPromptEnabled: () => _settings?.ShowPairingApprovalDialog ?? true,
            logger: new AppLogger());
        _pairingApprovalCoordinator.ApprovalRequested += OnPairingApprovalRequested;
        _pairingApprovalCoordinator.DecisionCompleted += OnPairingDecisionCompleted;
        _gatewayService.PairListsChanged += OnPairListsChanged;

        // Safety-net poll: the gateway broadcasts pair requests with dropIfSlow=true, so a busy
        // socket can silently drop a "device wants to connect" event and the operator would never
        // be prompted. A periodic reconcile recovers any missed request. RefreshFromGatewayAsync
        // no-ops unless connected with approval scope, so this is idle-cheap.
        _pairingApprovalPollTimer = _dispatcherQueue!.CreateTimer();
        _pairingApprovalPollTimer.Interval = TimeSpan.FromSeconds(20);
        _pairingApprovalPollTimer.IsRepeating = true;
        _pairingApprovalPollTimer.Tick += (_, _) => _ = _pairingApprovalCoordinator?.RefreshFromGatewayAsync();
        _pairingApprovalPollTimer.Start();

        DiagnosticsJsonlService.Write("app.start", new
        {
            nodeMode = _settings.EnableNodeMode,
            useSshTunnel = _settings.UseSshTunnel
        });

        // Register URI scheme on first run
        DeepLinkHandler.RegisterUriScheme();

        // Anchor the WinUI runtime so transient windows (UpdateDialog,
        // setup wizard, etc.) don't terminate the process when closed.
        // WinUI 3 Desktop's default DispatcherShutdownMode is
        // OnLastWindowClose — without this override, closing the
        // UpdateDialog on the startup path (when it is the only window)
        // would shut down the WinUI runtime mid-flight and kill the
        // in-progress download/extraction. We still control shutdown
        // explicitly via Application.Exit().
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        // Check for updates before launching. Skip in test instances — no UI dialogs,
        // no network calls, no startup delay.
        if (DataDirOverride is null &&
            Environment.GetEnvironmentVariable("OPENCLAW_SKIP_UPDATE_CHECK") != "1")
        {
            var shouldLaunch = await CheckForUpdatesAsync();
            if (!shouldLaunch)
            {
                Exit();
                return;
            }
        }

        // Register toast activation handler
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;

        _sshTunnelService = new SshTunnelService(new AppLogger());
        _sshTunnelService.TunnelExited += OnSshTunnelExited;

        // Initialize tray icon FIRST (window-less pattern from WinUIEx).
        // The tray is application chrome and must always survive any failure
        // in the onboarding wizard. OnLaunched delegates through a guarded
        // async boundary so onboarding failures are logged instead of escaping
        // before the tray ever initializes.
        InitializeTrayIcon();
        // Apply the user's saved default chat preset (if any) before any chat
        // surface mounts so initial render uses their preferred styling.
        OpenClawTray.Chat.Explorations.ChatExplorationPresetStore.ApplyDefaultIfPresent();
        ShowSurfaceImprovementsTipIfNeeded();

        // Initialize connection manager before setup flow.
        _gatewayRegistry = new GatewayRegistry(SettingsManager.SettingsDirectoryPath);
        _gatewayRegistry.Load();
        var credentialResolver = new CredentialResolver(DeviceIdentityFileReader.Instance);
        var clientFactory = new GatewayClientFactory();
        var appLogger = new AppLogger();
        var diagnostics = new ConnectionDiagnostics();
        var nodeConnector = new NodeConnector(appLogger, diagnostics);
        // Bridge: whenever NodeConnector creates a fresh WindowsNodeClient (initial
        // connect or reconnect), register the node's capabilities on it BEFORE the
        // outbound "connect" handshake runs. Without this hookup the gateway sees
        // the node as having no advertised commands and the agent cannot invoke
        // anything on it. _nodeService may be null at app startup (constructed
        // lazily); when null we no-op and the gateway will see an empty caps list
        // until the next reconnect after _nodeService becomes available.
        nodeConnector.ClientCreated += (_, args) =>
        {
            try
            {
                // A node client was just created (manager auto-start OR setup engine
                // EnsureNodeConnectedAsync). We MUST have a NodeService to register
                // capabilities on this client before the outbound "connect" goes out —
                // see NodeConnector.cs:66. Build it lazily here so we never depend on
                // OnLaunched ordering. EnsureNodeService is
                // idempotent — it returns the existing instance if already built.
                // Without this, _nodeService?.AttachClient below is a silent no-op and
                // the gateway sees the node with caps=0/cmds=0 (regression introduced
                // 2026-05-12 in 62533e2 when capability registration moved to this
                // lazy bridge pattern).
                if (_settings == null)
                {
                    Logger.Warn("[App] NodeConnector.ClientCreated fired before settings were initialized; node may connect without capabilities");
                    diagnostics.Record("node", "WARNING: settings unavailable; cannot initialize NodeService for capability binding");
                }
                else
                {
                    EnsureNodeService(_settings);
                }

                diagnostics.Record("node", $"ClientCreated fired, _nodeService null={_nodeService is null}");
                if (_nodeService == null)
                {
                    Logger.Warn("[App] NodeService unavailable during ClientCreated; node may connect with caps=0/cmds=0");
                    diagnostics.Record("node", "WARNING: NodeService unavailable; cannot bind node capabilities");
                    return;
                }

                _nodeService.AttachClient(args.Client, args.BearerToken);
                WireAppCapabilityHandlers();
                var client = args.Client;
                diagnostics.Record("node", $"After AttachClient: caps={client.Capabilities.Count}, cmds={client.RegisteredCommandCount}");
                if (client.RegisteredCommandCount > 0)
                    diagnostics.Record("node", $"Commands sample: {string.Join(", ", client.RegisteredCommandsSample)}...");
                else
                {
                    Logger.Warn("[App] Node capability binding produced 0 commands before connect");
                    diagnostics.Record("node", "WARNING: 0 commands registered on node client before connect");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[App] NodeConnector.ClientCreated handler failed: {ex.Message}");
                diagnostics.Record("node", $"ClientCreated handler THREW: {ex.Message}");
            }
        };
        // SshTunnelService implements ISshTunnelManager directly — no shim needed
        _connectionManager = new GatewayConnectionManager(
            credentialResolver, clientFactory, _gatewayRegistry, appLogger,
            identityStore: new DeviceIdentityFileStore(appLogger),
            nodeConnector: nodeConnector,
            isNodeEnabled: ShouldInitializeNodeService,
            diagnostics: diagnostics,
            tunnelManager: _sshTunnelService);
        _connectionManager.OperatorClientChanged += OnOperatorClientChanged;
        _connectionManager.StateChanged += OnManagerStateChanged;

        // First-run check (also supports forced onboarding for testing).
        // Wrapped in try/catch so a wizard construction failure cannot tear
        // down the tray; user can retry via the Setup Guide menu item.
        var setupShownDuringStartup = false;
        try
        {
            if ((!_isPostSetupRestart && RequiresSetup(_settings)) ||
                Environment.GetEnvironmentVariable("OPENCLAW_FORCE_ONBOARDING") == "1")
            {
                await ShowOnboardingAsync();
                setupShownDuringStartup = true;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Onboarding failed during launch (tray remains available): {ex}");
        }

        // Ensure NodeService is constructed BEFORE InitializeGatewayClient triggers a
        // NodeConnector connect. The NodeConnector.ClientCreated event subscription
        // above relies on _nodeService being non-null to register capabilities on the
        // new WindowsNodeClient. If we don't pre-construct here, the first connect
        // happens with empty caps and the gateway records this node as having no
        // advertised commands (which leaves the agent unable to invoke anything on it).
        // The method is idempotent — safe to call here AND later if first-run setup runs.
        if (ShouldInitializeNodeService() && _settings != null)
        {
            EnsureNodeService(_settings);
        }

        // Initialize connections — always create operator client for UI data,
        // additionally create node service for gateway node mode or local MCP.
        // Re-arm the WSL keepalive so the local gateway VM stays up across tray
        // restarts and across the 20s WSL vmIdleTimeout window observed on some
        // hosts. Fire-and-forget on a background task so a slow LxssManager at
        // cold logon never delays InitializeGatewayClient. The keepalive itself
        // runs detached from the tray — see WslDistroKeepAlive in LocalGatewaySetup.cs.
        _ = Task.Run(TryEnsureLocalGatewayKeepAliveAsync);
        InitializeGatewayClient();

        // Pre-warm chat window (WebView2 init takes 1-3s, do it now so left-click is instant)
        if (_settings != null &&
            TryResolveChatCredentials(out var prewarmUrl, out var prewarmToken, out _, out var prewarmIsBootstrapToken) &&
            !prewarmIsBootstrapToken)
        {
            _chatWindow = new ChatWindow(prewarmUrl, prewarmToken);
            // Window is created but hidden — WebView2 initializes in the background
        }

        // Start deep link server
        StartDeepLinkServer();

        // Register global hotkey if enabled
        if (_settings?.GlobalHotkeyEnabled == true)
        {
            _globalHotkey = new GlobalHotkeyService();
            _globalHotkey.VoiceHotkeyPressed += OnVoiceHotkeyPressed;
            _globalHotkey.SettingsHotkeyPressed += OnSettingsHotkeyPressed;
            _globalHotkey.Register();
        }

        // Process startup deep link (command-line or MSIX protocol activation)
        var startupDeepLink = _pendingProtocolUri
            ?? (_startupArgs.Length > 1 && _startupArgs[1].StartsWith("openclaw://", StringComparison.OrdinalIgnoreCase)
                ? _startupArgs[1] : null);
        if (!setupShownDuringStartup && startupDeepLink != null)
        {
            await HandleDeepLinkAsync(startupDeepLink);
        }
        else if (!setupShownDuringStartup && string.Equals(_postSetupLaunch, "chat", StringComparison.OrdinalIgnoreCase))
        {
            await HandleDeepLinkAsync("openclaw://chat");
        }

        Logger.Info("Application started (WinUI 3)");
    }

    private void InitializeKeepAliveWindow()
    {
        // Create a hidden window to keep the WinUI runtime properly initialized
        // This prevents GC/threading issues when creating windows after idle
        _keepAliveWindow = new Window();
        _keepAliveWindow.Content = new Microsoft.UI.Xaml.Controls.Grid();
        _keepAliveWindow.AppWindow.IsShownInSwitchers = false;
        
        // Move off-screen and set minimal size
        _keepAliveWindow.AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(-32000, -32000, 1, 1));
    }

    private void InitializeTrayIcon()
    {
        // Initialize keep-alive window first to anchor WinUI runtime
        InitializeKeepAliveWindow();
        
        // Pre-create tray menu window at startup to avoid creation crashes later
        InitializeTrayMenuWindow();
        
        var iconPath = IconHelper.GetStatusIconPath(ConnectionStatus.Disconnected);
        _trayIcon = new TrayIcon(1, iconPath, BuildTrayTooltip());
        _trayIcon.IsVisible = true;
        ApplyTrayTooltip(BuildTrayTooltip());
        _trayIcon.Selected += OnTrayIconSelected;
        _trayIcon.ContextMenu += OnTrayContextMenu;
    }

    private void InitializeTrayMenuWindow()
    {
        // Pre-create menu window once - reuse to avoid crash on window creation after idle
        _trayMenuWindow = new TrayMenuWindow();
        _trayMenuWindow.MenuItemClicked += OnTrayMenuItemClicked;
        // Don't close - just hide
    }

    private void OnTrayIconSelected(TrayIcon sender, TrayIconEventArgs e)
    {
        if (_connectionManager?.CurrentSnapshot.OperatorState == RoleConnectionState.Connected)
        {
            ShowChatWindow();
            return;
        }

        ShowHub("connection");
    }

    internal void ShowChatWindow()
    {
        if (_settings == null) return;
        if (!TryResolveChatCredentials(out var url, out var token, out var credentialSource, out var isBootstrapToken))
        {
            ShowConnectionSettingsForPairingIssue(
                "ChatWindow",
                "Gateway URL or credential is not configured");
            return;
        }

        if (isBootstrapToken)
        {
            ShowConnectionSettingsForPairingIssue(
                "ChatWindow",
                "Gateway pairing is not complete");
            return;
        }

        Logger.Info($"[ChatWindow] Quick-chat credentials resolved from {credentialSource}");
        if (_chatWindow == null)
        {
            _chatWindow = new ChatWindow(url, token);
        }

        // Bug 2: cached ChatWindow may have been pre-warmed with empty/stale credentials
        // (built before pairing completed). Refresh on every tray click so quick-chat
        // follows the same resolver path as the companion-app operator client.
        _chatWindow.RefreshCredentials(url, token);

        // Toggle: if visible, hide; if hidden, show near tray
        if (_chatWindow.Visible)
        {
            _chatWindow.Hide();
        }
        else
        {
            // Bug 1: When called from the wizard's close handler, OnboardingWindow.Close()
            // steals focus on the same UI tick, deactivating ChatWindow → its
            // OnWindowActivated auto-hides it immediately. Defer the show to a later
            // dispatcher tick (Low priority) so the close + focus-loss cascade settles
            // before we make the chat window visible.
            var window = _chatWindow;
            var dispatcher = _dispatcherQueue;
            if (dispatcher != null)
            {
                dispatcher.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () =>
                    {
                        try { window.ShowNearTrayAnimated(); }
                        catch (Exception ex) { Logger.Warn($"ShowChatWindow deferred show failed: {ex.Message}"); }
                    });
            }
            else
            {
                window.ShowNearTrayAnimated();
            }
        }

    }

    private void ShowCanvasWindow()
    {
        if (_settings?.NodeCanvasEnabled == false)
        {
            Logger.Warn("[Canvas] Canvas capability is disabled; opening capability settings");
            ShowHub("capabilities");
            return;
        }

        if (_nodeService == null)
        {
            ShowConnectionSettingsForPairingIssue(
                "Canvas",
                "Windows node is not initialized");
            return;
        }

        if (_nodeService.IsPendingApproval || !_nodeService.IsPaired)
        {
            ShowConnectionSettingsForPairingIssue(
                "Canvas",
                "Windows node pairing is not complete");
            return;
        }

        _nodeService.ShowCanvasWindow();
    }

    private void ShowConnectionSettingsForPairingIssue(string source, string reason)
    {
        Logger.Warn($"[{source}] {reason}; opening connection settings");
        ShowHub("connection");
    }

    // Voice overlay disabled — inline chat voice mode is used instead.
    // private VoiceOverlayWindow? _voiceOverlayWindow;
    private VoiceService? _standaloneVoiceService;

    /// <summary>
    /// Gets the current VoiceService instance (from the node service or standalone).
    /// Returns null if STT is not enabled.
    /// </summary>
    public VoiceService? VoiceServiceInstance =>
        _nodeService?.VoiceService ?? EnsureStandaloneVoiceService();

    // Voice overlay disabled — inline chat voice mode is used instead.
    // Kept for potential future re-enablement.
    /*
    private void ShowVoiceOverlay()
    {
        var voiceService = _nodeService?.VoiceService ?? EnsureStandaloneVoiceService();
        if (voiceService == null)
        {
            // STT not enabled — show settings
            ShowHub("voice");
            return;
        }

        if (_voiceOverlayWindow == null || _voiceOverlayWindow.AppWindow == null)
        {
            _voiceOverlayWindow = new VoiceOverlayWindow(voiceService, new AppLogger());
            _voiceOverlayWindow.Closed += (_, _) => _voiceOverlayWindow = null;
            // Wire transcription to gateway chat when connected
            _voiceOverlayWindow.TextSubmitted += text =>
            {
                var client = _connectionManager?.OperatorClient;
                if (client != null && _appState!.Status == ConnectionStatus.Connected)
                {
                    _ = client.SendChatMessageAsync(text);
                }
            };
            // Wire Settings button → open the Hub on the Voice & Audio page.
            _voiceOverlayWindow.SettingsRequested += () =>
            {
                OnUiThread(() => ShowHub("voice"));
            };
        }

        _voiceOverlayWindow.Activate();
    }
    */

    private VoiceService? EnsureStandaloneVoiceService()
    {
        if (_settings?.NodeSttEnabled != true)
            return null;

        return _standaloneVoiceService ??= new VoiceService(new AppLogger(), _settings);
    }

    private void OnTrayContextMenu(TrayIcon sender, TrayIconEventArgs e)
    {
        // Right-click: show menu
        ShowTrayMenuPopup();
    }

    private void ShowTrayMenuPopup()
    {
        try
        {
            // Verify dispatcher is still valid
            if (_dispatcherQueue == null)
            {
                Logger.Error("DispatcherQueue is null - cannot show menu");
                return;
            }

            // Menu uses purely cached data — no gateway requests on open
            // Data stays fresh via WebSocket event stream (session/health broadcasts)

            // Reuse pre-created window - never create new ones after startup
            if (_trayMenuWindow == null)
            {
                // This shouldn't happen, but recreate if needed
                Logger.Warn("TrayMenuWindow was null, recreating");
                InitializeTrayMenuWindow();
            }

            // Rebuild menu content
            _trayMenuWindow!.ClearItems();
            BuildTrayMenuPopup(_trayMenuWindow);
            _trayMenuWindow.ShowAtCursor();
        }
        catch (Exception ex)
        {
            _crashLogger.Log("ShowTrayMenuPopup", ex);
            Logger.Error($"Failed to show tray menu: {ex.Message}");
        }
    }

    private void OnTrayMenuItemClicked(object? sender, string action)
    {
        switch (action)
        {
            case "status": ShowStatusDetail(); break;
            case "reconnect": _ = _connectionManager?.ReconnectAsync(); break;
            case "disconnect":
                _ = _connectionManager?.DisconnectAsync();
                LocalDisconnectCleanup();
                break;
            case "connection": ShowHub("connection"); break;
            case "permissions": ShowHub("permissions"); break;
            case "dashboard": OpenDashboard(); break;
            case "canvas": ShowCanvasWindow(); break;
            case "openchat": ShowHub("chat"); break;
            case "voice": ShowHub("voice"); break; // was: ShowVoiceOverlay()
            case "webchat": ShowWebChat(); break;
            case "hub": ShowHub(); break;
            case "companion":
                // If disconnected, open Connection page (status, gateways, add flow)
                // If connected, open Hub at default page
                if (_appState!.Status != ConnectionStatus.Connected)
                    ShowHub("connection");
                else
                    ShowHub();
                break;
            case "quicksend": break; // Quick Send removed
            case "history": ShowHub("channels"); break;
            case "activity": ShowHub("channels"); break;
            case "healthcheck": _ = RunHealthCheckAsync(userInitiated: true); break;
            case "checkupdates": _ = CheckForUpdatesUserInitiatedAsync(); break;
            case "settings": ShowSettings(); break;
            case "setup": _ = ShowOnboardingAsync(); break;
            case "autostart": ToggleAutoStart(); break;
            case "log": OpenLogFile(); break;
            case "logfolder": OpenLogFolder(); break;
            case "configfolder": OpenConfigFolder(); break;
            case "diagnosticsfolder": OpenDiagnosticsFolder(); break;
            case "connectionstatus": ShowConnectionStatusWindow(); break;
            case "supportcontext": _diagnosticsClipboard!.CopySupportContext(); break;
            case "debugbundle": _diagnosticsClipboard!.CopyDebugBundle(); break;
            case "browsersetup": _diagnosticsClipboard!.CopyBrowserSetupGuidance(); break;
            case "portdiagnostics": _diagnosticsClipboard!.CopyPortDiagnostics(); break;
            case "capabilitydiagnostics": _diagnosticsClipboard!.CopyCapabilityDiagnostics(); break;
            case "nodeinventory": _diagnosticsClipboard!.CopyNodeInventory(); break;
            case "channelsummary": _diagnosticsClipboard!.CopyChannelSummary(); break;
            case "activitysummary": _diagnosticsClipboard!.CopyActivitySummary(); break;
            case "extensibilitysummary": _diagnosticsClipboard!.CopyExtensibilitySummary(); break;
            case "restartsshtunnel": RestartSshTunnel(); break;
            case "copydeviceid": CopyDeviceIdToClipboard(); break;
            case "copynodesummary": CopyNodeSummaryToClipboard(); break;
            case "exit": ExitApplication(); break;
            case "about": ShowHub("about"); break;
            default:
                if (action.StartsWith("perm-toggle|", StringComparison.Ordinal)
                    && _permToggleActions.TryGetValue(action, out var permAction))
                {
                    permAction();
                }
                else if (action.StartsWith("session-reset|", StringComparison.Ordinal))
                    _ = ExecuteSessionActionAsync("reset", action["session-reset|".Length..]);
                else if (action.StartsWith("session-compact|", StringComparison.Ordinal))
                    _ = ExecuteSessionActionAsync("compact", action["session-compact|".Length..]);
                else if (action.StartsWith("session-delete|", StringComparison.Ordinal))
                    _ = ExecuteSessionActionAsync("delete", action["session-delete|".Length..]);
                else if (action.StartsWith("session-thinking|", StringComparison.Ordinal))
                {
                    var split = action.Split('|', 3);
                    if (split.Length == 3)
                        _ = ExecuteSessionActionAsync("thinking", split[2], split[1]);
                }
                else if (action.StartsWith("session-verbose|", StringComparison.Ordinal))
                {
                    var split = action.Split('|', 3);
                    if (split.Length == 3)
                        _ = ExecuteSessionActionAsync("verbose", split[2], split[1]);
                }
                else if (action.StartsWith("session:", StringComparison.Ordinal))
                    OpenDashboard($"sessions/{action[8..]}");
                else if (action.StartsWith("dashboard:", StringComparison.Ordinal))
                    OpenDashboard(action["dashboard:".Length..]);
                else if (action.StartsWith("activity:", StringComparison.Ordinal))
                    ShowHub("channels");
                else if (action.StartsWith("channel:", StringComparison.Ordinal))
                    ToggleChannel(action[8..]);
                else
                    // Default: treat as a Hub navigation tag (e.g. "nodes", "agent:main:sessions")
                    ShowHub(action);
                break;
        }
    }
    
    private void CopyDeviceIdToClipboard()
    {
        if (_nodeService?.FullDeviceId == null) return;
        
        try
        {
            CopyTextToClipboard(_nodeService.FullDeviceId);
            
            // Show toast confirming copy
            _toastService!.ShowToast(new ToastContentBuilder()
                .AddText(LocalizationHelper.GetString("Toast_DeviceIdCopied"))
                .AddText(string.Format(LocalizationHelper.GetString("Toast_DeviceIdCopiedDetail"), _nodeService.ShortDeviceId)));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to copy device ID: {ex.Message}");
        }
    }

    private void CopyNodeSummaryToClipboard()
    {
        if (_appState!.Nodes.Length == 0) return;

        try
        {
            var lines = _appState!.Nodes.Select(node =>
            {
                var state = node.IsOnline ? "online" : "offline";
                var name = string.IsNullOrWhiteSpace(node.DisplayName) ? node.ShortId : node.DisplayName;
                return $"{state}: {name} ({node.ShortId}) · {node.DetailText}";
            });
            var summary = string.Join(Environment.NewLine, lines);

            CopyTextToClipboard(summary);

            _toastService!.ShowToast(new ToastContentBuilder()
                .AddText(LocalizationHelper.GetString("Toast_NodeSummaryCopied"))
                .AddText(string.Format(LocalizationHelper.GetString("Toast_NodeSummaryCopiedDetail"), _appState!.Nodes.Length)));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to copy node summary: {ex.Message}");
        }
    }

    private async Task ExecuteSessionActionAsync(string action, string sessionKey, string? value = null)
    {
        var client = _connectionManager?.OperatorClient;
        if (client == null || string.IsNullOrWhiteSpace(sessionKey)) return;

        try
        {
            if (action is "reset" or "compact" or "delete")
            {
                var title = action switch
                {
                    "reset" => "Reset session?",
                    "compact" => "Compact session log?",
                    "delete" => "Delete session?",
                    _ => "Confirm session action"
                };
                var body = action switch
                {
                    "reset" => $"Start a fresh session for '{sessionKey}'?",
                    "compact" => $"Keep the latest log lines for '{sessionKey}' and archive the rest?",
                    "delete" => $"Delete '{sessionKey}' and archive its transcript?",
                    _ => "Continue?"
                };
                var button = action switch
                {
                    "reset" => "Reset",
                    "compact" => "Compact",
                    "delete" => "Delete",
                    _ => "Continue"
                };

                var confirmed = await ConfirmSessionActionAsync(title, body, button);
                if (!confirmed) return;
            }

            var sent = action switch
            {
                "reset" => await client.ResetSessionAsync(sessionKey),
                "compact" => await client.CompactSessionAsync(sessionKey, 400),
                "delete" => await client.DeleteSessionAsync(sessionKey, deleteTranscript: true),
                "thinking" => await client.PatchSessionAsync(sessionKey, thinkingLevel: value),
                "verbose" => await client.PatchSessionAsync(sessionKey, verboseLevel: value),
                _ => false
            };

            if (!sent)
            {
                _toastService!.ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_SessionActionFailed"))
                    .AddText(LocalizationHelper.GetString("Toast_SessionActionFailedDetail")));
                return;
            }

            if (action is "thinking" or "verbose")
            {
                _ = client.RequestSessionsAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Session action error ({action}): {ex.Message}");
            try
            {
                _toastService!.ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_SessionActionFailed"))
                    .AddText(ex.Message));
            }
            catch { }
        }
    }

    private async Task<bool> ConfirmSessionActionAsync(string title, string body, string actionLabel)
    {
        var root = _keepAliveWindow?.Content as FrameworkElement;
        if (root?.XamlRoot == null) return false;

        var dialog = new ContentDialog
        {
            Title = title,
            Content = body,
            PrimaryButtonText = actionLabel,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root.XamlRoot
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private async Task<bool> ConfirmDeepLinkActionAsync(DeepLinkResult result)
    {
        var root = _keepAliveWindow?.Content as FrameworkElement;
        if (root?.XamlRoot == null)
        {
            Logger.Warn($"Cannot confirm deep link action without XAML root: {DeepLinkSecurityPolicy.RedactForLog($"openclaw://{result.Path}")}");
            return false;
        }

        var dialog = new ContentDialog
        {
            Title = "Confirm OpenClaw action",
            Content = $"A deep link wants to {DeepLinkSecurityPolicy.GetActionDisplayName(result)}.",
            PrimaryButtonText = "Allow",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root.XamlRoot
        };
        var dialogResult = await dialog.ShowAsync();
        return dialogResult == ContentDialogResult.Primary;
    }

    private void AddRecentActivity(
        string line,
        string category = "general",
        string? icon = null,
        string? dashboardPath = null,
        string? details = null,
        string? sessionKey = null,
        string? nodeId = null)
    {
        ActivityStreamService.Add(
            category: category,
            title: line,
            icon: icon,
            details: details,
            dashboardPath: dashboardPath,
            sessionKey: sessionKey,
            nodeId: nodeId);
    }

    private List<string> GetRecentActivity(int maxItems)
    {
        return ActivityStreamService.GetItems(Math.Max(0, maxItems))
            .Select(item => $"{item.Timestamp:HH:mm:ss} {item.Title}")
            .ToList();
    }

    private void LocalDisconnectCleanup()
    {
        _appState?.ClearCachedData();
        UpdateTrayIcon();
        // Dismiss the tray menu on disconnect — it will capture fresh data on next open
        _trayMenuWindow?.HideCascade();
    }

    private void BuildTrayMenuPopup(TrayMenuWindow menu)
    {
        // Preview data must be applied before snapshot capture so the injected
        // values are visible to the builder without coupling it to App state.
        ApplyTrayMenuPreviewDataIfRequested();
        var snapshot = CaptureTrayMenuSnapshot();
        var callbacks = new TrayMenuCallbacks(
            DispatchAction: action => OnTrayMenuItemClicked(null, action),
            SaveAndReconnect: () => { _settings?.Save(); _ = _connectionManager?.ReconnectAsync(); },
            TrackConnectionToggle: toggle => _connectionToggleRef = new WeakReference<ToggleSwitch>(toggle),
            IsConnectionToggleSuspended: () => _suspendConnectionToggleEvent);
        var builder = new TrayMenuStateBuilder(snapshot, _permToggleActions, callbacks);

        // Render the whole menu inside a single update batch so layout
        // measures only once instead of once-per-row. Pair with EndUpdate
        // in finally so an exception mid-build doesn't wedge layout.
        menu.BeginUpdate();
        try
        {
            builder.Build(menu);
        }
        finally
        {
            menu.EndUpdate();
        }
    }

    private TrayMenuSnapshot CaptureTrayMenuSnapshot()
    {
        // Show "Reconfigure" if there's an existing setup, "Setup Guide" if fresh
        var hasExistingConfig = _settings != null
            && !StartupSetupState.RequiresSetup(_settings, IdentityDataPath, _gatewayRegistry);
        var hasSetupManagedLocalWslGateway = WslKeepAlivePolicy.HasSetupManagedLocalGateway(_gatewayRegistry?.GetAll());
        var setupMenuLabel = hasExistingConfig
            ? LocalizationHelper.GetString("Menu_Reconfigure")
            : LocalizationHelper.GetString("Menu_SetupGuide");

        return new TrayMenuSnapshot
        {
            CurrentStatus = _appState!.Status,
            AuthFailureMessage = _appState?.AuthFailureMessage,
            GatewayUrl = _gatewayRegistry?.GetActive()?.Url ?? _settings?.GetEffectiveGatewayUrl(),
            GatewaySelf = _appState?.GatewaySelf,
            Presence = _appState?.Presence,
            EnableNodeMode = _settings?.EnableNodeMode == true && _nodeService != null,
            NodeIsPaired = _nodeService?.IsPaired ?? false,
            NodeIsPendingApproval = _nodeService?.IsPendingApproval ?? false,
            NodeIsConnected = _nodeService?.IsConnected ?? false,
            NodePairList = _appState?.NodePairList,
            DevicePairList = _appState?.DevicePairList,
            Nodes = _appState?.Nodes ?? Array.Empty<GatewayNodeInfo>(),
            Sessions = _appState?.Sessions ?? Array.Empty<SessionInfo>(),
            Usage = _appState?.Usage,
            UsageStatus = _appState?.UsageStatus,
            UsageCost = _appState?.UsageCost,
            Settings = _settings,
            SetupMenuLabel = setupMenuLabel,
            ShowSetupMenuEntry = !hasSetupManagedLocalWslGateway,
        };
    }


    /// <summary>
    /// Opt-in design preview: when the <c>OPENCLAW_TRAY_PREVIEW_DATA</c>
    /// environment variable is set to <c>1</c>, populate the session/usage
    /// caches with synthetic values so the Sessions and Usage flyouts render
    /// meaningful progress bars and provider data without a live gateway.
    /// Real data takes precedence — preview values are only written when the
    /// corresponding cache is empty/null, so attaching to a real gateway
    /// after launch immediately replaces the preview.
    /// </summary>
    private void ApplyTrayMenuPreviewDataIfRequested()
    {
        var flag = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_PREVIEW_DATA");
        if (string.IsNullOrEmpty(flag) || flag == "0") return;

        {
            var now = DateTime.UtcNow;
            if (_appState != null)
            {
                _appState.Sessions = new[]
                {
                    new SessionInfo
                    {
                        Key = "preview:main", IsMain = true, Status = "active",
                        Model = "claude-opus-4.7", DisplayName = "Main · preview",
                        InputTokens = 124_000, OutputTokens = 36_000,
                        ContextTokens = 200_000,
                        UpdatedAt = now.AddMinutes(-2), LastSeen = now,
                    },
                    new SessionInfo
                    {
                        Key = "preview:dashboard", IsMain = false, Status = "idle",
                        Model = "gpt-5.4", DisplayName = "agent:main:dashboard",
                        InputTokens = 58_000, OutputTokens = 12_000,
                        ContextTokens = 128_000,
                        UpdatedAt = now.AddHours(-1), LastSeen = now,
                    },
                    new SessionInfo
                    {
                        Key = "preview:scratch", IsMain = false, Status = "idle",
                        Model = "claude-haiku-4.5", DisplayName = "agent:main:scratch",
                        InputTokens = 6_400, OutputTokens = 1_200,
                        ContextTokens = 64_000,
                        UpdatedAt = now.AddHours(-4), LastSeen = now,
                    },
                };

                _appState.Usage = new GatewayUsageInfo
                {
                    InputTokens = 188_400,
                    OutputTokens = 49_200,
                    TotalTokens = 237_600,
                    CostUsd = 4.82,
                    RequestCount = 142,
                    Model = "claude-opus-4.7",
                };

                _appState.UsageStatus = new GatewayUsageStatusInfo
                {
                    UpdatedAt = DateTime.UtcNow,
                    Providers = new()
                    {
                        new GatewayUsageProviderInfo
                        {
                            Provider = "anthropic", DisplayName = "Anthropic",
                            Plan = "Pro",
                            Windows = new()
                            {
                                new() { Label = "5h window", UsedPercent = 64 },
                                new() { Label = "Weekly",    UsedPercent = 28 },
                                new() { Label = "Monthly",   UsedPercent = 0 },
                            },
                        },
                        new GatewayUsageProviderInfo
                        {
                            Provider = "openai", DisplayName = "OpenAI",
                            Plan = "Tier 4",
                            Windows = new()
                            {
                                new() { Label = "RPM",    UsedPercent = 41 },
                                new() { Label = "TPM",    UsedPercent = 73 },
                                new() { Label = "Daily",  UsedPercent = 96 },
                            },
                        },
                    },
                };
            }
        }
    }


    private readonly Dictionary<string, Action> _permToggleActions = new(StringComparer.Ordinal);

    #region Gateway Client

    private void InitializeGatewayClient(bool useBootstrapHandoffAuth = false)
    {
        if (_settings == null || _connectionManager == null || _gatewayRegistry == null) return;
        // SSH tunnel lifecycle is now handled by the connection manager.

        var gatewayUrl = _settings.GetEffectiveGatewayUrl();

        // Check registry first — it's the source of truth after initial setup
        var activeRecord = _gatewayRegistry.GetActive();
        if (activeRecord != null)
        {
            if (!TryConnectGatewayIfCredentialAvailable(activeRecord, "startup"))
            {
                // Still start MCP-only node if enabled — the active record may be stale
                // and MCP-only mode must work without gateway credentials.
                TryStartLocalMcpOnlyNode();
            }
            return;
        }

        TryMigrateLegacyGatewaySettings(gatewayUrl, new AppLogger());
        activeRecord = _gatewayRegistry.GetActive();
        if (activeRecord != null)
        {
            if (!TryConnectGatewayIfCredentialAvailable(activeRecord, "legacy migration"))
                TryStartLocalMcpOnlyNode();
            return;
        }

        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            if (TryStartLocalMcpOnlyNode())
                return;

            Logger.Info("Gateway URL not configured — skipping client initialization");
            return;
        }

        // Bridge: create/update a GatewayRecord from current settings URL.
        // Credentials come from GatewayRegistry and DeviceIdentity, not settings.
        var existing = _gatewayRegistry.FindByUrl(gatewayUrl);
        if (existing != null)
        {
            // Record already exists — just ensure it's active and connect
            _gatewayRegistry.SetActive(existing.Id);
        }
        else
        {
            // No record yet — create one from settings URL if we have a stored device token.
            var hasStoredDeviceToken = DeviceIdentity.HasStoredDeviceToken(
                Path.Combine(SettingsManager.SettingsDirectoryPath));
            if (!hasStoredDeviceToken)
            {
                if (TryStartLocalMcpOnlyNode())
                    return;

                Logger.Info("No stored device token — skipping startup connect (use Setup Code)");
                return;
            }

            var recordId = Guid.NewGuid().ToString();
            var record = new GatewayRecord
            {
                Id = recordId,
                Url = gatewayUrl,
                IsLocal = LocalGatewayUrlClassifier.IsLocalGatewayUrl(gatewayUrl),
                SshTunnel = _settings.UseSshTunnel
                    ? new SshTunnelConfig(
                        _settings.SshTunnelUser ?? "",
                        _settings.SshTunnelHost ?? "",
                        _settings.SshTunnelRemotePort,
                        _settings.SshTunnelLocalPort,
                        _settings.NodeBrowserProxyEnabled &&
                            SshTunnelCommandLine.CanForwardBrowserProxyPort(
                                _settings.SshTunnelRemotePort, _settings.SshTunnelLocalPort))
                    : null,
            };
            _gatewayRegistry.AddOrUpdate(record);
            _gatewayRegistry.SetActive(recordId);
        }

        var migratedRecord = _gatewayRegistry.GetActive()!;

        // Ensure identity directory exists for credential resolution
        var identityDir = _gatewayRegistry.GetIdentityDirectory(migratedRecord.Id);
        if (!Directory.Exists(identityDir))
            Directory.CreateDirectory(identityDir);

        // Copy identity file from legacy location if needed.
        // device-key-ed25519.json holds BOTH the operator DeviceToken and the
        // node NodeDeviceToken on a single record (DeviceIdentity.DeviceKeyData),
        // so this single copy migrates both roles' identity for paired-pre-
        // unification installs (the easy-button setup engine used to write the
        // node-side tokens to this same legacy path via NodeService.ConnectAsync).
        // The legacy file is preserved (copy, not move) for at least one release
        // to allow safe rollback.
        var legacyIdentityPath = Path.Combine(SettingsManager.SettingsDirectoryPath, "device-key-ed25519.json");
        var newIdentityPath = Path.Combine(identityDir, "device-key-ed25519.json");
        if (File.Exists(legacyIdentityPath) && !File.Exists(newIdentityPath))
        {
            try { File.Copy(legacyIdentityPath, newIdentityPath, overwrite: false); }
            catch (Exception ex) { Logger.Warn($"Failed to copy identity file: {ex.Message}"); }
        }

        // Delegate to connection manager — it creates the client, fires OperatorClientChanged,
        // and our handler re-wires the 27 event subscriptions
        if (!TryConnectGatewayIfCredentialAvailable(migratedRecord, "startup bridge"))
            TryStartLocalMcpOnlyNode();
    }

    /// <summary>
    /// Connects only when the active gateway has a usable operator credential:
    /// device token, shared gateway token, or bootstrap token.
    /// </summary>
    private bool TryConnectGatewayIfCredentialAvailable(GatewayRecord record, string context)
    {
        if (_connectionManager == null || _gatewayRegistry == null)
            return false;

        var resolver = new CredentialResolver(DeviceIdentityFileReader.Instance);
        var identityDir = _gatewayRegistry.GetIdentityDirectory(record.Id);
        var credential = ResolveStartupOperatorCredential(record, resolver, identityDir);
        if (credential == null)
        {
            var nodeCredential = ResolveStartupNodeCredential(record, resolver, identityDir);
            if (nodeCredential != null && ShouldInitializeNodeService())
            {
                Logger.Info(
                    $"Connecting node-only gateway during {context}: {record.Url} ({nodeCredential.Source})");
                ObserveBackgroundFault(
                    _connectionManager.ConnectNodeOnlyAsync(record.Id),
                    $"[App] Startup node-only gateway connect failed during {context}");
                return true;
            }

            Logger.Info($"Active gateway has no usable credential — skipping {context} connect");
            return false;
        }

        var connectionKind = record.LastConnected.HasValue
            ? "last successful gateway"
            : "credentialed gateway";
        Logger.Info($"Connecting to {connectionKind} during {context}: {record.Url} ({credential.Source})");
        ObserveBackgroundFault(
            _connectionManager.ConnectAsync(record.Id),
            $"[App] Startup gateway connect failed during {context}");
        return true;
    }

    private static void ObserveBackgroundFault(Task task, string message)
    {
        if (task.IsFaulted)
        {
            Logger.Error($"{message}: {task.Exception.GetBaseException().Message}");
            return;
        }

        if (task.IsCanceled)
        {
            Logger.Warn($"{message}: canceled");
            return;
        }

        if (!task.IsCompleted)
        {
            _ = task.ContinueWith(
                t => Logger.Error($"{message}: {t.Exception!.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private OpenClaw.Connection.GatewayCredential? ResolveStartupOperatorCredential(
        GatewayRecord record,
        CredentialResolver resolver,
        string identityDir)
    {
        if (_gatewayRegistry == null)
            return null;

        var credential = resolver.ResolveOperator(record, identityDir);
        if (credential != null)
            return credential;

        // Backfill for legacy installs that still have the identity file at the
        // root settings path while the active registry record points at that URL.
        var effectiveUrl = _settings?.GetEffectiveGatewayUrl();
        if (!string.IsNullOrWhiteSpace(effectiveUrl) &&
            string.Equals(record.Url, effectiveUrl, StringComparison.OrdinalIgnoreCase))
        {
            return resolver.ResolveOperator(record, SettingsManager.SettingsDirectoryPath);
        }

        return null;
    }

    private OpenClaw.Connection.GatewayCredential? ResolveStartupNodeCredential(
        GatewayRecord record,
        CredentialResolver resolver,
        string identityDir)
    {
        var credential = resolver.ResolveNode(record, identityDir);
        if (credential != null)
            return credential;

        var effectiveUrl = _settings?.GetEffectiveGatewayUrl();
        if (string.IsNullOrWhiteSpace(effectiveUrl) ||
            !string.Equals(record.Url, effectiveUrl, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        credential = resolver.ResolveNode(record, SettingsManager.SettingsDirectoryPath);
        if (credential == null)
            return null;

        TryCopyLegacyIdentityToGateway(record.Id, identityDir);
        return credential;
    }

    private static void TryCopyLegacyIdentityToGateway(string gatewayId, string identityDir)
    {
        var legacyIdentityPath = Path.Combine(SettingsManager.SettingsDirectoryPath, "device-key-ed25519.json");
        var newIdentityPath = Path.Combine(identityDir, "device-key-ed25519.json");
        if (!File.Exists(legacyIdentityPath) || File.Exists(newIdentityPath))
            return;

        try
        {
            if (!Directory.Exists(identityDir))
                Directory.CreateDirectory(identityDir);
            File.Copy(legacyIdentityPath, newIdentityPath, overwrite: false);
            Logger.Info($"[GatewayRegistry] Copied legacy identity into active gateway {gatewayId}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to copy legacy identity file for gateway {gatewayId}: {ex.Message}");
        }
    }

    private void TryMigrateLegacyGatewaySettings(string gatewayUrl, IOpenClawLogger logger)
    {
        if (_settings == null || _gatewayRegistry == null || string.IsNullOrWhiteSpace(gatewayUrl))
        {
            return;
        }

        var legacyIdentityPath = Path.Combine(SettingsManager.SettingsDirectoryPath, "device-key-ed25519.json");
        if (!_settings.HasLegacyGatewayCredentials && !File.Exists(legacyIdentityPath))
        {
            return;
        }

        var migrated = _gatewayRegistry.MigrateFromSettings(
            gatewayUrl,
            _settings.LegacyToken,
            _settings.LegacyBootstrapToken,
            _settings.UseSshTunnel,
            _settings.SshTunnelUser,
            _settings.SshTunnelHost,
            _settings.SshTunnelRemotePort,
            _settings.SshTunnelLocalPort,
            SettingsManager.SettingsDirectoryPath,
            logger);

        if (migrated)
        {
            Logger.Info("[GatewayRegistry] Migrated legacy gateway settings into registry");
        }
    }

    private bool TryStartLocalMcpOnlyNode()
    {
        if (_settings == null || !_settings.EnableMcpServer || _settings.EnableNodeMode)
        {
            return false;
        }

        var nodeService = EnsureNodeService(_settings);
        if (nodeService == null)
        {
            Logger.Warn("MCP-only mode requested but node service could not be initialized");
            return false;
        }

        try
        {
            nodeService.StartLocalOnlyAsync().GetAwaiter().GetResult();
            WireAppCapabilityHandlers();
            Logger.Info("Started MCP-only node service without gateway connection");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to start MCP-only node service: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Handles the connection manager's OperatorClientChanged event.
    /// Re-wires all 27 data event handlers from the old client to the new one.
    /// </summary>
    private void OnOperatorClientChanged(object? sender, OperatorClientChangedEventArgs e)
    {
        if (_dispatcherQueue is { HasThreadAccess: false } dispatcher)
        {
            if (!dispatcher.TryEnqueue(() => OnOperatorClientChanged(sender, e)))
            {
                Logger.Warn("[ConnMgr] Failed to dispatch operator client swap to UI thread");
            }
            return;
        }

        // Delegate all 27 event subscriptions to GatewayService
        _gatewayService?.AttachClient(e.NewClient, e.OldClient);

        // Configure new client
        if (e.NewClient is { } client)
        {
            client.SetUserRules(_settings?.UserRules?.Count > 0 ? _settings.UserRules : null);
            client.SetPreferStructuredCategories(_settings?.PreferStructuredCategories ?? true);

            var concreteClient = client as OpenClawGatewayClient;
            if (concreteClient == null)
                Logger.Warn("[ConnMgr] NewClient is not OpenClawGatewayClient — chat coordinator disabled");
            _chatCoordinator?.SetOperatorClient(concreteClient);
        }
        else
        {
            _chatCoordinator?.SetOperatorClient(null);
            _pairingApprovalCoordinator?.Reset();
        }

        RaiseChatProviderChanged();

        // Update UI references
        if (_appState != null)
            _appState.GatewaySelf = null;
    }

    private void RaiseChatProviderChanged()
    {
        ChatProviderChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Handles the connection manager's StateChanged event.
    /// Maps the snapshot to the existing tray icon / UI status system.
    /// Authoritative writer of gateway lifecycle status. Local prerequisite
    /// failures can still mark the app Error before the manager can connect.
    /// </summary>
    private void OnManagerStateChanged(object? sender, GatewayConnectionSnapshot snap)
    {
        // Map OverallConnectionState to the existing ConnectionStatus enum
        // for backward compat with tray icon and hub window
        var mapped = snap.OverallState switch
        {
            OverallConnectionState.Idle => ConnectionStatus.Disconnected,
            OverallConnectionState.Connecting => ConnectionStatus.Connecting,
            OverallConnectionState.Connected => ConnectionStatus.Connected,
            OverallConnectionState.Ready => ConnectionStatus.Connected,
            OverallConnectionState.Degraded => ConnectionStatus.Connected,
            OverallConnectionState.PairingRequired => ConnectionStatus.Connecting,
            OverallConnectionState.Error => ConnectionStatus.Error,
            OverallConnectionState.Disconnecting => ConnectionStatus.Disconnected,
            _ => ConnectionStatus.Disconnected
        };
        OnUiThread(() =>
        {
            if (_appState != null) _appState.Status = mapped;
            UpdateTrayIcon();
            SyncConnectionToggle(mapped);
            if (mapped is ConnectionStatus.Connected or ConnectionStatus.Disconnected or ConnectionStatus.Error)
            {
                // Dismiss the tray menu on state change — it will capture fresh data on next open
                _trayMenuWindow?.HideCascade();
            }
        });
    }

    private NodeService? EnsureNodeService(SettingsManager settings)
    {
        if (_nodeService != null)
            return _nodeService;

        if (_dispatcherQueue == null)
            return null;

        if (_gatewayService == null)
        {
            Logger.Error("GatewayService must be initialized before NodeService event wiring");
            return null;
        }

        try
        {
            _nodeService = new NodeService(
                new AppLogger(),
                _dispatcherQueue,
                DataPath,
                () => _keepAliveWindow?.Content as FrameworkElement,
                settings,
                enableMcpServer: settings.EnableMcpServer,
                identityDataPath: IdentityDataPath);
            _nodeService.StatusChanged += OnNodeStatusChanged;
            _nodeService.NotificationRequested += OnNodeNotificationRequested;
            _nodeService.ToastRequested += OnNodeToastRequested;
            _nodeService.PairingStatusChanged += OnPairingStatusChanged;
            _nodeService.ChannelHealthUpdated += _gatewayService.OnChannelHealthUpdated;
            _nodeService.InvokeCompleted += OnNodeInvokeCompleted;
            _nodeService.GatewaySelfUpdated += _gatewayService.OnGatewaySelfUpdated;
            return _nodeService;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to initialize node service for local gateway setup: {ex}");
            _nodeService = null;
            return null;
        }
    }

    private void WireAppCapabilityHandlers()
    {
        if (_nodeService == null) return;
        if (_appCapability != null) return; // already wired

        _appCapability = new AppCapability(new AppLogger());
        _nodeService.RegisterMcpOnlyCapability(_appCapability);
        var app = _appCapability;

        app.NavigateHandler = async (page) =>
        {
            var tcs = new TaskCompletionSource<object?>();
            var queued = _dispatcherQueue?.TryEnqueue(() =>
            {
                try { ShowHub(page); tcs.SetResult(new { navigated = true, page }); }
                catch (Exception ex) { tcs.SetResult(new { navigated = false, error = ex.Message }); }
            }) ?? false;
            if (!queued) tcs.TrySetResult(new { navigated = false, error = "UI thread unavailable" });
            return await tcs.Task;
        };

        app.StatusHandler = () => new
        {
            connectionStatus = _appState!.Status.ToString(),
            nodeConnected = _nodeService?.IsConnected ?? false,
            nodePaired = _nodeService?.IsPaired ?? false,
            nodePendingApproval = _nodeService?.IsPendingApproval ?? false,
            gatewayVersion = _appState!.GatewaySelf?.ServerVersion,
            sessionCount = _appState!.Sessions?.Length ?? 0,
            nodeCount = _appState!.Nodes?.Length ?? 0,
        };

        app.SessionsHandler = async (agentId) =>
        {
            var sessions = _appState!.Sessions ?? Array.Empty<SessionInfo>();
            if (!string.IsNullOrEmpty(agentId))
                sessions = sessions.Where(s => s.Key != null &&
                    s.Key.StartsWith($"agent:{agentId}:", StringComparison.OrdinalIgnoreCase)).ToArray();
            return sessions.Select(s => new { s.Key, s.Status, s.Model, s.AgeText, tokens = s.InputTokens + s.OutputTokens }).ToArray();
        };

        app.AgentsHandler = async () =>
        {
            if (_appState!.AgentsList.HasValue &&
                _appState!.AgentsList.Value.TryGetProperty("agents", out var agentsArr) &&
                agentsArr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                return System.Text.Json.JsonSerializer.Deserialize<object>(agentsArr.GetRawText());
            }
            return Array.Empty<object>();
        };

        app.NodesHandler = () =>
        {
            return _appState!.Nodes?.Select(n => new
            {
                n.DisplayName,
                n.NodeId,
                n.IsOnline,
                n.Platform,
                n.CapabilityCount,
                n.CommandCount,
                n.Capabilities,
                n.Commands,
                n.DisabledCommands,
                n.Permissions
            }).ToArray()
                ?? Array.Empty<object>();
        };

        app.ConfigGetHandler = async (path) =>
        {
            if (_appState?.Config == null) return new { error = "Config not loaded" };
            // Config is already redacted by the gateway's redactConfigSnapshot
            var raw = _appState.Config.Value;
            var config = raw.TryGetProperty("parsed", out var parsed) ? parsed
                : (raw.TryGetProperty("config", out var cfg) ? cfg : raw);
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var segment in path.Split('.'))
                {
                    if (config.TryGetProperty(segment, out var child)) config = child;
                    else return (object)new { error = $"Path not found: {path}" };
                }
            }
            return System.Text.Json.JsonSerializer.Deserialize<object>(config.GetRawText());
        };

        // Allowlist of safe settings (no secrets like Token, BootstrapToken, API keys)
        var safeSettings = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AutoStart", "GlobalHotkeyEnabled", "ShowNotifications", "NotificationSound",
            "NotifyHealth", "NotifyUrgent", "NotifyReminder", "NotifyEmail", "NotifyCalendar",
            "NotifyBuild", "NotifyStock", "NotifyInfo", "NotifyChatResponses",
            "EnableNodeMode", "EnableMcpServer", "PreferStructuredCategories",
            "NodeCanvasEnabled", "NodeScreenEnabled", "NodeCameraEnabled",
            "NodeLocationEnabled", "NodeBrowserProxyEnabled", "NodeTtsEnabled",
            "HasSeenActivityStreamTip", "TtsProvider"
        };

        app.SettingsGetHandler = (name) =>
        {
            if (_settings == null) return null;
            if (!safeSettings.Contains(name)) return new { error = $"Setting '{name}' is not accessible" };
            var prop = typeof(SettingsManager).GetProperty(name,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            return prop?.GetValue(_settings);
        };

        app.SettingsSetHandler = (name, value) =>
        {
            if (_settings == null) return new { error = "Settings not loaded" };
            if (!safeSettings.Contains(name)) return new { error = $"Setting '{name}' is not accessible" };
            var prop = typeof(SettingsManager).GetProperty(name,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            if (prop == null) return new { error = $"Unknown setting: {name}" };
            try
            {
                var converted = Convert.ChangeType(value, prop.PropertyType);
                prop.SetValue(_settings, converted);
                _settings.Save();
                return new { name, value = prop.GetValue(_settings) };
            }
            catch (Exception ex) { return new { error = ex.Message }; }
        };

        app.MenuHandler = () =>
        {
            var items = new List<object>
            {
                new { type = "status", status = _appState!.Status.ToString() },
                new { type = "sessions", count = _appState!.Sessions?.Length ?? 0 },
                new { type = "nodes", count = _appState!.Nodes?.Length ?? 0 },
            };
            return items;
        };

        app.SearchHandler = (query) =>
        {
            if (_hubWindow == null) return Array.Empty<object>();
            var commands = _hubWindow.BuildCommandList();
            var matches = commands
                .Where(c => c.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || (c.Subtitle?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .Take(10)
                .Select(c => new { c.Title, c.Subtitle, c.Icon })
                .ToArray();
            return matches;
        };

        app.DashboardUrlHandler = (path) =>
        {
            if (!TryResolveChatCredentials(out var gatewayUrl, out var token, out var credentialSource, out var isBootstrapToken))
                return new { error = "Gateway URL or credential is not configured" };

            var url = GatewayDashboardUrlBuilder.Build(
                gatewayUrl,
                path,
                token,
                !isBootstrapToken && credentialSource == CredentialResolver.SourceSharedGatewayToken);

            return new
            {
                url,
                credentialSource,
                usesSharedGatewayToken = !isBootstrapToken && credentialSource == CredentialResolver.SourceSharedGatewayToken,
                hasTokenQuery = url.Contains("?token=", StringComparison.Ordinal) || url.Contains("&token=", StringComparison.Ordinal)
            };
        };
    }

    private bool RequiresSetup(SettingsManager settings)
    {
        return StartupSetupState.RequiresSetup(settings, IdentityDataPath, _gatewayRegistry);
    }

    private bool ShouldInitializeNodeService()
    {
        return _settings?.EnableNodeMode == true || _settings?.EnableMcpServer == true;
    }

    /// <summary>
    /// Ensures a WSL keepalive process is running for the local gateway distro
    /// so the WSL2 VM stays up even after the tray exits.
    /// Best-effort, fire-and-forget.
    /// </summary>
    private async Task TryEnsureLocalGatewayKeepAliveAsync()
    {
        try
        {
            if (_settings is null) return;

            var activeRecord = _gatewayRegistry?.GetActive();
            if (!WslKeepAlivePolicy.ShouldStart(activeRecord, _settings.GetEffectiveGatewayUrl()))
            {
                await StopStaleLocalGatewayKeepAliveAsync();
                return;
            }

            var distroName = await ResolveLocalGatewayDistroNameAsync(activeRecord);
            if (string.IsNullOrWhiteSpace(distroName)) return;

            // Verify distro exists before spawning keepalive
            var runner = new WslExeCommandRunner(new AppLogger(), defaultTimeout: TimeSpan.FromSeconds(4));
            var distros = await runner.ListDistrosAsync();
            if (!distros.Any(d => string.Equals(d.Name, distroName, StringComparison.OrdinalIgnoreCase)))
            {
                Logger.Warn($"[WslKeepAlive] Distro '{distroName}' not found; skipping keepalive.");
                return;
            }

            // Spawn a detached wsl sleep process to keep the VM alive
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ResolveWslExePath(),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(distroName);
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add("sleep");
            psi.ArgumentList.Add("infinity");

            var proc = System.Diagnostics.Process.Start(psi);
            if (proc is not null)
            {
                Logger.Info($"[WslKeepAlive] Started keepalive for {distroName} (PID {proc.Id}).");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[WslKeepAlive] Startup keepalive failed (non-fatal): {ex.Message}");
        }
    }

    private async Task StopStaleLocalGatewayKeepAliveAsync()
    {
        try
        {
            var localDataDir = SetupExistingGatewayClassifier.ResolveLocalDataPath();
            var markerDir = Path.Combine(localDataDir, "wsl-keepalive");
            var markerDistroNames = ReadKeepAliveMarkerDistroNames(markerDir);
            var setupStateDistroName = await ReadSetupStateDistroNameAsync(localDataDir);
            var records = _gatewayRegistry?.GetAll() ?? [];

            foreach (var distroName in WslKeepAlivePolicy.FindStaleSetupManagedDistroNames(
                records,
                markerDistroNames,
                setupStateDistroName))
            {
                StopKeepAliveProcessesForDistro(distroName);
                DeleteKeepAliveMarker(markerDir, distroName);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[WslKeepAlive] Stale keepalive cleanup failed (non-fatal): {ex.Message}");
        }
    }

    private static IReadOnlyList<string> ReadKeepAliveMarkerDistroNames(string markerDir)
    {
        if (!Directory.Exists(markerDir))
            return [];

        var distroNames = new List<string>();
        foreach (var markerPath in Directory.EnumerateFiles(markerDir, "*.json"))
        {
            if (WslKeepAlivePolicy.TryGetMarkerDistroName(File.ReadAllText(markerPath), out var distroName))
                distroNames.Add(distroName);
        }

        return distroNames;
    }

    private static async Task<string?> ReadSetupStateDistroNameAsync(string localDataDir)
    {
        var stateFile = Path.Combine(localDataDir, "setup-state.json");
        if (!File.Exists(stateFile))
            return null;

        var json = await File.ReadAllTextAsync(stateFile);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("DistroName", out var distroElement)
            ? distroElement.GetString()
            : null;
    }

    private static void StopKeepAliveProcessesForDistro(string distroName)
    {
        var procs = System.Diagnostics.Process.GetProcessesByName("wsl")
            .Concat(System.Diagnostics.Process.GetProcessesByName("wsl.exe"));

        foreach (var proc in procs)
        {
            try
            {
                if (WslKeepAlivePolicy.IsKeepaliveCommandLine(GetProcessCommandLine(proc.Id), distroName))
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                    Logger.Info($"[WslKeepAlive] Stopped stale keepalive for {distroName} (PID {proc.Id}).");
                }
            }
            catch
            {
                // Process may have exited while being inspected.
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    private static void DeleteKeepAliveMarker(string markerDir, string distroName)
    {
        if (!Directory.Exists(markerDir))
            return;

        foreach (var markerPath in Directory.EnumerateFiles(markerDir, "*.json"))
        {
            try
            {
                if (WslKeepAlivePolicy.TryGetMarkerDistroName(File.ReadAllText(markerPath), out var markerDistro)
                    && string.Equals(markerDistro, distroName, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(markerPath);
                    Logger.Info($"[WslKeepAlive] Deleted stale keepalive marker for {distroName}.");
                }
            }
            catch
            {
                // Best-effort cleanup; stale/corrupt markers are not fatal.
            }
        }
    }

    private static string? GetProcessCommandLine(int pid)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                $"-NoProfile -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').CommandLine\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output.Trim();
        }
        catch { return null; }
    }

    private static string ResolveWslExePath()
    {
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windowsDir))
            windowsDir = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

        return Path.Combine(windowsDir, "System32", "wsl.exe");
    }

    /// <summary>
    /// Resolves the WSL distro name to keep alive. Prefers the value persisted by
    /// onboarding in <c>setup-state.json</c> so the keepalive always targets the distro
    /// the user actually installed. In DEBUG / test builds, an
    /// <c>OPENCLAW_WSL_DISTRO_NAME</c> environment override is honored to match
    /// Resolves the local gateway distro name by reading setup-state.json.
    /// Falls back to "OpenClawGateway" if not found.
    /// </summary>
    private async Task<string?> ResolveLocalGatewayDistroNameAsync(GatewayRecord? activeRecord)
    {
        string? setupStateDistroName = null;
        try
        {
            var stateFile = Path.Combine(
                SetupExistingGatewayClassifier.ResolveLocalDataPath(),
                "setup-state.json");

            if (File.Exists(stateFile))
            {
                var json = await File.ReadAllTextAsync(stateFile);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("DistroName", out var dn) &&
                    dn.GetString() is { Length: > 0 } distroName)
                {
                    setupStateDistroName = distroName;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[WslKeepAlive] Failed to read setup-state.json: {ex.Message}");
        }

        return WslKeepAlivePolicy.ResolveDistroName(
            activeRecord,
            setupStateDistroName,
            Environment.GetEnvironmentVariable("OPENCLAW_WSL_DISTRO_NAME"));
    }

    // The pre-unification ShouldInitializeNodeService(GatewayRecord, string) overload
    // and LocalNodeServiceOwnsIdentityFor have been removed: GatewayConnectionManager
    // is now the single owner of the WindowsNodeClient lifecycle for ALL gateways
    // (local + remote). NodeService remains as the capability registrar via the
    // NodeConnector.ClientCreated → AttachClient bridge wired in InitializeApp.

    private void OnNodeStatusChanged(object? sender, ConnectionStatus status)
    {
        Logger.Info($"Node status: {status}");
        AddRecentActivity($"Node mode {status}", category: "node", dashboardPath: "nodes");
        
        // In node-only mode, surface node connection in main status indicator
        if (_settings?.EnableNodeMode == true)
        {
            // Status field is maintained by OnManagerStateChanged — no write needed here.
            UpdateTrayIcon();
            OnUiThread(UpdateStatusDetailWindow);
        }
        
        // Don't show "connected" toast if waiting for pairing - we'll show pairing status instead
        var nodeService = _nodeService;
        if (status == ConnectionStatus.Connected && nodeService?.IsPaired == true)
        {
            var deviceId = nodeService.FullDeviceId;
            if (_toastService!.HasRecentToast("node-paired", deviceId))
            {
                Logger.Info($"[ToastDeduper] Suppressed node-connected toast after node-paired deviceId={deviceId}");
                return;
            }

            try
            {
                _toastService!.ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_NodeModeActive"))
                    .AddText(LocalizationHelper.GetString("Toast_NodeModeActiveDetail")),
                    "node-connected",
                    deviceId);
            }
            catch { /* ignore */ }
        }
    }

    private void OnPairingStatusChanged(object? sender, OpenClaw.Shared.PairingStatusEventArgs args)
    {
        Logger.Info($"Pairing status: {args.Status}");

        // The local node's own device id may have just become known. Re-run the
        // approval reconcile so the own-node filter drops any self pairing request
        // rather than prompting the operator to approve their own machine.
        OnUiThread(() => _pairingApprovalCoordinator?.OnPairListsUpdated(
            _appState?.DevicePairList, _appState?.NodePairList));

        try
        {
            if (args.Status == OpenClaw.Shared.PairingStatus.Pending)
            {
                ShowPairingPendingNotification(args.DeviceId);
            }
            else if (args.Status == OpenClaw.Shared.PairingStatus.Paired)
            {
                // Bug 3: idempotency guard — only show "Node paired" toast/activity once
                // per device per session. WS reconnects re-fire Paired; suppress duplicates.
                var deviceKey = args.DeviceId ?? string.Empty;
                if (!_toastService!.HasShownPairedToast(deviceKey))
                {
                    _toastService!.MarkPairedToastShown(deviceKey);
                    AddRecentActivity("Node paired", category: "node", dashboardPath: "nodes", nodeId: args.DeviceId);
                    _toastService!.ShowToast(new ToastContentBuilder()
                        .AddText(LocalizationHelper.GetString("Toast_NodePaired"))
                        .AddText(LocalizationHelper.GetString("Toast_NodePairedDetail")),
                        "node-paired",
                        args.DeviceId);
                }
                else
                {
                    Logger.Info($"Suppressing duplicate Paired toast for device {deviceKey}");
                }
            }
            else if (args.Status == OpenClaw.Shared.PairingStatus.Rejected)
            {
                AddRecentActivity("Node pairing rejected", category: "node", dashboardPath: "nodes", nodeId: args.DeviceId, details: args.Message ?? LocalizationHelper.GetString("Toast_PairingRejectedDetail"));
                _toastService!.ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_PairingRejected"))
                    .AddText(LocalizationHelper.GetString("Toast_PairingRejectedDetail")),
                    "node-pairing-rejected",
                    args.DeviceId);
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Pushes current node service state to hub window so ConnectionPage reflects live pairing/identity.
    /// Now a no-op — pages read App properties directly via CurrentApp.
    /// </summary>

    public static string BuildPairingApprovalCommand(string deviceId) =>
        $"openclaw devices approve {deviceId}";

    public void ShowPairingPendingNotification(string deviceId, string? approvalCommand = null)
    {
        var command = approvalCommand ?? BuildPairingApprovalCommand(deviceId);
        var shortDeviceId = deviceId.Length > 16 ? deviceId[..16] : deviceId;

        AddRecentActivity("Node pairing pending", category: "node", dashboardPath: "nodes", nodeId: deviceId);
        _toastService!.ShowToast(new ToastContentBuilder()
            .AddText(LocalizationHelper.GetString("Toast_PairingPending"))
            .AddText(string.Format(LocalizationHelper.GetString("Toast_PairingPendingDetail"), shortDeviceId))
            .AddButton(new ToastButton()
                .SetContent(LocalizationHelper.GetString("Toast_CopyPairingCommand"))
                .AddArgument("action", "copy_pairing_command")
                .AddArgument("command", command)),
            "node-pairing-pending",
            deviceId);
    }
    
    private void OnNodeNotificationRequested(object? sender, OpenClaw.Shared.Capabilities.SystemNotifyArgs args)
    {
        AddRecentActivity(args.Title, category: "node", dashboardPath: "nodes", details: args.Body);

        // Agent requested a notification via node.invoke system.notify
        try
        {
            _toastService!.ShowToast(new ToastContentBuilder()
                .AddText(args.Title)
                .AddText(args.Body));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to show node notification: {ex.Message}");
        }
    }

    private void OnNodeToastRequested(object? sender, Microsoft.Toolkit.Uwp.Notifications.ToastContentBuilder builder)
        => OnUiThread(() =>
            NonFatalAction.Run(() => _toastService!.ShowToast(builder), msg => Logger.Warn($"Failed to show node toast: {msg}")));

    private void OnNodeInvokeCompleted(object? sender, NodeInvokeCompletedEventArgs args)
    {
        var status = args.Ok ? "completed" : "failed";
        var durationMs = Math.Max(0, (int)Math.Round(args.Duration.TotalMilliseconds));
        var details = args.Ok
            ? $"{GetNodeInvokePrivacyClass(args.Command)} · {durationMs} ms"
            : $"{GetNodeInvokePrivacyClass(args.Command)} · {durationMs} ms · {args.Error ?? "unknown error"}";

        AddRecentActivity(
            $"node.invoke {status}: {args.Command}",
            category: "node.invoke",
            dashboardPath: "nodes",
            details: details,
            nodeId: args.NodeId);

        OnUiThread(UpdateStatusDetailWindow);
    }

    private static string GetNodeInvokePrivacyClass(string command)
    {
        if (string.Equals(command, "screen.record", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "screen.snapshot", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "camera.snap", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "camera.clip", StringComparison.OrdinalIgnoreCase))
        {
            return "privacy-sensitive";
        }

        if (command.StartsWith("system.run", StringComparison.OrdinalIgnoreCase))
        {
            return "exec";
        }

        return "metadata";
    }

    // ── Re-raised event handlers from GatewayService ──────────────────

    private void OnGatewayConnectionStatusChanged(object? sender, ConnectionStatus status)
    {
        if (status == ConnectionStatus.Connected && _appState != null)
        {
            _appState.AuthFailureMessage = null;
        }

        UpdateTrayIcon();
        OnUiThread(() =>
        {
            UpdateStatusDetailWindow();
            SyncConnectionToggle(status);
            if (status is ConnectionStatus.Connected or ConnectionStatus.Disconnected or ConnectionStatus.Error)
            {
                // Dismiss the tray menu on state change — it will capture fresh data on next open
                _trayMenuWindow?.HideCascade();
            }
        });

        if (status == ConnectionStatus.Connected)
        {
            _ = RunHealthCheckAsync();
            // For local gateways, the NodeConnector is suppressed because NodeService
            // owns the identity. Connect the NodeService directly after operator connects.
            _ = TryConnectLocalNodeServiceAsync();
        }
    }

    /// <summary>
    /// Connects the local NodeService to the active gateway when the operator connection
    /// is established. This handles the restart case where the NodeConnector is suppressed
    /// for local gateways (LocalNodeServiceOwnsIdentityFor returns true) but the NodeService
    /// was never told to connect.
    /// </summary>
    private async Task TryConnectLocalNodeServiceAsync()
    {
        if (_connectionManager == null)
            return;

        Logger.Info("[App] Auto-connecting local NodeService via EnsureNodeConnectedAsync");
        try
        {
            await _connectionManager.EnsureNodeConnectedAsync();
        }
        catch (Exception ex)
        {
            Logger.Error($"[App] Local NodeService auto-connect failed: {ex.Message}");
        }
    }

    private void OnGatewayAuthenticationFailed(object? sender, string message)
    {
        UpdateTrayIcon();

        // Store auth failure in AppState — HubWindow observes it via PropertyChanged
        if (_appState != null)
        {
            _appState.AuthFailureMessage = message;
        }
    }

    private void OnGatewaySessionCommandCompleted(object? sender, SessionCommandResult result)
    {
        OnUiThread(() =>
        {
            try
            {
                var title = result.Ok ? "✅ Session updated" : "❌ Session action failed";
                var key = string.IsNullOrWhiteSpace(result.Key) ? "session" : result.Key!;
                var message = result.Ok
                    ? result.Method switch
                    {
                        "sessions.patch" => $"Updated settings for {key}",
                        "sessions.reset" => $"Reset {key}",
                        "sessions.compact" => result.Kept.HasValue
                            ? $"Compacted {key} ({result.Kept.Value} lines kept)"
                            : $"Compacted {key}",
                        "sessions.delete" => $"Deleted {key}",
                        _ => $"Completed action for {key}"
                    }
                    : result.Error ?? "Request failed";
                AddRecentActivity(
                    $"{title.Replace("✅ ", "").Replace("❌ ", "")}: {message}",
                    category: "session",
                    dashboardPath: !string.IsNullOrWhiteSpace(result.Key) ? $"sessions/{result.Key}" : "sessions",
                    sessionKey: result.Key);

                _toastService!.ShowToast(new ToastContentBuilder()
                    .AddText(title)
                    .AddText(message));
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to show session action toast: {ex.Message}");
            }
        });

        if (result.Ok)
        {
            _ = _connectionManager?.OperatorClient?.RequestSessionsAsync();
        }
    }

    private void OnGatewayNotificationReceived(object? sender, OpenClawNotification notification)
    {
        // Voice overlay: show agent chat responses, and (independently) speak them
        // if the user enabled "Read responses aloud".
        if (notification.IsChat && !string.IsNullOrEmpty(notification.Message))
        {
            // Suppress TTS/voice overlay when the user has aborted the response.
            if (ChatProvider?.IsResponseSuppressed == true)
                return;

            // Voice overlay disabled — agent responses no longer routed to overlay window.
            // if (_voiceOverlayWindow != null)
            // {
            //     OnUiThread(() =>
            //     {
            //         try
            //         {
            //             _voiceOverlayWindow?.AddAgentResponse(notification.Message);
            //         }
            //         catch { }
            //     });
            // }

            // TTS: read response aloud whenever the toggle is on (any chat surface).
            if (_settings?.VoiceTtsEnabled == true)
            {
                _ = (_chatCoordinator?.SpeakResponseAsync(notification.Message) ?? Task.CompletedTask);
            }
        }

        if (_settings?.ShowNotifications != true) return;
        if (!ShouldShowNotification(notification)) return;

        // Store in history
        NotificationHistoryService.AddNotification(new Services.GatewayNotification
        {
            Title = notification.Title,
            Message = notification.Message,
            Category = notification.Type
        });

        // Show toast
        try
        {
            var builder = new ToastContentBuilder()
                .AddText(notification.Title ?? "OpenClaw")
                .AddText(notification.Message);

            var logoPath = GetNotificationIcon(notification.Type);
            if (!string.IsNullOrEmpty(logoPath) && System.IO.File.Exists(logoPath))
            {
                builder.AddAppLogoOverride(new Uri(logoPath), ToastGenericAppLogoCrop.Circle);
            }

            if (notification.IsChat)
            {
                builder.AddArgument("action", "open_chat")
                       .AddButton(new ToastButton()
                           .SetContent("Open Chat")
                           .AddArgument("action", "open_chat"));
            }

            _toastService!.ShowToast(builder);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to show toast: {ex.Message}");
        }
    }

    // ── AppState → tray-level side effects (tray icon, status detail) ──
    // The tray menu is NOT refreshed live while open — data is frozen at
    // open time via TrayMenuSnapshot to avoid WinUI layout races that cause
    // blank subflyouts. The menu captures a fresh snapshot on every open.

    private void OnAppStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_appState == null) return;

        switch (e.PropertyName)
        {
            case nameof(AppState.GatewaySelf):
            case nameof(AppState.Sessions):
            case nameof(AppState.UsageCost):
            case nameof(AppState.Nodes):
                UpdateStatusDetailWindow();
                break;
            case nameof(AppState.CurrentActivity):
                UpdateTrayIcon();
                break;
        }
    }


    private void SyncConnectionToggle(ConnectionStatus status)
    {
        if (_connectionToggleRef == null)
            return;

        if (!_connectionToggleRef.TryGetTarget(out var toggle))
            return;

        if (toggle.XamlRoot == null)
        {
            _connectionToggleRef = null;
            return;
        }

        var shouldBeOn = status == ConnectionStatus.Connected;
        var canToggle = status is ConnectionStatus.Connected or ConnectionStatus.Disconnected or ConnectionStatus.Error;
        _suspendConnectionToggleEvent = true;
        try
        {
            TrayMenuWindow.SetMenuToggleSwitchState(toggle, shouldBeOn, canToggle);
            ToolTipService.SetToolTip(toggle,
                shouldBeOn ? "Connected - toggle off to disconnect"
                    : status == ConnectionStatus.Connecting ? "Connecting..."
                    : "Disconnected - toggle on to connect");
        }
        finally
        {
            _suspendConnectionToggleEvent = false;
        }
    }

    private static string? GetNotificationIcon(string? type)
    {
        // For now, use the app icon for all notifications
        // In the future, we could create category-specific icons
        var appDir = AppContext.BaseDirectory;
        var iconPath = System.IO.Path.Combine(appDir, "Assets", "openclaw.ico");
        return System.IO.File.Exists(iconPath) ? iconPath : null;
    }

    private bool ShouldShowNotification(OpenClawNotification notification)
    {
        if (_settings == null) return true;

        // Chat toggle: suppress all chat responses if disabled
        if (notification.IsChat && !_settings.NotifyChatResponses)
            return false;

        // Suppress chat notifications when a chat window is already showing them
        if (notification.IsChat)
        {
            if (_hubWindow != null && !_hubWindow.IsClosed)
                return false;
            if (_chatWindow is { IsClosed: false, Visible: true })
                return false;
        }

        var type = notification.Type;
        if (type == null) return true;
        return s_notifTypeMap.TryGetValue(type, out var selector) ? selector(_settings) : true;
    }

    #endregion

    #region Health Check

    /// <summary>User-initiated health check (from UI button). No background timers.</summary>
    private async Task RunHealthCheckAsync(bool userInitiated = false)
    {
        var client = _connectionManager?.OperatorClient;
        if (client == null)
        {
            if (_settings?.EnableNodeMode == true && _nodeService?.IsConnected == true)
            {
                _appState!.LastCheckTime = DateTime.Now;
                OnUiThread(UpdateStatusDetailWindow);
                if (userInitiated)
                {
                    _toastService!.ShowToast(new ToastContentBuilder()
                        .AddText(LocalizationHelper.GetString("Toast_HealthCheck"))
                        .AddText("Node Mode is connected; gateway health is streaming."));
                }
                return;
            }

            if (userInitiated)
            {
                _toastService!.ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_HealthCheck"))
                    .AddText(LocalizationHelper.GetString("Toast_HealthCheckNotConnected")));
            }
            return;
        }

        try
        {
            _appState!.LastCheckTime = DateTime.Now;
            await client.CheckHealthAsync();
            if (userInitiated)
            {
                _toastService!.ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_HealthCheck"))
                    .AddText(LocalizationHelper.GetString("Toast_HealthCheckSent")));
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Health check failed: {ex.Message}");
            if (userInitiated)
            {
                _toastService!.ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_HealthCheckFailed"))
                    .AddText(ex.Message));
            }
        }
    }

    #endregion

    #region Tray Icon

    private void UpdateTrayIcon()
    {
        if (_dispatcherQueue != null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(UpdateTrayIcon);
            return;
        }

        if (_trayIcon == null) return;

        // Tray icon is pinned to the app icon so it visually matches the agent
        // avatar and chat-window title bar. Status is communicated via the
        // tooltip text below rather than swapping the icon image.
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "openclaw.ico");
        var tooltip = BuildTrayTooltip();

        try
        {
            _trayIcon.SetIcon(iconPath);
            ApplyTrayTooltip(tooltip);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to update tray icon: {ex.Message}");
        }
    }

    private void ApplyTrayTooltip(string tooltip)
    {
        if (_trayIcon == null)
            return;

        if (string.Equals(_trayIcon.Tooltip, tooltip, StringComparison.Ordinal))
        {
            _trayIcon.Tooltip = string.Empty;
        }

        _trayIcon.Tooltip = tooltip;
    }

    private string BuildTrayTooltip() =>
        new TrayTooltipBuilder(CaptureTraySnapshot()).Build();

    private TrayStateSnapshot CaptureTraySnapshot() => new TrayStateSnapshot
    {
        Status = _appState!.Status,
        CurrentActivity = _appState!.CurrentActivity,
        Channels = _appState!.Channels,
        Nodes = _appState!.Nodes,
        LocalNodeFallback = _nodeService?.GetLocalNodeInfo(),
        AuthFailureMessage = _appState!.AuthFailureMessage,
        LastCheckTime = _appState!.LastCheckTime,
        Settings = _settings
    };

    #endregion

    #region Window Management

    internal void ShowHub(string? navigateTo = null, bool activate = true, string? originTag = null)
    {
        if (_hubWindow == null || _hubWindow.IsClosed)
        {
            _hubWindow = new HubWindow();
            _hubWindow.AppModel = _appState;
            _hubWindow.ApplyNavPaneState(_settings!);
            _hubWindow.OpenSetupAction = () => _ = ShowOnboardingAsync();
            _hubWindow.OpenConnectionStatusAction = ShowConnectionStatusWindow;
            _hubWindow.OpenVoiceAction = () => ShowHub("voice"); // was: ShowVoiceOverlay()
            _hubWindow.ConnectionManager = _connectionManager;
            _hubWindow.GatewayRegistry = _gatewayRegistry;
            _hubWindow.ConnectAction = () =>
            {
                _ = _connectionManager?.ReconnectAsync();
            };
            _hubWindow.DisconnectAction = () =>
            {
                _ = _connectionManager?.DisconnectAsync();
                // Status is updated by OnManagerStateChanged when disconnect completes.
                UpdateTrayIcon();
            };
            _hubWindow.ReconnectAction = () =>
            {
                _ = _connectionManager?.ReconnectAsync();
            };
            if (_nodeService != null)
            {
                _hubWindow.NodeIsConnected = _nodeService.IsConnected;
                _hubWindow.NodeIsPaired = _nodeService.IsPaired;
                _hubWindow.NodeIsPendingApproval = _nodeService.IsPendingApproval;
                _hubWindow.NodeShortDeviceId = _nodeService.ShortDeviceId;
                _hubWindow.NodeFullDeviceId = _nodeService.FullDeviceId;
            }
            _hubWindow.VoiceServiceInstance = _nodeService?.VoiceService ?? _standaloneVoiceService;
            _hubWindow.SettingsSaved += OnSettingsSaved;
            _hubWindow.Closed += (s, e) =>
            {
                _hubWindow.SettingsSaved -= OnSettingsSaved;
                _hubWindow = null;
            };

            _hubWindow.BindToAppState();

            // Navigate to default page now that AppModel is set
            _hubWindow.NavigateToDefault();
        }

        if (navigateTo != null)
        {
            _hubWindow.NavigateTo(navigateTo, originTag);
        }
        if (activate)
        {
            var hubWindow = _hubWindow;
            AsyncEventHandlerGuard.Run(
                () => ActivateHubWhenReadyAsync(hubWindow),
                new AppLogger(),
                nameof(ActivateHubWhenReadyAsync));
        }
        else
        {
            // Show without stealing focus — used by right-click on the
            // tray icon where the popup needs to remain the foreground
            // window (popups light-dismiss if focus moves away).
            // If the Hub was minimized, restore it first so it actually
            // becomes visible behind the popup; otherwise Show(false)
            // is a no-op on a minimized window.
            try
            {
                if (_hubWindow.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op
                    && op.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
                {
                    op.Restore(activateWindow: false);
                }
                _hubWindow.AppWindow.Show(activateWindow: false);
            }
            catch { /* swallow */ }
        }
    }

    private async Task ActivateHubWhenReadyAsync(HubWindow hubWindow)
    {
        await hubWindow.WaitForCurrentContentReadyAsync();
        if (ReferenceEquals(_hubWindow, hubWindow) && !hubWindow.IsClosed)
            hubWindow.Activate();
    }

    private void ShowSettings()
    {
        ShowHub("settings");
    }

    private void OnSettingsCommandCenterRequested(object? sender, EventArgs e)
    {
        ShowStatusDetail();
    }

    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        var currentSnapshot = _settings?.ToSettingsData()?.ToConnectionSnapshot();
        var impact = SettingsChangeClassifier.Classify(_previousSettingsSnapshot, currentSnapshot);
        _previousSettingsSnapshot = currentSnapshot;
        Logger.Info($"[SETTINGS] Change impact: {impact}");

        switch (impact)
        {
            case SettingsChangeImpact.FullReconnectRequired:
            case SettingsChangeImpact.OperatorReconnectRequired:
                // Full reconnect: tear down everything and rebuild
                _appState!.GatewaySelf = null;
                if (_settings?.UseSshTunnel != true)
                {
                    _sshTunnelService?.Stop();
                }
                // Status is updated by OnManagerStateChanged when reconnect starts.
                UpdateTrayIcon();

                // Reset chat window — it has a stale URL/token
                if (_chatWindow != null)
                {
                    _chatWindow.ForceClose();
                    _chatWindow = null;
                }

                _ = _connectionManager?.ReconnectAsync();
                break;

            case SettingsChangeImpact.NodeReconnectRequired:
                _ = _connectionManager?.ReconnectAsync();
                break;

            case SettingsChangeImpact.CapabilityReload:
                _ = _connectionManager?.ReconnectAsync();
                break;

            case SettingsChangeImpact.UiOnly:
            case SettingsChangeImpact.NoOp:
                // No connection changes needed
                break;
        }

        // MCP server lifecycle — handled separately from gateway reconnects
        // because MCP-only mode doesn't involve a gateway at all. SetMcpEnabled
        // checks actual runtime state (_mcpServer != null), so it's safe to
        // call unconditionally. Only create NodeService when MCP is being
        // enabled or the service already exists.
        if (_settings != null && (_nodeService != null || _settings.EnableMcpServer))
        {
            var nodeService = EnsureNodeService(_settings);
            nodeService?.SetMcpEnabled(_settings.EnableMcpServer);
            WireAppCapabilityHandlers();
        }

        // Non-connection settings always applied regardless of impact
        if (_settings!.GlobalHotkeyEnabled)
        {
            _globalHotkey ??= new GlobalHotkeyService();
            _globalHotkey.VoiceHotkeyPressed -= OnVoiceHotkeyPressed;
            _globalHotkey.VoiceHotkeyPressed += OnVoiceHotkeyPressed;
            _globalHotkey.SettingsHotkeyPressed -= OnSettingsHotkeyPressed;
            _globalHotkey.SettingsHotkeyPressed += OnSettingsHotkeyPressed;
            _globalHotkey.Register();
        }
        else
        {
            _globalHotkey?.Unregister();
        }

        AutoStartManager.SetAutoStart(_settings.AutoStart);

        // Notify ad-hoc listeners (e.g. ChatWindow may be alive but not
        // owned by the hub) that settings have changed. Marshal onto the
        // UI thread because IAppCommands.NotifySettingsSaved is a public
        // entry point that may be invoked from background work; existing
        // handlers (DebugPage, ChatWindow) update UI directly and would
        // crash if dispatched from a non-UI thread (Hanselman v2 #7).
        if (_dispatcherQueue != null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => SettingsChanged?.Invoke(this, EventArgs.Empty));
        }
        else
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ShowWebChat()
    {
        if (_settings == null) return;
        if (!TryResolveChatCredentials(out _, out _, out _, out var isBootstrapToken))
        {
            ShowConnectionSettingsForPairingIssue(
                "Chat",
                "Gateway URL or credential is not configured");
            return;
        }

        if (isBootstrapToken)
        {
            ShowConnectionSettingsForPairingIssue(
                "Chat",
                "Gateway pairing is not complete");
            return;
        }

        ShowHub("chat");
    }

    private void ShowStatusDetail()
    {
        ShowHub("connection");
    }

    private void ShowConnectionStatusWindow()
    {
        if (_connectionStatusWindow != null && !_connectionStatusWindow.IsClosed)
        {
            _connectionStatusWindow.Activate();
            return;
        }
        _connectionStatusWindow = new ConnectionStatusWindow(
            _connectionManager!.Diagnostics,
            _gatewayRegistry,
            _connectionManager);
        _connectionStatusWindow.Activate();
    }

    // ─── Inbound pairing approvals ───────────────────────────────────────

    /// <summary>Feeds fresh device/node pending pair-lists into the approval coordinator.</summary>
    private void OnPairListsChanged(object? sender, EventArgs e)
    {
        _pairingApprovalCoordinator?.OnPairListsUpdated(_appState?.DevicePairList, _appState?.NodePairList);
    }

    /// <summary>A new inbound pairing request arrived — present the focused dialog and an awareness toast.</summary>
    private void OnPairingApprovalRequested(object? sender, PendingApproval approval)
    {
        OnUiThread(() =>
        {
            ShowPairingApprovalDialog();

            var name = string.IsNullOrWhiteSpace(approval.DisplayName)
                ? approval.DeviceId
                : approval.DisplayName!;
            AddRecentActivity(
                LocalizationHelper.GetString("Toast_PairingRequestTitle"),
                category: "pairing",
                dashboardPath: "connection",
                details: name);

            var bodyKey = approval.Kind == PairingApprovalKind.Node
                ? "Toast_PairingRequestBodyNode"
                : "Toast_PairingRequestBody";
            _toastService?.ShowToast(new ToastContentBuilder()
                .AddText(LocalizationHelper.GetString("Toast_PairingRequestTitle"))
                .AddText(string.Format(LocalizationHelper.GetString(bodyKey), name))
                .AddButton(new ToastButton()
                    .SetContent(LocalizationHelper.GetString("Toast_PairingReview"))
                    .AddArgument("action", "review_pairing")),
                "pairing-request",
                approval.DecisionId);
        });
    }

    /// <summary>After a decision succeeds, confirm with a toast + activity entry.</summary>
    private void OnPairingDecisionCompleted(object? sender, PairingDecisionResult result)
    {
        if (!result.Success) return; // failures are retried in the dialog
        OnUiThread(() =>
        {
            var name = string.IsNullOrWhiteSpace(result.Approval.DisplayName)
                ? result.Approval.DeviceId
                : result.Approval.DisplayName!;
            var titleKey = result.Approved ? "Toast_PairingApprovedTitle" : "Toast_PairingRejectedTitle";
            var bodyKey = result.Approved ? "Toast_PairingApprovedBody" : "Toast_PairingRejectedBody";
            AddRecentActivity(
                LocalizationHelper.GetString(titleKey),
                category: "pairing",
                dashboardPath: "connection",
                details: name);
            _toastService?.ShowToast(new ToastContentBuilder()
                .AddText(LocalizationHelper.GetString(titleKey))
                .AddText(string.Format(LocalizationHelper.GetString(bodyKey), name)),
                "pairing-decided",
                result.Approval.DecisionId);
        });
    }

    /// <summary>Opens (or re-focuses) the inbound pairing approval dialog when there is something to decide.</summary>
    private void ShowPairingApprovalDialog()
    {
        if (_pairingApprovalCoordinator == null) return;
        if (_pairingApprovalCoordinator.Current.Count == 0) return;

        if (_pairingApprovalDialog is { IsClosed: false } existing)
        {
            existing.ShowForeground();
            return;
        }

        _pairingApprovalDialog = new OpenClawTray.Dialogs.PairingApprovalDialog(_pairingApprovalCoordinator);
        _pairingApprovalDialog.ShowForeground();
    }

    private void RestartSshTunnel()
    {
        if (_settings?.UseSshTunnel != true)
        {
            _toastService!.ShowToast(new ToastContentBuilder()
                .AddText("SSH tunnel")
                .AddText("Managed SSH tunnel mode is not enabled."));
            return;
        }

        try
        {
            Logger.Info("Restarting managed SSH tunnel from Command Center");
            DiagnosticsJsonlService.Write("tunnel.restart_requested", new
            {
                localEndpoint = _settings.SshTunnelLocalPort > 0 ? $"127.0.0.1:{_settings.SshTunnelLocalPort}" : null,
                remotePort = _settings.SshTunnelRemotePort
            });

            _sshTunnelService?.Stop();
            // Status is updated by OnManagerStateChanged when reconnect completes.
            UpdateTrayIcon();

            if (!EnsureSshTunnelConfigured())
            {
                UpdateStatusDetailWindow();
                _toastService!.ShowToast(new ToastContentBuilder()
                    .AddText("SSH tunnel restart failed")
                    .AddText(_sshTunnelService?.LastError ?? "Check SSH tunnel settings and logs."));
                return;
            }

            _ = _connectionManager?.ReconnectAsync();

            UpdateStatusDetailWindow();
            _toastService!.ShowToast(new ToastContentBuilder()
                .AddText("SSH tunnel")
                .AddText("Restarted; reconnecting to gateway."));
        }
        catch (Exception ex)
        {
            Logger.Error($"SSH tunnel restart request failed: {ex.Message}");
            DiagnosticsJsonlService.Write("tunnel.restart_request_failed", new { ex.Message });
            _toastService!.ShowToast(new ToastContentBuilder()
                .AddText("SSH tunnel restart failed")
                .AddText(ex.Message));
        }
    }

    private async Task RefreshCommandCenterAsync()
    {
        await RunHealthCheckAsync(userInitiated: true);
        var client = _connectionManager?.OperatorClient;
        if (client != null)
        {
            await client.RequestSessionsAsync();
            await client.RequestUsageAsync();
            await client.RequestNodesAsync();
        }
        UpdateStatusDetailWindow();
    }

    private void UpdateStatusDetailWindow()
    {
        // No-op — hub window observes AppState.PropertyChanged directly.
        // Tray status detail window reads from AppState too.
    }

    internal GatewayCommandCenterState BuildCommandCenterState() =>
        new CommandCenterStateBuilder(CaptureSnapshot()).Build();

    private AppStateSnapshot CaptureSnapshot() => new AppStateSnapshot
    {
        Status = _appState!.Status,
        LastCheckTime = _appState!.LastCheckTime,
        Channels = _appState!.Channels,
        Sessions = _appState!.Sessions,
        Nodes = _appState!.Nodes,
        Usage = _appState!.Usage,
        UsageStatus = _appState!.UsageStatus,
        UsageCost = _appState!.UsageCost,
        GatewaySelf = _appState!.GatewaySelf,
        AuthFailureMessage = _appState!.AuthFailureMessage,
        LastUpdateInfo = _appState!.UpdateInfo,
        Settings = _settings,
        NodeService = _nodeService,
        SshTunnelSnapshot = _sshTunnelService?.CreateSnapshot(),
        HasGatewayClient = _connectionManager?.OperatorClient != null
    };

    private void ShowNotificationHistory()
    {
        // ActivityPage removed; legacy callers now land on the Channels page.
        ShowHub("channels");
    }

    private void ShowActivityStream(string? filter = null)
    {
        // ActivityPage removed; legacy callers now land on the Channels page.
        _ = filter;
        ShowHub("channels");
    }

    private async Task ShowOnboardingAsync()
    {
        if (_settings == null)
            return;

        if (_setupWindow != null)
        {
            var existingSetupWindow = _setupWindow;
            await existingSetupWindow.WaitForInitialContentReadyAsync();
            if (ReferenceEquals(_setupWindow, existingSetupWindow) && !existingSetupWindow.IsClosed)
                existingSetupWindow.BringToFrontForSetupLaunch();
            return;
        }

        try
        {
            var setupWindow = new SetupWindow();
            _setupWindow = setupWindow;
            setupWindow.AdvancedSetupRequested += OnSetupAdvancedSetupRequested;
            setupWindow.SetupCompleted += OnSetupCompleted;
            setupWindow.Closed += (_, _) =>
            {
                if (ReferenceEquals(_setupWindow, setupWindow))
                    _setupWindow = null;
            };
            await setupWindow.WaitForInitialContentReadyAsync();
            if (ReferenceEquals(_setupWindow, setupWindow) && !setupWindow.IsClosed)
            {
                setupWindow.BringToFrontForSetupLaunch();
                Logger.Info("Opened tray-hosted setup window");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to open setup window: {ex}");
        }
    }

    private void OnSetupAdvancedSetupRequested(object? sender, EventArgs e)
    {
        ShowHub("connection");
        _setupWindow?.Close();
    }

    private void OnSetupCompleted(object? sender, SetupCompletedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => RestartAfterSetupAsync(e.EnableAutoStart),
            new AppLogger(),
            nameof(OnSetupCompleted));

    private async Task RestartAfterSetupAsync(bool enableAutoStart)
    {
        var exePath = ResolveCurrentExecutablePath();
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            await ShowSetupRestartErrorAsync("OpenClaw setup finished, but the tray executable could not be found for restart.");
            return;
        }

        try
        {
            if (enableAutoStart)
            {
                try
                {
                    AutoStartManager.SetAutoStart(true);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to enable autostart after setup: {ex}");
                }
            }

            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--post-setup-restart");
            psi.ArgumentList.Add("--wait-for-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--post-setup-launch");
            psi.ArgumentList.Add("chat");

            var restarted = Process.Start(psi);
            if (restarted == null)
                throw new InvalidOperationException("Process.Start returned null.");
            restarted.Dispose();

            Logger.Info("Started post-setup tray restart process");
            _setupWindow?.Close();
            await ExitApplicationAsync();
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to restart tray after setup: {ex}");
            await ShowSetupRestartErrorAsync("OpenClaw setup finished, but restarting the tray failed. The current tray will keep running; please exit and reopen OpenClaw.");
        }
    }

    private async Task ShowSetupRestartErrorAsync(string message)
    {
        if (_setupWindow?.Content is not FrameworkElement root || root.XamlRoot is null)
        {
            Logger.Error(message);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Restart OpenClaw",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = root.XamlRoot,
        };

        await dialog.ShowAsync();
    }

    private static string? ResolveCurrentExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            return Environment.ProcessPath;

        try
        {
            return Process.GetCurrentProcess().MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private void ShowSurfaceImprovementsTipIfNeeded()
    {
        if (_settings == null || _settings.HasSeenActivityStreamTip) return;

        _settings.HasSeenActivityStreamTip = true;
        _settings.Save();

        try
        {
            _toastService!.ShowToast(new ToastContentBuilder()
                .AddText(LocalizationHelper.GetString("Toast_ActivityStreamTip"))
                .AddText(LocalizationHelper.GetString("Toast_ActivityStreamTipDetail"))
                .AddButton(new ToastButton()
                    .SetContent(LocalizationHelper.GetString("Toast_ActivityStreamTipButton"))
                    .AddArgument("action", "open_activity")));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to show activity stream tip: {ex.Message}");
        }
    }

    #endregion

    private bool TryResolveChatCredentials(
        out string gatewayUrl,
        out string token,
        out string credentialSource,
        out bool isBootstrapToken)
    {
        gatewayUrl = string.Empty;
        token = string.Empty;
        credentialSource = "none";
        isBootstrapToken = false;

        if (_settings == null)
            return false;

        if (!InteractiveGatewayCredentialResolver.TryResolve(
            _gatewayRegistry,
            SettingsManager.SettingsDirectoryPath,
            DeviceIdentityFileReader.Instance,
            _settings.GetEffectiveGatewayUrl(),
            _settings.LegacyToken,
            _settings.LegacyBootstrapToken,
            out var credential) ||
            credential == null)
        {
            return false;
        }

        gatewayUrl = credential.GatewayUrl;
        token = credential.Token;
        credentialSource = credential.Source;
        isBootstrapToken = credential.IsBootstrapToken;
        return true;
    }

    #region Actions

    private void OpenDashboard(string? path = null)
    {
        if (_settings == null) return;
        if (!EnsureSshTunnelConfigured()) return;

        if (!TryResolveChatCredentials(out var gatewayUrl, out var token, out var credentialSource, out var isBootstrapToken))
        {
            ShowConnectionSettingsForPairingIssue(
                "Dashboard",
                "Gateway URL or credential is not configured");
            return;
        }

        var url = GatewayDashboardUrlBuilder.Build(
            gatewayUrl,
            path,
            token,
            !isBootstrapToken && credentialSource == CredentialResolver.SourceSharedGatewayToken);

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to open dashboard: {ex.Message}");
        }
    }

    // ── IAppCommands implementation ─────────────────────────────────────

    void IAppCommands.OpenDashboard(string? path) => OpenDashboard(path);
    void IAppCommands.Navigate(string pageTag) => ShowHub(pageTag);
    void IAppCommands.Navigate(string pageTag, string? originTag) => ShowHub(pageTag, originTag: originTag);
    void IAppCommands.Reconnect() => _ = _connectionManager?.ReconnectAsync();
    void IAppCommands.Disconnect()
    {
        _ = _connectionManager?.DisconnectAsync();
        UpdateTrayIcon();
    }
    void IAppCommands.ShowVoiceOverlay() => ShowHub("voice");
    void IAppCommands.ShowChat() => ShowChatWindow();
    void IAppCommands.CheckForUpdates() => _ = CheckForUpdatesUserInitiatedAsync();
    void IAppCommands.ShowOnboarding() => _ = ShowOnboardingAsync();
    void IAppCommands.ShowConnectionStatus() => ShowConnectionStatusWindow();
    void IAppCommands.NotifySettingsSaved() => OnSettingsSaved(this, EventArgs.Empty);

    private void ToggleChannel(string channelName) =>
        AsyncEventHandlerGuard.Run(
            () => ToggleChannelAsync(channelName),
            new AppLogger(),
            nameof(ToggleChannel));

    private async Task ToggleChannelAsync(string channelName)
    {
        var client = _connectionManager?.OperatorClient;
        if (client == null) return;

        var channel = _appState!.Channels.FirstOrDefault(c => c.Name == channelName);
        if (channel == null) return;

        try
        {
            var isRunning = ChannelHealth.IsHealthyStatus(channel.Status);
            if (isRunning)
            {
                await client.StopChannelAsync(channelName);
                AddRecentActivity($"Stopped channel: {channelName}", category: "channel", dashboardPath: "settings");
            }
            else
            {
                await client.StartChannelAsync(channelName);
                AddRecentActivity($"Started channel: {channelName}", category: "channel", dashboardPath: "settings");
            }
             
            // Refresh health
            await RunHealthCheckAsync();
        }
        catch (Exception ex)
        {
            AddRecentActivity($"Channel toggle failed: {channelName}", category: "channel", details: ex.Message);
            Logger.Error($"Failed to toggle channel: {ex.Message}");
        }
    }

    private void ToggleAutoStart()
    {
        if (_settings == null) return;
        _settings.AutoStart = !_settings.AutoStart;
        _settings.Save();
        AutoStartManager.SetAutoStart(_settings.AutoStart);
    }

    private void OpenLogFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Logger.LogFilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to open log file: {ex.Message}");
        }
    }

    private void OpenLogFolder()
    {
        OpenFolder(Path.GetDirectoryName(Logger.LogFilePath), "logs");
    }

    private void OpenConfigFolder()
    {
        OpenFolder(SettingsManager.SettingsDirectoryPath, "config");
    }

    private void OpenDiagnosticsFolder()
    {
        OpenFolder(Path.GetDirectoryName(DiagnosticsJsonlService.FilePath), "diagnostics");
    }

    private static void OpenFolder(string? folderPath, string label)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            Logger.Warn($"Failed to open {label} folder: path is not configured");
            return;
        }

        try
        {
            Directory.CreateDirectory(folderPath);
            Process.Start(new ProcessStartInfo(folderPath) { UseShellExecute = true });
            Logger.Info($"Opened {label} folder: {folderPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Logger.Warn($"Failed to open {label} folder {folderPath}: {ex.Message}");
        }
    }

    private void OnVoiceHotkeyPressed(object? sender, EventArgs e)
    {
        if (_dispatcherQueue == null) return;
        _dispatcherQueue.TryEnqueue(() =>
        {
            // Always set the flag first — ChatPage checks it during navigation
            var hubExisted = _hubWindow != null;
            ShowHub("chat");
            if (_hubWindow == null) return;

            if (_hubWindow.CurrentPage is Pages.ChatPage chatPage)
            {
                // Chat page is already visible — trigger voice directly
                chatPage.TriggerAutoStartVoice();
            }
            else
            {
                // Chat page is being created — set the flag for ChatPage.Initialize to pick up.
                // Also schedule a delayed trigger in case the flag isn't consumed during navigation.
                _hubWindow.PendingAutoStartVoice = true;
                _dispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (_hubWindow?.PendingAutoStartVoice == true &&
                        _hubWindow.CurrentPage is Pages.ChatPage cp)
                    {
                        _hubWindow.PendingAutoStartVoice = false;
                        cp.TriggerAutoStartVoice();
                    }
                });
            }
        });
    }

    private void OnSettingsHotkeyPressed(object? sender, EventArgs e)
    {
        OnUiThread(() => ShowHub("companion"));
    }

    #endregion

    #region Updates

    private static UpdateCommandCenterInfo BuildInitialUpdateInfo() => new()
    {
        Status = "Not checked",
        CurrentVersion = AppVersionInfo.Version
    };

    // Cross-path concurrency for update checks, split into two phases:
    //  - _updateCheckGate: held only during the metadata/network check.
    //    Short timeout so contended callers don't block on user thinking.
    //  - _updateInstallInProgress: Interlocked flag covering the user-facing
    //    UpdateDialog + download + install. Prevents two parallel installs
    //    without holding a lock across user interaction.
    private readonly System.Threading.SemaphoreSlim _updateCheckGate = new(1, 1);
#if !DEBUG
    private int _updateInstallInProgress;
#endif

    private async Task<bool> CheckForUpdatesAsync(bool userInitiated = false)
    {
        // === Stage 1: metadata check (gate-protected) ===
        if (!await _updateCheckGate.WaitAsync(TimeSpan.FromSeconds(30)))
        {
            Logger.Warn("Update check gate timed out: another check is in progress");
            if (_appState != null)
            {
                _appState.UpdateInfo = new UpdateCommandCenterInfo
                {
                    Status = "Failed",
                    CurrentVersion = AppVersionInfo.Version,
                    CheckedAt = DateTime.UtcNow,
                    Detail = "another update check is already in progress; try again in a moment"
                };
            }
            return true; // Don't block launch
        }

#if DEBUG
        try
        {
            Logger.Info("Skipping update check in debug build");
            _appState!.UpdateInfo = new UpdateCommandCenterInfo
            {
                Status = "Skipped",
                CurrentVersion = AppVersionInfo.Version,
                CheckedAt = DateTime.UtcNow,
                Detail = "debug build"
            };
            return true;
        }
        finally
        {
            _updateCheckGate.Release();
        }
#else
        string releaseTag;
        string changelog;
        try
        {
            Logger.Info("Checking for updates...");
            _appState!.UpdateInfo = new UpdateCommandCenterInfo
            {
                Status = "Checking",
                CurrentVersion = AppVersionInfo.Version,
                CheckedAt = DateTime.UtcNow
            };
            var updateFound = await AppUpdater.CheckForUpdatesAsync();

            if (!updateFound)
            {
                Logger.Info("No updates available");
                _appState!.UpdateInfo = new UpdateCommandCenterInfo
                {
                    Status = "Current",
                    CurrentVersion = AppVersionInfo.Version,
                    CheckedAt = DateTime.UtcNow,
                    Detail = "no updates available"
                };
                return true;
            }

            var release = AppUpdater.LatestRelease!;
            if (string.IsNullOrEmpty(release.TagName))
            {
                // Defensive: AppUpdater says an update is available but the
                // release has no tag. Don't silently claim "up to date" —
                // surface as Failed so the user sees something is off.
                Logger.Warn("Update reported available but release has no TagName");
                _appState!.UpdateInfo = new UpdateCommandCenterInfo
                {
                    Status = "Failed",
                    CurrentVersion = AppVersionInfo.Version,
                    CheckedAt = DateTime.UtcNow,
                    Detail = "update metadata incomplete (missing version tag)"
                };
                return true;
            }

            releaseTag = release.TagName;
            changelog = AppUpdater.GetChangelog(true) ?? "No release notes available.";
            Logger.Info($"Update available: {releaseTag}");
            _appState!.UpdateInfo = new UpdateCommandCenterInfo
            {
                Status = "Available",
                CurrentVersion = AppVersionInfo.Version,
                LatestVersion = releaseTag,
                CheckedAt = DateTime.UtcNow,
                Detail = "prompted"
            };

            if (!string.IsNullOrWhiteSpace(_settings?.SkippedUpdateTag) &&
                string.Equals(_settings.SkippedUpdateTag, releaseTag, StringComparison.OrdinalIgnoreCase) &&
                !userInitiated)
            {
                Logger.Info($"Skipping update prompt for remembered version {releaseTag}");
                _appState!.UpdateInfo.Detail = "skipped by user";
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Info("Update check cancelled");
            if (_appState != null)
            {
                // Avoid leaving Status="Checking" stale for the manual flow
                // or the command-center UI. Surface as Failed with a clear
                // "cancelled" detail.
                _appState.UpdateInfo = new UpdateCommandCenterInfo
                {
                    Status = "Failed",
                    CurrentVersion = AppVersionInfo.Version,
                    CheckedAt = DateTime.UtcNow,
                    Detail = "update check cancelled"
                };
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Update check failed: {ex.Message}");
            if (_appState != null)
            {
                _appState.UpdateInfo = new UpdateCommandCenterInfo
                {
                    Status = "Failed",
                    CurrentVersion = AppVersionInfo.Version,
                    CheckedAt = DateTime.UtcNow,
                    Detail = ex.Message
                };
            }
            return true;
        }
        finally
        {
            // Release the gate BEFORE user interaction & download/install.
            // Holding it across these long phases would silently time-out
            // any concurrent manual click.
            _updateCheckGate.Release();
        }

        // === Stage 2: user-interactive prompt + download/install ===
        // Gate is released. Use Interlocked flag so concurrent callers can't
        // start a second parallel install while we're prompting/downloading.
        if (System.Threading.Interlocked.CompareExchange(ref _updateInstallInProgress, 1, 0) != 0)
        {
            Logger.Info("Update prompt/install already in progress; skipping");
            if (_appState != null)
            {
                _appState.UpdateInfo = new UpdateCommandCenterInfo
                {
                    Status = "Failed",
                    CurrentVersion = AppVersionInfo.Version,
                    CheckedAt = DateTime.UtcNow,
                    Detail = "an update is already being downloaded or installed"
                };
            }
            return true;
        }

        try
        {
            var dialog = new UpdateDialog(releaseTag, changelog);
            UpdateDialogResult result;
            try
            {
                result = await dialog.ShowAsync();
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                // Visual tree torn down mid-await (e.g. window closed).
                // Treat as "remind me later" rather than tainting Status with
                // "Failed" — the network check itself succeeded.
                Logger.Warn($"[Update] Prompt dialog dismissed before completion: 0x{ex.HResult:X8}");
                return true;
            }
            catch (InvalidOperationException ex)
            {
                // Another ContentDialog is already open on this XamlRoot.
                Logger.Warn($"[Update] Prompt dialog could not be shown: {ex.Message}");
                return true;
            }

            if (result == UpdateDialogResult.Download)
            {
                // Assign a fresh object rather than mutating .Detail in place:
                // a concurrent loser of the install-flag CAS may have just
                // overwritten _appState.UpdateInfo with a "Failed" object,
                // and mutating its Detail would leave Status="Failed" with
                // our "download requested" detail — briefly inconsistent.
                _appState!.UpdateInfo = new UpdateCommandCenterInfo
                {
                    Status = "Available",
                    CurrentVersion = AppVersionInfo.Version,
                    LatestVersion = releaseTag,
                    CheckedAt = DateTime.UtcNow,
                    Detail = "download requested"
                };
                if (_settings != null)
                {
                    _settings.SkippedUpdateTag = string.Empty;
                    _settings.Save();
                }
                var installed = await DownloadAndInstallUpdateAsync();
                if (!installed)
                {
                    // Surface the failure so callers (and the manual-check
                    // dialog) don't show stale "download requested" state.
                    _appState!.UpdateInfo = new UpdateCommandCenterInfo
                    {
                        Status = "Failed",
                        CurrentVersion = AppVersionInfo.Version,
                        CheckedAt = DateTime.UtcNow,
                        Detail = "download or install failed"
                    };
                }
                return !installed; // Don't launch if update succeeded
            }

            if (result == UpdateDialogResult.Skip && _settings != null)
            {
                _settings.SkippedUpdateTag = releaseTag;
                _settings.Save();
                _appState!.UpdateInfo = new UpdateCommandCenterInfo
                {
                    Status = "Available",
                    CurrentVersion = AppVersionInfo.Version,
                    LatestVersion = releaseTag,
                    CheckedAt = DateTime.UtcNow,
                    Detail = "skipped by user"
                };
            }
            else if (userInitiated && _settings != null
                && string.Equals(_settings.SkippedUpdateTag, releaseTag,
                                 StringComparison.OrdinalIgnoreCase))
            {
                // User explicitly bypassed the remembered skip for THIS
                // release and picked RemindLater — clear the stale tag.
                _settings.SkippedUpdateTag = string.Empty;
                _settings.Save();
            }

            return true; // RemindLater or Skip - continue launch
        }
        catch (Exception ex)
        {
            Logger.Warn($"Update prompt/install failed: {ex.Message}");
            if (_appState != null)
            {
                _appState.UpdateInfo = new UpdateCommandCenterInfo
                {
                    Status = "Failed",
                    CurrentVersion = AppVersionInfo.Version,
                    CheckedAt = DateTime.UtcNow,
                    Detail = ex.Message
                };
            }
            return true;
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _updateInstallInProgress, 0);
        }
#endif
    }

    // Re-entrancy guard: the button/menu/deep-link are all fire-and-forget
    // (`_ = CheckForUpdatesUserInitiatedAsync()`), so a double-click would
    // otherwise open two ContentDialogs on the same XamlRoot which throws
    // COMException. One in-flight manual check at a time is enough.
    private int _manualUpdateCheckInFlight;

    private async Task CheckForUpdatesUserInitiatedAsync()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _manualUpdateCheckInFlight, 1, 0) != 0)
        {
            Logger.Info("Manual update check ignored: another check is already in progress");
            return;
        }

        try
        {
            Logger.Info("Manual update check requested");
            // Pass userInitiated=true so an explicit click bypasses the
            // "remind me later" SkippedUpdateTag — the user is asking *now*.
            var shouldContinue = await CheckForUpdatesAsync(userInitiated: true);
            UpdateStatusDetailWindow();

            // The "Available" path already prompts via UpdateDialog. For the
            // other terminal states a manual click would otherwise produce no
            // UI at all, leaving users wondering whether the click registered.
            // Surface each explicitly with a small OK dialog.
            var info = _appState?.UpdateInfo;
            if (info != null)
            {
                switch (info.Status)
                {
                    case "Current":
                        await ShowUpdateInfoDialogAsync(
                            "UpToDate",
                            LocalizationHelper.GetString("Update_Title_UpToDate"),
                            LocalizationHelper.Format("Update_Message_UpToDate", info.CurrentVersion));
                        break;
                    case "Failed":
                        // Format string ends with "\n\n{0}"; an empty Detail
                        // would leave a dangling blank line. Trim only the
                        // newline characters we added, never arbitrary
                        // whitespace from the localized string.
                        var failedMessage = LocalizationHelper
                            .Format("Update_Message_Failed", info.Detail ?? "")
                            .TrimEnd('\r', '\n');
                        await ShowUpdateInfoDialogAsync(
                            "Failed",
                            LocalizationHelper.GetString("Update_Title_Failed"),
                            failedMessage);
                        break;
#if DEBUG
                    // Status="Skipped" is only produced by the DEBUG short-circuit
                    // in CheckForUpdatesAsync. User-skipped versions keep
                    // Status="Available", so this case must not exist in RELEASE
                    // or it would surface a confusing "disabled in debug builds"
                    // dialog to end users.
                    case "Skipped":
                        await ShowUpdateInfoDialogAsync(
                            "Skipped",
                            LocalizationHelper.GetString("Update_Title_Skipped"),
                            LocalizationHelper.GetString("Update_Message_Skipped_Debug"));
                        break;
#endif
                }
            }

            if (!shouldContinue)
            {
                Exit();
            }
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _manualUpdateCheckInFlight, 0);
        }
    }

    private async Task ShowUpdateInfoDialogAsync(string logKey, string title, string message)
    {
        // Prefer the Hub window when open so the dialog appears modal to what
        // the user is actually looking at; fall back to the hidden keep-alive
        // window so the dialog still renders if the Hub has been dismissed.
        XamlRoot? xamlRoot = null;
        if (_hubWindow != null && !_hubWindow.IsClosed)
            xamlRoot = (_hubWindow.Content as FrameworkElement)?.XamlRoot;
        if (xamlRoot == null)
            xamlRoot = (_keepAliveWindow?.Content as FrameworkElement)?.XamlRoot;
        if (xamlRoot == null)
        {
            // Log the stable English key, not the localized title, so log
            // grepping works across locales.
            Logger.Warn($"[Update] No XAML root available to show dialog: {logKey}");
            return;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = LocalizationHelper.GetString("Update_OK"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot
        };
        try
        {
            await dialog.ShowAsync();
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // ContentDialog.ShowAsync throws COMException if its XamlRoot's
            // visual tree is torn down mid-await (e.g. Hub window closed).
            Logger.Warn($"[Update] Dialog dismissed before completion ({logKey}): 0x{ex.HResult:X8}");
        }
        catch (InvalidOperationException ex)
        {
            // WinUI throws InvalidOperationException when another ContentDialog
            // is already open on the same thread/XamlRoot. The re-entrancy
            // guard only blocks duplicate *update* dialogs; collisions with
            // other features' dialogs (onboarding, connection, etc.) must be
            // tolerated here so the fire-and-forget call sites don't crash.
            Logger.Warn($"[Update] Dialog could not be shown ({logKey}): {ex.Message}");
        }
    }

    private async Task<bool> DownloadAndInstallUpdateAsync()
    {
        DownloadProgressDialog? progressDialog = null;
        try
        {
            progressDialog = new DownloadProgressDialog(AppUpdater);
            progressDialog.ShowAsync(); // Fire and forget

            var downloadedAsset = await AppUpdater.DownloadUpdateAsync();

            TryCloseProgressDialog(progressDialog);

            if (downloadedAsset == null || !System.IO.File.Exists(downloadedAsset.FilePath))
            {
                Logger.Error("Update download failed or file missing");
                return false;
            }

            Logger.Info("Installing update and restarting...");
            await AppUpdater.InstallUpdateAsync(downloadedAsset);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Update failed: {ex.Message}");
            TryCloseProgressDialog(progressDialog);
            return false;
        }
    }

    private static void TryCloseProgressDialog(DownloadProgressDialog? dialog)
    {
        if (dialog == null) return;
        try
        {
            dialog.Close();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Window already closed — closing a closed WinUI window throws
            // COMException 0x80070578. Swallow so a real exception in the
            // outer catch isn't masked by this cleanup failure.
        }
        catch (InvalidOperationException)
        {
            // Same as above for other "already-disposed" race variants.
        }
    }

    #endregion

    #region Deep Links

    private void StartDeepLinkServer()
    {
        _deepLinkCts = new CancellationTokenSource();
        var token = _deepLinkCts.Token;
        
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(
                        DeepLinkPipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                        inBufferSize: DeepLinkSecurityPolicy.MaxIpcMessageBytes,
                        outBufferSize: 0);
                    await pipe.WaitForConnectionAsync(token);
                    var uri = await ReadDeepLinkIpcPayloadAsync(pipe, token);
                    if (!string.IsNullOrEmpty(uri))
                    {
                        Logger.Info($"Received deep link via IPC: {DeepLinkSecurityPolicy.RedactForLog(uri)}");
                        OnUiThread(() => _ = HandleDeepLinkAsync(uri));
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.Info("Deep link server stopping (canceled)");
                    break; // Normal shutdown
                }
                catch (InvalidDataException ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        Logger.Warn($"Rejected deep link IPC payload: {ex.Message}");
                    }
                }
                catch (TimeoutException ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        Logger.Warn($"Rejected deep link IPC payload: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        Logger.Warn($"Deep link server error: {ex.Message}");
                        try { await Task.Delay(1000, token); } catch { break; }
                    }
                }
            }
        }, token);
    }

    private static async Task<string?> ReadDeepLinkIpcPayloadAsync(Stream stream, CancellationToken appToken)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        readCts.CancelAfter(DeepLinkSecurityPolicy.IpcReadTimeout);

        var scratch = new byte[1024];
        var payload = new byte[DeepLinkSecurityPolicy.MaxIpcMessageBytes + 1];
        var totalBytes = 0;

        try
        {
            while (true)
            {
                var remaining = payload.Length - totalBytes;
                if (remaining <= 0)
                    throw new InvalidDataException("payload exceeds maximum size");

                var read = await stream.ReadAsync(
                    scratch.AsMemory(0, Math.Min(scratch.Length, remaining)),
                    readCts.Token);
                if (read == 0)
                    break;

                scratch.AsSpan(0, read).CopyTo(payload.AsSpan(totalBytes));
                totalBytes += read;
                if (totalBytes > DeepLinkSecurityPolicy.MaxIpcMessageBytes)
                    throw new InvalidDataException("payload exceeds maximum size");
            }
        }
        catch (OperationCanceledException) when (!appToken.IsCancellationRequested)
        {
            throw new TimeoutException("timed out while reading payload");
        }

        if (totalBytes == 0)
            return null;

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(payload, 0, totalBytes)
                .TrimEnd('\r', '\n');
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("payload is not valid UTF-8", ex);
        }
    }

    private async Task HandleDeepLinkAsync(string uri)
    {
        var result = DeepLinkParser.ParseDeepLink(uri);
        if (result == null)
        {
            Logger.Warn($"Rejected invalid deep link: {DeepLinkSecurityPolicy.RedactForLog(uri)}");
            return;
        }

        if (DeepLinkSecurityPolicy.RequiresConfirmation(result))
        {
            var confirmed = await ConfirmDeepLinkActionAsync(result);
            if (!confirmed)
            {
                Logger.Warn($"Rejected unconfirmed deep link action: {DeepLinkSecurityPolicy.RedactForLog(uri)}");
                return;
            }
        }

        HandleDeepLink(uri);
    }

    private void HandleDeepLink(string uri)
    {
        DeepLinkHandler.Handle(uri, new DeepLinkActions
        {
            OpenSettings = ShowSettings,
            OpenSetup = () => _ = ShowOnboardingAsync(),
            RunHealthCheck = () => RunHealthCheckAsync(userInitiated: true),
            CheckForUpdates = CheckForUpdatesUserInitiatedAsync,
            OpenLogFile = OpenLogFile,
            OpenLogFolder = OpenLogFolder,
            OpenConfigFolder = OpenConfigFolder,
            OpenDiagnosticsFolder = OpenDiagnosticsFolder,
            OpenConnectionStatus = ShowConnectionStatusWindow,
            CopySupportContext = _diagnosticsClipboard!.CopySupportContext,
            CopyDebugBundle = _diagnosticsClipboard!.CopyDebugBundle,
            CopyBrowserSetupGuidance = _diagnosticsClipboard!.CopyBrowserSetupGuidance,
            CopyPortDiagnostics = _diagnosticsClipboard!.CopyPortDiagnostics,
            CopyCapabilityDiagnostics = _diagnosticsClipboard!.CopyCapabilityDiagnostics,
            CopyNodeInventory = _diagnosticsClipboard!.CopyNodeInventory,
            CopyChannelSummary = _diagnosticsClipboard!.CopyChannelSummary,
            CopyActivitySummary = _diagnosticsClipboard!.CopyActivitySummary,
            CopyExtensibilitySummary = _diagnosticsClipboard!.CopyExtensibilitySummary,
            RestartSshTunnel = RestartSshTunnel,
            OpenChat = ShowWebChat,
            OpenCommandCenter = ShowStatusDetail,
            OpenTrayMenu = ShowTrayMenuPopup,
            OpenActivityStream = ShowActivityStream,
            OpenNotificationHistory = ShowNotificationHistory,
            OpenDashboard = OpenDashboard,
            OpenHub = (page) => ShowHub(page),
            OpenVoice = () => ShowHub("voice"), // was: ShowVoiceOverlay()
            StopVoice = () => _ = StopVoiceAsync(),
            SendMessage = async (msg) =>
            {
                var client = _connectionManager?.OperatorClient;
                if (client != null)
                {
                    await client.SendChatMessageAsync(msg);
                }
            }
        });
    }

    private async Task StopVoiceAsync()
    {
        var voiceService = _nodeService?.VoiceService;
        if (voiceService != null)
            await voiceService.StopAsync();
    }

    public Task SpeakChatTextAsync(string text) =>
        _chatCoordinator?.SpeakChatTextAsync(text) ?? Task.CompletedTask;

    public void StopChatSpeaking() => _chatCoordinator?.StopSpeaking();

    /// <summary>Raised when speaker mute state changes from any source (composer, settings, etc.).</summary>
    public event Action<bool>? SpeakerMuteChanged;

    public void SetChatSpeakerMuted(bool muted)
    {
        if (_chatCoordinator is { } c) c.IsMuted = muted;
        // Persist to settings
        if (_settings != null)
        {
            _settings.VoiceTtsEnabled = !muted;
            _settings.Save();
        }
        // Broadcast to all subscribers
        SpeakerMuteChanged?.Invoke(muted);
    }

    private static void SendDeepLinkToRunningInstance(string uri)
    {
        try
        {
            if (!DeepLinkSecurityPolicy.IsIpcPayloadWithinLimit(uri))
            {
                Logger.Warn($"Rejected oversized deep link before IPC forwarding: {DeepLinkSecurityPolicy.RedactForLog(uri)}");
                return;
            }

            if (DeepLinkParser.ParseDeepLink(uri) == null)
            {
                Logger.Warn($"Rejected invalid deep link before IPC forwarding: {DeepLinkSecurityPolicy.RedactForLog(uri)}");
                return;
            }

            var payload = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetBytes(uri);
            using var pipe = new NamedPipeClientStream(
                ".",
                DeepLinkPipeName,
                PipeDirection.Out,
                PipeOptions.CurrentUserOnly);
            pipe.Connect(1000);
            pipe.Write(payload, 0, payload.Length);
            pipe.Flush();
            pipe.WaitForPipeDrain();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to forward deep link: {ex.Message}");
        }
    }

    #endregion

    #region Exit

    private void ExitApplication()
    {
        _ = ExitApplicationAsync();
    }

    private async Task ExitApplicationAsync()
    {
        if (_isExiting)
        {
            Logger.Info("Exit requested while shutdown already in progress");
            return;
        }

        _isExiting = true;
        Logger.Info("Application exiting");

        // Cancel background tasks
        if (_deepLinkCts != null)
        {
            Logger.Info("Shutdown: canceling deep link server");
            try { _deepLinkCts.Cancel(); } catch (Exception ex) { Logger.Warn($"Shutdown: deep link cancel failed: {ex.Message}"); }
        }

        // Cleanup hotkey
        SafeShutdownStep("global hotkey", () =>
        {
            _globalHotkey?.Dispose();
            _globalHotkey = null;
        });

        // Dispose runtime services
        var connectionManager = _connectionManager;
        if (connectionManager != null)
        {
            await SafeShutdownStepAsync("gateway client", async () =>
            {
                await connectionManager.DisposeAsync();
            });
            _connectionManager = null;
        }

        SafeShutdownStep("chat coordinator", () =>
        {
            _chatCoordinator?.Dispose();
            _chatCoordinator = null;
        });

        var nodeService = _nodeService;
        if (nodeService != null)
        {
            await SafeShutdownStepAsync("node service", async () =>
            {
                await nodeService.DisposeAsync();
            });
            _nodeService = null;
        }

        var standaloneVoiceService = _standaloneVoiceService;
        if (standaloneVoiceService != null)
        {
            await SafeShutdownStepAsync("standalone voice service", async () =>
            {
                await standaloneVoiceService.DisposeAsync();
            });
            _standaloneVoiceService = null;
        }

        SafeShutdownStep("ssh tunnel service", () =>
        {
            _sshTunnelService?.Dispose();
            _sshTunnelService = null;
        });

        SafeShutdownStep("pairing approval", () =>
        {
            _pairingApprovalPollTimer?.Stop();
            _pairingApprovalPollTimer = null;
            _pairingApprovalDialog?.Close();
            _pairingApprovalDialog = null;
        });

        // Close windows explicitly for deterministic shutdown tracing.
        SafeShutdownStep("chat window", () => { _chatWindow?.ForceClose(); _chatWindow = null; });
        SafeShutdownStep("setup window", () => { _setupWindow?.Close(); _setupWindow = null; });
        SafeShutdownStep("tray menu window", () => CloseWindow(_trayMenuWindow));
        _trayMenuWindow = null;
        SafeShutdownStep("keep alive window", () => CloseWindow(_keepAliveWindow));
        _keepAliveWindow = null;

        // Dispose tray and mutex
        SafeShutdownStep("tray icon", () =>
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
        });

        SafeShutdownStep("single-instance mutex", () =>
        {
            _mutex?.Dispose();
            _mutex = null;
        });

        // Dispose cancellation token source
        SafeShutdownStep("deep link token source", () =>
        {
            _deepLinkCts?.Dispose();
            _deepLinkCts = null;
        });

        Logger.Info("Shutdown complete; calling Exit() now");
        Exit();
    }

    private static void CloseWindow(Window? window)
    {
        try
        {
            window?.Close();
        }
        catch
        {
            // Let caller log specific failure context.
            throw;
        }
    }

    private static void SafeShutdownStep(string name, Action action)
    {
        try
        {
            Logger.Info($"Shutdown: disposing {name}");
            action();
            Logger.Info($"Shutdown: disposed {name}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Shutdown: failed disposing {name}: {ex.Message}");
        }
    }

    private static async Task SafeShutdownStepAsync(string name, Func<Task> action)
    {
        try
        {
            Logger.Info($"Shutdown: disposing {name}");
            await action();
            Logger.Info($"Shutdown: disposed {name}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Shutdown: failed disposing {name}: {ex.Message}");
        }
    }

    private bool EnsureSshTunnelConfigured()
    {
        if (_settings == null)
        {
            return false;
        }

        if (_settings.UseSshTunnel)
        {
            if (string.IsNullOrWhiteSpace(_settings.SshTunnelUser) ||
                string.IsNullOrWhiteSpace(_settings.SshTunnelHost) ||
                _settings.SshTunnelRemotePort is < 1 or > 65535 ||
                _settings.SshTunnelLocalPort is < 1 or > 65535)
            {
                Logger.Warn("SSH tunnel is enabled but settings are incomplete");
                _appState!.Status = ConnectionStatus.Error;
                UpdateTrayIcon();
                return false;
            }

            try
            {
                _sshTunnelService ??= new SshTunnelService(new AppLogger());
                var includeBrowserProxy =
                    _settings.NodeBrowserProxyEnabled &&
                    SshTunnelCommandLine.CanForwardBrowserProxyPort(_settings.SshTunnelRemotePort, _settings.SshTunnelLocalPort);
                _sshTunnelService.EnsureStarted(
                    _settings.SshTunnelUser,
                    _settings.SshTunnelHost,
                    _settings.SshTunnelRemotePort,
                    _settings.SshTunnelLocalPort,
                    includeBrowserProxy);
                DiagnosticsJsonlService.Write("tunnel.ensure_started", new
                {
                    status = _sshTunnelService.Status.ToString(),
                    localEndpoint = $"127.0.0.1:{_settings.SshTunnelLocalPort}",
                    remoteHost = string.IsNullOrWhiteSpace(_settings.SshTunnelHost) ? null : _settings.SshTunnelHost,
                    remotePort = _settings.SshTunnelRemotePort
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to start SSH tunnel: {ex.Message}");
                _appState!.Status = ConnectionStatus.Error;
                UpdateTrayIcon();
                return false;
            }
        }
        else
        {
            _sshTunnelService?.Stop();
        }

        return true;
    }

    #endregion

    private void OnSshTunnelExited(object? sender, int exitCode) =>
        AsyncEventHandlerGuard.Run(
            () => OnSshTunnelExitedAsync(exitCode),
            new AppLogger(),
            nameof(OnSshTunnelExited));

    private async Task OnSshTunnelExitedAsync(int exitCode)
    {
        Logger.Warn($"SSH tunnel exited unexpectedly (code {exitCode}); restarting in 3s...");
        _sshTunnelService?.MarkRestarting(exitCode);
        DiagnosticsJsonlService.Write("tunnel.restart_scheduled", new
        {
            exitCode,
            localEndpoint = _sshTunnelService?.CurrentLocalPort > 0
                ? $"127.0.0.1:{_sshTunnelService.CurrentLocalPort}"
                : null
        });
        await Task.Delay(3000);
        if (_sshTunnelService != null && _settings?.UseSshTunnel == true)
        {
            try
            {
                var restartBrowserProxy =
                    _settings.NodeBrowserProxyEnabled &&
                    SshTunnelCommandLine.CanForwardBrowserProxyPort(_settings.SshTunnelRemotePort, _settings.SshTunnelLocalPort);
                _sshTunnelService.EnsureStarted(
                    _settings.SshTunnelUser,
                    _settings.SshTunnelHost,
                    _settings.SshTunnelRemotePort,
                    _settings.SshTunnelLocalPort,
                    restartBrowserProxy);
                Logger.Info("SSH tunnel restarted successfully");
                DiagnosticsJsonlService.Write("tunnel.restart_succeeded", new
                {
                    localEndpoint = _sshTunnelService.CurrentLocalPort > 0
                        ? $"127.0.0.1:{_sshTunnelService.CurrentLocalPort}"
                        : null
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"SSH tunnel restart failed: {ex.Message}");
                DiagnosticsJsonlService.Write("tunnel.restart_failed", new { ex.Message });
            }
        }
    }
}
