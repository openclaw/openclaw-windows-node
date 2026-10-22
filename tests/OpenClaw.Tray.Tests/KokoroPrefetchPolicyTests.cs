using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class KokoroPrefetchPolicyTests
{
    private static string? Select(
        string? provider = "kokoro",
        string? voiceId = "af_maple",
        bool ttsEnabled = true,
        bool autoDownload = true,
        bool downloaded = false,
        bool metered = false) =>
        KokoroPrefetchPolicy.SelectPackToPrefetch(
            provider, voiceId, ttsEnabled, autoDownload, _ => downloaded, () => metered);

    [Fact]
    public void KokoroWithMissingPack_SelectsVoicePack()
    {
        Assert.Equal("v1.1-en-zh", Select());
        Assert.Equal("v1.0-es", Select(voiceId: "ef_dora"));
        Assert.Equal("v1.1-en-zh", Select(provider: null)); // blank provider resolves to Kokoro
    }

    [Theory]
    [InlineData("piper", "af_maple", true, true, false, false)]
    [InlineData("windows", "af_maple", true, true, false, false)]
    [InlineData("kokoro", "af_maple", false, true, false, false)] // TTS disabled
    [InlineData("kokoro", "af_maple", true, false, false, false)] // user deleted the pack
    [InlineData("kokoro", "af_maple", true, true, true, false)]   // already on disk
    [InlineData("kokoro", "af_maple", true, true, false, true)]   // metered connection
    [InlineData("kokoro", "not-a-voice", true, true, false, false)]
    public void SkipsWhenAnyGateFails(string provider, string voiceId, bool tts, bool auto, bool downloaded, bool metered)
    {
        Assert.Null(Select(provider, voiceId, tts, auto, downloaded, metered));
    }

    [Fact]
    public void MeteredCheck_NotQueriedWhenOtherGatesFail()
    {
        var queried = false;
        KokoroPrefetchPolicy.SelectPackToPrefetch("piper", "af_maple", true, true, _ => false, () => queried = true);
        Assert.False(queried);
    }
}
