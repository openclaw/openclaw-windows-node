using System;
using OpenClaw.Shared;

namespace OpenClawTray.Services.Voice;

internal static class VoiceSpeechToTextRouteFactory
{
    public static IVoiceSpeechToTextRoute Create(
        VoiceProviderOption provider,
        IOpenClawLogger logger)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(logger);

        return ResolveRouteKind(provider) switch
        {
            VoiceSpeechToTextRouteKind.WindowsMedia => new WindowsMediaSpeechToTextRoute(logger),
            VoiceSpeechToTextRouteKind.Streaming => new AudioGraphStreamingSpeechToTextRoute(logger),
            VoiceSpeechToTextRouteKind.SherpaOnnx => new SherpaOnnxSpeechToTextRoute(logger),
            _ => new WindowsMediaSpeechToTextRoute(logger)
        };
    }

    public static VoiceSpeechToTextRouteKind ResolveRouteKind(VoiceProviderOption provider)
    {
        return VoiceSpeechToTextRouteResolver.ResolveRouteKind(provider);
    }
}
