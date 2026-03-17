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
        Assert.True(settings.NotifyChatResponses);
        Assert.True(settings.PreferStructuredCategories);
        Assert.Null(settings.UserRules);
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
        // New fields should have sensible defaults
        Assert.True(settings.NotifyChatResponses);
        Assert.True(settings.PreferStructuredCategories);
        Assert.False(settings.EnableNodeMode);
        Assert.False(settings.HasSeenActivityStreamTip);
        Assert.True(settings.GlobalHotkeyEnabled);
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
