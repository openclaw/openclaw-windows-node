using OpenClaw.Connection;

namespace OpenClawTray.Services;

/// <summary>
/// Derives the tunnel inputs that <c>GatewayTopologyClassifier.Classify</c> needs, using the
/// same active-GatewayRecord-first priority as <see cref="BrowserProxyTunnelState"/>.
/// When a GatewayRecord is active it is authoritative: a null <c>SshTunnel</c> means "this
/// gateway is direct" and must not be overridden by stale global SettingsManager values.
/// </summary>
internal static class CommandCenterTopologyTunnelResolver
{
    internal readonly record struct TunnelInputs(
        bool UsesSshTunnel,
        string? SshHost,
        int LocalPort,
        int RemotePort);

    internal static TunnelInputs Derive(
        bool hasActiveGatewayRecord,
        SshTunnelConfig? activeGatewaySshTunnel,
        bool settingsUseSshTunnel,
        string? settingsHost,
        int settingsLocalPort,
        int settingsRemotePort)
    {
        if (hasActiveGatewayRecord)
            return new TunnelInputs(
                activeGatewaySshTunnel != null,
                activeGatewaySshTunnel?.Host,
                activeGatewaySshTunnel?.LocalPort ?? 0,
                activeGatewaySshTunnel?.RemotePort ?? 0);

        return new TunnelInputs(
            settingsUseSshTunnel,
            settingsUseSshTunnel ? settingsHost : null,
            settingsUseSshTunnel ? settingsLocalPort : 0,
            settingsUseSshTunnel ? settingsRemotePort : 0);
    }
}
