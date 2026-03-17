using System;
using System.IO;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// Manages application settings with JSON persistence.
/// </summary>
public class SettingsManager
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OpenClawTray");

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

    // Connection
    public string GatewayUrl { get; set; } = "ws://localhost:18789";
    public string Token { get; set; } = "";

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
    
    // Node mode (enables Windows as a node, not just operator)
    public bool EnableNodeMode { get; set; } = false;
    public bool HasSeenActivityStreamTip { get; set; } = false;

    public SettingsManager()
    {
        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var loaded = SettingsData.FromJson(json);
                if (loaded != null)
                {
                    GatewayUrl = loaded.GatewayUrl ?? GatewayUrl;
                    Token = loaded.Token ?? Token;
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
                    HasSeenActivityStreamTip = loaded.HasSeenActivityStreamTip;
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
            Directory.CreateDirectory(SettingsDirectory);
            
            var data = new SettingsData
            {
                GatewayUrl = GatewayUrl,
                Token = Token,
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
                HasSeenActivityStreamTip = HasSeenActivityStreamTip,
                NotifyChatResponses = NotifyChatResponses,
                PreferStructuredCategories = PreferStructuredCategories,
                UserRules = UserRules
            };

            var json = data.ToJson();
            File.WriteAllText(SettingsFilePath, json);
            
            Logger.Info("Settings saved");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to save settings: {ex.Message}");
        }
    }
}
