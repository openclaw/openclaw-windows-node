namespace OpenClaw.SetupEngine;

/// <summary>
/// Thin orchestrator: all setup-time keepalive process/marker/identity/rollback logic lives in
/// <see cref="KeepaliveProcessManager"/>. This step only maps its result to
/// <see cref="StepResult"/> and wires <see cref="SetupContext"/> values through.
/// </summary>
public sealed class StartKeepaliveStep : SetupStep
{
    private readonly IKeepaliveProcessRuntime _runtime;

    public StartKeepaliveStep() : this(new ProcessKeepaliveRuntime())
    {
    }

    internal StartKeepaliveStep(IKeepaliveProcessRuntime runtime)
    {
        _runtime = runtime;
    }

    public override string Id => "start-keepalive";
    public override string DisplayName => "Start WSL keepalive";

    public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var manager = CreateManager(ctx);
        var result = manager.EnsureStarted();

        // Starting the keepalive is a soft best-effort action — a failed start (or an already-
        // running keepalive) never fails this step; the tray starts its own keepalive on launch.
        return Task.FromResult(result switch
        {
            KeepaliveStartResult.AlreadyRunning => StepResult.Ok("Keepalive already running"),
            KeepaliveStartResult.Started => StepResult.Ok(),
            KeepaliveStartResult.FailedToStart => StepResult.Ok(),
            _ => throw new InvalidOperationException($"Unknown keepalive result: {result.GetType().Name}")
        });
    }

    public override Task RollbackAsync(SetupContext ctx, CancellationToken ct)
        => CreateManager(ctx).RollbackAsync(ct);

    private KeepaliveProcessManager CreateManager(SetupContext ctx)
        => new(ctx.DistroName, ctx.LocalDataDir, WslConstants.WslExePath, ctx.Logger, _runtime);
}
