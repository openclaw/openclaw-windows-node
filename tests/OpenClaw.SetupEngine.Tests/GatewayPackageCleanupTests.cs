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
