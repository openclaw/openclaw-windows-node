using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class SettingsRoundTripTests
{
    [Fact]
    public void RoundTrip_AllFields_Preserved()
    {
        var original = new SettingsData
        {
            GatewayUrl = "ws://localhost:18789",
            Token = "secret-token",
            BootstrapToken = "bootstrap-token",
            UseSshTunnel = true,
            SshTunnelUser = "user1",
            SshTunnelHost = "remote-host",
            SshTunnelRemotePort = 18789,
            SshTunnelLocalPort = 28789,
            AutoStart = true,
            GlobalHotkeyEnabled = false,
            ShowNotifications = true,
            NotificationSound = "Chime",
            NotifyHealth = false,
            NotifyUrgent = true,
            NotifyReminder = false,
            NotifyEmail = true,
            NotifyCalendar = false,
            NotifyBuild = true,
            NotifyStock = false,
            NotifyInfo = true,
            EnableNodeMode = true,
            NodeCanvasEnabled = false,
            NodeScreenEnabled = true,
            NodeCameraEnabled = false,
            ScreenRecordingConsentGiven = true,
            CameraRecordingConsentGiven = true,
            NodeLocationEnabled = true,
            NodeBrowserProxyEnabled = false,
            NodeSttEnabled = true,
            SttLanguage = "en-GB",
            SttModelName = "tiny",
            SttSilenceTimeout = 2.5f,
            VoiceTtsEnabled = false,
            VoiceAudioFeedback = false,
            NodeTtsEnabled = true,
            TtsProvider = "elevenlabs",
            TtsElevenLabsApiKey = "elevenlabs-key",
            TtsElevenLabsModel = "eleven_multilingual_v2",
            TtsElevenLabsVoiceId = "voice-123",
            TtsWindowsVoiceId = "Microsoft Zira Desktop",
            HubNavPaneOpen = false,
            TtsPiperVoiceId = "fr_FR-siwis-low",
            HasSeenActivityStreamTip = true,
            SkippedUpdateTag = "v1.2.3",
            NotifyChatResponses = false,
            PreferStructuredCategories = true,
            UserRules = new List<UserNotificationRule>
            {
                new() { Pattern = "build.*fail", IsRegex = true, Category = "urgent", Enabled = true }
            }
        };

        var json = original.ToJson();
        var restored = SettingsData.FromJson(json);

        Assert.NotNull(restored);
        Assert.Equal(original.GatewayUrl, restored.GatewayUrl);
        Assert.Equal(original.Token, restored.Token);
        Assert.Equal(original.BootstrapToken, restored.BootstrapToken);
        Assert.Equal(original.UseSshTunnel, restored.UseSshTunnel);
        Assert.Equal(original.SshTunnelUser, restored.SshTunnelUser);
        Assert.Equal(original.SshTunnelHost, restored.SshTunnelHost);
        Assert.Equal(original.SshTunnelRemotePort, restored.SshTunnelRemotePort);
        Assert.Equal(original.SshTunnelLocalPort, restored.SshTunnelLocalPort);
        Assert.Equal(original.AutoStart, restored.AutoStart);
        Assert.Equal(original.GlobalHotkeyEnabled, restored.GlobalHotkeyEnabled);
        Assert.Equal(original.ShowNotifications, restored.ShowNotifications);
        Assert.Equal(original.NotificationSound, restored.NotificationSound);
        Assert.Equal(original.NotifyHealth, restored.NotifyHealth);
        Assert.Equal(original.NotifyUrgent, restored.NotifyUrgent);
        Assert.Equal(original.NotifyReminder, restored.NotifyReminder);
        Assert.Equal(original.NotifyEmail, restored.NotifyEmail);
        Assert.Equal(original.NotifyCalendar, restored.NotifyCalendar);
        Assert.Equal(original.NotifyBuild, restored.NotifyBuild);
        Assert.Equal(original.NotifyStock, restored.NotifyStock);
        Assert.Equal(original.NotifyInfo, restored.NotifyInfo);
        Assert.Equal(original.EnableNodeMode, restored.EnableNodeMode);
        Assert.Equal(original.NodeCanvasEnabled, restored.NodeCanvasEnabled);
        Assert.Equal(original.NodeScreenEnabled, restored.NodeScreenEnabled);
        Assert.Equal(original.NodeCameraEnabled, restored.NodeCameraEnabled);
        Assert.Equal(original.ScreenRecordingConsentGiven, restored.ScreenRecordingConsentGiven);
        Assert.Equal(original.CameraRecordingConsentGiven, restored.CameraRecordingConsentGiven);
        Assert.Equal(original.NodeLocationEnabled, restored.NodeLocationEnabled);
        Assert.Equal(original.NodeBrowserProxyEnabled, restored.NodeBrowserProxyEnabled);
        Assert.Equal(original.NodeSttEnabled, restored.NodeSttEnabled);
        Assert.Equal(original.SttLanguage, restored.SttLanguage);
        Assert.Equal(original.SttModelName, restored.SttModelName);
        Assert.Equal(original.SttSilenceTimeout, restored.SttSilenceTimeout);
        Assert.Equal(original.VoiceTtsEnabled, restored.VoiceTtsEnabled);
        Assert.Equal(original.VoiceAudioFeedback, restored.VoiceAudioFeedback);
        Assert.Equal(original.NodeTtsEnabled, restored.NodeTtsEnabled);
        Assert.Equal(original.TtsProvider, restored.TtsProvider);
        Assert.Equal(original.TtsElevenLabsApiKey, restored.TtsElevenLabsApiKey);
        Assert.Equal(original.TtsElevenLabsModel, restored.TtsElevenLabsModel);
        Assert.Equal(original.TtsElevenLabsVoiceId, restored.TtsElevenLabsVoiceId);
        Assert.Equal(original.TtsWindowsVoiceId, restored.TtsWindowsVoiceId);
        Assert.Equal(original.HubNavPaneOpen, restored.HubNavPaneOpen);
        Assert.Equal(original.TtsPiperVoiceId, restored.TtsPiperVoiceId);
        Assert.Equal(original.HasSeenActivityStreamTip, restored.HasSeenActivityStreamTip);
        Assert.Equal(original.SkippedUpdateTag, restored.SkippedUpdateTag);
        Assert.Equal(original.NotifyChatResponses, restored.NotifyChatResponses);
        Assert.Equal(original.PreferStructuredCategories, restored.PreferStructuredCategories);
        Assert.NotNull(restored.UserRules);
        Assert.Single(restored.UserRules);
        Assert.Equal("build.*fail", restored.UserRules[0].Pattern);
        Assert.True(restored.UserRules[0].IsRegex);
    }

    [Fact]
    public void UnknownNotificationSound_DeserializesGracefully()
    {
        var json = """
        {
            "NotificationSound": "NonExistentBeep42"
        }
        """;

        var settings = SettingsData.FromJson(json);
        Assert.NotNull(settings);
        Assert.Equal("NonExistentBeep42", settings.NotificationSound);
    }

    [Fact]
    public void MissingFields_UseDefaults()
    {
        var json = "{}";
        var settings = SettingsData.FromJson(json);

        Assert.NotNull(settings);
        Assert.Null(settings.GatewayUrl);
        Assert.Null(settings.Token);
        Assert.Null(settings.BootstrapToken);
        Assert.False(settings.UseSshTunnel);
        Assert.Null(settings.SshTunnelUser);
        Assert.Null(settings.SshTunnelHost);
        Assert.Equal(18789, settings.SshTunnelRemotePort);
        Assert.Equal(18789, settings.SshTunnelLocalPort);
        Assert.True(settings.AutoStart);
        Assert.True(settings.GlobalHotkeyEnabled);
        Assert.True(settings.ShowNotifications);
        Assert.Null(settings.NotificationSound);
        Assert.True(settings.NotifyHealth);
        Assert.True(settings.NotifyUrgent);
        Assert.True(settings.NotifyReminder);
        Assert.True(settings.NotifyEmail);
        Assert.True(settings.NotifyCalendar);
        Assert.True(settings.NotifyBuild);
        Assert.True(settings.NotifyStock);
        Assert.True(settings.NotifyInfo);
        Assert.False(settings.EnableNodeMode);
        Assert.True(settings.NodeCanvasEnabled);
        Assert.True(settings.NodeScreenEnabled);
        Assert.True(settings.NodeCameraEnabled);
        Assert.False(settings.ScreenRecordingConsentGiven);
        Assert.False(settings.CameraRecordingConsentGiven);
        Assert.True(settings.NodeLocationEnabled);
        Assert.True(settings.NodeBrowserProxyEnabled);
        Assert.False(settings.NodeSttEnabled);
        Assert.Equal("auto", settings.SttLanguage);
        Assert.False(settings.NodeTtsEnabled);
        Assert.Equal("piper", settings.TtsProvider);
        Assert.Null(settings.TtsElevenLabsApiKey);
        Assert.Null(settings.TtsElevenLabsModel);
        Assert.Null(settings.TtsElevenLabsVoiceId);
        Assert.False(settings.HasSeenActivityStreamTip);
        Assert.Null(settings.SkippedUpdateTag);
        Assert.True(settings.NotifyChatResponses);
        Assert.True(settings.PreferStructuredCategories);
        // HubNavPaneOpen defaults to true (NavView starts expanded for new
        // installs and for any settings file that predates the field).
        Assert.True(settings.HubNavPaneOpen);
        Assert.Null(settings.UserRules);
    }

    [Fact]
    public void HubNavPaneOpen_DefaultsTrue_ForEmptyJson()
    {
        // Existing users have a settings file written before HubNavPaneOpen
        // existed. The default-true initializer must survive deserialization
        // of a missing field so the NavView lands expanded for them, not
        // silently collapsed.
        var settings = SettingsData.FromJson("{}");
        Assert.NotNull(settings);
        Assert.True(settings!.HubNavPaneOpen);
    }

    [Fact]
    public void BackwardCompatibility_OldSettingsWithoutNewFields()
    {
        // Simulate an old settings.json that predates NotifyChatResponses and PreferStructuredCategories
        var json = """
        {
            "GatewayUrl": "ws://localhost:18789",
            "Token": "abc",
            "AutoStart": false,
            "ShowNotifications": true,
            "NotificationSound": "Default",
            "NotifyHealth": true,
            "NotifyUrgent": true,
            "NotifyReminder": true,
            "NotifyEmail": true,
            "NotifyCalendar": true,
            "NotifyBuild": true,
            "NotifyStock": true,
            "NotifyInfo": true
        }
        """;

        var settings = SettingsData.FromJson(json);

        Assert.NotNull(settings);
        Assert.Equal("ws://localhost:18789", settings.GatewayUrl);
        Assert.Equal("abc", settings.Token);
        Assert.Null(settings.BootstrapToken);
        Assert.False(settings.UseSshTunnel);
        Assert.Null(settings.SshTunnelUser);
        Assert.Null(settings.SshTunnelHost);
        Assert.Equal(18789, settings.SshTunnelRemotePort);
        Assert.Equal(18789, settings.SshTunnelLocalPort);
        // New fields should have sensible defaults
        Assert.True(settings.NotifyChatResponses);
        Assert.True(settings.PreferStructuredCategories);
        Assert.False(settings.EnableNodeMode);
        Assert.True(settings.NodeCanvasEnabled);
        Assert.True(settings.NodeScreenEnabled);
        Assert.True(settings.NodeCameraEnabled);
        Assert.False(settings.ScreenRecordingConsentGiven);
        Assert.False(settings.CameraRecordingConsentGiven);
        Assert.True(settings.NodeLocationEnabled);
        Assert.True(settings.NodeBrowserProxyEnabled);
        Assert.False(settings.NodeSttEnabled);
        Assert.Equal("auto", settings.SttLanguage);
        Assert.False(settings.NodeTtsEnabled);
        Assert.Equal("piper", settings.TtsProvider);
        Assert.Null(settings.TtsElevenLabsApiKey);
        Assert.Null(settings.TtsElevenLabsModel);
        Assert.Null(settings.TtsElevenLabsVoiceId);
        Assert.False(settings.HasSeenActivityStreamTip);
        Assert.Null(settings.SkippedUpdateTag);
        Assert.True(settings.GlobalHotkeyEnabled);
        // HubNavPaneOpen wasn't in this older JSON shape; default true.
        Assert.True(settings.HubNavPaneOpen);
        Assert.Null(settings.UserRules);
    }

    [Fact]
    public void InvalidJson_ReturnsNull()
    {
        Assert.Null(SettingsData.FromJson("not json at all"));
    }

    [Fact]
    public void SettingsManager_PersistsRecordingConsentFlags()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OpenClaw.Tray.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            var settings = new SettingsManager(dir)
            {
                ScreenRecordingConsentGiven = true,
                CameraRecordingConsentGiven = true
            };

            settings.Save();

            var reloaded = new SettingsManager(dir);
            Assert.True(reloaded.ScreenRecordingConsentGiven);
            Assert.True(reloaded.CameraRecordingConsentGiven);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [WindowsFact]
    public void SettingsManager_ProtectsElevenLabsApiKeyForStorage()
    {
        var protectedValue = SettingsManager.ProtectSettingSecret("elevenlabs-key");

        Assert.NotNull(protectedValue);
        Assert.StartsWith("dpapi:", protectedValue);
        Assert.DoesNotContain("elevenlabs-key", protectedValue);
        Assert.Equal("elevenlabs-key", SettingsManager.UnprotectSettingSecret(protectedValue));
    }

    [Fact]
    public void SettingsManager_ReturnsNullForCorruptedProtectedSecret()
    {
        Assert.Null(SettingsManager.UnprotectSettingSecret("dpapi:not-base64"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmptyJson_ReturnsNull(string? json)
    {
        Assert.Null(SettingsData.FromJson(json));
    }

    [Fact]
    public void EmptyUserRules_RoundTrips()
    {
        var original = new SettingsData { UserRules = new List<UserNotificationRule>() };
        var json = original.ToJson();
        var restored = SettingsData.FromJson(json);

        Assert.NotNull(restored);
        Assert.NotNull(restored.UserRules);
        Assert.Empty(restored.UserRules);
    }

    [Fact]
    public void ToJson_ProducesIndentedOutput()
    {
        var data = new SettingsData { GatewayUrl = "ws://test" };
        var json = data.ToJson();
        Assert.Contains("\n", json);
        Assert.Contains("  ", json);
    }
}
