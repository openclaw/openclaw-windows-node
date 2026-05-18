using System.Text;
using OpenClawTray.Services.LocalGatewaySetup;
using OpenClaw.Shared;

namespace OpenClaw.Tray.Tests;

public class LocalGatewaySetupTests
{
    [Fact]
    public async Task DrainAsync_ReturnsCompletedReadImmediately()
    {
        var task = Task.FromResult("hello");
        var result = await WslExeCommandRunner.DrainAsync(task, TimeSpan.FromSeconds(1), new NullLogger(), isStderr: false);
        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task DrainAsync_ReturnsEmpty_WhenReadHangsBeyondTimeout()
    {
        // Regression: PR #274 smoke test — `wsl.exe --list --verbose` returned but stdout
        // ReadToEndAsync hung indefinitely because the gateway distro / wslhost descendants
        // inherited and held the redirected stdout pipe handle. The wizard's "checking
        // system" step (HasDistroAsync → ListDistrosAsync) blocked forever. DrainAsync now
        // bounds the post-exit drain so the wizard surfaces partial output instead of
        // hanging the entire app.
        var neverCompletes = new TaskCompletionSource<string>().Task;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await WslExeCommandRunner.DrainAsync(neverCompletes, TimeSpan.FromMilliseconds(150), new NullLogger(), isStderr: false);
        sw.Stop();
        Assert.Equal(string.Empty, result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"DrainAsync should return promptly after timeout; took {sw.Elapsed}");
    }

    [Fact]
    public void ParseDistroList_ParsesVerboseWslOutput()
    {
        const string output = """
          NAME                   STATE           VERSION
        * Ubuntu                 Running         2
          OpenClawGateway        Stopped         2
          Legacy                 Stopped         1
        """;

        var distros = WslExeCommandRunner.ParseDistroList(output);

        Assert.Equal(3, distros.Count);
        Assert.Contains(distros, d => d.Name == "OpenClawGateway" && d.State == "Stopped" && d.Version == 2);
        Assert.Contains(distros, d => d.Name == "Legacy" && d.Version == 1);
    }

    [Fact]
    public void ParseStatus_ReadsDefaultAndWslVersions()
    {
        const string output = """
        Default Version: 1
        WSL version: 2.1.5.0
        Kernel version: 5.15.146.1-2
        """;

        var status = WslExeCommandRunner.ParseStatus(output);

        Assert.Equal(1, status.DefaultVersion);
        Assert.Equal("2.1.5.0", status.WslVersion);
        Assert.Equal("5.15.146.1-2", status.KernelVersion);
    }

    [Fact]
    public void RuntimeConfiguration_ReadsOnlyCleanWslEnvironment()
    {
        var environment = new FakeSetupEnvironment(new Dictionary<string, string?>
        {
            [LocalGatewaySetupRuntimeConfiguration.DistroNameVariable] = "OpenClawGatewayE2E",
            [LocalGatewaySetupRuntimeConfiguration.AllowExistingDistroVariable] = "1"
        });

        var config = LocalGatewaySetupRuntimeConfiguration.FromEnvironment(environment);

        Assert.Equal("OpenClawGatewayE2E", config.DistroName);
        Assert.True(config.AllowExistingDistro);
    }

    [Fact]
    public async Task Preflight_BlocksExistingOpenClawDistro()
    {
        var runner = new FakeWslCommandRunner { Distros = [new WslDistroInfo("OpenClawGateway", "Stopped", 2)] };
        var preflight = new LocalGatewayPreflightProbe(runner, new FixedPortProbe(available: true));

        var result = await preflight.RunAsync(new LocalGatewaySetupOptions());

        Assert.False(result.CanContinue);
        Assert.Contains(result.Issues, issue => issue.Code == "distro_exists" && issue.Severity == LocalGatewaySetupSeverity.Blocking);
    }

    [Fact]
    public async Task Preflight_WslStatusFailure_IncludesWslLogsHelp()
    {
        var runner = new FakeWslCommandRunner { WslStatusExitCode = 1 };
        var preflight = new LocalGatewayPreflightProbe(runner, new FixedPortProbe(available: true));

        var result = await preflight.RunAsync(new LocalGatewaySetupOptions());

        Assert.False(result.CanContinue);
        Assert.Contains(result.Issues, issue => issue.Code == "wsl_unavailable" && issue.Message.Contains("aka.ms/wsllogs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preflight_AllowsExistingGatewayOwnedLoopbackPort_WhenExistingDistroAllowed()
    {
        var runner = new FakeWslCommandRunner
        {
            Distros = [new WslDistroInfo("OpenClawGateway", "Running", 2)],
            CommandOutputByContains = { ["gateway status"] = "{\"ok\":true}" }
        };
        var preflight = new LocalGatewayPreflightProbe(runner, new FixedPortProbe(available: false));

        var result = await preflight.RunAsync(new LocalGatewaySetupOptions { AllowExistingDistro = true });

        Assert.True(result.CanContinue);
        Assert.Contains(result.Issues, issue => issue.Code == "gateway_port_already_active" && issue.Severity == LocalGatewaySetupSeverity.Warning);
        Assert.Contains(runner.Commands, command => command.Count == 8 && command[7].Contains("--url 'ws://localhost:18789'", StringComparison.Ordinal));
    }

    [Fact]
    public void ShouldAllowExistingDistroForRun_AllowsFreshAppStateToReuseOrphanedOpenClawDistro()
    {
        var state = LocalGatewaySetupState.Create(new LocalGatewaySetupOptions());

        var allow = LocalGatewaySetupEngine.ShouldAllowExistingDistroForRun(
            state,
            distroExists: true,
            configuredAllowExistingDistro: false);

        Assert.True(allow);
    }

    [Fact]
    public void ShouldAllowExistingDistroForRun_AllowsRetryAfterDistroExistsPreflightFailure()
    {
        var state = LocalGatewaySetupState.Create(new LocalGatewaySetupOptions());
        state.Issues.Add(new LocalGatewaySetupIssue(
            "distro_exists",
            "A WSL distro named OpenClawGateway already exists.",
            LocalGatewaySetupSeverity.Blocking));
        state.Block("preflight_blocked", "This PC is not ready for local WSL gateway setup.");

        var allow = LocalGatewaySetupEngine.ShouldAllowExistingDistroForRun(
            state,
            distroExists: true,
            configuredAllowExistingDistro: false);

        Assert.True(allow);
    }

    [Fact]
    public void ShouldAllowExistingDistroForRun_DoesNotAllowUnrelatedFailedState()
    {
        var state = LocalGatewaySetupState.Create(new LocalGatewaySetupOptions());
        state.Block("port_in_use", "Local gateway port is already in use.");

        var allow = LocalGatewaySetupEngine.ShouldAllowExistingDistroForRun(
            state,
            distroExists: true,
            configuredAllowExistingDistro: false);

        Assert.False(allow);
    }

    [Fact]
    public async Task WslStoreInstanceInstaller_UsesCraigApprovedInstallCommand_AndTrustsExitCode()
    {
        using var temp = new TempDirectory();
        var installLocation = System.IO.Path.Combine(temp.Path, "OpenClawGateway");
        var wsl = new FakeWslCommandRunner();
        var installer = new WslStoreInstanceInstaller(wsl, createDirectory: _ => { });

        var result = await installer.EnsureInstalledAsync(new LocalGatewaySetupOptions { InstanceInstallLocation = installLocation });

        Assert.True(result.Success);
        Assert.Contains(wsl.Commands, command => command.SequenceEqual([
            "--install",
            "Ubuntu-24.04",
            "--name",
            "OpenClawGateway",
            "--location",
            installLocation,
            "--no-launch",
            "--version",
            "2"]));
        Assert.DoesNotContain(wsl.Commands, command => command.Contains("--web-download"));
        Assert.DoesNotContain(wsl.Commands, command => command.Contains("--from-file"));
    }

    [Fact]
    public async Task WslStoreInstanceInstaller_UnregistersBrokenExistingDistroAndReinstalls_WhenExistingDistroAllowed()
    {
        using var temp = new TempDirectory();
        var installLocation = System.IO.Path.Combine(temp.Path, "OpenClawGateway");
        var wsl = new FakeWslCommandRunner
        {
            Distros = [new WslDistroInfo("OpenClawGateway", "Stopped", 2)]
        };
        wsl.CommandResultByContains["-d OpenClawGateway -u root -- true"] = new WslCommandResult(
            -1,
            "",
            "Failed to attach disk 'C:\\Users\\test\\AppData\\Local\\OpenClawTray\\wsl\\OpenClawGateway\\ext4.vhdx' to WSL2: The system cannot find the path specified. Error code: Wsl/Service/CreateInstance/MountDisk/HCS/ERROR_PATH_NOT_FOUND");
        var installer = new WslStoreInstanceInstaller(wsl, createDirectory: _ => { });

        var result = await installer.EnsureInstalledAsync(new LocalGatewaySetupOptions
        {
            AllowExistingDistro = true,
            InstanceInstallLocation = installLocation
        });

        Assert.True(result.Success);
        Assert.Contains("OpenClawGateway", wsl.UnregisteredDistros);
        Assert.Contains(wsl.Commands, command => command.SequenceEqual([
            "--install",
            "Ubuntu-24.04",
            "--name",
            "OpenClawGateway",
            "--location",
            installLocation,
            "--no-launch",
            "--version",
            "2"]));
    }

    [Fact]
    public async Task WslStoreInstanceInstaller_UnregistersBrokenExistingDistro_WhenWslOutputsNulSeparatedText()
    {
        using var temp = new TempDirectory();
        var installLocation = System.IO.Path.Combine(temp.Path, "OpenClawGateway");
        var wsl = new FakeWslCommandRunner
        {
            Distros = [new WslDistroInfo("OpenClawGateway", "Stopped", 2)]
        };
        var missingDiskMessage = "Failed to attach disk 'C:\\Users\\test\\AppData\\Local\\OpenClawTray\\wsl\\OpenClawGateway\\ext4.vhdx' to WSL2: The system cannot find the path specified. Error code: Wsl/Service/CreateInstance/MountDisk/HCS/ERROR_PATH_NOT_FOUND";
        wsl.CommandResultByContains["-d OpenClawGateway -u root -- true"] = new WslCommandResult(
            -1,
            string.Join("\0", missingDiskMessage.ToCharArray()),
            "");
        var installer = new WslStoreInstanceInstaller(wsl, createDirectory: _ => { });

        var result = await installer.EnsureInstalledAsync(new LocalGatewaySetupOptions
        {
            AllowExistingDistro = true,
            InstanceInstallLocation = installLocation
        });

        Assert.True(result.Success);
        Assert.Contains("OpenClawGateway", wsl.UnregisteredDistros);
        Assert.Contains(wsl.Commands, command => command.Count > 0 && command[0] == "--install");
    }

    [Fact]
    public async Task WslStoreInstanceInstaller_DoesNotUnregisterExistingDistro_WhenMountDiskFailureIsNotMissingPath()
    {
        using var temp = new TempDirectory();
        var wsl = new FakeWslCommandRunner
        {
            Distros = [new WslDistroInfo("OpenClawGateway", "Stopped", 2)]
        };
        wsl.CommandResultByContains["-d OpenClawGateway -u root -- true"] = new WslCommandResult(
            -1,
            "",
            "Error code: Wsl/Service/CreateInstance/MountDisk/HCS/E_ACCESSDENIED");
        var installer = new WslStoreInstanceInstaller(wsl, createDirectory: _ => { });

        var result = await installer.EnsureInstalledAsync(new LocalGatewaySetupOptions
        {
            AllowExistingDistro = true,
            InstanceInstallLocation = temp.Path
        });

        Assert.False(result.Success);
        Assert.Equal("wsl_existing_distro_unavailable", result.ErrorCode);
        Assert.Empty(wsl.UnregisteredDistros);
        Assert.DoesNotContain(wsl.Commands, command => command.Count > 0 && command[0] == "--install");
    }

    [Fact]
    public async Task WslStoreInstanceInstaller_InstallsFresh_WhenFailedUnregisterAlreadyRemovedDistro()
    {
        using var temp = new TempDirectory();
        var installLocation = System.IO.Path.Combine(temp.Path, "OpenClawGateway");
        var wsl = new FakeWslCommandRunner
        {
            Distros = [new WslDistroInfo("OpenClawGateway", "Stopped", 2)],
            UnregisterResult = new WslCommandResult(1, "", "The specified distribution was not found."),
            RemoveDistroOnUnregister = true
        };
        wsl.CommandResultByContains["-d OpenClawGateway -u root -- true"] = new WslCommandResult(
            -1,
            "",
            "Failed to attach disk 'C:\\Users\\test\\AppData\\Local\\OpenClawTray\\wsl\\OpenClawGateway\\ext4.vhdx' to WSL2: The system cannot find the path specified. Error code: Wsl/Service/CreateInstance/MountDisk/HCS/ERROR_PATH_NOT_FOUND");
        var installer = new WslStoreInstanceInstaller(wsl, createDirectory: _ => { });

        var result = await installer.EnsureInstalledAsync(new LocalGatewaySetupOptions
        {
            AllowExistingDistro = true,
            InstanceInstallLocation = installLocation
        });

        Assert.True(result.Success);
        Assert.Contains("OpenClawGateway", wsl.UnregisteredDistros);
        Assert.Contains(wsl.Commands, command => command.Count > 0 && command[0] == "--install");
    }

    [Fact]
    public async Task WslStoreInstanceInstaller_FailsExistingDistroProbe_WhenFailureIsNotMissingDisk()
    {
        using var temp = new TempDirectory();
        var wsl = new FakeWslCommandRunner
        {
            Distros = [new WslDistroInfo("OpenClawGateway", "Stopped", 2)]
        };
        wsl.CommandResultByContains["-d OpenClawGateway -u root -- true"] = new WslCommandResult(
            1,
            "",
            "WSL is unavailable");
        var installer = new WslStoreInstanceInstaller(wsl, createDirectory: _ => { });

        var result = await installer.EnsureInstalledAsync(new LocalGatewaySetupOptions
        {
            AllowExistingDistro = true,
            InstanceInstallLocation = temp.Path
        });

        Assert.False(result.Success);
        Assert.Equal("wsl_existing_distro_unavailable", result.ErrorCode);
        Assert.Empty(wsl.UnregisteredDistros);
        Assert.DoesNotContain(wsl.Commands, command => command.Count > 0 && command[0] == "--install");
    }

    [Fact]
    public async Task WslStoreInstanceInstaller_FailedInstall_IncludesWslLogsHelpWithoutPostconditionRecovery()
    {
        using var temp = new TempDirectory();
        var wsl = new FakeWslCommandRunner { InstallExitCode = 42 };
        var installer = new WslStoreInstanceInstaller(wsl, createDirectory: _ => { });

        var result = await installer.EnsureInstalledAsync(new LocalGatewaySetupOptions { InstanceInstallLocation = temp.Path });

        Assert.False(result.Success);
        Assert.Equal("wsl_instance_install_failed", result.ErrorCode);
        Assert.Contains("aka.ms/wsllogs", result.ErrorMessage!);
        Assert.Single(wsl.Commands, command => command.Count > 0 && command[0] == "--install");
        Assert.DoesNotContain(wsl.Commands, command => command.SequenceEqual(["-d", "OpenClawGateway", "-u", "root", "--", "true"]));
    }

    [Fact]
    public async Task WslFirstBootConfigurator_WritesCraigWslConfigurationThroughWslExe()
    {
        var wsl = new FakeWslCommandRunner();
        var configurator = new WslFirstBootConfigurator(wsl);

        var result = await configurator.ConfigureAsync(new LocalGatewaySetupOptions());

        Assert.True(result.Success);
        var command = Assert.Single(wsl.Commands, command => command.Count == 8 && command[5] == "bash" && command[6] == "-lc");
        Assert.Contains("cat >/etc/wsl.conf", command[7]);
        Assert.Contains("[automount]", command[7]);
        Assert.Contains("enabled=false", command[7]);
        Assert.Contains("mountFsTab=false", command[7]);
        Assert.Contains("appendWindowsPath=false", command[7]);
        Assert.Contains("[time]", command[7]);
        Assert.Contains("useWindowsTimezone=true", command[7]);
        Assert.Contains("cat >/etc/wsl-distribution.conf", command[7]);
        Assert.Contains("loginctl enable-linger openclaw", command[7]);
        Assert.DoesNotContain("machine-id", command[7], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resolv.conf", command[7], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\\wsl", command[7], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(wsl.Commands, command => command.SequenceEqual(["--manage", "OpenClawGateway", "--set-default-user", "openclaw"]));
        Assert.Contains(wsl.Commands, command => command.SequenceEqual(["--terminate", "OpenClawGateway"]));
    }

    [Fact]
    public async Task WslFirstBootConfigurator_SkipsConfiguration_WhenAlreadyConfigured()
    {
        var wsl = new FakeWslCommandRunner();
        var configurator = new WslFirstBootConfigurator(wsl);

        var result = await configurator.ConfigureAsync(new LocalGatewaySetupOptions { AllowExistingDistro = true });

        Assert.True(result.Success);
        Assert.Contains("wsl_instance_already_configured", result.Warnings ?? []);
        Assert.DoesNotContain(wsl.Commands, command => command.Count == 8 && command[5] == "bash" && command[6] == "-lc" && command[7].Contains("cat >/etc/wsl.conf"));
    }

    [Fact]
    public async Task WslFirstBootConfigurator_Reconfigures_WhenProbeFails_AlreadyConfiguredPath()
    {
        var wsl = new FakeWslCommandRunner { CommandExitCodeByContains = { ["awk"] = 1 } };
        var configurator = new WslFirstBootConfigurator(wsl);

        var result = await configurator.ConfigureAsync(new LocalGatewaySetupOptions { AllowExistingDistro = true });

        Assert.True(result.Success);
        Assert.DoesNotContain("wsl_instance_already_configured", result.Warnings ?? []);
        Assert.Contains(wsl.Commands, command => command.Count == 8 && command[5] == "bash" && command[6] == "-lc" && command[7].Contains("cat >/etc/wsl.conf"));
    }

    [Fact]
    public async Task WslFirstBootConfigurator_ProbeScript_ContainsSectionAwareAwkCheck()
    {
        var wsl = new FakeWslCommandRunner { CommandExitCodeByContains = { ["awk"] = 1 } };
        var configurator = new WslFirstBootConfigurator(wsl);

        await configurator.ConfigureAsync(new LocalGatewaySetupOptions { AllowExistingDistro = true });

        var probeCommand = Assert.Single(wsl.Commands, command =>
            command.Count == 8 && command[5] == "bash" && command[6] == "-lc" && command[7].Contains("awk"));
        var script = probeCommand[7];
        Assert.Contains("sec==\"automount\"&&$0==\"enabled=false\"", script);
        Assert.Contains("sec==\"automount\"&&$0==\"mountFsTab=false\"", script);
        Assert.Contains("sec==\"interop\"&&$0==\"enabled=false\"", script);
        Assert.Contains("sec==\"time\"&&$0==\"useWindowsTimezone=true\"", script);
    }

    [Fact]
    public async Task OpenClawInstallCliLinuxInstaller_UsesUpstreamInstallerAndRedactsEvents()
    {
        var wsl = new FakeWslCommandRunner
        {
            CommandOutputByContains = { ["install-cli.sh"] = "{\"event\":\"progress\",\"message\":\"bootstrapToken: secret-token\"}" }
        };
        var installer = new OpenClawInstallCliLinuxInstaller(wsl);

        var result = await installer.InstallAsync(new LocalGatewaySetupOptions { OpenClawInstallVersion = "next" });

        Assert.True(result.Success);
        Assert.Contains(wsl.Commands, command => command.Count == 8 && command[7].Contains("https://openclaw.ai/install-cli.sh", StringComparison.Ordinal));
        Assert.Contains(wsl.Commands, command => command.SequenceEqual(["-d", "OpenClawGateway", "-u", "openclaw", "--", "/opt/openclaw/bin/openclaw", "--version"]));
        Assert.DoesNotContain("secret-token", result.Events![0].RawLine);
        Assert.Contains("<redacted>", result.Events![0].RawLine);
    }

    [Fact]
    public async Task GatewayConfigurationPreparer_WritesLoopbackOnlyConfigWithoutBindOrTokenValue()
    {
        var wsl = new FakeWslCommandRunner();
        var preparer = new OpenClawCliGatewayConfigurationPreparer(wsl);

        const string sharedToken = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        var result = await preparer.PrepareAsync(new LocalGatewaySetupOptions(), sharedToken);

        Assert.True(result.Success);
        var command = Assert.Single(wsl.Commands, command =>
            command.Count == 8
            && command[7].Contains("config set gateway.mode local", StringComparison.Ordinal)
            && command[7].Contains("config set gateway.port 18789 --strict-json", StringComparison.Ordinal)
            && command[7].Contains("config set gateway.auth.mode token", StringComparison.Ordinal)
            && command[7].Contains("config set gateway.auth.token", StringComparison.Ordinal)
            && !command[7].Contains("gateway.bind", StringComparison.Ordinal)
            && !command[7].Contains("lan", StringComparison.Ordinal));
        Assert.Contains(": \"${OPENCLAW_SHARED_GATEWAY_TOKEN:?missing shared gateway token}\"", command[7]);
        Assert.Contains("printf '%s' \"$OPENCLAW_SHARED_GATEWAY_TOKEN\" >/var/lib/openclaw/gateway-token", command[7]);
        Assert.DoesNotContain("od -An -N32", command[7]);
        Assert.DoesNotContain(sharedToken, string.Join(" ", command));
        var environment = Assert.Single(wsl.Environments);
        Assert.Equal(sharedToken, environment[SharedGatewayTokenEnvironment.VariableName]);
    }

    [Fact]
    public async Task EndpointResolver_UsesOnlyLocalhostCandidate()
    {
        var wsl = new FakeWslCommandRunner { CommandOutputByContains = { ["hostname -I"] = "172.30.138.183" } };
        var health = new ReachableOnlyHealthProbe("ws://localhost:18789");
        var resolver = new LocalGatewayEndpointResolver();

        var result = await resolver.ResolveAsync(new LocalGatewaySetupOptions { GatewayUrl = "ws://127.0.0.1:18789" }, "ws://127.0.0.1:18789", health, wsl);

        Assert.True(result.Success);
        Assert.Equal("ws://localhost:18789", result.GatewayUrl);
        Assert.Equal(["ws://localhost:18789"], health.Attempts);
        Assert.DoesNotContain(wsl.Commands, command => string.Join(" ", command).Contains("hostname -I", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BootstrapTokenProvider_RunsGatewayQrCommandAndDecodesSetupCode()
    {
        var setupPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"url\":\"ws://localhost:18789\",\"bootstrapToken\":\"minted-token\"}"))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        var runner = new FakeWslCommandRunner { RunInDistroOutput = $"{{\"setupCode\":\"{setupPayload}\",\"expiresAtMs\":1893456000000}}" };
        var provider = new WslGatewayCliBootstrapTokenProvider(runner, "/opt/openclaw/bin/openclaw");

        var result = await provider.MintAsync(new LocalGatewaySetupState { DistroName = "OpenClawGateway", GatewayUrl = "ws://localhost:18789" });

        Assert.True(result.Success);
        Assert.Equal("minted-token", result.BootstrapToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1893456000000), result.ExpiresAtUtc);
        Assert.Contains(runner.Commands, command => command.Count == 3 && command[2].Contains("'/opt/openclaw/bin/openclaw' qr --json --url 'ws://localhost:18789'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SharedGatewayTokenProvider_GeneratesFreshLowercaseHexToken_WhenWslTokenMissing()
    {
        var runner = new FakeWslCommandRunner();
        var provider = new WslGatewayCliSharedGatewayTokenProvider(runner);

        var first = await provider.MintAsync(new LocalGatewaySetupState { DistroName = "OpenClawGateway" });
        var second = await provider.MintAsync(new LocalGatewaySetupState { DistroName = "OpenClawGateway" });

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(SharedGatewayTokenSource.Generated, first.Source);
        Assert.Matches("^[0-9a-f]{64}$", first.Token!);
        Assert.Matches("^[0-9a-f]{64}$", second.Token!);
        Assert.NotEqual(first.Token, second.Token);
    }

    [Fact]
    public async Task SharedGatewayTokenProvider_PreservesExistingSafeWslToken()
    {
        var existing = new string('a', 64);
        var runner = new FakeWslCommandRunner
        {
            CommandOutputByContains = { ["cat /var/lib/openclaw/gateway-token"] = existing + "\n" }
        };
        var provider = new WslGatewayCliSharedGatewayTokenProvider(runner);

        var result = await provider.MintAsync(new LocalGatewaySetupState { DistroName = "OpenClawGateway" });

        Assert.True(result.Success);
        Assert.Equal(existing, result.Token);
        Assert.Equal(SharedGatewayTokenSource.PreservedFromWsl, result.Source);
    }

    [Fact]
    public async Task SettingsSharedGatewayTokenProvisioner_PersistsTokenOnlyAfterGatewayConfigSucceeds()
    {
        var settings = new FakeSetupSettings();
        var tokenProvider = new FakeSharedGatewayTokenProvider(new string('b', 64));
        var preparer = new FakeGatewayConfigurationPreparer();
        var provisioner = new SettingsSharedGatewayTokenProvisioner(settings, tokenProvider, preparer);

        var result = await provisioner.ProvisionAsync(new LocalGatewaySetupState(), new LocalGatewaySetupOptions());

        Assert.True(result.Success);
        Assert.Equal(tokenProvider.Token, settings.Token);
        Assert.Equal(1, settings.SaveCount);
        Assert.Equal(tokenProvider.Token, preparer.LastSharedGatewayToken);
    }

    [Fact]
    public async Task SettingsSharedGatewayTokenProvisioner_DoesNotPersistTokenWhenGatewayConfigFails()
    {
        var settings = new FakeSetupSettings();
        var tokenProvider = new FakeSharedGatewayTokenProvider(new string('c', 64));
        var preparer = new FakeGatewayConfigurationPreparer { Result = new GatewayConfigurationResult(false, "boom", "failed") };
        var provisioner = new SettingsSharedGatewayTokenProvisioner(settings, tokenProvider, preparer);

        var result = await provisioner.ProvisionAsync(new LocalGatewaySetupState(), new LocalGatewaySetupOptions());

        Assert.False(result.Success);
        Assert.Equal("", settings.Token);
        Assert.Equal(0, settings.SaveCount);
        Assert.Equal(tokenProvider.Token, preparer.LastSharedGatewayToken);
    }

    [Fact]
    public async Task SettingsBootstrapTokenProvisioner_IgnoresSharedToken_WhenBootstrapTokenMissing()
    {
        var settings = new FakeSetupSettings { Token = "shared" };
        var provider = new FakeBootstrapTokenProvider("bootstrap");
        var provisioner = new SettingsBootstrapTokenProvisioner(settings, provider);

        var result = await provisioner.MintAsync(new LocalGatewaySetupState());

        Assert.True(result.Success);
        Assert.Equal("bootstrap", settings.BootstrapToken);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, settings.SaveCount);
    }

    [Fact]
    public void WslEnvironmentPassthrough_AppendsSharedTokenToExistingWslenvWithoutLoggingValues()
    {
        const string token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var environment = WslExeCommandRunner.BuildProcessEnvironment(
            new Dictionary<string, string> { ["WSLENV"] = "EXISTING/u" },
            new Dictionary<string, string> { [SharedGatewayTokenEnvironment.VariableName] = token });

        Assert.Equal(token, environment[SharedGatewayTokenEnvironment.VariableName]);
        Assert.Equal("EXISTING/u:OPENCLAW_SHARED_GATEWAY_TOKEN/u", environment["WSLENV"]);
        Assert.DoesNotContain(token, "[WSL] wsl.exe -d OpenClawGateway -u openclaw -- bash -lc <redacted>");
    }

    [Fact]
    public void WslEnvironmentPassthrough_AppendsGatewayTokenToExistingWslenvWithoutLoggingValues()
    {
        const string token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var environment = WslExeCommandRunner.BuildProcessEnvironment(
            new Dictionary<string, string> { ["WSLENV"] = "EXISTING/u" },
            new Dictionary<string, string> { [OpenClawGatewayTokenEnvironment.VariableName] = token });

        Assert.Equal(token, environment[OpenClawGatewayTokenEnvironment.VariableName]);
        Assert.Equal("EXISTING/u:OPENCLAW_GATEWAY_TOKEN/u", environment["WSLENV"]);
        Assert.DoesNotContain(token, "[WSL] wsl.exe -d OpenClawGateway -- bash -lc <redacted>");
    }

    [Fact]
    public async Task Engine_SharedGatewayProvisioning_ClosesBug6NonBootstrapSetupPath()
    {
        using var temp = new TempDirectory();
        var settings = new FakeSetupSettings();
        var sharedToken = new string('d', 64);
        var sharedProvider = new FakeSharedGatewayTokenProvider(sharedToken);
        var gatewayPreparer = new FakeGatewayConfigurationPreparer();
        var connector = new RecordingGatewayOperatorConnector();
        var engine = new LocalGatewaySetupEngine(
            new LocalGatewaySetupOptions { InstanceInstallLocation = System.IO.Path.Combine(temp.Path, "OpenClawGateway"), EnableWindowsTrayNodeByDefault = false },
            new LocalGatewaySetupStateStore(System.IO.Path.Combine(temp.Path, "setup-state.json")),
            new LocalGatewayPreflightProbe(new FakeWslCommandRunner(), new FixedPortProbe(available: true)),
            new FakeWslCommandRunner(),
            new SuccessfulHealthProbe(),
            new SettingsBootstrapTokenProvisioner(settings, new FakeBootstrapTokenProvider("bootstrap")),
            new SettingsOperatorPairingService(settings, connector),
            new FakeProvisioner(),
            wslInstanceInstaller: new WslStoreInstanceInstaller(new FakeWslCommandRunner(), createDirectory: _ => { }),
            wslInstanceConfigurator: new FakeWslInstanceConfigurator(),
            openClawLinuxInstaller: new FakeOpenClawLinuxInstaller(),
            gatewayConfigurationPreparer: gatewayPreparer,
            gatewayServiceManager: new FakeGatewayServiceManager(),
            sharedGatewayTokenProvisioner: new SettingsSharedGatewayTokenProvisioner(settings, sharedProvider, gatewayPreparer));

        var state = await engine.RunLocalOnlyAsync();

        Assert.Equal(LocalGatewaySetupStatus.Complete, state.Status);
        Assert.Equal(sharedToken, settings.Token);
        Assert.Equal(sharedToken, connector.LastToken);
        Assert.False(connector.LastTokenIsBootstrap);
    }

    [Fact]
    public async Task Engine_RunsCleanPhaseListThroughWindowsTrayNode()
    {
        using var temp = new TempDirectory();
        var statePath = System.IO.Path.Combine(temp.Path, "setup-state.json");
        var installLocation = System.IO.Path.Combine(temp.Path, "OpenClawGateway");
        var wsl = new FakeWslCommandRunner();
        var provisioning = new FakeProvisioner();
        var engine = new LocalGatewaySetupEngine(
            new LocalGatewaySetupOptions { InstanceInstallLocation = installLocation },
            new LocalGatewaySetupStateStore(statePath),
            new LocalGatewayPreflightProbe(wsl, new FixedPortProbe(available: true)),
            wsl,
            new SuccessfulHealthProbe(),
            provisioning,
            provisioning,
            provisioning,
            wslInstanceInstaller: new WslStoreInstanceInstaller(wsl, createDirectory: _ => { }),
            wslInstanceConfigurator: new FakeWslInstanceConfigurator(),
            openClawLinuxInstaller: new FakeOpenClawLinuxInstaller(),
            gatewayConfigurationPreparer: new FakeGatewayConfigurationPreparer(),
            gatewayServiceManager: new FakeGatewayServiceManager());

        var state = await engine.RunLocalOnlyAsync();

        Assert.Equal(LocalGatewaySetupStatus.Complete, state.Status);
        Assert.True(state.IsLocalOnly);
        Assert.Equal("ws://localhost:18789", state.GatewayUrl);
        Assert.Contains(state.History, h => h.Phase == LocalGatewaySetupPhase.CreateWslInstance);
        Assert.Contains(state.History, h => h.Phase == LocalGatewaySetupPhase.MintBootstrapToken);
        Assert.Contains(state.History, h => h.Phase == LocalGatewaySetupPhase.PairOperator);
        Assert.Contains(state.History, h => h.Phase == LocalGatewaySetupPhase.PairWindowsTrayNode);
        Assert.DoesNotContain(state.History, h => h.Phase.ToString().Contains("Worker", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(state.History, h => h.Phase.ToString().Contains("Import", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, provisioning.BootstrapMintCalls);
        Assert.Equal(1, provisioning.OperatorPairCalls);
        Assert.Equal(1, provisioning.WindowsNodeReadinessCalls);
        Assert.Equal(1, provisioning.WindowsNodePairCalls);
    }

    [Fact]
    public async Task Engine_ReusesOrphanedExistingDistro_WhenAppStateIsFresh()
    {
        using var temp = new TempDirectory();
        var wsl = new FakeWslCommandRunner { Distros = [new WslDistroInfo("OpenClawGateway", "Stopped", 2)] };
        var provisioning = new FakeProvisioner();
        var engine = new LocalGatewaySetupEngine(
            new LocalGatewaySetupOptions(),
            new LocalGatewaySetupStateStore(System.IO.Path.Combine(temp.Path, "setup-state.json")),
            new LocalGatewayPreflightProbe(wsl, new FixedPortProbe(available: true)),
            wsl,
            new SuccessfulHealthProbe(),
            provisioning,
            provisioning,
            provisioning);

        var state = await engine.RunLocalOnlyAsync();

        Assert.Equal(LocalGatewaySetupStatus.Complete, state.Status);
        Assert.DoesNotContain(wsl.Commands, command => command.Count > 0 && command[0] == "--install");
        Assert.Contains(state.History, h => h.Phase == LocalGatewaySetupPhase.CreateWslInstance);
    }

    [Fact]
    public async Task LifecycleManager_RepairTerminatesOnlyGatewayDistroAndRestartsGatewayService()
    {
        var wsl = new FakeWslCommandRunner { Distros = [new WslDistroInfo("OpenClawGateway", "Running", 2)] };
        var manager = new LocalGatewayLifecycleManager(new LocalGatewaySetupOptions(), wsl, new SuccessfulHealthProbe());

        var result = await manager.RepairAsync();

        Assert.True(result.Success);
        Assert.Contains("distro_terminated", result.Steps!);
        Assert.Contains(wsl.Commands, command => command.SequenceEqual(["--terminate", "OpenClawGateway"]));
        Assert.Contains(wsl.Commands, command => command.SequenceEqual(["-d", "OpenClawGateway", "-u", "root", "--", "systemctl", "restart", "openclaw-gateway.service"]));
        Assert.DoesNotContain(wsl.Commands, command => command.SequenceEqual(["--shutdown"]));
        Assert.DoesNotContain(wsl.Commands, command => string.Join(" ", command).Contains("openclaw-worker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LifecycleManager_RemoveUnregistersDistroAndClearsLocalCredentials()
    {
        var settings = new FakeSetupSettings { Token = "token", BootstrapToken = "bootstrap", EnableNodeMode = true };
        var wsl = new FakeWslCommandRunner { Distros = [new WslDistroInfo("OpenClawGateway", "Stopped", 2)] };
        var manager = new LocalGatewayLifecycleManager(new LocalGatewaySetupOptions(), wsl, new SuccessfulHealthProbe(), settings);

        var result = await manager.RemoveAsync(new LocalGatewayRemoveRequest(ConfirmRemove: true, ClearLocalCredentials: true));

        Assert.True(result.Success);
        Assert.Contains("OpenClawGateway", wsl.UnregisteredDistros);
        Assert.Equal("", settings.Token);
        Assert.Equal("", settings.BootstrapToken);
        Assert.False(settings.EnableNodeMode);
        Assert.True(settings.SaveCalled);
    }

    [Fact]
    public void CreateLocalOnly_Succeeds_WhenExistingConfigAndConfirmed()
    {
        using var temp = new TempDirectory();
        var settings = new OpenClawTray.Services.SettingsManager(temp.Path) { GatewayUrl = "ws://remote:9000" };

        var engine = LocalGatewaySetupEngineFactory.CreateLocalOnly(
            settings,
            FakeOperatorConnector.Instance,
            FakeWindowsNodeConnector.Instance,
            replaceExistingConfigurationConfirmed: true);

        Assert.NotNull(engine);
    }

    [Fact]
    public void CreateLocalOnly_ThrowsInvalidOperation_WhenNonDefaultGatewayUrlAndNotConfirmed()
    {
        using var temp = new TempDirectory();
        var settings = new OpenClawTray.Services.SettingsManager(temp.Path) { GatewayUrl = "ws://my-server:9000" };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LocalGatewaySetupEngineFactory.CreateLocalOnly(
                settings,
                FakeOperatorConnector.Instance,
                FakeWindowsNodeConnector.Instance,
                identityDataPath: temp.Path,
                replaceExistingConfigurationConfirmed: false));

        Assert.Contains("existing_config_replacement_not_confirmed", ex.Message);
    }

    [Fact]
    public void CreateLocalOnly_ThrowsInvalidOperation_WhenOperatorDeviceTokenExistsAndNotConfirmed()
    {
        using var temp = new TempDirectory();
        var settings = new OpenClawTray.Services.SettingsManager(temp.Path);
        File.WriteAllText(
            Path.Combine(temp.Path, "device-key-ed25519.json"),
            """{"DeviceToken":"op-device-token-value"}""");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LocalGatewaySetupEngineFactory.CreateLocalOnly(
                settings,
                FakeOperatorConnector.Instance,
                FakeWindowsNodeConnector.Instance,
                identityDataPath: temp.Path,
                replaceExistingConfigurationConfirmed: false));

        Assert.Contains("existing_config_replacement_not_confirmed", ex.Message);
    }

    [Fact]
    public void CreateLocalOnly_ThrowsInvalidOperation_WhenNodeDeviceTokenExistsAndNotConfirmed()
    {
        using var temp = new TempDirectory();
        var settings = new OpenClawTray.Services.SettingsManager(temp.Path);
        File.WriteAllText(
            Path.Combine(temp.Path, "device-key-ed25519.json"),
            """{"NodeDeviceToken":"node-device-token-value"}""");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LocalGatewaySetupEngineFactory.CreateLocalOnly(
                settings,
                FakeOperatorConnector.Instance,
                FakeWindowsNodeConnector.Instance,
                identityDataPath: temp.Path,
                replaceExistingConfigurationConfirmed: false));

        Assert.Contains("existing_config_replacement_not_confirmed", ex.Message);
    }

    [Fact]
    public void CreateLocalOnly_ThrowsInvalidOperation_WhenActiveSetupStateAndNotConfirmed()
    {
        using var temp = new TempDirectory();
        var settings = new OpenClawTray.Services.SettingsManager(temp.Path);
        var setupStatePath = Path.Combine(temp.Path, "setup-state.json");
        File.WriteAllText(setupStatePath, """{"Phase":"ConfigureWslInstance"}""");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LocalGatewaySetupEngineFactory.CreateLocalOnly(
                settings,
                FakeOperatorConnector.Instance,
                FakeWindowsNodeConnector.Instance,
                identityDataPath: temp.Path,
                setupStatePath: setupStatePath,
                replaceExistingConfigurationConfirmed: false));

        Assert.Contains("existing_config_replacement_not_confirmed", ex.Message);
    }

    /// <summary>
    /// No-op operator connector used by tests that hit existing-config guards before
    /// any pairing happens. Production paths supply ConnectionManagerOperatorConnector.
    /// </summary>
    private sealed class FakeOperatorConnector : IGatewayOperatorConnector
    {
        public static readonly FakeOperatorConnector Instance = new();
        public Task<GatewayOperatorConnectionResult> ConnectAsync(string gatewayUrl, string token, bool tokenIsBootstrapToken = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.Connected));
        public Task<GatewayOperatorConnectionResult> ConnectWithStoredDeviceTokenAsync(string gatewayUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.Connected));
    }

    /// <summary>
    /// No-op node connector used by tests that hit existing-config guards before
    /// any node pairing happens. Production paths supply ConnectionManagerWindowsNodeConnector.
    /// </summary>
    private sealed class FakeWindowsNodeConnector : IWindowsNodeConnector
    {
        public static readonly FakeWindowsNodeConnector Instance = new();
        public Task ConnectAsync(string gatewayUrl, string token, string? bootstrapToken, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    internal sealed class FakeWslCommandRunner : IWslCommandRunner
    {
        public List<WslDistroInfo> Distros { get; set; } = [];
        public List<string> UnregisteredDistros { get; } = [];
        public List<IReadOnlyList<string>> Commands { get; } = [];
        public List<IReadOnlyDictionary<string, string>> Environments { get; } = [];
        public int WslStatusExitCode { get; set; }
        public string WslStatusOutput { get; set; } = "";
        public string RunInDistroOutput { get; set; } = "";
        public int InstallExitCode { get; set; }
        public WslCommandResult UnregisterResult { get; set; } = new(0, "", "");
        public bool RemoveDistroOnUnregister { get; set; } = true;
        public Dictionary<string, WslCommandResult> CommandResultByContains { get; } = new();
        public Dictionary<string, string> CommandOutputByContains { get; } = new();
        public Dictionary<string, int> CommandExitCodeByContains { get; } = new();

        public Task<WslCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? environment = null)
        {
            Commands.Add(arguments);
            if (environment is not null)
                Environments.Add(new Dictionary<string, string>(environment));
            if (arguments.SequenceEqual(["--status"]))
                return Task.FromResult(new WslCommandResult(WslStatusExitCode, WslStatusOutput, ""));

            if (arguments.Count > 0 && arguments[0] == "--install")
                return Task.FromResult(new WslCommandResult(InstallExitCode, "", InstallExitCode == 0 ? "" : "install failed"));

            var joined = string.Join(" ", arguments);
            foreach (var pair in CommandResultByContains)
            {
                if (joined.Contains(pair.Key, StringComparison.Ordinal))
                    return Task.FromResult(pair.Value);
            }
            foreach (var pair in CommandExitCodeByContains)
            {
                if (joined.Contains(pair.Key, StringComparison.Ordinal))
                    return Task.FromResult(new WslCommandResult(pair.Value, "", ""));
            }
            foreach (var pair in CommandOutputByContains)
            {
                if (joined.Contains(pair.Key, StringComparison.Ordinal))
                    return Task.FromResult(new WslCommandResult(0, pair.Value, ""));
            }

            return Task.FromResult(new WslCommandResult(0, "", ""));
        }

        public Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WslDistroInfo>>(Distros.ToArray());

        public Task<WslCommandResult> TerminateDistroAsync(string name, CancellationToken cancellationToken = default)
        {
            Commands.Add(["--terminate", name]);
            return Task.FromResult(new WslCommandResult(0, "", ""));
        }

        public Task<WslCommandResult> UnregisterDistroAsync(string name, CancellationToken cancellationToken = default)
        {
            UnregisteredDistros.Add(name);
            if (RemoveDistroOnUnregister)
                Distros.RemoveAll(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(UnregisterResult);
        }

        public Task<WslCommandResult> RunInDistroAsync(string name, IReadOnlyList<string> command, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? environment = null)
        {
            Commands.Add(command);
            if (environment is not null)
                Environments.Add(new Dictionary<string, string>(environment));
            return Task.FromResult(new WslCommandResult(0, RunInDistroOutput, ""));
        }
    }

    private sealed class FakeSetupEnvironment : ILocalGatewaySetupEnvironment
    {
        private readonly IReadOnlyDictionary<string, string?> _values;
        public FakeSetupEnvironment(IReadOnlyDictionary<string, string?> values) => _values = values;
        public string? GetVariable(string name) => _values.TryGetValue(name, out var value) ? value : null;
    }

    internal sealed class FixedPortProbe : IPortProbe
    {
        private readonly bool _available;
        public FixedPortProbe(bool available) => _available = available;
        public bool IsPortAvailable(int port) => _available;
    }

    internal sealed class SuccessfulHealthProbe : ILocalGatewayHealthProbe
    {
        public Task<LocalGatewayHealthResult> WaitForHealthyAsync(string gatewayUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LocalGatewayHealthResult(true));
    }

    private sealed class ReachableOnlyHealthProbe : ILocalGatewayHealthProbe
    {
        private readonly string _reachableGatewayUrl;
        public ReachableOnlyHealthProbe(string reachableGatewayUrl) => _reachableGatewayUrl = reachableGatewayUrl;
        public List<string> Attempts { get; } = [];
        public Task<LocalGatewayHealthResult> WaitForHealthyAsync(string gatewayUrl, CancellationToken cancellationToken = default)
        {
            Attempts.Add(gatewayUrl);
            return Task.FromResult(gatewayUrl.Equals(_reachableGatewayUrl, StringComparison.OrdinalIgnoreCase)
                ? new LocalGatewayHealthResult(true)
                : new LocalGatewayHealthResult(false, "unreachable"));
        }
    }

    private sealed class FakeProvisioner : IBootstrapTokenProvisioner, IOperatorPairingService, IWindowsTrayNodeProvisioner
    {
        public int BootstrapMintCalls { get; private set; }
        public int OperatorPairCalls { get; private set; }
        public int WindowsNodeReadinessCalls { get; private set; }
        public int WindowsNodePairCalls { get; private set; }

        public Task<ProvisioningResult> MintAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default)
        {
            BootstrapMintCalls++;
            return Task.FromResult(new ProvisioningResult(true));
        }

        Task<ProvisioningResult> IOperatorPairingService.PairAsync(LocalGatewaySetupState state, CancellationToken cancellationToken)
        {
            OperatorPairCalls++;
            return Task.FromResult(new ProvisioningResult(true));
        }

        Task<ProvisioningResult> IWindowsTrayNodeProvisioner.CheckReadinessAsync(LocalGatewaySetupState state, CancellationToken cancellationToken)
        {
            WindowsNodeReadinessCalls++;
            return Task.FromResult(new ProvisioningResult(true));
        }

        Task<ProvisioningResult> IWindowsTrayNodeProvisioner.PairAsync(LocalGatewaySetupState state, CancellationToken cancellationToken)
        {
            WindowsNodePairCalls++;
            return Task.FromResult(new ProvisioningResult(true));
        }
    }

    private sealed class FakeSetupSettings : ILocalGatewaySetupSettings
    {
        public string GatewayUrl { get; set; } = "";
        public string Token { get; set; } = "";
        public string BootstrapToken { get; set; } = "";
        public bool UseSshTunnel { get; set; } = true;
        public bool EnableNodeMode { get; set; }
        public bool SaveCalled { get; private set; }
        public int SaveCount { get; private set; }
        public void Save()
        {
            SaveCalled = true;
            SaveCount++;
        }
    }

    internal sealed class FakeWslInstanceConfigurator : IWslInstanceConfigurator
    {
        public Task<WslInstanceConfigurationResult> ConfigureAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WslInstanceConfigurationResult(true));
    }

    internal sealed class FakeOpenClawLinuxInstaller : IOpenClawLinuxInstaller
    {
        public Task<OpenClawLinuxInstallResult> InstallAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OpenClawLinuxInstallResult(true));
    }

    private sealed class RecordingGatewayOperatorConnector : IGatewayOperatorConnector
    {
        public string? LastToken { get; private set; }
        public bool LastTokenIsBootstrap { get; private set; }

        public Task<GatewayOperatorConnectionResult> ConnectAsync(string gatewayUrl, string token, bool tokenIsBootstrapToken = false, CancellationToken cancellationToken = default)
        {
            LastToken = token;
            LastTokenIsBootstrap = tokenIsBootstrapToken;
            return Task.FromResult(new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.Connected));
        }

        public Task<GatewayOperatorConnectionResult> ConnectWithStoredDeviceTokenAsync(string gatewayUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.Connected));
    }

    private sealed class FakeBootstrapTokenProvider : IBootstrapTokenProvider
    {
        private readonly string _token;
        public FakeBootstrapTokenProvider(string token) => _token = token;
        public int Calls { get; private set; }
        public Task<BootstrapTokenResult> MintAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new BootstrapTokenResult(true, _token));
        }
    }

    private sealed class FakeSharedGatewayTokenProvider : ISharedGatewayTokenProvider
    {
        public FakeSharedGatewayTokenProvider(string token) => Token = token;
        public string Token { get; }
        public Task<SharedGatewayTokenResult> MintAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SharedGatewayTokenResult(true, Token, SharedGatewayTokenSource.Generated));
    }

    internal sealed class FakeGatewayConfigurationPreparer : IGatewayConfigurationPreparer
    {
        public GatewayConfigurationResult Result { get; set; } = new(true);
        public string? LastSharedGatewayToken { get; private set; }
        public Task<GatewayConfigurationResult> PrepareAsync(LocalGatewaySetupOptions options, string sharedGatewayToken, CancellationToken cancellationToken = default)
        {
            LastSharedGatewayToken = sharedGatewayToken;
            return Task.FromResult(Result);
        }
    }

    internal sealed class FakeGatewayServiceManager : IGatewayServiceManager
    {
        public Task<GatewayServiceOperationResult> InstallAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GatewayServiceOperationResult(true));

        public Task<GatewayServiceOperationResult> StartAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GatewayServiceOperationResult(true));
    }

    internal sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-tests-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup best effort.
            }
        }
    }
}

