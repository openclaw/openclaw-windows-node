using OpenClawTray.Chat;

namespace OpenClawTray.Services.VoiceAssistant;

public sealed class VoiceAssistantSpeaker : IVoiceAssistantSpeaker
{
    private readonly OpenClawChatCoordinator _chatCoordinator;

    public VoiceAssistantSpeaker(OpenClawChatCoordinator chatCoordinator)
    {
        _chatCoordinator = chatCoordinator;
    }

    public Task SpeakAsync(string text, CancellationToken cancellationToken) =>
        _chatCoordinator.SpeakAssistantResponseAsync(text, cancellationToken);
}
