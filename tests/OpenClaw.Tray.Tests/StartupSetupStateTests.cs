using OpenClaw.Shared;
using OpenClawTray.Services;
using OpenClaw.Connection;
using OpenClawTray.Services.LocalGatewaySetup;

namespace OpenClaw.Tray.Tests;

public class StartupSetupStateTests
{
    [Fact]
    public void RequiresSetup_ReturnsFalse_WhenNodeHasStoredDeviceToken()
    {
        using var temp = TempSettings.Create();
        StoreNodeDeviceToken(temp.Path);
        var settings = new SettingsManager(temp.Path) { EnableNodeMode = true };

        Assert.False(StartupSetupState.RequiresSetup(settings, temp.Path));
        Assert.True(StartupSetupState.CanStartNodeGateway(settings, temp.Path));
    }

    [Fact]
    public void RequiresSetup_ReturnsFalse_WhenNodeTokenStoredOnlyInPerGatewayDir()
    {
        using var temp = TempSettings.Create();
        var perGatewayDir = Path.Combine(temp.Path, "gateways", "gw-node");
        Directory.CreateDirectory(perGatewayDir);
        StoreNodeDeviceToken(perGatewayDir);
        var settings = new SettingsManager(temp.Path) { EnableNodeMode = true };

        Assert.False(StartupSetupState.RequiresSetup(settings, temp.Path));
        Assert.True(StartupSetupState.CanStartNodeGateway(settings, temp.Path));
    }

    [Fact]
    public void RequiresSetup_ReturnsTrue_WhenOnlyOperatorTokenExistsForNodeMode()
    {
        using var temp = TempSettings.Create();
        StoreDeviceToken(temp.Path);
        var settings = new SettingsManager(temp.Path) { EnableNodeMode = true };

        Assert.True(StartupSetupState.RequiresSetup(settings, temp.Path));
        Assert.False(StartupSetupState.CanStartNodeGateway(settings, temp.Path));
    }

    [Fact]
    public void RequiresSetup_ReturnsFalse_WhenMcpOnlyModeIsEnabled()
    {
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path) { EnableMcpServer = true };

