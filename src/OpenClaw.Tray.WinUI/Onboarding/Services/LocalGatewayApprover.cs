using OpenClaw.Shared;

namespace OpenClawTray.Onboarding.Services;

/// <summary>
/// Classifies gateway URLs that point at the local machine.
/// </summary>
public static class LocalGatewayApprover
{
    /// <summary>
    /// Checks if the gateway URL points to localhost.
    /// </summary>
    public static bool IsLocalGateway(string gatewayUrl) => LocalGatewayUrlClassifier.IsLocalGatewayUrl(gatewayUrl);
}
