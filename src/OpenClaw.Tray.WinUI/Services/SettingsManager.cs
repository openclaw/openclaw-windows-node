using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.Codex;

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
    internal ISettingsFileOperations FileOperations { get; set; } = new SettingsFileOperations();
    private const string ProtectedSecretPrefix = "dpapi:";
    private const int CurrentSettingsSchemaVersion = 1;
    private static readonly byte[] ProtectedSecretEntropy = Encoding.UTF8.GetBytes("OpenClawTray.Settings.v1");
    public const string AppThemeSystem = "System";
    public const string AppThemeLight = "Light";
    public const string AppThemeDark = "Dark";

    public static string SettingsDirectoryPath => GetDefaultSettingsDirectory();
    public static string SettingsPath => Path.Combine(SettingsDirectoryPath, "settings.json");
    public string SettingsDirectory => _settingsDirectory;

    /// <summary>Raised after settings are persisted to disk.</summary>
    public event EventHandler? Saved;

    private readonly object _saveLock = new();
    private SettingsData _data = CreateDefaultData();

    private T ReadData<T>(Func<SettingsData, T> read)
    {
        lock (_saveLock)
            return read(_data);
    }

    private void UpdateData(Func<SettingsData, SettingsData> update)
    {
        lock (_saveLock)
            _data = update(_data);
    }

    // Connection
    public string GatewayUrl { get => ReadData(data => data.GatewayUrl ?? AppIdentity.SetupGatewayUrl); set => UpdateData(data => data with { GatewayUrl = value }); }
    public bool UseSshTunnel { get => ReadData(data => data.UseSshTunnel); set => UpdateData(data => data with { UseSshTunnel = value }); }
    public string SshTunnelUser { get => ReadData(data => data.SshTunnelUser ?? ""); set => UpdateData(data => data with { SshTunnelUser = value }); }
    public string SshTunnelHost { get => ReadData(data => data.SshTunnelHost ?? ""); set => UpdateData(data => data with { SshTunnelHost = value }); }
    public int SshTunnelSshPort { get => ReadData(data => IsValidPort(data.SshTunnelSshPort) ? data.SshTunnelSshPort : 22); set => UpdateData(data => data with { SshTunnelSshPort = value }); }
    public int SshTunnelRemotePort { get => ReadData(data => data.SshTunnelRemotePort <= 0 ? 18789 : data.SshTunnelRemotePort); set => UpdateData(data => data with { SshTunnelRemotePort = value }); }
    public int SshTunnelLocalPort { get => ReadData(data => data.SshTunnelLocalPort <= 0 ? 18789 : data.SshTunnelLocalPort); set => UpdateData(data => data with { SshTunnelLocalPort = value }); }
    /// <inheritdoc cref="SettingsData.BrowserControlPort"/>
    public int? BrowserControlPort { get => ReadData(data => data.BrowserControlPort); set => UpdateData(data => data with { BrowserControlPort = value }); }
    public string? LegacyToken { get; private set; }
    public string? LegacyBootstrapToken { get; private set; }
    public bool HasLegacyGatewayCredentials =>
        !string.IsNullOrWhiteSpace(LegacyToken) ||
        !string.IsNullOrWhiteSpace(LegacyBootstrapToken);

    // Startup
    public bool AutoStart { get => ReadData(data => data.AutoStart); set => UpdateData(data => data with { AutoStart = value }); }
    public bool GlobalHotkeyEnabled { get => ReadData(data => data.GlobalHotkeyEnabled); set => UpdateData(data => data with { GlobalHotkeyEnabled = value }); }
    /// <summary>
    /// One-shot gate: set to true after the post-onboarding "first-run" bootstrap
    /// kickoff message has been injected into the chat exactly once.
    /// </summary>
    public bool HasInjectedFirstRunBootstrap { get => ReadData(data => data.HasInjectedFirstRunBootstrap); set => UpdateData(data => data with { HasInjectedFirstRunBootstrap = value }); }

    // Notifications
    public bool ShowNotifications { get => ReadData(data => data.ShowNotifications); set => UpdateData(data => data with { ShowNotifications = value }); }
    public string NotificationSound { get => ReadData(data => data.NotificationSound ?? "Default"); set => UpdateData(data => data with { NotificationSound = value }); }
    
    // Notification filters
    public bool NotifyHealth { get => ReadData(data => data.NotifyHealth); set => UpdateData(data => data with { NotifyHealth = value }); }
    public bool NotifyUrgent { get => ReadData(data => data.NotifyUrgent); set => UpdateData(data => data with { NotifyUrgent = value }); }
    public bool NotifyReminder { get => ReadData(data => data.NotifyReminder); set => UpdateData(data => data with { NotifyReminder = value }); }
    public bool NotifyEmail { get => ReadData(data => data.NotifyEmail); set => UpdateData(data => data with { NotifyEmail = value }); }
    public bool NotifyCalendar { get => ReadData(data => data.NotifyCalendar); set => UpdateData(data => data with { NotifyCalendar = value }); }
    public bool NotifyBuild { get => ReadData(data => data.NotifyBuild); set => UpdateData(data => data with { NotifyBuild = value }); }
    public bool NotifyStock { get => ReadData(data => data.NotifyStock); set => UpdateData(data => data with { NotifyStock = value }); }
    public bool NotifyInfo { get => ReadData(data => data.NotifyInfo); set => UpdateData(data => data with { NotifyInfo = value }); }

    // Enhanced categorization
    public bool NotifyChatResponses { get => ReadData(data => data.NotifyChatResponses); set => UpdateData(data => data with { NotifyChatResponses = value }); }
    public bool PreferStructuredCategories { get => ReadData(data => data.PreferStructuredCategories); set => UpdateData(data => data with { PreferStructuredCategories = value }); }
    public List<OpenClaw.Shared.UserNotificationRule> UserRules
    {
        get => ReadData(data => data.UserRules ?? []);
        set => UpdateData(data => data with { UserRules = value ?? new() });
    }

    // User interface
    /// <summary>
    /// When true, host the legacy WebView2 gateway chat UI instead of the
    /// native chat surface in both the Hub Chat tab and tray Chat popup.
    /// Default false (native).
    /// </summary>
    public bool UseLegacyWebChat { get => ReadData(data => data.UseLegacyWebChat); set => UpdateData(data => data with { UseLegacyWebChat = value }); }
    public bool ShowCompletedSessions { get => ReadData(data => data.ShowCompletedSessions); set => UpdateData(data => data with { ShowCompletedSessions = value }); }
    public string AppTheme { get => ReadData(data => NormalizeAppTheme(data.AppTheme)); set => UpdateData(data => data with { AppTheme = NormalizeAppTheme(value) }); }
    public bool? ShowDiagnosticsOverride { get => ReadData(data => data.ShowDiagnostics); set => UpdateData(data => data with { ShowDiagnostics = value }); }
    public bool ShowDiagnosticsEffective => ReadData(data => data.ShowDiagnostics ?? OpenClawTray.Helpers.DiagnosticsGate.BuildDefault);
    public string OpenTelemetryEndpoint { get => ReadData(data => data.OpenTelemetryEndpoint ?? ""); set => UpdateData(data => data with { OpenTelemetryEndpoint = NormalizeOptionalString(value) }); }
    public string OpenTelemetryProtocol { get => ReadData(data => OpenTelemetryEndpointProtocol.Normalize(data.OpenTelemetryProtocol)); set => UpdateData(data => data with { OpenTelemetryProtocol = OpenTelemetryEndpointProtocol.Normalize(value) }); }

    // Node mode(gateway WebSocket connection — separate from MCP)
    public bool EnableNodeMode { get => ReadData(data => data.EnableNodeMode); set => UpdateData(data => data with { EnableNodeMode = value }); }
    /// <summary>Master switch for the focused inbound-pairing approval dialog + awareness toast.</summary>
    public bool ShowPairingApprovalDialog { get => ReadData(data => data.ShowPairingApprovalDialog); set => UpdateData(data => data with { ShowPairingApprovalDialog = value }); }
    public bool NodeCanvasEnabled { get => ReadData(data => data.NodeCanvasEnabled); set => UpdateData(data => data with { NodeCanvasEnabled = value }); }
    public bool NodeScreenEnabled { get => ReadData(data => data.NodeScreenEnabled); set => UpdateData(data => data with { NodeScreenEnabled = value }); }
    public bool NodeCameraEnabled { get => ReadData(data => data.NodeCameraEnabled); set => UpdateData(data => data with { NodeCameraEnabled = value }); }
    public bool ScreenRecordingConsentGiven { get => ReadData(data => data.ScreenRecordingConsentGiven); set => UpdateData(data => data with { ScreenRecordingConsentGiven = value }); }
    public bool CameraRecordingConsentGiven { get => ReadData(data => data.CameraRecordingConsentGiven); set => UpdateData(data => data with { CameraRecordingConsentGiven = value }); }
    public bool NodeLocationEnabled { get => ReadData(data => data.NodeLocationEnabled); set => UpdateData(data => data with { NodeLocationEnabled = value }); }
    public bool NodeBrowserProxyEnabled { get => ReadData(data => data.NodeBrowserProxyEnabled); set => UpdateData(data => data with { NodeBrowserProxyEnabled = value }); }
    public CodexSessionAccessMode CodexSessionAccess { get => ReadData(data => data.CodexSessionAccess); set => UpdateData(data => data with { CodexSessionAccess = value }); }
    /// <summary>
    /// Master switch for the <c>system.run</c> / <c>system.run.prepare</c>
    /// commands. Per-command exec approvals still apply when this is on;
    /// flipping it off removes those commands from the declared capability
    /// entirely. Default <c>true</c> (backward compatible).
    /// </summary>
    public bool NodeSystemRunEnabled { get => ReadData(data => data.NodeSystemRunEnabled); set => UpdateData(data => data with { NodeSystemRunEnabled = value }); }
    public bool NodeSttEnabled { get => ReadData(data => data.NodeSttEnabled); set => UpdateData(data => data with { NodeSttEnabled = value }); }
    /// <summary>STT language: "auto" for Whisper auto-detect, or a BCP-47 tag like "en-US".</summary>
    public string SttLanguage { get => ReadData(data => string.IsNullOrWhiteSpace(data.SttLanguage) ? "auto" : data.SttLanguage); set => UpdateData(data => data with { SttLanguage = value }); }
    /// <summary>Whisper model size: "tiny", "base", or "small".</summary>
    public string SttModelName { get => ReadData(data => string.IsNullOrWhiteSpace(data.SttModelName) ? "base" : data.SttModelName); set => UpdateData(data => data with { SttModelName = value }); }
    /// <summary>Seconds of silence before auto-submit in voice chat mode.</summary>
    public float SttSilenceTimeout { get => ReadData(data => data.SttSilenceTimeout > 0 ? data.SttSilenceTimeout : 1.5f); set => UpdateData(data => data with { SttSilenceTimeout = value }); }
    /// <summary>Enable TTS playback of responses during voice sessions.</summary>
    public bool VoiceTtsEnabled { get => ReadData(data => data.VoiceTtsEnabled); set => UpdateData(data => data with { VoiceTtsEnabled = value }); }
    /// <summary>Show tool-call and usage chips inline in the chat timeline.</summary>
    public bool ShowChatToolCalls { get => ReadData(data => data.ShowChatToolCalls); set => UpdateData(data => data with { ShowChatToolCalls = value }); }
    /// <summary>Play audio feedback chimes on listen start/stop.</summary>
    public bool VoiceAudioFeedback { get => ReadData(data => data.VoiceAudioFeedback); set => UpdateData(data => data with { VoiceAudioFeedback = value }); }
    public bool NodeTtsEnabled { get => ReadData(data => data.NodeTtsEnabled); set => UpdateData(data => data with { NodeTtsEnabled = value }); }
    public string TtsProvider { get => ReadData(data => string.IsNullOrWhiteSpace(data.TtsProvider) ? TtsCapability.PiperProvider : data.TtsProvider); set => UpdateData(data => data with { TtsProvider = value }); }
    public string TtsElevenLabsApiKey { get => ReadData(data => data.TtsElevenLabsApiKey ?? ""); set => UpdateData(data => data with { TtsElevenLabsApiKey = value }); }
    public string TtsElevenLabsModel { get => ReadData(data => data.TtsElevenLabsModel ?? ""); set => UpdateData(data => data with { TtsElevenLabsModel = value }); }
    public string TtsElevenLabsVoiceId { get => ReadData(data => data.TtsElevenLabsVoiceId ?? ""); set => UpdateData(data => data with { TtsElevenLabsVoiceId = value }); }
    public string TtsWindowsVoiceId { get => ReadData(data => data.TtsWindowsVoiceId ?? ""); set => UpdateData(data => data with { TtsWindowsVoiceId = value }); }
    /// <summary>Hub NavigationView pane expanded (true) vs compact (false). Default true.</summary>
    public bool HubNavPaneOpen { get => ReadData(data => data.HubNavPaneOpen); set => UpdateData(data => data with { HubNavPaneOpen = value }); }
    /// <summary>Piper voice identifier, e.g. "en_US-amy-low".</summary>
    public string TtsPiperVoiceId { get => ReadData(data => string.IsNullOrWhiteSpace(data.TtsPiperVoiceId) ? "en_US-amy-low" : data.TtsPiperVoiceId); set => UpdateData(data => data with { TtsPiperVoiceId = value }); }
    // Local MCP HTTP server (independent of EnableNodeMode)
    public bool EnableMcpServer { get => ReadData(data => data.EnableMcpServer); set => UpdateData(data => data with { EnableMcpServer = value }); }
    // Automatic self-repair of app-owned setup-managed local WSL gateways (kill switch).
    public bool EnableManagedLocalGatewayAutoRepair { get => ReadData(data => data.EnableManagedLocalGatewayAutoRepair); set => UpdateData(data => data with { EnableManagedLocalGatewayAutoRepair = value }); }
    /// <summary>
    /// Hostnames the A2UI image renderer is allowed to fetch over HTTPS.
    /// Empty by default — agents can still ship inline data: images. The
    /// runtime never bypasses this list, so it is the single switch keeping
    /// agent JSON from issuing arbitrary outbound HTTP from the tray process.
    /// </summary>
    public List<string> A2UIImageHosts
    {
        get => ReadData(data => data.A2UIImageHosts ?? []);
        set => UpdateData(data => data with { A2UIImageHosts = value ?? new() });
    }
    public bool HasSeenActivityStreamTip { get => ReadData(data => data.HasSeenActivityStreamTip); set => UpdateData(data => data with { HasSeenActivityStreamTip = value }); }
    public string SkippedUpdateTag { get => ReadData(data => data.SkippedUpdateTag ?? ""); set => UpdateData(data => data with { SkippedUpdateTag = value }); }
    public string? PreferredGatewayId { get => ReadData(data => data.PreferredGatewayId); set => UpdateData(data => data with { PreferredGatewayId = value }); }

    // ── MXC sandbox ─────────────────────────────────────────────────────
    /// <summary>Master switch for system.run containment. When true (default), system.run uses MXC when available and falls back to host execution when unavailable unless strict fallback blocking is enabled. When false, system.run runs on host like before.</summary>
    public bool SystemRunSandboxEnabled { get => ReadData(data => data.SystemRunSandboxEnabled); set => UpdateData(data => data with { SystemRunSandboxEnabled = value }); }
    /// <summary>When true, sandbox-enabled system.run blocks instead of using the compatibility host fallback if MXC is unavailable. Default false.</summary>
    public bool SystemRunBlockHostFallbackWhenMxcUnavailable { get => ReadData(data => data.SystemRunBlockHostFallbackWhenMxcUnavailable); set => UpdateData(data => data with { SystemRunBlockHostFallbackWhenMxcUnavailable = value }); }
    /// <summary>When sandboxed, allow system.run commands to reach the public internet. Default false.</summary>
    public bool SystemRunAllowOutbound { get => ReadData(data => data.SystemRunAllowOutbound); set => UpdateData(data => data with { SystemRunAllowOutbound = value }); }
    // ── MXC sandbox: additional knobs (Sandbox page) ─────────────────
    public SandboxClipboardMode SandboxClipboard { get => ReadData(data => data.SandboxClipboard); set => UpdateData(data => data with { SandboxClipboard = value }); }
    public SandboxFolderAccess? SandboxDocumentsAccess { get => ReadData(data => data.SandboxDocumentsAccess); set => UpdateData(data => data with { SandboxDocumentsAccess = value }); }
    public SandboxFolderAccess? SandboxDownloadsAccess { get => ReadData(data => data.SandboxDownloadsAccess); set => UpdateData(data => data with { SandboxDownloadsAccess = value }); }
    public SandboxFolderAccess? SandboxDesktopAccess { get => ReadData(data => data.SandboxDesktopAccess); set => UpdateData(data => data with { SandboxDesktopAccess = value }); }
    public List<SandboxCustomFolder> SandboxCustomFolders
    {
        get => ReadData(data => data.SandboxCustomFolders ?? []);
        set => UpdateData(data => data with { SandboxCustomFolders = value ?? new() });
    }
    public int SandboxTimeoutMs { get => ReadData(data => data.SandboxTimeoutMs > 0 ? data.SandboxTimeoutMs : 30_000); set => UpdateData(data => data with { SandboxTimeoutMs = value }); }
    public long SandboxMaxOutputBytes { get => ReadData(data => data.SandboxMaxOutputBytes > 0 ? data.SandboxMaxOutputBytes : 4 * 1024 * 1024); set => UpdateData(data => data with { SandboxMaxOutputBytes = value }); }

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
        return AppIdentity.ResolveRoamingDataDirectory();
    }

    public void Load()
    {
        lock (_saveLock)
            LoadCore();
    }

    private void LoadCore()
    {
        LegacyToken = null;
        LegacyBootstrapToken = null;
        _data = CreateDefaultData();

        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                LoadLegacyGatewayCredentials(json);
                var loaded = SettingsData.FromJson(json);
                if (loaded != null)
                {
                    _data = NormalizeLoadedData(loaded, json);
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

    private static SettingsData CreateDefaultData() => new()
    {
        SettingsSchemaVersion = CurrentSettingsSchemaVersion,
        GatewayUrl = AppIdentity.SetupGatewayUrl,
        UseSshTunnel = false,
        SshTunnelUser = "",
        SshTunnelHost = "",
        SshTunnelSshPort = 22,
        SshTunnelRemotePort = 18789,
        SshTunnelLocalPort = 18789,
        AutoStart = true,
        GlobalHotkeyEnabled = true,
        HasInjectedFirstRunBootstrap = false,
        ShowNotifications = true,
        NotificationSound = "Default",
        NotifyHealth = true,
        NotifyUrgent = true,
        NotifyReminder = true,
        NotifyEmail = true,
        NotifyCalendar = true,
        NotifyBuild = true,
        NotifyStock = true,
        NotifyInfo = true,
        NotifyChatResponses = true,
        PreferStructuredCategories = true,
        UserRules = new(),
        UseLegacyWebChat = false,
        ShowCompletedSessions = false,
        AppTheme = AppThemeSystem,
        OpenTelemetryEndpoint = null,
        OpenTelemetryProtocol = OpenTelemetryEndpointProtocol.Grpc,
        EnableNodeMode = false,
        NodeCanvasEnabled = true,
        NodeScreenEnabled = true,
        NodeCameraEnabled = true,
        ScreenRecordingConsentGiven = false,
        CameraRecordingConsentGiven = false,
        NodeLocationEnabled = true,
        NodeBrowserProxyEnabled = true,
        CodexSessionAccess = CodexSessionAccessMode.Off,
        NodeSystemRunEnabled = true,
        NodeSttEnabled = false,
        SttLanguage = "auto",
        SttModelName = "base",
        SttSilenceTimeout = 1.5f,
        VoiceTtsEnabled = true,
        VoiceAudioFeedback = true,
        NodeTtsEnabled = false,
        TtsProvider = TtsCapability.PiperProvider,
        TtsElevenLabsApiKey = "",
        TtsElevenLabsModel = "",
        TtsElevenLabsVoiceId = "",
        TtsWindowsVoiceId = "",
        HubNavPaneOpen = true,
        TtsPiperVoiceId = "en_US-amy-low",
        EnableMcpServer = false,
        A2UIImageHosts = new(),
        HasSeenActivityStreamTip = false,
        SkippedUpdateTag = "",
        PreferredGatewayId = null,
        SystemRunSandboxEnabled = true,
        SystemRunBlockHostFallbackWhenMxcUnavailable = false,
        SystemRunAllowOutbound = false,
        SandboxClipboard = SandboxClipboardMode.None,
        SandboxDocumentsAccess = null,
        SandboxDownloadsAccess = null,
        SandboxDesktopAccess = null,
        SandboxCustomFolders = new(),
        SandboxTimeoutMs = 30_000,
        SandboxMaxOutputBytes = 4 * 1024 * 1024
    };

    private static SettingsData NormalizeLoadedData(SettingsData loaded, string? rawJson = null)
    {
        var defaults = CreateDefaultData();
        var data = loaded with
        {
            SettingsSchemaVersion = CurrentSettingsSchemaVersion,
            GatewayUrl = loaded.GatewayUrl ?? defaults.GatewayUrl,
            SshTunnelUser = loaded.SshTunnelUser ?? defaults.SshTunnelUser,
            SshTunnelHost = loaded.SshTunnelHost ?? defaults.SshTunnelHost,
            SshTunnelSshPort = IsValidPort(loaded.SshTunnelSshPort) ? loaded.SshTunnelSshPort : defaults.SshTunnelSshPort,
            SshTunnelRemotePort = loaded.SshTunnelRemotePort <= 0 ? defaults.SshTunnelRemotePort : loaded.SshTunnelRemotePort,
            SshTunnelLocalPort = loaded.SshTunnelLocalPort <= 0 ? defaults.SshTunnelLocalPort : loaded.SshTunnelLocalPort,
            NotificationSound = loaded.NotificationSound ?? defaults.NotificationSound,
            SttLanguage = string.IsNullOrWhiteSpace(loaded.SttLanguage) ? defaults.SttLanguage : loaded.SttLanguage,
            SttModelName = string.IsNullOrWhiteSpace(loaded.SttModelName) ? defaults.SttModelName : loaded.SttModelName,
            SttSilenceTimeout = loaded.SttSilenceTimeout > 0 ? loaded.SttSilenceTimeout : defaults.SttSilenceTimeout,
            TtsProvider = string.IsNullOrWhiteSpace(loaded.TtsProvider) ? defaults.TtsProvider : loaded.TtsProvider,
            TtsElevenLabsApiKey = UnprotectSettingSecret(loaded.TtsElevenLabsApiKey) ?? defaults.TtsElevenLabsApiKey,
            TtsElevenLabsModel = loaded.TtsElevenLabsModel ?? defaults.TtsElevenLabsModel,
            TtsElevenLabsVoiceId = loaded.TtsElevenLabsVoiceId ?? defaults.TtsElevenLabsVoiceId,
            TtsWindowsVoiceId = loaded.TtsWindowsVoiceId ?? defaults.TtsWindowsVoiceId,
            TtsPiperVoiceId = string.IsNullOrWhiteSpace(loaded.TtsPiperVoiceId) ? defaults.TtsPiperVoiceId : loaded.TtsPiperVoiceId,
            A2UIImageHosts = loaded.A2UIImageHosts is { Count: > 0 } hosts ? new List<string>(hosts) : new(),
            SkippedUpdateTag = loaded.SkippedUpdateTag ?? defaults.SkippedUpdateTag,
            PreferredGatewayId = loaded.PreferredGatewayId ?? defaults.PreferredGatewayId,
            AppTheme = NormalizeAppTheme(loaded.AppTheme),
            ShowDiagnostics = loaded.ShowDiagnostics,
            OpenTelemetryEndpoint = NormalizeOptionalString(loaded.OpenTelemetryEndpoint),
            OpenTelemetryProtocol = OpenTelemetryEndpointProtocol.Normalize(loaded.OpenTelemetryProtocol),
            UserRules = loaded.UserRules != null ? new List<UserNotificationRule>(loaded.UserRules) : new(),
            SandboxCustomFolders = CloneSandboxCustomFolders(loaded.SandboxCustomFolders),
            SystemRunBlockHostFallbackWhenMxcUnavailable = loaded.SystemRunBlockHostFallbackWhenMxcUnavailable,
            SandboxTimeoutMs = loaded.SandboxTimeoutMs > 0 ? loaded.SandboxTimeoutMs : defaults.SandboxTimeoutMs,
            SandboxMaxOutputBytes = loaded.SandboxMaxOutputBytes > 0 ? loaded.SandboxMaxOutputBytes : defaults.SandboxMaxOutputBytes,
            McpOnlyMode = null
        };

        // Legacy McpOnlyMode migration:
        //   true  -> node off (no gateway), MCP on
        //   false -> leave MCP off; the user has not opted in to a local HTTP server.
        if (loaded.McpOnlyMode is true)
        {
            data = data with
            {
                EnableMcpServer = true,
                EnableNodeMode = false
            };
        }

        return data;
    }

    private static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    private static List<SandboxCustomFolder> CloneSandboxCustomFolders(IEnumerable<SandboxCustomFolder>? folders) =>
        folders is null
            ? new List<SandboxCustomFolder>()
            : folders
                .Select(folder => new SandboxCustomFolder
                {
                    Path = folder.Path,
                    Access = folder.Access
                })
                .ToList();

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
        // slopwatch-ignore: SW003 Optional persisted state fallback is intentional; caller continues with defaults or prior state.
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
    /// Creates a detached snapshot of current settings. No DPAPI protection is
    /// applied here; Save applies it to a second clone for on-disk storage only.
    /// </summary>
    public SettingsData ToSettingsData() => ReadData(data => data with
    {
        GatewayUrl = GatewayUrl,
        SshTunnelUser = SshTunnelUser,
        SshTunnelHost = SshTunnelHost,
        SshTunnelRemotePort = SshTunnelRemotePort,
        SshTunnelLocalPort = SshTunnelLocalPort,
        NotificationSound = NotificationSound,
        SttLanguage = SttLanguage,
        SttModelName = SttModelName,
        SttSilenceTimeout = SttSilenceTimeout,
        TtsProvider = TtsProvider,
        TtsElevenLabsApiKey = TtsElevenLabsApiKey,
        TtsElevenLabsModel = string.IsNullOrWhiteSpace(TtsElevenLabsModel) ? null : TtsElevenLabsModel,
        TtsElevenLabsVoiceId = string.IsNullOrWhiteSpace(TtsElevenLabsVoiceId) ? null : TtsElevenLabsVoiceId,
        TtsWindowsVoiceId = string.IsNullOrWhiteSpace(TtsWindowsVoiceId) ? null : TtsWindowsVoiceId,
        TtsPiperVoiceId = TtsPiperVoiceId,
        AppTheme = AppTheme,
        ShowDiagnostics = ShowDiagnosticsOverride,
        OpenTelemetryEndpoint = NormalizeOptionalString(OpenTelemetryEndpoint),
        OpenTelemetryProtocol = OpenTelemetryEndpointProtocol.Normalize(OpenTelemetryProtocol),
        A2UIImageHosts = A2UIImageHosts.Count == 0 ? null : new List<string>(A2UIImageHosts),
        SkippedUpdateTag = string.IsNullOrWhiteSpace(SkippedUpdateTag) ? null : SkippedUpdateTag,
        PreferredGatewayId = string.IsNullOrWhiteSpace(PreferredGatewayId) ? null : PreferredGatewayId,
        UserRules = new List<UserNotificationRule>(UserRules),
        SandboxCustomFolders = SandboxCustomFolders.Count == 0 ? null : CloneSandboxCustomFolders(SandboxCustomFolders),
        SandboxTimeoutMs = SandboxTimeoutMs,
        SandboxMaxOutputBytes = SandboxMaxOutputBytes,
        McpOnlyMode = null
    });

    public static string NormalizeAppTheme(string? value)
    {
        if (string.Equals(value, AppThemeLight, StringComparison.OrdinalIgnoreCase))
            return AppThemeLight;
        if (string.Equals(value, AppThemeDark, StringComparison.OrdinalIgnoreCase))
            return AppThemeDark;
        return AppThemeSystem;
    }

    private static string? NormalizeOptionalString(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Save()
    {
        try
        {
            SaveOrThrow();
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to save settings: {ex.Message}");
        }
    }

    internal void SaveOrThrow()
    {
        lock (_saveLock)
        {
            SaveOrThrowCore();
        }
    }

    internal bool UpdateAndSave(Action<SettingsManager> update) =>
        UpdateAndSave(update, rollbackOnFailure: false);

    internal bool TryUpdateAndSave(Action<SettingsManager> update) =>
        UpdateAndSave(update, rollbackOnFailure: true);

    private bool UpdateAndSave(Action<SettingsManager> update, bool rollbackOnFailure)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_saveLock)
        {
            var previousData = _data;
            update(this);
            try
            {
                SaveOrThrowCore();
                return true;
            }
            catch (Exception ex)
            {
                if (rollbackOnFailure)
                    _data = previousData;
                Logger.Error($"Failed to save settings: {ex.Message}");
                return false;
            }
        }
    }

    internal T ReadLocked<T>(Func<SettingsManager, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_saveLock)
        {
            return read(this);
        }
    }

    private void SaveOrThrowCore()
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
        WriteSettingsAtomically(json);

        Logger.Info("Settings saved");
        try
        {
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Settings saved, but a notification subscriber failed: {ex.Message}");
        }
    }

    private void WriteSettingsAtomically(string json)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var tempPath = Path.Combine(_settingsDirectory, $"settings.{suffix}.tmp");
        var backupPath = Path.Combine(_settingsDirectory, $"settings.{suffix}.backup");
        try
        {
            FileOperations.WriteAllText(tempPath, json);
            if (FileOperations.Exists(_settingsFilePath))
                FileOperations.Replace(tempPath, _settingsFilePath, backupPath);
            else
                FileOperations.Move(tempPath, _settingsFilePath);
        }
        finally
        {
            TryDeleteSettingsArtifact(tempPath);
            TryDeleteSettingsArtifact(backupPath);
        }
    }

    private void TryDeleteSettingsArtifact(string path)
    {
        try
        {
            if (FileOperations.Exists(path))
                FileOperations.Delete(path);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to remove settings persistence artifact: {ex.Message}");
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

    internal static bool CanProtectSettingSecretsForCurrentUser()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var bytes = Encoding.UTF8.GetBytes("openclaw-dpapi-probe");
            var protectedBytes = ProtectedData.Protect(bytes, ProtectedSecretEntropy, DataProtectionScope.CurrentUser);
            var unprotectedBytes = ProtectedData.Unprotect(protectedBytes, ProtectedSecretEntropy, DataProtectionScope.CurrentUser);
            return bytes.SequenceEqual(unprotectedBytes);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
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

internal interface ISettingsFileOperations
{
    bool Exists(string path);
    void WriteAllText(string path, string contents);
    void Replace(string source, string destination, string backup);
    void Move(string source, string destination);
    void Delete(string path);
}

internal sealed class SettingsFileOperations : ISettingsFileOperations
{
    public bool Exists(string path) => File.Exists(path);
    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);
    public void Replace(string source, string destination, string backup) =>
        File.Replace(source, destination, backup, ignoreMetadataErrors: true);
    public void Move(string source, string destination) => File.Move(source, destination);
    public void Delete(string path) => File.Delete(path);
}
