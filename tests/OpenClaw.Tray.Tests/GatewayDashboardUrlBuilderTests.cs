using OpenClawTray.Helpers;

namespace OpenClaw.Tray.Tests;

public sealed class GatewayDashboardUrlBuilderTests
{
    [Fact]
    public void Build_AppendsSharedTokenToDashboardRoot()
    {
        var url = GatewayDashboardUrlBuilder.Build("ws://localhost:4317", null, "shared token", appendSharedGatewayToken: true);

        Assert.Equal("http://localhost:4317#token=shared%20token", url);
    }

    [Fact]
    public void Build_AppendsSharedTokenToDashboardPath()
    {
        var url = GatewayDashboardUrlBuilder.Build("wss://gateway.example/", "/sessions/abc", "shared", appendSharedGatewayToken: true);

        Assert.Equal("https://gateway.example/sessions/abc#token=shared", url);
    }

    [Fact]
    public void Build_DoesNotAppendNonSharedToken()
    {
        var url = GatewayDashboardUrlBuilder.Build("ws://localhost:4317", "config", "device-token", appendSharedGatewayToken: false);

        Assert.Equal("http://localhost:4317/config", url);
    }

    [Fact]
    public void Build_PutsRouteOnPathAndTokenInFragment()
    {
        var url = GatewayDashboardUrlBuilder.Build(
            "ws://user:secret@host:18789/ui?x=1#old",
            "config",
            "tok",
            appendSharedGatewayToken: true);

        Assert.Equal("http://host:18789/ui/config?x=1#token=tok", url);
        Assert.DoesNotContain("user:secret", url);
    }

    [Fact]
    public void Build_ReplacesExistingTokenFragment()
    {
        var url = GatewayDashboardUrlBuilder.Build(
            "ws://localhost:4317#token=old",
            null,
            "tok",
            appendSharedGatewayToken: true);

        Assert.Equal("http://localhost:4317#token=tok", url);
        Assert.DoesNotContain("&token=", url);
    }

    [Fact]
    public void Build_JoinsRouteQueryBeforeGatewayQueryAndDropsOldFragments()
    {
        var url = GatewayDashboardUrlBuilder.Build(
            "wss://gateway.example/ui?x=1&to%6ben=old#old-base",
            "config?tab=one&token=old-route#old-route",
            "new token",
            appendSharedGatewayToken: true);

        Assert.Equal("https://gateway.example/ui/config?tab=one&x=1#token=new%20token", url);
        Assert.DoesNotContain("old", url);
    }

    [Fact]
    public void Build_RejectsSchemeLessGatewayInput()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            GatewayDashboardUrlBuilder.Build(
                "localhost:18789/ui",
                "config",
                "tok",
                appendSharedGatewayToken: true));

        Assert.Equal("gatewayUrl", error.ParamName);
    }

    [Fact]
    public void Build_DoesNotRetainOldTokenWhenNoSharedTokenIsAppended()
    {
        var url = GatewayDashboardUrlBuilder.Build(
            "ws://localhost:4317/ui?token=old&x=1#old-base",
            "config?tab=one&to%6ben=old-route#old-route",
            "device-token",
            appendSharedGatewayToken: false);

        Assert.Equal("http://localhost:4317/ui/config?tab=one&x=1", url);
    }
}
