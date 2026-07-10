using OpenClaw.Shared.Capabilities;
using System.Text.Json;

namespace OpenClaw.Shared.Tests;

public sealed class AppConnectionCapabilityTests
{
    private static JsonElement ParseArgs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Commands_ContainsConnectionDiagnosticsAndControls()
    {
        var cap = new AppConnectionCapability(NullLogger.Instance);
        var expected = new[]
        {
            "app.connection.status",
            "app.connection.gateways",
            "app.connection.applySetupCode",
            "app.connection.connectSharedToken",
            "app.connection.pendingApprovals",
            "app.connection.approveDevicePairing",
            "app.connection.rejectDevicePairing",
            "app.connection.approveNodePairing",
            "app.connection.rejectNodePairing",
            "app.connection.reconnect",
            "app.connection.reconnectNode"
        };

        foreach (var command in expected)
        {
            Assert.Contains(command, cap.Commands);
            Assert.True(cap.CanHandle(command), $"CanHandle should return true for '{command}'.");
        }
    }

    [Fact]
    public async Task Status_WithHandler_ReturnsData()
    {
        var cap = new AppConnectionCapability(NullLogger.Instance)
        {
            StatusHandler = () => Task.FromResult<object?>(new { connectionState = "Ready" })
        };

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "1",
            Command = "app.connection.status",
            Args = ParseArgs("{}")
        });

        Assert.True(res.Ok);
        Assert.NotNull(res.Payload);
    }

    [Fact]
    public async Task Gateways_WithHandler_ReturnsData()
    {
        var cap = new AppConnectionCapability(NullLogger.Instance)
        {
            GatewaysHandler = () => Task.FromResult<object?>(new { count = 0 })
        };

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "1",
            Command = "app.connection.gateways",
            Args = ParseArgs("{}")
        });

        Assert.True(res.Ok);
        Assert.NotNull(res.Payload);
    }

    [Fact]
    public async Task Status_WithNoHandler_ReturnsError()
    {
        var cap = new AppConnectionCapability(NullLogger.Instance);

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "1",
            Command = "app.connection.status",
            Args = ParseArgs("{}")
        });

        Assert.False(res.Ok);
        Assert.Contains("status handler", res.Error);
    }

    [Fact]
    public async Task Gateways_WithNoHandler_ReturnsError()
    {
        var cap = new AppConnectionCapability(NullLogger.Instance);

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "1",
            Command = "app.connection.gateways",
            Args = ParseArgs("{}")
        });

        Assert.False(res.Ok);
        Assert.Contains("gateways handler", res.Error);
    }
}
