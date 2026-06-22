namespace OpenClaw.Shared;

/// <summary>
/// Single source of truth for the effective local browser-control endpoint that the
/// node-side <c>browser.proxy</c> capability connects to.
///
/// Both <see cref="OpenClaw.Shared.Capabilities.BrowserProxyCapability"/> and the
/// Command Center diagnostics resolve the port through here, so the port the proxy
/// actually dials, the endpoint shown in diagnostics, and the copied SSH-forward
/// guidance can never diverge.
///
/// Contract — scoped to the active gateway/tunnel, highest priority first:
/// <list type="number">
///   <item>Explicit <c>BrowserControlPort</c> override (validated): the active
///   connection's browser-control local port for split / manual-forward topologies.</item>
///   <item>Managed SSH tunnel active: tunnel local gateway port + 2 — the companion
///   browser-proxy forward the tunnel sets up
///   (<c>SshTunnelCommandLine</c> forwards <c>localPort + 2 -&gt; remotePort + 2</c>).</item>
///   <item>Co-located gateway: gateway local port + 2.</item>
/// </list>
/// </summary>
public static class BrowserControlEndpoint
{
    public static bool AllowsGatewayPortFallback(string? gatewayUrl)
    {
        var topology = GatewayTopologyClassifier.Classify(gatewayUrl, useSshTunnel: false);
        return topology.DetectedKind is GatewayKind.WindowsNative or GatewayKind.Wsl;
    }

    /// <summary>Resolves the effective local browser-control TCP port for the active connection.</summary>
    /// <param name="gatewayLocalPort">Local port parsed from the effective gateway URL (the local
    /// forwarded port when an SSH tunnel is active), or null when no usable gateway URL is known.</param>
    /// <param name="useSshTunnel">Whether the managed SSH tunnel is the active transport.</param>
    /// <param name="sshTunnelLocalPort">The tunnel's local gateway forward port (settings value).</param>
    /// <param name="controlPortOverride"><c>SettingsData.BrowserControlPort</c>, or null.</param>
    /// <param name="allowGatewayPortFallback">Whether gateway local port + 2 is safe to infer.
    /// Set false when the effective gateway URL is an SSH tunnel that does not declare a
    /// browser-control companion forward.</param>
    public static bool TryResolveControlPort(
        int? gatewayLocalPort,
        bool useSshTunnel,
        int? sshTunnelLocalPort,
        int? controlPortOverride,
        out int controlPort,
        out string error,
        bool allowGatewayPortFallback = true)
    {
        controlPort = 0;
        error = "";

        // (1) Explicit override pins the control port for the active connection.
        if (controlPortOverride is { } overridePort)
        {
            if (overridePort is < 1 or > 65535)
            {
                error = "Configured browser-control port is outside the valid TCP port range.";
                return false;
            }
            controlPort = overridePort;
            return true;
        }

        // (2) Managed SSH tunnel: the companion browser-proxy forward lands on local+2.
        if (useSshTunnel && sshTunnelLocalPort is { } tunnelLocal)
        {
            if (tunnelLocal is < 1 or > 65533)
            {
                error = "SSH tunnel local port leaves no room for the browser-control port (local + 2).";
                return false;
            }
            controlPort = tunnelLocal + 2;
            return true;
        }

        if (!allowGatewayPortFallback)
        {
            error = "Browser proxy requires an explicit browser-control port or a managed SSH browser-proxy forward.";
            return false;
        }

        // (3) Co-located gateway: control host on gateway port + 2.
        if (gatewayLocalPort is { } gatewayPort)
        {
            if (gatewayPort <= 0)
            {
                error = "Browser proxy requires a gateway URL with an explicit local port.";
                return false;
            }
            if (gatewayPort > 65533)
            {
                error = "Browser proxy control port is outside the valid TCP port range.";
                return false;
            }
            controlPort = gatewayPort + 2;
            return true;
        }

        error = "Browser proxy requires a gateway URL with an explicit local port.";
        return false;
    }
}
