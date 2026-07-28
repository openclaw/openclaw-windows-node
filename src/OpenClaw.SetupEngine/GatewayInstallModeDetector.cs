using OpenClaw.Connection;
using OpenClaw.Shared;
using System.Text.Json;

namespace OpenClaw.SetupEngine;

public static class GatewayInstallModeDetector
{
    internal const string NativeOwnershipFileName = "native-gateway-install.json";
    internal const string NativeProfileOwnershipFileName = "native-gateway-profile-owner.json";
    internal const string WslOwnershipFileName = "wsl-gateway-install.json";
    internal const string NativeStopIntentFileName = "native-gateway-user-stopped.json";

    public static string GetNativeWizardLogPath(SetupConfig config) =>
        Path.Combine(GatewayCliRunner.GetManagedNativeStateDir(config), "logs", "wizard-console.log");

    public static GatewayInstallMode Detect(string dataDir, GatewayInstallMode fallback)
    {
        var registry = new GatewayRegistry(dataDir);
        registry.Load();
        var active = registry.GetActive();

        if (active is not { IsLocal: true })
            return fallback;

        if (!string.IsNullOrWhiteSpace(active.SetupManagedDistroName))
            return GatewayInstallMode.Wsl;

        return !string.IsNullOrWhiteSpace(active.SetupManagedNativeTaskName)
            ? GatewayInstallMode.NativeWindows
            : fallback;
    }

    public static GatewayInstallMode DetectInstalled(
        string dataDir,
        string localDataDir,
        GatewayInstallMode fallback)
    {
        // The ownership marker is written before native configuration/service work.
        // It therefore represents the newest setup intent after an interrupted mode switch.
        if (HasNativeOwnershipMarker(localDataDir))
            return GatewayInstallMode.NativeWindows;

        if (TryReadSetupStateMode(Path.Combine(localDataDir, "setup-state.json"), out var persistedMode))
            return persistedMode;

        var registry = new GatewayRegistry(dataDir);
        registry.Load();
        var localRecords = registry.GetAll().Where(record => record.IsLocal && record.SshTunnel is null).ToArray();

        if (localRecords.Any(record => !string.IsNullOrWhiteSpace(record.SetupManagedDistroName)))
            return GatewayInstallMode.Wsl;

        return fallback;
    }

    public static bool HasManagedNativeInstallation(string dataDir, string localDataDir)
    {
        if (HasNativeOwnershipMarker(localDataDir))
            return true;

        if (TryReadSetupStateMode(Path.Combine(localDataDir, "setup-state.json"), out var persistedMode))
            return persistedMode == GatewayInstallMode.NativeWindows;

        return false;
    }

    public static bool HasManagedNativeInstallation(
        string dataDir,
        string localDataDir,
        SetupConfig config)
    {
        var ownership = GetNativeOwnershipState(localDataDir, config);
        if (ownership != ManagedInstallationOwnership.Absent)
        {
            return ownership == ManagedInstallationOwnership.Owned;
        }

        return TryReadSetupStateMode(Path.Combine(localDataDir, "setup-state.json"), out var persistedMode)
            && persistedMode == GatewayInstallMode.NativeWindows;
    }

    public static bool HasManagedWslInstallation(
        string dataDir,
        string localDataDir,
        SetupConfig config)
    {
        var ownership = GetWslOwnershipState(localDataDir, config);
        if (ownership != ManagedInstallationOwnership.Absent)
        {
            return ownership == ManagedInstallationOwnership.Owned;
        }

        if (!TryReadSetupStateMode(Path.Combine(localDataDir, "setup-state.json"), out var persistedMode)
            || persistedMode != GatewayInstallMode.Wsl
            || !DistroInstallPathPolicy.TryGetManagedInstallPath(
                localDataDir,
                config.DistroName,
                out var installPath,
                out _)
            || (!Directory.Exists(installPath) && !File.Exists(installPath)))
        {
            return false;
        }

        var registry = new GatewayRegistry(dataDir);
        registry.Load();
        return registry.GetAll().Any(record =>
            record.IsLocal
            && record.SshTunnel is null
            && string.Equals(record.SetupManagedDistroName, config.DistroName, StringComparison.Ordinal));
    }

    internal static string? GetUninstallOwnershipError(string localDataDir, SetupConfig config)
    {
        if (GetNativeOwnershipState(localDataDir, config) == ManagedInstallationOwnership.ForeignOrInvalid)
        {
            return "Native gateway ownership markers do not match the configured profile and Scheduled Task. Repair the existing installation before uninstalling.";
        }

        if (GetWslOwnershipState(localDataDir, config) == ManagedInstallationOwnership.ForeignOrInvalid)
        {
            return "WSL gateway ownership marker does not match the configured distro and install path. Repair the existing installation before uninstalling.";
        }

        return null;
    }

    internal static string GetNativeOwnershipPath(string localDataDir) =>
        Path.Combine(localDataDir, NativeOwnershipFileName);

    internal static bool HasNativeOwnershipMarker(string localDataDir) =>
        File.Exists(GetNativeOwnershipPath(localDataDir));

    internal static string GetNativeProfileOwnershipPath(string localDataDir) =>
        Path.Combine(localDataDir, NativeProfileOwnershipFileName);

    internal static bool HasNativeProfileOwnershipMarker(string localDataDir) =>
        File.Exists(GetNativeProfileOwnershipPath(localDataDir));

