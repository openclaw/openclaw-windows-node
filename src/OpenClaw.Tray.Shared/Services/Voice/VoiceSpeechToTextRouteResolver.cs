using OpenClaw.Shared;

namespace OpenClawTray.Services.Voice;

public static class VoiceSpeechToTextRouteResolver
{
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
