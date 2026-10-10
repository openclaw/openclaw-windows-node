using OpenClaw.Shared.Audio;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class VoicePreviewTextTests
{
    private const string UiFallback = "ui-localized";

    [Theory]
    [InlineData("es-ES", "es")]
    [InlineData("zh-CN", "zh")]
    [InlineData("en-GB", "en")]
    [InlineData("ES-es", "es")]
    public void For_RegionalTag_UsesItsLanguageSentence(string regionalTag, string primaryTag)
    {
        var text = VoicePreviewText.For(regionalTag, UiFallback);

        Assert.NotEqual(UiFallback, text);
        Assert.Equal(VoicePreviewText.For(primaryTag, UiFallback), text);
    }

    [Fact]
    public void For_DifferentLanguages_GetDifferentSentences()
    {
        var sentences = new[] { "en-US", "es-ES", "zh-CN" }.Select(tag => VoicePreviewText.For(tag, UiFallback));

        Assert.Equal(3, sentences.Distinct().Count());
    }

    [Theory]
    [InlineData("xx-XX")]
    [InlineData("")]
    [InlineData(null)]
    public void For_UnknownOrMissingLanguage_UsesUiLocalizedText(string? languageTag)
    {
        Assert.Equal(UiFallback, VoicePreviewText.For(languageTag, UiFallback));
    }

    [Fact]
    public void EveryKokoroVoiceLanguage_HasPreviewText()
    {
        var missing = KokoroModelManager.AvailablePacks
            .SelectMany(p => p.Voices)
            .Select(v => v.LanguageTag)
            .Distinct()
            .Where(tag => VoicePreviewText.For(tag, UiFallback) == UiFallback)
            .ToList();

        Assert.Empty(missing);
    }
}
