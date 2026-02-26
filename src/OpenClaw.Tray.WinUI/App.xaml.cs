using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClawTray.Dialogs;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
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
    private OpenClawGatewayClient? _gatewayClient;
    private SettingsManager? _settings;
    private GlobalHotkeyService? _globalHotkey;
    private System.Timers.Timer? _healthCheckTimer;
    private System.Timers.Timer? _sessionPollTimer;
    private Mutex? _mutex;
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    private CancellationTokenSource? _deepLinkCts;
    
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
    private DateTime _lastCheckTime = DateTime.Now;
    private DateTime _lastUsageActivityLogUtc = DateTime.MinValue;

    // Session-aware activity tracking
    private readonly Dictionary<string, AgentActivity> _sessionActivities = new();
    private string? _displayedSessionKey;
    private DateTime _lastSessionSwitch = DateTime.MinValue;
    private static readonly TimeSpan SessionSwitchDebounce = TimeSpan.FromSeconds(3);

    // Windows (created on demand)
    private SettingsWindow? _settingsWindow;
    private WebChatWindow? _webChatWindow;
    private StatusDetailWindow? _statusDetailWindow;
    private NotificationHistoryWindow? _notificationHistoryWindow;
    private ActivityStreamWindow? _activityStreamWindow;
    private TrayMenuWindow? _trayMenuWindow;
    
    // Node service (optional, enabled in settings)
    private NodeService? _nodeService;
    
    // Keep-alive window to anchor WinUI runtime (prevents GC/threading issues)
    private Window? _keepAliveWindow;

    private string[]? _startupArgs;
    private string? _pendingProtocolUri;
    private static readonly string DataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenClawTray");
    private static readonly string CrashLogPath = Path.Combine(DataPath, "crash.log");
    private static readonly string RunMarkerPath = Path.Combine(DataPath, "run.marker");

    public App()
    {
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

        // Single instance check - keep mutex alive for app lifetime
        _mutex = new Mutex(true, "OpenClawTray", out bool createdNew);
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

        // Register URI scheme on first run
        DeepLinkHandler.RegisterUriScheme();

        // Check for updates before launching
        var shouldLaunch = await CheckForUpdatesAsync();
        if (!shouldLaunch)
        {
            Exit();
            return;
        }

        // Register toast activation handler
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;

        // Initialize settings
        _settings = new SettingsManager();

        // First-run check
        if (string.IsNullOrWhiteSpace(_settings.Token))
        {
            await ShowFirstRunWelcomeAsync();
        }

        // Initialize tray icon (window-less pattern from WinUIEx)
        InitializeTrayIcon();
        ShowSurfaceImprovementsTipIfNeeded();

        // Initialize connections - only use operator if node mode is disabled
        // (dual connections cause gateway conflicts)
        if (_settings?.EnableNodeMode == true)
        {
            // Node mode: only use node connection (provides health events too)
            InitializeNodeService();
        }
        else
        {
            // Operator mode: use operator connection
            InitializeGatewayClient();
        }

        // Start health check timer
        StartHealthCheckTimer();

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
        // Left-click: show custom popup menu
        ShowTrayMenuPopup();
    }

    private void OnTrayContextMenu(TrayIcon sender, TrayIconEventArgs e)
    {
        // Right-click: show custom popup menu
        ShowTrayMenuPopup();
    }

    private MenuFlyout BuildTrayMenuFlyout()
    {
        // Pre-fetch data (fire and forget - flyout will show with cached data)
        if (_gatewayClient != null && _currentStatus == ConnectionStatus.Connected)
        {
            try
            {
                _ = _gatewayClient.CheckHealthAsync();
                _ = _gatewayClient.RequestSessionsAsync();
                _ = _gatewayClient.RequestUsageAsync();
            }
            catch { /* ignore */ }
        }

        var flyout = new MenuFlyout();
        
        // Brand header
        var header = new MenuFlyoutItem { Text = "🦞 Molty", IsEnabled = false };
        header.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        flyout.Items.Add(header);
        flyout.Items.Add(new MenuFlyoutSeparator());

        // Status
        var statusIcon = _currentStatus switch
        {
            ConnectionStatus.Connected => "✅",
            ConnectionStatus.Connecting => "🔄",
            ConnectionStatus.Error => "❌",
            _ => "⚪"
        };
        var statusItem = new MenuFlyoutItem { Text = $"{statusIcon} Status: {_currentStatus}" };
        statusItem.Click += (s, e) => ShowStatusDetail();
        flyout.Items.Add(statusItem);

        // Activity (if any)
        if (_currentActivity != null && _currentActivity.Kind != OpenClaw.Shared.ActivityKind.Idle)
        {
            flyout.Items.Add(new MenuFlyoutItem 
            { 
                Text = $"{_currentActivity.Glyph} {_currentActivity.DisplayText}", 
                IsEnabled = false 
            });
        }

        // Usage
        if (_lastUsage != null)
        {
            flyout.Items.Add(new MenuFlyoutItem 
            { 
                Text = $"📊 {_lastUsage.DisplayText}", 
                IsEnabled = false 
            });
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Sessions
        if (_lastSessions.Length > 0)
        {
            var sessionsMenu = new MenuFlyoutSubItem { Text = $"📋 Sessions ({_lastSessions.Length})" };
            foreach (var session in _lastSessions.Take(5))
            {
                var sessionItem = new MenuFlyoutItem { Text = session.DisplayText };
                var sessionKey = session.Key;
                sessionItem.Click += (s, e) => OpenDashboard($"sessions/{sessionKey}");
                sessionsMenu.Items.Add(sessionItem);
            }
            flyout.Items.Add(sessionsMenu);
        }

        // Quick actions
        var dashboardItem = new MenuFlyoutItem { Text = "🌐 Open Dashboard" };
        dashboardItem.Click += (s, e) => OpenDashboard();
        flyout.Items.Add(dashboardItem);

        var chatItem = new MenuFlyoutItem { Text = "💬 Web Chat" };
        chatItem.Click += (s, e) => ShowWebChat();
        flyout.Items.Add(chatItem);

        var quickSendItem = new MenuFlyoutItem { Text = "✉️ Quick Send" };
        quickSendItem.Click += (s, e) => ShowQuickSend();
        flyout.Items.Add(quickSendItem);

        var historyItem = new MenuFlyoutItem { Text = "📜 Notification History" };
        historyItem.Click += (s, e) => ShowNotificationHistory();
        flyout.Items.Add(historyItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Settings & Exit
        var settingsItem = new MenuFlyoutItem { Text = "⚙️ Settings" };
        settingsItem.Click += (s, e) => ShowSettings();
        flyout.Items.Add(settingsItem);

        var logItem = new MenuFlyoutItem { Text = "📄 View Log" };
        logItem.Click += (s, e) => OpenLogFile();
        flyout.Items.Add(logItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var exitItem = new MenuFlyoutItem { Text = "❌ Exit" };
        exitItem.Click += (s, e) => ExitApplication();
        flyout.Items.Add(exitItem);

        return flyout;
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

            // Pre-fetch latest data before showing menu
            if (_gatewayClient != null && _currentStatus == ConnectionStatus.Connected)
            {
                try
                {
                    // Request fresh data
                    _ = _gatewayClient.CheckHealthAsync();
                    _ = _gatewayClient.RequestSessionsAsync();
                    _ = _gatewayClient.RequestUsageAsync();
                    
                    // Only wait if we have NO cached session data
                    // Otherwise show instantly with cached data (feels snappier)
                    if (_lastSessions.Length == 0)
                    {
                        await Task.Delay(200); // Wait for first-time data
                    }
                    else
                    {
                        await Task.Delay(50); // Brief yield to let fresh data arrive if ready
                    }
                    
                    Logger.Info($"Menu data: {_lastSessions.Length} sessions, {_lastChannels.Length} channels");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Data fetch error: {ex.Message}");
                }
            }

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
            case "dashboard": OpenDashboard(); break;
            case "webchat": ShowWebChat(); break;
            case "quicksend": ShowQuickSend(); break;
            case "history": ShowNotificationHistory(); break;
            case "activity": ShowActivityStream(); break;
            case "healthcheck": _ = RunHealthCheckAsync(userInitiated: true); break;
            case "settings": ShowSettings(); break;
            case "autostart": ToggleAutoStart(); break;
            case "log": OpenLogFile(); break;
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
            new ToastContentBuilder()
                .AddText("📋 Device ID Copied")
                .AddText($"Run: openclaw devices approve {_nodeService.ShortDeviceId}...")
                .Show();
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

            new ToastContentBuilder()
                .AddText("📋 Node summary copied")
                .AddText($"{_lastNodes.Length} node(s) copied to clipboard")
                .Show();
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
                new ToastContentBuilder()
                    .AddText("❌ Session action failed")
                    .AddText("Could not send request to gateway.")
                    .Show();
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
                new ToastContentBuilder()
                    .AddText("❌ Session action failed")
                    .AddText(ex.Message)
                    .Show();
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

    private static string TruncateMenuText(string text, int maxLength = 96)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 1)] + "…";
    }

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
        // Brand header
        menu.AddBrandHeader("🦞", "Molty");
        menu.AddSeparator();

        // Status
        var statusIcon = _currentStatus switch
        {
            ConnectionStatus.Connected => "✅",
            ConnectionStatus.Connecting => "🔄",
            ConnectionStatus.Error => "❌",
            _ => "⚪"
        };
        menu.AddMenuItem($"Status: {_currentStatus}", statusIcon, "status");

        // Activity (if any)
        if (_currentActivity != null && _currentActivity.Kind != OpenClaw.Shared.ActivityKind.Idle)
        {
            menu.AddMenuItem(_currentActivity.DisplayText, _currentActivity.Glyph, "", isEnabled: false);
        }

        // Usage
        if (_lastUsage != null || _lastUsageStatus != null || _lastUsageCost != null)
        {
            var usageText = _lastUsage?.DisplayText;
            if (string.IsNullOrWhiteSpace(usageText) || string.Equals(usageText, "No usage data", StringComparison.Ordinal))
            {
                usageText = _lastUsageStatus?.Providers.Count > 0
                    ? $"{_lastUsageStatus.Providers.Count} provider{(_lastUsageStatus.Providers.Count == 1 ? "" : "s")} active"
                    : "No usage data";
            }

            menu.AddMenuItem(usageText ?? "No usage data", "📊", "activity:usage");

            if (!string.IsNullOrWhiteSpace(_lastUsage?.ProviderSummary))
            {
                menu.AddMenuItem(
                    $"↳ {TruncateMenuText(_lastUsage.ProviderSummary!, 88)}",
                    "",
                    "",
                    isEnabled: false,
                    indent: true);
            }

            if (_lastUsageCost is { Days: > 0 } usageCost)
            {
                menu.AddMenuItem(
                    $"↳ {usageCost.Days}d cost: ${usageCost.Totals.TotalCost:F2}",
                    "",
                    "",
                    isEnabled: false,
                    indent: true);
                var recent = usageCost.Daily.TakeLast(3).ToArray();
                if (recent.Length > 0)
                {
                    menu.AddMenuItem(
                        $"↳ Last {recent.Length}d: ${recent.Sum(d => d.TotalCost):F2}",
                        "",
                        "",
                        isEnabled: false,
                        indent: true);
                }
            }
        }
        
        // Node Mode status (if enabled)
        if (_settings?.EnableNodeMode == true && _nodeService != null)
        {
            menu.AddSeparator();
            menu.AddHeader("🔌 Node Mode");
            
            if (_nodeService.IsPendingApproval)
            {
                menu.AddMenuItem("⏳ Waiting for approval...", "", "", isEnabled: false, indent: true);
                menu.AddMenuItem($"ID: {_nodeService.ShortDeviceId}...", "", "copydeviceid", indent: true);
            }
            else if (_nodeService.IsPaired && _nodeService.IsConnected)
            {
                menu.AddMenuItem("✅ Paired & Connected", "", "", isEnabled: false, indent: true);
            }
            else if (_nodeService.IsConnected)
            {
                menu.AddMenuItem("🔄 Connecting...", "", "", isEnabled: false, indent: true);
            }
            else
            {
                menu.AddMenuItem("⚪ Disconnected", "", "", isEnabled: false, indent: true);
            }
        }

        // Sessions (if any) - show meaningful info like the WinForms version
        if (_lastSessions.Length > 0)
        {
            menu.AddSeparator();
            menu.AddMenuItem($"Sessions ({_lastSessions.Length})", "💬", "activity:sessions");

            var visibleSessions = _lastSessions.Take(3).ToArray();
            foreach (var session in visibleSessions)
            {
                var displayName = session.RichDisplayText;
                if (!string.IsNullOrWhiteSpace(session.AgeText))
                    displayName += $" · {session.AgeText}";
                var icon = session.IsMain ? "⭐" : "•";
                menu.AddMenuItem(displayName, icon, $"session:{session.Key}", indent: true);

                SessionPreviewInfo? preview;
                lock (_sessionPreviewsLock)
                {
                    _sessionPreviews.TryGetValue(session.Key, out preview);
                }

                if (preview != null)
                {
                    var previewText = preview.Items.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Text))?.Text;
                    if (!string.IsNullOrWhiteSpace(previewText))
                    {
                        menu.AddMenuItem(
                            $"↳ {TruncateMenuText(previewText)}",
                            "",
                            "",
                            isEnabled: false,
                            indent: true);
                    }
                }

                var currentThinking = string.IsNullOrWhiteSpace(session.ThinkingLevel) ? "off" : session.ThinkingLevel;
                var currentVerbose = string.IsNullOrWhiteSpace(session.VerboseLevel) ? "off" : session.VerboseLevel;
                var nextVerbose = string.Equals(currentVerbose, "on", StringComparison.OrdinalIgnoreCase) ? "off" : "on";
                menu.AddMenuItem(
                    $"↳ Thinking: {currentThinking} → high",
                    "🧠",
                    $"session-thinking|high|{session.Key}",
                    indent: true);
                menu.AddMenuItem(
                    $"↳ Verbose: {currentVerbose} → {nextVerbose}",
                    "📝",
                    $"session-verbose|{nextVerbose}|{session.Key}",
                    indent: true);
                menu.AddMenuItem("↳ Reset session", "♻️", $"session-reset|{session.Key}", indent: true);
                menu.AddMenuItem("↳ Compact log", "🗜️", $"session-compact|{session.Key}", indent: true);
                if (!session.IsMain && !string.Equals(session.Key, "global", StringComparison.OrdinalIgnoreCase))
                    menu.AddMenuItem("↳ Delete session", "🗑️", $"session-delete|{session.Key}", indent: true);
            }
            if (_lastSessions.Length > visibleSessions.Length)
                menu.AddMenuItem($"+{_lastSessions.Length - visibleSessions.Length} more...", "", "", isEnabled: false, indent: true);
        }

        // Channels (if any)
        if (_lastChannels.Length > 0)
        {
            menu.AddSeparator();
            menu.AddHeader("📡 Channels");

            foreach (var channel in _lastChannels)
            {
                var rawStatus = channel.Status?.ToLowerInvariant() ?? "";
                
                // Match status logic from WinForms version
                var channelIcon = rawStatus switch
                {
                    "ok" or "connected" or "running" or "active" or "ready" => "🟢",
                    "stopped" or "idle" or "paused" or "configured" or "pending" => "🟡",
                    "error" or "disconnected" or "failed" => "🔴",
                    _ => "⚪"
                };
                
                var channelName = char.ToUpper(channel.Name[0]) + channel.Name[1..];
                menu.AddMenuItem(channelName, channelIcon, $"channel:{channel.Name}", indent: true);
            }
        }

        if (_lastNodes.Length > 0)
        {
            menu.AddSeparator();
            menu.AddMenuItem($"Nodes ({_lastNodes.Length})", "🖥️", "activity:nodes");

            var visibleNodes = _lastNodes.Take(3).ToArray();
            foreach (var node in visibleNodes)
            {
                var icon = node.IsOnline ? "🟢" : "⚪";
                menu.AddMenuItem(TruncateMenuText(node.DisplayText, 92), icon, "", isEnabled: false, indent: true);
                menu.AddMenuItem($"↳ {TruncateMenuText(node.DetailText, 92)}", "", "", isEnabled: false, indent: true);
            }

            if (_lastNodes.Length > visibleNodes.Length)
                menu.AddMenuItem($"+{_lastNodes.Length - visibleNodes.Length} more...", "", "", isEnabled: false, indent: true);

            menu.AddMenuItem("Copy node summary", "📋", "copynodesummary", indent: true);
        }

        var recentActivity = GetRecentActivity(maxItems: 4);
        if (recentActivity.Count > 0)
        {
            menu.AddSeparator();
            var totalActivity = ActivityStreamService.GetItems().Count;
            menu.AddMenuItem($"Recent Activity ({totalActivity})", "⚡", "activity");
            foreach (var line in recentActivity)
            {
                menu.AddMenuItem(TruncateMenuText(line, 94), "", "", isEnabled: false, indent: true);
            }
        }

        menu.AddSeparator();

        // Actions
        menu.AddMenuItem("Open Dashboard", "🌐", "dashboard");
        menu.AddMenuItem("Open Web Chat", "💬", "webchat");
        menu.AddMenuItem("Quick Send...", "📤", "quicksend");
        menu.AddMenuItem("Activity Stream...", "⚡", "activity");
        menu.AddMenuItem("Notification History...", "📋", "history");
        menu.AddMenuItem("Run Health Check", "🔄", "healthcheck");

        menu.AddSeparator();

        // Settings
        menu.AddMenuItem("Settings...", "⚙️", "settings");
        var autoStartText = (_settings?.AutoStart ?? false) ? "Auto-start ✓" : "Auto-start";
        menu.AddMenuItem(autoStartText, "🚀", "autostart");

        menu.AddSeparator();

        menu.AddMenuItem("Open Log File", "📄", "log");
        menu.AddMenuItem("Exit", "❌", "exit");
    }

    // Keep the old MenuFlyout method for reference but it won't be used
    private void BuildTrayMenu(MenuFlyout flyout)
    {
        // Brand header
        var brandItem = new MenuFlyoutItem
        {
            Text = "🦞 OpenClaw Tray",
            IsEnabled = false
        };
        flyout.Items.Add(brandItem);
        flyout.Items.Add(new MenuFlyoutSeparator());

        // Status
        var statusIcon = _currentStatus switch
        {
            ConnectionStatus.Connected => "✅",
            ConnectionStatus.Connecting => "🔄",
            ConnectionStatus.Error => "❌",
            _ => "⚪"
        };
        var statusItem = new MenuFlyoutItem
        {
            Text = $"{statusIcon} Status: {_currentStatus}"
        };
        statusItem.Click += (s, e) => ShowStatusDetail();
        flyout.Items.Add(statusItem);

        // Activity (if any)
        if (_currentActivity != null && _currentActivity.Kind != OpenClaw.Shared.ActivityKind.Idle)
        {
            var activityItem = new MenuFlyoutItem
            {
                Text = $"{_currentActivity.Glyph} {_currentActivity.DisplayText}",
                IsEnabled = false
            };
            flyout.Items.Add(activityItem);
        }

        // Usage
        if (_lastUsage != null)
        {
            var usageItem = new MenuFlyoutItem
            {
                Text = $"📊 {_lastUsage.DisplayText}",
                IsEnabled = false
            };
            flyout.Items.Add(usageItem);
        }

        // Sessions
        if (_lastSessions.Length > 0)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var sessionsHeader = new MenuFlyoutItem
            {
                Text = $"💬 Sessions ({_lastSessions.Length})"
            };
            sessionsHeader.Click += (s, e) => OpenDashboard("sessions");
            flyout.Items.Add(sessionsHeader);

            foreach (var session in _lastSessions.Take(5))
            {
                var sessionItem = new MenuFlyoutItem
                {
                    Text = $"   • {session.DisplayText}"
                };
                sessionItem.Click += (s, e) => OpenDashboard($"sessions/{session.Key}");
                flyout.Items.Add(sessionItem);
            }
        }

        // Channels
        if (_lastChannels.Length > 0)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var channelsHeader = new MenuFlyoutItem
            {
                Text = "📡 Channels",
                IsEnabled = false
            };
            flyout.Items.Add(channelsHeader);

            foreach (var channel in _lastChannels)
            {
                var channelIcon = channel.Status?.ToLowerInvariant() switch
                {
                    _ when ChannelHealth.IsHealthyStatus(channel.Status) => "🟢",
                    _ when ChannelHealth.IsIntermediateStatus(channel.Status) => "🟡",
                    _ => "🔴"
                };
                var channelItem = new MenuFlyoutItem
                {
                    Text = $"   {channelIcon} {channel.Name}"
                };
                channelItem.Click += (s, e) => ToggleChannel(channel.Name);
                flyout.Items.Add(channelItem);
            }
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Actions
        var dashboardItem = new MenuFlyoutItem { Text = "🌐 Open Dashboard" };
        dashboardItem.Click += (s, e) => OpenDashboard();
        flyout.Items.Add(dashboardItem);

        var webChatItem = new MenuFlyoutItem { Text = "💬 Open Web Chat" };
        webChatItem.Click += (s, e) => ShowWebChat();
        flyout.Items.Add(webChatItem);

        var quickSendItem = new MenuFlyoutItem { Text = "📤 Quick Send..." };
        quickSendItem.Click += (s, e) => ShowQuickSend();
        flyout.Items.Add(quickSendItem);

        var historyItem = new MenuFlyoutItem { Text = "📋 Notification History..." };
        historyItem.Click += (s, e) => ShowNotificationHistory();
        flyout.Items.Add(historyItem);

        var healthCheckItem = new MenuFlyoutItem { Text = "🔄 Run Health Check" };
        healthCheckItem.Click += async (s, e) => await RunHealthCheckAsync(userInitiated: true);
        flyout.Items.Add(healthCheckItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Settings
        var settingsItem = new MenuFlyoutItem { Text = "⚙️ Settings..." };
        settingsItem.Click += (s, e) => ShowSettings();
        flyout.Items.Add(settingsItem);

        var autoStartItem = new ToggleMenuFlyoutItem
        {
            Text = "🚀 Auto-start",
            IsChecked = _settings?.AutoStart ?? false
        };
        autoStartItem.Click += (s, e) => ToggleAutoStart();
        flyout.Items.Add(autoStartItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var logItem = new MenuFlyoutItem { Text = "📄 Open Log File" };
        logItem.Click += (s, e) => OpenLogFile();
        flyout.Items.Add(logItem);

        var exitItem = new MenuFlyoutItem { Text = "❌ Exit" };
        exitItem.Click += (s, e) => ExitApplication();
        flyout.Items.Add(exitItem);
    }

    #region Gateway Client

    private void InitializeGatewayClient()
    {
        if (_settings == null) return;

        // Unsubscribe from old client if exists
        UnsubscribeGatewayEvents();

        _gatewayClient = new OpenClawGatewayClient(_settings.GatewayUrl, _settings.Token, new AppLogger());
        _gatewayClient.StatusChanged += OnConnectionStatusChanged;
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
        _ = _gatewayClient.ConnectAsync();
    }

    private void UnsubscribeGatewayEvents()
    {
        if (_gatewayClient != null)
        {
            _gatewayClient.StatusChanged -= OnConnectionStatusChanged;
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
        }
    }
    
    private void InitializeNodeService()
    {
        if (_settings == null || !_settings.EnableNodeMode) return;
        if (_dispatcherQueue == null) return;
        
        try
        {
            Logger.Info("Initializing Windows Node service...");
            
            _nodeService = new NodeService(new AppLogger(), _dispatcherQueue, DataPath);
            _nodeService.StatusChanged += OnNodeStatusChanged;
            _nodeService.NotificationRequested += OnNodeNotificationRequested;
            _nodeService.PairingStatusChanged += OnPairingStatusChanged;
            
            // Connect to gateway as a node (separate connection from operator)
            _ = _nodeService.ConnectAsync(_settings.GatewayUrl, _settings.Token);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to initialize node service: {ex.Message}");
        }
    }
    
    private void OnNodeStatusChanged(object? sender, ConnectionStatus status)
    {
        Logger.Info($"Node status: {status}");
        AddRecentActivity($"Node mode {status}", category: "node", dashboardPath: "nodes");
        
        // In node-only mode, surface node connection in main status indicator
        if (_settings?.EnableNodeMode == true)
        {
            _currentStatus = status;
            UpdateTrayIcon();
        }
        
        // Don't show "connected" toast if waiting for pairing - we'll show pairing status instead
        if (status == ConnectionStatus.Connected && _nodeService?.IsPaired == true)
        {
            try
            {
                new ToastContentBuilder()
                    .AddText("🔌 Node Mode Active")
                    .AddText("This PC can now receive commands from the agent (canvas, screenshots)")
                    .Show();
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
                AddRecentActivity("Node pairing pending", category: "node", dashboardPath: "nodes", nodeId: args.DeviceId);
                // Show toast with approval instructions
                new ToastContentBuilder()
                    .AddText("⏳ Awaiting Pairing Approval")
                    .AddText($"Run on gateway: openclaw devices approve {args.DeviceId.Substring(0, 16)}...")
                    .Show();
            }
            else if (args.Status == OpenClaw.Shared.PairingStatus.Paired)
            {
                AddRecentActivity("Node paired", category: "node", dashboardPath: "nodes", nodeId: args.DeviceId);
                new ToastContentBuilder()
                    .AddText("✅ Node Paired!")
                    .AddText("This PC can now receive commands from the agent")
                    .Show();
            }
        }
        catch { /* ignore */ }
    }
    
    private void OnNodeNotificationRequested(object? sender, OpenClaw.Shared.Capabilities.SystemNotifyArgs args)
    {
        AddRecentActivity(args.Title, category: "node", dashboardPath: "nodes", details: args.Body);

        // Agent requested a notification via node.invoke system.notify
        try
        {
            new ToastContentBuilder()
                .AddText(args.Title)
                .AddText(args.Body)
                .Show();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to show node notification: {ex.Message}");
        }
    }

    private void OnConnectionStatusChanged(object? sender, ConnectionStatus status)
    {
        _currentStatus = status;
        UpdateTrayIcon();
        
        if (status == ConnectionStatus.Connected)
        {
            _ = RunHealthCheckAsync();
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
    }

    private void OnSessionsUpdated(object? sender, SessionInfo[] sessions)
    {
        _lastSessions = sessions;

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
    }

    private void OnUsageStatusUpdated(object? sender, GatewayUsageStatusInfo usageStatus)
    {
        _lastUsageStatus = usageStatus;
    }

    private void OnUsageCostUpdated(object? sender, GatewayCostUsageInfo usageCost)
    {
        _lastUsageCost = usageCost;

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

    private void OnNodesUpdated(object? sender, GatewayNodeInfo[] nodes)
    {
        var previousCount = _lastNodes.Length;
        var previousOnline = _lastNodes.Count(n => n.IsOnline);
        var online = nodes.Count(n => n.IsOnline);
        _lastNodes = nodes;

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

                new ToastContentBuilder()
                    .AddText(title)
                    .AddText(message)
                    .Show();
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

            builder.Show();
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

        return notification.Type?.ToLowerInvariant() switch
        {
            "health" => _settings.NotifyHealth,
            "urgent" => _settings.NotifyUrgent,
            "reminder" => _settings.NotifyReminder,
            "email" => _settings.NotifyEmail,
            "calendar" => _settings.NotifyCalendar,
            "build" => _settings.NotifyBuild,
            "stock" => _settings.NotifyStock,
            "info" => _settings.NotifyInfo,
            "error" => _settings.NotifyUrgent, // errors follow urgent setting
            _ => true
        };
    }

    #endregion

    #region Health Check

    private void StartHealthCheckTimer()
    {
        _healthCheckTimer = new System.Timers.Timer(30000); // 30 seconds
        _healthCheckTimer.Elapsed += async (s, e) => await RunHealthCheckAsync();
        _healthCheckTimer.Start();

        _sessionPollTimer = new System.Timers.Timer(10000); // 10 seconds
        _sessionPollTimer.Elapsed += async (s, e) => await PollSessionsAsync();
        _sessionPollTimer.Start();

        // Initial check
        _ = RunHealthCheckAsync();
    }

    private async Task RunHealthCheckAsync(bool userInitiated = false)
    {
        if (_gatewayClient == null)
        {
            if (userInitiated)
            {
                new ToastContentBuilder()
                    .AddText("Health Check")
                    .AddText("Gateway is not connected yet.")
                    .Show();
            }
            return;
        }

        try
        {
            _lastCheckTime = DateTime.Now;
            await _gatewayClient.CheckHealthAsync();
            if (userInitiated)
            {
                new ToastContentBuilder()
                    .AddText("Health Check")
                    .AddText("Health check request sent.")
                    .Show();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Health check failed: {ex.Message}");
            if (userInitiated)
            {
                new ToastContentBuilder()
                    .AddText("Health Check Failed")
                    .AddText(ex.Message)
                    .Show();
            }
        }
    }

    private async Task PollSessionsAsync()
    {
        if (_gatewayClient == null) return;

        try
        {
            await _gatewayClient.RequestSessionsAsync();
            await _gatewayClient.RequestUsageAsync();
            await _gatewayClient.RequestNodesAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Session poll failed: {ex.Message}");
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
        var tooltip = $"OpenClaw Tray — {_currentStatus}";
        
        if (_currentActivity != null && !string.IsNullOrEmpty(_currentActivity.DisplayText))
        {
            tooltip += $"\n{_currentActivity.DisplayText}";
        }

        tooltip += $"\nLast check: {_lastCheckTime:HH:mm:ss}";

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

    #endregion

    #region Window Management

    private void ShowSettings()
    {
        if (_settingsWindow == null || _settingsWindow.IsClosed)
        {
            _settingsWindow = new SettingsWindow(_settings!);
            _settingsWindow.Closed += (s, e) => 
            {
                _settingsWindow.SettingsSaved -= OnSettingsSaved;
                _settingsWindow = null;
            };
            _settingsWindow.SettingsSaved += OnSettingsSaved;
        }
        _settingsWindow.Activate();
    }

    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        // Reconnect with new settings
        _gatewayClient?.Dispose();
        InitializeGatewayClient();
        
        // Reinitialize node service (safe dispose pattern)
        var oldNodeService = _nodeService;
        _nodeService = null;
        try { oldNodeService?.Dispose(); } catch (Exception ex) { Logger.Warn($"Node dispose error: {ex.Message}"); }
        InitializeNodeService();

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
    }

    private void ShowWebChat()
    {
        if (_webChatWindow == null || _webChatWindow.IsClosed)
        {
            _webChatWindow = new WebChatWindow(_settings!.GatewayUrl, _settings.Token);
            _webChatWindow.Closed += (s, e) => _webChatWindow = null;
        }
        _webChatWindow.Activate();
    }

    private void ShowQuickSend(string? prefillMessage = null)
    {
        if (_gatewayClient == null) return;
        var dialog = new QuickSendDialog(_gatewayClient, prefillMessage);
        dialog.Activate();
    }

    private void ShowStatusDetail()
    {
        if (_statusDetailWindow == null || _statusDetailWindow.IsClosed)
        {
            _statusDetailWindow = new StatusDetailWindow(
                _currentStatus, _lastChannels, _lastSessions, _lastUsage, _lastCheckTime);
            _statusDetailWindow.Closed += (s, e) => _statusDetailWindow = null;
        }
        else
        {
            _statusDetailWindow.UpdateStatus(
                _currentStatus, _lastChannels, _lastSessions, _lastUsage, _lastCheckTime);
        }
        _statusDetailWindow.Activate();
    }

    private void ShowNotificationHistory()
    {
        if (_notificationHistoryWindow == null || _notificationHistoryWindow.IsClosed)
        {
            _notificationHistoryWindow = new NotificationHistoryWindow();
            _notificationHistoryWindow.Closed += (s, e) => _notificationHistoryWindow = null;
        }
        _notificationHistoryWindow.Activate();
    }

    private void ShowActivityStream(string? filter = null)
    {
        if (_activityStreamWindow == null || _activityStreamWindow.IsClosed)
        {
            _activityStreamWindow = new ActivityStreamWindow(OpenDashboard);
            _activityStreamWindow.Closed += (s, e) => _activityStreamWindow = null;
        }

        _activityStreamWindow.SetFilter(filter);
        _activityStreamWindow.Activate();
    }

    private async Task ShowFirstRunWelcomeAsync()
    {
        var dialog = new WelcomeDialog();
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            ShowSettings();
        }
    }

    private void ShowSurfaceImprovementsTipIfNeeded()
    {
        if (_settings == null || _settings.HasSeenActivityStreamTip) return;

        _settings.HasSeenActivityStreamTip = true;
        _settings.Save();

        try
        {
            new ToastContentBuilder()
                .AddText("⚡ New: Activity Stream")
                .AddText("Open the tray menu to view live sessions, usage, and node activity in one flyout.")
                .AddButton(new ToastButton()
                    .SetContent("Open Activity Stream")
                    .AddArgument("action", "open_activity"))
                .Show();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to show activity stream tip: {ex.Message}");
        }
    }

    #endregion

    #region Actions

    private void OpenDashboard(string? path = null)
    {
        if (_settings == null) return;
        
        var baseUrl = _settings.GatewayUrl
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
            }
            else
            {
                await _gatewayClient.StartChannelAsync(channelName);
            }
            
            // Refresh health
            await RunHealthCheckAsync();
        }
        catch (Exception ex)
        {
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

    private void OnGlobalHotkeyPressed(object? sender, EventArgs e)
    {
        ShowQuickSend();
    }

    #endregion

    #region Updates

    private async Task<bool> CheckForUpdatesAsync()
    {
        try
        {
            Logger.Info("Checking for updates...");
            var updateFound = await AppUpdater.CheckForUpdatesAsync();

            if (!updateFound)
            {
                Logger.Info("No updates available");
                return true;
            }

            var release = AppUpdater.LatestRelease!;
            var changelog = AppUpdater.GetChangelog(true) ?? "No release notes available.";
            Logger.Info($"Update available: {release.TagName}");

            var dialog = new UpdateDialog(release.TagName, changelog);
            var result = await dialog.ShowAsync();

            if (result == UpdateDialogResult.Download)
            {
                var installed = await DownloadAndInstallUpdateAsync();
                return !installed; // Don't launch if update succeeded
            }

            return true; // RemindLater or Skip - continue
        }
        catch (Exception ex)
        {
            Logger.Warn($"Update check failed: {ex.Message}");
            return true;
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
            OpenChat = ShowWebChat,
            OpenDashboard = OpenDashboard,
            OpenQuickSend = ShowQuickSend,
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
                }
            });
        }
    }

    #endregion

    #region Exit

    private void ExitApplication()
    {
        Logger.Info("Application exiting");
        
        // Cancel background tasks
        _deepLinkCts?.Cancel();
        
        // Stop timers
        _healthCheckTimer?.Stop();
        _healthCheckTimer?.Dispose();
        _sessionPollTimer?.Stop();
        _sessionPollTimer?.Dispose();
        
        // Cleanup hotkey
        _globalHotkey?.Dispose();
        
        // Unsubscribe and dispose gateway client
        UnsubscribeGatewayEvents();
        _gatewayClient?.Dispose();
        
        // Dispose tray and mutex
        _trayIcon?.Dispose();
        _mutex?.Dispose();
        
        // Dispose cancellation token source
        _deepLinkCts?.Dispose();
        
        Exit();
    }

    #endregion

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
