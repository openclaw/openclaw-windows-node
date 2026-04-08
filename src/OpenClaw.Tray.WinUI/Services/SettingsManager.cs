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
    public VoiceSettings Voice { get; set; } = new();
    public VoiceRepeaterWindowSettings VoiceRepeaterWindow { get; set; } = new();
    public VoiceProviderConfigurationStore VoiceProviderConfiguration { get; set; } = new();
    
    // Node mode (enables Windows as a node, not just operator)
    public bool EnableNodeMode { get; set; } = false;
    public bool HasSeenActivityStreamTip { get; set; } = false;
    public string SkippedUpdateTag { get; set; } = "";

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
                    HasSeenActivityStreamTip = loaded.HasSeenActivityStreamTip;
                    SkippedUpdateTag = loaded.SkippedUpdateTag ?? SkippedUpdateTag;
                    NotifyChatResponses = loaded.NotifyChatResponses;
                    PreferStructuredCategories = loaded.PreferStructuredCategories;
                    if (loaded.UserRules != null)
                        UserRules = loaded.UserRules;
                    Voice = loaded.Voice ?? new VoiceSettings();
                    VoiceRepeaterWindow = loaded.VoiceRepeaterWindow ?? new VoiceRepeaterWindowSettings();
                    VoiceProviderConfiguration = loaded.VoiceProviderConfiguration?.Clone() ?? new VoiceProviderConfigurationStore();
                    VoiceProviderConfiguration.MigrateLegacyCredentials(loaded.VoiceProviderCredentials);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to load settings: {ex.Message}");
        }
    }

    public void Save(bool logSuccess = true)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            
            var data = new SettingsData
            {
                GatewayUrl = GatewayUrl,
                Token = Token,
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
                HasSeenActivityStreamTip = HasSeenActivityStreamTip,
                SkippedUpdateTag = string.IsNullOrWhiteSpace(SkippedUpdateTag) ? null : SkippedUpdateTag,
                NotifyChatResponses = NotifyChatResponses,
                PreferStructuredCategories = PreferStructuredCategories,
                UserRules = UserRules,
                Voice = Voice,
                VoiceRepeaterWindow = VoiceRepeaterWindow,
                VoiceProviderConfiguration = VoiceProviderConfiguration.Clone()
            };

            var json = data.ToJson();
            File.WriteAllText(SettingsFilePath, json);
            
            if (logSuccess)
            {
                Logger.Info("Settings saved");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to save settings: {ex.Message}");
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
