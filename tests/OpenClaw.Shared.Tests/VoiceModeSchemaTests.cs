using OpenClaw.Shared;
using System.Text.Json;

namespace OpenClaw.Shared.Tests;

public class VoiceCommandsTests
{
    [Fact]
    public void All_ContainsExpectedCommandsInStableOrder()
    {
        Assert.Equal(
        [
            "voice.devices.list",
            "voice.settings.get",
            "voice.settings.set",
            "voice.status.get",
            "voice.start",
            "voice.stop",
            "voice.pause",
            "voice.resume",
            "voice.response.skip"
        ],
        VoiceCommands.All);
    }
}

public class VoiceSchemaDefaultsTests
{
    [Fact]
    public void VoiceSettings_Defaults_AreConcreteAndProviderAgnostic()
    {
        var settings = new VoiceSettings();

        Assert.False(settings.Enabled);
        Assert.Equal(VoiceActivationMode.Off, settings.Mode);
        Assert.True(settings.ShowRepeaterAtStartup);
        Assert.False(settings.ShowConversationToasts);
        Assert.Equal(VoiceProviderIds.Windows, settings.SpeechToTextProviderId);
        Assert.Equal(VoiceProviderIds.Windows, settings.TextToSpeechProviderId);
        Assert.Equal(16000, settings.SampleRateHz);
        Assert.Equal(80, settings.CaptureChunkMs);
        Assert.True(settings.BargeInEnabled);
        Assert.Equal("NanoWakeWord", settings.VoiceWake.Engine);
        Assert.Equal("hey_openclaw", settings.VoiceWake.ModelId);
        Assert.Equal(0.65f, settings.VoiceWake.TriggerThreshold);
        Assert.Equal(250, settings.TalkMode.MinSpeechMs);
    }

    [Fact]
    public void VoiceStatusInfo_Defaults_ToStopped()
    {
        var status = new VoiceStatusInfo();

        Assert.False(status.Available);
        Assert.False(status.Running);
        Assert.Equal(VoiceActivationMode.Off, status.Mode);
        Assert.Equal(VoiceRuntimeState.Stopped, status.State);
        Assert.False(status.VoiceWakeLoaded);
        Assert.Equal(0, status.PendingReplyCount);
        Assert.False(status.CanSkipReply);
        Assert.Null(status.CurrentReplyPreview);
        Assert.Null(status.LastError);
    }

    [Fact]
    public void VoiceEnums_Serialize_AsStrings()
    {
        var json = JsonSerializer.Serialize(new VoiceStartArgs
        {
            Mode = VoiceActivationMode.VoiceWake
        });

        Assert.Contains("\"VoiceWake\"", json);
    }

    [Fact]
    public void VoiceProviderCatalog_Defaults_ToEmptyLists()
    {
        var catalog = new VoiceProviderCatalog();

        Assert.Empty(catalog.SpeechToTextProviders);
        Assert.Empty(catalog.TextToSpeechProviders);
    }

    [Fact]
    public void VoiceProviderIds_ExposeRequiredBuiltInProviders()
    {
        Assert.Equal("windows", VoiceProviderIds.Windows);
        Assert.Equal("foundry-local", VoiceProviderIds.FoundryLocal);
        Assert.Equal("openai-whisper", VoiceProviderIds.OpenAiWhisper);
        Assert.Equal("elevenlabs-stt", VoiceProviderIds.ElevenLabsSpeechToText);
        Assert.Equal("azure-ai-speech", VoiceProviderIds.AzureAiSpeech);
        Assert.Equal("sherpa-onnx", VoiceProviderIds.SherpaOnnx);
        Assert.Equal("minimax", VoiceProviderIds.MiniMax);
        Assert.Equal("elevenlabs", VoiceProviderIds.ElevenLabs);
        Assert.Equal("endpoint", VoiceProviderSettingKeys.Endpoint);
        Assert.Equal("modelPath", VoiceProviderSettingKeys.ModelPath);
        Assert.Equal("voiceSettingsJson", VoiceProviderSettingKeys.VoiceSettingsJson);
    }

    [Fact]
    public void VoiceProviderOption_Defaults_ToVisibleAndSelectable()
    {
        var option = new VoiceProviderOption { Name = "Provider" };

        Assert.True(option.VisibleInSettings);
        Assert.True(option.Selectable);
        Assert.Equal("Provider", option.DisplayName);
        Assert.Equal(1.0, option.DisplayOpacity);
    }

    [Fact]
    public void VoiceProviderConfigurationStore_Defaults_ToEmptyProviders()
    {
        var configuration = new VoiceProviderConfigurationStore();

        Assert.Empty(configuration.Providers);
    }

    [Fact]
    public void VoiceProviderConfigurationStore_MigratesLegacyProviderCredentials()
    {
        var configuration = new VoiceProviderConfigurationStore();
        configuration.MigrateLegacyCredentials(new VoiceProviderCredentials
        {
            MiniMaxApiKey = "minimax-key",
            MiniMaxModel = "speech-2.8-turbo",
            MiniMaxVoiceId = "English_MatureBoss",
            ElevenLabsApiKey = "eleven-key",
            ElevenLabsModel = "eleven_multilingual_v2",
            ElevenLabsVoiceId = "voice-42"
        });

        Assert.Equal("minimax-key", configuration.GetValue(VoiceProviderIds.MiniMax, VoiceProviderSettingKeys.ApiKey));
        Assert.Equal("speech-2.8-turbo", configuration.GetValue(VoiceProviderIds.MiniMax, VoiceProviderSettingKeys.Model));
        Assert.Equal("English_MatureBoss", configuration.GetValue(VoiceProviderIds.MiniMax, VoiceProviderSettingKeys.VoiceId));
        Assert.Equal("eleven-key", configuration.GetValue(VoiceProviderIds.ElevenLabs, VoiceProviderSettingKeys.ApiKey));
        Assert.Equal("eleven_multilingual_v2", configuration.GetValue(VoiceProviderIds.ElevenLabs, VoiceProviderSettingKeys.Model));
        Assert.Equal("voice-42", configuration.GetValue(VoiceProviderIds.ElevenLabs, VoiceProviderSettingKeys.VoiceId));
    }
}
