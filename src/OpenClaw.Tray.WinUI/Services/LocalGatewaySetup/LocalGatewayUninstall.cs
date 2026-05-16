using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using OpenClaw.Shared;
using OpenClaw.Connection;

namespace OpenClawTray.Services.LocalGatewaySetup;

// ---------------------------------------------------------------------------
// Option / result types
// ---------------------------------------------------------------------------

public enum UninstallStepStatus
{
    Executed,
    Skipped,
    DryRun,
    Failed
}

public sealed record UninstallStep(string Name, UninstallStepStatus Status, string? Detail = null);

public sealed record LocalGatewayUninstallPostconditions
{
    /// <summary>wsl --list --quiet does NOT show the distro.</summary>
    public bool WslDistroAbsent { get; init; }

    /// <summary>Registry Run value absent AND settings.AutoStart == false.</summary>
    public bool AutostartCleared { get; init; }

    /// <summary>setup-state.json does not exist.</summary>
    public bool SetupStateAbsent { get; init; }

    /// <summary>device-key-ed25519.json absent OR operator/node device tokens are null/empty.</summary>
    public bool DeviceTokenCleared { get; init; }

    /// <summary>mcp-token.txt exists if it existed before (never touched).</summary>
    public bool McpTokenPreserved { get; init; }

    /// <summary>No OpenClaw keepalive process running.</summary>
    public bool KeepalivesAbsent { get; init; }

    /// <summary>VHD parent directory absent: %LOCALAPPDATA%\OpenClawTray\wsl\&lt;DistroName&gt;.</summary>
    public bool VhdDirAbsent { get; init; }

    /// <summary>No gateway records matching local predicate remain in gateways.json.</summary>
    public bool LocalGatewayRecordsAbsent { get; init; }

    /// <summary>No per-gateway identity directories remain on disk for local gateway records.</summary>
    public bool LocalGatewayIdentityDirsAbsent { get; init; }
}

/// <summary>
/// Options controlling what the uninstall engine does.
/// DryRun=true is the safety default — no destructive changes are made.
/// </summary>
public sealed record LocalGatewayUninstallOptions
{
    /// <summary>
    /// When true (default), the engine records what it would do but never
    /// destroys anything.  Set to false together with ConfirmDestructive=true
    /// to actually run the uninstall.
    /// </summary>
    public bool DryRun { get; init; } = true;

    /// <summary>Must be true (alongside DryRun=false) to permit destructive ops.</summary>
    public bool ConfirmDestructive { get; init; }

    public string DistroName { get; init; } = "OpenClawGateway";

    /// <summary>When true (default per Q-L), logs are not deleted.</summary>
    public bool PreserveLogs { get; init; } = true;

    /// <summary>When true (default per Q-E), exec-policy.json is not deleted.</summary>
    public bool PreserveExecPolicy { get; init; } = true;

    /// <summary>
    /// When true, replacement flows preserve root legacy device tokens if
    /// non-local gateway records remain after local records are removed.
    /// </summary>
    public bool PreserveRootDeviceTokensWhenExternalGatewaysExist { get; init; }

    // NO KeepMcpToken — mcp-token.txt is preserved unconditionally (v3 §F).
    // NO InstallLocation — knob is gone; path is fixed at
    //   %LOCALAPPDATA%\OpenClawTray\wsl\OpenClawGateway.
}

public sealed record LocalGatewayUninstallResult
{
    public bool Success { get; init; }

    /// <summary>Ordered audit trail of every step attempted.</summary>
    public IReadOnlyList<UninstallStep> Steps { get; init; } = Array.Empty<UninstallStep>();

    /// <summary>Names of steps that were skipped (target already absent, etc.).</summary>
    public IReadOnlyList<string> SkippedSteps { get; init; } = Array.Empty<string>();

    /// <summary>Error messages from Failed steps (uninstall continues past failures).</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public LocalGatewayUninstallPostconditions Postconditions { get; init; } = new();
}

// ---------------------------------------------------------------------------
// Core engine
// ---------------------------------------------------------------------------

/// <summary>
/// 13-step canonical uninstall engine.  Create via <see cref="Build"/> and
/// call <see cref="RunAsync"/>.
/// </summary>
public sealed class LocalGatewayUninstall
{
    private const string AutoStartRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoStartAppName = "OpenClawTray";

