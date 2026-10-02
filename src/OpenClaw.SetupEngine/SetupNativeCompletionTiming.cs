namespace OpenClaw.SetupEngine;

public enum SetupNativeCompletionPhase { PageDrain, Connection, ModelRecovery, ModelVerification, Verification }

public sealed class SetupNativeCompletionTimeoutException : TimeoutException
{
    public SetupNativeCompletionPhase Phase { get; }

    internal SetupNativeCompletionTimeoutException(SetupNativeCompletionPhase phase, Exception innerException)
        : base($"Native AI completion timed out while {Describe(phase)}. Retry when the Gateway is ready.", innerException)
    {
        Phase = phase;
    }

    private static string Describe(SetupNativeCompletionPhase phase) => phase switch
    {
        SetupNativeCompletionPhase.PageDrain => "closing the previous AI setup page",
        SetupNativeCompletionPhase.Connection => "connecting to the native Gateway",
        SetupNativeCompletionPhase.ModelRecovery => "waiting for Local AI recovery",
        SetupNativeCompletionPhase.ModelVerification => "verifying the selected AI model",
        SetupNativeCompletionPhase.Verification => "checking AI setup readiness",
        _ => throw new ArgumentOutOfRangeException(nameof(phase))
    };
}

/// <summary>Finite phase budgets for an already selected native setup destination.</summary>
public static class SetupNativeCompletionTiming
{
    public static readonly TimeSpan Connection = TimeSpan.FromSeconds(210);
    public static readonly TimeSpan ModelRecovery = TimeSpan.FromSeconds(210);
    public static readonly TimeSpan ModelVerification = TimeSpan.FromSeconds(150);
    public static readonly TimeSpan Navigation = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PublishedVerification = Connection + ModelRecovery + Connection + ModelVerification;
    public static readonly TimeSpan Execution = PublishedVerification + Navigation;

    internal static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation, TimeSpan budget, SetupNativeCompletionPhase phase,
        CancellationToken cancellationToken, TimeProvider? timeProvider = null)
    {
        using var deadline = new CancellationTokenSource(budget, timeProvider ?? TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var result = await operation(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException error) when (
            deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new SetupNativeCompletionTimeoutException(phase, error);
        }
    }
}
