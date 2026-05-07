using System.Net;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public class HttpUrlRiskEvaluatorTests
{
    [Fact]
    public void Evaluate_PublicHttpsHost_DoesNotRequireConfirmation()
    {
        var risk = HttpUrlRiskEvaluator.Evaluate("https://example.com/path?q=1");

        Assert.False(risk.RequiresConfirmation);
        Assert.Equal("https://example.com/", risk.CanonicalOrigin);
        Assert.Equal("example.com", risk.HostKey);
    }

    [Theory]
    [InlineData("http://example.com/", "HTTPS")]
    [InlineData("https://127.0.0.1:8080/", "loopback")]
    [InlineData("https://192.168.1.1/", "private")]
    [InlineData("https://router/", "no dot")]
    [InlineData("https://8.8.8.8/", "IP literal")]
    public void Evaluate_HighRiskUrls_RequireConfirmation(string url, string reasonFragment)
    {
        var risk = HttpUrlRiskEvaluator.Evaluate(url);

        Assert.True(risk.RequiresConfirmation);
        Assert.Contains(risk.Reasons, reason => reason.Contains(reasonFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_IdnHostnameMismatch_TriggersConfirmation()
    {
        // Cyrillic 'а' looks identical to Latin 'a' but Punycode-encodes to a
        // different ASCII form. The evaluator must surface the mismatch as a
        // Reason so the prompt fires on visually-deceptive hosts.
        var risk = HttpUrlRiskEvaluator.Evaluate("https://аpple.com/");
        Assert.True(risk.RequiresConfirmation);
        Assert.Contains(risk.Reasons, r => r.Contains("punycode", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("::")]                    // unspecified
    [InlineData("2001:db8::1")]           // documentation
    [InlineData("2001:0000::1")]          // Teredo
    [InlineData("2002::1")]               // 6to4
    [InlineData("100::1")]                // discard-only
    [InlineData("fc00::1")]               // unique local
    [InlineData("fe80::1")]               // link-local
    [InlineData("ff00::1")]               // multicast
    [InlineData("::ffff:10.0.0.1")]       // IPv4-mapped private
    public void IsPublicAddress_NonPublicIPv6_ReturnsFalse(string ipString)
    {
        var ip = IPAddress.Parse(ipString);
        Assert.False(HttpUrlRiskEvaluator.IsPublicAddress(ip),
            $"Expected {ipString} to be classified as non-public");
    }

    [Theory]
    [InlineData("2606:4700:4700::1111")]  // Cloudflare DNS — public IPv6
    [InlineData("2400:cb00::1")]
    public void IsPublicAddress_PublicIPv6_ReturnsTrue(string ipString)
    {
        var ip = IPAddress.Parse(ipString);
        Assert.True(HttpUrlRiskEvaluator.IsPublicAddress(ip),
            $"Expected {ipString} to be classified as public");
    }

    // ── IsPublicAddress: IPv4 ────────────────────────────────────────────────

    [Theory]
    [InlineData("0.1.2.3")]           // 0.0.0.0/8 — "this" network
    [InlineData("10.0.0.1")]          // RFC 1918 class A
    [InlineData("10.255.255.255")]     // RFC 1918 class A — boundary
    [InlineData("100.64.0.1")]        // CGNAT 100.64.0.0/10
    [InlineData("100.127.255.255")]   // CGNAT upper boundary
    [InlineData("127.0.0.1")]         // loopback
    [InlineData("127.255.255.255")]   // loopback range boundary
    [InlineData("169.254.0.1")]       // link-local
    [InlineData("169.254.255.255")]   // link-local boundary
    [InlineData("172.16.0.1")]        // RFC 1918 class B lower
    [InlineData("172.31.255.255")]    // RFC 1918 class B upper boundary
    [InlineData("192.168.0.1")]       // RFC 1918 class C
    [InlineData("192.168.255.255")]   // RFC 1918 class C boundary
    [InlineData("224.0.0.1")]         // multicast lower
    [InlineData("239.255.255.255")]   // multicast upper boundary
    [InlineData("255.255.255.255")]   // limited broadcast (>= 224)
    public void IsPublicAddress_NonPublicIPv4_ReturnsFalse(string ipString)
    {
        var ip = IPAddress.Parse(ipString);
        Assert.False(HttpUrlRiskEvaluator.IsPublicAddress(ip),
            $"Expected {ipString} to be classified as non-public");
    }

    [Theory]
    [InlineData("8.8.8.8")]           // Google DNS
    [InlineData("1.1.1.1")]           // Cloudflare DNS
    [InlineData("203.0.113.1")]       // TEST-NET-3 — still public by the classifier's rules
    [InlineData("172.32.0.1")]        // just outside RFC 1918 class B range
    [InlineData("172.15.255.255")]    // just below RFC 1918 class B range
    [InlineData("100.63.255.255")]    // just below CGNAT range
    [InlineData("100.128.0.1")]       // just above CGNAT range
    public void IsPublicAddress_PublicIPv4_ReturnsTrue(string ipString)
    {
        var ip = IPAddress.Parse(ipString);
        Assert.True(HttpUrlRiskEvaluator.IsPublicAddress(ip),
            $"Expected {ipString} to be classified as public");
    }

    [Theory]
    [InlineData("::ffff:8.8.8.8")]    // IPv4-mapped public
    [InlineData("::ffff:1.1.1.1")]    // IPv4-mapped public (Cloudflare)
    public void IsPublicAddress_IPv4MappedPublic_ReturnsTrue(string ipString)
    {
        var ip = IPAddress.Parse(ipString);
        Assert.True(HttpUrlRiskEvaluator.IsPublicAddress(ip),
            $"Expected IPv4-mapped public address {ipString} to be classified as public");
    }

    // ── Evaluate: additional hostname / address forms ─────────────────────────

    [Fact]
    public void Evaluate_Localhost_RequiresConfirmationWithLocalhostReason()
    {
        var risk = HttpUrlRiskEvaluator.Evaluate("https://localhost:3000/");

        Assert.True(risk.RequiresConfirmation);
        Assert.Contains(risk.Reasons, r => r.Contains("localhost", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_PublicIPv6Literal_RequiresConfirmationWithIpLiteralReason()
    {
        var risk = HttpUrlRiskEvaluator.Evaluate("https://[2606:4700:4700::1111]/");

        Assert.True(risk.RequiresConfirmation);
        Assert.Contains(risk.Reasons, r => r.Contains("IP literal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_HostKeyIsLowercased()
    {
        // Port 443 is the default for HTTPS, so uri.Authority omits it.
        // Use a non-default port to verify the port is included in HostKey.
        var risk = HttpUrlRiskEvaluator.Evaluate("https://Example.COM:8443/");

        Assert.Equal("example.com:8443", risk.HostKey);
    }

    [Fact]
    public void Evaluate_CanonicalOriginIncludesNonStandardPort()
    {
        var risk = HttpUrlRiskEvaluator.Evaluate("https://example.com:8443/path");

        Assert.Equal("https://example.com:8443/", risk.CanonicalOrigin);
    }
}
