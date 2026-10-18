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
}
