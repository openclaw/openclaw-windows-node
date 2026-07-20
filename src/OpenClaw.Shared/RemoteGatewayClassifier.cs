using System;

namespace OpenClaw.Shared;

/// <summary>
/// How the tray reaches a configured gateway. Drives remote-setup guidance and
/// the cleartext-token warning in Connection settings.
/// </summary>
public enum GatewayConnectionTopology
{
    /// <summary>URL was empty or could not be parsed.</summary>
    Unknown,

    /// <summary>localhost / 127.0.0.1 / ::1 - reached directly on this machine.</summary>
    Local,

    /// <summary>
    /// A loopback URL that actually fronts a remote gateway through a managed
    /// SSH tunnel (the WebSocket talks to <c>ws://localhost:&lt;localPort&gt;</c>
    /// but the bytes are encrypted by SSH end-to-end).
    /// </summary>
    SshTunnel,

    /// <summary>Remote host over TLS (<c>wss://</c> or <c>https://</c>).</summary>
    DirectSecure,

    /// <summary>
    /// Remote host over cleartext (<c>ws://</c> or <c>http://</c>) - the token
    /// travels unencrypted across the network.
    /// </summary>
    DirectInsecure,
}

/// <summary>Whether the credential is protected in transit.</summary>
public enum GatewayTransportSecurity
{
    /// <summary>Loopback only - no network exposure.</summary>
    LocalLoopback,

    /// <summary>Encrypted (TLS) or tunnelled (SSH) - token protected on the wire.</summary>
    Encrypted,

    /// <summary>Cleartext to a non-local host - token exposed on the wire.</summary>
    Cleartext,
}

/// <summary>Immutable classification of a gateway endpoint for setup/repair UX.</summary>
public sealed record RemoteGatewayProfile(
    GatewayConnectionTopology Topology,
    GatewayTransportSecurity Security,
    string Host,
    bool IsTls)
{
    public bool IsLocal => Topology == GatewayConnectionTopology.Local;

    public bool IsRemote =>
        Topology is GatewayConnectionTopology.DirectSecure
                 or GatewayConnectionTopology.DirectInsecure
                 or GatewayConnectionTopology.SshTunnel;

    /// <summary>
    /// True when a token would travel in cleartext over a network. The UI should
    /// steer the user to TLS (<c>wss://</c>), an SSH tunnel, or a trusted proxy
    /// (e.g. Tailscale) before saving such a gateway.
    /// </summary>
    public bool RecommendsTransportHardening =>
        Security == GatewayTransportSecurity.Cleartext;
}

/// <summary>
/// Pure classifier that maps a gateway URL (plus whether a managed SSH tunnel is
/// configured) to a <see cref="RemoteGatewayProfile"/>. No I/O, no UI types.
/// </summary>
public static class RemoteGatewayClassifier
{
    public static RemoteGatewayProfile Classify(string? url, bool hasSshTunnel = false)
    {
        var trimmed = url?.Trim();
        if (string.IsNullOrEmpty(trimmed) ||
            !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            string.IsNullOrEmpty(uri.Host))
        {
            // Unparseable input is left to GatewayUrlHelper validation elsewhere;
            // we surface no transport warning so half-typed URLs don't flicker a
            // scary banner on every keystroke.
            return new RemoteGatewayProfile(
                GatewayConnectionTopology.Unknown,
                GatewayTransportSecurity.LocalLoopback,
                Host: string.Empty,
                IsTls: false);
        }

        var host = uri.Host;
        var scheme = uri.Scheme;
        var isTls = scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) ||
                    scheme.Equals("https", StringComparison.OrdinalIgnoreCase);

        // A managed SSH tunnel encrypts the hop even though the WebSocket URL is
        // loopback. Treat it as a remote-but-encrypted endpoint.
        if (hasSshTunnel)
        {
            return new RemoteGatewayProfile(
                GatewayConnectionTopology.SshTunnel,
                GatewayTransportSecurity.Encrypted,
                host,
                isTls);
        }

        if (LocalGatewayUrlClassifier.IsLocalGatewayUrl(trimmed))
        {
            return new RemoteGatewayProfile(
                GatewayConnectionTopology.Local,
                GatewayTransportSecurity.LocalLoopback,
                host,
                isTls);
        }

        return isTls
            ? new RemoteGatewayProfile(
                GatewayConnectionTopology.DirectSecure,
                GatewayTransportSecurity.Encrypted,
                host,
                IsTls: true)
            : new RemoteGatewayProfile(
                GatewayConnectionTopology.DirectInsecure,
                GatewayTransportSecurity.Cleartext,
                host,
                IsTls: false);
    }
}
