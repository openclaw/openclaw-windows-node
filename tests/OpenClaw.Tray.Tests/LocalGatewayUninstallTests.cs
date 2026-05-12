using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClawTray.Services;
using OpenClawTray.Services.Connection;
using OpenClawTray.Services.LocalGatewaySetup;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Unit tests for <see cref="LocalGatewayUninstall"/> core engine.
/// All tests use isolated temp directories via OPENCLAW_TRAY_DATA_DIR /
/// OPENCLAW_TRAY_LOCALAPPDATA_DIR to avoid touching the real user profile.
/// </summary>
public sealed class LocalGatewayUninstallTests
{
    // -----------------------------------------------------------------------
    // Helper: isolated temp test environment
    // -----------------------------------------------------------------------

    private sealed class UninstallTestEnv : IDisposable
    {
        public string DataDir { get; }      // replaces %APPDATA%\OpenClawTray
        public string LocalDataDir { get; } // replaces %LOCALAPPDATA%\OpenClawTray
        private readonly string _prevDataDir;
        private readonly string _prevLocalDataDir;

        public SettingsManager Settings { get; }

        public UninstallTestEnv()
        {
            var root = Path.Combine(Path.GetTempPath(), "oc-uninstall-tests-" + Guid.NewGuid().ToString("N"));
            DataDir = Path.Combine(root, "appdata");
            LocalDataDir = Path.Combine(root, "localappdata");
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(LocalDataDir);

            _prevDataDir = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") ?? "";
            _prevLocalDataDir = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR") ?? "";

            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", DataDir);
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR", LocalDataDir);

            Settings = new SettingsManager(DataDir);
        }

        /// <summary>
        /// Builds an uninstall engine wired to the isolated dirs.
        /// </summary>
        public LocalGatewayUninstall BuildEngine(
            IWslCommandRunner? wsl = null,
            GatewayRegistry? registry = null)
            => LocalGatewayUninstall.Build(
                Settings,
                wsl: wsl ?? new FakeWslCommandRunner(),
                identityDataPath: DataDir,
                localDataPath: LocalDataDir,
                registry: registry);

