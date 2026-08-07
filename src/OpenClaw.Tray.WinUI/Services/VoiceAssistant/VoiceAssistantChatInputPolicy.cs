namespace OpenClawTray.Services.VoiceAssistant;

public static class VoiceAssistantChatInputPolicy
{
    public const string WakeListeningReasonKey = "Chat_Composer_VoiceUnavailable_AssistantListening";
    public const string WakeListeningStatusKey = "Chat_Composer_Status_AssistantListening";

    public static bool IsVoiceInputUnavailable(VoiceAssistantState state) =>
        state == VoiceAssistantState.WakeListening;
}
