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
            UseSshTunnel= true,
            SshTunnelUser = "user1",
            SshTunnelHost = "remote-host",
            SshTunnelSshPort = 2222,
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
            AppTheme = "Dark",
            ShowDiagnostics = true,
            SystemRunSandboxEnabled = true,
            SystemRunBlockHostFallbackWhenMxcUnavailable = true,
            SystemRunAllowOutbound = true,
            UserRules = new List<UserNotificationRule>
            {
                new() { Pattern = "build.*fail", IsRegex = true, Category = "urgent", Enabled = true }
            }
        };

        var json = original.ToJson();
        var restored = SettingsData.FromJson(json);

        Assert.NotNull(restored);
        Assert.Equal(original.SettingsSchemaVersion, restored.SettingsSchemaVersion);
        Assert.Equal(original.GatewayUrl, restored.GatewayUrl);
        Assert.Equal(original.UseSshTunnel, restored.UseSshTunnel);
        Assert.Equal(original.SshTunnelUser, restored.SshTunnelUser);
        Assert.Equal(original.SshTunnelHost, restored.SshTunnelHost);
        Assert.Equal(original.SshTunnelSshPort, restored.SshTunnelSshPort);
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
        Assert.Equal(original.AppTheme, restored.AppTheme);
        Assert.Equal(original.ShowDiagnostics, restored.ShowDiagnostics);
        Assert.Equal(original.SystemRunSandboxEnabled, restored.SystemRunSandboxEnabled);
        Assert.Equal(original.SystemRunBlockHostFallbackWhenMxcUnavailable, restored.SystemRunBlockHostFallbackWhenMxcUnavailable);
        Assert.Equal(original.SystemRunAllowOutbound, restored.SystemRunAllowOutbound);
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
        Assert.False(settings.UseSshTunnel);
        Assert.Null(settings.SshTunnelUser);
        Assert.Null(settings.SshTunnelHost);
        Assert.Equal(22, settings.SshTunnelSshPort);
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
        Assert.Equal("System", settings.AppTheme);
        Assert.Null(settings.ShowDiagnostics);
        Assert.True(settings.SystemRunSandboxEnabled);
        Assert.False(settings.SystemRunBlockHostFallbackWhenMxcUnavailable);
        Assert.False(settings.SystemRunAllowOutbound);
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
    public void SettingsManager_PreservesLegacySandboxFallbackDefault()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OpenClaw.Tray.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), """
            {
                "SystemRunSandboxEnabled": true,
                "SystemRunBlockHostFallbackWhenMxcUnavailable": false
            }
            """);

            var settings = new SettingsManager(dir);

            Assert.True(settings.SystemRunSandboxEnabled);
            Assert.False(settings.SystemRunBlockHostFallbackWhenMxcUnavailable);

            settings.Save();

            using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")));
            Assert.Equal(1, saved.RootElement.GetProperty(nameof(SettingsData.SettingsSchemaVersion)).GetInt32());
            Assert.False(saved.RootElement.GetProperty(nameof(SettingsData.SystemRunBlockHostFallbackWhenMxcUnavailable)).GetBoolean());
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SettingsManager_PreservesVersionedSandboxFallbackCompatibility()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OpenClaw.Tray.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), """
            {
                "SettingsSchemaVersion": 1,
                "SystemRunSandboxEnabled": true,
                "SystemRunBlockHostFallbackWhenMxcUnavailable": false
            }
            """);

            var settings = new SettingsManager(dir);

            Assert.True(settings.SystemRunSandboxEnabled);
            Assert.False(settings.SystemRunBlockHostFallbackWhenMxcUnavailable);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SettingsManager_PreservesVersionedStrictFallbackBlockingOptIn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OpenClaw.Tray.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), """
            {
                "SettingsSchemaVersion": 1,
                "SystemRunSandboxEnabled": true,
                "SystemRunBlockHostFallbackWhenMxcUnavailable": true
            }
            """);

            var settings = new SettingsManager(dir);

            Assert.True(settings.SystemRunSandboxEnabled);
            Assert.True(settings.SystemRunBlockHostFallbackWhenMxcUnavailable);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
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
        Assert.False(settings.UseSshTunnel);
        Assert.Null(settings.SshTunnelUser);
        Assert.Null(settings.SshTunnelHost);
        Assert.Equal(22, settings.SshTunnelSshPort);
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
        Assert.Equal("System", settings.AppTheme);
        Assert.Null(settings.ShowDiagnostics);
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
    public void SettingsManager_DefaultsInvalidSshPort()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OpenClaw.Tray.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), """
            {
              "SshTunnelSshPort": 70000
            }
            """);

            var settings = new SettingsManager(dir);

            Assert.Equal(22, settings.SshTunnelSshPort);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
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

    [Fact]
    public void SettingsManager_NormalizesInvalidAppTheme()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OpenClaw.Tray.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), """
            {
              "AppTheme": "Neon"
            }
            """);

            var settings = new SettingsManager(dir);

            Assert.Equal("System", settings.AppTheme);

            settings.AppTheme = "dark";
            Assert.Equal("Dark", settings.AppTheme);
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
        if (!SettingsManager.CanProtectSettingSecretsForCurrentUser())
            return;

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

    [WindowsFact]
    public void SettingsManager_SaveProtectsSecretsWithoutMutatingInMemoryData()
    {
        if (!SettingsManager.CanProtectSettingSecretsForCurrentUser())
            return;

        var dir = Path.Combine(Path.GetTempPath(), "OpenClaw.Tray.Tests", Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(dir, "settings.json");

        try
        {
            var settings = new SettingsManager(dir)
            {
                TtsElevenLabsApiKey = "elevenlabs-key"
            };

            settings.Save();
            Assert.Equal("elevenlabs-key", settings.TtsElevenLabsApiKey);

            using (var saved = JsonDocument.Parse(File.ReadAllText(settingsPath)))
            {
                var stored = saved.RootElement.GetProperty(nameof(SettingsData.TtsElevenLabsApiKey)).GetString();
                Assert.StartsWith("dpapi:", stored);
                Assert.DoesNotContain("elevenlabs-key", stored);
            }

            settings.Save();
            Assert.Equal("elevenlabs-key", settings.TtsElevenLabsApiKey);

            var reloaded = new SettingsManager(dir);
            Assert.Equal("elevenlabs-key", reloaded.TtsElevenLabsApiKey);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SettingsManager_ToSettingsData_ReturnsDetachedMutableLists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OpenClaw.Tray.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            var settings = new SettingsManager(dir);
            settings.A2UIImageHosts.Add("images.example.test");
            settings.SandboxCustomFolders.Add(new SandboxCustomFolder
            {
                Path = "C:\\Temp\\OpenClaw",
                Access = SandboxFolderAccess.ReadOnly
            });

            var snapshot = settings.ToSettingsData();
            snapshot.A2UIImageHosts!.Add("mutated.example.test");
            snapshot.SandboxCustomFolders![0].Path = "C:\\Mutated";

            Assert.Equal(["images.example.test"], settings.A2UIImageHosts);
            Assert.Equal("C:\\Temp\\OpenClaw", settings.SandboxCustomFolders[0].Path);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
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
