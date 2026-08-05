using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Presentation;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Chat;

/// <summary>
/// Focused workflow orchestrator for the composer. It owns send/lifecycle/stop/reset
/// confirmation/queue cancel/model set-clear/thinking/catalog/attachment ingress-
/// remove/paste-image/voice operation cancellation and IDs, executed over the narrow
/// <see cref="IChatComposerRuntimePort"/> and <see cref="ChatComposerHostActions"/>
/// ports. It reuses <see cref="ChatComposerSubmissionPolicy"/>,
/// <see cref="ReactorSlashCommandController"/> (through the view model), and
/// <see cref="ChatLifecycleCommandParser"/>/<see cref="ChatLifecycleCommandDispatcher"/>/
/// <see cref="ChatLifecycleCommandExecutionPolicy"/> exactly as the pre-D2 root did.
/// </summary>
/// <remarks>
/// Never writes a named control, builds XAML, owns a popup instance, or calls
/// <c>Application.Current</c>. D1 remains authoritative for send admission, queue
/// mechanics, reset/history state, and permission identity; this controller only
/// converts operation outcomes into typed results applied to the view model.
/// </remarks>
internal sealed partial class ChatComposerController : IDisposable
{
    private readonly ChatComposerViewModel _vm;
    private readonly IChatComposerRuntimePort _port;
    private readonly ChatComposerHostActions _hostActions;

    /// <summary>Canceled exactly once, in <see cref="Dispose"/>. Threaded into every
    /// port call whose interface exposes a <see cref="CancellationToken"/> — ordinary
    /// send, stop, queue-cancel, model set/clear, thinking, and catalog — so
    /// outstanding network/provider work is actually interrupted on teardown, not
    /// merely fenced at the UI-outcome boundary. Never used to cancel <c>/reset</c>,
    /// <c>/new</c>, or <c>/compact</c>, whose interface omits a token entirely (D1
    /// owns those as atomic gateway round-trips); those remain fenced only at their
    /// UI-outcome boundary via the generation/operation checks in
    /// <see cref="SendAsync"/> and <see cref="SendCoreAsync"/>.</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();

    private Action<string>? _selectedSessionHandoff;
    private CancellationTokenSource? _voiceCancellation;
    private CancellationTokenSource? _pasteCancellation;
#pragma warning disable CS0169 // Consumed only by the WinRT paste partial (ChatComposerControllerClipboard.cs),
                               // which is not linked into the pure net10.0 OpenClaw.Tray.Tests project.
    private int _pasteOperation;
#pragma warning restore CS0169
    private int _voiceOperation;
    private int _voiceStopOperation;
    private int _sendOperation;
    private int _catalogOperation;
    private int _generation;

    /// <summary>Controller-owned single-flight send gate, independent of the
    /// rendered/projected <see cref="ChatComposerViewModel.IsSending"/> value.
    /// <see cref="ChatComposerViewModel.SetSending"/> is dispatched through
    /// <see cref="ChatComposerViewModel.Mutate"/>, which — when the host dispatcher
    /// does not currently have thread access — only *enqueues* the mutation rather
    /// than applying it immediately; <c>IsSending</c> can therefore still read
    /// <see langword="false"/> for a window after a send has already started. Using
    /// it as the single-flight guard would let a second concurrent
    /// <see cref="SendAsync"/> call slip through and invoke the provider twice for
    /// one user action. This field is the actual gate (acquired/released only via
    /// <see cref="Interlocked"/>, never read/written as a plain bool), and
    /// <c>IsSending</c> remains purely a derived, render-only output.</summary>
    private int _sendGate;
    private volatile bool _disposed;

    public ChatComposerController(
        ChatComposerViewModel viewModel,
        IChatComposerRuntimePort port,
        ChatComposerHostActions hostActions)
    {
        _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _hostActions = hostActions ?? throw new ArgumentNullException(nameof(hostActions));
    }
    /// <summary>Binds the root's session-selection handoff exactly once. Safe to call
    /// on every render: it is a no-op once bound, since the underlying closure is
    /// stable for the lifetime of the mounted root. No-ops after disposal.</summary>
    public void BindSelectionHandoff(Action<string> handoff)
    {
        if (_disposed)
            return;

        _selectedSessionHandoff ??= handoff;
    }

