using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class GatewayUrlHelperTests
{
    [Theory]
    [InlineData("ws://localhost:18789", "ws://localhost:18789")]
    [InlineData("wss://host.tailnet.ts.net", "wss://host.tailnet.ts.net")]
    [InlineData("http://localhost:18789", "ws://localhost:18789")]
    [InlineData("https://host.tailnet.ts.net", "wss://host.tailnet.ts.net")]
    [InlineData("HTTP://LOCALHOST:18789", "ws://LOCALHOST:18789")]
    [InlineData("HTTPS://HOST.EXAMPLE.COM", "wss://HOST.EXAMPLE.COM")]
    public void TryNormalizeWebSocketUrl_NormalizesSupportedSchemes(string inputUrl, string expected)
    {
        var result = GatewayUrlHelper.TryNormalizeWebSocketUrl(inputUrl, out var normalized);

        Assert.True(result);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost:18789")]
    [InlineData("ftp://example.com")]
    [InlineData("file://localhost/c$/temp")]
    public void TryNormalizeWebSocketUrl_RejectsInvalidOrUnsupportedUrls(string inputUrl)
    {
        var result = GatewayUrlHelper.TryNormalizeWebSocketUrl(inputUrl, out var normalized);

        Assert.False(result);
        Assert.Equal(string.Empty, normalized);
    }

    [Theory]
    [InlineData("wss://user:pass@example.com", "user:pass")]
    [InlineData("wss://mytoken:secretkey@gateway.example.org", "mytoken:secretkey")]
    [InlineData("wss://apikey:secrettoken@gateway.example.org", "apikey:secrettoken")]
    [InlineData("ws://user:pass@localhost:18789", "user:pass")]
    [InlineData("https://user:pass@example.com", "user:pass")]
    [InlineData("http://user:pass@localhost:8080", "user:pass")]
    public void ExtractCredentials_ExtractsCredentialsFromUrl(string inputUrl, string expectedCredentials)
    {
        var credentials = GatewayUrlHelper.ExtractCredentials(inputUrl);
        Assert.Equal(expectedCredentials, credentials);
    }

    [Theory]
    [InlineData("wss://example.com")]
    [InlineData("wss://gateway.example.org")]
    [InlineData("ws://localhost:18789")]
    [InlineData("https://example.com")]
    [InlineData("http://localhost:8080")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    public void ExtractCredentials_ReturnsNullWhenNoCredentials(string inputUrl)
    {
        var credentials = GatewayUrlHelper.ExtractCredentials(inputUrl);
        Assert.Null(credentials);
    }

    [Theory]
    [InlineData("wss://user:pass@example.com/path/to/endpoint", "wss://user:pass@example.com/path/to/endpoint")]
    [InlineData("wss://user:pass@host.com:8443/api", "wss://user:pass@host.com:8443/api")]
    public void TryNormalizeWebSocketUrl_PreservesPathAndCredentials(string inputUrl, string expectedUrl)
    {
        var result = GatewayUrlHelper.TryNormalizeWebSocketUrl(inputUrl, out var normalized);

        Assert.True(result);
        Assert.Equal(expectedUrl, normalized);
    }
}