    internal static string GetWslOwnershipPath(string localDataDir) =>
        Path.Combine(localDataDir, WslOwnershipFileName);

    internal static bool HasWslOwnershipMarker(string localDataDir) =>
        File.Exists(GetWslOwnershipPath(localDataDir));

    internal static async Task WriteWslOwnershipMarkerAsync(
        string localDataDir,
        SetupConfig config,
        string? gatewayRecordId,
        CancellationToken ct)
    {
        if (!DistroInstallPathPolicy.TryGetManagedInstallPath(
                localDataDir,
                config.DistroName,
                out var installPath,
                out var pathError))
        {
            throw new InvalidOperationException($"Cannot persist WSL ownership: {pathError}");
        }

        Directory.CreateDirectory(localDataDir);
        var marker = new
        {
            SchemaVersion = 1,
            InstallMode = GatewayInstallMode.Wsl.ToString(),
            DistroName = config.DistroName,
            GatewayPort = config.GatewayPort,
            GatewayRecordId = gatewayRecordId,
            InstallPath = Path.GetFullPath(installPath),
        };
        await AtomicFile.WriteAllTextAsync(
            GetWslOwnershipPath(localDataDir),
            JsonSerializer.Serialize(marker, SetupConfig.JsonWriteOptions),
            ct);
    }

    internal static void DeleteWslOwnershipMarker(string localDataDir)
    {
        var path = GetWslOwnershipPath(localDataDir);
        if (File.Exists(path))
            File.Delete(path);
    }

    public static string GetNativeStopIntentPath(string localDataDir) =>
        Path.Combine(localDataDir, NativeStopIntentFileName);

    public static void DeleteNativeStopIntent(string localDataDir)
    {
        var path = GetNativeStopIntentPath(localDataDir);
        if (File.Exists(path))
            File.Delete(path);
    }

    internal static bool IsNativeProfileOwned(string localDataDir, SetupConfig config)
    {
        foreach (var path in new[]
                 {
                     GetNativeOwnershipPath(localDataDir),
                     GetNativeProfileOwnershipPath(localDataDir),
                 })
        {
            if (IsNativeOwnershipFileOwned(path, config))
                return true;
        }

        return false;
    }

    internal static bool IsNativeProfileOwnershipMarkerOwned(string localDataDir, SetupConfig config) =>
        IsNativeOwnershipFileOwned(GetNativeProfileOwnershipPath(localDataDir), config);

    internal static bool IsNativeOwnershipMarkerOwned(string localDataDir, SetupConfig config) =>
        IsNativeOwnershipFileOwned(GetNativeOwnershipPath(localDataDir), config);

    internal static bool IsNativeOwnershipFileOwned(string path, SetupConfig config)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("ProfileName", out var profile)
                && profile.ValueKind == JsonValueKind.String
                && document.RootElement.TryGetProperty("TaskName", out var task)
                && task.ValueKind == JsonValueKind.String
                && string.Equals(
                    profile.GetString(),
                    GatewayCliRunner.GetManagedNativeProfile(config),
                    StringComparison.Ordinal)
                && string.Equals(
                    task.GetString(),
                    GatewayCliRunner.GetManagedNativeTaskName(config),
                    StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static ManagedInstallationOwnership GetNativeOwnershipState(
        string localDataDir,
        SetupConfig config)
    {
        var paths = new[]
        {
            GetNativeOwnershipPath(localDataDir),
            GetNativeProfileOwnershipPath(localDataDir),
        };
        var existing = paths.Where(File.Exists).ToArray();
        if (existing.Length == 0)
            return ManagedInstallationOwnership.Absent;

        return existing.Any(path => IsNativeOwnershipFileOwned(path, config))
            ? ManagedInstallationOwnership.Owned
            : ManagedInstallationOwnership.ForeignOrInvalid;
    }

    private static ManagedInstallationOwnership GetWslOwnershipState(
        string localDataDir,
        SetupConfig config)
    {
        var path = GetWslOwnershipPath(localDataDir);
        if (!File.Exists(path))
            return ManagedInstallationOwnership.Absent;

        try
        {
            if (!DistroInstallPathPolicy.TryGetManagedInstallPath(
                    localDataDir,
                    config.DistroName,
                    out var expectedInstallPath,
                    out _))
            {
                return ManagedInstallationOwnership.ForeignOrInvalid;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("InstallMode", out var installMode)
                && string.Equals(installMode.GetString(), GatewayInstallMode.Wsl.ToString(), StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("DistroName", out var distro)
                && string.Equals(distro.GetString(), config.DistroName, StringComparison.Ordinal)
                && root.TryGetProperty("InstallPath", out var installPath)
                && string.Equals(
                    Path.GetFullPath(installPath.GetString() ?? string.Empty),
                    Path.GetFullPath(expectedInstallPath),
                    StringComparison.OrdinalIgnoreCase)
                ? ManagedInstallationOwnership.Owned
                : ManagedInstallationOwnership.ForeignOrInvalid;
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException or NotSupportedException)
        {
            return ManagedInstallationOwnership.ForeignOrInvalid;
        }
    }

    private static bool TryReadSetupStateMode(string path, out GatewayInstallMode mode)
    {
        mode = default;
        if (!File.Exists(path))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("InstallMode", out var installMode)
                || installMode.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            return Enum.TryParse(installMode.GetString(), ignoreCase: true, out mode);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private enum ManagedInstallationOwnership
    {
        Absent,
        Owned,
        ForeignOrInvalid,
    }
}
