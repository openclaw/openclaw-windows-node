using OpenClaw.Chat;
using OpenClaw.Shared;
using System.Text.Json.Nodes;

namespace OpenClawTray.Chat;

internal sealed record ChatProjectionContext(
    string? MainSessionKey,
    bool HasHandshakeSnapshot);

internal readonly record struct ChatRuntimeGeneration(
    long ConnectionGeneration,
    long ResetGeneration);

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
    string[] SubmittedRunIds);

internal sealed record ChatAbortStart(
    string? RunId,
    bool HadActiveTurn);

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
    ChatDataSnapshot? Snapshot);

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
    string? ActiveRunId);
