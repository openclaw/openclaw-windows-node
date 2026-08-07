using OpenClawTray.Services.VoiceAssistant;

namespace OpenClaw.Tray.Tests.Services;

public sealed class VoiceWakeGateTests
{
    [Theory]
    [InlineData("OpenClaw, summarize my inbox", "OpenClaw", "Summarize my inbox")]
    [InlineData("  openclaw: What's next?", "OpenClaw", "What's next?")]
    [InlineData("Hey OpenClaw - check weather", "hey openclaw", "Check weather")]
    [InlineData("OPENCLAW\uFF0C list reminders", "OpenClaw", "List reminders")]
    [InlineData("OpenClaw, élargis ce résumé", "OpenClaw", "Élargis ce résumé")]
    [InlineData("OpenClaw, 123 reminders", "OpenClaw", "123 reminders")]
    public void TryExtractRequest_MatchingPrefix_ReturnsTrailingRequest(
        string transcript,
        string wakePhrase,
        string expected)
    {
        var matched = VoiceWakeGate.TryExtractRequest(transcript, wakePhrase, out var request);

        Assert.True(matched);
        Assert.Equal(expected, request);
    }

    [Theory]
    [InlineData(null, "OpenClaw")]
    [InlineData("", "OpenClaw")]
    [InlineData("OpenClaw", "OpenClaw")]
    [InlineData("OpenClaw !!!", "OpenClaw")]
    [InlineData("Please OpenClaw summarize", "OpenClaw")]
    [InlineData("OpenClawish summarize", "OpenClaw")]
    [InlineData("Open summarize", "OpenClaw")]
    [InlineData("OpenClaw summarize", "too many words in this phrase")]
    public void TryExtractRequest_NonMatch_ReturnsFalse(string? transcript, string wakePhrase)
    {
        var matched = VoiceWakeGate.TryExtractRequest(transcript, wakePhrase, out var request);

        Assert.False(matched);
        Assert.Empty(request);
    }
}
