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
    public void ToHttpOrigin_NormalizesWebSocketUrls()
    {
        Assert.Equal("https://gateway.example:443", CanvasGatewayUrlRewriter.ToHttpOrigin("wss://gateway.example"));
        Assert.Equal("http://localhost:18789", CanvasGatewayUrlRewriter.ToHttpOrigin("ws://localhost:18789"));
    }
}