        Assert.False(StartupSetupState.RequiresSetup(settings, temp.Path));
    }

    [Fact]
    public void RequiresSetup_ReturnsTrue_WhenNoAuthOrLocalServerModeExists()
    {
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path);

        Assert.True(StartupSetupState.RequiresSetup(settings, temp.Path));
        Assert.False(StartupSetupState.CanStartNodeGateway(settings, temp.Path));
    }

    [Fact]
    public void RequiresSetup_ReturnsFalse_WhenOperatorPairedWithRemoteGateway()
    {
        // Scott Hanselman repro: operator mode with a non-default (remote) gateway URL
        // and a stored operator device token — wizard must NOT auto-launch on next start.
        using var temp = TempSettings.Create();
        StoreDeviceToken(temp.Path);
        var settings = new SettingsManager(temp.Path) { GatewayUrl = "wss://remote.example.com:443" };

        Assert.False(StartupSetupState.RequiresSetup(settings, temp.Path));
    }

    [Fact]
    public void RequiresSetup_ReturnsTrue_WhenOperatorTokenExistsButGatewayUrlIsDefault()
    {
        // Stale-token guard: a stored operator token alone is not enough. Without a
        // configured non-default gateway URL the app has no target to connect to,
        // so first-run setup should still be offered.
        using var temp = TempSettings.Create();
        StoreDeviceToken(temp.Path);
        var settings = new SettingsManager(temp.Path) { GatewayUrl = "ws://localhost:18789" };

        Assert.True(StartupSetupState.RequiresSetup(settings, temp.Path));
    }

    [Fact]
    public void RequiresSetup_ReturnsTrue_WhenNonDefaultGatewayUrlButNoOperatorToken()
    {
        // Inverse guard: a non-default URL alone (no pairing yet) still needs setup.
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path) { GatewayUrl = "wss://remote.example.com:443" };

        Assert.True(StartupSetupState.RequiresSetup(settings, temp.Path));
    }

    [Fact]
    public void HasUsableOperatorConfiguration_ReturnsFalse_WhenGatewayUrlIsNullOrWhitespace()
    {
        using var temp = TempSettings.Create();
        StoreDeviceToken(temp.Path);
        var settings = new SettingsManager(temp.Path) { GatewayUrl = "   " };

        Assert.False(StartupSetupState.HasUsableOperatorConfiguration(settings, temp.Path));
    }

    [Fact]
    public void RequiresSetup_ReturnsFalse_WhenSshTunnelConfiguredWithStoredToken()
    {
        // SSH topology routes via ws://127.0.0.1:LocalPort so the user keeps
        // GatewayUrl at default. Detection must treat (UseSshTunnel + host) as
        // a configured target so SSH operators are not re-prompted at launch.
        using var temp = TempSettings.Create();
        StoreDeviceToken(temp.Path);
        var settings = new SettingsManager(temp.Path)
        {
            UseSshTunnel = true,
            SshTunnelHost = "ssh.example.com",
            SshTunnelUser = "ops",
        };

        Assert.False(StartupSetupState.RequiresSetup(settings, temp.Path));
    }

    [Fact]
    public void RequiresSetup_ReturnsTrue_WhenSshTunnelEnabledButNoHostConfigured()
    {
        using var temp = TempSettings.Create();
        StoreDeviceToken(temp.Path);
        var settings = new SettingsManager(temp.Path) { UseSshTunnel = true };

        Assert.True(StartupSetupState.RequiresSetup(settings, temp.Path));
    }

    [Fact]
    public void RequiresSetup_ReturnsFalse_WhenOperatorTokenStoredOnlyInPerGatewayDir()
    {
        // Modern pairings (post-GatewayRegistry) store device tokens in
        // <dataPath>/gateways/<gatewayId>/device-key-ed25519.json via
        // DeviceIdentityStore. The legacy root file is NOT created for fresh
        // pairings, so RequiresSetup must scan the per-gateway directories.
        using var temp = TempSettings.Create();
        var perGatewayDir = Path.Combine(temp.Path, "gateways", "gw-abc");
        Directory.CreateDirectory(perGatewayDir);
        StoreDeviceToken(perGatewayDir);
        var settings = new SettingsManager(temp.Path) { GatewayUrl = "wss://remote.example.com:443" };

        Assert.False(StartupSetupState.RequiresSetup(settings, temp.Path));
    }

    [Fact]
    public void RequiresSetup_ReturnsFalse_WhenRegistryHasExternalGatewayToken()
    {
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path);
        var registry = new GatewayRegistry(temp.Path);
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "external-gateway",
            Url = "wss://remote.example.com",
            SharedGatewayToken = "shared-token"
        });

        Assert.False(StartupSetupState.RequiresSetup(settings, temp.Path, registry));
    }

    [Fact]
    public void RequiresSetup_ReturnsTrue_WhenRegistryRecordHasNoCredential()
    {
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path);
        var registry = new GatewayRegistry(temp.Path);
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "stale-gateway",
            Url = "wss://remote.example.com"
        });

        Assert.True(StartupSetupState.RequiresSetup(settings, temp.Path, registry));
    }

    [Fact]
    public void RequiresSetup_ReturnsFalse_WhenStaleRegistryRecordAndLegacyOperatorConfigExist()
    {
        using var temp = TempSettings.Create();
        StoreDeviceToken(temp.Path);
        var settings = new SettingsManager(temp.Path) { GatewayUrl = "wss://remote.example.com" };
        var registry = new GatewayRegistry(temp.Path);
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "stale-gateway",
            Url = "wss://old.example.com"
        });

        Assert.False(StartupSetupState.RequiresSetup(settings, temp.Path, registry));
    }

    [Fact]
    public void RequiresSetup_ReturnsFalse_WhenRegistryRecordHasPerGatewayIdentity()
    {
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path);
        var registry = new GatewayRegistry(temp.Path);
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "paired-gateway",
            Url = "wss://remote.example.com"
        });
        Directory.CreateDirectory(registry.GetIdentityDirectory("paired-gateway"));
        StoreDeviceToken(registry.GetIdentityDirectory("paired-gateway"));

        Assert.False(StartupSetupState.RequiresSetup(settings, temp.Path, registry));
    }

    [Fact]
    public void RequiresSetup_PreservesNodeModePrecedence_WhenRegistryHasExternalGatewayToken()
    {
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path) { EnableNodeMode = true };
        var registry = new GatewayRegistry(temp.Path);
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "external-gateway",
            Url = "wss://remote.example.com",
            SharedGatewayToken = "shared-token"
        });

        Assert.True(StartupSetupState.RequiresSetup(settings, temp.Path, registry));
    }

    [Fact]
    public async Task ClassifyAsync_ReturnsAppOwnedLocalWsl_WhenDistroAndLocalRegistryEvidenceExist()
    {
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path);
        var registry = new GatewayRegistry(temp.Path);
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "local-gateway",
            Url = "ws://localhost:18789",
            IsLocal = true,
            SharedGatewayToken = "shared-token"
        });
        var wsl = new FakeWslCommandRunner([new WslDistroInfo("OpenClawGateway", "Stopped", 2)]);

        var kind = await SetupExistingGatewayClassifier.ClassifyAsync(registry, settings, temp.Path, wsl);

        Assert.Equal(SetupExistingGatewayKind.AppOwnedLocalWsl, kind);
    }

    [Fact]
    public async Task ClassifyAsync_StaleOpenClawDistroWithoutLocalEvidence_DoesNotTriggerLocalReplacementWarning()
    {
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path);
        var registry = new GatewayRegistry(temp.Path);
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "external-gateway",
            Url = "wss://remote.example.com",
            SharedGatewayToken = "shared-token"
        });
        var wsl = new FakeWslCommandRunner([new WslDistroInfo("OpenClawGateway", "Stopped", 2)]);

        var kind = await SetupExistingGatewayClassifier.ClassifyAsync(registry, settings, temp.Path, wsl);

        Assert.Equal(SetupExistingGatewayKind.ExternalOnly, kind);
    }

    [Fact]
    public async Task ClassifyAsync_StaleOpenClawDistroOnFreshAppState_ReturnsNone()
    {
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path);
        var registry = new GatewayRegistry(temp.Path);
        var wsl = new FakeWslCommandRunner([new WslDistroInfo("OpenClawGateway", "Stopped", 2)]);

        var kind = await SetupExistingGatewayClassifier.ClassifyAsync(registry, settings, temp.Path, wsl);

        Assert.Equal(SetupExistingGatewayKind.None, kind);
    }

    [Fact]
    public async Task ClassifyAsync_UsesProvidedLocalDataPath_WhenWslProbeFails()
    {
        using var temp = TempSettings.Create();
        var localDataPath = Path.Combine(temp.Path, "local-data");
        Directory.CreateDirectory(localDataPath);
        File.WriteAllText(
            Path.Combine(localDataPath, "setup-state.json"),
            """{"DistroName":"OpenClawGateway","Phase":"Complete"}""");
        var settings = new SettingsManager(temp.Path);
        var registry = new GatewayRegistry(temp.Path);
        var wsl = new ThrowingWslCommandRunner();

        var kind = await SetupExistingGatewayClassifier.ClassifyAsync(
            registry,
            settings,
            temp.Path,
            wsl,
            localDataPath: localDataPath);

        Assert.Equal(SetupExistingGatewayKind.AppOwnedLocalWsl, kind);
    }

    [Fact]
    public void RequiresSetup_ReturnsFalse_WhenMcpEnabledEvenWithNodeModeAndNoNodeToken()
    {
        // Regression guard: the original code returned !EnableMcpServer as the
        // fallback so MCP-only mode bypassed onboarding even when EnableNodeMode
        // was accidentally true with no node token. The new ordering must
        // preserve "MCP wins" precedence.
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path)
        {
            EnableNodeMode = true,
            EnableMcpServer = true,
        };

        Assert.False(StartupSetupState.RequiresSetup(settings, temp.Path));
    }

    [Fact]
    public void DefaultGatewayUrl_MatchesGuardConstant()
    {
        // OnboardingExistingConfigGuard.DefaultGatewayUrl is the single source
        // of truth referenced by StartupSetupState.HasNonDefaultGatewayUrl.
        // This test exists so a future change to the constant (or a refactor
        // that re-introduces a duplicate) is caught immediately.
        Assert.Equal(
            "ws://localhost:18789",
            OpenClawTray.Onboarding.Services.OnboardingExistingConfigGuard.DefaultGatewayUrl);
    }

    private static void StoreDeviceToken(string dataPath)
    {
        var identity = new DeviceIdentity(dataPath);
        identity.Initialize();
        identity.StoreDeviceToken("stored-device-token");
    }

    private static void StoreNodeDeviceToken(string dataPath)
    {
        var identity = new DeviceIdentity(dataPath);
        identity.Initialize();
        identity.StoreDeviceTokenForRole("node", "stored-node-token");
    }

    private sealed class TempSettings : IDisposable
    {
        public string Path { get; }

        private TempSettings(string path)
        {
            Path = path;
        }

        public static TempSettings Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"openclaw-tray-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempSettings(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }

    private sealed class FakeWslCommandRunner(IReadOnlyList<WslDistroInfo> distros) : IWslCommandRunner
    {
        public Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(distros);

        public Task<WslCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? environment = null) =>
            Task.FromResult(new WslCommandResult(0, "", ""));

        public Task<WslCommandResult> TerminateDistroAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WslCommandResult(0, "", ""));

        public Task<WslCommandResult> UnregisterDistroAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WslCommandResult(0, "", ""));

        public Task<WslCommandResult> RunInDistroAsync(string name, IReadOnlyList<string> command, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? environment = null) =>
            Task.FromResult(new WslCommandResult(0, "", ""));
    }

    private sealed class ThrowingWslCommandRunner : IWslCommandRunner
    {
        public Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("WSL unavailable");

        public Task<WslCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? environment = null) =>
            Task.FromResult(new WslCommandResult(0, "", ""));

        public Task<WslCommandResult> TerminateDistroAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WslCommandResult(0, "", ""));

        public Task<WslCommandResult> UnregisterDistroAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WslCommandResult(0, "", ""));

        public Task<WslCommandResult> RunInDistroAsync(string name, IReadOnlyList<string> command, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? environment = null) =>
            Task.FromResult(new WslCommandResult(0, "", ""));
    }
}
