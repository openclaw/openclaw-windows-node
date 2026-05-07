using OpenClaw.Shared.Audio;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// SttCapability accepts BCP-47 language tags (the validator + MCP docs
/// both advertise the wider shape like "en-US"), but Whisper.net's
/// WithLanguage call only understands "auto" or 2-letter ISO 639-1 codes.
/// SpeechToTextService.NormalizeForWhisper bridges the gap. These tests
/// pin the normalization rules so a future change can't silently start
/// passing a region-tagged BCP-47 string straight to Whisper.
/// </summary>
public class SpeechToTextLanguageNormalizationTests
{
    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("AUTO", "auto")]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData("en-US", "en")]
    [InlineData("en-us", "en")]
    [InlineData("zh-Hans-CN", "zh")]
    [InlineData("fr-FR", "fr")]
    [InlineData(" ja-JP ", "ja")]
    public void NormalizeForWhisper_StripsRegionAndScript(string input, string expected)
    {
        Assert.Equal(expected, SpeechToTextService.NormalizeForWhisper(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]            // 3-letter — no safe ISO 639-3 cross-walk
    [InlineData("e")]              // single letter
    [InlineData("123-XX")]         // numeric primary subtag
    [InlineData("en1-US")]         // non-letter primary
    public void NormalizeForWhisper_FallsBackToAuto_OnInvalid(string? input)
    {
        Assert.Equal("auto", SpeechToTextService.NormalizeForWhisper(input));
    }
}
