namespace OpenClawTray.Services.VoiceAssistant;

public enum VoiceAssistantState
{
    Off,
    Unavailable,
    Starting,
    WakeListening,
    Dispatching,
    WaitingForReply,
    Speaking,
    Error
}