/// <summary>
/// Tests for LocalGatewaySetup.SecretRedactor — verifies that the key-value regex
/// redacts sensitive terms found in subprocess output and log strings.
/// </summary>
public class LocalGatewaySetupSecretRedactorTests
{
    [Theory]
    [InlineData("gateway-token=abc123def456", "gateway-token=<redacted>")]
    [InlineData("gateway_token: abc123def456", "gateway_token: <redacted>")]
    [InlineData("bootstrap-token=tok789", "bootstrap-token=<redacted>")]
    [InlineData("bootstrap_token = tok789", "bootstrap_token = <redacted>")]
    [InlineData("device-token: dev-abc", "device-token: <redacted>")]
    [InlineData("auth-token=\"xyz\"", "auth-token=\"<redacted>\"")]
    [InlineData("setup-code=ABCD-1234", "setup-code=<redacted>")]
    [InlineData("setup_code='ABCD-1234'", "setup_code='<redacted>'")]
    [InlineData("secret: mysecretvalue", "secret: <redacted>")]
    [InlineData("private-key=MIIB...", "private-key=<redacted>")]
    [InlineData("private_key: MIIB...", "private_key: <redacted>")]
    public void Redact_SecretKeyValuePattern_IsRedacted(string input, string expected)
    {
        Assert.Equal(expected, SecretRedactor.Redact(input));
    }

