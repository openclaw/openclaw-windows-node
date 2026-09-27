namespace OpenClaw.SetupEngine;

public enum SetupNativeCompletionStage { Verifying, Finalizing, Opening }

/// <summary>One chooser lifetime. Showing it grants nothing; every choice requires fresh read-only proof.</summary>
public sealed class SetupNativeCompletionCoordinator(
    GatewayAiSetupCompletion proof,
    Func<CancellationToken, Task> drain,
    Func<GatewayAiSetupCompletion, CancellationToken, Task<SetupVerifiedNativeRoute>> verify,
    Func<GatewayAiSetupCompletion, CancellationToken, Task> finalize,
    Func<SetupNativeCompletion, CancellationToken, Task> publish) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private int _busy;
    private bool _disposed;
    private bool _finalized;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool IsCompleted { get; private set; }
    public GatewayAiSetupCompletion Proof { get; } = proof;
    public SetupNativeCompletionStage Stage { get; private set; }
    public event Action? StateChanged;
    public Task ActiveTask { get; private set; } = Task.CompletedTask;

    public Task SelectAsync(SetupNativeDestination destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enum.IsDefined(destination) || IsCompleted || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("This completion choice is not available.");
        return ActiveTask = SelectCoreAsync(destination);
    }

    private async Task SelectCoreAsync(SetupNativeDestination destination)
    {
        var lifetime = _lifetime.Token;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            Stage = SetupNativeCompletionStage.Verifying;
            StateChanged?.Invoke();
            await drain(timeout.Token);
            var current = await verify(Proof, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            SetupNativeVerification.RequireSame(Proof, current);
            if (!_finalized)
            {
                Stage = SetupNativeCompletionStage.Finalizing;
                StateChanged?.Invoke();
                await finalize(current.Verification, lifetime);
                _finalized = true;
            }
            lifetime.ThrowIfCancellationRequested();
            Stage = SetupNativeCompletionStage.Opening;
            StateChanged?.Invoke();
            await publish(new(current.Verification, new(destination, current.SessionKey)), lifetime);
            IsCompleted = true;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
