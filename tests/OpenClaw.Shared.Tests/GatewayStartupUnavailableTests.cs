using System.Text.Json;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class GatewayStartupUnavailableTests
{
    [Theory]
    [InlineData("", 500)]
    [InlineData(",\"retryAfterMs\":null", 500)]
    [InlineData(",\"retryAfterMs\":\"100\"", 500)]
    [InlineData(",\"retryAfterMs\":-10", 100)]
    [InlineData(",\"retryAfterMs\":0", 100)]
    [InlineData(",\"retryAfterMs\":100.9", 100)]
    [InlineData(",\"retryAfterMs\":850.9", 850)]
    [InlineData(",\"retryAfterMs\":2000", 2000)]
    [InlineData(",\"retryAfterMs\":999999999999", 2000)]
    [InlineData(",\"retryAfterMs\":1e999", 500)]
    public void RetryHint_UsesGatewayDefaultFloorAndBounds(string hint, int expected)
    {
        using var response = JsonDocument.Parse(
            """{"error":{"code":"UNAVAILABLE","retryable":true,"details":{"reason":"startup-sidecars"}"""
            + hint + "}}");

        Assert.Equal(expected, GatewayStartupUnavailable.Parse(response.RootElement)?.RetryAfterMs);
    }

    [Theory]
    [InlineData("""{"code":"UNAVAILABLE","retryable":true}""")]
    [InlineData("""{"code":"UNAVAILABLE","retryable":true,"details":{"reason":"maintenance"}}""")]
    [InlineData("""{"code":"UNAVAILABLE","retryable":false,"details":{"reason":"startup-sidecars"}}""")]
    [InlineData("""{"code":"UNAVAILABLE","retryable":"true","details":{"reason":"startup-sidecars"}}""")]
    [InlineData("""{"code":"UNAVAILABLE","details":{"reason":"startup-sidecars"}}""")]
    [InlineData("""{"code":"AUTH_TOKEN_MISMATCH","retryable":true,"details":{"reason":"startup-sidecars"}}""")]
    [InlineData("""{"code":"PROTOCOL_MISMATCH","retryable":true,"details":{"reason":"startup-sidecars"}}""")]
    [InlineData("""{"code":"UNAVAILABLE","retryable":true,"details":[]}""")]
    [InlineData("""{"code":1013,"retryable":true,"details":{"reason":"startup-sidecars"}}""")]
    [InlineData("null")]
    [InlineData("\"gateway starting\"")]
    public void OtherRejections_AreNotStartupState(string error)
    {
        using var response = JsonDocument.Parse("{\"error\":" + error + "}");

        Assert.Null(GatewayStartupUnavailable.Parse(response.RootElement));
    }
}
