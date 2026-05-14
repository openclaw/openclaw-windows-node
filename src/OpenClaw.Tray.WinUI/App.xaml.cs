using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using OpenClaw.Shared;
using OpenClawTray.Dialogs;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using OpenClawTray.Onboarding;
using OpenClawTray.Services.Connection;
using OpenClawTray.Services.LocalGatewaySetup;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Updatum;
using WinUIEx;

namespace OpenClawTray;

public partial class App : Application
{
    private const string PipeName = "OpenClawTray-DeepLink";
    
    internal static readonly UpdatumManager AppUpdater = new("shanselman", "openclaw-windows-hub")
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
    private LocalGatewaySetupEngine? _localSetupEngine;
    /// <summary>
    /// When true, the connection manager suppresses node auto-connect after operator handshake.
    /// Set during the WSL local-setup flow so the engine controls node pairing in its own phase.
    /// </summary>
    private volatile bool _suppressNodeDuringSetup;

    /// <summary>The persistent gateway client. Used by the onboarding wizard for RPC calls.</summary>
    public IOperatorGatewayClient? GatewayClient => _connectionManager?.OperatorClient;
    public GatewayRegistry? Registry => _gatewayRegistry;
    public GatewayConnectionManager? ConnectionManager => _connectionManager;
    internal SettingsManager Settings => _settings ?? throw new InvalidOperationException("Settings are not initialized.");

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
    /// Used by the onboarding ConnectionPage when the user picks the SSH topology.
    /// </summary>
    public void EnsureSshTunnelStarted() => _sshTunnelService?.EnsureStarted(_settings);

    /// <summary>
    /// Creates the WSL local gateway setup engine using the current tray settings.
    /// Onboarding pages (Phase 5) call this to drive the local-WSL setup flow;
    /// the engine pairs the operator + Windows tray node into the gateway it
    /// installs, so we eagerly materialize the NodeService when needed.
    /// </summary>
    public LocalGatewaySetupEngine CreateLocalGatewaySetupEngine(
        bool replaceExistingConfigurationConfirmed = false)
    {
        var settings = _settings ?? new SettingsManager();
        var nodeService = EnsureNodeServiceForLocalGatewaySetup(settings);
        // Suppress node auto-connect in the connection manager during setup.
        // The engine controls node pairing in its own phase (PairWindowsTrayNode).
        _suppressNodeDuringSetup = true;
        try
        {
            // Use the connection manager's operator connector so all handshake/pairing
            // events appear in the diagnostics window and reuse the manager's v2/v3
            // signature fallback, credential resolution, and device token persistence.
            IGatewayOperatorConnector? operatorConnector = null;
            if (_connectionManager != null && _gatewayRegistry != null)
            {
                operatorConnector = new ConnectionManagerOperatorConnector(
                    _connectionManager, _gatewayRegistry, new AppLogger());
            }
            var engine = LocalGatewaySetupEngineFactory.CreateLocalOnly(
                settings,
                new AppLogger(),
                nodeService,
                replaceExistingConfigurationConfirmed: replaceExistingConfigurationConfirmed,
                gatewayRegistry: _gatewayRegistry,
                operatorConnectorOverride: operatorConnector);
            // Clear suppress flag when engine completes so normal node connections resume.
            // Only clear if this engine is still the active one (prevents stale engine #1
            // from clearing the flag while engine #2 is running).
            var capturedEngine = engine;
            engine.StateChanged += (st) =>
            {
                if (st.Status is LocalGatewaySetupStatus.Complete or LocalGatewaySetupStatus.FailedTerminal
                    or LocalGatewaySetupStatus.FailedRetryable or LocalGatewaySetupStatus.Cancelled)
                {
                    if (_localSetupEngine == capturedEngine)
                        _suppressNodeDuringSetup = false;
                }
            };
            // Bug #2: cache so OnPairingStatusChanged can read engine.IsAutoPairingWindowsNode
            // and suppress the "copy pairing command" toast during the Phase 14 blip.
            _localSetupEngine = engine;
            return engine;
        }
        catch
        {
            _suppressNodeDuringSetup = false;
            throw;
        }
    }

    /// <summary>
    /// Returns the HWND of the active onboarding window, or IntPtr.Zero if none.
    /// Used by onboarding pages that need to host file pickers / dialogs.
    /// </summary>
    public IntPtr GetOnboardingWindowHandle()
        => _onboardingWindow != null
            ? WinRT.Interop.WindowNative.GetWindowHandle(_onboardingWindow)
            : IntPtr.Zero;

    private SettingsManager? _settings;
    private SettingsData? _previousSettingsSnapshot;
    private SshTunnelService? _sshTunnelService;
    private GlobalHotkeyService? _globalHotkey;
    private Mutex? _mutex;
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    private CancellationTokenSource? _deepLinkCts;
    private bool _isExiting;
    
    /// <summary>
    /// Cached connection status — sole writer is OnManagerStateChanged.
    /// Reads are safe from any thread; derives from the connection manager's state machine.
    /// SSH tunnel errors in EnsureSshTunnelConfigured also write this temporarily (Phase 3 moves tunnel to manager).
    /// </summary>
    private ConnectionStatus _currentStatus = ConnectionStatus.Disconnected;
    private AgentActivity? _currentActivity;
    private ChannelHealth[] _lastChannels = Array.Empty<ChannelHealth>();
    private SessionInfo[] _lastSessions = Array.Empty<SessionInfo>();
    private GatewayNodeInfo[] _lastNodes = Array.Empty<GatewayNodeInfo>();
    private readonly Dictionary<string, SessionPreviewInfo> _sessionPreviews = new();
    private readonly object _sessionPreviewsLock = new();
    private DateTime _lastPreviewRequestUtc = DateTime.MinValue;
    private GatewayUsageInfo? _lastUsage;
    private GatewayUsageStatusInfo? _lastUsageStatus;
    private GatewayCostUsageInfo? _lastUsageCost;
    private GatewaySelfInfo? _lastGatewaySelf;
    private PairingListInfo? _lastNodePairList;
    private DevicePairingListInfo? _lastDevicePairList;
    private ModelsListInfo? _lastModelsList;
    private PresenceEntry[]? _lastPresence;
    private readonly List<AgentEventInfo> _agentEventsCache = new();
    private const int MaxAppAgentEvents = 400;
    private UpdateCommandCenterInfo _lastUpdateInfo = BuildInitialUpdateInfo();
    private DateTime _lastCheckTime = DateTime.Now;
    private DateTime _lastUsageActivityLogUtc = DateTime.MinValue;
    private string? _lastChannelStatusSignature;

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

    // Session-aware activity tracking
    private readonly Dictionary<string, AgentActivity> _sessionActivities = new();
    private string? _displayedSessionKey;
    private DateTime _lastSessionSwitch = DateTime.MinValue;
    private static readonly TimeSpan SessionSwitchDebounce = TimeSpan.FromSeconds(3);

    // Windows (created on demand)
    private HubWindow? _hubWindow;
    private TrayMenuWindow? _trayMenuWindow;
    private QuickSendDialog? _quickSendDialog;
    private ChatWindow? _chatWindow;
    private ConnectionStatusWindow? _connectionStatusWindow;
    private string? _authFailureMessage;

    // Bug 3: per-device idempotency for "Node paired" toast. WindowsNodeClient.HandleHelloOk
    // re-fires PairingStatusChanged(Paired) on every WS reconnect; we only want one toast
    // per device per session. (Source-side suppression also exists in WindowsNodeClient as
    // defense-in-depth.)
    private readonly HashSet<string> _shownPairedToasts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _recentToastKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ToastDedupeWindow = TimeSpan.FromSeconds(30);
    
    // Node service (optional, enabled in settings)
    private NodeService? _nodeService;
    
    // Keep-alive window to anchor WinUI runtime (prevents GC/threading issues)
    private Window? _keepAliveWindow;

    private string[]? _startupArgs;
    private string? _pendingProtocolUri;
    // OPENCLAW_TRAY_DATA_DIR isolates a test instance: settings, logs, run marker,
    // crash log, exec approvals, and the single-instance mutex name all derive from it.
    private static readonly string? DataDirOverride =
        Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") is { Length: > 0 } v ? v : null;
    private static readonly string DataPath = DataDirOverride
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenClawTray");
    // Operator/node identity store (DeviceIdentity). Lives at %APPDATA%\OpenClawTray
    // by convention so it follows the user across machines via roaming profile.
    // OPENCLAW_TRAY_APPDATA_DIR isolates a test/E2E identity store the same way
    // OPENCLAW_TRAY_DATA_DIR isolates the per-machine data directory.
    private static readonly string IdentityDataPath = Path.Combine(
        Environment.GetEnvironmentVariable("OPENCLAW_TRAY_APPDATA_DIR")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OpenClawTray");
    private static readonly string CrashLogPath = Path.Combine(DataPath, "crash.log");
    private static readonly string RunMarkerPath = Path.Combine(DataPath, "run.marker");

    public App()
    {
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
        
        CheckPreviousRun();
        MarkRunStarted();
        
        // Hook up crash handlers
        this.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogCrash("UnhandledException", e.Exception);
        e.Handled = true; // Try to prevent crash
    }

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        LogCrash("DomainUnhandledException", e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("UnobservedTaskException", e.Exception);
        e.SetObserved(); // Prevent crash
    }
    
    private void OnProcessExit(object? sender, EventArgs e)
    {
        MarkRunEnded();
        try
        {
            Logger.Info($"Process exiting (ExitCode={Environment.ExitCode})");
        }
        catch { }
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(CrashLogPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var message = $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}\n{ex}\n";
            File.AppendAllText(CrashLogPath, message);
        }
        catch { /* Can't log the crash logger crash */ }
        
        try
        {
            if (ex != null)
            {
                Logger.Error($"CRASH {source}: {ex}");
            }
            else
            {
                Logger.Error($"CRASH {source}");
            }
        }
        catch { /* Ignore logging failures */ }
    }

    // -----------------------------------------------------------------------
    // CLI uninstall path
    // Invoked when --uninstall is present in argv. Runs headlessly without
    // creating the tray UI. Attaches to the parent console so stdout/stderr
    // are visible when invoked from PowerShell or cmd.
    // -----------------------------------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    private const int AttachParentProcess = -1;

    private static async Task RunCliUninstallAsync(string[] args)
    {
        // Attach to parent console so output is visible when invoked from
        // PowerShell or cmd.  Fails silently if no parent console exists.
        AttachConsole(AttachParentProcess);

        bool dryRun            = args.Contains("--dry-run",            StringComparer.OrdinalIgnoreCase);
        bool confirmDestructive = args.Contains("--confirm-destructive", StringComparer.OrdinalIgnoreCase);

        // Locate --json-output <path> argument
        string? jsonOutputPath = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--json-output", StringComparison.OrdinalIgnoreCase))
            {
                jsonOutputPath = args[i + 1];
                break;
            }
        }

        if (!confirmDestructive && !dryRun)
        {
            Console.Error.WriteLine(
                "ERROR: --uninstall requires --confirm-destructive (or --dry-run).");
            Environment.Exit(2);
            return;
        }

        var settings = new SettingsManager();
        var engine   = LocalGatewayUninstall.Build(settings, logger: new AppLogger());

