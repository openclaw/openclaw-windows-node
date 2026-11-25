using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

/// <summary>Registers user-scoped cleanup that survives removal of the Companion MSIX.</summary>
public static class GatewayPackageCleanup
{
    private const string PolicyProbeScript = """
        $policies = Get-ExecutionPolicy -List
        $machine = ($policies | Where-Object Scope -eq 'MachinePolicy').ExecutionPolicy
        $user = ($policies | Where-Object Scope -eq 'UserPolicy').ExecutionPolicy
        [Console]::WriteLine("$machine|$user")
        """;
    private static readonly TimeSpan PolicyProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupTaskRemovalTimeout = TimeSpan.FromSeconds(30);
    private const string CleanupTaskRemovalPendingFileName = "cleanup-task-removal.pending";
    private const string PreservationPendingFileName = "gateway-preservation.pending";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder family);

    public static Task<bool> InitializeAsync(ICommandRunner commands, string dataDir, string localDataDir,
        CancellationToken ct = default, Func<string, bool>? preserve = null, Action<string>? warning = null) =>
        InitializePackagedAsync(commands, dataDir, localDataDir, ct, preserve, warning);

    private static async Task<bool> InitializePackagedAsync(ICommandRunner commands, string dataDir, string localDataDir,
        CancellationToken ct, Func<string, bool>? preserve, Action<string>? warning)
    {
        var directory = GetCleanupDirectory();
        if (directory is null) return true;
        var taskRemovalPendingPath = Path.Combine(directory, CleanupTaskRemovalPendingFileName);
        var preservationPendingPath = Path.Combine(directory, PreservationPendingFileName);
        var ownedPath = Path.Combine(directory, "owned.json");
        if (!File.Exists(ownedPath) &&
            !File.Exists(Path.Combine(directory, "cleanup-result.json")) &&
            !File.Exists(taskRemovalPendingPath) &&
            !File.Exists(preservationPendingPath)) return true;
        if (!await CanRunCleanupScriptAsync(commands, ct))
        {
            if (!File.Exists(ownedPath) && !File.Exists(taskRemovalPendingPath))
                await AtomicFile.WriteAllTextAsync(taskRemovalPendingPath, string.Empty, ct);

            bool initialized;
            try
            {
                initialized = await InitializeAsync(async (_, preserveGateways) =>
                {
                    if (preserveGateways) await AppendPreservedRemovalAsync(directory, ct);
                    return directory;
                }, dataDir, localDataDir, ct, preserve);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                InvalidOperationException or InvalidDataException or TimeoutException or
                System.ComponentModel.Win32Exception)
            {
                await ResolvePendingCleanupTaskRemovalAsync(
                    commands, directory, false, dataDir, localDataDir, CancellationToken.None, warning);
                throw;
            }

            await ResolvePendingCleanupTaskRemovalAsync(
                commands, directory, initialized, dataDir, localDataDir, ct, warning);
            warning?.Invoke(CleanupUnavailableWarning);
            return initialized;
        }

        try
        {
            var initialized = await InitializeAsync(
                (register, keep) => RunAsync(commands, register, null, null, false, ct, preserve: keep),
                dataDir, localDataDir, ct, preserve);
            await ResolvePendingCleanupTaskRemovalAsync(
                commands, directory, initialized, dataDir, localDataDir, ct, warning);
            return initialized;
        }
        catch (CleanupTaskRegistrationUnavailableException)
        {
            await ResolvePendingCleanupTaskRemovalAsync(
                commands, directory, false, dataDir, localDataDir, ct, warning);
            warning?.Invoke(CleanupUnavailableWarning);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidOperationException or InvalidDataException or TimeoutException or
            System.ComponentModel.Win32Exception)
        {
            await ResolvePendingCleanupTaskRemovalAsync(
                commands, directory, false, dataDir, localDataDir, CancellationToken.None, warning);
            throw;
        }
    }

    public const string CleanupUnavailableWarning =
        "Automatic Gateway cleanup is unavailable. Before uninstalling OpenClaw, remove the WSL Gateway in Settings or remove the Store Gateway in Windows Settings > Apps only if no other installed Companion variant uses it.";

    internal static async Task<bool> InitializeAsync(Func<bool, bool, Task<string?>> runCleanup,
        string dataDir, string localDataDir, CancellationToken ct = default, Func<string, bool>? preserve = null)
    {
        try
        {
            var directory = await runCleanup(false, false);
            if (directory is not null) await ReconcileAsync(directory, dataDir, localDataDir, ct);
            if (directory is not null &&
                File.Exists(Path.Combine(directory, "owned.json")) &&
                !File.Exists(Path.Combine(directory, PreservationPendingFileName)))
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

    public static async Task<bool> RegisterAsync(ICommandRunner commands, string? distroName = null,
        string? installPath = null, bool ownNativePackage = false, CancellationToken ct = default)
    {
        try
        {
            if (GetCleanupDirectory() is null || await CanRunCleanupScriptAsync(commands, ct))
            {
                await RunAsync(commands, true, distroName, installPath, ownNativePackage, ct,
                    createdDistro: distroName is not null);
                return true;
            }

            return false;
        }
        catch (CleanupTaskRegistrationUnavailableException)
        {
            return false;
        }
    }

    internal static bool IsCleanupScriptAllowedByPolicy(string? machinePolicy, string? userPolicy)
    {
        if (!IsKnownExecutionPolicy(machinePolicy) || !IsKnownExecutionPolicy(userPolicy)) return false;
        var machine = machinePolicy!.Trim();
        var user = userPolicy!.Trim();
        var effectivePolicy = !machine.Equals("Undefined", StringComparison.OrdinalIgnoreCase) ? machine : user;
        return !effectivePolicy!.Equals("AllSigned", StringComparison.OrdinalIgnoreCase) &&
            !effectivePolicy.Equals("Restricted", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownExecutionPolicy(string? policy) =>
        policy?.Trim() is { } value &&
        (value.Equals("Undefined", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Bypass", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Unrestricted", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("RemoteSigned", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("AllSigned", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Restricted", StringComparison.OrdinalIgnoreCase));

    private static bool IsCleanupRegistrationFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or
            TimeoutException or NotSupportedException;

    internal static async Task<bool> CanRunCleanupScriptAsync(ICommandRunner commands, CancellationToken ct)
    {
        CommandResult result;
        try
        {
            result = await commands.RunAsync(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe"),
                ["-NoProfile", "-NonInteractive", "-Command", PolicyProbeScript],
                PolicyProbeTimeout,
                ct: ct);
        }
        catch (Exception error) when (IsCleanupRegistrationFailure(error))
        {
            return false;
        }

        if (result.TimedOut || result.ExitCode != 0) return false;
        var policies = result.Stdout.Trim().Split('|');
        return policies.Length == 2 && IsCleanupScriptAllowedByPolicy(policies[0], policies[1]);
    }

    private static string? GetCleanupDirectory()
    {
        if (!OperatingSystem.IsWindows() ||
            Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") is { Length: > 0 }) return null;
        uint length = 256;
        var family = new StringBuilder((int)length);
        var result = GetCurrentPackageFamilyName(ref length, family);
        if (result == 15700) return null;
        if (result != 0) throw new InvalidOperationException("Companion package identity could not be read.");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            OpenClawAppIdentity.CompanionHomeDirectoryName, "cleanup", family.ToString());
    }

    private static async Task<string?> RunAsync(ICommandRunner commands, bool register, string? distroName,
        string? installPath, bool ownNativePackage, CancellationToken ct, bool preserve = false, bool createdDistro = false)
    {
        var directory = GetCleanupDirectory();
        if (directory is null) return null;
        var family = Path.GetFileName(directory);
        if (!register && !preserve && !File.Exists(Path.Combine(directory, "owned.json"))) return directory;
        var scriptPath = Path.Combine(directory, "cleanup.ps1");
        var hostPath = await DeployAsync(directory, ct);
        var arguments = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", scriptPath, "-Family", family.ToString(), "-CleanupHostPath", hostPath };
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
            const string unavailableMarker = "OPENCLAW_CLEANUP_TASK_UNAVAILABLE:";
            var markerIndex = registration.Stderr.IndexOf(unavailableMarker, StringComparison.Ordinal);
            if (markerIndex >= 0)
                throw new CleanupTaskRegistrationUnavailableException(
                    SetupLogger.Sanitize(registration.Stderr[(markerIndex + unavailableMarker.Length)..]));
            var diagnostic = SetupLogger.Sanitize(registration.Stderr);
            var message = register
                ? "Automatic Gateway cleanup could not be registered. " + diagnostic[..Math.Min(diagnostic.Length, 2048)]
                : "Previous managed Gateway cleanup remains pending. Reopen Companion to retry. " + diagnostic[..Math.Min(diagnostic.Length, 2048)];
            throw new InvalidOperationException(message);
        }
        return directory;
    }

    internal static async Task<string> DeployAsync(string directory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        string hostPath;
        using (var host = typeof(GatewayPackageCleanup).Assembly.GetManifestResourceStream(
            "OpenClaw.SetupEngine.GatewayCleanupHost.exe")
            ?? throw new InvalidOperationException("The Gateway cleanup host is missing."))
        {
            using var content = new MemoryStream();
            await host.CopyToAsync(content, ct);
            var bytes = content.ToArray();
            // Immutable versioned paths allow refresh while the previous task's executable is running.
            hostPath = Path.Combine(directory, "cleanup-host-" + Convert.ToHexString(SHA256.HashData(bytes)) + ".exe");
            var existing = File.Exists(hostPath) ? await File.ReadAllBytesAsync(hostPath, ct) : [];
            if (!bytes.AsSpan().SequenceEqual(existing))
            {
                var temporary = hostPath + ".tmp";
                await File.WriteAllBytesAsync(temporary, bytes, ct);
                File.Move(temporary, hostPath, overwrite: true);
            }
        }
        foreach (var (resource, file) in new[]
        {
            ("GatewayPackageCleanup.ps1", "cleanup.ps1"),
            ("GatewayPackageCleanup.Task.ps1", "cleanup-task.ps1")
        })
        {
            using var source = typeof(GatewayPackageCleanup).Assembly.GetManifestResourceStream(
                "OpenClaw.SetupEngine." + resource)
                ?? throw new InvalidOperationException("The Gateway cleanup helper is missing.");
            using var reader = new StreamReader(source);
            await AtomicFile.WriteAllTextAsync(Path.Combine(directory, file), await reader.ReadToEndAsync(ct), ct);
        }
        return hostPath;
    }

    private sealed class CleanupTaskRegistrationUnavailableException(string message) : Exception(message);

    private sealed record Removal(string? DistroName, string? InstallPath, string? NativeFamily);
    private sealed record PreservationIntent(string OwnedRecordSha256);
    private sealed record LegacySetupState(string? DistroName, string? GatewayUrl);
    private sealed record LegacySettings(string? GatewayUrl, bool? UseSshTunnel, string? PreferredGatewayId);

    private static async Task ReconcileAsync(string directory, string dataDir, string localDataDir, CancellationToken ct)
    {
        var receipt = Path.Combine(directory, "cleanup-result.json");
        if (!File.Exists(receipt)) return;
        using var cleanupLease = await AcquireCleanupLeaseAsync(directory, ct);
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

    internal static async Task AppendPreservedRemovalAsync(string directory, CancellationToken ct)
    {
        using var cleanupLease = await AcquireCleanupLeaseAsync(directory, ct);
        var ownedPath = Path.Combine(directory, "owned.json");
        if (!File.Exists(ownedPath)) return;

        var ownedRecord = await File.ReadAllBytesAsync(ownedPath, ct);
        var removal = DeserializeOwnedRemoval(ownedRecord);
        var pendingPath = Path.Combine(directory, PreservationPendingFileName);
        var intent = new PreservationIntent(Convert.ToHexString(SHA256.HashData(ownedRecord)));
        await AtomicFile.WriteAllTextAsync(pendingPath, JsonSerializer.Serialize(intent), ct);
        await AppendRemovalReceiptAsync(directory, removal, ct);
        File.Delete(ownedPath);
    }

    internal static async Task<bool> CompletePendingPreservationAsync(string directory, CancellationToken ct)
    {
        using var cleanupLease = await AcquireCleanupLeaseAsync(directory, ct);
        var pendingPath = Path.Combine(directory, PreservationPendingFileName);
        if (!File.Exists(pendingPath)) return false;

        var ownedPath = Path.Combine(directory, "owned.json");
        if (!File.Exists(ownedPath)) return true;

        PreservationIntent intent;
        try
        {
            intent = JsonSerializer.Deserialize<PreservationIntent>(
                await File.ReadAllTextAsync(pendingPath, ct))
                ?? throw new InvalidDataException("The Gateway preservation record is invalid.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The Gateway preservation record is invalid.", error);
        }

        var ownedRecord = await File.ReadAllBytesAsync(ownedPath, ct);
        var fingerprint = Convert.ToHexString(SHA256.HashData(ownedRecord));
        if (!string.Equals(intent.OwnedRecordSha256, fingerprint, StringComparison.OrdinalIgnoreCase))
            return false;

        await AppendRemovalReceiptAsync(directory, DeserializeOwnedRemoval(ownedRecord), ct);
        File.Delete(ownedPath);
        return true;
    }

    internal static async Task<bool> DisarmCleanupTaskAsync(
        ICommandRunner commands, string directory, CancellationToken ct, Action<string>? warning = null)
    {
        using var cleanupLease = await AcquireCleanupLeaseAsync(directory, ct);
        if (File.Exists(Path.Combine(directory, "owned.json"))) return false;

        var pendingPath = Path.Combine(directory, CleanupTaskRemovalPendingFileName);
        if (!File.Exists(pendingPath))
            await AtomicFile.WriteAllTextAsync(pendingPath, string.Empty, ct);

        if (!await TryRemoveCleanupTaskAsync(commands, Path.GetFileName(directory), ct, warning))
            return false;

        File.Delete(pendingPath);
        return true;
    }

    private static async Task ResolvePendingCleanupTaskRemovalAsync(
        ICommandRunner commands, string directory, bool initialized, string dataDir, string localDataDir,
        CancellationToken ct, Action<string>? warning)
    {
        var pendingPath = Path.Combine(directory, CleanupTaskRemovalPendingFileName);
        var ownedPath = Path.Combine(directory, "owned.json");
        var preservationPath = Path.Combine(directory, PreservationPendingFileName);
        if (File.Exists(preservationPath))
        {
            if (File.Exists(ownedPath))
            {
                if (!initialized) return;
                if (!await CompletePendingPreservationAsync(directory, ct))
                {
                    warning?.Invoke(CleanupUnavailableWarning);
                    return;
                }

                await ReconcileAsync(directory, dataDir, localDataDir, ct);
            }

            if (!File.Exists(ownedPath) &&
                await DisarmCleanupTaskAsync(commands, directory, ct, warning))
                File.Delete(preservationPath);
            return;
        }

        if (!File.Exists(pendingPath)) return;
        if (File.Exists(ownedPath))
        {
            if (initialized) File.Delete(pendingPath);
            return;
        }

        await DisarmCleanupTaskAsync(commands, directory, ct, warning);
    }

    private static Removal DeserializeOwnedRemoval(ReadOnlySpan<byte> json)
    {
        // Windows PowerShell 5.1 writes a BOM. Ignore it only for parsing, not for the ownership hash.
        if (json.StartsWith(Encoding.UTF8.Preamble))
            json = json[Encoding.UTF8.Preamble.Length..];

        try
        {
            return JsonSerializer.Deserialize<Removal>(json)
                ?? throw new InvalidDataException("The Gateway ownership record is invalid.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The Gateway ownership record is invalid.", error);
        }
    }

    private static async Task AppendRemovalReceiptAsync(string directory, Removal removal, CancellationToken ct)
    {
        var receiptPath = Path.Combine(directory, "cleanup-result.json");
        Removal[] removals = [];
        if (File.Exists(receiptPath))
        {
            try
            {
                removals = JsonSerializer.Deserialize<Removal[]>(await File.ReadAllTextAsync(receiptPath, ct))
                    ?? throw new InvalidDataException("The Gateway cleanup receipt is invalid.");
            }
            catch (JsonException error)
            {
                throw new InvalidDataException("The Gateway cleanup receipt is invalid.", error);
            }
            if (removals.Any(item => item is null))
                throw new InvalidDataException("The Gateway cleanup receipt contains an invalid removal.");
        }

        if (!removals.Contains(removal))
            await AtomicFile.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(removals.Append(removal)), ct);
    }

    private static async Task<bool> TryRemoveCleanupTaskAsync(
        ICommandRunner commands, string family, CancellationToken ct, Action<string>? warning)
    {
        try
        {
            await RemoveCleanupTaskAsync(commands, family, ct);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException or
            NotSupportedException or SecurityException)
        {
            warning?.Invoke("The Gateway cleanup task could not be removed. " + SetupLogger.Sanitize(error.Message));
            return false;
        }
    }

    private static async Task RemoveCleanupTaskAsync(ICommandRunner commands, string family, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Gateway cleanup task can only be removed on Windows.");

        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var taskName = $"OpenClaw Gateway cleanup - {sid} - {family}".Replace("'", "''", StringComparison.Ordinal);
        var command = $$"""
            $taskName = '{{taskName}}'
            try {
                $task = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
            } catch {
                if ($_.FullyQualifiedErrorId -like 'CmdletizationQuery_NotFound_TaskName,Get-ScheduledTask*') { exit 0 }
                throw
            }
            if ($task) {
                Unregister-ScheduledTask -InputObject $task -Confirm:$false -ErrorAction Stop
            }
            """;
        var result = await commands.RunAsync(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-Command", command],
            CleanupTaskRemovalTimeout,
            ct: ct);
        if (result.TimedOut)
            throw new TimeoutException("Removing the Gateway cleanup task timed out.");
        if (result.ExitCode != 0)
        {
            var diagnostic = SetupLogger.Sanitize(result.Stderr);
            throw new IOException("The Gateway cleanup task could not be removed. " +
                diagnostic[..Math.Min(diagnostic.Length, 2048)]);
        }
    }

    private static async Task<FileStream> AcquireCleanupLeaseAsync(string directory, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.Open(Path.Combine(directory, "cleanup.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 100)
            {
                await Task.Delay(100, ct);
            }
        }
    }
}
