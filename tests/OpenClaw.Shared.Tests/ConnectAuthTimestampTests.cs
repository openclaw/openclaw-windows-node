using System.Text.Json;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class ConnectAuthTimestampTests
{
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    [Fact]
    public void ResolveSignedAt_UsesValidChallengeTimestamp()
    {
        const long challengeTimestampMs = 1_716_480_000_000;
        var fallback = new FixedTimeProvider(DateTimeOffset.UnixEpoch);

        var signedAt = ConnectAuthTimestamp.ResolveSignedAt(challengeTimestampMs, fallback);

        Assert.Equal(challengeTimestampMs, signedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void ResolveSignedAt_MissingOrInvalidChallenge_UsesBoundedHostFallback(long? challengeTimestampMs)
    {
        var fallbackTime = DateTimeOffset.FromUnixTimeMilliseconds(1_716_480_130_000);
        var fallback = new FixedTimeProvider(fallbackTime);

        var signedAt = ConnectAuthTimestamp.ResolveSignedAt(challengeTimestampMs, fallback);

        Assert.Equal(fallbackTime.ToUnixTimeMilliseconds(), signedAt);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"ts":0}""")]
    [InlineData("""{"ts":-1}""")]
    [InlineData("""{"ts":"1716480000000"}""")]
    [InlineData("""{"ts":1.5}""")]
    public void ReadChallengeTimestamp_MissingOrInvalidValue_ReturnsNull(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);

        Assert.Null(ConnectAuthTimestamp.ReadChallengeTimestamp(document.RootElement));
    }
}
