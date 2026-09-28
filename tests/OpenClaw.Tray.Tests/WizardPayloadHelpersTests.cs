using System.Text.Json;
using OpenClaw.SetupEngine.UI;
using Xunit;

namespace OpenClaw.Tray.Tests;

public class WizardPayloadHelpersTests
{
    [Theory]
    [InlineData("error", "Model catalog could not be loaded.")]
    [InlineData("cancelled", "The wizard was cancelled.")]
    [InlineData("done", "Configuration could not be saved.")]
    [InlineData("error", "this.prompt is not a function")]
    public void NativeTerminalError_PreservesGatewayErrorInsteadOfMaskingIt(string status, string error)
    {
        var payload = JsonSerializer.SerializeToElement(new { done = true, status, error });
        Assert.Equal(error, WizardPayloadHelpers.GetNativeTerminalError(payload));
    }

    [Theory]
    [InlineData("\"error\"")]
    [InlineData("\"cancelled\"")]
    [InlineData("null")]
    [InlineData("42")]
    public void NativeTerminalError_RejectsNonSuccessWithoutErrorDetail(string status)
    {
        var payload = Parse($"{{\"done\":true,\"status\":{status}}}");
        Assert.Contains("wizard stopped with status", WizardPayloadHelpers.GetNativeTerminalError(payload));
    }

    [Theory]
    [InlineData("""{"done":true,"status":"done"}""")]
    [InlineData("""{"done":true,"status":"done","error":null}""")]
    [InlineData("""{"done":true}""")]
    public void NativeTerminalError_PreservesSuccessfulAndLegacyCompletion(string json)
    {
        Assert.Null(WizardPayloadHelpers.GetNativeTerminalError(Parse(json)));
    }

    private static JsonElement Parse(string json)
        => JsonDocument.Parse(json).RootElement;

    // ---- ExtractStepMessage -----------------------------------------------

    [Fact]
    public void ExtractStepMessage_returns_string_message_unchanged()
    {
        var step = Parse("""{"type":"text","message":"Paste the code:"}""");
        Assert.Equal("Paste the code:", WizardPayloadHelpers.ExtractStepMessage(step));
    }

    [Fact]
    public void ExtractStepMessage_returns_empty_when_field_missing()
    {
        var step = Parse("""{"type":"note"}""");
        Assert.Equal(string.Empty, WizardPayloadHelpers.ExtractStepMessage(step));
    }

    [Fact]
    public void ExtractStepMessage_returns_empty_when_null()
    {
        var step = Parse("""{"type":"note","message":null}""");
        Assert.Equal(string.Empty, WizardPayloadHelpers.ExtractStepMessage(step));
    }

    [Fact]
    public void ExtractStepMessage_unwraps_nested_gemini_note_with_title_and_body()
    {
        // Real-world Gemini OAuth payload
        var step = Parse("""
            {
              "type": "text",
              "message": {
                "type": "note",
                "title": "Gemini CLI OAuth",
                "message": "You are running in a remote/VPS environment. Open the URL below..."
              }
            }
            """);
        var result = WizardPayloadHelpers.ExtractStepMessage(step);
        Assert.Equal(
            "Gemini CLI OAuth\n\nYou are running in a remote/VPS environment. Open the URL below...",
            result);
    }

    [Fact]
    public void ExtractStepMessage_unwraps_nested_note_with_only_message()
    {
        var step = Parse("""{"type":"text","message":{"type":"note","message":"hi"}}""");
        Assert.Equal("hi", WizardPayloadHelpers.ExtractStepMessage(step));
    }

    [Fact]
    public void ExtractStepMessage_unwraps_nested_note_with_only_title()
    {
        var step = Parse("""{"type":"text","message":{"type":"note","title":"Heads up"}}""");
        Assert.Equal("Heads up", WizardPayloadHelpers.ExtractStepMessage(step));
    }

    [Fact]
    public void ExtractStepMessage_returns_empty_for_unknown_object_shape()
    {
        // Defensive: never render raw JSON in the UI. Empty is better than garbage.
        var step = Parse("""{"type":"text","message":{"some":"thing"}}""");
        Assert.Equal(string.Empty, WizardPayloadHelpers.ExtractStepMessage(step));
    }

    [Fact]
    public void ExtractStepMessage_returns_empty_for_non_string_non_object()
    {
        var step = Parse("""{"type":"text","message":42}""");
        Assert.Equal(string.Empty, WizardPayloadHelpers.ExtractStepMessage(step));
    }
}
