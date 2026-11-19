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
/// on its own. Option failures propagate so first-send draft preparation cannot
/// send with an unconfirmed option; interactive option changes are observed by the controller.
/// </summary>
internal sealed class ChatComposerRuntimePort(IChatDataProvider provider) : IChatComposerRuntimePort
{
    public bool SupportsNativeLifecycle => provider is OpenClawChatDataProvider;
    public bool SupportsSessionScopedModelCatalog =>
        provider is OpenClawChatDataProvider { SupportsSessionScopedModelCatalog: true };
    public long ModelCatalogRevision =>
        provider is OpenClawChatDataProvider native ? native.ModelCatalogRevision : 0;
    public string? GetSessionId(string threadId) =>
        provider is OpenClawChatDataProvider native ? native.GetSessionId(threadId) : null;

    public async Task<ChatSendOutcome> SendMessageAsync(
        string threadId,
        string message,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationToken cancellationToken,
        string? idempotencyKey = null,
        string? expectedSessionId = null)
    {
        try
        {
            if (provider is OpenClawChatDataProvider native)
            {
                if (idempotencyKey is not null)
                {
                    return await native.SendDraftMessageReportingOutcomeAsync(
                        threadId,
                        message,
                        idempotencyKey,
                        cancellationToken,
                        attachments,
                        expectedSessionId).ConfigureAwait(true);
                }

                return await native.SendMessageReportingAcceptanceAsync(
                    threadId,
                    message,
                    cancellationToken,
                    attachments).ConfigureAwait(true)
                    ? ChatSendOutcome.Accepted
                    : ChatSendOutcome.Indeterminate;
            }

            await provider.SendMessageAsync(threadId, message, cancellationToken, attachments).ConfigureAwait(true);
            return ChatSendOutcome.Accepted;
        }
        catch (ChatSendRejectedException ex)
        {
            System.Diagnostics.Trace.WriteLine($"[chat] send rejected: {ex.Message}");
            return ChatSendOutcome.DefinitivelyRejected;
        }
        catch (InvalidOperationException ex) when (GatewayRequestErrors.Code(ex) == "INVALID_REQUEST")
        {
            System.Diagnostics.Trace.WriteLine($"[chat] Gateway rejected send: {ex.Message}");
            return ChatSendOutcome.DefinitivelyRejected;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[chat] send failed: {ex}");
            return ChatSendOutcome.Indeterminate;
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

    public void ReportLocalError(string threadId, string message)
    {
        if (provider is OpenClawChatDataProvider native)
            native.ReportLocalError(threadId, message);
    }

    public async Task StopResponseAsync(string threadId, CancellationToken cancellationToken)
    {
        try { await provider.StopResponseAsync(threadId, cancellationToken).ConfigureAwait(true); }
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

    public Task SetModelAsync(string threadId, string model, CancellationToken cancellationToken) =>
        provider.SetModelAsync(threadId, model, cancellationToken);

    public Task ClearModelAsync(string threadId, CancellationToken cancellationToken) =>
        provider.ClearModelAsync(threadId, cancellationToken);

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

    public Task SetThinkingLevelAsync(string threadId, string thinkingLevel, CancellationToken cancellationToken) =>
        provider.SetThinkingLevelAsync(threadId, thinkingLevel, cancellationToken);

    public Task ClearThinkingLevelAsync(string threadId, CancellationToken cancellationToken) =>
        provider.ClearThinkingLevelAsync(threadId, cancellationToken);

    public Task PatchDraftOptionsConfirmedAsync(
        string threadId, SessionPatch patch, CancellationToken cancellationToken) =>
        provider is OpenClawChatDataProvider native
            ? native.PatchDraftOptionsConfirmedAsync(threadId, patch, cancellationToken)
            : Task.FromException(new NotSupportedException(
                "Draft options require a Gateway response-confirmed session patch."));

    public Task<SessionCreateResult> CreateDefaultDraftSessionAsync(CancellationToken cancellationToken) =>
        provider is OpenClawChatDataProvider native
            ? native.CreateDefaultDraftSessionAsync(cancellationToken)
            : Task.FromException<SessionCreateResult>(new NotSupportedException(
                "Gateway-default draft creation is unavailable on this provider."));

    public Task<SessionCreateResult> CreateDraftSessionAsync(
        string agentId, CancellationToken cancellationToken) =>
        provider is OpenClawChatDataProvider native
            ? native.CreateDraftSessionAsync(agentId, cancellationToken)
            : Task.FromException<SessionCreateResult>(new NotSupportedException(
                "Gateway draft creation is unavailable on this provider."));

    public async Task EnsureCommandCatalogAsync(CancellationToken cancellationToken)
    {
        try { await provider.EnsureCommandCatalogAsync(cancellationToken).ConfigureAwait(true); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[chat] operation failed: {ex}"); }
    }
}
