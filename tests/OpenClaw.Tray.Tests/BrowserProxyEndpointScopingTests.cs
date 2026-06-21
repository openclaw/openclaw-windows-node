using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Services;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Regression coverage for the two endpoint-scoping findings on PR #793:
///  (1) NodeService must not inherit stale global SSH-tunnel settings after a
///      tunnel-&gt;direct gateway switch (BrowserProxyTunnelState).
///  (2) Command Center port diagnostics must probe the SAME effective browser-control
///      port that browser.proxy dials, including BrowserControlPort overrides
///      (PortDiagnosticsService).
/// </summary>
public sealed class BrowserProxyEndpointScopingTests
{
    // ---- Finding 1: tunnel state scoped to the active gateway ----

    [Fact]
    public void TunnelState_ResolverSupplied_DirectActiveGateway_IgnoresStaleGlobalUseSshTunnel()
    {
        // The bug: switching from a tunnel gateway to a direct gateway while the global
        // SettingsManager.UseSshTunnel is still true used to keep the tunnel "enabled" and
        // dial the stale tunnel-local+2 endpoint. With the active resolver supplied and the
        // active record direct (null tunnel), the tunnel must be OFF.
        var state = BrowserProxyTunnelState.Resolve(
            activeResolverSupplied: true,
            activeTunnel: null,
            settingsUseSshTunnel: true,        // stale global from the previous tunnel gateway
            settingsLocalPort: 9100,
            settingsRemotePort: 18789);

        Assert.False(state.Enabled);
        Assert.Null(state.LocalPort);
        Assert.Null(state.RemotePort);
    }

    [Fact]
    public void TunnelState_ResolverSupplied_ActiveTunnel_UsesActiveRecordPorts()
    {
        var state = BrowserProxyTunnelState.Resolve(
            activeResolverSupplied: true,
            activeTunnel: new SshTunnelConfig("user", "host", RemotePort: 18789, LocalPort: 9100),
            settingsUseSshTunnel: false,       // global says off; active record wins
            settingsLocalPort: null,
            settingsRemotePort: null);

        Assert.True(state.Enabled);
        Assert.Equal(9100, state.LocalPort);
        Assert.Equal(18789, state.RemotePort);
    }

    [Fact]
    public void TunnelState_NoResolver_FallsBackToGlobalSettings()
    {
        // Legacy construction path (no active-gateway resolver wired) honours global settings.
        var on = BrowserProxyTunnelState.Resolve(
            activeResolverSupplied: false,
            activeTunnel: null,
            settingsUseSshTunnel: true,
            settingsLocalPort: 9100,
            settingsRemotePort: 18789);
        Assert.True(on.Enabled);
        Assert.Equal(9100, on.LocalPort);
        Assert.Equal(18789, on.RemotePort);

        var off = BrowserProxyTunnelState.Resolve(
            activeResolverSupplied: false,
            activeTunnel: null,
            settingsUseSshTunnel: false,
            settingsLocalPort: 9100,
            settingsRemotePort: 18789);
        Assert.False(off.Enabled);
        Assert.Null(off.LocalPort);
        Assert.Null(off.RemotePort);
    }

    // ---- Finding 2: diagnostics probe the override port browser.proxy actually dials ----

    private static GatewayTopologyInfo Topology(GatewayKind kind, string url, bool tunnel = false) => new()
    {
        DetectedKind = kind,
        GatewayUrl = url,
        UsesSshTunnel = tunnel,
        IsLoopback = url.Contains("127.0.0.1") || url.Contains("localhost")
    };

    private static int? BrowserProxyProbePort(GatewayTopologyInfo topology, TunnelCommandCenterInfo? tunnel, int? overridePort)
    {
        var diags = PortDiagnosticsService.BuildDiagnostics(topology, tunnel, overridePort);
        var entry = diags.Find(d => d.Purpose.Equals("Browser proxy host", System.StringComparison.OrdinalIgnoreCase));
        return entry?.Port;
    }

    [Fact]
    public void Diagnostics_OverrideSet_ProbesOverridePort_NotGatewayPlusTwo()
    {
        // gateway+2 would be 18791; the override pins the real listener at 19000.
        var port = BrowserProxyProbePort(
            Topology(GatewayKind.WindowsNative, "ws://127.0.0.1:18789"),
            tunnel: null,
            overridePort: 19000);

        Assert.Equal(19000, port);
    }

