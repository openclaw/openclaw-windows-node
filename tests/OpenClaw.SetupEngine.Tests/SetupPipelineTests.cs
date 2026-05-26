namespace OpenClaw.SetupEngine.Tests;

public class SetupPipelineTests
{
    private SetupLogger CreateLogger() => new(filePath: null, LogLevel.Trace);

    private SetupContext CreateContext(SetupConfig? config = null, CancellationToken ct = default)
    {
        var cfg = config ?? new SetupConfig();
        var logger = CreateLogger();
        var journal = new TransactionJournal(filePath: null);
        var commands = new CommandRunner(logger);
        return new SetupContext(cfg, logger, journal, commands, ct);
    }

    // A mock step for testing
    private sealed class MockStep : SetupStep
    {
        private readonly Func<SetupContext, CancellationToken, Task<StepResult>> _execute;
        private readonly Func<SetupContext, CancellationToken, Task>? _rollback;
        private readonly bool _canSkip;

        public override string Id { get; }
        public override string DisplayName { get; }
        public override bool CanRetry => false;

        public MockStep(string id, Func<SetupContext, CancellationToken, Task<StepResult>> execute,
            Func<SetupContext, CancellationToken, Task>? rollback = null,
            bool canSkip = false)
        {
            Id = id;
            DisplayName = id;
            _execute = execute;
            _rollback = rollback;
            _canSkip = canSkip;
        }

