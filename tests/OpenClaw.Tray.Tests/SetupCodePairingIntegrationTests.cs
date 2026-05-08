using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services;
using Xunit;
using Xunit.Abstractions;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// End-to-end integration test for the setup-code pairing flow.
/// Requires a running gateway on ws://localhost:18789 (WSL) and the openclaw CLI.
/// Skipped in CI — run manually with OPENCLAW_RUN_INTEGRATION=1.
/// </summary>
public class SetupCodePairingIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private const string GatewayUrl = "ws://localhost:18789";

    public SetupCodePairingIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private bool ShouldRun()
    {
        return Environment.GetEnvironmentVariable("OPENCLAW_RUN_INTEGRATION") == "1";
    }

    private string RunWsl(string command)
    {
        var psi = new ProcessStartInfo("wsl")
        {
            Arguments = $"bash -c '{command}'",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(15000);
        return stdout.Trim();
    }

    private (string url, string bootstrapToken)? GenerateFreshSetupCode()
    {
        _output.WriteLine("[TEST] Generating fresh setup code via WSL...");
        var raw = RunWsl("openclaw qr --url ws://localhost:18789 2>&1 | grep 'Setup code:'");
        var code = raw.Replace("Setup code:", "").Trim();
        if (string.IsNullOrEmpty(code))
        {
            _output.WriteLine("[TEST] ERROR: Could not generate setup code");
            return null;
        }

        var decoded = SetupCodeDecoder.Decode(code);
        if (!decoded.Success)
        {
            _output.WriteLine($"[TEST] ERROR: Setup code decode failed: {decoded.Error}");
            return null;
        }
        _output.WriteLine($"[TEST] Setup code: url={decoded.Url}, token={decoded.Token?[..8]}...");
        return (decoded.Url!, decoded.Token!);
    }

    private void ClearGatewayPairings()
    {
        _output.WriteLine("[TEST] Clearing gateway pairings...");
        RunWsl("echo '{}' > ~/.openclaw/devices/paired.json");
        RunWsl("echo '[]' > ~/.openclaw/nodes/paired.json");
        RunWsl("echo '[]' > ~/.openclaw/nodes/pending.json");
    }

    [Fact]
    public async Task SetupCodePairing_NodePairsAndOperatorConnects()
    {
        if (!ShouldRun())
        {
            _output.WriteLine("Skipped (set OPENCLAW_RUN_INTEGRATION=1 to run)");
            return;
        }

        // --- Step 1: Clean state ---
        var dataPath = Path.Combine(Path.GetTempPath(), $"openclaw-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);
        var settingsDir = Path.Combine(Path.GetTempPath(), $"openclaw-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(settingsDir);

        try
        {
            ClearGatewayPairings();

            // --- Step 2: Generate fresh setup code ---
            var setup = GenerateFreshSetupCode();
            Assert.NotNull(setup);
            var (url, bootstrapToken) = setup.Value;

            // --- Step 3: Apply setup code (same as SetupCodeApplicator.Apply) ---
            var settings = new SettingsManager(settingsDir);
            settings.GatewayUrl = url;
            settings.BootstrapToken = bootstrapToken;
            settings.Token = "";
            settings.EnableNodeMode = true;
            settings.Save();
            _output.WriteLine($"[TEST] Settings: url={settings.GatewayUrl}, bootstrapToken={settings.BootstrapToken[..8]}..., token='{settings.Token}'");

            // Also clear any stored device token (same as SetupCodeApplicator does)
            DeviceIdentity.TryClearStoredDeviceToken(dataPath);

            // --- Step 4: Connect node (same as InitializeNodeService → ConnectAsync) ---
            var logger = new TestOutputLogger(_output);
            var nodeClient = new WindowsNodeClient(
                settings.GatewayUrl,
                settings.Token,  // empty
                dataPath,
                logger,
                settings.BootstrapToken);

            var nodePairedTcs = new TaskCompletionSource<PairingStatusEventArgs>();
            nodeClient.PairingStatusChanged += (_, args) =>
            {
                _output.WriteLine($"[TEST] Node PairingStatusChanged: {args.Status}, operatorToken={args.OperatorDeviceToken?[..Math.Min(8, args.OperatorDeviceToken?.Length ?? 0)] ?? "null"}");
                if (args.Status == PairingStatus.Paired)
                    nodePairedTcs.TrySetResult(args);
            };
            nodeClient.StatusChanged += (_, status) =>
                _output.WriteLine($"[TEST] Node StatusChanged: {status}");

            _output.WriteLine("[TEST] Connecting node...");
            _ = nodeClient.ConnectAsync();

            // Wait for node to pair (max 15s)
            var pairedCt = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            pairedCt.Token.Register(() => nodePairedTcs.TrySetException(new TimeoutException("Node did not pair within 15s")));

            PairingStatusEventArgs pairingResult;
            try
            {
                pairingResult = await nodePairedTcs.Task;
            }
            catch (TimeoutException)
            {
                _output.WriteLine("[TEST] TIMEOUT: Node did not pair");
                Assert.Fail("Node did not pair within 15 seconds");
                return;
            }

            _output.WriteLine($"[TEST] Node paired! DeviceId={pairingResult.DeviceId[..16]}");
            Assert.Equal(PairingStatus.Paired, pairingResult.Status);

            // --- Step 5: Extract operator device token (same as OnPairingStatusChanged) ---
            var operatorDeviceToken = pairingResult.OperatorDeviceToken;
            _output.WriteLine($"[TEST] Operator device token: {(operatorDeviceToken != null ? operatorDeviceToken[..8] + "..." : "NULL")}");
            Assert.NotNull(operatorDeviceToken);

            // Store in settings (same as App.OnPairingStatusChanged)
            settings.Token = operatorDeviceToken!;
            settings.BootstrapToken = "";
            settings.Save();
            _output.WriteLine($"[TEST] Settings updated: token={settings.Token[..8]}..., bootstrap cleared");

            // --- Step 6: Connect operator (same as InitializeGatewayClient reinit) ---
            var isDeviceToken = DeviceIdentity.HasStoredDeviceToken(dataPath);
            _output.WriteLine($"[TEST] Creating operator client: effectiveToken={settings.Token[..8]}..., isDeviceToken={isDeviceToken}");

            var operatorClient = new OpenClawGatewayClient(
                settings.GatewayUrl,
                settings.Token,
                logger,
                useBootstrapHandoffAuth: false,
                dataPath: dataPath,
                isDeviceToken: isDeviceToken);

            var operatorConnectedTcs = new TaskCompletionSource<bool>();
            var operatorErrors = new List<string>();
            operatorClient.StatusChanged += (_, status) =>
            {
                _output.WriteLine($"[TEST] Operator StatusChanged: {status}");
                if (status == ConnectionStatus.Connected)
                    operatorConnectedTcs.TrySetResult(true);
                else if (status == ConnectionStatus.Error)
                    operatorConnectedTcs.TrySetResult(false);
            };
            operatorClient.AuthenticationFailed += (_, msg) =>
            {
                _output.WriteLine($"[TEST] Operator AuthFailed: {msg}");
                operatorErrors.Add(msg);
            };

            _output.WriteLine("[TEST] Connecting operator...");
            _ = operatorClient.ConnectAsync();

            // Wait for operator (max 15s)
            var opCt = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            opCt.Token.Register(() => operatorConnectedTcs.TrySetResult(false));

            var operatorConnected = await operatorConnectedTcs.Task;
            _output.WriteLine($"[TEST] Operator result: connected={operatorConnected}, errors={string.Join("; ", operatorErrors)}");

            Assert.True(operatorConnected, $"Operator failed to connect. Errors: {string.Join("; ", operatorErrors)}");
            Assert.True(operatorClient.IsConnectedToGateway);

            // --- Step 7: Verify both connected ---
            _output.WriteLine("[TEST] ✅ SUCCESS: Both node and operator connected!");
            Assert.True(nodeClient.IsConnected);
            Assert.True(nodeClient.IsPaired);
            Assert.True(operatorClient.IsConnectedToGateway);

            // Cleanup
            await nodeClient.DisconnectAsync();
            await operatorClient.DisconnectAsync();
        }
        finally
        {
            if (Directory.Exists(dataPath))
                Directory.Delete(dataPath, true);
            if (Directory.Exists(settingsDir))
                Directory.Delete(settingsDir, true);
        }
    }

    private class TestOutputLogger : IOpenClawLogger
    {
        private readonly ITestOutputHelper _output;
        public TestOutputLogger(ITestOutputHelper output) => _output = output;
        public void Info(string message) => _output.WriteLine($"[INFO] {message}");
        public void Debug(string message) { } // suppress verbose
        public void Warn(string message) => _output.WriteLine($"[WARN] {message}");
        public void Error(string message, Exception? ex = null) => _output.WriteLine($"[ERROR] {message}");
    }
}
