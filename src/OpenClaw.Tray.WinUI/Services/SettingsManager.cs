using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;

namespace OpenClawTray.Services;

/// <summary>
/// Manages application settings with JSON persistence.
/// </summary>
public class SettingsManager
{
    // OPENCLAW_TRAY_DATA_DIR overrides both this and App.DataPath so an isolated test
    // instance can run alongside the user's real tray without clobbering settings.
    private readonly string _settingsDirectory;
    private readonly string _settingsFilePath;
    private const string ProtectedSecretPrefix = "dpapi:";
    private static readonly byte[] ProtectedSecretEntropy = Encoding.UTF8.GetBytes("OpenClawTray.Settings.v1");

    public static string SettingsDirectoryPath => GetDefaultSettingsDirectory();
    public static string SettingsPath => Path.Combine(SettingsDirectoryPath, "settings.json");

    /// <summary>Raised after settings are persisted to disk.</summary>
    public event EventHandler? Saved;

    private readonly object _saveLock = new();

    // Connection
    public string GatewayUrl { get; set; } = "ws://localhost:18789";
    public bool UseSshTunnel { get; set; } = false;
    public string SshTunnelUser { get; set; } = "";
    public string SshTunnelHost { get; set; } = "";
    public int SshTunnelRemotePort { get; set; } = 18789;
    public int SshTunnelLocalPort { get; set; } = 18789;
    public string? LegacyToken { get; private set; }
    public string? LegacyBootstrapToken { get; private set; }
    public bool HasLegacyGatewayCredentials =>
        !string.IsNullOrWhiteSpace(LegacyToken) ||
        !string.IsNullOrWhiteSpace(LegacyBootstrapToken);

    // Startup
    public bool AutoStart { get; set; } = true;
    public bool GlobalHotkeyEnabled { get; set; } = true;
    /// <summary>
    /// One-shot gate: set to true after the post-onboarding "first-run" bootstrap
    /// kickoff message has been injected into the chat exactly once.
    /// </summary>
    public bool HasInjectedFirstRunBootstrap { get; set; } = false;

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
    public bool ScreenRecordingConsentGiven { get; set; } = false;
    public bool CameraRecordingConsentGiven { get; set; } = false;
    public bool NodeLocationEnabled { get; set; } = true;
    public bool NodeBrowserProxyEnabled { get; set; } = true;
    public bool NodeSttEnabled { get; set; } = false;
    /// <summary>STT language: "auto" for Whisper auto-detect, or a BCP-47 tag like "en-US".</summary>
    public string SttLanguage { get; set; } = "auto";
    /// <summary>Whisper model size: "tiny", "base", or "small".</summary>
    public string SttModelName { get; set; } = "base";
    /// <summary>Seconds of silence before auto-submit in voice chat mode.</summary>
    public float SttSilenceTimeout { get; set; } = 1.5f;
    /// <summary>Enable TTS playback of responses during voice sessions.</summary>
    public bool VoiceTtsEnabled { get; set; } = true;
    /// <summary>Play audio feedback chimes on listen start/stop.</summary>
    public bool VoiceAudioFeedback { get; set; } = true;
    public bool NodeTtsEnabled { get; set; } = false;
    public string TtsProvider { get; set; } = TtsCapability.PiperProvider;
    public string TtsElevenLabsApiKey { get; set; } = "";
    public string TtsElevenLabsModel { get; set; } = "";
    public string TtsElevenLabsVoiceId { get; set; } = "";
    public string TtsWindowsVoiceId { get; set; } = "";
    /// <summary>Hub NavigationView pane expanded (true) vs compact (false). Default true.</summary>
    public bool HubNavPaneOpen { get; set; } = true;
    /// <summary>Piper voice identifier, e.g. "en_US-amy-low".</summary>
    public string TtsPiperVoiceId { get; set; } = "en_US-amy-low";
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
    public string? PreferredGatewayId { get; set; }

    // ── MXC sandbox ─────────────────────────────────────────────────────
    /// <summary>Master switch for system.run containment. When true (default), system.run runs sandboxed and is denied if MXC is unavailable. When false, system.run runs on host like before.</summary>
    public bool SystemRunSandboxEnabled { get; set; } = true;
    /// <summary>When sandboxed, allow system.run commands to reach the public internet. Default false.</summary>
    public bool SystemRunAllowOutbound { get; set; } = false;

    // ── MXC sandbox: additional knobs (Sandbox page) ─────────────────
    public SandboxClipboardMode SandboxClipboard { get; set; } = SandboxClipboardMode.None;
    public SandboxFolderAccess? SandboxDocumentsAccess { get; set; }
    public SandboxFolderAccess? SandboxDownloadsAccess { get; set; }
    public SandboxFolderAccess? SandboxDesktopAccess { get; set; }
    public List<SandboxCustomFolder> SandboxCustomFolders { get; set; } = new();
    public int SandboxTimeoutMs { get; set; } = 30_000;
    public long SandboxMaxOutputBytes { get; set; } = 4 * 1024 * 1024;