        public override bool CanSkip(SetupContext ctx) => _canSkip;
        public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct) => _execute(ctx, ct);
        public override Task RollbackAsync(SetupContext ctx, CancellationToken ct) =>
            _rollback?.Invoke(ctx, ct) ?? Task.CompletedTask;
    }

    [Fact]
    public async Task RunAsync_AllStepsSucceed_ReturnsSuccess()
    {
        var ctx = CreateContext();
        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => Task.FromResult(StepResult.Ok())),
            new MockStep("s2", (_, _) => Task.FromResult(StepResult.Ok())),
        ]);

        var result = await pipeline.RunAsync(ctx);
        Assert.Equal(PipelineOutcome.Success, result.Outcome);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void BuildDefaultSteps_IncludesCurrentSetupFlow()
    {
        var steps = SetupStepFactory.BuildDefaultSteps();

        Assert.Equal(18, steps.Count);
        Assert.Contains(steps, s => s is ValidateWslLockdownStep);
        Assert.Contains(steps, s => s is RunGatewayWizardStep);
        Assert.IsType<StartKeepaliveStep>(steps[^1]);
    }

    [Fact]
    public async Task RunAsync_StepFails_ReturnsFailed()
    {
        var ctx = CreateContext();
        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => Task.FromResult(StepResult.Ok())),
            new MockStep("s2", (_, _) => Task.FromResult(StepResult.Fail("broken"))),
            new MockStep("s3", (_, _) => Task.FromResult(StepResult.Ok())),
        ]);

        var result = await pipeline.RunAsync(ctx);
        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal("s2", result.FailedStepId);
        Assert.Equal("broken", result.Message);
    }

    [Fact]
    public async Task RunAsync_StepFails_WithRollback_CallsRollbackInReverseOrder()
    {
        var rollbackOrder = new List<string>();
        var config = new SetupConfig { RollbackOnFailure = true };
        var ctx = CreateContext(config);

        var pipeline = new SetupPipeline([
            new MockStep("s1",
                (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { rollbackOrder.Add("s1"); return Task.CompletedTask; }),
            new MockStep("s2",
                (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { rollbackOrder.Add("s2"); return Task.CompletedTask; }),
            new MockStep("s3",
                (_, _) => Task.FromResult(StepResult.Fail("fail"))),
        ]);

        var result = await pipeline.RunAsync(ctx);
        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal(["s2", "s1"], rollbackOrder);
    }

    [Fact]
    public async Task RunAsync_StepFails_WithRollback_CleansUpFailedStepFirst()
    {
        var rollbackOrder = new List<string>();
        var config = new SetupConfig { RollbackOnFailure = true };
        var ctx = CreateContext(config);

        var pipeline = new SetupPipeline([
            new MockStep("s1",
                (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { rollbackOrder.Add("s1"); return Task.CompletedTask; }),
            new MockStep("s2",
                (_, _) => Task.FromResult(StepResult.Fail("fail")),
                (_, _) => { rollbackOrder.Add("s2"); return Task.CompletedTask; }),
        ]);

        var result = await pipeline.RunAsync(ctx);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal(["s2", "s1"], rollbackOrder);
        Assert.Contains(ctx.Journal.Entries, e => e.StepId == "s2" && e.Event == "rollback_ok");
    }

    [Fact]
    public async Task RunAsync_StepFails_WithRollback_ContinuesWhenOneRollbackFails()
    {
        var rollbackOrder = new List<string>();
        var config = new SetupConfig { RollbackOnFailure = true };
        var ctx = CreateContext(config);

        var pipeline = new SetupPipeline([
            new MockStep("s1",
                (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { rollbackOrder.Add("s1"); return Task.CompletedTask; }),
            new MockStep("s2",
                (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) =>
                {
                    rollbackOrder.Add("s2");
                    throw new InvalidOperationException("rollback failed");
                }),
            new MockStep("s3",
                (_, _) => Task.FromResult(StepResult.Fail("fail"))),
        ]);

        var result = await pipeline.RunAsync(ctx);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal(["s2", "s1"], rollbackOrder);
        Assert.Contains(ctx.Journal.Entries, e => e.StepId == "s2" && e.Event == "rollback_failed");
        Assert.Contains(ctx.Journal.Entries, e => e.StepId == "s1" && e.Event == "rollback_ok");
    }

    [Fact]
    public async Task RunAsync_StepFails_WithRollback_TimesOutHungRollback()
    {
        var rollbackCalled = false;
        var config = new SetupConfig { RollbackOnFailure = true, RollbackTimeoutSeconds = 1 };
        var ctx = CreateContext(config);

        var pipeline = new SetupPipeline([
            new MockStep("s1",
                (_, _) => Task.FromResult(StepResult.Ok()),
                async (_, ct) =>
                {
                    rollbackCalled = true;
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }),
            new MockStep("s2", (_, _) => Task.FromResult(StepResult.Fail("fail"))),
        ]);

        var result = await pipeline.RunAsync(ctx);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.True(rollbackCalled);
        Assert.Contains(ctx.Journal.Entries, e => e.StepId == "s1" && e.Event == "rollback_failed");
    }

    [Fact]
    public async Task RunAsync_StepFails_WithoutRollbackConfig_NoRollback()
    {
        var rollbackCalled = false;
        var config = new SetupConfig { RollbackOnFailure = false };
        var ctx = CreateContext(config);

        var pipeline = new SetupPipeline([
            new MockStep("s1",
                (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { rollbackCalled = true; return Task.CompletedTask; }),
            new MockStep("s2",
                (_, _) => Task.FromResult(StepResult.Fail("fail"))),
        ]);

        await pipeline.RunAsync(ctx);
        Assert.False(rollbackCalled);
    }

    [Fact]
    public async Task RunAsync_SkippableStep_IsSkipped()
    {
        var executed = false;
        var ctx = CreateContext();
        var stepEvents = new List<StepProgressEvent>();

        var pipeline = new SetupPipeline([
            new MockStep("s1",
                (_, _) => { executed = true; return Task.FromResult(StepResult.Ok()); },
                canSkip: true),
        ]);
        
        pipeline.StepProgress += (sender, e) => stepEvents.Add(e);

        var result = await pipeline.RunAsync(ctx);
        Assert.Equal(PipelineOutcome.Success, result.Outcome);
        Assert.False(executed, "Step should not have executed when canSkip is true");
        
        // Verify the step was actually skipped via progress events
        var stepEvent = Assert.Single(stepEvents);
        Assert.Equal(StepOutcome.Skipped, stepEvent.Outcome);
    }

    [Fact]
    public async Task RunAsync_Cancellation_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ctx = CreateContext(ct: cts.Token);

        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => Task.FromResult(StepResult.Ok())),
        ]);

        var result = await pipeline.RunAsync(ctx);
        Assert.Equal(PipelineOutcome.Cancelled, result.Outcome);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_StepThrowsException_ReturnsFail()
    {
        var ctx = CreateContext();
        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => throw new InvalidOperationException("unexpected")),
        ]);

        var result = await pipeline.RunAsync(ctx);
        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Contains("unexpected", result.Message);
    }

    [Fact]
    public async Task RunAsync_EmitsStepProgress()
    {
        var events = new List<StepProgressEvent>();
        var ctx = CreateContext();
        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => Task.FromResult(StepResult.Ok())),
        ]);
        pipeline.StepProgress += (_, e) => events.Add(e);

        await pipeline.RunAsync(ctx);

        Assert.Equal(2, events.Count); // started + completed
        Assert.Null(events[0].Outcome); // started event has no outcome
        Assert.Equal(StepOutcome.Success, events[1].Outcome);
    }

    [Fact]
    public async Task RunAsync_RecordsJournal()
    {
        var ctx = CreateContext();
        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => Task.FromResult(StepResult.Ok())),
        ]);

        await pipeline.RunAsync(ctx);

        // Should have pipeline_started, step started, step completed, pipeline_completed
        Assert.True(ctx.Journal.Entries.Count >= 3);
        Assert.Equal("pipeline_started", ctx.Journal.Entries[0].Event);
    }

    [Fact]
    public async Task UninstallAsync_RequiresConfirmDestructive()
    {
        var config = new SetupConfig { ConfirmDestructive = false };
        var ctx = CreateContext(config);
        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => Task.FromResult(StepResult.Ok())),
        ]);

        var result = await pipeline.UninstallAsync(ctx);
        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Contains("confirm-destructive", result.Message);
    }

    [Fact]
    public async Task UninstallAsync_DryRun_DoesNotRequireConfirmDestructive()
    {
        var config = new SetupConfig { ConfirmDestructive = false, DryRun = true };
        var ctx = CreateContext(config);
        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => Task.FromResult(StepResult.Ok())),
        ]);

        var result = await pipeline.UninstallAsync(ctx);

        Assert.Equal(PipelineOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task UninstallAsync_RunsRollbacksInReverse()
    {
        var order = new List<string>();
        var config = new SetupConfig { ConfirmDestructive = true };
        var ctx = CreateContext(config);

        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { order.Add("s1"); return Task.CompletedTask; }),
            new MockStep("s2", (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { order.Add("s2"); return Task.CompletedTask; }),
            new MockStep("s3", (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { order.Add("s3"); return Task.CompletedTask; }),
        ]);

        var result = await pipeline.UninstallAsync(ctx);
        Assert.Equal(PipelineOutcome.Success, result.Outcome);
        Assert.Equal(["s3", "s2", "s1"], order);
    }

    [Fact]
    public async Task UninstallAsync_ContinuesPastFailures()
    {
        var order = new List<string>();
        var config = new SetupConfig { ConfirmDestructive = true };
        var ctx = CreateContext(config);

        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { order.Add("s1"); return Task.CompletedTask; }),
            new MockStep("s2", (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { order.Add("s2"); throw new Exception("rollback failed"); }),
            new MockStep("s3", (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { order.Add("s3"); return Task.CompletedTask; }),
        ]);

        var result = await pipeline.UninstallAsync(ctx);
        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        // All three rollbacks should have been attempted despite s2 failure
        Assert.Equal(["s3", "s2", "s1"], order);
    }

    [Fact]
    public async Task UninstallAsync_RollbackTimeout_ContinuesPastFailure()
    {
        var order = new List<string>();
        var config = new SetupConfig { ConfirmDestructive = true, RollbackTimeoutSeconds = 1 };
        var ctx = CreateContext(config);

        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { order.Add("s1"); return Task.CompletedTask; }),
            new MockStep("s2", (_, _) => Task.FromResult(StepResult.Ok()),
                async (_, ct) =>
                {
                    order.Add("s2");
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }),
            new MockStep("s3", (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { order.Add("s3"); return Task.CompletedTask; }),
        ]);

        var result = await pipeline.UninstallAsync(ctx);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal(["s3", "s2", "s1"], order);
    }

    [Fact]
    public async Task UninstallAsync_DryRun_DoesNotCallRollback()
    {
        var rollbackCalled = false;
        var config = new SetupConfig { ConfirmDestructive = true, DryRun = true };
        var ctx = CreateContext(config);

        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { rollbackCalled = true; return Task.CompletedTask; }),
        ]);

        var result = await pipeline.UninstallAsync(ctx);
        Assert.Equal(PipelineOutcome.Success, result.Outcome);
        Assert.False(rollbackCalled);
    }
}