    /// <summary>Exposed for disposal characterization tests.</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>Handles a session-picker selection. Reuses the same handoff delegate
    /// the lifecycle "/new" flow uses to select a freshly created session. No-ops
    /// after disposal.</summary>
    public void SelectChannel(string threadId)
    {
        if (_disposed)
            return;

        _selectedSessionHandoff?.Invoke(threadId);
    }

    /// <summary>Full composer send workflow: local admission first, snapshot of draft
    /// revision/attachment identities/compose target at operation start, delegate once
    /// to <see cref="SendCoreAsync"/>, then clear only the accepted, still-matching
    /// draft/attachments. In-flight edits and attachment additions survive.</summary>
    /// <remarks>
    /// Single-flight is enforced by <see cref="_sendGate"/> (an
    /// <see cref="Interlocked"/>-guarded field), not by
    /// <see cref="ChatComposerViewModel.IsSending"/>: that VM property is only
    /// dispatched through <see cref="ChatComposerViewModel.Mutate"/>, which may
    /// merely enqueue (not yet apply) the "sending" flag when the host dispatcher
    /// does not currently have thread access, leaving a window where a second
    /// concurrent call would otherwise observe stale "not sending" state and send
    /// twice.
    /// </remarks>
    public async Task<bool> SendAsync()
    {
        if (_disposed)
            return false;
        if (_vm.Inputs is not { } inputs)
            return false;

        var thread = inputs.CurrentThread;
        var draft = _vm.Draft;
        var attachments = _vm.PendingAttachments;
        var message = draft.Trim();
        if ((message.Length == 0 && attachments.Count == 0)
            || _vm.SlashDisplay.IsLoading
            || inputs.ConnectionState != "connected")
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _sendGate, 1, 0) != 0)
            return false;

        try
        {
            var submittedRevision = _vm.DraftRevision;
            var generationAtStart = _generation;
            var sendOperation = ++_sendOperation;
            _vm.SetSending(true);
            try
            {
                var accepted = await SendCoreAsync(thread.Id, thread.Title, message, attachments).ConfigureAwait(true);
                if (_disposed || generationAtStart != _generation || sendOperation != _sendOperation)
                    return accepted;

                if (accepted)
                {
                    if (ChatComposerSubmissionPolicy.ShouldClearInput(submittedRevision, _vm.DraftRevision))
                        _vm.ClearDraft();
                    _vm.RemoveSubmittedAttachments(attachments);
                }

                return accepted;
            }
            finally
            {
                if (!_disposed && generationAtStart == _generation && sendOperation == _sendOperation)
                    _vm.SetSending(false);
            }
        }
        finally
        {
            // Released unconditionally — including on a throw from the snapshot/
            // SetSending(true) statements above or from anywhere in the inner try
            // — so a controller that is still alive (or whose send merely got
            // fenced by the disposed/generation check above) can always accept
            // its next send once this one has fully unwound.
            Interlocked.Exchange(ref _sendGate, 0);
        }
    }

    /// <summary>Pure send/lifecycle workflow with no composer draft/attachment state.
    /// Used by the composer send workflow above and directly by the root's welcome-
    /// screen quick-start suggestion, exactly as the pre-D2 root's private
    /// <c>SendAsync</c> helper served both call sites. Fences every resume point
    /// (after the compact enqueue, the reset-confirmation dialog, and the lifecycle
    /// execute/send calls) against a dispose/generation change that happened while
    /// awaiting, so a controller disposed mid-confirmation cannot still execute the
    /// destructive command or hand a stale session key to a torn-down host. No-ops
    /// (returns false without calling the port) if already disposed at entry.</summary>
    public async Task<bool> SendCoreAsync(
        string threadId,
        string? displayName,
        string message,
        IReadOnlyList<ChatAttachment> attachments)
    {
        if (_disposed)
            return false;

        var generationAtStart = _generation;
        bool StillLive() => !_disposed && generationAtStart == _generation;

        if (_port.SupportsNativeLifecycle
            && ChatLifecycleCommandParser.TryParse(message, attachments.Count > 0, out var command))
        {
            if (ChatLifecycleCommandExecutionPolicy.ShouldQueue(command))
            {
                var queued = await _port.EnqueueCompactCommandAsync(threadId).ConfigureAwait(true);
                return StillLive() && queued;
            }

            if (command == ChatLifecycleCommandKind.Reset && _hostActions.ConfirmResetAsync is not null)
            {
                var confirmed = await _hostActions.ConfirmResetAsync(threadId, displayName).ConfigureAwait(true);
                if (!StillLive() || !confirmed)
                    return false;
            }

            var result = await _port.ExecuteLifecycleCommandAsync(threadId, command).ConfigureAwait(true);
            if (!StillLive())
                return false;
            if (result.Succeeded && result.NewSessionKey is { } sessionKey)
                _selectedSessionHandoff?.Invoke(sessionKey);
            return result.Succeeded;
        }

        var accepted = await _port.SendMessageAsync(threadId, message, attachments, _lifetimeCts.Token).ConfigureAwait(true);
        return StillLive() && accepted;
    }

    public void Stop()
    {
        if (_disposed)
            return;
        if (_vm.Inputs?.CurrentThread.Id is not { } threadId)
            return;

        FireAndForget(_ => _port.StopResponseAsync(threadId, _lifetimeCts.Token));
    }

    public void CancelQueuedMessage(string queuedMessageId)
    {
        if (_disposed)
            return;
        if (_vm.Inputs?.CurrentThread.Id is not { } threadId)
            return;

        FireAndForget(_ => _port.CancelQueuedMessageAsync(threadId, queuedMessageId, _lifetimeCts.Token));
    }

    public void SetModel(string model)
    {
        if (_disposed)
            return;
        if (_vm.Inputs?.CurrentThread.Id is not { } threadId)
            return;

        FireAndForget(_ => _port.SetModelAsync(threadId, model, _lifetimeCts.Token));
    }

    public void ClearModel()
    {
        if (_disposed)
            return;
        if (_vm.Inputs?.CurrentThread.Id is not { } threadId)
            return;

        FireAndForget(_ => _port.ClearModelAsync(threadId, _lifetimeCts.Token));
    }

    public void SetThinkingLevel(string level)
    {
        if (_disposed)
            return;
        if (_vm.Inputs?.CurrentThread.Id is not { } threadId)
            return;

        FireAndForget(_ => _port.SetThinkingLevelAsync(threadId, level, _lifetimeCts.Token));
    }

    /// <summary>Requests a command-catalog refresh. Assigns a monotonic operation ID
    /// and threads the shared lifetime token so the outstanding request is actually
    /// canceled on dispose. Refreshed results flow back only through the root's
    /// provider-subscribed snapshot/<c>ApplyInputs</c> path (already monotonic-guarded),
    /// so this call itself owns no VM mutation to fence beyond disposal/cancellation.</summary>
    public void RequestCommandCatalog()
    {
        if (_disposed)
            return;

        ++_catalogOperation;
        FireAndForget(_ => _port.EnsureCommandCatalogAsync(_lifetimeCts.Token));
    }

    public void AddAttachment(ChatAttachment attachment)
    {
        if (_disposed)
            return;

        _vm.AddAttachments(new[] { attachment });
    }

    /// <summary>Ingests attachments that originate outside the declarative tree (the
    /// host file picker). Bound once at session creation, not reassigned per render.
    /// No-ops after disposal.</summary>
    public void AddAttachments(IReadOnlyList<ChatAttachment> attachments)
    {
        if (_disposed)
            return;

        _vm.AddAttachments(attachments);
    }

    public void RemoveAttachment(ChatAttachment attachment)
    {
        if (_disposed)
            return;

        _vm.RemoveAttachment(attachment);
    }

    public void ToggleSpeakerMuted()
    {
        if (_disposed)
            return;

        var next = !_vm.IsSpeakerMuted;
        _vm.SetSpeakerMuted(next);
        _hostActions.SpeakerMuteChanged?.Invoke(next);
    }

    /// <summary>Starts a voice-capture operation. Cancels any prior in-flight capture,
    /// assigns a new monotonic operation ID, and fences the eventual completion so a
    /// stale capture (superseded, unmounted, or disposed) cannot mutate the view model.
    /// No-ops after disposal.</summary>
    public void StartVoiceRecording()
    {
        if (_disposed || _hostActions.VoiceCaptureRequest is not { } request || _vm.IsRecording)
            return;

        var cancellation = new CancellationTokenSource();
        _voiceCancellation?.Cancel();
        _voiceCancellation?.Dispose();
        _voiceCancellation = cancellation;
        var operation = ++_voiceOperation;
        _voiceStopOperation = 0;
        var generationAtStart = _generation;
        _vm.SetRecording(true);
        _ = ReceiveVoiceAsync(request, cancellation, operation, generationAtStart);
    }

    /// <summary>Requests cancellation of the in-flight voice capture. The capture's
    /// own completion path decides whether a partial transcript survives. No-ops
    /// after disposal (Dispose already cancels any in-flight capture).</summary>
    public void StopVoiceRecording()
    {
        if (_disposed)
            return;

        _voiceStopOperation = _voiceOperation;
        _voiceCancellation?.Cancel();
    }

    private async Task ReceiveVoiceAsync(
        Func<CancellationToken, Action?, Task<string?>> request,
        CancellationTokenSource cancellation,
        int operation,
        int generationAtStart)
    {
        try
        {
            var transcript = await request(cancellation.Token, () => _vm.SetRecording(true)).ConfigureAwait(true);
            var stoppedByUser = _voiceStopOperation == operation;
            if (!_disposed
                && generationAtStart == _generation
                && (!cancellation.IsCancellationRequested || stoppedByUser)
                && !string.IsNullOrWhiteSpace(transcript))
            {
                _vm.AppendVoiceTranscript(transcript);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OpenClawTray.Services.Logger.Debug($"Reactor chat composer voice request failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_voiceCancellation, cancellation))
                _voiceCancellation = null;
            cancellation.Dispose();
            if (!_disposed && generationAtStart == _generation && _voiceOperation == operation)
                _vm.SetRecording(false);
        }
    }

    /// <summary>Invokes <paramref name="operation"/> synchronously (on the caller's
    /// thread) to obtain its Task, then observes completion/errors without awaiting.
    /// Invoking synchronously — rather than deferring the call itself into
    /// <c>Task.Run</c> — preserves call order when the UI issues several of these in
    /// quick succession (for example two rapid model picks), matching the pre-D2
    /// root's <c>ObserveFireAndForget(props.Provider.SetModelAsync(...))</c> pattern
    /// where the provider call itself was already evaluated eagerly at the call site.</summary>
    private static void FireAndForget(Func<CancellationToken, Task> operation)
    {
        Task task;
        try
        {
            task = operation(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[chat] operation failed: {ex}");
            return;
        }

        _ = ObserveAsync(task);

        static async Task ObserveAsync(Task pending)
        {
            try { await pending.ConfigureAwait(true); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[chat] operation failed: {ex}"); }
        }
    }

    /// <summary>Marks the controller disposed, cancels every outstanding operation
    /// (voice, paste, and the shared lifetime token used by stop/queue-cancel/model/
    /// thinking/catalog/send), and bumps the generation so any already-running
    /// completion is fenced out. Idempotent: repeated calls are a no-op.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _generation++;
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
        _voiceCancellation?.Cancel();
        _voiceCancellation?.Dispose();
        _voiceCancellation = null;
        _pasteCancellation?.Cancel();
        _pasteCancellation?.Dispose();
        _pasteCancellation = null;
    }
}
