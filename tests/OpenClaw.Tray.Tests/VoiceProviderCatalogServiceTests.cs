using System;
using System.IO;
using OpenClaw.Shared;
using OpenClawTray.Services.Voice;
using System.Linq;

namespace OpenClaw.Tray.Tests;

public class VoiceProviderCatalogServiceTests
{
    [Fact]
    public void CatalogFilePath_ResolvesToExistingBundledAsset()
    {
        Assert.EndsWith("voice-providers.json", VoiceProviderCatalogService.CatalogFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(VoiceProviderCatalogService.CatalogFilePath));
    }

    [Fact]
    public void LoadCatalog_IncludesBuiltInMiniMaxAndElevenLabsTtsProviders()
    {
        var catalog = VoiceProviderCatalogService.LoadCatalog();

        Assert.Contains(catalog.TextToSpeechProviders, p => p.Id == VoiceProviderIds.Windows);
        Assert.Contains(catalog.TextToSpeechProviders, p => p.Id == VoiceProviderIds.MiniMax);
        Assert.Contains(catalog.TextToSpeechProviders, p => p.Id == VoiceProviderIds.ElevenLabs);
    }

    [Fact]
    public void SupportsTextToSpeechRuntime_ReturnsTrueForMiniMaxOnlyWhenImplemented()
    {
        Assert.True(VoiceProviderCatalogService.SupportsTextToSpeechRuntime(VoiceProviderIds.Windows));
        Assert.True(VoiceProviderCatalogService.SupportsTextToSpeechRuntime(VoiceProviderIds.MiniMax));
        Assert.True(VoiceProviderCatalogService.SupportsTextToSpeechRuntime(VoiceProviderIds.ElevenLabs));
    }

    [Fact]
    public void LoadCatalog_ExposesBuiltInCloudTtsContracts()
    {
        var catalog = VoiceProviderCatalogService.LoadCatalog();

        var minimax = Assert.Single(catalog.TextToSpeechProviders, p => p.Id == VoiceProviderIds.MiniMax);
        Assert.Equal("MiniMax", minimax.Name);
        Assert.NotNull(minimax.TextToSpeechHttp);
        Assert.Equal("https://api-uw.minimax.io/v1/t2a_v2", minimax.TextToSpeechHttp!.EndpointTemplate);
        Assert.Equal("Authorization", minimax.TextToSpeechHttp.AuthenticationHeaderName);
        Assert.Equal(VoiceTextToSpeechResponseModes.HexJsonString, minimax.TextToSpeechHttp.ResponseAudioMode);
        var minimaxModelSetting = minimax.Settings.Single(s => s.Key == VoiceProviderSettingKeys.Model);
        Assert.Equal("speech-2.8-turbo", minimaxModelSetting.DefaultValue);
        Assert.Contains("speech-2.8-turbo", minimaxModelSetting.Options);
        Assert.Contains("speech-2.5-turbo-preview", minimaxModelSetting.Options);
        Assert.Equal("English_MatureBoss", minimax.Settings.Single(s => s.Key == VoiceProviderSettingKeys.VoiceId).DefaultValue);
        var minimaxVoiceSettingsJson = minimax.Settings.Single(s => s.Key == VoiceProviderSettingKeys.VoiceSettingsJson);
        Assert.False(minimaxVoiceSettingsJson.Required);
        Assert.True(minimaxVoiceSettingsJson.JsonValue);
        Assert.Contains("\"voice_setting\":", minimaxVoiceSettingsJson.Placeholder);
        Assert.Contains("{{voiceId}}", minimaxVoiceSettingsJson.DefaultValue);

        var elevenLabs = Assert.Single(catalog.TextToSpeechProviders, p => p.Id == VoiceProviderIds.ElevenLabs);
        Assert.Equal("ElevenLabs", elevenLabs.Name);
        Assert.NotNull(elevenLabs.TextToSpeechHttp);
        Assert.Equal(
            "https://api.elevenlabs.io/v1/text-to-speech/{{voiceId}}?output_format=mp3_44100_128",
            elevenLabs.TextToSpeechHttp!.EndpointTemplate);
        Assert.Equal("xi-api-key", elevenLabs.TextToSpeechHttp.AuthenticationHeaderName);
        Assert.Equal(VoiceTextToSpeechResponseModes.Binary, elevenLabs.TextToSpeechHttp.ResponseAudioMode);
        var elevenLabsModelSetting = elevenLabs.Settings.Single(s => s.Key == VoiceProviderSettingKeys.Model);
        Assert.Equal("eleven_multilingual_v2", elevenLabsModelSetting.DefaultValue);
        Assert.Contains("eleven_flash_v2_5", elevenLabsModelSetting.Options);
        Assert.Contains("eleven_turbo_v2_5", elevenLabsModelSetting.Options);
        var elevenLabsVoiceSettingsJson = elevenLabs.Settings.Single(s => s.Key == VoiceProviderSettingKeys.VoiceSettingsJson);
        Assert.False(elevenLabsVoiceSettingsJson.Required);
        Assert.True(elevenLabsVoiceSettingsJson.JsonValue);
        Assert.Equal("\"voice_settings\": null", elevenLabsVoiceSettingsJson.DefaultValue);
    }
}
