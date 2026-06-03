using OpenClaw.Connection;
using System.Net;
using System.Net.Sockets;

namespace OpenClaw.SetupEngine.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public class SetupStepsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _localTempDir;
    private readonly string? _prevDataDir;
    private readonly string? _prevLocalDataDir;

    public SetupStepsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"steps-test-{Guid.NewGuid():N}");
        _localTempDir = Path.Combine(Path.GetTempPath(), $"steps-local-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_localTempDir);
        _prevDataDir = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR");
        _prevLocalDataDir = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR");
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", _tempDir);
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR", _localTempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", _prevDataDir);
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR", _prevLocalDataDir);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        try { Directory.Delete(_localTempDir, recursive: true); } catch { }
    }

    private SetupContext CreateContext(SetupConfig? config = null, ICommandRunner? commands = null)
    {
        var cfg = config ?? new SetupConfig { CleanBeforeRun = true };
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        var journal = new TransactionJournal(filePath: null);
        return new SetupContext(cfg, logger, journal, commands ?? new CommandRunner(logger), CancellationToken.None);
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

    [Fact]
    public void InstallCli_BuildInstallCommand_UsesDefaultWhenVersionMissing()
    {
        var command = InstallCliStep.BuildInstallCommand("https://openclaw.ai/install-cli.sh", null);

        Assert.Equal("curl -fsSL --proto '=https' --tlsv1.2 'https://openclaw.ai/install-cli.sh' | bash", command);
    }

    [Fact]
    public void InstallCli_BuildInstallCommand_AppendsVersionWhenConfigured()
    {
        var command = InstallCliStep.BuildInstallCommand("https://openclaw.ai/install-cli.sh", "2026.5.22");

        Assert.Equal("curl -fsSL --proto '=https' --tlsv1.2 'https://openclaw.ai/install-cli.sh' | bash -s -- --version '2026.5.22'", command);
    }

    [Fact]
    public void InstallCli_BuildInstallCommand_EscapesSingleQuotesInUrlAndVersion()
    {
        var command = InstallCliStep.BuildInstallCommand("https://openclaw.ai/install-cli's.sh", "2026.5.22'a");

        Assert.Equal("curl -fsSL --proto '=https' --tlsv1.2 'https://openclaw.ai/install-cli'\\''s.sh' | bash -s -- --version '2026.5.22'\\''a'", command);
    }

    [Fact]
    public async Task PreflightWsl_FailsForUnsupportedDirectInstallVersion()
    {
        var commands = new FakeCommandRunner(args =>
            args is ["--version"]
                ? Ok("WSL version: 2.3.0.0\n")
                : Ok());
        var ctx = CreateContext(commands: commands);

        var result = await new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("Update WSL", result.Message);
        Assert.Contains(WslInstallSupport.UpdateUrl, result.Message);
    }

    [Fact]
    public async Task PreflightWsl_FailsWithUpdateMessageWhenVersionCommandIsUnsupported()
    {
        var commands = new FakeCommandRunner(args =>
            args is ["--version"]
                ? new CommandResult(1, "", "Invalid command line option: --version", TimeSpan.Zero, TimedOut: false)
                : Ok());
        var ctx = CreateContext(commands: commands);

        var result = await new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("too old", result.Message);
        Assert.Contains(WslInstallSupport.UpdateUrl, result.Message);
    }

    [Fact]
    public async Task CreateWslInstance_UsesDirectFreshInstallAndDoesNotExportBaseDistro()
    {
        var installed = false;
        var commands = new FakeCommandRunner(args =>
        {
            if (args.SequenceEqual(["--list", "--quiet"]))
                return Ok(installed ? "OpenClawGateway\n" : "");
            if (args.Contains("--install"))
            {
                installed = true;
                return Ok("Installing Ubuntu-24.04\n");
            }
            if (args.SequenceEqual(["--list", "--verbose"]))
                return Ok("  NAME              STATE           VERSION\n* OpenClawGateway   Stopped         2\n");
            if (args.SequenceEqual(["-d", "OpenClawGateway", "-u", "root", "--", "sh", "-lc", "id -u && test -d / && echo OPENCLAW_FRESH_WSL_READY"]))
                return Ok("0\nOPENCLAW_FRESH_WSL_READY\n");

            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(commands: commands);

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.DoesNotContain(commands.Calls, c => c.Arguments.Contains("--export"));
        Assert.DoesNotContain(commands.Calls, c =>
            c.Arguments is ["--terminate", "Ubuntu-24.04"] or ["--unregister", "Ubuntu-24.04"]);

        var installCall = Assert.Single(commands.Calls, c => c.Arguments.Contains("--install"));
        Assert.Contains("--distribution", installCall.Arguments);
        Assert.Contains("Ubuntu-24.04", installCall.Arguments);
        Assert.Contains("--name", installCall.Arguments);
        Assert.Contains("OpenClawGateway", installCall.Arguments);
        Assert.Contains("--location", installCall.Arguments);
        Assert.Contains(Path.Combine(ctx.LocalDataDir, "wsl", "OpenClawGateway"), installCall.Arguments);
        Assert.Contains("--web-download", installCall.Arguments);
    }

    [Fact]
    public async Task CreateWslInstance_PartialCleanupAvoidsGlobalShutdownWhenUnregisterSucceeds()
    {
        var listCalls = 0;
        var commands = new FakeCommandRunner(args =>
        {
            if (args.SequenceEqual(["--list", "--quiet"]))
            {
                listCalls++;
                return Ok(listCalls == 1 ? "" : "OpenClawGateway\n");
            }
            if (args.Contains("--install"))
                return Fail("download failed");
            if (args.SequenceEqual(["--terminate", "OpenClawGateway"]))
                return Ok();
            if (args.SequenceEqual(["--unregister", "OpenClawGateway"]))
                return Ok();

            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(commands: commands);

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("download failed", result.Message);
        Assert.DoesNotContain(commands.Calls, c => c.Arguments.SequenceEqual(["--shutdown"]));
    }

    [Fact]
    public async Task CreateWslInstance_PartialCleanupSkipsInstallPathDeleteWhenDistroStateIsUnknown()
    {
        var listCalls = 0;
        var installPath = "";
        var commands = new FakeCommandRunner(args =>
        {
            if (args.SequenceEqual(["--list", "--quiet"]))
            {
                listCalls++;
                return listCalls == 1 ? Ok("") : Fail("list failed");
            }
            if (args.Contains("--install"))
            {
                Directory.CreateDirectory(installPath);
                File.WriteAllText(Path.Combine(installPath, "ext4.vhdx"), "partial");
                return Fail("download failed");
            }
            if (args.SequenceEqual(["--terminate", "OpenClawGateway"]))
                return Fail("terminate unavailable");
            if (args.SequenceEqual(["--unregister", "OpenClawGateway"]))
                return Fail("unregister unavailable");
            if (args.SequenceEqual(["--shutdown"]))
                return Ok();

            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(commands: commands);
        installPath = Path.Combine(ctx.LocalDataDir, "wsl", "OpenClawGateway");

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("download failed", result.Message);
        Assert.Contains("could not confirm whether distro 'OpenClawGateway' is still registered", result.Message);
        Assert.Contains("skipped deleting app-owned install path", result.Message);
        Assert.True(File.Exists(Path.Combine(installPath, "ext4.vhdx")));
    }

    [Fact]
    public async Task CreateWslInstance_PartialCleanupDeletesInstallPathWhenListFailsButDistroIsAlreadyGone()
    {
        var listCalls = 0;
        var installPath = "";
        var commands = new FakeCommandRunner(args =>
        {
            if (args.SequenceEqual(["--list", "--quiet"]))
            {
                listCalls++;
                return listCalls == 1 ? Ok("") : Fail("list failed");
            }
            if (args.Contains("--install"))
            {
                Directory.CreateDirectory(installPath);
                File.WriteAllText(Path.Combine(installPath, "ext4.vhdx"), "partial");
                return Fail("download failed");
            }
            if (args.SequenceEqual(["--terminate", "OpenClawGateway"]) ||
                args.SequenceEqual(["--unregister", "OpenClawGateway"]))
            {
                return Fail("There is no distribution with the supplied name.");
            }

            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(commands: commands);
        installPath = Path.Combine(ctx.LocalDataDir, "wsl", "OpenClawGateway");

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("download failed", result.Message);
        Assert.DoesNotContain("Partial app-owned distro cleanup also failed", result.Message);
        Assert.False(Directory.Exists(installPath));
        Assert.DoesNotContain(commands.Calls, c => c.Arguments.SequenceEqual(["--shutdown"]));
    }

    [Fact]
    public async Task CreateWslInstance_FailsWhenTargetDistroStillExists()
    {
        var commands = new FakeCommandRunner(args =>
            args.SequenceEqual(["--list", "--quiet"])
                ? Ok("OpenClawGateway\n")
                : Fail($"unexpected args: {string.Join(' ', args)}"));
        var ctx = CreateContext(commands: commands);

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("still exists after cleanup", result.Message);
        Assert.DoesNotContain(commands.Calls, c => c.Arguments.Contains("--install"));
    }

    [Fact]
    public async Task CreateWslInstance_FailsWhenInstallDirectoryIsDirty()
    {
        var commands = new FakeCommandRunner(args =>
            args.SequenceEqual(["--list", "--quiet"])
                ? Ok("")
                : Fail($"unexpected args: {string.Join(' ', args)}"));
        var ctx = CreateContext(commands: commands);
        var installPath = Path.Combine(ctx.LocalDataDir, "wsl", "OpenClawGateway");
        Directory.CreateDirectory(installPath);
        File.WriteAllText(Path.Combine(installPath, "ext4.vhdx"), "stale");

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("still contains files after cleanup", result.Message);
        Assert.DoesNotContain(commands.Calls, c => c.Arguments.Contains("--install"));
    }

    [Fact]
    public async Task CreateWslInstance_RemovesStaleFileAtInstallPathBeforeInstalling()
    {
        var installed = false;
        var commands = new FakeCommandRunner(args =>
        {
            if (args.SequenceEqual(["--list", "--quiet"]))
                return Ok(installed ? "OpenClawGateway\n" : "");
            if (args.Contains("--install"))
            {
                installed = true;
                return Ok("Installing Ubuntu-24.04\n");
            }
            if (args.SequenceEqual(["--list", "--verbose"]))
                return Ok("  NAME              STATE           VERSION\n* OpenClawGateway   Stopped         2\n");
            if (args.SequenceEqual(["-d", "OpenClawGateway", "-u", "root", "--", "sh", "-lc", "id -u && test -d / && echo OPENCLAW_FRESH_WSL_READY"]))
                return Ok("0\nOPENCLAW_FRESH_WSL_READY\n");

            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(commands: commands);
        var installPath = Path.Combine(ctx.LocalDataDir, "wsl", "OpenClawGateway");
        Directory.CreateDirectory(Path.GetDirectoryName(installPath)!);
        File.WriteAllText(installPath, "stale");

        var result = await new CreateWslInstanceStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.False(File.Exists(installPath));
        Assert.Contains(commands.Calls, c => c.Arguments.Contains("--install"));
    }

    [Fact]
    public void WslInstallSupport_ParsesVersionAndVerboseDistroList()
    {
        Assert.True(WslInstallSupport.TryParseWslVersion("WSL version: 2.7.3.0", out var version));
        Assert.True(WslInstallSupport.SupportsDirectNamedInstall(version));

        Assert.True(WslInstallSupport.TryGetDistroVersion(
            "  NAME              STATE           VERSION\n* OpenClawGateway   Stopped         2\n",
            "OpenClawGateway",
            out var distroVersion));
        Assert.Equal(2, distroVersion);
    }

    [Fact]
    public void WslInstallSupport_TryGetEnvironmentIssue_DetectsFirmwareVirtualizationOff()
    {
        Assert.True(WslInstallSupport.TryGetEnvironmentIssue(
            "WSL2 is unable to start since virtualization is not enabled on this machine. "
            + "Please ensure the 'Virtual Machine Platform' optional component is enabled "
            + "and virtualization is turned on in your computer's firmware settings.",
            out var message));
        Assert.Contains("BIOS", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("virtualization", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WslInstallSupport_TryGetEnvironmentIssue_DetectsCanonical0x80370102Error()
    {
        // This is the actual error wsl.exe emits on modern Windows builds when
        // the Virtual Machine Platform / Hyper-V feature is disabled.
        Assert.True(WslInstallSupport.TryGetEnvironmentIssue(
            "WSL 2 requires an update to its kernel component.\n"
            + "For information please visit https://aka.ms/wsl2kernel\n"
            + "Error: 0x80370102 The virtual machine could not be started because a "
            + "required feature is not installed.",
            out var message));
        Assert.Contains("Virtual Machine Platform", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wsl --install --no-distribution", message);
    }

    [Fact]
    public void WslInstallSupport_TryGetEnvironmentIssue_ReturnsFalseForHealthyStatus()
    {
        Assert.False(WslInstallSupport.TryGetEnvironmentIssue(
            "Default Distribution: OpenClawGateway\nDefault Version: 2\n",
            out var message));
        Assert.Equal(string.Empty, message);
    }

    [Fact]
    public async Task PreflightWsl_FailsTerminalWhenVirtualizationDisabledInFirmware()
    {
        var commands = new FakeCommandRunner(args =>
        {
            if (args is ["--version"])
                return Ok("WSL version: 2.7.3.0\n");
            if (args is ["--status"])
                return Ok(
                    "WSL2 is unable to start since virtualization is not enabled on this machine. "
                    + "Please ensure the 'Virtual Machine Platform' optional component is enabled "
                    + "and virtualization is turned on in your computer's firmware settings.");
            return Ok();
        });
        var ctx = CreateContext(commands: commands);

        var result = await new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("virtualization", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BIOS", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreflightWsl_FailsTerminalWhenWslEmitsHcsServiceNotAvailable()
    {
        var commands = new FakeCommandRunner(args =>
        {
            if (args is ["--version"])
                return Ok("WSL version: 2.7.3.0\n");
            if (args is ["--status"])
                return Ok(
                    "WSL 2 requires an update to its kernel component.\n"
                    + "Error: 0x80370102 The virtual machine could not be started because a "
                    + "required feature is not installed.");
            return Ok();
        });
        var ctx = CreateContext(commands: commands);

        var result = await new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("Virtual Machine Platform", result.Message);
        Assert.Contains("wsl --install --no-distribution", result.Message);
    }

    [Fact]
    public async Task PreflightWsl_SucceedsWhenStatusOutputIsHealthy()
    {
        var commands = new FakeCommandRunner(args =>
        {
            if (args is ["--version"])
                return Ok("WSL version: 2.7.3.0\n");
            if (args is ["--status"])
                return Ok("Default Distribution: OpenClawGateway\nDefault Version: 2\n");
            return Ok();
        });
        var ctx = CreateContext(commands: commands);

        var result = await new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
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

    [Fact]
    public void WindowsNodeContext_CanSkipWhenDisabled()
    {
        var ctx = CreateContext(new SetupConfig
        {
            WindowsNodeContext = new WindowsNodeContextConfig { Enabled = false }
        });

        Assert.True(new WindowsNodeBootstrapContextStep().CanSkip(ctx));
    }

    [Fact]
    public void WindowsNodeContext_BuildApplyScript_UsesAbsolutePathAndMinimalShape()
    {
        var script = WindowsNodeBootstrapContextStep.BuildApplyScript("/home/openclaw/.openclaw/workspace");

        Assert.Contains("set -o pipefail", script);
        Assert.Contains("workspace='/home/openclaw/.openclaw/workspace'", script);
        Assert.Contains("AGENTS_SYMLINK:$agents", script);
        Assert.Contains("mkdir -p \"$workspace\"", script);
        Assert.Contains(": > \"$agents\"", script);
        Assert.Contains("WINDOWS_NODE_CONTEXT_BOOTSTRAP_FALLBACK", script);
        Assert.Contains("awk -v BEGIN_M=\"$begin_marker\" -v END_M=\"$end_marker\"", script);
        Assert.Contains("printf '%s' \"$block_b64\" | base64 -d >> \"$tmp\"", script);
        Assert.Contains("WINDOWS_NODE_CONTEXT_MARKERS_MALFORMED", script);
        Assert.Contains("WINDOWS_NODE_CONTEXT_READY", script);
        // Must not depend on node or carry an embedded JS payload.
        Assert.DoesNotContain(" node ", script);
        Assert.DoesNotContain(" node -", script);
        Assert.DoesNotContain("apply_js_b64", script);
        Assert.DoesNotContain("openclaw setup", script);
        Assert.DoesNotContain("openclaw config get", script);
        Assert.DoesNotContain("AGENTS_MISSING_AFTER_SETUP", script);
        Assert.DoesNotContain("$HOME", script);
        Assert.DoesNotContain("case \"$candidate\"", script);
        Assert.DoesNotContain("<<'NODE'", script);
        Assert.DoesNotContain("OPENCLAW_GATEWAY_TOKEN", script);
    }

    [Fact]
    public void WindowsNodeContext_BuildRollbackScript_UsesAbsolutePathAndMinimalShape()
    {
        var script = WindowsNodeBootstrapContextStep.BuildRollbackScript("/home/openclaw/.openclaw/workspace");

        Assert.Contains("set -o pipefail", script);
        Assert.Contains("workspace='/home/openclaw/.openclaw/workspace'", script);
        Assert.Contains("awk -v BEGIN_M=\"$begin_marker\" -v END_M=\"$end_marker\"", script);
        Assert.Contains("WINDOWS_NODE_CONTEXT_ABSENT", script);
        Assert.Contains("WINDOWS_NODE_CONTEXT_REMOVED", script);
        // Must not depend on node or carry an embedded JS payload.
        Assert.DoesNotContain(" node ", script);
        Assert.DoesNotContain(" node -", script);
        Assert.DoesNotContain("rollback_js_b64", script);
        Assert.DoesNotContain("openclaw setup", script);
        Assert.DoesNotContain("openclaw config get", script);
        Assert.DoesNotContain("rm -f \"$agents\"", script);
        Assert.DoesNotContain("$HOME", script);
        Assert.DoesNotContain("case \"$candidate\"", script);
        Assert.DoesNotContain("<<'NODE'", script);
    }

    [Theory]
    [InlineData("/home/openclaw/.openclaw/workspace", "/home/openclaw", "/home/openclaw/.openclaw/workspace")]
    [InlineData("~", "/home/openclaw", "/home/openclaw")]
    [InlineData("~/.openclaw/custom workspace", "/home/openclaw", "/home/openclaw/.openclaw/custom workspace")]
    [InlineData("relative/path", "/home/openclaw", "/home/openclaw/relative/path")]
    [InlineData("", "/home/openclaw", "/home/openclaw/.openclaw/workspace")]
    [InlineData("null", "/home/openclaw", "/home/openclaw/.openclaw/workspace")]
    [InlineData("undefined", "/home/openclaw", "/home/openclaw/.openclaw/workspace")]
    [InlineData("/abs/path", "/home/openclaw/", "/abs/path")]
    [InlineData("~/x", "/home/openclaw/", "/home/openclaw/x")]
    public void WindowsNodeContext_ExpandLinuxPath_ResolvesCorrectly(string input, string home, string expected)
    {
        Assert.Equal(expected, WindowsNodeBootstrapContextStep.ExpandLinuxPath(input, home));
    }

    [Theory]
    [InlineData("\"/home/openclaw/.openclaw/workspace\"\n", "/home/openclaw/.openclaw/workspace")]
    [InlineData("Config warnings:\n- plugins.entries.device-pair\n\"/home/openclaw/.openclaw/workspace\"\n", "/home/openclaw/.openclaw/workspace")]
    [InlineData("\"~/.openclaw/workspace\"\n", "~/.openclaw/workspace")]
    [InlineData("/home/openclaw/.openclaw/workspace\n", "/home/openclaw/.openclaw/workspace")]
    [InlineData("null\n", null)]
    [InlineData("", null)]
    public void WindowsNodeContext_ExtractWorkspaceFromConfigOutput_ParsesValues(string stdout, string? expected)
    {
        Assert.Equal(expected, WindowsNodeBootstrapContextStep.ExtractWorkspaceFromConfigOutput(stdout));
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_RunsInWslAsConfiguredUserAndResolvesWorkspace()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw setup"))
                    return Ok("");
                if (command.Contains("openclaw config get agents.defaults.workspace"))
                    return Ok("\"~/.openclaw/workspace\"\n");
                if (command.Contains("WINDOWS_NODE_CONTEXT_READY"))
                    return Ok(string.Join("\n",
                        "WINDOWS_NODE_CONTEXT_BOOTSTRAP_FALLBACK:/home/openclaw/.openclaw/workspace/AGENTS.md",
                        "WINDOWS_NODE_CONTEXT_WORKSPACE:/home/openclaw/.openclaw/workspace",
                        "WINDOWS_NODE_CONTEXT_READY",
                        ""));
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);

        var result = await new WindowsNodeBootstrapContextStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(4, commands.WslCalls.Count);
        Assert.All(commands.WslCalls, c =>
        {
            Assert.Equal("OpenClawGateway", c.DistroName);
            Assert.Equal("openclaw", c.User);
        });
        Assert.Contains("getent passwd", commands.WslCalls[0].Command);
        Assert.Contains("openclaw setup", commands.WslCalls[1].Command);
        Assert.Contains("openclaw config get agents.defaults.workspace", commands.WslCalls[2].Command);
        Assert.Contains("workspace='/home/openclaw/.openclaw/workspace'", commands.WslCalls[3].Command);
        // getent uses $(id -un) command-substitution and no $vars, so argv path is safe.
        Assert.False(commands.WslCalls[0].InputViaStdin);
        // openclaw setup + config get scripts both reference $PATH via WslPathPrefix,
        // which wsl.exe would rewrite on the argv path — see docs/WSL_EXE_ARGV_PITFALL.md.
        Assert.True(commands.WslCalls[1].InputViaStdin);
        Assert.True(commands.WslCalls[2].InputViaStdin);
        // Apply script uses $workspace etc., must use stdin.
        Assert.True(commands.WslCalls[3].InputViaStdin);
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_UsesExplicitWorkspaceOverride()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw setup --workspace"))
                    return Ok("");
                if (command.Contains("workspace='/custom/abs/path'"))
                    return Ok("WINDOWS_NODE_CONTEXT_READY\n");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(new SetupConfig
        {
            WindowsNodeContext = new WindowsNodeContextConfig { WorkspacePath = "/custom/abs/path" }
        }, commands);

        var result = await new WindowsNodeBootstrapContextStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        // No config-get call when override is set.
        Assert.DoesNotContain(commands.WslCalls, c => c.Command.Contains("openclaw config get"));
        // Absolute path threads through to BOTH the setup command and the apply script
        // (verified by the apply script asserting workspace='/custom/abs/path').
        Assert.Contains(commands.WslCalls, c => c.Command.Contains("openclaw setup --workspace '/custom/abs/path'"));
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_OverrideWithTilde_ExpandsBeforePassingToSetup()
    {
        // Regression: a ~/foo override must be expanded once so that the same
        // absolute path goes to `openclaw setup --workspace` and the apply script.
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw setup --workspace"))
                    return Ok("");
                if (command.Contains("workspace='/home/openclaw/custom-ws'"))
                    return Ok("WINDOWS_NODE_CONTEXT_READY\n");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(new SetupConfig
        {
            WindowsNodeContext = new WindowsNodeContextConfig { WorkspacePath = "~/custom-ws" }
        }, commands);

        var result = await new WindowsNodeBootstrapContextStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Contains(commands.WslCalls,
            c => c.Command.Contains("openclaw setup --workspace '/home/openclaw/custom-ws'"));
        Assert.DoesNotContain(commands.WslCalls,
            c => c.Command.Contains("--workspace '~/custom-ws'"));
    }

    [Fact]
    public async Task WindowsNodeContext_Rollback_RunsRollbackScriptViaStdin()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw setup"))
                    return Ok("");
                if (command.Contains("openclaw config get agents.defaults.workspace"))
                    return Ok("\"~/.openclaw/workspace\"\n");
                if (command.Contains("WINDOWS_NODE_CONTEXT_REMOVED"))
                    return Ok("WINDOWS_NODE_CONTEXT_REMOVED\n");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);

        await new WindowsNodeBootstrapContextStep().RollbackAsync(ctx, CancellationToken.None);

        Assert.NotEmpty(commands.WslCalls);
        // Last call is the rollback script and must use stdin.
        Assert.Contains("WINDOWS_NODE_CONTEXT_REMOVED", commands.WslCalls[^1].Command);
        Assert.True(commands.WslCalls[^1].InputViaStdin);
        // The getent helper still uses argv (no $vars); config-get helper now uses stdin.
        Assert.False(commands.WslCalls[0].InputViaStdin);
        Assert.Contains("getent passwd", commands.WslCalls[0].Command);
        Assert.Contains(commands.WslCalls, c =>
            c.Command.Contains("openclaw config get agents.defaults.workspace") && c.InputViaStdin);
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_FailsWhenHomeUnresolvable()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return new CommandResult(1, "", "", TimeSpan.Zero, TimedOut: false);
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);

        var result = await new WindowsNodeBootstrapContextStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("Could not resolve Linux home directory", result.Message);
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_FailsWithoutReadyMarker()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw setup"))
                    return Ok("");
                if (command.Contains("openclaw config get agents.defaults.workspace"))
                    return Ok("\"~/.openclaw/workspace\"\n");
                return Fail("apply script failed");
            });
        var ctx = CreateContext(commands: commands);

        var result = await new WindowsNodeBootstrapContextStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("Windows node context injection failed", result.Message);
    }

    private static CommandResult Ok(string stdout = "", string stderr = "")
        => new(0, stdout, stderr, TimeSpan.Zero, TimedOut: false);

    private static CommandResult Fail(string stderr = "")
        => new(1, "", stderr, TimeSpan.Zero, TimedOut: false);

    private sealed class FakeCommandRunner(
        Func<string[], CommandResult> run,
        Func<string, string, string?, CommandResult>? runInWsl = null) : ICommandRunner
    {
        public List<(string Executable, string[] Arguments)> Calls { get; } = [];
        public List<(string DistroName, string Command, TimeSpan Timeout, string? User, bool InputViaStdin)> WslCalls { get; } = [];

        public Task<CommandResult> RunAsync(
            string executable,
            string[] arguments,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null,
            string? stdinInput = null,
            CancellationToken ct = default)
        {
            Calls.Add((executable, arguments));
            return Task.FromResult(run(arguments));
        }

        public Task<CommandResult> RunInWslAsync(
            string distroName,
            string command,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken ct = default,
            string? user = null,
            bool inputViaStdin = false)
        {
            WslCalls.Add((distroName, command, timeout, user, inputViaStdin));
            if (runInWsl == null)
                throw new NotSupportedException("RunInWslAsync is not expected in these tests.");

            return Task.FromResult(runInWsl(distroName, command, user));
        }
    }
}
