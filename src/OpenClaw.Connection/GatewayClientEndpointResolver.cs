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
}
