using OpenClaw.Connection;

namespace OpenClawTray.Services;

/// <summary>
/// Resolves the effective browser-proxy SSH-tunnel state for the node-side
/// <c>browser.proxy</c> capability.
///
/// When an active-gateway tunnel resolver is wired (the normal app path), the active
/// <see cref="GatewayRecord"/> is authoritative: a null tunnel means the active gateway
/// is <i>direct</i>, so stale global <c>SettingsManager</c> tunnel values must NOT leak in
/// after a tunnel-&gt;direct gateway switch (which would otherwise dial the old
/// tunnel-local + 2 endpoint and send the active shared token there).
///
/// Falls back to global settings only on the legacy construction path where no
/// active-gateway resolver was supplied at all.
/// </summary>
internal static class BrowserProxyTunnelState
{
    internal readonly record struct Resolved(bool Enabled, int? LocalPort, int? RemotePort);

    internal static Resolved Resolve(
        bool activeResolverSupplied,
        SshTunnelConfig? activeTunnel,
        bool settingsUseSshTunnel,
        int? settingsLocalPort,
        int? settingsRemotePort)
    {
        if (activeResolverSupplied)
        {
            // Active record wins. Null tunnel == direct gateway, NOT "inherit global".
            return new Resolved(activeTunnel != null, activeTunnel?.LocalPort, activeTunnel?.RemotePort);
        }

        // Legacy path: no active-gateway resolver, honour global settings.
        return new Resolved(
            settingsUseSshTunnel,
            settingsUseSshTunnel ? settingsLocalPort : null,
            settingsUseSshTunnel ? settingsRemotePort : null);
    }
}
