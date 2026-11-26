using System.Security.Cryptography;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class GatewayPackageCleanupTests
{
    private const string WorkerTimeoutEnvironmentVariable = "OPENCLAW_GATEWAY_CLEANUP_TEST_TIMEOUT_SECONDS";
    // Bounds one PowerShell 5.1 worker run; hosted runners exceeded 30 s on cold start under load.
    private static readonly TimeSpan WorkerTimeout =
        int.TryParse(Environment.GetEnvironmentVariable(WorkerTimeoutEnvironmentVariable), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(120);

    [Theory]
    [InlineData("AllSigned", "Undefined", false)]
    [InlineData("Undefined", "AllSigned", false)]
    [InlineData("RemoteSigned", "AllSigned", true)]
    [InlineData("Restricted", "Undefined", false)]
    [InlineData("Undefined", "RemoteSigned", true)]
    [InlineData("", "Undefined", false)]
    [InlineData("Undefined", null, false)]
    [InlineData("Unrecognized", "Undefined", false)]
    public void CleanupPolicy_RequiresKnownValuesAndUsesEffectiveGroupPolicy(
        string? machinePolicy, string? userPolicy, bool allowed) =>
        Assert.Equal(allowed, GatewayPackageCleanup.IsCleanupScriptAllowedByPolicy(machinePolicy, userPolicy));

    [Theory]
    [InlineData("AllSigned|Undefined", 0, false, false)]
    [InlineData("Undefined|RemoteSigned", 0, false, true)]
    [InlineData("AllSigned|Undefined", 1, false, false)]
    [InlineData("Undefined|RemoteSigned", 0, true, false)]
    [InlineData("Unrecognized|Undefined", 0, false, false)]
    public async Task CleanupPolicyProbe_FailsClosedOnBlockedOrInvalidResults(
        string output, int exitCode, bool timedOut, bool expected)
    {
        var commands = new PolicyProbeCommandRunner(output, exitCode, timedOut);

        Assert.Equal(expected, await GatewayPackageCleanup.CanRunCleanupScriptAsync(
            commands, CancellationToken.None));
        var call = Assert.Single(commands.Calls);
        Assert.StartsWith("-NoProfile", call.Arguments[0]);
        Assert.Contains("-Command", call.Arguments);
    }

    [Fact]
    public async Task CleanupTaskRemovalFailure_IsLoggedAndRetried()
    {
        using var fixture = new CleanupFixture();
        var warnings = new List<string>();
        var failedCommands = new PolicyProbeCommandRunner("", 1, false, "access denied");

        Assert.False(await GatewayPackageCleanup.DisarmCleanupTaskAsync(
            failedCommands, fixture.Cleanup, CancellationToken.None, warnings.Add));
        Assert.True(File.Exists(Path.Combine(fixture.Cleanup, "cleanup-task-removal.pending")));
        Assert.Contains("access denied", Assert.Single(warnings));

        var successfulCommands = new PolicyProbeCommandRunner("", 0, false);
        Assert.True(await GatewayPackageCleanup.DisarmCleanupTaskAsync(
            successfulCommands, fixture.Cleanup, CancellationToken.None, warnings.Add));
        Assert.False(File.Exists(Path.Combine(fixture.Cleanup, "cleanup-task-removal.pending")));

        var call = Assert.Single(successfulCommands.Calls);
        Assert.Contains("-Command", call.Arguments);
        Assert.Contains("Unregister-ScheduledTask", call.Arguments[^1]);
        Assert.DoesNotContain("-ExecutionPolicy", call.Arguments);
    }

    [Fact]
    public async Task CleanupTaskDisarm_DoesNotRemoveTaskWhileOwnershipExists()
    {
        using var fixture = new CleanupFixture();
        File.WriteAllText(fixture.OwnedPath, "{}");
        var commands = new PolicyProbeCommandRunner("", 0, false);

        Assert.False(await GatewayPackageCleanup.DisarmCleanupTaskAsync(
            commands, fixture.Cleanup, CancellationToken.None));

        Assert.Empty(commands.Calls);
        Assert.False(File.Exists(Path.Combine(fixture.Cleanup, "cleanup-task-removal.pending")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreserveReceipt_AppendsGatewayRemovalAndDisownsOwner(bool windowsPowerShell)
    {
        using var fixture = new CleanupFixture();
        var fingerprint = Convert.ToHexString(SHA256.HashData(await fixture.WriteOwnedRecordAsync(windowsPowerShell)));
        File.WriteAllText(fixture.ReceiptPath, "[]");

        await GatewayPackageCleanup.AppendPreservedRemovalAsync(fixture.Cleanup, CancellationToken.None);

        using var pending = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(fixture.Cleanup, "gateway-preservation.pending")));
        Assert.Equal(fingerprint, pending.RootElement.GetProperty("OwnedRecordSha256").GetString());
        using var receipt = JsonDocument.Parse(File.ReadAllText(fixture.ReceiptPath));
        Assert.Equal(1, receipt.RootElement.GetArrayLength());
        Assert.Equal("OpenClawGateway", receipt.RootElement[0].GetProperty("DistroName").GetString());
        Assert.Equal(Path.Combine("C:\\", "OpenClawGateway"),
            receipt.RootElement[0].GetProperty("InstallPath").GetString());
        Assert.Equal(JsonValueKind.Null, receipt.RootElement[0].GetProperty("NativeFamily").ValueKind);
        Assert.False(File.Exists(fixture.OwnedPath));

        Assert.True(await fixture.InitializeAsync());

        Assert.Equal([(false, false)], fixture.Calls);
    }

    [Fact]
    public async Task PreserveReceiptWriteFailure_LeavesWorkerGatedForRetry()
    {
        using var fixture = new CleanupFixture();
        File.WriteAllText(fixture.OwnedPath, """{"DistroName":"OpenClawGateway"}""");
        Directory.CreateDirectory(fixture.ReceiptPath);

        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() =>
            GatewayPackageCleanup.AppendPreservedRemovalAsync(fixture.Cleanup, CancellationToken.None));

        Assert.True(File.Exists(fixture.OwnedPath));
        Assert.True(File.Exists(Path.Combine(fixture.Cleanup, "gateway-preservation.pending")));
    }

    [Fact]
    public async Task PendingPreservation_DoesNotDisownAChangedOwner()
    {
        using var fixture = new CleanupFixture();
        var originalOwner = """{"DistroName":"OpenClawGateway","RegistrationId":"original"}""";
        File.WriteAllText(fixture.OwnedPath, originalOwner);
        var fingerprint = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture.OwnedPath)));
        File.WriteAllText(Path.Combine(fixture.Cleanup, "gateway-preservation.pending"),
            JsonSerializer.Serialize(new { OwnedRecordSha256 = fingerprint }));
        var newOwner = """{"DistroName":"OpenClawGateway","RegistrationId":"new"}""";
        File.WriteAllText(fixture.OwnedPath, newOwner);

        Assert.False(await GatewayPackageCleanup.CompletePendingPreservationAsync(
            fixture.Cleanup, CancellationToken.None));

        Assert.Equal(newOwner, File.ReadAllText(fixture.OwnedPath));
        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingPreservation_CompletesForTheOriginalOwner(bool windowsPowerShell)
    {
        using var fixture = new CleanupFixture();
        var fingerprint = Convert.ToHexString(SHA256.HashData(await fixture.WriteOwnedRecordAsync(windowsPowerShell)));
        File.WriteAllText(Path.Combine(fixture.Cleanup, "gateway-preservation.pending"),
            JsonSerializer.Serialize(new { OwnedRecordSha256 = fingerprint }));

        Assert.True(await GatewayPackageCleanup.CompletePendingPreservationAsync(
            fixture.Cleanup, CancellationToken.None));

        Assert.False(File.Exists(fixture.OwnedPath));
        using var receipt = JsonDocument.Parse(File.ReadAllText(fixture.ReceiptPath));
        Assert.Equal("OpenClawGateway", receipt.RootElement[0].GetProperty("DistroName").GetString());
    }

    [Fact]
    public async Task Startup_PreservesUnboundLegacyDistroWithoutRegisteringCleanup()
    {
        using var fixture = new CleanupFixture();
        var path = Path.Combine(fixture.Local, "wsl", "OpenClawGateway");
        Directory.CreateDirectory(path);
        await ManagedDistroOwnership.WriteMarkerAsync(fixture.Local, "OpenClawGateway", path, CancellationToken.None);
        var marker = File.ReadAllText(Path.Combine(fixture.Local, "setup-managed-distro.json"));
        var canary = Path.Combine(path, "replacement-data.txt");
        File.WriteAllText(canary, "replacement installed before upgraded startup");

        Assert.True(await fixture.InitializeAsync());

        Assert.Equal([(false, false)], fixture.Calls);
        Assert.False(File.Exists(fixture.OwnedPath));
        Assert.Equal(marker, File.ReadAllText(Path.Combine(fixture.Local, "setup-managed-distro.json")));
        Assert.Equal("replacement installed before upgraded startup", File.ReadAllText(canary));
    }

    [Fact]
    public async Task Startup_RefreshesExistingCleanupWithoutRebindingDistro()
    {
        using var fixture = new CleanupFixture();
        File.WriteAllText(fixture.OwnedPath, """{"DistroName":"OpenClawGateway","RegistrationId":"original"}""");

        Assert.True(await fixture.InitializeAsync());

        Assert.Equal([(false, false), (true, false)], fixture.Calls);
        Assert.Contains("original", File.ReadAllText(fixture.OwnedPath));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[null]")]
    public async Task InvalidReceipt_LeavesEvidenceAndSettingsIntactWithoutDisowning(string receipt)
    {
        using var fixture = new CleanupFixture();
        File.WriteAllText(fixture.OwnedPath, "{}");
        File.WriteAllText(fixture.ReceiptPath, receipt);
        File.WriteAllText(fixture.SettingsPath, """{"Theme":"Dark"}""");
        var prompts = 0;

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.InitializeAsync(_ =>
        {
            prompts++;
            return true;
        }));

        Assert.Contains("cleanup receipt", error.Message);
        Assert.Equal(0, prompts);
        Assert.Equal([(false, false)], fixture.Calls);
        Assert.Equal(receipt, File.ReadAllText(fixture.ReceiptPath));
        Assert.Equal("{}", File.ReadAllText(fixture.OwnedPath));
        Assert.Equal("""{"Theme":"Dark"}""", File.ReadAllText(fixture.SettingsPath));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"GatewayUrl":5}""")]
    [InlineData("""{"UseSshTunnel":"yes"}""")]
    [InlineData("""{"PreferredGatewayId":5}""")]
    public async Task InvalidSettings_DoNotRetireAuthenticationOrPendingEvidence(string settings)
    {
        using var fixture = new CleanupFixture();
        var registry = new GatewayRegistry(fixture.Data);
        var managed = registry.AddOrUpdate(new GatewayRecord
        {
            Id = Guid.NewGuid().ToString(),
            Url = "ws://127.0.0.1:18789",
            IsLocal = true,
            SetupManagedDistroName = "OpenClawGateway",
            SharedGatewayToken = "test-auth-token"
        });
        registry.Save();
        var identityDirectory = registry.GetIdentityDirectory(managed.Id);
        var identity = new DeviceIdentity(identityDirectory);
        identity.Initialize();
        identity.StoreDeviceToken("test-auth-token");
        var identityPath = Path.Combine(identityDirectory, "device-key-ed25519.json");
        var originalIdentity = File.ReadAllText(identityPath);
        var registryPath = Path.Combine(fixture.Data, "gateways.json");
        var originalRegistry = File.ReadAllText(registryPath);
        File.WriteAllText(fixture.OwnedPath, "{}");
        File.WriteAllText(fixture.ReceiptPath, """[{"DistroName":"OpenClawGateway"}]""");
        File.WriteAllText(fixture.SettingsPath, settings);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.InitializeAsync(_ =>
            throw new InvalidOperationException("Invalid JSON must not offer disowning.")));

        Assert.Equal(originalRegistry, File.ReadAllText(registryPath));
        Assert.Equal(originalIdentity, File.ReadAllText(identityPath));
        Assert.Equal(settings, File.ReadAllText(fixture.SettingsPath));
        Assert.Equal("{}", File.ReadAllText(fixture.OwnedPath));
        Assert.True(File.Exists(fixture.ReceiptPath));
        Assert.Equal([(false, false)], fixture.Calls);
    }

    [Fact]
    public async Task ValidReceipt_RetiresOnlyRemovedTargetAndKeepsUnrelatedSettings()
    {
        using var fixture = new CleanupFixture();
        var registry = new GatewayRegistry(fixture.Data);
        var managed = registry.AddOrUpdate(new GatewayRecord
        {
            Id = Guid.NewGuid().ToString(),
            Url = "ws://127.0.0.1:18789",
            IsLocal = true,
            SetupManagedDistroName = "OpenClawGateway",
            SharedGatewayToken = "test-auth-token"
        });
        var remote = registry.AddOrUpdate(new GatewayRecord
        {
            Id = Guid.NewGuid().ToString(),
            Url = "wss://gateway.example.test",
            SharedGatewayToken = "test-auth-token"
        });
        registry.Save();
        var identityDirectory = registry.GetIdentityDirectory(managed.Id);
        var identity = new DeviceIdentity(identityDirectory);
        identity.Initialize();
        identity.StoreDeviceToken("test-auth-token");
        File.WriteAllText(fixture.ReceiptPath, """[{"DistroName":"OpenClawGateway"}]""");
        File.WriteAllText(fixture.SettingsPath,
            """{"GatewayUrl":"ws://127.0.0.1:18789","Token":"test-auth-token","Theme":"Dark","EnableMcpServer":true}""");

        Assert.True(await fixture.InitializeAsync());

        registry.Load();
        Assert.Equal(remote, Assert.Single(registry.GetAll()));
        Assert.Null(DeviceIdentity.TryReadStoredDeviceToken(identityDirectory));
        using var settings = JsonDocument.Parse(File.ReadAllText(fixture.SettingsPath));
        Assert.Equal("", settings.RootElement.GetProperty("GatewayUrl").GetString());
        Assert.False(settings.RootElement.TryGetProperty("Token", out _));
        Assert.Equal("Dark", settings.RootElement.GetProperty("Theme").GetString());
        Assert.True(settings.RootElement.GetProperty("EnableMcpServer").GetBoolean());
        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnershipConflict_PreserveChoiceStillControlsRecovery(bool preserve)
    {
        using var fixture = new CleanupFixture();
        var calls = new List<(bool, bool)>();
        var prompts = 0;
        Task<string?> Run(bool register, bool keep)
        {
            calls.Add((register, keep));
            if (!keep) throw new InvalidOperationException("Managed WSL ownership changed.");
            File.WriteAllText(fixture.ReceiptPath, "[]");
            return Task.FromResult<string?>(fixture.Cleanup);
        }

        var result = await GatewayPackageCleanup.InitializeAsync(Run, fixture.Data, fixture.Local,
            preserve: message =>
            {
                Assert.Contains("ownership changed", message);
                prompts++;
                return preserve;
            });

        Assert.Equal(preserve, result);
        Assert.Equal(1, prompts);
        Assert.Equal(preserve ? [(false, false), (false, true)] : [(false, false)], calls);
        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    [Fact]
    public async Task InvalidReceiptAfterPreservation_RemainsPendingAndSurfacesRecoveryError()
    {
        using var fixture = new CleanupFixture();
        Task<string?> Run(bool register, bool keep)
        {
            if (!keep) throw new InvalidOperationException("Managed WSL ownership changed.");
            File.WriteAllText(fixture.ReceiptPath, "{");
            return Task.FromResult<string?>(fixture.Cleanup);
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            GatewayPackageCleanup.InitializeAsync(Run, fixture.Data, fixture.Local, preserve: _ => true));

        Assert.IsType<JsonException>(error.InnerException);
        Assert.Contains("reopen Companion", error.Message);
        Assert.Equal("{", File.ReadAllText(fixture.ReceiptPath));
    }

    [Theory]
    [InlineData("[", false)]
    [InlineData("", false)]
    [InlineData("null", false)]
    [InlineData("{}", false)]
    [InlineData("[null]", false)]
    [InlineData("""[{"DistroName":5}]""", false)]
    [InlineData("[]", true)]
    [InlineData("""[{"DistroName":"existing"}]""", true)]
    public async Task Worker_ValidatesReceiptsBeforeCleanupAndPreservesAppendIdentities(string receipt, bool valid)
    {
        Assert.True(OperatingSystem.IsWindows(), "Worker contract proof requires Windows PowerShell 5.1.");
        using var temp = new TempDirectory();
        using var source = typeof(GatewayPackageCleanup).Assembly.GetManifestResourceStream(
            "OpenClaw.SetupEngine.GatewayPackageCleanup.ps1");
        Assert.NotNull(source);
        using (var reader = new StreamReader(source))
            File.WriteAllText(temp.Combine("worker.ps1"), await reader.ReadToEndAsync());
        var resultPath = temp.Combine("cleanup-result.json");
        File.WriteAllText(resultPath, receipt);
        var harness = temp.Combine("receipt-test.ps1");
        File.WriteAllText(harness, """
            $ErrorActionPreference = 'Stop'
            $tokens = $null; $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $PSScriptRoot 'worker.ps1'), [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw 'Worker parsing failed.' }
            foreach ($name in @('Assert-ValidRemovalReceipt', 'Record-Removal')) {
                $function = $ast.Find({ param($n)
                    $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name
                }, $true)
                . ([scriptblock]::Create($function.Extent.Text))
            }
            $root = $PSScriptRoot
            try { Assert-ValidRemovalReceipt } catch {
                if ($_.Exception.Message -notlike 'OPENCLAW_CLEANUP_RECEIPT_INVALID:*') { throw }
                'invalid'
                exit 0
            }
            Record-Removal 'first' 'C:\fixture\first' $null
            Record-Removal 'second' 'C:\fixture\second' $null
            Record-Removal $null $null 'fixture.native'
            Assert-ValidRemovalReceipt
            'valid'
            """);
        using var logger = new SetupLogger(filePath: null);
        var result = await new CommandRunner(logger).RunAsync(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-File", harness], WorkerTimeout);
        Assert.False(result.TimedOut);
        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Equal(valid ? "valid" : "invalid", result.Stdout.Trim());
        if (!valid)
        {
            Assert.Equal(receipt, File.ReadAllText(resultPath));
            return;
        }
        using var removals = JsonDocument.Parse(File.ReadAllText(resultPath));
        var values = removals.RootElement.EnumerateArray().ToArray();
        Assert.Equal("first", values[^3].GetProperty("DistroName").GetString());
        Assert.Equal("second", values[^2].GetProperty("DistroName").GetString());
        Assert.Equal("fixture.native", values[^1].GetProperty("NativeFamily").GetString());
        Assert.Equal(receipt == "[]" ? 3 : 4, values.Length);
        Assert.False(File.Exists(resultPath + ".tmp"));
    }

    [Fact]
    public async Task Worker_PendingPreserveMarkerPreventsCleanupWhileOwnershipRemains()
    {
        Assert.True(OperatingSystem.IsWindows(), "Worker contract proof requires Windows PowerShell 5.1.");
        using var temp = new TempDirectory();
        using var source = typeof(GatewayPackageCleanup).Assembly.GetManifestResourceStream(
            "OpenClaw.SetupEngine.GatewayPackageCleanup.ps1");
        Assert.NotNull(source);
        var workerPath = temp.Combine("worker.ps1");
        using (var reader = new StreamReader(source))
            File.WriteAllText(workerPath, await reader.ReadToEndAsync());

        var profile = Directory.CreateDirectory(temp.Combine("profile")).FullName;
        var cleanup = Directory.CreateDirectory(Path.Combine(
            profile, ".openclaw-companion", "cleanup", "Fixture_123")).FullName;
        var ownedPath = Path.Combine(cleanup, "owned.json");
        File.WriteAllText(ownedPath, """{"DistroName":"OpenClawGateway"}""");
        File.WriteAllText(Path.Combine(cleanup, "gateway-preservation.pending"), string.Empty);
        var harnessPath = temp.Combine("worker-test.ps1");
        File.WriteAllText(harnessPath, """
            $env:USERPROFILE = Join-Path $PSScriptRoot 'profile'
            & (Join-Path $PSScriptRoot 'worker.ps1') -Family 'Fixture_123'
            """);

        using var logger = new SetupLogger(filePath: null);
        var result = await new CommandRunner(logger).RunAsync(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-File", harnessPath], WorkerTimeout);

        Assert.False(result.TimedOut);
        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.True(File.Exists(ownedPath));
        Assert.False(File.Exists(Path.Combine(cleanup, "cleanup-result.txt")));
    }

    [Theory]
    [InlineData(null, null, false, false)]
    [InlineData("OpenClawGateway", null, false, false)]
    [InlineData("OpenClawGateway", "old-registration", false, false)]
    [InlineData("OtherGateway", "current-registration", false, false)]
    [InlineData("OpenClawGateway", "current-registration", false, true)]
    [InlineData(null, null, true, true)]
    [InlineData("OpenClawGateway", "old-registration", true, true)]
    public async Task Worker_BindsOnlyFreshCreationOrAnAlreadyBoundRegistration(
        string? previousDistro, string? previousId, bool createdDistro, bool expectedAllowed)
    {
        Assert.True(OperatingSystem.IsWindows(), "Worker contract proof requires Windows PowerShell 5.1.");
        using var temp = new TempDirectory();
        using var source = typeof(GatewayPackageCleanup).Assembly.GetManifestResourceStream(
            "OpenClaw.SetupEngine.GatewayPackageCleanup.ps1");
        Assert.NotNull(source);
        var workerPath = temp.Combine("worker.ps1");
        using (var reader = new StreamReader(source))
            File.WriteAllText(workerPath, await reader.ReadToEndAsync());
        var installPath = temp.Combine("replacement");
        File.WriteAllText(temp.Combine("fixture.json"), JsonSerializer.Serialize(new
        {
            Record = new { DistroName = previousDistro, RegistrationId = previousId, InstallPath = installPath },
            InstallPath = installPath,
            CreatedDistro = createdDistro
        }));
        var harness = temp.Combine("guard-test.ps1");
        File.WriteAllText(harness, """
            $ErrorActionPreference = 'Stop'
            $tokens = $null; $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $PSScriptRoot 'worker.ps1'), [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw 'Worker parsing failed.' }
            $normalize = $ast.Find({ param($n)
                $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Normalize-Path'
            }, $true)
            . ([scriptblock]::Create($normalize.Extent.Text))
            $guards = @($ast.FindAll({ param($n)
                $n -is [System.Management.Automation.Language.IfStatementAst] -and
                $n.Clauses[0].Item1.Extent.Text -eq '$DistroName'
            }, $true))
            if ($guards.Count -ne 1) { throw 'Expected exactly one registration guard.' }
            $body = $guards[0].Clauses[0].Item2.Extent.Text
            $inputData = Get-Content (Join-Path $PSScriptRoot 'fixture.json') -Raw | ConvertFrom-Json
            $record = $inputData.Record
            $InstallPath = $inputData.InstallPath
            $DistroName = 'OpenClawGateway'
            $CreatedDistro = $inputData.CreatedDistro
            function Read-WslRegistrations {
                [pscustomobject]@{
                    DistributionName = 'OpenClawGateway'
                    BasePath = $InstallPath
                    PSChildName = 'current-registration'
                }
            }
            $allowed = $false
            $failure = $null
            try {
                . ([scriptblock]::Create($body.Substring(1, $body.Length - 2)))
                $allowed = $true
            } catch { $failure = $_.Exception.Message }
            [pscustomobject]@{ Allowed = $allowed; Record = $record; Failure = $failure } | ConvertTo-Json -Depth 4
            """);
        using var logger = new SetupLogger(filePath: null);
        var result = await new CommandRunner(logger).RunAsync(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-File", harness], WorkerTimeout);
        Assert.False(result.TimedOut);
        Assert.True(result.ExitCode == 0, result.Stderr);
        using var output = JsonDocument.Parse(result.Stdout);
        Assert.Equal(expectedAllowed, output.RootElement.GetProperty("Allowed").GetBoolean());
        var expectedId = expectedAllowed ? "current-registration" : previousId;
        Assert.Equal(expectedId, output.RootElement.GetProperty("Record").GetProperty("RegistrationId").GetString());
        if (!expectedAllowed)
            Assert.Contains("registration could not be confirmed", output.RootElement.GetProperty("Failure").GetString());
    }

    [Theory]
    [InlineData("removed", true, true)]
    [InlineData("not-configured", true, true)]
    [InlineData("failed", true, false)]
    [InlineData("nonzero", true, false)]
    [InlineData("malformed", true, false)]
    [InlineData("string-ok", true, false)]
    [InlineData("wrong-command", true, false)]
    [InlineData("wrong-gateway-state", true, false)]
    [InlineData("wrong-session-state", true, false)]
    [InlineData("timeout", true, false)]
    [InlineData("missing-cli", false, false)]
    [InlineData("foreign-publisher", false, false)]
    [InlineData("foreign-family", false, false)]
    [InlineData("shared", false, false)]
    [InlineData("replaced-marker", false, false)]
    [InlineData("replaced-during-teardown", true, false)]
    [InlineData("shared-during-teardown", true, false)]
    [InlineData("remove-failed", true, false)]
    public async Task Worker_RemovesOwnedPackageOnlyAfterConfirmedTeardown(
        string scenario, bool teardownInvoked, bool removed)
    {
        Assert.True(OperatingSystem.IsWindows(), "Worker contract proof requires Windows PowerShell 5.1.");
        using var temp = new TempDirectory();
        using var source = typeof(GatewayPackageCleanup).Assembly.GetManifestResourceStream(
            "OpenClaw.SetupEngine.GatewayPackageCleanup.ps1");
        Assert.NotNull(source);
        using (var reader = new StreamReader(source))
            File.WriteAllText(temp.Combine("worker.ps1"), await reader.ReadToEndAsync());
        File.WriteAllText(temp.Combine("scenario.txt"), scenario);
        var owned = JsonSerializer.Serialize(new
        {
            OwnNativePackage = true, NativeFamily = "fixture.native", NativeInstallationId = "owned",
            InstallationId = "removed-companion", RegisteredUtc = "2026-11-01T00:00:00Z"
        });
        File.WriteAllText(temp.Combine("owned.json"), owned);
        var harness = temp.Combine("native-cleanup-test.ps1");
        File.WriteAllText(harness, """
            $ErrorActionPreference = 'Stop'
            $root = $PSScriptRoot
            $recordPath = Join-Path $root 'owned.json'
            $scenario = Get-Content (Join-Path $root 'scenario.txt') -Raw
            $Family = 'fixture.companion'
            $sentinel = Join-Path $root 'absent-companion-marker'
            $taskName = 'fixture-cleanup-task'
            $Register = $false; $Preserve = $false
            $lock = [IO.MemoryStream]::new()
            $events = [Collections.Generic.List[string]]::new()
            $tokens = $null; $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $root 'worker.ps1'), [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw 'Worker parsing failed.' }
            # Execute the real worker's cleanup/error/finalization flow, not a copied ordering.
            foreach ($name in @('Assert-ValidRemovalReceipt', 'Record-Removal', 'Invoke-NativeGatewayTeardown')) {
                $function = $ast.Find({ param($n)
                    $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name
                }, $true)
                if ($function) { . ([scriptblock]::Create($function.Extent.Text)) }
            }
            $worker = @($ast.EndBlock.Statements | Where-Object {
                $_ -is [System.Management.Automation.Language.TryStatementAst]
            })
            if ($worker.Count -ne 1) { throw 'Expected one worker cleanup boundary.' }
            function Test-PackageRemovedSince { return $true }
            function Test-Path {
                param([Alias('LiteralPath')][string]$Path, [string]$PathType)
                if ($Path.EndsWith('companion-cleanup-owner.txt')) { return $true }
                if ($Path.EndsWith('clawctl.exe')) { return $scenario -ne 'missing-cli' }
                Microsoft.PowerShell.Management\Test-Path -LiteralPath $Path
            }
            function Get-Content {
                param([Alias('LiteralPath')][string]$Path, [switch]$Raw)
                if ($Path.EndsWith('companion-cleanup-owner.txt')) {
                    if ($scenario -eq 'replaced-marker') { return 'replacement' }
                    return 'owned'
                }
                Microsoft.PowerShell.Management\Get-Content -LiteralPath $Path -Raw
            }
            function Get-AppxPackage {
                param([string]$Name, [string]$PackageTypeFilter)
                $afterTeardown = $events.Contains('teardown')
                [pscustomobject]@{
                    Name = 'OpenClawFoundation.OpenClawGateway'
                    Publisher = $(if ($scenario -eq 'foreign-publisher') { 'other' } else { 'CN=4BA40A7A-B719-4C40-BF91-84AF4F1136FC' })
                    PackageFamilyName = $(if ($scenario -eq 'foreign-family') { 'other.family' } else { 'fixture.native' })
                    PackageFullName = $(if ($scenario -eq 'replaced-during-teardown' -and $afterTeardown) { 'replacement-package' } else { 'owned-package' })
                }
                if (-not $Name -and ($scenario -eq 'shared' -or ($scenario -eq 'shared-during-teardown' -and $afterTeardown))) {
                    [pscustomobject]@{ Name = 'OpenClawFoundation.OpenClaw.Dev'; PackageFamilyName = 'other.companion' }
                }
            }
            function Invoke-NativeTeardownCommand([string]$alias) {
                $expected = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Microsoft\WindowsApps\fixture.native\clawctl.exe'
                if ($alias -ne $expected) { throw 'Teardown was not package-qualified.' }
                $events.Add('teardown')
                if ($scenario -eq 'timeout') { throw 'Native Gateway teardown timed out; its outcome is unconfirmed. Cleanup remains pending.' }
                $result = @{ ok = $true; command = 'teardown'; gateway = @{ state = 'records-removed' }; session = @{ state = 'removed' } }
                switch ($scenario) {
                    'not-configured' { $result.session.state = 'not-configured' }
                    'failed' { $result.ok = $false }
                    'string-ok' { $result.ok = 'true' }
                    'wrong-command' { $result.command = 'status' }
                    'wrong-gateway-state' { $result.gateway.state = 'running' }
                    'wrong-session-state' { $result.session.state = 'ready' }
                }
                [pscustomobject]@{
                    ExitCode = $(if ($scenario -eq 'nonzero') { 1 } else { 0 })
                    Stdout = $(if ($scenario -eq 'malformed') { '{' } else { ConvertTo-Json $result -Depth 4 })
                }
            }
            function Remove-AppxPackage {
                param([Parameter(ValueFromPipeline)]$Package)
                process {
                    $events.Add('remove')
                    if ($scenario -eq 'remove-failed') { throw 'Fixture removal failed.' }
                }
            }
            function Get-ScheduledTask { param($TaskName, $ErrorAction); return $true }
            function Unregister-ScheduledTask { param($TaskName, [switch]$Confirm); $events.Add('unregister-cleanup') }
            $failure = $null
            try { . ([scriptblock]::Create($worker[0].Extent.Text)) }
            catch { $failure = $_.Exception.Message }
            finally {
                [pscustomobject]@{ Events = @($events.ToArray()); Failure = $failure } |
                    ConvertTo-Json | Set-Content (Join-Path $root 'observed.json') -Encoding UTF8
            }
            """);
        using var logger = new SetupLogger(filePath: null);
        var result = await new CommandRunner(logger).RunAsync(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-File", harness], WorkerTimeout);
        Assert.False(result.TimedOut);
        Assert.True(result.ExitCode == 0, result.Stderr);
        using var output = JsonDocument.Parse(File.ReadAllText(temp.Combine("observed.json")));
        var events = output.RootElement.GetProperty("Events").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Equal(teardownInvoked, events.Contains("teardown"));
        if (removed)
        {
            Assert.Equal(["teardown", "remove", "unregister-cleanup"], events);
            Assert.Null(output.RootElement.GetProperty("Failure").GetString());
            Assert.False(File.Exists(temp.Combine("owned.json")));
            using var receipt = JsonDocument.Parse(File.ReadAllText(temp.Combine("cleanup-result.json")));
            Assert.Equal("fixture.native", Assert.Single(receipt.RootElement.EnumerateArray()).GetProperty("NativeFamily").GetString());
            Assert.Equal("complete", File.ReadAllText(temp.Combine("cleanup-result.txt")).Trim());
        }
        else
        {
            Assert.Equal(scenario == "remove-failed", events.Contains("remove"));
            Assert.False(File.Exists(temp.Combine("cleanup-result.json")));
            if (scenario == "replaced-marker")
            {
                // Existing ownership policy retires this cleanup claim without touching the replacement.
                Assert.False(File.Exists(temp.Combine("owned.json")));
            }
            else
            {
                Assert.DoesNotContain("unregister-cleanup", events);
                Assert.Equal(owned, File.ReadAllText(temp.Combine("owned.json")));
                Assert.False(File.Exists(temp.Combine("cleanup-result.txt")));
                if (scenario is not ("shared" or "shared-during-teardown"))
                    Assert.True(File.Exists(temp.Combine("cleanup-error.txt")));
            }
        }
    }

    private sealed class CleanupFixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        public string Data { get; }
        public string Local { get; }
        public string Cleanup { get; }
        public string OwnedPath => Path.Combine(Cleanup, "owned.json");
        public string ReceiptPath => Path.Combine(Cleanup, "cleanup-result.json");
        public string SettingsPath => Path.Combine(Data, "settings.json");
        public List<(bool, bool)> Calls { get; } = [];

        public CleanupFixture()
        {
            Data = Directory.CreateDirectory(_temp.Combine("roaming")).FullName;
            Local = Directory.CreateDirectory(_temp.Combine("local")).FullName;
            Cleanup = Directory.CreateDirectory(_temp.Combine("cleanup")).FullName;
        }

        public async Task<byte[]> WriteOwnedRecordAsync(bool windowsPowerShell)
        {
            const string json = """{"DistroName":"OpenClawGateway","InstallPath":"C:\\OpenClawGateway","NativeFamily":null}""";
            if (!windowsPowerShell)
            {
                File.WriteAllText(OwnedPath, json);
                return File.ReadAllBytes(OwnedPath);
            }

            using var logger = new SetupLogger(filePath: null);
            var result = await new CommandRunner(logger).RunAsync(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe"),
                ["-NoProfile", "-NonInteractive", "-Command", $$"""
                    $record = '{{json}}' | ConvertFrom-Json
                    $record | ConvertTo-Json | Set-Content 'owned.json' -Encoding UTF8
                    """], WorkerTimeout, workingDirectory: Cleanup);
            Assert.False(result.TimedOut);
            Assert.True(result.ExitCode == 0, result.Stderr);
            var bytes = File.ReadAllBytes(OwnedPath);
            Assert.True(bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));
            return bytes;
        }

        public Task<bool> InitializeAsync(Func<string, bool>? preserve = null) =>
            GatewayPackageCleanup.InitializeAsync((register, keep) =>
            {
                Calls.Add((register, keep));
                return Task.FromResult<string?>(Cleanup);
            }, Data, Local, preserve: preserve);

        public void Dispose() => _temp.Dispose();
    }

    private sealed class PolicyProbeCommandRunner(
        string output, int exitCode, bool timedOut, string error = "") : ICommandRunner
    {
        public List<(string Executable, string[] Arguments)> Calls { get; } = [];

        public Task<CommandResult> RunAsync(string executable, string[] arguments, TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null,
            string? stdinInput = null, CancellationToken ct = default, Stream? stdinStream = null)
        {
            Calls.Add((executable, arguments));
            return Task.FromResult(new CommandResult(exitCode, output, error, TimeSpan.Zero, timedOut));
        }

        public Task<CommandResult> RunInWslAsync(string distroName, string command, TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null, CancellationToken ct = default,
            string? user = null, bool inputViaStdin = false) =>
            throw new NotSupportedException();
    }
}
