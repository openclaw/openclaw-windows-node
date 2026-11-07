namespace OpenClaw.Connection.Tests;

public sealed class DashboardBrowserHandoffTests
{
    private static SshTunnelConfig Tunnel(int localPort = 18789) =>
        new("user", "gateway.example", 22, localPort, false, 22);

    [Fact]
    public void SameBinding_AllowsTokenRotationOnTheSameGatewayAndDestination()
    {
        var tunnel = Tunnel();
        var before = new DashboardGatewayTunnelSnapshot(
            "gw-a",
            "ws://127.0.0.1:18789",
            "old-token",
            true,
            tunnel);
        var after = before with { Token = "new-token" };

        Assert.True(DashboardBrowserHandoff.SameBinding(before, after));
    }

    [Fact]
    public void SameBinding_RejectsGatewaySwitchOntoTheSameLocalPort()
    {
        var tunnel = Tunnel();
        var before = new DashboardGatewayTunnelSnapshot(
            "gw-a",
            "ws://127.0.0.1:18789",
            "token-a",
            true,
            tunnel);
        var after = new DashboardGatewayTunnelSnapshot(
            "gw-b",
            "ws://127.0.0.1:18789",
            "token-b",
            true,
            tunnel);

        Assert.False(DashboardBrowserHandoff.SameBinding(before, after));
        Assert.True(DashboardBrowserHandoff.SameTunnelDestination(before.Tunnel, after.Tunnel));
    }

    [Fact]
    public void SameBinding_RejectsDestinationChangeWhenLocalPortStays()
    {
        var before = new DashboardGatewayTunnelSnapshot(
            "gw-a",
            "ws://127.0.0.1:18789",
            "token-a",
            true,
            Tunnel());
        var after = before with { Tunnel = Tunnel() with { Host = "other.example" } };

        Assert.False(DashboardBrowserHandoff.SameBinding(before, after));
    }

    [Fact]
    public void ProjectOntoLocalForward_KeepsTheSavedMountPathAndFragment()
    {
        var tunnel = new SshTunnelConfig("user", "gateway.example", 18789, 19001);
        var saved = new DashboardGatewayTunnelSnapshot(
            "gateway-a",
            "wss://gateway.example/mount/#token=old&view=compact",
            "token-a",
            true,
            tunnel);

        var browserUrl = DashboardBrowserHandoff.ProjectOntoLocalForward(saved.GatewayUrl, tunnel.LocalPort);

        Assert.Equal("https://127.0.0.1:19001/mount/#token=old&view=compact", browserUrl);
        Assert.True(DashboardBrowserHandoff.UrlUsesCapturedForward(
            "https://127.0.0.1:19001/mount/custodian#token=token-a",
            saved));
    }

    [Fact]
    public void UrlUsesCapturedForward_RequiresLoopbackPortAndCapturedToken()
    {
        var captured = new DashboardGatewayTunnelSnapshot(
            "gw-a",
            "ws://127.0.0.1:18789",
            "token a",
            true,
            Tunnel());
        var token = Uri.EscapeDataString(captured.Token);

        Assert.True(DashboardBrowserHandoff.UrlUsesCapturedForward(
            $"http://127.0.0.1:18789/#token={token}",
            captured));
        Assert.False(DashboardBrowserHandoff.UrlUsesCapturedForward(
            "http://127.0.0.1:18789/#token=other",
            captured));
        Assert.False(DashboardBrowserHandoff.UrlUsesCapturedForward(
            $"http://127.0.0.1:9/#token={token}",
            captured));
        Assert.False(DashboardBrowserHandoff.UrlUsesCapturedForward(
            $"https://gateway.example/#token={token}",
            captured));
    }
}
