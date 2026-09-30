using OpenClaw.Connection;
using OpenClaw.TestSupport;
using OpenClawTray.Services;

namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupPipelineSettlementTests
{
    [Theory]
    [InlineData(PipelineOutcome.Success)]
    [InlineData(PipelineOutcome.Failed)]
    [InlineData(PipelineOutcome.Cancelled)]
    public async Task SettlementFailureRetainsTheOriginalResultAndFailsOverall(PipelineOutcome outcome)
    {
        var result = new PipelineResult(outcome, "original-step", "original diagnostics") { RequiresRestart = true };
        var settlement = new IOException("registry settlement diagnostics");
        var calls = 0;
        var error = await Assert.ThrowsAsync<SetupPipelineSettlementException>(() =>
            SetupPipeline.RunWithSettlementAsync(() => Task.FromResult(result), observed =>
            {
                calls++;
                Assert.Same(result, observed);
                throw settlement;
            }));
        Assert.Equal(1, calls);
        Assert.Same(result, error.OriginalResult);
        Assert.Null(error.RunFailure);
        Assert.Same(settlement, error.SettlementFailure);
        Assert.Equal([settlement], error.InnerExceptions);
        Assert.Contains(outcome.ToString(), error.Message);
        Assert.Contains("original-step", error.Message);
        Assert.Contains("original diagnostics", error.Message);
        Assert.Contains("registry settlement diagnostics", error.Message);
        Assert.True(Assert.IsType<PipelineResult>(error.OriginalResult).RequiresRestart);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrownRunAndSettlementFailuresBothSurviveIncludingCancellation(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception runFailure = cancel
            ? new OperationCanceledException("cancelled run", cancellation.Token)
            : new InvalidOperationException("original run failure");
        var settlement = new IOException("settlement failed");
        var calls = 0;
        var error = await Assert.ThrowsAsync<SetupPipelineSettlementException>(() =>
            SetupPipeline.RunWithSettlementAsync(() => Task.FromException<PipelineResult>(runFailure), observed =>
            {
                calls++;
                Assert.Null(observed);
                throw settlement;
            }));
        Assert.Equal(1, calls);
        Assert.Null(error.OriginalResult);
        Assert.Same(runFailure, error.RunFailure);
        Assert.Equal([runFailure, settlement], error.InnerExceptions);
        if (cancel)
            Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error.RunFailure).CancellationToken);
    }

    [Fact]
    public async Task SuccessfulSettlementReturnsSameResultOrRethrowsOriginalRunFailureOnce()
    {
        var result = new PipelineResult(PipelineOutcome.Failed, "keep", "unchanged");
        var calls = 0;
        var returned = await SetupPipeline.RunWithSettlementAsync(() => Task.FromResult(result), observed =>
        {
            calls++;
            Assert.Same(result, observed);
            return Task.CompletedTask;
        });
        Assert.Same(result, returned);
        var original = new IOException("same exception");
        var thrown = await Assert.ThrowsAsync<IOException>(() =>
            SetupPipeline.RunWithSettlementAsync(() => Task.FromException<PipelineResult>(original), observed =>
            {
                calls++;
                Assert.Null(observed);
                return Task.CompletedTask;
            }));
        Assert.Same(original, thrown);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FailedOrCancelledPipelineSettlesBeforeClosedOwnerFinishes(bool cancel, bool rollbackFails)
    {
        using var temp = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        var registry = new GatewayRegistry(temp.Path);
        registry.AddOrUpdate(new() { Id = "old", Url = "wss://old.example", IsLocal = true, SetupManagedDistroName = "Managed" });
        registry.SetActive("old");
        registry.Save();
        var callbacks = 0;
        var host = Host(registry, (_, _) => { callbacks++; return Task.CompletedTask; });
        using var logger = new SetupLogger(filePath: null);
        using var journal = new TransactionJournal(filePath: null);
        var ctx = new SetupContext(new SetupConfig { RollbackOnFailure = true, DistroName = "Managed" }, logger, journal,
            new CommandRunner(logger), cancellation.Token, temp.Path, temp.Path)
        { ExpectedGatewayRegistry = host.BeginGatewaySetup() };
        var closed = false;
        var pipeline = new SetupPipeline([new WriterStep(rollbackFails), new FailureStep(() =>
        {
            closed = true;
            if (cancel) cancellation.Cancel();
        })]);
        var changed = 0;
        registry.Changed += (_, _) => changed++;
        var result = await SetupPipeline.RunWithSettlementAsync(() => pipeline.RunAsync(ctx),
            _ =>
            {
                Assert.True(closed);
                return host.ReconcileGatewaySetupAsync(ctx.ExpectedGatewayRegistry!, null);
            });
        Assert.NotEqual(PipelineOutcome.Success, result.Outcome);
        Assert.Equal(1, callbacks);
        Assert.Equal(1, changed);
        Assert.Equal(rollbackFails ? "created" : null, registry.ActiveGatewayId);
        registry.AddOrUpdate(new() { Id = "normal", Url = "wss://normal.example" });
        registry.SetActive("normal");
        registry.Save();
        Assert.Equal("normal", registry.CapturePersistedSnapshot().ActiveId);
    }

    [Fact]
    public async Task ExternalConflictPreservesLiveEditsAndExplicitReloadRecoversSaving()
    {
        using var temp = new TempDirectory();
        var registry = new GatewayRegistry(temp.Path);
        registry.AddOrUpdate(new() { Id = "old", Url = "wss://old.example" });
        registry.SetActive("old");
        registry.Save();
        var failures = 0;
        var host = Host(registry, failure: () => failures++);
        var baseline = host.BeginGatewaySetup();
        var external = new GatewayRegistry(temp.Path);
        external.Load();
        external.UpdateAndSave("old", value => value with { Url = "wss://new.example" });
        registry.Update("old", value => value with { FriendlyName = "unsaved-live-edit" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ReconcileGatewaySetupAsync(baseline, null));
        Assert.Equal("unsaved-live-edit", registry.GetActive()!.FriendlyName);
        Assert.Equal(1, failures);
        // Explicit user recovery can discard that admitted draft and load the saved selection.
        registry.AdoptPersistedSnapshot(registry.GetSnapshot());
        registry.UpdateAndSave("old", value => value with { FriendlyName = "after-reload" });
        Assert.Equal("wss://new.example", registry.CapturePersistedSnapshot().Records[0].Url);
    }

    private static SetupLocalAiHost Host(GatewayRegistry registry,
        Func<GatewayRecord?, GatewayRecord?, Task>? reconcile = null, Action? failure = null) =>
        new(() => throw new NotSupportedException(), () => registry, () => null,
            _ => throw new NotSupportedException(), (_, _) => throw new NotSupportedException(),
            _ => throw new NotSupportedException(), () => throw new NotSupportedException(), reconcile, failure);

    private sealed class WriterStep(bool rollbackFails) : SetupStep
    {
        public override string Id => "writer";
        public override string DisplayName => Id;
        public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
        {
            var registry = ctx.LoadSetupRegistry();
            registry.AddOrUpdate(new() { Id = "created", Url = "wss://created.example", IsLocal = true, SetupManagedDistroName = ctx.DistroName });
            registry.SetActive("created");
            ctx.SaveSetupRegistry(registry);
            ctx.GatewayRecordId = "created";
            return Task.FromResult(StepResult.Ok("Saved owned output"));
        }
        public override Task RollbackAsync(SetupContext ctx, CancellationToken ct)
        {
            var registry = ctx.LoadSetupRegistry();
            if (rollbackFails) throw new IOException("Identity deletion failed before rollback save");
            foreach (var record in registry.GetAll().Where(record => PairOperatorStep.IsSetupManagedLocalRecord(record, ctx)))
                registry.Remove(record.Id);
            ctx.SaveSetupRegistry(registry);
            return Task.CompletedTask;
        }
    }

    private sealed class FailureStep(Action fail) : SetupStep
    {
        public override string Id => "failure";
        public override string DisplayName => Id;
        public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
        {
            fail();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(StepResult.Terminal("Synthetic pipeline failure"));
        }
    }
}
