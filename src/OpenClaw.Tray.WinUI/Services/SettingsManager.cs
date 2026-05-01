using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// Manages application settings with JSON persistence.
/// </summary>
public class SettingsManager
{
    // OPENCLAW_TRAY_DATA_DIR overrides both this and App.DataPath so an isolated test
    // instance can run alongside the user's real tray without clobbering settings.
    private static readonly string DefaultSettingsDirectory = GetDefaultSettingsDirectory();
    private static readonly string DefaultSettingsFilePath = Path.Combine(DefaultSettingsDirectory, "settings.json");

    private readonly string _settingsDirectory;
    private readonly string _settingsFilePath;
    private const string ProtectedSecretPrefix = "dpapi:";
    private static readonly byte[] ProtectedSecretEntropy = Encoding.UTF8.GetBytes("OpenClawTray.Settings.v1");

    public static string SettingsDirectoryPath => DefaultSettingsDirectory;
    public static string SettingsPath => DefaultSettingsFilePath;

    // Connection
    public string GatewayUrl { get; set; } = "ws://localhost:18789";
    public string Token { get; set; } = "";
    public string BootstrapToken { get; set; } = "";
    public bool UseSshTunnel { get; set; } = false;
    public string SshTunnelUser { get; set; } = "";
    public string SshTunnelHost { get; set; } = "";
    public int SshTunnelRemotePort { get; set; } = 18789;
    public int SshTunnelLocalPort { get; set; } = 18789;

    // Startup
    public bool AutoStart { get; set; } = false;
    public bool GlobalHotkeyEnabled { get; set; } = true;

    // Notifications
    public bool ShowNotifications { get; set; } = true;
    public string NotificationSound { get; set; } = "Default";
    
    // Notification filters
    public bool NotifyHealth { get; set; } = true;
    public bool NotifyUrgent { get; set; } = true;
    public bool NotifyReminder { get; set; } = true;
    public bool NotifyEmail { get; set; } = true;
    public bool NotifyCalendar { get; set; } = true;
    public bool NotifyBuild { get; set; } = true;
    public bool NotifyStock { get; set; } = true;
    public bool NotifyInfo { get; set; } = true;

    // Enhanced categorization
    public bool NotifyChatResponses { get; set; } = true;
    public bool PreferStructuredCategories { get; set; } = true;
    public List<OpenClaw.Shared.UserNotificationRule> UserRules { get; set; } = new();
    
    // Node mode (gateway WebSocket connection — separate from MCP)
    public bool EnableNodeMode { get; set; } = false;
    public bool NodeCanvasEnabled { get; set; } = true;
    public bool NodeScreenEnabled { get; set; } = true;
    public bool NodeCameraEnabled { get; set; } = true;
    public bool NodeLocationEnabled { get; set; } = true;
    public bool NodeBrowserProxyEnabled { get; set; } = true;
    public bool NodeTtsEnabled { get; set; } = false;
    public string TtsProvider { get; set; } = "windows";
    public string TtsElevenLabsApiKey { get; set; } = "";
    public string TtsElevenLabsModel { get; set; } = "";
    public string TtsElevenLabsVoiceId { get; set; } = "";
    // Local MCP HTTP server (independent of EnableNodeMode)
    public bool EnableMcpServer { get; set; } = false;
    /// <summary>
    /// Hostnames the A2UI image renderer is allowed to fetch over HTTPS.
    /// Empty by default — agents can still ship inline data: images. The
    /// runtime never bypasses this list, so it is the single switch keeping
    /// agent JSON from issuing arbitrary outbound HTTP from the tray process.
    /// </summary>
    public List<string> A2UIImageHosts { get; set; } = new();
    public bool HasSeenActivityStreamTip { get; set; } = false;
    public string SkippedUpdateTag { get; set; } = "";

    public SettingsManager() : this(DefaultSettingsDirectory)
    {
    }

    public SettingsManager(string settingsDirectory)
    {
        if (string.IsNullOrWhiteSpace(settingsDirectory))
            throw new ArgumentException("Settings directory cannot be empty.", nameof(settingsDirectory));

        _settingsDirectory = settingsDirectory;
        _settingsFilePath = Path.Combine(settingsDirectory, "settings.json");
        Load();
    }

