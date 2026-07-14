namespace OpenClaw.SetupEngine.Tests;

public class NativeGatewayStepsTests
{
    [Fact]
    public async Task ManagedNativeGatewayController_RunsOwnedTaskWithManagedCliEnvironment()
    {
        var root = Path.Combine(Path.GetTempPath(), $"native-control-{Guid.NewGuid():N}");
        var profile = $"OpenClawGateway-Test-{Guid.NewGuid():N}";
        var config = new SetupConfig
        {
            InstallMode = GatewayInstallMode.NativeWindows,
            DistroName = profile,
        };
        Directory.CreateDirectory(root);
        var cliPrefix = GatewayCliRunner.GetManagedNativeCliPrefix(root);
        Directory.CreateDirectory(cliPrefix);
        await File.WriteAllTextAsync(Path.Combine(cliPrefix, "openclaw.ps1"), "# test shim");
        var marker = $$"""{"InstallMode":"NativeWindows","ProfileName":"{{profile}}","TaskName":"OpenClaw Gateway ({{profile}})","ManagedConfigPaths":[]}""";
        await File.WriteAllTextAsync(GatewayInstallModeDetector.GetNativeOwnershipPath(root), marker);
        await File.WriteAllTextAsync(GatewayInstallModeDetector.GetNativeProfileOwnershipPath(root), marker);
        var runner = new CapturingCommandRunner(
            new CommandResult(0, """{"service":{"runtime":{"status":"running"}}}""", "", TimeSpan.Zero, false));
        var controller = new ManagedNativeGatewayController(root, root, commandRunner: runner);

        try
        {
            var result = await controller.RunAsync($"OpenClaw Gateway ({profile})", NativeGatewayControlAction.Status);

            Assert.True(result.Success);
            Assert.True(result.IsRunning);
            Assert.Equal("powershell.exe", runner.Executable);
            Assert.Contains(runner.Arguments, argument => argument.Contains("gateway", StringComparison.Ordinal));
            Assert.Contains(runner.Arguments, argument => argument.Contains("status", StringComparison.Ordinal));
            Assert.Equal($"OpenClaw Gateway ({profile})", runner.Environment["OPENCLAW_WINDOWS_TASK_NAME"]);
            Assert.Equal(profile, runner.Environment["OPENCLAW_PROFILE"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ManagedNativeGatewayController_OnlyStatusRequestsJson()
    {
        Assert.Equal(["gateway", "status", "--json"],
            ManagedNativeGatewayController.BuildGatewayArguments(NativeGatewayControlAction.Status));
        Assert.Equal(["gateway", "start"],
            ManagedNativeGatewayController.BuildGatewayArguments(NativeGatewayControlAction.Start));
        Assert.Equal(["gateway", "stop"],
            ManagedNativeGatewayController.BuildGatewayArguments(NativeGatewayControlAction.Stop));
        Assert.Equal(["gateway", "restart"],
            ManagedNativeGatewayController.BuildGatewayArguments(NativeGatewayControlAction.Restart));
    }

    [Fact]
    public async Task ManagedNativeGatewayController_RefusesUnownedTask()
    {
        var root = Path.Combine(Path.GetTempPath(), $"native-control-unowned-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var runner = new CapturingCommandRunner(
            new CommandResult(0, """{"service":{"runtime":{"status":"running"}}}""", "", TimeSpan.Zero, false));
        var controller = new ManagedNativeGatewayController(root, root, commandRunner: runner);

        try
        {
            var result = await controller.RunAsync("OpenClaw Gateway (unowned)", NativeGatewayControlAction.Start);

            Assert.False(result.Success);
            Assert.Contains("not owned", result.StandardError);
            Assert.Null(runner.Executable);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BeginNativeInstall_RecordsIntentBeforeCleanupAndRestoresPriorMarkers(
        bool markerExisted,
        bool profileMarkerExisted)
    {
        var root = Path.Combine(Path.GetTempPath(), $"native-intent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var markerPath = GatewayInstallModeDetector.GetNativeOwnershipPath(root);
        var profileMarkerPath = GatewayInstallModeDetector.GetNativeProfileOwnershipPath(root);
        var profile = $"OpenClawGateway-Test-{Guid.NewGuid():N}";
        var previous = System.Text.Encoding.UTF8.GetBytes(
            $$"""{"InstallMode":"NativeWindows","ProfileName":"{{profile}}","TaskName":"OpenClaw Gateway ({{profile}})","ManagedConfigPaths":["gateway.previous"]}""");
        var previousProfile = System.Text.Encoding.UTF8.GetBytes(
            $$"""{"InstallMode":"NativeWindows","ProfileName":"{{profile}}","TaskName":"OpenClaw Gateway ({{profile}})","ManagedConfigPaths":["gateway.profile"]}""");
        if (markerExisted)
            await File.WriteAllBytesAsync(markerPath, previous);
        if (profileMarkerExisted)
            await File.WriteAllBytesAsync(profileMarkerPath, previousProfile);

        try
        {
            var config = new SetupConfig
            {
                InstallMode = GatewayInstallMode.NativeWindows,
                DistroName = profile,
            };
            var logger = new SetupLogger(filePath: null, LogLevel.Trace);
            var ctx = new SetupContext(
                config,
                logger,
                new TransactionJournal(filePath: null),
                new CommandRunner(logger),
                CancellationToken.None,
                dataDir: root,
                localDataDir: root);
            var step = new BeginNativeGatewayInstallStep();

            Assert.True((await step.ExecuteAsync(ctx, CancellationToken.None)).IsSuccess);
            Assert.True(File.Exists(markerPath));
            var intentPaths = ConfigureGatewayStep.ReadNativeManagedConfigPaths(root, config);
            if (markerExisted)
                Assert.Contains("gateway.previous", intentPaths);
            if (profileMarkerExisted)
                Assert.Contains("gateway.profile", intentPaths);
            if (!markerExisted && !profileMarkerExisted)
                Assert.Empty(intentPaths);

            await step.RollbackAsync(ctx, CancellationToken.None);

            if (markerExisted)
                Assert.Equal(previous, await File.ReadAllBytesAsync(markerPath));
            else
                Assert.False(File.Exists(markerPath));
            if (profileMarkerExisted)
                Assert.Equal(previousProfile, await File.ReadAllBytesAsync(profileMarkerPath));
            else
                Assert.False(File.Exists(profileMarkerPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

    }

    [Fact]
    public async Task BeginNativeInstall_RollbackPreservesOwnershipWhenManagedProfileRemains()
    {
        var root = Path.Combine(Path.GetTempPath(), $"native-intent-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var config = new SetupConfig
        {
            InstallMode = GatewayInstallMode.NativeWindows,
            DistroName = $"OpenClawGateway-Test-{Guid.NewGuid():N}",
        };
        var stateDirectory = GatewayCliRunner.GetManagedNativeStateDir(config);
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        var ctx = new SetupContext(
            config,
            logger,
            new TransactionJournal(filePath: null),
            new CommandRunner(logger),
            CancellationToken.None,
            dataDir: root,
            localDataDir: root);
        var step = new BeginNativeGatewayInstallStep();

        try
        {
            Assert.True((await step.ExecuteAsync(ctx, CancellationToken.None)).IsSuccess);
            Directory.CreateDirectory(stateDirectory);
            await File.WriteAllTextAsync(Path.Combine(stateDirectory, "openclaw.json"), "{}");

            await step.RollbackAsync(ctx, CancellationToken.None);

            Assert.False(GatewayInstallModeDetector.HasNativeOwnershipMarker(root));
            Assert.True(GatewayInstallModeDetector.IsNativeProfileOwnershipMarkerOwned(root, config));
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
                Directory.Delete(stateDirectory, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BeginNativeInstall_RefusesUnownedProfileCollision()
    {
        var profile = $"OpenClawGateway-Collision-{Guid.NewGuid():N}";
        var config = new SetupConfig
        {
            InstallMode = GatewayInstallMode.NativeWindows,
            DistroName = profile,
        };
        var stateDirectory = GatewayCliRunner.GetManagedNativeStateDir(config);
        Directory.CreateDirectory(stateDirectory);
        var root = Path.Combine(Path.GetTempPath(), $"native-collision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var logger = new SetupLogger(filePath: null, LogLevel.Trace);
            var ctx = new SetupContext(
                config,
                logger,
                new TransactionJournal(filePath: null),
                new CommandRunner(logger),
                CancellationToken.None,
                dataDir: root,
                localDataDir: root);

            var result = await new BeginNativeGatewayInstallStep().ExecuteAsync(ctx, CancellationToken.None);

            Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
            Assert.Contains("without OpenClaw Companion ownership", result.Message);
            Assert.False(GatewayInstallModeDetector.HasNativeOwnershipMarker(root));
            Assert.False(GatewayInstallModeDetector.HasNativeProfileOwnershipMarker(root));
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildInstallScript_UsesHttpsInstallerAndSkipsNestedOnboarding()
    {
        var script = InstallNativeCliStep.BuildInstallScript(
            "https://openclaw.ai/install.ps1",
            "2026.6.11");

        Assert.Contains("Invoke-WebRequest -UseBasicParsing -Uri 'https://openclaw.ai/install.ps1'", script);
        Assert.Contains("[Text.Encoding]::UTF8.GetString($content)", script);
        Assert.Contains("-NoOnboard -Tag '2026.6.11'", script);
        Assert.Contains("$env:NPM_CONFIG_PREFIX = $prefix", script);
        Assert.Contains("[Environment]::GetEnvironmentVariable('Path', 'Machine')", script);
        Assert.Contains("Join-Path $env:ProgramFiles 'nodejs'", script);
        Assert.Contains("$sqliteProbe =", script);
        // Missing probe is best-effort: proceed unpatched rather than fail loudly,
        // so a newer `node -v` installer that drops the probe cannot break setup.
        Assert.Contains("proceeding without the Windows PowerShell 5 quoting workaround", script);
        Assert.DoesNotContain("OpenClaw installer SQLite probe shape changed", script);
        Assert.DoesNotContain("throw 'OpenClaw installer SQLite probe", script);
        Assert.Contains(@"require(\""node:sqlite\"")", script);
        Assert.Contains("$gitWrapperBytes", script);
        Assert.Contains("[IO.File]::WriteAllBytes($gitWrapper, $gitWrapperBytes)", script);
        Assert.Contains("SetEnvironmentVariable('Path', $filteredUserPath, 'User')", script);
        Assert.DoesNotContain("Get-Command openclaw", script);
        Assert.Contains("OPENCLAW_CLI_PATH=", script);
        Assert.DoesNotContain("iwr -useb", script);
    }

    [Theory]
    [InlineData("2026.6.11", "2026.6.11", true)]
    [InlineData("v2026.6.11\r\n", "2026.6.11", true)]
    [InlineData("OpenClaw 2026.6.11 (abc123)", "2026.6.11", true)]
    [InlineData("2026.6.10", "2026.6.11", false)]
    public void MatchesRequestedVersion_RequiresInstalledPinnedVersion(
        string output,
        string requested,
        bool expected)
    {
        Assert.Equal(expected, InstallNativeCliStep.MatchesRequestedVersion(output, requested));
    }

    [Theory]
    [InlineData("2026.6.11", true)]
    [InlineData("v2026.6.11-beta.1", true)]
    [InlineData("latest", false)]
    [InlineData("beta", false)]
    [InlineData("openclaw@beta", false)]
    [InlineData("github:openclaw/openclaw", false)]
    public void IsExactVersionRequest_DistinguishesPinnedVersionsFromInstallerTags(
        string requested,
        bool expected)
    {
        Assert.Equal(expected, InstallNativeCliStep.IsExactVersionRequest(requested));
    }

    [Fact]
    public void EncodeInstallScript_UsesPowerShellUtf16Encoding()
    {
        const string script = "Write-Output 'OPENCLAW_CLI_PATH=C:\\\\OpenClaw\\\\openclaw.cmd'";

        var encoded = InstallNativeCliStep.EncodePowerShellScript(script);

        Assert.Equal(script, System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded)));
    }

    [Fact]
    public void PreferPowerShellShim_AvoidsCmdJsonArgumentLoss()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"openclaw-cli-shim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var cmd = Path.Combine(directory, "openclaw.cmd");
            var powerShell = Path.Combine(directory, "openclaw.ps1");
            File.WriteAllText(cmd, "@echo off");
            File.WriteAllText(powerShell, "& node @args");

            Assert.Equal(powerShell, GatewayCliRunner.PreferPowerShellShim(cmd));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ResolveNativeCliPath_FindsCustomNpmPrefix()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"openclaw-npm-prefix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var powerShell = Path.Combine(directory, "openclaw.ps1");
            File.WriteAllText(powerShell, "& node @args");

            Assert.Equal(powerShell, GatewayCliRunner.TryResolveNativeCliPathFromNpmPrefix(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ManagedNativeCliPrefix_IsIsolatedUnderLocalAppData()
    {
        var localDataDir = Path.Combine("C:\\Users\\me\\AppData\\Local", "OpenClawTray");

        var prefix = GatewayCliRunner.GetManagedNativeCliPrefix(localDataDir);

        Assert.Equal(Path.Combine(localDataDir, "native-cli"), prefix);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallNativeCli_RollbackOnlyDeletesManagedCliDuringUninstall(bool uninstalling)
    {
        var root = Path.Combine(Path.GetTempPath(), $"native-cli-rollback-{Guid.NewGuid():N}");
        var localDataDir = Path.Combine(root, "local");
        var prefix = GatewayCliRunner.GetManagedNativeCliPrefix(localDataDir);
        Directory.CreateDirectory(prefix);
        await File.WriteAllTextAsync(Path.Combine(prefix, "openclaw.ps1"), "& node @args");
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        var ctx = new SetupContext(
            new SetupConfig { InstallMode = GatewayInstallMode.NativeWindows },
            logger,
            new TransactionJournal(filePath: null),
            new CommandRunner(logger),
            CancellationToken.None,
            dataDir: Path.Combine(root, "data"),
            localDataDir: localDataDir)
        {
            IsUninstalling = uninstalling,
        };

        try
        {
            await new InstallNativeCliStep().RollbackAsync(ctx, CancellationToken.None);

            Assert.Equal(!uninstalling, Directory.Exists(prefix));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MergeNativePaths_PrefersFreshMachineAndUserEntriesWithoutDuplicates()
    {
        var merged = GatewayCliRunner.MergeNativePaths(
            @"C:\Windows;C:\Program Files\nodejs",
            @"C:\Users\me\AppData\Roaming\npm",
            @"C:\WINDOWS;C:\legacy");

        Assert.Equal(
            @"C:\Windows;C:\Program Files\nodejs;C:\Users\me\AppData\Roaming\npm;C:\legacy",
            merged);
    }

    [Fact]
    public void BuildInstallScript_RejectsVersionWithNewline()
    {
        Assert.Throws<ArgumentException>(() => InstallNativeCliStep.BuildInstallScript(
            "https://openclaw.ai/install.ps1",
            "latest\nmalicious"));
    }

    [Fact]
    public void NativeReviewSummary_DescribesWindowsScheduledTask()
    {
        var config = new SetupConfig
        {
            InstallMode = GatewayInstallMode.NativeWindows,
            GatewayPort = 19876,
        };
        config.Gateway.Version = "2026.6.11";

        var summary = SetupReviewSummaryBuilder.Build(config, "C:\\TrayData");

        Assert.Equal("Install directly on Windows", summary.DistroTitle);
        Assert.Contains("Windows Scheduled Task", summary.ExactCommands);
        Assert.Contains("19876", summary.GatewayEndpoint);
        Assert.StartsWith("Windows native", summary.CompletionGatewaySummary);
    }

    [Theory]
    [InlineData("OpenClawGateway", 18789)]
    [InlineData("OpenClawGateway-Dev", 18790)]
    public void NativeGatewayIdentity_UsesAppOwnedProfile(string profile, int port)
    {
        var config = new SetupConfig
        {
            InstallMode = GatewayInstallMode.NativeWindows,
            DistroName = profile,
            GatewayPort = port,
        };

        var environment = GatewayCliRunner.GetManagedNativeEnvironmentDefaults(config);

        Assert.Equal(profile, environment["OPENCLAW_PROFILE"]);
        Assert.Equal($"OpenClaw Gateway ({profile})", environment["OPENCLAW_WINDOWS_TASK_NAME"]);
        Assert.EndsWith($".openclaw-{profile}", environment["OPENCLAW_STATE_DIR"]);
        Assert.EndsWith(
            Path.Combine($".openclaw-{profile}", "openclaw.json"),
            environment["OPENCLAW_CONFIG_PATH"]);
        Assert.EndsWith(
            Path.Combine($".openclaw-{profile}", "logs", "wizard-console.log"),
            GatewayInstallModeDetector.GetNativeWizardLogPath(config));
    }

    [Fact]
    public void NativeGatewayIdentity_NeverUsesSharedDefaultProfile()
    {
        var config = new SetupConfig
        {
            InstallMode = GatewayInstallMode.NativeWindows,
            DistroName = "default",
            GatewayPort = 18789,
        };

        Assert.Equal("companion-18789", GatewayCliRunner.GetManagedNativeProfile(config));
        Assert.EndsWith(".openclaw-companion-18789", GatewayCliRunner.GetManagedNativeStateDir(config));
        Assert.NotEqual(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw"),
            GatewayCliRunner.GetManagedNativeStateDir(config));
    }

    [Theory]
    [InlineData("{\"service\":{\"loaded\":true,\"command\":null}}", true)]
    [InlineData("{\"service\":{\"loaded\":false,\"command\":{\"programArguments\":[]}}}", true)]
    [InlineData("{\"service\":{\"loaded\":false,\"command\":null}}", false)]
    [InlineData("not json", false)]
    public void IsServiceInstalled_DetectsLoadedOrConfiguredService(string json, bool expected)
    {
        Assert.Equal(expected, NativeGatewayServiceCleanupStep.IsServiceInstalled(json));
    }

    [Theory]
    [InlineData("running", true)]
    [InlineData("stopped", false)]
    [InlineData("Ready", false)]
    public void IsServiceRunning_UsesRuntimeStatus(string status, bool expected)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            service = new { runtime = new { status } }
        });

        Assert.Equal(expected, NativeGatewayServiceCleanupStep.IsServiceRunning(json));
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.OK, true)]
    [InlineData(System.Net.HttpStatusCode.Unauthorized, false)]
    [InlineData(System.Net.HttpStatusCode.Forbidden, false)]
    [InlineData(System.Net.HttpStatusCode.NotFound, false)]
    [InlineData(System.Net.HttpStatusCode.InternalServerError, false)]
    public void IsHealthyHttpStatus_RejectsUnrelatedOrFailingResponses(
        System.Net.HttpStatusCode statusCode,
        bool expected)
    {
        Assert.Equal(expected, StartGatewayStep.IsHealthyHttpStatus(statusCode));
    }

    [Fact]
    public void NativeHealthUri_UsesDedicatedLivenessEndpoint()
    {
        Assert.Equal("http://127.0.0.1:18789/healthz", StartGatewayStep.GetNativeHealthUri(18789).AbsoluteUri);
    }

    [Fact]
    public async Task NativeReadiness_WaitsForInstallLaunchedGatewayWithoutStartingAgain()
    {
        var config = new SetupConfig
        {
            InstallMode = GatewayInstallMode.NativeWindows,
            DistroName = $"OpenClawGateway-Test-{Guid.NewGuid():N}",
            GatewayPort = 18789,
        };
        using var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        var runner = new RecordingCommandRunner(
            new CommandResult(
                0,
                BuildOwnedNativeStatusJson(config),
                "",
                TimeSpan.Zero,
                false));
        var ctx = new SetupContext(
            config,
            logger,
            new TransactionJournal(filePath: null),
            runner,
            CancellationToken.None);
        ctx.NativeCliPath = @"C:\test\openclaw.ps1";

        var result = await StartGatewayStep.WaitForNativeGatewayReadyAfterInstallAsync(
            ctx,
            (_, _) => Task.FromResult<System.Net.HttpStatusCode?>(System.Net.HttpStatusCode.OK),
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.CommandText.Contains("'gateway' 'start'", StringComparison.Ordinal));
        Assert.Contains(runner.Invocations, invocation => invocation.CommandText.Contains("'gateway' 'status' '--json'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NativeReadiness_TimeoutIsTerminalAndDoesNotStartGateway()
    {
        var config = new SetupConfig
        {
            InstallMode = GatewayInstallMode.NativeWindows,
            DistroName = $"OpenClawGateway-Test-{Guid.NewGuid():N}",
            GatewayPort = 18789,
        };
        using var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        var runner = new RecordingCommandRunner(
            new CommandResult(
                0,
                BuildOwnedNativeStatusJson(config),
                "",
                TimeSpan.Zero,
                false));
        var ctx = new SetupContext(
            config,
            logger,
            new TransactionJournal(filePath: null),
            runner,
            CancellationToken.None);
        ctx.NativeCliPath = @"C:\test\openclaw.ps1";

        var result = await StartGatewayStep.WaitForNativeGatewayReadyAfterInstallAsync(
            ctx,
            (_, _) => Task.FromResult<System.Net.HttpStatusCode?>(null),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.Zero,
            CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.CommandText.Contains("'gateway' 'start'", StringComparison.Ordinal));
        Assert.Contains(runner.Invocations, invocation => invocation.CommandText.Contains("'gateway' 'status' '--json'", StringComparison.Ordinal));
    }

    [Fact]
    public void IsManagedNativeGatewayStatus_RequiresManagedProfileAndPort()
    {
        var config = new SetupConfig
        {
            InstallMode = GatewayInstallMode.NativeWindows,
            DistroName = $"OpenClawGateway-Test-{Guid.NewGuid():N}",
            GatewayPort = 18789,
        };

        Assert.True(StartGatewayStep.IsManagedNativeGatewayStatus(BuildOwnedNativeStatusJson(config), config));

        var wrongProfile = BuildOwnedNativeStatusJson(config).Replace(
            GatewayCliRunner.GetManagedNativeProfile(config),
            "other-profile");
        Assert.False(StartGatewayStep.IsManagedNativeGatewayStatus(wrongProfile, config));

        var wrongPort = BuildOwnedNativeStatusJson(config).Replace("\"port\":18789", "\"port\":18790");
        Assert.False(StartGatewayStep.IsManagedNativeGatewayStatus(wrongPort, config));
    }

    [Fact]
    public void IsManagedNativeGatewayStatus_FailsClosedOnSparseOrPortOnlyStatus()
    {
        var config = new SetupConfig
        {
            InstallMode = GatewayInstallMode.NativeWindows,
            DistroName = $"OpenClawGateway-Test-{Guid.NewGuid():N}",
            GatewayPort = 18789,
        };

        // A status that only reports the matching port (e.g. an unrelated gateway that
        // happens to bind it) must not be accepted as the setup-managed gateway.
        var portOnly = System.Text.Json.JsonSerializer.Serialize(new { gateway = new { port = 18789 } });
        Assert.False(StartGatewayStep.IsManagedNativeGatewayStatus(portOnly, config));

        // The real filtered status (task name absent from environment) must still pass
        // on its profile + sourcePath + label + port signals — regression guard against
        // requiring OPENCLAW_WINDOWS_TASK_NAME, which the CLI omits from environment.
        Assert.True(StartGatewayStep.IsManagedNativeGatewayStatus(BuildOwnedNativeStatusJson(config), config));

        // Missing the Scheduled Task label must fail closed.
        var missingLabel = BuildOwnedNativeStatusJson(config).Replace("Scheduled Task", "");
        Assert.False(StartGatewayStep.IsManagedNativeGatewayStatus(missingLabel, config));

        // Missing the profile identity signal must fail closed.
        var missingProfile = BuildOwnedNativeStatusJson(config).Replace("\"OPENCLAW_PROFILE\"", "\"OPENCLAW_PROFILE_RENAMED\"");
        Assert.False(StartGatewayStep.IsManagedNativeGatewayStatus(missingProfile, config));

        // Missing the managed state-dir sourcePath must fail closed.
        var missingSourcePath = BuildOwnedNativeStatusJson(config).Replace("\"sourcePath\"", "\"sourcePathRenamed\"");
        Assert.False(StartGatewayStep.IsManagedNativeGatewayStatus(missingSourcePath, config));
    }

    [Fact]
    public async Task InstallNativeCli_SkipsReinstallWhenAppOwnedCliMatchesPinnedVersion()
    {
        using var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        var ctx = CreateNativeSkipContext(logger);
        var probes = 0;

        var result = await InstallNativeCliStep.TrySkipInstallWhenCurrentAsync(
            ctx,
            @"C:\managed\native-cli\openclaw.ps1",
            "2026.6.11",
            (_, _) =>
            {
                probes++;
                return Task.FromResult(new CommandResult(0, "openclaw 2026.6.11", "", TimeSpan.Zero, false));
            },
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.IsSuccess);
        Assert.Equal(1, probes);
        Assert.Equal(@"C:\managed\native-cli\openclaw.ps1", ctx.NativeCliPath);
    }

    [Fact]
    public async Task InstallNativeCli_ReinstallsWhenInstalledVersionDiffers()
    {
        using var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        var ctx = CreateNativeSkipContext(logger);

        var result = await InstallNativeCliStep.TrySkipInstallWhenCurrentAsync(
            ctx,
            @"C:\managed\native-cli\openclaw.ps1",
            "2026.6.11",
            (_, _) => Task.FromResult(new CommandResult(0, "openclaw 2026.5.9", "", TimeSpan.Zero, false)),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task InstallNativeCli_ReinstallsWhenManagedCliMissing()
    {
        using var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        var ctx = CreateNativeSkipContext(logger);

        var result = await InstallNativeCliStep.TrySkipInstallWhenCurrentAsync(
            ctx,
            null,
            "2026.6.11",
            (_, _) => throw new InvalidOperationException("probe must not run without a managed launcher"),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Null(ctx.NativeCliPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("stable")]
    public async Task InstallNativeCli_ReinstallsWhenVersionNotExactPin(string requested)
    {
        using var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        var ctx = CreateNativeSkipContext(logger);

        var result = await InstallNativeCliStep.TrySkipInstallWhenCurrentAsync(
            ctx,
            @"C:\managed\native-cli\openclaw.ps1",
            requested,
            (_, _) => throw new InvalidOperationException("probe must not run for a non-exact version"),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Null(ctx.NativeCliPath);
    }

    [Fact]
    public async Task InstallNativeCli_ReinstallsWhenProbeFails()
    {
        using var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        var ctx = CreateNativeSkipContext(logger);

        var result = await InstallNativeCliStep.TrySkipInstallWhenCurrentAsync(
            ctx,
            @"C:\managed\native-cli\openclaw.ps1",
            "2026.6.11",
            (_, _) => Task.FromResult(new CommandResult(1, "", "not found", TimeSpan.Zero, false)),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task InstallNativeCli_ReinstallsWhenProbeThrows()
    {
        using var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        var ctx = CreateNativeSkipContext(logger);

        var result = await InstallNativeCliStep.TrySkipInstallWhenCurrentAsync(
            ctx,
            @"C:\managed\native-cli\openclaw.ps1",
            "2026.6.11",
            (_, _) => throw new TimeoutException("probe timed out"),
            CancellationToken.None);

        Assert.Null(result);
    }

    private static SetupContext CreateNativeSkipContext(SetupLogger logger)
    {
        var config = new SetupConfig
        {
            InstallMode = GatewayInstallMode.NativeWindows,
            DistroName = $"OpenClawGateway-Test-{Guid.NewGuid():N}",
            GatewayPort = 18789,
        };
        return new SetupContext(
            config,
            logger,
            new TransactionJournal(filePath: null),
            new RecordingCommandRunner(),
            CancellationToken.None);
    }

    private static string BuildOwnedNativeStatusJson(SetupConfig config)
    {
        var stateLeaf = Path.GetFileName(GatewayCliRunner.GetManagedNativeStateDir(config));
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            service = new
            {
                label = "Scheduled Task",
                command = new
                {
                    sourcePath = $@"%USERPROFILE%\{stateLeaf}\gateway.cmd",
                    // Mirror the real `gateway status --json`: the environment object is
                    // projected to a subset and OMITS OPENCLAW_WINDOWS_TASK_NAME (that key
                    // surfaces only under environmentValueSources), so ownership must not
                    // depend on the task name being present in environment.
                    environment = new Dictionary<string, string>
                    {
                        ["OPENCLAW_PROFILE"] = GatewayCliRunner.GetManagedNativeProfile(config),
                        ["OPENCLAW_STATE_DIR"] = GatewayCliRunner.GetManagedNativeStateDir(config),
                        ["OPENCLAW_GATEWAY_PORT"] = config.GatewayPort.ToString(),
                    },
                    environmentValueSources = new Dictionary<string, string>
                    {
                        ["OPENCLAW_PROFILE"] = "inline",
                        ["OPENCLAW_WINDOWS_TASK_NAME"] = "inline",
                    },
                },
                runtime = new { status = "running" },
            },
            gateway = new
            {
                port = config.GatewayPort,
            },
        });
    }

    private sealed class CapturingCommandRunner(CommandResult result) : ICommandRunner
    {
        public string? Executable { get; private set; }
        public string[] Arguments { get; private set; } = [];
        public IReadOnlyDictionary<string, string> Environment { get; private set; } =
            new Dictionary<string, string>();

        public Task<CommandResult> RunAsync(
            string executable,
            string[] arguments,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null,
            string? stdinInput = null,
            CancellationToken ct = default)
        {
            Executable = executable;
            Arguments = arguments;
            Environment = environment ?? new Dictionary<string, string>();
            return Task.FromResult(result);
        }

        public Task<CommandResult> RunInWslAsync(
            string distroName,
            string command,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken ct = default,
            string? user = null,
            bool inputViaStdin = false)
            => throw new NotSupportedException();
    }

    private sealed class RecordingCommandRunner(params CommandResult[] results) : ICommandRunner
    {
        private readonly Queue<CommandResult> _results = new(results);

        public List<Invocation> Invocations { get; } = [];

        public Task<CommandResult> RunAsync(
            string executable,
            string[] arguments,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null,
            string? stdinInput = null,
            CancellationToken ct = default)
        {
            Invocations.Add(new Invocation(executable, arguments));
            return Task.FromResult(_results.Count > 0
                ? _results.Dequeue()
                : new CommandResult(0, "", "", TimeSpan.Zero, false));
        }

        public Task<CommandResult> RunInWslAsync(
            string distroName,
            string command,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken ct = default,
            string? user = null,
            bool inputViaStdin = false)
            => throw new NotSupportedException();
    }

    private sealed record Invocation(string Executable, string[] Arguments)
    {
        public string CommandText => string.Join(" ", Arguments);
    }
}
