using OpenClawTray.Windows;

namespace OpenClaw.Tray.Tests;

public class CanvasNavigationPolicyTests
{
    [Theory]
    [InlineData("data:text/html,<h1>x</h1>")]
    [InlineData("data:text/html;charset=utf-8,x")]
    public void IsPresentableDataUrl_RejectsHtml(string url)
    {
        Assert.False(CanvasNavigationPolicy.IsPresentableDataUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("data:")]
    public void IsPresentableDataUrl_DoesNotThrowOnShortOrEmptyInput(string? url)
    {
        Assert.False(CanvasNavigationPolicy.IsPresentableDataUrl(url));
    }

    [Fact]
    public void IsPresentableDataUrl_AllowsRfcDefaultWhenMediaTypeIsOmitted()
    {
        Assert.True(CanvasNavigationPolicy.IsPresentableDataUrl("data:,hello"));
    }

    [Fact]
    public void IsPresentableDataUrl_AllowsTextPlain()
    {
        Assert.True(CanvasNavigationPolicy.IsPresentableDataUrl("data:text/plain,hello"));
    }

    [Theory]
    [InlineData("http://127.0.0.1/", false)]
    [InlineData("http://192.168.1.5/", false)]
    [InlineData("https://example.com/", true)]
    public void IsNavigationTargetAllowed_RejectsLoopbackAndLan(string url, bool allowed)
    {
        Assert.Equal(allowed, CanvasNavigationPolicy.IsNavigationTargetAllowed(url));
    }

    [Theory]
    [InlineData(CanvasBearerSource.Document, "same-origin", null, null, true)]
    [InlineData(CanvasBearerSource.None, "same-origin", null, null, true)]
    [InlineData(CanvasBearerSource.Document, "none", null, null, true)]
    [InlineData(CanvasBearerSource.Document, "same-site", null, null, false)]
    [InlineData(CanvasBearerSource.Document, "cross-site", null, null, false)]
    [InlineData(CanvasBearerSource.Document, "SAME-SITE", null, null, false)]
    [InlineData(CanvasBearerSource.ServiceWorker, "same-origin", null, null, false)]
    [InlineData(CanvasBearerSource.SharedWorker, "same-origin", null, null, false)]
    [InlineData(CanvasBearerSource.Other, "same-origin", null, null, false)]
    [InlineData(CanvasBearerSource.Document, null, "document", "navigate", true)]
    [InlineData(CanvasBearerSource.Document, null, "iframe", "navigate", false)]
    [InlineData(CanvasBearerSource.Document, null, null, null, false)]
    public void MayAttachGatewayBearer_RequiresExactOriginOrTopLevelNavigation(
        CanvasBearerSource source,
        string? fetchSite,
        string? fetchDestination,
        string? fetchMode,
        bool allowed)
    {
        Assert.Equal(
            allowed,
            CanvasNavigationPolicy.MayAttachGatewayBearer(source, fetchSite, fetchDestination, fetchMode));
    }
}
