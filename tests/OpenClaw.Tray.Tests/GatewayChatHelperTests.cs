using OpenClawTray.Helpers;

namespace OpenClaw.Tray.Tests;

public class GatewayChatHelperTests
{
    #region TryBuildChatUrl — scheme conversion

    [Fact]
    public void TryBuildChatUrl_WsScheme_ConvertsToHttp()
    {
        var ok = GatewayChatUrlBuilder.TryBuildChatUrl(
            "ws://localhost:18789", "tok", out var url, out _);

        Assert.True(ok);
        Assert.StartsWith("http://localhost:18789", url);
    }

    [Fact]
    public void TryBuildChatUrl_WssScheme_ConvertsToHttps()
    {
        var ok = GatewayChatUrlBuilder.TryBuildChatUrl(
            "wss://gateway.example.com", "tok", out var url, out _);

        Assert.True(ok);
        Assert.StartsWith("https://gateway.example.com", url);
    }

    #endregion

    #region TryBuildChatUrl — token encoding

    [Fact]
    public void TryBuildChatUrl_TokenIsUrlEncoded()
    {
        var ok = GatewayChatUrlBuilder.TryBuildChatUrl(
            "ws://localhost:18789", "a b&c=d", out var url, out _);

        Assert.True(ok);
        Assert.Contains("token=a%20b%26c%3Dd", url);
    }

    #endregion

    #region TryBuildChatUrl — session key

    [Fact]
    public void TryBuildChatUrl_SessionKeyAppendedWhenProvided()
    {
        var ok = GatewayChatUrlBuilder.TryBuildChatUrl(
            "ws://localhost:18789", "tok", out var url, out _, sessionKey: "sess123");

        Assert.True(ok);
        Assert.Contains("&session=sess123", url);
    }

    [Fact]
    public void TryBuildChatUrl_SessionKeyOmittedWhenNull()
    {
        var ok = GatewayChatUrlBuilder.TryBuildChatUrl(
            "ws://localhost:18789", "tok", out var url, out _, sessionKey: null);

        Assert.True(ok);
        Assert.DoesNotContain("session=", url);
    }

    [Fact]
    public void TryBuildChatUrl_SessionKeyOmittedWhenEmpty()
    {
        var ok = GatewayChatUrlBuilder.TryBuildChatUrl(
            "ws://localhost:18789", "tok", out var url, out _, sessionKey: "");

        Assert.True(ok);
        Assert.DoesNotContain("session=", url);
    }

    #endregion

    #region TryBuildChatUrl — security restrictions

    [Fact]
    public void TryBuildChatUrl_NonLocalhostHttp_Rejected()
    {
        var ok = GatewayChatUrlBuilder.TryBuildChatUrl(
            "ws://gateway.remote.com:18789", "tok", out _, out var error);

        Assert.False(ok);
        Assert.Contains("secure", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryBuildChatUrl_LocalhostHttp_Accepted()
    {
        var ok = GatewayChatUrlBuilder.TryBuildChatUrl(
            "ws://localhost:18789", "tok", out var url, out _);

        Assert.True(ok);
        Assert.StartsWith("http://localhost", url);
    }

    [Fact]
    public void TryBuildChatUrl_127001_AcceptedAsLocal()
    {
        var ok = GatewayChatUrlBuilder.TryBuildChatUrl(
            "ws://127.0.0.1:18789", "tok", out var url, out _);

        Assert.True(ok);
        Assert.StartsWith("http://127.0.0.1:18789", url);
    }

    #endregion

    #region TryBuildChatUrl — error cases

    [Fact]
    public void TryBuildChatUrl_InvalidUrl_ReturnsFalse()
    {
        var ok = GatewayChatUrlBuilder.TryBuildChatUrl(
            "not-a-url", "tok", out _, out var error);

        Assert.False(ok);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryBuildChatUrl_EmptyUrl_ReturnsFalse()
    {
        var ok = GatewayChatUrlBuilder.TryBuildChatUrl(
            "", "tok", out _, out var error);

        Assert.False(ok);
        Assert.NotEmpty(error);
    }

    #endregion
}
