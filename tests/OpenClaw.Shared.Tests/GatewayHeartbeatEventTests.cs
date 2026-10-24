using System.Text.Json;

namespace OpenClaw.Shared.Tests;

public sealed class GatewayHeartbeatEventTests
{
    [Fact]
    public void NullIsNoRecord_NotMalformedOrAnOkHeartbeat()
    {
        Assert.Null(GatewayHeartbeatEvent.Parse(JsonSerializer.SerializeToElement<object?>(null)));
        Assert.Throws<JsonException>(() => GatewayHeartbeatEvent.Parse(default));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"ts":1791257718360}""")]
    [InlineData("""{"status":"sent"}""")]
    [InlineData("""{"ts":null,"status":"sent"}""")]
    [InlineData("""{"ts":1791257718360,"status":null}""")]
    [InlineData("""{"ts":1791257718360,"status":""}""")]
    [InlineData("""{"ts":9223372036854775807,"status":"sent"}""")]
    [InlineData("""{"ts":1791257718360,"status":"sent","silent":"false"}""")]
    [InlineData("""{"ts":1791257718360,"status":"sent","durationMs":"42"}""")]
    [InlineData("[]")]
    public void MalformedPayloadFailsExplicitly(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<JsonException>(() => GatewayHeartbeatEvent.Parse(document.RootElement));
    }

    [Fact]
    public void ExactPinnedFieldsAreTyped_UnknownStatusRemainsUnknown()
    {
        using var document = JsonDocument.Parse("""
            {"ts":1791257718360,"status":"future-status","to":"synthetic-destination","accountId":"fixture",
             "preview":"fixture preview","durationMs":42.5,"hasMedia":true,"reason":"fixture-reason",
             "message":"fixture message","channel":"fixture-channel","silent":false,"indicatorType":"alert"}
            """);
        var heartbeat = Assert.IsType<GatewayHeartbeatEvent>(GatewayHeartbeatEvent.Parse(document.RootElement));
        Assert.Equal(1791257718360, heartbeat.Timestamp);
        Assert.Equal("future-status", heartbeat.Status);
        Assert.Equal("synthetic-destination", heartbeat.To);
        Assert.Equal("fixture", heartbeat.AccountId);
        Assert.Equal("fixture preview", heartbeat.Preview);
        Assert.Equal(42.5, heartbeat.DurationMs);
        Assert.True(heartbeat.HasMedia);
        Assert.Equal("fixture-reason", heartbeat.Reason);
        Assert.Equal("fixture message", heartbeat.Message);
        Assert.Equal("fixture-channel", heartbeat.Channel);
        Assert.False(heartbeat.Silent);
        Assert.Equal("alert", heartbeat.IndicatorType);
    }
}
