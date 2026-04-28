using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// The sanitizer is meant to make it safe to put agent-supplied URLs in
/// disk-backed log files. The threat is that query strings carry tokens, and
/// log retention is "forever" on a developer machine. These tests assert that
/// the common credential-bearing query parameters never appear in the output.
/// </summary>
public sealed class UrlLogSanitizerTests
{
    [Theory]
    [InlineData("https://login.example.com/oauth/callback?code=abc123&state=xyz",
                "https://login.example.com/oauth/…")]
    [InlineData("https://example.com/reset?token=secret-recovery-link",
                "https://example.com/reset")]
    [InlineData("https://api.example.com/v1/users?email=user@example.com",
                "https://api.example.com/v1/…")]
    [InlineData("https://share.example.com/file?sig=AAA&Expires=1",
                "https://share.example.com/file")]
    [InlineData("https://example.com/", "https://example.com/")]
    [InlineData("https://example.com:8443/path?q=1#frag",
                "https://example.com:8443/path")]
    public void Sanitize_DropsQueryAndFragment(string input, string expected)
    {
        Assert.Equal(expected, UrlLogSanitizer.Sanitize(input));
    }

    [Theory]
    [InlineData("token=secret")]
    [InlineData("code=abc")]
    [InlineData("email=user@example.com")]
    [InlineData("sig=signature-bytes")]
    [InlineData("password=hunter2")]
    public void Sanitize_NeverEchoesCredentialFragments(string credentialQuery)
    {
        var sanitized = UrlLogSanitizer.Sanitize($"https://example.com/path?{credentialQuery}");
        Assert.DoesNotContain(credentialQuery, sanitized, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, "<empty>")]
    [InlineData("", "<empty>")]
    [InlineData("not a url", "<unparseable URL>")]
    public void Sanitize_HandlesEdgeCases(string? input, string expected)
    {
        Assert.Equal(expected, UrlLogSanitizer.Sanitize(input));
    }
}
