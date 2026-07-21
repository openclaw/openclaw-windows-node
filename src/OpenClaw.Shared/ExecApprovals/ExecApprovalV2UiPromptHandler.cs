using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Display-ready strings for the exec approval prompt window. All values are already
/// sanitized for rendering; the dialog never sees the raw request.
/// </summary>
public sealed record ExecApprovalPromptView(
    string CommandText,
    string AgentLabel,
    string? CwdText,
    string? ExecutablePathText,
    bool HasConfusableWarning);

/// <summary>
/// Prompt-handler core behind the approval dialog. UI-free: the dispatcher hop and the
/// window are injected as delegates so the full decision behavior is testable without
/// WinUI. Fail-closed contract: never throws, never returns plain Allow, and resolves
/// Deny on cancellation, enqueue failure, window close, or any internal error.
/// </summary>
public sealed class ExecApprovalV2UiPromptHandler : IExecApprovalV2PromptHandler
{
    private readonly Func<Action, bool> _tryEnqueue;
    private readonly Func<ExecApprovalPromptView, CancellationToken, Task<ExecApprovalPromptOutcome>> _showDialog;
    private readonly IOpenClawLogger _logger;

    public ExecApprovalV2UiPromptHandler(
        Func<Action, bool> tryEnqueue,
        Func<ExecApprovalPromptView, CancellationToken, Task<ExecApprovalPromptOutcome>> showDialog,
        IOpenClawLogger logger)
    {
        _tryEnqueue = tryEnqueue ?? throw new ArgumentNullException(nameof(tryEnqueue));
        _showDialog = showDialog ?? throw new ArgumentNullException(nameof(showDialog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ExecApprovalPromptOutcome> PromptAsync(
        ExecApprovalV2PromptRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
                return ExecApprovalPromptOutcome.Deny;

            // Every field shown in the dialog originates from agent-controlled input,
            // including the agent label, so all of them go through the display sanitizer
            // before any UI binding to neutralize BiDi and control-character spoofing.
            var commandText = ExecApprovalCommandDisplaySanitizer.Sanitize(request.DisplayCommand);
            var agentLabel = ExecApprovalCommandDisplaySanitizer.Sanitize(request.AgentId);
            var cwdText = request.Cwd is null ? null : ExecApprovalCommandDisplaySanitizer.Sanitize(request.Cwd);
            var pathText = request.ResolvedPath is null ? null : ExecApprovalCommandDisplaySanitizer.Sanitize(request.ResolvedPath);

            // Homoglyphs and mixed scripts survive sanitization by design (escaping them
            // would break legitimate non-ASCII text); surface them as a visible warning
            // instead so the user knows the command may not read the way it looks.
            var hasConfusable =
                ExecApprovalConfusableDetector.HasMixedScriptConfusable(commandText)
                || ExecApprovalConfusableDetector.HasMixedScriptConfusable(cwdText)
                || ExecApprovalConfusableDetector.HasMixedScriptConfusable(pathText);

            var view = new ExecApprovalPromptView(commandText, agentLabel, cwdText, pathText, hasConfusable);

            var tcs = new TaskCompletionSource<ExecApprovalPromptOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            // This TCS is the single authority for the outcome; first result wins.
            // Cancellation resolves it directly — no dispatcher hop — so a hung or
            // slow dialog can never delay the deny. Window teardown on cancel is
            // visual cleanup only, owned by the dialog delegate.
            using var registration = cancellationToken.Register(
                () => tcs.TrySetResult(ExecApprovalPromptOutcome.Deny));

            // Cancellation may have resolved the TCS already (inline during Register).
            // A request that is already denied must never surface a window.
            if (tcs.Task.IsCompleted)
                return await tcs.Task.ConfigureAwait(false);

            if (!_tryEnqueue(() => _ = RunDialogAsync(view, tcs, cancellationToken)))
            {
                _logger.Warn("[EXEC-APPROVALS] prompt: dispatcher enqueue failed; denying");
                tcs.TrySetResult(ExecApprovalPromptOutcome.Deny);
            }

            var outcome = await tcs.Task.ConfigureAwait(false);
            if (outcome == ExecApprovalPromptOutcome.Allow)
            {
                _logger.Error("[EXEC-APPROVALS] prompt: dialog returned plain Allow; " +
                    "only AllowOnce/AllowAlways are valid from UI — denying");
                return ExecApprovalPromptOutcome.Deny;
            }
            return outcome;
        }
        catch (Exception ex)
        {
            try { _logger.Warn($"[EXEC-APPROVALS] prompt failed; denying: {ex.Message}"); } catch { }
            return ExecApprovalPromptOutcome.Deny;
        }
    }

    // Runs on the dispatcher. The try/catch wraps the entire async body so failures
    // after the first await (where the delegate is effectively fire-and-forget) still
    // resolve the outcome instead of escaping.
    private async Task RunDialogAsync(
        ExecApprovalPromptView view,
        TaskCompletionSource<ExecApprovalPromptOutcome> tcs,
        CancellationToken cancellationToken)
    {
        try
        {
            // The request may have been cancelled while this action sat in the
            // dispatcher queue; it is already denied, so never show the dialog.
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetResult(ExecApprovalPromptOutcome.Deny);
                return;
            }

            var outcome = await _showDialog(view, cancellationToken).ConfigureAwait(false);
            tcs.TrySetResult(outcome);
        }
        catch (Exception ex)
        {
            try { _logger.Warn($"[EXEC-APPROVALS] prompt dialog failed; denying: {ex.Message}"); } catch { }
            tcs.TrySetResult(ExecApprovalPromptOutcome.Deny);
        }
    }
}
