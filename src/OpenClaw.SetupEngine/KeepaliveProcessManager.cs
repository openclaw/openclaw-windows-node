using System.Diagnostics;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

internal abstract record KeepaliveStartResult
{
    private KeepaliveStartResult() { }

    internal sealed record AlreadyRunning(int Pid) : KeepaliveStartResult;
    internal sealed record Started(int Pid) : KeepaliveStartResult;
    internal sealed record FailedToStart : KeepaliveStartResult;
}

/// <summary>
/// Owns the setup-time detached WSL keepalive process that keeps the app-owned WSL2 distro
/// alive between distro creation and the point the tray's own long-lived
/// <c>WslGatewayKeepAliveService</c> (OpenClaw.Tray.WinUI) takes over. Its marker path and JSON
/// shape are the intentional handoff contract consumed by that tray service; this setup owner
/// does not invoke the tray service itself. A failed start never hard-fails setup because the tray
/// will start its own keepalive on next launch.
///
/// Takes explicit immutable inputs (distro name, local data dir, wsl.exe path) rather than a
/// <see cref="SetupContext"/> — only <see cref="StartKeepaliveStep"/> reads <c>SetupContext</c>
/// and maps this type's results onto the step's <see cref="StepResult"/>/logging contract.
///
/// Raw OS process interaction (PID liveness, command-line lookup, process enumeration, starting
/// the detached process, killing a process tree) is delegated to an
/// <see cref="IKeepaliveProcessRuntime"/> so tests can control those primitives deterministically;
/// this class owns all keepalive *policy* (identity matching via
/// <see cref="OpenClaw.Shared.WslCommandLineMatcher"/>, marker read/write, soft-fail contract).
/// </summary>
internal sealed class KeepaliveProcessManager
{
    private readonly string? _distroName;
    private readonly string _localDataDir;
    private readonly string _wslExePath;
    private readonly SetupLogger _logger;
    private readonly IKeepaliveProcessRuntime _runtime;

    internal KeepaliveProcessManager(
        string? distroName,
        string localDataDir,
        string wslExePath,
        SetupLogger logger)
        : this(distroName, localDataDir, wslExePath, logger, new ProcessKeepaliveRuntime())
    {
    }

    internal KeepaliveProcessManager(
        string? distroName,
        string localDataDir,
        string wslExePath,
        SetupLogger logger,
        IKeepaliveProcessRuntime runtime)
    {
        _distroName = distroName;
        _localDataDir = localDataDir;
        _wslExePath = wslExePath;
        _logger = logger;
        _runtime = runtime;
    }

    internal static string GetMarkerPath(string localDataDir, string distroName)
        => Path.Combine(localDataDir, "wsl-keepalive", $"{distroName}.json");

    internal bool TryGetExisting(string markerPath, string distro, out int pid)
    {
        pid = 0;
        if (!File.Exists(markerPath))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(markerPath));
            if (!doc.RootElement.TryGetProperty("Pid", out var pidElement) || !pidElement.TryGetInt32(out pid))
            {
                pid = 0;
                return false;
            }

            if (!_runtime.IsProcessAlive(pid))
            {
                pid = 0;
                return false;
            }

