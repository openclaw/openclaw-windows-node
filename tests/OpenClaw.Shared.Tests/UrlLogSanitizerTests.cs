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
    [InlineData("https://example.com/reset-password%2Fsecret", "https://example.com/reset-password/…")]
    [InlineData("https://example.com/oauth%3Fcode=secret", "https://example.com/oauth")]
    [InlineData("https://example.com/cb%23token=secret", "https://example.com/cb")]
    public void Sanitize_DropsEncodedSeparators(string input, string expected)
    {
        var sanitized = UrlLogSanitizer.Sanitize(input);
        Assert.Equal(expected, sanitized);
        Assert.DoesNotContain("secret", sanitized, System.StringComparison.OrdinalIgnoreCase);
    }

    // Percent-decoding %2F/%3F/%23 also decodes line breaks and other controls.
    // A decoded newline in a canvas log line forges the next entry. Strip the
    // control instead of leaving it for a later regex that stops at that break.
    [Theory]
    [InlineData("https://example.com/a%0A[INFO]%20FORGED", "https://example.com/a[INFO] FORGED")]
    [InlineData("https://example.com/api%0D%0AInjected", "https://example.com/apiInjected")]
    [InlineData("https://example.com/ok%00secret", "https://example.com/oksecret")]
    [InlineData("https://example.com/log%1B%5B31mRED", "https://example.com/log[31mRED")]
    [InlineData("https://example.com/col%09umn", "https://example.com/column")]
    [InlineData("https://example.com/nel%C2%85injected", "https://example.com/nelinjected")]
    [InlineData("https://example.com/lsep%E2%80%A8injected", "https://example.com/lsepinjected")]
    [InlineData("https://example.com/psep%E2%80%A9injected", "https://example.com/psepinjected")]
    [InlineData("https://example.com/ok%0A/secret-token", "https://example.com/ok/…")]
    public void Sanitize_StripsDecodedLogControls(string input, string expected)
    {
        var sanitized = UrlLogSanitizer.Sanitize(input);
        Assert.Equal(expected, sanitized);
        Assert.DoesNotMatch(@"[\x00-\x1F\x7F\u0085\u2028\u2029]", sanitized);
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
