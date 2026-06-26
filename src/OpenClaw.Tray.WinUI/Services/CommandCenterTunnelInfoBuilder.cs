using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// Builds the Command Center tunnel diagnostics (endpoints, status, copied-SSH host/user) from the
/// active-GatewayRecord-first <see cref="CommandCenterTopologyTunnelResolver.TunnelInputs"/>, layering
/// live <see cref="SshTunnelSnapshot"/> values on top. A stale global SettingsManager.UseSshTunnel must
/// not hide an active gateway's tunnel — nor surface one a now-direct gateway no longer uses — so these
/// details match the endpoint browser.proxy actually dials. Kept free of <c>AppStateSnapshot</c>/WinUI
/// so it is unit-testable, the same way <see cref="BrowserProxyTunnelState"/> and
/// <see cref="CommandCenterTopologyTunnelResolver"/> are.
/// </summary>
internal static class CommandCenterTunnelInfoBuilder
{
    internal static TunnelCommandCenterInfo? Build(
        CommandCenterTopologyTunnelResolver.TunnelInputs tunnelInputs,
        string? baseUser,
        SshTunnelSnapshot? snapshot)
    {
        if (!tunnelInputs.UsesSshTunnel)
        {
            return null;
        }

        var localPort = snapshot is { CurrentLocalPort: > 0 }
            ? snapshot.CurrentLocalPort
            : tunnelInputs.LocalPort;
        var remotePort = snapshot is { CurrentRemotePort: > 0 }
            ? snapshot.CurrentRemotePort
            : tunnelInputs.RemotePort;
        var host = string.IsNullOrWhiteSpace(snapshot?.CurrentHost)
            ? tunnelInputs.SshHost
            : snapshot!.CurrentHost!;
        var user = string.IsNullOrWhiteSpace(snapshot?.CurrentUser)
            ? baseUser
            : snapshot!.CurrentUser!;
        var status = snapshot?.Status is TunnelStatus.Up or TunnelStatus.Starting or TunnelStatus.Restarting or TunnelStatus.Failed
            ? snapshot.Status
            : string.IsNullOrWhiteSpace(snapshot?.LastError)
                ? TunnelStatus.Stopped
                : TunnelStatus.Failed;

        return new TunnelCommandCenterInfo
        {
            Status = status,
            LocalEndpoint = $"127.0.0.1:{localPort}",
            RemoteEndpoint = string.IsNullOrWhiteSpace(host)
                ? $"127.0.0.1:{remotePort}"
                : $"{host}:127.0.0.1:{remotePort}",
            BrowserProxyLocalEndpoint = snapshot?.CurrentBrowserProxyLocalPort > 0
                ? $"127.0.0.1:{snapshot.CurrentBrowserProxyLocalPort}"
                : "",
            BrowserProxyRemoteEndpoint = snapshot?.CurrentBrowserProxyRemotePort > 0
                ? string.IsNullOrWhiteSpace(host)
                    ? $"127.0.0.1:{snapshot.CurrentBrowserProxyRemotePort}"
                    : $"{host}:127.0.0.1:{snapshot.CurrentBrowserProxyRemotePort}"
                : "",
            Host = host,
            User = user,
            LastError = snapshot?.LastError,
            StartedAt = snapshot?.StartedAtUtc
        };
    }
}
