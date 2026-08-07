using OpenClaw.Shared.Capabilities;
using OpenClawTray.Services.VoiceAssistant;

namespace OpenClaw.Tray.Tests.Services;

public sealed class VoiceAssistantReadinessTests
{
    public static TheoryData<VoiceAssistantReadinessInput, VoiceAssistantReadinessReason> MissingPrerequisites =>
        new()
        {
            { ReadyInput() with { SttEnabled = false }, VoiceAssistantReadinessReason.SttDisabled },
            { ReadyInput() with { SttModelDownloaded = false }, VoiceAssistantReadinessReason.SttModelMissing },
            { ReadyInput() with { TtsEnabled = false }, VoiceAssistantReadinessReason.TtsDisabled },
            {
                ReadyInput() with
                {
                    TtsProvider = TtsCapability.PiperProvider,
                    PiperVoiceDownloaded = false
                },
                VoiceAssistantReadinessReason.PiperVoiceMissing
            },
            {
                ReadyInput() with
                {
                    TtsProvider = TtsCapability.ElevenLabsProvider,
                    HasElevenLabsApiKey = false
                },
                VoiceAssistantReadinessReason.ElevenLabsApiKeyMissing
            },
            {
                ReadyInput() with
                {
                    TtsProvider = TtsCapability.ElevenLabsProvider,
                    HasElevenLabsVoiceId = false
                },
                VoiceAssistantReadinessReason.ElevenLabsVoiceMissing
            },
            { ReadyInput() with { WakePhrase = "too many words in this phrase" }, VoiceAssistantReadinessReason.WakePhraseInvalid }
        };

    [Fact]
    public void Evaluate_ReadyWindowsConfiguration_ReturnsReady()
    {
        var result = VoiceAssistantReadiness.Evaluate(ReadyInput());

        Assert.True(result.IsReady);
        Assert.Equal(VoiceAssistantReadinessReason.Ready, result.Reason);
    }

    [Theory]
    [MemberData(nameof(MissingPrerequisites))]
    public void Evaluate_MissingPrerequisite_ReturnsSpecificReason(
        VoiceAssistantReadinessInput input,
        VoiceAssistantReadinessReason expected)
    {
        var result = VoiceAssistantReadiness.Evaluate(input);

        Assert.False(result.IsReady);
        Assert.Equal(expected, result.Reason);
    }

    private static VoiceAssistantReadinessInput ReadyInput() =>
        new(
            SttEnabled: true,
            SttModelDownloaded: true,
            TtsEnabled: true,
            TtsProvider: TtsCapability.WindowsProvider,
            PiperVoiceDownloaded: true,
            HasElevenLabsApiKey: true,
            HasElevenLabsVoiceId: true,
            WakePhrase: "OpenClaw");
}
