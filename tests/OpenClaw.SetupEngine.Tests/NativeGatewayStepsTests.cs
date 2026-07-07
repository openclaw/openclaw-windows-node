namespace OpenClaw.SetupEngine.Tests;

public class NativeGatewayStepsTests
{
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

    [Fact]
    public void NativeReplacementSummary_PreservesExistingWslFiles()
    {
        var existing = new ExistingConfigDetector.ExistingConfig(
            HasLocalGateway: true,
            LocalGatewayId: "local",
            LocalGatewayUrl: "ws://localhost:18789",
            HasDistro: true,
            DistroName: "OpenClawGateway",
            HasIdentityFiles: true,
            PreservedGatewayCount: 0,
            PreservedGatewayNames: []);

        var summary = ExistingConfigDetector.BuildReplacementSummary(
            existing,
            GatewayInstallMode.NativeWindows);

        Assert.Contains("directly on Windows", summary);
        Assert.Contains("stopped but its files will be preserved", summary);
    }

    [Fact]
    public void WslReplacementSummary_DisclosesNativeGatewayRemoval()
    {
        var existing = new ExistingConfigDetector.ExistingConfig(
            HasLocalGateway: true,
            LocalGatewayId: "local",
            LocalGatewayUrl: "ws://localhost:18789",
            HasDistro: false,
            DistroName: null,
            HasIdentityFiles: true,
            PreservedGatewayCount: 0,
            PreservedGatewayNames: [],
            LocalGatewayMode: GatewayInstallMode.NativeWindows);

        var title = ExistingConfigDetector.BuildReplacementTitle(existing, GatewayInstallMode.Wsl);
        var summary = ExistingConfigDetector.BuildReplacementSummary(existing, GatewayInstallMode.Wsl);

        Assert.Equal("Replace existing native Windows gateway?", title);
        Assert.Contains("native Windows gateway service", summary);
        Assert.Contains("setup-managed configuration", summary);
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
}
