using System.Globalization;
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

public static class GatewaySpeechPresentation
{
    public static string FailureKey(GatewaySpeechFailure failure) => failure switch
    {
        GatewaySpeechFailure.Disconnected => "GatewayVoice_Disconnected",
        GatewaySpeechFailure.Unsupported => "GatewayVoice_Unsupported",
        GatewaySpeechFailure.PermissionRequired => "GatewayVoice_PermissionRequired",
        GatewaySpeechFailure.ProviderUnavailable => "GatewayVoice_ProviderUnavailable",
        GatewaySpeechFailure.TextTooLong => "GatewayVoice_TextTooLong",
        GatewaySpeechFailure.InvalidAudio => "GatewayVoice_InvalidAudio",
        GatewaySpeechFailure.UnsupportedAudio => "GatewayVoice_UnsupportedAudio",
        GatewaySpeechFailure.TimedOut => "GatewayVoice_TimedOut",
        GatewaySpeechFailure.PlaybackFailed => "GatewayVoice_PlaybackFailed",
        _ => "GatewayVoice_SynthesisFailed"
    };

    public static string Format(GatewaySpeechPlaybackState state, Func<string, string> localize)
    {
        if (state.Phase == GatewaySpeechPhase.Idle) return string.Empty;
        if (state.Phase == GatewaySpeechPhase.Failed)
            return localize(FailureKey(state.Failure ?? GatewaySpeechFailure.SynthesisFailed));
        if (state.Phase == GatewaySpeechPhase.Synthesizing) return localize("GatewayVoice_Synthesizing");
        var fallback = state.ConfiguredProvider is not null && state.Provider != state.ConfiguredProvider;
        return fallback
            ? string.Format(CultureInfo.CurrentCulture, localize("GatewayVoice_ActualFallback"), state.Provider, state.ConfiguredProvider)
            : string.Format(CultureInfo.CurrentCulture, localize("GatewayVoice_ActualProvider"), state.Provider);
    }
}
