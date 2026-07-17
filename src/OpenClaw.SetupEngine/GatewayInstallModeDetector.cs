using OpenClaw.Connection;
using OpenClaw.Shared;
using System.Text.Json;

namespace OpenClaw.SetupEngine;

public static class GatewayInstallModeDetector
{
    internal const string NativeOwnershipFileName = "native-gateway-install.json";
    internal const string NativeProfileOwnershipFileName = "native-gateway-profile-owner.json";

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

        return string.Equals(active.FriendlyName, "Local (Windows)", StringComparison.Ordinal)
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
        var activeMarkerExists = HasNativeOwnershipMarker(localDataDir);
        var profileMarkerExists = HasNativeProfileOwnershipMarker(localDataDir);
        if (activeMarkerExists || profileMarkerExists)
        {
            return IsNativeOwnershipMarkerOwned(localDataDir, config)
                || IsNativeProfileOwnershipMarkerOwned(localDataDir, config);
        }

        return TryReadSetupStateMode(Path.Combine(localDataDir, "setup-state.json"), out var persistedMode)
            && persistedMode == GatewayInstallMode.NativeWindows;
    }

    public static bool HasManagedWslInstallation(
        string dataDir,
        string localDataDir,
        SetupConfig config)
    {
        if (TryReadSetupStateMode(Path.Combine(localDataDir, "setup-state.json"), out var persistedMode)
            && persistedMode == GatewayInstallMode.Wsl)
        {
            return true;
        }

        var installPath = Path.Combine(localDataDir, "wsl", config.DistroName);
        if (Directory.Exists(installPath) || File.Exists(installPath))
            return true;

        var registry = new GatewayRegistry(dataDir);
        registry.Load();
        return registry.GetAll().Any(record =>
            record.IsLocal
            && record.SshTunnel is null
            && (string.Equals(record.SetupManagedDistroName, config.DistroName, StringComparison.Ordinal)
                || (string.IsNullOrWhiteSpace(record.SetupManagedDistroName)
                    && string.Equals(record.FriendlyName, $"Local ({config.DistroName})", StringComparison.Ordinal)
                    && LocalGatewayUrlClassifier.IsLocalGatewayUrl(record.Url))));
    }

    internal static string GetNativeOwnershipPath(string localDataDir) =>
        Path.Combine(localDataDir, NativeOwnershipFileName);

    internal static bool HasNativeOwnershipMarker(string localDataDir) =>
        File.Exists(GetNativeOwnershipPath(localDataDir));

    internal static string GetNativeProfileOwnershipPath(string localDataDir) =>
        Path.Combine(localDataDir, NativeProfileOwnershipFileName);

    internal static bool HasNativeProfileOwnershipMarker(string localDataDir) =>
        File.Exists(GetNativeProfileOwnershipPath(localDataDir));

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
}
