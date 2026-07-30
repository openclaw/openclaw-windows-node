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

    private sealed class StubNodeRuntimeClientFactory(INodeRuntimeClient client)
        : INodeRuntimeClientFactory
    {
        public string? GatewayUrl { get; private set; }
        public GatewayCredential? Credential { get; private set; }
        public string? IdentityPath { get; private set; }

        public INodeRuntimeClient Create(
            string gatewayUrl,
            GatewayCredential credential,
            string identityPath,
            IOpenClawLogger logger)
        {
            GatewayUrl = gatewayUrl;
            Credential = credential;
            IdentityPath = identityPath;
            return client;
        }
    }

    private sealed class DelegateNodeRuntimeClientFactory(Func<INodeRuntimeClient> create)
        : INodeRuntimeClientFactory
    {
        public INodeRuntimeClient Create(
            string gatewayUrl,
            GatewayCredential credential,
            string identityPath,
            IOpenClawLogger logger) => create();
    }

    private sealed class StubNodeRuntimeClient : INodeRuntimeClient
    {
        private readonly Dictionary<string, bool> _permissions = [];

        public bool UseV2Signature { get; set; }
        public Func<CancellationToken, Task<ReconnectAuthorizationResult>>?
            ReconnectAuthorizationAsync { get; set; }
        public bool IsConnected { get; private set; }
        public string? NodeId => null;
        public string GatewayUrl => "ws://runtime.example";
        public IReadOnlyList<INodeCapability> Capabilities => [];
        public bool IsPendingApproval => false;
        public bool IsPaired => false;
        public string ShortDeviceId => "stub";
        public string FullDeviceId => "stub-runtime-client";
        public string DisplayName => "Stub runtime client";
        public int RegisteredCapabilityCount => 0;
        public int RegisteredCommandCount => 0;
        public IEnumerable<string> RegisteredCommandsSample => [];
        public bool PermissionWasSetBeforeConnect { get; private set; }
        public Func<CancellationToken, Task>? ConnectOverride { get; init; }
        public bool WasDisposed { get; private set; }
        public bool ConnectWasCalled { get; private set; }

        public event EventHandler<ConnectionStatus> StatusChanged { add { } remove { } }
        public event EventHandler<NodeInvokeCompletedEventArgs> InvokeCompleted { add { } remove { } }
        public event EventHandler<OpenClaw.Shared.Telemetry.NodeToolTelemetryCompletion> ToolTelemetryCompleted { add { } remove { } }
        public event EventHandler<PairingStatusEventArgs> PairingStatusChanged { add { } remove { } }
        public event EventHandler<System.Text.Json.JsonElement> HealthReceived { add { } remove { } }
        public event EventHandler<GatewaySelfInfo> GatewaySelfUpdated { add { } remove { } }
        public event EventHandler<DeviceTokenReceivedEventArgs> DeviceTokenReceived { add { } remove { } }
        public event EventHandler TransportConnected { add { } remove { } }
        public event EventHandler<GatewayErrorKind> ConnectionFailure { add { } remove { } }
        public event EventHandler Disposed { add { } remove { } }

        public void RegisterCapability(INodeCapability capability) { }
        public void SetPermission(string permission, bool value) => _permissions[permission] = value;

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            ConnectWasCalled = true;
            PermissionWasSetBeforeConnect = _permissions.GetValueOrDefault("test.permission");
            if (ConnectOverride != null)
                await ConnectOverride(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            IsConnected = true;
        }

        public Task DisconnectAsync()
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task<bool> SendNodeEventAsync(
            string eventName,
            System.Text.Json.Nodes.JsonObject payload) => Task.FromResult(true);

        public void Dispose()
        {
            WasDisposed = true;
            IsConnected = false;
        }
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
    public async Task ConnectAsync_UsesInjectedRuntimeClientBeforeHandshake()
    {
        var runtimeClient = new StubNodeRuntimeClient();
        var factory = new StubNodeRuntimeClientFactory(runtimeClient);
        using var connector = new NodeConnector(new StubLogger(), clientFactory: factory);
        Func<CancellationToken, Task<ReconnectAuthorizationResult>> reconnectAuthorization =
            _ => Task.FromResult(ReconnectAuthorizationResult.AllowedResult);
        connector.ReconnectAuthorizationAsync = reconnectAuthorization;
        INodeRuntimeClient? createdClient = null;
        connector.ClientCreated += (_, args) =>
        {
            createdClient = args.Client;
            args.Client.SetPermission("test.permission", true);
        };

        var credential = new GatewayCredential("token", false, "test");
        await connector.ConnectAsync(
            "ws://gateway.example",
            credential,
            "identity-path",
            useV2Signature: true);

        Assert.Same(runtimeClient, connector.Client);
        Assert.Same(runtimeClient, createdClient);
        Assert.Equal("ws://gateway.example", factory.GatewayUrl);
        Assert.Equal(credential, factory.Credential);
        Assert.Equal("identity-path", factory.IdentityPath);
        Assert.True(runtimeClient.UseV2Signature);
        Assert.Same(reconnectAuthorization, runtimeClient.ReconnectAuthorizationAsync);
        Assert.True(runtimeClient.PermissionWasSetBeforeConnect);
    }

    [Fact]
    public async Task ConnectAsync_CancelledBlockedRuntime_ReleasesConnectorForNextAttempt()
    {
        var connectStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedRuntime = new StubNodeRuntimeClient
        {
            ConnectOverride = async cancellationToken =>
            {
                connectStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        };
        var nextRuntime = new StubNodeRuntimeClient();
        var clients = new Queue<INodeRuntimeClient>([blockedRuntime, nextRuntime]);
        var factory = new DelegateNodeRuntimeClientFactory(() => clients.Dequeue());
        using var connector = new NodeConnector(new StubLogger(), clientFactory: factory);
        using var cts = new CancellationTokenSource();

        var blockedAttempt = connector.ConnectAsync(
            "ws://gateway.example",
            new GatewayCredential("token", false, "test"),
            "identity-path",
            useV2Signature: false,
            cancellationToken: cts.Token);
        await connectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => blockedAttempt.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(blockedRuntime.WasDisposed);
        Assert.Null(connector.Client);
        Assert.Equal(NodeConnectionMode.Disabled, connector.Mode);

        await connector.ConnectAsync(
            "ws://gateway.example",
            new GatewayCredential("token", false, "test"),
            "identity-path");

        Assert.Same(nextRuntime, connector.Client);
        Assert.True(nextRuntime.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_CancelledDuringClientCreated_RetiresBeforeHandshake()
    {
        var runtime = new StubNodeRuntimeClient();
        var factory = new StubNodeRuntimeClientFactory(runtime);
        using var connector = new NodeConnector(new StubLogger(), clientFactory: factory);
        using var cts = new CancellationTokenSource();
        connector.ClientCreated += (_, _) => cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            connector.ConnectAsync(
                "ws://gateway.example",
                new GatewayCredential("token", false, "test"),
                "identity-path",
                useV2Signature: false,
                cancellationToken: cts.Token));

        Assert.True(runtime.WasDisposed);
        Assert.False(runtime.ConnectWasCalled);
        Assert.Null(connector.Client);
        Assert.Equal(NodeConnectionMode.Disabled, connector.Mode);
    }

    [Fact]
    public async Task ConnectAsync_CancelledRuntimeThrowsTransportError_StillRetiresCandidate()
    {
        var connectStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new StubNodeRuntimeClient
        {
            ConnectOverride = async cancellationToken =>
            {
                connectStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw new IOException("transport aborted during cancellation");
                }
            }
        };
        var factory = new StubNodeRuntimeClientFactory(runtime);
        using var connector = new NodeConnector(new StubLogger(), clientFactory: factory);
        using var cts = new CancellationTokenSource();

        var attempt = connector.ConnectAsync(
            "ws://gateway.example",
            new GatewayCredential("token", false, "test"),
            "identity-path",
            useV2Signature: false,
            cancellationToken: cts.Token);
        await connectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => attempt.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(runtime.WasDisposed);
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
        var clientA = Assert.IsType<WindowsNodeClient>(connector.Client);

        await connector.ConnectAsync(
            "ws://127.0.0.1:2",
            new GatewayCredential("tok2", false, "test"),
            "id-path");
        var clientB = Assert.IsType<WindowsNodeClient>(connector.Client);
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
        var retiredClient = Assert.IsType<WindowsNodeClient>(connector.Client);

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
        INodeRuntimeClient? clientRef = null;

        await connector.ConnectAsync(
            "ws://127.0.0.1:1",
            new GatewayCredential("tok", false, "test"),
            "id-path");
        var currentClient = Assert.IsType<WindowsNodeClient>(connector.Client);

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
