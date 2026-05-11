using OpenClaw.Shared;
using OpenClawTray.Services.Connection;

namespace OpenClawTray.Tests.Connection;

public class NodeConnectorTests
{
    private class StubLogger : IOpenClawLogger
    {
        public void Info(string message) { }
        public void Debug(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }

    [Fact]
    public void InitialState_IsConnected_IsFalse()
    {
        using var connector = new NodeConnector(new StubLogger());
        Assert.False(connector.IsConnected);
    }

    [Fact]
    public void InitialState_PairingStatus_IsUnknown()
    {
        using var connector = new NodeConnector(new StubLogger());
        Assert.Equal(PairingStatus.Unknown, connector.PairingStatus);
    }

    [Fact]
    public void InitialState_Mode_IsDisabled()
    {
        using var connector = new NodeConnector(new StubLogger());
        Assert.Equal(NodeConnectionMode.Disabled, connector.Mode);
    }

    [Fact]
    public void InitialState_Client_IsNull()
    {
        using var connector = new NodeConnector(new StubLogger());
        Assert.Null(connector.Client);
    }

    [Fact]
    public void InitialState_NodeDeviceId_IsNull()
    {
        using var connector = new NodeConnector(new StubLogger());
        Assert.Null(connector.NodeDeviceId);
    }

    [Fact]
    public async Task ConnectAsync_AfterDispose_IsNoOp()
    {
        var connector = new NodeConnector(new StubLogger());
        connector.Dispose();

        // Should return without error; disposed connector skips connection.
        await connector.ConnectAsync("wss://example.com", new GatewayCredential("tok", false, "test"), "id-path");

        Assert.False(connector.IsConnected);
        Assert.Null(connector.Client);
    }

    [Fact]
    public async Task DisconnectAsync_WhenNotConnected_CompletesWithoutError()
    {
        using var connector = new NodeConnector(new StubLogger());
        await connector.DisconnectAsync();

        Assert.False(connector.IsConnected);
        Assert.Equal(NodeConnectionMode.Disabled, connector.Mode);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var connector = new NodeConnector(new StubLogger());
        connector.Dispose();
        connector.Dispose(); // second call should not throw
    }
}
