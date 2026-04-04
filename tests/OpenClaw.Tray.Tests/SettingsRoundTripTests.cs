using System.Collections.Generic;
using System.Text.Json;
using OpenClaw.Shared;

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
            HasSeenActivityStreamTip = true,
            SkippedUpdateTag = "v1.2.3",
            NotifyChatResponses = false,
            PreferStructuredCategories = true,
            Voice = new VoiceSettings
            {
                Enabled = true,
                Mode = VoiceActivationMode.VoiceWake,
                ShowRepeaterAtStartup = false,
                ShowConversationToasts = true,
                SpeechToTextProviderId = "windows",
                TextToSpeechProviderId = "elevenlabs",
                InputDeviceId = "mic-1",
                OutputDeviceId = "spk-2",
                SampleRateHz = 16000,
                CaptureChunkMs = 80,
                BargeInEnabled = false,
                VoiceWake = new VoiceWakeSettings
                {
                    Engine = "NanoWakeWord",
                    ModelId = "hey_openclaw",
                    TriggerThreshold = 0.72f,
                    TriggerCooldownMs = 2500,
                    PreRollMs = 1400,
                    EndSilenceMs = 1000
                },
                TalkMode = new TalkModeSettings
                {
                    MinSpeechMs = 300,
                    EndSilenceMs = 1100,
                    MaxUtteranceMs = 18000
                }
            },
            VoiceProviderConfiguration = new VoiceProviderConfigurationStore
            {
                Providers =
                [
                    new VoiceProviderConfiguration
                    {
                        ProviderId = VoiceProviderIds.MiniMax,
                        Values = new Dictionary<string, string>
                        {
                            [VoiceProviderSettingKeys.ApiKey] = "minimax-key",
                            [VoiceProviderSettingKeys.Model] = "speech-2.8-turbo",
                            [VoiceProviderSettingKeys.VoiceId] = "English_MatureBoss",
                            [VoiceProviderSettingKeys.VoiceSettingsJson] = "{\"voice_id\":\"English_MatureBoss\",\"speed\":1.1}"
                        }
                    },
                    new VoiceProviderConfiguration
                    {
                        ProviderId = VoiceProviderIds.ElevenLabs,
                        Values = new Dictionary<string, string>
                        {
                            [VoiceProviderSettingKeys.ApiKey] = "eleven-key",
                            [VoiceProviderSettingKeys.Model] = "eleven_multilingual_v2",
                            [VoiceProviderSettingKeys.VoiceId] = "voice-42"
                        }
                    }
                ]
            },
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
        Assert.Equal(original.HasSeenActivityStreamTip, restored.HasSeenActivityStreamTip);
        Assert.Equal(original.SkippedUpdateTag, restored.SkippedUpdateTag);
        Assert.Equal(original.NotifyChatResponses, restored.NotifyChatResponses);
        Assert.Equal(original.PreferStructuredCategories, restored.PreferStructuredCategories);
        Assert.NotNull(restored.Voice);
        Assert.True(restored.Voice.Enabled);
        Assert.Equal(VoiceActivationMode.VoiceWake, restored.Voice.Mode);
        Assert.False(restored.Voice.ShowRepeaterAtStartup);
        Assert.True(restored.Voice.ShowConversationToasts);
        Assert.Equal("windows", restored.Voice.SpeechToTextProviderId);
        Assert.Equal("elevenlabs", restored.Voice.TextToSpeechProviderId);
        Assert.Equal("mic-1", restored.Voice.InputDeviceId);
        Assert.Equal("spk-2", restored.Voice.OutputDeviceId);
        Assert.Equal("NanoWakeWord", restored.Voice.VoiceWake.Engine);
        Assert.Equal("hey_openclaw", restored.Voice.VoiceWake.ModelId);
        Assert.Equal(0.72f, restored.Voice.VoiceWake.TriggerThreshold);
        Assert.Equal(300, restored.Voice.TalkMode.MinSpeechMs);
        Assert.NotNull(restored.VoiceProviderConfiguration);
        Assert.Equal("minimax-key", restored.VoiceProviderConfiguration.GetValue(VoiceProviderIds.MiniMax, VoiceProviderSettingKeys.ApiKey));
        Assert.Equal("speech-2.8-turbo", restored.VoiceProviderConfiguration.GetValue(VoiceProviderIds.MiniMax, VoiceProviderSettingKeys.Model));
        Assert.Equal("English_MatureBoss", restored.VoiceProviderConfiguration.GetValue(VoiceProviderIds.MiniMax, VoiceProviderSettingKeys.VoiceId));
        Assert.Equal("{\"voice_id\":\"English_MatureBoss\",\"speed\":1.1}", restored.VoiceProviderConfiguration.GetValue(VoiceProviderIds.MiniMax, VoiceProviderSettingKeys.VoiceSettingsJson));
        Assert.Equal("eleven-key", restored.VoiceProviderConfiguration.GetValue(VoiceProviderIds.ElevenLabs, VoiceProviderSettingKeys.ApiKey));
        Assert.Equal("eleven_multilingual_v2", restored.VoiceProviderConfiguration.GetValue(VoiceProviderIds.ElevenLabs, VoiceProviderSettingKeys.Model));
        Assert.Equal("voice-42", restored.VoiceProviderConfiguration.GetValue(VoiceProviderIds.ElevenLabs, VoiceProviderSettingKeys.VoiceId));
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
        Assert.False(settings.UseSshTunnel);
        Assert.Null(settings.SshTunnelUser);
        Assert.Null(settings.SshTunnelHost);
        Assert.Equal(18789, settings.SshTunnelRemotePort);
        Assert.Equal(18789, settings.SshTunnelLocalPort);
        Assert.False(settings.AutoStart);
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
        Assert.False(settings.HasSeenActivityStreamTip);
        Assert.Null(settings.SkippedUpdateTag);
        Assert.True(settings.NotifyChatResponses);
        Assert.True(settings.PreferStructuredCategories);
        Assert.NotNull(settings.Voice);
        Assert.False(settings.Voice.Enabled);
        Assert.Equal(VoiceActivationMode.Off, settings.Voice.Mode);
        Assert.True(settings.Voice.ShowRepeaterAtStartup);
        Assert.False(settings.Voice.ShowConversationToasts);
        Assert.Equal(VoiceProviderIds.Windows, settings.Voice.SpeechToTextProviderId);
        Assert.Equal(VoiceProviderIds.Windows, settings.Voice.TextToSpeechProviderId);
        Assert.NotNull(settings.VoiceProviderConfiguration);
        Assert.Empty(settings.VoiceProviderConfiguration.Providers);
        Assert.Equal(16000, settings.Voice.SampleRateHz);
        Assert.Equal("NanoWakeWord", settings.Voice.VoiceWake.Engine);
        Assert.Null(settings.UserRules);
    }

    [Fact]
    public void LegacyVoiceProviderCredentials_Deserialize_ForMigration()
    {
        var json = """
        {
          "VoiceProviderCredentials": {
            "MiniMaxApiKey": "minimax-key",
            "MiniMaxModel": "speech-2.8-turbo",
            "MiniMaxVoiceId": "English_MatureBoss"
          }
        }
        """;

        var settings = SettingsData.FromJson(json);

        Assert.NotNull(settings);
        Assert.NotNull(settings.VoiceProviderCredentials);
        Assert.Equal("minimax-key", settings.VoiceProviderCredentials.MiniMaxApiKey);
        Assert.Equal("speech-2.8-turbo", settings.VoiceProviderCredentials.MiniMaxModel);
        Assert.Equal("English_MatureBoss", settings.VoiceProviderCredentials.MiniMaxVoiceId);
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
        Assert.False(settings.UseSshTunnel);
        Assert.Null(settings.SshTunnelUser);
        Assert.Null(settings.SshTunnelHost);
        Assert.Equal(18789, settings.SshTunnelRemotePort);
        Assert.Equal(18789, settings.SshTunnelLocalPort);
        // New fields should have sensible defaults
        Assert.True(settings.NotifyChatResponses);
        Assert.True(settings.PreferStructuredCategories);
        Assert.False(settings.EnableNodeMode);
        Assert.False(settings.HasSeenActivityStreamTip);
        Assert.Null(settings.SkippedUpdateTag);
        Assert.True(settings.GlobalHotkeyEnabled);
        Assert.NotNull(settings.Voice);
        Assert.False(settings.Voice.Enabled);
        Assert.Equal(VoiceActivationMode.Off, settings.Voice.Mode);
        Assert.True(settings.Voice.ShowRepeaterAtStartup);
        Assert.False(settings.Voice.ShowConversationToasts);
        Assert.Equal(VoiceProviderIds.Windows, settings.Voice.SpeechToTextProviderId);
        Assert.Equal(VoiceProviderIds.Windows, settings.Voice.TextToSpeechProviderId);
        Assert.Null(settings.UserRules);
    }

    [Fact]
    public void InvalidJson_ReturnsNull()
    {
        Assert.Null(SettingsData.FromJson("not json at all"));
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
