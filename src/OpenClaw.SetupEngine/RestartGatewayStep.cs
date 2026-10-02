namespace OpenClaw.SetupEngine;

public sealed class RestartGatewayStep : SetupStep
{
    private readonly Func<SetupContext, CancellationToken, Task<StepResult>> _restart;

    public RestartGatewayStep()
        : this(StartGatewayStep.RestartAndWaitForHealthAsync)
    {
    }

    internal RestartGatewayStep(
        Func<SetupContext, CancellationToken, Task<StepResult>> restart) =>
        _restart = restart ?? throw new ArgumentNullException(nameof(restart));

    public override string Id => "restart-gateway";
    public override string DisplayName => "Restart gateway";
    public override RetryPolicy Retry => new(MaxAttempts: 3, InitialDelay: TimeSpan.FromSeconds(3));

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(ctx.Config.LocalAiRecoveryGatewayId))
            ctx.LocalAiRecoveryStoppedWsl = true;

        StepResult result = await _restart(ctx, ct);
        if (result.IsSuccess)
            ctx.LocalAiRecoveryStoppedWsl = false;
        return result;
    }

    public override Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(ctx.Config.LocalAiRecoveryGatewayId))
        {
            // Configuration rollback runs after this step in reverse order. Arm the
            // early recovery guard to restart the Gateway once the prior route is restored.
            ctx.LocalAiRecoveryStoppedWsl = true;
        }
        return Task.CompletedTask;
    }
}
