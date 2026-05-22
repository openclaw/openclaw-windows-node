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
        string? localDataPath = null,
        IOpenClawLogger? logger = null)
    {
        var hasAnyGateway = HasAnyExistingGatewayConnection(registry, settings, dataPath);
        if (await HasAppOwnedLocalWslGatewayAsync(
                registry,
                localDataPath ?? GetLocalDataPath(),
                wsl,
                cancellationToken,
                logger).ConfigureAwait(false))
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
        CancellationToken cancellationToken,
        IOpenClawLogger? logger = null)
    {
        var hasLocalSetupEvidence = HasLocalSetupEvidence(registry, localDataPath);
        // Use the caller-supplied logger so probe failures / WSL stderr
        // surface in the same diagnostic stream as the rest of setup.
        // NullLogger.Instance swallows the most useful breadcrumbs ("WSL
        // platform not installed", "wsl --list timed out") that would
        // otherwise let us correlate "first-run hangs ~30s" reports.
        var probeLogger = logger ?? NullLogger.Instance;
        wsl ??= new WslExeCommandRunner(probeLogger);
        try
        {
            // Bound the WSL probe with a fast-fail platform check first. On
            // hosts where the WSL platform is not installed, the underlying
            // `wsl --list --verbose` hangs for the runner's full default
            // timeout (30s today, 30 minutes in some callers). That blocks
            // the "Set up locally" click handler before navigation can even
            // begin. The platform probe completes in ~1s (or its own 5s
            // ceiling on slow hosts) and tells us we can skip the distro
            // probe entirely — no platform → no possible app-owned distro.
            var platform = await new OpenClawTray.Services.LocalGatewaySetup.WslPlatformProbe(wsl)
                .ProbeAsync(cancellationToken)
                .ConfigureAwait(false);

            // Round-3 fix: distinguish NotInstalled (definitive) from
            // Unknown (transient — probe timed out / policy-blocked / etc).
            // For NotInstalled, fall back to local-setup evidence (no
            // distro could possibly exist). For Unknown we should NOT
            // confidently label setup as AppOwnedLocalWsl based on
            // evidence alone — that would steer the user into the
            // "replace existing local gateway" UX path even though we
            // couldn't actually confirm the distro exists. Treat Unknown
            // conservatively as "no app-owned distro confirmed".
            if (platform.State == OpenClawTray.Services.LocalGatewaySetup.WslPlatformState.NotInstalled)
            {
                return hasLocalSetupEvidence;
            }
            if (platform.State == OpenClawTray.Services.LocalGatewaySetup.WslPlatformState.Unknown)
            {
                // Use the caller-supplied logger (probeLogger) so this
                // breadcrumb lands in the same diagnostic stream as the
                // rest of setup — see the comment ~30 lines above on
                // why NullLogger swallowing this kind of signal made
                // first-run hangs hard to correlate.
                probeLogger.Warn("[SetupExistingGatewayClassifier] WSL platform probe returned Unknown; cannot confirm app-owned distro — treating as not present.");
                return false;
            }

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
