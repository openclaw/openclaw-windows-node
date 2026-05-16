namespace OpenClaw.Connection;

/// <summary>
/// Immutable snapshot of connection-relevant settings for change classification.
/// Extracted from the full SettingsData to decouple the connection layer from UI settings.
/// </summary>
public sealed record ConnectionSettingsSnapshot(
    string? GatewayUrl,
    bool UseSshTunnel,
    string? SshTunnelUser,
    string? SshTunnelHost,
    int SshTunnelRemotePort,
    int SshTunnelLocalPort,
    bool EnableNodeMode,
    bool EnableMcpServer,
    bool NodeCanvasEnabled,
    bool NodeScreenEnabled,
    bool NodeCameraEnabled,
    bool NodeLocationEnabled,
    bool NodeBrowserProxyEnabled,
    bool NodeSttEnabled,
    bool NodeTtsEnabled,
    string? FullSettingsJson);
