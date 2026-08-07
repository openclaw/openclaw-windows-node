namespace OpenClawTray.Services.VoiceAssistant;

public interface IVoiceAssistantInput
{
    event Action<string>? UtteranceCompleted;
    event Action? CaptureAvailable;

    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
}