        /// <summary>
        /// Creates a GatewayRegistry rooted at DataDir, adds the supplied records,
        /// optionally sets active, persists to gateways.json, and creates per-record
        /// identity directories on disk. Returns the (loaded) registry.
        /// </summary>
        public GatewayRegistry SeedRegistry(
            IEnumerable<GatewayRecord> records,
            string? activeId = null,
            bool createIdentityDirs = true)
        {
            var registry = new GatewayRegistry(DataDir);
            foreach (var r in records)
            {
                registry.AddOrUpdate(r);
                if (createIdentityDirs)
                {
                    var dir = registry.GetIdentityDirectory(r.Id);
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "device-key-ed25519.json"), "{}");
                }
            }
            if (activeId != null)
                registry.SetActive(activeId);
            registry.Save();
            return registry;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR",
                string.IsNullOrEmpty(_prevDataDir) ? null : _prevDataDir);
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR",
                string.IsNullOrEmpty(_prevLocalDataDir) ? null : _prevLocalDataDir);

            try { Directory.Delete(Path.GetDirectoryName(DataDir)!, recursive: true); }
            catch { /* best-effort test cleanup */ }
        }

        // -----------------------------------------------------------------------
        // Convenience: write a device-key file with or without a token
        // -----------------------------------------------------------------------
        public string DeviceKeyPath => Path.Combine(DataDir, "device-key-ed25519.json");

        public void WriteDeviceKey(string? deviceToken, string? nodeDeviceToken = null)
        {
            var data = new
            {
                PrivateKeyBase64 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                PublicKeyBase64 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                DeviceId = "test-device-id",
                Algorithm = "Ed25519",
                DeviceToken = deviceToken,
                NodeDeviceToken = nodeDeviceToken,
                CreatedAt = 0L
            };
            File.WriteAllText(DeviceKeyPath,
                JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }

        public string SetupStatePath => Path.Combine(LocalDataDir, "setup-state.json");
        public string McpTokenPath => Path.Combine(DataDir, "mcp-token.txt");
        public string ExecPolicyPath => Path.Combine(LocalDataDir, "exec-policy.json");

        // Round 2 (Scott #7): logs may live in either of two locations.
        // LogsDir / LogPath are the SettingsPage "View Logs" target under
        // %LOCALAPPDATA%; AppDataLogsDir / AppDataLogPath are where
        // DiagnosticsJsonlService actually writes today under %APPDATA%.
        public string LogsDir => Path.Combine(LocalDataDir, "Logs");
        public string LogPath => Path.Combine(LogsDir, "diagnostics.jsonl");
        public string AppDataLogsDir => Path.Combine(DataDir, "Logs");
        public string AppDataLogPath => Path.Combine(AppDataLogsDir, "diagnostics.jsonl");
    }

    // -----------------------------------------------------------------------
    // Fake WSL runner (reuse definition from LocalGatewaySetupTests)
    // -----------------------------------------------------------------------

    private sealed class FakeWslCommandRunner : IWslCommandRunner
    {
        public System.Collections.Generic.List<WslDistroInfo> Distros { get; set; } = [];
        public System.Collections.Generic.List<string> UnregisteredDistros { get; } = [];

        public Task<System.Collections.Generic.IReadOnlyList<WslDistroInfo>> ListDistrosAsync(
            System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult<System.Collections.Generic.IReadOnlyList<WslDistroInfo>>(
                Distros.ToArray());

        public Task<WslCommandResult> RunAsync(
            System.Collections.Generic.IReadOnlyList<string> arguments,
            System.Threading.CancellationToken cancellationToken = default,
            System.Collections.Generic.IReadOnlyDictionary<string, string>? environment = null)
            => Task.FromResult(new WslCommandResult(0, "", ""));

        public Task<WslCommandResult> RunInDistroAsync(
            string name,
            System.Collections.Generic.IReadOnlyList<string> command,
            System.Threading.CancellationToken cancellationToken = default,
            System.Collections.Generic.IReadOnlyDictionary<string, string>? environment = null)
            => Task.FromResult(new WslCommandResult(0, "", ""));

        public Task<WslCommandResult> TerminateDistroAsync(
            string name,
            System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(new WslCommandResult(0, "", ""));

        public Task<WslCommandResult> UnregisterDistroAsync(
            string name,
            System.Threading.CancellationToken cancellationToken = default)
        {
            UnregisteredDistros.Add(name);
            Distros.RemoveAll(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new WslCommandResult(0, "", ""));
        }
    }

    // -----------------------------------------------------------------------
    // Test 1: DryRun never destroys
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task DryRun_NeverDestroys_FileSystemAndRegistryUntouched()
    {
        using var env = new UninstallTestEnv();
        env.WriteDeviceKey("test-token-abc");
        File.WriteAllText(env.SetupStatePath, """{"Phase":"Complete"}""");
        File.WriteAllText(env.McpTokenPath, "mcp-secret");
        File.WriteAllText(env.ExecPolicyPath, "{}");
        Directory.CreateDirectory(env.LogsDir);
        File.WriteAllText(env.LogPath, "log content");

        // Write legacy-format settings.json so SettingsManager.Load() populates LegacyToken
        File.WriteAllText(Path.Combine(env.DataDir, "settings.json"),
            """{"Token":"gateway-token","AutoStart":true,"GatewayUrl":"ws://localhost:18789"}""");
        env.Settings.Load();

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(
            new LocalGatewayUninstallOptions { DryRun = true });

        // All steps should be DryRun (or Skipped for mcp-token preserve); none Executed
        var realSteps = result.Steps.Where(s =>
            s.Status is UninstallStepStatus.Executed or UninstallStepStatus.Failed).ToList();
        Assert.Empty(realSteps);

        // Nothing destroyed
        Assert.True(File.Exists(env.DeviceKeyPath));
        Assert.True(File.Exists(env.SetupStatePath));
        Assert.True(File.Exists(env.McpTokenPath));
        Assert.True(File.Exists(env.ExecPolicyPath));
        Assert.True(File.Exists(env.LogPath));

        // Settings not mutated by DryRun
        var reloaded = new SettingsManager(env.DataDir);
        Assert.Equal("gateway-token", reloaded.LegacyToken);
        Assert.True(reloaded.AutoStart);
    }

    // -----------------------------------------------------------------------
    // Test 2: Preflight throws when ConfirmDestructive=false and DryRun=false
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_Throws_WhenConfirmDestructiveFalseAndDryRunFalse()
    {
        using var env = new UninstallTestEnv();
        var engine = env.BuildEngine();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.RunAsync(new LocalGatewayUninstallOptions
            {
                DryRun = false,
                ConfirmDestructive = false
            }));
    }

    // -----------------------------------------------------------------------
    // Test 3: Idempotency — absent setup-state.json records Skipped, not Failed
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task SetupState_Absent_StepIsSkipped()
    {
        using var env = new UninstallTestEnv();
        Assert.False(File.Exists(env.SetupStatePath));

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        var step = result.Steps.FirstOrDefault(s => s.Name == "Delete setup-state.json");
        Assert.NotNull(step);
        Assert.Equal(UninstallStepStatus.Skipped, step.Status);
        Assert.DoesNotContain(result.Errors, e => e.Contains("setup-state", StringComparison.OrdinalIgnoreCase));
    }

    // -----------------------------------------------------------------------
    // Test 4: Idempotency — absent distro records Skipped, not Failed
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task WslDistro_Absent_UnregisterIsSkipped()
    {
        using var env = new UninstallTestEnv();
        var runner = new FakeWslCommandRunner(); // Distros is empty
        var engine = env.BuildEngine(runner);

        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        var step = result.Steps.FirstOrDefault(s => s.Name == "Unregister WSL distro");
        Assert.NotNull(step);
        Assert.Equal(UninstallStepStatus.Skipped, step.Status);
        Assert.Empty(runner.UnregisteredDistros);
    }

    // -----------------------------------------------------------------------
    // Test 5: Autostart ordering — settings persist BEFORE registry delete
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task Autostart_SettingsPersistedBeforeRegistryDelete()
    {
        using var env = new UninstallTestEnv();
        env.Settings.AutoStart = true;
        env.Settings.Save();

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        var stepNames = result.Steps.Select(s => s.Name).ToList();
        var persistIdx = stepNames.IndexOf("Persist settings (AutoStart=false)");
        var registryIdx = stepNames.IndexOf("Delete autostart registry");

        Assert.True(persistIdx >= 0, "Expected 'Persist settings (AutoStart=false)' step");
        Assert.True(registryIdx >= 0, "Expected 'Delete autostart registry' step");
        Assert.True(persistIdx < registryIdx,
            $"Settings persist (index {persistIdx}) must precede registry delete (index {registryIdx})");

        // settings.AutoStart is false after uninstall
        var reloaded = new SettingsManager(env.DataDir);
        Assert.False(reloaded.AutoStart);
    }

    // -----------------------------------------------------------------------
    // Test 6: Device-key step nulls token but preserves file
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task DeviceKey_TokensNulled_FilePreserved_OtherFieldsIntact()
    {
        using var env = new UninstallTestEnv();
        env.WriteDeviceKey("my-operator-token", "my-node-token");
        Assert.True(File.Exists(env.DeviceKeyPath));

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        // File still exists
        Assert.True(File.Exists(env.DeviceKeyPath));

        // DeviceToken is null
        Assert.False(DeviceIdentity.HasStoredDeviceToken(env.DataDir));
        Assert.False(DeviceIdentity.HasStoredDeviceTokenForRole(env.DataDir, "node"));

        // DeviceId and Algorithm fields are preserved
        using var doc = JsonDocument.Parse(File.ReadAllText(env.DeviceKeyPath));
        Assert.True(doc.RootElement.TryGetProperty("DeviceId", out var idEl));
        Assert.Equal("test-device-id", idEl.GetString());
        Assert.True(doc.RootElement.TryGetProperty("Algorithm", out var algEl));
        Assert.Equal("Ed25519", algEl.GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("DeviceToken").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("NodeDeviceToken").ValueKind);

        // Step records Executed
        var step = result.Steps.FirstOrDefault(s => s.Name == "Null device tokens");
        Assert.NotNull(step);
        Assert.Equal(UninstallStepStatus.Executed, step.Status);
    }

    // -----------------------------------------------------------------------
    // Test 7: Device-key step skipped when token already null
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task DeviceKey_AlreadyNull_StepIsSkipped()
    {
        using var env = new UninstallTestEnv();
        env.WriteDeviceKey(null); // token already null
        Assert.False(DeviceIdentity.HasStoredDeviceToken(env.DataDir));

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        var step = result.Steps.FirstOrDefault(s => s.Name == "Null device tokens");
        Assert.NotNull(step);
        Assert.Equal(UninstallStepStatus.Skipped, step.Status);
    }

    // -----------------------------------------------------------------------
    // Test 8: Device-key step skipped when file absent
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task DeviceKey_FileAbsent_StepIsSkipped()
    {
        using var env = new UninstallTestEnv();
        Assert.False(File.Exists(env.DeviceKeyPath));

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        var step = result.Steps.FirstOrDefault(s => s.Name == "Null device tokens");
        Assert.NotNull(step);
        Assert.Equal(UninstallStepStatus.Skipped, step.Status);
    }

    // -----------------------------------------------------------------------
    // Test 9: mcp-token.txt is NEVER deleted, even in destructive mode
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task McpToken_NeverDeleted_EvenDestructive()
    {
        using var env = new UninstallTestEnv();
        var mcpContent = "super-secret-mcp-token";
        File.WriteAllText(env.McpTokenPath, mcpContent);

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        // File still exists with original content
        Assert.True(File.Exists(env.McpTokenPath));
        Assert.Equal(mcpContent, File.ReadAllText(env.McpTokenPath));

        // The preserve step is present
        var step = result.Steps.FirstOrDefault(s => s.Name == "Preserve mcp-token.txt");
        Assert.NotNull(step);
        Assert.Equal(UninstallStepStatus.Skipped, step.Status); // logged as no-op
    }

    // -----------------------------------------------------------------------
    // Test 10: Distro-name guard refuses non-OpenClawGateway distro names
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task DistroNameGuard_RefusesNonAllowedName()
    {
        using var env = new UninstallTestEnv();
        var runner = new FakeWslCommandRunner
        {
            Distros = [new WslDistroInfo("Ubuntu", "Running", 2)]
        };
        var engine = env.BuildEngine(runner);

        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true,
            DistroName = "Ubuntu" // not allowed
        });

        var step = result.Steps.FirstOrDefault(s => s.Name == "Unregister WSL distro");
        Assert.NotNull(step);
        Assert.Equal(UninstallStepStatus.Failed, step.Status);
        Assert.Contains("Ubuntu", step.Detail);
        Assert.Empty(runner.UnregisteredDistros);
    }

    // -----------------------------------------------------------------------
    // Test 11: Distro unregistered when present and name is OpenClawGateway
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task WslDistro_Unregistered_WhenPresent()
    {
        using var env = new UninstallTestEnv();
        var runner = new FakeWslCommandRunner
        {
            Distros = [new WslDistroInfo("OpenClawGateway", "Running", 2)]
        };
        var engine = env.BuildEngine(runner);

        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        Assert.Contains("OpenClawGateway", runner.UnregisteredDistros);
        var step = result.Steps.FirstOrDefault(s => s.Name == "Unregister WSL distro");
        Assert.NotNull(step);
        Assert.Equal(UninstallStepStatus.Executed, step.Status);
    }

    // -----------------------------------------------------------------------
    // Test 12: Postcondition SetupStateAbsent reflects actual filesystem state
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task Postconditions_SetupStateAbsent_MatchesFilesystem()
    {
        using var env = new UninstallTestEnv();
        File.WriteAllText(env.SetupStatePath, """{"Phase":"Complete"}""");

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        Assert.False(File.Exists(env.SetupStatePath));
        Assert.True(result.Postconditions.SetupStateAbsent);
    }

    // -----------------------------------------------------------------------
    // Test 13: Postcondition DeviceTokenCleared matches state
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task Postconditions_DeviceTokenCleared_AfterDestructiveRun()
    {
        using var env = new UninstallTestEnv();
        env.WriteDeviceKey("my-operator-token", "my-node-token");

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        Assert.True(result.Postconditions.DeviceTokenCleared);
    }

    // -----------------------------------------------------------------------
    // Test 14: PreserveLogs=true (default) — Logs/ directories NOT deleted
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task Logs_NotDeleted_WhenPreserveLogsTrue()
    {
        using var env = new UninstallTestEnv();
        Directory.CreateDirectory(env.LogsDir);
        File.WriteAllText(env.LogPath, "diagnostics");
        Directory.CreateDirectory(env.AppDataLogsDir);
        File.WriteAllText(env.AppDataLogPath, "diagnostics");

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true,
            PreserveLogs = true
        });

        Assert.True(File.Exists(env.LogPath));
        Assert.True(File.Exists(env.AppDataLogPath));
        var step = result.Steps.FirstOrDefault(s => s.Name == "Delete gateway logs");
        Assert.NotNull(step);
        Assert.Equal(UninstallStepStatus.Skipped, step.Status);
    }

    // -----------------------------------------------------------------------
    // Test 15: PreserveLogs=false — both Logs/ directories ARE deleted
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task Logs_Deleted_WhenPreserveLogsFalse()
    {
        using var env = new UninstallTestEnv();
        Directory.CreateDirectory(env.LogsDir);
        File.WriteAllText(env.LogPath, "diagnostics");
        Directory.CreateDirectory(env.AppDataLogsDir);
        File.WriteAllText(env.AppDataLogPath, "diagnostics");

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true,
            PreserveLogs = false
        });

        Assert.False(Directory.Exists(env.LogsDir));
        Assert.False(Directory.Exists(env.AppDataLogsDir));
        var step = result.Steps.FirstOrDefault(s => s.Name == "Delete gateway logs");
        Assert.NotNull(step);
        Assert.Equal(UninstallStepStatus.Executed, step.Status);
        Assert.Contains("2", step.Detail); // "Deleted 2 log directory(ies)."
    }

    // -----------------------------------------------------------------------
    // Test 15b: PreserveLogs=false, no Logs/ dirs present — step Skipped
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task Logs_Step_Skipped_WhenNoLogsDirectoryExists()
    {
        using var env = new UninstallTestEnv();
        // No Logs/ dirs created.

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true,
            PreserveLogs = false
        });

        var step = result.Steps.FirstOrDefault(s => s.Name == "Delete gateway logs");
        Assert.NotNull(step);
        Assert.Equal(UninstallStepStatus.Skipped, step.Status);
        Assert.Contains("No log directories present", step.Detail);
    }

    // -----------------------------------------------------------------------
    // Test 16: PreserveExecPolicy=false — exec-policy.json deleted
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task ExecPolicy_Deleted_WhenPreserveExecPolicyFalse()
    {
        using var env = new UninstallTestEnv();
        File.WriteAllText(env.ExecPolicyPath, "{}");

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true,
            PreserveExecPolicy = false
        });

        Assert.False(File.Exists(env.ExecPolicyPath));
        var step = result.Steps.FirstOrDefault(s => s.Name == "Delete exec-policy.json");
        Assert.NotNull(step);
        Assert.Equal(UninstallStepStatus.Executed, step.Status);
    }

    // -----------------------------------------------------------------------
    // Test 17: Onboarding settings cleared; EnableMcpServer preserved
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task OnboardingSettings_Cleared_EnableMcpServerPreserved()
    {
        using var env = new UninstallTestEnv();
        // Write legacy-format settings.json so SettingsManager.Load() populates LegacyToken/LegacyBootstrapToken
        File.WriteAllText(Path.Combine(env.DataDir, "settings.json"),
            """{"Token":"tok","BootstrapToken":"btok","GatewayUrl":"ws://custom:9999","EnableNodeMode":true,"EnableMcpServer":true}""");
        env.Settings.Load();

        var engine = env.BuildEngine();
        await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        var reloaded = new SettingsManager(env.DataDir);
        // Save() no longer writes Token/BootstrapToken — they're absent from the new JSON,
        // so a fresh Load() produces null for both legacy credential fields.
        Assert.True(string.IsNullOrEmpty(reloaded.LegacyToken));
        Assert.True(string.IsNullOrEmpty(reloaded.LegacyBootstrapToken));
        Assert.Equal("ws://localhost:18789", reloaded.GatewayUrl);
        Assert.False(reloaded.EnableNodeMode);
        // EnableMcpServer must be preserved
        Assert.True(reloaded.EnableMcpServer);
    }

    // -----------------------------------------------------------------------
    // Test 18: McpTokenPreserved postcondition always true
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task Postconditions_McpTokenPreserved_AlwaysTrue()
    {
        using var env = new UninstallTestEnv();

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        Assert.True(result.Postconditions.McpTokenPreserved);
    }

    // -----------------------------------------------------------------------
    // Test 19: TryClearDeviceToken static helper (unit)
    // -----------------------------------------------------------------------

    [WindowsFact]
    public void TryClearDeviceToken_ReturnsFalse_WhenFileAbsent()
    {
        using var env = new UninstallTestEnv();
        Assert.False(DeviceIdentity.TryClearDeviceToken(env.DataDir));
    }

    [WindowsFact]
    public void TryClearDeviceToken_ReturnsFalse_WhenTokenAlreadyNull()
    {
        using var env = new UninstallTestEnv();
        env.WriteDeviceKey(null);
        Assert.False(DeviceIdentity.TryClearDeviceToken(env.DataDir));
    }

    [WindowsFact]
    public void TryClearDeviceToken_ReturnsTrue_AndNullsToken()
    {
        using var env = new UninstallTestEnv();
        env.WriteDeviceKey("some-token");
        Assert.True(DeviceIdentity.TryClearDeviceToken(env.DataDir));
        Assert.False(DeviceIdentity.HasStoredDeviceToken(env.DataDir));
        Assert.True(File.Exists(env.DeviceKeyPath));
    }

    [WindowsFact]
    public void TryClearDeviceToken_Idempotent_SecondCallReturnsFalse()
    {
        using var env = new UninstallTestEnv();
        env.WriteDeviceKey("some-token");
        Assert.True(DeviceIdentity.TryClearDeviceToken(env.DataDir));
        Assert.False(DeviceIdentity.TryClearDeviceToken(env.DataDir)); // second call = no-op
    }

    [WindowsFact]
    public void TryClearDeviceTokenForRole_Node_ReturnsTrue_AndNullsNodeTokenOnly()
    {
        using var env = new UninstallTestEnv();
        env.WriteDeviceKey("operator-token", "node-token");

        Assert.True(DeviceIdentity.TryClearDeviceTokenForRole(env.DataDir, "node"));
        Assert.Equal("operator-token", DeviceIdentity.TryReadStoredDeviceToken(env.DataDir));
        Assert.False(DeviceIdentity.HasStoredDeviceTokenForRole(env.DataDir, "node"));
    }

    // -----------------------------------------------------------------------
    // Test 20: Step 13 (Compute postconditions) always runs, even after errors
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task Postconditions_AlwaysComputed_EvenWhenPriorStepsFail()
    {
        using var env = new UninstallTestEnv();
        // Engine with a runner that always has no distros — simulates partial failure scenario
        var engine = env.BuildEngine(new FakeWslCommandRunner());
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true,
            // Disable guards to exercise step branches
            PreserveLogs = false,
            PreserveExecPolicy = false,
            DistroName = "OpenClawGateway"
        });

        var postconditionStep = result.Steps.FirstOrDefault(s => s.Name == "Compute postconditions");
        Assert.NotNull(postconditionStep);
        Assert.Equal(UninstallStepStatus.Executed, postconditionStep.Status);
        Assert.NotNull(result.Postconditions);
    }

    // -----------------------------------------------------------------------
    // Test 21: VHD parent-dir cleanup — directory present → Executed + deleted
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task VhdDirCleanup_DirectoryPresent_ExecutedAndDeleted()
    {
        using var env = new UninstallTestEnv();
        // Create the VHD parent dir structure
        var vhdDir = Path.Combine(env.LocalDataDir, "wsl", "OpenClawGateway");
        Directory.CreateDirectory(vhdDir);
        File.WriteAllText(Path.Combine(vhdDir, "ext4.vhdx"), "fake vhd");
        Assert.True(Directory.Exists(vhdDir));

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        Assert.False(Directory.Exists(vhdDir));
        var step = result.Steps.FirstOrDefault(s => s.Name == "VHD parent dir cleanup");
        Assert.NotNull(step);
        Assert.Equal(UninstallStepStatus.Executed, step.Status);
        Assert.True(result.Postconditions.VhdDirAbsent);
    }

    // -----------------------------------------------------------------------
    // Test 22: VHD parent-dir cleanup — directory absent → Skipped (idempotent)
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task VhdDirCleanup_DirectoryAbsent_Skipped()
    {
        using var env = new UninstallTestEnv();
        var vhdDir = Path.Combine(env.LocalDataDir, "wsl", "OpenClawGateway");
        Assert.False(Directory.Exists(vhdDir));

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        var step = result.Steps.FirstOrDefault(s => s.Name == "VHD parent dir cleanup");
        Assert.NotNull(step);
        Assert.Equal(UninstallStepStatus.Skipped, step.Status);
        Assert.True(result.Postconditions.VhdDirAbsent);
    }

    // -----------------------------------------------------------------------
    // Test 23: VhdDirAbsent postcondition in DryRun remains default (false)
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task VhdDirCleanup_DryRun_StepRecordedNotExecuted()
    {
        using var env = new UninstallTestEnv();
        var vhdDir = Path.Combine(env.LocalDataDir, "wsl", "OpenClawGateway");
        Directory.CreateDirectory(vhdDir);

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(
            new LocalGatewayUninstallOptions { DryRun = true });

        Assert.True(Directory.Exists(vhdDir)); // not deleted in DryRun
        var step = result.Steps.FirstOrDefault(s => s.Name == "VHD parent dir cleanup");
        Assert.NotNull(step);
        Assert.Equal(UninstallStepStatus.DryRun, step.Status);
    }

    // -----------------------------------------------------------------------
    // Test 24: run.marker cleanup — file present → Executed + deleted
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task RunMarker_Present_ExecutedAndDeleted()
    {
        using var env = new UninstallTestEnv();
        var markerPath = Path.Combine(env.LocalDataDir, "run.marker");
        File.WriteAllText(markerPath, DateTime.Now.ToString("O"));
        Assert.True(File.Exists(markerPath));

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        Assert.False(File.Exists(markerPath));
        var step = result.Steps.FirstOrDefault(s => s.Name == "Delete run.marker");
        Assert.NotNull(step);
        Assert.Equal(UninstallStepStatus.Executed, step.Status);
    }

    // -----------------------------------------------------------------------
    // Test 25: run.marker cleanup — file absent → Skipped (idempotent)
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task RunMarker_Absent_Skipped()
    {
        using var env = new UninstallTestEnv();
        var markerPath = Path.Combine(env.LocalDataDir, "run.marker");
        Assert.False(File.Exists(markerPath));

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        var step = result.Steps.FirstOrDefault(s => s.Name == "Delete run.marker");
        Assert.NotNull(step);
        Assert.Equal(UninstallStepStatus.Skipped, step.Status);
    }

    // -----------------------------------------------------------------------
    // Test 26: Stopped distro — systemctl stop is skipped, no hang (BUG-02)
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task StoppedDistro_SystemctlStopSkipped_NoHang()
    {
        using var env = new UninstallTestEnv();
        // Distro is registered but Stopped — simulates the exact BUG-02 scenario.
        var runner = new FakeWslCommandRunner
        {
            Distros = [new WslDistroInfo("OpenClawGateway", "Stopped", 2)]
        };
        var engine = env.BuildEngine(runner);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });
        sw.Stop();

        // Must complete well under the old 30-second hang budget.
        Assert.True(sw.Elapsed.TotalSeconds < 10,
            $"Uninstall took {sw.Elapsed.TotalSeconds:F1}s; expected < 10s for a stopped distro.");

        // Step must be Skipped (not Executed) — we should NOT have called into WSL.
        var step = result.Steps.FirstOrDefault(s => s.Name == "Stop systemd gateway service");
        Assert.NotNull(step);
        Assert.Equal(UninstallStepStatus.Skipped, step.Status);
        Assert.Contains("not Running", step.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // Test 27: Wedged running distro — 5-second timeout fires, continues (BUG-02)
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task WedgedRunningDistro_SystemctlTimesOut_ContinuesUninstall()
    {
        using var env = new UninstallTestEnv();
        // Distro is Running but RunInDistroAsync hangs for longer than our 5-second timeout.
        var runner = new HangingWslCommandRunner(hangSeconds: 30);
        var engine = env.BuildEngine(runner);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });
        sw.Stop();

        // Inner timeout is 5 s; allow headroom for test executor jitter.
        Assert.True(sw.Elapsed.TotalSeconds < 15,
            $"Uninstall took {sw.Elapsed.TotalSeconds:F1}s; expected < 15s even with a wedged distro.");

        var step = result.Steps.FirstOrDefault(s => s.Name == "Stop systemd gateway service");
        Assert.NotNull(step);
        // Step is recorded as Executed (with timeout note), not Failed — uninstall continues.
        Assert.Equal(UninstallStepStatus.Executed, step.Status);
        Assert.Contains("timed out", step.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // HangingWslCommandRunner — simulates a Running distro whose wsl.exe
    // invocation never returns within the caller's timeout budget.
    // -----------------------------------------------------------------------

    private sealed class HangingWslCommandRunner : IWslCommandRunner
    {
        private readonly int _hangSeconds;

        public HangingWslCommandRunner(int hangSeconds) => _hangSeconds = hangSeconds;

        public Task<System.Collections.Generic.IReadOnlyList<WslDistroInfo>> ListDistrosAsync(
            System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult<System.Collections.Generic.IReadOnlyList<WslDistroInfo>>(
                [new WslDistroInfo("OpenClawGateway", "Running", 2)]);

        public Task<WslCommandResult> RunAsync(
            System.Collections.Generic.IReadOnlyList<string> arguments,
            System.Threading.CancellationToken cancellationToken = default,
            System.Collections.Generic.IReadOnlyDictionary<string, string>? environment = null)
            => Task.FromResult(new WslCommandResult(0, "", ""));

        public async Task<WslCommandResult> RunInDistroAsync(
            string name,
            System.Collections.Generic.IReadOnlyList<string> command,
            System.Threading.CancellationToken cancellationToken = default,
            System.Collections.Generic.IReadOnlyDictionary<string, string>? environment = null)
        {
            // Simulate a wedged distro: hang until cancelled or the timeout fires.
            await Task.Delay(TimeSpan.FromSeconds(_hangSeconds), cancellationToken);
            return new WslCommandResult(0, "", "");
        }

        public Task<WslCommandResult> TerminateDistroAsync(
            string name,
            System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(new WslCommandResult(0, "", ""));

        public Task<WslCommandResult> UnregisterDistroAsync(
            string name,
            System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(new WslCommandResult(0, "", ""));
    }

    // =======================================================================
    // PR #310 Blocker #2 / #3 — GatewayRegistry cleanup + postcondition-gated Success
    // =======================================================================

    private static GatewayRecord LocalRecord(string id, string url = "ws://localhost:18789", bool isLocal = true)
        => new() { Id = id, Url = url, IsLocal = isLocal };

    private static GatewayRecord RemoteRecord(string id, string url = "wss://gateway.example.com")
        => new() { Id = id, Url = url, IsLocal = false };

    // -----------------------------------------------------------------------
    // Run_LocalGatewayRecordsCleared_PostconditionTrue
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task Run_LocalGatewayRecordsCleared_PostconditionTrue()
    {
        using var env = new UninstallTestEnv();
        var registry = env.SeedRegistry([
            LocalRecord("local-1"),
            LocalRecord("local-2", url: "ws://127.0.0.1:18789"),
        ]);

        var engine = env.BuildEngine(registry: registry);
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        Assert.True(result.Postconditions.LocalGatewayRecordsAbsent);
        Assert.True(result.Postconditions.LocalGatewayIdentityDirsAbsent);
        Assert.True(result.Success, "expected Success=true; errors=" + string.Join(" | ", result.Errors));
        Assert.False(Directory.Exists(Path.Combine(env.DataDir, "gateways", "local-1")));
        Assert.False(Directory.Exists(Path.Combine(env.DataDir, "gateways", "local-2")));
    }

    // -----------------------------------------------------------------------
    // Run_OnlyLocalRecordsRemoved_RemoteGatewaysPreserved
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task Run_OnlyLocalRecordsRemoved_RemoteGatewaysPreserved()
    {
        using var env = new UninstallTestEnv();
        var registry = env.SeedRegistry([
            LocalRecord("local-1"),
            RemoteRecord("remote-1"),
        ]);

        var engine = env.BuildEngine(registry: registry);
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        // Reload from disk to assert persistence.
        var reloaded = new GatewayRegistry(env.DataDir);
        reloaded.Load();
        var ids = reloaded.GetAll().Select(r => r.Id).ToList();
        Assert.DoesNotContain("local-1", ids);
        Assert.Contains("remote-1", ids);
        Assert.True(Directory.Exists(Path.Combine(env.DataDir, "gateways", "remote-1")));
        Assert.False(Directory.Exists(Path.Combine(env.DataDir, "gateways", "local-1")));
        Assert.True(result.Postconditions.LocalGatewayRecordsAbsent);
    }

    // -----------------------------------------------------------------------
    // Run_LocalAndRemoteWithSameUrl_OnlyIsLocalRemoved
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task Run_LocalAndRemoteWithSameUrl_OnlyIsLocalRemoved()
    {
        using var env = new UninstallTestEnv();
        const string sharedUrl = "wss://gateway.example.com"; // non-local — classifier won't match
        var registry = env.SeedRegistry([
            new GatewayRecord { Id = "marked-local", Url = sharedUrl, IsLocal = true },
            new GatewayRecord { Id = "remote", Url = sharedUrl, IsLocal = false },
        ]);

        var engine = env.BuildEngine(registry: registry);
        await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        var reloaded = new GatewayRegistry(env.DataDir);
        reloaded.Load();
        var ids = reloaded.GetAll().Select(r => r.Id).ToList();
        Assert.DoesNotContain("marked-local", ids);
        Assert.Contains("remote", ids);
    }

    // -----------------------------------------------------------------------
    // Run_LegacyRecordWithoutIsLocal_RemovedByUrlClassifier
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task Run_LegacyRecordWithoutIsLocal_RemovedByUrlClassifier()
    {
        using var env = new UninstallTestEnv();
        var registry = env.SeedRegistry([
            new GatewayRecord { Id = "legacy", Url = "ws://localhost:18789", IsLocal = false },
        ]);

        var engine = env.BuildEngine(registry: registry);
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        var reloaded = new GatewayRegistry(env.DataDir);
        reloaded.Load();
        Assert.Empty(reloaded.GetAll());
        Assert.True(result.Postconditions.LocalGatewayRecordsAbsent);
        Assert.False(Directory.Exists(Path.Combine(env.DataDir, "gateways", "legacy")));
    }

    [WindowsFact]
    public async Task Run_SshTunnelLocalhostRemote_Preserved()
    {
        using var env = new UninstallTestEnv();
        var registry = env.SeedRegistry([
            new GatewayRecord
            {
                Id = "remote-tunnel",
                Url = "ws://localhost:18789",
                IsLocal = false,
                SshTunnel = new SshTunnelConfig("user", "remote.example.com", 18789, 18789)
            },
        ]);

        var engine = env.BuildEngine(registry: registry);
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        var reloaded = new GatewayRegistry(env.DataDir);
        reloaded.Load();
        Assert.Contains("remote-tunnel", reloaded.GetAll().Select(r => r.Id));
        Assert.True(Directory.Exists(Path.Combine(env.DataDir, "gateways", "remote-tunnel")));
        Assert.True(result.Postconditions.LocalGatewayRecordsAbsent);
        Assert.True(result.Postconditions.LocalGatewayIdentityDirsAbsent);
    }

    // -----------------------------------------------------------------------
    // Run_RegistryRecordsRemain_ReturnsSuccessFalse
    // Simulate persistence failure by giving the engine an empty in-memory
    // registry while gateways.json on disk still contains a local record.
    // The fresh-disk-reload postcondition catches this.
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task Run_RegistryRecordsRemain_ReturnsSuccessFalse()
    {
        using var env = new UninstallTestEnv();
        // In-memory registry is empty (engine sees no records to remove).
        var emptyRegistry = new GatewayRegistry(env.DataDir);
        // Disk has a stale local record.
        File.WriteAllText(Path.Combine(env.DataDir, "gateways.json"),
            """{"gateways":[{"id":"stale","url":"ws://localhost:18789","isLocal":true}],"activeId":null}""");

        var engine = env.BuildEngine(registry: emptyRegistry);
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        Assert.False(result.Postconditions.LocalGatewayRecordsAbsent);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e =>
            e.Contains("local gateway records still in registry", StringComparison.OrdinalIgnoreCase));
    }

    // -----------------------------------------------------------------------
    // Run_IdentityDirPersists_ReturnsSuccessFalse
    // Hold an open file handle inside the identity dir to make
    // Directory.Delete(recursive: true) throw on Windows.
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task Run_IdentityDirPersists_ReturnsSuccessFalse()
    {
        using var env = new UninstallTestEnv();
        var registry = env.SeedRegistry([LocalRecord("stuck")]);
        var stuckFile = Path.Combine(env.DataDir, "gateways", "stuck", "device-key-ed25519.json");
        Assert.True(File.Exists(stuckFile));

        using var holdOpen = File.Open(stuckFile, FileMode.Open, FileAccess.Read, FileShare.Read);

        var engine = env.BuildEngine(registry: registry);
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        // Identity dir wasn't fully removed; the snapshot-based postcondition catches it.
        Assert.True(Directory.Exists(Path.Combine(env.DataDir, "gateways", "stuck")));
        Assert.False(result.Postconditions.LocalGatewayIdentityDirsAbsent);
        Assert.False(result.Success);
    }

    // -----------------------------------------------------------------------
    // Run_PostconditionFailed_ErrorListed
    // Force VHD dir to persist (hold a file handle) → VhdDirAbsent=false →
    // matching postcondition error appears in Errors.
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task Run_PostconditionFailed_ErrorListed()
    {
        using var env = new UninstallTestEnv();
        var vhdDir = Path.Combine(env.LocalDataDir, "wsl", "OpenClawGateway");
        Directory.CreateDirectory(vhdDir);
        var blocker = Path.Combine(vhdDir, "ext4.vhdx");
        File.WriteAllText(blocker, "");
        using var holdOpen = File.Open(blocker, FileMode.Open, FileAccess.Read, FileShare.Read);

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        Assert.False(result.Postconditions.VhdDirAbsent);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e =>
            e.StartsWith("Postcondition failed:", StringComparison.OrdinalIgnoreCase)
            && e.Contains("VHD", StringComparison.OrdinalIgnoreCase));
    }

    // -----------------------------------------------------------------------
    // Run_StepErrorAndPostconditionFailure_BothInErrors
    // Identity-dir delete failure produces a step error AND a postcondition
    // error; both must appear in the Errors list.
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task Run_StepErrorAndPostconditionFailure_BothInErrors()
    {
        using var env = new UninstallTestEnv();
        var registry = env.SeedRegistry([LocalRecord("stuck")]);
        var stuckFile = Path.Combine(env.DataDir, "gateways", "stuck", "device-key-ed25519.json");
        using var holdOpen = File.Open(stuckFile, FileMode.Open, FileAccess.Read, FileShare.Read);

        var engine = env.BuildEngine(registry: registry);
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        // Step error from Step 6a:
        Assert.Contains(result.Errors, e =>
            e.StartsWith("Remove local gateway records:", StringComparison.OrdinalIgnoreCase));
        // Plus postcondition error from the residual identity dir:
        Assert.Contains(result.Errors, e =>
            e.StartsWith("Postcondition failed:", StringComparison.OrdinalIgnoreCase)
            && e.Contains("identity directories", StringComparison.OrdinalIgnoreCase));
    }

    // -----------------------------------------------------------------------
    // Run_ActiveGatewayCleared_WhenLocalRemoved
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task Run_ActiveGatewayCleared_WhenLocalRemoved()
    {
        using var env = new UninstallTestEnv();
        var registry = env.SeedRegistry(
            [LocalRecord("local-active")],
            activeId: "local-active");
        Assert.Equal("local-active", registry.ActiveGatewayId);

        var engine = env.BuildEngine(registry: registry);
        await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        Assert.Null(registry.ActiveGatewayId);
        var reloaded = new GatewayRegistry(env.DataDir);
        reloaded.Load();
        Assert.Null(reloaded.ActiveGatewayId);
    }

    // -----------------------------------------------------------------------
    // Run_ActiveRemoteGateway_StaysActive
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task Run_ActiveRemoteGateway_StaysActive()
    {
        using var env = new UninstallTestEnv();
        var registry = env.SeedRegistry(
            [LocalRecord("local-1"), RemoteRecord("remote-active")],
            activeId: "remote-active");

        var engine = env.BuildEngine(registry: registry);
        await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        Assert.Equal("remote-active", registry.ActiveGatewayId);
        var reloaded = new GatewayRegistry(env.DataDir);
        reloaded.Load();
        Assert.Equal("remote-active", reloaded.ActiveGatewayId);
    }

    // -----------------------------------------------------------------------
    // Run_NoGatewaysJson_SucceedsWithPostconditionsTrue
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task Run_NoGatewaysJson_SucceedsWithPostconditionsTrue()
    {
        using var env = new UninstallTestEnv();
        Assert.False(File.Exists(Path.Combine(env.DataDir, "gateways.json")));

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        Assert.True(result.Postconditions.LocalGatewayRecordsAbsent);
        Assert.True(result.Postconditions.LocalGatewayIdentityDirsAbsent);
        Assert.True(result.Success, "expected Success=true; errors=" + string.Join(" | ", result.Errors));
        var step6a = result.Steps.FirstOrDefault(s => s.Name == "Remove local gateway records");
        Assert.NotNull(step6a);
        Assert.Equal(UninstallStepStatus.Skipped, step6a.Status);
    }

    // -----------------------------------------------------------------------
    // Run_PostconditionUsesFreeDiskRegistry_NotInMemory
    // The postcondition reload-from-disk uses a fresh GatewayRegistry,
    // independent of whatever the engine mutated in-memory.
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task Run_PostconditionUsesFreeDiskRegistry_NotInMemory()
    {
        using var env = new UninstallTestEnv();
        // Engine sees an empty in-memory registry — Step 6a logs "no records found"
        // and does NOT call Save(), so disk content is preserved.
        var emptyRegistry = new GatewayRegistry(env.DataDir);
        File.WriteAllText(Path.Combine(env.DataDir, "gateways.json"),
            """{"gateways":[{"id":"on-disk","url":"ws://localhost:18789","isLocal":true}],"activeId":null}""");

        var engine = env.BuildEngine(registry: emptyRegistry);
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        // Engine in-memory said "nothing to remove" — but the fresh-disk-reload
        // postcondition correctly reports the on-disk record still exists.
        Assert.False(result.Postconditions.LocalGatewayRecordsAbsent);
        var step6a = result.Steps.FirstOrDefault(s => s.Name == "Remove local gateway records");
        Assert.NotNull(step6a);
        Assert.Equal(UninstallStepStatus.Skipped, step6a.Status);
    }

    // -----------------------------------------------------------------------
    // DryRun_RegistryNotMutated
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task DryRun_RegistryNotMutated()
    {
        using var env = new UninstallTestEnv();
        var registry = env.SeedRegistry([LocalRecord("dryrun-id")]);
        var gatewaysJsonBefore = File.ReadAllText(Path.Combine(env.DataDir, "gateways.json"));
        var identityDir = Path.Combine(env.DataDir, "gateways", "dryrun-id");
        Assert.True(Directory.Exists(identityDir));

        var engine = env.BuildEngine(registry: registry);
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions { DryRun = true });

        // gateways.json untouched
        Assert.Equal(gatewaysJsonBefore,
            File.ReadAllText(Path.Combine(env.DataDir, "gateways.json")));
        // Identity dir untouched
        Assert.True(Directory.Exists(identityDir));
        // Step recorded as DryRun with IDs in detail
        var step6a = result.Steps.FirstOrDefault(s => s.Name == "Remove local gateway records");
        Assert.NotNull(step6a);
        Assert.Equal(UninstallStepStatus.DryRun, step6a.Status);
        Assert.Contains("dryrun-id", step6a.Detail ?? "");
    }

    // -----------------------------------------------------------------------
    // DryRun_SuccessTrue_PostconditionsSkipped
    // DryRun bypasses postcondition gating; Success=true even when
    // residual artifacts exist that would otherwise fail postconditions.
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task DryRun_SuccessTrue_PostconditionsSkipped()
    {
        using var env = new UninstallTestEnv();
        // Stage a VHD dir that DryRun won't clean → VhdDirAbsent would be false.
        var vhdDir = Path.Combine(env.LocalDataDir, "wsl", "OpenClawGateway");
        Directory.CreateDirectory(vhdDir);

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions { DryRun = true });

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
    }
}
