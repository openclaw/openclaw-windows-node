using OpenClawTray.Onboarding.Services;

namespace OpenClaw.Tray.Tests;

public class GatewayHealthCheckTests
{
    #region TryBuildHealthUri — scheme conversion

    [Fact]
    public void TryBuildHealthUri_WsScheme_ConvertsToHttp()
    {
        var ok = GatewayHealthCheck.TryBuildHealthUri("ws://localhost:18789", out var uri, out _);

        Assert.True(ok);
        Assert.NotNull(uri);
        Assert.Equal("http", uri!.Scheme);
        Assert.Equal("/health", uri.AbsolutePath);
        Assert.Equal(18789, uri.Port);
    }

    [Fact]
    public void TryBuildHealthUri_WssScheme_ConvertsToHttps()
    {
        var ok = GatewayHealthCheck.TryBuildHealthUri("wss://gateway.example.com", out var uri, out _);

        Assert.True(ok);
        Assert.NotNull(uri);
        Assert.Equal("https", uri!.Scheme);
        Assert.Equal("/health", uri.AbsolutePath);
    }

    [Fact]
    public void TryBuildHealthUri_UrlWithPath_AppendsHealth()
    {
        var ok = GatewayHealthCheck.TryBuildHealthUri("ws://host/prefix", out var uri, out _);

        Assert.True(ok);
        Assert.Equal("/prefix/health", uri!.AbsolutePath);
    }

    [Fact]
    public void TryBuildHealthUri_PreservesPort()
    {
        var ok = GatewayHealthCheck.TryBuildHealthUri("ws://localhost:9999", out var uri, out _);

        Assert.True(ok);
        Assert.Equal(9999, uri!.Port);
    }

    #endregion

    #region TryBuildHealthUri — error cases

    [Fact]
    public void TryBuildHealthUri_EmptyUrl_ReturnsFalse()
    {
        var ok = GatewayHealthCheck.TryBuildHealthUri("", out _, out var error);

        Assert.False(ok);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryBuildHealthUri_NullUrl_ReturnsFalse()
    {
        var ok = GatewayHealthCheck.TryBuildHealthUri(null!, out _, out var error);

        Assert.False(ok);
        Assert.NotEmpty(error);
    }

    #endregion
}
