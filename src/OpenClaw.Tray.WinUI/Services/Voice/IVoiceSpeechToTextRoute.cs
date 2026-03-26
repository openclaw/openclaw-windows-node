using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;

namespace OpenClawTray.Services.Voice;

internal interface IVoiceSpeechToTextRoute
{
    VoiceSpeechToTextRouteKind Kind { get; }

    Task<VoiceSpeechToTextRouteResources> StartAsync(
        VoiceProviderOption provider,
        VoiceSettings settings,
        CancellationToken cancellationToken);
}
