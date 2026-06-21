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

    /// <summary>True when this gateway is known to require v2 auth signatures.</summary>
    public bool RequiresV2Signature { get; init; }

    /// <summary>WSL distro name for gateway records provisioned by SetupEngine.</summary>
    public string? SetupManagedDistroName { get; init; }

    /// <summary>Per-gateway SSH tunnel configuration. Null if no tunnel needed.</summary>
    public SshTunnelConfig? SshTunnel { get; init; }

    /// <summary>
    /// Per-gateway override for the local browser-control host port that the node-side
    /// <c>browser.proxy</c> capability connects to. Null (default) derives the port from the
    /// active gateway/tunnel (see <c>BrowserControlEndpoint</c>). Scoped to this gateway so a
    /// split/remote forward set up for one gateway cannot misroute when another is active.
    /// </summary>
    public int? BrowserControlPort { get; init; }

    /// <summary>
    /// Identity directory name, deterministically derived from Id.
    /// GUIDs are path-safe and guarantee uniqueness even if URLs change.
    /// </summary>
    public string IdentityDirName => Id;
}

/// <summary>
/// Helpers for the saved-gateway edit/connect flows, which rebuild a fresh
/// <see cref="GatewayRecord"/> from the form fields rather than mutating the stored one.
/// </summary>
public static class GatewayRecordEditing
{
    /// <summary>
    /// Carries forward advanced per-gateway fields that the edit/connect forms don't expose,
    /// so editing name / token / URL / SSH settings can't silently drop them. A value already
    /// set on the rebuilt record wins (the form changed it); otherwise the existing record's
    /// value is preserved. Currently scoped to <see cref="GatewayRecord.BrowserControlPort"/>.
    /// </summary>
    public static GatewayRecord PreserveAdvancedFields(this GatewayRecord rebuilt, GatewayRecord? existing)
        => existing is null
            ? rebuilt
            : rebuilt with { BrowserControlPort = rebuilt.BrowserControlPort ?? existing.BrowserControlPort };
}

/// <summary>Per-gateway SSH tunnel configuration.</summary>
public sealed record SshTunnelConfig(
    string User,
    string Host,
    int RemotePort,
    int LocalPort,
    bool IncludeBrowserProxyForward = false,
    int SshPort = 22);
