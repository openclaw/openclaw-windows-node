using OpenClawTray.Helpers;

namespace OpenClaw.Tray.Tests;

public sealed class GatewayDashboardUrlBuilderTests
{
    [Theory]
    [InlineData("wss://gateway.example/mount/", "https://gateway.example/mount/custodian?onboarding=1#token=synthetic")]
    [InlineData("wss://gateway.example/mount/#token=synthetic", "https://gateway.example/mount/custodian?onboarding=1#token=synthetic")]
    public void Build_CustodianPreservesMountAndAuthFragment(string gateway, string expected)
    {
        Assert.Equal(expected, GatewayDashboardUrlBuilder.Build(
            gateway, "custodian?onboarding=1", gateway.Contains('#') ? null : "synthetic", true));
    }

    [Fact]
    public void Build_RotatedSharedTokenReplacesOldFragmentTokenWithoutQueryExport()
    {
        Assert.Equal("https://gateway.example/mount/custodian?onboarding=1#view=compact&token=current",
            GatewayDashboardUrlBuilder.Build("wss://gateway.example/mount/#token=old&view=compact",
                "custodian?onboarding=1", "current", true));
    }

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
    public void TryBuild_UnsupportedScheme_ReturnsFalseWithoutAUrl()
    {
        var ok = GatewayDashboardUrlBuilder.TryBuild(
            "ftp://gateway.example",
            "config",
            "tok",
            appendSharedGatewayToken: true,
            out var url,
            out var error);

        Assert.False(ok);
        Assert.Equal(string.Empty, url);
        Assert.Contains("http, https, ws, or wss", error, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("ws://gateway.example", "chat#session=agent%3Amain%3Areview", "http://gateway.example/chat#session=agent%3Amain%3Areview")]
    [InlineData("ws://gateway.example#session=agent%3Amain%3Areview", null, "http://gateway.example#session=agent%3Amain%3Areview")]
    [InlineData("ws://gateway.example#session=base", "chat#ses%73ion=route%26one%2Btwo+three", "http://gateway.example/chat#session=route%26one%2Btwo+three")]
    [InlineData("ws://gateway.example#session=base", "chat#session=first&session=second", "http://gateway.example/chat#session=first")]
    [InlineData("ws://gateway.example#session=base", "chat#session=&session=second", "http://gateway.example/chat#session=")]
    [InlineData("ws://gateway.example#session=base", "chat#session", "http://gateway.example/chat#session=")]
    public void Build_PreservesOnlyTheFirstFragmentSession(
        string gatewayUrl, string? path, string expected)
    {
        Assert.Equal(expected, GatewayDashboardUrlBuilder.Build(
            gatewayUrl, path, "test-auth-token", appendSharedGatewayToken: false));
        Assert.Equal(expected + "&token=test-auth-token", GatewayDashboardUrlBuilder.Build(
            gatewayUrl, path, "test-auth-token", appendSharedGatewayToken: true));
    }

    [Theory]
    [InlineData("ws://gateway.example", "chat?session=query#session=fragment", "http://gateway.example/chat?session=query")]
    [InlineData("ws://gateway.example?session=base-query", "chat#session=fragment", "http://gateway.example/chat?session=base-query")]
    [InlineData("ws://gateway.example?session=base-query", "chat?session=route-query#session=fragment", "http://gateway.example/chat?session=route-query&session=base-query")]
    [InlineData("ws://gateway.example", "chat?ses%73ion=encoded%3Aquery#session=fragment", "http://gateway.example/chat?ses%73ion=encoded%3Aquery")]
    [InlineData("ws://gateway.example", "chat?session=&session=second#session=fragment", "http://gateway.example/chat?session=&session=second")]
    [InlineData("ws://gateway.example", "chat?session#session=fragment", "http://gateway.example/chat?session")]
    public void Build_PreservesQuerySessionPrecedenceIncludingEmptySelection(
        string gatewayUrl, string path, string expected)
    {
        Assert.Equal(expected, GatewayDashboardUrlBuilder.Build(
            gatewayUrl, path, "test-auth-token", appendSharedGatewayToken: false));
        Assert.Equal(expected + "#token=test-auth-token", GatewayDashboardUrlBuilder.Build(
            gatewayUrl, path, "test-auth-token", appendSharedGatewayToken: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_SessionAllowlistDoesNotRestoreFragmentAuthOrEndpointOverrides(bool appendToken)
    {
        var url = GatewayDashboardUrlBuilder.Build(
            "ws://gateway.example?token=old&x=1#gatewayUrl=wss%3A%2F%2Fother.example&password=old&token=old&session=base",
            "chat?to%6ben=old-route#token=old-route&session=agent%3Amain%3Areview&token=duplicate&gatewayUrl=wss%3A%2F%2Fother.example&password=old-route",
            "test-auth-token",
            appendToken);

        Assert.Equal(
            "http://gateway.example/chat?x=1#session=agent%3Amain%3Areview" +
            (appendToken ? "&token=test-auth-token" : string.Empty),
            url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Build_PreservesSessionWithoutAnAvailableSharedToken(string? sharedToken)
    {
        Assert.Equal("http://gateway.example/chat#session=agent%3Amain%3Areview",
            GatewayDashboardUrlBuilder.Build(
                "ws://gateway.example", "chat#session=agent%3Amain%3Areview",
                sharedToken, appendSharedGatewayToken: true));
    }

    [Theory]
    [InlineData("chat#old")]
    [InlineData("chat#SESSION=ignored")]
    [InlineData("chat#token=old&password=old&gatewayUrl=wss%3A%2F%2Fother.example")]
    public void Build_DoesNotInventMissingSessionSelection(string path)
    {
        Assert.Equal("http://gateway.example/chat#token=test-auth-token",
            GatewayDashboardUrlBuilder.Build(
                "ws://gateway.example", path, "test-auth-token", appendSharedGatewayToken: true));
    }
}
