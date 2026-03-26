using System;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using Windows.Media.SpeechRecognition;

namespace OpenClawTray.Services.Voice;

internal sealed class WindowsMediaSpeechToTextRoute : IVoiceSpeechToTextRoute
{
    private readonly IOpenClawLogger _logger;

    public WindowsMediaSpeechToTextRoute(IOpenClawLogger logger)
    {
        _logger = logger;
    }

    public VoiceSpeechToTextRouteKind Kind => VoiceSpeechToTextRouteKind.WindowsMedia;

    public async Task<VoiceSpeechToTextRouteResources> StartAsync(
        VoiceProviderOption provider,
        VoiceSettings settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return new VoiceSpeechToTextRouteResources
        {
            SpeechRecognizer = await CreateRecognizerAsync(settings)
        };
    }

    public async Task<SpeechRecognizer> CreateRecognizerAsync(VoiceSettings settings)
    {
        var recognizer = new SpeechRecognizer();
        recognizer.Timeouts.EndSilenceTimeout = TimeSpan.FromMilliseconds(settings.TalkMode.EndSilenceMs);
        recognizer.Timeouts.InitialSilenceTimeout = TimeSpan.FromSeconds(10);
        recognizer.Timeouts.BabbleTimeout = TimeSpan.FromSeconds(4);
        recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "always-on-dictation"));

        var compilation = await recognizer.CompileConstraintsAsync();
        if (compilation.Status != SpeechRecognitionResultStatus.Success)
        {
            recognizer.Dispose();
            throw new InvalidOperationException($"Speech recognizer unavailable: {compilation.Status}");
        }

        _logger.Info($"Speech recognizer compiled successfully ({compilation.Status})");
        return recognizer;
    }
}