    /// <summary>
    /// Only this distro name may be unregistered.  Safety guard prevents
    /// wildcard / user-supplied names from being passed to wsl --unregister.
    /// </summary>
    private const string AllowedDistroName = "OpenClawGateway";

    private readonly SettingsManager _settings;
    private readonly IWslCommandRunner _wsl;
    private readonly IOpenClawLogger _logger;

    // %APPDATA%\OpenClawTray — device key, mcp-token, settings.json
    private readonly string _dataPath;

    // %LOCALAPPDATA%\OpenClawTray — setup-state, logs, exec-policy
    private readonly string _localDataPath;

    private readonly GatewayRegistry _registry;

    /// <summary>
    /// Local gateway IDs identified at step start — used by postcondition to verify
    /// identity dirs are gone regardless of whether Remove() succeeded for each.
    /// </summary>
    private IReadOnlyList<string> _localGatewayIdsSnapshot = Array.Empty<string>();

    private readonly List<UninstallStep> _steps = new();
    private readonly List<string> _errors = new();

    private LocalGatewayUninstall(
        SettingsManager settings,
        IWslCommandRunner wsl,
        IOpenClawLogger logger,
        string dataPath,
        string localDataPath,
        GatewayRegistry registry)
    {
        _settings = settings;
        _wsl = wsl;
        _logger = logger;
        _dataPath = dataPath;
        _localDataPath = localDataPath;
        _registry = registry;
    }

