namespace OpenClaw.Connection;

/// <summary>
/// Immutable record representing a known gateway endpoint.
/// Stored in <c>gateways.json</c> via <see cref="GatewayRegistry"/>.
/// </summary>
public sealed record GatewayRecord
{
    /// <summary>Stable GUID, primary key.</summary>
    public string Id { get; init; } = "";

    /// <summary>Gateway WebSocket URL (e.g. wss://gateway.example.com).</summary>
    public string Url { get; init; } = "";

    /// <summary>User-facing label (e.g. "Home Gateway").</summary>
    public string? FriendlyName { get; init; }

    /// <summary>Long-lived shared token for any device.</summary>
    public string? SharedGatewayToken { get; init; }

    /// <summary>One-time bootstrap token for first-time pairing.</summary>
    public string? BootstrapToken { get; init; }

    /// <summary>Last successful connection time.</summary>
    public DateTime? LastConnected { get; init; }

    /// <summary>True for gateways provisioned locally (localhost/WSL).</summary>
    public bool IsLocal { get; init; }

    /// <summary>Per-gateway SSH tunnel configuration. Null if no tunnel needed.</summary>
    public SshTunnelConfig? SshTunnel { get; init; }

    /// <summary>
    /// Identity directory name, deterministically derived from Id.
    /// GUIDs are path-safe and guarantee uniqueness even if URLs change.
    /// </summary>
    public string IdentityDirName => Id;
}

/// <summary>Per-gateway SSH tunnel configuration.</summary>
public sealed record SshTunnelConfig(
    string User,
    string Host,
    int RemotePort,
    int LocalPort,
    bool IncludeBrowserProxyForward = false);