    private static string GetDefaultSettingsDirectory()
    {
        return Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") is { Length: > 0 } overrideDir
            ? overrideDir
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OpenClawTray");
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var loaded = SettingsData.FromJson(json);
                if (loaded != null)
                {
                    GatewayUrl = loaded.GatewayUrl ?? GatewayUrl;
                    Token = loaded.Token ?? Token;
                    BootstrapToken = loaded.BootstrapToken ?? BootstrapToken;
                    UseSshTunnel = loaded.UseSshTunnel;
                    SshTunnelUser = loaded.SshTunnelUser ?? SshTunnelUser;
                    SshTunnelHost = loaded.SshTunnelHost ?? SshTunnelHost;
                    SshTunnelRemotePort = loaded.SshTunnelRemotePort <= 0 ? SshTunnelRemotePort : loaded.SshTunnelRemotePort;
                    SshTunnelLocalPort = loaded.SshTunnelLocalPort <= 0 ? SshTunnelLocalPort : loaded.SshTunnelLocalPort;
                    AutoStart = loaded.AutoStart;
                    GlobalHotkeyEnabled = loaded.GlobalHotkeyEnabled;
                    ShowNotifications = loaded.ShowNotifications;
                    NotificationSound = loaded.NotificationSound ?? NotificationSound;
                    NotifyHealth = loaded.NotifyHealth;
                    NotifyUrgent = loaded.NotifyUrgent;
                    NotifyReminder = loaded.NotifyReminder;
                    NotifyEmail = loaded.NotifyEmail;
                    NotifyCalendar = loaded.NotifyCalendar;
                    NotifyBuild = loaded.NotifyBuild;
                    NotifyStock = loaded.NotifyStock;
                    NotifyInfo = loaded.NotifyInfo;
                    EnableNodeMode = loaded.EnableNodeMode;
                    NodeCanvasEnabled = loaded.NodeCanvasEnabled;
                    NodeScreenEnabled = loaded.NodeScreenEnabled;
                    NodeCameraEnabled = loaded.NodeCameraEnabled;
                    NodeLocationEnabled = loaded.NodeLocationEnabled;
                    NodeBrowserProxyEnabled = loaded.NodeBrowserProxyEnabled;
                    NodeTtsEnabled = loaded.NodeTtsEnabled;
                    TtsProvider = string.IsNullOrWhiteSpace(loaded.TtsProvider) ? TtsProvider : loaded.TtsProvider;
                    TtsElevenLabsApiKey = UnprotectSettingSecret(loaded.TtsElevenLabsApiKey) ?? TtsElevenLabsApiKey;
                    TtsElevenLabsModel = loaded.TtsElevenLabsModel ?? TtsElevenLabsModel;
                    TtsElevenLabsVoiceId = loaded.TtsElevenLabsVoiceId ?? TtsElevenLabsVoiceId;
                    EnableMcpServer = loaded.EnableMcpServer;
                    A2UIImageHosts = loaded.A2UIImageHosts ?? new List<string>();
                    // Legacy McpOnlyMode migration:
                    //   true  → node off (no gateway), MCP on
                    //   false → leave MCP off; the user has not opted in to a
                    //           local HTTP server. Earlier dev builds tied MCP
                    //           to EnableNodeMode silently — we deliberately
                    //           do *not* re-enable MCP for those users on
                    //           upgrade. They can flip the toggle in Settings.
                    if (loaded.McpOnlyMode is bool legacyMcpOnly && legacyMcpOnly)
                    {
                        EnableMcpServer = true;
                        EnableNodeMode = false;
                    }
                    HasSeenActivityStreamTip = loaded.HasSeenActivityStreamTip;
                    SkippedUpdateTag = loaded.SkippedUpdateTag ?? SkippedUpdateTag;
                    NotifyChatResponses = loaded.NotifyChatResponses;
                    PreferStructuredCategories = loaded.PreferStructuredCategories;
                    if (loaded.UserRules != null)
                        UserRules = loaded.UserRules;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to load settings: {ex.Message}");
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(_settingsDirectory);
            // Lock the tray data dir to current user + SYSTEM + Administrators —
            // it co-locates the MCP bearer token, settings.json (which embeds
            // gateway/bootstrap credentials), and diagnostics jsonl. Other apps
            // running as the same user could otherwise read these freely.
            OpenClaw.Shared.Mcp.McpAuthToken.TryRestrictDataDirectoryAcl(_settingsDirectory);

            var data = new SettingsData
            {
                GatewayUrl = GatewayUrl,
                Token = Token,
                BootstrapToken = string.IsNullOrWhiteSpace(BootstrapToken) ? null : BootstrapToken,
                UseSshTunnel = UseSshTunnel,
                SshTunnelUser = SshTunnelUser,
                SshTunnelHost = SshTunnelHost,
                SshTunnelRemotePort = SshTunnelRemotePort,
                SshTunnelLocalPort = SshTunnelLocalPort,
                AutoStart = AutoStart,
                GlobalHotkeyEnabled = GlobalHotkeyEnabled,
                ShowNotifications = ShowNotifications,
                NotificationSound = NotificationSound,
                NotifyHealth = NotifyHealth,
                NotifyUrgent = NotifyUrgent,
                NotifyReminder = NotifyReminder,
                NotifyEmail = NotifyEmail,
                NotifyCalendar = NotifyCalendar,
                NotifyBuild = NotifyBuild,
                NotifyStock = NotifyStock,
                NotifyInfo = NotifyInfo,
                EnableNodeMode = EnableNodeMode,
                NodeCanvasEnabled = NodeCanvasEnabled,
                NodeScreenEnabled = NodeScreenEnabled,
                NodeCameraEnabled = NodeCameraEnabled,
                NodeLocationEnabled = NodeLocationEnabled,
                NodeBrowserProxyEnabled = NodeBrowserProxyEnabled,
                NodeTtsEnabled = NodeTtsEnabled,
                TtsProvider = TtsProvider,
                TtsElevenLabsApiKey = ProtectSettingSecret(TtsElevenLabsApiKey),
                TtsElevenLabsModel = string.IsNullOrWhiteSpace(TtsElevenLabsModel) ? null : TtsElevenLabsModel,
                TtsElevenLabsVoiceId = string.IsNullOrWhiteSpace(TtsElevenLabsVoiceId) ? null : TtsElevenLabsVoiceId,
                EnableMcpServer = EnableMcpServer,
                A2UIImageHosts = A2UIImageHosts.Count == 0 ? null : new List<string>(A2UIImageHosts),
                // McpOnlyMode is legacy — never written; remains null in serialized output.
                HasSeenActivityStreamTip = HasSeenActivityStreamTip,
                SkippedUpdateTag = string.IsNullOrWhiteSpace(SkippedUpdateTag) ? null : SkippedUpdateTag,
                NotifyChatResponses = NotifyChatResponses,
                PreferStructuredCategories = PreferStructuredCategories,
                UserRules = UserRules
            };

            var json = data.ToJson();
            File.WriteAllText(_settingsFilePath, json);
            
            Logger.Info("Settings saved");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to save settings: {ex.Message}");
        }
    }

