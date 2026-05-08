using System;
using System.IO;
using OpenClaw.Shared;
using OpenClawTray.Services;
using Xunit;

namespace OpenClaw.Tray.Tests;

public class SetupCodeApplicatorTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"openclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static SettingsManager CreateTempSettings() => new SettingsManager(CreateTempDir());

    private static string EncodeSetupCode(string json) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    [Fact]
    public void Apply_ValidSetupCode_ReturnsSuccess()
    {
        var dir = CreateTempDir();
        var settings = new SettingsManager(dir);
        var registry = new GatewayRegistry(dir);

        var code = EncodeSetupCode("""{"url":"ws://localhost:18789","bootstrapToken":"bt-123"}""");
        var result = SetupCodeApplicator.Apply(code, settings, dir, registry);

        Assert.True(result.Success);
        var gw = registry.GetActive();
        Assert.NotNull(gw);
        Assert.Equal("ws://localhost:18789", gw.Url);
        Assert.Equal("bt-123", gw.BootstrapToken);
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
    public void Apply_BootstrapTokenStoredInRegistry()
    {
        var dir = CreateTempDir();
        var settings = new SettingsManager(dir);
        var registry = new GatewayRegistry(dir);

        var code = EncodeSetupCode("""{"url":"ws://10.0.0.1:18789","bootstrapToken":"fresh-token"}""");
        SetupCodeApplicator.Apply(code, settings, dir, registry);

        var gw = registry.GetActive();
        Assert.NotNull(gw);
        Assert.Equal("fresh-token", gw.BootstrapToken);
    }

    // ── Gateway Registry Integration ──

    [Fact]
    public void Apply_WithRegistry_CreatesRecordWithBootstrapToken()
    {
        var dir = CreateTempDir();
        var settings = new SettingsManager(dir);
        var registry = new GatewayRegistry(dir);

        var code = EncodeSetupCode("""{"url":"ws://gw1:18789","bootstrapToken":"bt-1"}""");
        var result = SetupCodeApplicator.Apply(code, settings, dir, registry);

        Assert.True(result.Success);
        var active = registry.GetActive();
        Assert.NotNull(active);
        Assert.Equal("gw1-18789", active.Id);
        Assert.Equal("ws://gw1:18789", active.Url);
        Assert.Equal("bt-1", active.BootstrapToken);
        Assert.Null(active.OperatorDeviceToken);
    }

    [Fact]
    public void Apply_WithRegistry_ClearsOldTokensOnReApply()
    {
        var dir = CreateTempDir();
        var settings = new SettingsManager(dir);
        var registry = new GatewayRegistry(dir);

        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "gw1-18789", Url = "ws://gw1:18789",
            OperatorDeviceToken = "old-op", NodeDeviceToken = "old-node",
        });

        var code = EncodeSetupCode("""{"url":"ws://gw1:18789","bootstrapToken":"bt-fresh"}""");
        SetupCodeApplicator.Apply(code, settings, dir, registry);

        var active = registry.GetActive();
        Assert.NotNull(active);
        Assert.Equal("bt-fresh", active.BootstrapToken);
        Assert.Null(active.OperatorDeviceToken);
        Assert.Null(active.NodeDeviceToken);
    }

    [Fact]
    public void Apply_WithoutRegistry_StillWorks()
    {
        var settings = CreateTempSettings();
        var code = EncodeSetupCode("""{"url":"ws://host:1234","bootstrapToken":"tok"}""");

        var result = SetupCodeApplicator.Apply(code, settings);

        Assert.True(result.Success);
    }
}
