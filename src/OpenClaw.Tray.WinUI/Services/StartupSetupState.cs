using OpenClaw.Shared;
using OpenClaw.Connection;

namespace OpenClawTray.Services;

internal static class StartupSetupState
{
    public static bool HasStoredNodeDeviceToken(string dataPath, GatewayRegistry? registry = null) =>
        HasAnyDeviceTokenForRole(dataPath, "node", registry);

    /// <summary>
    /// True if the user has an operator device token (root or any per-gateway dir)
    /// AND a configured gateway target (non-default <c>GatewayUrl</c> or an SSH tunnel
    /// host). Both signals together indicate a working operator config.
    /// </summary>
    public static bool HasUsableOperatorConfiguration(SettingsManager settings, string dataPath) =>
        HasAnyDeviceTokenForRole(dataPath, "operator")
        && HasAnyConfiguredGatewayTarget(settings);

    /// <summary>
    /// Scans both the legacy root identity file and per-gateway identity directories
    /// for a device token for the specified role.
    /// </summary>
    internal static bool HasAnyDeviceTokenForRole(
        string dataPath,
        string role,
        GatewayRegistry? registry = null)
    {
        string? activeIdentityDir = null;
        if (!string.IsNullOrWhiteSpace(registry?.ActiveGatewayId))
        {
            activeIdentityDir = registry.GetIdentityDirectory(registry.ActiveGatewayId);
            if (DeviceIdentity.HasStoredDeviceTokenForRole(
                    activeIdentityDir,
                    role,
                    NullLogger.Instance))
            {
                return true;
            }
        }

        if (DeviceIdentity.HasStoredDeviceTokenForRole(dataPath, role, NullLogger.Instance))
            return true;

        var gatewaysDir = Path.Combine(dataPath, "gateways");
        if (!Directory.Exists(gatewaysDir))
            return false;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(gatewaysDir))
            {
                if (string.Equals(
                        Path.GetFullPath(dir),
                        activeIdentityDir is null ? null : Path.GetFullPath(activeIdentityDir),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    if (DeviceIdentity.HasStoredDeviceTokenForRole(dir, role, NullLogger.Instance))
                        return true;
                }
                catch (DeviceIdentityLoadException)
                {
                    Logger.Debug($"Stored device token scan skipped an unusable gateway identity: {dir}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Stored device token scan skipped a gateway directory: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// True when the user has configured an actual gateway target — either a
    /// non-default <c>GatewayUrl</c> or an SSH tunnel host.
    /// </summary>
    internal static bool HasAnyConfiguredGatewayTarget(SettingsManager settings)
    {
        if (settings.UseSshTunnel && !string.IsNullOrWhiteSpace(settings.SshTunnelHost))
        {
            return true;
        }

        return HasNonDefaultGatewayUrl(settings);
    }

    private static bool HasNonDefaultGatewayUrl(SettingsManager settings) =>
        !string.IsNullOrWhiteSpace(settings.GatewayUrl)
        && !string.Equals(
            settings.GatewayUrl,
            AppIdentity.SetupGatewayUrl,
            StringComparison.OrdinalIgnoreCase);

    public static bool CanStartNodeGateway(SettingsManager settings, string dataPath)
    {
        if (!settings.EnableNodeMode)
        {
            return false;
        }

        return HasStoredNodeDeviceToken(dataPath);
    }

    internal static bool HasBootstrapGatewayRecord(GatewayRegistry? registry)
    {
        var active = registry?.GetActive();
        return !string.IsNullOrWhiteSpace(active?.BootstrapToken);
    }

    public static bool RequiresSetup(SettingsManager settings, string dataPath) =>
        RequiresSetup(settings, dataPath, registry: null);

    public static bool RequiresSetup(SettingsManager settings, string dataPath, GatewayRegistry? registry)
    {
        if (settings.EnableMcpServer)
        {
            return false;
        }

        if (settings.EnableNodeMode)
        {
            return !HasStoredNodeDeviceToken(dataPath, registry)
                && !HasBootstrapGatewayRecord(registry);
        }

        if (registry is not null
            && SetupExistingGatewayClassifier.HasAnyExistingGatewayConnection(registry, settings, dataPath))
        {
            return false;
        }

        if (HasUsableOperatorConfiguration(settings, dataPath))
        {
            return false;
        }

        return true;
    }
}
