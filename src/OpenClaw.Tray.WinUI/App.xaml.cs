using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClawTray.Dialogs;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using OpenClawTray.Onboarding;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Updatum;
using Windows.ApplicationModel.DataTransfer;
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
    private OpenClawGatewayClient? _gatewayClient;

    /// <summary>The persistent gateway client. Used by the onboarding wizard for RPC calls.</summary>
    public OpenClawGatewayClient? GatewayClient => _gatewayClient;

    /// <summary>
    /// Ensures the managed SSH tunnel is started using the current settings.
    /// Used by the onboarding ConnectionPage when the user picks the SSH topology.
    /// </summary>
    public void EnsureSshTunnelStarted() => _sshTunnelService?.EnsureStarted(_settings);

    /// <summary>
    /// Returns the HWND of the active onboarding window, or IntPtr.Zero if none.
    /// Used by onboarding pages that need to host file pickers / dialogs.
    /// </summary>
    public IntPtr GetOnboardingWindowHandle()
        => _onboardingWindow != null
            ? WinRT.Interop.WindowNative.GetWindowHandle(_onboardingWindow)
            : IntPtr.Zero;

    /// <summary>
    /// Reinitializes the gateway client with current settings.
    /// Called by the onboarding wizard after saving URL + Token.
    /// </summary>
    public void ReinitializeGatewayClient(bool useBootstrapHandoffAuth = false) =>
        InitializeGatewayClient(useBootstrapHandoffAuth);
    private SettingsManager? _settings;
    private SshTunnelService? _sshTunnelService;
    private GlobalHotkeyService? _globalHotkey;
    private Mutex? _mutex;
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    private CancellationTokenSource? _deepLinkCts;
    private bool _isExiting;
    
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
    private string? _authFailureMessage;
    
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

        // Check for protocol activation (MSIX packaged apps receive deep links this way)
        string? protocolUri = GetProtocolActivationUri();

        // Single instance check - keep mutex alive for app lifetime.
        // When running with an isolated data dir (tests), suffix the mutex name so
        // the test instance does not collide with the user's regular tray app.
        // String.GetHashCode() is randomized per process since .NET Core 2.1, so
        // two test runs against the same data dir would otherwise pick different
        // mutex names — and `Math.Abs(int.MinValue)` overflows. Use a stable
        // SHA-256 prefix instead.
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

        // First-run check (also supports forced onboarding for testing)
        if (RequiresSetup(_settings) ||
            Environment.GetEnvironmentVariable("OPENCLAW_FORCE_ONBOARDING") == "1")
        {
            await ShowOnboardingAsync();
        }

        // Initialize tray icon (window-less pattern from WinUIEx)
        InitializeTrayIcon();
        ShowSurfaceImprovementsTipIfNeeded();

        // Initialize connections — always create operator client for UI data,
        // additionally create node service for gateway node mode or local MCP.
        InitializeGatewayClient();
        if (ShouldInitializeNodeService())
        {
            InitializeNodeService();
        }

        // Pre-warm chat window (WebView2 init takes 1-3s, do it now so left-click is instant)
        if (_settings != null && !string.IsNullOrWhiteSpace(_settings.GetEffectiveGatewayUrl()))
        {
            _chatWindow = new ChatWindow(_settings.GetEffectiveGatewayUrl(), _settings.Token);
            // Window is created but hidden — WebView2 initializes in the background
        }

        // Start deep link server
        StartDeepLinkServer();

        // Register global hotkey if enabled
        if (_settings.GlobalHotkeyEnabled)
        {
            _globalHotkey = new GlobalHotkeyService();
            _globalHotkey.HotkeyPressed += OnGlobalHotkeyPressed;
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
        _trayIcon = new TrayIcon(1, iconPath, "OpenClaw Tray — Disconnected");
        _trayIcon.IsVisible = true;
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

    private void ShowChatWindow()
    {
        if (_settings == null) return;
        if (_chatWindow == null)
        {
            _chatWindow = new ChatWindow(_settings.GetEffectiveGatewayUrl(), _settings.Token);
        }

        // Toggle: if visible, hide; if hidden, show near tray
        if (_chatWindow.Visible)
        {
            _chatWindow.Hide();
        }
        else
        {
            _chatWindow.ShowNearTrayAnimated();
        }
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
            _trayMenuWindow.SizeToContent();
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
            case "reconnect": ReconnectGateway(); break;
            case "dashboard": OpenDashboard(); break;
            case "canvas": _nodeService?.ShowCanvasWindow(); break;
            case "openchat": ShowChatWindow(); break;
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
            var dataPackage = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(_nodeService.FullDeviceId);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
            
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

            var dataPackage = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(summary);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);

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
        if (_gatewayClient == null || string.IsNullOrWhiteSpace(sessionKey)) return;

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
                "reset" => await _gatewayClient.ResetSessionAsync(sessionKey),
                "compact" => await _gatewayClient.CompactSessionAsync(sessionKey, 400),
                "delete" => await _gatewayClient.DeleteSessionAsync(sessionKey, deleteTranscript: true),
                "thinking" => await _gatewayClient.PatchSessionAsync(sessionKey, thinkingLevel: value),
                "verbose" => await _gatewayClient.PatchSessionAsync(sessionKey, verboseLevel: value),
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
                _ = _gatewayClient.RequestSessionsAsync();
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
        string? dashboardPath = null,
        string? details = null,
        string? sessionKey = null,
        string? nodeId = null)
    {
        ActivityStreamService.Add(
            category: category,
            title: line,
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
                InitializeGatewayClient();
                if (ShouldInitializeNodeService()) InitializeNodeService();
            }
            else
            {
                UnsubscribeGatewayEvents();
                _gatewayClient?.Dispose();
                _gatewayClient = null;
                var oldNode = _nodeService;
                _nodeService = null;
                try { oldNode?.Dispose(); } catch { }
                _currentStatus = ConnectionStatus.Disconnected;
                _lastSessions = Array.Empty<SessionInfo>();
                _lastNodePairList = null;
                _lastDevicePairList = null;
                _lastModelsList = null;
                UpdateTrayIcon();
                _hubWindow?.UpdateStatus(_currentStatus);
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
        if (_lastNodes.Length > 0)
        {
            menu.AddSeparator();

            var onlineCount = _lastNodes.Count(n => n.IsOnline);
            var totalCaps = _lastNodes.Sum(n => n.CapabilityCount);
            var deviceSummaryRight = $"{onlineCount} online · {totalCaps} caps";
            menu.AddCustomElement(BuildSectionHeader("Devices", deviceSummaryRight));

            var currentHost = Environment.MachineName;

            foreach (var node in _lastNodes.Take(5))
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
                                capRef.Set(on); _settings.Save(); ReconnectNodeServiceOnly();
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
        menu.AddMenuItem("Companion", "🦞", "companion");
        menu.AddMenuItem(LocalizationHelper.GetString("Menu_QuickSend"), "📤", "quicksend");

        // ── Exit ──
        menu.AddSeparator();
        menu.AddMenuItem(LocalizationHelper.GetString("Menu_Exit"), "❌", "exit");

        // Bottom padding
        menu.AddCustomElement(new Border { Height = 4 });
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
        if (_settings == null) return;
        if (!EnsureSshTunnelConfigured()) return;

        // Guard against empty gateway URL (e.g., fresh install before onboarding)
        var gatewayUrl = _settings.GetEffectiveGatewayUrl();
        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            Logger.Info("Gateway URL not configured — skipping client initialization");
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.Token))
        {
            Logger.Info("Gateway token not configured — skipping operator client initialization");
            return;
        }

        // Unsubscribe from old client if exists
        UnsubscribeGatewayEvents();
        _lastGatewaySelf = null;

        _gatewayClient = new OpenClawGatewayClient(
            gatewayUrl,
            _settings.Token,
            new AppLogger(),
            useBootstrapHandoffAuth);
        _gatewayClient.SetUserRules(_settings.UserRules.Count > 0 ? _settings.UserRules : null);
        _gatewayClient.SetPreferStructuredCategories(_settings.PreferStructuredCategories);
        _gatewayClient.StatusChanged += OnConnectionStatusChanged;
        _gatewayClient.AuthenticationFailed += OnAuthenticationFailed;
        _gatewayClient.ActivityChanged += OnActivityChanged;
        _gatewayClient.NotificationReceived += OnNotificationReceived;
        _gatewayClient.ChannelHealthUpdated += OnChannelHealthUpdated;
        _gatewayClient.SessionsUpdated += OnSessionsUpdated;
        _gatewayClient.UsageUpdated += OnUsageUpdated;
        _gatewayClient.UsageStatusUpdated += OnUsageStatusUpdated;
        _gatewayClient.UsageCostUpdated += OnUsageCostUpdated;
        _gatewayClient.NodesUpdated += OnNodesUpdated;
        _gatewayClient.SessionPreviewUpdated += OnSessionPreviewUpdated;
        _gatewayClient.SessionCommandCompleted += OnSessionCommandCompleted;
        _gatewayClient.GatewaySelfUpdated += OnGatewaySelfUpdated;
        _gatewayClient.CronListUpdated += OnCronListUpdated;
        _gatewayClient.CronStatusUpdated += OnCronStatusUpdated;
        _gatewayClient.ConfigUpdated += OnConfigUpdated;
        _gatewayClient.ConfigSchemaUpdated += OnConfigSchemaUpdated;
        _gatewayClient.SkillsStatusUpdated += OnSkillsStatusUpdated;
        _gatewayClient.AgentEventReceived += OnAgentEventReceived;
        _gatewayClient.NodePairListUpdated += OnNodePairListUpdated;
        _gatewayClient.DevicePairListUpdated += OnDevicePairListUpdated;
        _gatewayClient.ModelsListUpdated += OnModelsListUpdated;
        _gatewayClient.PresenceUpdated += OnPresenceUpdated;
        _gatewayClient.AgentsListUpdated += OnAgentsListUpdated;
        _gatewayClient.AgentFilesListUpdated += OnAgentFilesListUpdated;
        _gatewayClient.AgentFileContentUpdated += OnAgentFileContentUpdated;
        _ = _gatewayClient.ConnectAsync();
    }

    private void UnsubscribeGatewayEvents()
    {
        if (_gatewayClient != null)
        {
            _gatewayClient.StatusChanged -= OnConnectionStatusChanged;
            _gatewayClient.AuthenticationFailed -= OnAuthenticationFailed;
            _gatewayClient.ActivityChanged -= OnActivityChanged;
            _gatewayClient.NotificationReceived -= OnNotificationReceived;
            _gatewayClient.ChannelHealthUpdated -= OnChannelHealthUpdated;
            _gatewayClient.SessionsUpdated -= OnSessionsUpdated;
            _gatewayClient.UsageUpdated -= OnUsageUpdated;
            _gatewayClient.UsageStatusUpdated -= OnUsageStatusUpdated;
            _gatewayClient.UsageCostUpdated -= OnUsageCostUpdated;
            _gatewayClient.NodesUpdated -= OnNodesUpdated;
            _gatewayClient.SessionPreviewUpdated -= OnSessionPreviewUpdated;
            _gatewayClient.SessionCommandCompleted -= OnSessionCommandCompleted;
            _gatewayClient.GatewaySelfUpdated -= OnGatewaySelfUpdated;
            _gatewayClient.CronListUpdated -= OnCronListUpdated;
            _gatewayClient.CronStatusUpdated -= OnCronStatusUpdated;
            _gatewayClient.ConfigUpdated -= OnConfigUpdated;
            _gatewayClient.ConfigSchemaUpdated -= OnConfigSchemaUpdated;
            _gatewayClient.SkillsStatusUpdated -= OnSkillsStatusUpdated;
            _gatewayClient.AgentEventReceived -= OnAgentEventReceived;
            _gatewayClient.NodePairListUpdated -= OnNodePairListUpdated;
            _gatewayClient.DevicePairListUpdated -= OnDevicePairListUpdated;
            _gatewayClient.ModelsListUpdated -= OnModelsListUpdated;
            _gatewayClient.PresenceUpdated -= OnPresenceUpdated;
            _gatewayClient.AgentsListUpdated -= OnAgentsListUpdated;
            _gatewayClient.AgentFilesListUpdated -= OnAgentFilesListUpdated;
            _gatewayClient.AgentFileContentUpdated -= OnAgentFileContentUpdated;
        }
    }
    
    private void InitializeNodeService()
    {
        if (_settings == null) return;
        if (_dispatcherQueue == null) return;

        var enableNode = _settings.EnableNodeMode;
        var enableMcp = _settings.EnableMcpServer;
        if (!enableNode && !enableMcp) return;

        // Gateway connection requires auth (operator token, bootstrap token, or stored device token); MCP doesn't.
        var canRunGateway = StartupSetupState.CanStartNodeGateway(_settings, DataPath);

        if (enableNode && !canRunGateway && !enableMcp)
        {
            Logger.Warn("Node mode enabled but no token or bootstrap token configured — skipping node service. Run Setup Guide to configure.");
            return;
        }

        // Surface gateway-disabled fallback so the user isn't surprised when
        // they enabled both but only MCP comes up.
        if (enableNode && !canRunGateway && enableMcp)
        {
            Logger.Warn("Node mode enabled but gateway auth is missing — running MCP-only.");
        }

        try
        {
            _nodeService = new NodeService(
                new AppLogger(),
                _dispatcherQueue,
                DataPath,
                () => _keepAliveWindow?.Content as FrameworkElement,
                _settings,
                enableMcpServer: enableMcp);
            _nodeService.StatusChanged += OnNodeStatusChanged;
            _nodeService.NotificationRequested += OnNodeNotificationRequested;
            _nodeService.PairingStatusChanged += OnPairingStatusChanged;
            _nodeService.ChannelHealthUpdated += OnChannelHealthUpdated;
            _nodeService.InvokeCompleted += OnNodeInvokeCompleted;
            _nodeService.GatewaySelfUpdated += OnGatewaySelfUpdated;

            if (canRunGateway)
            {
                Logger.Info($"Initializing Windows Node service (gateway{(enableMcp ? " + MCP" : "")})...");
                _ = _nodeService.ConnectAsync(_settings.GetEffectiveGatewayUrl(), _settings.Token, _settings.BootstrapToken);
            }
            else
            {
                Logger.Info("Initializing Windows Node service (MCP-only, no gateway)...");
                _ = _nodeService.StartLocalOnlyAsync();
            }

            // Wire handlers after connect (RegisterCapabilities runs synchronously within Connect/StartLocal)
            WireAppCapabilityHandlers();
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to initialize node service: {ex}");
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
        return StartupSetupState.RequiresSetup(settings, DataPath);
    }

    private bool ShouldInitializeNodeService()
    {
        return _settings?.EnableNodeMode == true || _settings?.EnableMcpServer == true;
    }

    private void OnNodeStatusChanged(object? sender, ConnectionStatus status)
    {
        Logger.Info($"Node status: {status}");
        AddRecentActivity($"Node mode {status}", category: "node", dashboardPath: "nodes");
        
        // In node-only mode, surface node connection in main status indicator
        if (_settings?.EnableNodeMode == true)
        {
            _currentStatus = status;
            _hubWindow?.UpdateStatus(_currentStatus);
            UpdateTrayIcon();
            _dispatcherQueue?.TryEnqueue(UpdateStatusDetailWindow);
        }

        // Keep hub node state in sync for ConnectionPage
        SyncHubNodeState();
        
        // Don't show "connected" toast if waiting for pairing - we'll show pairing status instead
        if (status == ConnectionStatus.Connected && _nodeService?.IsPaired == true)
        {
            try
            {
                ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_NodeModeActive"))
                    .AddText(LocalizationHelper.GetString("Toast_NodeModeActiveDetail")));
            }
            catch { /* ignore */ }
        }
    }
    
    private void OnPairingStatusChanged(object? sender, OpenClaw.Shared.PairingStatusEventArgs args)
    {
        Logger.Info($"Pairing status: {args.Status}");
        
        try
        {
            if (args.Status == OpenClaw.Shared.PairingStatus.Pending)
            {
                ShowPairingPendingNotification(args.DeviceId);
            }
            else if (args.Status == OpenClaw.Shared.PairingStatus.Paired)
            {
                AddRecentActivity("Node paired", category: "node", dashboardPath: "nodes", nodeId: args.DeviceId);
                ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_NodePaired"))
                    .AddText(LocalizationHelper.GetString("Toast_NodePairedDetail")));
            }
            else if (args.Status == OpenClaw.Shared.PairingStatus.Rejected)
            {
                AddRecentActivity("Node pairing rejected", category: "node", dashboardPath: "nodes", nodeId: args.DeviceId, details: args.Message ?? LocalizationHelper.GetString("Toast_PairingRejectedDetail"));
                ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_PairingRejected"))
                    .AddText(LocalizationHelper.GetString("Toast_PairingRejectedDetail")));
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
                .AddArgument("command", command)));
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
        _currentStatus = status;
        DiagnosticsJsonlService.Write("connection.status", new
        {
            status = status.ToString(),
            nodeMode = _settings?.EnableNodeMode == true
        });
        _hubWindow?.UpdateStatus(_currentStatus);
        if (status == ConnectionStatus.Connected)
        {
            _authFailureMessage = null;
            if (_hubWindow != null && !_hubWindow.IsClosed)
                _hubWindow.LastAuthError = null;
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

        if (_gatewayClient != null &&
            sessions.Length > 0 &&
            DateTime.UtcNow - _lastPreviewRequestUtc > TimeSpan.FromSeconds(5))
        {
            _lastPreviewRequestUtc = DateTime.UtcNow;
            var keys = sessions.Take(5).Select(s => s.Key).ToArray();
            _ = _gatewayClient.RequestSessionPreviewAsync(keys, limit: 3, maxChars: 140);
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
            _ = _gatewayClient?.RequestSessionsAsync();
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
        _dispatcherQueue?.TryEnqueue(() => _hubWindow?.UpdateAgentEvent(evt));
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
        if (_gatewayClient == null)
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
            await _gatewayClient.CheckHealthAsync();
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

        var status = _currentStatus;
        if (_currentActivity != null && _currentActivity.Kind != OpenClaw.Shared.ActivityKind.Idle)
        {
            status = ConnectionStatus.Connecting; // Use connecting icon for activity
        }

        var iconPath = IconHelper.GetStatusIconPath(status);
        var tooltip = BuildTrayTooltip();

        try
        {
            _trayIcon.SetIcon(iconPath);
            _trayIcon.Tooltip = tooltip;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to update tray icon: {ex.Message}");
        }
    }

    private string BuildTrayTooltip()
    {
        var topology = GatewayTopologyClassifier.Classify(
            _settings?.GatewayUrl,
            _settings?.UseSshTunnel == true,
            _settings?.SshTunnelHost,
            _settings?.SshTunnelLocalPort ?? 0,
            _settings?.SshTunnelRemotePort ?? 0);
        var channelReady = _lastChannels.Count(c => ChannelHealth.IsHealthyStatus(c.Status));
        var nodeOnline = _lastNodes.Count(n => n.IsOnline);
        var nodeTotal = _lastNodes.Length;
        if (nodeTotal == 0 && _nodeService?.GetLocalNodeInfo() is { } localNode)
        {
            nodeTotal = 1;
            nodeOnline = localNode.IsOnline ? 1 : 0;
        }

        var warningCount = 0;
        if (_currentStatus != ConnectionStatus.Connected)
            warningCount++;
        if (_authFailureMessage != null)
            warningCount++;
        if (_lastChannels.Length == 0 && _currentStatus == ConnectionStatus.Connected)
            warningCount++;

        var tooltip = new List<string>
        {
            $"OpenClaw Tray — {_currentStatus}",
            $"Topology: {topology.DisplayName}",
            $"Channels: {channelReady}/{_lastChannels.Length} ready · Nodes: {nodeOnline}/{nodeTotal} online",
            $"Warnings: {warningCount} · Last check: {_lastCheckTime:HH:mm:ss}"
        };

        if (_currentActivity != null && !string.IsNullOrEmpty(_currentActivity.DisplayText))
        {
            tooltip.Insert(1, _currentActivity.DisplayText);
        }

        return string.Join("\n", tooltip);
    }

    #endregion

    #region Window Management

    private void ShowHub(string? navigateTo = null)
    {
        if (_hubWindow == null || _hubWindow.IsClosed)
        {
            _hubWindow = new HubWindow();
            _hubWindow.Settings = _settings;
            _hubWindow.GatewayClient = _gatewayClient;
            _hubWindow.CurrentStatus = _currentStatus;
            _hubWindow.OpenDashboardAction = OpenDashboard;
            _hubWindow.CheckForUpdatesAction = () => _ = CheckForUpdatesUserInitiatedAsync();
            _hubWindow.QuickSendAction = () => ShowQuickSend();
            _hubWindow.ConnectAction = () =>
            {
                InitializeGatewayClient();
                if (ShouldInitializeNodeService()) InitializeNodeService();
            };
            _hubWindow.DisconnectAction = () =>
            {
                UnsubscribeGatewayEvents();
                _gatewayClient?.Dispose();
                _gatewayClient = null;
                var oldNode = _nodeService;
                _nodeService = null;
                try { oldNode?.Dispose(); } catch { }
                _currentStatus = ConnectionStatus.Disconnected;
                UpdateTrayIcon();
                _hubWindow?.UpdateStatus(_currentStatus);
            };
            _hubWindow.ReconnectAction = () =>
            {
                ReconnectGateway();
            };
            if (_nodeService != null)
            {
                _hubWindow.NodeIsConnected = _nodeService.IsConnected;
                _hubWindow.NodeIsPaired = _nodeService.IsPaired;
                _hubWindow.NodeIsPendingApproval = _nodeService.IsPendingApproval;
                _hubWindow.NodeShortDeviceId = _nodeService.ShortDeviceId;
                _hubWindow.NodeFullDeviceId = _nodeService.FullDeviceId;
            }
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
        _hubWindow.GatewayClient = _gatewayClient;
        _hubWindow.CurrentStatus = _currentStatus;
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
        _hubWindow.Activate();
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
        // Reconnect with new settings — mirror the startup if/else pattern
        // to avoid dual connections that cause gateway conflicts.
        UnsubscribeGatewayEvents();
        _gatewayClient?.Dispose();
        _gatewayClient = null;
        _lastGatewaySelf = null;
        var oldNodeService = _nodeService;
        _nodeService = null;
        try { oldNodeService?.Dispose(); } catch (Exception ex) { Logger.Warn($"Node dispose error: {ex.Message}"); }
        if (_settings?.UseSshTunnel != true)
        {
            _sshTunnelService?.Stop();
        }

        // Reset status so the tray doesn't show a stale "Connected" from the previous mode
        _currentStatus = ConnectionStatus.Disconnected;
        _hubWindow?.UpdateStatus(_currentStatus);
        UpdateTrayIcon();

        // Reset chat window if gateway URL or token changed (pre-warmed window has stale URL)
        if (_chatWindow != null)
        {
            _chatWindow.ForceClose();
            _chatWindow = null;
        }
        
        // Always reconnect operator client for UI data
        InitializeGatewayClient();
        
        // Additionally reconnect node service if enabled
        if (ShouldInitializeNodeService())
        {
            InitializeNodeService();
        }

        // Refresh the Hub window if it's open
        _hubWindow?.UpdateStatus(_currentStatus);

        // Update global hotkey
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

        // Update auto-start
        AutoStartManager.SetAutoStart(_settings.AutoStart);

        // Keep hub window in sync with new client
        if (_hubWindow != null && !_hubWindow.IsClosed)
        {
            _hubWindow.Settings = _settings;
            _hubWindow.GatewayClient = _gatewayClient;
            _hubWindow.CurrentStatus = _currentStatus;
        }
    }

    /// <summary>
    /// Lightweight reconnect: tears down and rebuilds gateway + node connections
    /// without destroying the chat window or re-registering hotkeys/autostart.
    /// Use for "Reconnect" actions where only the connection needs recycling.
    /// </summary>
    private void ReconnectGateway()
    {
        UnsubscribeGatewayEvents();
        _gatewayClient?.Dispose();
        _gatewayClient = null;
        _lastGatewaySelf = null;
        var oldNodeService = _nodeService;
        _nodeService = null;
        try { oldNodeService?.Dispose(); } catch (Exception ex) { Logger.Warn($"Node dispose error: {ex.Message}"); }

        _currentStatus = ConnectionStatus.Disconnected;
        _hubWindow?.UpdateStatus(_currentStatus);
        UpdateTrayIcon();

        InitializeGatewayClient();
        if (ShouldInitializeNodeService())
            InitializeNodeService();

        if (_hubWindow != null && !_hubWindow.IsClosed)
        {
            _hubWindow.Settings = _settings;
            _hubWindow.GatewayClient = _gatewayClient;
            _hubWindow.CurrentStatus = _currentStatus;
        }
    }

    /// <summary>
    /// Reconnects only the node service (preserves gateway client + chat window).
    /// Use for capability toggle changes that don't require a full reconnect.
    /// </summary>
    private void ReconnectNodeServiceOnly()
    {
        var oldNodeService = _nodeService;
        _nodeService = null;
        try { oldNodeService?.Dispose(); } catch (Exception ex) { Logger.Warn($"Node dispose error: {ex.Message}"); }

        if (ShouldInitializeNodeService())
            InitializeNodeService();
    }

    private void ShowWebChat()
    {
        ShowHub("chat");
    }

    private void ShowQuickSend(string? prefillMessage = null)
    {
        if (_gatewayClient == null)
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
            var dialog = new QuickSendDialog(_gatewayClient, prefillMessage);
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

            UnsubscribeGatewayEvents();
            _gatewayClient?.Dispose();
            _gatewayClient = null;
            _lastGatewaySelf = null;

            var oldNodeService = _nodeService;
            _nodeService = null;
            try { oldNodeService?.Dispose(); } catch (Exception ex) { Logger.Warn($"Node dispose error: {ex.Message}"); }

            _sshTunnelService?.Stop();
            _currentStatus = ConnectionStatus.Disconnected;
            UpdateTrayIcon();

            if (!EnsureSshTunnelConfigured())
            {
                UpdateStatusDetailWindow();
                ShowToast(new ToastContentBuilder()
                    .AddText("SSH tunnel restart failed")
                    .AddText(_sshTunnelService?.LastError ?? "Check SSH tunnel settings and logs."));
                return;
            }

            if (_settings.EnableNodeMode)
                InitializeNodeService();
            else
            {
                InitializeGatewayClient();
                if (_settings.EnableMcpServer)
                    InitializeNodeService();
            }

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
        if (_gatewayClient != null)
        {
            await _gatewayClient.RequestSessionsAsync();
            await _gatewayClient.RequestUsageAsync();
            await _gatewayClient.RequestNodesAsync();
        }
        UpdateStatusDetailWindow();
    }

    private void UpdateStatusDetailWindow()
    {
        _hubWindow?.UpdateStatus(_currentStatus);
    }

    private GatewayCommandCenterState BuildCommandCenterState()
    {
        var nodes = _lastNodes.Select(NodeCapabilityHealthInfo.FromNode).ToList();
        if (nodes.Count == 0 && _nodeService?.GetLocalNodeInfo() is { } localNode)
        {
            nodes.Add(NodeCapabilityHealthInfo.FromNode(localNode));
        }

        var topology = GatewayTopologyClassifier.Classify(
            _settings?.GatewayUrl,
            _settings?.UseSshTunnel == true,
            _settings?.SshTunnelHost,
            _settings?.SshTunnelLocalPort ?? 0,
            _settings?.SshTunnelRemotePort ?? 0);
        var tunnel = BuildTunnelInfo();
        var portDiagnostics = PortDiagnosticsService.BuildDiagnostics(topology, tunnel);
        ApplyDetectedSshForwardTopology(topology, portDiagnostics);
        var runtime = BuildGatewayRuntimeInfo(portDiagnostics);
        var warnings = nodes.SelectMany(n => n.Warnings).ToList();
        warnings.AddRange(CommandCenterDiagnostics.BuildTopologyWarnings(topology, tunnel));
        warnings.AddRange(BuildPortDiagnosticWarnings(portDiagnostics, topology, tunnel));
        warnings.AddRange(BuildBrowserProxyAuthWarnings(nodes));

        if (!string.IsNullOrWhiteSpace(_authFailureMessage))
        {
            warnings.Insert(0, new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Critical,
                Category = "auth",
                Title = "Gateway authentication failed",
                Detail = _authFailureMessage
            });
        }

        if (_nodeService?.IsPendingApproval == true && !string.IsNullOrWhiteSpace(_nodeService.FullDeviceId))
        {
            var approvalCommand = $"openclaw devices approve {_nodeService.FullDeviceId}";
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Warning,
                Category = "pairing",
                Title = "Node is waiting for approval",
                Detail = $"Approve device {_nodeService.ShortDeviceId} from the gateway CLI, then re-open the command center after reconnect.",
                RepairAction = "Copy approval command",
                CopyText = approvalCommand
            });
        }

        if (_currentStatus == ConnectionStatus.Error)
        {
            warnings.Insert(0, new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Critical,
                Category = "gateway",
                Title = "Gateway connection error",
                Detail = "The tray is not currently connected to the gateway."
            });
        }
        else if (_currentStatus != ConnectionStatus.Connected)
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Warning,
                Category = "gateway",
                Title = "Gateway is not connected",
                Detail = $"Current connection state is {_currentStatus}."
            });
        }

        if (_currentStatus == ConnectionStatus.Connected &&
            DateTime.Now - _lastCheckTime > TimeSpan.FromMinutes(2))
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Warning,
                Category = "gateway",
                Title = "Gateway health is stale",
                Detail = $"Last health check was {_lastCheckTime:t}. Run a health check or verify the localhost tunnel."
            });
        }

        if (_lastChannels.Length == 0 && _currentStatus == ConnectionStatus.Connected && _gatewayClient != null)
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Info,
                Category = "channel",
                Title = "No channels reported",
                Detail = "The gateway health payload did not report any channels."
            });
        }
        else if (_lastChannels.Length == 0 && _currentStatus == ConnectionStatus.Connected && _settings?.EnableNodeMode == true)
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Info,
                Category = "gateway",
                Title = "Waiting for gateway health",
                Detail = "Node mode is connected. Channel/session inventories are filled from gateway health events when available."
            });
        }
        else if (_lastChannels.Length > 0 && _lastChannels.All(c => !ChannelHealth.IsHealthyStatus(c.Status)))
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Warning,
                Category = "channel",
                Title = "No channels are currently running",
                Detail = "Channels are configured but none are reporting a running/ready state."
            });
        }

        if (_currentStatus == ConnectionStatus.Connected && nodes.Count == 0 && _gatewayClient != null)
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Info,
                Category = "node",
                Title = "No nodes reported",
                Detail = "node.list did not report any connected nodes. Pair a Windows node or verify the operator token has node inventory access."
            });
        }

        if (_lastUsageCost?.Totals.MissingCostEntries > 0)
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Info,
                Category = "usage",
                Title = "Some usage costs are missing",
                Detail = $"{_lastUsageCost.Totals.MissingCostEntries} usage entr{(_lastUsageCost.Totals.MissingCostEntries == 1 ? "y is" : "ies are")} missing cost data."
            });
        }

        return new GatewayCommandCenterState
        {
            ConnectionStatus = _currentStatus,
            LastRefresh = _lastCheckTime.ToUniversalTime(),
            Topology = topology,
            Runtime = runtime,
            Update = _lastUpdateInfo,
            Tunnel = tunnel,
            GatewaySelf = _lastGatewaySelf,
            PortDiagnostics = portDiagnostics,
            Permissions = PermissionDiagnostics.BuildDefaultWindowsMatrix(),
            Channels = _lastChannels.Select(ChannelCommandCenterInfo.FromHealth).ToList(),
            Sessions = _lastSessions.ToList(),
            Usage = _lastUsage,
            UsageStatus = _lastUsageStatus,
            UsageCost = _lastUsageCost,
            Nodes = nodes,
            Warnings = CommandCenterDiagnostics.SortAndDedupeWarnings(warnings),
            RecentActivity = ActivityStreamService.GetItems(12)
                .Select(item => new CommandCenterActivityInfo
                {
                    Timestamp = item.Timestamp,
                    Category = item.Category,
                    Title = item.Title,
                    Details = item.Details,
                    DashboardPath = item.DashboardPath,
                    SessionKey = item.SessionKey,
                    NodeId = item.NodeId
                })
                .ToList()
        };
    }

    private IEnumerable<GatewayDiagnosticWarning> BuildBrowserProxyAuthWarnings(IReadOnlyList<NodeCapabilityHealthInfo> nodes)
    {
        if (_settings?.NodeBrowserProxyEnabled == false ||
            !string.IsNullOrWhiteSpace(_settings?.Token) ||
            !nodes.Any(node => node.BrowserDeclaredCommands.Contains("browser.proxy", StringComparer.OrdinalIgnoreCase)))
        {
            yield break;
        }

        yield return new GatewayDiagnosticWarning
        {
            Severity = GatewayDiagnosticSeverity.Info,
            Category = "browser",
            Title = "Browser proxy auth may need a gateway token",
            Detail = "This Windows node is advertising browser.proxy without a saved gateway shared token. QR/bootstrap pairing can connect the node, but an authenticated browser-control host may still require the same gateway token in Settings.",
            RepairAction = "Copy browser proxy auth guidance",
            CopyText = "If browser.proxy returns an auth error, enter the gateway shared token in Settings > Gateway Token, or configure the browser-control host to use auth compatible with the Windows node. Do not paste QR bootstrap tokens into the normal gateway token field."
        };
    }

    private static IEnumerable<GatewayDiagnosticWarning> BuildPortDiagnosticWarnings(
        IReadOnlyList<PortDiagnosticInfo> ports,
        GatewayTopologyInfo topology,
        TunnelCommandCenterInfo? tunnel)
    {
        foreach (var port in ports)
        {
            if (tunnel?.Status == TunnelStatus.Up &&
                port.Purpose.Equals("SSH tunnel local forward", StringComparison.OrdinalIgnoreCase) &&
                !port.IsListening)
            {
                yield return new GatewayDiagnosticWarning
                {
                    Severity = GatewayDiagnosticSeverity.Warning,
                    Category = "port",
                    Title = "SSH tunnel port is not listening",
                    Detail = port.Detail
                };
            }

            if (topology.DetectedKind == GatewayKind.WindowsNative &&
                port.Purpose.Equals("Gateway endpoint", StringComparison.OrdinalIgnoreCase) &&
                !port.IsListening)
            {
                yield return new GatewayDiagnosticWarning
                {
                    Severity = GatewayDiagnosticSeverity.Info,
                    Category = "port",
                    Title = "No local gateway listener detected",
                    Detail = port.Detail
                };
            }

            if (port.Purpose.Equals("Browser proxy host", StringComparison.OrdinalIgnoreCase) &&
                !port.IsListening)
            {
                if (topology.UsesSshTunnel)
                {
                    yield return new GatewayDiagnosticWarning
                    {
                        Severity = GatewayDiagnosticSeverity.Info,
                        Category = "browser",
                        Title = "Browser proxy SSH forward is not listening",
                        Detail = $"browser.proxy over SSH needs a companion local forward for port {port.Port}. Add the browser-control forward to the same tunnel, or enable the managed SSH tunnel so Windows starts both forwards.",
                        RepairAction = "Copy browser proxy SSH forward",
                        CopyText = BuildBrowserProxySshForwardHint(port.Port, tunnel)
                    };
                    continue;
                }

                yield return new GatewayDiagnosticWarning
                {
                    Severity = GatewayDiagnosticSeverity.Info,
                    Category = "browser",
                    Title = "Browser proxy host not detected",
                    Detail = "browser.proxy needs a compatible browser-control host listening on the gateway port + 2.",
                    RepairAction = "Copy browser setup guidance",
                    CopyText = CommandCenterTextHelper.BuildBrowserSetupGuidance(port.Port, topology, tunnel)
                };
            }
        }
    }

    private static string BuildBrowserProxySshForwardHint(int browserProxyPort, TunnelCommandCenterInfo? tunnel)
    {
        if (browserProxyPort is < 1 or > 65535)
            return "ssh -N -L <local-browser-port>:127.0.0.1:<remote-browser-port> <user>@<host>";

        var localBrowserPort = ResolveLocalBrowserProxyPort(browserProxyPort, tunnel);
        var target = BuildSshTarget(tunnel);
        var remoteBrowserPort = ResolveRemoteBrowserProxyPort(localBrowserPort, tunnel);
        return remoteBrowserPort is >= 1 and <= 65535
            ? $"ssh -N -L {localBrowserPort}:127.0.0.1:{remoteBrowserPort} {target}"
            : $"ssh -N -L {localBrowserPort}:127.0.0.1:<remote-gateway-port+2> {target}";
    }

    private static string BuildSshTarget(TunnelCommandCenterInfo? tunnel)
    {
        var host = tunnel?.Host?.Trim();
        var user = tunnel?.User?.Trim();
        if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(user))
            return $"{user}@{host}";
        if (!string.IsNullOrWhiteSpace(host))
            return $"<user>@{host}";
        return "<user>@<host>";
    }

    private static int ResolveLocalBrowserProxyPort(int fallbackBrowserProxyPort, TunnelCommandCenterInfo? tunnel)
    {
        if (TryGetEndpointPort(tunnel?.BrowserProxyLocalEndpoint, out var browserLocalPort))
            return browserLocalPort;

        if (TryGetEndpointPort(tunnel?.LocalEndpoint, out var localGatewayPort) &&
            localGatewayPort <= 65533)
        {
            return localGatewayPort + 2;
        }

        return fallbackBrowserProxyPort;
    }

    private static int? ResolveRemoteBrowserProxyPort(int localBrowserProxyPort, TunnelCommandCenterInfo? tunnel)
    {
        if (TryGetEndpointPort(tunnel?.BrowserProxyRemoteEndpoint, out var browserRemotePort))
            return browserRemotePort;

        if (!TryGetEndpointPort(tunnel?.RemoteEndpoint, out var remoteGatewayPort) ||
            remoteGatewayPort > 65533)
        {
            return null;
        }

        if (TryGetEndpointPort(tunnel?.LocalEndpoint, out var localGatewayPort) &&
            localBrowserProxyPort != localGatewayPort + 2)
        {
            return null;
        }

        return remoteGatewayPort + 2;
    }

    private static bool TryGetEndpointPort(string? endpoint, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(endpoint))
            return false;

        var separator = endpoint.LastIndexOf(':');
        return separator >= 0 &&
            int.TryParse(endpoint[(separator + 1)..], out port) &&
            port is >= 1 and <= 65535;
    }

    private static void ApplyDetectedSshForwardTopology(
        GatewayTopologyInfo topology,
        IReadOnlyList<PortDiagnosticInfo> ports)
    {
        if (topology.UsesSshTunnel ||
            topology.DetectedKind != GatewayKind.WindowsNative ||
            !topology.IsLoopback)
        {
            return;
        }

        var gatewayPort = ports.FirstOrDefault(port =>
            port.Purpose.Equals("Gateway endpoint", StringComparison.OrdinalIgnoreCase));
        if (gatewayPort is null ||
            !gatewayPort.IsListening ||
            !string.Equals(gatewayPort.OwningProcessName, "ssh", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        topology.DetectedKind = GatewayKind.MacOverSsh;
        topology.DisplayName = "SSH tunnel (detected)";
        topology.Transport = "ssh tunnel";
        topology.UsesSshTunnel = true;
        topology.Detail = $"Local gateway port {gatewayPort.Port} is owned by ssh, so Command Center treats it as a manually managed SSH local forward.";
    }

    private static GatewayRuntimeInfo BuildGatewayRuntimeInfo(IReadOnlyList<PortDiagnosticInfo> ports)
    {
        var gatewayPort = ports.FirstOrDefault(port =>
            port.Purpose.Equals("Gateway endpoint", StringComparison.OrdinalIgnoreCase));
        if (gatewayPort is null || !gatewayPort.IsListening)
            return new GatewayRuntimeInfo();

        return new GatewayRuntimeInfo
        {
            ProcessName = gatewayPort.OwningProcessName ?? "",
            ProcessId = gatewayPort.OwningProcessId,
            Port = gatewayPort.Port,
            IsSshForward = string.Equals(gatewayPort.OwningProcessName, "ssh", StringComparison.OrdinalIgnoreCase)
        };
    }

    private TunnelCommandCenterInfo? BuildTunnelInfo()
    {
        if (_settings?.UseSshTunnel != true)
        {
            return null;
        }

        var localPort = _sshTunnelService is { CurrentLocalPort: > 0 }
            ? _sshTunnelService.CurrentLocalPort
            : _settings.SshTunnelLocalPort;
        var remotePort = _sshTunnelService is { CurrentRemotePort: > 0 }
            ? _sshTunnelService.CurrentRemotePort
            : _settings.SshTunnelRemotePort;
        var host = string.IsNullOrWhiteSpace(_sshTunnelService?.CurrentHost)
            ? _settings.SshTunnelHost
            : _sshTunnelService!.CurrentHost!;
        var user = string.IsNullOrWhiteSpace(_sshTunnelService?.CurrentUser)
            ? _settings.SshTunnelUser
            : _sshTunnelService!.CurrentUser!;
        var status = _sshTunnelService?.Status is TunnelStatus.Up or TunnelStatus.Starting or TunnelStatus.Restarting or TunnelStatus.Failed
            ? _sshTunnelService.Status
            : string.IsNullOrWhiteSpace(_sshTunnelService?.LastError)
                ? TunnelStatus.Stopped
                : TunnelStatus.Failed;

        return new TunnelCommandCenterInfo
        {
            Status = status,
            LocalEndpoint = $"127.0.0.1:{localPort}",
            RemoteEndpoint = string.IsNullOrWhiteSpace(host)
                ? $"127.0.0.1:{remotePort}"
                : $"{host}:127.0.0.1:{remotePort}",
            BrowserProxyLocalEndpoint = _sshTunnelService?.CurrentBrowserProxyLocalPort > 0
                ? $"127.0.0.1:{_sshTunnelService.CurrentBrowserProxyLocalPort}"
                : "",
            BrowserProxyRemoteEndpoint = _sshTunnelService?.CurrentBrowserProxyRemotePort > 0
                ? string.IsNullOrWhiteSpace(host)
                    ? $"127.0.0.1:{_sshTunnelService.CurrentBrowserProxyRemotePort}"
                    : $"{host}:127.0.0.1:{_sshTunnelService.CurrentBrowserProxyRemotePort}"
                : "",
            Host = host,
            User = user,
            LastError = _sshTunnelService?.LastError,
            StartedAt = _sshTunnelService?.StartedAtUtc
        };
    }

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

        _onboardingWindow = new OnboardingWindow(_settings);
        _onboardingWindow.OnboardingCompleted += (s, e) =>
        {
            Logger.Info("Onboarding completed");
            _onboardingWindow = null;

            // If the persistent client was already initialized during onboarding, keep it
            if (_gatewayClient?.IsConnectedToGateway == true)
            {
                Logger.Info("Gateway client already connected from onboarding — keeping");
                return;
            }

            // Otherwise reinitialize with saved settings
            UnsubscribeGatewayEvents();
            _gatewayClient?.Dispose();
            _gatewayClient = null;
            var oldNodeService = _nodeService;
            _nodeService = null;
            try { oldNodeService?.Dispose(); } catch (Exception ex) { Logger.Warn($"Node dispose error: {ex.Message}"); }

            _currentStatus = ConnectionStatus.Disconnected;
            _hubWindow?.UpdateStatus(_currentStatus);
            UpdateTrayIcon();

            // Always reconnect operator client for UI data
            InitializeGatewayClient();
            if (ShouldInitializeNodeService())
                InitializeNodeService();

            // Keep hub window in sync with new client
            if (_hubWindow != null && !_hubWindow.IsClosed)
            {
                _hubWindow.Settings = _settings;
                _hubWindow.GatewayClient = _gatewayClient;
                _hubWindow.CurrentStatus = _currentStatus;
            }
        };
        _onboardingWindow.Closed += (s, e) => _onboardingWindow = null;
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

    private void ShowToast(ToastContentBuilder builder)
    {
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

        if (!string.IsNullOrEmpty(_settings.Token))
        {
            var separator = url.Contains('?') ? "&" : "?";
            url = $"{url}{separator}token={Uri.EscapeDataString(_settings.Token)}";
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
        if (_gatewayClient == null) return;

        var channel = _lastChannels.FirstOrDefault(c => c.Name == channelName);
        if (channel == null) return;

        try
        {
            var isRunning = ChannelHealth.IsHealthyStatus(channel.Status);
            if (isRunning)
            {
                await _gatewayClient.StopChannelAsync(channelName);
                AddRecentActivity($"Stopped channel: {channelName}", category: "channel", dashboardPath: "settings");
            }
            else
            {
                await _gatewayClient.StartChannelAsync(channelName);
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

    private void CopySupportContext()
    {
        try
        {
            var package = new DataPackage();
            package.SetText(CommandCenterTextHelper.BuildSupportContext(BuildCommandCenterState()));
            Clipboard.SetContent(package);
            Logger.Info("Copied support context from deep link");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to copy support context from deep link: {ex.Message}");
        }
    }

    private void CopyDebugBundle()
    {
        try
        {
            var package = new DataPackage();
            package.SetText(CommandCenterTextHelper.BuildDebugBundle(BuildCommandCenterState()));
            Clipboard.SetContent(package);
            Logger.Info("Copied debug bundle from deep link");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to copy debug bundle from deep link: {ex.Message}");
        }
    }

    private void CopyBrowserSetupGuidance()
    {
        try
        {
            var package = new DataPackage();
            package.SetText(CommandCenterTextHelper.BuildBrowserSetupGuidance(BuildCommandCenterState()));
            Clipboard.SetContent(package);
            Logger.Info("Copied browser setup guidance from deep link");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to copy browser setup guidance from deep link: {ex.Message}");
        }
    }

    private void CopyPortDiagnostics()
    {
        try
        {
            var package = new DataPackage();
            package.SetText(CommandCenterTextHelper.BuildPortDiagnosticsSummary(BuildCommandCenterState().PortDiagnostics));
            Clipboard.SetContent(package);
            Logger.Info("Copied port diagnostics from deep link");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to copy port diagnostics from deep link: {ex.Message}");
        }
    }

    private void CopyCapabilityDiagnostics()
    {
        try
        {
            var package = new DataPackage();
            package.SetText(CommandCenterTextHelper.BuildCapabilityDiagnosticsSummary(BuildCommandCenterState()));
            Clipboard.SetContent(package);
            Logger.Info("Copied capability diagnostics from deep link");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to copy capability diagnostics from deep link: {ex.Message}");
        }
    }

    private void CopyNodeInventory()
    {
        try
        {
            var package = new DataPackage();
            package.SetText(CommandCenterTextHelper.BuildNodeInventorySummary(BuildCommandCenterState().Nodes));
            Clipboard.SetContent(package);
            Logger.Info("Copied node inventory from deep link");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to copy node inventory from deep link: {ex.Message}");
        }
    }

    private void CopyChannelSummary()
    {
        try
        {
            var package = new DataPackage();
            package.SetText(CommandCenterTextHelper.BuildChannelSummaryText(BuildCommandCenterState().Channels));
            Clipboard.SetContent(package);
            Logger.Info("Copied channel summary from deep link");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to copy channel summary from deep link: {ex.Message}");
        }
    }

    private void CopyActivitySummary()
    {
        try
        {
            var package = new DataPackage();
            package.SetText(CommandCenterTextHelper.BuildActivitySummary(BuildCommandCenterState().RecentActivity));
            Clipboard.SetContent(package);
            Logger.Info("Copied activity summary from deep link");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to copy activity summary from deep link: {ex.Message}");
        }
    }

    private void CopyExtensibilitySummary()
    {
        try
        {
            var package = new DataPackage();
            package.SetText(CommandCenterTextHelper.BuildExtensibilitySummary(BuildCommandCenterState().Channels));
            Clipboard.SetContent(package);
            Logger.Info("Copied extensibility summary from deep link");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to copy extensibility summary from deep link: {ex.Message}");
        }
    }

    private void OnGlobalHotkeyPressed(object? sender, EventArgs e)
    {
        // Hotkey events are raised from a dedicated Win32 message-loop thread.
        // Creating/activating WinUI windows must happen on the app's UI thread.
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
            SendMessage = async (msg) =>
            {
                if (_gatewayClient != null)
                {
                    await _gatewayClient.SendChatMessageAsync(msg);
                }
            }
        });
    }

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
        var dataPackage = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetText(text);
        global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
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
            UnsubscribeGatewayEvents();
            _gatewayClient?.Dispose();
            _gatewayClient = null;
        });

        SafeShutdownStep("node service", () =>
        {
            _nodeService?.Dispose();
            _nodeService = null;
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
