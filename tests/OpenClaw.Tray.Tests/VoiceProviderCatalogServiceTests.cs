using System;
using System.IO;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services.Voice;
using System.Linq;

namespace OpenClaw.Tray.Tests;

public class VoiceProviderCatalogServiceTests
{
    [Fact]
    public void GetVoiceTrayIconPath_ReturnsBundledAppIconForOff()
    {
        var path = IconHelper.GetVoiceTrayIconPath(VoiceTrayIconState.Off);

        Assert.Equal(IconHelper.GetAppIconPath(), path, ignoreCase: true);
    }

    [Fact]
    public void GetVoiceTrayIconPath_GeneratesListeningVariant()
    {
        var path = IconHelper.GetVoiceTrayIconPath(VoiceTrayIconState.Listening);

        Assert.True(File.Exists(path));
        Assert.EndsWith(".ico", path, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(IconHelper.GetAppIconPath(), path, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CatalogFilePath_ResolvesToExistingBundledAsset()
    {
        Assert.EndsWith("voice-providers.json", VoiceProviderCatalogService.CatalogFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(VoiceProviderCatalogService.CatalogFilePath));
    }

    [Fact]
    public void LoadCatalog_IncludesOnlySelectableAndVisibleSpeechProviders()
    {
        var catalog = VoiceProviderCatalogService.LoadCatalog();

        Assert.Contains(catalog.SpeechToTextProviders, p => p.Id == VoiceProviderIds.Windows);
        Assert.Contains(catalog.SpeechToTextProviders, p => p.Id == VoiceProviderIds.SherpaOnnx);
        Assert.DoesNotContain(catalog.SpeechToTextProviders, p => p.Id == VoiceProviderIds.FoundryLocal);
        Assert.DoesNotContain(catalog.SpeechToTextProviders, p => p.Id == VoiceProviderIds.OpenAiWhisper);
        Assert.DoesNotContain(catalog.SpeechToTextProviders, p => p.Id == VoiceProviderIds.ElevenLabsSpeechToText);
        Assert.DoesNotContain(catalog.SpeechToTextProviders, p => p.Id == VoiceProviderIds.AzureAiSpeech);
        Assert.Contains(catalog.TextToSpeechProviders, p => p.Id == VoiceProviderIds.Windows);
        Assert.Contains(catalog.TextToSpeechProviders, p => p.Id == VoiceProviderIds.MiniMax);
        Assert.Contains(catalog.TextToSpeechProviders, p => p.Id == VoiceProviderIds.ElevenLabs);
    }

    [Fact]
    public void SupportsSpeechToTextRuntime_ReportsWindowsRouteSupportForConfiguredSpeechProviders()
    {
        Assert.True(VoiceProviderCatalogService.SupportsSpeechToTextRuntime(VoiceProviderIds.Windows));
        Assert.True(VoiceProviderCatalogService.SupportsSpeechToTextRuntime(VoiceProviderIds.FoundryLocal));
        Assert.True(VoiceProviderCatalogService.SupportsSpeechToTextRuntime(VoiceProviderIds.OpenAiWhisper));
        Assert.True(VoiceProviderCatalogService.SupportsSpeechToTextRuntime(VoiceProviderIds.ElevenLabsSpeechToText));
        Assert.True(VoiceProviderCatalogService.SupportsSpeechToTextRuntime(VoiceProviderIds.AzureAiSpeech));
        Assert.False(VoiceProviderCatalogService.SupportsSpeechToTextRuntime(VoiceProviderIds.SherpaOnnx));
    }

    [Fact]
    public void SupportsTextToSpeechRuntime_ReturnsTrueForImplementedProviders()
    {
        Assert.True(VoiceProviderCatalogService.SupportsTextToSpeechRuntime(VoiceProviderIds.Windows));
        Assert.True(VoiceProviderCatalogService.SupportsTextToSpeechRuntime(VoiceProviderIds.MiniMax));
        Assert.True(VoiceProviderCatalogService.SupportsTextToSpeechRuntime(VoiceProviderIds.ElevenLabs));
    }

    [Fact]
    public void LoadCatalog_ExposesBuiltInCloudTtsContracts()
    {
        var catalog = VoiceProviderCatalogService.LoadCatalog();

        var sherpaOnnx = Assert.Single(catalog.SpeechToTextProviders, p => p.Id == VoiceProviderIds.SherpaOnnx);
        Assert.Equal(VoiceProviderRuntimeIds.Embedded, sherpaOnnx.Runtime);
        Assert.False(sherpaOnnx.Enabled);
        Assert.True(sherpaOnnx.VisibleInSettings);
        Assert.False(sherpaOnnx.Selectable);
        Assert.Equal(string.Empty, sherpaOnnx.Settings.Single(s => s.Key == VoiceProviderSettingKeys.ModelPath).DefaultValue);

        var minimax = Assert.Single(catalog.TextToSpeechProviders, p => p.Id == VoiceProviderIds.MiniMax);
        Assert.Equal("MiniMax", minimax.Name);
        Assert.NotNull(minimax.TextToSpeechWebSocket);
        Assert.Equal("wss://api.minimax.io/ws/v1/t2a_v2", minimax.TextToSpeechWebSocket!.EndpointTemplate);
        Assert.Equal("Authorization", minimax.TextToSpeechWebSocket.AuthenticationHeaderName);
        Assert.Equal(VoiceTextToSpeechResponseModes.HexJsonString, minimax.TextToSpeechWebSocket.ResponseAudioMode);
        Assert.Contains("\"event\": \"task_start\"", minimax.TextToSpeechWebSocket.StartMessageTemplate);
        Assert.Contains("\"event\": \"task_continue\"", minimax.TextToSpeechWebSocket.ContinueMessageTemplate);
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
        Assert.NotNull(elevenLabs.TextToSpeechWebSocket);
        Assert.Equal(
            "wss://api.elevenlabs.io/v1/text-to-speech/{{voiceId}}/stream-input?model_id={{model}}&output_format=mp3_44100_128&auto_mode=true",
            elevenLabs.TextToSpeechWebSocket!.EndpointTemplate);
        Assert.Equal("xi-api-key", elevenLabs.TextToSpeechWebSocket.AuthenticationHeaderName);
        Assert.Equal(string.Empty, elevenLabs.TextToSpeechWebSocket.AuthenticationScheme);
        Assert.Equal(string.Empty, elevenLabs.TextToSpeechWebSocket.ConnectSuccessEventName);
        Assert.Equal(string.Empty, elevenLabs.TextToSpeechWebSocket.StartSuccessEventName);
        Assert.Contains("\"xi_api_key\": {{apiKey}}", elevenLabs.TextToSpeechWebSocket.StartMessageTemplate);
        Assert.Contains("\"try_trigger_generation\": true", elevenLabs.TextToSpeechWebSocket.ContinueMessageTemplate);
        Assert.Contains("{{textWithTrailingSpace}}", elevenLabs.TextToSpeechWebSocket.ContinueMessageTemplate);
        Assert.Equal("{ \"text\": \"\" }", elevenLabs.TextToSpeechWebSocket.FinishMessageTemplate);
        Assert.Equal(VoiceTextToSpeechResponseModes.Base64JsonString, elevenLabs.TextToSpeechWebSocket.ResponseAudioMode);
        Assert.Equal("audio", elevenLabs.TextToSpeechWebSocket.ResponseAudioJsonPath);
        Assert.Equal("isFinal", elevenLabs.TextToSpeechWebSocket.FinalFlagJsonPath);
        var elevenLabsModelSetting = elevenLabs.Settings.Single(s => s.Key == VoiceProviderSettingKeys.Model);
        Assert.Equal("eleven_multilingual_v2", elevenLabsModelSetting.DefaultValue);
        Assert.Contains("eleven_flash_v2_5", elevenLabsModelSetting.Options);
        Assert.Contains("eleven_turbo_v2_5", elevenLabsModelSetting.Options);
        Assert.Equal("6aDn1KB0hjpdcocrUkmq", elevenLabs.Settings.Single(s => s.Key == VoiceProviderSettingKeys.VoiceId).DefaultValue);
        var elevenLabsVoiceSettingsJson = elevenLabs.Settings.Single(s => s.Key == VoiceProviderSettingKeys.VoiceSettingsJson);
        Assert.False(elevenLabsVoiceSettingsJson.Required);
        Assert.True(elevenLabsVoiceSettingsJson.JsonValue);
        Assert.Contains("\"voice_settings\":", elevenLabsVoiceSettingsJson.DefaultValue);
        Assert.Contains("\"speed\": 0.9", elevenLabsVoiceSettingsJson.DefaultValue);
    }
}
