using OpenClaw.Shared;

namespace OpenClawTray.Services.VoiceAssistant;

public enum VoiceAssistantSendDisposition
{
    Direct,
    Queued,
    Untrackable,
    Terminated
}

public sealed record VoiceAssistantTurnReceipt(
    VoiceAssistantSendDisposition Disposition,
    string SessionKey,
    string LocalMessageId,
    string? GatewayRunId,
    int? PreSendSequence);

public readonly record struct VoiceAssistantAvailability(
    bool IsUsable,
    string? SessionKey,
    bool CanSendDirectly,
    string? ActiveRunId);

public sealed record VoiceAssistantTurnInvalidation(
    string SessionKey,
    string GatewayRunId,
    string LocalMessageId);

public interface IVoiceAssistantChatTurnClient
{
    event Action? ReadinessChanged;
    event Action<VoiceAssistantTurnInvalidation>? TurnInvalidated;

    string? GetReadySessionKey();
    VoiceAssistantAvailability GetAvailability();

    Task<VoiceAssistantTurnReceipt> SendAsync(
        string sessionKey,
        string request,
        CancellationToken cancellationToken);

    Task CancelAsync(VoiceAssistantTurnReceipt receipt, CancellationToken cancellationToken);

    bool IsTurnInvalidated(VoiceAssistantTurnReceipt receipt);

    bool TryTakeBufferedResponse(VoiceAssistantTurnReceipt receipt, out string responseText);

    bool IsResponseForTurn(VoiceAssistantTurnReceipt receipt, OpenClawNotification notification);
}

public interface IVoiceAssistantSpeaker
{
    Task SpeakAsync(string text, CancellationToken cancellationToken);
}
