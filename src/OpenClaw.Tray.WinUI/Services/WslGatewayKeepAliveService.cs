using OpenClaw.Connection;
using OpenClawTray;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

/// <summary>
/// Keeps the local gateway's WSL distro alive by spawning a detached
/// <c>wsl.exe -- sleep infinity</c> process, and cleans up stale keepalive
/// processes/markers for setup-managed distros that are no longer active.
/// Best-effort, fire-and-forget. Runs entirely off the UI thread.
/// </summary>
internal sealed class WslGatewayKeepAliveService(
    Func<SettingsManager?> getSettings,
    Func<GatewayRegistry?> getRegistry)
{
    private readonly Func<SettingsManager?> _getSettings = getSettings;
    private readonly Func<GatewayRegistry?> _getRegistry = getRegistry;

    /// <summary>
    /// Ensures a WSL keepalive process is running for the local gateway distro
    /// so the WSL2 VM stays up even after the tray exits.
    /// Best-effort, fire-and-forget.
    /// </summary>
    public async Task TryEnsureAsync()
    {
        try
        {
            var settings = _getSettings();
            if (settings is null) return;

            var activeRecord = _getRegistry()?.GetActive();
            if (!WslKeepAlivePolicy.ShouldStart(activeRecord, settings.GetEffectiveGatewayUrl()))
            {
                await StopStaleLocalGatewayKeepAliveAsync();
                return;
            }

            var distroName = await ResolveLocalGatewayDistroNameAsync(activeRecord);
            if (string.IsNullOrWhiteSpace(distroName)) return;

            // Verify distro exists before spawning keepalive
            var runner = new WslExeCommandRunner(new AppLogger(), defaultTimeout: TimeSpan.FromSeconds(4));
            var distros = await runner.ListDistrosAsync();
            if (!distros.Any(d => string.Equals(d.Name, distroName, StringComparison.OrdinalIgnoreCase)))
            {
                Logger.Warn($"[WslKeepAlive] Distro '{distroName}' not found; skipping keepalive.");
                return;
            }

            // Spawn a detached wsl sleep process to keep the VM alive. Idempotent: TryEnsureAsync runs
            // at startup AND on every managed-local auto-repair (re-arm after a distro restart), so a
            // gateway-only outage where the VM stays up would otherwise leak a new detached wsl.exe sleep
            // process on each repair. Skip the spawn when a keepalive for this distro already exists.
            var localDataDir = SetupExistingGatewayClassifier.ResolveLocalDataPath();
            var markerPath = Path.Combine(localDataDir, "wsl-keepalive", $"{distroName}.json");
            if (IsKeepAliveRunningForDistro(distroName, markerPath))
            {
                Logger.Info($"[WslKeepAlive] Keepalive already running for {distroName}; skipping spawn.");
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ResolveWslExePath(),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(distroName);
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add("sleep");
            psi.ArgumentList.Add("infinity");

            var proc = System.Diagnostics.Process.Start(psi);
            if (proc is not null)
            {
                Logger.Info($"[WslKeepAlive] Started keepalive for {distroName} (PID {proc.Id}).");
                WriteKeepAliveMarker(
                    markerPath,
                    distroName,
                    proc.Id,
                    proc.StartTime.ToUniversalTime());
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[WslKeepAlive] Startup keepalive failed (non-fatal): {ex.Message}");
        }
    }

    private async Task StopStaleLocalGatewayKeepAliveAsync()
    {
        try
        {
            var localDataDir = SetupExistingGatewayClassifier.ResolveLocalDataPath();
            var markerDir = Path.Combine(localDataDir, "wsl-keepalive");
            var markerDistroNames = ReadKeepAliveMarkerDistroNames(markerDir);
            var setupStateDistroName = await ReadSetupStateDistroNameAsync(localDataDir);
            var records = _getRegistry()?.GetAll() ?? [];

            foreach (var distroName in WslKeepAlivePolicy.FindStaleSetupManagedDistroNames(
                records,
                markerDistroNames,
                setupStateDistroName))
            {
                StopKeepAliveProcessesForDistro(distroName);
                DeleteKeepAliveMarker(markerDir, distroName);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[WslKeepAlive] Stale keepalive cleanup failed (non-fatal): {ex.Message}");
        }
    }

    private static IReadOnlyList<string> ReadKeepAliveMarkerDistroNames(string markerDir)
    {
        if (!Directory.Exists(markerDir))
            return [];

        var distroNames = new List<string>();
        foreach (var markerPath in Directory.EnumerateFiles(markerDir, "*.json"))
        {
            if (WslKeepAlivePolicy.TryGetMarkerDistroName(File.ReadAllText(markerPath), out var distroName))
                distroNames.Add(distroName);
        }

        return distroNames;
    }

    private static async Task<string?> ReadSetupStateDistroNameAsync(string localDataDir)
    {
        var stateFile = Path.Combine(localDataDir, "setup-state.json");
        if (!File.Exists(stateFile))
            return null;

        var json = await File.ReadAllTextAsync(stateFile);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("DistroName", out var distroElement)
            ? distroElement.GetString()
            : null;
    }

    private static bool IsKeepAliveRunningForDistro(string distroName, string markerPath)
    {
        if (TryGetMarkedKeepAlive(markerPath, distroName))
            return true;

        var procs = System.Diagnostics.Process.GetProcessesByName("wsl")
            .Concat(System.Diagnostics.Process.GetProcessesByName("wsl.exe"))
            .ToArray();

        try
        {
            foreach (var proc in procs)
            {
                try
                {
                    var commandLine = GetProcessCommandLine(proc.Id);

                    // Unreadable unrelated WSL processes are not proof that our keepalive exists.
                    // Owned keepalives have a PID marker, checked above.
                    if (commandLine is null)
                        continue;
                    if (WslKeepAlivePolicy.IsKeepaliveCommandLine(commandLine, distroName))
                        return true; // positively found
                }
                // slopwatch-ignore: SW003 Inspection is best-effort; a process may exit mid-enumeration and that cannot improve caller state.
                catch
                {
                    // Process exited or is protected; continue looking for a positive match.
                }
            }

            // Every live wsl process was readable and none was a keepalive for this distro — safe to
            // spawn. A genuinely idled-out VM has no wsl processes at all and also lands here.
            return false;
        }
        finally
        {
            foreach (var proc in procs)
            {
                // slopwatch-ignore: SW003 Best-effort disposal of enumerated process handles.
                try { proc.Dispose(); } catch { }
            }
        }
    }

    private static bool TryGetMarkedKeepAlive(string markerPath, string distroName)
    {
        if (!File.Exists(markerPath))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(markerPath));
            if (!doc.RootElement.TryGetProperty("DistroName", out var distroElement) ||
                !string.Equals(distroElement.GetString(), distroName, StringComparison.OrdinalIgnoreCase) ||
                !doc.RootElement.TryGetProperty("Pid", out var pidElement) ||
                !pidElement.TryGetInt32(out var pid) ||
                !doc.RootElement.TryGetProperty("StartTimeUtc", out var startElement) ||
                !startElement.TryGetDateTime(out var markerStartTimeUtc))
            {
                return false;
            }

            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (process.HasExited ||
                !WslKeepAlivePolicy.IsMarkedKeepaliveProcessIdentity(
                    process.ProcessName,
                    process.StartTime.ToUniversalTime(),
                    markerStartTimeUtc))
                return false;

            // The marker was written only after this service/setup spawned the process. A transient
            // CIM failure therefore remains safe to treat as running for this exact owned PID.
            var commandLine = GetProcessCommandLine(pid);
            return commandLine is null ||
                WslKeepAlivePolicy.IsKeepaliveCommandLine(commandLine, distroName);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteKeepAliveMarker(
        string markerPath,
        string distroName,
        int pid,
        DateTime processStartTimeUtc)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            var json = JsonSerializer.Serialize(new
            {
                DistroName = distroName,
                Pid = pid,
                StartTimeUtc = processStartTimeUtc,
                ProcessName = "wsl"
            });
            var tempPath = markerPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, markerPath, overwrite: true);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[WslKeepAlive] Could not persist keepalive marker: {ex.Message}");
        }
    }

    private static void StopKeepAliveProcessesForDistro(string distroName)
    {
        var procs = System.Diagnostics.Process.GetProcessesByName("wsl")
            .Concat(System.Diagnostics.Process.GetProcessesByName("wsl.exe"));

        foreach (var proc in procs)
        {
            try
            {
                if (WslKeepAlivePolicy.IsKeepaliveCommandLine(GetProcessCommandLine(proc.Id), distroName))
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                    Logger.Info($"[WslKeepAlive] Stopped stale keepalive for {distroName} (PID {proc.Id}).");
                }
            }
            // slopwatch-ignore: SW003 Cleanup is best-effort; failure cannot improve caller state and the original outcome is preserved.
            catch
            {
                // Process may have exited while being inspected.
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    private static void DeleteKeepAliveMarker(string markerDir, string distroName)
    {
        if (!Directory.Exists(markerDir))
            return;

        foreach (var markerPath in Directory.EnumerateFiles(markerDir, "*.json"))
        {
            try
            {
                if (WslKeepAlivePolicy.TryGetMarkerDistroName(File.ReadAllText(markerPath), out var markerDistro)
                    && string.Equals(markerDistro, distroName, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(markerPath);
                    Logger.Info($"[WslKeepAlive] Deleted stale keepalive marker for {distroName}.");
                }
            }
            // slopwatch-ignore: SW003 Cleanup is best-effort; failure cannot improve caller state and the original outcome is preserved.
            catch
            {
                // Best-effort cleanup; stale/corrupt markers are not fatal.
            }
        }
    }

    private static string? GetProcessCommandLine(int pid)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                $"-NoProfile -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').CommandLine\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;

            // Drain stdout asynchronously so a large command line cannot deadlock the fixed-size pipe,
            // and bound the whole inspection: WaitForExit(5000) returns before ReadToEnd could block
            // forever on a hung CIM/PowerShell. On timeout, kill and report indeterminate (null).
            var readTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                // slopwatch-ignore: SW003 Best-effort kill of a stuck inspection process; failure cannot improve caller state.
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            return readTask.GetAwaiter().GetResult()?.Trim();
        }
        catch { return null; }
    }

    private static string ResolveWslExePath()
    {
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windowsDir))
            windowsDir = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

        return Path.Combine(windowsDir, "System32", "wsl.exe");
    }

    /// <summary>
    /// Resolves the WSL distro name to keep alive. Prefers the value persisted by
    /// onboarding in <c>setup-state.json</c> so the keepalive always targets the distro
    /// the user actually installed. In DEBUG / test builds, an
    /// <c>OPENCLAW_WSL_DISTRO_NAME</c> environment override is honored. Falls
    /// back to the current dev or release app identity if no state exists.
    /// </summary>
    private async Task<string?> ResolveLocalGatewayDistroNameAsync(GatewayRecord? activeRecord)
    {
        string? setupStateDistroName = null;
        try
        {
            var stateFile = Path.Combine(
                SetupExistingGatewayClassifier.ResolveLocalDataPath(),
                "setup-state.json");

            if (File.Exists(stateFile))
            {
                var json = await File.ReadAllTextAsync(stateFile);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("DistroName", out var dn) &&
                    dn.GetString() is { Length: > 0 } distroName)
                {
                    setupStateDistroName = distroName;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[WslKeepAlive] Failed to read setup-state.json: {ex.Message}");
        }

        return WslKeepAlivePolicy.ResolveDistroName(
            activeRecord,
            setupStateDistroName,
            Environment.GetEnvironmentVariable("OPENCLAW_WSL_DISTRO_NAME"));
    }
}
