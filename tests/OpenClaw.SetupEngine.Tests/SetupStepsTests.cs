using OpenClaw.Connection;
using System.Net;
using System.Net.Sockets;

namespace OpenClaw.SetupEngine.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public class SetupStepsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _prevDataDir;

    public SetupStepsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"steps-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _prevDataDir = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR");
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", _tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", _prevDataDir);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private SetupContext CreateContext(SetupConfig? config = null)
    {
        var cfg = config ?? new SetupConfig { CleanBeforeRun = true };
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        var journal = new TransactionJournal(filePath: null);
        var commands = new CommandRunner(logger);
        return new SetupContext(cfg, logger, journal, commands, CancellationToken.None);
    }

    // ─── CleanupStaleGatewayStep: Preserve non-local records ───

    [Fact]
    public async Task CleanupStaleGateway_RemovesLocalRecord()
    {
        var ctx = CreateContext();
        var gatewayUrl = ctx.GatewayUrl!;

        // Seed a local gateway record
        var registry = new GatewayRegistry(_tempDir);
        registry.Load();
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "local-gw",
            Url = gatewayUrl,
            IsLocal = true,
            SetupManagedDistroName = ctx.DistroName,
            SshTunnel = null,
        });
        registry.Save();

        var step = new CleanupStaleGatewayStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);

        // Verify record was removed
        var reloaded = new GatewayRegistry(_tempDir);
        reloaded.Load();
        Assert.Null(reloaded.FindByUrl(gatewayUrl));
    }

    [Fact]
    public async Task CleanupStaleGateway_PreservesSshTunneledRecord()
    {
        var ctx = CreateContext();
        var gatewayUrl = ctx.GatewayUrl!;

        // Seed a gateway record with SSH tunnel (remote gateway using localhost)
        var registry = new GatewayRegistry(_tempDir);
        registry.Load();
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "tunneled-gw",
            Url = gatewayUrl,
            IsLocal = true,
            SshTunnel = new SshTunnelConfig("user", "remote.host", 18789, 18789),
        });
        registry.Save();

        var step = new CleanupStaleGatewayStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);

        // Verify record was NOT removed
        var reloaded = new GatewayRegistry(_tempDir);
        reloaded.Load();
        Assert.NotNull(reloaded.FindByUrl(gatewayUrl));
    }

    [Fact]
    public async Task CleanupStaleGateway_PreservesNonLocalRecord()
    {
        var ctx = CreateContext();
        var gatewayUrl = ctx.GatewayUrl!;

        // Seed a non-local gateway record
        var registry = new GatewayRegistry(_tempDir);
        registry.Load();
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "remote-gw",
            Url = gatewayUrl,
            IsLocal = false,
            SshTunnel = null,
        });
        registry.Save();

        var step = new CleanupStaleGatewayStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);

        // Verify record was NOT removed
        var reloaded = new GatewayRegistry(_tempDir);
        reloaded.Load();
        Assert.NotNull(reloaded.FindByUrl(gatewayUrl));
    }

    [Fact]
    public async Task CleanupStaleGateway_DeletesIdentityDirectoryForLocalRecord()
    {
        var ctx = CreateContext();
        var gatewayUrl = ctx.GatewayUrl!;

        var registry = new GatewayRegistry(_tempDir);
        registry.Load();
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "local-gw-with-identity",
            Url = gatewayUrl,
            IsLocal = true,
            SetupManagedDistroName = ctx.DistroName,
        });
        registry.Save();

        // Create an identity directory
        var identityDir = registry.GetIdentityDirectory("local-gw-with-identity");
        Directory.CreateDirectory(identityDir);
        File.WriteAllText(Path.Combine(identityDir, "device-key.json"), "{}");

        var step = new CleanupStaleGatewayStep();
        await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(Directory.Exists(identityDir));
    }

    [Fact]
    public async Task CleanupStaleGateway_SkippedWhenCleanBeforeRunFalse()
    {
        var ctx = CreateContext(new SetupConfig { CleanBeforeRun = false });

        var step = new CleanupStaleGatewayStep();
        Assert.True(step.CanSkip(ctx));
    }

    // ─── InstallCliStep: URL validation and quoting ───

    [Fact]
    public async Task PreflightPort_Loopback_SucceedsForAvailablePort()
    {
        var port = GetFreeTcpPort();
        var ctx = CreateContext(new SetupConfig { GatewayPort = port });

        var result = await new PreflightPortStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task PreflightPort_Lan_FailsWhenAnyBindPortInUse()
    {
        var listener = new TcpListener(IPAddress.Any, 0)
        {
            ExclusiveAddressUse = true
        };
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var ctx = CreateContext(new SetupConfig
            {
                GatewayPort = port,
                Gateway = new GatewayConfig { Bind = "lan" }
            });

            var result = await new PreflightPortStep().ExecuteAsync(ctx, CancellationToken.None);

            Assert.Equal(StepOutcome.Failed, result.Outcome);
            Assert.Contains("already in use", result.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task InstallCli_RejectsHttpUrl()
    {
        var ctx = CreateContext(new SetupConfig
        {
            Gateway = new GatewayConfig { InstallUrl = "http://evil.com/install.sh" }
        });

        var step = new InstallCliStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("HTTPS", result.Message);
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData("bad;user")]
    [InlineData("BadUser")]
    [InlineData("bad user")]
    [InlineData("bad$user")]
    public async Task ConfigureWsl_RejectsInvalidLinuxUserName(string user)
    {
        var ctx = CreateContext();
        ctx.Config.Wsl.User = user;
        ctx.DistroName = "test-distro";

        var step = new ConfigureWslInstanceStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("Invalid WSL user", result.Message);
    }

    [Fact]
    public void WslConfig_AcceptsValidLinuxUserName()
    {
        Assert.True(WslConfig.IsValidLinuxUserName("openclaw"));
        Assert.True(WslConfig.IsValidLinuxUserName("_openclaw"));
        Assert.True(WslConfig.IsValidLinuxUserName("openclaw-user_1"));
    }

    [Fact]
    public async Task CleanupStaleGateway_PreservesUnmarkedLocalhostRecord()
    {
        var ctx = CreateContext();
        var gatewayUrl = ctx.GatewayUrl!;

        var registry = new GatewayRegistry(_tempDir);
        registry.Load();
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "external-localhost",
            Url = gatewayUrl,
            IsLocal = true,
            SshTunnel = null,
        });
        registry.Save();

        var result = await new CleanupStaleGatewayStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var reloaded = new GatewayRegistry(_tempDir);
        reloaded.Load();
        Assert.NotNull(reloaded.GetById("external-localhost"));
    }

    [Fact]
    public async Task InstallCli_RejectsInvalidUrl()
    {
        var ctx = CreateContext(new SetupConfig
        {
            Gateway = new GatewayConfig { InstallUrl = "not-a-url" }
        });

        var step = new InstallCliStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("HTTPS", result.Message);
    }

    [Theory]
    [InlineData("gateway.auth.token")]
    [InlineData("gateway_nodes-allowCommands")]
    [InlineData("a.b_c-1")]
    public void ConfigureGateway_AcceptsSafeExtraConfigKeys(string key)
    {
        Assert.True(ConfigureGatewayStep.IsSafeExtraConfigKey(key));
    }

    [Theory]
    [InlineData("bad key")]
    [InlineData("bad$key")]
    [InlineData("bad;key")]
    [InlineData("bad\nkey")]
    public void ConfigureGateway_RejectsUnsafeExtraConfigKeys(string key)
    {
        Assert.False(ConfigureGatewayStep.IsSafeExtraConfigKey(key));
    }

    [Fact]
    public void ConfigureGateway_AddsDevicePairPublicUrlForLoopbackGateway()
    {
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig { Bind = "loopback" },
            18789,
            "'[]'");

        Assert.Contains(
            "openclaw config set plugins.entries.device-pair.config.publicUrl 'http://127.0.0.1:18789'",
            commands);
    }

    [Fact]
    public void ConfigureGateway_DoesNotOverrideExplicitDevicePairPublicUrl()
    {
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig
            {
                Bind = "loopback",
                ExtraConfig = new Dictionary<string, string>
                {
                    [ConfigureGatewayStep.DevicePairPublicUrlKey] = "https://gateway.example.test",
                },
            },
            18789,
            "'[]'");

        Assert.DoesNotContain("'http://127.0.0.1:18789'", commands);
        Assert.Contains(
            "openclaw config set plugins.entries.device-pair.config.publicUrl 'https://gateway.example.test'",
            commands);
    }

    [Theory]
    [InlineData("""{"bootstrapToken":"boot-token"}""", "boot-token", "bootstrapToken")]
    [InlineData("""{"setupCode":"setup-code"}""", "setup-code", "setupCode")]
    public void MintBootstrapToken_ReadsSupportedQrJsonShapes(string json, string expectedToken, string expectedSource)
    {
        var parsed = MintBootstrapTokenStep.TryReadBootstrapToken(json, out var token, out var source);

        Assert.True(parsed);
        Assert.Equal(expectedToken, token);
        Assert.Equal(expectedSource, source);
    }

    [Fact]
    public void MintBootstrapToken_RejectsQrJsonWithoutUsableBootstrapCredential()
    {
        var parsed = MintBootstrapTokenStep.TryReadBootstrapToken("""{"gatewayUrl":"ws://127.0.0.1:18789"}""", out var token, out var source);

        Assert.False(parsed);
        Assert.Null(token);
        Assert.Null(source);
    }

    [Fact]
    public async Task InstallCli_RejectsFtpUrl()
    {
        var ctx = CreateContext(new SetupConfig
        {
            Gateway = new GatewayConfig { InstallUrl = "ftp://files.com/install.sh" }
        });

        var step = new InstallCliStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("HTTPS", result.Message);
    }

    [Fact]
    public void BuildReplacementSummary_NoExistingConfig_StatesNothingAffected()
    {
        var config = new ExistingConfigDetector.ExistingConfig(
            HasLocalGateway: false,
            LocalGatewayId: null,
            LocalGatewayUrl: null,
            HasDistro: false,
            DistroName: null,
            HasIdentityFiles: false,
            PreservedGatewayCount: 0,
            PreservedGatewayNames: []);

        var summary = ExistingConfigDetector.BuildReplacementSummary(config);

        Assert.Contains("No existing configuration will be affected", summary);
    }

    [Fact]
    public void BuildReplacementSummary_LocalGatewayAndDistro_MentionsReplacement()
    {
        var config = new ExistingConfigDetector.ExistingConfig(
            HasLocalGateway: true,
            LocalGatewayId: "local-gw",
            LocalGatewayUrl: "ws://localhost:18789",
            HasDistro: true,
            DistroName: "OpenClaw",
            HasIdentityFiles: false,
            PreservedGatewayCount: 0,
            PreservedGatewayNames: []);

        var summary = ExistingConfigDetector.BuildReplacementSummary(config);

        Assert.Contains("WSL distro 'OpenClaw' will be deleted and recreated", summary);
        Assert.Contains("Local gateway record will be replaced", summary);
    }

    [Fact]
    public void BuildReplacementSummary_PreservedGateways_MentionsPreservation()
    {
        var config = new ExistingConfigDetector.ExistingConfig(
            HasLocalGateway: true,
            LocalGatewayId: "local-gw",
            LocalGatewayUrl: "ws://localhost:18789",
            HasDistro: false,
            DistroName: null,
            HasIdentityFiles: false,
            PreservedGatewayCount: 2,
            PreservedGatewayNames: ["Remote Gateway", "SSH Tunnel"]);

        var summary = ExistingConfigDetector.BuildReplacementSummary(config);

        Assert.Contains("will NOT be affected", summary);
        Assert.Contains("Remote Gateway", summary);
        Assert.Contains("SSH Tunnel", summary);
    }

    [Fact]
    public void BuildReplacementSummary_IdentityFiles_MentionsRegeneration()
    {
        var config = new ExistingConfigDetector.ExistingConfig(
            HasLocalGateway: true,
            LocalGatewayId: "local-gw",
            LocalGatewayUrl: "ws://localhost:18789",
            HasDistro: false,
            DistroName: null,
            HasIdentityFiles: true,
            PreservedGatewayCount: 0,
            PreservedGatewayNames: []);

        var summary = ExistingConfigDetector.BuildReplacementSummary(config);

        Assert.Contains("Device identity files for the local gateway will be regenerated", summary);
    }

    [Fact]
    public void RedactTokens_RedactsThirtyTwoCharHexString()
    {
        const string token = "1234567890abcdef1234567890abcdef";

        var result = StartGatewayStep.RedactTokens(token);

        Assert.Equal("12345678…[REDACTED]", result);
    }

    [Fact]
    public void RedactTokens_DoesNotRedactShortHexString()
    {
        const string token = "1234567890abcdef1234567890abcde";

        var result = StartGatewayStep.RedactTokens(token);

        Assert.Equal(token, result);
    }

    [Fact]
    public void RedactTokens_LeavesNormalTextUnchanged()
    {
        const string text = "gateway started successfully";

        var result = StartGatewayStep.RedactTokens(text);

        Assert.Equal(text, result);
    }

    [Fact]
    public void RedactTokens_RedactsEmbeddedTokenOnly()
    {
        const string text = "token=1234567890abcdef1234567890abcdef status=ok";

        var result = StartGatewayStep.RedactTokens(text);

        Assert.Equal("token=12345678…[REDACTED] status=ok", result);
    }

    [Fact]
    public void TryGetExistingKeepalive_ReturnsFalseForCorruptMarker()
    {
        var markerPath = Path.Combine(_tempDir, "keepalive.json");
        File.WriteAllText(markerPath, "not json");

        var result = StartKeepaliveStep.TryGetExistingKeepalive(markerPath, "OpenClawGateway", out var pid);

        Assert.False(result);
        Assert.Equal(0, pid);
    }

    [Fact]
    public void IsKeepaliveCommandLine_RequiresDistroAndSleepInfinity()
    {
        Assert.True(StartKeepaliveStep.IsKeepaliveCommandLine(
            @"C:\Windows\System32\wsl.exe -d OpenClawGateway -- sleep infinity",
            "OpenClawGateway"));
        Assert.False(StartKeepaliveStep.IsKeepaliveCommandLine(
            @"C:\Windows\System32\wsl.exe -d OpenClawGateway -- sleep 60",
            "OpenClawGateway"));
        Assert.False(StartKeepaliveStep.IsKeepaliveCommandLine(
            @"C:\Windows\System32\wsl.exe -d OtherGateway -- sleep infinity",
            "OpenClawGateway"));
    }

    // ─── Bind validation ───

    [Fact]
    public async Task ConfigureGateway_RejectsInvalidBind()
    {
        var ctx = CreateContext(new SetupConfig
        {
            Gateway = new GatewayConfig { Bind = "0.0.0.0" }
        });
        ctx.DistroName = "test-distro";

        var step = new ConfigureGatewayStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("Invalid Gateway.Bind", result.Message);
    }

    [Theory]
    [InlineData("loopback")]
    [InlineData("lan")]
    public void ConfigureGateway_AcceptsValidBindValues(string bind)
    {
        var gw = new GatewayConfig { Bind = bind };
        Assert.True(gw.Bind is "loopback" or "lan");
    }

    // ─── Secure defaults ───

    [Fact]
    public void DefaultConfig_HasSecureDefaults()
    {
        var config = new SetupConfig();

        Assert.Equal("loopback", config.Gateway.Bind);
        Assert.True(config.Wsl.Systemd);
        Assert.False(config.Wsl.Interop);
        Assert.False(config.Wsl.AppendWindowsPath);
        Assert.False(config.Wsl.Automount);
        Assert.False(config.Wsl.MountFsTab);
    }

    [Fact]
    public void DefaultConfig_NoPairingScopeFields()
    {
        var props = typeof(PairingConfig).GetProperties();
        var scopeProps = props.Where(p => p.Name.Contains("Scope", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Empty(scopeProps);
    }
}