        LocalGatewayUninstallResult result;
        try
        {
            result = await engine.RunAsync(new LocalGatewayUninstallOptions
            {
                DryRun             = dryRun,
                ConfirmDestructive = confirmDestructive
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Uninstall engine threw: {ex.Message}");
            Environment.Exit(1);
            return;
        }

        // Human-readable summary (tokens already redacted inside engine steps)
        Console.WriteLine("OpenClaw Local Gateway Uninstall");
        Console.WriteLine($"DryRun:   {dryRun}");
        Console.WriteLine($"Success:  {result.Success}");
        Console.WriteLine($"Steps:    {result.Steps.Count} ({result.SkippedSteps.Count} skipped)");
        Console.WriteLine($"Errors:   {result.Errors.Count}");
        foreach (var e in result.Errors)
            Console.Error.WriteLine($"  ERROR: {CliRedact(e)}");
        Console.WriteLine("Postconditions:");
        Console.WriteLine($"  WslDistroAbsent:    {result.Postconditions.WslDistroAbsent}");
        Console.WriteLine($"  AutostartCleared:   {result.Postconditions.AutostartCleared}");
        Console.WriteLine($"  SetupStateAbsent:   {result.Postconditions.SetupStateAbsent}");
        Console.WriteLine($"  DeviceTokenCleared: {result.Postconditions.DeviceTokenCleared}");
        Console.WriteLine($"  McpTokenPreserved:  {result.Postconditions.McpTokenPreserved}");
        Console.WriteLine($"  KeepalivesAbsent:   {result.Postconditions.KeepalivesAbsent}");
        Console.WriteLine($"  VhdDirAbsent:       {result.Postconditions.VhdDirAbsent}");
        Console.WriteLine($"  LocalGatewayRecordsAbsent:      {result.Postconditions.LocalGatewayRecordsAbsent}");
        Console.WriteLine($"  LocalGatewayIdentityDirsAbsent: {result.Postconditions.LocalGatewayIdentityDirsAbsent}");

        // JSON output — redaction applied to step details and error strings
        if (jsonOutputPath != null)
        {
            try
            {
                var dir = Path.GetDirectoryName(jsonOutputPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var payload = new
                {
                    success = result.Success,
                    dry_run = dryRun,
                    steps   = result.Steps.Select(s => new
                    {
                        name   = s.Name,
                        status = s.Status.ToString(),
                        detail = CliRedact(s.Detail)
                    }),
                    errors       = result.Errors.Select(CliRedact),
                    skipped_steps = result.SkippedSteps,
                    postconditions = new
                    {
                        wsl_distro_absent     = result.Postconditions.WslDistroAbsent,
                        autostart_cleared     = result.Postconditions.AutostartCleared,
                        setup_state_absent    = result.Postconditions.SetupStateAbsent,
                        device_token_cleared  = result.Postconditions.DeviceTokenCleared,
                        mcp_token_preserved   = result.Postconditions.McpTokenPreserved,
                        keepalives_absent     = result.Postconditions.KeepalivesAbsent,
                        vhd_dir_absent        = result.Postconditions.VhdDirAbsent,
                        local_gateway_records_absent = result.Postconditions.LocalGatewayRecordsAbsent,
                        local_gateway_identity_dirs_absent = result.Postconditions.LocalGatewayIdentityDirsAbsent
                    }
                };

                File.WriteAllText(jsonOutputPath, JsonSerializer.Serialize(
                    payload, new JsonSerializerOptions { WriteIndented = true }));

                Console.WriteLine($"JSON result: {jsonOutputPath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"WARNING: Failed to write JSON output to '{jsonOutputPath}': {ex.Message}");
            }
        }

        Environment.Exit(result.Success ? 0 : 1);
    }

    /// <summary>
    /// Redacts token/key material from a string before writing it to CLI
    /// stdout or a JSON output file.  Mirrors the PowerShell Invoke-Redact
    /// pattern in validate-wsl-gateway-uninstall.ps1.
    /// </summary>
    private static string? CliRedact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        // Redact JSON field values for known secret fields.
        value = System.Text.RegularExpressions.Regex.Replace(
            value,
            @"(""(?i:deviceToken|device_token|token|bootstrapToken|bootstrap_token|PrivateKeyBase64|PublicKeyBase64)""\s*:\s*"")[^""]+("")",
            "$1<redacted>$2");
        // Redact bare key=value / key: value patterns.
        value = System.Text.RegularExpressions.Regex.Replace(
            value,
            @"(?i)((?:device|bootstrap|gateway|auth|mcp)[_-]?token\s*[:=]\s*)[^\s,""'}{]+",
            "$1<redacted>");
        return value;
    }
    
    private static void CheckPreviousRun()
    {
        try
        {
            if (File.Exists(RunMarkerPath))
            {
                var startedAt = File.ReadAllText(RunMarkerPath);
                Logger.Error($"Previous session did not exit cleanly (started {startedAt})");
                File.Delete(RunMarkerPath);
            }
        }
        catch { }
    }
    
    private static void MarkRunStarted()
    {
        try
        {
            var dir = Path.GetDirectoryName(RunMarkerPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(RunMarkerPath, DateTime.Now.ToString("O"));
        }
        catch { }
    }
    
    private static void MarkRunEnded()
    {
        try
        {
            if (File.Exists(RunMarkerPath))
                File.Delete(RunMarkerPath);
        }
        catch { }
    }

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

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _startupArgs = Environment.GetCommandLineArgs();
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // -----------------------------------------------------------------------
        // CLI uninstall path — headless; never shows tray or any windows.
        // Approach: detect in OnLaunched before any UI is created (WinUI3 Main
        // is auto-generated; earliest interception point is OnLaunched).
        // Bypasses the single-instance mutex so the Inno uninstaller can invoke
        // this even while the tray is running.
        // -----------------------------------------------------------------------
        if (_startupArgs.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            await RunCliUninstallAsync(_startupArgs);
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
        if (!createdNew)
        {
            // Forward deep link args to running instance (command-line or protocol activation)
            var deepLink = protocolUri
                ?? (_startupArgs.Length > 1 && _startupArgs[1].StartsWith("openclaw://", StringComparison.OrdinalIgnoreCase)
                    ? _startupArgs[1] : null);
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
        _previousSettingsSnapshot = _settings.ToSettingsData();
        _chatCoordinator = new OpenClawTray.Chat.OpenClawChatCoordinator(
            _settings,
            () => _nodeService,
            new AppLogger(),
            _dispatcherQueue is null
                ? null
                : OpenClawTray.Chat.FunctionalChatHostExtensions.AsPost(_dispatcherQueue));
        DiagnosticsJsonlService.Configure(DataPath);
        DiagnosticsJsonlService.Write("app.start", new
        {
            nodeMode = _settings.EnableNodeMode,
            useSshTunnel = _settings.UseSshTunnel
        });

        // Register URI scheme on first run
        DeepLinkHandler.RegisterUriScheme();

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
        // in the onboarding wizard. OnLaunched is async void, so a synchronous
        // throw inside the OnboardingWindow constructor would otherwise
        // propagate through `await ShowOnboardingAsync()` and abort OnLaunched
        // before the tray ever initializes.
        InitializeTrayIcon();
        // Apply the user's saved default chat preset (if any) before any chat
        // surface mounts so initial render uses their preferred styling.
        OpenClawTray.Chat.Explorations.ChatExplorationPresetStore.ApplyDefaultIfPresent();
        ShowSurfaceImprovementsTipIfNeeded();

        // First-run check (also supports forced onboarding for testing).
        // Wrapped in try/catch so a wizard construction failure cannot tear
        // down the tray; user can retry via the Setup Guide menu item.
        try
        {
            if (RequiresSetup(_settings) ||
                Environment.GetEnvironmentVariable("OPENCLAW_FORCE_ONBOARDING") == "1")
            {
                await ShowOnboardingAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Onboarding failed during launch (tray remains available): {ex}");
        }

        // Initialize connection manager (north star architecture)
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
                diagnostics.Record("node", $"ClientCreated fired, _nodeService null={_nodeService is null}");
                _nodeService?.AttachClient(args.Client, args.BearerToken);
                var client = args.Client;
                diagnostics.Record("node", $"After AttachClient: caps={client.Capabilities.Count}, cmds={client.RegisteredCommandCount}");
                if (client.RegisteredCommandCount > 0)
                    diagnostics.Record("node", $"Commands sample: {string.Join(", ", client.RegisteredCommandsSample)}...");
                else
                    diagnostics.Record("node", "WARNING: 0 commands registered on node client before connect");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[App] NodeConnector.ClientCreated handler failed: {ex.Message}");
                diagnostics.Record("node", $"ClientCreated handler THREW: {ex.Message}");
            }
        };
        // Wrap the SSH tunnel service so the connection manager can start/stop the tunnel
        var tunnelManager = _sshTunnelService != null
            ? new SshTunnelManager(_sshTunnelService, appLogger)
            : null;
        _connectionManager = new GatewayConnectionManager(
            credentialResolver, clientFactory, _gatewayRegistry, appLogger,
            identityStore: new DeviceIdentityFileStore(appLogger),
            nodeConnector: nodeConnector,
            isNodeEnabled: ShouldInitializeNodeService,
            diagnostics: diagnostics,
            tunnelManager: tunnelManager,
            shouldStartNodeConnection: ShouldInitializeNodeService);
        _connectionManager.OperatorClientChanged += OnOperatorClientChanged;
        _connectionManager.StateChanged += OnManagerStateChanged;

        // Ensure NodeService is constructed BEFORE InitializeGatewayClient triggers a
        // NodeConnector connect. The NodeConnector.ClientCreated event subscription
        // above relies on _nodeService being non-null to register capabilities on the
        // new WindowsNodeClient. If we don't pre-construct here, the first connect
        // happens with empty caps and the gateway records this node as having no
        // advertised commands (which leaves the agent unable to invoke anything on it).
        // The method is idempotent — safe to call here AND later if first-run setup runs.
        if (ShouldInitializeNodeService() && _settings != null)
        {
            EnsureNodeServiceForLocalGatewaySetup(_settings);
        }

        // Initialize connections — always create operator client for UI data,
        // additionally create node service for gateway node mode or local MCP.
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
        if (_settings.GlobalHotkeyEnabled)
        {
            _globalHotkey = new GlobalHotkeyService();
            _globalHotkey.HotkeyPressed += OnGlobalHotkeyPressed;
            _globalHotkey.VoiceHotkeyPressed += OnVoiceHotkeyPressed;
            _globalHotkey.Register();
        }

        // Process startup deep link (command-line or MSIX protocol activation)
        var startupDeepLink = _pendingProtocolUri
            ?? (_startupArgs.Length > 1 && _startupArgs[1].StartsWith("openclaw://", StringComparison.OrdinalIgnoreCase)
                ? _startupArgs[1] : null);
        if (startupDeepLink != null)
        {
            HandleDeepLink(startupDeepLink);
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
        ShowChatWindow();
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

    private VoiceOverlayWindow? _voiceOverlayWindow;
    private VoiceService? _standaloneVoiceService;

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
                if (client != null && _currentStatus == ConnectionStatus.Connected)
                {
                    _ = client.SendChatMessageAsync(text);
                }
            };
            // Wire Settings button → open the Hub on the Voice & Audio page.
            _voiceOverlayWindow.SettingsRequested += () =>
            {
                _dispatcherQueue?.TryEnqueue(() => ShowHub("voice"));
            };
        }

        _voiceOverlayWindow.Activate();
    }

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

    private async void ShowTrayMenuPopup()
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
            LogCrash("ShowTrayMenuPopup", ex);
            Logger.Error($"Failed to show tray menu: {ex.Message}");
        }
    }