            if (!IsKeepaliveCommandLine(GetProcessCommandLine(pid), distro))
            {
                pid = 0;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // TryGetExisting returns false on any failure (file missing/unreadable, or a corrupt
            // marker). Debug-level via Trace so the failure is still visible in dev diagnostics.
            Trace.WriteLine($"[Keepalive] TryGetExistingKeepalive failed: {ex.Message}");
            pid = 0;
            return false;
        }
    }

    /// <summary>
    /// Ensures a setup-time keepalive process is running for the given distro. Never throws for
    /// a failed process start — the caller (StartKeepaliveStep) treats that as a soft failure and
    /// still succeeds, since the tray will start its own keepalive on next launch.
    /// </summary>
    internal KeepaliveStartResult EnsureStarted()
    {
        var distroName = _distroName!;
        _logger.Info($"Launching persistent keepalive for distro: {distroName}");

        var markerPath = GetMarkerPath(_localDataDir, distroName);
        if (TryGetExisting(markerPath, distroName, out var existingPid))
        {
            _logger.Info($"Keepalive already running for distro '{distroName}' (PID {existingPid})");
            return new KeepaliveStartResult.AlreadyRunning(existingPid);
        }

        if (File.Exists(markerPath))
        {
            try { File.Delete(markerPath); }
            catch (Exception ex) { _logger.Debug($"[Keepalive] Stale marker delete failed: {ex.Message}"); }
        }

        // Launch detached keepalive process — keeps the distro alive so port forwarding
        // remains stable until the tray starts its own keepalive.
        int? pid;
        try
        {
            pid = _runtime.StartDetached(new KeepaliveProcessStartSpec(
                _wslExePath,
                ["-d", distroName, "--", "sleep", "infinity"]));
        }
        catch (Exception ex)
        {
            // A thrown exception here is treated identically to a null PID: a soft failure that
            // never fails the setup pipeline — the tray will start its own keepalive on launch.
            // The warning text matches the null-PID branch exactly; exception detail goes to
            // Debug only, so callers observing the warning contract can't distinguish the two.
            _logger.Debug($"[Keepalive] Process start threw: {ex.Message}");
            _logger.Warn("Failed to start keepalive process — tray will start its own");
            return new KeepaliveStartResult.FailedToStart();
        }

        if (pid is null)
        {
            _logger.Warn("Failed to start keepalive process — tray will start its own");
            return new KeepaliveStartResult.FailedToStart();
        }

        _logger.Info($"Keepalive process started (PID {pid}), distro will stay alive for tray launch");

        WriteMarker(markerPath, distroName, pid.Value);

        return new KeepaliveStartResult.Started(pid.Value);
    }

    private void WriteMarker(string markerPath, string distroName, int pid)
    {
        var marker = new
        {
            DistroName = distroName,
            Pid = pid,
            StartTimeUtc = DateTimeOffset.UtcNow,
            ProcessName = "wsl"
        };
        var json = JsonSerializer.Serialize(marker, SetupConfig.JsonWriteOptions);
        AtomicFile.WriteAllText(markerPath, json);
        _logger.Info($"Wrote keepalive marker: {markerPath}");
    }

    /// <summary>
    /// Kills any detached keepalive process(es) for this distro and deletes the marker
    /// file/directory. Best-effort and continues past individual failures — this is the primary
    /// cleanup path during uninstall and must not assume EnsureStarted ran in this process.
    /// </summary>
    internal async Task RollbackAsync(CancellationToken ct)
    {
        var distroName = _distroName;
        if (string.IsNullOrEmpty(distroName))
        {
            _logger.Info("[Uninstall] No distro name — skipping keepalive cleanup");
            return;
        }

        // Kill keepalive wsl.exe processes for this distro.
        // Pattern: wsl.exe -d <distro> -- sleep infinity
        try
        {
            var processIds = _runtime.EnumerateProcessIds("wsl")
                .Concat(_runtime.EnumerateProcessIds("wsl.exe"));
            foreach (var pid in processIds)
            {
                try
                {
                    // Read command line via WMI/CIM (through the runtime seam)
                    var cmdLine = GetProcessCommandLine(pid);
                    if (IsKeepaliveCommandLine(cmdLine, distroName))
                    {
                        _runtime.KillProcessTree(pid, TimeSpan.FromSeconds(5));
                        _logger.Info($"[Uninstall] Killed keepalive process tree PID {pid}");
                    }
                }
                catch (Exception ex) { _logger.Debug($"[Uninstall] Keepalive proc {pid} cleanup skipped (may have exited): {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Uninstall] Error enumerating keepalive processes: {ex.Message}");
        }

        // Delete keepalive marker file
        var markerPath = GetMarkerPath(_localDataDir, distroName);
        var markerDir = Path.GetDirectoryName(markerPath)!;

        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
            _logger.Info($"[Uninstall] Deleted keepalive marker: {markerPath}");
        }

        // Clean up empty marker directory
        if (Directory.Exists(markerDir) && !Directory.EnumerateFileSystemEntries(markerDir).Any())
        {
            Directory.Delete(markerDir);
            _logger.Info("[Uninstall] Deleted empty wsl-keepalive directory");
        }

        await Task.CompletedTask;
    }

    private string? GetProcessCommandLine(int pid)
    {
        try
        {
            return _runtime.GetCommandLine(pid);
        }
        catch (Exception ex)
        {
            SetupDiagnostics.TryWriteStderrWarning(
                $"Failed to query command line for process {pid}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Single canonical WSL keepalive command-line matcher lives in
    /// <see cref="OpenClaw.Shared.WslCommandLineMatcher"/>; this is a thin delegate kept for the
    /// existing call sites/tests, not a second implementation.
    /// </summary>
    internal static bool IsKeepaliveCommandLine(string? commandLine, string distro)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(distro))
            return false;

        return WslCommandLineMatcher.IsKeepaliveForDistro(commandLine, distro);
    }
}
