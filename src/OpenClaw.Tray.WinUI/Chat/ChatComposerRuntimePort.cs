using OpenClaw.Chat;
using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Chat;

/// <summary>
/// Production <see cref="IChatComposerRuntimePort"/> adapter. It holds only the
/// current <see cref="IChatDataProvider"/> reference and forwards each call
/// verbatim; it never caches a decision, subscribes to <c>Changed</c>, or retries
/// on its own. Exceptions from the underlying provider are swallowed and traced the
/// same way the pre-D2 root/composer closures did, so behavior is unchanged.
/// </summary>
internal sealed class ChatComposerRuntimePort(IChatDataProvider provider) : IChatComposerRuntimePort
{
    public bool SupportsNativeLifecycle => provider is OpenClawChatDataProvider;
    public bool SupportsSessionScopedModelCatalog =>
        provider is OpenClawChatDataProvider { SupportsSessionScopedModelCatalog: true };
    public long ModelCatalogRevision =>
        provider is OpenClawChatDataProvider native ? native.ModelCatalogRevision : 0;
    public bool SupportsImmediateSteer =>
        provider is OpenClawChatDataProvider { SupportsImmediateSteer: true };

    public ChatInputLease? CaptureInputOrigin(string threadId, IChatInputOrigin? externalOrigin = null) =>
        provider is OpenClawChatDataProvider native ? native.CaptureInputOrigin(threadId, externalOrigin) : null;

    public bool IsInputOriginCurrent(ChatInputLease origin) =>
        provider is OpenClawChatDataProvider native && native.IsInputOriginCurrent(origin);

    public Task<ChatInputLease?> ContinueInputPreparationAsync(
        ChatInputLease original, ChatPlacementMaterializationReceipt receipt, CancellationToken cancellationToken) =>
        provider is OpenClawChatDataProvider native
            ? native.ContinueInputPreparationAsync(original, receipt, cancellationToken)
            : Task.FromResult<ChatInputLease?>(null);

    public async Task<ChatInputSubmissionResult> SendInputAsync(
        string threadId, string message, IReadOnlyList<ChatAttachment> attachments,
        ChatInputAction action, ChatInputLease? origin, CancellationToken cancellationToken)
    {
        try
        {
            if (provider is OpenClawChatDataProvider native)
                return await native.SendInputAsync(threadId, message, attachments, action, origin, cancellationToken)
                    .ConfigureAwait(true);
            if (ChatInputCustody.WireMode(action) is not null || origin?.ExternalOrigin is not null)
                return new(ChatInputSubmissionState.Rejected, Error: ChatInputMessages.Unavailable);
            await provider.SendMessageAsync(threadId, message, cancellationToken, attachments).ConfigureAwait(true);
            return new(ChatInputSubmissionState.Accepted);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[chat] scoped input failed: {ex}");
            return new(ChatInputSubmissionState.Rejected, Error: ex.Message);
        }
    }

    public async Task<bool> SendMessageAsync(
        string threadId,
        string message,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationToken cancellationToken)
    {
        try
        {
            if (provider is OpenClawChatDataProvider native)
                return await native.SendMessageReportingAcceptanceAsync(
                    threadId,
                    message,
                    cancellationToken,
                    attachments).ConfigureAwait(true);

            await provider.SendMessageAsync(threadId, message, cancellationToken, attachments).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[chat] send failed: {ex}");
            return false;
        }
    }

    public Task<bool> EnqueueCompactCommandAsync(string threadId) =>
        provider is OpenClawChatDataProvider native
            ? native.EnqueueCompactCommandAsync(threadId)
            : Task.FromResult(false);

    public Task<ChatLifecycleCommandResult> ExecuteLifecycleCommandAsync(
        string threadId,
        ChatLifecycleCommandKind command) =>
        provider is OpenClawChatDataProvider native
            ? native.ExecuteLifecycleCommandAsync(threadId, command)
            : Task.FromResult(new ChatLifecycleCommandResult(
                command,
                Succeeded: false,
                Error: "This gateway does not support lifecycle commands."));

