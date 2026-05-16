using OpenClaw.Shared;
using OpenClaw.Connection;

namespace OpenClaw.Connection.Tests;

public class GatewayRegistryMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GatewayRegistry _registry;

    public GatewayRegistryMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openclaw-mig-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _registry = new GatewayRegistry(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void MigrateFromSettings_CreatesRecord()
    {
        var result = _registry.MigrateFromSettings(
            "wss://test.example.com", "shared-tok", null,
            false, null, null, 0, 0, _tempDir);

        Assert.True(result);
        var all = _registry.GetAll();
        Assert.Single(all);
        Assert.Equal("wss://test.example.com", all[0].Url);
        Assert.Equal("shared-tok", all[0].SharedGatewayToken);
        Assert.Null(all[0].BootstrapToken);
    }

    [Fact]
    public void MigrateFromSettings_WithBootstrapToken()
    {
        var result = _registry.MigrateFromSettings(
            "wss://test.example.com", null, "boot-tok",
            false, null, null, 0, 0, _tempDir);

        Assert.True(result);
        var record = _registry.GetActive()!;
        Assert.Equal("boot-tok", record.BootstrapToken);
        Assert.Null(record.SharedGatewayToken);
    }

    [Fact]
    public void MigrateFromSettings_WithSshTunnel()
    {
        var result = _registry.MigrateFromSettings(
            "wss://test.example.com", "tok", null,
            true, "user", "host.com", 18789, 18789, _tempDir);

        Assert.True(result);
        var record = _registry.GetActive()!;
        Assert.NotNull(record.SshTunnel);
        Assert.Equal("user", record.SshTunnel.User);
        Assert.Equal("host.com", record.SshTunnel.Host);
    }

    [Fact]
    public void MigrateFromSettings_IsIdempotent()
    {
        _registry.MigrateFromSettings(
            "wss://test.example.com", "tok", null,
            false, null, null, 0, 0, _tempDir);

        // Second migration with same URL should be skipped
        var result = _registry.MigrateFromSettings(
            "wss://test.example.com", "tok2", null,
            false, null, null, 0, 0, _tempDir);

        Assert.False(result);
        Assert.Single(_registry.GetAll());
        // Original token preserved
        Assert.Equal("tok", _registry.GetAll()[0].SharedGatewayToken);
    }

    [Fact]
    public void MigrateFromSettings_SkipsEmptyUrl()
    {
        var result = _registry.MigrateFromSettings(
            "", "tok", null, false, null, null, 0, 0, _tempDir);
        Assert.False(result);
        Assert.Empty(_registry.GetAll());
    }

    [Fact]
    public void MigrateFromSettings_SkipsNullUrl()
    {
        var result = _registry.MigrateFromSettings(
            null, "tok", null, false, null, null, 0, 0, _tempDir);
        Assert.False(result);
    }

    [Fact]
    public void MigrateFromSettings_SetsActiveGateway()
    {
        _registry.MigrateFromSettings(
            "wss://test.example.com", "tok", null,
            false, null, null, 0, 0, _tempDir);

        Assert.NotNull(_registry.GetActive());
        Assert.Equal("wss://test.example.com", _registry.GetActive()!.Url);
    }

    [Fact]
    public void MigrateFromSettings_CopiesIdentityFile()
    {
        // Create a fake legacy identity file
        var legacyPath = Path.Combine(_tempDir, "device-key-ed25519.json");
        File.WriteAllText(legacyPath, "{\"test\": true}");

        _registry.MigrateFromSettings(
            "wss://test.example.com", "tok", null,
            false, null, null, 0, 0, _tempDir);

        var record = _registry.GetActive()!;
        var newPath = Path.Combine(_registry.GetIdentityDirectory(record.Id), "device-key-ed25519.json");
        Assert.True(File.Exists(newPath));
        Assert.Equal("{\"test\": true}", File.ReadAllText(newPath));

        // Original still exists (copy, not move)
        Assert.True(File.Exists(legacyPath));
    }

    [Fact]
    public void MigrateFromSettings_PersistsToFile()
    {
        _registry.MigrateFromSettings(
            "wss://test.example.com", "tok", null,
            false, null, null, 0, 0, _tempDir);

        // Load in a new registry instance
        var registry2 = new GatewayRegistry(_tempDir);
        registry2.Load();

        Assert.Single(registry2.GetAll());
        Assert.Equal("wss://test.example.com", registry2.GetActive()!.Url);
    }
}
