using OpenClaw.Shared;
using OpenClaw.Connection;
using OpenClawTray.Services.LocalGatewaySetup;
using System.Text.Json;

namespace OpenClawTray.Services;

public enum SetupExistingGatewayKind
{
    None = 0,
    AppOwnedLocalWsl = 1,
    ExternalOnly = 2,
}

public static class SetupExistingGatewayClassifier
{
    private const string AppOwnedDistroName = "OpenClawGateway";

    public static SetupExistingGatewayKind ClassifyWithoutWslProbe(
        GatewayRegistry? registry,
        SettingsManager settings,
        string dataPath)
    {
        return HasAnyExistingGatewayConnection(registry, settings, dataPath)
            ? SetupExistingGatewayKind.ExternalOnly
            : SetupExistingGatewayKind.None;
    }

    public static async Task<SetupExistingGatewayKind> ClassifyAsync(
        GatewayRegistry? registry,
        SettingsManager settings,
        string dataPath,
        IWslCommandRunner? wsl = null,
        CancellationToken cancellationToken = default,
        string? localDataPath = null)
    {
        var hasAnyGateway = HasAnyExistingGatewayConnection(registry, settings, dataPath);
        if (await HasAppOwnedLocalWslGatewayAsync(
                registry,
                localDataPath ?? GetLocalDataPath(),
                wsl,
                cancellationToken).ConfigureAwait(false))
        {
            return SetupExistingGatewayKind.AppOwnedLocalWsl;
        }

        return hasAnyGateway ? SetupExistingGatewayKind.ExternalOnly : SetupExistingGatewayKind.None;
    }

    public static bool HasAnyExistingGatewayConnection(
        GatewayRegistry? registry,
        SettingsManager settings,
        string dataPath)
    {
        if (registry is not null)
        {
            foreach (var record in registry.GetAll())
            {
                if (HasUsableGatewayRecord(registry, record))
                {
                    return true;
                }
            }
        }

        return StartupSetupState.HasUsableOperatorConfiguration(settings, dataPath);
    }

    public static bool HasUsableGatewayRecord(GatewayRegistry registry, GatewayRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.SharedGatewayToken)
            || !string.IsNullOrWhiteSpace(record.BootstrapToken))
        {
            return true;
        }

        var identityDir = registry.GetIdentityDirectory(record.Id);
        return DeviceIdentity.HasStoredDeviceTokenForRole(identityDir, "operator", NullLogger.Instance)
            || DeviceIdentity.HasStoredDeviceTokenForRole(identityDir, "node", NullLogger.Instance);
    }

    private static async Task<bool> HasAppOwnedLocalWslGatewayAsync(
        GatewayRegistry? registry,
        string localDataPath,
        IWslCommandRunner? wsl,
        CancellationToken cancellationToken)
    {
        var hasLocalSetupEvidence = HasLocalSetupEvidence(registry, localDataPath);
        wsl ??= new WslExeCommandRunner(NullLogger.Instance);
        try
        {
            var distros = await wsl.ListDistrosAsync(cancellationToken).ConfigureAwait(false);
            var hasAppOwnedDistro = distros.Any(d => string.Equals(d.Name, AppOwnedDistroName, StringComparison.OrdinalIgnoreCase));
            return hasAppOwnedDistro && hasLocalSetupEvidence;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[SetupExistingGatewayClassifier] WSL distro probe failed: {ex.Message}");
            return hasLocalSetupEvidence;
        }
    }

    private static bool HasLocalSetupEvidence(GatewayRegistry? registry, string localDataPath)
    {
        if (registry is not null
            && registry.GetAll().Any(record =>
                record.IsLocal
                && record.SshTunnel is null
                && LocalGatewayUrlClassifier.IsLocalGatewayUrl(record.Url)))
        {
            return true;
        }

        var setupStatePath = Path.Combine(localDataPath, "setup-state.json");
        if (File.Exists(setupStatePath) && SetupStateLooksLocal(setupStatePath))
        {
            return true;
        }

        return false;
    }

    private static bool SetupStateLooksLocal(string setupStatePath)
    {
        try
        {
            var json = File.ReadAllText(setupStatePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var distroMatches = root.TryGetProperty("DistroName", out var distroEl)
                && string.Equals(distroEl.GetString(), AppOwnedDistroName, StringComparison.OrdinalIgnoreCase);
            if (!distroMatches)
            {
                return false;
            }

            if (root.TryGetProperty("Phase", out var phaseEl))
            {
                var phaseName = phaseEl.GetString();
                return phaseName is not (null or "NotStarted" or "Failed" or "Cancelled");
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[SetupExistingGatewayClassifier] Failed to read setup-state.json: {ex.Message}");
            return false;
        }
    }

    private static string GetLocalDataPath()
    {
        return Path.Combine(
            Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenClawTray");
    }
}
