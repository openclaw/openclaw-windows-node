using OpenClaw.Shared;
using OpenClaw.Connection;

namespace OpenClaw.Connection.Tests;

public class GatewayConnectionManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GatewayRegistry _registry;
    private readonly MockCredentialResolver _resolver;
    private readonly MockClientFactory _factory;
    private readonly GatewayConnectionManager _manager;

    public GatewayConnectionManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openclaw-mgr-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _registry = new GatewayRegistry(_tempDir);
        _resolver = new MockCredentialResolver();
        _factory = new MockClientFactory();
        _manager = new GatewayConnectionManager(
            _resolver, _factory, _registry, NullLogger.Instance);
    }

    public void Dispose()
    {
        _manager.Dispose();
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void InitialState_IsIdle()
    {
        Assert.Equal(OverallConnectionState.Idle, _manager.CurrentSnapshot.OverallState);
        Assert.Null(_manager.OperatorClient);
        Assert.Null(_manager.ActiveGatewayUrl);
    }

    [Fact]
    public async Task ConnectAsync_WithNoGateway_DoesNothing()
    {
        await _manager.ConnectAsync();
        Assert.Equal(OverallConnectionState.Idle, _manager.CurrentSnapshot.OverallState);
    }

    [Fact]
    public async Task ConnectAsync_WithNoCredential_TransitionsToError()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = null;

        GatewayConnectionSnapshot? lastSnap = null;
        _manager.StateChanged += (_, s) => lastSnap = s;

        await _manager.ConnectAsync("gw-1");

        Assert.Equal(OverallConnectionState.Error, _manager.CurrentSnapshot.OverallState);
        Assert.NotNull(lastSnap);
    }

    [Fact]
    public async Task ConnectAsync_WithCredential_TransitionsToConnecting()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");

        Assert.Equal(OverallConnectionState.Connecting, _manager.CurrentSnapshot.OverallState);
        Assert.Equal("wss://test", _manager.ActiveGatewayUrl);
        Assert.Equal("gw-1", _manager.CurrentSnapshot.GatewayId);
    }

    /// <summary>
    /// Regression guard for the post-onboarding "don't cancel an in-flight reconnect"
    /// path in App.OnboardingCompleted. When the V2 GatewayWelcome wizard saves a new
    /// provider/model config the gateway emits a 1012 shutdown and clients enter the
    /// Connecting state via the auto-reconnect timer. The App handler must see
    /// OperatorState == Connecting from CurrentSnapshot (without poking OperatorClient
    /// internals) so it can skip the redundant reconnect call.
    /// </summary>
    [Fact]
    public async Task CurrentSnapshot_OperatorState_IsConnecting_WhileConnectInFlight()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");

        // Mid-connect (handshake not yet succeeded): operator role-state should be Connecting,
        // and the overall snapshot mirrors it. This is the signal App.OnboardingCompleted
        // uses to avoid canceling an in-flight reconnect from a gateway-restart event.
        Assert.Equal(RoleConnectionState.Connecting, _manager.CurrentSnapshot.OperatorState);
        Assert.Equal(OverallConnectionState.Connecting, _manager.CurrentSnapshot.OverallState);
    }

    [Fact]
    public async Task ConnectAsync_CreatesClient()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");

        Assert.Single(_factory.CreatedClients);
        Assert.NotNull(_manager.OperatorClient);
    }

    [Fact]
    public async Task DisconnectAsync_TransitionsToIdle()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");
        await _manager.ConnectAsync("gw-1");

        await _manager.DisconnectAsync();

        Assert.Equal(OverallConnectionState.Idle, _manager.CurrentSnapshot.OverallState);
        Assert.Null(_manager.OperatorClient);
    }

    [Fact]
    public async Task SwitchGatewayAsync_DisconnectsAndReconnects()
    {
        SetupGateway("gw-1", "wss://test1");
        SetupGateway("gw-2", "wss://test2");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");
        await _manager.SwitchGatewayAsync("gw-2");

        Assert.Equal("gw-2", _manager.CurrentSnapshot.GatewayId);
        Assert.Equal("wss://test2", _manager.ActiveGatewayUrl);
    }

    [Fact]
    public async Task SwitchGatewayAsync_UsesTargetGatewayIdentityAndCredential()
    {
        _registry.AddOrUpdate(new GatewayRecord { Id = "gw-1", Url = "wss://test1" });
        _registry.AddOrUpdate(new GatewayRecord { Id = "gw-2", Url = "wss://test2" });
        _registry.SetActive("gw-1");

        var identity1 = new DeviceIdentity(_registry.GetIdentityDirectory("gw-1"), NullLogger.Instance);
        identity1.Initialize();
        identity1.StoreDeviceTokenForRole("operator", "operator-token-1");
        var identity2 = new DeviceIdentity(_registry.GetIdentityDirectory("gw-2"), NullLogger.Instance);
        identity2.Initialize();
        identity2.StoreDeviceTokenForRole("operator", "operator-token-2");

        var resolver = new CredentialResolver(new DeviceIdentityFileReader());
        var factory = new MockClientFactory();
        using var manager = new GatewayConnectionManager(
            resolver,
            factory,
            _registry,
            NullLogger.Instance);

        await manager.ConnectAsync("gw-1");
        await manager.SwitchGatewayAsync("gw-2");

        Assert.Equal(["operator-token-1", "operator-token-2"], factory.CreatedCredentials.Select(c => c.Token).ToArray());
        Assert.Equal(
            [_registry.GetIdentityDirectory("gw-1"), _registry.GetIdentityDirectory("gw-2")],
            factory.CreatedIdentityPaths);
        Assert.Equal(["wss://test1", "wss://test2"], factory.CreatedGatewayUrls);
        Assert.Equal("gw-2", _registry.ActiveGatewayId);
    }

    [Fact]
    public async Task StateChanged_Fires_OnConnect()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        var snapshots = new List<GatewayConnectionSnapshot>();
        _manager.StateChanged += (_, s) => snapshots.Add(s);

        await _manager.ConnectAsync("gw-1");

        Assert.NotEmpty(snapshots);
        Assert.Contains(snapshots, s => s.OverallState == OverallConnectionState.Connecting);
    }

    [Fact]
    public async Task DiagnosticEvent_Fires_OnCredentialResolution()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test.source");

        var events = new List<ConnectionDiagnosticEvent>();
        _manager.DiagnosticEvent += (_, e) => events.Add(e);

        await _manager.ConnectAsync("gw-1");

        Assert.Contains(events, e => e.Category == "credential");
    }

    [Fact]
    public async Task Dispose_CleansUp()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");
        await _manager.ConnectAsync("gw-1");

        _manager.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _manager.ConnectAsync("gw-1").GetAwaiter().GetResult());
    }

    [Fact]
    public async Task DisposeAsync_AwaitsNodeDisconnect()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");
        var nodeConnector = new BlockingNodeDisconnectConnector();
        await using var manager = new GatewayConnectionManager(
            _resolver, _factory, _registry, NullLogger.Instance,
            nodeConnector: nodeConnector);

        await manager.ConnectAsync("gw-1");
        nodeConnector.BlockDisconnects = true;

        var disposeTask = manager.DisposeAsync().AsTask();
        await nodeConnector.DisconnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(disposeTask.IsCompleted);

        nodeConnector.AllowDisconnect.SetResult(true);
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.ConnectAsync("gw-1"));
    }

    [Fact]
    public void Diagnostics_IsAccessible()
    {
        Assert.NotNull(_manager.Diagnostics);
        Assert.Equal(0, _manager.Diagnostics.Count);
    }

    [Fact]
    public async Task HandshakeSucceeded_RespectsShouldStartNodeConnectionGate_WhenFalse()
    {
        // The shouldStartNodeConnection delegate (on the manager constructor) is a
        // generic per-gateway gate. Pre-unification the App used it to defer to a
        // legacy NodeService for local gateways; post-unification the App no longer
        // wires this predicate, but the gate itself remains a useful seam for callers.
        SetupGateway("gw-local", "ws://localhost:18789", isLocal: true);
        _resolver.OperatorCredential = new GatewayCredential("op-tok", false, "test");
        _resolver.NodeCredential = new GatewayCredential("node-tok", false, "test");
        var nodeConnector = new CountingNodeConnector();
        using var manager = new GatewayConnectionManager(
            _resolver, _factory, _registry, NullLogger.Instance,
            nodeConnector: nodeConnector,
            shouldStartNodeConnection: (record, _) => !record.IsLocal);

        await manager.ConnectAsync("gw-local");
        await InvokeHandshakeSucceededAsync(manager);

        Assert.Equal(0, nodeConnector.ConnectCount);
    }

    [Fact]
    public async Task HandshakeSucceeded_StartsManagerNodeConnector_WhenGateAllows()
    {
        SetupGateway("gw-remote", "wss://remote.example", isLocal: false);
        _resolver.OperatorCredential = new GatewayCredential("op-tok", false, "test");
        _resolver.NodeCredential = new GatewayCredential("node-tok", false, "test");
        var nodeConnector = new CountingNodeConnector();
        using var manager = new GatewayConnectionManager(
            _resolver, _factory, _registry, NullLogger.Instance,
            nodeConnector: nodeConnector,
            shouldStartNodeConnection: (record, _) => !record.IsLocal);

        await manager.ConnectAsync("gw-remote");
        await InvokeHandshakeSucceededAsync(manager);

        Assert.Equal(1, nodeConnector.ConnectCount);
        Assert.Equal("wss://remote.example", nodeConnector.LastGatewayUrl);
    }

    [Fact]
    public async Task ConnectAsync_WithPersistedV2Requirement_SetsClientUseV2Signature()
    {
        _registry.AddOrUpdate(new GatewayRecord
        {
            Id = "gw-remote",
            Url = "wss://remote.example",
            RequiresV2Signature = true
        });
        _registry.SetActive("gw-remote");
        _resolver.OperatorCredential = new GatewayCredential("op-tok", false, "test");

        await _manager.ConnectAsync("gw-remote");

        Assert.True(_factory.CreatedClients[0].DataClient.UseV2Signature);
    }

    [Fact]
    public async Task V2SignatureFallback_PersistsGatewayRequirement()
    {
        SetupGateway("gw-remote", "wss://remote.example", isLocal: false);
        _resolver.OperatorCredential = new GatewayCredential("op-tok", false, "test");

        await _manager.ConnectAsync("gw-remote");

        var lifecycle = _factory.CreatedClients[0];
        lifecycle.SimulateV2SignatureFallback();

        Assert.True(_registry.GetById("gw-remote")?.RequiresV2Signature);
    }

    [Fact]
    public async Task AuthenticationFailed_DeviceTokenMismatchWithBootstrap_ReconnectsWithBootstrap()
    {
        _registry.AddOrUpdate(new GatewayRecord
        {
            Id = "gw-remote",
            Url = "wss://remote.example",
            BootstrapToken = "bootstrap-token"
        });
        _registry.SetActive("gw-remote");

        var identityDir = _registry.GetIdentityDirectory("gw-remote");
        var identity = new DeviceIdentity(identityDir, NullLogger.Instance);
        identity.Initialize();
        identity.StoreDeviceToken("stale-device-token");

        var resolver = new CredentialResolver(new DeviceIdentityFileReader());
        var factory = new MockClientFactory();
        using var manager = new GatewayConnectionManager(
            resolver,
            factory,
            _registry,
            NullLogger.Instance,
            reconnectDelay: _ => Task.CompletedTask);

        await manager.ConnectAsync("gw-remote");
        Assert.Equal(CredentialResolver.SourceDeviceToken, factory.CreatedCredentials[0].Source);

        factory.CreatedClients[0].SimulateAuthFailed("unauthorized: device token mismatch (rotate/reissue device token)");

        await WaitUntilAsync(() => factory.CreatedCredentials.Count >= 2);

        Assert.Null(DeviceIdentity.TryReadStoredDeviceToken(identityDir));
        Assert.Equal(CredentialResolver.SourceBootstrapToken, factory.CreatedCredentials[1].Source);
        Assert.True(factory.CreatedCredentials[1].IsBootstrapToken);
    }

    [Fact]
    public async Task AuthenticationFailed_DeviceTokenMismatchAfterSuccessfulRecovery_CanRecoverAgain()
    {
        _registry.AddOrUpdate(new GatewayRecord
        {
            Id = "gw-remote",
            Url = "wss://remote.example",
            BootstrapToken = "bootstrap-token"
        });
        _registry.SetActive("gw-remote");

        var identityDir = _registry.GetIdentityDirectory("gw-remote");
        var identity = new DeviceIdentity(identityDir, NullLogger.Instance);
        identity.Initialize();
        identity.StoreDeviceToken("stale-device-token-1");

        var resolver = new CredentialResolver(new DeviceIdentityFileReader());
        var factory = new MockClientFactory();
        using var manager = new GatewayConnectionManager(
            resolver,
            factory,
            _registry,
            NullLogger.Instance,
            reconnectDelay: _ => Task.CompletedTask);

        await manager.ConnectAsync("gw-remote");
        factory.CreatedClients[0].SimulateAuthFailed("AUTH_DEVICE_TOKEN_MISMATCH");
        await WaitUntilAsync(() => factory.CreatedCredentials.Count >= 2);

        factory.CreatedClients[1].SimulateHandshake();
        await WaitUntilAsync(() => manager.CurrentSnapshot.OperatorState == RoleConnectionState.Connected);

        identity.Initialize();
        identity.StoreDeviceToken("stale-device-token-2");

        await manager.ReconnectAsync();
        await WaitUntilAsync(() => factory.CreatedCredentials.Count >= 3);
        Assert.Equal(CredentialResolver.SourceDeviceToken, factory.CreatedCredentials[2].Source);

        factory.CreatedClients[2].SimulateAuthFailed("AUTH_DEVICE_TOKEN_MISMATCH");
        await WaitUntilAsync(() => factory.CreatedCredentials.Count >= 4);

        Assert.Null(DeviceIdentity.TryReadStoredDeviceToken(identityDir));
        Assert.Equal(CredentialResolver.SourceBootstrapToken, factory.CreatedCredentials[3].Source);
        Assert.True(factory.CreatedCredentials[3].IsBootstrapToken);
    }

    [Fact]
    public async Task HandshakeSucceeded_StartsNodeConnectorWithPersistedV2Requirement()
    {
        _registry.AddOrUpdate(new GatewayRecord
        {
            Id = "gw-remote",
            Url = "wss://remote.example",
            RequiresV2Signature = true
        });
        _registry.SetActive("gw-remote");
        _resolver.OperatorCredential = new GatewayCredential("op-tok", false, "test");
        _resolver.NodeCredential = new GatewayCredential("node-tok", false, "test");
        var nodeConnector = new CountingNodeConnector();
        using var manager = new GatewayConnectionManager(
            _resolver, _factory, _registry, NullLogger.Instance,
            nodeConnector: nodeConnector,
            shouldStartNodeConnection: (_, _) => true);

        await manager.ConnectAsync("gw-remote");
        await InvokeHandshakeSucceededAsync(manager);

        Assert.Equal(1, nodeConnector.ConnectCount);
        Assert.True(nodeConnector.LastUseV2Signature);
    }

    [Fact]
    public async Task ChatPageNavigationReadiness_DoesNotCompleteUntilHandshakeSucceeded()
    {
        SetupGateway("gw-chat", "ws://localhost:18789", isLocal: true);
        _resolver.OperatorCredential = new GatewayCredential("op-tok", false, "test");

        await _manager.ConnectAsync("gw-chat");

        var readiness = ChatNavigationReadiness.WaitForOperatorHandshakeAsync(
            _manager,
            TimeSpan.FromSeconds(5));

        Assert.False(readiness.IsCompleted);

        await InvokeHandshakeSucceededAsync(_manager);

        Assert.True(await readiness);
    }

    // ─── Helpers ───

    private void SetupGateway(string id, string url, bool isLocal = false)
    {
        _registry.AddOrUpdate(new GatewayRecord { Id = id, Url = url, IsLocal = isLocal });
        _registry.SetActive(id);
    }

    private static async Task InvokeHandshakeSucceededAsync(GatewayConnectionManager manager)
    {
        var method = typeof(GatewayConnectionManager).GetMethod(
            "HandleHandshakeSucceededAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(manager, [1L])!;
        await task;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Condition was not met before the timeout.");

            // slopwatch-ignore: SW004 Test delay is an intentional bounded async wait; replacing it would change the scenario under test.
            await Task.Delay(20);
        }
    }

    // ─── EnsureNodeConnectedAsync tests ───

    [Fact]
    public async Task EnsureNodeConnectedAsync_OperatorNotConnected_Throws()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");
        var node = new ScriptedNodeConnector();
        using var manager = new GatewayConnectionManager(
            _resolver, _factory, _registry, NullLogger.Instance,
            nodeConnector: node);

        // ConnectAsync only transitions to Connecting; HandshakeSucceeded would be needed to reach Connected.
        await manager.ConnectAsync("gw-1");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.EnsureNodeConnectedAsync());
        Assert.Contains("Operator must be Connected", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, node.ConnectCount);
    }

    [Fact]
    public async Task EnsureNodeConnectedAsync_NoConnector_Throws()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        // _manager has no node connector wired
        await _manager.ConnectAsync("gw-1");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _manager.EnsureNodeConnectedAsync());
    }

    [Fact]
    public async Task EnsureNodeConnectedAsync_AlreadyPaired_NoOp()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("op", false, "test");
        _resolver.NodeCredential = new GatewayCredential("nd", false, "test");
        var node = new ScriptedNodeConnector
        {
            ConnectAction = (s, _) =>
            {
                s.SimulateStatus(ConnectionStatus.Connected);
                s.SimulatePairing(PairingStatus.Paired);
            }
        };
        using var manager = new GatewayConnectionManager(
            _resolver, _factory, _registry, NullLogger.Instance,
            nodeConnector: node);

        await manager.ConnectAsync("gw-1");
        await InvokeHandshakeSucceededAsync(manager);

        await manager.EnsureNodeConnectedAsync();
        var firstCount = node.ConnectCount;
        await manager.EnsureNodeConnectedAsync();

        // Second call must short-circuit (no new connect)
        Assert.Equal(firstCount, node.ConnectCount);
    }

    [Fact]
    public async Task ConnectNodeOnlyAsync_UsesNodeCredential_WhenOperatorCredentialMissing()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = null;
        _resolver.NodeCredential = new GatewayCredential(
            "node-token",
            IsBootstrapToken: false,
            Source: CredentialResolver.SourceNodeDeviceToken);
        var node = new CountingNodeConnector();
        using var manager = new GatewayConnectionManager(
            _resolver,
            _factory,
            _registry,
            NullLogger.Instance,
            nodeConnector: node);

        await manager.ConnectNodeOnlyAsync("gw-1");

        Assert.Empty(_factory.CreatedCredentials);
        Assert.Equal(1, node.ConnectCount);
        Assert.Equal("wss://test", node.LastGatewayUrl);
    }

    [Fact]
    public async Task ConnectNodeOnlyAsync_PreservesConnectedOperatorForNodeListRefresh()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("operator-token", false, "test");
        _resolver.NodeCredential = new GatewayCredential("node-token", false, "test");
        var node = new CountingNodeConnector();
        using var manager = new GatewayConnectionManager(
            _resolver,
            _factory,
            _registry,
            NullLogger.Instance,
            nodeConnector: node);

        await manager.ConnectAsync("gw-1");
        await InvokeHandshakeSucceededAsync(manager);
        var operatorLifecycle = Assert.Single(_factory.CreatedClients);
        var operatorClient = manager.OperatorClient;

        await manager.ConnectNodeOnlyAsync();

        Assert.False(operatorLifecycle.IsDisposed);
        Assert.Same(operatorClient, manager.OperatorClient);
        Assert.Single(_factory.CreatedClients);
        Assert.Equal(1, node.ConnectCount);
    }

    [Theory]
    [InlineData("gw-2", "wss://test-1", false, "wss://test-1")]
    [InlineData("gw-1", "wss://test-2", false, "wss://test-2")]
    [InlineData("gw-1", "wss://test-1", true, "ws://localhost:45678")]
    public async Task ConnectNodeOnlyAsync_ChangedGatewayConnectionDisposesConnectedOperator(
        string targetId,
        string targetUrl,
        bool addTunnel,
        string expectedNodeUrl)
    {
        SetupGateway("gw-1", "wss://test-1");
        _resolver.OperatorCredential = new GatewayCredential("operator-token", false, "test");
        _resolver.NodeCredential = new GatewayCredential("node-token", false, "test");
        var node = new CountingNodeConnector();
        using var manager = new GatewayConnectionManager(
            _resolver,
            _factory,
            _registry,
            NullLogger.Instance,
            nodeConnector: node);

        await manager.ConnectAsync("gw-1");
        await InvokeHandshakeSucceededAsync(manager);
        var operatorLifecycle = Assert.Single(_factory.CreatedClients);
        _registry.AddOrUpdate(new GatewayRecord
        {
            Id = targetId,
            Url = targetUrl,
            SshTunnel = addTunnel
                ? new SshTunnelConfig("user", "host.example", 18789, 45678)
                : null
        });
        _registry.SetActive(targetId);

        await manager.ConnectNodeOnlyAsync(targetId);

        Assert.True(operatorLifecycle.IsDisposed);
        Assert.Null(manager.OperatorClient);
        Assert.Equal(expectedNodeUrl, node.LastGatewayUrl);
    }

    [Fact]
    public async Task ConnectNodeOnlyAsync_StartsSshTunnel_WhenGatewayUsesTunnel()
    {
        _registry.AddOrUpdate(new GatewayRecord
        {
            Id = "gw-ssh",
            Url = "wss://remote.example",
            SshTunnel = new SshTunnelConfig("user", "host.example", 18789, 45678, SshPort: 2222)
        });
        _registry.SetActive("gw-ssh");
        _resolver.OperatorCredential = null;
        _resolver.NodeCredential = new GatewayCredential(
            "node-token",
            IsBootstrapToken: false,
            Source: CredentialResolver.SourceNodeDeviceToken);
        var node = new CountingNodeConnector();
        var tunnel = new CountingTunnelManager();
        using var manager = new GatewayConnectionManager(
            _resolver,
            _factory,
            _registry,
            NullLogger.Instance,
            nodeConnector: node,
            tunnelManager: tunnel);

        await manager.ConnectNodeOnlyAsync("gw-ssh");

        Assert.Equal(1, tunnel.StartCount);
        Assert.Equal("host.example", tunnel.LastConfig?.Host);
        Assert.Equal(2222, tunnel.LastConfig?.SshPort);
        Assert.Equal("ws://localhost:45678", node.LastGatewayUrl);
    }

    [Fact]
    public async Task ConnectAsync_StartsSshTunnelAndUsesTunnelUrl_WhenGatewayUsesTunnel()
    {
        _registry.AddOrUpdate(new GatewayRecord
        {
            Id = "gw-ssh",
            Url = "wss://remote.example",
            SshTunnel = new SshTunnelConfig(
                "user",
                "host.example",
                RemotePort: 18789,
                LocalPort: 45678,
                IncludeBrowserProxyForward: true,
                SshPort: 2222)
        });
        _registry.SetActive("gw-ssh");
        _resolver.OperatorCredential = new GatewayCredential(
            "operator-token",
            IsBootstrapToken: false,
            Source: CredentialResolver.SourceSharedGatewayToken);
        var tunnel = new CountingTunnelManager();
        using var manager = new GatewayConnectionManager(
            _resolver,
            _factory,
            _registry,
            NullLogger.Instance,
            tunnelManager: tunnel);

        await manager.ConnectAsync("gw-ssh");

        Assert.Equal(1, tunnel.StartCount);
        Assert.Equal("user", tunnel.LastConfig?.User);
        Assert.Equal("host.example", tunnel.LastConfig?.Host);
        Assert.Equal(18789, tunnel.LastConfig?.RemotePort);
        Assert.Equal(45678, tunnel.LastConfig?.LocalPort);
        Assert.True(tunnel.LastConfig?.IncludeBrowserProxyForward);
        Assert.Equal(2222, tunnel.LastConfig?.SshPort);
        Assert.Equal(["ws://localhost:45678"], _factory.CreatedGatewayUrls);
    }

    [Fact]
    public async Task EnsureNodeConnectedAsync_HappyPath_ReturnsWhenPaired()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("op", false, "test");
        _resolver.NodeCredential = new GatewayCredential("nd", false, "test");
        var node = new ScriptedNodeConnector
        {
            ConnectAction = (s, _) =>
            {
                s.SimulateStatus(ConnectionStatus.Connected);
                s.SimulatePairing(PairingStatus.Paired);
            }
        };
        using var manager = new GatewayConnectionManager(
            _resolver, _factory, _registry, NullLogger.Instance,
            nodeConnector: node,
            // Suppress auto-start to mimic the easy-button path: setup engine drives it.
            shouldStartNodeConnection: (_, _) => false);

        await manager.ConnectAsync("gw-1");
        await InvokeHandshakeSucceededAsync(manager);

        Assert.Equal(0, node.ConnectCount); // suppressed auto-start

        await manager.EnsureNodeConnectedAsync();

        Assert.Equal(1, node.ConnectCount);
        Assert.Equal(RoleConnectionState.Connected, manager.CurrentSnapshot.NodeState);
        Assert.Equal(PairingStatus.Paired, manager.CurrentSnapshot.NodePairingStatus);
    }

    [Fact]
    public async Task EnsureNodeConnectedAsync_PairingRejected_Throws()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("op", false, "test");
        _resolver.NodeCredential = new GatewayCredential("nd", false, "test");
        var node = new ScriptedNodeConnector
        {
            ConnectAction = (s, _) =>
            {
                s.SimulateStatus(ConnectionStatus.Connecting);
                s.SimulatePairing(PairingStatus.Rejected);
            }
        };
        using var manager = new GatewayConnectionManager(
            _resolver, _factory, _registry, NullLogger.Instance,
            nodeConnector: node);

        await manager.ConnectAsync("gw-1");
        await InvokeHandshakeSucceededAsync(manager);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.EnsureNodeConnectedAsync());
    }

    [Fact]
    public async Task EnsureNodeConnectedAsync_NodeError_Throws()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("op", false, "test");
        _resolver.NodeCredential = new GatewayCredential("nd", false, "test");
        var node = new ScriptedNodeConnector
        {
            // NodeError trigger requires NodeState != Idle, so transition through Connecting first.
            ConnectAction = (s, _) =>
            {
                s.SimulateStatus(ConnectionStatus.Connecting);
                s.SimulateStatus(ConnectionStatus.Error);
            }
        };
        using var manager = new GatewayConnectionManager(
            _resolver, _factory, _registry, NullLogger.Instance,
            nodeConnector: node);

        await manager.ConnectAsync("gw-1");
        await InvokeHandshakeSucceededAsync(manager);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.EnsureNodeConnectedAsync());
    }

    [Fact]
    public async Task EnsureNodeConnectedAsync_CallerCancellation_PropagatesOperationCanceled()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("op", false, "test");
        _resolver.NodeCredential = new GatewayCredential("nd", false, "test");
        var node = new ScriptedNodeConnector
        {
            // Connect but never reach Paired — caller will cancel
            ConnectAction = (s, _) => s.SimulateStatus(ConnectionStatus.Connecting)
        };
        using var manager = new GatewayConnectionManager(
            _resolver, _factory, _registry, NullLogger.Instance,
            nodeConnector: node);

        await manager.ConnectAsync("gw-1");
        await InvokeHandshakeSucceededAsync(manager);

        using var cts = new CancellationTokenSource();
        var task = manager.EnsureNodeConnectedAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    // ─── Mocks ───

    private sealed class MockCredentialResolver : ICredentialResolver
    {
        public GatewayCredential? OperatorCredential { get; set; }
        public GatewayCredential? NodeCredential { get; set; }

        public GatewayCredential? ResolveOperator(GatewayRecord record, string identityPath) => OperatorCredential;
        public GatewayCredential? ResolveNode(GatewayRecord record, string identityPath) => NodeCredential;
    }

    private sealed class MockClientFactory : IGatewayClientFactory
    {
        public List<MockLifecycle> CreatedClients { get; } = [];
        public List<GatewayCredential> CreatedCredentials { get; } = [];
        public List<string> CreatedIdentityPaths { get; } = [];
        public List<string> CreatedGatewayUrls { get; } = [];

        public IGatewayClientLifecycle Create(string gatewayUrl, GatewayCredential credential, string identityPath, IOpenClawLogger logger)
        {
            var mock = new MockLifecycle(gatewayUrl);
            CreatedClients.Add(mock);
            CreatedCredentials.Add(credential);
            CreatedIdentityPaths.Add(identityPath);
            CreatedGatewayUrls.Add(gatewayUrl);
            return mock;
        }
    }

    internal sealed class MockLifecycle : IGatewayClientLifecycle
    {
        private readonly MockGatewayClient _client;

        public MockLifecycle(string url)
        {
            _client = new MockGatewayClient(url);
        }

        public OpenClawGatewayClient DataClient => _client;
        public bool IsDisposed { get; private set; }
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<string>? AuthenticationFailed;

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

        public void SimulateStatusChanged(ConnectionStatus status) =>
            StatusChanged?.Invoke(this, status);

        public void SimulateAuthFailed(string msg) =>
            AuthenticationFailed?.Invoke(this, msg);

        public void SimulateHandshake() =>
            _client.SimulateHandshakeSucceeded();

        public void SimulateV2SignatureFallback() =>
            _client.SimulateV2SignatureFallback();

        public void SimulateDeviceTokenReceived(string token, string role, string[]? scopes = null) =>
            _client.SimulateDeviceTokenReceived(token, role, scopes);

        public void Dispose() => IsDisposed = true;
    }

    private sealed class MockGatewayClient : OpenClawGatewayClient
    {
        public MockGatewayClient(string url)
            : base(url, "mock-token", NullLogger.Instance) { }

        /// <summary>Simulate a successful hello-ok handshake for testing.</summary>
        public void SimulateHandshakeSucceeded()
        {
            // Fire the HandshakeSucceeded event to trigger the manager's handler
            OnHandshakeSucceeded();
        }

        public void SimulateV2SignatureFallback()
        {
            var field = typeof(OpenClawGatewayClient).GetField(
                nameof(V2SignatureFallback),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (field != null)
            {
                var handler = field.GetValue(this) as EventHandler;
                handler?.Invoke(this, EventArgs.Empty);
            }
        }

        // Protected invoker — OpenClawGatewayClient.HandshakeSucceeded is a public event.
        // We use reflection because the event doesn't have a virtual invoker.
        private void OnHandshakeSucceeded()
        {
            var field = typeof(OpenClawGatewayClient).GetField(
                nameof(HandshakeSucceeded),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            // Events compiled as backing fields in C# are named the same as the event.
            // In case the compiler generates a different name, fall back to raising through the base.
            if (field != null)
            {
                var handler = field.GetValue(this) as EventHandler;
                handler?.Invoke(this, EventArgs.Empty);
            }
        }

        public void SimulateDeviceTokenReceived(string token, string role, string[]? scopes = null)
        {
            var field = typeof(OpenClawGatewayClient).GetField(
                nameof(DeviceTokenReceived),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (field != null)
            {
                var handler = field.GetValue(this) as EventHandler<DeviceTokenReceivedEventArgs>;
                handler?.Invoke(this, new DeviceTokenReceivedEventArgs(token, scopes, role));
            }
        }
    }

    [Fact]
    public async Task HandshakeSucceeded_StampsLastConnectedOnGatewayRecord()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");

        // Simulate successful handshake
        var lifecycle = _factory.CreatedClients[0];
        lifecycle.SimulateHandshake();

        await WaitUntilAsync(() => _registry.GetById("gw-1")?.LastConnected is not null);

        var record = _registry.GetById("gw-1");
        Assert.NotNull(record?.LastConnected);
    }

    [Fact]
    public async Task HandshakeSucceeded_PreservesOtherRecordFields()
    {
        _registry.AddOrUpdate(new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://test",
            SharedGatewayToken = "shared-tok",
            FriendlyName = "TestGW"
        });
        _registry.SetActive("gw-1");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");

        var lifecycle = _factory.CreatedClients[0];
        lifecycle.SimulateHandshake();
        await WaitUntilAsync(() => _registry.GetById("gw-1")?.LastConnected is not null);

        var record = _registry.GetById("gw-1")!;
        Assert.True(record.LastConnected.HasValue);
        Assert.Equal("shared-tok", record.SharedGatewayToken);
        Assert.Equal("TestGW", record.FriendlyName);
    }

    // ─── DeviceTokenReceived / bootstrap handoff tests ───

    [Fact]
    public async Task DeviceTokenReceived_ClearsBootstrapOnlyAfterOperatorAndNodeTokensAreDurable()
    {
        _registry.AddOrUpdate(new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://test",
            BootstrapToken = "bs-secret"
        });
        _registry.SetActive("gw-1");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");
        var lifecycle = _factory.CreatedClients[0];

        var identityDir = _registry.GetIdentityDirectory("gw-1");
        var identity = new DeviceIdentity(identityDir, NullLogger.Instance);
        identity.Initialize();
        identity.StoreDeviceTokenForRole("node", "node-device-token");

        lifecycle.SimulateDeviceTokenReceived("node-device-token", "node");

        var updated = _registry.GetById("gw-1");
        Assert.Equal("bs-secret", updated?.BootstrapToken);

        identity.Initialize();
        identity.StoreDeviceTokenForRole("operator", "op-device-token", ["operator.read"]);

        lifecycle.SimulateDeviceTokenReceived("op-device-token", "operator", ["operator.read"]);

        updated = _registry.GetById("gw-1");
        Assert.Null(updated?.BootstrapToken);
    }

    [Fact]
    public async Task DeviceTokenReceived_OperatorRole_PreservesBootstrapTokenUntilNodeTokenIsDurable()
    {
        _registry.AddOrUpdate(new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://test",
            BootstrapToken = "bs-secret"
        });
        _registry.SetActive("gw-1");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");
        var lifecycle = _factory.CreatedClients[0];

        var identityDir = _registry.GetIdentityDirectory("gw-1");
        var identity = new DeviceIdentity(identityDir, NullLogger.Instance);
        identity.Initialize();
        identity.StoreDeviceTokenForRole("operator", "op-device-token", ["operator.read"]);

        lifecycle.SimulateDeviceTokenReceived("op-device-token", "operator");

        var record = _registry.GetById("gw-1");
        Assert.Equal("bs-secret", record?.BootstrapToken);
    }

    [Fact]
    public async Task DeviceTokenReceived_OperatorRole_AfterBootstrapConnect_ReconnectsUsingV2Signature()
    {
        _registry.AddOrUpdate(new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://test",
            BootstrapToken = "bs-secret"
        });
        _registry.SetActive("gw-1");
        _resolver.OperatorCredential = new GatewayCredential("bs-secret", true, CredentialResolver.SourceBootstrapToken);

        await _manager.ConnectAsync("gw-1");
        var lifecycle = _factory.CreatedClients[0];

        var identityDir = _registry.GetIdentityDirectory("gw-1");
        var identity = new DeviceIdentity(identityDir, NullLogger.Instance);
        identity.Initialize();
        identity.StoreDeviceTokenForRole("operator", "op-device-token", ["operator.read"]);

        lifecycle.SimulateDeviceTokenReceived("op-device-token", "operator", ["operator.read"]);

        await WaitUntilAsync(() => _factory.CreatedClients.Count >= 2);
        Assert.True(_factory.CreatedClients[1].DataClient.UseV2Signature);
    }

    [Fact]
    public async Task NodeDeviceTokenReceived_ClearsBootstrapWhenNodeTokenBecomesDurableAfterOperatorToken()
    {
        _registry.AddOrUpdate(new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://test",
            BootstrapToken = "bs-secret"
        });
        _registry.SetActive("gw-1");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");
        var node = new ScriptedNodeConnector();
        using var manager = new GatewayConnectionManager(
            _resolver,
            _factory,
            _registry,
            NullLogger.Instance,
            nodeConnector: node);

        await manager.ConnectAsync("gw-1");
        var lifecycle = _factory.CreatedClients[0];
        var identityDir = _registry.GetIdentityDirectory("gw-1");
        var identity = new DeviceIdentity(identityDir, NullLogger.Instance);
        identity.Initialize();
        identity.StoreDeviceTokenForRole("operator", "op-device-token", ["operator.read"]);

        lifecycle.SimulateDeviceTokenReceived("op-device-token", "operator", ["operator.read"]);
        Assert.Equal("bs-secret", _registry.GetById("gw-1")?.BootstrapToken);

        identity.Initialize();
        identity.StoreDeviceTokenForRole("node", "node-device-token");
        node.SimulateDeviceTokenReceived("node-device-token");

        await WaitUntilAsync(() => _registry.GetById("gw-1")?.BootstrapToken == null);
    }

    [Fact]
    public async Task DeviceTokenReceived_NodeRole_WhenBootstrapAlreadyNull_Succeeds()
    {
        _registry.AddOrUpdate(new GatewayRecord { Id = "gw-1", Url = "wss://test" });
        _registry.SetActive("gw-1");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");
        var lifecycle = _factory.CreatedClients[0];

        // Should not throw even when bootstrap is already null
        lifecycle.SimulateDeviceTokenReceived("node-device-token", "node");

        var record = _registry.GetById("gw-1");
        Assert.Null(record?.BootstrapToken);
    }

    [Fact]
    public async Task DeviceTokenReceived_WithIdentityStore_PersistsToken()
    {
        var capturedTokens = new List<(string path, string token, string role)>();
        var store = new CaptureIdentityStore(capturedTokens);
        using var manager = new GatewayConnectionManager(
            _resolver, _factory, _registry, NullLogger.Instance,
            identityStore: store);

        _registry.AddOrUpdate(new GatewayRecord { Id = "gw-1", Url = "wss://test" });
        _registry.SetActive("gw-1");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await manager.ConnectAsync("gw-1");
        var lifecycle = _factory.CreatedClients[0];
        lifecycle.SimulateDeviceTokenReceived("op-device-token", "operator");

        Assert.Single(capturedTokens, t => t.token == "op-device-token" && t.role == "operator");
    }

    private sealed class CaptureIdentityStore : IDeviceIdentityStore
    {
        private readonly List<(string path, string token, string role)> _captured;
        public CaptureIdentityStore(List<(string, string, string)> captured) => _captured = captured;
        public void StoreToken(string identityPath, string token, string[]? scopes, string role) =>
            _captured.Add((identityPath, token, role));
    }

    private sealed class CountingNodeConnector : INodeConnector
    {
        public int ConnectCount { get; private set; }
        public string? LastGatewayUrl { get; private set; }
        public bool LastUseV2Signature { get; private set; }
        public bool IsConnected => ConnectCount > 0;
        public PairingStatus PairingStatus { get; private set; } = PairingStatus.Unknown;
        public string? NodeDeviceId => "test-node";
        public NodeConnectionMode Mode => IsConnected ? NodeConnectionMode.Gateway : NodeConnectionMode.Disabled;

#pragma warning disable CS0067 // Events required by interface but not fired in tests
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;
        public event EventHandler<DeviceTokenReceivedEventArgs>? DeviceTokenReceived;
        public event EventHandler<NodeClientCreatedEventArgs>? ClientCreated;
#pragma warning restore CS0067

        public Task ConnectAsync(string gatewayUrl, GatewayCredential credential, string identityPath, bool useV2Signature = false)
        {
            ConnectCount++;
            LastGatewayUrl = gatewayUrl;
            LastUseV2Signature = useV2Signature;
            PairingStatus = PairingStatus.Paired;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync() => Task.CompletedTask;

        public void Dispose() { }
    }

    private sealed class BlockingNodeDisconnectConnector : INodeConnector
    {
        public TaskCompletionSource<bool> DisconnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> AllowDisconnect { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockDisconnects { get; set; }
        public bool IsConnected => true;
        public PairingStatus PairingStatus => PairingStatus.Paired;
        public string? NodeDeviceId => "blocking-node";
        public NodeConnectionMode Mode => NodeConnectionMode.Gateway;

#pragma warning disable CS0067 // Events required by interface but not fired in tests
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;
        public event EventHandler<DeviceTokenReceivedEventArgs>? DeviceTokenReceived;
        public event EventHandler<NodeClientCreatedEventArgs>? ClientCreated;
#pragma warning restore CS0067

        public Task ConnectAsync(string gatewayUrl, GatewayCredential credential, string identityPath, bool useV2Signature = false)
            => Task.CompletedTask;

        public async Task DisconnectAsync()
        {
            if (!BlockDisconnects)
            {
                return;
            }

            DisconnectStarted.SetResult(true);
            await AllowDisconnect.Task;
        }

        public void Dispose() { }
    }

    private sealed class CountingTunnelManager : ISshTunnelManager
    {
        public int StartCount { get; private set; }
        public SshTunnelConfig? LastConfig { get; private set; }
        public bool IsActive => StartCount > 0;
        public string? LocalTunnelUrl { get; private set; }

        public Task<string> StartAsync(SshTunnelConfig config, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            StartCount++;
            LastConfig = config;
            LocalTunnelUrl = $"ws://localhost:{config.LocalPort}";
            return Task.FromResult(LocalTunnelUrl);
        }

        public Task StopAsync()
        {
            LocalTunnelUrl = null;
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Test connector that fires StatusChanged / PairingStatusChanged events synchronously
    /// so tests can drive the manager's state machine through realistic transitions.
    /// </summary>
    private sealed class ScriptedNodeConnector : INodeConnector
    {
        public int ConnectCount { get; private set; }
        public string? LastGatewayUrl { get; private set; }
        public bool IsConnected { get; private set; }
        public PairingStatus PairingStatus { get; private set; } = PairingStatus.Unknown;
        public string? NodeDeviceId => "scripted-node";
        public NodeConnectionMode Mode => IsConnected ? NodeConnectionMode.Gateway : NodeConnectionMode.Disabled;

        /// <summary>
        /// Optional callback fired during ConnectAsync. Receives this connector and the
        /// gateway URL — use SimulateStatus / SimulatePairing to walk the state machine.
        /// </summary>
        public Action<ScriptedNodeConnector, string>? ConnectAction { get; set; }

        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;
        public event EventHandler<DeviceTokenReceivedEventArgs>? DeviceTokenReceived;
#pragma warning disable CS0067 // ClientCreated unused in current tests
        public event EventHandler<NodeClientCreatedEventArgs>? ClientCreated;
#pragma warning restore CS0067

        public Task ConnectAsync(string gatewayUrl, GatewayCredential credential, string identityPath, bool useV2Signature = false)
        {
            ConnectCount++;
            LastGatewayUrl = gatewayUrl;
            ConnectAction?.Invoke(this, gatewayUrl);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            IsConnected = false;
            PairingStatus = PairingStatus.Unknown;
            return Task.CompletedTask;
        }

        public void SimulateStatus(ConnectionStatus status)
        {
            IsConnected = status == ConnectionStatus.Connected;
            StatusChanged?.Invoke(this, status);
        }

        public void SimulatePairing(PairingStatus status, string? requestId = null)
        {
            PairingStatus = status;
            PairingStatusChanged?.Invoke(this, new PairingStatusEventArgs(status, deviceId: "scripted-node", requestId: requestId));
        }

        public void SimulateDeviceTokenReceived(string token, string role = "node", string[]? scopes = null) =>
            DeviceTokenReceived?.Invoke(this, new DeviceTokenReceivedEventArgs(token, scopes, role));

        public void Dispose() { }
    }
}