    private void OnTrayMenuItemClicked(object? sender, string action)
    {
        switch (action)
        {
            case "status": ShowStatusDetail(); break;
            case "reconnect": _ = _connectionManager?.ReconnectAsync(); break;
            case "dashboard": OpenDashboard(); break;
            case "canvas": ShowCanvasWindow(); break;
            case "openchat": ShowChatWindow(); break;
            case "voice": ShowVoiceOverlay(); break;
            case "webchat": ShowWebChat(); break;
            case "hub": ShowHub(); break;
            case "companion":
                // If disconnected, open General page (has connection settings + discovery)
                // If connected, open Hub at default page
                if (_currentStatus != ConnectionStatus.Connected)
                    ShowHub("general");
                else
                    ShowHub();
                break;
            case "quicksend": ShowQuickSend(); break;
            case "history": ShowNotificationHistory(); break;
            case "activity": ShowActivityStream(); break;
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
            case "supportcontext": CopySupportContext(); break;
            case "debugbundle": CopyDebugBundle(); break;
            case "browsersetup": CopyBrowserSetupGuidance(); break;
            case "portdiagnostics": CopyPortDiagnostics(); break;
            case "capabilitydiagnostics": CopyCapabilityDiagnostics(); break;
            case "nodeinventory": CopyNodeInventory(); break;
            case "channelsummary": CopyChannelSummary(); break;
            case "activitysummary": CopyActivitySummary(); break;
            case "extensibilitysummary": CopyExtensibilitySummary(); break;
            case "restartsshtunnel": RestartSshTunnel(); break;
            case "copydeviceid": CopyDeviceIdToClipboard(); break;
            case "copynodesummary": CopyNodeSummaryToClipboard(); break;
            case "exit": ExitApplication(); break;
            default:
                if (action.StartsWith("session-reset|", StringComparison.Ordinal))
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
                    ShowActivityStream(action["activity:".Length..]);
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
            ShowToast(new ToastContentBuilder()
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
        if (_lastNodes.Length == 0) return;

        try
        {
            var lines = _lastNodes.Select(node =>
            {
                var state = node.IsOnline ? "online" : "offline";
                var name = string.IsNullOrWhiteSpace(node.DisplayName) ? node.ShortId : node.DisplayName;
                return $"{state}: {name} ({node.ShortId}) · {node.DetailText}";
            });
            var summary = string.Join(Environment.NewLine, lines);

            CopyTextToClipboard(summary);

            ShowToast(new ToastContentBuilder()
                .AddText(LocalizationHelper.GetString("Toast_NodeSummaryCopied"))
                .AddText(string.Format(LocalizationHelper.GetString("Toast_NodeSummaryCopiedDetail"), _lastNodes.Length)));
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
                ShowToast(new ToastContentBuilder()
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
                ShowToast(new ToastContentBuilder()
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

    private static string TruncateMenuText(string text, int maxLength = 96) =>
        MenuDisplayHelper.TruncateText(text, maxLength);

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

    private void BuildTrayMenuPopup(TrayMenuWindow menu)
    {
        var isConnected = _currentStatus == ConnectionStatus.Connected;
        var statusText = LocalizationHelper.GetConnectionStatusText(_currentStatus);

        // ── Brand Header (non-interactive) ──
        menu.AddCustomElement(new StackPanel
        {
            Padding = new Thickness(14, 10, 14, 6),
            Children =
            {
                new TextBlock
                {
                    Text = "🦞 OpenClaw",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    FontSize = 14
                }
            }
        });

        // ── Gateway Section ──
        var gwGrid = new Grid
        {
            Padding = new Thickness(14, 4, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };

        var gwInfo = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };

        // Gateway status line
        var gwStatusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        gwStatusRow.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                isConnected ? Microsoft.UI.Colors.LimeGreen
                : _currentStatus == ConnectionStatus.Connecting ? Microsoft.UI.Colors.Orange
                : Microsoft.UI.Colors.Gray)
        });
        gwStatusRow.Children.Add(new TextBlock
        {
            Text = $"Gateway · {statusText}",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });
        gwInfo.Children.Add(gwStatusRow);

        // Gateway details
        if (isConnected)
        {
            var detailParts = new List<string>();
            if (_lastGatewaySelf != null && !string.IsNullOrEmpty(_lastGatewaySelf.ServerVersion))
                detailParts.Add($"v{_lastGatewaySelf.ServerVersion}");
            var url = _settings?.GetEffectiveGatewayUrl();
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                detailParts.Add($"{uri.Host}:{uri.Port}");
            if (_lastPresence != null && _lastPresence.Length > 0)
                detailParts.Add($"{_lastPresence.Length} client{(_lastPresence.Length != 1 ? "s" : "")}");
            if (detailParts.Count > 0)
            {
                gwInfo.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", detailParts),
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    FontSize = 11
                });
            }
        }

        // Node pairing status
        if (_settings?.EnableNodeMode == true && _nodeService != null)
        {
            var nodeText = _nodeService.IsPaired ? "Node paired"
                : _nodeService.IsPendingApproval ? "⏳ Node pairing pending"
                : _nodeService.IsConnected ? "Node connected"
                : null;
            if (nodeText != null)
            {
                gwInfo.Children.Add(new TextBlock
                {
                    Text = nodeText,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    FontSize = 11
                });
            }
        }

        // Auth failure
        if (!string.IsNullOrEmpty(_authFailureMessage))
        {
            gwInfo.Children.Add(new TextBlock
            {
                Text = $"⚠️ {_authFailureMessage}",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 240
            });
        }

        Grid.SetColumn(gwInfo, 0);
        gwGrid.Children.Add(gwInfo);

        // Gateway connect/disconnect button
        var connectBtn = new ToggleButton
        {
            IsChecked = isConnected,
            Content = isConnected ? "Connected" : "Disconnected",
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(10, 4, 10, 4),
            MinHeight = 0,
            MinWidth = 0,
            FontSize = 11
        };
        ToolTipService.SetToolTip(connectBtn, isConnected ? "Click to disconnect from gateway" : "Click to connect to gateway");
        connectBtn.Click += (s, ev) =>
        {
            var on = connectBtn.IsChecked == true;
            connectBtn.Content = on ? "Connected" : "Disconnected";
            ToolTipService.SetToolTip(connectBtn, on ? "Click to disconnect from gateway" : "Click to connect to gateway");
            if (on)
            {
                _ = _connectionManager?.ReconnectAsync();
            }
            else
            {
                _ = _connectionManager?.DisconnectAsync();
                // Status is updated by OnManagerStateChanged when disconnect completes.
                _lastSessions = Array.Empty<SessionInfo>();
                _lastNodePairList = null;
                _lastDevicePairList = null;
                _lastModelsList = null;
                _agentEventsCache.Clear();
                UpdateTrayIcon();
                _hubWindow?.UpdateStatus(ConnectionStatus.Disconnected);
            }
            // Dismiss menu after toggle — header will rebuild with correct state on next open
            _trayMenuWindow?.HideCascade();
        };
        Grid.SetColumn(connectBtn, 1);
        gwGrid.Children.Add(connectBtn);

        // Make gateway info area clickable → opens Connection page
        gwInfo.Tapped += (s, ev) =>
        {
            ShowHub("connection");
        };
        menu.AddCustomElement(gwGrid);

        // ── Sessions ──
        if (_lastSessions.Length > 0)
        {
            menu.AddSeparator();

            // Section header: "Sessions  3 active · 45K tokens"
            var sessionCount = _lastSessions.Length;
            var activeCount = _lastSessions.Count(s => string.Equals(s.Status, "active", StringComparison.OrdinalIgnoreCase));
            var totalTokensAll = _lastSessions.Sum(s => s.InputTokens + s.OutputTokens);
            var sessionSummaryRight = $"{activeCount} active · {FormatTokenCount(totalTokensAll)} tokens";
            menu.AddCustomElement(BuildSectionHeader("Sessions", sessionSummaryRight));

            // Individual session cards
            foreach (var session in _lastSessions.Take(5))
            {
                var card = BuildSessionCard(session);
                var flyoutItems = BuildSessionFlyoutItems(session);
                menu.AddFlyoutCustomItem(card, flyoutItems, action: "sessions");
            }
        }

        // ── Pairing Pending ──
        var nodePendingCount = _lastNodePairList?.Pending.Count ?? 0;
        var devicePendingCount = _lastDevicePairList?.Pending.Count ?? 0;
        if (nodePendingCount + devicePendingCount > 0)
        {
            var total = nodePendingCount + devicePendingCount;
            menu.AddMenuItem($"⚠️ Pairing approval pending ({total})", "🔗", "hub");
        }

        // ── Connected Devices with inline permission toggles ──
        // Only show currently-connected nodes; offline/stale paired nodes
        // remain visible on the full Nodes page where they can be renamed
        // or forgotten.
        var connectedNodes = _lastNodes.Where(n => n.IsOnline).ToArray();
        if (connectedNodes.Length > 0)
        {
            menu.AddSeparator();

            var totalCaps = connectedNodes.Sum(n => n.CapabilityCount);
            var deviceSummaryRight = $"{connectedNodes.Length} online · {totalCaps} caps";
            menu.AddCustomElement(BuildSectionHeader("Devices", deviceSummaryRight));

            var currentHost = Environment.MachineName;

            foreach (var node in connectedNodes.Take(5))
            {
                var card = BuildDeviceCard(node);
                var flyoutItems = BuildDeviceFlyoutItems(node);
                menu.AddFlyoutCustomItem(card, flyoutItems, action: "nodes");

                // If this node is the local machine, show capability toggles underneath
                bool isLocal = node.DisplayName?.Contains(currentHost, StringComparison.OrdinalIgnoreCase) == true
                    || node.NodeId?.Contains(currentHost, StringComparison.OrdinalIgnoreCase) == true;
                if (isLocal && _settings != null)
                {
                    // Build compact toggle button grid (3 columns)
                    var capToggles = new Dictionary<string, (Func<bool> Get, Action<bool> Set)>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["browser"] = (() => _settings.NodeBrowserProxyEnabled, v => _settings.NodeBrowserProxyEnabled = v),
                        ["camera"] = (() => _settings.NodeCameraEnabled, v => _settings.NodeCameraEnabled = v),
                        ["canvas"] = (() => _settings.NodeCanvasEnabled, v => _settings.NodeCanvasEnabled = v),
                        ["screen"] = (() => _settings.NodeScreenEnabled, v => _settings.NodeScreenEnabled = v),
                        ["location"] = (() => _settings.NodeLocationEnabled, v => _settings.NodeLocationEnabled = v),
                        ["tts"] = (() => _settings.NodeTtsEnabled, v => _settings.NodeTtsEnabled = v),
                        ["system"] = (() => _settings.EnableNodeMode, v => _settings.EnableNodeMode = v),
                    };

                    // Show ALL possible capability toggles (not just gateway-reported ones)
                    // so disabled capabilities like TTS appear as "off" buttons
                    var allCaps = capToggles.Keys.ToList();

                    if (allCaps.Count > 0)
                    {
                        var columns = 3;
                        var grid = new Grid
                        {
                            Margin = new Thickness(28, 4, 14, 4),
                            ColumnSpacing = 4,
                            RowSpacing = 4,
                            HorizontalAlignment = HorizontalAlignment.Stretch
                        };
                        for (int c = 0; c < columns; c++)
                            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        var rowCount = (allCaps.Count + columns - 1) / columns;
                        for (int r = 0; r < rowCount; r++)
                            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                        for (int i = 0; i < allCaps.Count; i++)
                        {
                            var cap = allCaps[i];
                            var capToggle = capToggles[cap];
                            var icon = CapabilityIcons.TryGetValue(cap, out var emoji) ? emoji : "▪";
                            var label = char.ToUpper(cap[0]) + cap[1..];
                            var isOn = capToggle.Get();

                            var btn = new ToggleButton
                            {
                                IsChecked = isOn,
                                HorizontalAlignment = HorizontalAlignment.Stretch,
                                HorizontalContentAlignment = HorizontalAlignment.Center,
                                Padding = new Thickness(6, 5, 6, 5),
                                MinHeight = 0,
                                MinWidth = 0,
                                Content = new TextBlock
                                {
                                    Text = $"{icon} {label}",
                                    FontSize = 11,
                                    TextTrimming = TextTrimming.CharacterEllipsis
                                }
                            };
                            var capRef = capToggle; // capture for lambda
                            btn.Click += (s, ev) =>
                            {
                                var on = ((ToggleButton)s!).IsChecked == true;
                                capRef.Set(on); _settings.Save(); _ = _connectionManager?.ReconnectAsync();
                            };
                            Grid.SetRow(btn, i / columns);
                            Grid.SetColumn(btn, i % columns);
                            grid.Children.Add(btn);
                        }
                        menu.AddCustomElement(grid);
                    }
                }
            }
        }

        // ── Actions ──
        menu.AddSeparator();
        menu.AddMenuItem("Dashboard", "🌐", "dashboard");
        menu.AddMenuItem("Chat", "💬", "openchat");
        menu.AddMenuItem("Canvas", "🎨", "canvas");
        menu.AddMenuItem("Voice", "🎙️", "voice");
        menu.AddMenuItem("Companion Settings...", "🦞", "companion");
        menu.AddMenuItem(LocalizationHelper.GetString("Menu_QuickSend"), "📤", "quicksend");

        // Setup Guide / Reconfigure entry (PR #274 must-fix #6) — label flips
        // based on whether prior config exists. Click dispatches "setup" which
        // invokes the existing ShowOnboardingAsync handler (case in OnTrayMenuAction).
        var setupMenuLabel = _settings != null
            && new OpenClawTray.Onboarding.Services.OnboardingExistingConfigGuard(_settings, IdentityDataPath)
                .HasExistingConfiguration()
            ? LocalizationHelper.GetString("Menu_Reconfigure")
            : LocalizationHelper.GetString("Menu_SetupGuide");
        menu.AddMenuItem(setupMenuLabel, "🧭", "setup");

        // ── Exit ──
        menu.AddSeparator();
        menu.AddMenuItem(LocalizationHelper.GetString("Menu_Exit"), "❌", "exit");
    }

    private static string FormatTokenCount(long n)
    {
        if (n >= 1_000_000) return $"{n / 1_000_000.0:F1}M";
        if (n >= 1_000) return $"{n / 1_000.0:F1}K";
        return n.ToString();
    }

    // ── Rich card builder helpers for tray menu ──

    private static readonly FrozenDictionary<string, string> CapabilityIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["screen"] = "🖥",
        ["camera"] = "📷",
        ["browser"] = "🌐",
        ["clipboard"] = "📋",
        ["tts"] = "🔊",
        ["location"] = "📍",
        ["canvas"] = "🎨",
        ["system"] = "⚙",
        ["device"] = "📱",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static Grid BuildSectionHeader(string title, string summary)
    {
        var grid = new Grid
        {
            Padding = new Thickness(12, 8, 12, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        grid.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center
        });
        grid.Children.Add(new TextBlock
        {
            Text = summary,
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        });
        return grid;
    }

    private static UIElement BuildSessionCard(SessionInfo session)
    {
        var usedTokens = session.InputTokens + session.OutputTokens;
        var contextTokens = session.ContextTokens > 0 ? session.ContextTokens : 200_000;
        var pct = usedTokens > 0 ? (int)(Math.Min(1.0, (double)usedTokens / contextTokens) * 100) : 0;
        var isActive = string.Equals(session.Status, "active", StringComparison.OrdinalIgnoreCase);
        var isIdle = string.Equals(session.Status, "idle", StringComparison.OrdinalIgnoreCase);

        var grid = new Grid
        {
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            RowSpacing = 2,
            ColumnSpacing = 6
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // status dot
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // model badge
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // chevron

        // Row 0: status dot
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                isActive ? Microsoft.UI.Colors.LimeGreen
                : isIdle ? Microsoft.UI.Colors.Orange
                : Microsoft.UI.Colors.Gray)
        };
        Grid.SetRow(dot, 0);
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        // Row 0: session name
        var nameBlock = new TextBlock
        {
            Text = session.DisplayName ?? session.Key,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };
        Grid.SetRow(nameBlock, 0);
        Grid.SetColumn(nameBlock, 1);
        grid.Children.Add(nameBlock);

        // Row 0: model badge
        if (!string.IsNullOrEmpty(session.Model))
        {
            var modelBadge = BuildBadge(session.Model);
            Grid.SetRow(modelBadge, 0);
            Grid.SetColumn(modelBadge, 2);
            grid.Children.Add(modelBadge);
        }

        // Row 0: chevron
        var chevron = new TextBlock
        {
            Text = "›",
            FontSize = 14,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };
        Grid.SetRow(chevron, 0);
        Grid.SetColumn(chevron, 3);
        grid.Children.Add(chevron);

