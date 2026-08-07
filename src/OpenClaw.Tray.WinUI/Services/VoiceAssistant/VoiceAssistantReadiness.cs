using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;

namespace OpenClawTray.Services.VoiceAssistant;

public enum VoiceAssistantReadinessReason
{
    Ready,
    SttDisabled,
    SttModelMissing,
    TtsDisabled,
    UnsupportedTtsProvider,
    PiperVoiceMissing,
    ElevenLabsApiKeyMissing,
    ElevenLabsVoiceMissing,
    WakePhraseInvalid
}

public readonly record struct VoiceAssistantReadinessResult(
    bool IsReady,
    VoiceAssistantReadinessReason Reason);

public readonly record struct VoiceAssistantReadinessInput(
    bool SttEnabled,
    bool SttModelDownloaded,
    bool TtsEnabled,
    string? TtsProvider,
    bool PiperVoiceDownloaded,
    bool HasElevenLabsApiKey,
    bool HasElevenLabsVoiceId,
    string? WakePhrase);

public static class VoiceAssistantReadiness
{
    public static VoiceAssistantReadinessResult Evaluate(VoiceAssistantReadinessInput input)
    {
        if (!input.SttEnabled)
            return NotReady(VoiceAssistantReadinessReason.SttDisabled);
        if (!input.SttModelDownloaded)
            return NotReady(VoiceAssistantReadinessReason.SttModelMissing);
        if (!input.TtsEnabled)
            return NotReady(VoiceAssistantReadinessReason.TtsDisabled);

        var provider = TtsCapability.ResolveProvider(null, input.TtsProvider);
        if (string.Equals(provider, TtsCapability.PiperProvider, StringComparison.Ordinal))
        {
            if (!input.PiperVoiceDownloaded)
                return NotReady(VoiceAssistantReadinessReason.PiperVoiceMissing);
        }
        else if (string.Equals(provider, TtsCapability.ElevenLabsProvider, StringComparison.Ordinal))
        {
            if (!input.HasElevenLabsApiKey)
                return NotReady(VoiceAssistantReadinessReason.ElevenLabsApiKeyMissing);
            if (!input.HasElevenLabsVoiceId)
                return NotReady(VoiceAssistantReadinessReason.ElevenLabsVoiceMissing);
        }
        else if (!string.Equals(provider, TtsCapability.WindowsProvider, StringComparison.Ordinal))
        {
            return NotReady(VoiceAssistantReadinessReason.UnsupportedTtsProvider);
        }

        if (!VoiceAssistantSettingsPolicy.TryNormalizeWakePhrase(input.WakePhrase, out _))
            return NotReady(VoiceAssistantReadinessReason.WakePhraseInvalid);

        return new VoiceAssistantReadinessResult(true, VoiceAssistantReadinessReason.Ready);
    }

    private static VoiceAssistantReadinessResult NotReady(VoiceAssistantReadinessReason reason) =>
        new(false, reason);
}
