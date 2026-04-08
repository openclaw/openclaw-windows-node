using System;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;

namespace OpenClawTray.Services.Voice;

internal sealed class AudioGraphStreamingSpeechToTextRoute : IVoiceSpeechToTextRoute
{
    private readonly IOpenClawLogger _logger;

    public AudioGraphStreamingSpeechToTextRoute(IOpenClawLogger logger)
    {
        _logger = logger;
    }

    public VoiceSpeechToTextRouteKind Kind => VoiceSpeechToTextRouteKind.Streaming;

    public Task<VoiceSpeechToTextRouteResources> StartAsync(
        VoiceProviderOption provider,
        VoiceSettings settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.Info($"Selected streaming STT route for provider '{provider.Name}'.");
        throw new NotSupportedException(
            $"STT provider '{provider.Name}' is assigned to the AudioGraph streaming route, but that adapter is not implemented yet.");
    }
}
