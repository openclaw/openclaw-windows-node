namespace OpenClaw.SetupEngine;

/// <summary>Bounds read-only UI waits without releasing ownership of admitted mutations.</summary>
public sealed class SetupPageRequestDrain
{
    public static readonly TimeSpan PresentationTimeout = TimeSpan.FromSeconds(5);
    public Task ObservationCompleted { get; private set; } = Task.CompletedTask;

    public static bool RequiresOwnership(GatewayAiSetupPhase? phase, bool localMutation) =>
        localMutation || phase is null or GatewayAiSetupPhase.Running or GatewayAiSetupPhase.Uncertain or
            GatewayAiSetupPhase.VerificationRequired or GatewayAiSetupPhase.Prepared;

    public async Task DrainAsync(Task request, bool retainOwnership, Action<bool> reportDelay,
        TimeProvider? timeProvider = null)
    {
        try { await request.WaitAsync(PresentationTimeout, timeProvider ?? TimeProvider.System); }
        catch (TimeoutException) when (!request.IsCompleted)
        {
            reportDelay(retainOwnership);
            if (retainOwnership)
                await request;
            else
                ObservationCompleted = ObserveReadAsync(request);
        }
    }

    private static async Task ObserveReadAsync(Task request)
    {
        try { await request; }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning("Closed setup read finished ({0}).", error.GetType().Name);
        }
    }
}
