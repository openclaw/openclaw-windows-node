using System;
using System.IO;
using OpenClaw.Shared;
using OpenClawTray.Services;
using Xunit;

namespace OpenClaw.Tray.Tests;

public class SetupCodeApplicatorTests
{
    private SettingsManager CreateTempSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"openclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return new SettingsManager(dir);
    }

    [Fact]
    public void Apply_ValidSetupCode_SetsBootstrapTokenAndClearsToken()
    {
        var settings = CreateTempSettings();
        settings.Token = "old-manual-token";
        settings.Save();

        // Build a valid setup code: base64url of { "url": "ws://localhost:18789", "bootstrapToken": "bt-123" }
        var json = """{"url":"ws://localhost:18789","bootstrapToken":"bt-123"}""";
        var code = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var result = SetupCodeApplicator.Apply(code, settings);

        Assert.True(result.Success);
        Assert.Equal("ws://localhost:18789", settings.GatewayUrl);
        Assert.Equal("bt-123", settings.BootstrapToken);
        Assert.Equal("", settings.Token); // Must be cleared
    }

    [Fact]
    public void Apply_EmptyCode_ReturnsFalse()
    {
        var settings = CreateTempSettings();
        var result = SetupCodeApplicator.Apply("", settings);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Apply_InvalidBase64_ReturnsFalse()
    {
        var settings = CreateTempSettings();
        var result = SetupCodeApplicator.Apply("!!!not-base64!!!", settings);

        Assert.False(result.Success);
    }

    [Fact]
    public void Apply_DoesNotLeaveTokenPopulated()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"openclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var settings = new SettingsManager(dir);
        settings.Token = "should-be-cleared";
        settings.BootstrapToken = "";
        settings.Save();

        var json = """{"url":"ws://10.0.0.1:18789","bootstrapToken":"fresh-token"}""";
        var code = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        SetupCodeApplicator.Apply(code, settings);

        // Reload settings from disk to verify persistence
        var reloaded = new SettingsManager(dir);
        Assert.Equal("", reloaded.Token);
        Assert.Equal("fresh-token", reloaded.BootstrapToken);
    }
}
