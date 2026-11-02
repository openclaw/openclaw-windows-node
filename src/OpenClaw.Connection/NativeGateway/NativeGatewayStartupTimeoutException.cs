namespace OpenClaw.Connection.NativeGateway;

/// <summary>Which part of an owned package start exhausted the startup budget.</summary>
public enum NativeGatewayStartupTimeoutPhase
{
    /// <summary><c>clawctl gateway-service start</c> did not return an acknowledgement in the budget.</summary>
    StartRequest,
    /// <summary>The start was acknowledged, but status never reached a verified running listener.</summary>
    Readiness
}

/// <summary>Safe-to-display startup timeout: contains only validated package states and token-shaped reason codes.</summary>
public sealed class NativeGatewayStartupTimeoutException : TimeoutException
{
    internal NativeGatewayStartupTimeoutException(
        NativeGatewayStartupTimeoutPhase phase,
        TimeSpan budget,
        IsolatedGatewayStatus? lastStatus,
        Exception? innerException = null)
        : base(FormatMessage(phase, budget, lastStatus), innerException)
    {
        Phase = phase;
        Budget = budget;
        LastStatus = lastStatus;
    }

    public NativeGatewayStartupTimeoutPhase Phase { get; }
    public TimeSpan Budget { get; }
    public IsolatedGatewayStatus? LastStatus { get; }

    private static string FormatMessage(
        NativeGatewayStartupTimeoutPhase phase, TimeSpan budget, IsolatedGatewayStatus? lastStatus)
    {
        int seconds = (int)Math.Round(budget.TotalSeconds);
        string lead = phase == NativeGatewayStartupTimeoutPhase.StartRequest
            ? $"The Gateway package did not finish its start request within {seconds} seconds."
            : $"The package-managed Gateway did not report a verified listener within {seconds} seconds of starting.";
        string status = lastStatus is null ? "" : $" Last status: {lastStatus.Describe()}.";
        return lead + status + " Run clawctl gateway-service status or clawctl collect-logs for details, then retry.";
    }
}
