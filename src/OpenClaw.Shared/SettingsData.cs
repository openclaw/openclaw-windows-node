using System;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>
/// Serializable settings data model. Used for JSON round-trip persistence.
/// </summary>
public class SettingsData
{
    public string? GatewayUrl { get; set; }
    public string? Token { get; set; }
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
    public bool HasSeenActivityStreamTip { get; set; } = false;
    public string? SkippedUpdateTag { get; set; }
    public bool NotifyChatResponses { get; set; } = true;
    public bool PreferStructuredCategories { get; set; } = true;
    public List<UserNotificationRule>? UserRules { get; set; }
    public VoiceSettings Voice { get; set; } = new();
    public VoiceRepeaterWindowSettings VoiceRepeaterWindow { get; set; } = new();
    public VoiceProviderConfigurationStore VoiceProviderConfiguration { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VoiceProviderCredentials? VoiceProviderCredentials { get; set; }

    private static readonly JsonSerializerOptions s_options = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, s_options);

    public static SettingsData? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SettingsData>(MigrateLegacyVoiceJson(json));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string MigrateLegacyVoiceJson(string json)
    {
        return json
            .Replace("\"WakeWord\":", "\"VoiceWake\":", StringComparison.Ordinal)
            .Replace("\"AlwaysOn\":", "\"TalkMode\":", StringComparison.Ordinal)
            .Replace("\"WakeWordModelId\":", "\"VoiceWakeModelId\":", StringComparison.Ordinal)
            .Replace("\"WakeWordLoaded\":", "\"VoiceWakeLoaded\":", StringComparison.Ordinal)
            .Replace("\"LastWakeWordUtc\":", "\"LastVoiceWakeUtc\":", StringComparison.Ordinal)
            .Replace("\"Mode\":\"WakeWord\"", "\"Mode\":\"VoiceWake\"", StringComparison.Ordinal)
            .Replace("\"Mode\": \"WakeWord\"", "\"Mode\": \"VoiceWake\"", StringComparison.Ordinal)
            .Replace("\"Mode\":\"AlwaysOn\"", "\"Mode\":\"TalkMode\"", StringComparison.Ordinal)
            .Replace("\"Mode\": \"AlwaysOn\"", "\"Mode\": \"TalkMode\"", StringComparison.Ordinal)
            .Replace("\"State\":\"ListeningForWakeWord\"", "\"State\":\"ListeningForVoiceWake\"", StringComparison.Ordinal)
            .Replace("\"State\": \"ListeningForWakeWord\"", "\"State\": \"ListeningForVoiceWake\"", StringComparison.Ordinal);
    }
}

public sealed class VoiceRepeaterWindowSettings
{
    public bool AutoScroll { get; set; } = true;
    public bool FloatingEnabled { get; set; } = true;
    public bool HasSavedPlacement { get; set; }
    public double TextSize { get; set; } = 13;
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
}
