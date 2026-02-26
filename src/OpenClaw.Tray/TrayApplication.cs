using Microsoft.Toolkit.Uwp.Notifications;
using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ActivityKind = OpenClaw.Shared.ActivityKind;

namespace OpenClawTray;

public class TrayApplication : ApplicationContext
{
    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _contextMenu;
    private ModernTrayMenu? _modernMenu;
    private OpenClawGatewayClient? _gatewayClient;
    private SettingsManager? _settings;
    private System.Windows.Forms.Timer? _healthCheckTimer;
    private System.Windows.Forms.Timer? _sessionPollTimer;
    private GlobalHotkey? _globalHotkey;
    private ConnectionStatus _currentStatus = ConnectionStatus.Disconnected;
    private AgentActivity? _currentActivity;
    private readonly SynchronizationContext? _syncContext;

    // Session-aware activity: track per-session state to avoid flip-flopping
    private readonly Dictionary<string, AgentActivity> _sessionActivities = new();
    private string? _displayedSessionKey;
    private DateTime _lastSessionSwitch = DateTime.MinValue;
    private DateTime _lastCheckTime = DateTime.Now;
    private static readonly TimeSpan SessionSwitchDebounce = TimeSpan.FromSeconds(3);

    // Menu items for dynamic updates
    private ToolStripMenuItem? _statusItem;
    private ToolStripMenuItem? _activityItem;
    private ToolStripMenuItem? _usageItem;
    private ToolStripSeparator? _channelSeparator;
    private ToolStripSeparator? _sessionSeparator;
    private readonly List<ToolStripItem> _channelItems = new();
    private readonly List<ToolStripItem> _sessionItems = new();

    // Channel and session data for modern menu
    private ChannelHealth[] _lastChannels = Array.Empty<ChannelHealth>();
    private SessionInfo[] _lastSessions = Array.Empty<SessionInfo>();
    private GatewayUsageInfo? _lastUsage;

    private readonly string[] _startupArgs;

    // P/Invoke for proper icon cleanup
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    public TrayApplication(string[]? args = null)
    {
        _startupArgs = args ?? Array.Empty<string>();
        _syncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Logger.Info("Application starting");
        try
        {
            InitializeComponent();
            InitializeAsync();
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to initialize: {ex}");
            throw;
        }
    }

    private void InitializeComponent()
    {
        _settings = new SettingsManager();
        
        // First-run check: show welcome if no token configured
        if (string.IsNullOrWhiteSpace(_settings.Token))
        {
            ShowFirstRunWelcome();
        }
        
        // Register toast activation handler
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;

        _contextMenu = new ContextMenuStrip();

        // Title
        var titleItem = new ToolStripMenuItem("⚡ OpenClaw Tray") { Enabled = false };
        _contextMenu.Items.Add(titleItem);
        _contextMenu.Items.Add(new ToolStripSeparator());

        // Status (clickable — opens detail view)
        _statusItem = new ToolStripMenuItem("Status: Disconnected");
        _statusItem.Click += OnShowStatusDetail;
        _contextMenu.Items.Add(_statusItem);

        // Activity (hidden when idle)
        _activityItem = new ToolStripMenuItem("") { Enabled = false, Visible = false };
        _contextMenu.Items.Add(_activityItem);

        // Usage (hidden until data available)
        _usageItem = new ToolStripMenuItem("") { Enabled = false, Visible = false };
        _contextMenu.Items.Add(_usageItem);

        // Session separator + placeholder
        _sessionSeparator = new ToolStripSeparator { Visible = false };
        _contextMenu.Items.Add(_sessionSeparator);

        // Channel health separator + placeholder
        _channelSeparator = new ToolStripSeparator { Visible = false };
        _contextMenu.Items.Add(_channelSeparator);

        _contextMenu.Items.Add(new ToolStripSeparator());

        // Actions
        _contextMenu.Items.Add("Open Dashboard", null, OnOpenDashboard);
        _contextMenu.Items.Add("Open Web Chat", null, OnOpenWebUI);
        _contextMenu.Items.Add("Quick Send...", null, OnQuickSend);
        _contextMenu.Items.Add("Notification History...", null, OnNotificationHistory);
        _contextMenu.Items.Add("Run Health Check", null, OnManualHealthCheck);
        _contextMenu.Items.Add(new ToolStripSeparator());

        // Settings
        _contextMenu.Items.Add("Settings...", null, OnSettings);
        var autoStartMenuItem = new ToolStripMenuItem("Auto-start", null, OnToggleAutoStart)
        {
            Checked = _settings.AutoStart
        };
        _contextMenu.Items.Add(autoStartMenuItem);
        _contextMenu.Items.Add(new ToolStripSeparator());

        // Log file access
        _contextMenu.Items.Add("Open Log File", null, OnOpenLogFile);
        _contextMenu.Items.Add("Exit", null, OnExit);

        // Modern tray menu (Windows 11 style)
        _modernMenu = new ModernTrayMenu();
        _modernMenu.MenuItemClicked += OnModernMenuItemClicked;

        // Tray icon - use modern menu on right-click
        _notifyIcon = new NotifyIcon
        {
            Icon = CreateStatusIcon(ConnectionStatus.Disconnected),
            Text = "OpenClaw Tray — Disconnected",
            Visible = true
        };
        _notifyIcon.MouseClick += OnTrayIconClick;
        _notifyIcon.DoubleClick += OnDoubleClick;

        // Health check timer (30s)
        _healthCheckTimer = new System.Windows.Forms.Timer { Interval = 30000, Enabled = true };
        _healthCheckTimer.Tick += OnHealthCheck;

        // Session/usage poll timer (60s) — less frequent
        _sessionPollTimer = new System.Windows.Forms.Timer { Interval = 60000, Enabled = true };
        _sessionPollTimer.Tick += OnSessionPoll;

        // Global hotkey: Ctrl+Alt+Shift+C → Quick Send
        _globalHotkey = new GlobalHotkey();
        _globalHotkey.HotkeyPressed += (_, _) => OnQuickSend(null, EventArgs.Empty);
        _globalHotkey.Register();
    }

