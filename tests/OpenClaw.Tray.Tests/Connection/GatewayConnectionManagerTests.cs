using OpenClaw.Shared;
using OpenClawTray.Services.Connection;

namespace OpenClaw.Tray.Tests.Connection;

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
    public void Diagnostics_IsAccessible()
    {
        Assert.NotNull(_manager.Diagnostics);
        Assert.Equal(0, _manager.Diagnostics.Count);
    }

    // ─── Helpers ───

    private void SetupGateway(string id, string url)
    {
        _registry.AddOrUpdate(new GatewayRecord { Id = id, Url = url });
        _registry.SetActive(id);
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

        public IGatewayClientLifecycle Create(string gatewayUrl, GatewayCredential credential, string identityPath, IOpenClawLogger logger)
        {
            var mock = new MockLifecycle(gatewayUrl);
            CreatedClients.Add(mock);
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
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<string>? AuthenticationFailed;

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

        public void SimulateStatusChanged(ConnectionStatus status) =>
            StatusChanged?.Invoke(this, status);

        public void SimulateAuthFailed(string msg) =>
            AuthenticationFailed?.Invoke(this, msg);

        public void Dispose() { }
    }

    private sealed class MockGatewayClient : OpenClawGatewayClient
    {
        public MockGatewayClient(string url)
            : base(url, "mock-token", NullLogger.Instance) { }
    }
}
