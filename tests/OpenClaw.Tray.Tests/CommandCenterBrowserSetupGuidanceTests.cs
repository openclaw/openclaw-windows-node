using OpenClaw.Shared;
using OpenClawTray.Helpers;

namespace OpenClaw.Tray.Tests;

public class CommandCenterBrowserSetupGuidanceTests
{
    [Fact]
    public void ManualForward_UsesRemoteGatewayPortPlusTwoWhenBrowserRemoteIsMissing()
    {
        var text = CommandCenterTextHelper.BuildBrowserSetupGuidance(
            19002,
            SshTopology(),
            Tunnel("127.0.0.1:19000", "127.0.0.1:18789", browserRemote: null));

        Assert.Contains("ssh -N -L 19002:127.0.0.1:18791 oc@gateway.example", text);
        Assert.DoesNotContain("127.0.0.1:19002 oc@", text);
    }

    [Fact]
    public void ManualForward_KeepsAnExplicitBrowserRemotePort()
    {
        var text = CommandCenterTextHelper.BuildBrowserSetupGuidance(
            19002,
            SshTopology(),
            Tunnel("127.0.0.1:19000", "127.0.0.1:18789", "127.0.0.1:19999"));

        Assert.Contains("ssh -N -L 19002:127.0.0.1:19999 oc@gateway.example", text);
    }

    [Fact]
    public void ManualForward_LeavesAPlaceholderWhenTheRemoteGatewayPortIsMissing()
    {
        var text = CommandCenterTextHelper.BuildBrowserSetupGuidance(
            19002,
            SshTopology(),
            Tunnel("127.0.0.1:19000", remote: null, browserRemote: null));

        Assert.Contains("ssh -N -L 19002:127.0.0.1:<remote-gateway-port+2> oc@gateway.example", text);
        Assert.DoesNotContain("ssh -N -L 19002:127.0.0.1:19002", text);
    }

    private static GatewayTopologyInfo SshTopology()
        => new()
        {
            UsesSshTunnel = true,
            Host = "gateway.example",
            GatewayUrl = "ws://127.0.0.1:19000"
        };

    private static TunnelCommandCenterInfo Tunnel(string? local, string? remote, string? browserRemote)
        => new()
        {
            User = "oc",
            Host = "gateway.example",
            LocalEndpoint = local ?? "",
            RemoteEndpoint = remote ?? "",
            BrowserProxyRemoteEndpoint = browserRemote ?? ""
        };
}