    [Fact]
    public void Diagnostics_NoOverride_CoLocated_ProbesGatewayPlusTwo()
    {
        var port = BrowserProxyProbePort(
            Topology(GatewayKind.WindowsNative, "ws://127.0.0.1:18789"),
            tunnel: null,
            overridePort: null);

        Assert.Equal(18791, port);
    }

    [Fact]
    public void Diagnostics_NoOverride_Tunnel_ProbesTunnelLocalPlusTwo()
    {
        var tunnel = new TunnelCommandCenterInfo
        {
            Status = TunnelStatus.Up,
            LocalEndpoint = "127.0.0.1:9100"
        };
        var port = BrowserProxyProbePort(
            Topology(GatewayKind.Wsl, "ws://127.0.0.1:9100", tunnel: true),
            tunnel,
            overridePort: null);

        Assert.Equal(9102, port);
    }

    [Fact]
    public void Diagnostics_OverrideSet_RemoteKind_StillProbesOverride()
    {
        // Before the fix a non-local gateway kind produced no browser-proxy probe at all,
        // so an override-only split listener was never reflected in diagnostics.
        var port = BrowserProxyProbePort(
            Topology(GatewayKind.Remote, "wss://gateway.example.com:443"),
            tunnel: null,
            overridePort: 19000);

        Assert.Equal(19000, port);
    }

    // ---- Finding 1 extended: Command Center topology also uses active GatewayRecord, not stale settings ----

    [Fact]
    public void CommandCenterTopology_DirectActiveGateway_IgnoresStaleSettingsTunnel()
    {
        // After switching from a tunnel gateway to a direct one, global SettingsManager may
        // still say UseSshTunnel=true. The topology classifier must NOT inherit it.
        var inputs = CommandCenterTopologyTunnelResolver.Derive(
            hasActiveGatewayRecord: true,
            activeGatewaySshTunnel: null,          // direct gateway — no tunnel
            settingsUseSshTunnel: true,             // stale global setting from old gateway
            settingsHost: "old-host.example.com",
            settingsLocalPort: 9100,
            settingsRemotePort: 18789);

        Assert.False(inputs.UsesSshTunnel);
        Assert.Null(inputs.SshHost);
        Assert.Equal(0, inputs.LocalPort);
        Assert.Equal(0, inputs.RemotePort);
    }

    [Fact]
    public void CommandCenterTopology_TunnelActiveGateway_UsesRecordPorts()
    {
        // When the active GatewayRecord has an SshTunnel, its ports drive topology — not settings.
        var tunnel = new SshTunnelConfig("user", "host.example.com", RemotePort: 18789, LocalPort: 9100);
        var inputs = CommandCenterTopologyTunnelResolver.Derive(
            hasActiveGatewayRecord: true,
            activeGatewaySshTunnel: tunnel,
            settingsUseSshTunnel: false,            // global says off; active record wins
            settingsHost: null,
            settingsLocalPort: 0,
            settingsRemotePort: 0);

        Assert.True(inputs.UsesSshTunnel);
        Assert.Equal("host.example.com", inputs.SshHost);
        Assert.Equal(9100, inputs.LocalPort);
        Assert.Equal(18789, inputs.RemotePort);
    }

    [Fact]
    public void CommandCenterTopology_NoActiveGatewayRecord_FallsBackToSettings()
    {
        // Legacy / pre-registry path: no active record wired, so global settings are the source.
        var inputs = CommandCenterTopologyTunnelResolver.Derive(
            hasActiveGatewayRecord: false,
            activeGatewaySshTunnel: null,
            settingsUseSshTunnel: true,
            settingsHost: "mac.local",
            settingsLocalPort: 9200,
            settingsRemotePort: 18789);

        Assert.True(inputs.UsesSshTunnel);
        Assert.Equal("mac.local", inputs.SshHost);
        Assert.Equal(9200, inputs.LocalPort);
        Assert.Equal(18789, inputs.RemotePort);
    }

    // ---- Finding 1 closed: Command Center tunnel DETAILS (not just topology) follow the active record ----

