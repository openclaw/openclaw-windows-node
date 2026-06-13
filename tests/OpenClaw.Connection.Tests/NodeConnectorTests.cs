using OpenClaw.Shared;
using OpenClaw.Connection;

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
    public async Task ConnectAsync_PreCancelledAttempt_DoesNotCreateClient()
    {
        using var connector = new NodeConnector(new StubLogger());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            connector.ConnectAsync(
                "wss://example.com",
                new GatewayCredential("tok", false, "test"),
                "id-path",
                useV2Signature: false,
                cancellationToken: cts.Token));

        Assert.False(connector.IsConnected);
        Assert.Null(connector.Client);
    }

    [Fact]
    public async Task ConnectAsync_WhenClientCreatedHandlerThrows_AbortsBeforeHandshake()
    {
        var diagnostics = new ConnectionDiagnostics();
        using var connector = new NodeConnector(new StubLogger(), diagnostics);
        connector.ClientCreated += (_, _) => throw new InvalidOperationException("boom");
        ConnectionStatus? status = null;
        connector.StatusChanged += (_, e) => status = e;

        await connector.ConnectAsync("ws://127.0.0.1:9", new GatewayCredential("tok", false, "test"), "id-path");

        var evt = Assert.Single(diagnostics.GetAll(), e => e.Category == "node");
        Assert.Equal("ClientCreated handler failed; node connection aborted before handshake", evt.Message);
        Assert.Equal("boom", evt.Detail);
        Assert.Equal(ConnectionStatus.Error, status);
        Assert.Null(connector.Client);
        Assert.Equal(NodeConnectionMode.Disabled, connector.Mode);
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
