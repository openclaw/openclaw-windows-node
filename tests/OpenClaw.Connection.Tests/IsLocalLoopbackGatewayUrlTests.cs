using OpenClaw.Connection;

namespace OpenClaw.Connection.Tests;

/// <summary>
/// Pins the security-sensitive loopback URL parser used by
/// <see cref="GatewayConnectionManager"/> to scope node-auto-approve
/// suppression to the local wizard's gateway. The original prefix-match
/// implementation accepted hostile lookalikes
/// (<c>ws://localhost.evil.example.com</c>, <c>ws://127.attacker.com</c>);
/// these tests pin the URI-parsing replacement so a future "optimization"
/// back to prefix matching surfaces as a test failure.
/// </summary>
public class IsLocalLoopbackGatewayUrlTests
{
    [Theory]
    // Real loopback targets — must be accepted.
    [InlineData("ws://localhost:18789")]
    [InlineData("ws://localhost:18789/")]
    [InlineData("ws://localhost:18789/path")]
    [InlineData("wss://localhost:18789")]
    [InlineData("ws://127.0.0.1:18789")]
    [InlineData("ws://127.0.0.2:18789")]        // entire 127/8 is loopback
    [InlineData("ws://127.255.255.255:18789")]
    [InlineData("wss://127.0.0.1:18789")]
    [InlineData("ws://[::1]:18789")]            // IPv6 loopback
    [InlineData("wss://[::1]:18789")]
    [InlineData("ws://[::ffff:127.0.0.1]:18789")] // IPv4-mapped IPv6 loopback
    // Coverage extensions: pin behavior so a future .NET runtime
    // change to Uri.IsLoopback can't silently shift the security gate.
    [InlineData("ws://LOCALHOST:18789")]        // host comparison case-insensitive
    [InlineData("ws://user@localhost:18789")]   // userinfo doesn't affect host
    [InlineData("ws://2130706433:18789")]       // decimal-encoded 127.0.0.1 — also loopback
    public void Accepts_RealLoopbackTargets(string url)
    {
        Assert.True(GatewayConnectionManager.IsLocalLoopbackGatewayUrl(url),
            $"Expected loopback acceptance for '{url}'");
    }

    [Theory]
    // Hostile lookalikes the prefix matcher used to accept.
    [InlineData("ws://localhost.evil.example.com:18789")]
    [InlineData("ws://localhost.attacker.com")]
    [InlineData("ws://127.attacker.com:18789")]
    [InlineData("ws://127.0.0.1.evil.com:18789")]
    [InlineData("wss://localhost.evil.example.com")]
    // Wildcard bind is NOT loopback — packets routed through 0.0.0.0
    // can reach the network, so suppression must not apply.
    [InlineData("ws://0.0.0.0:18789")]
    // Non-loopback IPv4 / IPv6 / private addresses.
    [InlineData("ws://192.168.1.1:18789")]
    [InlineData("ws://10.0.0.1:18789")]
    [InlineData("ws://gateway.example.com:18789")]
    [InlineData("ws://[fe80::1]:18789")]        // link-local, not loopback
    // Wrong scheme.
    [InlineData("http://localhost:18789")]
    [InlineData("https://localhost:18789")]
    [InlineData("file://localhost/path")]
    // Malformed / blank.
    [InlineData("not-a-url")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ws://%6cocalhost:18789")]      // percent-encoded "localhost" — Uri rejects parse
    public void Rejects_NonLoopbackOrInvalid(string? url)
    {
        Assert.False(GatewayConnectionManager.IsLocalLoopbackGatewayUrl(url),
            $"Expected loopback rejection for '{url ?? "<null>"}'");
    }
}
