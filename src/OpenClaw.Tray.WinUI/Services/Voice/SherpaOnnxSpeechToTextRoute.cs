using System;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;

namespace OpenClawTray.Services.Voice;

internal sealed class SherpaOnnxSpeechToTextRoute : IVoiceSpeechToTextRoute
{
    private readonly IOpenClawLogger _logger;

    public SherpaOnnxSpeechToTextRoute(IOpenClawLogger logger)
    {
        _logger = logger;
    }

    public VoiceSpeechToTextRouteKind Kind => VoiceSpeechToTextRouteKind.SherpaOnnx;

    public Task<VoiceSpeechToTextRouteResources> StartAsync(
        VoiceProviderOption provider,
        VoiceSettings settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.Info($"Selected embedded sherpa-onnx STT route for provider '{provider.Name}'.");
        throw new NotSupportedException(
            "The sherpa-onnx STT route is not implemented yet. This route will require a user-provided local model bundle.");
    }
}
