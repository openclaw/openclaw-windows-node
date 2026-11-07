using OpenClawTray.Windows;

namespace OpenClaw.Tray.Tests;

public class CanvasGatewayAuthTests
{
    private const string TrustedOrigin = "https://gateway.example";

    [Fact]
    public void ShouldNotAttach_WhenRefererIsAbsentEvenIfDocumentIsTrusted()
    {
        Assert.False(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            TrustedOrigin,
            TrustedOrigin,
            TrustedOrigin));
    }

    [Fact]
    public void ShouldAttach_WhenDocumentIsCanvasVirtualHostAndRequestIsTrustedOrigin()
    {
        Assert.True(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            "about:blank",
            TrustedOrigin,
            TrustedOrigin,
            "https://openclaw-canvas.local/page"));
    }

    [Fact]
    public void ShouldNotAttach_WhenDocumentIsUntrusted()
    {
        Assert.False(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            "https://evil.example/page",
            TrustedOrigin,
            TrustedOrigin));
    }

    [Fact]
    public void ShouldNotAttach_WhenRequestIsUntrusted()
    {
        Assert.False(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            TrustedOrigin,
            "https://evil.example/",
            TrustedOrigin));
    }

    [Fact]
    public void ShouldAttach_WhenDocumentIsAboutBlankDuringFirstNavigation()
    {
        Assert.False(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            "about:blank",
            TrustedOrigin + "/__openclaw__/a2ui/index.html",
            TrustedOrigin));
    }

    [Fact]
    public void ShouldAttach_WhenNativeNavigationTargetsAnotherGatewayPage()
    {
        var page = TrustedOrigin + "/chat";
        Assert.True(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            "about:blank",
            page,
            TrustedOrigin,
            initiatorUri: null,
            pendingNativeNavigationUrl: page));
        Assert.False(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            "about:blank",
            TrustedOrigin + "/api",
            TrustedOrigin,
            initiatorUri: null,
            pendingNativeNavigationUrl: page));
    }

    [Fact]
    public void ShouldAttach_WhenNativeA2uiNavigationIsPendingFromAboutBlank()
    {
        Assert.True(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            "about:blank",
            TrustedOrigin + "/__openclaw__/a2ui/index.html",
            TrustedOrigin,
            initiatorUri: null,
            pendingNativeNavigationUrl: TrustedOrigin + "/__openclaw__/a2ui/index.html"));
    }

    [Fact]
    public void ShouldNotAttach_WhenAboutBlankRequestsAnythingOtherThanA2ui()
    {
        Assert.False(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            "about:blank",
            TrustedOrigin + "/api",
            TrustedOrigin));
    }

    [Fact]
    public void ShouldNotAttach_WhenInitiatorIsUntrustedEvenIfDocumentIsTrusted()
    {
        Assert.False(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            TrustedOrigin + "/page",
            TrustedOrigin + "/api",
            TrustedOrigin,
            "https://evil.example/frame"));
    }

    [Fact]
    public void ShouldAttach_WhenInitiatorIsTheTrustedGateway()
    {
        Assert.True(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            "about:blank",
            TrustedOrigin + "/api",
            TrustedOrigin,
            TrustedOrigin + "/a2ui"));
    }

    [Fact]
    public void ShouldNotAttach_WhenRequestIsPrefixLookalike()
    {
        Assert.False(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            TrustedOrigin,
            "https://gateway.example.evil/",
            TrustedOrigin));
    }

    [Fact]
    public void ShouldAttach_WhenPendingNavigationDiffersOnlyByFragment()
    {
        var pending = "https://gateway.example/ui/canvas#section";
        Assert.True(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            "about:blank",
            "https://gateway.example/ui/canvas",
            TrustedOrigin,
            initiatorUri: null,
            pendingNativeNavigationUrl: pending));
        Assert.False(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            "about:blank",
            "https://gateway.example/ui/other",
            TrustedOrigin,
            initiatorUri: null,
            pendingNativeNavigationUrl: pending));
        Assert.True(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            "about:blank",
            "https://gateway.example/ui/canvas#other",
            TrustedOrigin,
            initiatorUri: null,
            pendingNativeNavigationUrl: pending));
    }
}
