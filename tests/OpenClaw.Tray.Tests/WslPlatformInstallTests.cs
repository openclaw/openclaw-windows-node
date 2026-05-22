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
        // case. We trust the probe over the exit code here. attempts=1 to
        // disable the finalize-race retry for this test (the retry has its
        // own dedicated tests below).
        var probe = new SequencedPlatformProbe(WslPlatformState.NotInstalled);
        var installer = new ElevatedWslPlatformInstaller(
            probe,
            processRunner: (_, _) => Task.FromResult(0),
            postInstallProbeAttempts: 1);

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

    [Fact]
    public async Task Probe_ReturnsUnknown_WhenStatusCallExceedsTimeout()
    {
        // Simulate wsl --status hanging well past the probe timeout. We must
        // NOT block the wizard for the full 30s engine timeout in this case —
        // the whole point of the fast-detect was to bound it.
        var runner = new HangingWslCommandRunner();
        var probe = new WslPlatformProbe(
            runner,
            fileExists: _ => true,
            wslExePath: @"C:\Windows\System32\wsl.exe",
            statusProbeTimeout: TimeSpan.FromMilliseconds(100));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await probe.ProbeAsync();
        sw.Stop();

        Assert.Equal(WslPlatformState.Unknown, result.State);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Probe should give up promptly; took {sw.Elapsed}");
    }

    [Fact]
    public async Task Preflight_TreatsUnknownPlatformAsBlockingUnavailable()
    {
        // The platform probe returns Unknown (e.g. policy-blocked wsl --status
        // that doesn't match the not-installed banner). The preflight should
        // surface wsl_unavailable as Blocking, NOT silently proceed as if the
        // platform were missing or installed.
        var runner = new LocalGatewaySetupTests.FakeWslCommandRunner
        {
            WslStatusExitCode = 1,
            WslStatusOutput = "Access denied by group policy.",
        };
        var probe = new WslPlatformProbe(runner, fileExists: _ => true, wslExePath: @"C:\Windows\System32\wsl.exe");
        var preflight = new LocalGatewayPreflightProbe(runner, new LocalGatewaySetupTests.FixedPortProbe(available: true), probe);

        var result = await preflight.RunAsync(new LocalGatewaySetupOptions());

        Assert.False(result.CanContinue);
        Assert.Contains(result.Issues, i => i.Code == "wsl_unavailable" && i.Severity == LocalGatewaySetupSeverity.Blocking);
        Assert.DoesNotContain(result.Issues, i => i.Code == "wsl_platform_not_installed");
    }

    [Fact]
    public async Task Installer_RetriesPostProbe_RidingOutFinalizeRace()
    {
        // wsl.exe exits 0 immediately, but the first re-probe still says
        // NotInstalled (Store/lifted-WSL not yet exposed). The installer
        // should retry briefly and pick up the eventual Installed state
        // instead of falsely classifying as RequiresRestart.
        var states = new Queue<WslPlatformState>(
        [
            WslPlatformState.NotInstalled,
            WslPlatformState.NotInstalled,
            WslPlatformState.Installed,
        ]);
        var probe = new QueuedPlatformProbe(states);
        var delayCalls = 0;
        var installer = new ElevatedWslPlatformInstaller(
            probe,
            processRunner: (_, _) => Task.FromResult(0),
            postInstallProbeDelay: TimeSpan.FromMilliseconds(1),
            postInstallProbeAttempts: 6,
            delayAsync: (_, _) => { delayCalls++; return Task.CompletedTask; });

        var result = await installer.InstallAsync();

        Assert.Equal(WslPlatformInstallOutcome.InstalledNoRestart, result.Outcome);
        Assert.Equal(2, delayCalls); // two delays between three probe attempts
    }

    [Fact]
    public async Task Installer_StopsRetryingOnce_AttemptsExhausted()
    {
        var states = new Queue<WslPlatformState>(
            Enumerable.Range(0, 10).Select(_ => WslPlatformState.NotInstalled));
        var probe = new QueuedPlatformProbe(states);
        var installer = new ElevatedWslPlatformInstaller(
            probe,
            processRunner: (_, _) => Task.FromResult(0),
            postInstallProbeDelay: TimeSpan.FromMilliseconds(1),
            postInstallProbeAttempts: 3,
            delayAsync: (_, _) => Task.CompletedTask);

        var result = await installer.InstallAsync();

        // Exit 0 + still NotInstalled after retries → restart required.
        Assert.Equal(WslPlatformInstallOutcome.InstalledRequiresRestart, result.Outcome);
    }

    [Fact]
    public async Task Installer_ReturnsFailed_WhenProcessRunnerThrowsGenericException()
    {
        var probe = new SequencedPlatformProbe(WslPlatformState.NotInstalled);
        var installer = new ElevatedWslPlatformInstaller(
            probe,
            processRunner: (_, _) => throw new InvalidOperationException("boom"));

        var result = await installer.InstallAsync();

        Assert.Equal(WslPlatformInstallOutcome.Failed, result.Outcome);
        Assert.Contains("Unexpected error", result.ErrorMessage);
    }

    [Fact]
    public async Task Installer_PropagatesCancellation()
    {
        var probe = new SequencedPlatformProbe(WslPlatformState.NotInstalled);
        var installer = new ElevatedWslPlatformInstaller(
            probe,
            processRunner: (_, ct) => Task.FromException<int>(new OperationCanceledException(ct)));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => installer.InstallAsync(cts.Token));
    }

    [Fact]
    public async Task Engine_EnsureWslEnabled_RequiresRestart_DoesNotPaintCheckSystemAsFailure()
    {
        // Regression for Hanselman review H1: the previous implementation
        // set Phase=Failed for the RequiresRestart outcome, which makes
        // LocalSetupProgressStageMap render the "Check system" stage row as
        // a hard failure even though the user just needs to reboot. The
        // fix mirrors the preflight RequiresRestart pattern: keep Phase at
        // EnsureWslEnabled, only flip Status.
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
        Assert.Equal(LocalGatewaySetupPhase.EnsureWslEnabled, state.Phase);
        Assert.DoesNotContain(state.Issues, i => i.Severity == LocalGatewaySetupSeverity.Blocking);
        Assert.Equal("wsl_install_requires_restart", state.FailureCode);

        // The H1 regression we are guarding: ComputeStageState for the first
        // visible stage (CheckSystem) must not return Active under
        // RequiresRestart, otherwise the wizard renders an infinite spinner.
        // The fix in LocalSetupProgressStageMap pins it to Failed so the user
        // sees a clear "something needs your attention" marker, with the
        // reboot instruction surfaced via ShouldShowErrorRow.
        var firstStage = OpenClawTray.Onboarding.Services.LocalSetupProgressStageMap.VisibleStages[0];
        var stageState = OpenClawTray.Onboarding.Services.LocalSetupProgressStageMap.ComputeStageState(
            firstStage.Phases, state.Phase, state.Status, state.Phase);
        Assert.NotEqual(OpenClawTray.Onboarding.Services.LocalSetupProgressStageMap.StageState.Active, stageState);
        Assert.True(OpenClawTray.Onboarding.Services.LocalSetupProgressStageMap.ShouldShowErrorRow(state.Status));
        Assert.False(OpenClawTray.Onboarding.Services.LocalSetupProgressStageMap.ShouldShowRetryButton(state.Status));
    }

    [Fact]
    public async Task Engine_ResumesAfterReboot_WhenPersistedStateIsWslInstallRequiresRestart()
    {
        // Regression for Hanselman review H1 / M4: after a previous run ended
        // in RequiresRestart, the persisted setup-state.json must not freeze
        // the wizard. The engine should reset Status→Pending on entry so the
        // post-reboot re-launch actually re-runs preflight and continues.
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var statePath = System.IO.Path.Combine(temp.Path, "setup-state.json");

        // Seed a stale persisted state representing the prior RequiresRestart run.
        var seed = LocalGatewaySetupState.Create(new LocalGatewaySetupOptions());
        seed.Status = LocalGatewaySetupStatus.RequiresRestart;
        seed.Phase = LocalGatewaySetupPhase.EnsureWslEnabled;
        seed.FailureCode = "wsl_install_requires_restart";
        seed.UserMessage = "Restart required.";
        await new LocalGatewaySetupStateStore(statePath).SaveAsync(seed);

        // After "reboot": wsl is now installed, no installer call needed.
        var wsl = new LocalGatewaySetupTests.FakeWslCommandRunner
        {
            WslStatusExitCode = 0,
            WslStatusOutput = "Default Version: 2\nWSL version: 2.1.5.0",
        };
        var preflightProbe = new WslPlatformProbe(wsl, fileExists: _ => true, wslExePath: @"C:\Windows\System32\wsl.exe");
        var installerCalls = 0;
        var installer = new FakeWslPlatformInstaller(() =>
        {
            installerCalls++;
            return new WslPlatformInstallResult(WslPlatformInstallOutcome.InstalledNoRestart, 0);
        });
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

        Assert.NotEqual(LocalGatewaySetupStatus.RequiresRestart, state.Status);
        Assert.Equal(0, installerCalls); // platform is now installed; no need to invoke installer again
        Assert.Equal(LocalGatewaySetupStatus.Complete, state.Status);
    }

    [Fact]
    public async Task Preflight_TreatsProbeTimeoutAsBlockingUnavailable_WithoutFallingBackToLongRunner()
    {
        // Regression for Hanselman round 2: when WslPlatformProbe times out
        // (Unknown + null StatusResult), the preflight must NOT fall back to
        // _wsl.RunAsync(["--status"]) — that runner inherits the engine's
        // 30s default timeout, undoing the whole point of the probe's 5s
        // fast-fail.
        var runner = new HangingWslCommandRunner();
        var probe = new WslPlatformProbe(
            runner,
            fileExists: _ => true,
            wslExePath: @"C:\Windows\System32\wsl.exe",
            statusProbeTimeout: TimeSpan.FromMilliseconds(50));
        var preflight = new LocalGatewayPreflightProbe(runner, new LocalGatewaySetupTests.FixedPortProbe(available: true), probe);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await preflight.RunAsync(new LocalGatewaySetupOptions());
        sw.Stop();

        Assert.False(result.CanContinue);
        Assert.Contains(result.Issues, i => i.Code == "wsl_unavailable" && i.Severity == LocalGatewaySetupSeverity.Blocking);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Preflight should not re-invoke wsl --status with the long runner timeout; took {sw.Elapsed}");
    }

    [Fact]
    public async Task Engine_ResumesAfterReboot_EvenWhenPersistedStateHasNoFailureCode()
    {
        // Forward-compat coverage: today only the EnsureWslEnabled path
        // produces Status=RequiresRestart (and it sets FailureCode), and the
        // preflight RequiresRestart branch is unreachable from current code.
        // The self-heal is keyed off Status alone so any future producer
        // that surfaces RequiresRestart without a FailureCode (e.g. the
        // preflight branch, or a new gateway-install reboot path) won't
        // permanently brick the wizard.
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var statePath = System.IO.Path.Combine(temp.Path, "setup-state.json");

        var seed = LocalGatewaySetupState.Create(new LocalGatewaySetupOptions());
        seed.Status = LocalGatewaySetupStatus.RequiresRestart;
        seed.Phase = LocalGatewaySetupPhase.Preflight;
        seed.FailureCode = null;   // preflight-style RequiresRestart never sets FailureCode
        seed.UserMessage = "Restart required.";
        await new LocalGatewaySetupStateStore(statePath).SaveAsync(seed);

        var wsl = new LocalGatewaySetupTests.FakeWslCommandRunner
        {
            WslStatusExitCode = 0,
            WslStatusOutput = "Default Version: 2\nWSL version: 2.1.5.0",
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
            gatewayServiceManager: new LocalGatewaySetupTests.FakeGatewayServiceManager());

        var state = await engine.RunLocalOnlyAsync();

        Assert.NotEqual(LocalGatewaySetupStatus.RequiresRestart, state.Status);
        Assert.Equal(LocalGatewaySetupStatus.Complete, state.Status);
    }

    [Fact]
    public async Task Installer_ClampsNonSensicalAttemptsAndDelay()
    {
        // Round-3 M-3 / L-1: the test must force the retry loop to actually
        // execute the bad delay, otherwise it cannot distinguish clamped vs
        // unclamped behavior. Probe sequence forces two delays between three
        // probes; with the unclamped negative TimeSpan, Task.Delay throws
        // ArgumentOutOfRangeException. With the clamp, delay degrades to
        // TimeSpan.Zero and the installer completes happily.
        var states = new Queue<WslPlatformState>(
        [
            WslPlatformState.NotInstalled,
            WslPlatformState.NotInstalled,
            WslPlatformState.Installed,
        ]);
        var probe = new QueuedPlatformProbe(states);
        var installer = new ElevatedWslPlatformInstaller(
            probe,
            processRunner: (_, _) => Task.FromResult(0),
            postInstallProbeAttempts: 6,
            postInstallProbeDelay: TimeSpan.FromMilliseconds(-100));
            // delayAsync deliberately defaulted to real Task.Delay so the
            // clamp's behavior matters.
        var result = await installer.InstallAsync();
        Assert.Equal(WslPlatformInstallOutcome.InstalledNoRestart, result.Outcome);
    }

    [Fact]
    public async Task Installer_RemapsUnknownProbeToRequiresRestart_WhenExitCodeIsZero()
    {
        // Round-3 Codex MEDIUM (revised in Round-2 of WSLInstall1 review):
        // exit=0 with the post-install probe still returning Unknown after
        // exhausting retries means the install succeeded but the lifted-WSL
        // service is racing warmup. RequiresRestart is closer to ground
        // truth than Failed (the documented Microsoft fix is to reboot,
        // not to re-launch UAC for another install).
        var probe = new SequencedPlatformProbe(WslPlatformState.Unknown);
        var installer = new ElevatedWslPlatformInstaller(
            probe,
            processRunner: (_, _) => Task.FromResult(0),
            postInstallProbeAttempts: 1);

        var result = await installer.InstallAsync();

        Assert.Equal(WslPlatformInstallOutcome.InstalledRequiresRestart, result.Outcome);
    }

    [Fact]
    public async Task Engine_SelfHeal_ResetsCancelledStatus()
    {
        // Pins #5: a previous run that ended in Status=Cancelled (e.g. user
        // closed the wizard mid-install, or pressed Cancel during UAC) must
        // self-heal on re-entry so the next "Set up locally" click doesn't
        // short-circuit out of every phase via RunPhaseAsync's
        // `Status is Pending or Running` guard. Without this the user sees
        // an instant "nothing happened" with no error card, which is worse
        // than the original failure because there's no actionable hint.
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var statePath = System.IO.Path.Combine(temp.Path, "setup-state.json");

        var seed = LocalGatewaySetupState.Create(new LocalGatewaySetupOptions());
        seed.Status = LocalGatewaySetupStatus.Cancelled;
        seed.Phase = LocalGatewaySetupPhase.ConfigureWslInstance;
        seed.UserMessage = "Cancelled by user.";
        await new LocalGatewaySetupStateStore(statePath).SaveAsync(seed);

        var wsl = new LocalGatewaySetupTests.FakeWslCommandRunner
        {
            WslStatusExitCode = 0,
            WslStatusOutput = "Default Version: 2\nWSL version: 2.1.5.0",
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
            gatewayServiceManager: new LocalGatewaySetupTests.FakeGatewayServiceManager());

        var state = await engine.RunLocalOnlyAsync();

        Assert.Equal(LocalGatewaySetupStatus.Complete, state.Status);
    }

    [Fact]
    public async Task Engine_ConfigureFails_AfterJustInstalled_DoesNotRemapUnrelatedFailure()
    {
        // Pins #4: when the WSL platform was just installed in this session
        // (InstalledNoRestart outcome) AND a later configure step fails for
        // an unrelated reason (e.g. apt repo down, script bug, distro disk
        // full) — the failure must NOT be remapped to
        // "wsl_firstboot_config_failed_after_install" because telling the
        // user to reboot won't fix anything that doesn't look like a WSL
        // kernel issue. We keep the original failure code so the
        // localization layer can show the correct message.
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var statePath = System.IO.Path.Combine(temp.Path, "setup-state.json");

        var wsl = new LocalGatewaySetupTests.FakeWslCommandRunner
        {
            // First preflight: wsl --status reports not-installed; probe
            // says NotInstalled and the engine drives the WSL install path.
            WslStatusExitCode = -1,
            WslStatusOutput = "The Windows Subsystem for Linux is not installed. https://aka.ms/wslinstall",
        };
        var preflightProbe = new WslPlatformProbe(wsl, fileExists: _ => true, wslExePath: @"C:\Windows\System32\wsl.exe");
        var installerCalls = 0;
        var installer = new FakeWslPlatformInstaller(() =>
        {
            installerCalls++;
            // After install succeeds, flip the runner so subsequent preflight
            // / configure probes see WSL as installed and healthy.
            wsl.WslStatusExitCode = 0;
            wsl.WslStatusOutput = "Default Version: 2\nWSL version: 2.1.5.0";
            return new WslPlatformInstallResult(WslPlatformInstallOutcome.InstalledNoRestart, 0);
        });
        var failingConfigurator = new FailingConfigurator(
            new WslInstanceConfigurationResult(
                Success: false,
                ErrorCode: "apt_repository_unreachable",
                ErrorMessage: "apt-get update failed: temporary failure resolving archive.ubuntu.com",
                Detail: "exit=100; stderr=W: Failed to fetch http://archive.ubuntu.com/ubuntu/dists/noble/main; stdout="));
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
            wslInstanceConfigurator: failingConfigurator,
            openClawLinuxInstaller: new LocalGatewaySetupTests.FakeOpenClawLinuxInstaller(),
            gatewayConfigurationPreparer: new LocalGatewaySetupTests.FakeGatewayConfigurationPreparer(),
            gatewayServiceManager: new LocalGatewaySetupTests.FakeGatewayServiceManager(),
            wslPlatformInstaller: installer);

        var state = await engine.RunLocalOnlyAsync();

        Assert.Equal(1, installerCalls);
        Assert.NotEqual(LocalGatewaySetupStatus.Complete, state.Status);
        Assert.Equal("apt_repository_unreachable", state.FailureCode);
        Assert.DoesNotContain("restart your PC", state.UserMessage ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LooksLikePostInstallKernelIssue_MatchesKnownKernelSignatures()
    {
        // Pins #4: the gate that decides whether to remap a configure-phase
        // failure to "restart your PC after WSL install" must positively
        // match the known WSL-kernel-y signatures and negatively reject
        // unrelated configure errors.
        Assert.True(LocalGatewaySetupEngine.LooksLikePostInstallKernelIssue("exit=4294967295; stderr=Error: 0x80370102 The virtual machine could not be started; stdout="));
        Assert.True(LocalGatewaySetupEngine.LooksLikePostInstallKernelIssue("WslRegisterDistribution failed with error: 0x80004002"));
        Assert.True(LocalGatewaySetupEngine.LooksLikePostInstallKernelIssue("Error: 0x800401f0  Class not registered"));
        Assert.True(LocalGatewaySetupEngine.LooksLikePostInstallKernelIssue("WSL2 requires an update to its kernel component"));
        // Round-2 fix: blank Detail must NOT default to true (was a permissive
        // catch-all that re-introduced the false-positive the gate is meant
        // to eliminate).
        Assert.False(LocalGatewaySetupEngine.LooksLikePostInstallKernelIssue(null));
        Assert.False(LocalGatewaySetupEngine.LooksLikePostInstallKernelIssue(""));
        // Bare substring "kernel" is too weak on its own (matches "kernel
        // panic in user script", etc). Require kernel-component context.
        Assert.False(LocalGatewaySetupEngine.LooksLikePostInstallKernelIssue("kernel panic in custom init script"));

        Assert.False(LocalGatewaySetupEngine.LooksLikePostInstallKernelIssue("exit=100; stderr=E: Unable to fetch some archives; stdout="));
        Assert.False(LocalGatewaySetupEngine.LooksLikePostInstallKernelIssue("bash: line 12: syntax error near unexpected token"));
        Assert.False(LocalGatewaySetupEngine.LooksLikePostInstallKernelIssue("Permission denied: /etc/openclaw/config"));

        // Round-3 overload: when postFreshInstall=true (caller already
        // knows WSL was just installed in this session), blank Detail
        // should default to true. Many real WslRegisterDistribution
        // HRESULTs / host-compute warmup errors write to the Windows
        // event log, not stderr — so collected Detail is often empty.
        Assert.True(LocalGatewaySetupEngine.LooksLikePostInstallKernelIssue(null, postFreshInstall: true));
        Assert.True(LocalGatewaySetupEngine.LooksLikePostInstallKernelIssue("", postFreshInstall: true));
        Assert.True(LocalGatewaySetupEngine.LooksLikePostInstallKernelIssue("   ", postFreshInstall: true));
        // But postFreshInstall=true must still reject confirmed-unrelated
        // signatures — strict signature wins over the post-install default.
        Assert.False(LocalGatewaySetupEngine.LooksLikePostInstallKernelIssue("bash: syntax error", postFreshInstall: true));
        Assert.False(LocalGatewaySetupEngine.LooksLikePostInstallKernelIssue("E: Unable to fetch some archives", postFreshInstall: true));
    }

    private sealed class FailingConfigurator : IWslInstanceConfigurator
    {
        private readonly WslInstanceConfigurationResult _result;
        public FailingConfigurator(WslInstanceConfigurationResult result) { _result = result; }
        public Task<WslInstanceConfigurationResult> ConfigureAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
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

    private sealed class HangingWslCommandRunner : IWslCommandRunner
    {
        public async Task<WslCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? environment = null)
        {
            // Wait until cancellation; throws when the probe's timeout CTS fires.
            await Task.Delay(TimeSpan.FromMinutes(10), cancellationToken);
            return new WslCommandResult(0, "", "");
        }
        public async Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default)
        {
            // Also hang. The point of the round-3 H-1 fix is that preflight
            // must NOT call ListDistrosAsync when the platform probe couldn't
            // confirm WSL is healthy. If the fix regresses, this fake will
            // never return and the preflight test will hit its 3s elapsed
            // assertion, exposing the regression.
            await Task.Delay(TimeSpan.FromMinutes(10), cancellationToken);
            return System.Array.Empty<WslDistroInfo>();
        }
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

    private sealed class QueuedPlatformProbe : IWslPlatformProbe
    {
        private readonly Queue<WslPlatformState> _states;

        public QueuedPlatformProbe(Queue<WslPlatformState> states) { _states = states; }

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