        // Row 1: token info + channel badge + status
        var row1 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        var tokenText = usedTokens > 0
            ? $"{FormatTokenCount(usedTokens)}/{FormatTokenCount(contextTokens)} ({pct}%)"
            : "";
        if (!string.IsNullOrEmpty(tokenText))
        {
            row1.Children.Add(new TextBlock
            {
                Text = tokenText,
                FontSize = 11,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                IsTextSelectionEnabled = false
            });
        }
        if (!string.IsNullOrEmpty(session.Channel))
        {
            var channelAbbrev = session.Channel!.Length <= 2
                ? session.Channel.ToUpperInvariant()
                : session.Channel[..2].ToUpperInvariant();
            row1.Children.Add(BuildBadge(channelAbbrev));
        }
        var statusText = string.IsNullOrEmpty(session.Status) ? "Unknown"
            : char.ToUpperInvariant(session.Status[0]) + session.Status[1..];
        row1.Children.Add(new TextBlock
        {
            Text = statusText,
            FontSize = 11,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        });
        Grid.SetRow(row1, 1);
        Grid.SetColumn(row1, 1);
        Grid.SetColumnSpan(row1, 3);
        grid.Children.Add(row1);

        // Row 2: thin progress bar
        if (usedTokens > 0)
        {
            var bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = pct,
                Height = 3,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(1.5),
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    pct > 80 ? Microsoft.UI.Colors.Red
                    : pct > 50 ? Microsoft.UI.Colors.Orange
                    : Microsoft.UI.Colors.LimeGreen)
            };
            Grid.SetRow(bar, 2);
            Grid.SetColumn(bar, 0);
            Grid.SetColumnSpan(bar, 4);
            grid.Children.Add(bar);
        }

        return grid;
    }

    private static List<TrayMenuFlyoutItem> BuildSessionFlyoutItems(SessionInfo session)
    {
        var usedTokens = session.InputTokens + session.OutputTokens;
        var contextTokens = session.ContextTokens > 0 ? session.ContextTokens : 200_000;
        var pct = usedTokens > 0 ? (int)(Math.Min(1.0, (double)usedTokens / contextTokens) * 100) : 0;
        var statusIcon = string.Equals(session.Status, "active", StringComparison.OrdinalIgnoreCase) ? "🟢"
            : string.Equals(session.Status, "done", StringComparison.OrdinalIgnoreCase) ? "✅" : "⚪";

        var items = new List<TrayMenuFlyoutItem>
        {
            new() { Text = session.DisplayName ?? session.Key, IsHeader = true },
        };

        // Model · Provider
        var modelParts = new List<string>();
        if (!string.IsNullOrEmpty(session.Model)) modelParts.Add(session.Model);
        if (!string.IsNullOrEmpty(session.Provider)) modelParts.Add(session.Provider);
        if (modelParts.Count > 0) items.Add(new() { Text = string.Join(" · ", modelParts) });

        // Channel
        if (!string.IsNullOrEmpty(session.Channel))
            items.Add(new() { Text = $"📡 {session.Channel}" });

        // Status · age
        items.Add(new() { Text = $"{statusIcon} {session.Status} · {session.AgeText}" });

        // Token usage
        items.Add(new() { Text = "Token Usage", IsHeader = true });
        if (usedTokens > 0)
        {
            items.Add(new() { Text = $"Input     {FormatTokenCount(session.InputTokens)}" });
            items.Add(new() { Text = $"Output    {FormatTokenCount(session.OutputTokens)}" });
            items.Add(new() { Text = $"Total     {FormatTokenCount(usedTokens)} / {FormatTokenCount(contextTokens)} ({pct}%)" });
        }
        else
        {
            items.Add(new() { Text = "No token usage yet" });
        }

        // Context window
        if (session.ContextTokens > 0)
            items.Add(new() { Text = $"Context   {FormatTokenCount(session.ContextTokens)} window" });

        // Thinking / Verbose
        if (!string.IsNullOrEmpty(session.ThinkingLevel) || !string.IsNullOrEmpty(session.VerboseLevel))
        {
            items.Add(new() { Text = "Settings", IsHeader = true });
            if (!string.IsNullOrEmpty(session.ThinkingLevel))
                items.Add(new() { Text = $"🧠 Thinking: {session.ThinkingLevel}" });
            if (!string.IsNullOrEmpty(session.VerboseLevel))
                items.Add(new() { Text = $"📝 Verbose: {session.VerboseLevel}" });
        }

        // Subject / Room
        if (!string.IsNullOrEmpty(session.Subject))
            items.Add(new() { Text = $"Subject: {session.Subject}" });
        if (!string.IsNullOrEmpty(session.Room))
            items.Add(new() { Text = $"Room: {session.Room}" });

        return items;
    }

    private static UIElement BuildDeviceCard(GatewayNodeInfo node)
    {
        var nodeName = !string.IsNullOrWhiteSpace(node.DisplayName) ? node.DisplayName : node.ShortId;

        var grid = new Grid
        {
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            RowSpacing = 2,
            ColumnSpacing = 6
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // dot
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // platform badge
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // chevron

        // Row 0: status dot
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                node.IsOnline ? Microsoft.UI.Colors.LimeGreen : Microsoft.UI.Colors.Gray)
        };
        Grid.SetRow(dot, 0);
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        // Row 0: device name
        var nameBlock = new TextBlock
        {
            Text = nodeName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };
        Grid.SetRow(nameBlock, 0);
        Grid.SetColumn(nameBlock, 1);
        grid.Children.Add(nameBlock);

        // Row 0: platform badge
        if (!string.IsNullOrEmpty(node.Platform))
        {
            var badge = BuildBadge(node.Platform);
            Grid.SetRow(badge, 0);
            Grid.SetColumn(badge, 2);
            grid.Children.Add(badge);
        }

        // Row 0: chevron
        var chevron = new TextBlock
        {
            Text = "›",
            FontSize = 14,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };
        Grid.SetRow(chevron, 0);
        Grid.SetColumn(chevron, 3);
        grid.Children.Add(chevron);

        // Row 1: capability icons + count + online/offline
        var row1 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Capability emoji icons
        var capIcons = new System.Text.StringBuilder();
        if (node.Capabilities.Count > 0)
        {
            foreach (var cap in node.Capabilities)
            {
                if (CapabilityIcons.TryGetValue(cap, out var icon))
                    capIcons.Append(icon);
            }
        }
        var capText = capIcons.Length > 0
            ? $"{capIcons} {node.CapabilityCount} caps"
            : node.CapabilityCount > 0 ? $"{node.CapabilityCount} caps" : "";
        var statusLabel = node.IsOnline ? "online" : "offline";
        var row1Text = !string.IsNullOrEmpty(capText) ? $"{capText}  ·  {statusLabel}" : statusLabel;

        row1.Children.Add(new TextBlock
        {
            Text = row1Text,
            FontSize = 11,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        });
        Grid.SetRow(row1, 1);
        Grid.SetColumn(row1, 1);
        Grid.SetColumnSpan(row1, 3);
        grid.Children.Add(row1);

        return grid;
    }

    private static List<TrayMenuFlyoutItem> BuildDeviceFlyoutItems(GatewayNodeInfo node)
    {
        var nodeName = !string.IsNullOrWhiteSpace(node.DisplayName) ? node.DisplayName : node.ShortId;
        var items = new List<TrayMenuFlyoutItem>
        {
            new() { Text = nodeName, IsHeader = true },
        };

        // Status + platform + mode on one line
        var statusIcon = node.IsOnline ? "🟢" : "⚪";
        var statusText = node.IsOnline ? "Online" : "Offline";
        var infoParts = new List<string> { $"{statusIcon} {statusText}" };
        if (!string.IsNullOrEmpty(node.Platform)) infoParts.Add(node.Platform);
        if (!string.IsNullOrEmpty(node.Mode)) infoParts.Add(node.Mode);
        items.Add(new() { Text = string.Join(" · ", infoParts) });

        // Last seen
        if (node.LastSeen.HasValue)
        {
            var age = DateTime.UtcNow - node.LastSeen.Value;
            var seenText = age.TotalMinutes < 1 ? "just now"
                : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
                : age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago"
                : $"{(int)age.TotalDays}d ago";
            items.Add(new() { Text = $"Last seen {seenText}" });
        }

        // Capabilities + Commands merged — capability as header, commands as details
        if (node.Capabilities.Count > 0 || node.Commands.Count > 0)
        {
            items.Add(new() { Text = $"Capabilities ({node.CapabilityCount}) · Commands ({node.CommandCount})", IsHeader = true });

            var cmdGroups = node.Commands
                .GroupBy(c => c.Contains('.') ? c[..c.IndexOf('.')] : c, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(c => c.Contains('.') ? c[(c.IndexOf('.') + 1)..] : c).ToList(), StringComparer.OrdinalIgnoreCase);

            // Show each capability with its commands on separate lines
            var shownGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cap in node.Capabilities)
            {
                var icon = CapabilityIcons.TryGetValue(cap, out var emoji) ? emoji : "▪";
                if (cmdGroups.TryGetValue(cap, out var cmds) && cmds.Count > 0)
                {
                    items.Add(new() { Text = $"{icon} {cap}" });
                    items.Add(new() { Text = $"    {string.Join(", ", cmds)}" });
                    shownGroups.Add(cap);
                }
                else
                {
                    items.Add(new() { Text = $"{icon} {cap}" });
                    shownGroups.Add(cap);
                }
            }

            // Show any command groups not covered by a capability
            foreach (var group in cmdGroups.Where(g => !shownGroups.Contains(g.Key)).OrderBy(g => g.Key))
            {
                items.Add(new() { Text = $"▸ {group.Key}" });
                items.Add(new() { Text = $"    {string.Join(", ", group.Value)}" });
            }
        }

        return items;
    }

    private static Border BuildBadge(string text)
    {
        return new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                IsTextSelectionEnabled = false
            }
        };
    }

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

        // Copy identity file from legacy location if needed
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
        if (_connectionManager == null)
            return false;

        var credential = ResolveStartupOperatorCredential(record);
        if (credential == null)
        {
            Logger.Info($"Active gateway has no usable credential — skipping {context} connect");
            return false;
        }

        var connectionKind = record.LastConnected.HasValue
            ? "last successful gateway"
            : "credentialed gateway";
        Logger.Info($"Connecting to {connectionKind} during {context}: {record.Url} ({credential.Source})");
        _ = _connectionManager.ConnectAsync(record.Id);
        return true;
    }

    private OpenClawTray.Services.Connection.GatewayCredential? ResolveStartupOperatorCredential(GatewayRecord record)
    {
        if (_gatewayRegistry == null)
            return null;

        var resolver = new CredentialResolver(DeviceIdentityFileReader.Instance);
        var identityDir = _gatewayRegistry.GetIdentityDirectory(record.Id);
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

        var nodeService = EnsureNodeServiceForLocalGatewaySetup(_settings);
        if (nodeService == null)
        {
            Logger.Warn("MCP-only mode requested but node service could not be initialized");
            return false;
        }

        try
        {
            nodeService.StartLocalOnlyAsync().GetAwaiter().GetResult();
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
        // Unsubscribe from old client
        if (e.OldClient is { } old)
        {
            old.StatusChanged -= OnConnectionStatusChanged;
            old.AuthenticationFailed -= OnAuthenticationFailed;
            old.ActivityChanged -= OnActivityChanged;
            old.NotificationReceived -= OnNotificationReceived;
            old.ChannelHealthUpdated -= OnChannelHealthUpdated;
            old.SessionsUpdated -= OnSessionsUpdated;
            old.UsageUpdated -= OnUsageUpdated;
            old.UsageStatusUpdated -= OnUsageStatusUpdated;
            old.UsageCostUpdated -= OnUsageCostUpdated;
            old.NodesUpdated -= OnNodesUpdated;
            old.SessionPreviewUpdated -= OnSessionPreviewUpdated;
            old.SessionCommandCompleted -= OnSessionCommandCompleted;
            old.GatewaySelfUpdated -= OnGatewaySelfUpdated;
            old.CronListUpdated -= OnCronListUpdated;
            old.CronStatusUpdated -= OnCronStatusUpdated;
            old.CronRunsUpdated -= OnCronRunsUpdated;
            old.ConfigUpdated -= OnConfigUpdated;
            old.ConfigSchemaUpdated -= OnConfigSchemaUpdated;
            old.SkillsStatusUpdated -= OnSkillsStatusUpdated;
            old.AgentEventReceived -= OnAgentEventReceived;
            old.NodePairListUpdated -= OnNodePairListUpdated;
            old.DevicePairListUpdated -= OnDevicePairListUpdated;
            old.ModelsListUpdated -= OnModelsListUpdated;
            old.PresenceUpdated -= OnPresenceUpdated;
            old.AgentsListUpdated -= OnAgentsListUpdated;
            old.AgentFilesListUpdated -= OnAgentFilesListUpdated;
            old.AgentFileContentUpdated -= OnAgentFileContentUpdated;
        }

        // Subscribe to new client
        if (e.NewClient is { } client)
        {
            client.SetUserRules(_settings?.UserRules?.Count > 0 ? _settings.UserRules : null);
            client.SetPreferStructuredCategories(_settings?.PreferStructuredCategories ?? true);
            client.StatusChanged += OnConnectionStatusChanged;
            client.AuthenticationFailed += OnAuthenticationFailed;
            client.ActivityChanged += OnActivityChanged;
            client.NotificationReceived += OnNotificationReceived;
            client.ChannelHealthUpdated += OnChannelHealthUpdated;
            client.SessionsUpdated += OnSessionsUpdated;
            client.UsageUpdated += OnUsageUpdated;
            client.UsageStatusUpdated += OnUsageStatusUpdated;
            client.UsageCostUpdated += OnUsageCostUpdated;
            client.NodesUpdated += OnNodesUpdated;
            client.SessionPreviewUpdated += OnSessionPreviewUpdated;
            client.SessionCommandCompleted += OnSessionCommandCompleted;
            client.GatewaySelfUpdated += OnGatewaySelfUpdated;
            client.CronListUpdated += OnCronListUpdated;
            client.CronStatusUpdated += OnCronStatusUpdated;
            client.CronRunsUpdated += OnCronRunsUpdated;
            client.ConfigUpdated += OnConfigUpdated;
            client.ConfigSchemaUpdated += OnConfigSchemaUpdated;
            client.SkillsStatusUpdated += OnSkillsStatusUpdated;
            client.AgentEventReceived += OnAgentEventReceived;
            client.NodePairListUpdated += OnNodePairListUpdated;
            client.DevicePairListUpdated += OnDevicePairListUpdated;
            client.ModelsListUpdated += OnModelsListUpdated;
            client.PresenceUpdated += OnPresenceUpdated;
            client.AgentsListUpdated += OnAgentsListUpdated;
            client.AgentFilesListUpdated += OnAgentFilesListUpdated;
            client.AgentFileContentUpdated += OnAgentFileContentUpdated;

            _chatCoordinator?.SetOperatorClient(client);
        }
        else
        {
            _chatCoordinator?.SetOperatorClient(null);
        }

        RaiseChatProviderChanged();

        _lastGatewaySelf = null;

        // Update UI references
        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (_hubWindow != null && !_hubWindow.IsClosed)
            {
                _hubWindow.GatewayClient = _connectionManager?.OperatorClient;
                _hubWindow.CurrentStatus = _currentStatus;
            }
        });
    }

    private void RaiseChatProviderChanged()
    {
        ChatProviderChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Handles the connection manager's StateChanged event.
    /// Maps the snapshot to the existing tray icon / UI status system.
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

        _currentStatus = mapped;
        _dispatcherQueue?.TryEnqueue(() =>
        {
            _hubWindow?.UpdateStatus(mapped);
            UpdateTrayIcon();
        });
    }

    private NodeService? EnsureNodeServiceForLocalGatewaySetup(SettingsManager settings)
    {
        if (_nodeService != null)
            return _nodeService;

        if (_dispatcherQueue == null)
            return null;

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
            _nodeService.PairingStatusChanged += OnPairingStatusChanged;
            _nodeService.ChannelHealthUpdated += OnChannelHealthUpdated;
            _nodeService.InvokeCompleted += OnNodeInvokeCompleted;
            _nodeService.GatewaySelfUpdated += OnGatewaySelfUpdated;
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
        var app = _nodeService?.AppCapability;
        if (app == null) return;

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
            connectionStatus = _currentStatus.ToString(),
            nodeConnected = _nodeService?.IsConnected ?? false,
            nodePaired = _nodeService?.IsPaired ?? false,
            nodePendingApproval = _nodeService?.IsPendingApproval ?? false,
            gatewayVersion = _lastGatewaySelf?.ServerVersion,
            sessionCount = _lastSessions?.Length ?? 0,
            nodeCount = _lastNodes?.Length ?? 0,
        };

        app.SessionsHandler = async (agentId) =>
        {
            var sessions = _lastSessions ?? Array.Empty<SessionInfo>();
            if (!string.IsNullOrEmpty(agentId))
                sessions = sessions.Where(s => s.Key != null &&
                    s.Key.StartsWith($"agent:{agentId}:", StringComparison.OrdinalIgnoreCase)).ToArray();
            return sessions.Select(s => new { s.Key, s.Status, s.Model, s.AgeText, tokens = s.InputTokens + s.OutputTokens }).ToArray();
        };

        app.AgentsHandler = async () =>
        {
            if (_lastAgentsList.HasValue &&
                _lastAgentsList.Value.TryGetProperty("agents", out var agentsArr) &&
                agentsArr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                return System.Text.Json.JsonSerializer.Deserialize<object>(agentsArr.GetRawText());
            }
            return Array.Empty<object>();
        };

        app.NodesHandler = () =>
        {
            return _lastNodes?.Select(n => new { n.DisplayName, n.NodeId, n.IsOnline, n.Platform, n.CapabilityCount }).ToArray()
                ?? Array.Empty<object>();
        };

        app.ConfigGetHandler = async (path) =>
        {
            if (_hubWindow?.LastConfig == null) return new { error = "Config not loaded" };
            // Config is already redacted by the gateway's redactConfigSnapshot
            var raw = _hubWindow.LastConfig.Value;
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
                new { type = "status", status = _currentStatus.ToString() },
                new { type = "sessions", count = _lastSessions?.Length ?? 0 },
                new { type = "nodes", count = _lastNodes?.Length ?? 0 },
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
    }

    private static bool RequiresSetup(SettingsManager settings)
    {
        return StartupSetupState.RequiresSetup(settings, IdentityDataPath);
    }

    private bool ShouldInitializeNodeService()
    {
        if (_suppressNodeDuringSetup) return false;
        return _settings?.EnableNodeMode == true || _settings?.EnableMcpServer == true;
    }

    private bool ShouldInitializeNodeService(GatewayRecord activeGateway, string managerIdentityPath)
    {
        if (!ShouldInitializeNodeService()) return false;

        if (LocalNodeServiceOwnsIdentityFor(activeGateway))
        {
            Logger.Info("[ConnMgr] Suppressing manager-owned NodeConnector because local NodeService owns the active local gateway identity");
            return false;
        }

        return true;
    }

    private bool LocalNodeServiceOwnsIdentityFor(GatewayRecord activeGateway)
    {
        if (!activeGateway.IsLocal || _settings == null) return false;
        if (!StartupSetupState.HasStoredNodeDeviceToken(IdentityDataPath)) return false;

        return EnsureNodeServiceForLocalGatewaySetup(_settings) != null;
    }

    private void OnNodeStatusChanged(object? sender, ConnectionStatus status)
    {
        Logger.Info($"Node status: {status}");
        AddRecentActivity($"Node mode {status}", category: "node", dashboardPath: "nodes");
        
        // In node-only mode, surface node connection in main status indicator
        if (_settings?.EnableNodeMode == true)
        {
            // Status field is maintained by OnManagerStateChanged — no write needed here.
            _hubWindow?.UpdateStatus(status);
            UpdateTrayIcon();
            _dispatcherQueue?.TryEnqueue(UpdateStatusDetailWindow);
        }

        // Keep hub node state in sync for ConnectionPage
        SyncHubNodeState();
        
        // Don't show "connected" toast if waiting for pairing - we'll show pairing status instead
        var nodeService = _nodeService;
        if (status == ConnectionStatus.Connected && nodeService?.IsPaired == true)
        {
            var deviceId = nodeService.FullDeviceId;
            if (HasRecentToast("node-paired", deviceId))
            {
                Logger.Info($"[ToastDeduper] Suppressed node-connected toast after node-paired deviceId={deviceId}");
                return;
            }

            try
            {
                ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_NodeModeActive"))
                    .AddText(LocalizationHelper.GetString("Toast_NodeModeActiveDetail")),
                    "node-connected",
                    deviceId);
            }
            catch { /* ignore */ }
        }
    }
    
    private void OnRecordingStateChanged(object? sender, RecordingStateEventArgs args)
    {
        var source = args.Type == RecordingType.Screen ? "Screen" : "Camera";
        if (args.IsActive)
        {
            var title = args.Type == RecordingType.Screen
                ? LocalizationHelper.GetString("Activity_ScreenRecordingStarted")
                : LocalizationHelper.GetString("Activity_CameraRecordingStarted");
            var duration = args.DurationMs > 0 ? $" ({args.DurationMs / 1000.0:0.#}s)" : "";
            AddRecentActivity($"{title}{duration}", category: "node",
                icon: "🔴",
                details: string.Format(LocalizationHelper.GetString("Activity_RecordingRequestedByAgent"), source));
        }
        else
        {
            var title = args.Type == RecordingType.Screen
                ? LocalizationHelper.GetString("Activity_ScreenRecordingComplete")
                : LocalizationHelper.GetString("Activity_CameraRecordingComplete");
            AddRecentActivity(title, category: "node",
                icon: "✅",
                details: string.Format(LocalizationHelper.GetString("Activity_RecordingSentToAgent"), source));
        }
    }

    private void OnPairingStatusChanged(object? sender, OpenClaw.Shared.PairingStatusEventArgs args)
    {
        Logger.Info($"Pairing status: {args.Status}");
        
        try
        {
            if (args.Status == OpenClaw.Shared.PairingStatus.Pending)
            {
                // Bug #2 (manual test 2026-05-05): suppress the "copy pairing command"
                // toast while the local-setup engine is mid-Phase-14 node-role PairAsync.
                // The loopback gateway parks the role-upgrade as Pending for ~100ms before
                // SettingsWindowsTrayNodeProvisioner's pending-approver auto-approves it;
                // the user never needs to copy the command in that window. Manual
                // ConnectionPage pairings call ShowPairingPendingNotification directly
                // (bypassing this event handler), so the suppression scope is exactly
                // the autopair window.
                if (LocalGatewaySetupEngine.ShouldSuppressPairingPendingNotification(_localSetupEngine, args.Status))
                {
                    Logger.Info($"Suppressing pairing-pending toast: autopair Phase 14 in progress for {args.DeviceId}");
                    return;
                }
                ShowPairingPendingNotification(args.DeviceId);
            }
            else if (args.Status == OpenClaw.Shared.PairingStatus.Paired)
            {
                // Bug 3: idempotency guard — only show "Node paired" toast/activity once
                // per device per session. WS reconnects re-fire Paired; suppress duplicates.
                var deviceKey = args.DeviceId ?? string.Empty;
                if (_shownPairedToasts.Add(deviceKey))
                {
                    AddRecentActivity("Node paired", category: "node", dashboardPath: "nodes", nodeId: args.DeviceId);
                    ShowToast(new ToastContentBuilder()
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
                ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_PairingRejected"))
                    .AddText(LocalizationHelper.GetString("Toast_PairingRejectedDetail")),
                    "node-pairing-rejected",
                    args.DeviceId);
            }
        }
        catch { /* ignore */ }

        SyncHubNodeState();
    }

    /// <summary>
    /// Pushes current node service state to hub window so ConnectionPage reflects live pairing/identity.
    /// </summary>
    private void SyncHubNodeState()
    {
        if (_hubWindow == null || _hubWindow.IsClosed) return;
        if (_nodeService != null)
        {
            _hubWindow.NodeIsConnected = _nodeService.IsConnected;
            _hubWindow.NodeIsPaired = _nodeService.IsPaired;
            _hubWindow.NodeIsPendingApproval = _nodeService.IsPendingApproval;
            _hubWindow.NodeShortDeviceId = _nodeService.ShortDeviceId;
            _hubWindow.NodeFullDeviceId = _nodeService.FullDeviceId;
            _hubWindow.VoiceServiceInstance = _nodeService.VoiceService;
        }
        else
        {
            _hubWindow.NodeIsConnected = false;
            _hubWindow.NodeIsPaired = false;
            _hubWindow.NodeIsPendingApproval = false;
        }
    }

    public static string BuildPairingApprovalCommand(string deviceId) =>
        $"openclaw devices approve {deviceId}";

    public void ShowPairingPendingNotification(string deviceId, string? approvalCommand = null)
    {
        var command = approvalCommand ?? BuildPairingApprovalCommand(deviceId);
        var shortDeviceId = deviceId.Length > 16 ? deviceId[..16] : deviceId;

        AddRecentActivity("Node pairing pending", category: "node", dashboardPath: "nodes", nodeId: deviceId);
        ShowToast(new ToastContentBuilder()
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
            ShowToast(new ToastContentBuilder()
                .AddText(args.Title)
                .AddText(args.Body));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to show node notification: {ex.Message}");
        }
    }

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

        _dispatcherQueue?.TryEnqueue(UpdateStatusDetailWindow);
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

    private void OnConnectionStatusChanged(object? sender, ConnectionStatus status)
    {
        // Status field is maintained by OnManagerStateChanged — no write needed here.
        DiagnosticsJsonlService.Write("connection.status", new
        {
            status = status.ToString(),
            nodeMode = _settings?.EnableNodeMode == true
        });
        _hubWindow?.UpdateStatus(status);
        if (status == ConnectionStatus.Connected)
        {
            _authFailureMessage = null;
            if (_hubWindow != null && !_hubWindow.IsClosed)
                _hubWindow.LastAuthError = null;
        }

        // Clear stale data when disconnected so tray menu doesn't show old sessions/nodes
        if (status == ConnectionStatus.Disconnected || status == ConnectionStatus.Error)
        {
            _lastSessions = Array.Empty<SessionInfo>();
            _lastChannels = Array.Empty<ChannelHealth>();
            _lastNodes = Array.Empty<GatewayNodeInfo>();
            _lastNodePairList = null;
            _lastDevicePairList = null;
            _lastModelsList = null;
            _lastGatewaySelf = null;
        }

        UpdateTrayIcon();
        _dispatcherQueue?.TryEnqueue(UpdateStatusDetailWindow);
        
        if (status == ConnectionStatus.Connected)
        {
            _ = RunHealthCheckAsync();
        }
    }

    private void OnAuthenticationFailed(object? sender, string message)
    {
        _authFailureMessage = message;
        Logger.Error($"Authentication failed: {message}");
        DiagnosticsJsonlService.Write("connection.auth_failed", new
        {
            message,
            nodeMode = _settings?.EnableNodeMode == true
        });
        AddRecentActivity($"Auth failed: {message}", category: "error");
        UpdateTrayIcon();

        // Forward to hub/connection page
        if (_hubWindow != null && !_hubWindow.IsClosed)
        {
            _hubWindow.LastAuthError = message;
            _hubWindow.UpdateStatus(_currentStatus);
        }
    }

    private void OnActivityChanged(object? sender, AgentActivity? activity)
    {
        if (activity == null)
        {
            // Activity ended
            if (_displayedSessionKey != null && _sessionActivities.ContainsKey(_displayedSessionKey))
            {
                _sessionActivities.Remove(_displayedSessionKey);
            }
            _currentActivity = null;
        }
        else
        {
            var sessionKey = activity.SessionKey ?? "default";
            _sessionActivities[sessionKey] = activity;
            AddRecentActivity(
                $"{sessionKey}: {activity.Label}",
                category: "session",
                dashboardPath: $"sessions/{sessionKey}",
                details: activity.Kind.ToString(),
                sessionKey: sessionKey);

            // Debounce session switching
            var now = DateTime.Now;
            if (_displayedSessionKey != sessionKey && 
                (now - _lastSessionSwitch) > SessionSwitchDebounce)
            {
                _displayedSessionKey = sessionKey;
                _lastSessionSwitch = now;
            }

            if (_displayedSessionKey == sessionKey)
            {
                _currentActivity = activity;
            }
        }
        
        UpdateTrayIcon();
    }

    private void OnChannelHealthUpdated(object? sender, ChannelHealth[] channels)
    {
        _lastChannels = channels;
        _lastCheckTime = DateTime.Now;
        var signature = string.Join("|", channels
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => $"{c.Name}:{c.Status}:{c.Error}"));
        if (!string.Equals(signature, _lastChannelStatusSignature, StringComparison.Ordinal))
        {
            _lastChannelStatusSignature = signature;
            var summary = channels.Length == 0
                ? "No channels reported"
                : string.Join(", ", channels.Select(c => $"{c.Name}={c.Status}"));
            DiagnosticsJsonlService.Write("gateway.health.channels", new
            {
                channelCount = channels.Length,
                healthyCount = channels.Count(c => ChannelHealth.IsHealthyStatus(c.Status)),
                errorCount = channels.Count(c => !string.IsNullOrWhiteSpace(c.Error))
            });
            AddRecentActivity("Channel health updated", category: "channel", dashboardPath: "channels", details: summary);
        }

        _dispatcherQueue?.TryEnqueue(() =>
        {
            _hubWindow?.UpdateChannelHealth(channels);
            _hubWindow?.UpdateStatus(_currentStatus);
        });
    }

    private void OnSessionsUpdated(object? sender, SessionInfo[] sessions)
    {
        _lastSessions = sessions;
        _dispatcherQueue?.TryEnqueue(UpdateStatusDetailWindow);

        _dispatcherQueue?.TryEnqueue(() =>
        {
            _hubWindow?.UpdateSessions(sessions);
        });

        var activeKeys = new HashSet<string>(sessions.Select(s => s.Key), StringComparer.Ordinal);
        lock (_sessionPreviewsLock)
        {
            var stale = _sessionPreviews.Keys.Where(key => !activeKeys.Contains(key)).ToArray();
            foreach (var key in stale)
                _sessionPreviews.Remove(key);
        }

        if (_connectionManager?.OperatorClient != null &&
            sessions.Length > 0 &&
            DateTime.UtcNow - _lastPreviewRequestUtc > TimeSpan.FromSeconds(5))
        {
            _lastPreviewRequestUtc = DateTime.UtcNow;
            var keys = sessions.Take(5).Select(s => s.Key).ToArray();
            _ = _connectionManager.OperatorClient.RequestSessionPreviewAsync(keys, limit: 3, maxChars: 140);
        }
    }

    private void OnUsageUpdated(object? sender, GatewayUsageInfo usage)
    {
        _lastUsage = usage;
        _dispatcherQueue?.TryEnqueue(() =>
        {
            _hubWindow?.UpdateUsage(usage);
        });
    }

    private void OnUsageStatusUpdated(object? sender, GatewayUsageStatusInfo usageStatus)
    {
        _lastUsageStatus = usageStatus;
        _dispatcherQueue?.TryEnqueue(() =>
        {
            _hubWindow?.UpdateUsageStatus(usageStatus);
        });
    }

    private void OnUsageCostUpdated(object? sender, GatewayCostUsageInfo usageCost)
    {
        _lastUsageCost = usageCost;
        _dispatcherQueue?.TryEnqueue(UpdateStatusDetailWindow);

        _dispatcherQueue?.TryEnqueue(() =>
        {
            _hubWindow?.UpdateUsageCost(usageCost);
        });

        if (DateTime.UtcNow - _lastUsageActivityLogUtc > TimeSpan.FromMinutes(1))
        {
            _lastUsageActivityLogUtc = DateTime.UtcNow;
            AddRecentActivity(
                $"{usageCost.Days}d usage ${usageCost.Totals.TotalCost:F2}",
                category: "usage",
                dashboardPath: "usage",
                details: $"{usageCost.Totals.TotalTokens:N0} tokens");
        }
    }

    private void OnGatewaySelfUpdated(object? sender, GatewaySelfInfo gatewaySelf)
    {
        _lastGatewaySelf = _lastGatewaySelf?.Merge(gatewaySelf) ?? gatewaySelf;
        DiagnosticsJsonlService.Write("gateway.self", new
        {
            version = _lastGatewaySelf.ServerVersion,
            protocol = _lastGatewaySelf.Protocol,
            uptimeMs = _lastGatewaySelf.UptimeMs,
            authMode = _lastGatewaySelf.AuthMode,
            stateVersionPresence = _lastGatewaySelf.StateVersionPresence,
            stateVersionHealth = _lastGatewaySelf.StateVersionHealth,
            presenceCount = _lastGatewaySelf.PresenceCount
        });
        _dispatcherQueue?.TryEnqueue(() =>
        {
            UpdateStatusDetailWindow();
            _hubWindow?.UpdateGatewaySelf(_lastGatewaySelf);
        });
    }

    private void OnNodesUpdated(object? sender, GatewayNodeInfo[] nodes)
    {
        var previousCount = _lastNodes.Length;
        var previousOnline = _lastNodes.Count(n => n.IsOnline);
        var online = nodes.Count(n => n.IsOnline);
        _lastNodes = nodes;
        _dispatcherQueue?.TryEnqueue(UpdateStatusDetailWindow);

        _dispatcherQueue?.TryEnqueue(() =>
        {
            _hubWindow?.UpdateNodes(nodes);
        });

        if (nodes.Length != previousCount || online != previousOnline)
        {
            AddRecentActivity(
                $"Nodes {online}/{nodes.Length} online",
                category: "node",
                dashboardPath: "nodes");
        }
    }

    private void OnSessionPreviewUpdated(object? sender, SessionsPreviewPayloadInfo payload)
    {
        lock (_sessionPreviewsLock)
        {
            foreach (var preview in payload.Previews)
            {
                _sessionPreviews[preview.Key] = preview;
            }
        }
        _dispatcherQueue?.TryEnqueue(UpdateStatusDetailWindow);
    }

    private void OnSessionCommandCompleted(object? sender, SessionCommandResult result)
    {
        if (_dispatcherQueue == null) return;

        _dispatcherQueue.TryEnqueue(() =>
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

                ShowToast(new ToastContentBuilder()
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

    private void OnCronListUpdated(object? sender, System.Text.Json.JsonElement data)
    {
        _dispatcherQueue?.TryEnqueue(() => _hubWindow?.UpdateCronList(data));
    }

    private void OnCronStatusUpdated(object? sender, System.Text.Json.JsonElement data)
    {
        _dispatcherQueue?.TryEnqueue(() => _hubWindow?.UpdateCronStatus(data));
    }

    private void OnCronRunsUpdated(object? sender, System.Text.Json.JsonElement data)
    {
        _dispatcherQueue?.TryEnqueue(() => _hubWindow?.UpdateCronRuns(data));
    }

    private void OnSkillsStatusUpdated(object? sender, System.Text.Json.JsonElement data)
    {
        _dispatcherQueue?.TryEnqueue(() => _hubWindow?.UpdateSkillsStatus(data));
    }

    private void OnConfigUpdated(object? sender, System.Text.Json.JsonElement data)
    {
        _dispatcherQueue?.TryEnqueue(() => _hubWindow?.UpdateConfig(data));
    }

    private void OnConfigSchemaUpdated(object? sender, System.Text.Json.JsonElement data)
    {
        _dispatcherQueue?.TryEnqueue(() => _hubWindow?.UpdateConfigSchema(data));
    }

    private System.Text.Json.JsonElement? _lastAgentsList;

    private void OnAgentsListUpdated(object? sender, System.Text.Json.JsonElement data)
    {
        _lastAgentsList = data.Clone();
        _dispatcherQueue?.TryEnqueue(() => _hubWindow?.UpdateAgentsList(data));
    }

    private void OnAgentFilesListUpdated(object? sender, System.Text.Json.JsonElement data)
    {
        _dispatcherQueue?.TryEnqueue(() => _hubWindow?.UpdateAgentFilesList(data));
    }

    private void OnAgentFileContentUpdated(object? sender, System.Text.Json.JsonElement data)
    {
        _dispatcherQueue?.TryEnqueue(() => _hubWindow?.UpdateAgentFileContent(data));
    }

    private void OnAgentEventReceived(object? sender, AgentEventInfo evt)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            _agentEventsCache.Insert(0, evt);
            if (_agentEventsCache.Count > MaxAppAgentEvents)
                _agentEventsCache.RemoveRange(MaxAppAgentEvents, _agentEventsCache.Count - MaxAppAgentEvents);
            _hubWindow?.UpdateAgentEvent(evt);
        });
    }

    private void OnNodePairListUpdated(object? sender, PairingListInfo data)
    {
        _lastNodePairList = data;
        _dispatcherQueue?.TryEnqueue(() => _hubWindow?.UpdateNodePairList(data));
    }

    private void OnDevicePairListUpdated(object? sender, DevicePairingListInfo data)
    {
        _lastDevicePairList = data;
        _dispatcherQueue?.TryEnqueue(() => _hubWindow?.UpdateDevicePairList(data));
    }

    private void OnModelsListUpdated(object? sender, ModelsListInfo data)
    {
        _lastModelsList = data;
        _dispatcherQueue?.TryEnqueue(() => _hubWindow?.UpdateModelsList(data));
    }

    private void OnPresenceUpdated(object? sender, PresenceEntry[] data)
    {
        _lastPresence = data;
        _dispatcherQueue?.TryEnqueue(() => _hubWindow?.UpdatePresence(data));
    }

    private void OnNotificationReceived(object? sender, OpenClawNotification notification)
    {
        AddRecentActivity(
            $"{notification.Type ?? "info"}: {notification.Title ?? "notification"}",
            category: "notification",
            details: notification.Message);

        // Voice overlay: show agent chat responses, and (independently) speak them
        // if the user enabled "Read responses aloud". TTS used to be gated on
        // an active voice overlay session — we want the toggle to honor every
        // chat reply now that voice and text chat will eventually share one UI.
        if (notification.IsChat && !string.IsNullOrEmpty(notification.Message))
        {
            if (_voiceOverlayWindow != null)
            {
                _dispatcherQueue?.TryEnqueue(() =>
                {
                    try
                    {
                        _voiceOverlayWindow?.AddAgentResponse(notification.Message);
                    }
                    catch { }
                });
            }

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

            // Add category-specific inline image (emoji rendered as text is fine, 
            // but we can add app logo override for better visibility)
            var logoPath = GetNotificationIcon(notification.Type);
            if (!string.IsNullOrEmpty(logoPath) && System.IO.File.Exists(logoPath))
            {
                builder.AddAppLogoOverride(new Uri(logoPath), ToastGenericAppLogoCrop.Circle);
            }

            // Add "Open Chat" button for chat notifications
            if (notification.IsChat)
            {
                builder.AddArgument("action", "open_chat")
                       .AddButton(new ToastButton()
                           .SetContent("Open Chat")
                           .AddArgument("action", "open_chat"));
            }

            ShowToast(builder);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to show toast: {ex.Message}");
        }
    }

    private static string? GetNotificationIcon(string? type)
    {
        // For now, use the app icon for all notifications
        // In the future, we could create category-specific icons
        var appDir = AppContext.BaseDirectory;
        var iconPath = System.IO.Path.Combine(appDir, "Assets", "claw.ico");
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
            if (_onboardingWindow != null)
                return false; // Onboarding window has chat overlay
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
                _lastCheckTime = DateTime.Now;
                _dispatcherQueue?.TryEnqueue(UpdateStatusDetailWindow);
                if (userInitiated)
                {
                    ShowToast(new ToastContentBuilder()
                        .AddText(LocalizationHelper.GetString("Toast_HealthCheck"))
                        .AddText("Node Mode is connected; gateway health is streaming."));
                }
                return;
            }

            if (userInitiated)
            {
                ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_HealthCheck"))
                    .AddText(LocalizationHelper.GetString("Toast_HealthCheckNotConnected")));
            }
            return;
        }

        try
        {
            _lastCheckTime = DateTime.Now;
            await client.CheckHealthAsync();
            if (userInitiated)
            {
                ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_HealthCheck"))
                    .AddText(LocalizationHelper.GetString("Toast_HealthCheckSent")));
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Health check failed: {ex.Message}");
            if (userInitiated)
            {
                ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_HealthCheckFailed"))
                    .AddText(ex.Message));
            }
        }
    }

    #endregion

    #region Tray Icon

    private void UpdateTrayIcon()
    {
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
        Status = _currentStatus,
        CurrentActivity = _currentActivity,
        Channels = _lastChannels,
        Nodes = _lastNodes,
        LocalNodeFallback = _nodeService?.GetLocalNodeInfo(),
        AuthFailureMessage = _authFailureMessage,
        LastCheckTime = _lastCheckTime,
        Settings = _settings
    };

    #endregion

    #region Window Management

    internal void ShowHub(string? navigateTo = null, bool activate = true)
    {
        if (_hubWindow == null || _hubWindow.IsClosed)
        {
            _hubWindow = new HubWindow();
            _hubWindow.Settings = _settings;
            _hubWindow.GatewayClient = _connectionManager?.OperatorClient;
            _hubWindow.CurrentStatus = _currentStatus;
            _hubWindow.OpenDashboardAction = OpenDashboard;
            _hubWindow.CheckForUpdatesAction = () => _ = CheckForUpdatesUserInitiatedAsync();
            _hubWindow.QuickSendAction = () => ShowQuickSend();
            _hubWindow.OpenSetupAction = () => _ = ShowOnboardingAsync();
            _hubWindow.OpenConnectionStatusAction = ShowConnectionStatusWindow;
            _hubWindow.OpenVoiceAction = () => ShowVoiceOverlay();
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
                _hubWindow?.UpdateStatus(ConnectionStatus.Disconnected);
            };
            _hubWindow.ReconnectAction = () =>
            {
                _ = _connectionManager?.ReconnectAsync();
            };
            _hubWindow.ClearAppAgentEventsCache = () => _agentEventsCache.Clear();
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

            // Seed ALL cached data BEFORE first navigation so pages see data in Initialize()
            SeedHubCachedData();

            // Navigate to default page now that properties and data are set
            _hubWindow.NavigateToDefault();
        }
        // Always update live state
        _hubWindow.Settings = _settings;
        _hubWindow.GatewayClient = _connectionManager?.OperatorClient;
        _hubWindow.CurrentStatus = _currentStatus;
        _hubWindow.VoiceServiceInstance = _nodeService?.VoiceService ?? _standaloneVoiceService;
        if (_nodeService != null)
        {
            _hubWindow.NodeIsConnected = _nodeService.IsConnected;
            _hubWindow.NodeIsPaired = _nodeService.IsPaired;
            _hubWindow.NodeIsPendingApproval = _nodeService.IsPendingApproval;
            _hubWindow.NodeShortDeviceId = _nodeService.ShortDeviceId;
            _hubWindow.NodeFullDeviceId = _nodeService.FullDeviceId;
        }

        // Seed cached data into hub (also on re-show)
        SeedHubCachedData();

        if (navigateTo != null)
        {
            _hubWindow.NavigateTo(navigateTo);
        }
        if (activate)
        {
            _hubWindow.Activate();
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

    private void SeedHubCachedData()
    {
        if (_hubWindow == null) return;
        // Seed all cached data types so pages see data immediately
        if (_lastSessions.Length > 0) _hubWindow.UpdateSessions(_lastSessions);
        if (_lastNodes.Length > 0) _hubWindow.UpdateNodes(_lastNodes);
        if (_lastNodePairList != null) _hubWindow.UpdateNodePairList(_lastNodePairList);
        if (_lastDevicePairList != null) _hubWindow.UpdateDevicePairList(_lastDevicePairList);
        if (_lastModelsList != null) _hubWindow.UpdateModelsList(_lastModelsList);
        if (_lastPresence != null) _hubWindow.UpdatePresence(_lastPresence);
        if (_lastGatewaySelf != null) _hubWindow.UpdateGatewaySelf(_lastGatewaySelf);
        if (_lastAgentsList.HasValue) _hubWindow.UpdateAgentsList(_lastAgentsList.Value);
        if (_agentEventsCache.Count > 0) _hubWindow.SeedAgentEvents(_agentEventsCache);
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
        var currentSnapshot = _settings?.ToSettingsData();
        var impact = SettingsChangeClassifier.Classify(_previousSettingsSnapshot, currentSnapshot);
        _previousSettingsSnapshot = currentSnapshot;
        Logger.Info($"[SETTINGS] Change impact: {impact}");

        switch (impact)
        {
            case SettingsChangeImpact.FullReconnectRequired:
            case SettingsChangeImpact.OperatorReconnectRequired:
                // Full reconnect: tear down everything and rebuild
                _lastGatewaySelf = null;
                if (_settings?.UseSshTunnel != true)
                {
                    _sshTunnelService?.Stop();
                }
                // Status is updated by OnManagerStateChanged when reconnect starts.
                _hubWindow?.UpdateStatus(ConnectionStatus.Disconnected);
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

        // Non-connection settings always applied regardless of impact
        if (_settings!.GlobalHotkeyEnabled)
        {
            _globalHotkey ??= new GlobalHotkeyService();
            _globalHotkey.HotkeyPressed -= OnGlobalHotkeyPressed;
            _globalHotkey.HotkeyPressed += OnGlobalHotkeyPressed;
            _globalHotkey.Register();
        }
        else
        {
            _globalHotkey?.Unregister();
        }

        AutoStartManager.SetAutoStart(_settings.AutoStart);

        // Keep hub window in sync
        if (_hubWindow != null && !_hubWindow.IsClosed)
        {
            _hubWindow.Settings = _settings;
            _hubWindow.GatewayClient = _connectionManager?.OperatorClient;
            _hubWindow.CurrentStatus = _currentStatus;
        }

        // Notify ad-hoc listeners (e.g. ChatWindow may be alive but not
        // owned by the hub) that settings have changed.
        SettingsChanged?.Invoke(this, EventArgs.Empty);
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

    private void ShowQuickSend(string? prefillMessage = null)
    {
        if (_connectionManager?.OperatorClient == null)
        {
            Logger.Warn("QuickSend blocked: gateway client not initialized");
            return;
        }

        try
        {
            // Keep a strong reference to the window; otherwise the dialog can be GC'd
            // and appear to not open (especially when triggered from a hotkey).
            if (_quickSendDialog != null)
            {
                // If caller wants a prefill, re-create to apply it.
                if (!string.IsNullOrEmpty(prefillMessage))
                {
                    try { _quickSendDialog.Close(); } catch { }
                    _quickSendDialog = null;
                }
                else
                {
                    Logger.Info("QuickSend dialog already open; activating");
                    _quickSendDialog.ShowAsync();
                    return;
                }
            }

            Logger.Info("Showing QuickSend dialog");
            // Bug #3: pass a Func that resolves the live OperatorClient on
            // every Send so post-pair / restart / reinit swaps are observed.
            var dialog = new QuickSendDialog(() => _connectionManager?.OperatorClient as OpenClawGatewayClient, prefillMessage);
            dialog.Closed += (s, e) =>
            {
                if (ReferenceEquals(_quickSendDialog, dialog))
                {
                    _quickSendDialog = null;
                }
            };
            _quickSendDialog = dialog;
            dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to show QuickSend dialog: {ex.Message}");
        }
    }

    private void ShowStatusDetail()
    {
        ShowHub("general");
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

    private void RestartSshTunnel()
    {
        if (_settings?.UseSshTunnel != true)
        {
            ShowToast(new ToastContentBuilder()
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
                ShowToast(new ToastContentBuilder()
                    .AddText("SSH tunnel restart failed")
                    .AddText(_sshTunnelService?.LastError ?? "Check SSH tunnel settings and logs."));
                return;
            }

            _ = _connectionManager?.ReconnectAsync();

            UpdateStatusDetailWindow();
            ShowToast(new ToastContentBuilder()
                .AddText("SSH tunnel")
                .AddText("Restarted; reconnecting to gateway."));
        }
        catch (Exception ex)
        {
            Logger.Error($"SSH tunnel restart request failed: {ex.Message}");
            DiagnosticsJsonlService.Write("tunnel.restart_request_failed", new { ex.Message });
            ShowToast(new ToastContentBuilder()
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
        _hubWindow?.UpdateStatus(_currentStatus);
    }

    private GatewayCommandCenterState BuildCommandCenterState() =>
        new CommandCenterStateBuilder(CaptureSnapshot()).Build();

    private AppStateSnapshot CaptureSnapshot() => new AppStateSnapshot
    {
        Status = _currentStatus,
        LastCheckTime = _lastCheckTime,
        Channels = _lastChannels,
        Sessions = _lastSessions,
        Nodes = _lastNodes,
        Usage = _lastUsage,
        UsageStatus = _lastUsageStatus,
        UsageCost = _lastUsageCost,
        GatewaySelf = _lastGatewaySelf,
        AuthFailureMessage = _authFailureMessage,
        LastUpdateInfo = _lastUpdateInfo,
        Settings = _settings,
        NodeService = _nodeService,
        SshTunnelService = _sshTunnelService,
        HasGatewayClient = _connectionManager?.OperatorClient != null
    };

    private void ShowNotificationHistory()
    {
        ShowActivityStream("notification");
    }

    private void ShowActivityStream(string? filter = null)
    {
        ShowHub("activity");
        _hubWindow?.SetActivityFilter(filter);
    }

    private OnboardingWindow? _onboardingWindow;

    private async Task ShowOnboardingAsync()
    {
        if (_settings == null) return;

        if (_onboardingWindow != null)
        {
            try { _onboardingWindow.Activate(); return; } catch { _onboardingWindow = null; }
        }

        // Disconnect existing gateway connection for a clean setup flow.
        // ActiveId is preserved so it can be restored if setup is cancelled.
        var restoreGatewayId = _gatewayRegistry?.ActiveGatewayId;
        var disconnectedForOnboarding = false;
        if (_connectionManager != null &&
            _connectionManager.CurrentSnapshot.OverallState is not OverallConnectionState.Idle)
        {
            Logger.Info("Disconnecting existing gateway connection for clean setup");
            await _connectionManager.DisconnectAsync();
            disconnectedForOnboarding = restoreGatewayId != null;
        }

        var onboardingCompleted = false;
        _onboardingWindow = new OnboardingWindow(_settings, IdentityDataPath);
        _onboardingWindow.OnboardingCompleted += (s, e) =>
        {
            onboardingCompleted = true;
            Logger.Info("Onboarding completed");
            _onboardingWindow = null;

            // If the persistent client was already initialized during onboarding, keep it
            if (_connectionManager?.OperatorClient is OpenClawGatewayClient { IsConnectedToGateway: true })
            {
                Logger.Info("Gateway client already connected from onboarding — keeping");
                return;
            }

            // Reconnect only if there's an active gateway with credentials —
            // don't blindly reconnect a pre-setup gateway the user may be replacing.
            var activeRecord = _gatewayRegistry?.GetActive();
            if (activeRecord != null && TryConnectGatewayIfCredentialAvailable(activeRecord, "post-onboarding"))
            {
                Logger.Info("Reconnecting to active gateway after onboarding");
            }
            else
            {
                Logger.Info("No previously connected gateway after onboarding — skipping reconnect");
                TryStartLocalMcpOnlyNode();
            }

            // Keep hub window in sync with new client
            if (_hubWindow != null && !_hubWindow.IsClosed)
            {
                _hubWindow.Settings = _settings;
                _hubWindow.GatewayClient = _connectionManager?.OperatorClient;
                _hubWindow.CurrentStatus = _currentStatus;
            }
        };
        _onboardingWindow.Closed += (s, e) =>
        {
            _onboardingWindow = null;
            if (!onboardingCompleted && disconnectedForOnboarding && restoreGatewayId != null)
            {
                Logger.Info("Onboarding closed before completion — restoring previous gateway connection");
                _ = _connectionManager?.ConnectAsync(restoreGatewayId);
            }
        };
        _onboardingWindow.Activate();
    }

    private void ShowSurfaceImprovementsTipIfNeeded()
    {
        if (_settings == null || _settings.HasSeenActivityStreamTip) return;

        _settings.HasSeenActivityStreamTip = true;
        _settings.Save();

        try
        {
            ShowToast(new ToastContentBuilder()
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

    private void ShowToast(ToastContentBuilder builder, string? toastTag = null, string? deviceId = null)
    {
        if (!ShouldShowToast(toastTag, deviceId))
            return;

        var sound = _settings?.NotificationSound;
        if (string.Equals(sound, "None", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddAudio(new ToastAudio { Silent = true });
        }
        else if (string.Equals(sound, "Subtle", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddAudio(new Uri("ms-winsoundevent:Notification.IM"), silent: false);
        }
        builder.Show();
    }

    private bool ShouldShowToast(string? toastTag, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(toastTag))
            return true;

        var normalizedDeviceId = NormalizeToastDeviceId(deviceId);
        var dedupeKey = BuildToastKey(toastTag, normalizedDeviceId);
        var now = DateTime.UtcNow;

        foreach (var staleKey in _recentToastKeys
            .Where(pair => now - pair.Value >= ToastDedupeWindow)
            .Select(pair => pair.Key)
            .ToArray())
        {
            _recentToastKeys.Remove(staleKey);
        }

        if (_recentToastKeys.TryGetValue(dedupeKey, out var lastShown) &&
            now - lastShown < ToastDedupeWindow)
        {
            Logger.Info($"[ToastDeduper] Suppressed duplicate toast tag={toastTag} deviceId={normalizedDeviceId}");
            return false;
        }

        _recentToastKeys[dedupeKey] = now;
        Logger.Info($"[ToastDeduper] Showing toast tag={toastTag} deviceId={normalizedDeviceId}");
        return true;
    }

    private bool HasRecentToast(string toastTag, string? deviceId)
    {
        var normalizedDeviceId = NormalizeToastDeviceId(deviceId);
        return _recentToastKeys.TryGetValue(BuildToastKey(toastTag, normalizedDeviceId), out var lastShown) &&
            DateTime.UtcNow - lastShown < ToastDedupeWindow;
    }

    private static string NormalizeToastDeviceId(string? deviceId) =>
        string.IsNullOrWhiteSpace(deviceId) ? "global" : deviceId.Trim();

    private static string BuildToastKey(string toastTag, string normalizedDeviceId) =>
        $"{toastTag.Trim()}:{normalizedDeviceId}";

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
            _settings,
            _gatewayRegistry,
            SettingsManager.SettingsDirectoryPath,
            DeviceIdentityFileReader.Instance,
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
        
        var baseUrl = _settings.GetEffectiveGatewayUrl()
            .Replace("ws://", "http://")
            .Replace("wss://", "https://")
            .TrimEnd('/');

        var url = string.IsNullOrEmpty(path)
            ? baseUrl
            : $"{baseUrl}/{path.TrimStart('/')}";

        var activeToken = _gatewayRegistry?.GetActive()?.SharedGatewayToken;
        if (!string.IsNullOrEmpty(activeToken))
        {
            var separator = url.Contains('?') ? "&" : "?";
            url = $"{url}{separator}token={Uri.EscapeDataString(activeToken)}";
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to open dashboard: {ex.Message}");
        }
    }

    private async void ToggleChannel(string channelName)
    {
        var client = _connectionManager?.OperatorClient;
        if (client == null) return;

        var channel = _lastChannels.FirstOrDefault(c => c.Name == channelName);
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

    private void CopyDiagnostic(string label, Func<GatewayCommandCenterState, string> format)
    {
        try
        {
            CopyTextToClipboard(format(BuildCommandCenterState()));
            Logger.Info($"Copied {label} from deep link");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to copy {label} from deep link: {ex.Message}");
        }
    }

    private void CopySupportContext() =>
        CopyDiagnostic("support context", CommandCenterTextHelper.BuildSupportContext);

    private void CopyDebugBundle() =>
        CopyDiagnostic("debug bundle", CommandCenterTextHelper.BuildDebugBundle);

    private void CopyBrowserSetupGuidance() =>
        CopyDiagnostic("browser setup guidance", CommandCenterTextHelper.BuildBrowserSetupGuidance);

    private void CopyPortDiagnostics() =>
        CopyDiagnostic("port diagnostics", s => CommandCenterTextHelper.BuildPortDiagnosticsSummary(s.PortDiagnostics));

    private void CopyCapabilityDiagnostics() =>
        CopyDiagnostic("capability diagnostics", CommandCenterTextHelper.BuildCapabilityDiagnosticsSummary);

    private void CopyNodeInventory() =>
        CopyDiagnostic("node inventory", s => CommandCenterTextHelper.BuildNodeInventorySummary(s.Nodes));

    private void CopyChannelSummary() =>
        CopyDiagnostic("channel summary", s => CommandCenterTextHelper.BuildChannelSummaryText(s.Channels));

    private void CopyActivitySummary() =>
        CopyDiagnostic("activity summary", s => CommandCenterTextHelper.BuildActivitySummary(s.RecentActivity));

    private void CopyExtensibilitySummary() =>
        CopyDiagnostic("extensibility summary", s => CommandCenterTextHelper.BuildExtensibilitySummary(s.Channels));

    private void OnGlobalHotkeyPressed(object? sender, EventArgs e)
    {
        if (_dispatcherQueue == null)
        {
            Logger.Warn("Hotkey pressed but DispatcherQueue is null");
            return;
        }

        var enqueued = _dispatcherQueue.TryEnqueue(() => ShowQuickSend());
        if (!enqueued)
        {
            Logger.Warn("Hotkey pressed but failed to enqueue QuickSend on UI thread");
        }
    }

    private void OnVoiceHotkeyPressed(object? sender, EventArgs e)
    {
        if (_dispatcherQueue == null) return;
        _dispatcherQueue.TryEnqueue(() => ShowVoiceOverlay());
    }

    #endregion

    #region Updates

    private static UpdateCommandCenterInfo BuildInitialUpdateInfo() => new()
    {
        Status = "Not checked",
        CurrentVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown"
    };

    private async Task<bool> CheckForUpdatesAsync()
    {
        try
        {
#if DEBUG
            Logger.Info("Skipping update check in debug build");
            _lastUpdateInfo = new UpdateCommandCenterInfo
            {
                Status = "Skipped",
                CurrentVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
                CheckedAt = DateTime.UtcNow,
                Detail = "debug build"
            };
            return true;
#else
            Logger.Info("Checking for updates...");
            _lastUpdateInfo = new UpdateCommandCenterInfo
            {
                Status = "Checking",
                CurrentVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
                CheckedAt = DateTime.UtcNow
            };
            var updateFound = await AppUpdater.CheckForUpdatesAsync();

            if (!updateFound)
            {
                Logger.Info("No updates available");
                _lastUpdateInfo = new UpdateCommandCenterInfo
                {
                    Status = "Current",
                    CurrentVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
                    CheckedAt = DateTime.UtcNow,
                    Detail = "no updates available"
                };
                return true;
            }

            var release = AppUpdater.LatestRelease!;
            var changelog = AppUpdater.GetChangelog(true) ?? "No release notes available.";
            Logger.Info($"Update available: {release.TagName}");
            _lastUpdateInfo = new UpdateCommandCenterInfo
            {
                Status = "Available",
                CurrentVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
                LatestVersion = release.TagName,
                CheckedAt = DateTime.UtcNow,
                Detail = "prompted"
            };

            if (!string.IsNullOrWhiteSpace(_settings?.SkippedUpdateTag) &&
                string.Equals(_settings.SkippedUpdateTag, release.TagName, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info($"Skipping update prompt for remembered version {release.TagName}");
                _lastUpdateInfo.Detail = "skipped by user";
                return true;
            }

            var dialog = new UpdateDialog(release.TagName, changelog);
            var result = await dialog.ShowAsync();

            if (result == UpdateDialogResult.Download)
            {
                _lastUpdateInfo.Detail = "download requested";
                if (_settings != null)
                {
                    _settings.SkippedUpdateTag = string.Empty;
                    _settings.Save();
                }
                var installed = await DownloadAndInstallUpdateAsync();
                return !installed; // Don't launch if update succeeded
            }

            if (result == UpdateDialogResult.Skip && _settings != null)
            {
                _settings.SkippedUpdateTag = release.TagName ?? string.Empty;
                _settings.Save();
                _lastUpdateInfo.Detail = "skipped by user";
            }

            return true; // RemindLater or Skip - continue
#endif
        }
        catch (Exception ex)
        {
            Logger.Warn($"Update check failed: {ex.Message}");
            _lastUpdateInfo = new UpdateCommandCenterInfo
            {
                Status = "Failed",
                CurrentVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
                CheckedAt = DateTime.UtcNow,
                Detail = ex.Message
            };
            return true;
        }
    }

    private async Task CheckForUpdatesUserInitiatedAsync()
    {
        Logger.Info("Manual update check requested");
        var shouldContinue = await CheckForUpdatesAsync();
        UpdateStatusDetailWindow();
        if (!shouldContinue)
        {
            Exit();
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

            progressDialog?.Close();

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
            progressDialog?.Close();
            return false;
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
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In);
                    await pipe.WaitForConnectionAsync(token);
                    using var reader = new System.IO.StreamReader(pipe);
                    var uri = await reader.ReadLineAsync(token);
                    if (!string.IsNullOrEmpty(uri))
                    {
                        Logger.Info($"Received deep link via IPC: {uri}");
                        _dispatcherQueue?.TryEnqueue(() => HandleDeepLink(uri));
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.Info("Deep link server stopping (canceled)");
                    break; // Normal shutdown
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
            CopySupportContext = CopySupportContext,
            CopyDebugBundle = CopyDebugBundle,
            CopyBrowserSetupGuidance = CopyBrowserSetupGuidance,
            CopyPortDiagnostics = CopyPortDiagnostics,
            CopyCapabilityDiagnostics = CopyCapabilityDiagnostics,
            CopyNodeInventory = CopyNodeInventory,
            CopyChannelSummary = CopyChannelSummary,
            CopyActivitySummary = CopyActivitySummary,
            CopyExtensibilitySummary = CopyExtensibilitySummary,
            RestartSshTunnel = RestartSshTunnel,
            OpenChat = ShowWebChat,
            OpenCommandCenter = ShowStatusDetail,
            OpenTrayMenu = ShowTrayMenuPopup,
            OpenActivityStream = ShowActivityStream,
            OpenNotificationHistory = ShowNotificationHistory,
            OpenDashboard = OpenDashboard,
            OpenQuickSend = ShowQuickSend,
            OpenHub = (page) => ShowHub(page),
            OpenVoice = () => ShowVoiceOverlay(),
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

    private static void SendDeepLinkToRunningInstance(string uri)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(1000);
            using var writer = new System.IO.StreamWriter(pipe);
            writer.WriteLine(uri);
            writer.Flush();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to forward deep link: {ex.Message}");
        }
    }

    #endregion

    #region Toast Activation

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat args)
    {
        var arguments = ToastArguments.Parse(args.Argument);
        
        if (arguments.TryGetValue("action", out var action))
        {
            _dispatcherQueue?.TryEnqueue(() =>
            {
                switch (action)
                {
                    case "open_url" when arguments.TryGetValue("url", out var url):
                        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                        catch { }
                        break;
                    case "open_dashboard":
                        OpenDashboard();
                        break;
                    case "open_settings":
                        ShowSettings();
                        break;
                    case "open_chat":
                        ShowWebChat();
                        break;
                    case "open_activity":
                        ShowActivityStream();
                        break;
                    case "copy_pairing_command" when arguments.TryGetValue("command", out var command):
                        CopyTextToClipboard(command);
                        ShowToast(new ToastContentBuilder()
                            .AddText(LocalizationHelper.GetString("Toast_PairingCommandCopied"))
                            .AddText(command));
                        break;
                }
            });
        }
    }

    public static void CopyTextToClipboard(string text)
    {
        ClipboardHelper.CopyText(text);
    }

    #endregion

    #region Exit

    private void ExitApplication()
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
        SafeShutdownStep("gateway client", () =>
        {
            _connectionManager?.Dispose();
        });

        SafeShutdownStep("chat coordinator", () =>
        {
            _chatCoordinator?.Dispose();
            _chatCoordinator = null;
        });

        SafeShutdownStep("node service", () =>
        {
            _nodeService?.Dispose();
            _nodeService = null;
        });

        SafeShutdownStep("standalone voice service", () =>
        {
            _standaloneVoiceService?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _standaloneVoiceService = null;
        });

        SafeShutdownStep("ssh tunnel service", () =>
        {
            _sshTunnelService?.Dispose();
            _sshTunnelService = null;
        });

        // Close windows explicitly for deterministic shutdown tracing.
        SafeShutdownStep("chat window", () => { _chatWindow?.ForceClose(); _chatWindow = null; });
        SafeShutdownStep("tray menu window", () => CloseWindow(_trayMenuWindow));
        _trayMenuWindow = null;
        SafeShutdownStep("quick send dialog", () => CloseWindow(_quickSendDialog));
        _quickSendDialog = null;
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
                _currentStatus = ConnectionStatus.Error;
                _hubWindow?.UpdateStatus(_currentStatus);
                UpdateTrayIcon();
                return false;
            }

            try
            {
                _sshTunnelService ??= new SshTunnelService(new AppLogger());
                _sshTunnelService.EnsureStarted(_settings);
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
                _currentStatus = ConnectionStatus.Error;
                _hubWindow?.UpdateStatus(_currentStatus);
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

    private async void OnSshTunnelExited(object? sender, int exitCode)
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
                _sshTunnelService.EnsureStarted(_settings);
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

    private Microsoft.UI.Dispatching.DispatcherQueue? AppDispatcherQueue =>
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
}

internal class AppLogger : IOpenClawLogger
{
    public void Info(string message) => Logger.Info(message);
    public void Debug(string message) => Logger.Debug(message);
    public void Warn(string message) => Logger.Warn(message);
    public void Error(string message, Exception? ex = null) => 
        Logger.Error(ex != null ? $"{message}: {ex.Message}" : message);
}
