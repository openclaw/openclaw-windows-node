using Microsoft.UI.Dispatching;
using OpenClaw.Shared;
using OpenClaw.Shared.ExecApprovals;
using OpenClawTray.Dialogs;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

/// <summary>
/// WinUI adapter for the exec approval prompt: supplies the real dispatcher hop and
/// dialog window to <see cref="ExecApprovalV2UiPromptHandler"/>, which owns all
/// decision logic (fail-closed semantics, cancellation authority, sanitization).
/// </summary>
public sealed class ExecApprovalV2DialogPromptHandler : IExecApprovalV2PromptHandler
{
    private readonly ExecApprovalV2UiPromptHandler _core;

    public ExecApprovalV2DialogPromptHandler(DispatcherQueue dispatcherQueue, IOpenClawLogger logger)
    {
        ArgumentNullException.ThrowIfNull(dispatcherQueue);
        _core = new ExecApprovalV2UiPromptHandler(
            action => dispatcherQueue.TryEnqueue(() => action()),
            (view, cancellationToken) => ShowDialogAsync(dispatcherQueue, view, cancellationToken),
            logger);
    }

    public Task<ExecApprovalPromptOutcome> PromptAsync(
        ExecApprovalV2PromptRequest request, CancellationToken cancellationToken = default)
        => _core.PromptAsync(request, cancellationToken);

    // Runs on the UI thread (enqueued by the core). The cancellation registration only
    // tears the window down; the outcome is already settled by the core's first-wins TCS.
    private static async Task<ExecApprovalPromptOutcome> ShowDialogAsync(
        DispatcherQueue dispatcherQueue, ExecApprovalPromptView view, CancellationToken cancellationToken)
    {
        // Already denied by the core; do not flash a window for a dead request.
        if (cancellationToken.IsCancellationRequested)
            return ExecApprovalPromptOutcome.Deny;

        var dialog = new ExecApprovalDialog(view);
        using var registration = cancellationToken.Register(() =>
            dispatcherQueue.TryEnqueue(() =>
            {
                if (!dialog.IsClosed) dialog.Close();
            }));
        return await dialog.ShowAsync();
    }
}
