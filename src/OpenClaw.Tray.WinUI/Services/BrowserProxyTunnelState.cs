using OpenClaw.Connection;
using OpenClaw.Shared;

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
    internal readonly record struct Resolved(
        bool Enabled,
        int? LocalPort,
        int? RemotePort,
        bool AllowGatewayPortFallback);

    internal static Resolved Resolve(
        bool activeResolverSupplied,
        SshTunnelConfig? activeTunnel,
        string? activeGatewayUrl,
        bool settingsUseSshTunnel,
        int? settingsLocalPort,
        int? settingsRemotePort,
        string? settingsGatewayUrl)
    {
        if (activeResolverSupplied)
        {
            // Active record wins. Null tunnel == direct gateway, NOT "inherit global".
            if (activeTunnel is null)
            {
                return new Resolved(
                    false,
                    null,
                    null,
                    AllowGatewayPortFallback: BrowserControlEndpoint.AllowsGatewayPortFallback(activeGatewayUrl));
            }

            if (!activeTunnel.IncludeBrowserProxyForward)
                return new Resolved(false, null, null, AllowGatewayPortFallback: false);

            return new Resolved(
                Enabled: true,
                LocalPort: activeTunnel.LocalPort,
                RemotePort: activeTunnel.RemotePort,
                AllowGatewayPortFallback: false);
        }

        // Legacy path: no active-gateway resolver, honour global settings.
        return new Resolved(
            settingsUseSshTunnel,
            settingsUseSshTunnel ? settingsLocalPort : null,
            settingsUseSshTunnel ? settingsRemotePort : null,
            AllowGatewayPortFallback: !settingsUseSshTunnel &&
                BrowserControlEndpoint.AllowsGatewayPortFallback(settingsGatewayUrl));
    }
}

internal static class BrowserProxySshTunnelForwardPolicy
{
    internal static bool ShouldInclude(bool nodeBrowserProxyEnabled, int remotePort, int localPort) =>
        nodeBrowserProxyEnabled && SshTunnelCommandLine.CanForwardBrowserProxyPort(remotePort, localPort);

    internal static bool ShouldInclude(SettingsManager? settings, SshTunnelConfig tunnel) =>
        settings?.NodeBrowserProxyEnabled == true && ShouldInclude(true, tunnel.RemotePort, tunnel.LocalPort);

    internal static SshTunnelConfig Apply(SettingsManager? settings, SshTunnelConfig tunnel)
    {
        var includeBrowserProxyForward = ShouldInclude(settings, tunnel);
        return tunnel.IncludeBrowserProxyForward == includeBrowserProxyForward
            ? tunnel
            : tunnel with { IncludeBrowserProxyForward = includeBrowserProxyForward };
    }
}
