using OpenClaw.Chat;
using OpenClaw.Shared;
using System.Text.Json.Nodes;
#if !OPENCLAW_TRAY_TESTS
using OpenClawTray.Helpers;
#endif

namespace OpenClawTray.Chat;

internal static class ChatInputMessages
{
    internal static string ScopeChanged => LocalizationHelper.GetString("Chat_Input_ScopeChanged");
    internal static string Unconfirmed => LocalizationHelper.GetString("Chat_Input_Unconfirmed");
    internal static string Unavailable => LocalizationHelper.GetString("Chat_Input_Unavailable");
    internal static string ReviewRequired => LocalizationHelper.GetString("Chat_Input_ReviewRequired");
    internal static string NoReceipt => LocalizationHelper.GetString("Chat_Input_NoReceipt");
    internal static string CannotSteer => LocalizationHelper.GetString("Chat_Input_CannotSteer");
    internal static string ExternalMutationRejected => LocalizationHelper.GetString("Chat_Input_ExternalMutationRejected");
    internal static string RecoveryUnavailable => LocalizationHelper.GetString("Chat_Input_RecoveryUnavailable");
    internal static string QueueActionRejected => LocalizationHelper.GetString("Chat_Input_QueueActionRejected");
    internal static string AccountChanged => LocalizationHelper.GetString("Chat_Input_AccountChanged");
    internal static string Interrupted => LocalizationHelper.GetString("Chat_Input_Interrupted");
    internal static string Cancelled => LocalizationHelper.GetString("Chat_Input_Cancelled");
}

internal sealed record ChatProjectionContext(
    string? MainSessionKey,
    bool HasHandshakeSnapshot,
    long TransportEpoch = 0);

internal readonly record struct ChatRuntimeGeneration(
    long ConnectionGeneration,
    long ResetGeneration,
    long TransportEpoch = 0);

internal sealed record ChatInputLease(
    object ConnectionOwner,
    string ThreadId,
    string? SessionId,
    ChatRuntimeGeneration Generation,
    string? RunId,
    IChatInputOrigin? ExternalOrigin = null);

internal sealed record ChatInputReceiptQuery(
    object ConnectionOwner,
    string ThreadId,
    string? SessionId,
    long TransportEpoch,
    IReadOnlyList<ChatQueuedSendRequest> Requests,
    string? AgentId);

internal sealed record ChatStatusTransition(
    ChatDataSnapshot Snapshot,
    bool Reconnected,
    bool Disconnected,
    string[] InterruptedThreads,
    long HistoryGeneration);

internal sealed record ChatSessionsTransition(
    ChatDataSnapshot Snapshot,
    string[] QueuedThreadsToDrain);

internal sealed record ChatResetTransition(
    ChatDataSnapshot Snapshot,
    string? OldSessionId,
    long ResetGeneration,
    string ThreadId,
    string[] SubmittedRunIds)
{
    internal ChatRuntimeGeneration RuntimeGeneration { get; init; }
}

internal sealed record ChatAbortStart(
    string? RunId,
    bool HadActiveTurn)
{
    internal ChatRuntimeGeneration RuntimeGeneration { get; init; }
}

internal sealed record ChatAgentEventTransition(
    bool Process,
    bool ReloadHistory,
    ChatTerminalEventDropReason? DroppedTerminalReason,
    string? DeferredAbortRunId,
    int DeferredAbortCount,
    string? CompletedRunId,
    string? CompletionPhase,
    bool FetchRemoteUser,
    bool AllowRemoteTurn,
    bool WasAborted,
    bool Suppressed,
    ChatEvent? MappedEvent,
    ChatToolMetadataWrite? ToolMetadata,
    ChatDataSnapshot[] Snapshots,
    ChatOpenedLifecycleTransition? OpenedLifecycle,
    ChatRuntimeGeneration RuntimeGeneration);