    /// <summary>
    /// Factory mirroring <c>LocalGatewaySetupEngineFactory.CreateLocalOnly</c>.
    /// </summary>
    public static LocalGatewayUninstall Build(
        SettingsManager settings,
        IWslCommandRunner? wsl = null,
        IOpenClawLogger? logger = null,
        string? identityDataPath = null,
        string? localDataPath = null,
        GatewayRegistry? registry = null)
    {
        var resolvedLogger = logger ?? NullLogger.Instance;
        var resolvedDataPath = identityDataPath ?? SettingsManager.SettingsDirectoryPath;
        var resolvedLocalDataPath = localDataPath ?? Path.Combine(
            Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenClawTray");

        var resolvedRegistry = registry;
        if (resolvedRegistry == null)
        {
            resolvedRegistry = new GatewayRegistry(resolvedDataPath);
            resolvedRegistry.Load();
        }

        return new LocalGatewayUninstall(
            settings,
            wsl ?? new WslExeCommandRunner(resolvedLogger),
            resolvedLogger,
            resolvedDataPath,
            resolvedLocalDataPath,
            resolvedRegistry);
    }

    /// <summary>
    /// Executes the 13-step uninstall sequence.  Steps that fail are recorded
    /// in <see cref="LocalGatewayUninstallResult.Errors"/> and the engine
    /// continues to the next step where reasonable.
    /// </summary>
    public async Task<LocalGatewayUninstallResult> RunAsync(
        LocalGatewayUninstallOptions options,
        CancellationToken ct = default)
    {
        _steps.Clear();
        _errors.Clear();

        // ------------------------------------------------------------------
        // Step 1 — Preflight gate
        // ------------------------------------------------------------------
        // Non-DryRun with ConfirmDestructive=false is a hard stop — throw so
        // the caller knows immediately that nothing happened.
        if (!options.DryRun && !options.ConfirmDestructive)
        {
            const string msg =
                "ConfirmDestructive must be true to perform a destructive uninstall. " +
                "Set DryRun=true to preview without destroying.";
            RecordStep("Preflight gate", UninstallStepStatus.Failed, msg);
            _errors.Add(msg);
            throw new InvalidOperationException(msg);
        }

        RecordStep("Preflight gate",
            options.DryRun ? UninstallStepStatus.DryRun : UninstallStepStatus.Executed);

        // ------------------------------------------------------------------
        // Step 2 — Stop keepalive process
        // ------------------------------------------------------------------
        await RunStepAsync("Stop keepalive process", options, ct, async () =>
        {
            var pids = await FindKeepaliveProcessIdsAsync(options.DistroName, ct);
            if (pids.Count == 0)
            {
                RecordStep("Stop keepalive process", UninstallStepStatus.Skipped,
                    "No keepalive process found.");
                return;
            }

            var stopped = 0;
            foreach (var pid in pids)
            {
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    // Round 2 (Bot B2): wsl.exe spawns wslhost.exe + the
                    // in-distro init/sleep processes. Killing only the parent
                    // PID leaves child WSL services holding distro state,
                    // which prevents `wsl --unregister` from completing.
                    proc.Kill(entireProcessTree: true);
                    stopped++;
                    _logger.Info($"[Uninstall] Stopped WSL keepalive PID {pid}.");
                }
                catch (Exception ex)
                {
                    _logger.Warn($"[Uninstall] Failed to stop PID {pid}: {ex.Message}");
                }
            }

            RecordStep("Stop keepalive process", UninstallStepStatus.Executed,
                $"Stopped {stopped}/{pids.Count} process(es).");
        });

        // ------------------------------------------------------------------
        // Step 3 — Stop systemd gateway service inside WSL
        // ------------------------------------------------------------------
        await RunStepAsync("Stop systemd gateway service", options, ct, async () =>
        {
            var distros = await _wsl.ListDistrosAsync(ct);
            var distro = distros.FirstOrDefault(d => string.Equals(
                d.Name, options.DistroName, StringComparison.OrdinalIgnoreCase));

            if (distro is null)
            {
                RecordStep("Stop systemd gateway service", UninstallStepStatus.Skipped,
                    "Distro not registered.");
                return;
            }

            // If the distro is not Running, issuing `wsl -d ... systemctl stop` would
            // start the distro, run the command, then WSL hangs ~30 s waiting for its
            // own session to terminate — even though the service was already stopped.
            // Skip the systemctl call when the distro is stopped; the unregister step
            // that follows will force-terminate it anyway.
            if (!string.Equals(distro.State, "Running", StringComparison.OrdinalIgnoreCase))
            {
                RecordStep("Stop systemd gateway service", UninstallStepStatus.Skipped,
                    $"Distro state is '{distro.State}' (not Running); skipping systemctl stop.");
                return;
            }

            using var cts5s = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts5s.CancelAfter(TimeSpan.FromSeconds(5));
            WslCommandResult result;
            try
            {
                result = await _wsl.RunInDistroAsync(
                    options.DistroName,
                    ["bash", "-c", "sudo systemctl stop openclaw-gateway 2>&1 || true"],
                    cts5s.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 5-second inner timeout fired; the distro is wedged.
                // Log and continue — the wsl --unregister step will force-terminate it.
                _logger.Warn("[Uninstall] systemctl stop timed out after 5 s; distro appears wedged. " +
                             "Proceeding to wsl --unregister.");
                RecordStep("Stop systemd gateway service", UninstallStepStatus.Executed,
                    "systemctl stop timed out (5 s); distro wedged — wsl --unregister will force-terminate.");
                return;
            }

            var detail = (result.StandardOutput + result.StandardError).Trim();
            RecordStep("Stop systemd gateway service", UninstallStepStatus.Executed,
                string.IsNullOrWhiteSpace(detail) ? null : detail);
        });

        // ------------------------------------------------------------------
        // Step 4 — Revoke operator token (best-effort)
        // ------------------------------------------------------------------
        await RunStepAsync("Revoke operator token", options, ct, async () =>
        {
            if (string.IsNullOrWhiteSpace(_settings.LegacyToken))
            {
                RecordStep("Revoke operator token", UninstallStepStatus.Skipped,
                    "No operator token stored.");
                return;
            }

            try
            {
                var token = _settings.LegacyToken;
                var httpBase = _settings.GatewayUrl
                    .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)
                    .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
                    .TrimEnd('/');

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                var response = await http.PostAsync(
                    $"{httpBase}/api/v1/operator/disconnect", content: null, cts.Token);

                RecordStep("Revoke operator token", UninstallStepStatus.Executed,
                    $"Response: {(int)response.StatusCode}. Token: ***REDACTED***");
            }
            catch (Exception ex)
            {
                // Gateway is likely already down — absorb and continue.
                RecordStep("Revoke operator token", UninstallStepStatus.Executed,
                    $"Best-effort revoke failed ({ex.GetType().Name}); gateway may be down. Token: ***REDACTED***");
            }
        });

        // ------------------------------------------------------------------
        // Step 5 — Unregister WSL distro
        // ------------------------------------------------------------------
        await RunStepAsync("Unregister WSL distro", options, ct, async () =>
        {
            // Distro-name guard: only AllowedDistroName may be unregistered.
            if (!string.Equals(options.DistroName, AllowedDistroName,
                    StringComparison.OrdinalIgnoreCase))
            {
                var guard = $"Refused to unregister '{options.DistroName}': " +
                            $"only '{AllowedDistroName}' is allowed.";
                RecordStep("Unregister WSL distro", UninstallStepStatus.Failed, guard);
                _errors.Add(guard);
                return;
            }

            var distros = await _wsl.ListDistrosAsync(ct);
            if (!distros.Any(d => string.Equals(d.Name, options.DistroName,
                    StringComparison.OrdinalIgnoreCase)))
            {
                RecordStep("Unregister WSL distro", UninstallStepStatus.Skipped,
                    "Distro not registered.");
                return;
            }

            var result = await _wsl.UnregisterDistroAsync(options.DistroName, ct);
            if (result.Success || result.ExitCode == 1)
            {
                // exit 1 is treated as success: WSL may return 1 when distro
                // is already gone mid-unregister.
                RecordStep("Unregister WSL distro", UninstallStepStatus.Executed);
            }
            else
            {
                var msg = $"wsl --unregister exited {result.ExitCode}: {result.StandardError}";
                RecordStep("Unregister WSL distro", UninstallStepStatus.Failed, msg);
                _errors.Add(msg);
            }
        });

        // ------------------------------------------------------------------
        // Step 5a — VHD parent-dir cleanup (idempotent)
        // wsl --unregister removes the .vhdx but the parent dir may persist
        // if Windows wrote additional files there.
        // ------------------------------------------------------------------
        await RunStepAsync("VHD parent dir cleanup", options, ct, () =>
        {
            var vhdDir = Path.Combine(_localDataPath, "wsl", options.DistroName);
            if (Directory.Exists(vhdDir))
            {
                Directory.Delete(vhdDir, recursive: true);
                RecordStep("VHD parent dir cleanup", UninstallStepStatus.Executed,
                    "Deleted VHD parent directory.");
            }
            else
            {
                RecordStep("VHD parent dir cleanup", UninstallStepStatus.Skipped,
                    "Directory absent.");
            }
            return Task.CompletedTask;
        });

        // ------------------------------------------------------------------
        // Step 6 — Reset autostart
        // CRITICAL ORDERING (v3 §B): persist settings BEFORE deleting registry.
        // ------------------------------------------------------------------
        await RunStepAsync("Reset autostart", options, ct, () =>
        {
            // 6a–6c: set false and persist FIRST
            _settings.AutoStart = false;
            _settings.Save();
            RecordStep("Persist settings (AutoStart=false)", UninstallStepStatus.Executed);

            // 6d: delete registry Run value SECOND (idempotent)
            TryDeleteAutoStartRegistryValue();
            RecordStep("Delete autostart registry", UninstallStepStatus.Executed);

            return Task.CompletedTask;
        });

        // ------------------------------------------------------------------
        // Step 6a — Remove local gateway records and identity directories
        // Snapshot BEFORE any mutation so postconditions verify the full
        // candidate set regardless of partial-remove failures.
        // ------------------------------------------------------------------
        {
            var localRecords = _registry.GetAll()
                .Where(IsLocalGatewayRecordForUninstall)
                .ToList();
            _localGatewayIdsSnapshot = localRecords.Select(r => r.Id).ToList();

            if (options.DryRun)
            {
                RecordStep("Remove local gateway records", UninstallStepStatus.DryRun,
                    $"Would remove {localRecords.Count} local gateway record(s): " +
                    $"[{string.Join(", ", localRecords.Select(r => r.Id))}]");
            }
            else
            {
                try
                {
                    if (localRecords.Count == 0)
                    {
                        RecordStep("Remove local gateway records", UninstallStepStatus.Skipped,
                            "No local gateway records found.");
                    }
                    else
                    {
                        foreach (var record in localRecords)
                        {
                            _registry.Remove(record.Id);
                            var identityDir = _registry.GetIdentityDirectory(record.Id);
                            if (Directory.Exists(identityDir))
                                Directory.Delete(identityDir, recursive: true);
                        }
                        _registry.Save();
                        RecordStep("Remove local gateway records", UninstallStepStatus.Executed,
                            $"Removed {localRecords.Count} record(s): " +
                            $"[{string.Join(", ", localRecords.Select(r => r.Id))}]");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Error($"[Uninstall] Step 'Remove local gateway records' threw: {ex.Message}");
                    RecordStep("Remove local gateway records", UninstallStepStatus.Failed, ex.Message);
                    _errors.Add($"Remove local gateway records: {ex.Message}");
                }
            }
        }

        // ------------------------------------------------------------------
        // Step 7 — Null device tokens (preserve file, per v3 §C)
        // ------------------------------------------------------------------
        await RunStepAsync("Null device tokens", options, ct, () =>
        {
            if (ShouldPreserveRootDeviceTokens(options))
            {
                RecordStep("Null device tokens", UninstallStepStatus.Skipped,
                    "Preserving root device tokens because external gateway records remain.");
                return Task.CompletedTask;
            }

            var operatorCleared = DeviceIdentity.TryClearDeviceTokenForRole(_dataPath, "operator", _logger);
            var nodeCleared = DeviceIdentity.TryClearDeviceTokenForRole(_dataPath, "node", _logger);
            var cleared = operatorCleared || nodeCleared;
            RecordStep("Null device tokens",
                cleared ? UninstallStepStatus.Executed : UninstallStepStatus.Skipped,
                cleared
                    ? "DeviceToken/NodeDeviceToken set to null where present; keypair file preserved. Values: ***REDACTED***"
                    : "File absent or device tokens already null.");
            return Task.CompletedTask;
        });

        // ------------------------------------------------------------------
        // Step 8 — Delete setup-state.json
        // ------------------------------------------------------------------
        await RunStepAsync("Delete setup-state.json", options, ct, () =>
        {
            var path = Path.Combine(_localDataPath, "setup-state.json");
            if (!File.Exists(path))
            {
                RecordStep("Delete setup-state.json", UninstallStepStatus.Skipped,
                    "File not found.");
                return Task.CompletedTask;
            }

            File.Delete(path);
            RecordStep("Delete setup-state.json", UninstallStepStatus.Executed);
            return Task.CompletedTask;
        });

        // ------------------------------------------------------------------
        // Step 8a — Delete run.marker (idempotent)
        // run.marker is written by App.xaml.cs at tray startup and deleted
        // on clean exit. A stale marker may remain after a crash. Include
        // here so the engine removes it as part of data cleanup.
        // ------------------------------------------------------------------
        await RunStepAsync("Delete run.marker", options, ct, () =>
        {
            var path = Path.Combine(_localDataPath, "run.marker");
            if (!File.Exists(path))
            {
                RecordStep("Delete run.marker", UninstallStepStatus.Skipped,
                    "File not found.");
                return Task.CompletedTask;
            }

            File.Delete(path);
            RecordStep("Delete run.marker", UninstallStepStatus.Executed);
            return Task.CompletedTask;
        });

        // ------------------------------------------------------------------
        // Step 9 — Delete gateway logs (unless PreserveLogs=true)
        //
        // Round 2 (Scott #7): delete both candidate Logs/ locations recursively
        // (idempotent — skip if absent). DiagnosticsJsonlService writes to
        // %APPDATA%\OpenClawTray\Logs today, but the SettingsPage "View Logs"
        // button points at %LOCALAPPDATA%\OpenClawTray\Logs — a pre-existing
        // inconsistency. PreserveLogs=false must leave no logs behind in
        // either location. The appdata-vs-localappdata ambiguity itself is
        // tracked as a separate follow-up.
        // ------------------------------------------------------------------
        await RunStepAsync("Delete gateway logs", options, ct, () =>
        {
            if (options.PreserveLogs)
            {
                RecordStep("Delete gateway logs", UninstallStepStatus.Skipped,
                    "PreserveLogs=true.");
                return Task.CompletedTask;
            }

            var deletedDirs = 0;
            foreach (var logsDir in new[]
            {
                Path.Combine(_localDataPath, "Logs"), // SettingsPage "View Logs" target
                Path.Combine(_dataPath, "Logs")        // DiagnosticsJsonlService actual writer
            })
            {
                if (Directory.Exists(logsDir))
                {
                    Directory.Delete(logsDir, recursive: true);
                    deletedDirs++;
                }
            }

            if (deletedDirs == 0)
            {
                RecordStep("Delete gateway logs", UninstallStepStatus.Skipped,
                    "No log directories present.");
            }
            else
            {
                RecordStep("Delete gateway logs", UninstallStepStatus.Executed,
                    $"Deleted {deletedDirs} log directory(ies).");
            }
            return Task.CompletedTask;
        });

        // ------------------------------------------------------------------
        // Step 10 — Delete exec-policy.json (unless PreserveExecPolicy=true)
        // ------------------------------------------------------------------
        await RunStepAsync("Delete exec-policy.json", options, ct, () =>
        {
            if (options.PreserveExecPolicy)
            {
                RecordStep("Delete exec-policy.json", UninstallStepStatus.Skipped,
                    "PreserveExecPolicy=true.");
                return Task.CompletedTask;
            }

            var path = Path.Combine(_localDataPath, "exec-policy.json");
            if (!File.Exists(path))
            {
                RecordStep("Delete exec-policy.json", UninstallStepStatus.Skipped,
                    "File not found.");
                return Task.CompletedTask;
            }

            File.Delete(path);
            RecordStep("Delete exec-policy.json", UninstallStepStatus.Executed);
            return Task.CompletedTask;
        });

        // ------------------------------------------------------------------
        // Step 11 — Reset onboarding-relevant settings flags
        // EnableMcpServer is deliberately left as-is (v3 §F / Q-M): it
        // controls whether RequiresSetup fires post-uninstall.
        // ------------------------------------------------------------------
        await RunStepAsync("Reset onboarding settings", options, ct, () =>
        {
            _settings.GatewayUrl = "ws://localhost:18789";
            _settings.EnableNodeMode = false;
            // EnableMcpServer: NOT touched.
            // LegacyToken/LegacyBootstrapToken: cleared implicitly — Save() no longer
            // writes Token/BootstrapToken fields (GatewayRegistry is the source of truth).
            _settings.Save();
            RecordStep("Reset onboarding settings", UninstallStepStatus.Executed,
                "LegacyToken/LegacyBootstrapToken cleared (not written by Save()), GatewayUrl reset. " +
                "EnableNodeMode disabled; EnableMcpServer preserved.");
            return Task.CompletedTask;
        });

        // ------------------------------------------------------------------
        // Step 12 — Preserve mcp-token.txt (no-op; logged for audit clarity)
        // Per v3 §F: mcp-token is not a gateway artifact and is NEVER deleted.
        // ------------------------------------------------------------------
        RecordStep("Preserve mcp-token.txt", UninstallStepStatus.Skipped,
            "mcp-token.txt preserved unconditionally (v3 §F). Not a gateway artifact.");

        // ------------------------------------------------------------------
        // Step 13 — Compute postconditions
        // ------------------------------------------------------------------
        var postconditions = new LocalGatewayUninstallPostconditions();
        if (options.DryRun)
        {
            RecordStep("Compute postconditions", UninstallStepStatus.DryRun,
                "DryRun=true; postconditions not evaluated.");
        }
        else
        {
            try
            {
                postconditions = await ComputePostconditionsAsync(options, ct);
                RecordStep("Compute postconditions", UninstallStepStatus.Executed);
            }
            catch (Exception ex)
            {
                _logger.Warn($"[Uninstall] Failed to compute postconditions: {ex.Message}");
                RecordStep("Compute postconditions", UninstallStepStatus.Failed, ex.Message);
                _errors.Add($"Postconditions: {ex.Message}");
            }
        }

        if (!options.DryRun)
        {
            AppendPostconditionErrors(postconditions);
        }
        return BuildResult(
            success: _errors.Count == 0
                && (options.DryRun || AllRequiredPostconditionsMet(postconditions)),
            postconditions: postconditions);
    }

    // -----------------------------------------------------------------------
    // Step runner
    // -----------------------------------------------------------------------

    /// <summary>
    /// Wraps a step action with DryRun gate and exception handling.
    /// When DryRun=true the action is skipped and a DryRun record is logged.
    /// Unhandled exceptions record the step as Failed and add to Errors but
    /// do NOT abort the remaining steps.
    /// </summary>
    private async Task RunStepAsync(
        string stepName,
        LocalGatewayUninstallOptions options,
        CancellationToken ct,
        Func<Task> action)
    {
        if (options.DryRun)
        {
            RecordStep(stepName, UninstallStepStatus.DryRun, "DryRun=true; no changes made.");
            return;
        }

        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"[Uninstall] Step '{stepName}' threw: {ex.Message}");
            RecordStep(stepName, UninstallStepStatus.Failed, ex.Message);
            _errors.Add($"{stepName}: {ex.Message}");
        }
    }

    private void RecordStep(string name, UninstallStepStatus status, string? detail = null)
    {
        _logger.Info(
            $"[Uninstall] {name}: {status}" +
            (detail is null ? string.Empty : $" — {detail}"));
        _steps.Add(new UninstallStep(name, status, detail));
    }

    // -----------------------------------------------------------------------
    // Postcondition computation
    // -----------------------------------------------------------------------

    private async Task<LocalGatewayUninstallPostconditions> ComputePostconditionsAsync(
        LocalGatewayUninstallOptions options,
        CancellationToken ct)
    {
        // WSL distro absent?
        bool wslAbsent = false;
        try
        {
            var distros = await _wsl.ListDistrosAsync(ct);
            wslAbsent = !distros.Any(d => string.Equals(
                d.Name, options.DistroName, StringComparison.OrdinalIgnoreCase));
        }
        catch { /* leave false */ }

        // Autostart cleared? Registry value absent AND settings.AutoStart == false.
        bool autostartCleared;
        try
        {
            var regAbsent = TryReadAutoStartRegistryPresent() != true;
            autostartCleared = regAbsent && !_settings.AutoStart;
        }
        catch
        {
            autostartCleared = !_settings.AutoStart;
        }

        bool setupStateAbsent = !File.Exists(Path.Combine(_localDataPath, "setup-state.json"));
        bool deviceTokenCleared = ShouldPreserveRootDeviceTokens(options)
            || !DeviceIdentity.HasStoredDeviceToken(_dataPath, _logger)
            && !DeviceIdentity.HasStoredDeviceTokenForRole(_dataPath, "node", _logger);

        // mcp-token.txt is never touched, so it's always "preserved" from our POV.
        bool mcpTokenPreserved = true;

        // No keepalive?
        bool keepalivesAbsent = true;
        try
        {
            var pids = await FindKeepaliveProcessIdsAsync(options.DistroName, ct);
            keepalivesAbsent = pids.Count == 0;
        }
        catch { /* leave true */ }

        // VHD parent dir absent?
        bool vhdDirAbsent = !Directory.Exists(
            Path.Combine(_localDataPath, "wsl", options.DistroName));

        // Local gateway records absent? Reload from disk — fresh instance, not mutated in-memory.
        bool localRecordsAbsent;
        try
        {
            var freshRegistry = new GatewayRegistry(_dataPath);
            freshRegistry.Load();
            localRecordsAbsent = !freshRegistry.GetAll().Any(IsLocalGatewayRecordForUninstall);
        }
        catch { localRecordsAbsent = false; }

        // Local gateway identity directories absent? Check snapshot against disk.
        bool localIdentityDirsAbsent = _localGatewayIdsSnapshot.All(id =>
            !Directory.Exists(Path.Combine(_dataPath, "gateways", id)));

        return new LocalGatewayUninstallPostconditions
        {
            WslDistroAbsent = wslAbsent,
            AutostartCleared = autostartCleared,
            SetupStateAbsent = setupStateAbsent,
            DeviceTokenCleared = deviceTokenCleared,
            McpTokenPreserved = mcpTokenPreserved,
            KeepalivesAbsent = keepalivesAbsent,
            VhdDirAbsent = vhdDirAbsent,
            LocalGatewayRecordsAbsent = localRecordsAbsent,
            LocalGatewayIdentityDirsAbsent = localIdentityDirsAbsent
        };
    }

    private static bool AllRequiredPostconditionsMet(LocalGatewayUninstallPostconditions p)
        => p.WslDistroAbsent
        && p.AutostartCleared
        && p.SetupStateAbsent
        && p.DeviceTokenCleared
        && p.LocalGatewayRecordsAbsent
        && p.LocalGatewayIdentityDirsAbsent
        && p.KeepalivesAbsent
        && p.VhdDirAbsent;

    private static bool IsLocalGatewayRecordForUninstall(GatewayRecord record)
    {
        if (record.IsLocal)
            return true;

        // Legacy local records may predate the IsLocal marker. Do not apply the
        // URL fallback to records with explicit SSH tunnel metadata: those can
        // legitimately use localhost as a forwarded remote endpoint.
        return record.SshTunnel is null
            && LocalGatewayUrlClassifier.IsLocalGatewayUrl(record.Url);
    }

    private bool ShouldPreserveRootDeviceTokens(LocalGatewayUninstallOptions options) =>
        options.PreserveRootDeviceTokensWhenExternalGatewaysExist
        && _registry.GetAll().Any(record => !IsLocalGatewayRecordForUninstall(record));

    private void AppendPostconditionErrors(LocalGatewayUninstallPostconditions p)
    {
        if (!p.WslDistroAbsent)
            _errors.Add("Postcondition failed: WSL distro still registered.");
        if (!p.AutostartCleared)
            _errors.Add("Postcondition failed: autostart still enabled.");
        if (!p.SetupStateAbsent)
            _errors.Add("Postcondition failed: setup-state.json still present.");
        if (!p.DeviceTokenCleared)
            _errors.Add("Postcondition failed: device token still present.");
        if (!p.LocalGatewayRecordsAbsent)
            _errors.Add("Postcondition failed: local gateway records still in registry.");
        if (!p.LocalGatewayIdentityDirsAbsent)
            _errors.Add("Postcondition failed: local gateway identity directories still on disk.");
        if (!p.KeepalivesAbsent)
            _errors.Add("Postcondition failed: keepalive process still running.");
        if (!p.VhdDirAbsent)
            _errors.Add("Postcondition failed: VHD directory still present.");
    }

    private LocalGatewayUninstallResult BuildResult(
        bool success,
        LocalGatewayUninstallPostconditions? postconditions = null)
    {
        var skipped = _steps
            .Where(s => s.Status == UninstallStepStatus.Skipped)
            .Select(s => s.Name)
            .ToList();

        return new LocalGatewayUninstallResult
        {
            Success = success,
            Steps = _steps.ToList(),
            SkippedSteps = skipped,
            Errors = _errors.ToList(),
            Postconditions = postconditions ?? new LocalGatewayUninstallPostconditions()
        };
    }

    // -----------------------------------------------------------------------
    // Keepalive process detection (via PowerShell — no WMI/P-Invoke dependency)
    // -----------------------------------------------------------------------

    private static async Task<IReadOnlyList<int>> FindKeepaliveProcessIdsAsync(
        string distroName,
        CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(
                "try { " +
                "(Get-CimInstance Win32_Process -Filter \"Name='wsl.exe'\") | " +
                $"Where-Object {{ $_.CommandLine -like '*-d {distroName}*' -and $_.CommandLine -like '*sleep*' }} | " +
                "Select-Object -ExpandProperty ProcessId" +
                " } catch { }");

            using var proc = Process.Start(psi);
            if (proc == null) return Array.Empty<int>();

            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            return output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => int.TryParse(l, out _))
                .Select(int.Parse)
                .ToList();
        }
        catch
        {
            return Array.Empty<int>();
        }
    }

    // -----------------------------------------------------------------------
    // Registry helpers (OS-guarded to avoid CA1416 in net10.0 non-windows builds)
    // -----------------------------------------------------------------------

    private static void TryDeleteAutoStartRegistryValue()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegistryKey, writable: true);
            key?.DeleteValue(AutoStartAppName, throwOnMissingValue: false);
        }
        catch
        {
            // Idempotent best-effort — missing key is not an error.
        }
    }

    /// <returns>true if value exists, false if absent, null if registry unavailable.</returns>
    private static bool? TryReadAutoStartRegistryPresent()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegistryKey, writable: false);
            return key?.GetValue(AutoStartAppName) != null;
        }
        catch
        {
            return null;
        }
    }

    // -----------------------------------------------------------------------
    // File helpers
    // -----------------------------------------------------------------------

    private static int DeleteMatchingFiles(string directory, params string[] patterns)
    {
        if (!Directory.Exists(directory)) return 0;
        var count = 0;
        foreach (var pattern in patterns)
        {
            foreach (var file in Directory.EnumerateFiles(
                         directory, pattern, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.Delete(file);
                    count++;
                }
                catch
                {
                    // Best-effort; continue with remaining files.
                }
            }
        }

        return count;
    }
}
