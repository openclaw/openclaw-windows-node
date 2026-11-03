using System.Text.Json;

namespace OpenClaw.Connection.Tests;

public sealed class GatewayPersonalAuthenticationPolicyTests
{
    [Theory]
    [InlineData("wss://node.example.ts.net", "wss://node.example.ts.net")]
    [InlineData("wss://node.example.ts.net:443/", "wss://node.example.ts.net")]
    [InlineData("wss://node.example.ts.net:9443/", "wss://node.example.ts.net:9443")]
    [InlineData("wss://node.example.ts.net/gateway", "wss://node.example.ts.net/gateway")]
    public void TryNormalizeGatewayUrl_AcceptsCredentialFreeTailscaleTlsRoutes(string input, string expected)
    {
        Assert.True(GatewayPersonalAuthenticationPolicy.TryNormalizeGatewayUrl(input, out var route, out _));
        Assert.Equal(expected, route);
    }

    [Theory]
    [InlineData("ws://node.example.ts.net")]
    [InlineData("wss://node.example.com")]
    [InlineData("wss://ts.net")]
    [InlineData("wss://user:password@node.example.ts.net")]
    [InlineData("wss://node.example.ts.net/?token=secret")]
    [InlineData("wss://node.example.ts.net/#token=secret")]
    [InlineData("wss://node.example.ts.net:0")]
    public void TryNormalizeGatewayUrl_RejectsNonPersonalOrCredentialBearingRoutes(string input)
    {
        Assert.False(GatewayPersonalAuthenticationPolicy.TryNormalizeGatewayUrl(input, out _, out _));
    }

    [Fact]
    public void TryGetCanonicalProfileId_RequiresNonemptyProfileId()
    {
        using var valid = JsonDocument.Parse("{\"profile\":{\"id\":\"person-42\"}}");
        using var missing = JsonDocument.Parse("{\"profile\":{\"name\":\"V\"}}");
        using var whitespace = JsonDocument.Parse("{\"profile\":{\"id\":\"  \"}}");

        Assert.True(GatewayPersonalAuthenticationPolicy.TryGetCanonicalProfileId(valid.RootElement, out var id));
        Assert.Equal("person-42", id);
        Assert.False(GatewayPersonalAuthenticationPolicy.TryGetCanonicalProfileId(missing.RootElement, out _));
        Assert.False(GatewayPersonalAuthenticationPolicy.TryGetCanonicalProfileId(whitespace.RootElement, out _));
    }

    [Fact]
    public void IsTrustedDashboardSource_MatchesTheConfiguredTlsPort()
    {
        Assert.True(GatewayPersonalAuthenticationPolicy.IsTrustedDashboardSource(
            "https://node.example.ts.net:9443/chat", "wss://node.example.ts.net:9443"));
        Assert.False(GatewayPersonalAuthenticationPolicy.IsTrustedDashboardSource(
            "https://node.example.ts.net:9444/chat", "wss://node.example.ts.net:9443"));
        Assert.False(GatewayPersonalAuthenticationPolicy.IsTrustedDashboardSource(
            "https://node.example.ts.net/chat", "wss://node.example.ts.net:9443"));
    }
}
