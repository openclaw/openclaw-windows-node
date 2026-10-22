using OpenClaw.Chat;
using OpenClaw.Shared;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Chat;

/// <summary>
/// Narrow adapter over the current <see cref="IChatDataProvider"/> and the native
/// lifecycle bridge. It holds only a provider reference: no cache, subscription,
/// collection, generation, or persistence. Provider queue/runtime decisions remain
/// authoritative; this port never makes an admission or retry decision itself.
/// </summary>
internal interface IChatComposerRuntimePort
{
    /// <summary>True when the underlying provider supports native lifecycle commands.</summary>
    bool SupportsNativeLifecycle { get; }
    bool SupportsSessionScopedModelCatalog { get; }
    long ModelCatalogRevision { get; }
    bool SupportsImmediateSteer => false;
    ChatInputLease? CaptureInputOrigin(string threadId, IChatInputOrigin? externalOrigin = null) => null;
    bool IsInputOriginCurrent(ChatInputLease origin) => false;
    Task<ChatInputLease?> ContinueInputPreparationAsync(
        ChatInputLease original, ChatPlacementMaterializationReceipt receipt, CancellationToken cancellationToken) =>
        Task.FromResult<ChatInputLease?>(null);

    async Task<ChatInputSubmissionResult> SendInputAsync(
        string threadId, string message, IReadOnlyList<ChatAttachment> attachments,
        ChatInputAction action, ChatInputLease? origin, CancellationToken cancellationToken)
    {
        if (ChatInputCustody.WireMode(action) is not null || origin?.ExternalOrigin is not null)
            return new(ChatInputSubmissionState.Rejected, Error: ChatInputMessages.Unavailable);
        return new(await SendMessageAsync(threadId, message, attachments, cancellationToken)
            ? ChatInputSubmissionState.Accepted : ChatInputSubmissionState.Rejected);
    }

    Task<bool> SendMessageAsync(
        string threadId,
        string message,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationToken cancellationToken);

    Task<bool> EnqueueCompactCommandAsync(string threadId);

    Task<ChatLifecycleCommandResult> ExecuteLifecycleCommandAsync(string threadId, ChatLifecycleCommandKind command);

    Task StopResponseAsync(string threadId, CancellationToken cancellationToken,
        ChatInputLease? origin = null, Func<bool>? isCurrent = null);

    Task CancelQueuedMessageAsync(string threadId, string queuedMessageId, CancellationToken cancellationToken);
    Task<bool> EditQueuedInputAsync(string threadId, string messageId, string text) => Task.FromResult(false);
    Task<bool> MoveQueuedInputAsync(string threadId, string messageId, int direction) => Task.FromResult(false);
    Task<ChatInputSubmissionResult> RetryQueuedInputAsync(
        string threadId, string messageId, bool steer, CancellationToken cancellationToken) =>
        Task.FromResult(new ChatInputSubmissionResult(ChatInputSubmissionState.Rejected, messageId,
            ChatInputMessages.RecoveryUnavailable));
    Task<bool> DiscardUnconfirmedInputAsync(string threadId, string messageId) => Task.FromResult(false);

    Task SetModelAsync(string threadId, string model, CancellationToken cancellationToken,
        ChatInputLease? origin = null, Func<bool>? isCurrent = null);

    Task ClearModelAsync(string threadId, CancellationToken cancellationToken,
        ChatInputLease? origin = null, Func<bool>? isCurrent = null);

    Task<ModelsListInfo?> RequestSessionModelsListAsync(
        string threadId,
        CancellationToken cancellationToken);

    Task SetThinkingLevelAsync(string threadId, string thinkingLevel, CancellationToken cancellationToken,
        ChatInputLease? origin = null, Func<bool>? isCurrent = null);

    Task ClearThinkingLevelAsync(string threadId, CancellationToken cancellationToken,
        ChatInputLease? origin = null, Func<bool>? isCurrent = null);

    Task EnsureCommandCatalogAsync(CancellationToken cancellationToken);
}
