namespace OpenClawTray.Services.VoiceAssistant;

public sealed class VoiceAssistantInput : IVoiceAssistantInput, IDisposable
{
    private readonly VoiceService _voiceService;

    public VoiceAssistantInput(VoiceService voiceService)
    {
        _voiceService = voiceService;
        _voiceService.UtteranceCompleted += OnUtteranceCompleted;
    }

    public event Action<string>? UtteranceCompleted;

    public event Action? CaptureAvailable
    {
        add => _voiceService.CaptureAvailable += value;
        remove => _voiceService.CaptureAvailable -= value;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _voiceService.StartWakeListeningAsync();
    }

    public Task StopAsync() => _voiceService.StopAsync();

    private void OnUtteranceCompleted(OpenClaw.Shared.Audio.UtteranceResult result) =>
        UtteranceCompleted?.Invoke(result.Text);

    public void Dispose() => _voiceService.UtteranceCompleted -= OnUtteranceCompleted;
}
