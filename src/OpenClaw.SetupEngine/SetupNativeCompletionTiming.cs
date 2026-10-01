namespace OpenClaw.SetupEngine;

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
        Func<CancellationToken, Task<T>> operation, TimeSpan budget, string phase,
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
            throw new TimeoutException($"Native AI completion timed out while {phase}. Retry when the Gateway is ready.", error);
        }
    }
}
