using OpenClaw.Shared;
using OpenClawTray.Services;
using OpenClaw.Connection;

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
    public void RequiresSetup_WhenSavedIdentityIsCorrupt_ThrowsTypedFailureInsteadOfStartingOnboarding()
    {
        using var temp = TempSettings.Create();
        File.WriteAllText(Path.Combine(temp.Path, "device-key-ed25519.json"), "{");
        var settings = new SettingsManager(temp.Path) { EnableNodeMode = true };

        Assert.Throws<DeviceIdentityLoadException>(
            () => StartupSetupState.RequiresSetup(settings, temp.Path));
        Assert.Throws<DeviceIdentityLoadException>(
            () => StartupSetupState.CanStartNodeGateway(settings, temp.Path));
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
    public void RequiresSetup_SkipsCorruptPerGatewayIdentityAndFindsHealthyToken()
    {
        using var temp = TempSettings.Create();
        var staleDir = Path.Combine(temp.Path, "gateways", "a-stale");
        Directory.CreateDirectory(staleDir);
        File.WriteAllText(Path.Combine(staleDir, "device-key-ed25519.json"), "{");
        var healthyDir = Path.Combine(temp.Path, "gateways", "b-healthy");
        Directory.CreateDirectory(healthyDir);
        StoreDeviceToken(healthyDir);
        var settings = new SettingsManager(temp.Path)
        {
            GatewayUrl = "wss://remote.example.com:443"
        };

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
    public void ExistingGatewayInventory_SkipsInactiveCorruptIdentityAndFindsHealthyActiveGateway()
    {
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path);
        var registry = new GatewayRegistry(temp.Path);
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "stale-corrupt",
            Url = "wss://stale.example.com"
        });
        var staleIdentityDir = registry.GetIdentityDirectory("stale-corrupt");
        Directory.CreateDirectory(staleIdentityDir);
        File.WriteAllText(Path.Combine(staleIdentityDir, "device-key-ed25519.json"), "{");

        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "healthy-active",
            Url = "wss://healthy.example.com"
        });
        var healthyIdentityDir = registry.GetIdentityDirectory("healthy-active");
        Directory.CreateDirectory(healthyIdentityDir);
        StoreDeviceToken(healthyIdentityDir);
        registry.SetActive("healthy-active");

        Assert.False(StartupSetupState.RequiresSetup(settings, temp.Path, registry));
        Assert.Equal(
            SetupExistingGatewayKind.ExternalOnly,
            SetupExistingGatewayClassifier.ClassifyWithoutWslProbe(registry, settings, temp.Path));
    }

    [Fact]
    public void ExistingGatewayInventory_WhenActiveIdentityIsCorrupt_PropagatesTypedFailure()
    {
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path);
        var registry = new GatewayRegistry(temp.Path);
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "corrupt-active",
            Url = "wss://corrupt.example.com"
        });
        var identityDir = registry.GetIdentityDirectory("corrupt-active");
        Directory.CreateDirectory(identityDir);
        File.WriteAllText(Path.Combine(identityDir, "device-key-ed25519.json"), "{");
        registry.SetActive("corrupt-active");

        Assert.Throws<DeviceIdentityLoadException>(
            () => SetupExistingGatewayClassifier.ClassifyWithoutWslProbe(
                registry,
                settings,
                temp.Path));
    }

    [Fact]
    public void ExistingGatewayInventory_ValidatesCorruptActiveIdentityBeforeHealthyInactiveRecord()
    {
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path);
        var registry = new GatewayRegistry(temp.Path);
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "healthy-inactive",
            Url = "wss://healthy.example.com",
            SharedGatewayToken = "healthy-shared-token"
        });
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "corrupt-active",
            Url = "wss://corrupt.example.com",
            BootstrapToken = "bootstrap-token"
        });
        var identityDir = registry.GetIdentityDirectory("corrupt-active");
        Directory.CreateDirectory(identityDir);
        File.WriteAllText(Path.Combine(identityDir, "device-key-ed25519.json"), "{");
        registry.SetActive("corrupt-active");

        Assert.Throws<DeviceIdentityLoadException>(
            () => SetupExistingGatewayClassifier.ClassifyWithoutWslProbe(
                registry,
                settings,
                temp.Path));
    }

    [Fact]
    public void RequiresSetup_InNodeMode_WhenActiveIdentityIsCorrupt_PropagatesTypedFailure()
    {
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path) { EnableNodeMode = true };
        var registry = new GatewayRegistry(temp.Path);
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "healthy-inactive",
            Url = "wss://healthy.example.com"
        });
        StoreNodeDeviceToken(registry.GetIdentityDirectory("healthy-inactive"));
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "corrupt-active",
            Url = "wss://corrupt.example.com"
        });
        var activeIdentityDir = registry.GetIdentityDirectory("corrupt-active");
        Directory.CreateDirectory(activeIdentityDir);
        File.WriteAllText(Path.Combine(activeIdentityDir, "device-key-ed25519.json"), "{");
        registry.SetActive("corrupt-active");

        Assert.Throws<DeviceIdentityLoadException>(
            () => StartupSetupState.RequiresSetup(settings, temp.Path, registry));
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
    public void RequiresSetup_ReturnsFalse_WhenNodeModeHasActiveBootstrapGateway()
    {
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path) { EnableNodeMode = true };
        var registry = new GatewayRegistry(temp.Path);
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "bootstrap-gateway",
            Url = "wss://remote.example.com",
            BootstrapToken = "bootstrap-token"
        });
        registry.SetActive("bootstrap-gateway");

        Assert.False(StartupSetupState.RequiresSetup(settings, temp.Path, registry));
        Assert.False(StartupSetupState.CanStartNodeGateway(settings, temp.Path));
    }

    [Fact]
    public void RequiresSetup_ReturnsTrue_WhenNodeModeHasInactiveBootstrapGateway()
    {
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path) { EnableNodeMode = true };
        var registry = new GatewayRegistry(temp.Path);
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "inactive-bootstrap-gateway",
            Url = "wss://remote.example.com",
            BootstrapToken = "bootstrap-token"
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

        var kind = await SetupExistingGatewayClassifier.ClassifyAsync(
            registry,
            settings,
            temp.Path,
            wsl,
            localDataPath: temp.Path);

        Assert.Equal(SetupExistingGatewayKind.AppOwnedLocalWsl, kind);
    }

    [Fact]
    public async Task ClassifyAsync_ReturnsAppOwnedLocalWsl_WhenManagedGatewayUsesTailscaleUrl()
    {
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path);
        var registry = new GatewayRegistry(temp.Path);
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "tailscale-gateway",
            Url = "wss://openclaw.tailnet.ts.net",
            IsLocal = true,
            SetupManagedDistroName = "OpenClawGateway",
        });
        var wsl = new FakeWslCommandRunner([new WslDistroInfo("OpenClawGateway", "Stopped", 2)]);

        var kind = await SetupExistingGatewayClassifier.ClassifyAsync(
            registry, settings, temp.Path, wsl, localDataPath: temp.Path);

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

        var kind = await SetupExistingGatewayClassifier.ClassifyAsync(
            registry,
            settings,
            temp.Path,
            wsl,
            localDataPath: temp.Path);

        Assert.Equal(SetupExistingGatewayKind.ExternalOnly, kind);
    }

    [Fact]
    public async Task ClassifyAsync_StaleOpenClawDistroOnFreshAppState_ReturnsNone()
    {
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path);
        var registry = new GatewayRegistry(temp.Path);
        var wsl = new FakeWslCommandRunner([new WslDistroInfo("OpenClawGateway", "Stopped", 2)]);

        var kind = await SetupExistingGatewayClassifier.ClassifyAsync(
            registry,
            settings,
            temp.Path,
            wsl,
            localDataPath: temp.Path);

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
    public async Task ClassifyAsync_AcceptsNumericSetupStatePhase()
    {
        using var temp = TempSettings.Create();
        var localDataPath = Path.Combine(temp.Path, "local-data");
        Directory.CreateDirectory(localDataPath);
        File.WriteAllText(
            Path.Combine(localDataPath, "setup-state.json"),
            """{"DistroName":"OpenClawGateway","Phase":13,"Status":7}""");
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
    public async Task ClassifyAsync_RejectsNumericNotStartedSetupStatePhase()
    {
        using var temp = TempSettings.Create();
        var localDataPath = Path.Combine(temp.Path, "local-data");
        Directory.CreateDirectory(localDataPath);
        File.WriteAllText(
            Path.Combine(localDataPath, "setup-state.json"),
            """{"DistroName":"OpenClawGateway","Phase":0,"Status":0}""");
        var settings = new SettingsManager(temp.Path);
        var registry = new GatewayRegistry(temp.Path);
        var wsl = new ThrowingWslCommandRunner();

        var kind = await SetupExistingGatewayClassifier.ClassifyAsync(
            registry,
            settings,
            temp.Path,
            wsl,
            localDataPath: localDataPath);

        Assert.Equal(SetupExistingGatewayKind.None, kind);
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

    [Theory]
    [InlineData("ws://127.0.0.1:18789")]
    [InlineData("ws://localhost:18789")]
    public void DefaultAndLegacyGatewayUrls_DoNotBypassSetup(string gatewayUrl)
    {
        // Setup uses explicit IPv4 to avoid localhost resolving to IPv6 across
        // mirrored WSL networking. Preserve the older localhost value only as
        // a settings-migration alias, not as the runtime connection target.
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path) { GatewayUrl = gatewayUrl };

        Assert.True(StartupSetupState.RequiresSetup(settings, temp.Path));
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
