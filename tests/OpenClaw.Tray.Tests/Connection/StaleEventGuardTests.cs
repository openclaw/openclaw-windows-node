using OpenClaw.Shared;
using OpenClawTray.Services.Connection;

namespace OpenClawTray.Tests.Connection;

/// <summary>
/// Tests the generation-based stale event guard in GatewayConnectionManager.
/// Each ConnectAsync increments _generation; event handlers captured with an
/// older generation value are silently discarded, preventing stale connections
/// from corrupting state.
/// </summary>
public class StaleEventGuardTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GatewayRegistry _registry;
    private readonly MockCredentialResolver _resolver;
    private readonly TrackingClientFactory _factory;
    private readonly GatewayConnectionManager _manager;

    public StaleEventGuardTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        _registry = new GatewayRegistry(_tempDir);
        _resolver = new MockCredentialResolver();
        _factory = new TrackingClientFactory();
        _manager = new GatewayConnectionManager(
            _resolver, _factory, _registry, NullLogger.Instance);
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ─── Tests ───

    [Fact]
    public async Task ConnectAsync_IncrementsGeneration_OldLifecycleDisposed()
    {
        SetupGateway("gw1", "ws://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw1");
        var first = _factory.CreatedClients[^1];
        Assert.False(first.IsDisposed);

        // Disconnect to return to Idle, then connect again
        await _manager.DisconnectAsync();
        await _manager.ConnectAsync("gw1");
        var second = _factory.CreatedClients[^1];

        Assert.True(first.IsDisposed, "First lifecycle should be disposed after disconnect");
        Assert.False(second.IsDisposed);
        Assert.Equal(2, _factory.CreatedClients.Count);
        Assert.Equal(OverallConnectionState.Connecting, _manager.CurrentSnapshot.OverallState);
    }

    [Fact]
    public async Task StaleStatusChanged_IsIgnored()
    {
        SetupGateway("gw1", "ws://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        // First connect
        await _manager.ConnectAsync("gw1");
        var staleLifecycle = _factory.CreatedClients[^1];

        // Disconnect + reconnect — increments generation, disposes first client
        await _manager.DisconnectAsync();
        await _manager.ConnectAsync("gw1");

        // Capture snapshots after this point
        var snapshots = new List<GatewayConnectionSnapshot>();
        _manager.StateChanged += (_, s) => snapshots.Add(s);

        // Fire Error on the stale lifecycle — should be ignored by the generation guard
        staleLifecycle.SimulateStatusChanged(ConnectionStatus.Error);

        // Give any async handler a moment to run
        await Task.Delay(50);

        // The manager should still be in Connecting (from the reconnect),
        // not Error (from the stale event)
        Assert.Equal(OverallConnectionState.Connecting, _manager.CurrentSnapshot.OverallState);
        Assert.DoesNotContain(snapshots, s => s.OverallState == OverallConnectionState.Error);
    }

    [Fact]
    public async Task ReconnectAsync_DisposesOldClient_CreatesNew()
    {
        SetupGateway("gw1", "ws://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw1");
        var oldLifecycle = _factory.CreatedClients[^1];

        await _manager.ReconnectAsync();
        var newLifecycle = _factory.CreatedClients[^1];

        Assert.True(oldLifecycle.IsDisposed, "Old lifecycle should be disposed after ReconnectAsync");
        Assert.False(newLifecycle.IsDisposed);
        Assert.NotSame(oldLifecycle, newLifecycle);
        Assert.Equal(OverallConnectionState.Connecting, _manager.CurrentSnapshot.OverallState);
    }

    [Fact]
    public async Task MultipleRapidConnects_OnlyLastConnectionProcessesEvents()
    {
        SetupGateway("gw1", "ws://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        // Three connect cycles via disconnect between each
        await _manager.ConnectAsync("gw1");
        var first = _factory.CreatedClients[^1];

        await _manager.DisconnectAsync();
        await _manager.ConnectAsync("gw1");
        var second = _factory.CreatedClients[^1];

        await _manager.DisconnectAsync();
        await _manager.ConnectAsync("gw1");
        var third = _factory.CreatedClients[^1];

        Assert.Equal(3, _factory.CreatedClients.Count);
        Assert.True(first.IsDisposed);
        Assert.True(second.IsDisposed);
        Assert.False(third.IsDisposed);

        var snapshots = new List<GatewayConnectionSnapshot>();
        _manager.StateChanged += (_, s) => snapshots.Add(s);

        // Fire Error on stale lifecycles — should be ignored by generation guard
        first.SimulateStatusChanged(ConnectionStatus.Error);
        second.SimulateStatusChanged(ConnectionStatus.Error);
        await Task.Delay(50);

        Assert.Equal(OverallConnectionState.Connecting, _manager.CurrentSnapshot.OverallState);
        Assert.DoesNotContain(snapshots, s => s.OverallState == OverallConnectionState.Error);

        // Fire Error on the current (third) lifecycle — should be processed
        third.SimulateStatusChanged(ConnectionStatus.Error);
        await Task.Delay(50);

        Assert.Equal(OverallConnectionState.Error, _manager.CurrentSnapshot.OverallState);
    }

    [Fact]
    public async Task DisconnectThenConnect_OldEventsIgnored()
    {
        SetupGateway("gw1", "ws://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw1");
        var oldLifecycle = _factory.CreatedClients[^1];

        await _manager.DisconnectAsync();
        Assert.Equal(OverallConnectionState.Idle, _manager.CurrentSnapshot.OverallState);

        await _manager.ConnectAsync("gw1");
        var newLifecycle = _factory.CreatedClients[^1];

        var snapshots = new List<GatewayConnectionSnapshot>();
        _manager.StateChanged += (_, s) => snapshots.Add(s);

        // Fire error on the old lifecycle — should be ignored by generation guard
        oldLifecycle.SimulateStatusChanged(ConnectionStatus.Error);
        await Task.Delay(50);

        // Manager should still be Connecting from the new connect, not Error
        Assert.Equal(OverallConnectionState.Connecting, _manager.CurrentSnapshot.OverallState);
        Assert.DoesNotContain(snapshots, s => s.OverallState == OverallConnectionState.Error);

        // Current lifecycle's Error event should be processed
        newLifecycle.SimulateStatusChanged(ConnectionStatus.Error);
        await Task.Delay(50);

        Assert.Equal(OverallConnectionState.Error, _manager.CurrentSnapshot.OverallState);
        Assert.Contains(snapshots, s => s.OverallState == OverallConnectionState.Error);
    }

    // ─── Helpers ───

    private void SetupGateway(string id, string url)
    {
        _registry.AddOrUpdate(new GatewayRecord { Id = id, Url = url });
        _registry.SetActive(id);
        Directory.CreateDirectory(_registry.GetIdentityDirectory(id));
    }

    // ─── Test Fakes ───

    private sealed class MockCredentialResolver : ICredentialResolver
    {
        public GatewayCredential? OperatorCredential { get; set; }
        public GatewayCredential? NodeCredential { get; set; }

        public GatewayCredential? ResolveOperator(GatewayRecord record, string identityPath) => OperatorCredential;
        public GatewayCredential? ResolveNode(GatewayRecord record, string identityPath) => NodeCredential;
    }

    private sealed class TrackingClientFactory : IGatewayClientFactory
    {
        public List<TrackingLifecycle> CreatedClients { get; } = [];

        public IGatewayClientLifecycle Create(string gatewayUrl, GatewayCredential credential, string identityPath, IOpenClawLogger logger)
        {
            var lifecycle = new TrackingLifecycle(gatewayUrl);
            CreatedClients.Add(lifecycle);
            return lifecycle;
        }
    }

    internal sealed class TrackingLifecycle : IGatewayClientLifecycle
    {
        private readonly MockGatewayClient _client;

        public TrackingLifecycle(string url)
        {
            _client = new MockGatewayClient(url);
        }

        public bool IsDisposed { get; private set; }
        public OpenClawGatewayClient DataClient => _client;
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<string>? AuthenticationFailed;

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

        public void SimulateStatusChanged(ConnectionStatus status) =>
            StatusChanged?.Invoke(this, status);

        public void SimulateAuthFailed(string msg) =>
            AuthenticationFailed?.Invoke(this, msg);

        public void Dispose() => IsDisposed = true;
    }

    private sealed class MockGatewayClient : OpenClawGatewayClient
    {
        public MockGatewayClient(string url)
            : base(url, "mock-token", NullLogger.Instance) { }
    }
}
