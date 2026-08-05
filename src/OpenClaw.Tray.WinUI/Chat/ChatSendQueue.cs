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
    int DeferredAdmissionRetryCount = 0,
    DateTimeOffset? DeferredAdmissionRetryAfter = null,
    ChatLifecycleCommandKind? LifecycleCommand = null);

internal sealed record ChatQueuedSendDispatch(
    ChatQueuedSendRequest Request,
    string? SessionId,
    long ConnectionGeneration,
    long ResetVersion,
    long StartedLifecycleSequence,
    long StartedRunStartSequence,
    bool StartedDirectly);

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
    internal const int MaxDeferredAdmissionRetries = 8;
    internal static readonly TimeSpan DrainDelay = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan MaxDeferredAdmissionRetryDelay = TimeSpan.FromSeconds(1);

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

    internal static bool IsDeferredAdmissionStatus(string? status) =>
        string.Equals(status, "in_flight", StringComparison.OrdinalIgnoreCase);

    internal static bool IsCanceledAdmissionStatus(string? status) =>
        string.Equals(status, "aborted", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase);

    internal static ChatAdmissionOutcome ClassifyAdmission(
        ChatSendResult result)
    {
        if (IsDeferredAdmissionStatus(result.Status))
            return ChatAdmissionOutcome.Deferred;
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

    internal static TimeSpan DeferredAdmissionRetryDelay(int retryCount)
    {
        var exponent = Math.Min(Math.Max(retryCount - 1, 0), 5);
        var delayMs = DrainDelay.TotalMilliseconds * (1 << exponent);
        return TimeSpan.FromMilliseconds(
            Math.Min(delayMs, MaxDeferredAdmissionRetryDelay.TotalMilliseconds));
    }
}
