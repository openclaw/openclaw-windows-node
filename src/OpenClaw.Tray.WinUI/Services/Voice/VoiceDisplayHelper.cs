using OpenClaw.Shared;

namespace OpenClawTray.Services.Voice;

public static class VoiceDisplayHelper
{
    public static string GetModeLabel(VoiceActivationMode mode)
    {
        return mode switch
        {
            VoiceActivationMode.VoiceWake => "Voice Wake",
            VoiceActivationMode.TalkMode => "Talk Mode",
            _ => "Off"
        };
    }

    public static string GetStateLabel(VoiceRuntimeState state)
    {
        return state switch
        {
            VoiceRuntimeState.Arming => "Starting",
            VoiceRuntimeState.ListeningForVoiceWake => "Listening",
            VoiceRuntimeState.ListeningContinuously => "Listening",
            VoiceRuntimeState.RecordingUtterance => "Recording",
            VoiceRuntimeState.SubmittingAudio => "Sending",
            VoiceRuntimeState.AwaitingResponse => "Waiting for reply",
            VoiceRuntimeState.PlayingResponse => "Speaking",
            VoiceRuntimeState.Paused => "Paused",
            VoiceRuntimeState.Error => "Error",
            VoiceRuntimeState.Idle => "Idle",
            _ => "Stopped"
        };
    }

    public static string GetRuntimeLabel(VoiceStatusInfo status)
    {
        if (status.State == VoiceRuntimeState.Paused)
        {
            return $"{GetModeLabel(status.Mode)} (Paused)";
        }

        if (status.Running)
        {
            return $"{GetModeLabel(status.Mode)} ({GetStateLabel(status.State)})";
        }

        return "Off";
    }
}
