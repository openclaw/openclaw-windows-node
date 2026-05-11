using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared;

/// <summary>
/// Serializable settings data model. Used for JSON round-trip persistence.
/// </summary>
public class SettingsData
{
    public string? GatewayUrl { get; set; }
    public bool UseSshTunnel { get; set; } = false;
    public string? SshTunnelUser { get; set; }
    public string? SshTunnelHost { get; set; }
    public int SshTunnelRemotePort { get; set; } = 18789;
    public int SshTunnelLocalPort { get; set; } = 18789;
    public bool AutoStart { get; set; } = true;
    public bool GlobalHotkeyEnabled { get; set; } = true;
    /// <summary>
    /// One-shot gate: set to true after the post-onboarding "first-run" bootstrap
    /// kickoff message has been injected into the chat exactly once. Subsequent
    /// chat-window launches skip injection.
    /// </summary>
    public bool HasInjectedFirstRunBootstrap { get; set; }
    public bool ShowNotifications { get; set; } = true;
    public string? NotificationSound { get; set; }
    public bool NotifyHealth { get; set; } = true;
    public bool NotifyUrgent { get; set; } = true;
    public bool NotifyReminder { get; set; } = true;
    public bool NotifyEmail { get; set; } = true;
    public bool NotifyCalendar { get; set; } = true;
    public bool NotifyBuild { get; set; } = true;
    public bool NotifyStock { get; set; } = true;
    public bool NotifyInfo { get; set; } = true;
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
    /// <summary>Whisper model name: "tiny", "base", or "small".</summary>
    public string SttModelName { get; set; } = "base";
    /// <summary>Seconds of silence before auto-submit in voice chat mode.</summary>
    public float SttSilenceTimeout { get; set; } = 2.5f;
    /// <summary>Enable TTS playback of responses during voice sessions.</summary>
    public bool VoiceTtsEnabled { get; set; } = true;
    /// <summary>Play audio feedback chimes on listen start/stop.</summary>
    public bool VoiceAudioFeedback { get; set; } = true;
    public bool NodeTtsEnabled { get; set; } = false;
    public string TtsProvider { get; set; } = OpenClaw.Shared.Capabilities.TtsCapability.PiperProvider;
    /// <summary>Persisted: whether the Hub's NavigationView pane is expanded
    /// (true) or collapsed/compact (false). Default true.</summary>
    public bool HubNavPaneOpen { get; set; } = true;
    /// <summary>Optional Windows TTS voice id (or display name). Empty = system default.</summary>
    public string? TtsWindowsVoiceId { get; set; }
    /// <summary>
    /// ElevenLabs API key storage slot. When persisted by the Windows tray's
    /// SettingsManager this is an opaque dpapi:-prefixed blob, not plaintext.
    /// </summary>
    public string? TtsElevenLabsApiKey { get; set; }
    public string? TtsElevenLabsModel { get; set; }
    public string? TtsElevenLabsVoiceId { get; set; }
    /// <summary>Piper voice identifier, e.g. "en_US-amy-low". Voice file is downloaded on first use.</summary>
    public string TtsPiperVoiceId { get; set; } = "en_US-amy-low";
    /// <summary>Run the local MCP HTTP server. Independent of EnableNodeMode.</summary>
    public bool EnableMcpServer { get; set; } = false;
    /// <summary>
    /// Hostnames the A2UI image renderer is allowed to fetch over HTTPS.
    /// Empty by default — agents can still ship inline data: images. Add hosts
    /// (e.g., "cdn.example.com") via the Settings window.
    /// </summary>
    public List<string>? A2UIImageHosts { get; set; }
    /// <summary>
    /// Legacy flag (replaced by EnableMcpServer + the EnableNodeMode pair).
    /// Kept for one-time migration on Load; not written on Save.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? McpOnlyMode { get; set; }
    public string? PreferredGatewayId { get; set; }
    public bool HasSeenActivityStreamTip { get; set; } = false;
    public string? SkippedUpdateTag { get; set; }
    public bool NotifyChatResponses { get; set; } = true;
    public bool PreferStructuredCategories { get; set; } = true;
    public List<UserNotificationRule>? UserRules { get; set; }

    // ── (Voice / STT settings consolidated into the block above.) ──

    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, s_options);

    public static SettingsData? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<SettingsData>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
