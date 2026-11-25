using OpenClaw.Chat;
using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Chat;

internal enum ChatSendOutcome
{
    Accepted,
    DefinitivelyRejected,
    Indeterminate,
    Deferred
}

internal sealed class ChatSendRejectedException(string message) : InvalidOperationException(message);

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
    string? GetSessionId(string threadId);

    Task<ChatSendOutcome> SendMessageAsync(
        string threadId,
        string message,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationToken cancellationToken,
        string? idempotencyKey = null,
        string? expectedSessionId = null);

    Task<bool> EnqueueCompactCommandAsync(string threadId);

    Task<ChatLifecycleCommandResult> ExecuteLifecycleCommandAsync(string threadId, ChatLifecycleCommandKind command);

    void ReportLocalError(string threadId, string message);

    Task StopResponseAsync(string threadId, CancellationToken cancellationToken);

    Task CancelQueuedMessageAsync(string threadId, string queuedMessageId, CancellationToken cancellationToken);

    Task SetModelAsync(string threadId, string model, CancellationToken cancellationToken);

    Task ClearModelAsync(string threadId, CancellationToken cancellationToken);

    Task<ModelsListInfo?> RequestSessionModelsListAsync(
        string threadId,
        CancellationToken cancellationToken);

    Task SetThinkingLevelAsync(string threadId, string thinkingLevel, CancellationToken cancellationToken);

    Task ClearThinkingLevelAsync(string threadId, CancellationToken cancellationToken);

    Task PatchDraftOptionsConfirmedAsync(string threadId, SessionPatch patch, CancellationToken cancellationToken);

    Task<SessionCreateResult> CreateDefaultDraftSessionAsync(CancellationToken cancellationToken);

    Task<SessionCreateResult> CreateDraftSessionAsync(string agentId, CancellationToken cancellationToken);

    Task EnsureCommandCatalogAsync(CancellationToken cancellationToken);
}
