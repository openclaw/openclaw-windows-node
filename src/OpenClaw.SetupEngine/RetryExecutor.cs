namespace OpenClaw.SetupEngine;

// ─── Retry Executor ───

public sealed record RetryPolicy(int MaxAttempts = 3, TimeSpan? InitialDelay = null, double BackoffMultiplier = 2.0, TimeSpan? MaxDelay = null)
{
    public static readonly RetryPolicy Default = new(MaxAttempts: 3, InitialDelay: TimeSpan.FromSeconds(2));
    public static readonly RetryPolicy None = new(MaxAttempts: 1);

    public TimeSpan EffectiveInitialDelay => InitialDelay ?? TimeSpan.FromSeconds(2);
    public TimeSpan EffectiveMaxDelay => MaxDelay ?? TimeSpan.FromSeconds(30);
}

public static class RetryExecutor
{
    public static async Task<StepResult> ExecuteWithRetry(
        Func<Task<StepResult>> action,
        RetryPolicy policy,
        SetupLogger logger,
        string stepId,
        CancellationToken ct)
    {
        var delay = policy.EffectiveInitialDelay;
        StepResult? lastResult = null;

        for (int attempt = 1; attempt <= policy.MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                lastResult = await action();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // propagate cancellation
            }
            catch (Exception ex)
            {
                // Logged at Warn here because we may still retry; if we exhaust retries,
                // SetupLogger.StepCompleted will emit the authoritative Error entry with
                // the full exception once the failing StepResult is returned.
                logger.Warn($"Step '{stepId}' threw exception (attempt {attempt}/{policy.MaxAttempts}): {ex.Message}");
                lastResult = StepResult.Fail($"Unhandled exception: {ex.Message}", ex);
            }

            if (lastResult.IsSuccess || lastResult.Outcome == StepOutcome.FailedTerminal)
                return lastResult;

            if (attempt < policy.MaxAttempts)
            {
                logger.Warn($"Step '{stepId}' failed (attempt {attempt}/{policy.MaxAttempts}), retrying in {delay.TotalSeconds:F0}s: {lastResult.Message}");
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromMilliseconds(Math.Min(
                    delay.TotalMilliseconds * policy.BackoffMultiplier,
                    policy.EffectiveMaxDelay.TotalMilliseconds));
            }
        }

        return lastResult ?? StepResult.Fail("Retry exhausted with no result");
    }
}
