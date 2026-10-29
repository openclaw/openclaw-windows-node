using OpenClaw.Shared.Dashboards;
using Xunit;

namespace OpenClaw.Shared.Tests.Dashboards;

public class BoardWidgetUrlPolicyTests
{
    private static readonly Uri Gateway = new("http://127.0.0.1:18789/");
    private static readonly Uri Host = BoardWidgetHostPage.PageUri;

    [Theory]
    [InlineData("ws://127.0.0.1:18789", "http://127.0.0.1:18789/")]
    [InlineData("wss://gw.example.com/socket?token=x", "https://gw.example.com/")]
    [InlineData("WSS://GW.Example.com:8443", "https://gw.example.com:8443/")]
    [InlineData("ws://[::1]:18789", "http://[::1]:18789/")]
    [InlineData("wss://[2001:db8::7]/", "https://[2001:db8::7]/")]
    public void GatewayOrigin_MapsWebSocketSchemeAndDropsPathAndQuery(string url, string expected) =>
        Assert.Equal(new Uri(expected), BoardWidgetUrlPolicy.TryGetGatewayHttpOrigin(url));

    [Fact]
    public void SandboxUrl_KeepsIPv6GatewayAndConfiguredOrigins()
    {
        Assert.True(BoardWidgetUrlPolicy.TryResolveSandboxUrl("/mcp-app-sandbox", 18790, null, new Uri("http://[::1]:18789/"), Host, out var derived));
        Assert.Equal("http://[::1]:18790", BoardWidgetUrlPolicy.OriginString(derived));
        Assert.True(BoardWidgetUrlPolicy.TryResolveSandboxUrl(
            "/mcp-app-sandbox", 18790, "https://[2001:db8::9]:9443", new Uri("https://[2001:db8::7]/"), Host, out var configured));
        Assert.Equal("https://[2001:db8::9]:9443/mcp-app-sandbox", configured.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("file:///c:/gateway")]
    [InlineData("ws://user:secret@127.0.0.1:18789")]
    [InlineData("not a url")]
    public void GatewayOrigin_RejectsUnusableUrls(string? url) =>
        Assert.Null(BoardWidgetUrlPolicy.TryGetGatewayHttpOrigin(url));

    [Fact]
    public void FrameUrl_ResolvesTicketedBoardRouteOnGatewayOrigin()
    {
        Assert.True(BoardWidgetUrlPolicy.TryResolveFrameUrl(
            "/__openclaw__/board/agent%3Amain%3Aops/chart/index.html?bt=abc", Gateway, out var resolved));
        Assert.Equal("http://127.0.0.1:18789/__openclaw__/board/agent%3Amain%3Aops/chart/index.html?bt=abc", resolved.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://evil.example/__openclaw__/board/k/w/index.html?bt=abc")]
    [InlineData("http://127.0.0.1:18790/__openclaw__/board/k/w/index.html?bt=abc")]
    [InlineData("//evil.example/__openclaw__/board/k/w/index.html?bt=abc")]
    [InlineData("/__openclaw__/board/k/w/index.html")]
    [InlineData("/__openclaw__/board/k/w/index.html?bt=")]
    [InlineData("/__openclaw__/a2ui/index.html?bt=abc")]
    [InlineData("/__openclaw__/board/k/w/other.html?bt=abc")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://user:pw@127.0.0.1:18789/__openclaw__/board/k/w/index.html?bt=abc")]
    public void FrameUrl_RejectsOtherOriginsRoutesAndMissingTickets(string frameUrl) =>
        Assert.False(BoardWidgetUrlPolicy.TryResolveFrameUrl(frameUrl, Gateway, out _));

    [Fact]
    public void SandboxUrl_UsesGatewayHostWithSandboxPort()
    {
        Assert.True(BoardWidgetUrlPolicy.TryResolveSandboxUrl("/mcp-app-sandbox?csp=abc&v=1", 18790, null, Gateway, Host, out var resolved));
        Assert.Equal("http://127.0.0.1:18790/mcp-app-sandbox?csp=abc&v=1", resolved.AbsoluteUri);
    }

    [Fact]
    public void SandboxUrl_UsesConfiguredSandboxOrigin()
    {
        Assert.True(BoardWidgetUrlPolicy.TryResolveSandboxUrl(
            "/mcp-app-sandbox?v=1", 18790, "https://sandbox.example.com", Gateway, Host, out var resolved));
        Assert.Equal("https://sandbox.example.com/mcp-app-sandbox?v=1", resolved.AbsoluteUri);
    }

    [Theory]
    [InlineData("/mcp-app-sandbox", 18789, null)]
    [InlineData("/mcp-app-sandbox", 0, null)]
    [InlineData("/mcp-app-sandbox", 70000, null)]
    [InlineData("/other", 18790, null)]
    [InlineData("https://evil.example/mcp-app-sandbox", 18790, null)]
    [InlineData("/mcp-app-sandbox", 18790, "http://127.0.0.1:18789")]
    [InlineData("/mcp-app-sandbox", 18790, "https://sandbox.example.com/path")]
    [InlineData("/mcp-app-sandbox", 18790, "https://user:pw@sandbox.example.com")]
    [InlineData("/mcp-app-sandbox", 18790, "http://openclaw-board-host.invalid")]
    [InlineData("/mcp-app-sandbox", 18790, "ftp://sandbox.example.com")]
    public void SandboxUrl_RejectsGatewayHostOtherPathsAndInvalidOrigins(string url, int port, string? origin) =>
        Assert.False(BoardWidgetUrlPolicy.TryResolveSandboxUrl(url, port, origin, Gateway, Host, out _));

    [Theory]
    [InlineData("http://sandbox.example.com", false)]
    [InlineData("https://sandbox.example.com", true)]
    [InlineData("http://127.0.0.1:18790", true)]
    [InlineData("http://localhost:18790", true)]
    public void SandboxUrl_HttpsGatewayRefusesRemotePlainHttpSandbox(string origin, bool expected)
    {
        var secureGateway = new Uri("https://gw.example.com/");
        Assert.Equal(expected, BoardWidgetUrlPolicy.TryResolveSandboxUrl("/mcp-app-sandbox", 18790, origin, secureGateway, Host, out _));
    }

    [Fact]
    public void SandboxUrl_HttpGatewayKeepsDerivedPlainHttpSandbox()
    {
        var lanGateway = new Uri("http://192.168.1.20:18789/");
        Assert.True(BoardWidgetUrlPolicy.TryResolveSandboxUrl("/mcp-app-sandbox", 18790, null, lanGateway, Host, out var resolved));
        Assert.Equal("http://192.168.1.20:18790/mcp-app-sandbox", resolved.AbsoluteUri);
    }

    [Fact]
    public void SandboxOriginRequired_OnlyForRemoteGatewaysWithoutConfiguredOrigin()
    {
        var widget = TestWidgets.Html("chart");
        Assert.False(BoardWidgetUrlPolicy.SandboxOriginRequired(widget, new Uri("http://127.0.0.1:18790/mcp-app-sandbox")));
        Assert.False(BoardWidgetUrlPolicy.SandboxOriginRequired(widget, new Uri("http://localhost:18790/mcp-app-sandbox")));
        Assert.True(BoardWidgetUrlPolicy.SandboxOriginRequired(widget, new Uri("https://gw.example.com:18790/mcp-app-sandbox")));
        Assert.False(BoardWidgetUrlPolicy.SandboxOriginRequired(
            widget with { SandboxOrigin = "https://sandbox.example.com" }, new Uri("https://sandbox.example.com/mcp-app-sandbox")));
    }

    [Theory]
    [InlineData("https://example.com/docs", true, false)]
    [InlineData("https://user:pw@example.com/", false, false)]
    [InlineData("javascript:alert(1)", false, false)]
    [InlineData("", false, false)]
    public void Website_RequiresHttpsWithoutCredentialsAndFlagsGatewayHost(string url, bool valid, bool sameHost)
    {
        Assert.Equal(valid, BoardWidgetUrlPolicy.TryParseWebsite(url, Gateway, out _, out var actualSameHost));
        Assert.Equal(sameHost, actualSameHost);
    }

    [Fact]
    public void Website_RejectsOverlongUrls() =>
        Assert.False(BoardWidgetUrlPolicy.TryParseWebsite("https://example.com/" + new string('a', 2100), Gateway, out _, out _));

    [Theory]
    [InlineData("https://cdn.example.com/app.js", false)]
    [InlineData("wss://live.example.com/socket", false)]
    [InlineData("data:text/plain,hi", false)]
    [InlineData("http://127.0.0.1:18789/api", true)]
    [InlineData("https://app.localhost./x", true)]
    [InlineData("ws://192.168.1.10:9000/", true)]
    [InlineData("https://[fe80::1]/", true)]
    [InlineData("https://nas/share", true)]
    public void Website_SubresourcesToPrivateNetworkHostsAreBlocked(string url, bool expected) =>
        Assert.Equal(expected, BoardWidgetUrlPolicy.IsBlockedWebsiteRequest(new Uri(url)));

    [Fact]
    public void Website_FlagsGatewayHostRegardlessOfPort()
    {
        Assert.True(BoardWidgetUrlPolicy.TryParseWebsite("https://gw.example.com:9443/app", new Uri("https://gw.example.com/"), out _, out var sameHost));
        Assert.True(sameHost);
    }

    [Theory]
    [InlineData("https://example.com/", true)]
    [InlineData("https://www.example.com:8443/path", true)]
    [InlineData("https://93.184.215.14/", true)]
    [InlineData("https://[2606:2800:21f:cb07:6820:80da:af6b:8b2c]/", true)]
    [InlineData("http://example.com/", false)]
    [InlineData("https://127.0.0.1/", false)]
    [InlineData("https://localhost/", false)]
    [InlineData("https://app.localhost/", false)]
    [InlineData("https://2130706433/", false)]
    [InlineData("https://10.0.0.5/", false)]
    [InlineData("https://192.168.1.1/admin", false)]
    [InlineData("https://169.254.169.254/latest", false)]
    [InlineData("https://100.64.0.1/", false)]
    [InlineData("https://[::1]/", false)]
    [InlineData("https://[fd00::1]/", false)]
    [InlineData("https://router/", false)]
    [InlineData("https://localhost./", false)]
    [InlineData("https://app.localhost./", false)]
    [InlineData("https://router./", false)]
    [InlineData("https://127.0.0.1./", false)]
    public void Website_NavigationsMustStayOnPublicHttpsHosts(string url, bool expected) =>
        Assert.Equal(expected, BoardWidgetUrlPolicy.IsPublicHttpsUrl(new Uri(url)));

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com/a", true)]
    [InlineData("blob:https://example.com/x", false)]
    [InlineData("file:///c:/x", false)]
    [InlineData("https://u:p@example.com", false)]
    public void ExternalLinks_AreHttpOnly(string url, bool expected) =>
        Assert.Equal(expected, BoardWidgetUrlPolicy.IsExternalHttpLink(url, out _));
}

internal static class TestWidgets
{
    public static BoardWidget Html(string name, BoardWidgetGrantState grant = BoardWidgetGrantState.None, bool ticket = true) => new()
    {
        Name = name,
        TabId = "main",
        ContentKind = BoardWidgetContentKind.Html,
        SizeW = 6,
        SizeH = 4,
        Position = 0,
        Revision = 1,
        GrantState = grant,
        FrameUrl = ticket ? $"/__openclaw__/board/k/{name}/index.html?bt=t" : null,
        ViewTicket = ticket ? "t" : null,
    };

    public static BoardWidget Plugin(string name, string kind) => new()
    {
        Name = name,
        TabId = "main",
        ContentKind = BoardWidgetContentKind.Plugin,
        PluginKind = kind,
        SizeW = 6,
        SizeH = 4,
        Position = 0,
        Revision = 1,
    };
}
