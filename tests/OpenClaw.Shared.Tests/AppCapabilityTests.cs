using OpenClaw.Shared.Capabilities;
using System.Text.Json;

namespace OpenClaw.Shared.Tests;

public class AppCapabilityTests
{
    private static JsonElement ParseArgs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Category_IsApp()
    {
        var cap = new AppCapability(NullLogger.Instance);
        Assert.Equal("app", cap.Category);
    }

    [Fact]
    public void Commands_ContainsAllExpectedTools()
    {
        var cap = new AppCapability(NullLogger.Instance);
        var expected = new[] { "app.navigate", "app.status", "app.sessions", "app.agents",
            "app.nodes", "app.config.get", "app.settings.get", "app.settings.set", "app.menu", "app.search" };
        foreach (var cmd in expected)
            Assert.Contains(cmd, cap.Commands);
    }

    [Fact]
    public void CanHandle_AppCommands()
    {
        var cap = new AppCapability(NullLogger.Instance);
        Assert.True(cap.CanHandle("app.navigate"));
        Assert.True(cap.CanHandle("app.status"));
        Assert.False(cap.CanHandle("system.run"));
        Assert.False(cap.CanHandle("device.info"));
    }

    [Fact]
    public async Task Navigate_WithNoHandler_ReturnsError()
    {
        var cap = new AppCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "1", Command = "app.navigate", Args = ParseArgs("{\"page\":\"home\"}") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
    }

    [Fact]
    public async Task Status_WithHandler_ReturnsData()
    {
        var cap = new AppCapability(NullLogger.Instance);
        cap.StatusHandler = () => new { connected = true };
        var req = new NodeInvokeRequest { Id = "1", Command = "app.status", Args = ParseArgs("{}") };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
    }

    [Fact]
    public async Task SettingsGet_WithNoHandler_ReturnsError()
    {
        var cap = new AppCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "1", Command = "app.settings.get", Args = ParseArgs("{\"name\":\"Token\"}") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
    }

    [Fact]
    public async Task UnknownCommand_ReturnsError()
    {
        var cap = new AppCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "1", Command = "app.unknown", Args = ParseArgs("{}") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("Unknown", res.Error);
    }
}
