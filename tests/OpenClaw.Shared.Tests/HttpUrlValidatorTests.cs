using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public class HttpUrlValidatorTests
{
    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://example.com")]
    [InlineData("https://example.com/path?query=1#frag")]
    [InlineData("https://sub.example.com:8443/x")]
    [InlineData("HTTPS://EXAMPLE.COM/")]
    public void Accepts_HttpAndHttps(string url)
    {
        var ok = HttpUrlValidator.TryParse(url, out var canonical, out var error);
        Assert.True(ok, error);
        Assert.NotNull(canonical);
    }

    [Fact]
    public void Canonicalizes_LowercasesSchemeAndHost()
    {
        HttpUrlValidator.TryParse("HTTPS://EXAMPLE.COM/Path", out var canonical, out _);
        // Uri.AbsoluteUri lowercases scheme and host; preserves path case.
        Assert.StartsWith("https://example.com/", canonical);
        Assert.EndsWith("Path", canonical);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:network")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("ftp://example.com/")]
    [InlineData("openclaw://settings")]
    public void Rejects_NonHttpSchemes(string url)
    {
        var ok = HttpUrlValidator.TryParse(url, out _, out var error);
        Assert.False(ok);
        Assert.Contains("scheme", error);
    }

    [Theory]
    [InlineData("/relative/path")]
    [InlineData("example.com")]
    [InlineData("not a url")]
    public void Rejects_NonAbsolute(string url)
    {
        var ok = HttpUrlValidator.TryParse(url, out _, out var error);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_NullEmptyOrWhitespace(string? url)
    {
        var ok = HttpUrlValidator.TryParse(url, out _, out var error);
        Assert.False(ok);
        Assert.Equal("url is empty", error);
    }

    [Theory]
    [InlineData("https://attacker@evil.com/")]
    [InlineData("https://user:pass@example.com/")]
    public void Rejects_Userinfo(string url)
    {
        var ok = HttpUrlValidator.TryParse(url, out _, out var error);
        Assert.False(ok);
        Assert.Contains("userinfo", error);
    }

    [Fact]
    public void Trims_LeadingAndTrailingWhitespace()
    {
        var ok = HttpUrlValidator.TryParse("  https://example.com/  ", out var canonical, out _);
        Assert.True(ok);
        Assert.Equal("https://example.com/", canonical);
    }
}
