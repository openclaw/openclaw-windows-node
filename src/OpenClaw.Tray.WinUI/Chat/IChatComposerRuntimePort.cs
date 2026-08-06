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

    Task<bool> SendMessageAsync(
        string threadId,
        string message,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationToken cancellationToken);

    Task<bool> EnqueueCompactCommandAsync(string threadId);

    Task<ChatLifecycleCommandResult> ExecuteLifecycleCommandAsync(string threadId, ChatLifecycleCommandKind command);

    Task StopResponseAsync(string threadId, CancellationToken cancellationToken);

    Task CancelQueuedMessageAsync(string threadId, string queuedMessageId, CancellationToken cancellationToken);

    Task SetModelAsync(string threadId, string model, CancellationToken cancellationToken);

    Task ClearModelAsync(string threadId, CancellationToken cancellationToken);

    Task SetThinkingLevelAsync(string threadId, string thinkingLevel, CancellationToken cancellationToken);

    Task EnsureCommandCatalogAsync(CancellationToken cancellationToken);
}
