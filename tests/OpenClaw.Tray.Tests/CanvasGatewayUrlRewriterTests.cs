using OpenClawTray.Helpers;

namespace OpenClaw.Tray.Tests;

public class CanvasGatewayUrlRewriterTests
{
    [Fact]
    public void Rewrite_LeavesExternalUrlUntouched()
    {
        var rewritten = CanvasGatewayUrlRewriter.Rewrite(
            "https://example.com/path?q=1",
            "http://localhost:18789",
            "https://gateway.example");

        Assert.Equal("https://example.com/path?q=1", rewritten);
    }

    [Fact]
    public void Rewrite_MapsConfiguredGatewayOriginToEffectiveTunnelOrigin()
    {
        var rewritten = CanvasGatewayUrlRewriter.Rewrite(
            "https://gateway.example/__openclaw__/a2ui/?session=main",
            CanvasGatewayUrlRewriter.ToHttpOrigin("ws://localhost:18789"),
            CanvasGatewayUrlRewriter.ToHttpOrigin("wss://gateway.example"));

        Assert.Equal("http://localhost:18789/__openclaw__/a2ui/?session=main", rewritten);
    }

    [Fact]
    public void Rewrite_MapsRelativePathToEffectiveGatewayOrigin()
    {
        var rewritten = CanvasGatewayUrlRewriter.Rewrite(
            "/__openclaw__/a2ui/",
            "http://localhost:18789",
            "https://gateway.example");

        Assert.Equal("http://localhost:18789/__openclaw__/a2ui/", rewritten);
    }

    [Fact]
    public void ToHttpOrigin_OmitsDefaultPortsAndKeepsExplicitPorts()
    {
        Assert.Equal("https://gateway.example", CanvasGatewayUrlRewriter.ToHttpOrigin("wss://gateway.example"));
        Assert.Equal("https://gateway.example:8443", CanvasGatewayUrlRewriter.ToHttpOrigin("wss://gateway.example:8443"));
        Assert.Equal("http://localhost:18789", CanvasGatewayUrlRewriter.ToHttpOrigin("ws://localhost:18789"));
        Assert.Equal("https://[::1]", CanvasGatewayUrlRewriter.ToHttpOrigin("wss://[::1]"));
    }

    [Fact]
    public void Rewrite_MapsPortlessPrivateGatewayIncludingExplicitDefaultPort()
    {
        var configured = CanvasGatewayUrlRewriter.ToHttpOrigin("wss://192.168.1.5");
        var effective = CanvasGatewayUrlRewriter.ToHttpOrigin("ws://127.0.0.1:18789");

        Assert.Equal("https://192.168.1.5", configured);
        Assert.Equal(
            "http://127.0.0.1:18789/__openclaw__/canvas/",
            CanvasGatewayUrlRewriter.Rewrite("https://192.168.1.5/__openclaw__/canvas/", effective, configured));
        Assert.Equal(
            "http://127.0.0.1:18789/__openclaw__/canvas/",
            CanvasGatewayUrlRewriter.Rewrite("https://192.168.1.5:443/__openclaw__/canvas/", effective, configured));
    }

    [Theory]
    [InlineData("https://192.168.1.5/canvas", "https://192.168.1.5", true)]
    [InlineData("https://192.168.1.5:443/canvas", "https://192.168.1.5", true)]
    [InlineData("https://100.64.0.1/canvas", "https://100.64.0.1", true)]
    [InlineData("https://192.168.1.5.evil/canvas", "https://192.168.1.5", false)]
    [InlineData("https://192.168.1.5/canvas", "https://192.168.1.5:8443", false)]
    [InlineData("https://example.com/canvas", "https://192.168.1.5", false)]
    public void SharesHttpOrigin_MatchesSchemeHostAndPort(string url, string origin, bool expected)
    {
        Assert.Equal(expected, CanvasGatewayUrlRewriter.SharesHttpOrigin(url, origin));
    }
}
