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
    [InlineData("removed")]
    [InlineData("unknown-files")]
    [InlineData("remove-failed")]
    [InlineData("removed-then-interrupted")]
    [InlineData("identity-retired-then-interrupted")]
    [InlineData("registry-retired-then-interrupted")]
    [InlineData("record-replaced")]
    [InlineData("identity-replaced")]
    [InlineData("package-replaced")]
    [InlineData("surviving-companion")]
    [InlineData("invalid-registry")]
    [InlineData("invalid-settings")]
    [InlineData("unsafe-id")]
    [InlineData("redirected-identity")]
    [InlineData("legacy-id")]
    [InlineData("registration-refresh")]
    [InlineData("ssh-settings")]
    [InlineData("unrelated-settings")]
    public async Task NativeWorker_RetiresOnlyAdmittedCredentialsWithoutCompanionStartup(string scenario)
    {
        Assert.True(OperatingSystem.IsWindows(), "Worker contract proof requires Windows PowerShell 5.1.");
        using var fixture = new CleanupFixture();
        var registry = new GatewayRegistry(fixture.Data);
        var native = registry.AddOrUpdate(new GatewayRecord
        {
            Id = scenario == "legacy-id" ? "gw-native-fixture" : Guid.NewGuid().ToString(),
            Url = "ws://127.0.0.1:18789", IsLocal = true,
            NativePackageFamilyName = "OpenClawFoundation.OpenClawGateway_fixture",
            SharedGatewayToken = "synthetic-native-token"
        });
        var others = new[]
        {
            new GatewayRecord { Id = "gw-remote-fixture", Url = "wss://remote.example.test", SharedGatewayToken = "synthetic-remote-token" },
            new GatewayRecord { Id = Guid.NewGuid().ToString(), Url = "ws://127.0.0.1:18790", IsLocal = true, SetupManagedDistroName = "fixture-wsl" },
            new GatewayRecord { Id = Guid.NewGuid().ToString(), Url = "wss://manual.example.test", NativePackageFamilyName = native.NativePackageFamilyName },
            new GatewayRecord { Id = Guid.NewGuid().ToString(), Url = "ws://127.0.0.1:18791", IsLocal = true,
                NativePackageFamilyName = native.NativePackageFamilyName, SshTunnel = new("fixture", "remote.example.test", 18789, 18791) }
        };
        foreach (var other in others) registry.AddOrUpdate(other);
        registry.SetActive(native.Id);
        registry.Save();
        var keyPath = Path.Combine(registry.GetIdentityDirectory(native.Id), "device-key-ed25519.json");
        new DeviceIdentity(registry.GetIdentityDirectory(native.Id)).Initialize();
        var keyBefore = File.ReadAllText(keyPath);
        var rootKeyPath = Path.Combine(fixture.Data, "device-key-ed25519.json");
        new DeviceIdentity(fixture.Data).Initialize();
        var rootBefore = File.ReadAllText(rootKeyPath);
        var legacyUrl = scenario == "unrelated-settings" ? "wss://unrelated.example.test" : "ws://localhost:18789";
        File.WriteAllText(fixture.SettingsPath, JsonSerializer.Serialize(new
        {
            PreferredGatewayId = native.Id, Theme = "Dark", GatewayUrl = legacyUrl,
            Token = "synthetic-legacy-token", BootstrapToken = "synthetic-bootstrap-token",
            UseSshTunnel = scenario == "ssh-settings"
        }));
        var registryPath = Path.Combine(fixture.Data, "gateways.json");
        var registryBefore = File.ReadAllText(registryPath);
        var marker = Path.Combine(fixture.Cleanup, "native-owner.txt");
        File.WriteAllText(marker, "fixture-installation");
        File.WriteAllText(fixture.OwnedPath, JsonSerializer.Serialize(new
        {
            OwnNativePackage = true, NativeFamily = native.NativePackageFamilyName,
            NativeInstallationId = "fixture-installation"
        }));
        using var source = typeof(GatewayPackageCleanup).Assembly.GetManifestResourceStream(
            "OpenClaw.SetupEngine.GatewayPackageCleanup.ps1");
        Assert.NotNull(source);
        using (var reader = new StreamReader(source))
            File.WriteAllText(Path.Combine(fixture.Cleanup, "worker.ps1"), await reader.ReadToEndAsync());
        File.WriteAllText(Path.Combine(fixture.Cleanup, "input.json"), JsonSerializer.Serialize(new
        {
            Scenario = scenario, Data = fixture.Data, Id = native.Id, Marker = marker
        }));
        var harness = Path.Combine(fixture.Cleanup, "native-test.ps1");
        File.WriteAllText(harness, """
            $ErrorActionPreference = 'Stop'
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $PSScriptRoot 'worker.ps1'), [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw 'Worker parsing failed.' }
            foreach ($function in $ast.FindAll({ param($n)
                $n -is [Management.Automation.Language.FunctionDefinitionAst]
            }, $true)) { . ([scriptblock]::Create($function.Extent.Text)) }
            $inputData = Get-Content (Join-Path $PSScriptRoot 'input.json') -Raw | ConvertFrom-Json
            $scenario = $inputData.Scenario
            $root = $PSScriptRoot
            $recordPath = Join-Path $root 'owned.json'
            $record = Get-Content $recordPath -Raw | ConvertFrom-Json
            $Family = 'OpenClawFoundation.OpenClaw_fixture'
            $registryPath = Join-Path $inputData.Data 'gateways.json'
            $keyDir = Join-Path (Join-Path $inputData.Data 'gateways') $inputData.Id
            $keyPath = Join-Path $keyDir 'device-key-ed25519.json'
            $script:installed = $true
            $script:removals = 0
            $script:injected = $false
            function Get-AppxPackage {
                param($Name, $PackageTypeFilter)
                if ($script:installed) {
                    [pscustomobject]@{ Name = 'OpenClawFoundation.OpenClawGateway'; PackageFamilyName = $record.NativeFamily; Publisher = 'CN=4BA40A7A-B719-4C40-BF91-84AF4F1136FC' }
                }
                if ($scenario -eq 'surviving-companion' -and -not $Name) {
                    [pscustomobject]@{ Name = 'OpenClawFoundation.OpenClaw.Dev'; PackageFamilyName = 'other-companion' }
                }
            }
            function Remove-AppxPackage {
                [CmdletBinding()] param([Parameter(ValueFromPipeline)]$Package)
                process {
                    if ($scenario -eq 'remove-failed') { throw 'Synthetic package removal failure.' }
                    $script:removals++
                    $script:installed = $false
                    Remove-Item -LiteralPath $inputData.Marker
                    if ($scenario -in @('removed-then-interrupted', 'record-replaced', 'identity-replaced', 'registration-refresh') -and -not $script:injected) {
                        $script:injected = $true
                        throw 'Synthetic interruption after removal.'
                    }
                }
            }
            $writeJson = (Get-Item Function:Write-CleanupJson).ScriptBlock
            function Write-CleanupJson([string]$path, $value) {
                if (-not $script:injected -and (
                    ($scenario -eq 'identity-retired-then-interrupted' -and $path -eq $registryPath) -or
                    ($scenario -eq 'registry-retired-then-interrupted' -and $path -eq $recordPath -and -not $value.OwnNativePackage))) {
                    $script:injected = $true
                    throw 'Synthetic persistence failure.'
                }
                & $writeJson $path $value
            }
            switch ($scenario) {
                'unknown-files' { 'keep' | Set-Content (Join-Path $keyDir 'unrelated.txt') }
                'package-replaced' { 'replacement-installation' | Set-Content -LiteralPath $inputData.Marker }
                'invalid-registry' { '{' | Set-Content -LiteralPath $registryPath }
                'invalid-settings' { 'null' | Set-Content (Join-Path $inputData.Data 'settings.json') }
                'unsafe-id' {
                    $saved = Get-Content $registryPath -Raw | ConvertFrom-Json
                    $saved.gateways[0].id = '../outside'
                    Write-CleanupJson $registryPath $saved
                }
                'redirected-identity' {
                    $outside = Join-Path $root 'outside'
                    Move-Item -LiteralPath $keyDir -Destination $outside
                    New-Item -ItemType Junction -Path $keyDir -Target $outside | Out-Null
                }
            }
            $failure = $null
            try { Remove-OwnedNativeGateway $inputData.Data $inputData.Marker }
            catch { $failure = $_.Exception.Message }
            $pending = Get-Content $recordPath -Raw
            if ($scenario -in @('removed-then-interrupted', 'identity-retired-then-interrupted', 'registry-retired-then-interrupted', 'record-replaced', 'identity-replaced', 'registration-refresh')) {
                if (-not $failure) { throw 'Expected the injected failure before retry.' }
                if ($scenario -eq 'record-replaced') {
                    $saved = Get-Content $registryPath -Raw | ConvertFrom-Json
                    $saved.gateways[0].sharedGatewayToken = 'synthetic-replacement-token'
                    Write-CleanupJson $registryPath $saved
                }
                if ($scenario -eq 'identity-replaced') { 'replacement-key' | Set-Content -LiteralPath $keyPath }
                # Resume from persisted intent, not the first invocation's mutated object.
                $record = Get-Content $recordPath -Raw | ConvertFrom-Json
                if ($scenario -eq 'registration-refresh') {
                    $registration = @($ast.FindAll({ param($n)
                        $n -is [Management.Automation.Language.IfStatementAst]
                    }, $true) | ForEach-Object { $_.Clauses } | Where-Object { $_.Item1.Extent.Text -eq '$Register' })
                    if ($registration.Count -ne 1) { throw 'Expected exactly one registration branch.' }
                    $branch = $registration[0].Item2.Extent.Text
                    $refused = $false
                    try { . ([scriptblock]::Create($branch.Substring(1, $branch.Length - 2))) }
                    catch {
                        if ($_.Exception.Message -notlike '*ownership cannot be refreshed*') { throw }
                        $refused = $true
                    }
                    if (-not $refused -or (Get-Content $recordPath -Raw) -cne $pending) {
                        throw 'Registration did not preserve the pending native cleanup intent.'
                    }
                }
                $failure = $null
                try { Remove-OwnedNativeGateway $inputData.Data $inputData.Marker }
                catch { $failure = $_.Exception.Message }
            }
            [pscustomobject]@{ Failure = $failure; Removals = $script:removals; Pending = $pending } | ConvertTo-Json -Depth 5
            """);
        using var logger = new SetupLogger(filePath: null);
        var result = await new CommandRunner(logger).RunAsync(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-File", harness], WorkerTimeout);
        Assert.False(result.TimedOut);
        Assert.True(result.ExitCode == 0, result.Stderr);
        using var output = JsonDocument.Parse(result.Stdout);
        var completed = scenario is "removed" or "unknown-files" or "removed-then-interrupted"
            or "identity-retired-then-interrupted" or "registry-retired-then-interrupted"
            or "legacy-id" or "registration-refresh" or "ssh-settings" or "unrelated-settings";
        Assert.Equal(rootBefore, File.ReadAllText(rootKeyPath));
        Assert.DoesNotContain("synthetic-native-token", output.RootElement.GetProperty("Pending").GetString());
        Assert.DoesNotContain("PrivateKey", output.RootElement.GetProperty("Pending").GetString());
        if (completed)
        {
            Assert.Equal(JsonValueKind.Null, output.RootElement.GetProperty("Failure").ValueKind);
            Assert.Equal(1, output.RootElement.GetProperty("Removals").GetInt32());
            Assert.False(File.Exists(keyPath));
            Assert.Equal(scenario == "unknown-files", Directory.Exists(Path.GetDirectoryName(keyPath)));
            registry.Load();
            Assert.Equal(others, registry.GetAll());
            Assert.Null(registry.ActiveGatewayId);
            using var settings = JsonDocument.Parse(File.ReadAllText(fixture.SettingsPath));
            Assert.Equal(JsonValueKind.Null, settings.RootElement.GetProperty("PreferredGatewayId").ValueKind);
            Assert.Equal("Dark", settings.RootElement.GetProperty("Theme").GetString());
            if (scenario is "ssh-settings" or "unrelated-settings")
            {
                Assert.Equal(legacyUrl, settings.RootElement.GetProperty("GatewayUrl").GetString());
                Assert.Equal("synthetic-legacy-token", settings.RootElement.GetProperty("Token").GetString());
            }
            else
            {
                var savedUrl = settings.RootElement.GetProperty("GatewayUrl").GetString();
                Assert.Equal("", savedUrl);
                Assert.False(settings.RootElement.TryGetProperty("Token", out _));
                Assert.False(settings.RootElement.TryGetProperty("BootstrapToken", out _));
                Assert.False(registry.MigrateFromSettings(savedUrl, null, null, false, null, null, 18789, 18789, fixture.Data));
                Assert.Equal(others, registry.GetAll());
                Assert.Null(registry.ActiveGatewayId);
            }
            Assert.False(File.Exists(fixture.ReceiptPath));
        }
        else
        {
            if (scenario != "package-replaced")
                Assert.Equal(JsonValueKind.String, output.RootElement.GetProperty("Failure").ValueKind);
            Assert.Equal(scenario == "identity-replaced" ? "replacement-key" : keyBefore,
                scenario == "identity-replaced" ? File.ReadAllText(keyPath).Trim() : File.ReadAllText(keyPath));
            if (scenario is not ("invalid-registry" or "unsafe-id" or "record-replaced"))
                Assert.Equal(registryBefore, File.ReadAllText(registryPath));
            if (scenario is "record-replaced" or "identity-replaced")
                Assert.Contains("NativeDataRemoval", File.ReadAllText(fixture.OwnedPath));
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

        public Task<bool> InitializeAsync(Func<string, bool>? preserve = null) =>
            GatewayPackageCleanup.InitializeAsync((register, keep) =>
            {
                Calls.Add((register, keep));
                return Task.FromResult<string?>(Cleanup);
            }, Data, Local, preserve: preserve);

        public void Dispose() => _temp.Dispose();
    }
}
