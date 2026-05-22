using System.Diagnostics;

namespace OpenClaw.SetupEngine;

// ─── Abstract Step Base ───

public abstract class SetupStep
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }

    public abstract Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct);

    public virtual Task RollbackAsync(SetupContext ctx, CancellationToken ct) => Task.CompletedTask;
    public virtual bool CanSkip(SetupContext ctx) => false;
    public virtual bool CanRetry => true;
    public virtual RetryPolicy Retry => RetryPolicy.Default;
}

// ─── Pipeline Result ───

public enum PipelineOutcome { Success, Failed, Cancelled }

public sealed record PipelineResult(PipelineOutcome Outcome, string? FailedStepId = null, string? Message = null)
{
    public int ExitCode => Outcome switch
    {
        PipelineOutcome.Success => 0,
        PipelineOutcome.Failed => 1,
        PipelineOutcome.Cancelled => 3,
        _ => 1
    };
}

// ─── Pipeline Events ───

public sealed record StepProgressEvent(string StepId, string DisplayName, StepOutcome? Outcome, TimeSpan? Elapsed);

// ─── Setup Pipeline ───

public sealed class SetupPipeline
{
    private readonly List<SetupStep> _steps;
    private readonly List<SetupStep> _completedSteps = new();

    public event EventHandler<StepProgressEvent>? StepProgress;

    public SetupPipeline(IEnumerable<SetupStep> steps)
    {
        _steps = steps.ToList();
    }

    public async Task<PipelineResult> RunAsync(SetupContext ctx)
    {
        var ct = ctx.CancellationToken;
        ctx.Journal.RecordPipelineEvent("pipeline_started", $"steps={_steps.Count}");
        ctx.Logger.Info($"Pipeline starting with {_steps.Count} steps", new { run_id = ctx.Logger.RunId });

        var pipelineSw = Stopwatch.StartNew();

        foreach (var step in _steps)
        {
            if (ct.IsCancellationRequested)
            {
                ctx.Journal.RecordPipelineEvent("pipeline_cancelled");
                return new PipelineResult(PipelineOutcome.Cancelled);
            }

            // Check if step can be skipped
            if (step.CanSkip(ctx))
            {
                ctx.Logger.Info($"Skipping step: {step.DisplayName}");
                ctx.Journal.RecordStepCompleted(step.Id, StepOutcome.Skipped, TimeSpan.Zero, "precondition met");
                StepProgress?.Invoke(this, new(step.Id, step.DisplayName, StepOutcome.Skipped, null));
                continue;
            }

            // Execute with retry
            ctx.Logger.StepStarted(step.Id, step.DisplayName);
            ctx.Journal.RecordStepStarted(step.Id);
            StepProgress?.Invoke(this, new(step.Id, step.DisplayName, null, null));

            var sw = Stopwatch.StartNew();
            StepResult result;

            if (step.CanRetry && step.Retry.MaxAttempts > 1)
            {
                result = await RetryExecutor.ExecuteWithRetry(
                    () => step.ExecuteAsync(ctx, ct),
                    step.Retry,
                    ctx.Logger,
                    step.Id,
                    ct);
            }
            else
            {
                try
                {
                    result = await step.ExecuteAsync(ctx, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    ctx.Journal.RecordPipelineEvent("pipeline_cancelled", $"during step {step.Id}");
                    return new PipelineResult(PipelineOutcome.Cancelled);
                }
                catch (Exception ex)
                {
                    result = StepResult.Fail($"Unhandled exception: {ex.Message}", ex);
                }
            }

            sw.Stop();
            ctx.Logger.StepCompleted(step.Id, result, sw.Elapsed);
            ctx.Journal.RecordStepCompleted(step.Id, result.Outcome, sw.Elapsed, result.Message);
            StepProgress?.Invoke(this, new(step.Id, step.DisplayName, result.Outcome, sw.Elapsed));

            if (result.IsSuccess)
            {
                _completedSteps.Add(step);
                continue;
            }

            // Step failed — handle rollback if configured
            ctx.Logger.Error($"Step '{step.Id}' failed: {result.Message}");

            if (ctx.Config.RollbackOnFailure)
            {
                await RollbackCompletedSteps(ctx);
            }

            ctx.Journal.RecordPipelineEvent("pipeline_failed", $"step={step.Id}, message={result.Message}");
            return new PipelineResult(PipelineOutcome.Failed, step.Id, result.Message);
        }

        pipelineSw.Stop();
        ctx.Journal.RecordPipelineEvent("pipeline_completed", $"elapsed={pipelineSw.Elapsed.TotalSeconds:F1}s");
        ctx.Logger.Info($"Pipeline completed successfully in {pipelineSw.Elapsed.TotalSeconds:F1}s");
        return new PipelineResult(PipelineOutcome.Success);
    }

    private async Task RollbackCompletedSteps(SetupContext ctx)
    {
        ctx.Logger.Warn($"Rolling back {_completedSteps.Count} completed steps");
        for (int i = _completedSteps.Count - 1; i >= 0; i--)
        {
            var step = _completedSteps[i];
            try
            {
                ctx.Logger.Info($"Rolling back: {step.DisplayName}");
                await step.RollbackAsync(ctx, CancellationToken.None);
                ctx.Journal.RecordRollback(step.Id, success: true);
            }
            catch (Exception ex)
            {
                ctx.Logger.Error($"Rollback failed for {step.Id}: {ex.Message}");
                ctx.Journal.RecordRollback(step.Id, success: false);
            }
        }
    }
}
