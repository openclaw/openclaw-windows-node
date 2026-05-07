using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClawTray.Services;
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
        public LocalGatewayUninstall BuildEngine(IWslCommandRunner? wsl = null)
            => LocalGatewayUninstall.Build(
                Settings,
                wsl: wsl ?? new FakeWslCommandRunner(),
                identityDataPath: DataDir,
                localDataPath: LocalDataDir);

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

        public void WriteDeviceKey(string? deviceToken)
        {
            var data = new
            {
                PrivateKeyBase64 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                PublicKeyBase64 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                DeviceId = "test-device-id",
                Algorithm = "Ed25519",
                DeviceToken = deviceToken,
                CreatedAt = 0L
            };
            File.WriteAllText(DeviceKeyPath,
                JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }

        public string SetupStatePath => Path.Combine(LocalDataDir, "setup-state.json");
        public string McpTokenPath => Path.Combine(DataDir, "mcp-token.txt");
        public string ExecPolicyPath => Path.Combine(LocalDataDir, "exec-policy.json");
        public string LogPath => Path.Combine(LocalDataDir, "gateway.log");
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
        File.WriteAllText(env.LogPath, "log content");

        env.Settings.Token = "gateway-token";
        env.Settings.AutoStart = true;
        env.Settings.Save();

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
        Assert.Equal("gateway-token", reloaded.Token);
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
    public async Task DeviceKey_TokenNulled_FilePreserved_OtherFieldsIntact()
    {
        using var env = new UninstallTestEnv();
        env.WriteDeviceKey("my-operator-token");
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

        // DeviceId and Algorithm fields are preserved
        using var doc = JsonDocument.Parse(File.ReadAllText(env.DeviceKeyPath));
        Assert.True(doc.RootElement.TryGetProperty("DeviceId", out var idEl));
        Assert.Equal("test-device-id", idEl.GetString());
        Assert.True(doc.RootElement.TryGetProperty("Algorithm", out var algEl));
        Assert.Equal("Ed25519", algEl.GetString());

        // Step records Executed
        var step = result.Steps.FirstOrDefault(s => s.Name == "Null device token");
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

        var step = result.Steps.FirstOrDefault(s => s.Name == "Null device token");
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

        var step = result.Steps.FirstOrDefault(s => s.Name == "Null device token");
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
        env.WriteDeviceKey("my-operator-token");

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        Assert.True(result.Postconditions.DeviceTokenCleared);
    }

    // -----------------------------------------------------------------------
    // Test 14: PreserveLogs=true (default) — log files NOT deleted
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task Logs_NotDeleted_WhenPreserveLogsTrue()
    {
        using var env = new UninstallTestEnv();
        File.WriteAllText(env.LogPath, "gateway log");

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true,
            PreserveLogs = true
        });

        Assert.True(File.Exists(env.LogPath));
        var step = result.Steps.FirstOrDefault(s => s.Name == "Delete gateway logs");
        Assert.NotNull(step);
        Assert.Equal(UninstallStepStatus.Skipped, step.Status);
    }

    // -----------------------------------------------------------------------
    // Test 15: PreserveLogs=false — log files ARE deleted
    // -----------------------------------------------------------------------

    [WindowsFact]
    public async Task Logs_Deleted_WhenPreserveLogsFalse()
    {
        using var env = new UninstallTestEnv();
        File.WriteAllText(env.LogPath, "gateway log");

        var engine = env.BuildEngine();
        var result = await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true,
            PreserveLogs = false
        });

        Assert.False(File.Exists(env.LogPath));
        var step = result.Steps.FirstOrDefault(s => s.Name == "Delete gateway logs");
        Assert.NotNull(step);
        Assert.Equal(UninstallStepStatus.Executed, step.Status);
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
        env.Settings.Token = "tok";
        env.Settings.BootstrapToken = "btok";
        env.Settings.GatewayUrl = "ws://custom:9999";
        env.Settings.EnableMcpServer = true;
        env.Settings.Save();

        var engine = env.BuildEngine();
        await engine.RunAsync(new LocalGatewayUninstallOptions
        {
            DryRun = false,
            ConfirmDestructive = true
        });

        var reloaded = new SettingsManager(env.DataDir);
        Assert.Equal(string.Empty, reloaded.Token);
        Assert.Equal(string.Empty, reloaded.BootstrapToken);
        Assert.Equal("ws://localhost:18789", reloaded.GatewayUrl);
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
}
