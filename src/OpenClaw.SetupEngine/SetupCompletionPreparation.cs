namespace OpenClaw.SetupEngine;

/// <summary>The restart process exists, so publication must not launch it again.</summary>
public sealed class SetupPreparationCommittedException : InvalidOperationException
{
    public SetupPreparationCommittedException()
        : base("The setup restart was launched, but shutdown could not be scheduled. Close OpenClaw to continue.") { }
}

/// <summary>
/// Window-owned completion, admitted by the explicit AI action. Start returns before
/// draining that action's page. Teardown must join CleanupCompleted before releasing ownership.
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
    private bool _publicationCommitted;
    private Task? _draining;
    private Task _execution = Task.CompletedTask;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private CancellationTokenSource? _deadline;
    private CancellationTokenSource? _executionLifetime;
    private long _startedAt;
    private GatewayAiSetupCompletion? _verified;
    private Task _cleanup = Task.CompletedTask;
    public Task ActiveTask { get; private set; } = Task.CompletedTask;
    public Task CleanupCompleted => _cleanup;
    public bool HasPendingWork => !_execution.IsCompleted || _draining is { IsCompleted: false };
    private bool ExecutionExpired => _deadline is not null &&
        (_deadline.IsCancellationRequested ||
         _clock.GetElapsedTime(_startedAt) >= SetupNativeCompletionTiming.Execution);
    public bool CanRetry => _started && !_disposed && !IsCompleted && ActiveTask.IsCompleted &&
        !HasPendingWork && !ExecutionExpired && !_publicationCommitted &&
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
        _startedAt = _clock.GetTimestamp();
        _deadline = new(SetupNativeCompletionTiming.Execution, _clock);
        _executionLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _deadline.Token);
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
        var ct = _executionLifetime!.Token;
        try
        {
            ct.ThrowIfCancellationRequested();
            // Request cooperative cancellation, but bound only the presentation wait.
            // Retain an admitted write or restart through its actual settlement.
            _execution = RunCoreAsync(ct);
            await _execution.WaitAsync(ct);
        }
        catch (OperationCanceledException error) when (ExecutionExpired && !_lifetime.IsCancellationRequested)
        {
            throw new SetupNativeCompletionTimeoutException(SetupNativeCompletionPhase.Execution, error);
        }
    }

    private async Task RunCoreAsync(CancellationToken ct)
    {
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
            ct.ThrowIfCancellationRequested();
            _finalizationUncertain = true;
            await finalize(_verified!, ct);
            _finalized = true;
            _finalizationUncertain = false;
        }
        ct.ThrowIfCancellationRequested();
        Stage = SetupNativeCompletionStage.Opening;
        StateChanged?.Invoke();
        ct.ThrowIfCancellationRequested();
        try
        {
            await publish(new(_verified!, _verified!.SessionKey!), ct);
            _publicationCommitted = true;
        }
        catch (SetupPreparationCommittedException)
        {
            _publicationCommitted = true;
            throw;
        }
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
            finally
            {
                try { await _execution; }
                finally { await (_draining ?? Task.CompletedTask); }
            }
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning("Setup completion cleanup settled with failure ({0}).",
                error.GetType().Name);
        }
        finally
        {
            _executionLifetime?.Dispose();
            _deadline?.Dispose();
            _lifetime.Dispose();
        }
    }
}
