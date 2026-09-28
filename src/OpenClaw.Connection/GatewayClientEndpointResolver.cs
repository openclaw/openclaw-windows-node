namespace OpenClaw.Connection;

/// <summary>
/// Resolves the Windows-side WebSocket endpoint for a gateway record.
/// SSH-backed records must stay on their local forward rather than bypassing the tunnel.
/// </summary>
public static class GatewayClientEndpointResolver
{
    public static string Resolve(GatewayRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.SshTunnel is not { } tunnel)
            return record.Url;

        if (tunnel.LocalPort is < 1 or > 65535)
            throw new InvalidOperationException("SSH tunnel local port must be between 1 and 65535.");

        return $"ws://localhost:{tunnel.LocalPort}";
    }

    /// <summary>
    /// Picks the WebSocket endpoint a dashboard may open.
    /// SSH records open only the local forward, and only while that forward is up.
    /// The shared token must not be attached to the saved, untunneled address.
    /// </summary>
    public static bool TryResolveDashboardEndpoint(
        GatewayRecord record,
        SshTunnelSnapshot? tunnel,
        out string endpoint,
        out bool appendSharedToken,
        bool listenerOwned = false)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.SshTunnel is not { } config)
        {
            endpoint = record.Url;
            appendSharedToken = !string.IsNullOrWhiteSpace(record.SharedGatewayToken);
            return !string.IsNullOrWhiteSpace(endpoint);
        }

        if (!listenerOwned || !IsDashboardTunnelUp(config, tunnel))
        {
            endpoint = "";
            appendSharedToken = false;
            return false;
        }

        endpoint = Resolve(record);
        appendSharedToken = !string.IsNullOrWhiteSpace(record.SharedGatewayToken);
        return true;
    }

    private static bool IsDashboardTunnelUp(SshTunnelConfig config, SshTunnelSnapshot? tunnel) =>
        tunnel is { IsRunning: true, Status: OpenClaw.Shared.TunnelStatus.Up } &&
        tunnel.CurrentLocalPort == config.LocalPort &&
        tunnel.CurrentRemotePort == config.RemotePort &&
        tunnel.CurrentSshPort == config.SshPort &&
        string.Equals(tunnel.CurrentUser, config.User, StringComparison.Ordinal) &&
        string.Equals(tunnel.CurrentHost, config.Host, StringComparison.OrdinalIgnoreCase);
}
