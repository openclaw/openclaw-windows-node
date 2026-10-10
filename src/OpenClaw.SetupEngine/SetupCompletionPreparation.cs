namespace OpenClaw.SetupEngine;

/// <summary>
/// Window-owned completion, admitted by the explicit AI action. Start returns before
/// draining that action's page. Teardown must join ActiveTask before releasing ownership.
/// </summary>
public sealed class SetupCompletionPreparation(
    GatewayAiSetupCompletion proof,
    Func<CancellationToken, Task> drain,
    Func<GatewayAiSetupCompletion, CancellationToken, Task<SetupVerifiedNativeRoute>> verify,
    Func<GatewayAiSetupCompletion, CancellationToken, Task> finalize,
    Func<SetupNativePreparation, CancellationToken, Task> publish,
    TimeProvider? timeProvider = null, Func<bool>? canResumeCommittedFinalization = null) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _started;
    private bool _finalized;
    private bool _disposed;
    private bool _finalizationUncertain;
    private Task? _draining;
    private GatewayAiSetupCompletion? _verified;
    private Task _cleanup = Task.CompletedTask;
    public Task ActiveTask { get; private set; } = Task.CompletedTask;
    public Task CleanupCompleted => _cleanup;
    public bool CanRetry => !_disposed && !IsCompleted && ActiveTask.IsCompleted &&
        (!_finalizationUncertain || canResumeCommittedFinalization?.Invoke() == true) &&
        (_draining?.IsCompletedSuccessfully ?? true);
    public SetupNativeCompletionStage Stage { get; private set; }
    public event Action? StateChanged;
    public bool IsCompleted { get; private set; }

    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return ActiveTask;
        _started = true;
        return ActiveTask = RunAsync();
    }

    public Task RetryAsync()
    {
        if (!CanRetry)
            throw new InvalidOperationException("Completion cannot be replayed while cleanup or an uncertain write is pending.");
        return ActiveTask = RunAsync();
    }

    private async Task RunAsync()
    {
        // The caller is still inside AiSetupPage._activeRequest.
        await Task.Yield();
        var ct = _lifetime.Token;
        ct.ThrowIfCancellationRequested();
        if (!new SetupNativePreparation(proof, proof.SessionKey ?? "").IsValid)
            throw new SetupNativeOwnershipException();
        if (!_finalized && !_finalizationUncertain)
        {
            Stage = SetupNativeCompletionStage.Draining;
            StateChanged?.Invoke();
            // Bound only the presentation wait. The window and CleanupCompleted
            // retain the real drain, including any admitted mutation.
            await SetupNativeCompletionTiming.RunAsync(async token =>
            {
                _draining ??= drain(ct);
                await _draining.WaitAsync(token);
                return true;
            }, SetupNativeCompletionCoordinator.DrainTimeout, SetupNativeCompletionPhase.PageDrain, ct, timeProvider);
            ct.ThrowIfCancellationRequested();
            Stage = SetupNativeCompletionStage.Verifying;
            StateChanged?.Invoke();
            var current = await SetupNativeCompletionTiming.RunAsync(
                token => verify(proof, token), SetupNativeCompletionCoordinator.VerificationTimeout,
                SetupNativeCompletionPhase.Verification, ct, timeProvider);
            SetupNativeVerification.RequireSame(proof, current);
            _verified = current.Verification;
        }
        if (!_finalized)
        {
            if (_finalizationUncertain && canResumeCommittedFinalization?.Invoke() != true)
                throw new InvalidOperationException("The admitted finalization must be reconciled before it can continue.");
            Stage = SetupNativeCompletionStage.Finalizing;
            StateChanged?.Invoke();
            _finalizationUncertain = true;
            await finalize(_verified!, ct);
            _finalized = true;
            _finalizationUncertain = false;
        }
        ct.ThrowIfCancellationRequested();
        Stage = SetupNativeCompletionStage.Opening;
        StateChanged?.Invoke();
        await publish(new(_verified!, _verified!.SessionKey!), ct);
        ct.ThrowIfCancellationRequested();
        IsCompleted = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _cleanup = DisposeAfterCleanupAsync();
    }

    private async Task DisposeAfterCleanupAsync()
    {
        try
        {
            // Drain admission may still be returning its task when cancellation arrives.
            // Join the owner before reading the drain it retained.
            try { await ActiveTask; }
            finally { await (_draining ?? Task.CompletedTask); }
        }
        catch (Exception) { /* ActiveTask reports failure; disposal still joins retained ownership. */ }
        finally { _lifetime.Dispose(); }
    }
}