    [Fact]
    public void CommandCenterTunnel_ActiveGatewayTunnel_StaleGlobalUseSshTunnelOff_ShowsActiveRecordDetails()
    {
        // The residual half of finding 1: tunnel DETAILS used to gate purely on the global
        // SettingsManager.UseSshTunnel. With it stale/off but the active GatewayRecord carrying a
        // tunnel, the Command Center showed NO tunnel — so its diagnostics and the copied SSH
        // guidance disagreed with the endpoint browser.proxy actually dials. Details must follow
        // the active record (resolved active-record-first, host/ports via Derive, user via caller).
        var inputs = CommandCenterTopologyTunnelResolver.Derive(
            hasActiveGatewayRecord: true,
            activeGatewaySshTunnel: new SshTunnelConfig("camuser", "mac.local", RemotePort: 18789, LocalPort: 9100),
            settingsUseSshTunnel: false,            // stale global says off — must be ignored
            settingsHost: "stale-global.example",
            settingsLocalPort: 9999,
            settingsRemotePort: 18000);

        var tunnel = CommandCenterTunnelInfoBuilder.Build(inputs, baseUser: "camuser", snapshot: null);

        Assert.NotNull(tunnel);
        Assert.Equal("mac.local", tunnel!.Host);
        Assert.Equal("camuser", tunnel.User);
        Assert.Equal("127.0.0.1:9100", tunnel.LocalEndpoint);
        Assert.Equal("mac.local:127.0.0.1:18789", tunnel.RemoteEndpoint);
    }

    [Fact]
    public void CommandCenterTunnel_DirectActiveGateway_StaleGlobalUseSshTunnelOn_ShowsNoTunnel()
    {
        // Inverse: after switching to a direct gateway, a stale global UseSshTunnel=true must NOT
        // surface tunnel details the active gateway no longer uses.
        var inputs = CommandCenterTopologyTunnelResolver.Derive(
            hasActiveGatewayRecord: true,
            activeGatewaySshTunnel: null,           // direct gateway — no tunnel
            settingsUseSshTunnel: true,             // stale global says on — must be ignored
            settingsHost: "old-host.example",
            settingsLocalPort: 9100,
            settingsRemotePort: 18789);

        Assert.Null(CommandCenterTunnelInfoBuilder.Build(inputs, baseUser: null, snapshot: null));
    }

    [Fact]
    public void CommandCenterTunnel_LiveSnapshot_OverridesResolvedBase()
    {
        // The live SshTunnelSnapshot (actual running tunnel) still wins over the resolved base,
        // including the browser-proxy forward endpoints — unchanged by the active-record refactor.
        var inputs = CommandCenterTopologyTunnelResolver.Derive(
            hasActiveGatewayRecord: true,
            activeGatewaySshTunnel: new SshTunnelConfig("camuser", "mac.local", RemotePort: 18789, LocalPort: 9100),
            settingsUseSshTunnel: false,
            settingsHost: null,
            settingsLocalPort: 0,
            settingsRemotePort: 0);
        var live = new SshTunnelSnapshot(
            IsRunning: true,
            CurrentUser: "liveuser",
            CurrentHost: "live.host",
            CurrentRemotePort: 28789,
            CurrentLocalPort: 19100,
            CurrentBrowserProxyRemotePort: 18791,
            CurrentBrowserProxyLocalPort: 18792,
            StartedAtUtc: null,
            LastError: null,
            Status: TunnelStatus.Up);

        var tunnel = CommandCenterTunnelInfoBuilder.Build(inputs, baseUser: "camuser", snapshot: live);

        Assert.NotNull(tunnel);
        Assert.Equal(TunnelStatus.Up, tunnel!.Status);
        Assert.Equal("live.host", tunnel.Host);
        Assert.Equal("liveuser", tunnel.User);
        Assert.Equal("127.0.0.1:19100", tunnel.LocalEndpoint);
        Assert.Equal("live.host:127.0.0.1:28789", tunnel.RemoteEndpoint);
        Assert.Equal("127.0.0.1:18792", tunnel.BrowserProxyLocalEndpoint);
        Assert.Equal("live.host:127.0.0.1:18791", tunnel.BrowserProxyRemoteEndpoint);
    }
}
