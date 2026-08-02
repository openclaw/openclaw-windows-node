using OpenClaw.Shared;
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

    public static bool IsConfiguredSttModelSetupRequired(SettingsManager settings) =>
        IsConfiguredSttModelSetupRequired(
            settings,
            SettingsManager.SettingsDirectoryPath,
            new AppLogger());

    internal static bool IsConfiguredSttModelSetupRequired(
        SettingsManager settings,
        string dataDirectory,
        IOpenClawLogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        var modelName = settings.SttModelName?.Trim();
        if (string.IsNullOrWhiteSpace(modelName)
            || !WhisperModelManager.AvailableModels.Any(model =>
                string.Equals(model.Name, modelName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var models = new WhisperModelManager(dataDirectory, logger);
        return !models.IsModelDownloaded(modelName);
    }
}
