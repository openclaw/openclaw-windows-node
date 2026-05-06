using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public class TokenSanitizerTests
{
    [Fact]
    public void Sanitize_RedactsAuthorizationBearerHeader()
    {
        var sanitized = TokenSanitizer.Sanitize("Authorization: Bearer abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNO12");

        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", sanitized);
        Assert.Contains("Authorization: Bearer [REDACTED]", sanitized);
    }

    [Fact]
    public void Sanitize_RedactsJsonTokenFields()
    {
        var sanitized = TokenSanitizer.Sanitize("""{"authToken":"super-secret-value","other":"visible"}""");

        Assert.Contains(""""authToken":"[REDACTED]"""", sanitized);
        Assert.Contains(""""other":"visible"""", sanitized);
    }

    [Fact]
    public void Sanitize_RedactsBareMcpTokenShape()
    {
        var token = new string('A', 43);
        var sanitized = TokenSanitizer.Sanitize($"token is {token}");

        Assert.DoesNotContain(token, sanitized);
        Assert.Contains("[REDACTED_TOKEN]", sanitized);
    }

    [Fact]
    public void Sanitize_RedactsBareGatewayHexTokenShape()
    {
        const string token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        var sanitized = TokenSanitizer.Sanitize($"argv: openclaw devices approve --token {token}");

        Assert.DoesNotContain(token, sanitized);
        Assert.Contains("[REDACTED_TOKEN]", sanitized);
    }

    [Fact]
    public void Sanitize_DoesNotRedactGatewayHexTokenAdjacentToHexCharacters()
    {
        const string token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        var sanitized = TokenSanitizer.Sanitize($"x{token}f");

        Assert.Equal($"x{token}f", sanitized);
    }
}
