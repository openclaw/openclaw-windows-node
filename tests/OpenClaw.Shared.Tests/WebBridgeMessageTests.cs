using Xunit;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public class WebBridgeMessageTests
{
    // ── TryParse ─────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_ValidTypeOnly_ReturnsMsgWithNullPayload()
    {
        var msg = WebBridgeMessage.TryParse("""{"type":"ready","payload":{}}""");
        Assert.NotNull(msg);
        Assert.Equal("ready", msg!.Type);
    }

    [Fact]
    public void TryParse_ValidWithStringPayload_ReturnsMsgWithPayload()
    {
        var msg = WebBridgeMessage.TryParse("""{"type":"draft-text","payload":{"text":"hello"}}""");
        Assert.NotNull(msg);
        Assert.Equal("draft-text", msg!.Type);
        Assert.Equal("""{"text":"hello"}""", msg.PayloadJson);
    }

    [Fact]
    public void TryParse_MissingTypeField_ReturnsNull()
    {
        var msg = WebBridgeMessage.TryParse("""{"payload":{}}""");
        Assert.Null(msg);
    }

    [Fact]
    public void TryParse_EmptyTypeValue_ReturnsNull()
    {
        var msg = WebBridgeMessage.TryParse("""{"type":"","payload":{}}""");
        Assert.Null(msg);
    }

    [Fact]
    public void TryParse_NullOrEmptyInput_ReturnsNull()
    {
        Assert.Null(WebBridgeMessage.TryParse(null));
        Assert.Null(WebBridgeMessage.TryParse(""));
        Assert.Null(WebBridgeMessage.TryParse("   "));
    }

    [Fact]
    public void TryParse_InvalidJson_ReturnsNull()
    {
        Assert.Null(WebBridgeMessage.TryParse("not-json"));
        Assert.Null(WebBridgeMessage.TryParse("{bad json"));
    }

    [Fact]
    public void TryParse_TypeIsNotString_ReturnsNull()
    {
        var msg = WebBridgeMessage.TryParse("""{"type":42,"payload":{}}""");
        Assert.Null(msg);
    }

    [Fact]
    public void TryParse_NullPayload_IgnoresPayload()
    {
        var msg = WebBridgeMessage.TryParse("""{"type":"voice-start","payload":null}""");
        Assert.NotNull(msg);
        Assert.Equal("voice-start", msg!.Type);
        Assert.Null(msg.PayloadJson);
    }

    // ── ToJson ───────────────────────────────────────────────────────────

    [Fact]
    public void ToJson_NoPayload_EmitsEmptyObject()
    {
        var msg = new WebBridgeMessage(WebBridgeMessage.TypeRecordingStart);
        var json = msg.ToJson();
        Assert.Contains("\"type\":\"recording-start\"", json);
        Assert.Contains("\"payload\":{}", json);
    }

    [Fact]
    public void ToJson_WithAnonymousPayload_SerializesPayload()
    {
        var msg = new WebBridgeMessage(WebBridgeMessage.TypeDraftText);
        var json = msg.ToJson(new { text = "hello world" });
        Assert.Contains("\"type\":\"draft-text\"", json);
        Assert.Contains("\"text\":\"hello world\"", json);
    }

    [Fact]
    public void ToJson_WithStoredPayloadJson_EmbeddedVerbatim()
    {
        var msg = new WebBridgeMessage(WebBridgeMessage.TypeDraftText, """{"text":"hi"}""");
        var json = msg.ToJson();
        Assert.Contains("\"payload\":{\"text\":\"hi\"}", json);
    }

    [Fact]
    public void ToJson_PassedPayloadOverridesStoredPayloadJson()
    {
        var msg = new WebBridgeMessage(WebBridgeMessage.TypeDraftText, """{"text":"old"}""");
        var json = msg.ToJson(new { text = "new" });
        Assert.Contains("\"text\":\"new\"", json);
        Assert.DoesNotContain("old", json);
    }

    // ── round-trip ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(WebBridgeMessage.TypeRecordingStart)]
    [InlineData(WebBridgeMessage.TypeRecordingStop)]
    [InlineData(WebBridgeMessage.TypeVoiceStart)]
    [InlineData(WebBridgeMessage.TypeVoiceStop)]
    [InlineData(WebBridgeMessage.TypeReady)]
    public void RoundTrip_WellKnownTypes_PreserveType(string type)
    {
        var original = new WebBridgeMessage(type);
        var json = original.ToJson();
        var parsed = WebBridgeMessage.TryParse(json);
        Assert.NotNull(parsed);
        Assert.Equal(type, parsed!.Type);
    }

    [Fact]
    public void RoundTrip_DraftText_PreservesPayload()
    {
        var original = new WebBridgeMessage(WebBridgeMessage.TypeDraftText);
        var json = original.ToJson(new { text = "round trip" });
        var parsed = WebBridgeMessage.TryParse(json);
        Assert.NotNull(parsed);
        Assert.Equal(WebBridgeMessage.TypeDraftText, parsed!.Type);
        Assert.Contains("round trip", parsed.PayloadJson ?? "");
    }
}
