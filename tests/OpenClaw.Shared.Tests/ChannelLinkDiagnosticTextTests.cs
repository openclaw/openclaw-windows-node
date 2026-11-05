using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class ChannelLinkDiagnosticTextTests
{
    private const string ImagePayload = "data:image/png;base64,pairingimagesecret";

    [Fact]
    public void Format_OmitsTheQrImageFromAFailedWait()
    {
        var parameters = "{\"currentQrDataUrl\":\"" + ImagePayload + "\",\"timeoutMs\":30000}";
        var response = "{\"qrDataUrl\":\"" + ImagePayload + "\",\"error\":\"wait failed\"}";

        var text = ChannelLinkDiagnosticText.Format(
            "web.login.wait", "whatsapp", parameters, "wait failed " + ImagePayload, response);

        Assert.Contains("Method:   web.login.wait", text, StringComparison.Ordinal);
        Assert.Contains("Channel:  whatsapp", text, StringComparison.Ordinal);
        Assert.Contains("\"timeoutMs\":30000", text, StringComparison.Ordinal);
        Assert.Contains("\"currentQrDataUrl\":\"<omitted>\"", text, StringComparison.Ordinal);
        Assert.Contains("\"qrDataUrl\":\"<omitted>\"", text, StringComparison.Ordinal);
        Assert.Contains("wait failed <omitted>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pairingimagesecret", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_RedactsTheImageBeforeTheResponseIsTrimmed()
    {
        var payload = "data:image/png;base64," + new string('A', 1200);
        var text = ChannelLinkDiagnosticText.Format("web.login.wait", "whatsapp", "{}", null, payload);
        Assert.Contains("Response: <omitted>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AAAA", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_KeepsAStartCallThatHasNoImage()
    {
        var text = ChannelLinkDiagnosticText.Format(
            "web.login.start", "whatsapp", "{\"force\":false,\"timeoutMs\":30000}",
            "web login provider is not available", "{\"ok\":false}");

        Assert.Contains("{\"force\":false,\"timeoutMs\":30000}", text, StringComparison.Ordinal);
        Assert.Contains("web login provider is not available", text, StringComparison.Ordinal);
        Assert.Contains("{\"ok\":false}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<omitted>", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("data:image/png;base64,cGF5bG9hZA==")]
    [InlineData("DATA:IMAGE/png;base64,cGF5bG9hZA==")]
    [InlineData(@"data:image\/png;base64,cGF5\u002bG9hZA==")]
    [InlineData(@"data:image\\/png;base64,cGF5\u002bG9hZA==")]
    [InlineData("data:image/png;base64,cGF5\nbG9hZA==")]
    [InlineData("data:image/png;base64,\r\n  cGF5bG9hZA==")]
    [InlineData("data:image/png;base64,cGF5 bG9hZA==")]
    [InlineData(@"data:image\u002Fpng;base64,cGF5%2BbG9hZA==")]
    [InlineData("data:image/svg+xml,%3Csvg%20pairing%3E")]
    [InlineData("{\"currentQrDataUrl\":\"escaped\\\"pairing\",\"timeoutMs\":30000}")]
    public void Redact_OmitsEscapedAndWrappedImagePayloads(string input)
    {
        var text = ChannelLinkDiagnosticText.Redact(input);
        Assert.Contains("<omitted>", text);
        Assert.DoesNotContain("cGF5", text);
        Assert.DoesNotContain("bG9hZA", text);
        Assert.DoesNotContain("pairing", text);
    }

    [Fact]
    public void Redact_KeepsAnActionableFailureReasonAfterTheImage()
    {
        var error = ChannelLinkDiagnosticText.Redact(
            ImagePayload + " web login provider is not available; timed out after 30000ms", ImagePayload);
        Assert.Equal("<omitted> web login provider is not available; timed out after 30000ms", error);
        Assert.True(new WebLoginStartResult { Error = error }.LooksLikeMissingWebLoginProvider);
    }

    [Fact]
    public void Redact_KnownWhitespaceWrappedImageKeepsFollowingFailureReason()
    {
        const string image = "data:image/png;base64,\r\n  cGF5 bG9hZA==";
        Assert.Equal("<omitted> wait expired", ChannelLinkDiagnosticText.Redact(image + " wait expired", image));
        var serialized = System.Text.Json.JsonSerializer.Serialize(new { message = image + " wait expired" });
        Assert.Equal("{\"message\":\"<omitted> wait expired\"}", ChannelLinkDiagnosticText.Redact(serialized, image));
    }
}
