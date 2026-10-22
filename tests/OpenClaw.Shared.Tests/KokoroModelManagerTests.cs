using OpenClaw.Shared.Audio;
using OpenClaw.TestSupport;

namespace OpenClaw.Shared.Tests;

public sealed class KokoroModelManagerTests
{
    [Theory]
    [InlineData("af_maple", "v1.1-en-zh")]
    [InlineData("zm_100", "v1.1-en-zh")]
    [InlineData("ef_dora", "v1.0-es")]
    [InlineData("AF_MAPLE", null)] // kokoro.cpp voice names are case-sensitive
    [InlineData("en_US-amy-low", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FindPackForVoice_MatchesCatalogVoicesOrdinally(string? voiceId, string? expectedPackId)
    {
        Assert.Equal(expectedPackId, KokoroModelManager.FindPackForVoice(voiceId)?.PackId);
    }

    [Theory]
    [InlineData("af_maple", "en-US")]
    [InlineData("zm_100", "zh-CN")]
    [InlineData("ef_dora", "es-ES")]
    [InlineData("AF_MAPLE", null)]
    [InlineData(null, null)]
    public void FindVoice_ReturnsCatalogVoiceLanguage(string? voiceId, string? expectedLanguageTag)
    {
        Assert.Equal(expectedLanguageTag, KokoroModelManager.FindVoice(voiceId)?.LanguageTag);
    }

    [Fact]
    public void VoiceIds_AreUniqueAcrossPacks()
    {
        var voiceIds = KokoroModelManager.AvailablePacks.SelectMany(p => p.Voices).Select(v => v.VoiceId).ToList();

        Assert.Equal(voiceIds.Count, voiceIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void IsPackDownloaded_RequiresBothFilesAndNoInstallMarker_AndDeletePackClearsIt()
    {
        using var temp = new TempDirectory("kokoro-pack-");
        var manager = new KokoroModelManager(temp.Path, NullLogger.Instance);
        var pack = KokoroModelManager.AvailablePacks[0];
        var packDirectory = Directory.CreateDirectory(manager.GetPackDirectory(pack.PackId));
        File.WriteAllText(manager.GetModelPath(pack.PackId), "model");
        File.WriteAllText(manager.GetVoicesPath(pack.PackId), "voices");

        Assert.True(manager.IsPackDownloaded(pack.PackId));
        Assert.True(manager.IsVoiceReady(pack.Voices[0].VoiceId));

        var markerPath = $"{packDirectory.FullName}.installing";
        File.WriteAllText(markerPath, string.Empty);
        Assert.False(manager.IsPackDownloaded(pack.PackId));

        File.Delete(markerPath);
        File.Delete(manager.GetVoicesPath(pack.PackId));
        Assert.False(manager.IsPackDownloaded(pack.PackId));

        Assert.True(manager.DeletePack(pack.PackId));
        Assert.False(manager.IsPackDownloaded(pack.PackId));
        Assert.False(Directory.Exists(packDirectory.FullName));
    }
}
