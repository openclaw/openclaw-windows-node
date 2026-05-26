namespace OpenClaw.SetupEngine.Tests;

public class RetryExecutorTests
{
    private SetupLogger CreateLogger() => new(filePath: null, LogLevel.Trace);

    [Fact]
    public async Task ExecuteWithRetry_SuccessOnFirstAttempt_ReturnsSuccess()
    {
        using var logger = CreateLogger();
        var result = await RetryExecutor.ExecuteWithRetry(
            () => Task.FromResult(StepResult.Ok("done")),
            RetryPolicy.Default,
            logger, "test-step", CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal("done", result.Message);
    }

    [Fact]
    public async Task ExecuteWithRetry_FailsThenSucceeds_RetriesAndReturnsSuccess()
    {
        using var logger = CreateLogger();
        int attempts = 0;

        var result = await RetryExecutor.ExecuteWithRetry(
            () =>
            {
                attempts++;
                return Task.FromResult(attempts < 3
                    ? StepResult.Fail("not yet")
                    : StepResult.Ok("success on 3rd"));
            },
            new RetryPolicy(MaxAttempts: 3, InitialDelay: TimeSpan.FromMilliseconds(1)),
            logger, "test-step", CancellationToken.None);

        Assert.Equal(3, attempts);
        Assert.Equal(StepOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task ExecuteWithRetry_AllAttemptsFail_ReturnsLastFailure()
    {
        using var logger = CreateLogger();
        int attempts = 0;

        var result = await RetryExecutor.ExecuteWithRetry(
            () =>
            {
                attempts++;
                return Task.FromResult(StepResult.Fail($"fail #{attempts}"));
            },
            new RetryPolicy(MaxAttempts: 3, InitialDelay: TimeSpan.FromMilliseconds(1)),
            logger, "test-step", CancellationToken.None);

        Assert.Equal(3, attempts);
        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Equal("fail #3", result.Message);
    }

    [Fact]
    public async Task ExecuteWithRetry_TerminalFailure_StopsRetrying()
    {
        using var logger = CreateLogger();
        int attempts = 0;

        var result = await RetryExecutor.ExecuteWithRetry(
            () =>
            {
                attempts++;
                return Task.FromResult(StepResult.Terminal("unrecoverable"));
            },
            new RetryPolicy(MaxAttempts: 5, InitialDelay: TimeSpan.FromMilliseconds(1)),
            logger, "test-step", CancellationToken.None);

        Assert.Equal(1, attempts);
        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
    }

    [Fact]
    public async Task ExecuteWithRetry_ExceptionInAction_CatchesAndRetries()
    {
        using var logger = CreateLogger();
        int attempts = 0;

        var result = await RetryExecutor.ExecuteWithRetry(
            () =>
            {
                attempts++;
                if (attempts < 3)
                    throw new InvalidOperationException("boom");
                return Task.FromResult(StepResult.Ok("recovered"));
            },
            new RetryPolicy(MaxAttempts: 3, InitialDelay: TimeSpan.FromMilliseconds(1)),
            logger, "test-step", CancellationToken.None);

        Assert.Equal(3, attempts);
        Assert.Equal(StepOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task ExecuteWithRetry_ExceptionExhaustsRetries_ReturnsFailWithExceptionMessage()
    {
        using var logger = CreateLogger();

        var result = await RetryExecutor.ExecuteWithRetry(
            () => throw new InvalidOperationException("always fails"),
            new RetryPolicy(MaxAttempts: 2, InitialDelay: TimeSpan.FromMilliseconds(1)),
            logger, "test-step", CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("always fails", result.Message);
    }

    [Fact]
    public async Task ExecuteWithRetry_CancellationBeforeStart_ThrowsOCE()
    {
        using var logger = CreateLogger();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            RetryExecutor.ExecuteWithRetry(
                () => Task.FromResult(StepResult.Ok()),
                RetryPolicy.Default,
                logger, "test-step", cts.Token));
    }

    [Fact]
    public async Task ExecuteWithRetry_CancellationDuringAction_PropagatesCancellation()
    {
        using var logger = CreateLogger();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            RetryExecutor.ExecuteWithRetry(
                () =>
                {
                    cts.Cancel();
                    cts.Token.ThrowIfCancellationRequested();
                    return Task.FromResult(StepResult.Ok());
                },
                new RetryPolicy(MaxAttempts: 3, InitialDelay: TimeSpan.FromMilliseconds(1)),
                logger, "test-step", cts.Token));
    }

    [Fact]
    public async Task ExecuteWithRetry_NoRetryPolicy_RunsOnce()
    {
        using var logger = CreateLogger();
        int attempts = 0;

        var result = await RetryExecutor.ExecuteWithRetry(
            () =>
            {
                attempts++;
                return Task.FromResult(StepResult.Fail("no retry"));
            },
            RetryPolicy.None,
            logger, "test-step", CancellationToken.None);

        Assert.Equal(1, attempts);
        Assert.Equal(StepOutcome.Failed, result.Outcome);
    }

    [Fact]
    public void RetryPolicy_Default_Has3Attempts()
    {
        Assert.Equal(3, RetryPolicy.Default.MaxAttempts);
    }

    [Fact]
    public void RetryPolicy_None_Has1Attempt()
    {
        Assert.Equal(1, RetryPolicy.None.MaxAttempts);
    }
}