    public async Task StopResponseAsync(string threadId, CancellationToken cancellationToken,
        ChatInputLease? origin = null, Func<bool>? isCurrent = null)
    {
        try
        {
            if (provider is OpenClawChatDataProvider native)
                await native.StopResponseAsync(threadId, cancellationToken, origin, isCurrent).ConfigureAwait(true);
            else
                await provider.StopResponseAsync(threadId, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[chat] operation failed: {ex}"); }
    }

    public async Task CancelQueuedMessageAsync(
        string threadId,
        string queuedMessageId,
        CancellationToken cancellationToken)
    {
        try { await provider.CancelQueuedMessageAsync(threadId, queuedMessageId, cancellationToken).ConfigureAwait(true); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[chat] operation failed: {ex}"); }
    }

    public Task<bool> EditQueuedInputAsync(string threadId, string messageId, string text) =>
        provider is OpenClawChatDataProvider native
            ? native.EditQueuedInputAsync(threadId, messageId, text) : Task.FromResult(false);

    public Task<bool> MoveQueuedInputAsync(string threadId, string messageId, int direction) =>
        provider is OpenClawChatDataProvider native
            ? native.MoveQueuedInputAsync(threadId, messageId, direction) : Task.FromResult(false);

    public Task<ChatInputSubmissionResult> RetryQueuedInputAsync(
        string threadId, string messageId, bool steer, CancellationToken cancellationToken) =>
        provider is OpenClawChatDataProvider native
            ? native.RetryQueuedInputAsync(threadId, messageId, steer, cancellationToken)
            : Task.FromResult(new ChatInputSubmissionResult(ChatInputSubmissionState.Rejected, messageId,
                ChatInputMessages.RecoveryUnavailable));

    public Task<bool> DiscardUnconfirmedInputAsync(string threadId, string messageId) =>
        provider is OpenClawChatDataProvider native
            ? native.DiscardUnconfirmedInputAsync(threadId, messageId) : Task.FromResult(false);

    public async Task SetModelAsync(string threadId, string model, CancellationToken cancellationToken,
        ChatInputLease? origin = null, Func<bool>? isCurrent = null)
    {
        try
        {
            if (provider is OpenClawChatDataProvider native)
                await native.SetModelAsync(threadId, model, cancellationToken, origin, isCurrent).ConfigureAwait(true);
            else
                await provider.SetModelAsync(threadId, model, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[chat] operation failed: {ex}"); }
    }

    public async Task ClearModelAsync(string threadId, CancellationToken cancellationToken,
        ChatInputLease? origin = null, Func<bool>? isCurrent = null)
    {
        try
        {
            if (provider is OpenClawChatDataProvider native)
                await native.ClearModelAsync(threadId, cancellationToken, origin, isCurrent).ConfigureAwait(true);
            else
                await provider.ClearModelAsync(threadId, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[chat] operation failed: {ex}"); }
    }

    public async Task<ModelsListInfo?> RequestSessionModelsListAsync(
        string threadId,
        CancellationToken cancellationToken)
    {
        if (provider is not OpenClawChatDataProvider native)
            return null;
        cancellationToken.ThrowIfCancellationRequested();
        var models = await native.RequestSessionModelsListAsync(threadId).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        return models;
    }

    public async Task SetThinkingLevelAsync(string threadId, string thinkingLevel, CancellationToken cancellationToken,
        ChatInputLease? origin = null, Func<bool>? isCurrent = null)
    {
        try
        {
            if (provider is OpenClawChatDataProvider native)
                await native.SetThinkingLevelAsync(threadId, thinkingLevel, cancellationToken, origin, isCurrent).ConfigureAwait(true);
            else
                await provider.SetThinkingLevelAsync(threadId, thinkingLevel, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[chat] operation failed: {ex}"); }
    }

    public async Task ClearThinkingLevelAsync(string threadId, CancellationToken cancellationToken,
        ChatInputLease? origin = null, Func<bool>? isCurrent = null)
    {
        try
        {
            if (provider is OpenClawChatDataProvider native)
                await native.ClearThinkingLevelAsync(threadId, cancellationToken, origin, isCurrent).ConfigureAwait(true);
            else
                await provider.ClearThinkingLevelAsync(threadId, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[chat] operation failed: {ex}"); }
    }

    public async Task EnsureCommandCatalogAsync(CancellationToken cancellationToken)
    {
        try { await provider.EnsureCommandCatalogAsync(cancellationToken).ConfigureAwait(true); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[chat] operation failed: {ex}"); }
    }
}
