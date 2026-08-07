using OpenClaw.Shared.Audio;
using OpenClaw.Shared.Capabilities;
using OpenClawTray.Services.VoiceAssistant;

namespace OpenClawTray.Services;

public static class SpeechSetupReadiness
{
    public static bool IsChatTtsPlaybackReady(SettingsManager? settings)
    {
        return settings?.NodeTtsEnabled == true;
    }

    public static bool IsAutomaticChatTtsEnabled(SettingsManager? settings)
    {
        return settings?.VoiceTtsEnabled == true &&
            IsChatTtsPlaybackReady(settings);
    }

    public static bool IsConfiguredTtsProviderSetupRequired(SettingsManager settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var provider = TtsCapability.ResolveProvider(null, settings.TtsProvider);
        if (string.Equals(provider, TtsCapability.WindowsProvider, StringComparison.Ordinal))
            return false;

        if (string.Equals(provider, TtsCapability.PiperProvider, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(settings.TtsPiperVoiceId))
                return true;

            var voices = new PiperVoiceManager(SettingsManager.SettingsDirectoryPath, new AppLogger());
            return !voices.IsVoiceDownloaded(settings.TtsPiperVoiceId);
        }

        if (string.Equals(provider, TtsCapability.ElevenLabsProvider, StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(settings.TtsElevenLabsApiKey) ||
                string.IsNullOrWhiteSpace(settings.TtsElevenLabsVoiceId);
        }

        return true;
    }

    public static VoiceAssistantReadinessResult GetVoiceAssistantReadiness(
        SettingsManager settings,
        string? wakePhrase = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var whisperModels = new WhisperModelManager(SettingsManager.SettingsDirectoryPath, new AppLogger());
        var piperVoices = new PiperVoiceManager(SettingsManager.SettingsDirectoryPath, new AppLogger());
        var provider = TtsCapability.ResolveProvider(null, settings.TtsProvider);

        return VoiceAssistantReadiness.Evaluate(new VoiceAssistantReadinessInput(
            SttEnabled: settings.NodeSttEnabled,
            SttModelDownloaded: whisperModels.IsModelDownloaded(settings.SttModelName),
            TtsEnabled: settings.NodeTtsEnabled,
            TtsProvider: provider,
            PiperVoiceDownloaded:
                string.Equals(provider, TtsCapability.PiperProvider, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(settings.TtsPiperVoiceId) &&
                piperVoices.IsVoiceDownloaded(settings.TtsPiperVoiceId),
            HasElevenLabsApiKey: !string.IsNullOrWhiteSpace(settings.TtsElevenLabsApiKey),
            HasElevenLabsVoiceId: !string.IsNullOrWhiteSpace(settings.TtsElevenLabsVoiceId),
            WakePhrase: wakePhrase ?? settings.VoiceAssistantWakePhrase));
    }
}
