namespace OpenClaw.SetupEngine;

public sealed class RestartGatewayStep : SetupStep
{
    public override string Id => "restart-gateway";
    public override string DisplayName => "Restart gateway";
    public override RetryPolicy Retry => new(MaxAttempts: 3, InitialDelay: TimeSpan.FromSeconds(3));

    public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct) =>
        StartGatewayStep.RestartAndWaitForHealthAsync(ctx, ct);
}
