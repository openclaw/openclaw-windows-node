using System;
using System.Text;
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
    [InlineData("wss://user%40domain:p%40ss@example.com", "user%40domain:p%40ss")]
    [InlineData("wss://user%3Aname:p%2Fass@example.com", "user%3Aname:p%2Fass")]
    [InlineData("wss://user:pa%25ss@example.com", "user:pa%25ss")]
    [InlineData("wss://user@example.com", "user")]
    [InlineData("wss://user%40domain@example.com", "user%40domain")]
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
    [InlineData("wss://user:pass@example.com/path/to/endpoint", "wss://example.com/path/to/endpoint")]
    [InlineData("wss://user:pass@host.com:8443/api", "wss://host.com:8443/api")]
    [InlineData("https://user:pass@host.com:8443/api", "wss://host.com:8443/api")]
    [InlineData("http://user:pass@localhost:18789", "ws://localhost:18789")]
    public void TryNormalizeWebSocketUrl_StripsEmbeddedCredentials(string inputUrl, string expectedUrl)
    {
        var result = GatewayUrlHelper.TryNormalizeWebSocketUrl(inputUrl, out var normalized);

        Assert.True(result);
        Assert.Equal(expectedUrl, normalized);
    }

    [Theory]
    [InlineData("user%40domain:p%40ss", "user@domain:p@ss")]
    [InlineData("user%3Aname:p%2Fass", "user:name:p/ass")]
    [InlineData("user:pa%25ss", "user:pa%ss")]
    [InlineData("user%40domain", "user@domain:")]
    [InlineData("username", "username:")]
    public void DecodeCredentials_DecodesUrlEncodedValues(string input, string expected)
    {
        var decoded = GatewayUrlHelper.DecodeCredentials(input);
        Assert.Equal(expected, decoded);
    }

    [Theory]
    [InlineData("user%40domain", "user@domain:")]
    [InlineData("user%40domain:p%40ss", "user@domain:p@ss")]
    public void BasicAuthHeader_UsesDecodedCredentialShape(string input, string expectedCredentialValue)
    {
        var decoded = GatewayUrlHelper.DecodeCredentials(input);
        var header = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(decoded))}";
        var encodedCredentials = header.Substring("Basic ".Length);
        var credentialValue = Encoding.UTF8.GetString(Convert.FromBase64String(encodedCredentials));

        Assert.Equal(expectedCredentialValue, credentialValue);
    }

    [Theory]
    [InlineData("wss://user:pass@example.com", "wss://example.com")]
    [InlineData("https://user:pass@example.com/path", "https://example.com/path")]
    [InlineData("ws://localhost:18789", "ws://localhost:18789")]
    public void SanitizeForDisplay_RemovesUserInfo(string inputUrl, string expectedUrl)
    {
        var sanitized = GatewayUrlHelper.SanitizeForDisplay(inputUrl);
        Assert.Equal(expectedUrl, sanitized);
    }
}
