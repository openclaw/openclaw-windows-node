namespace OpenClaw.Connection;

/// <summary>
/// Gateway and SSH forward captured together for one dashboard launch.
/// </summary>
public sealed record DashboardGatewayTunnelSnapshot(
    string GatewayIdentity,
    string GatewayUrl,
    string Token,
    bool AppendToken,
    SshTunnelConfig? Tunnel,
    bool IsBootstrapToken = false,
    string Source = "");

/// <summary>
/// Binds a dashboard URL to the gateway and SSH destination checked before launch.
/// </summary>
public static class DashboardBrowserHandoff
{
    public static bool SameBinding(
        DashboardGatewayTunnelSnapshot before,
        DashboardGatewayTunnelSnapshot after)
    {
        if (!string.Equals(before.GatewayIdentity, after.GatewayIdentity, StringComparison.OrdinalIgnoreCase))
            return false;

        return SameTunnelDestination(before.Tunnel, after.Tunnel);
    }

    public static bool SameTunnelDestination(SshTunnelConfig? left, SshTunnelConfig? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        return string.Equals(left.User.Trim(), right.User.Trim(), StringComparison.Ordinal) &&
            string.Equals(left.Host.Trim(), right.Host.Trim(), StringComparison.OrdinalIgnoreCase) &&
            left.RemotePort == right.RemotePort &&
            left.LocalPort == right.LocalPort &&
            left.SshPort == right.SshPort &&
            left.IncludeBrowserProxyForward == right.IncludeBrowserProxyForward;
    }

    public static string ProjectOntoLocalForward(string savedGatewayUrl, int localPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(savedGatewayUrl);
        var normalized = savedGatewayUrl
            .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)
            .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase);
        var endpoint = new Uri(normalized, UriKind.Absolute);
        var builder = new UriBuilder(endpoint)
        {
            Scheme = endpoint.Scheme is "https" ? "https" : "http",
            Host = "127.0.0.1",
            Port = localPort,
        };
        return builder.Uri.AbsoluteUri;
    }

    public static bool UrlUsesCapturedForward(string url, DashboardGatewayTunnelSnapshot captured)
    {
        if (captured.Tunnel is not SshTunnelConfig tunnel)
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var loopback = uri.Host is "127.0.0.1" or "localhost" or "::1";
        if (!loopback || uri.Port != tunnel.LocalPort)
            return false;
        if (!captured.AppendToken)
            return true;
        if (string.IsNullOrEmpty(captured.Token))
            return false;

        var marker = "token=" + Uri.EscapeDataString(captured.Token);
        return uri.Fragment.Contains(marker, StringComparison.Ordinal);
    }
}
