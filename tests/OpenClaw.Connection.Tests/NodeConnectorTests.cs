using System.Reflection;
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
    public async Task ConnectAsync_CompletedAttempt_DoesNotRetainCancellationRegistration()
    {
        using var connector = new NodeConnector(new StubLogger());
        using var cts = new CancellationTokenSource();

        await connector.ConnectAsync(
            "ws://127.0.0.1:1",
            new GatewayCredential("tok", false, "test"),
            "id-path",
            useV2Signature: false,
            cancellationToken: cts.Token);
        var completedClient = connector.Client;
        Assert.NotNull(completedClient);

        cts.Cancel();

        Assert.Same(completedClient, connector.Client);
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

    [Fact]
    public async Task Reconnect_SuppressesStatusFromRetiredClient()
    {
        using var connector = new NodeConnector(new StubLogger());
        var statuses = new List<ConnectionStatus>();
        connector.StatusChanged += (_, status) => statuses.Add(status);

        await connector.ConnectAsync(
            "ws://127.0.0.1:1",
            new GatewayCredential("tok", false, "test"),
            "id-path");
        var clientA = connector.Client;
        Assert.NotNull(clientA);

        await connector.ConnectAsync(
            "ws://127.0.0.1:2",
            new GatewayCredential("tok2", false, "test"),
            "id-path");
        var clientB = connector.Client;
        Assert.NotNull(clientB);
        Assert.NotSame(clientA, clientB);
        statuses.Clear();

        // Retired client A — generation mismatch, must be suppressed.
        RaiseClientStatus(clientA, ConnectionStatus.Connected);
        Assert.Empty(statuses);

        // Current client B — generation matches, must be forwarded.
        RaiseClientStatus(clientB, ConnectionStatus.Connected);
        Assert.Single(statuses);
        Assert.Equal(ConnectionStatus.Connected, statuses[0]);
    }

    [Fact]
    public async Task Disconnect_RetiresClient_SuppressesForwarding()
    {
        using var connector = new NodeConnector(new StubLogger());

        await connector.ConnectAsync(
            "ws://127.0.0.1:1",
            new GatewayCredential("tok", false, "test"),
            "id-path");
        var retiredClient = connector.Client;
        Assert.NotNull(retiredClient);

        await connector.DisconnectAsync();

        var forwardedConnected = false;
        connector.StatusChanged += (_, status) =>
            forwardedConnected |= status == ConnectionStatus.Connected;
        RaiseClientStatus(retiredClient, ConnectionStatus.Connected);

        Assert.Null(connector.Client);
        Assert.Equal(NodeConnectionMode.Disabled, connector.Mode);
        Assert.False(forwardedConnected);
    }

    [Fact]
    public async Task CurrentClientStatusHandler_CanReadConnectorProperties_WithoutBlocking()
    {
        using var connector = new NodeConnector(new StubLogger());
        bool? wasConnected = null;
        PairingStatus? pairingStatus = null;
        NodeConnectionMode? mode = null;
        WindowsNodeClient? clientRef = null;

        await connector.ConnectAsync(
            "ws://127.0.0.1:1",
            new GatewayCredential("tok", false, "test"),
            "id-path");
        var currentClient = connector.Client;
        Assert.NotNull(currentClient);

        connector.StatusChanged += (_, status) =>
        {
            if (status != ConnectionStatus.Connecting)
                return;

            wasConnected = connector.IsConnected;
            pairingStatus = connector.PairingStatus;
            mode = connector.Mode;
            clientRef = connector.Client;
        };

        RaiseClientStatus(currentClient, ConnectionStatus.Connecting);

        Assert.False(wasConnected);
        Assert.Equal(PairingStatus.Unknown, pairingStatus);
        Assert.Equal(NodeConnectionMode.Gateway, mode);
        Assert.Same(currentClient, clientRef);
    }

    private static void RaiseClientStatus(WindowsNodeClient client, ConnectionStatus status)
    {
        var method = typeof(WebSocketClientBase).GetMethod(
            "RaiseStatusChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(client, [status]);
    }
}
