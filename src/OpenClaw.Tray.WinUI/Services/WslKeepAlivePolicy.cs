using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal static class WslKeepAlivePolicy
{
    public static bool ShouldStart(GatewayRecord? activeRecord, string? legacyGatewayUrl)
    {
        if (activeRecord is not null)
        {
            if (activeRecord.SshTunnel is not null)
                return false;

            return activeRecord.IsLocal
                || !string.IsNullOrWhiteSpace(activeRecord.SetupManagedDistroName)
                || LocalGatewayUrlClassifier.IsLocalGatewayUrl(activeRecord.Url);
        }

        return LocalGatewayUrlClassifier.IsLocalGatewayUrl(legacyGatewayUrl ?? string.Empty);
    }

    public static string? ResolveDistroName(
        GatewayRecord? activeRecord,
        string? setupStateDistroName,
        string? environmentOverride)
    {
        if (!string.IsNullOrWhiteSpace(activeRecord?.SetupManagedDistroName))
            return activeRecord.SetupManagedDistroName;

        if (!string.IsNullOrWhiteSpace(setupStateDistroName))
            return setupStateDistroName;

#if DEBUG || OPENCLAW_TRAY_TESTS
        if (!string.IsNullOrWhiteSpace(environmentOverride))
            return environmentOverride;
#endif

        return "OpenClawGateway";
    }
}
