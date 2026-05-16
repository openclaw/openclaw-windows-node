using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

public static class SettingsDataExtensions
{
    public static ConnectionSettingsSnapshot ToConnectionSnapshot(this SettingsData settings) => new(
        settings.GatewayUrl,
        settings.UseSshTunnel,
        settings.SshTunnelUser,
        settings.SshTunnelHost,
        settings.SshTunnelRemotePort,
        settings.SshTunnelLocalPort,
        settings.EnableNodeMode,
        settings.EnableMcpServer,
        settings.NodeCanvasEnabled,
        settings.NodeScreenEnabled,
        settings.NodeCameraEnabled,
        settings.NodeLocationEnabled,
        settings.NodeBrowserProxyEnabled,
        settings.NodeSttEnabled,
        settings.NodeTtsEnabled,
        settings.ToJson());
}