    internal static string? ProtectSettingSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Data Protection API is required for protected settings secrets.");

        var bytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(bytes, ProtectedSecretEntropy, DataProtectionScope.CurrentUser);
        return ProtectedSecretPrefix + Convert.ToBase64String(protectedBytes);
    }

    internal static string? UnprotectSettingSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;
        if (!value.StartsWith(ProtectedSecretPrefix, StringComparison.Ordinal))
            return value;

        if (!OperatingSystem.IsWindows())
        {
            Logger.Warn("Failed to decrypt protected settings secret: Windows Data Protection API is unavailable.");
            return null;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(value[ProtectedSecretPrefix.Length..]);
            var bytes = ProtectedData.Unprotect(protectedBytes, ProtectedSecretEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException ex)
        {
            Logger.Warn($"Failed to decode protected settings secret: {ex.Message}");
            return null;
        }
        catch (CryptographicException ex)
        {
            Logger.Warn($"Failed to decrypt protected settings secret: {ex.Message}");
            return null;
        }
        catch (NotSupportedException ex)
        {
            Logger.Warn($"Failed to decrypt protected settings secret: {ex.Message}");
            return null;
        }
        catch (ArgumentException ex)
        {
            Logger.Warn($"Failed to decrypt protected settings secret: {ex.Message}");
            return null;
        }
    }

    public string GetEffectiveGatewayUrl()
    {
        if (!UseSshTunnel)
        {
            return GatewayUrl;
        }

        return $"ws://127.0.0.1:{SshTunnelLocalPort}";
    }
}
