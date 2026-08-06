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

    public async Task<bool> SendMessageAsync(
        string threadId,
        string message,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationToken cancellationToken)
    {
        try
        {
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

    public async Task SetModelAsync(string threadId, string model, CancellationToken cancellationToken)
    {
        try { await provider.SetModelAsync(threadId, model, cancellationToken).ConfigureAwait(true); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[chat] operation failed: {ex}"); }
    }

    public async Task ClearModelAsync(string threadId, CancellationToken cancellationToken)
    {
        try { await provider.ClearModelAsync(threadId, cancellationToken).ConfigureAwait(true); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[chat] operation failed: {ex}"); }
    }

    public async Task SetThinkingLevelAsync(string threadId, string thinkingLevel, CancellationToken cancellationToken)
    {
        try { await provider.SetThinkingLevelAsync(threadId, thinkingLevel, cancellationToken).ConfigureAwait(true); }
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
