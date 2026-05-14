using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
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
using System.Globalization;
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
    private readonly GatewayDataStore _gatewayDataStore = new();
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
        _gatewayDataStore.SetLastUpdateInfo(_lastUpdateInfo);
        
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
            _globalHotkey.SettingsHotkeyPressed += OnSettingsHotkeyPressed;
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
            case "disconnect":
                _ = _connectionManager?.DisconnectAsync();
                _lastSessions = Array.Empty<SessionInfo>();
                _lastNodePairList = null;
                _lastDevicePairList = null;
                _lastModelsList = null;
                _agentEventsCache.Clear();
                _gatewayDataStore.ClearPairingAndAgentCaches();
                UpdateTrayIcon();
                _hubWindow?.UpdateStatus(ConnectionStatus.Disconnected);
                break;
            case "connection": ShowHub("connection"); break;
            case "permissions": ShowHub("permissions"); break;
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
        // Render the whole menu inside a single update batch so layout
        // measures only once instead of once-per-row. Pair with EndUpdate
        // in finally so an exception mid-build doesn't wedge layout.
        menu.BeginUpdate();
        try
        {
            BuildTrayMenuPopupCore(menu);
        }
        finally
        {
            menu.EndUpdate();
        }
    }

    private void BuildTrayMenuPopupCore(TrayMenuWindow menu)
    {
        // Stale closures from the previous build hold references to old
        // ToggleAction delegates; recreate the lookup each rebuild.
        _permToggleActions.Clear();

        var isConnected = _currentStatus == ConnectionStatus.Connected;
        var statusText = LocalizationHelper.GetConnectionStatusText(_currentStatus);

        // Cache theme brushes once per build so cells don't each do a
        // resource lookup. The previous implementation looked up
        // SystemFill/Text brushes per row, which contributed to the
        // visible right-click hitch.
        var resources = Application.Current.Resources;
        var successBrush = (Microsoft.UI.Xaml.Media.Brush)resources["SystemFillColorSuccessBrush"];
        var cautionBrush = (Microsoft.UI.Xaml.Media.Brush)resources["SystemFillColorCautionBrush"];
        var neutralBrush = (Microsoft.UI.Xaml.Media.Brush)resources["SystemFillColorNeutralBrush"];
        var criticalBrush = (Microsoft.UI.Xaml.Media.Brush)resources["SystemFillColorCriticalBrush"];
        var secondaryText = (Microsoft.UI.Xaml.Media.Brush)resources["TextFillColorSecondaryBrush"];
        var captionStyle = (Style)resources["CaptionTextBlockStyle"];
        var controlSecondaryFill = (Microsoft.UI.Xaml.Media.Brush)resources["ControlFillColorSecondaryBrush"];

        // ── Brand Header with Disconnect/Connect on the right ──
        var brandGrid = new Grid
        {
            Padding = new Thickness(14, 10, 14, 8),
            ColumnSpacing = 8
        };
        brandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        brandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        brandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var brandRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Microsoft.UI.Xaml.Controls.Image
                {
                    Source = new BitmapImage(new Uri("ms-appx:///Assets/Square44x44Logo.targetsize-48_altform-unplated.png")),
                    Width = 28,
                    Height = 28,
                    VerticalAlignment = VerticalAlignment.Center
                },
                new TextBlock
                {
                    Text = "OpenClaw",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    FontSize = 18,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsTextSelectionEnabled = false
                }
            }
        };
        Grid.SetColumn(brandRow, 0);
        brandGrid.Children.Add(brandRow);

        var brandBtn = new Button
        {
            Content = isConnected ? "Disconnect" : "Connect",
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(12, 4, 12, 4),
            MinHeight = 0,
            MinWidth = 0,
            FontSize = 12
        };
        AutomationProperties.SetName(brandBtn, isConnected ? "Disconnect from gateway" : "Connect to gateway");
        ToolTipService.SetToolTip(brandBtn, isConnected ? "Disconnect from gateway" : "Connect to gateway");
        brandBtn.Click += (s, ev) =>
        {
            OnTrayMenuItemClicked(this, isConnected ? "disconnect" : "reconnect");
            _trayMenuWindow?.HideCascade();
        };
        Grid.SetColumn(brandBtn, 2);
        brandGrid.Children.Add(brandBtn);

        menu.AddCustomElement(brandGrid);

        // ── Pairing approval pending (high-priority action above Gateway) ──
        var nodePendingCount = _lastNodePairList?.Pending.Count ?? 0;
        var devicePendingCount = _lastDevicePairList?.Pending.Count ?? 0;
        if (nodePendingCount + devicePendingCount > 0)
        {
            var total = nodePendingCount + devicePendingCount;
            menu.AddMenuItem(
                $"Pairing approval pending ({total})",
                FluentIconCatalog.Build(FluentIconCatalog.Approvals),
                "hub");
        }

        // ── Gateway Section ──
        // (device-card format)
        var gwOuter = new StackPanel
        {
            Padding = new Thickness(12, 8, 12, 8),
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // ── Line 1: dot + "Gateway" + Local chip ──
        var gwLine1 = new Grid { ColumnSpacing = 6 };
        gwLine1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        gwLine1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        gwLine1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var gwNameRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        gwNameRow.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = isConnected ? successBrush
                : _currentStatus == ConnectionStatus.Connecting ? cautionBrush
                : neutralBrush
        });
        gwNameRow.Children.Add(new TextBlock
        {
            Text = "Gateway",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        });
        Grid.SetColumn(gwNameRow, 0);
        gwLine1.Children.Add(gwNameRow);

        // Right-side: optional chip on the header line (Disconnect lives in brand header)
        string? chipText = null;
        var gwUrl = _settings?.GetEffectiveGatewayUrl();
        Uri? gwUri = null;
        if (!string.IsNullOrEmpty(gwUrl)) Uri.TryCreate(gwUrl, UriKind.Absolute, out gwUri);
        if (isConnected)
        {
            if (gwUri != null && (gwUri.Host == "localhost" || gwUri.Host == "127.0.0.1" || gwUri.Host == "::1"))
                chipText = "Local";
            else if (_lastGatewaySelf != null && !string.IsNullOrEmpty(_lastGatewaySelf.ServerVersion))
                chipText = $"v{_lastGatewaySelf.ServerVersion}";
        }
        if (chipText != null)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Background = controlSecondaryFill,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = new TextBlock
                {
                    Text = chipText,
                    FontSize = 10,
                    Foreground = secondaryText,
                    IsTextSelectionEnabled = false
                }
            };
            Grid.SetColumn(chip, 2);
            gwLine1.Children.Add(chip);
        }
        gwOuter.Children.Add(gwLine1);

        // ── Line 2: secondary details ──
        var gwLine2Parts = new List<string>();
        if (gwUri != null) gwLine2Parts.Add($"{gwUri.Host}:{gwUri.Port}");
        gwLine2Parts.Add(statusText.ToLowerInvariant());
        if (isConnected && _lastPresence != null && _lastPresence.Length > 0)
            gwLine2Parts.Add($"{_lastPresence.Length} client{(_lastPresence.Length != 1 ? "s" : "")}");
        if (_settings?.EnableNodeMode == true && _nodeService != null)
        {
            if (_nodeService.IsPaired) gwLine2Parts.Add("node paired");
            else if (_nodeService.IsPendingApproval) gwLine2Parts.Add("node pairing pending");
            else if (_nodeService.IsConnected) gwLine2Parts.Add("node connected");
        }
        gwOuter.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", gwLine2Parts),
            Style = captionStyle,
            Foreground = secondaryText,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = false
        });

        // Auth failure inline (line 3, critical brush) ── preserved from prior layout
        if (!string.IsNullOrEmpty(_authFailureMessage))
        {
            gwOuter.Children.Add(new TextBlock
            {
                Text = _authFailureMessage,
                Style = captionStyle,
                Foreground = criticalBrush,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 240,
                IsTextSelectionEnabled = false
            });
        }

        // Tap the gateway block to open connection settings (button has its own click handler)
        gwOuter.Padding = new Thickness(14, 6, 14, 8);

        AutomationProperties.SetName(gwOuter,
            $"Gateway {statusText}. Activate to open connection settings.");

        // Gateway hover flyout — richer connection details
        var gwFlyoutItems = BuildGatewayFlyoutItems(
            isConnected, statusText, gwUri, _lastPresence, _lastGatewaySelf,
            _lastNodePairList, _lastDevicePairList, _authFailureMessage,
            captionStyle, secondaryText, successBrush, neutralBrush, criticalBrush);
        menu.AddFlyoutCustomItem(gwOuter, gwFlyoutItems, action: "connection");

        // ── Connected Devices (moved above Sessions) ──
        // Devices flow directly after the Gateway block without a divider
        // or section header — they share the gateway visual format.
        var connectedNodes = _lastNodes.Where(n => n.IsOnline).ToArray();
        if (connectedNodes.Length > 0)
        {
            foreach (var node in connectedNodes.Take(5))
            {
                var card = BuildDeviceCard(node, successBrush, neutralBrush, secondaryText);
                var flyoutItems = BuildDeviceFlyoutItems(node);
                menu.AddFlyoutCustomItem(card, flyoutItems, action: "nodes");
            }
        }

        // ── Sessions (now below Devices) ──
        if (_lastSessions.Length > 0)
        {
            menu.AddSeparator();

            var sessionCount = _lastSessions.Length;
            var activeCount = _lastSessions.Count(s => string.Equals(s.Status, "active", StringComparison.OrdinalIgnoreCase));
            var totalTokensAll = _lastSessions.Sum(s => s.InputTokens + s.OutputTokens);
            var sessionSummaryRight = $"{activeCount} active · {FormatTokenCount(totalTokensAll)} tokens";

            // Single collapsed entry whose hover flyout reveals the session list.
            var sessionsRow = BuildSessionsListRow(sessionCount, activeCount, totalTokensAll, secondaryText);
            var sessionsFlyout = BuildSessionsListFlyoutItems(secondaryText, successBrush, cautionBrush, neutralBrush);
            menu.AddFlyoutCustomItem(sessionsRow, sessionsFlyout, action: "sessions");
        }

        // ── Usage (no divider — flows directly under Sessions) ──
        {
            var usageRow = BuildUsageRow(secondaryText);
            var usageFlyout = BuildUsageFlyoutItems(secondaryText);
            menu.AddFlyoutCustomItem(usageRow, usageFlyout, action: "usage");
        }

        // ── Actions ──
        menu.AddSeparator();
        if (_settings != null)
        {
            menu.AddFlyoutMenuItem(
                "Permissions",
                FluentIconCatalog.Build(FluentIconCatalog.Permissions),
                BuildPermissionsFlyoutItems(_settings),
                action: "permissions");
        }
        menu.AddMenuItem("Dashboard", FluentIconCatalog.Build(FluentIconCatalog.Dashboard), "dashboard");
        menu.AddMenuItem("Chat", FluentIconCatalog.Build(FluentIconCatalog.Chat), "openchat");
        menu.AddMenuItem("Canvas", FluentIconCatalog.Build(FluentIconCatalog.CanvasAct), "canvas");
        menu.AddMenuItem("Voice", FluentIconCatalog.Build(FluentIconCatalog.VoiceAct), "voice");
        menu.AddMenuItem(
            LocalizationHelper.GetString("Menu_QuickSend"),
            FluentIconCatalog.Build(FluentIconCatalog.QuickSend),
            "quicksend");

        // Setup Guide / Reconfigure entry — label flips based on whether prior
        // configuration exists; routes to the existing "setup" action handler.
        var setupMenuLabel = _settings != null
            && new OpenClawTray.Onboarding.Services.OnboardingExistingConfigGuard(_settings, IdentityDataPath)
                .HasExistingConfiguration()
            ? LocalizationHelper.GetString("Menu_Reconfigure")
            : LocalizationHelper.GetString("Menu_SetupGuide");
        menu.AddMenuItem(setupMenuLabel, FluentIconCatalog.Build(FluentIconCatalog.Setup), "setup");

        // ── Footer ──
        menu.AddSeparator();
        menu.AddMenuItemWithHint(
            "Companion Settings...",
            FluentIconCatalog.Build(FluentIconCatalog.Settings),
            "companion",
            "Ctrl+Alt+;");
        menu.AddMenuItem("About", FluentIconCatalog.Build(FluentIconCatalog.About), "about");
        menu.AddMenuItem("Close", FluentIconCatalog.Build(FluentIconCatalog.Exit), "exit");
    }

    /// <summary>
    /// Flyout items for the local-node Permissions row: one check-toggle per
    /// capability flag in <see cref="SettingsData"/>. Toggling saves the
    /// setting and reconnects so the gateway picks up the new capability set.
    /// </summary>
    private List<TrayMenuFlyoutItem> BuildPermissionsFlyoutItems(SettingsManager settings)
    {
        var items = new List<TrayMenuFlyoutItem>
        {
            new() { Text = "Permissions", IsHeader = true },
        };

        AddPermToggle(items, "Windows node", FluentIconCatalog.System,
            () => settings.EnableNodeMode, v => settings.EnableNodeMode = v);
        AddPermToggle(items, "Browser control", FluentIconCatalog.Browser,
            () => settings.NodeBrowserProxyEnabled, v => settings.NodeBrowserProxyEnabled = v);
        AddPermToggle(items, "Camera", FluentIconCatalog.Camera,
            () => settings.NodeCameraEnabled, v => settings.NodeCameraEnabled = v);
        AddPermToggle(items, "Canvas", FluentIconCatalog.Canvas,
            () => settings.NodeCanvasEnabled, v => settings.NodeCanvasEnabled = v);
        AddPermToggle(items, "Screen capture", FluentIconCatalog.Screen,
            () => settings.NodeScreenEnabled, v => settings.NodeScreenEnabled = v);
        AddPermToggle(items, "Location", FluentIconCatalog.Location,
            () => settings.NodeLocationEnabled, v => settings.NodeLocationEnabled = v);
        AddPermToggle(items, "Voice (TTS)", FluentIconCatalog.Voice,
            () => settings.NodeTtsEnabled, v => settings.NodeTtsEnabled = v);
        AddPermToggle(items, "Speech-to-text (STT)", FluentIconCatalog.Speech,
            () => settings.NodeSttEnabled, v => settings.NodeSttEnabled = v);

        return items;
    }

    private void AddPermToggle(List<TrayMenuFlyoutItem> items, string label, string iconGlyph, Func<bool> get, Action<bool> set)
    {
        var on = get();
        var actionId = $"perm-toggle|{label}";
        items.Add(new TrayMenuFlyoutItem
        {
            Text = label,
            Icon = iconGlyph,
            Action = actionId,
            IsToggle = true,
            IsOn = on,
        });
        _permToggleActions[actionId] = () =>
        {
            set(!get());
            _settings?.Save();
            _ = _connectionManager?.ReconnectAsync();
            if (_trayMenuWindow != null && _trayMenuWindow.IsShown)
            {
                _trayMenuWindow.ClearItems();
                BuildTrayMenuPopup(_trayMenuWindow);
            }
        };
    }

    private readonly Dictionary<string, Action> _permToggleActions = new(StringComparer.Ordinal);


    private static string FormatTokenCount(long n)
    {
        if (n >= 1_000_000) return $"{n / 1_000_000.0:F1}M";
        if (n >= 1_000) return $"{n / 1_000.0:F1}K";
        return n.ToString();
    }

    /// <summary>
    /// Mini progress bar built from Borders inside a Grid (two Star columns:
    /// pct and 100-pct). Avoids the default WinUI ProgressBar template which
    /// renders 0-height inside dynamic-width flyout layouts.
    /// </summary>
    private static FrameworkElement BuildMiniBar(double percent)
    {
        var p = Math.Min(100.0, Math.Max(0.0, percent));
        var resources = Application.Current.Resources;
        var accent = (Microsoft.UI.Xaml.Media.Brush)resources["AccentFillColorDefaultBrush"];
        var track = (Microsoft.UI.Xaml.Media.Brush)resources["ControlStrongFillColorDefaultBrush"];

        var grid = new Grid
        {
            Height = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 80
        };
        // 1e-6 guard so an empty (0%) bar still renders the track column at
        // full width; a 0/0 star pair would collapse.
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, p), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 100.0 - p), GridUnitType.Star) });

        var filled = new Microsoft.UI.Xaml.Controls.Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(3, 0, 0, 3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        if (p <= 0)
        {
            filled.Opacity = 0; // hide accent stub at exactly 0% but keep slot
        }
        Grid.SetColumn(filled, 0);
        grid.Children.Add(filled);

        var rest = new Microsoft.UI.Xaml.Controls.Border
        {
            Background = track,
            CornerRadius = p >= 100 ? new CornerRadius(0, 3, 3, 0) : new CornerRadius(0, 3, 3, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        if (p <= 0)
        {
            rest.CornerRadius = new CornerRadius(3);
        }
        Grid.SetColumn(rest, 1);
        grid.Children.Add(rest);

        return grid;
    }

    // ── Rich card builder helpers for tray menu ──

    private static readonly FrozenDictionary<string, string> CapabilityIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["screen"] = FluentIconCatalog.Screen,
        ["camera"] = FluentIconCatalog.Camera,
        ["browser"] = FluentIconCatalog.Browser,
        ["clipboard"] = "\uE77F",     // PasteAsText
        ["tts"] = FluentIconCatalog.Voice,
        ["stt"] = FluentIconCatalog.Speech,
        ["location"] = FluentIconCatalog.Location,
        ["canvas"] = FluentIconCatalog.Canvas,
        ["system"] = FluentIconCatalog.System,
        ["device"] = FluentIconCatalog.Devices,
        ["app"] = "\uECAA",           // AppIconDefault
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

    private static string FormatRelative(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        if (age.TotalSeconds < 60) return "just now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    // ── Sessions: collapsed entry + flyout list ─────────────────────────

    private UIElement BuildSessionsListRow(int total, int active, long totalTokens, Microsoft.UI.Xaml.Media.Brush secondaryText)
    {
        // Card row: [icon] Sessions    (N active · X tokens)
        var resources = Application.Current.Resources;
        var captionStyle = (Style)resources["CaptionTextBlockStyle"];

        var grid = new Grid
        {
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ColumnSpacing = 8
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "Sessions",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };
        Grid.SetColumn(title, 0);
        grid.Children.Add(title);

        var summary = new TextBlock
        {
            Text = $"{active} active · {FormatTokenCount(totalTokens)} tokens",
            Style = captionStyle,
            FontSize = 11,
            Foreground = secondaryText,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };
        Grid.SetColumn(summary, 1);
        grid.Children.Add(summary);

        return grid;
    }

    private static List<TrayMenuFlyoutItem> BuildGatewayFlyoutItems(
        bool isConnected,
        string statusText,
        Uri? gwUri,
        PresenceEntry[]? presence,
        GatewaySelfInfo? self,
        PairingListInfo? nodePair,
        DevicePairingListInfo? devicePair,
        string? authFailure,
        Style captionStyle,
        Microsoft.UI.Xaml.Media.Brush secondaryText,
        Microsoft.UI.Xaml.Media.Brush successBrush,
        Microsoft.UI.Xaml.Media.Brush neutralBrush,
        Microsoft.UI.Xaml.Media.Brush criticalBrush)
    {
        var items = new List<TrayMenuFlyoutItem>
        {
            new() { Text = "Gateway", IsHeader = true }
        };

        // Status card: ● Online/Offline · localhost:7070
        var statusCard = new StackPanel
        {
            Padding = new Thickness(12, 2, 12, 6),
            Spacing = 2,
            MinWidth = 280
        };
        var statusLine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        statusLine.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = isConnected ? successBrush : neutralBrush
        });
        var statusParts = new List<string> { statusText };
        if (gwUri != null) statusParts.Add($"{gwUri.Host}:{gwUri.Port}");
        statusLine.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", statusParts),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        });
        statusCard.Children.Add(statusLine);

        if (gwUri != null)
        {
            statusCard.Children.Add(new TextBlock
            {
                Text = gwUri.ToString(),
                Style = captionStyle,
                FontSize = 11,
                Foreground = secondaryText,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsTextSelectionEnabled = false
            });
        }
        items.Add(new() { CustomContent = statusCard });

        if (!string.IsNullOrEmpty(authFailure))
        {
            var authRow = new StackPanel { Padding = new Thickness(12, 2, 12, 4) };
            authRow.Children.Add(new TextBlock
            {
                Text = authFailure,
                Style = captionStyle, FontSize = 11,
                Foreground = criticalBrush,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 260,
                IsTextSelectionEnabled = false
            });
            items.Add(new() { CustomContent = authRow });
        }

        // Server details
        if (self != null && self.HasAnyDetails)
        {
            items.Add(new() { Text = "Server", IsHeader = true });
            if (!string.IsNullOrEmpty(self.ServerVersion))
                items.Add(BuildKvRow("Version", $"v{self.ServerVersion}", secondaryText, captionStyle));
            if (!string.IsNullOrEmpty(self.AuthMode))
                items.Add(BuildKvRow("Auth", self.AuthMode!, secondaryText, captionStyle));
            if (self.Protocol.HasValue)
                items.Add(BuildKvRow("Protocol", $"v{self.Protocol}", secondaryText, captionStyle));
            if (self.UptimeMs.HasValue)
                items.Add(BuildKvRow("Uptime", FormatUptime(self.UptimeMs.Value), secondaryText, captionStyle));
            if (!string.IsNullOrEmpty(self.ConnectionId))
                items.Add(BuildKvRow("Conn ID", self.ConnectionId!, secondaryText, captionStyle));
        }

        // Presence
        if (isConnected && presence != null && presence.Length > 0)
        {
            items.Add(new() { Text = $"Clients ({presence.Length})", IsHeader = true });
            foreach (var p in presence.Take(6))
            {
                var name = !string.IsNullOrEmpty(p.Host) ? p.Host! : (p.Platform ?? "client");
                var detailParts = new List<string>();
                if (!string.IsNullOrEmpty(p.Platform)) detailParts.Add(p.Platform!);
                if (!string.IsNullOrEmpty(p.Version)) detailParts.Add($"v{p.Version}");
                if (!string.IsNullOrEmpty(p.Mode)) detailParts.Add(p.Mode!);
                items.Add(BuildKvRow(name!, string.Join(" · ", detailParts), secondaryText, captionStyle));
            }
        }

        // Pending pairings (if any) — quick summary line
        var nodePending = nodePair?.Pending.Count ?? 0;
        var devicePending = devicePair?.Pending.Count ?? 0;
        if (nodePending + devicePending > 0)
        {
            items.Add(new() { Text = "Pending approval", IsHeader = true });
            if (nodePending > 0)
                items.Add(BuildKvRow("Nodes", nodePending.ToString(), secondaryText, captionStyle));
            if (devicePending > 0)
                items.Add(BuildKvRow("Devices", devicePending.ToString(), secondaryText, captionStyle));
        }

        return items;
    }

    private static TrayMenuFlyoutItem BuildKvRow(string key, string value, Microsoft.UI.Xaml.Media.Brush secondaryText, Style captionStyle)
    {
        var grid = new Grid
        {
            Padding = new Thickness(12, 2, 12, 2),
            ColumnSpacing = 12,
            MinWidth = 260
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var k = new TextBlock
        {
            Text = key,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = secondaryText,
            IsTextSelectionEnabled = false
        };
        Grid.SetColumn(k, 0);
        grid.Children.Add(k);

        var v = new TextBlock
        {
            Text = value,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = false
        };
        Grid.SetColumn(v, 1);
        grid.Children.Add(v);

        return new TrayMenuFlyoutItem { CustomContent = grid };
    }

    private static string FormatUptime(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m";
        return $"{(int)ts.TotalSeconds}s";
    }

    private List<TrayMenuFlyoutItem> BuildSessionsListFlyoutItems(
        Microsoft.UI.Xaml.Media.Brush secondaryText,
        Microsoft.UI.Xaml.Media.Brush successBrush,
        Microsoft.UI.Xaml.Media.Brush cautionBrush,
        Microsoft.UI.Xaml.Media.Brush neutralBrush)
    {
        var items = new List<TrayMenuFlyoutItem>
        {
            new() { Text = $"Sessions ({_lastSessions.Length})", IsHeader = true }
        };

        if (_lastSessions.Length == 0)
        {
            items.Add(new() { Text = "No active sessions" });
            return items;
        }

        foreach (var session in _lastSessions.Take(8))
        {
            var card = BuildSessionListCard(session, secondaryText, successBrush, cautionBrush, neutralBrush);
            items.Add(new() { CustomContent = card });
        }

        return items;
    }

    private static UIElement BuildSessionListCard(
        SessionInfo session,
        Microsoft.UI.Xaml.Media.Brush secondaryText,
        Microsoft.UI.Xaml.Media.Brush successBrush,
        Microsoft.UI.Xaml.Media.Brush cautionBrush,
        Microsoft.UI.Xaml.Media.Brush neutralBrush)
    {
        // 2-row card:
        //   Row 0: ● {name}                              {age}
        //   Row 1: {model}              [████░░░░] {used}/{ctx} ({pct}%)
        var isActive = string.Equals(session.Status, "active", StringComparison.OrdinalIgnoreCase);
        var isIdle = string.Equals(session.Status, "idle", StringComparison.OrdinalIgnoreCase);
        var usedTokens = session.InputTokens + session.OutputTokens;
        var contextTokens = session.ContextTokens > 0 ? session.ContextTokens : 200_000;
        var pct = usedTokens > 0 ? Math.Min(100.0, (double)usedTokens / contextTokens * 100.0) : 0.0;

        var resources = Application.Current.Resources;
        var captionStyle = (Style)resources["CaptionTextBlockStyle"];

        var outer = new StackPanel
        {
            Padding = new Thickness(12, 6, 12, 8),
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 260
        };

        // Row 0: dot + name + age
        var line1 = new Grid { ColumnSpacing = 6 };
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        nameRow.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = isActive ? successBrush : isIdle ? cautionBrush : neutralBrush
        });
        nameRow.Children.Add(new TextBlock
        {
            Text = session.DisplayName ?? session.Key,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        });
        Grid.SetColumn(nameRow, 0);
        line1.Children.Add(nameRow);

        if (session.UpdatedAt.HasValue)
        {
            var age = new TextBlock
            {
                Text = FormatRelative(session.UpdatedAt.Value),
                Style = captionStyle, FontSize = 11, Foreground = secondaryText,
                VerticalAlignment = VerticalAlignment.Center,
                IsTextSelectionEnabled = false
            };
            Grid.SetColumn(age, 1);
            line1.Children.Add(age);
        }
        outer.Children.Add(line1);

        // Row 1: model + progress + ratio
        var line2 = new Grid { ColumnSpacing = 8 };
        line2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var modelText = !string.IsNullOrEmpty(session.Model) ? session.Model! : "unknown";
        var model = new TextBlock
        {
            Text = modelText,
            Style = captionStyle, FontSize = 11, Foreground = secondaryText,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 100,
            IsTextSelectionEnabled = false
        };
        Grid.SetColumn(model, 0);
        line2.Children.Add(model);

        var bar = BuildMiniBar(pct);
        Grid.SetColumn(bar, 1);
        line2.Children.Add(bar);

        var ratio = new TextBlock
        {
            Text = $"{FormatTokenCount(usedTokens)}/{FormatTokenCount(contextTokens)} ({(int)pct}%)",
            Style = captionStyle, FontSize = 11, Foreground = secondaryText,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };
        Grid.SetColumn(ratio, 2);
        line2.Children.Add(ratio);

        outer.Children.Add(line2);
        return outer;
    }

    // ── Usage: collapsed entry + flyout body ────────────────────────────

    private UIElement BuildUsageRow(Microsoft.UI.Xaml.Media.Brush secondaryText)
    {
        var resources = Application.Current.Resources;
        var captionStyle = (Style)resources["CaptionTextBlockStyle"];

        var grid = new Grid
        {
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ColumnSpacing = 8
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "Usage",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };
        Grid.SetColumn(title, 0);
        grid.Children.Add(title);

        // Right-side summary: $X.XX · Y tokens (always include both when any data present)
        var totalTokens = _lastUsage?.TotalTokens
            ?? _lastSessions.Sum(s => s.InputTokens + s.OutputTokens);
        var cost = _lastUsage?.CostUsd
            ?? _lastUsageCost?.Totals.TotalCost
            ?? 0.0;
        string summaryText;
        if (cost <= 0 && totalTokens <= 0)
        {
            summaryText = "no data";
        }
        else
        {
            // Always show both, formatted as "$X.XX · Y tokens" even when one is 0.
            var costStr = "$" + cost.ToString("F2", CultureInfo.InvariantCulture);
            var tokStr = $"{FormatTokenCount(totalTokens)} tokens";
            summaryText = $"{costStr} · {tokStr}";
        }

        var summary = new TextBlock
        {
            Text = summaryText,
            Style = captionStyle, FontSize = 11,
            Foreground = secondaryText,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };
        Grid.SetColumn(summary, 1);
        grid.Children.Add(summary);

        return grid;
    }

    private List<TrayMenuFlyoutItem> BuildUsageFlyoutItems(Microsoft.UI.Xaml.Media.Brush secondaryText)
    {
        var resources = Application.Current.Resources;
        var captionStyle = (Style)resources["CaptionTextBlockStyle"];
        var subhead = (Style)resources["BodyStrongTextBlockStyle"];

        var items = new List<TrayMenuFlyoutItem>
        {
            new() { Text = "Usage", IsHeader = true }
        };

        var totalTokens = _lastUsage?.TotalTokens
            ?? _lastSessions.Sum(s => s.InputTokens + s.OutputTokens);
        var inputTokens = _lastUsage?.InputTokens
            ?? _lastSessions.Sum(s => s.InputTokens);
        var outputTokens = _lastUsage?.OutputTokens
            ?? _lastSessions.Sum(s => s.OutputTokens);
        var cost = _lastUsage?.CostUsd
            ?? _lastUsageCost?.Totals.TotalCost
            ?? 0.0;
        var requests = _lastUsage?.RequestCount ?? 0;

        // Totals card
        if (totalTokens > 0 || cost > 0)
        {
            var totalsCard = new StackPanel
            {
                Padding = new Thickness(12, 6, 12, 8),
                Spacing = 2,
                MinWidth = 260
            };
            if (cost > 0)
            {
                totalsCard.Children.Add(new TextBlock
                {
                    Text = "$" + cost.ToString("F2", CultureInfo.InvariantCulture),
                    FontSize = 20,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    IsTextSelectionEnabled = false
                });
            }
            var detail = new List<string>();
            if (totalTokens > 0) detail.Add($"{FormatTokenCount(totalTokens)} tokens");
            if (inputTokens > 0 || outputTokens > 0)
                detail.Add($"in {FormatTokenCount(inputTokens)} · out {FormatTokenCount(outputTokens)}");
            if (requests > 0) detail.Add($"{requests} requests");
            if (detail.Count > 0)
            {
                totalsCard.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", detail),
                    Style = captionStyle, FontSize = 11,
                    Foreground = secondaryText,
                    IsTextSelectionEnabled = false
                });
            }
            items.Add(new() { CustomContent = totalsCard });
        }
        else
        {
            items.Add(new() { Text = "No usage data yet" });
        }

        // Providers section
        var providers = _lastUsageStatus?.Providers;
        if (providers != null && providers.Count > 0)
        {
            items.Add(new() { Text = "Providers", IsHeader = true });
            foreach (var prov in providers)
            {
                var provCard = new StackPanel
                {
                    Padding = new Thickness(12, 4, 12, 6),
                    Spacing = 3,
                    MinWidth = 260
                };
                var header = !string.IsNullOrEmpty(prov.DisplayName) ? prov.DisplayName : prov.Provider;
                if (!string.IsNullOrEmpty(prov.Plan)) header += $" · {prov.Plan}";
                provCard.Children.Add(new TextBlock
                {
                    Text = header,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    IsTextSelectionEnabled = false
                });

                if (!string.IsNullOrEmpty(prov.Error))
                {
                    provCard.Children.Add(new TextBlock
                    {
                        Text = prov.Error!,
                        Style = captionStyle, FontSize = 11,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)resources["SystemFillColorCriticalBrush"],
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = false
                    });
                }

                foreach (var win in prov.Windows)
                {
                    var winRow = new Grid { ColumnSpacing = 8 };
                    winRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                    winRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    winRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var label = new TextBlock
                    {
                        Text = win.Label,
                        Style = captionStyle, FontSize = 11,
                        Foreground = secondaryText,
                        VerticalAlignment = VerticalAlignment.Center,
                        IsTextSelectionEnabled = false
                    };
                    Grid.SetColumn(label, 0);
                    winRow.Children.Add(label);

                    var bar = BuildMiniBar(Math.Min(100.0, Math.Max(0.0, win.UsedPercent)));
                    Grid.SetColumn(bar, 1);
                    winRow.Children.Add(bar);

                    var pctLbl = new TextBlock
                    {
                        Text = $"{(int)win.UsedPercent}%",
                        Style = captionStyle, FontSize = 11,
                        Foreground = secondaryText,
                        VerticalAlignment = VerticalAlignment.Center,
                        IsTextSelectionEnabled = false
                    };
                    Grid.SetColumn(pctLbl, 2);
                    winRow.Children.Add(pctLbl);

                    provCard.Children.Add(winRow);
                }

                items.Add(new() { CustomContent = provCard });
            }
        }

        // By Model section — aggregate from sessions
        var byModel = _lastSessions
            .Where(s => !string.IsNullOrEmpty(s.Model))
            .GroupBy(s => s.Model!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Model = g.Key, Tokens = g.Sum(s => s.InputTokens + s.OutputTokens) })
            .Where(x => x.Tokens > 0)
            .OrderByDescending(x => x.Tokens)
            .Take(3)
            .ToList();
        if (byModel.Count > 0)
        {
            items.Add(new() { Text = "By Model", IsHeader = true });
            foreach (var m in byModel)
            {
                var row = new Grid
                {
                    Padding = new Thickness(12, 2, 12, 2),
                    ColumnSpacing = 8,
                    MinWidth = 260
                };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = new TextBlock
                {
                    Text = m.Model,
                    Style = captionStyle, FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsTextSelectionEnabled = false
                };
                Grid.SetColumn(name, 0);
                row.Children.Add(name);
                var amt = new TextBlock
                {
                    Text = $"{FormatTokenCount(m.Tokens)} tokens",
                    Style = captionStyle, FontSize = 11,
                    Foreground = secondaryText,
                    IsTextSelectionEnabled = false
                };
                Grid.SetColumn(amt, 1);
                row.Children.Add(amt);
                items.Add(new() { CustomContent = row });
            }
        }

        return items;
    }

    private static UIElement BuildDeviceCard(
        GatewayNodeInfo node,
        Microsoft.UI.Xaml.Media.Brush successBrush,
        Microsoft.UI.Xaml.Media.Brush neutralBrush,
        Microsoft.UI.Xaml.Media.Brush secondaryText)
    {
        // VarB: verbose two-line device card.
        //   Line 1: ● {DisplayName}                [os-pill]  ›
        //   Line 2: Online · {Role} · Windows {OsVersion} · app {Version}
        var nodeName = !string.IsNullOrWhiteSpace(node.DisplayName) ? node.DisplayName : node.ShortId;

        var resources = Application.Current.Resources;
        var captionStyle = (Style)resources["CaptionTextBlockStyle"];
        var controlSecondaryFill = (Microsoft.UI.Xaml.Media.Brush)resources["ControlFillColorSecondaryBrush"];

        // Build line-2 tokens: drop empties, render only if at least one survives.
        var line2Tokens = new List<string>
        {
            node.IsOnline ? "Online" : "Offline"
        };
        if (!string.IsNullOrWhiteSpace(node.Mode)) line2Tokens.Add(node.Mode!);
        // No dedicated OsVersion field on GatewayNodeInfo; surface platform/family
        // when available as the OS hint. Falls under the "drop unknown tokens" rule.
        if (!string.IsNullOrWhiteSpace(node.DeviceFamily)) line2Tokens.Add(node.DeviceFamily!);
        if (!string.IsNullOrWhiteSpace(node.Version)) line2Tokens.Add($"app {node.Version}");

        var outer = new StackPanel
        {
            Padding = new Thickness(12, 8, 12, 8),
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // ── Line 1 ──
        var line1 = new Grid { ColumnSpacing = 6 };
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });            // dot + name stack
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // spacer
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });            // os chip
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });            // chevron

        var nameRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        nameRow.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = node.IsOnline ? successBrush : neutralBrush
        });
        nameRow.Children.Add(new TextBlock
        {
            Text = nodeName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        });
        Grid.SetColumn(nameRow, 0);
        line1.Children.Add(nameRow);

        if (!string.IsNullOrWhiteSpace(node.Platform))
        {
            var osChip = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Background = controlSecondaryFill,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = node.Platform!.ToLowerInvariant(),
                    FontSize = 10,
                    Foreground = secondaryText,
                    IsTextSelectionEnabled = false
                }
            };
            Grid.SetColumn(osChip, 2);
            line1.Children.Add(osChip);
        }

        var chevron = new TextBlock
        {
            Text = "›",
            FontSize = 14,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        };
        Grid.SetColumn(chevron, 3);
        line1.Children.Add(chevron);
        outer.Children.Add(line1);

        // ── Line 2 (verbose details) ──
        // Always render when at least one non-name token exists; otherwise the
        // card collapses to single-line (just line 1).
        if (line2Tokens.Count > 0)
        {
            outer.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", line2Tokens),
                Style = captionStyle,
                FontSize = 11,
                Foreground = secondaryText,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsTextSelectionEnabled = false
            });
        }

        return outer;
    }

    private static List<TrayMenuFlyoutItem> BuildDeviceFlyoutItems(GatewayNodeInfo node)
    {
        var nodeName = !string.IsNullOrWhiteSpace(node.DisplayName) ? node.DisplayName : node.ShortId;
        var items = new List<TrayMenuFlyoutItem>
        {
            new() { Text = nodeName, IsHeader = true },
        };

        // Status card: ● Online · windows · node
        //              Last seen 4m ago
        var resources = Application.Current.Resources;
        var captionStyle = (Style)resources["CaptionTextBlockStyle"];
        var secondaryText = (Microsoft.UI.Xaml.Media.Brush)resources["TextFillColorSecondaryBrush"];
        var successBrush = (Microsoft.UI.Xaml.Media.Brush)resources["SystemFillColorSuccessBrush"];
        var neutralBrush = (Microsoft.UI.Xaml.Media.Brush)resources["SystemFillColorNeutralBrush"];

        var statusCard = new StackPanel
        {
            Padding = new Thickness(12, 2, 12, 6),
            Spacing = 2,
            MinWidth = 260
        };
        var statusLine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        statusLine.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = node.IsOnline ? successBrush : neutralBrush
        });
        var statusParts = new List<string> { node.IsOnline ? "Online" : "Offline" };
        if (!string.IsNullOrEmpty(node.Platform)) statusParts.Add(node.Platform);
        if (!string.IsNullOrEmpty(node.Mode)) statusParts.Add(node.Mode);
        statusLine.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", statusParts),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false
        });
        statusCard.Children.Add(statusLine);

        if (node.LastSeen.HasValue)
        {
            var age = DateTime.UtcNow - node.LastSeen.Value;
            var seenText = age.TotalMinutes < 1 ? "just now"
                : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
                : age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago"
                : $"{(int)age.TotalDays}d ago";
            statusCard.Children.Add(new TextBlock
            {
                Text = $"Last seen {seenText}",
                Style = captionStyle, FontSize = 11,
                Foreground = secondaryText,
                IsTextSelectionEnabled = false
            });
        }
        items.Add(new() { CustomContent = statusCard });

        // Capabilities + Commands
        if (node.Capabilities.Count > 0 || node.Commands.Count > 0)
        {
            items.Add(new() { Text = $"Capabilities ({node.CapabilityCount}) · Commands ({node.CommandCount})", IsHeader = true });

            var cmdGroups = node.Commands
                .GroupBy(c => c.Contains('.') ? c[..c.IndexOf('.')] : c, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(c => c.Contains('.') ? c[(c.IndexOf('.') + 1)..] : c).ToList(), StringComparer.OrdinalIgnoreCase);

            var shownGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cap in node.Capabilities)
            {
                cmdGroups.TryGetValue(cap, out var cmds);
                items.Add(new() { CustomContent = BuildCapabilityRow(cap, cmds, secondaryText, captionStyle) });
                shownGroups.Add(cap);
            }

            // Command groups without a matching capability entry
            foreach (var group in cmdGroups.Where(g => !shownGroups.Contains(g.Key)).OrderBy(g => g.Key))
            {
                items.Add(new() { CustomContent = BuildCapabilityRow(group.Key, group.Value, secondaryText, captionStyle) });
            }
        }

        return items;
    }

    private static UIElement BuildCapabilityRow(string cap, List<string>? commands, Microsoft.UI.Xaml.Media.Brush secondaryText, Style captionStyle)
    {
        var grid = new Grid
        {
            Padding = new Thickness(12, 4, 12, 4),
            ColumnSpacing = 10,
            MinWidth = 260
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var glyph = CapabilityIcons.TryGetValue(cap, out var pua) ? pua : "\uE7C3"; // Page (fallback)
        var icon = FluentIconCatalog.Build(glyph);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 0, 0);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var stack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = cap,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            IsTextSelectionEnabled = false
        });
        if (commands != null && commands.Count > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = string.Join(", ", commands),
                Style = captionStyle, FontSize = 11,
                Foreground = secondaryText,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 240,
                IsTextSelectionEnabled = false
            });
        }
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);

        return grid;
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
            _nodeService.ToastRequested += OnNodeToastRequested;
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

    private void OnNodeToastRequested(object? sender, Microsoft.Toolkit.Uwp.Notifications.ToastContentBuilder builder)
        => _dispatcherQueue?.TryEnqueue(() =>
            NonFatalAction.Run(() => ShowToast(builder), msg => Logger.Warn($"Failed to show node toast: {msg}")));

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
            _gatewayDataStore.ClearPairingAndAgentCaches();
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
                _gatewayDataStore.RemoveSessionActivity(_displayedSessionKey);
            }
            _currentActivity = null;
            _gatewayDataStore.SetCurrentActivity(null);
        }
        else
        {
            var sessionKey = activity.SessionKey ?? "default";
            _sessionActivities[sessionKey] = activity;
            _gatewayDataStore.SetSessionActivity(sessionKey, activity);
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
                _gatewayDataStore.SetCurrentActivity(activity);
            }
        }
        
        UpdateTrayIcon();
    }

    private void OnChannelHealthUpdated(object? sender, ChannelHealth[] channels)
    {
        _lastChannels = channels;
        _gatewayDataStore.SetChannels(channels);
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
        _gatewayDataStore.SetSessions(sessions);
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
        _gatewayDataStore.SetUsage(usage);
        _dispatcherQueue?.TryEnqueue(() =>
        {
            _hubWindow?.UpdateUsage(usage);
        });
    }

    private void OnUsageStatusUpdated(object? sender, GatewayUsageStatusInfo usageStatus)
    {
        _lastUsageStatus = usageStatus;
        _gatewayDataStore.SetUsageStatus(usageStatus);
        _dispatcherQueue?.TryEnqueue(() =>
        {
            _hubWindow?.UpdateUsageStatus(usageStatus);
        });
    }

    private void OnUsageCostUpdated(object? sender, GatewayCostUsageInfo usageCost)
    {
        _lastUsageCost = usageCost;
        _gatewayDataStore.SetUsageCost(usageCost);
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
        _gatewayDataStore.SetGatewaySelf(_lastGatewaySelf);
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
        _gatewayDataStore.SetNodes(nodes);
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
        _gatewayDataStore.UpsertSessionPreviews(payload.Previews);
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
            _gatewayDataStore.AddAgentEvent(evt, MaxAppAgentEvents);
            _hubWindow?.UpdateAgentEvent(evt);
        });
    }

    private void OnNodePairListUpdated(object? sender, PairingListInfo data)
    {
        _lastNodePairList = data;
        _gatewayDataStore.SetNodePairList(data);
        _dispatcherQueue?.TryEnqueue(() => _hubWindow?.UpdateNodePairList(data));
    }

    private void OnDevicePairListUpdated(object? sender, DevicePairingListInfo data)
    {
        _lastDevicePairList = data;
        _gatewayDataStore.SetDevicePairList(data);
        _dispatcherQueue?.TryEnqueue(() => _hubWindow?.UpdateDevicePairList(data));
    }

    private void OnModelsListUpdated(object? sender, ModelsListInfo data)
    {
        _lastModelsList = data;
        _gatewayDataStore.SetModelsList(data);
        _dispatcherQueue?.TryEnqueue(() => _hubWindow?.UpdateModelsList(data));
    }

    private void OnPresenceUpdated(object? sender, PresenceEntry[] data)
    {
        _lastPresence = data;
        _gatewayDataStore.SetPresence(data);
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
            _hubWindow.GatewayDataStore = _gatewayDataStore;
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
            _hubWindow.ClearAppAgentEventsCache = () =>
            {
                _agentEventsCache.Clear();
                _gatewayDataStore.ClearAgentEvents();
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

            // Seed ALL cached data BEFORE first navigation so pages see data in Initialize()
            SeedHubCachedData();

            // Navigate to default page now that properties and data are set
            _hubWindow.NavigateToDefault();
        }
        // Always update live state
        _hubWindow.Settings = _settings;
        _hubWindow.GatewayDataStore = _gatewayDataStore;
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
        if (_gatewayDataStore.Sessions.Length > 0) _hubWindow.UpdateSessions(_gatewayDataStore.Sessions);
        if (_gatewayDataStore.Nodes.Length > 0) _hubWindow.UpdateNodes(_gatewayDataStore.Nodes);
        if (_gatewayDataStore.NodePairList != null) _hubWindow.UpdateNodePairList(_gatewayDataStore.NodePairList);
        if (_gatewayDataStore.DevicePairList != null) _hubWindow.UpdateDevicePairList(_gatewayDataStore.DevicePairList);
        if (_gatewayDataStore.ModelsList != null) _hubWindow.UpdateModelsList(_gatewayDataStore.ModelsList);
        if (_gatewayDataStore.Presence.Length > 0) _hubWindow.UpdatePresence(_gatewayDataStore.Presence);
        if (_gatewayDataStore.GatewaySelf != null) _hubWindow.UpdateGatewaySelf(_gatewayDataStore.GatewaySelf);
        if (_lastAgentsList.HasValue) _hubWindow.UpdateAgentsList(_lastAgentsList.Value);
        if (_gatewayDataStore.AgentEvents.Count > 0) _hubWindow.SeedAgentEvents(_gatewayDataStore.AgentEvents);
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
            _globalHotkey.SettingsHotkeyPressed -= OnSettingsHotkeyPressed;
            _globalHotkey.SettingsHotkeyPressed += OnSettingsHotkeyPressed;
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
        Channels = _gatewayDataStore.Channels,
        Sessions = _gatewayDataStore.Sessions,
        Nodes = _gatewayDataStore.Nodes,
        Usage = _gatewayDataStore.Usage,
        UsageStatus = _gatewayDataStore.UsageStatus,
        UsageCost = _gatewayDataStore.UsageCost,
        GatewaySelf = _gatewayDataStore.GatewaySelf,
        AuthFailureMessage = _authFailureMessage,
        LastUpdateInfo = _gatewayDataStore.LastUpdateInfo,
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

    private void OnSettingsHotkeyPressed(object? sender, EventArgs e)
    {
        if (_dispatcherQueue == null) return;
        _dispatcherQueue.TryEnqueue(() => ShowHub("companion"));
    }

    #endregion

    #region Updates

    private static UpdateCommandCenterInfo BuildInitialUpdateInfo() => new()
    {
        Status = "Not checked",
        CurrentVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown"
    };

    private void SetLastUpdateInfo(UpdateCommandCenterInfo info)
    {
        _lastUpdateInfo = info;
        _gatewayDataStore.SetLastUpdateInfo(info);
    }

    private void SetLastUpdateDetail(string detail) => SetLastUpdateInfo(new UpdateCommandCenterInfo
    {
        Status = _lastUpdateInfo.Status,
        CurrentVersion = _lastUpdateInfo.CurrentVersion,
        LatestVersion = _lastUpdateInfo.LatestVersion,
        CheckedAt = _lastUpdateInfo.CheckedAt,
        Detail = detail
    });

    private async Task<bool> CheckForUpdatesAsync()
    {
        try
        {
#if DEBUG
            Logger.Info("Skipping update check in debug build");
            SetLastUpdateInfo(new UpdateCommandCenterInfo
            {
                Status = "Skipped",
                CurrentVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
                CheckedAt = DateTime.UtcNow,
                Detail = "debug build"
            });
            return true;
#else
            Logger.Info("Checking for updates...");
            SetLastUpdateInfo(new UpdateCommandCenterInfo
            {
                Status = "Checking",
                CurrentVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
                CheckedAt = DateTime.UtcNow
            });
            var updateFound = await AppUpdater.CheckForUpdatesAsync();

            if (!updateFound)
            {
                Logger.Info("No updates available");
                SetLastUpdateInfo(new UpdateCommandCenterInfo
                {
                    Status = "Current",
                    CurrentVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
                    CheckedAt = DateTime.UtcNow,
                    Detail = "no updates available"
                });
                return true;
            }

            var release = AppUpdater.LatestRelease!;
            var changelog = AppUpdater.GetChangelog(true) ?? "No release notes available.";
            Logger.Info($"Update available: {release.TagName}");
            SetLastUpdateInfo(new UpdateCommandCenterInfo
            {
                Status = "Available",
                CurrentVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
                LatestVersion = release.TagName,
                CheckedAt = DateTime.UtcNow,
                Detail = "prompted"
            });

            if (!string.IsNullOrWhiteSpace(_settings?.SkippedUpdateTag) &&
                string.Equals(_settings.SkippedUpdateTag, release.TagName, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info($"Skipping update prompt for remembered version {release.TagName}");
                SetLastUpdateDetail("skipped by user");
                return true;
            }

            var dialog = new UpdateDialog(release.TagName, changelog);
            var result = await dialog.ShowAsync();

            if (result == UpdateDialogResult.Download)
            {
                SetLastUpdateDetail("download requested");
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
                SetLastUpdateDetail("skipped by user");
            }

            return true; // RemindLater or Skip - continue
#endif
        }
        catch (Exception ex)
        {
            Logger.Warn($"Update check failed: {ex.Message}");
            SetLastUpdateInfo(new UpdateCommandCenterInfo
            {
                Status = "Failed",
                CurrentVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
                CheckedAt = DateTime.UtcNow,
                Detail = ex.Message
            });
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
