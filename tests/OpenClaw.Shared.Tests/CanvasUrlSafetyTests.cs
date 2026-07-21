using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Tests for CanvasUrlSafety.IsPrivateOrLoopbackHost — the host-normalizing private/loopback guard
/// that closes the SSRF bypasses a literal-dotted-decimal regex misses (encoded IPv4, IPv6 forms,
/// CGNAT/Tailscale, 0.0.0.0). Regression for canvas.present loading internal hosts into the WebView.
/// </summary>
public class CanvasUrlSafetyTests
{
    [Theory]
    // canonical private / loopback / link-local
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("169.254.169.254")]   // link-local + cloud metadata
    // encoded IPv4 forms of 127.0.0.1 (what the regex missed)
    [InlineData("2130706433")]        // decimal
    [InlineData("0x7f000001")]        // hex
    [InlineData("0177.0.0.1")]        // octal first octet
    [InlineData("127.1")]             // short form
    // 0.0.0.0 -> localhost, and CGNAT / Tailscale
    [InlineData("0.0.0.0")]
    [InlineData("0")]
    [InlineData("100.119.0.78")]      // 100.64/10 (Tailscale range)
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    // IPv6 loopback / mapped / ULA / link-local (bracketed as they arrive from a URL authority)
    [InlineData("[::1]")]
    [InlineData("[::ffff:127.0.0.1]")]
    [InlineData("[fd00::1]")]
    [InlineData("[fe80::1]")]
    [InlineData("[::]")]
    public void IsPrivateOrLoopbackHost_BlocksInternalHosts(string host)
    {
        Assert.True(CanvasUrlSafety.IsPrivateOrLoopbackHost(host), $"expected {host} to be blocked");
    }

    [Theory]
    // public IPs and DNS names must not be over-blocked
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("example.com")]
    [InlineData("docs.openclaw.ai")]
    [InlineData("11.0.0.1")]           // 11.x is public
    [InlineData("172.15.0.1")]         // just below the 172.16/12 range
    [InlineData("172.32.0.1")]         // just above it
    [InlineData("100.63.255.255")]     // just below CGNAT
    [InlineData("100.128.0.1")]        // just above CGNAT
    [InlineData("[2606:4700::1]")]     // public IPv6
    [InlineData("")]
    public void IsPrivateOrLoopbackHost_AllowsPublicHosts(string host)
    {
        Assert.False(CanvasUrlSafety.IsPrivateOrLoopbackHost(host), $"expected {host} to be allowed");
    }
}
