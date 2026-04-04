using Windows.Media.SpeechRecognition;

namespace OpenClawTray.Services.Voice;

internal sealed class VoiceSpeechToTextRouteResources
{
    public VoiceCaptureService? CaptureService { get; init; }
    public SpeechRecognizer? SpeechRecognizer { get; init; }
}
