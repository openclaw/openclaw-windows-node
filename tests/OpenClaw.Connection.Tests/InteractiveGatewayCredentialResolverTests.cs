using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.Connection;

namespace OpenClaw.Connection.Tests;

public class InteractiveGatewayCredentialResolverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GatewayRegistry _registry;
    private readonly MockDeviceIdentityReader _identityReader = new();

    public InteractiveGatewayCredentialResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "OpenClaw.Tray.Tests", "InteractiveCred-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _registry = new GatewayRegistry(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void TryResolve_UsesActiveGatewaySharedToken()
    {
        var record = new GatewayRecord
        {
            Id = "gw-1",
            Url = "ws://active:18789",
            SharedGatewayToken = "shared-token"
        };
        _registry.AddOrUpdate(record);
        _registry.SetActive(record.Id);

        var resolved = InteractiveGatewayCredentialResolver.TryResolve(
            _registry,
            _tempDir,
            _identityReader,
            "ws://legacy:18789",
            null,
            null,
            out var credential);

        Assert.True(resolved);
        Assert.NotNull(credential);
        Assert.Equal("ws://active:18789", credential!.GatewayUrl);
        Assert.Equal("shared-token", credential.Token);
        Assert.False(credential.IsBootstrapToken);
        Assert.Equal(CredentialResolver.SourceSharedGatewayToken, credential.Source);
    }

    [Fact]
    public void TryResolve_PreservesBootstrapPairingState()
    {
        var record = new GatewayRecord
        {
            Id = "gw-1",
            Url = "ws://active:18789",
            BootstrapToken = "bootstrap-token"
        };
        _registry.AddOrUpdate(record);
        _registry.SetActive(record.Id);

        var resolved = InteractiveGatewayCredentialResolver.TryResolve(
            _registry,
            _tempDir,
            _identityReader,
            "ws://active:18789",
            null,
            null,
            out var credential);

        Assert.True(resolved);
        Assert.NotNull(credential);
        Assert.Equal("bootstrap-token", credential!.Token);
        Assert.True(credential.IsBootstrapToken);
        Assert.Equal(CredentialResolver.SourceBootstrapToken, credential.Source);
    }

    [Fact]
    public void TryResolve_PrefersSharedGatewayTokenOverDeviceTokenForHttpSurfaces()
    {
        var record = new GatewayRecord
        {
            Id = "gw-1",
            Url = "ws://active:18789",
            SharedGatewayToken = "shared-token"
        };
        _registry.AddOrUpdate(record);
        _registry.SetActive(record.Id);
        _identityReader.OperatorToken = "paired-token";

        var resolved = InteractiveGatewayCredentialResolver.TryResolve(
            _registry,
            _tempDir,
            _identityReader,
            "ws://active:18789",
            null,
            null,
            out var credential);

        Assert.True(resolved);
        Assert.NotNull(credential);
        Assert.Equal("shared-token", credential!.Token);
        Assert.False(credential.IsBootstrapToken);
        Assert.Equal(CredentialResolver.SourceSharedGatewayToken, credential.Source);
    }

    [Fact]
    public void TryResolve_FallsBackToLegacySettingsWhenNoRegistryIsActive()
    {
        var resolved = InteractiveGatewayCredentialResolver.TryResolve(
            _registry,
            _tempDir,
            _identityReader,
            "ws://legacy:18789",
            "legacy-token",
            null,
            out var credential);

        Assert.True(resolved);
        Assert.NotNull(credential);
        Assert.Equal("ws://legacy:18789", credential!.GatewayUrl);
        Assert.Equal("legacy-token", credential.Token);
        Assert.False(credential.IsBootstrapToken);
        Assert.Equal(CredentialResolver.SourceSharedGatewayToken, credential.Source);
    }

    private sealed class MockDeviceIdentityReader : IDeviceIdentityReader
    {
        public string? OperatorToken { get; set; }
        public string? LastOperatorPath { get; private set; }

        public string? TryReadStoredDeviceToken(string dataPath)
        {
            LastOperatorPath = dataPath;
            return OperatorToken;
        }

        public string? TryReadStoredNodeDeviceToken(string dataPath) => null;
    }
}
