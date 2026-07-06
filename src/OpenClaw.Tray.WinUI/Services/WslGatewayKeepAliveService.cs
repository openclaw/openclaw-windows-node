using OpenClaw.Connection;
using OpenClawTray;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            // Spawn a detached wsl sleep process to keep the VM alive
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
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output.Trim();
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