    [Theory]
    [InlineData("exit code 1")]
    [InlineData("Error: connection refused")]
    [InlineData("username: alice")]
    [InlineData("url: https://gateway.example.com")]
    public void Redact_NonSecretText_IsUnchanged(string input)
    {
        Assert.Equal(input, SecretRedactor.Redact(input));
    }

    [Fact]
    public void Redact_EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, SecretRedactor.Redact(string.Empty));
    }

    [Fact]
    public void Redact_CaseInsensitive_RedactsUpperAndMixedCase()
    {
        Assert.Contains("<redacted>", SecretRedactor.Redact("GATEWAY-TOKEN=abc123"));
        Assert.Contains("<redacted>", SecretRedactor.Redact("Bootstrap_Token: abc123"));
    }
}

/// <summary>
/// Tests for LocalGatewaySetup.DiagnosticFormatter — verifies that subprocess
/// stdout/stderr is sanitized through both SecretRedactor (key-value patterns)
/// and TokenSanitizer (raw token formats) before being included in diagnostics.
/// </summary>
public class LocalGatewaySetupDiagnosticFormatterTests
{
    private static WslCommandResult Result(string stdout = "", string stderr = "") =>
        new(ExitCode: 1, StandardOutput: stdout, StandardError: stderr);

    [Fact]
    public void Build_IncludesExitCode()
    {
        var result = new WslCommandResult(ExitCode: 42, StandardOutput: "", StandardError: "");
        var output = DiagnosticFormatter.Build("install", result);
        Assert.Contains("install_exit_code=42", output);
    }

