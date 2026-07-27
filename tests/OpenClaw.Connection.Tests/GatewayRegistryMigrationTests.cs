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
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
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
    public void MigrateFromSettings_LocalhostWithSetupState_BackfillsManagedDistro()
    {
        var localRoot = Path.Combine(_tempDir, "local-root");
        var stateDir = Path.Combine(localRoot, "OpenClawTray");
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(
            Path.Combine(stateDir, "setup-state.json"),
            """{"DistroName":"OpenClawGateway","GatewayUrl":"ws://127.0.0.1:18789"}""");
        var previous = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR", localRoot);

            Assert.True(_registry.MigrateFromSettings(
                "ws://localhost:18789",
                "shared-token",
                null,
                false,
                null,
                null,
                0,
                0,
                _tempDir));

            var record = _registry.GetActive()!;
            Assert.Equal("OpenClawGateway", record.SetupManagedDistroName);
            Assert.Equal("Local (OpenClawGateway)", record.FriendlyName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR", previous);
        }
    }

    [Fact]
    public void MigrateFromSettings_DirectLocalDataOverride_BackfillsManagedDistro()
    {
        var direct = Path.Combine(_tempDir, "direct-setup-data");
        Directory.CreateDirectory(direct);
        File.WriteAllText(
            Path.Combine(direct, "setup-state.json"),
            """{"DistroName":"OpenClawGateway-Dev","GatewayUrl":"ws://localhost:18789"}""");
        var previous = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR", direct);
            Assert.True(_registry.MigrateFromSettings(
                "ws://localhost:18789",
                "shared-token",
                null,
                false,
                null,
                null,
                0,
                0,
                _tempDir));
            Assert.Equal(
                "OpenClawGateway-Dev",
                _registry.GetActive()!.SetupManagedDistroName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR", previous);
        }
    }

    [Fact]
    public void MigrateFromSettings_ManualLocalhostWithDifferentSetupEndpoint_RemainsManual()
    {
        var direct = Path.Combine(_tempDir, "manual-localhost-setup-data");
        Directory.CreateDirectory(direct);
        File.WriteAllText(
            Path.Combine(direct, "setup-state.json"),
            """{"DistroName":"OpenClawGateway","GatewayUrl":"ws://localhost:18789"}""");
        var previous = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR", direct);

            Assert.True(_registry.MigrateFromSettings(
                "ws://localhost:19999",
                "shared-token",
                null,
                false,
                null,
                null,
                0,
                0,
                _tempDir));

            var record = _registry.GetActive()!;
            Assert.True(record.IsLocal);
            Assert.Null(record.SetupManagedDistroName);
            Assert.Null(record.FriendlyName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR", previous);
        }
    }

    [Fact]
    public void MigrateFromSettings_NonstandardLoopbackAliasWithSetupState_RemainsManual()
    {
        var direct = Path.Combine(_tempDir, "loopback-alias-setup-data");
        Directory.CreateDirectory(direct);
        File.WriteAllText(
            Path.Combine(direct, "setup-state.json"),
            """{"DistroName":"OpenClawGateway","GatewayUrl":"ws://localhost:18789"}""");
        var previous = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR", direct);

            Assert.True(_registry.MigrateFromSettings(
                "ws://127.0.0.2:18789",
                "shared-token",
                null,
                false,
                null,
                null,
                0,
                0,
                _tempDir));

            Assert.Null(_registry.GetActive()!.SetupManagedDistroName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR", previous);
        }
    }

    [Fact]
    public void MigrateFromSettings_SetupStateWithoutGatewayUrl_RemainsManual()
    {
        var direct = Path.Combine(_tempDir, "url-less-setup-data");
        Directory.CreateDirectory(direct);
        File.WriteAllText(
            Path.Combine(direct, "setup-state.json"),
            """{"DistroName":"OpenClawGateway"}""");
        var previous = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR", direct);

            Assert.True(_registry.MigrateFromSettings(
                "ws://localhost:18789",
                "shared-token",
                null,
                false,
                null,
                null,
                0,
                0,
                _tempDir));

            Assert.Null(_registry.GetActive()!.SetupManagedDistroName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR", previous);
        }
    }

    [Fact]
    public void MigrateFromSettings_DevSettingsPreferDevSetupState()
    {
        var localRoot = Path.Combine(_tempDir, "side-by-side-local");
        Directory.CreateDirectory(Path.Combine(localRoot, "OpenClawTray"));
        Directory.CreateDirectory(Path.Combine(localRoot, "OpenClawTray-Dev"));
        File.WriteAllText(
            Path.Combine(localRoot, "OpenClawTray", "setup-state.json"),
            """{"DistroName":"OpenClawGateway","GatewayUrl":"ws://localhost:18789"}""");
        File.WriteAllText(
            Path.Combine(localRoot, "OpenClawTray-Dev", "setup-state.json"),
            """{"DistroName":"OpenClawGateway-Dev","GatewayUrl":"ws://localhost:18790"}""");
        var previous = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR", localRoot);
            var devSettingsDir = Path.Combine(_tempDir, "OpenClawTray-Dev");
            Directory.CreateDirectory(devSettingsDir);
            Assert.True(_registry.MigrateFromSettings(
                "ws://localhost:18790",
                "shared-token",
                null,
                false,
                null,
                null,
                0,
                0,
                devSettingsDir));
            Assert.Equal(
                "OpenClawGateway-Dev",
                _registry.GetActive()!.SetupManagedDistroName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR", previous);
        }
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
    public void MigrateFromSettings_LocalSshTunnelWithMatchingSetupState_RemainsManual()
    {
        var direct = Path.Combine(_tempDir, "ssh-setup-data");
        Directory.CreateDirectory(direct);
        File.WriteAllText(
            Path.Combine(direct, "setup-state.json"),
            """{"DistroName":"OpenClawGateway","GatewayUrl":"ws://localhost:18789"}""");
        var previous = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR", direct);

            Assert.True(_registry.MigrateFromSettings(
                "ws://localhost:18789",
                "shared-token",
                null,
                true,
                "user",
                "host.example",
                22,
                18789,
                _tempDir));

            var record = _registry.GetActive()!;
            Assert.NotNull(record.SshTunnel);
            Assert.Null(record.SetupManagedDistroName);
            Assert.Null(record.FriendlyName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR", previous);
        }
    }

    [Fact]
    public void MigrateFromSettings_WithSshTunnelBrowserProxyForward_PreservesFlag()
    {
        var result = _registry.MigrateFromSettings(
            "wss://test.example.com", "tok", null,
            true, "user", "host.com", sshPort: 2222, sshRemotePort: 18789, sshLocalPort: 18789,
            includeBrowserProxyForward: true, settingsDir: _tempDir);

        Assert.True(result);
        var record = _registry.GetActive()!;
        Assert.NotNull(record.SshTunnel);
        Assert.True(record.SshTunnel.IncludeBrowserProxyForward);
        Assert.Equal(2222, record.SshTunnel.SshPort);
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