    public SettingsManager() : this(GetDefaultSettingsDirectory())
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
        LegacyToken = null;
        LegacyBootstrapToken = null;

        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                LoadLegacyGatewayCredentials(json);
                var loaded = SettingsData.FromJson(json);
                if (loaded != null)
                {
                    GatewayUrl = loaded.GatewayUrl ?? GatewayUrl;
                    UseSshTunnel = loaded.UseSshTunnel;
                    SshTunnelUser = loaded.SshTunnelUser ?? SshTunnelUser;
                    SshTunnelHost = loaded.SshTunnelHost ?? SshTunnelHost;
                    SshTunnelRemotePort = loaded.SshTunnelRemotePort <= 0 ? SshTunnelRemotePort : loaded.SshTunnelRemotePort;
                    SshTunnelLocalPort = loaded.SshTunnelLocalPort <= 0 ? SshTunnelLocalPort : loaded.SshTunnelLocalPort;
                    AutoStart = loaded.AutoStart;
                    GlobalHotkeyEnabled = loaded.GlobalHotkeyEnabled;
                    HasInjectedFirstRunBootstrap = loaded.HasInjectedFirstRunBootstrap;
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
                    ScreenRecordingConsentGiven = loaded.ScreenRecordingConsentGiven;
                    CameraRecordingConsentGiven = loaded.CameraRecordingConsentGiven;
                    NodeLocationEnabled = loaded.NodeLocationEnabled;
                    NodeBrowserProxyEnabled = loaded.NodeBrowserProxyEnabled;
                    NodeSttEnabled = loaded.NodeSttEnabled;
                    SttLanguage = string.IsNullOrWhiteSpace(loaded.SttLanguage) ? SttLanguage : loaded.SttLanguage;
                    SttModelName = string.IsNullOrWhiteSpace(loaded.SttModelName) ? SttModelName : loaded.SttModelName;
                    SttSilenceTimeout = loaded.SttSilenceTimeout > 0 ? loaded.SttSilenceTimeout : SttSilenceTimeout;
                    VoiceTtsEnabled = loaded.VoiceTtsEnabled;
                    VoiceAudioFeedback = loaded.VoiceAudioFeedback;
                    NodeTtsEnabled = loaded.NodeTtsEnabled;
                    TtsProvider = string.IsNullOrWhiteSpace(loaded.TtsProvider) ? TtsProvider : loaded.TtsProvider;
                    TtsElevenLabsApiKey = UnprotectSettingSecret(loaded.TtsElevenLabsApiKey) ?? TtsElevenLabsApiKey;
                    TtsElevenLabsModel = loaded.TtsElevenLabsModel ?? TtsElevenLabsModel;
                    TtsElevenLabsVoiceId = loaded.TtsElevenLabsVoiceId ?? TtsElevenLabsVoiceId;
                    TtsWindowsVoiceId = loaded.TtsWindowsVoiceId ?? TtsWindowsVoiceId;
                    HubNavPaneOpen = loaded.HubNavPaneOpen;
                    TtsPiperVoiceId = string.IsNullOrWhiteSpace(loaded.TtsPiperVoiceId) ? TtsPiperVoiceId : loaded.TtsPiperVoiceId;
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
                    PreferredGatewayId = loaded.PreferredGatewayId ?? PreferredGatewayId;
                    NotifyChatResponses = loaded.NotifyChatResponses;
                    PreferStructuredCategories = loaded.PreferStructuredCategories;
                    if (loaded.UserRules != null)
                        UserRules = loaded.UserRules;

                    // MXC sandbox settings
                    SystemRunSandboxEnabled = loaded.SystemRunSandboxEnabled;
                    SystemRunAllowOutbound = loaded.SystemRunAllowOutbound;

                    // MXC sandbox settings (Sandbox page)
                    SandboxClipboard = loaded.SandboxClipboard;
                    SandboxDocumentsAccess = loaded.SandboxDocumentsAccess;
                    SandboxDownloadsAccess = loaded.SandboxDownloadsAccess;
                    SandboxDesktopAccess = loaded.SandboxDesktopAccess;
                    SandboxCustomFolders = loaded.SandboxCustomFolders ?? new();
                    if (loaded.SandboxTimeoutMs > 0)
                        SandboxTimeoutMs = loaded.SandboxTimeoutMs;
                    if (loaded.SandboxMaxOutputBytes > 0)
                        SandboxMaxOutputBytes = loaded.SandboxMaxOutputBytes;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to load settings: {ex.Message}");
            LegacyToken = null;
            LegacyBootstrapToken = null;
        }
    }

    private void LoadLegacyGatewayCredentials(string json)
    {
        LegacyToken = null;
        LegacyBootstrapToken = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            LegacyToken = ReadLegacyString(document.RootElement, "Token");
            LegacyBootstrapToken = ReadLegacyString(document.RootElement, "BootstrapToken");
        }
        catch (JsonException)
        {
            // SettingsData.FromJson handles invalid settings by falling back to defaults.
        }
    }

    private static string? ReadLegacyString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    /// <summary>
    /// Creates a snapshot of current settings as an immutable SettingsData record.
    /// Used for settings change classification (no DPAPI protection applied).
    /// </summary>
    public SettingsData ToSettingsData() => new()
    {
        GatewayUrl = GatewayUrl,
        // Token and BootstrapToken are no longer written — GatewayRegistry is the source of truth
        UseSshTunnel = UseSshTunnel,
        SshTunnelUser = SshTunnelUser,
        SshTunnelHost = SshTunnelHost,
        SshTunnelRemotePort = SshTunnelRemotePort,
        SshTunnelLocalPort = SshTunnelLocalPort,
        AutoStart = AutoStart,
        GlobalHotkeyEnabled = GlobalHotkeyEnabled,
        HasInjectedFirstRunBootstrap = HasInjectedFirstRunBootstrap,
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
        ScreenRecordingConsentGiven = ScreenRecordingConsentGiven,
        CameraRecordingConsentGiven = CameraRecordingConsentGiven,
        NodeLocationEnabled = NodeLocationEnabled,
        NodeBrowserProxyEnabled = NodeBrowserProxyEnabled,
        NodeSttEnabled = NodeSttEnabled,
        SttLanguage = SttLanguage,
        SttModelName = SttModelName,
        SttSilenceTimeout = SttSilenceTimeout,
        VoiceTtsEnabled = VoiceTtsEnabled,
        VoiceAudioFeedback = VoiceAudioFeedback,
        NodeTtsEnabled = NodeTtsEnabled,
        TtsProvider = TtsProvider,
        TtsElevenLabsApiKey = TtsElevenLabsApiKey,
        TtsElevenLabsModel = string.IsNullOrWhiteSpace(TtsElevenLabsModel) ? null : TtsElevenLabsModel,
        TtsElevenLabsVoiceId = string.IsNullOrWhiteSpace(TtsElevenLabsVoiceId) ? null : TtsElevenLabsVoiceId,
        TtsWindowsVoiceId = string.IsNullOrWhiteSpace(TtsWindowsVoiceId) ? null : TtsWindowsVoiceId,
        HubNavPaneOpen = HubNavPaneOpen,
        TtsPiperVoiceId = TtsPiperVoiceId,
        EnableMcpServer = EnableMcpServer,
        A2UIImageHosts = A2UIImageHosts.Count == 0 ? null : new List<string>(A2UIImageHosts),
        HasSeenActivityStreamTip = HasSeenActivityStreamTip,
        SkippedUpdateTag = string.IsNullOrWhiteSpace(SkippedUpdateTag) ? null : SkippedUpdateTag,
        PreferredGatewayId = string.IsNullOrWhiteSpace(PreferredGatewayId) ? null : PreferredGatewayId,
        NotifyChatResponses = NotifyChatResponses,
        PreferStructuredCategories = PreferStructuredCategories,
        UserRules = UserRules,
        // MXC sandbox settings
        SystemRunSandboxEnabled = SystemRunSandboxEnabled,
        SystemRunAllowOutbound = SystemRunAllowOutbound,
        // MXC sandbox settings (Sandbox page)
        SandboxClipboard = SandboxClipboard,
        SandboxDocumentsAccess = SandboxDocumentsAccess,
        SandboxDownloadsAccess = SandboxDownloadsAccess,
        SandboxDesktopAccess = SandboxDesktopAccess,
        SandboxCustomFolders = SandboxCustomFolders.Count == 0 ? null : new List<SandboxCustomFolder>(SandboxCustomFolders),
        SandboxTimeoutMs = SandboxTimeoutMs,
        SandboxMaxOutputBytes = SandboxMaxOutputBytes
    };

    public void Save()
    {
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(_settingsDirectory);
                // Lock the tray data dir to current user + SYSTEM + Administrators —
                // it co-locates the MCP bearer token, settings.json (which embeds
                // gateway/bootstrap credentials), and diagnostics jsonl. Other apps
                // running as the same user could otherwise read these freely.
                OpenClaw.Shared.Mcp.McpAuthToken.TryRestrictDataDirectoryAcl(_settingsDirectory);

                var data = ToSettingsData();
                // Apply DPAPI protection to the API key for on-disk storage only
                data.TtsElevenLabsApiKey = ProtectSettingSecret(data.TtsElevenLabsApiKey);

                var json = data.ToJson();
                File.WriteAllText(_settingsFilePath, json);
                
                Logger.Info("Settings saved");
                Saved?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save settings: {ex.Message}");
            }
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
