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
    ChatLifecycleCommandKind? LifecycleCommand = null,
    string? TimelineText = null,
    IReadOnlyList<ChatAttachmentPresentation>? AttachmentPresentations = null,
    string AttachmentCorrelationSignature = "",
    string? GatewayIdempotencyKey = null,
    bool AdmissionOutcomeUncertain = false,
    string? AdmissionUncertainSessionId = null,
    long? AdmissionUncertainTurnGeneration = null)
{
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
    bool StartedDirectly,
    long StartedTurnGeneration = 0);

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

    internal static bool IsDeferredAdmission(ChatSendResult result) =>
        IsDeferredAdmissionStatus(result.Status) &&
        !(result.Cached && !string.IsNullOrWhiteSpace(result.RunId));

    internal static bool IsCanceledAdmissionStatus(string? status) =>
        string.Equals(status, "aborted", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase);

    internal static ChatAdmissionOutcome ClassifyAdmission(
        ChatSendResult result)
    {
        if (IsDeferredAdmission(result))
            return ChatAdmissionOutcome.Deferred;
        if (IsCanceledAdmissionStatus(result.Status))
            return ChatAdmissionOutcome.Canceled;
        if (result.IsTerminalFailure)
        {
            return string.IsNullOrWhiteSpace(result.RunId)
                ? ChatAdmissionOutcome.Rejected
                : ChatAdmissionOutcome.Accepted;
        }
        if (!string.IsNullOrWhiteSpace(result.RunId))
            return ChatAdmissionOutcome.Accepted;
        if (result.Cached &&
            !string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(result.Status, "started", StringComparison.OrdinalIgnoreCase))
        {
            return ChatAdmissionOutcome.Other;
        }
        if (string.IsNullOrWhiteSpace(result.Status) ||
            string.Equals(result.Status, "started", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return ChatAdmissionOutcome.Accepted;
        }
        return ChatAdmissionOutcome.Other;
    }

    internal static ChatAdmissionOutcome ClassifyDraftAdmission(
        ChatSendResult result)
    {
        if (IsDeferredAdmissionStatus(result.Status))
        {
            return result.Cached && !string.IsNullOrWhiteSpace(result.RunId)
                ? ChatAdmissionOutcome.Accepted
                : ChatAdmissionOutcome.Other;
        }

        if (IsCanceledAdmissionStatus(result.Status))
            return ChatAdmissionOutcome.Canceled;

        if (result.IsTerminalFailure)
        {
            if (!string.IsNullOrWhiteSpace(result.RunId))
                return ChatAdmissionOutcome.Accepted;
            return ChatAdmissionOutcome.Rejected;
        }

        if (string.Equals(result.Status, "started", StringComparison.OrdinalIgnoreCase))
            return ChatAdmissionOutcome.Accepted;
        if (string.IsNullOrWhiteSpace(result.Status) &&
            !string.IsNullOrWhiteSpace(result.RunId))
        {
            return ChatAdmissionOutcome.Accepted;
        }
        if (result.Cached &&
            (string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(result.Status, "in_flight", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(result.RunId))
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
