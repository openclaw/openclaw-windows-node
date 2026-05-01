using System;

namespace OpenClawTray.Onboarding.Services;

/// <summary>
/// Classifies gateway URLs that point at the local machine.
/// </summary>
public static class LocalGatewayApprover
{
    /// <summary>
    /// Checks if the gateway URL points to localhost.
    /// </summary>
    public static bool IsLocalGateway(string gatewayUrl)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl)) return false;
        try
        {
            var uri = new Uri(gatewayUrl);
            var host = uri.Host.ToLowerInvariant();
            return host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
        }
        catch
        {
            return false;
        }
    }
}
