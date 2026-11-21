namespace OpenClaw.Connection.NativeGateway;

/// <summary>Which part of a package start exhausted the startup budget.</summary>
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
    public NativeGatewayStartupTimeoutException(
        NativeGatewayStartupTimeoutPhase phase,
        TimeSpan budget,
        IsolatedGatewayStatus? lastStatus,
        Exception? innerException = null,
        bool existingStart = false)
        : base(FormatMessage(phase, budget, lastStatus, existingStart), innerException)
    {
        Phase = phase;
        Budget = budget;
        LastStatus = lastStatus;
    }

    public NativeGatewayStartupTimeoutPhase Phase { get; }
    public TimeSpan Budget { get; }
    public IsolatedGatewayStatus? LastStatus { get; }
    /// <summary>
    /// True when the budget was spent observing a start Companion did not request. Companion owns
    /// nothing to roll back, so a later attempt may observe the package again.
    /// </summary>
    public bool ExternalStart { get; init; }

    private static string FormatMessage(
        NativeGatewayStartupTimeoutPhase phase, TimeSpan budget, IsolatedGatewayStatus? lastStatus, bool existingStart)
    {
        int seconds = (int)Math.Round(budget.TotalSeconds);
        string lead = existingStart
            // The time may have gone to package status reads; Companion did not request this start.
            ? $"The package-managed Gateway was already starting and did not report a verified listener within {seconds} seconds."
            : phase == NativeGatewayStartupTimeoutPhase.StartRequest
                ? $"The Gateway package did not finish its start request within {seconds} seconds."
                : $"The package-managed Gateway did not report a verified listener within {seconds} seconds of starting.";
        string status = lastStatus is null ? "" : $" Last status: {lastStatus.Describe()}.";
        return lead + status + " Run clawctl gateway-service status or clawctl collect-logs for details, then retry.";
    }
}
