using OpenClaw.Shared;

namespace OpenClawTray.Services.Voice;

public enum VoiceConversationDirection
{
    Outgoing,
    Incoming
}

public sealed class VoiceConversationTurnEventArgs : EventArgs
{
    public VoiceConversationDirection Direction { get; set; }
    public string SessionKey { get; set; } = "main";
    public string Message { get; set; } = "";
    public VoiceActivationMode Mode { get; set; } = VoiceActivationMode.Off;
}

public sealed class VoiceTranscriptDraftEventArgs : EventArgs
{
    public string SessionKey { get; set; } = "main";
    public string Text { get; set; } = "";
    public bool Clear { get; set; }
    public VoiceActivationMode Mode { get; set; } = VoiceActivationMode.Off;
}
