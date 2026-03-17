using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>
/// Serializable settings data model. Used for JSON round-trip persistence.
/// </summary>
public class SettingsData
{
    public string? GatewayUrl { get; set; }
    public string? Token { get; set; }
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
    public bool NotifyChatResponses { get; set; } = true;
    public bool PreferStructuredCategories { get; set; } = true;
    public List<UserNotificationRule>? UserRules { get; set; }

    private static readonly JsonSerializerOptions s_options = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, s_options);

    public static SettingsData? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SettingsData>(json);
        }
        catch
        {
            return null;
        }
    }
}
