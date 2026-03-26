using System;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using Windows.Media.SpeechRecognition;

namespace OpenClawTray.Services.Voice;

internal sealed class WindowsMediaSpeechToTextRoute : IVoiceSpeechToTextRoute
{
    private static readonly TimeSpan InitialSilenceTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BabbleTimeout = TimeSpan.FromSeconds(4);

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
        recognizer.Timeouts.InitialSilenceTimeout = InitialSilenceTimeout;
        recognizer.Timeouts.BabbleTimeout = BabbleTimeout;
        recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "always-on-dictation"));

        var compilation = await recognizer.CompileConstraintsAsync();
        if (compilation.Status != SpeechRecognitionResultStatus.Success)
        {
            recognizer.Dispose();
            throw new InvalidOperationException($"Speech recognizer unavailable: {compilation.Status}");
        }

        _logger.Debug(
            $"Speech recognizer compiled successfully ({compilation.Status}); endSilenceMs={recognizer.Timeouts.EndSilenceTimeout.TotalMilliseconds:0}; initialSilenceMs={recognizer.Timeouts.InitialSilenceTimeout.TotalMilliseconds:0}; babbleMs={recognizer.Timeouts.BabbleTimeout.TotalMilliseconds:0}");
        return recognizer;
    }
}