    private async void OnTrayIconClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Left)
        {
            // Request fresh data before showing menu
            if (_gatewayClient != null && _currentStatus == ConnectionStatus.Connected)
            {
                try
                {
                    // Fire off requests - don't await, just let them update the cache
                    _ = _gatewayClient.CheckHealthAsync();
                    _ = _gatewayClient.RequestSessionsAsync();
                    _ = _gatewayClient.RequestUsageAsync();
                    // Small delay to let responses arrive
                    await Task.Delay(150);
                }
                catch { /* ignore - show cached data */ }
            }
            
            // Build and show modern menu
            BuildModernMenu();
            _modernMenu?.ShowAtCursor();
        }
    }

    private void BuildModernMenu()
    {
        if (_modernMenu == null) return;

        _modernMenu.ClearItems();
        Logger.Info("Building modern menu...");

        // Brand Header - big lobster!
        _modernMenu.AddBrandHeader("🦞", "Molty", "Made with 🦞 love by Scott Hanselman and Molty");

        // Status - use simple bullets that we can color
        var (statusIcon, statusText, statusColor) = _currentStatus switch
        {
            ConnectionStatus.Connected => ("●", "Connected", Color.FromArgb(46, 204, 113)),
            ConnectionStatus.Connecting => ("●", "Connecting...", Color.FromArgb(241, 196, 15)),
            ConnectionStatus.Error => ("●", "Error", Color.FromArgb(231, 76, 60)),
            _ => ("○", "Disconnected", Color.Gray)
        };
        _modernMenu.AddStatusItem("status", statusIcon, "Gateway", statusText, statusColor);

        // Activity (if active)
        if (_currentActivity?.Kind != ActivityKind.Idle && !string.IsNullOrEmpty(_currentActivity?.DisplayText))
        {
            _modernMenu.AddItem("activity", "▶", _currentActivity.DisplayText, enabled: false);
        }

        // Usage (if available)
        if (_lastUsage != null)
        {
            _modernMenu.AddItem("usage", "◆", _lastUsage.DisplayText, enabled: false);
        }

        _modernMenu.AddSeparator();

        // Sessions (if any) - show meaningful info, clickable to go to /sessions
        if (_lastSessions.Length > 0)
        {
            _modernMenu.AddItem("sessions", "◈", "Sessions", isHeader: true);  // Clickable header!
            foreach (var session in _lastSessions.Take(5))
            {
                // Extract session type from key like "agent:main:cron:uuid" or "agent:main:subagent:uuid"
                var parts = session.Key.Split(':');
                var sessionType = parts.Length >= 3 ? parts[2] : "session";
                var displayName = sessionType switch
                {
                    "main" => "Main Agent",
                    "cron" => "Scheduled Task",
                    "subagent" => "Sub-Agent",
                    _ => sessionType.Length > 0 ? char.ToUpper(sessionType[0]) + sessionType[1..] : "Session"
                };
                
                // Add model if available
                if (!string.IsNullOrEmpty(session.Model))
                    displayName += $" ({session.Model})";
                else if (!string.IsNullOrEmpty(session.Channel))
                    displayName += $" · {session.Channel}";
                    
                var icon = session.IsMain ? "★" : "·";
                _modernMenu.AddItem($"session:{session.Key}", icon, displayName, enabled: false);
            }
            if (_lastSessions.Length > 5)
                _modernMenu.AddItem("", "", $"+{_lastSessions.Length - 5} more...", enabled: false);
            _modernMenu.AddSeparator();
        }

        // Channels (if any)
        if (_lastChannels.Length > 0)
        {
            _modernMenu.AddItem("", "◉", "Channels", isHeader: true);
            foreach (var ch in _lastChannels)
            {
                var rawStatus = ch.Status?.ToLowerInvariant() ?? "";
                
                // Normalize status display
                // READY = configured and verified (linked or probe ok), ready to receive messages
                // IDLE = configured but not verified (needs setup)
                // ON = actively running/processing
                var (statusLabel, color) = rawStatus switch
                {
                    "ok" or "connected" or "running" or "active" => ("ON", Color.FromArgb(46, 204, 113)),
                    "ready" => ("READY", Color.FromArgb(46, 204, 113)),
                    "stopped" or "idle" or "paused" => ("IDLE", Color.FromArgb(241, 196, 15)),
                    "configured" or "pending" => ("IDLE", Color.FromArgb(241, 196, 15)),
                    "error" or "disconnected" or "failed" => ("ERROR", Color.FromArgb(231, 76, 60)),
                    "not configured" or "unconfigured" => ("N/A", Color.Gray),
                    _ => ("OFF", Color.Gray)
                };
                _modernMenu.AddStatusItem($"channel:{ch.Name}", "○", char.ToUpper(ch.Name[0]) + ch.Name[1..], statusLabel, color);
            }
            _modernMenu.AddSeparator();
        }

        // Actions - use simple shapes we can color
        _modernMenu.AddItem("dashboard", "◐", "Open Dashboard");
        _modernMenu.AddItem("webchat", "◉", "Open Web Chat");
        _modernMenu.AddItem("quicksend", "▷", "Quick Send...");
        _modernMenu.AddItem("cron", "⏱", "Cron Jobs");
        _modernMenu.AddItem("history", "≡", "Notification History");
        _modernMenu.AddItem("servicehealth", "♥", "Service Health...");

        _modernMenu.AddSeparator();

        // Settings
        _modernMenu.AddItem("settings", "⚙", "Settings...");
        _modernMenu.AddItem("autostart", _settings?.AutoStart == true ? "✓" : "○", 
            _settings?.AutoStart == true ? "Auto-start: On" : "Auto-start: Off");
        _modernMenu.AddItem("logs", "▤", "Open Log File");

        _modernMenu.AddSeparator();
        _modernMenu.AddItem("exit", "✕", "Exit");
    }

    private void OnModernMenuItemClicked(object? sender, string id)
    {
        switch (id)
        {
            case "status":
                OnShowStatusDetail(null, EventArgs.Empty);
                break;
            case "dashboard":
                OnOpenDashboard(null, EventArgs.Empty);
                break;
            case "webchat":
                OnOpenWebUI(null, EventArgs.Empty);
                break;
            case "quicksend":
                OnQuickSend(null, EventArgs.Empty);
                break;
            case "history":
                OnNotificationHistory(null, EventArgs.Empty);
                break;
            case "servicehealth":
                OnShowStatusDetail(null, EventArgs.Empty);
                break;
            case "sessions":
                OpenDashboardPath("/sessions");
                break;
            case "cron":
                OpenDashboardPath("/cron");
                break;
            case "settings":
                OnSettings(null, EventArgs.Empty);
                break;
            case "autostart":
                OnToggleAutoStart(null, EventArgs.Empty);
                break;
            case "logs":
                OnOpenLogFile(null, EventArgs.Empty);
                break;
            case "exit":
                OnExit(null, EventArgs.Empty);
                break;
            default:
                // Handle channel toggle: "channel:telegram" etc.
                if (id.StartsWith("channel:"))
                {
                    var channelName = id[8..]; // Remove "channel:" prefix
                    _ = ToggleChannelAsync(channelName);
                }
                break;
        }
    }

    private void OpenDashboardPath(string path)
    {
        var dashboardUrl = GetDashboardUrl().TrimEnd('/') + path;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dashboardUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Failed to open dashboard path {path}", ex);
        }
    }

    private async Task ToggleChannelAsync(string channelName)
    {
        if (_gatewayClient == null) return;

        // Find the channel to check its current status
        var channel = _lastChannels.FirstOrDefault(c => c.Name.Equals(channelName, StringComparison.OrdinalIgnoreCase));
        if (channel == null) return;

        var isRunning = ChannelHealth.IsHealthyStatus(channel.Status);
        
        if (isRunning)
        {
            Logger.Info($"Stopping channel: {channelName}");
            await _gatewayClient.StopChannelAsync(channelName);
        }
        else
        {
            Logger.Info($"Starting channel: {channelName}");
            await _gatewayClient.StartChannelAsync(channelName);
        }

        // Request fresh health data after a short delay
        await Task.Delay(500);
        await _gatewayClient.CheckHealthAsync();
    }

    private async void InitializeAsync()
    {
        try
        {
            _gatewayClient = new OpenClawGatewayClient(_settings!.GatewayUrl, _settings.Token, Logger.Instance);
            _gatewayClient.StatusChanged += OnStatusChanged;
            _gatewayClient.NotificationReceived += OnNotificationReceived;
            _gatewayClient.ActivityChanged += OnActivityChanged;
            _gatewayClient.ChannelHealthUpdated += OnChannelHealthUpdated;
            _gatewayClient.SessionsUpdated += OnSessionsUpdated;
            _gatewayClient.UsageUpdated += OnUsageUpdated;

            await _gatewayClient.ConnectAsync();

            // Process deep link if launched via URI
            if (DeepLinkHandler.TryGetDeepLink(_startupArgs, out var uri) && uri != null)
            {
                await ProcessDeepLinkInternalAsync(uri);
            }

            // Start IPC server for deep links from other instances
            Program.StartDeepLinkServer(OnDeepLinkReceivedViaIpc);
        }
        catch (Exception ex)
        {
            Logger.Error("Initial connection failed", ex);
            ShowErrorToast("Connection Failed", $"Failed to connect: {ex.Message}");
        }
    }

    private void OnDeepLinkReceivedViaIpc(string uriString)
    {
        if (Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
        {
            _syncContext?.Post(async _ =>
            {
                await ProcessDeepLinkInternalAsync(uri);
            }, null);
        }
    }

    private async Task ProcessDeepLinkInternalAsync(Uri uri)
    {
        if (_gatewayClient == null) return;
        
        await DeepLinkHandler.ProcessDeepLinkAsync(
            uri, 
            _gatewayClient,
            openDashboard: path => OpenDashboardPath("/" + path.TrimStart('/')),
            openChat: () => _syncContext?.Post(_ => OnOpenWebUI(null, EventArgs.Empty), null),
            openSettings: () => _syncContext?.Post(_ => OnSettings(null, EventArgs.Empty), null),
            openQuickSend: msg => _syncContext?.Post(_ => ShowQuickSendWithMessage(msg), null)
        );
    }

    private void ShowQuickSendWithMessage(string? prefill)
    {
        using var dialog = new QuickSendDialog();
        if (!string.IsNullOrEmpty(prefill))
        {
            dialog.SetMessage(prefill);
        }
        if (dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.Message))
        {
            _ = _gatewayClient?.SendChatMessageAsync(dialog.Message);
        }
    }

    // --- Event Handlers (marshal to UI thread) ---

    private void OnStatusChanged(object? sender, ConnectionStatus status)
    {
        _syncContext?.Post(_ => UpdateStatus(status), null);
    }

    private void OnNotificationReceived(object? sender, OpenClawNotification n)
    {
        _syncContext?.Post(_ => ShowNotificationToast(n.Title, n.Message, n.Type), null);
    }

    private void OnActivityChanged(object? sender, AgentActivity activity)
    {
        _syncContext?.Post(_ => UpdateActivity(activity), null);
    }

    private void OnChannelHealthUpdated(object? sender, ChannelHealth[] channels)
    {
        _lastCheckTime = DateTime.Now;
        _syncContext?.Post(_ => UpdateChannelHealth(channels), null);
    }

    private void OnSessionsUpdated(object? sender, SessionInfo[] sessions)
    {
        _lastCheckTime = DateTime.Now;
        _syncContext?.Post(_ => UpdateSessions(sessions), null);
    }

    private void OnUsageUpdated(object? sender, GatewayUsageInfo usage)
    {
        _syncContext?.Post(_ => UpdateUsage(usage), null);
    }

    // --- UI Updates ---

    private void UpdateStatus(ConnectionStatus status)
    {
        _currentStatus = status;

        if (_notifyIcon != null)
        {
            var oldIcon = _notifyIcon.Icon;
            _notifyIcon.Icon = CreateStatusIcon(status, _currentActivity?.Kind);
            SafeDestroyIcon(oldIcon);

            var tooltip = _currentActivity?.Kind != ActivityKind.Idle && !string.IsNullOrEmpty(_currentActivity?.DisplayText)
                ? $"OpenClaw — {_currentActivity.DisplayText}"
                : $"OpenClaw — {status}";
            
            // Add last check time (culture-aware)
            var checkTime = _lastCheckTime.ToShortTimeString();
            tooltip = $"{tooltip}\nLast check: {checkTime}";
            
            _notifyIcon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
        }

        if (_statusItem != null)
        {
            var label = status switch
            {
                ConnectionStatus.Connected => "[ON]",
                ConnectionStatus.Connecting => "[...]",
                ConnectionStatus.Error => "[ERR]",
                _ => "[OFF]"
            };
            _statusItem.Text = $"{label} Gateway: {status}";
        }
    }

    private void UpdateActivity(AgentActivity activity)
    {
        // Track per-session activity for stable display
        _sessionActivities[activity.SessionKey] = activity;

        // Resolve which session to display using stable selection:
        // 1. Active main session always wins
        // 2. Keep current session if still active (prevents flip-flop)
        // 3. Fall back to most recently active non-main session
        var displayActivity = ResolveDisplayActivity(activity);
        _currentActivity = displayActivity;

        if (_activityItem != null)
        {
            if (displayActivity.Kind != ActivityKind.Idle && !string.IsNullOrEmpty(displayActivity.DisplayText))
            {
                _activityItem.Text = displayActivity.DisplayText;
                _activityItem.Visible = true;
            }
            else
            {
                _activityItem.Visible = false;
            }
        }

        // Also update the tray icon to reflect activity
        UpdateStatus(_currentStatus);
    }

    /// <summary>
    /// Selects the best session to display in the activity row.
    /// Avoids rapid switching between sessions by applying a debounce window.
    /// </summary>
    private AgentActivity ResolveDisplayActivity(AgentActivity incoming)
    {
        var now = DateTime.UtcNow;

        // If main session is active, always prefer it
        if (incoming.IsMain && incoming.Kind != ActivityKind.Idle)
        {
            _displayedSessionKey = incoming.SessionKey;
            _lastSessionSwitch = now;
            return incoming;
        }

        // If the currently displayed session is still active, keep it (no flip-flop)
        if (_displayedSessionKey != null &&
            _sessionActivities.TryGetValue(_displayedSessionKey, out var current) &&
            current.Kind != ActivityKind.Idle)
        {
            // Only allow switching away if debounce period has passed
            if (now - _lastSessionSwitch < SessionSwitchDebounce)
                return current;
        }

        // Check if any main session is active
        foreach (var kvp in _sessionActivities)
        {
            if (kvp.Value.IsMain && kvp.Value.Kind != ActivityKind.Idle)
            {
                _displayedSessionKey = kvp.Key;
                _lastSessionSwitch = now;
                return kvp.Value;
            }
        }

        // No main active — show the incoming active session if it has work
        if (incoming.Kind != ActivityKind.Idle)
        {
            _displayedSessionKey = incoming.SessionKey;
            _lastSessionSwitch = now;
            return incoming;
        }

        // Everything is idle
        _displayedSessionKey = null;
        return incoming;
    }

    private void UpdateChannelHealth(ChannelHealth[] channels)
    {
        // Store for modern menu
        _lastChannels = channels;

        // Remove old channel items
        foreach (var item in _channelItems)
            _contextMenu?.Items.Remove(item);
        _channelItems.Clear();

        if (channels.Length == 0)
        {
            if (_channelSeparator != null) _channelSeparator.Visible = false;
            return;
        }

        if (_channelSeparator != null) _channelSeparator.Visible = true;

        var insertIndex = _contextMenu?.Items.IndexOf(_channelSeparator!) ?? -1;
        if (insertIndex < 0) return;

        // Add header
        insertIndex++;
        var header = new ToolStripMenuItem("📡 Channels") { Enabled = false };
        _contextMenu!.Items.Insert(insertIndex, header);
        _channelItems.Add(header);

        foreach (var ch in channels)
        {
            insertIndex++;
            var item = new ToolStripMenuItem($"  {ch.DisplayText}") { Enabled = false };
            _contextMenu.Items.Insert(insertIndex, item);
            _channelItems.Add(item);
        }
    }

    private void UpdateSessions(SessionInfo[] sessions)
    {
        // Store for modern menu
        _lastSessions = sessions;

        // Log session data for debugging
        Logger.Info($"UpdateSessions: {sessions.Length} sessions");
        foreach (var s in sessions)
            Logger.Info($"  Session: key={s.Key}, isMain={s.IsMain}, status={s.Status}, channel={s.Channel}");
        
        // Remove old session items
        foreach (var item in _sessionItems)
            _contextMenu?.Items.Remove(item);
        _sessionItems.Clear();

        if (sessions.Length == 0)
        {
            if (_sessionSeparator != null) _sessionSeparator.Visible = false;
            return;
        }

        if (_sessionSeparator != null) _sessionSeparator.Visible = true;

        var insertIndex = _contextMenu?.Items.IndexOf(_sessionSeparator!) ?? -1;
        if (insertIndex < 0) return;

        // Add header
        insertIndex++;
        var header = new ToolStripMenuItem("🧠 Sessions") { Enabled = false };
        _contextMenu!.Items.Insert(insertIndex, header);
        _sessionItems.Add(header);

        foreach (var session in sessions)
        {
            insertIndex++;
            var displayText = session.RichDisplayText;
            if (!string.IsNullOrWhiteSpace(session.AgeText))
                displayText += $" · {session.AgeText}";
            var item = new ToolStripMenuItem($"  {displayText}") { Enabled = false };
            _contextMenu.Items.Insert(insertIndex, item);
            _sessionItems.Add(item);
        }
    }

    private void UpdateUsage(GatewayUsageInfo usage)
    {
        // Store for modern menu
        _lastUsage = usage;

        if (_usageItem != null)
        {
            _usageItem.Text = $"📊 {usage.DisplayText}";
            _usageItem.Visible = true;
        }
    }

    // --- Icon Creation (with proper cleanup) ---

    private Icon CreateStatusIcon(ConnectionStatus status, ActivityKind? activity = null)
    {
        var bitmap = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            
            if (status == ConnectionStatus.Connected)
            {
                // Draw pixel lobster when connected
                DrawPixelLobster(g);
            }
            else
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                
                // Base color from status
                var baseColor = status switch
                {
                    ConnectionStatus.Connecting => Color.FromArgb(255, 180, 0), // Amber
                    ConnectionStatus.Error => Color.FromArgb(220, 50, 50), // Red
                    _ => Color.FromArgb(128, 128, 128) // Gray
                };

                // Main circle for non-connected states
                using var brush = new SolidBrush(baseColor);
                g.FillEllipse(brush, 1, 1, 13, 13);
            }

            // Activity badge (small dot in corner when working)
            if (activity is not null and not ActivityKind.Idle && status == ConnectionStatus.Connected)
            {
                var badgeColor = activity switch
                {
                    ActivityKind.Exec => Color.FromArgb(255, 100, 0), // Orange
                    ActivityKind.Write or ActivityKind.Edit => Color.FromArgb(100, 200, 50), // Green
                    ActivityKind.Read => Color.FromArgb(80, 150, 255), // Blue
                    ActivityKind.Search or ActivityKind.Browser => Color.FromArgb(180, 80, 255), // Purple
                    ActivityKind.Message => Color.FromArgb(50, 200, 100), // Bright green
                    _ => Color.White
                };
                using var badgeBrush = new SolidBrush(badgeColor);
                g.FillEllipse(badgeBrush, 10, 0, 6, 6);
                using var borderPen = new Pen(Color.Black, 1);
                g.DrawEllipse(borderPen, 10, 0, 6, 6);
            }
        }

        var hIcon = bitmap.GetHicon();
        var icon = Icon.FromHandle(hIcon);
        bitmap.Dispose();
        return icon;
    }

    private void DrawPixelLobster(Graphics g)
    {
        // Pixel lobster from SVG - 16x16 pixel art
        var outline = Color.FromArgb(58, 10, 13);      // #3a0a0d - dark outline
        var body = Color.FromArgb(255, 79, 64);        // #ff4f40 - red body
        var claw = Color.FromArgb(255, 119, 95);       // #ff775f - lighter claws
        var eyeDark = Color.FromArgb(8, 16, 22);       // #081016 - pupils
        var eyeLight = Color.FromArgb(245, 251, 255);  // #f5fbff - eye whites

        // Outline (dark border)
        var outlinePixels = new[] {
            (1,5), (1,6), (1,7),
            (2,4), (2,8),
            (3,3), (3,9),
            (4,2), (4,10),
            (5,2), (6,2), (7,2), (8,2), (9,2), (10,2),
            (11,2), (12,3), (12,9),
            (13,4), (13,8),
            (14,5), (14,6), (14,7),
            (5,11), (6,11), (7,11), (8,11), (9,11), (10,11),
            (4,12), (11,12),
            (3,13), (12,13),
            (5,14), (6,14), (7,14), (8,14), (9,14), (10,14)
        };
        foreach (var (x, y) in outlinePixels)
            bitmap_SetPixel(g, x, y, outline);

        // Body (red)
        var bodyPixels = new[] {
            (5,3), (6,3), (7,3), (8,3), (9,3), (10,3),
            (4,4), (5,4), (7,4), (8,4), (10,4), (11,4),
            (3,5), (4,5), (5,5), (7,5), (8,5), (10,5), (11,5), (12,5),
            (3,6), (4,6), (5,6), (6,6), (7,6), (8,6), (9,6), (10,6), (11,6), (12,6),
            (3,7), (4,7), (5,7), (6,7), (7,7), (8,7), (9,7), (10,7), (11,7), (12,7),
            (4,8), (5,8), (6,8), (7,8), (8,8), (9,8), (10,8), (11,8),
            (5,9), (6,9), (7,9), (8,9), (9,9), (10,9),
            (5,12), (6,12), (7,12), (8,12), (9,12), (10,12),
            (6,13), (7,13), (8,13), (9,13)
        };
        foreach (var (x, y) in bodyPixels)
            bitmap_SetPixel(g, x, y, body);

        // Claws (lighter red)
        var clawPixels = new[] {
            (1,6), (2,5), (2,6), (2,7),
            (13,5), (13,6), (13,7), (14,6)
        };
        foreach (var (x, y) in clawPixels)
            bitmap_SetPixel(g, x, y, claw);

        // Eyes
        bitmap_SetPixel(g, 6, 4, eyeLight);
        bitmap_SetPixel(g, 9, 4, eyeLight);
        bitmap_SetPixel(g, 6, 5, eyeDark);
        bitmap_SetPixel(g, 9, 5, eyeDark);
    }

    private void bitmap_SetPixel(Graphics g, int x, int y, Color c)
    {
        using var brush = new SolidBrush(c);
        g.FillRectangle(brush, x, y, 1, 1);
    }

    private static void SafeDestroyIcon(Icon? icon)
    {
        if (icon == null) return;
        try
        {
            DestroyIcon(icon.Handle);
            icon.Dispose();
        }
        catch { }
    }

    // --- Toast Notifications ---

    private void ShowNotificationToast(string title, string message, string type = "info")
    {
        // Always log to history regardless of filter
        NotificationHistoryForm.AddEntry(title, message, type);

        // Check per-type filter
        if (_settings?.ShouldNotify(type) != true) return;

        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(message)
                .Show();
        }
        catch (Exception)
        {
            _notifyIcon?.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);
        }
    }

    private void ShowErrorToast(string title, string message)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(message)
                .Show();
        }
        catch
        {
            _notifyIcon?.ShowBalloonTip(3000, title, message, ToolTipIcon.Error);
        }
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        // Parse arguments from toast
        var args = ToastArguments.Parse(e.Argument);
        
        if (args.TryGetValue("action", out var action) && action == "openDashboard")
        {
            if (args.TryGetValue("url", out var url))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to open dashboard from toast: {ex.Message}");
                }
            }
        }
    }

    // --- Menu Actions ---

    private async void OnHealthCheck(object? sender, EventArgs e)
    {
        if (_gatewayClient != null && _currentStatus != ConnectionStatus.Connecting)
            await _gatewayClient.CheckHealthAsync();
    }

    private async void OnSessionPoll(object? sender, EventArgs e)
    {
        if (_gatewayClient != null && _currentStatus == ConnectionStatus.Connected)
        {
            await _gatewayClient.RequestSessionsAsync();
            await _gatewayClient.RequestUsageAsync();
        }
    }

    private async void OnManualHealthCheck(object? sender, EventArgs e)
    {
        Logger.Info("Manual health check triggered");
        if (_gatewayClient != null)
        {
            await _gatewayClient.CheckHealthAsync();
            await _gatewayClient.RequestSessionsAsync();
            await _gatewayClient.RequestUsageAsync();
        }
    }

    private void OnOpenWebUI(object? sender, EventArgs e)
    {
        try
        {
            WebChatForm.ShowOrFocus(_settings!.GatewayUrl, _settings.Token);
        }
        catch (Exception ex)
        {
            // Fallback to browser if WebView2 fails
            Logger.Warn($"WebView2 failed, falling back to browser: {ex.Message}");
            var url = _settings!.GatewayUrl
                .Replace("ws://", "http://")
                .Replace("wss://", "https://");
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex2)
            {
                ShowErrorToast("Failed to open Web UI", ex2.Message);
            }
        }
    }

    private string GetDashboardUrl()
    {
        var baseUrl = _settings!.GatewayUrl
            .Replace("ws://", "http://")
            .Replace("wss://", "https://");
        
        // Add token if available
        if (!string.IsNullOrEmpty(_settings.Token))
        {
            var separator = baseUrl.Contains("?") ? "&" : "?";
            return $"{baseUrl}{separator}token={Uri.EscapeDataString(_settings.Token)}";
        }
        return baseUrl;
    }

    private void OnOpenDashboard(object? sender, EventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(GetDashboardUrl()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowErrorToast("Failed to open Dashboard", ex.Message);
        }
    }

    private async void OnQuickSend(object? sender, EventArgs e)
    {
        using var dialog = new QuickSendDialog();
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            try
            {
                await _gatewayClient!.SendChatMessageAsync(dialog.Message);
                ShowClickableToast("Message Sent", "Click to continue chat in dashboard");
            }
            catch (Exception ex)
            {
                ShowErrorToast("Failed to Send", ex.Message);
            }
        }
    }

    private void ShowClickableToast(string title, string message)
    {
        NotificationHistoryForm.AddEntry(title, message, "info");
        
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(message)
                .AddArgument("action", "openDashboard")
                .AddArgument("url", GetDashboardUrl())
                .Show();
        }
        catch
        {
            _notifyIcon?.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);
        }
    }

    private void OnSettings(object? sender, EventArgs e)
    {
        using var dialog = new SettingsDialog(_settings!);
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            _settings!.Save();
            Task.Run(async () => await ReconnectAsync());
        }
    }

    private void ShowFirstRunWelcome()
    {
        var dashboardUrl = _settings!.GatewayUrl
            .Replace("ws://", "http://")
            .Replace("wss://", "https://");
            
        using var welcome = new WelcomeDialog(dashboardUrl);
        if (welcome.ShowDialog() == DialogResult.OK)
        {
            // User clicked "Open Settings"
            using var settings = new SettingsDialog(_settings);
            if (settings.ShowDialog() == DialogResult.OK)
            {
                _settings.Save();
            }
        }
    }

    private void OnToggleAutoStart(object? sender, EventArgs e)
    {
        var menuItem = (ToolStripMenuItem)sender!;
        _settings!.AutoStart = !_settings.AutoStart;
        menuItem.Checked = _settings.AutoStart;
        _settings.Save();
        AutoStartManager.SetAutoStart(_settings.AutoStart);
        Logger.Info($"Auto-start: {_settings.AutoStart}");
    }

    private void OnShowStatusDetail(object? sender, EventArgs e)
    {
        StatusDetailForm.ShowOrFocus(_gatewayClient, _settings, _currentStatus);
    }

    private void OnNotificationHistory(object? sender, EventArgs e)
    {
        NotificationHistoryForm.ShowOrFocus();
    }

    private void OnOpenLogFile(object? sender, EventArgs e)
    {
        try
        {
            var logDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenClawTray");
            var logPath = System.IO.Path.Combine(logDir, "openclaw-tray.log");

            if (System.IO.File.Exists(logPath))
            {
                Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo(logDir) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            ShowErrorToast("Failed to Open Log", ex.Message);
        }
    }

    private async Task ReconnectAsync()
    {
        try
        {
            if (_gatewayClient != null)
            {
                await _gatewayClient.DisconnectAsync();
                _gatewayClient.Dispose();
            }

            _gatewayClient = new OpenClawGatewayClient(_settings!.GatewayUrl, _settings.Token, Logger.Instance);
            _gatewayClient.StatusChanged += OnStatusChanged;
            _gatewayClient.NotificationReceived += OnNotificationReceived;
            _gatewayClient.ActivityChanged += OnActivityChanged;
            _gatewayClient.ChannelHealthUpdated += OnChannelHealthUpdated;
            _gatewayClient.SessionsUpdated += OnSessionsUpdated;
            _gatewayClient.UsageUpdated += OnUsageUpdated;

            await _gatewayClient.ConnectAsync();
        }
        catch (Exception ex)
        {
            Logger.Error("Reconnection failed", ex);
            ShowErrorToast("Reconnection Failed", ex.Message);
        }
    }

    private void OnDoubleClick(object? sender, EventArgs e) => OnOpenWebUI(sender, e);
    private void OnExit(object? sender, EventArgs e) => ExitThread();

    // --- Cleanup ---

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Logger.Info("Application shutting down");
            _globalHotkey?.Dispose();
            _healthCheckTimer?.Dispose();
            _sessionPollTimer?.Dispose();
            _gatewayClient?.Dispose();
            _modernMenu?.Dispose();
            _notifyIcon?.Dispose();
            _contextMenu?.Dispose();
            Logger.Shutdown();
        }
        base.Dispose(disposing);
    }

    protected override void ExitThreadCore()
    {
        if (_notifyIcon != null) _notifyIcon.Visible = false;
        base.ExitThreadCore();
    }
}
