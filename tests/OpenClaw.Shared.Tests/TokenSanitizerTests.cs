using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public class TokenSanitizerTests
{
    // ── null / empty input ─────────────────────────────────────────────────

    [Fact]
    public void Sanitize_NullInput_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, TokenSanitizer.Sanitize(null));
    }

    [Fact]
    public void Sanitize_EmptyInput_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, TokenSanitizer.Sanitize(""));
    }

    [Fact]
    public void Sanitize_NoSecrets_ReturnsSameString()
    {
        const string harmless = "Hello, world! This log message has no secrets.";
        Assert.Equal(harmless, TokenSanitizer.Sanitize(harmless));
    }

    // ── Authorization: Bearer ──────────────────────────────────────────────

    [Fact]
    public void Sanitize_RedactsAuthorizationBearerHeader()
    {
        var sanitized = TokenSanitizer.Sanitize("Authorization: Bearer abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNO12");

        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", sanitized);
        Assert.Contains("Authorization: Bearer [REDACTED]", sanitized);
    }

    [Theory]
    [InlineData("AUTHORIZATION: BEARER my-token-value", "AUTHORIZATION: BEARER [REDACTED]")]
    [InlineData("authorization: bearer my-token-value", "authorization: bearer [REDACTED]")]
    [InlineData("Authorization:Bearer my-token-value", "Authorization:Bearer [REDACTED]")]
    [InlineData("Authorization :  Bearer   my-token-value", "Authorization :  Bearer   [REDACTED]")]
    public void Sanitize_BearerHeader_CaseAndSpacingVariants(string input, string expectedResult)
    {
        Assert.Equal(expectedResult, TokenSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_BearerToken_StopsAtWhitespace()
    {
        var sanitized = TokenSanitizer.Sanitize("Authorization: Bearer abc123 other text continues here");

        Assert.Contains("[REDACTED]", sanitized);
        Assert.Contains("other text continues here", sanitized);
        Assert.DoesNotContain("abc123", sanitized);
    }

    [Fact]
    public void Sanitize_BearerInLog_RedactsTokenOnly()
    {
        var input = "2024-01-15 Sending request with Authorization: Bearer tok-secret remaining-log-context";
        var sanitized = TokenSanitizer.Sanitize(input);

        Assert.DoesNotContain("tok-secret", sanitized);
        Assert.Contains("2024-01-15", sanitized);
        Assert.Contains("remaining-log-context", sanitized);
    }

    // ── JSON secret fields ─────────────────────────────────────────────────

    [Fact]
    public void Sanitize_RedactsJsonTokenFields()
    {
        var sanitized = TokenSanitizer.Sanitize("""{"authToken":"super-secret-value","other":"visible"}""");

        Assert.Contains(""""authToken":"[REDACTED]"""", sanitized);
        Assert.Contains(""""other":"visible"""", sanitized);
    }

    [Theory]
    [InlineData("token", """{"token":"my-secret"}""")]
    [InlineData("secret", """{"secret":"my-secret"}""")]
    [InlineData("bearer", """{"bearer":"my-secret"}""")]
    [InlineData("authorization", """{"authorization":"my-secret"}""")]
    [InlineData("access_token", """{"access_token":"my-secret"}""")]
    [InlineData("client_secret", """{"client_secret":"my-secret"}""")]
    [InlineData("BEARER_TOKEN", """{"BEARER_TOKEN":"my-secret"}""")]
    public void Sanitize_JsonFieldsContainingKeyword_AreRedacted(string key, string input)
    {
        var sanitized = TokenSanitizer.Sanitize(input);

        Assert.DoesNotContain("my-secret", sanitized);
        Assert.Contains(key, sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", sanitized);
    }

    [Fact]
    public void Sanitize_JsonFieldWithoutSecretKeyword_IsNotRedacted()
    {
        var sanitized = TokenSanitizer.Sanitize("""{"username":"alice","email":"alice@example.com"}""");

        Assert.Contains("alice", sanitized);
        Assert.DoesNotContain("[REDACTED]", sanitized);
    }

    [Fact]
    public void Sanitize_MultipleJsonSecretFields_AllRedacted()
    {
        var input = """{"token":"tok1","secret":"sec1","name":"alice","authorization":"auth1"}""";
        var sanitized = TokenSanitizer.Sanitize(input);

        Assert.DoesNotContain("tok1", sanitized);
        Assert.DoesNotContain("sec1", sanitized);
        Assert.DoesNotContain("auth1", sanitized);
        Assert.Contains("alice", sanitized);
    }

    // ── Long base64-url token shape ────────────────────────────────────────

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

    [Fact]
    public void Sanitize_TokenAtStartOfString_IsRedacted()
    {
        var token = new string('x', 43);
        var sanitized = TokenSanitizer.Sanitize($"{token} suffix");

        Assert.DoesNotContain(token, sanitized);
        Assert.Contains("[REDACTED_TOKEN]", sanitized);
        Assert.Contains("suffix", sanitized);
    }

    [Fact]
    public void Sanitize_TokenAtEndOfString_IsRedacted()
    {
        var token = new string('Z', 43);
        var sanitized = TokenSanitizer.Sanitize($"prefix {token}");

        Assert.DoesNotContain(token, sanitized);
        Assert.Contains("prefix", sanitized);
        Assert.Contains("[REDACTED_TOKEN]", sanitized);
    }

    [Fact]
    public void Sanitize_ShortToken_NotRedacted()
    {
        // The pattern requires exactly 43 chars; 42-char sequences are NOT redacted.
        var shortToken = new string('A', 42);
        var sanitized = TokenSanitizer.Sanitize($"token is {shortToken} here");

        Assert.Contains(shortToken, sanitized);
        Assert.DoesNotContain("[REDACTED_TOKEN]", sanitized);
    }

    [Fact]
    public void Sanitize_LongerToken44Chars_NotRedacted()
    {
        // 44-char sequences are not matched (pattern anchors at exactly 43 within word boundaries).
        var longToken = new string('A', 44);
        var sanitized = TokenSanitizer.Sanitize($"token is {longToken} here");

        Assert.Contains(longToken, sanitized);
    }

    [Fact]
    public void Sanitize_MultipleTokensInSameString_AllRedacted()
    {
        var t1 = new string('A', 43);
        var t2 = new string('B', 43);
        var sanitized = TokenSanitizer.Sanitize($"first={t1} second={t2}");

        Assert.DoesNotContain(t1, sanitized);
        Assert.DoesNotContain(t2, sanitized);
        Assert.Equal(2, CountOccurrences(sanitized, "[REDACTED_TOKEN]"));
    }

    // ── combinations ───────────────────────────────────────────────────────

    [Fact]
    public void Sanitize_BearerAndJsonInSameString_BothRedacted()
    {
        var input = """Authorization: Bearer tok123 {"apiToken":"api-secret"}""";
        var sanitized = TokenSanitizer.Sanitize(input);

        Assert.DoesNotContain("tok123", sanitized);
        Assert.DoesNotContain("api-secret", sanitized);
        Assert.Contains("[REDACTED]", sanitized);
    }

    private static int CountOccurrences(string source, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
