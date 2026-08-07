using OpenClawTray.Services.VoiceAssistant;

namespace OpenClawTray.Presentation;

public interface IVoiceAssistantSettingsEnvironment
{
    event EventHandler? Changed;

    VoiceAssistantState RuntimeState { get; }
    VoiceAssistantReadinessResult GetReadiness(string wakePhrase);
    string GetString(string key);
}
