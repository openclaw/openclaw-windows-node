using System;

namespace OpenClaw.Shared;

/// <summary>
/// Shared literal-host classifier for gateway URLs that point at the local machine.
/// </summary>
public static class LocalGatewayUrlClassifier
{
    public static bool IsLocalGatewayUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        try
        {
            var uri = new Uri(url);
            var host = uri.Host.ToLowerInvariant();
            return host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
        }
        catch
        {
            return false;
        }
    }
}
