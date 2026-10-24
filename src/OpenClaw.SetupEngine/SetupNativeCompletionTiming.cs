using OpenClaw.Connection;
using OpenClaw.Connection.NativeGateway;

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
    private static readonly TimeSpan MinimumConnection = TimeSpan.FromSeconds(210);
    public static readonly TimeSpan ModelRecovery = TimeSpan.FromSeconds(210);
    public static readonly TimeSpan ModelVerification = TimeSpan.FromSeconds(150);
    public static readonly TimeSpan Navigation = TimeSpan.FromSeconds(30);
    // The published borrow includes the native start budget (OPENCLAW_NATIVE_GATEWAY_START_TIMEOUT) plus handshake.
    // Static initializers run in textual order: keep the constants above the values derived from them.
    public static readonly TimeSpan Connection = ConnectionFor(IsolatedGatewayRuntime.StartupTimeout);
    public static readonly TimeSpan PublishedVerification = Connection + ModelRecovery + Connection + ModelVerification;
    public static readonly TimeSpan Execution = ExecutionFor(Connection);
    /// <summary>
    /// Bounds of an execution window any supported startup override can persist. A process restarted with a
    /// different override must keep, never recompute, a stored window inside these bounds.
    /// </summary>
    public static readonly TimeSpan MinimumExecution =
        ExecutionFor(ConnectionFor(TimeSpan.FromSeconds(IsolatedGatewayRuntime.MinimumStartupTimeoutSeconds)));
    public static readonly TimeSpan MaximumExecution =
        ExecutionFor(ConnectionFor(TimeSpan.FromSeconds(IsolatedGatewayRuntime.MaximumStartupTimeoutSeconds)));

    internal static TimeSpan ConnectionFor(TimeSpan startupTimeout)
    {
        var handoff = NativeGatewayEndpointSecurity.CredentialHandoffTimeoutFor(startupTimeout);
        return handoff > MinimumConnection ? handoff : MinimumConnection;
    }

    internal static TimeSpan ExecutionFor(TimeSpan connection) =>
        connection + ModelRecovery + connection + ModelVerification + Navigation;

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
