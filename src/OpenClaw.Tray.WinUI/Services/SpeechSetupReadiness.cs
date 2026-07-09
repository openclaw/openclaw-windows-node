using OpenClaw.Shared.Audio;
using OpenClaw.Shared.Capabilities;

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
}
