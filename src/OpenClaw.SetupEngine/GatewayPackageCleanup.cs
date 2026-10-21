using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

/// <summary>Registers user-scoped cleanup that survives removal of the Companion MSIX.</summary>
public static class GatewayPackageCleanup
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder family);

    public static Task<bool> InitializeAsync(ICommandRunner commands, string dataDir, string localDataDir,
        CancellationToken ct = default, Func<string, bool>? preserve = null) =>
        InitializeAsync((register, keep) => RunAsync(commands, register, null, null, false, ct, preserve: keep),
            dataDir, localDataDir, ct, preserve);

    internal static async Task<bool> InitializeAsync(Func<bool, bool, Task<string?>> runCleanup,
        string dataDir, string localDataDir, CancellationToken ct = default, Func<string, bool>? preserve = null)
    {
        try
        {
            var directory = await runCleanup(false, false);
            if (directory is not null) await ReconcileAsync(directory, dataDir, localDataDir, ct);
            if (directory is not null && File.Exists(Path.Combine(directory, "owned.json")))
                await runCleanup(true, false);
            // Legacy name/path markers cannot identify the registration that Companion created.
            // Only fresh creation may bind a WSL registration for unattended uninstall cleanup.
            return true;
        }
        catch (Exception error) when (preserve is not null &&
            error is (IOException or UnauthorizedAccessException or InvalidOperationException))
        {
            if (!preserve(error.Message)) return false;
            var directory = await runCleanup(false, true);
            if (directory is not null) await ReconcileAsync(directory, dataDir, localDataDir, ct);
            return true;
        }
    }

    public static Task RegisterAsync(ICommandRunner commands, string? distroName = null,
        string? installPath = null, bool ownNativePackage = false, CancellationToken ct = default) =>
        RunAsync(commands, true, distroName, installPath, ownNativePackage, ct, createdDistro: distroName is not null);

    private static async Task<string?> RunAsync(ICommandRunner commands, bool register, string? distroName,
        string? installPath, bool ownNativePackage, CancellationToken ct, bool preserve = false, bool createdDistro = false)
    {
        if (!OperatingSystem.IsWindows() ||
            Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") is { Length: > 0 }) return null;
        uint length = 256;
        var family = new StringBuilder((int)length);
        var result = GetCurrentPackageFamilyName(ref length, family);
        if (result == 15700) return null; // Unpackaged installations retain their existing uninstaller.
        if (result != 0) throw new InvalidOperationException("Companion package identity could not be read.");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            OpenClawAppIdentity.CompanionHomeDirectoryName, "cleanup", family.ToString());
        if (!register && !preserve && !File.Exists(Path.Combine(directory, "owned.json"))) return directory;
        Directory.CreateDirectory(directory);
        var scriptPath = Path.Combine(directory, "cleanup.ps1");
        using var source = typeof(GatewayPackageCleanup).Assembly.GetManifestResourceStream(
            "OpenClaw.SetupEngine.GatewayPackageCleanup.ps1")
            ?? throw new InvalidOperationException("The Gateway cleanup helper is missing.");
        using var reader = new StreamReader(source);
        await AtomicFile.WriteAllTextAsync(scriptPath, await reader.ReadToEndAsync(ct), ct);
        var arguments = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", scriptPath, "-Family", family.ToString() };
        if (register) arguments.Add("-Register");
        if (preserve) arguments.Add("-Preserve");
        if (createdDistro) arguments.Add("-CreatedDistro");
        if (distroName is not null) arguments.AddRange(["-DistroName", distroName, "-InstallPath", installPath!]);
        if (ownNativePackage) arguments.Add("-OwnNativePackage");
        var registration = await commands.RunAsync(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"), arguments.ToArray(),
            TimeSpan.FromSeconds(register ? 30 : 180), ct: ct);
        if (registration.TimedOut || registration.ExitCode != 0)
        {
            if (registration.Stderr.Contains("OPENCLAW_CLEANUP_RECEIPT_INVALID", StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"The Gateway cleanup receipt '{Path.Combine(directory, "cleanup-result.json")}' is invalid. " +
                    "Restore a valid receipt from a backup, then reopen Companion. Cleanup remains pending.");
            var diagnostic = SetupLogger.Sanitize(registration.Stderr);
            throw new InvalidOperationException(register
                ? "Automatic Gateway cleanup could not be registered. " + diagnostic[..Math.Min(diagnostic.Length, 2048)]
                : "Previous managed Gateway cleanup remains pending. Reopen Companion to retry. " + diagnostic[..Math.Min(diagnostic.Length, 2048)]);
        }
        return directory;
    }

    private sealed record Removal(string? DistroName, string? InstallPath, string? NativeFamily);
    private sealed record LegacySetupState(string? DistroName, string? GatewayUrl);
    private sealed record LegacySettings(string? GatewayUrl, bool? UseSshTunnel, string? PreferredGatewayId);

    private static async Task ReconcileAsync(string directory, string dataDir, string localDataDir, CancellationToken ct)
    {
        var receipt = Path.Combine(directory, "cleanup-result.json");
        if (!File.Exists(receipt)) return;
        FileStream lease;
        for (var attempt = 0; ; attempt++)
        {
            try { lease = File.Open(Path.Combine(directory, "cleanup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
            catch (IOException) when (attempt < 100) { await Task.Delay(100, ct); }
        }
        using var cleanupLease = lease;
        try
        {
            if (!File.Exists(receipt)) return;
            var removals = JsonSerializer.Deserialize<Removal[]>(File.ReadAllText(receipt))
                ?? throw new InvalidDataException("The Gateway cleanup receipt is invalid.");
            if (removals.Any(removal => removal is null))
                throw new InvalidDataException("The Gateway cleanup receipt contains an invalid removal.");
            var registry = new GatewayRegistry(dataDir);
            registry.Load();
            var removed = registry.GetAll().Where(record => record.IsLocal && record.SshTunnel is null && removals.Any(
                removal => removal.DistroName is { Length: > 0 } distro && string.Equals(
                    GatewayRecordEditing.ResolveManagedDistroName(record), distro, StringComparison.OrdinalIgnoreCase) ||
                    removal.NativeFamily is { Length: > 0 } family && record.NativePackageFamilyName == family)).ToArray();
            var settingsPath = Path.Combine(dataDir, "settings.json");
            var statePath = Path.Combine(localDataDir, "setup-state.json");
            LegacySetupState? state = null;
            try { if (File.Exists(statePath)) state = JsonSerializer.Deserialize<LegacySetupState>(File.ReadAllText(statePath)); }
            catch (JsonException) { } // Invalid state cannot bind legacy credentials to a removed Gateway.
            var retiredState = removals.Any(removal => removal.DistroName is { Length: > 0 } distro &&
                string.Equals(state?.DistroName, distro, StringComparison.OrdinalIgnoreCase) &&
                ManagedDistroOwnership.HasPathBoundMarkerEvidence(localDataDir, distro));
            using (PersistenceFileLease.Acquire(settingsPath))
            {
                var settings = File.Exists(settingsPath)
                    ? JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject
                        ?? throw new InvalidDataException("The saved Gateway settings must contain a JSON object.")
                    : null;
                var legacySettings = settings?.Deserialize<LegacySettings>();
                foreach (var record in removed)
                    if (!Guid.TryParse(record.Id, out _) || !DeviceIdentity.BeginClearAllDeviceTokens(registry.GetIdentityDirectory(record.Id)).Success)
                        throw new IOException("The removed Gateway's credentials could not be retired.");
                if (settings is not null)
                {
                    var legacyUrl = legacySettings?.GatewayUrl;
                    if (legacySettings?.UseSshTunnel != true && (removed.Any(record => record.Url == legacyUrl ||
                        GatewayRecordEditing.AreEquivalentLoopbackEndpoints(record.Url, legacyUrl) ||
                        string.IsNullOrWhiteSpace(legacyUrl) && record.Id == registry.ActiveGatewayId) ||
                        retiredState && GatewayRecordEditing.AreEquivalentLoopbackEndpoints(legacyUrl, state?.GatewayUrl)))
                    {
                        if (!DeviceIdentity.BeginClearAllDeviceTokens(dataDir).Success)
                            throw new IOException("The removed Gateway's legacy credentials could not be retired.");
                        settings["GatewayUrl"] = "";
                        settings.Remove("Token");
                        settings.Remove("BootstrapToken");
                    }
                    if (removed.Any(record => record.Id == legacySettings?.PreferredGatewayId)) settings["PreferredGatewayId"] = null;
                    AtomicFile.WriteAllText(settingsPath, settings.ToJsonString(new() { WriteIndented = true }));
                }
            }
            foreach (var record in removed) registry.Remove(record.Id);
            if (removed.Length > 0) registry.Save();
            if (retiredState) File.Delete(statePath);
            foreach (var removal in removals)
                if (removal.DistroName is { Length: > 0 } distro && removal.InstallPath is { Length: > 0 } path &&
                    ManagedDistroOwnership.HasPathBoundMarkerEvidence(localDataDir, distro) &&
                    !ManagedDistroOwnership.DeleteMarker(localDataDir, distro, path))
                    throw new IOException("The removed Gateway's setup ownership could not be retired.");
            File.Delete(receipt);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            throw new InvalidDataException(
                $"The Gateway cleanup receipt '{receipt}' or saved settings '{Path.Combine(dataDir, "settings.json")}' are invalid. " +
                "Cleanup remains pending. Restore valid files from a backup, then reopen Companion.", error);
        }
    }
}
