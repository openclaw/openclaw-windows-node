using OpenClaw.Shared;
using OpenClaw.Shared.Audio;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class SpeechSetupReadinessTests
{
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
