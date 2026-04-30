using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared;

/// <summary>
/// Serializable settings data model. Used for JSON round-trip persistence.
/// </summary>
public class SettingsData
{
    public string? GatewayUrl { get; set; }
    public string? Token { get; set; }
    public string? BootstrapToken { get; set; }
    public bool UseSshTunnel { get; set; } = false;
    public string? SshTunnelUser { get; set; }
    public string? SshTunnelHost { get; set; }
    public int SshTunnelRemotePort { get; set; } = 18789;
    public int SshTunnelLocalPort { get; set; } = 18789;
    public bool AutoStart { get; set; }
    public bool GlobalHotkeyEnabled { get; set; } = true;
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
    public bool NodeLocationEnabled { get; set; } = true;
    public bool NodeBrowserProxyEnabled { get; set; } = true;
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
    public bool HasSeenActivityStreamTip { get; set; } = false;
    public string? SkippedUpdateTag { get; set; }
    public bool NotifyChatResponses { get; set; } = true;
    public bool PreferStructuredCategories { get; set; } = true;
    public List<UserNotificationRule>? UserRules { get; set; }

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
