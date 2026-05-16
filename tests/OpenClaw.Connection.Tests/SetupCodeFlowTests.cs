using OpenClaw.Shared;
using OpenClaw.Connection;

namespace OpenClaw.Connection.Tests;

/// <summary>
/// Integration tests for the setup code → connect flow.
/// Tests the full path: decode → registry → credential resolution → client creation.
/// Does NOT require a real gateway — tests the logic up to client construction.
/// </summary>
public class SetupCodeFlowTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GatewayRegistry _registry;

    public SetupCodeFlowTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openclaw-flow-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _registry = new GatewayRegistry(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void SetupCodeDecoder_DecodesValidCode()
    {
        // Simulate a setup code: base64 of {"url":"ws://localhost:18790","bootstrapToken":"test-boot-token"}
        var json = """{"url":"ws://localhost:18790","bootstrapToken":"test-boot-token"}""";
        var code = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

        var result = SetupCodeDecoder.Decode(code);

        Assert.True(result.Success);
        Assert.Equal("ws://localhost:18790", result.Url);
        Assert.Equal("test-boot-token", result.Token);
    }

    [Fact]
    public void SetupCodeDecoder_HandlesGatewayUrlWithToken()
    {
        // QR codes use "bootstrapToken" field — verify that works with different URLs
        var json = """{"url":"wss://gateway.example.com","bootstrapToken":"shared-gw-token"}""";
        var code = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

        var result = SetupCodeDecoder.Decode(code);

        Assert.True(result.Success);
        Assert.Equal("wss://gateway.example.com", result.Url);
        Assert.Equal("shared-gw-token", result.Token);
    }

    [Fact]
    public async Task ApplySetupCode_StoresTokenAsBootstrap()
    {
        var json = """{"url":"ws://localhost:18790","bootstrapToken":"boot-tok-123"}""";
        var code = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

        var resolver = new CredentialResolver(new FakeIdentityReader());
        var factory = new RecordingClientFactory();
        var manager = new GatewayConnectionManager(
            resolver, factory, _registry, NullLogger.Instance);

        var result = await manager.ApplySetupCodeAsync(code);

        Assert.Equal(SetupCodeOutcome.Success, result.Outcome);

        // Verify the token was stored as BootstrapToken (not SharedGatewayToken)
        var active = _registry.GetActive();
        Assert.NotNull(active);
        Assert.Equal("ws://localhost:18790", active.Url);
        Assert.Equal("boot-tok-123", active.BootstrapToken);
        Assert.Null(active.SharedGatewayToken); // Must NOT be stored as shared token

        manager.Dispose();
    }

    [Fact]
    public async Task ApplySetupCode_CredentialResolverReturnsBootstrap()
    {
        var json = """{"url":"ws://localhost:18790","bootstrapToken":"boot-tok-456"}""";
        var code = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

        var resolver = new CredentialResolver(new FakeIdentityReader());
        var factory = new RecordingClientFactory();
        var manager = new GatewayConnectionManager(
            resolver, factory, _registry, NullLogger.Instance);

        await manager.ApplySetupCodeAsync(code);

        // The factory should have been called with IsBootstrapToken = true
        Assert.Single(factory.Calls);
        var call = factory.Calls[0];
        Assert.True(call.Credential.IsBootstrapToken,
            $"Expected bootstrap token but got Source={call.Credential.Source}, IsBootstrap={call.Credential.IsBootstrapToken}");
        Assert.Equal("boot-tok-456", call.Credential.Token);

        manager.Dispose();
    }

    [Fact]
    public async Task ApplySetupCode_WithExistingDeviceToken_UsesDeviceToken()
    {
        // First, apply a setup code to create the gateway record
        var json = """{"url":"ws://localhost:18790","bootstrapToken":"boot-tok"}""";
        var code = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

        // Simulate a device that already has a stored device token
        var fakeReader = new FakeIdentityReader { OperatorToken = "paired-device-token" };
        var resolver = new CredentialResolver(fakeReader);
        var factory = new RecordingClientFactory();
        var manager = new GatewayConnectionManager(
            resolver, factory, _registry, NullLogger.Instance);

        await manager.ApplySetupCodeAsync(code);

        // The factory should use the device token, NOT the bootstrap token
        Assert.Single(factory.Calls);
        var call = factory.Calls[0];
        Assert.False(call.Credential.IsBootstrapToken);
        Assert.Equal("paired-device-token", call.Credential.Token);
        Assert.Equal(CredentialResolver.SourceDeviceToken, call.Credential.Source);

        manager.Dispose();
    }

    [Fact]
    public async Task ApplySetupCode_SecondApply_PreservesExistingSharedToken()
    {
        // First apply with a shared token (not bootstrap)
        _registry.AddOrUpdate(new GatewayRecord
        {
            Id = "existing-gw",
            Url = "ws://localhost:18790",
            SharedGatewayToken = "original-shared-token"
        });
        _registry.SetActive("existing-gw");
        _registry.Save();

        // Now apply a setup code for the same URL with a new bootstrap token
        var json = """{"url":"ws://localhost:18790","bootstrapToken":"new-boot-tok"}""";
        var code = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

        var resolver = new CredentialResolver(new FakeIdentityReader());
        var factory = new RecordingClientFactory();
        var manager = new GatewayConnectionManager(
            resolver, factory, _registry, NullLogger.Instance);

        await manager.ApplySetupCodeAsync(code);

        // Should preserve the existing shared token AND add the bootstrap
        var active = _registry.GetActive();
        Assert.Equal("original-shared-token", active!.SharedGatewayToken);
        Assert.Equal("new-boot-tok", active.BootstrapToken);

        manager.Dispose();
    }

    [Fact]
    public async Task ApplySetupCode_InvalidCode_ReturnsError()
    {
        var resolver = new CredentialResolver(new FakeIdentityReader());
        var factory = new RecordingClientFactory();
        var manager = new GatewayConnectionManager(
            resolver, factory, _registry, NullLogger.Instance);

        var result = await manager.ApplySetupCodeAsync("not-valid-base64!!!");

        Assert.Equal(SetupCodeOutcome.InvalidCode, result.Outcome);
        Assert.Empty(factory.Calls);

        manager.Dispose();
    }

    [Fact]
    public async Task ConnectAsync_WithBootstrapRecord_PassesIsBootstrapTrue()
    {
        _registry.AddOrUpdate(new GatewayRecord
        {
            Id = "gw-boot",
            Url = "ws://localhost:18790",
            BootstrapToken = "my-bootstrap-token"
        });
        _registry.SetActive("gw-boot");

        // Ensure identity dir exists
        var identityDir = _registry.GetIdentityDirectory("gw-boot");
        Directory.CreateDirectory(identityDir);

        var resolver = new CredentialResolver(new FakeIdentityReader());
        var factory = new RecordingClientFactory();
        var manager = new GatewayConnectionManager(
            resolver, factory, _registry, NullLogger.Instance);

        await manager.ConnectAsync("gw-boot");

        Assert.Single(factory.Calls);
        Assert.True(factory.Calls[0].Credential.IsBootstrapToken);
        Assert.Equal("my-bootstrap-token", factory.Calls[0].Credential.Token);

        manager.Dispose();
    }

    [Fact]
    public async Task ConnectAsync_WithSharedTokenRecord_PassesIsBootstrapFalse()
    {
        _registry.AddOrUpdate(new GatewayRecord
        {
            Id = "gw-shared",
            Url = "ws://localhost:18790",
            SharedGatewayToken = "my-shared-token"
        });
        _registry.SetActive("gw-shared");

        var identityDir = _registry.GetIdentityDirectory("gw-shared");
        Directory.CreateDirectory(identityDir);

        var resolver = new CredentialResolver(new FakeIdentityReader());
        var factory = new RecordingClientFactory();
        var manager = new GatewayConnectionManager(
            resolver, factory, _registry, NullLogger.Instance);

        await manager.ConnectAsync("gw-shared");

        Assert.Single(factory.Calls);
        Assert.False(factory.Calls[0].Credential.IsBootstrapToken);
        Assert.Equal("my-shared-token", factory.Calls[0].Credential.Token);

        manager.Dispose();
    }

    // ─── Test Helpers ───

    private sealed class FakeIdentityReader : IDeviceIdentityReader
    {
        public string? OperatorToken { get; set; }
        public string? NodeToken { get; set; }
        public string? TryReadStoredDeviceToken(string dataPath) => OperatorToken;
        public string? TryReadStoredNodeDeviceToken(string dataPath) => NodeToken;
    }

    private sealed class RecordingClientFactory : IGatewayClientFactory
    {
        public List<CreateCall> Calls { get; } = [];

        public IGatewayClientLifecycle Create(string gatewayUrl, GatewayCredential credential, string identityPath, IOpenClawLogger logger)
        {
            Calls.Add(new CreateCall(gatewayUrl, credential, identityPath));
            return new FakeLifecycle();
        }

        public record CreateCall(string GatewayUrl, GatewayCredential Credential, string IdentityPath);
    }

    private sealed class FakeLifecycle : IGatewayClientLifecycle
    {
        private readonly FakeClient _client = new();
        public OpenClawGatewayClient DataClient => _client;
#pragma warning disable CS0067 // Event never used — required by interface
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<string>? AuthenticationFailed;
#pragma warning restore CS0067
        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class FakeClient : OpenClawGatewayClient
    {
        public FakeClient() : base("ws://fake", "fake-token", NullLogger.Instance) { }
    }
}