    [Fact]
    public void Build_IncludesStdoutAndStderr()
    {
        var result = Result(stdout: "ok", stderr: "warn");
        var output = DiagnosticFormatter.Build("step", result);
        Assert.Contains("step_stdout=ok", output);
        Assert.Contains("step_stderr=warn", output);
    }

    [Fact]
    public void Build_OmitsEmptyStdoutAndStderr()
    {
        var result = Result(stdout: "", stderr: "");
        var output = DiagnosticFormatter.Build("step", result);
        Assert.DoesNotContain("stdout", output);
        Assert.DoesNotContain("stderr", output);
    }

    [Fact]
    public void Build_RedactsGatewayTokenKeyValueInStdout()
    {
        var result = Result(stdout: "Config loaded: gateway-token=abc123secretvalue");
        var output = DiagnosticFormatter.Build("configure", result);
        Assert.DoesNotContain("abc123secretvalue", output);
        Assert.Contains("<redacted>", output);
    }

    [Fact]
    public void Build_RedactsRawHexGatewayTokenInStderr()
    {
        // 64-char hex token as would appear if a CLI tool echoes arguments in an error message.
        // TokenSanitizer.BareGatewayHexTokenPattern catches this even without a key= prefix.
        var rawToken = new string('a', 64);
        var result = Result(stderr: $"Error: failed to authenticate with token {rawToken}");
        var output = DiagnosticFormatter.Build("auth", result);
        Assert.DoesNotContain(rawToken, output);
        Assert.Contains("[REDACTED_TOKEN]", output);
    }

    [Fact]
    public void Build_RedactsMixedCaseRawHexGatewayTokenInStderr()
    {
        const string rawToken = "0123456789ABCDEF0123456789abcdef0123456789ABCDEF0123456789abcdef";
        var result = Result(stderr: $"Error: failed to authenticate with token {rawToken}");

        var output = DiagnosticFormatter.Build("auth", result);

        Assert.DoesNotContain(rawToken, output);
        Assert.Contains("[REDACTED_TOKEN]", output);
    }

    [Fact]
    public void Build_TruncatesLongOutput()
    {
        var longValue = new string('x', 3000);
        var result = Result(stdout: longValue);
        var output = DiagnosticFormatter.Build("step", result);
        Assert.Contains("...<truncated>", output);
        Assert.DoesNotContain(longValue, output);
    }

    [Fact]
    public void Build_StripsNullBytes()
    {
        var result = Result(stdout: "data\0with\0nulls");
        var output = DiagnosticFormatter.Build("step", result);
        Assert.DoesNotContain('\0', output);
        Assert.Contains("datawith", output);
    }
}
