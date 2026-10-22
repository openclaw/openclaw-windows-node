using OpenClaw.Chat;
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

internal sealed record ChatQueuedSendRequest(
    string Id,
    string SendRunId,
    string ThreadId,
    string Text,
    string DisplayText,
    string LocalNonce,
    IReadOnlyList<ChatAttachment>? Attachments,
    ChatLifecycleCommandKind? LifecycleCommand = null,
    string? TimelineText = null,
    IReadOnlyList<ChatAttachmentPresentation>? AttachmentPresentations = null,
    string AttachmentCorrelationSignature = "")
{
    internal ChatInputAction Action { get; init; }
    internal bool UseInputCustody { get; init; }
    internal ChatInputLease? Origin { get; init; }
    internal DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    internal bool RequiresInputConsumption => !Text.TrimStart().StartsWith('/');
    // The final timeline entry text never carries the cosmetic attachment
    // chip lines that DisplayText uses for the queued-message preview list
    // — structured Attachments render those instead. Falls back to
    // DisplayText for legacy callers (e.g. lifecycle commands) that never
    // set TimelineText explicitly.
    public string EffectiveTimelineText => TimelineText ?? DisplayText;
}

internal sealed record ChatQueuedSendDispatch(
    ChatQueuedSendRequest Request,
    string? SessionId,
    long ConnectionGeneration,
    long ResetVersion,
    long StartedLifecycleSequence,
    long StartedRunStartSequence,
    bool StartedDirectly)
{
    internal long TransportEpoch { get; init; }
}

internal enum AssistantQueueFrameDisposition
{
    Render,
    Drop,
}

internal enum ChatAdmissionOutcome
{
    Accepted,
    Deferred,
    Rejected,
    Canceled,
    Other,
}

internal static class ChatSendQueuePolicy
{
    internal static readonly TimeSpan DrainDelay = TimeSpan.FromMilliseconds(100);

    internal static bool CanSendDirectly(
        bool hasActiveRun,
        bool turnActive,
        bool hasPendingMessages) =>
        !hasActiveRun && !turnActive && !hasPendingMessages;

    internal static bool CanStartNext(
        bool requireConnected,
        ConnectionStatus status,
        bool hasActiveRun,
        bool turnActive,
        bool hasSendingMessage) =>
        (!requireConnected || status == ConnectionStatus.Connected)
        && !hasActiveRun
        && !turnActive
        && !hasSendingMessage;

    internal static bool IsInFlightAdmissionStatus(string? status) =>
        string.Equals(status, "in_flight", StringComparison.OrdinalIgnoreCase);

    internal static bool IsCanceledAdmissionStatus(string? status) =>
        string.Equals(status, "aborted", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase);

    internal static ChatAdmissionOutcome ClassifyAdmission(
        ChatSendResult result)
    {
        if (IsInFlightAdmissionStatus(result.Status))
            return ChatAdmissionOutcome.Accepted;
        if (result.IsTerminalFailure)
        {
            return IsCanceledAdmissionStatus(result.Status)
                ? ChatAdmissionOutcome.Canceled
                : ChatAdmissionOutcome.Rejected;
        }
        if (string.IsNullOrWhiteSpace(result.Status) ||
            string.Equals(result.Status, "started", StringComparison.OrdinalIgnoreCase))
        {
            return ChatAdmissionOutcome.Accepted;
        }
        return ChatAdmissionOutcome.Other;
    }

}
