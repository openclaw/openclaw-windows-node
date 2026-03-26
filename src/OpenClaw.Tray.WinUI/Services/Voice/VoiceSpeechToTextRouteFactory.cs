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
        ArgumentNullException.ThrowIfNull(provider);

        if (string.Equals(provider.Id, VoiceProviderIds.SherpaOnnx, StringComparison.OrdinalIgnoreCase))
        {
            return VoiceSpeechToTextRouteKind.SherpaOnnx;
        }

        if (string.Equals(provider.Runtime, VoiceProviderRuntimeIds.Streaming, StringComparison.OrdinalIgnoreCase))
        {
            return VoiceSpeechToTextRouteKind.Streaming;
        }

        if (string.Equals(provider.Runtime, VoiceProviderRuntimeIds.Embedded, StringComparison.OrdinalIgnoreCase))
        {
            return VoiceSpeechToTextRouteKind.SherpaOnnx;
        }

        return VoiceSpeechToTextRouteKind.WindowsMedia;
    }
}
