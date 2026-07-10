using OpenClaw.Connection;
using OpenClaw.TestSupport;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit.Abstractions;

namespace OpenClaw.SetupEngine.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public class SetupStepsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _localTempDir;
    private readonly string? _prevDataDir;
    private readonly string? _prevLocalDataDir;
    private readonly ITestOutputHelper _output;
    private const string DevicePairPluginNotFoundOutput = "plugins.entries.device-pair: plugin not found: device-pair";
    private const string OtherPluginNotFoundOutput = "plugins.entries.other-plugin: plugin not found: other-plugin";

    public SetupStepsTests(ITestOutputHelper output)
    {
        _output = output;
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
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(_localTempDir, recursive: true); } catch { }
    }

    private SetupContext CreateContext(SetupConfig? config = null, ICommandRunner? commands = null)
    {
        var cfg = config ?? new SetupConfig { CleanBeforeRun = true };
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        var journal = new TransactionJournal(filePath: null);
        return new SetupContext(cfg, logger, journal, commands ?? new CommandRunner(logger), CancellationToken.None);
    }

    [Fact]
    public void WriteSettingsJson_AppliesConfiguredCapabilitiesBeforePersisting()
    {
        var config = new SetupConfig
        {
            Capabilities = new CapabilitiesConfig
            {
                System = false,
                Canvas = true,
                Screen = true,
                Camera = false,
                Location = false,
                Browser = false,
                Device = true,
                Tts = true,
                Stt = false,
            },
        };
        var ctx = CreateContext(config);

        VerifyEndToEndStep.WriteSettingsJson(ctx);

        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(_tempDir, "settings.json")));
        Assert.False(result.RootElement.GetProperty("NodeSystemRunEnabled").GetBoolean());
        Assert.True(result.RootElement.GetProperty("NodeCanvasEnabled").GetBoolean());
        Assert.True(result.RootElement.GetProperty("NodeScreenEnabled").GetBoolean());
        Assert.False(result.RootElement.GetProperty("NodeCameraEnabled").GetBoolean());
        Assert.False(result.RootElement.GetProperty("NodeLocationEnabled").GetBoolean());
        Assert.False(result.RootElement.GetProperty("NodeBrowserProxyEnabled").GetBoolean());
        Assert.True(result.RootElement.GetProperty("NodeTtsEnabled").GetBoolean());
        Assert.False(result.RootElement.GetProperty("NodeSttEnabled").GetBoolean());
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
    public async Task WaitForPortFree_ReturnsImmediately_WhenPortIsAlreadyFree()
    {
        var port = GetFreeTcpPort();
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);

        // Should complete well within 1 second because the port is already free
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await PreflightPortStep.WaitForPortFreeAsync(port, "loopback", logger, cts.Token, maxWaitSeconds: 10);
        // No assertion needed — completing without cancellation/timeout is the success condition
    }

    [Fact]
    public async Task WaitForPortFree_PollsUntilPortReleased()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);

        // Release the port after a short delay (simulates WSL proxy teardown lag)
        _ = Task.Run(async () =>
        {
            await Task.Delay(400);
            listener.Stop();
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await PreflightPortStep.WaitForPortFreeAsync(port, "loopback", logger, cts.Token, maxWaitSeconds: 5);

        // Port should now be free
        Assert.True(PreflightPortStep.CanBind(IPAddress.Loopback, port, out _));
    }

    [Fact]
    public async Task PreflightPort_Loopback_SucceedsAfterPortReleasedDuringPoll()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Release after 300ms — simulates a slow WSL proxy shutdown
        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            listener.Stop();
        });

        var ctx = CreateContext(new SetupConfig { GatewayPort = port });
        var result = await new PreflightPortStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
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
        Assert.DoesNotContain(commands.Calls, c => c.Arguments.SequenceEqual(["--shutdown"]));
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
        Assert.Equal(new Version(2, 7, 3, 0), version);
        Assert.True(WslInstallSupport.SupportsDirectNamedInstall(version));

        Assert.True(WslInstallSupport.TryGetDistroVersion(
            "  NAME              STATE           VERSION\n* OpenClawGateway   Stopped         2\n",
            "OpenClawGateway",
            out var distroVersion));
        Assert.Equal(2, distroVersion);
    }

    // Regression: wsl.exe emits UTF-16LE on some Windows builds, and localized
    // Windows changes the human-readable label around the stable WSL product token.
    [Theory]
    [InlineData("WSL version: 2.7.3.0", "2.7.3.0")]                       // English
    [InlineData("WSL-Version: 2.7.7.0", "2.7.7.0")]                       // German / NUL-stripped UTF-16
    [InlineData("WSL-Version: 2.7.7.0\nKernelversion: 6.18.26.1-1\nWSLg-Version: 1.0.73.2\nWindows-Version: 10.0.26300.8553", "2.7.7.0")]
    [InlineData("Versión de WSL: 2.7.3.0", "2.7.3.0")]                    // Spanish
    [InlineData("Versión de WSL: 2.7.3.0\nKernel: 5.15.0.1", "2.7.3.0")]  // Spanish with trailing lines
    [InlineData("WSL バージョン: 2.7.8.0", "2.7.8.0")]                    // Japanese-style label
    [InlineData("WSL版本: 2.7.9.0", "2.7.9.0")]                          // No separator after WSL
    public void WslInstallSupport_TryParseWslVersion_HandlesLocalizedAndHyphenatedLabels(string output, string expectedVersion)
    {
        Assert.True(WslInstallSupport.TryParseWslVersion(output, out var version),
            $"Expected TryParseWslVersion to succeed for: {output}");
        Assert.Equal(Version.Parse(expectedVersion), version);
        Assert.True(WslInstallSupport.SupportsDirectNamedInstall(version),
            $"Expected parsed version {version} to satisfy minimum install requirement");
    }

    // Mirrors microsoft/WSL localization/strings/*/Resources.resw MessagePackageVersions.
    [Theory]
    [InlineData("cs-CZ", "Verze WSL: 2.7.3.0")]
    [InlineData("da-DK", "WSL-version: 2.7.3.0")]
    [InlineData("de-DE", "WSL-Version: 2.7.3.0")]
    [InlineData("en-GB", "WSL version: 2.7.3.0")]
    [InlineData("en-US", "WSL version: 2.7.3.0")]
    [InlineData("es-ES", "Versión de WSL: 2.7.3.0")]
    [InlineData("fi-FI", "WSL-versio: 2.7.3.0")]
    [InlineData("fr-FR", "Version WSL : 2.7.3.0")]
    [InlineData("hu-HU", "WSL-verzió: 2.7.3.0")]
    [InlineData("it-IT", "Versione WSL: 2.7.3.0")]
    [InlineData("ja-JP", "WSL バージョン: 2.7.3.0")]
    [InlineData("ko-KR", "WSL 버전: 2.7.3.0")]
    [InlineData("nb-NO", "WSL-versjon: 2.7.3.0")]
    [InlineData("nl-NL", "WSL-versie: 2.7.3.0")]
    [InlineData("pl-PL", "Wersja podsystemu WSL: 2.7.3.0")]
    [InlineData("pt-BR", "Versão do WSL: 2.7.3.0")]
    [InlineData("pt-PT", "Versão WSL: 2.7.3.0")]
    [InlineData("ru-RU", "Версия WSL: 2.7.3.0")]
    [InlineData("sv-SE", "WSL-version: 2.7.3.0")]
    [InlineData("tr-TR", "WSL sürümü: 2.7.3.0")]
    [InlineData("zh-CN", "WSL 版本: 2.7.3.0")]
    [InlineData("zh-TW", "WSL 版本： 2.7.3.0")]
    public void WslInstallSupport_TryParseWslVersion_HandlesMicrosoftLocalizedPackageVersionLabels(
        string locale,
        string output)
    {
        Assert.True(WslInstallSupport.TryParseWslVersion(output, out var version),
            $"Expected TryParseWslVersion to succeed for {locale}: {output}");
        Assert.Equal(new Version(2, 7, 3, 0), version);
    }

    [Theory]
    [InlineData("WSL-Version: 2.7.7.0", "2.7.7.0")]
    [InlineData("Versión de WSL: 2.7.3.0", "2.7.3.0")]
    public void WslInstallSupport_TryParseWslVersion_NulStrippedUtf16_ParsesCorrectVersion(string raw, string expectedVersion)
    {
        // Simulate UTF-16LE NUL-byte injection then NUL-stripping.
        var utf16Encoded = string.Join("\0", raw.ToCharArray()) + "\0";
        var stripped = utf16Encoded.Replace("\0", "");
        Assert.True(WslInstallSupport.TryParseWslVersion(stripped, out var version),
            $"Expected TryParseWslVersion to succeed for NUL-stripped: {raw}");
        Assert.Equal(Version.Parse(expectedVersion), version);
    }

    [Fact]
    public void WslInstallSupport_TryParseWslVersion_IgnoresAdjacentWslAndWindowsVersionLines()
    {
        var output = "WSLg-Version: 1.0.73.2\n"
            + "Windows-Version: 10.0.26300.8553\n"
            + "Kernelversion: 6.18.26.1-1\n"
            + "WSL-Version: 2.7.7.0\n";

        Assert.True(WslInstallSupport.TryParseWslVersion(output, out var version));
        Assert.Equal(new Version(2, 7, 7, 0), version);
    }

    [Fact]
    public void WslInstallSupport_TryParseWslVersion_FailsWhenOnlyAdjacentComponentVersionsArePresent()
    {
        var output = "WSLg-Version: 1.0.73.2\n"
            + "Windows-Version: 10.0.26300.8553\n"
            + "Kernelversion: 6.18.26.1-1\n";

        Assert.False(WslInstallSupport.TryParseWslVersion(output, out _));
    }

    [Fact]
    public void WslInstallSupport_TryGetEnvironmentIssue_DetectsFirmwareVirtualizationOff()
    {
        Assert.True(WslInstallSupport.TryGetEnvironmentIssue(
            "WSL2 is unable to start since virtualization is not enabled on this machine. "
            + "Please ensure the 'Virtual Machine Platform' optional component is enabled "
            + "and virtualization is turned on in your computer's firmware settings.",
            Architecture.X64,
            out var message));
        Assert.Contains("BIOS", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VT-x", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("virtualization", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WslInstallSupport_TryGetEnvironmentIssue_UsesArm64WordingOnArm64()
    {
        Assert.True(WslInstallSupport.TryGetEnvironmentIssue(
            "WSL2 is unable to start since virtualization is not enabled on this machine. "
            + "Please ensure the 'Virtual Machine Platform' optional component is enabled "
            + "and virtualization is turned on in your computer's firmware settings.",
            Architecture.Arm64,
            out var message));
        Assert.Contains("ARM64", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UEFI", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("virtualization", message, StringComparison.OrdinalIgnoreCase);
        // Must not name x86-specific extensions on ARM64.
        Assert.DoesNotContain("VT-x", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AMD-V", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SVM", message, StringComparison.OrdinalIgnoreCase);
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
    public void WslInstallSupport_TryGetEnvironmentIssue_DetectsUnsupportedMachineConfigurationStatus()
    {
        var status = NulSeparated("Default Version: 2\r\n\r\n"
            + "WSL2 is not supported with your current machine configuration.\r\n\r\n"
            + "Please enable the \"Virtual Machine Platform\" optional component and ensure virtualization is enabled in the BIOS.\r\n\r\n"
            + "Enable \"Virtual Machine Platform\" by running: wsl.exe --install --no-distribution\r\n\r\n"
            + "For information please visit https://aka.ms/enablevirtualization\r\n");

        Assert.True(WslInstallSupport.TryGetEnvironmentIssue(status, Architecture.X64, out var message));
        Assert.Contains("Virtual Machine Platform", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("virtualization", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wsl --install --no-distribution", message);
        Assert.Contains("VT-x", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BIOS/UEFI", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reboot", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WslInstallSupport_TryGetEnvironmentIssue_UsesArm64WordingForUnsupportedMachineConfiguration()
    {
        var status = NulSeparated("Default Version: 2\r\n\r\n"
            + "WSL2 is not supported with your current machine configuration.\r\n\r\n"
            + "Please enable the \"Virtual Machine Platform\" optional component and ensure virtualization is enabled in the BIOS.\r\n\r\n"
            + "Enable \"Virtual Machine Platform\" by running: wsl.exe --install --no-distribution\r\n\r\n"
            + "For information please visit https://aka.ms/enablevirtualization\r\n");

        Assert.True(WslInstallSupport.TryGetEnvironmentIssue(status, Architecture.Arm64, out var message));
        Assert.Contains("Virtual Machine Platform", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ARM64", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Surface", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("device-management policy", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wsl --install --no-distribution", message);
        Assert.DoesNotContain("BIOS", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VT-x", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AMD-V", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SVM", message, StringComparison.OrdinalIgnoreCase);
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
        // Don't assert on "BIOS" / "UEFI" here -- the wording flexes by host
        // CPU architecture (this test runs on either x64 or Arm64 dev boxes).
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
    public async Task PreflightWsl_FailsTerminalWhenStatusReportsUnsupportedMachineConfiguration()
    {
        var commands = new FakeCommandRunner(args =>
        {
            if (args is ["--version"])
                return Ok("WSL version: 2.5.9.0\n");
            if (args is ["--status"])
                return Ok(NulSeparated(
                    "Default Version: 2\r\n\r\n"
                    + "WSL2 is not supported with your current machine configuration.\r\n\r\n"
                    + "Please enable the \"Virtual Machine Platform\" optional component and ensure virtualization is enabled in the BIOS.\r\n\r\n"
                    + "Enable \"Virtual Machine Platform\" by running: wsl.exe --install --no-distribution\r\n\r\n"
                    + "For information please visit https://aka.ms/enablevirtualization\r\n"));
            return Fail($"unexpected args: {string.Join(' ', args)}");
        });
        var ctx = CreateContext(commands: commands);

        var result = await new PreflightWslStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("Virtual Machine Platform", result.Message);
        Assert.Contains("virtualization", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wsl --install --no-distribution", result.Message);
        Assert.DoesNotContain(commands.Calls, c => c.Arguments.Contains("--install"));
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

    // Issue: device-pair plugin must be enabled, not just configured. Otherwise
    // OAuth providers (Codex, etc.) hang at scope-upgrade and never emit auth URLs.
    [Fact]
    public void ConfigureGateway_EnablesDevicePairPluginForLoopbackGateway()
    {
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig { Bind = "loopback" },
            18789,
            "'[]'");

        Assert.Contains(
            "openclaw config set plugins.entries.device-pair.enabled true",
            commands);
    }

    [Fact]
    public void ConfigureGateway_TailscaleUsesNativeServeAndDefersPublicUrlUntilEndpointKnown()
    {
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig { Bind = "loopback" },
            18789,
            "'[]'",
            new TailscaleConfig { Enabled = true });

        Assert.Contains("openclaw config set gateway.tailscale.mode serve", commands);
        Assert.Contains("openclaw config set gateway.tailscale.resetOnExit false", commands);
        Assert.Contains("openclaw config set gateway.auth.allowTailscale true", commands);
        Assert.DoesNotContain("http://127.0.0.1:18789", commands);
    }

    [Fact]
    public void TailscalePolicy_ParsesAuthorizationUrlsAndServeRoutes()
    {
        var url = TailscaleSetupPolicy.TryReadAuthorizationUrl("To authenticate, visit https://login.tailscale.com/a/abc_123-now");

        Assert.Equal("https://login.tailscale.com/a/abc_123-now", url!.AbsoluteUri);
        Assert.True(TailscaleSetupPolicy.ServeStatusRoutesToPort("{\"Backend\":\"http://127.0.0.1:18789\"}", 18789));
        Assert.False(TailscaleSetupPolicy.ServeStatusRoutesToPort("{\"Backend\":\"http://127.0.0.1:9999\"}", 18789));
    }

    [Fact]
    public void ConfigureGateway_EnablesDevicePairPluginWhenPublicUrlOverridden()
    {
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig
            {
                Bind = "lan",
                ExtraConfig = new Dictionary<string, string>
                {
                    [ConfigureGatewayStep.DevicePairPublicUrlKey] = "https://gateway.example.test",
                },
            },
            18789,
            "'[]'");

        Assert.Contains(
            "openclaw config set plugins.entries.device-pair.enabled true",
            commands);
    }

    [Fact]
    public void ConfigureGateway_DoesNotEnableDevicePairWhenNoPublicUrlAvailable()
    {
        // LAN bind with no operator-supplied publicUrl: we don't know where the plugin
        // would be reachable, so don't enable it; preserves the prior behavior.
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig { Bind = "lan" },
            18789,
            "'[]'");

        Assert.DoesNotContain(
            "openclaw config set plugins.entries.device-pair.enabled",
            commands);
    }

    [Fact]
    public void ConfigureGateway_RespectsExplicitDevicePairEnabledOverride()
    {
        // If the operator explicitly sets the enabled flag via ExtraConfig, the
        // ExtraConfig loop writes it and we don't append a duplicate.
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig
            {
                Bind = "loopback",
                ExtraConfig = new Dictionary<string, string>
                {
                    [ConfigureGatewayStep.DevicePairEnabledKey] = "false",
                },
            },
            18789,
            "'[]'");

        Assert.Contains(
            "openclaw config set plugins.entries.device-pair.enabled 'false'",
            commands);
        Assert.DoesNotContain(
            "openclaw config set plugins.entries.device-pair.enabled true",
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

    // Characterization (PR 3 WslShellQuoting migration): the ExtraConfig value is emitted
    // as a fully-wrapped POSIX token, so an embedded single quote must close-escape-reopen
    // ('\'') and remain single-quoted. Pins the generated command byte-for-byte.
    [Fact]
    public void ConfigureGateway_QuotesExtraConfigValueWithEmbeddedSingleQuote()
    {
        var commands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig
            {
                Bind = "lan",
                ExtraConfig = new Dictionary<string, string>
                {
                    ["gateway.custom.note"] = "a'b",
                },
            },
            18789,
            "'[]'");

        Assert.Contains(
            "openclaw config set gateway.custom.note 'a'\\''b'",
            commands);
    }

    [Fact]
    public async Task ConfigureGateway_UsesExtendedTimeoutForWslConfig()
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, _, _) => Ok("GATEWAY_CONFIGURED"));
        var ctx = CreateContext(commands: commands);

        var result = await new ConfigureGatewayStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var wslCall = Assert.Single(commands.WslCalls);
        Assert.Equal(
            ConfigureGatewayStep.ComputeConfigurationTimeout(wslCall.Command),
            wslCall.Timeout);
        Assert.True(wslCall.Timeout >= ConfigureGatewayStep.MinConfigurationTimeout);
    }

    [Fact]
    public async Task ConfigureGateway_ReturnsTimeoutSpecificFailure()
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, _, timeout) => new CommandResult(-1, "", "", timeout, TimedOut: true));
        var ctx = CreateContext(commands: commands);

        var result = await new ConfigureGatewayStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        var message = Assert.IsType<string>(result.Message);
        Assert.Contains("Gateway configuration timed out after", message);
        Assert.DoesNotContain("exit -1", message);
    }

    [Fact]
    public void ComputeConfigurationTimeout_ScalesWithConfigCommandCount()
    {
        // Each `openclaw config set` pays a cold Node start inside WSL. As more keys are
        // configured the budget must grow, otherwise the step silently regresses toward a
        // timeout (the failure mode the fixed 120s cap only partially closed).
        var fewCommands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig { Bind = "lan" },
            18789,
            "'[]'");
        var manyCommands = ConfigureGatewayStep.BuildConfigCommands(
            new GatewayConfig
            {
                Bind = "loopback",
                ExtraConfig = new Dictionary<string, string>
                {
                    ["gateway.extra.one"] = "1",
                    ["gateway.extra.two"] = "2",
                    ["gateway.extra.three"] = "3",
                    ["gateway.extra.four"] = "4",
                },
            },
            18789,
            "'[]'");

        var fewTimeout = ConfigureGatewayStep.ComputeConfigurationTimeout(fewCommands);
        var manyTimeout = ConfigureGatewayStep.ComputeConfigurationTimeout(manyCommands);

        Assert.True(
            manyTimeout > fewTimeout,
            $"Timeout should grow with config command count; few={fewTimeout}, many={manyTimeout}");
    }

    [Fact]
    public void ComputeConfigurationTimeout_NeverBelowFloor()
    {
        // A minimal config set must still receive the safety floor, never base + one.
        var timeout = ConfigureGatewayStep.ComputeConfigurationTimeout(
            "openclaw config set gateway.mode local");

        Assert.True(timeout >= ConfigureGatewayStep.MinConfigurationTimeout);
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
        Assert.False(StartKeepaliveStep.IsKeepaliveCommandLine(
            @"C:\Windows\System32\wsl.exe -d OpenClawGateway-Dev -- sleep infinity",
            "OpenClawGateway"));
        Assert.True(StartKeepaliveStep.IsKeepaliveCommandLine(
            "wsl.exe --distribution \"OpenClawGateway-Dev\" -- sleep infinity",
            "OpenClawGateway-Dev"));
    }

    [Fact]
    public async Task AutoApprovePairing_ReturnsTerminalForDevicePairPluginNotFound()
    {
        var ctx = CreatePairingContext(DevicePairPluginNotFoundOutput);

        var result = await PairOperatorStep.AutoApprovePairing(ctx, "device-req-1", CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Equal(ApprovalRequestHelper.PluginNotFoundMessage, result.Message);
    }

    [Fact]
    public async Task AutoApprovePairing_KeepsOtherMissingPluginRetriable()
    {
        var ctx = CreatePairingContext(OtherPluginNotFoundOutput);

        var result = await PairOperatorStep.AutoApprovePairing(ctx, "device-req-1", CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("Device approval failed", result.Message);
        Assert.DoesNotContain(ApprovalRequestHelper.PluginNotFoundMessage, result.Message);
    }

    [Fact]
    public async Task AutoApproveNodePairing_ReturnsTerminalWhenPendingListReportsDevicePairPluginNotFound()
    {
        var ctx = CreatePairingContext(DevicePairPluginNotFoundOutput);

        var result = await PairNodeStep.AutoApproveNodePairing(ctx, requestId: null, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Equal(ApprovalRequestHelper.PluginNotFoundMessage, result.Message);
    }

    [Fact]
    public async Task AutoApproveNodePairing_KeepsOtherPendingListMissingPluginRetriable()
    {
        var ctx = CreatePairingContext(OtherPluginNotFoundOutput);

        var result = await PairNodeStep.AutoApproveNodePairing(ctx, requestId: null, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("Could not list pending node pairing requests", result.Message);
        Assert.DoesNotContain(ApprovalRequestHelper.PluginNotFoundMessage, result.Message);
    }

    [Fact]
    public async Task AutoApproveNodePairing_ReturnsTerminalWhenApproveReportsDevicePairPluginNotFound()
    {
        var ctx = CreatePairingContext(DevicePairPluginNotFoundOutput);

        var result = await PairNodeStep.AutoApproveNodePairing(ctx, "node-req-1", CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Equal(ApprovalRequestHelper.PluginNotFoundMessage, result.Message);
    }

    [Fact]
    public async Task AutoApproveNodePairing_KeepsOtherApproveMissingPluginRetriable()
    {
        var ctx = CreatePairingContext(OtherPluginNotFoundOutput);

        var result = await PairNodeStep.AutoApproveNodePairing(ctx, "node-req-1", CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("Node approval failed", result.Message);
        Assert.DoesNotContain(ApprovalRequestHelper.PluginNotFoundMessage, result.Message);
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
    public async Task ValidateWslLockdown_RetriesWslConfReadAfterStartupTimeout()
    {
        var catAttempts = 0;
        var ctx = CreateContext(commands: new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) =>
            {
                if (command == "cat /etc/wsl.conf")
                {
                    catAttempts++;
                    return catAttempts == 1
                        ? TimedOut()
                        : Ok("""
                            [boot]
                            systemd=true

                            [automount]
                            enabled=false
                            mountFsTab=false

                            [interop]
                            enabled=false
                            appendWindowsPath=false

                            [user]
                            default=openclaw
                            """);
                }

                if (command.Contains("LOCKDOWN_VALID", StringComparison.Ordinal))
                    return Ok("LOCKDOWN_VALID\n");

                return Fail("unexpected WSL command");
            }));
        ctx.DistroName = "test-distro";

        var result = await new ValidateWslLockdownStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, catAttempts);
    }

    // ─── PairOperatorStep: Windows-side gateway health check ───

    [Fact]
    public async Task PairOperatorStep_FailsWhenGatewayNotReachableFromWindows()
    {
        // Allocate a port and immediately release it so nothing is listening on it.
        var port = GetFreeTcpPort();

        var config = new SetupConfig { GatewayPort = port };
        var ctx = CreateContext(config);
        ctx.SharedGatewayToken = "test-shared-token";

        var step = new PairOperatorStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("not reachable", result.Message, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("mktemp \"$workspace/.AGENTS.md.openclaw.XXXXXX\"", script);
        Assert.Contains("chmod --reference=\"$agents\" \"$tmp\"", script);
        Assert.Contains("sub(/\\r$/, \"\", marker_line)", script);
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
        Assert.Contains("mktemp \"$workspace/.AGENTS.md.openclaw.XXXXXX\"", script);
        Assert.Contains("chmod --reference=\"$agents\" \"$tmp\"", script);
        Assert.Contains("sub(/\\r$/, \"\", marker_line)", script);
        Assert.Contains("WINDOWS_NODE_CONTEXT_ABSENT", script);
        Assert.Contains("WINDOWS_NODE_CONTEXT_REMOVED", script);
        Assert.Contains("AGENTS_SYMLINK_ROLLBACK_SKIPPED:$agents", script);
        Assert.Contains("exit 5", script);
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

    // Characterization (PR 3 WslShellQuoting migration): the workspace path is emitted as a
    // fully-wrapped POSIX token, so an embedded single quote must close-escape-reopen ('\'')
    // and remain single-quoted. Pins the generated script byte-for-byte.
    [Fact]
    public void WindowsNodeContext_BuildApplyScript_QuotesWorkspacePathWithEmbeddedSingleQuote()
    {
        var script = WindowsNodeBootstrapContextStep.BuildApplyScript("/home/o'brien/.openclaw/workspace");

        Assert.Contains("workspace='/home/o'\\''brien/.openclaw/workspace'", script);
    }

    [Fact]
    public void WindowsNodeContext_BuildRollbackScript_QuotesWorkspacePathWithEmbeddedSingleQuote()
    {
        var script = WindowsNodeBootstrapContextStep.BuildRollbackScript("/home/o'brien/.openclaw/workspace");

        Assert.Contains("workspace='/home/o'\\''brien/.openclaw/workspace'", script);
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

    [Theory]
    [InlineData("[{\"id\":\"main\",\"workspace\":\"/home/openclaw/main\",\"isDefault\":true}]", "/home/openclaw/main")]
    [InlineData("Warning\n[\n  {\"id\":\"other\",\"workspace\":\"/home/openclaw/other\",\"isDefault\":false},\n  {\"id\":\"primary\",\"workspace\":\"/home/openclaw/primary\",\"isDefault\":true}\n]\n", "/home/openclaw/primary")]
    [InlineData("[{\"id\":\"main\",\"workspace\":\"~/main\"}]", "~/main")]
    [InlineData("not json", null)]
    public void WindowsNodeContext_ExtractDefaultAgentWorkspace_ParsesCanonicalAgentsList(string stdout, string? expected)
    {
        Assert.Equal(expected, WindowsNodeBootstrapContextStep.ExtractDefaultAgentWorkspaceFromAgentsOutput(stdout));
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
                if (command.Contains("openclaw agents list --json"))
                    return Ok(AgentsListJson("/home/openclaw/.openclaw/workspace"));
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
        Assert.Equal(5, commands.WslCalls.Count);
        Assert.All(commands.WslCalls, c =>
        {
            Assert.Equal("OpenClawGateway", c.DistroName);
            Assert.Equal("openclaw", c.User);
        });
        Assert.Contains("getent passwd", commands.WslCalls[0].Command);
        Assert.Contains("openclaw agents list --json", commands.WslCalls[1].Command);
        Assert.Contains("openclaw config get agents.defaults.workspace", commands.WslCalls[2].Command);
        Assert.Contains("openclaw setup --help", commands.WslCalls[3].Command);
        Assert.Contains("openclaw setup --baseline --workspace '/home/openclaw/.openclaw/workspace'", commands.WslCalls[3].Command);
        Assert.Contains("openclaw setup --workspace '/home/openclaw/.openclaw/workspace'", commands.WslCalls[3].Command);
        Assert.Contains("workspace='/home/openclaw/.openclaw/workspace'", commands.WslCalls[4].Command);
        // getent uses $(id -un) command-substitution and no $vars, so argv path is safe.
        Assert.False(commands.WslCalls[0].InputViaStdin);
        // agents list + config get + openclaw setup scripts reference $PATH via WslPathPrefix,
        // which wsl.exe would rewrite on the argv path — see docs/WSL_EXE_ARGV_PITFALL.md.
        Assert.True(commands.WslCalls[1].InputViaStdin);
        Assert.True(commands.WslCalls[2].InputViaStdin);
        Assert.True(commands.WslCalls[3].InputViaStdin);
        // Apply script uses $workspace etc., must use stdin.
        Assert.True(commands.WslCalls[4].InputViaStdin);
        var state = await WindowsNodeBootstrapContextStep.ReadInstallStateAsync(ctx, CancellationToken.None);
        Assert.Contains(state.Targets, target =>
            target.DistroName == "OpenClawGateway" &&
            target.User == "openclaw" &&
            target.WorkspacePath == "/home/openclaw/.openclaw/workspace");
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_ResolvesRelativeConfiguredWorkspaceFromGatewayUserHome()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw agents list --json"))
                    return Ok(AgentsListJson("/home/openclaw/relative/workspace"));
                if (command.Contains("openclaw setup"))
                    return Ok("");
                if (command.Contains("openclaw config get agents.defaults.workspace"))
                    return Ok("\"relative/workspace\"\n");
                if (command.Contains("workspace='/home/openclaw/relative/workspace'"))
                    return Ok("WINDOWS_NODE_CONTEXT_READY\n");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);

        var result = await new WindowsNodeBootstrapContextStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Contains(commands.WslCalls,
            c => c.Command.Contains("openclaw setup --workspace '/home/openclaw/relative/workspace'"));
        Assert.Contains(commands.WslCalls,
            c => c.Command.Contains("workspace='/home/openclaw/relative/workspace'"));
        Assert.DoesNotContain(commands.WslCalls, c => c.Command == "pwd -P");
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_UsesDefaultOnlyWhenWorkspaceKeyIsAbsent()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw agents list --json"))
                    return Ok(AgentsListJson("/home/openclaw/.openclaw/workspace"));
                if (command.Contains("openclaw config get agents.defaults.workspace"))
                    return new CommandResult(
                        1,
                        "",
                        "Config path not found: agents.defaults.workspace. Run openclaw config validate to inspect config shape.",
                        TimeSpan.Zero,
                        TimedOut: false);
                if (command.Contains("openclaw setup --workspace '/home/openclaw/.openclaw/workspace'"))
                    return Ok();
                if (command.Contains("workspace='/home/openclaw/.openclaw/workspace'"))
                    return Ok("WINDOWS_NODE_CONTEXT_READY\n");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);

        var result = await new WindowsNodeBootstrapContextStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Contains(commands.WslCalls,
            c => c.Command.Contains("openclaw setup --workspace '/home/openclaw/.openclaw/workspace'"));
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_DoesNotPersistDefaultWhenWorkspaceLookupFails()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw agents list --json"))
                    return Ok(AgentsListJson("/home/openclaw/.openclaw/workspace"));
                if (command.Contains("openclaw config get agents.defaults.workspace"))
                    return Fail("gateway config is temporarily unavailable");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);

        var result = await new WindowsNodeBootstrapContextStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("Could not resolve OpenClaw default workspace path", result.Message);
        Assert.DoesNotContain(commands.WslCalls, c => c.Command.Contains("openclaw setup"));
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_DoesNotPersistDefaultForMalformedWorkspaceOutput()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw agents list --json"))
                    return Ok(AgentsListJson("/home/openclaw/.openclaw/workspace"));
                if (command.Contains("openclaw config get agents.defaults.workspace"))
                    return Ok("Config warning without a value\n");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);

        var result = await new WindowsNodeBootstrapContextStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.DoesNotContain(commands.WslCalls, c => c.Command.Contains("openclaw setup"));
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_UsesEffectiveDefaultAgentWorkspaceWithoutRewritingDefaults()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw agents list --json"))
                    return Ok(AgentsListJson("/home/openclaw/main-agent"));
                if (command.Contains("openclaw config get agents.defaults.workspace"))
                    return Ok("\"~/.openclaw/workspace\"\n");
                if (command.Contains("workspace='/home/openclaw/main-agent'"))
                    return Ok("WINDOWS_NODE_CONTEXT_READY\n");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);

        var result = await new WindowsNodeBootstrapContextStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.DoesNotContain(commands.WslCalls, c => c.Command.Contains("openclaw setup"));
        Assert.Contains(commands.WslCalls, c => c.Command.Contains("workspace='/home/openclaw/main-agent'"));
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_FailsWhenEffectiveAgentWorkspaceLookupFails()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw agents list --json"))
                    return Fail("agents unavailable");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);
        var priorTarget = new WindowsNodeContextTarget("prior-distro", "openclaw", "/prior/workspace");
        await WindowsNodeBootstrapContextStep.RecordAppliedTargetAsync(
            ctx,
            priorTarget,
            CancellationToken.None);

        var step = new WindowsNodeBootstrapContextStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);
        var callsAfterExecute = commands.WslCalls.Count;
        await step.RollbackAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("Could not resolve OpenClaw agent workspace path", result.Message);
        Assert.DoesNotContain(commands.WslCalls, c => c.Command.Contains("openclaw config get"));
        Assert.DoesNotContain(commands.WslCalls, c => c.Command.Contains("openclaw setup"));
        Assert.Equal(callsAfterExecute, commands.WslCalls.Count);
        var state = await WindowsNodeBootstrapContextStep.ReadInstallStateAsync(ctx, CancellationToken.None);
        Assert.Equal([priorTarget], state.Targets);
    }

    [Fact]
    public async Task WindowsNodeContext_Execute_RemovesNewStateWhenSymlinkCheckMakesNoChange()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw agents list --json"))
                    return Ok(AgentsListJson("/home/openclaw/.openclaw/workspace"));
                if (command.Contains("openclaw config get agents.defaults.workspace"))
                    return Ok("\"~/.openclaw/workspace\"\n");
                if (command.Contains("openclaw setup"))
                    return Ok();
                if (command.Contains("AGENTS_SYMLINK:$agents"))
                    return new CommandResult(2, "", "AGENTS_SYMLINK", TimeSpan.Zero, TimedOut: false);
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);

        var step = new WindowsNodeBootstrapContextStep();
        var result = await step.ExecuteAsync(ctx, CancellationToken.None);
        var callsAfterExecute = commands.WslCalls.Count;
        await step.RollbackAsync(ctx, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(WindowsNodeBootstrapContextStep.InstallStatePath(ctx)));
        Assert.Equal(callsAfterExecute, commands.WslCalls.Count);
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
                if (command.Contains("openclaw agents list --json"))
                    return Ok(AgentsListJson("/home/openclaw/.openclaw/workspace"));
                if (command.Contains("WINDOWS_NODE_CONTEXT_REMOVED"))
                    return Ok("WINDOWS_NODE_CONTEXT_REMOVED\n");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);
        await WindowsNodeBootstrapContextStep.RecordAppliedTargetAsync(
            ctx,
            new WindowsNodeContextTarget("recorded-distro", "recorded-user", "/recorded/workspace"),
            CancellationToken.None);

        await new WindowsNodeBootstrapContextStep().RollbackAsync(ctx, CancellationToken.None);

        Assert.NotEmpty(commands.WslCalls);
        // Last call is the rollback script and must use stdin.
        Assert.Contains("WINDOWS_NODE_CONTEXT_REMOVED", commands.WslCalls[^1].Command);
        Assert.True(commands.WslCalls[^1].InputViaStdin);
        var rollback = Assert.Single(commands.WslCalls);
        Assert.Equal("recorded-distro", rollback.DistroName);
        Assert.Equal("recorded-user", rollback.User);
        Assert.Contains("workspace='/recorded/workspace'", rollback.Command);
        Assert.False(File.Exists(WindowsNodeBootstrapContextStep.InstallStatePath(ctx)));
    }

    [Fact]
    public async Task WindowsNodeContext_Rollback_PropagatesCleanupFailureAndKeepsStateForRetry()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) => command.Contains("WINDOWS_NODE_CONTEXT_REMOVED")
                ? Fail("cannot update AGENTS.md")
                : Fail($"unexpected wsl command: {command}"));
        var ctx = CreateContext(commands: commands);
        await WindowsNodeBootstrapContextStep.RecordAppliedTargetAsync(
            ctx,
            new WindowsNodeContextTarget("recorded-distro", "recorded-user", "/recorded/workspace"),
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WindowsNodeBootstrapContextStep().RollbackAsync(ctx, CancellationToken.None));

        Assert.Contains("cannot update AGENTS.md", error.Message);
        Assert.True(File.Exists(WindowsNodeBootstrapContextStep.InstallStatePath(ctx)));
    }

    [Fact]
    public async Task WindowsNodeContext_Rollback_RemovesExistingTargetStateAfterCleanup()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw agents list --json"))
                    return Ok(AgentsListJson("/home/openclaw/.openclaw/workspace"));
                if (command.Contains("openclaw config get agents.defaults.workspace"))
                    return Ok("\"~/.openclaw/workspace\"\n");
                if (command.Contains("openclaw setup"))
                    return Ok();
                if (command.Contains("WINDOWS_NODE_CONTEXT_READY"))
                    return Ok("WINDOWS_NODE_CONTEXT_READY\n");
                if (command.Contains("WINDOWS_NODE_CONTEXT_REMOVED"))
                    return Ok("WINDOWS_NODE_CONTEXT_REMOVED\n");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);
        var target = new WindowsNodeContextTarget(
            "OpenClawGateway",
            "openclaw",
            "/home/openclaw/.openclaw/workspace");
        await WindowsNodeBootstrapContextStep.RecordAppliedTargetAsync(ctx, target, CancellationToken.None);
        var step = new WindowsNodeBootstrapContextStep();
        Assert.True((await step.ExecuteAsync(ctx, CancellationToken.None)).IsSuccess);

        await step.RollbackAsync(ctx, CancellationToken.None);

        Assert.False(File.Exists(WindowsNodeBootstrapContextStep.InstallStatePath(ctx)));
    }

    [Fact]
    public async Task WindowsNodeContext_Rollback_TreatsMissingRecordedDistroAsCleaned()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) => command.Contains("WINDOWS_NODE_CONTEXT_REMOVED")
                ? new CommandResult(1, "", "WSL_E_DISTRO_NOT_FOUND", TimeSpan.Zero, TimedOut: false)
                : Fail($"unexpected wsl command: {command}"));
        var ctx = CreateContext(commands: commands);
        await WindowsNodeBootstrapContextStep.RecordAppliedTargetAsync(
            ctx,
            new WindowsNodeContextTarget("missing-distro", "openclaw", "/recorded/workspace"),
            CancellationToken.None);

        await new WindowsNodeBootstrapContextStep().RollbackAsync(ctx, CancellationToken.None);

        Assert.False(File.Exists(WindowsNodeBootstrapContextStep.InstallStatePath(ctx)));
    }

    [Fact]
    public async Task WindowsNodeContext_Rollback_CleansLegacyEffectiveWorkspaceWithoutStateFile()
    {
        var commands = new FakeCommandRunner(
            _ => Fail("unexpected RunAsync"),
            (_, command, _) =>
            {
                if (command.Contains("getent passwd"))
                    return Ok("/home/openclaw\n");
                if (command.Contains("openclaw agents list --json"))
                    return Ok(AgentsListJson("/home/openclaw/legacy-main"));
                if (command.Contains("WINDOWS_NODE_CONTEXT_REMOVED"))
                    return Ok("WINDOWS_NODE_CONTEXT_REMOVED\n");
                return Fail($"unexpected wsl command: {command}");
            });
        var ctx = CreateContext(commands: commands);

        await new WindowsNodeBootstrapContextStep().RollbackAsync(ctx, CancellationToken.None);

        Assert.Contains(commands.WslCalls, call => call.Command.Contains("getent passwd"));
        Assert.Contains(commands.WslCalls, call => call.Command.Contains("openclaw agents list --json"));
        Assert.Contains(commands.WslCalls, call => call.Command.Contains("workspace='/home/openclaw/legacy-main'"));
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
                if (command.Contains("openclaw agents list --json"))
                    return Ok(AgentsListJson("/home/openclaw/.openclaw/workspace"));
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

    private static CommandResult FailWithStdout(string stdout)
        => new(1, stdout, "", TimeSpan.Zero, TimedOut: false);

    private static CommandResult TimedOut()
        => new(-1, "", "", TimeSpan.FromSeconds(30), TimedOut: true);

    private static string AgentsListJson(string workspace, string id = "main", bool isDefault = true)
        => JsonSerializer.Serialize(new[] { new { id, workspace, isDefault } });

    private static string NulSeparated(string value)
        => string.Join("\0", value.ToCharArray()) + "\0";

    private SetupContext CreatePairingContext(string failureStdout)
    {
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, _, _) => FailWithStdout(failureStdout));
        var ctx = CreateContext(commands: commands);
        ctx.DistroName = "test-distro";
        ctx.SharedGatewayToken = "shared-token";
        return ctx;
    }

    // Shared scenario for the node-pairing cancellation tests: a reachable gateway HTTP endpoint (so
    // WindowsGatewayReachability.VerifyAsync passes), a silent WebSocket the node client parks against,
    // a fake WSL runner whose approval drain is empty, and a seeded gateway registry record — enough to
    // drive the REAL PairNodeStep to its node-connection wait. `ctxToken` becomes the SetupContext's
    // CancellationToken (used by the pipeline); pass CancellationToken.None when cancelling the step call
    // directly. The caller disposes the returned `ws`/`http`.
    private (SetupContext ctx, SilentWebSocketServer ws, HttpListener http, SetupLogger logger)
        BuildNodePairingScenario(CancellationToken ctxToken)
    {
        var httpPort = GetFreeTcpPort();
        var http = new HttpListener();
        http.Prefixes.Add($"http://localhost:{httpPort}/");
        http.Start();
        _ = Task.Run(async () =>
        {
            while (http.IsListening)
            {
                HttpListenerContext c;
                try { c = await http.GetContextAsync(); }
                catch { return; }
                c.Response.StatusCode = 200;
                c.Response.Close();
            }
        });

        var ws = new SilentWebSocketServer();
        var commands = new FakeCommandRunner(_ => Ok(), (_, _, _) => Ok(stdout: "No pending device approvals"));
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        var ctx = new SetupContext(
            new SetupConfig { GatewayPort = httpPort }, logger,
            new TransactionJournal(filePath: null), commands, ctxToken);
        ctx.DistroName = "test-distro";
        ctx.SharedGatewayToken = "test-token-placeholder";
        ctx.GatewayUrl = $"ws://127.0.0.1:{ws.Port}";
        ctx.GatewayRecordId = "test-gw";

        var registry = new GatewayRegistry(_tempDir);
        registry.Load();
        registry.AddOrUpdate(new GatewayRecord
        {
            Id = "test-gw",
            Url = ctx.GatewayUrl,
            IsLocal = true,
            SetupManagedDistroName = ctx.DistroName,
            SshTunnel = null,
        });
        registry.Save();
        return (ctx, ws, http, logger);
    }

    // The step itself must rethrow a caller cancel rather than swallow it into StepResult.Fail.
    [Fact]
    public async Task PairNodeStep_CallerCancellation_PropagatesInsteadOfFailing()
    {
        var (ctx, ws, http, _) = BuildNodePairingScenario(CancellationToken.None);
        using (ws)
        using (http)
        {
            using var callerCts = new CancellationTokenSource();
            var task = new PairNodeStep().ExecuteAsync(ctx, callerCts.Token);
            await ws.UpgradeCompleted;   // deterministic barrier: the client is in its node-connection wait
            callerCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
    }

    // Durable end-to-end contract (the user-visible behavior the PR promises): driving the REAL
    // SetupPipeline with the real PairNodeStep and a caller cancel, the pipeline reports Cancelled/exit 3,
    // emits NO "connection failed"/retry warning, and journals the abort as a cancellation — not a
    // failed-step narrative. On base the step's retry masks the *outcome* to Cancelled too, so the
    // log/journal assertions are what actually pin this fix.
    [Fact]
    public async Task SetupPipeline_CallerCancel_CancelsCleanlyWithoutFailureNarrative()
    {
        using var callerCts = new CancellationTokenSource();
        var (ctx, ws, http, logger) = BuildNodePairingScenario(callerCts.Token);
        using (ws)
        using (http)
        {
            var logs = new List<LogEntry>();
            logger.LogEmitted += (_, e) => { lock (logs) { logs.Add(e); } };

            var pipeline = new SetupPipeline(new SetupStep[] { new PairNodeStep() }, rollbackOnFailureOverride: false);
            var run = pipeline.RunAsync(ctx);
            await ws.UpgradeCompleted;
            callerCts.Cancel();
            var result = await run;

            _output.WriteLine($"SetupPipeline on caller-cancel → Outcome={result.Outcome}, ExitCode={result.ExitCode}");

            // 1. user-observable outcome
            Assert.Equal(PipelineOutcome.Cancelled, result.Outcome);
            Assert.Equal(3, result.ExitCode);

            // 2. no misleading connection-failure / retry warning for a user abort
            List<LogEntry> snapshot;
            lock (logs) { snapshot = logs.ToList(); }
            Assert.DoesNotContain(snapshot, e => e.Message.Contains("Node connection failed", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(snapshot, e => e.Level == LogLevel.Warn && e.Message.Contains("retrying", StringComparison.OrdinalIgnoreCase));

            // 3. journal records the cancellation, not a failed-step narrative
            Assert.Contains(ctx.Journal.Entries, en => en.Event == "pipeline_cancelled");
            Assert.DoesNotContain(ctx.Journal.Entries, en => en.Event == "pipeline_failed");
        }
    }

    [Fact]
    public async Task PreflightWindowsTailscale_RequiresRunningMagicDnsClientBeforeCleanup()
    {
        var config = new SetupConfig { Tailscale = new TailscaleConfig { Enabled = true } };
        var commands = new FakeCommandRunner(_ => Ok("""{"BackendState":"Running","Self":{"DNSName":"windows.tailnet.ts.net"}}"""));
        var ctx = CreateContext(config, commands);

        var result = await new PreflightWindowsTailscaleStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("tailnet.ts.net", ctx.WindowsTailnetDnsSuffix);
        Assert.Equal("tailnet.ts.net", config.Tailscale.TailnetDnsSuffix);
        Assert.Contains(commands.Calls, call => call.Arguments.SequenceEqual(["status", "--json"]));
    }

    [Fact]
    public async Task AuthorizeTailscale_AuthKeyUsesTransientEnvironmentAndDerivesMagicDnsName()
    {
        var config = new SetupConfig
        {
            Tailscale = new TailscaleConfig
            {
                Enabled = true,
                AuthMode = TailscaleAuthMode.AuthKey,
                AuthKey = "tskey-auth-only-in-memory",
                AuthTimeoutSeconds = 30,
            }
        };
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) => command.Contains("tailscale status --json")
                ? Ok("""{"BackendState":"Running","Self":{"DNSName":"openclaw.tailnet.ts.net"}}""")
                : Ok());
        var ctx = CreateContext(config, commands);
        ctx.WindowsTailnetDnsSuffix = "tailnet.ts.net";

        var result = await new AuthorizeTailscaleStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("openclaw.tailnet.ts.net", ctx.TailscaleDnsName);
        Assert.Null(config.Tailscale.AuthKey);
        Assert.Contains(commands.WslCalls, call => call.Command.Contains("--auth-key=\"$TS_AUTHKEY\""));
        Assert.Contains(commands.WslEnvironments, environment => environment?["TS_AUTHKEY"] == "tskey-auth-only-in-memory");
    }

    [Fact]
    public async Task AuthorizeTailscale_BrowserPresentsUrlWithoutWritingItToSetupState()
    {
        var config = new SetupConfig
        {
            Tailscale = new TailscaleConfig { Enabled = true, AuthTimeoutSeconds = 30 }
        };
        var commands = new FakeCommandRunner(
            _ => Ok(),
            (_, command, _) => command.Contains("tailscale up")
                ? FailWithStdout("https://login.tailscale.com/a/browser-only-token")
                : command.Contains("tailscale status --json")
                    ? Ok("""{"BackendState":"Running","Self":{"DNSName":"openclaw.tailnet.ts.net"}}""")
                    : Ok());
        var presenter = new RecordingAuthorizationPresenter();
        var ctx = CreateContext(config, commands);
        ctx.WindowsTailnetDnsSuffix = "tailnet.ts.net";
        ctx.ExternalAuthorizationPresenter = presenter;

        var result = await new AuthorizeTailscaleStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Tailscale", presenter.Request!.Provider);
        Assert.Equal("https://login.tailscale.com/a/browser-only-token", presenter.Request.AuthorizationUri.AbsoluteUri);
    }

    private sealed class FakeCommandRunner(
        Func<string[], CommandResult> run,
        Func<string, string, TimeSpan, CommandResult>? runInWsl = null) : ICommandRunner
    {
        public List<(string Executable, string[] Arguments)> Calls { get; } = [];
        public List<(string Executable, string[] Arguments, string? StdinInput)> DetailedCalls { get; } = [];
        public List<(string DistroName, string Command, TimeSpan Timeout, string? User, bool InputViaStdin)> WslCalls { get; } = [];
        public List<IReadOnlyDictionary<string, string>?> WslEnvironments { get; } = [];

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
            DetailedCalls.Add((executable, arguments, stdinInput));
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
            WslEnvironments.Add(environment);
            if (runInWsl == null)
                throw new NotSupportedException("RunInWslAsync is not expected in these tests.");

            return Task.FromResult(runInWsl(distroName, command, timeout));
        }
    }

    private sealed class RecordingAuthorizationPresenter : IExternalAuthorizationPresenter
    {
        public ExternalAuthorizationRequest? Request { get; private set; }

        public Task PresentAsync(ExternalAuthorizationRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.CompletedTask;
        }
    }
}
