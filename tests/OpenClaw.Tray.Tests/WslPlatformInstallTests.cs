using OpenClaw.Shared;
using OpenClawTray.Services.LocalGatewaySetup;

namespace OpenClaw.Tray.Tests;

public class WslPlatformInstallTests
{
    [Theory]
    [InlineData("The Windows Subsystem for Linux is not installed. You can install by running 'wsl.exe --install'.\nFor more information please visit https://aka.ms/wslinstall", true)]
    [InlineData("AKA.MS/WSLINSTALL", true)]
    [InlineData("aka.ms/wslinstall", true)]
    [InlineData("Windows Subsystem for Linux is not installed", true)]
    [InlineData("\0\0The Windows Subsystem for Linux is not installed\0\0", true)]
    [InlineData("Default Version: 2\nWSL version: 2.1.5.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeNotInstalled_MatchesLocaleStableBanner(string? output, bool expected)
    {
        Assert.Equal(expected, WslPlatformProbe.LooksLikeNotInstalled(output));
    }

    [Fact]
    public async Task Probe_ReturnsNotInstalled_WhenWslExeMissingOnDisk()
    {
        // No call to wsl.exe should be made if the binary isn't even present.
        var runner = new RecordingWslCommandRunner();
        var probe = new WslPlatformProbe(runner, fileExists: _ => false, wslExePath: @"C:\Windows\System32\wsl.exe");

        var result = await probe.ProbeAsync();

        Assert.Equal(WslPlatformState.NotInstalled, result.State);
        Assert.Empty(runner.Calls);
        Assert.Contains("wsl.exe not found", result.Detail);
    }

    [Fact]
    public async Task Probe_ReturnsNotInstalled_WhenWslStatusReportsNotInstalled()
    {
        var runner = new RecordingWslCommandRunner
        {
            StatusResult = new WslCommandResult(
                ExitCode: -1,
                StandardOutput: "",
                StandardError: "The Windows Subsystem for Linux is not installed. You can install by running 'wsl.exe --install'.\nFor more information please visit https://aka.ms/wslinstall"),
        };
        var probe = new WslPlatformProbe(runner, fileExists: _ => true, wslExePath: @"C:\Windows\System32\wsl.exe");

        var result = await probe.ProbeAsync();

        Assert.Equal(WslPlatformState.NotInstalled, result.State);
        Assert.NotNull(result.StatusResult);
        // Only one wsl invocation — we do NOT additionally call wsl --list --verbose
        // (that's the preflight's job, which short-circuits when this probe says
        // NotInstalled).
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Probe_ReturnsInstalled_WhenWslStatusSucceeds()
    {
        var runner = new RecordingWslCommandRunner
        {
            StatusResult = new WslCommandResult(0, "Default Version: 2\nWSL version: 2.1.5.0", ""),
        };
        var probe = new WslPlatformProbe(runner, fileExists: _ => true, wslExePath: @"C:\Windows\System32\wsl.exe");

        var result = await probe.ProbeAsync();

        Assert.Equal(WslPlatformState.Installed, result.State);
        Assert.NotNull(result.StatusResult);
        Assert.True(result.StatusResult!.Success);
    }

    [Fact]
    public async Task Probe_ReturnsUnknown_WhenWslStatusFailsWithoutNotInstalledBanner()
    {
        var runner = new RecordingWslCommandRunner
        {
            StatusResult = new WslCommandResult(1, "", "Access is denied by group policy."),
        };
        var probe = new WslPlatformProbe(runner, fileExists: _ => true, wslExePath: @"C:\Windows\System32\wsl.exe");

        var result = await probe.ProbeAsync();

        Assert.Equal(WslPlatformState.Unknown, result.State);
    }

    [Fact]
    public async Task Preflight_EmitsWarning_AndSkipsDistroList_WhenPlatformMissing()
    {
        var runner = new LocalGatewaySetupTests.FakeWslCommandRunner
        {
            WslStatusExitCode = -1,
            WslStatusOutput = "The Windows Subsystem for Linux is not installed. https://aka.ms/wslinstall",
            Distros = [new WslDistroInfo("OpenClawGateway", "Stopped", 2)],
        };
        var probe = new WslPlatformProbe(runner, fileExists: _ => true, wslExePath: @"C:\Windows\System32\wsl.exe");
        var preflight = new LocalGatewayPreflightProbe(runner, new LocalGatewaySetupTests.FixedPortProbe(available: true), probe);

        var result = await preflight.RunAsync(new LocalGatewaySetupOptions());

        // Non-blocking — the engine should proceed and the EnsureWslEnabled
        // phase will run the elevated install.
        Assert.True(result.CanContinue);
        Assert.Contains(result.Issues, i => i.Code == "wsl_platform_not_installed" && i.Severity == LocalGatewaySetupSeverity.Warning);
        // The distro_exists check must NOT fire — we never enumerated distros
        // when WSL itself is missing.
        Assert.DoesNotContain(result.Issues, i => i.Code == "distro_exists");
        // FakeWslCommandRunner.ListDistrosAsync returns its Distros field directly
        // without invoking RunAsync, so we cannot assert on runner.Commands for the
        // --list --verbose call. The distro_exists absence above is the meaningful
        // signal: when platform is missing, the preflight skips the entire distro
        // enumeration block.
    }

    [Fact]
    public async Task Installer_ReturnsInstalledNoRestart_WhenProbeConfirmsInstalledAfterExit0()
    {
        var probe = new SequencedPlatformProbe(WslPlatformState.Installed);
        var installer = new ElevatedWslPlatformInstaller(
            probe,
            processRunner: (_, _) => Task.FromResult(0));

        var result = await installer.InstallAsync();

        Assert.Equal(WslPlatformInstallOutcome.InstalledNoRestart, result.Outcome);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Installer_ReturnsInstalledRequiresRestart_OnExit3010()
    {
        var probe = new SequencedPlatformProbe(WslPlatformState.NotInstalled);
        var installer = new ElevatedWslPlatformInstaller(
            probe,
            processRunner: (_, _) => Task.FromResult(3010));

        var result = await installer.InstallAsync();

        Assert.Equal(WslPlatformInstallOutcome.InstalledRequiresRestart, result.Outcome);
    }

    [Fact]
    public async Task Installer_ReturnsInstalledRequiresRestart_WhenExit0ButPostProbeStillMissing()
    {
        // wsl.exe says it succeeded but the platform isn't actually usable
        // until reboot — classic "ERROR_SUCCESS but kernel module not loaded"
        // case. We trust the probe over the exit code here.
        var probe = new SequencedPlatformProbe(WslPlatformState.NotInstalled);
        var installer = new ElevatedWslPlatformInstaller(
            probe,
            processRunner: (_, _) => Task.FromResult(0));

        var result = await installer.InstallAsync();

        Assert.Equal(WslPlatformInstallOutcome.InstalledRequiresRestart, result.Outcome);
    }

    [Fact]
    public async Task Installer_ReturnsFailed_WhenNonZeroExitAndStillNotInstalled()
    {
        var probe = new SequencedPlatformProbe(WslPlatformState.NotInstalled);
        var installer = new ElevatedWslPlatformInstaller(
            probe,
            processRunner: (_, _) => Task.FromResult(1));

        var result = await installer.InstallAsync();

        Assert.Equal(WslPlatformInstallOutcome.Failed, result.Outcome);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Installer_ReturnsUserDeclinedElevation_WhenShellExecuteThrowsCancelled()
    {
        var probe = new SequencedPlatformProbe(WslPlatformState.NotInstalled);
        var installer = new ElevatedWslPlatformInstaller(
            probe,
            processRunner: (_, _) => throw new System.ComponentModel.Win32Exception(1223 /* ERROR_CANCELLED */));

        var result = await installer.InstallAsync();

        Assert.Equal(WslPlatformInstallOutcome.UserDeclinedElevation, result.Outcome);
    }

    [Fact]
    public async Task Engine_EnsureWslEnabled_RunsInstaller_OnInstalledNoRestart_AndProceeds()
    {
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var statePath = System.IO.Path.Combine(temp.Path, "setup-state.json");
        var installLocation = System.IO.Path.Combine(temp.Path, "OpenClawGateway");

        var wsl = new LocalGatewaySetupTests.FakeWslCommandRunner
        {
            // First wsl --status emits the "not installed" banner so preflight
            // adds the warning.
            WslStatusExitCode = -1,
            WslStatusOutput = "The Windows Subsystem for Linux is not installed. https://aka.ms/wslinstall",
        };
        var preflightProbe = new WslPlatformProbe(wsl, fileExists: _ => true, wslExePath: @"C:\Windows\System32\wsl.exe");

        var installerInvocations = 0;
        var installer = new FakeWslPlatformInstaller(() =>
        {
            installerInvocations++;
            // Simulate a successful install: now report Installed so the
            // engine continues. Flip the runner's status output for any future
            // calls (the engine's WSL ops past this point use the fake runner
            // which defaults to success).
            wsl.WslStatusExitCode = 0;
            wsl.WslStatusOutput = "Default Version: 2\nWSL version: 2.1.5.0";
            return new WslPlatformInstallResult(WslPlatformInstallOutcome.InstalledNoRestart, 0);
        });

        var provisioning = new LocalGatewaySetupTests.FakeProvisioner();
        var engine = new LocalGatewaySetupEngine(
            new LocalGatewaySetupOptions { InstanceInstallLocation = installLocation, EnableWindowsTrayNodeByDefault = false },
            new LocalGatewaySetupStateStore(statePath),
            new LocalGatewayPreflightProbe(wsl, new LocalGatewaySetupTests.FixedPortProbe(available: true), preflightProbe),
            wsl,
            new LocalGatewaySetupTests.SuccessfulHealthProbe(),
            provisioning,
            provisioning,
            provisioning,
            wslInstanceInstaller: new WslStoreInstanceInstaller(wsl, createDirectory: _ => { }),
            wslInstanceConfigurator: new LocalGatewaySetupTests.FakeWslInstanceConfigurator(),
            openClawLinuxInstaller: new LocalGatewaySetupTests.FakeOpenClawLinuxInstaller(),
            gatewayConfigurationPreparer: new LocalGatewaySetupTests.FakeGatewayConfigurationPreparer(),
            gatewayServiceManager: new LocalGatewaySetupTests.FakeGatewayServiceManager(),
            wslPlatformInstaller: installer);

        var state = await engine.RunLocalOnlyAsync();

        Assert.Equal(1, installerInvocations);
        Assert.Equal(LocalGatewaySetupStatus.Complete, state.Status);
        Assert.DoesNotContain(state.Issues, i => i.Code == "wsl_platform_not_installed");
    }

    [Fact]
    public async Task Engine_EnsureWslEnabled_SurfacesRequiresRestart_WhenInstallerReportsRestartNeeded()
    {
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var statePath = System.IO.Path.Combine(temp.Path, "setup-state.json");

        var wsl = new LocalGatewaySetupTests.FakeWslCommandRunner
        {
            WslStatusExitCode = -1,
            WslStatusOutput = "The Windows Subsystem for Linux is not installed. https://aka.ms/wslinstall",
        };
        var preflightProbe = new WslPlatformProbe(wsl, fileExists: _ => true, wslExePath: @"C:\Windows\System32\wsl.exe");
        var installer = new FakeWslPlatformInstaller(() =>
            new WslPlatformInstallResult(WslPlatformInstallOutcome.InstalledRequiresRestart, 3010));

        var provisioning = new LocalGatewaySetupTests.FakeProvisioner();
        var engine = new LocalGatewaySetupEngine(
            new LocalGatewaySetupOptions { EnableWindowsTrayNodeByDefault = false },
            new LocalGatewaySetupStateStore(statePath),
            new LocalGatewayPreflightProbe(wsl, new LocalGatewaySetupTests.FixedPortProbe(available: true), preflightProbe),
            wsl,
            new LocalGatewaySetupTests.SuccessfulHealthProbe(),
            provisioning,
            provisioning,
            provisioning,
            wslInstanceInstaller: new WslStoreInstanceInstaller(wsl, createDirectory: _ => { }),
            wslInstanceConfigurator: new LocalGatewaySetupTests.FakeWslInstanceConfigurator(),
            openClawLinuxInstaller: new LocalGatewaySetupTests.FakeOpenClawLinuxInstaller(),
            gatewayConfigurationPreparer: new LocalGatewaySetupTests.FakeGatewayConfigurationPreparer(),
            gatewayServiceManager: new LocalGatewaySetupTests.FakeGatewayServiceManager(),
            wslPlatformInstaller: installer);

        var state = await engine.RunLocalOnlyAsync();

        Assert.Equal(LocalGatewaySetupStatus.RequiresRestart, state.Status);
        Assert.Equal("wsl_install_requires_restart", state.FailureCode);
        Assert.Contains("Restart", state.UserMessage ?? "");
    }

    [Fact]
    public async Task Engine_EnsureWslEnabled_BlocksRetryable_WhenInstallerFails()
    {
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var statePath = System.IO.Path.Combine(temp.Path, "setup-state.json");

        var wsl = new LocalGatewaySetupTests.FakeWslCommandRunner
        {
            WslStatusExitCode = -1,
            WslStatusOutput = "The Windows Subsystem for Linux is not installed. https://aka.ms/wslinstall",
        };
        var preflightProbe = new WslPlatformProbe(wsl, fileExists: _ => true, wslExePath: @"C:\Windows\System32\wsl.exe");
        var installer = new FakeWslPlatformInstaller(() =>
            new WslPlatformInstallResult(WslPlatformInstallOutcome.Failed, 1, "Install failed.", "stderr tail"));

        var provisioning = new LocalGatewaySetupTests.FakeProvisioner();
        var engine = new LocalGatewaySetupEngine(
            new LocalGatewaySetupOptions { EnableWindowsTrayNodeByDefault = false },
            new LocalGatewaySetupStateStore(statePath),
            new LocalGatewayPreflightProbe(wsl, new LocalGatewaySetupTests.FixedPortProbe(available: true), preflightProbe),
            wsl,
            new LocalGatewaySetupTests.SuccessfulHealthProbe(),
            provisioning,
            provisioning,
            provisioning,
            wslInstanceInstaller: new WslStoreInstanceInstaller(wsl, createDirectory: _ => { }),
            wslInstanceConfigurator: new LocalGatewaySetupTests.FakeWslInstanceConfigurator(),
            openClawLinuxInstaller: new LocalGatewaySetupTests.FakeOpenClawLinuxInstaller(),
            gatewayConfigurationPreparer: new LocalGatewaySetupTests.FakeGatewayConfigurationPreparer(),
            gatewayServiceManager: new LocalGatewaySetupTests.FakeGatewayServiceManager(),
            wslPlatformInstaller: installer);

        var state = await engine.RunLocalOnlyAsync();

        Assert.Equal(LocalGatewaySetupStatus.FailedRetryable, state.Status);
        Assert.Equal("wsl_install_failed", state.FailureCode);
    }

    [Fact]
    public async Task Engine_EnsureWslEnabled_BlocksWithUnavailableCode_WhenNoInstallerInjected()
    {
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var statePath = System.IO.Path.Combine(temp.Path, "setup-state.json");

        var wsl = new LocalGatewaySetupTests.FakeWslCommandRunner
        {
            WslStatusExitCode = -1,
            WslStatusOutput = "The Windows Subsystem for Linux is not installed. https://aka.ms/wslinstall",
        };
        var preflightProbe = new WslPlatformProbe(wsl, fileExists: _ => true, wslExePath: @"C:\Windows\System32\wsl.exe");
        var provisioning = new LocalGatewaySetupTests.FakeProvisioner();
        var engine = new LocalGatewaySetupEngine(
            new LocalGatewaySetupOptions { EnableWindowsTrayNodeByDefault = false },
            new LocalGatewaySetupStateStore(statePath),
            new LocalGatewayPreflightProbe(wsl, new LocalGatewaySetupTests.FixedPortProbe(available: true), preflightProbe),
            wsl,
            new LocalGatewaySetupTests.SuccessfulHealthProbe(),
            provisioning,
            provisioning,
            provisioning,
            wslInstanceInstaller: new WslStoreInstanceInstaller(wsl, createDirectory: _ => { }),
            wslInstanceConfigurator: new LocalGatewaySetupTests.FakeWslInstanceConfigurator(),
            openClawLinuxInstaller: new LocalGatewaySetupTests.FakeOpenClawLinuxInstaller(),
            gatewayConfigurationPreparer: new LocalGatewaySetupTests.FakeGatewayConfigurationPreparer(),
            gatewayServiceManager: new LocalGatewaySetupTests.FakeGatewayServiceManager()
            /* wslPlatformInstaller omitted */);

        var state = await engine.RunLocalOnlyAsync();

        Assert.Equal(LocalGatewaySetupStatus.FailedRetryable, state.Status);
        Assert.Equal("wsl_install_unavailable", state.FailureCode);
    }

    // --- helpers ---

    private sealed class RecordingWslCommandRunner : IWslCommandRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public WslCommandResult StatusResult { get; set; } = new(0, "", "");

        public Task<WslCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? environment = null)
        {
            Calls.Add(arguments);
            return Task.FromResult(StatusResult);
        }
        public Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WslDistroInfo>>(System.Array.Empty<WslDistroInfo>());
        public Task<WslCommandResult> TerminateDistroAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(new WslCommandResult(0, "", ""));
        public Task<WslCommandResult> UnregisterDistroAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(new WslCommandResult(0, "", ""));
        public Task<WslCommandResult> RunInDistroAsync(string name, IReadOnlyList<string> command, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? environment = null)
            => Task.FromResult(new WslCommandResult(0, "", ""));
    }

    private sealed class SequencedPlatformProbe : IWslPlatformProbe
    {
        private readonly Queue<WslPlatformState> _states;

        public SequencedPlatformProbe(params WslPlatformState[] states)
        {
            _states = new Queue<WslPlatformState>(states);
        }

        public Task<WslPlatformProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            var state = _states.Count > 0 ? _states.Dequeue() : WslPlatformState.Installed;
            return Task.FromResult(new WslPlatformProbeResult(state));
        }
    }

    private sealed class FakeWslPlatformInstaller : IWslPlatformInstaller
    {
        private readonly Func<WslPlatformInstallResult> _factory;
        public FakeWslPlatformInstaller(Func<WslPlatformInstallResult> factory) { _factory = factory; }
        public Task<WslPlatformInstallResult> InstallAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_factory());
    }
}
