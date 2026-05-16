namespace OpenClaw.Connection;

/// <summary>
/// Classifies what changed between two settings snapshots to determine
/// the minimum reconnect action needed.
/// </summary>
public enum SettingsChangeImpact
{
    /// <summary>No meaningful change.</summary>
    NoOp,
    /// <summary>UI-only change (nav pane, notifications, etc.) — no reconnect.</summary>
    UiOnly,
    /// <summary>Node capability toggled — reload capabilities, no full reconnect.</summary>
    CapabilityReload,
    /// <summary>EnableNodeMode toggled — reconnect node only.</summary>
    NodeReconnectRequired,
    /// <summary>SSH tunnel config changed — full reconnect.</summary>
    OperatorReconnectRequired,
    /// <summary>Gateway URL changed — full tear down and reconnect.</summary>
    FullReconnectRequired
}

/// <summary>
/// Classifies settings changes to determine minimum reconnect action.
/// </summary>
public static class SettingsChangeClassifier
{
    public static SettingsChangeImpact Classify(ConnectionSettingsSnapshot? prev, ConnectionSettingsSnapshot? next)
    {
        if (prev == null || next == null)
            return SettingsChangeImpact.FullReconnectRequired;

        // Gateway URL changed → full reconnect
        if (!string.Equals(prev.GatewayUrl, next.GatewayUrl, StringComparison.OrdinalIgnoreCase))
            return SettingsChangeImpact.FullReconnectRequired;

        // SSH tunnel config changed → operator reconnect
        if (prev.UseSshTunnel != next.UseSshTunnel ||
            prev.SshTunnelUser != next.SshTunnelUser ||
            prev.SshTunnelHost != next.SshTunnelHost ||
            prev.SshTunnelRemotePort != next.SshTunnelRemotePort ||
            prev.SshTunnelLocalPort != next.SshTunnelLocalPort)
            return SettingsChangeImpact.OperatorReconnectRequired;

        // EnableNodeMode toggled → node reconnect
        if (prev.EnableNodeMode != next.EnableNodeMode ||
            prev.EnableMcpServer != next.EnableMcpServer)
            return SettingsChangeImpact.NodeReconnectRequired;

        // Node capability toggles → capability reload
        if (prev.NodeCanvasEnabled != next.NodeCanvasEnabled ||
            prev.NodeScreenEnabled != next.NodeScreenEnabled ||
            prev.NodeCameraEnabled != next.NodeCameraEnabled ||
            prev.NodeLocationEnabled != next.NodeLocationEnabled ||
            prev.NodeBrowserProxyEnabled != next.NodeBrowserProxyEnabled ||
            prev.NodeSttEnabled != next.NodeSttEnabled ||
            prev.NodeTtsEnabled != next.NodeTtsEnabled)
            return SettingsChangeImpact.CapabilityReload;

        // Check if anything else changed (UI-only changes)
        if (prev.FullSettingsJson != next.FullSettingsJson)
            return SettingsChangeImpact.UiOnly;

        return SettingsChangeImpact.NoOp;
    }
}
