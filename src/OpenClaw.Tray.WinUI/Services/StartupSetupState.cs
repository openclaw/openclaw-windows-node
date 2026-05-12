using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal static class StartupSetupState
{
    public static bool HasStoredNodeDeviceToken(string dataPath) =>
        DeviceIdentity.HasStoredDeviceTokenForRole(dataPath, "node", NullLogger.Instance);

    public static bool CanStartNodeGateway(SettingsManager settings, string dataPath)
    {
        if (!settings.EnableNodeMode)
        {
            return false;
        }

        return HasStoredNodeDeviceToken(dataPath);
    }

    public static bool RequiresSetup(SettingsManager settings, string dataPath)
    {
        if (settings.EnableNodeMode && HasStoredNodeDeviceToken(dataPath))
        {
            return false;
        }

        return !settings.EnableMcpServer;
    }
}
