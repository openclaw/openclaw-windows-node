using OpenClawTray.Helpers;

namespace OpenClaw.Tray.Tests;

public sealed class PersonalGatewayAuthChallengeParserTests
{
    private const string Route = "wss://node.example.ts.net";

    [Theory]
    [InlineData("{\"id\":\"challenge-1\",\"nonce\":\"nonce-123\",\"signedAt\":1793563200000}")]
    [InlineData("\"{\\\"id\\\":\\\"challenge-1\\\",\\\"nonce\\\":\\\"nonce-123\\\",\\\"signedAt\\\":1793563200000}\"")]
    public void TryParse_AcceptsNarrowChallengeFromCurrentTlsAuthority(string message)
    {
        Assert.True(PersonalGatewayAuthChallengeParser.TryParse(
            message,
            "https://node.example.ts.net/chat",
            Route,
            out var challenge));

        Assert.NotNull(challenge);
        Assert.Equal("challenge-1", challenge.Id);
        Assert.Equal("nonce-123", challenge.Nonce);
        Assert.Equal(1793563200000, challenge.SignedAt);
    }

    [Fact]
    public void TryParse_AcceptsChallengeFromMatchingCustomTlsPort()
    {
        Assert.True(PersonalGatewayAuthChallengeParser.TryParse(
            "{\"id\":\"challenge-1\",\"nonce\":\"nonce-123\",\"signedAt\":1793563200000}",
            "https://node.example.ts.net:9443/chat",
            "wss://node.example.ts.net:9443",
            out _));
    }

    [Fact]
    public void TryParse_RejectsChallengeFromDifferentTlsPort()
    {
        Assert.False(PersonalGatewayAuthChallengeParser.TryParse(
            "{\"id\":\"challenge-1\",\"nonce\":\"nonce-123\",\"signedAt\":1793563200000}",
            "https://node.example.ts.net:9444/chat",
            "wss://node.example.ts.net:9443",
            out _));
    }

    [Theory]
    [InlineData("https://attacker.ts.net/chat", "{\"id\":\"id\",\"nonce\":\"n\",\"signedAt\":1}")]
    [InlineData("http://node.example.ts.net/chat", "{\"id\":\"id\",\"nonce\":\"n\",\"signedAt\":1}")]
    [InlineData("https://node.example.ts.net/chat", "{\"id\":\"id\",\"nonce\":\"n|token\",\"signedAt\":1}")]
    [InlineData("https://node.example.ts.net/chat", "{\"id\":\"id\",\"nonce\":\"n\",\"signedAt\":1,\"auth\":{\"token\":\"secret\"}}")]
    public void TryParse_RejectsUntrustedOrCredentialBearingMessages(string source, string message)
    {
        Assert.False(PersonalGatewayAuthChallengeParser.TryParse(message, source, Route, out _));
    }

    [Theory]
    [InlineData("\"1793563200000\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void TryParse_RejectsNonNumericSignedAtWithoutThrowing(string signedAt)
    {
        var message = $"{{\"id\":\"challenge-1\",\"nonce\":\"nonce-123\",\"signedAt\":{signedAt}}}";

        Assert.False(PersonalGatewayAuthChallengeParser.TryParse(
            message, "https://node.example.ts.net/chat", Route, out _));
    }
}
