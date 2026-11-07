using OpenClaw.Shared;
using OpenClaw.Shared.Audio;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class SpeechSetupReadinessTests
{
    [Fact]
    public void FreshSettings_EnablingTtsDoesNotEnableAutomaticReadAloud()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path)
        {
            NodeTtsEnabled = true,
        };

        Assert.False(settings.VoiceTtsEnabled);
        Assert.False(SpeechSetupReadiness.IsAutomaticChatTtsEnabled(settings));
        Assert.True(SpeechSetupReadiness.IsChatTtsPlaybackReady(settings));

        settings.SaveOrThrow();
        var restored = new SettingsManager(temp.Path);
        Assert.False(SpeechSetupReadiness.IsAutomaticChatTtsEnabled(restored));
        Assert.True(SpeechSetupReadiness.IsChatTtsPlaybackReady(restored));
    }

    [Fact]
    public void SavedSettingsWithoutReadAloudPreference_DefaultToQuiet()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """{"NodeTtsEnabled":true}""");

        var settings = new SettingsManager(temp.Path);

        Assert.False(settings.VoiceTtsEnabled);
        Assert.False(SpeechSetupReadiness.IsAutomaticChatTtsEnabled(settings));
        Assert.True(SpeechSetupReadiness.IsChatTtsPlaybackReady(settings));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SavedReadAloudPreference_IsPreservedAndRequiresTtsCapability(
        bool automaticReadAloud,
        bool ttsCapability)
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), new SettingsData
        {
            VoiceTtsEnabled = automaticReadAloud,
            NodeTtsEnabled = ttsCapability,
        }.ToJson());

        var settings = new SettingsManager(temp.Path);

        Assert.Equal(automaticReadAloud, settings.VoiceTtsEnabled);
        Assert.Equal(automaticReadAloud && ttsCapability,
            SpeechSetupReadiness.IsAutomaticChatTtsEnabled(settings));
        Assert.Equal(ttsCapability, SpeechSetupReadiness.IsChatTtsPlaybackReady(settings));

        settings.SaveOrThrow();
        var restored = new SettingsManager(temp.Path);
        Assert.Equal(automaticReadAloud, restored.VoiceTtsEnabled);
        Assert.Equal(automaticReadAloud && ttsCapability,
            SpeechSetupReadiness.IsAutomaticChatTtsEnabled(restored));
        Assert.Equal(ttsCapability, SpeechSetupReadiness.IsChatTtsPlaybackReady(restored));
    }

    [Fact]
    public void NodeSttEnabled_VoiceServiceNull_ModelPresent_DoesNotRequireSetup()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path)
        {
            NodeSttEnabled = true,
            SttModelName = "base",
        };
        var models = new WhisperModelManager(temp.Path, NullLogger.Instance);
        File.WriteAllBytes(models.GetModelPath("base"), [0]);

        var needsWarning = settings.NodeSttEnabled
            && SpeechSetupReadiness.IsConfiguredSttModelSetupRequired(
                settings,
                temp.Path,
                NullLogger.Instance);

        Assert.False(needsWarning);
    }

    [Theory]
    [InlineData("base")]
    [InlineData("unknown")]
    public void NodeSttEnabled_VoiceServiceNull_MissingOrUnknownModel_RequiresSetup(string modelName)
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path)
        {
            NodeSttEnabled = true,
            SttModelName = modelName,
        };

        var needsWarning = settings.NodeSttEnabled
            && SpeechSetupReadiness.IsConfiguredSttModelSetupRequired(
                settings,
                temp.Path,
                NullLogger.Instance);

        Assert.True(needsWarning);
    }
}
