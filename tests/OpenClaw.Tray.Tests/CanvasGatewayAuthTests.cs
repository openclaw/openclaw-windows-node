using OpenClawTray.Windows;

namespace OpenClaw.Tray.Tests;

public class CanvasGatewayAuthTests
{
    private const string TrustedOrigin = "https://gateway.example";

    [Fact]
    public void ShouldAttach_WhenDocumentAndRequestAreTrustedGatewayOrigin()
    {
        Assert.True(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            TrustedOrigin,
            TrustedOrigin,
            TrustedOrigin));
    }

    [Fact]
    public void ShouldAttach_WhenDocumentIsCanvasVirtualHostAndRequestIsTrustedOrigin()
    {
        Assert.True(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            "https://openclaw-canvas.local/page",
            TrustedOrigin,
            TrustedOrigin));
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
        Assert.True(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            "about:blank",
            TrustedOrigin + "/a2ui",
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
}
