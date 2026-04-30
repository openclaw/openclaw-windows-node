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
}