internal sealed record ChatToolMetadataWrite(
    string ThreadId,
    string CacheKey,
    long ResetGeneration,
    long TimestampMs,
    string ToolName,
    string Label,
    string? ToolCallId,
    JsonObject? ToolArgs,
    ChatToolIdentityStrength IdentityStrength,
    string? RunId,
    long LegacyTurn);

internal sealed record ChatRunTransition(
    string? DeferredAbortRunId,
    int DeferredAbortCount,
    ChatTerminalEventDropReason? DroppedTerminalReason,
    string? CompletedRunId,
    string? CompletionPhase,
    bool FetchRemoteUser,
    bool AllowRemoteTurn,
    bool WasAborted,
    ChatDataSnapshot? Snapshot);

internal sealed record ChatAgentEventGate(
    bool Process,
    bool ReloadHistory,
    ChatTerminalEventDropReason? DroppedTerminalReason,
    ChatOpenedLifecycleTransition? OpenedLifecycle);

internal sealed record ChatHistoryCommitToken(
    string ThreadId,
    long ConnectionGeneration,
    long ResetGeneration,
    long ReplacementGeneration);

internal sealed record ChatHistoryReplacementTransition(
    ChatDataSnapshot Snapshot,
    ChatHistoryCommitToken Token);

internal sealed record ChatHistoryRebuildPlan(
    string? SessionId,
    ChatTimelineState Timeline,
    IReadOnlyDictionary<string, ChatEntryMetadata> Metadata,
    int MaxHistorySequence);

internal sealed record ChatQueuedAdmission(
    string MessageId,
    bool Queued,
    ChatQueuedSendDispatch? Dispatch,
    ChatDataSnapshot Snapshot,
    ChatRuntimeGeneration RuntimeGeneration);

internal sealed record ChatQueueStart(
    ChatQueuedSendDispatch? Dispatch,
    TimeSpan? DelayedRetry,
    ChatDataSnapshot? Snapshot);

internal sealed record ChatOpenedLifecycleTransition(
    AgentEventInfo Event,
    bool AllowRemoteTurn,
    string? DeferredAbortRunId,
    int DeferredAbortCount);

internal sealed record ChatSendCommit(
    bool IsCurrent,
    ChatDataSnapshot? AcceptedSnapshot,
    ChatDataSnapshot? RequeuedSnapshot,
    string? StaleRunIdToAbort,
    bool BindAcceptedRun,
    bool RequeueRequired,
    bool RetryDeferredSend,
    TimeSpan DeferredRetryDelay,
    ChatOpenedLifecycleTransition? OpenedLifecycle,
    ChatRuntimeGeneration RuntimeGeneration);

internal sealed record ChatSendFailure(
    bool IsCurrent,
    ChatDataSnapshot? Snapshot,
    bool DeliveryResolved = false);

internal sealed record ChatSendPreparation(
    bool IsCurrent,
    ChatDataSnapshot? Snapshot);

internal sealed record ChatSessionOptionPatchLease(
    string ThreadId,
    Task? Previous,
    TaskCompletionSource Completion);

internal sealed record ChatDisposeTransition(
    long HistoryGeneration,
    bool IsFirstDispose);

internal sealed record ChatIncomingMessageGate(
    bool Drop,
    bool Suppressed,
    bool RequestRemoteBackfill,
    ChatDataSnapshot? Snapshot,
    ChatOpenedLifecycleTransition? OpenedLifecycle,
    ChatRuntimeGeneration RuntimeGeneration);

internal sealed record ChatRemoteUserBackfillTransition(
    ChatDataSnapshot Snapshot,
    ChatOpenedLifecycleTransition? OpenedLifecycle,
    ChatRuntimeGeneration RuntimeGeneration);

internal sealed record ChatLocalEchoTransition(
    bool Consumed,
    ChatDataSnapshot? Snapshot);

internal sealed record ChatAssistantPreparation(
    AssistantQueueFrameDisposition Disposition,
    ChatDataSnapshot? PromotionSnapshot,
    ChatEntryMetadata Metadata,
    string? ActiveRunId,
    bool IsBackgroundFinal,
    ChatDataSnapshot? BackgroundFinalSnapshot);
