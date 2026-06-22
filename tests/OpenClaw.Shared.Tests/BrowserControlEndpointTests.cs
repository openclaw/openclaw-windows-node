using Xunit;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Tests for the single browser-control endpoint contract shared by BrowserProxyCapability
/// and the Command Center diagnostics. Covers the chosen endpoint contract: override pins,
/// else SSH tunnel local + 2, else gateway port + 2 — and that the resolution is scoped to
/// the active gateway/tunnel (gateway-switch / tunnel-toggle behaviour).
/// </summary>
public class BrowserControlEndpointTests
{
    [Fact]
    public void CoLocated_NoOverride_NoTunnel_UsesGatewayPortPlusTwo()
    {
        Assert.True(BrowserControlEndpoint.TryResolveControlPort(18789, useSshTunnel: false, sshTunnelLocalPort: null, controlPortOverride: null, out var port, out _));
        Assert.Equal(18791, port);
    }

    [Fact]
    public void Tunnel_NoOverride_UsesTunnelLocalPortPlusTwo()
    {
        Assert.True(BrowserControlEndpoint.TryResolveControlPort(18789, useSshTunnel: true, sshTunnelLocalPort: 9100, controlPortOverride: null, out var port, out _));
        Assert.Equal(9102, port);
    }

    [Theory]
    [InlineData(18789, false, null)]  // co-located
    [InlineData(18789, true, 9100)]   // managed tunnel
    [InlineData(9000, true, 7000)]    // different active gateway + tunnel
    public void Override_PinsPort_RegardlessOfActiveTopology(int gatewayPort, bool useTunnel, int? tunnelLocal)
    {
        Assert.True(BrowserControlEndpoint.TryResolveControlPort(gatewayPort, useTunnel, tunnelLocal, controlPortOverride: 19000, out var port, out _));
        Assert.Equal(19000, port);
    }

    [Fact]
    public void GatewaySwitch_NoOverride_FollowsActiveGatewayPort()
    {
        // Switching the active gateway re-resolves the control port from that gateway —
        // the contract is scoped to the active connection, not a sticky global.
        Assert.True(BrowserControlEndpoint.TryResolveControlPort(18789, false, null, null, out var first, out _));
        Assert.True(BrowserControlEndpoint.TryResolveControlPort(9000, false, null, null, out var second, out _));
        Assert.Equal(18791, first);
        Assert.Equal(9002, second);
    }

    [Fact]
    public void TunnelToggle_SameGatewayPort_ResolvesPerActiveModel()
    {
        // Co-located: gateway + 2. Tunnel active: the tunnel's companion forward (local + 2),
        // not gateway + 2 — proving the endpoint is scoped to the active tunnel model.
        Assert.True(BrowserControlEndpoint.TryResolveControlPort(18789, useSshTunnel: false, sshTunnelLocalPort: 9100, controlPortOverride: null, out var coLocated, out _));
        Assert.Equal(18791, coLocated);
        Assert.True(BrowserControlEndpoint.TryResolveControlPort(18789, useSshTunnel: true, sshTunnelLocalPort: 9100, controlPortOverride: null, out var tunneled, out _));
        Assert.Equal(9102, tunneled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(65536)]
    [InlineData(70000)]
    public void Override_OutOfRange_Fails(int badOverride)
    {
        Assert.False(BrowserControlEndpoint.TryResolveControlPort(18789, false, null, badOverride, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Tunnel_LocalPortLeavesNoRoomForBrowserControl_Fails()
    {
        Assert.False(BrowserControlEndpoint.TryResolveControlPort(18789, useSshTunnel: true, sshTunnelLocalPort: 65534, controlPortOverride: null, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void GatewayPortLeavesNoRoom_Fails()
    {
        Assert.False(BrowserControlEndpoint.TryResolveControlPort(65534, false, null, null, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void NoGateway_NoTunnel_NoOverride_Fails()
    {
        Assert.False(BrowserControlEndpoint.TryResolveControlPort(null, false, null, null, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Tunnel_EnabledButNoLocalPort_FallsBackToGatewayPlusTwo()
    {
        Assert.True(BrowserControlEndpoint.TryResolveControlPort(18789, useSshTunnel: true, sshTunnelLocalPort: null, controlPortOverride: null, out var port, out _));
        Assert.Equal(18791, port);
    }

    [Fact]
    public void GatewayFallback_Disallowed_NoOverride_Fails()
    {
        Assert.False(BrowserControlEndpoint.TryResolveControlPort(
            gatewayLocalPort: 18789,
            useSshTunnel: false,
            sshTunnelLocalPort: null,
            controlPortOverride: null,
            out _,
            out var error,
            allowGatewayPortFallback: false));
        Assert.Contains("explicit browser-control port", error);
    }

    [Fact]
    public void GatewayFallback_Disallowed_OverrideStillPinsPort()
    {
        Assert.True(BrowserControlEndpoint.TryResolveControlPort(
            gatewayLocalPort: 18789,
            useSshTunnel: false,
            sshTunnelLocalPort: null,
            controlPortOverride: 19000,
            out var port,
            out _,
            allowGatewayPortFallback: false));
        Assert.Equal(19000, port);
    }

    [Fact]
    public void GatewaySwitch_TunnelGatewayToDirectGateway_StaleGlobalTunnelIgnored()
    {
        // After switching from a tunnel gateway to a direct gateway, the resolver
        // is called with useSshTunnel=false (from the active GatewayRecord, not stale
        // global settings). Override is null — resolves to co-located gateway+2.
        Assert.True(BrowserControlEndpoint.TryResolveControlPort(
            gatewayLocalPort: 18789,
            useSshTunnel: false,
            sshTunnelLocalPort: null,
            controlPortOverride: null,
            out var port, out _));
        Assert.Equal(18791, port);
    }
}
