using System.Collections.Immutable;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public class SetupPipelineTests
{
    private SetupLogger CreateLogger() => new(filePath: null, LogLevel.Trace);

    private SetupContext CreateContext(SetupConfig? config = null, CancellationToken ct = default, string? localDataDir = null)
    {
        var cfg = config ?? new SetupConfig();
        var logger = CreateLogger();
        var journal = new TransactionJournal(filePath: null);
        var commands = new CommandRunner(logger);
        return new SetupContext(cfg, logger, journal, commands, ct, localDataDir: localDataDir);
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
    public async Task RunAsync_CompatibilityFailure_PreservesTypedTerminalReason()
    {
        var compatibilityError = new GatewayCompatibilityException(
            GatewayCompatibilityFailureKind.ProtocolMismatch,
            "Expected protocol v4.");
        var pipeline = new SetupPipeline([
            new MockStep(
                "compatibility",
                (_, _) => Task.FromResult(
                    StepResult.Terminal(compatibilityError.Message, compatibilityError))),
        ]);

        var result = await pipeline.RunAsync(CreateContext());

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal(GatewayCompatibilityFailureKind.ProtocolMismatch, result.CompatibilityFailure);
    }

    [Fact]
    public async Task RunAsync_FailureDiagnosticRunsBeforeRollback_WithoutChangingFailure()
    {
        var order = new List<string>();
        var pipeline = new SetupPipeline(
            [new MockStep(
                "wizard",
                (_, _) => Task.FromResult(StepResult.Fail("original failure")),
                (_, _) => { order.Add("failed-step-rollback"); return Task.CompletedTask; })],
            rollbackOnFailureOverride: true,
            (_, stepId, result) =>
            {
                Assert.Equal("wizard", stepId);
                Assert.Equal("original failure", result.Message);
                order.Add("diagnostic");
                return Task.CompletedTask;
            });

        var result = await pipeline.RunAsync(CreateContext());

        Assert.Equal(["diagnostic", "failed-step-rollback"], order);
        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal("original failure", result.Message);
    }

    [Fact]
    public async Task RunAsync_FailingDiagnosticStillRollsBackAndPreservesOriginalFailure()
    {
        var rolledBack = false;
        var pipeline = new SetupPipeline(
            [new MockStep(
                "wizard",
                (_, _) => Task.FromResult(StepResult.Fail("original failure")),
                (_, _) => { rolledBack = true; return Task.CompletedTask; })],
            rollbackOnFailureOverride: true,
            (_, _, _) => throw new InvalidOperationException("diagnostic failure"));

        var result = await pipeline.RunAsync(CreateContext());

        Assert.True(rolledBack);
        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal("original failure", result.Message);
    }

    [Fact]
    public async Task RunAsync_RestartRequired_PreservesTypedTerminalReason()
    {
        var pipeline = new SetupPipeline([
            new MockStep(
                "restart",
                (_, _) => Task.FromResult(StepResult.RestartRequired("restart Windows"))),
        ]);

        var result = await pipeline.RunAsync(CreateContext());
        var (outcome, failedStepId, message, compatibilityFailure, detail) = result;

        Assert.Equal(PipelineOutcome.Failed, outcome);
        Assert.Equal("restart", failedStepId);
        Assert.Equal("restart Windows", message);
        Assert.Null(compatibilityFailure);
        Assert.Null(detail);
        Assert.True(result.RequiresRestart);
    }

    [Fact]
    public void BuildDefaultSteps_IncludesCurrentSetupFlow()
    {
        var steps = SetupStepFactory.BuildDefaultSteps();

        Assert.Equal(34, steps.Count);
        Assert.IsType<ValidateDistroInstallPathStep>(steps[0]);
        Assert.IsType<PreflightOsStep>(steps[1]);
        Assert.IsType<PreflightLocalAiHardwareStep>(steps[2]);
        Assert.IsType<PreflightWslStep>(steps[3]);
        Assert.IsType<PreflightWindowsTailscaleStep>(steps[4]);
        Assert.IsType<EnsureWslPlatformStep>(steps[5]);
        Assert.IsType<ReconcileLocalAiInstallationStep>(steps[6]);
        Assert.IsType<AcquireLocalAiRuntimeStep>(steps[7]);
        Assert.IsType<AcquireLocalAiModelStep>(steps[8]);
        Assert.IsType<PersistLocalAiManifestStep>(steps[9]);
        Assert.IsType<StartLocalAiRuntimeStep>(steps[10]);
        Assert.IsType<ConfigureLocalAiWslNetworkingStep>(steps[11]);
        Assert.IsType<CleanupStaleDistroStep>(steps[12]);
        Assert.IsType<CleanupStaleGatewayStep>(steps[13]);
        Assert.Contains(steps, s => s is ValidateWslLockdownStep);
        var lockdownIndex = steps.FindIndex(s => s is ValidateWslLockdownStep);
        var cliInstallIndex = steps.FindIndex(s => s is InstallCliStep);
        Assert.Equal(lockdownIndex + 1, cliInstallIndex);
        Assert.IsType<VerifyLocalAiWslStep>(steps[cliInstallIndex + 1]);
        Assert.IsType<InstallTailscaleStep>(steps[cliInstallIndex + 2]);
        Assert.IsType<AuthorizeTailscaleStep>(steps[cliInstallIndex + 3]);
        var installServiceIndex = steps.FindIndex(s => s is InstallGatewayServiceStep);
        Assert.IsType<ConfigureLocalAiGatewayStep>(steps[installServiceIndex - 1]);
        Assert.IsType<StartGatewayStep>(steps[installServiceIndex + 1]);
        Assert.IsType<FinalizeTailscaleServeStep>(steps[installServiceIndex + 2]);
        Assert.Contains(steps, s => s is RunGatewayWizardStep);
        var pairNodeIndex = steps.FindIndex(s => s is PairNodeStep);
        Assert.IsType<VerifyEndToEndStep>(steps[pairNodeIndex + 1]);
        var wizardIndex = steps.FindIndex(s => s is RunGatewayWizardStep);
        Assert.IsType<WindowsNodeBootstrapContextStep>(steps[wizardIndex + 1]);
        Assert.IsType<StartKeepaliveStep>(steps[^1]);

        var ensureWslIndex = steps.FindIndex(step => step is EnsureWslPlatformStep);
        var preflightWslIndex = steps.FindIndex(step => step is PreflightWslStep);
        var localAiHardwareIndex = steps.FindIndex(step => step is PreflightLocalAiHardwareStep);
        var runtimeDownloadIndex = steps.FindIndex(step => step is AcquireLocalAiRuntimeStep);
        var modelDownloadIndex = steps.FindIndex(step => step is AcquireLocalAiModelStep);
        Assert.True(localAiHardwareIndex < preflightWslIndex);
        Assert.True(preflightWslIndex < ensureWslIndex);
        Assert.True(ensureWslIndex < runtimeDownloadIndex);
        Assert.True(ensureWslIndex < modelDownloadIndex);
    }

    [Fact]
    public void BuildLocalAiRecoverySteps_PreservesExistingWslGateway()
    {
        var steps = SetupStepFactory.BuildLocalAiRecoverySteps();

        Assert.DoesNotContain(steps, step => step is ValidateDistroInstallPathStep);
        Assert.Equal(2, steps.Count(step => step is ValidateLocalAiRecoveryGatewayStep));
        Assert.Contains(steps, step => step is PreserveLocalAiRecoveryGatewayStep);
        Assert.Contains(steps, step => step is ValidateLocalAiRecoveryGatewayCompatibilityStep);
        Assert.DoesNotContain(steps, step => step is CleanupStaleDistroStep);
        Assert.DoesNotContain(steps, step => step is CleanupStaleGatewayStep);
        Assert.DoesNotContain(steps, step => step is CreateWslInstanceStep);
        Assert.DoesNotContain(steps, step => step is ConfigureWslInstanceStep);
        Assert.DoesNotContain(steps, step => step is InstallCliStep);
        Assert.Contains(steps, step => step is AcquireLocalAiRuntimeStep);
        Assert.Contains(steps, step => step is AcquireLocalAiModelStep);
        Assert.Contains(steps, step => step is VerifyLocalAiWslStep);
        Assert.IsType<ConfigureLocalAiGatewayStep>(steps[^3]);
        Assert.IsType<RestartGatewayStep>(steps[^2]);
        Assert.IsType<FinalizeLocalAiModelReplacementStep>(steps[^1]);
        Assert.True(
            steps.FindIndex(step => step is ReconcileLocalAiInstallationStep) <
            steps.FindIndex(step => step is ValidateLocalAiRecoveryGatewayCompatibilityStep));
        Assert.True(
            steps.FindIndex(step => step is ValidateLocalAiRecoveryGatewayCompatibilityStep) <
            steps.FindIndex(step => step is AcquireLocalAiRuntimeStep));
        Assert.True(
            steps.FindIndex(step => step is PreserveLocalAiRecoveryGatewayStep) <
            steps.FindIndex(step => step is StartLocalAiRuntimeStep));
        Assert.True(
            steps.FindIndex(step => step is PreserveLocalAiRecoveryGatewayStep) <
            steps.FindIndex(step => step is ConfigureLocalAiGatewayStep));
        Assert.True(
            steps.FindIndex(step => step is PreserveLocalAiRecoveryGatewayStep) <
            steps.FindIndex(step => step is ConfigureLocalAiWslNetworkingStep));
        Assert.IsType<PersistLocalAiManifestStep>(
            steps[steps.FindIndex(step => step is PreserveLocalAiRecoveryGatewayStep) - 1]);
    }

    [Fact]
    public async Task ValidateLocalAiRecoveryGateway_MissingDistro_BlocksBeforeRecovery()
    {
        var context = CreateContext(LocalAiRecoveryConfig());
        var step = new ValidateLocalAiRecoveryGatewayStep(
            (_, _, _, _) => ExistingLocalAiGateway(hasDistro: false, appOwned: false),
            _ => [ManagedGatewayRecord()]);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("run full setup", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateRecoveryGatewayCompatibility_AutomaticPortRejectsLegacyGateway()
    {
        using var temp = new TempDirectory("local-ai-compatibility-");
        SetupConfig config = LocalAiRecoveryConfig();
        config.LocalAi.Port = 18801;
        var context = CreateContext(config, localDataDir: temp.Path);
        context.LocalAiRecoveryOriginalInstall = CreateLocalAiResolvedInstall(temp.Path, 18801);
        var step = new ValidateLocalAiRecoveryGatewayCompatibilityStep((_, _) =>
            Task.FromResult(new CommandResult(
                42,
                ValidateLocalAiRecoveryGatewayCompatibilityStep.UnsupportedMarker,
                string.Empty,
                TimeSpan.Zero,
                false)));

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("Update the Gateway", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateRecoveryGatewayCompatibility_NoPriorReceiptSkipsProbe()
    {
        var context = CreateContext(LocalAiRecoveryConfig());
        var probeCalls = 0;
        var step = new ValidateLocalAiRecoveryGatewayCompatibilityStep((_, _) =>
        {
            probeCalls++;
            return Task.FromResult(new CommandResult(1, string.Empty, "failed", TimeSpan.Zero, false));
        });

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Skipped, result.Outcome);
        Assert.Equal(0, probeCalls);
    }

    [Fact]
    public async Task ValidateRecoveryGatewayCompatibility_FixedPortSkipsProbe()
    {
        using var temp = new TempDirectory("local-ai-compatibility-");
        SetupConfig config = LocalAiRecoveryConfig();
        config.LocalAi.Port = 0;
        var context = CreateContext(config, localDataDir: temp.Path);
        LocalAiResolvedInstall install = CreateLocalAiResolvedInstall(temp.Path, 18801);
        context.LocalAiRecoveryOriginalInstall = install with
        {
            Manifest = install.Manifest with { RequestedPort = 18801 },
        };
        var probeCalls = 0;
        var step = new ValidateLocalAiRecoveryGatewayCompatibilityStep((_, _) =>
        {
            probeCalls++;
            return Task.FromResult(new CommandResult(1, string.Empty, "failed", TimeSpan.Zero, false));
        });

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Skipped, result.Outcome);
        Assert.Equal(0, probeCalls);
    }

    [Fact]
    public async Task ValidateRecoveryGatewayCompatibility_AutomaticPortAcceptsConditionalWrites()
    {
        using var temp = new TempDirectory("local-ai-compatibility-");
        var context = CreateContext(LocalAiRecoveryConfig(), localDataDir: temp.Path);
        context.LocalAiRecoveryOriginalInstall = CreateLocalAiResolvedInstall(temp.Path, 18801);
        var step = new ValidateLocalAiRecoveryGatewayCompatibilityStep((_, _) =>
            Task.FromResult(new CommandResult(
                0,
                ValidateLocalAiRecoveryGatewayCompatibilityStep.SupportedMarker,
                string.Empty,
                TimeSpan.Zero,
                false)));

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task ValidateRecoveryGatewayCompatibility_ChecksAutomaticRollbackReceipt()
    {
        using var temp = new TempDirectory("local-ai-compatibility-");
        var context = CreateContext(LocalAiRecoveryConfig(), localDataDir: temp.Path);
        LocalAiResolvedInstall automatic = CreateLocalAiResolvedInstall(temp.Path, 18801);
        LocalAiResolvedInstall fixedPort = automatic with
        {
            Manifest = automatic.Manifest with { RequestedPort = 18802 },
        };
        context.LocalAiRecoveryOriginalInstall = automatic;
        context.LocalAiRecoveryPendingInstall = fixedPort;
        var probeCalls = 0;
        var step = new ValidateLocalAiRecoveryGatewayCompatibilityStep((_, _) =>
        {
            probeCalls++;
            return Task.FromResult(new CommandResult(
                42,
                ValidateLocalAiRecoveryGatewayCompatibilityStep.UnsupportedMarker,
                string.Empty,
                TimeSpan.Zero,
                false));
        });

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Equal(1, probeCalls);
    }

    [Fact]
    public async Task ValidateLocalAiRecoveryGateway_AppOwnedDistro_AllowsRecovery()
    {
        var context = CreateContext(LocalAiRecoveryConfig());
        var step = new ValidateLocalAiRecoveryGatewayStep(
            (_, _, _, _) => ExistingLocalAiGateway(hasDistro: true, appOwned: true),
            _ => [ManagedGatewayRecord()]);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task ValidateLocalAiRecoveryGateway_OwnerDrift_BlocksBeforeRecovery()
    {
        var context = CreateContext(LocalAiRecoveryConfig());
        var step = new ValidateLocalAiRecoveryGatewayStep(
            (_, _, _, _) => ExistingLocalAiGateway(hasDistro: true, appOwned: true),
            _ => [ManagedGatewayRecord() with { Id = "replacement-gateway" }]);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("owner changed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateLocalAiRecoveryGateway_TrimsEffectiveDistroName()
    {
        var context = CreateContext(LocalAiRecoveryConfig());
        var step = new ValidateLocalAiRecoveryGatewayStep(
            (_, _, _, _) => ExistingLocalAiGateway(hasDistro: true, appOwned: true),
            _ => [ManagedGatewayRecord() with { SetupManagedDistroName = " OpenClawGateway " }]);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task PreserveLocalAiRecoveryGateway_RestartsAfterWslShutdown()
    {
        var context = CreateContext(LocalAiRecoveryConfig());
        context.LocalAiRecoveryStoppedWsl = true;
        var restartCalls = 0;
        var step = new PreserveLocalAiRecoveryGatewayStep((_, _) =>
        {
            restartCalls++;
            return Task.FromResult(StepResult.Ok("restarted"));
        });

        await step.RollbackAsync(context, CancellationToken.None);

        Assert.Equal(1, restartCalls);
        Assert.False(context.LocalAiRecoveryStoppedWsl);
    }

    [Fact]
    public async Task PreserveLocalAiRecoveryGateway_UsesGatewayRestartRollbackBudget()
    {
        SetupConfig config = LocalAiRecoveryConfig();
        config.RollbackOnFailure = true;
        config.RollbackTimeoutSeconds = 1;
        config.Gateway.HealthTimeoutSeconds = 1;
        var context = CreateContext(config);
        context.LocalAiRecoveryStoppedWsl = true;
        var pipeline = new SetupPipeline([
            new PreserveLocalAiRecoveryGatewayStep(async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1_100), ct);
                return StepResult.Ok("restarted");
            }),
            new MockStep("failure", (_, _) => Task.FromResult(StepResult.Fail("fail"))),
        ]);

        PipelineResult result = await pipeline.RunAsync(context);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.False(context.LocalAiRecoveryStoppedWsl);
        Assert.Contains(
            context.Journal.Entries,
            entry => entry.StepId == "preserve-local-ai-recovery-gateway" &&
                     entry.Event == "rollback_ok");
    }

    [Fact]
    public async Task RestartGatewayStep_FailureArmsRecoveryRollbackRestart()
    {
        var context = CreateContext(LocalAiRecoveryConfig());
        var step = new RestartGatewayStep((_, _) =>
            Task.FromResult(StepResult.Fail("restart failed")));

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.True(context.LocalAiRecoveryStoppedWsl);
    }

    [Fact]
    public async Task FinalizationFailure_RestartsGatewayAfterConfigurationRollback()
    {
        SetupConfig config = LocalAiRecoveryConfig();
        config.RollbackOnFailure = true;
        var context = CreateContext(config);
        bool configurationRestored = false;
        bool restartedAfterRestore = false;
        var pipeline = new SetupPipeline([
            new PreserveLocalAiRecoveryGatewayStep((_, _) =>
            {
                restartedAfterRestore = configurationRestored;
                return Task.FromResult(StepResult.Ok("restarted"));
            }),
            new MockStep(
                "configure-local-ai-gateway",
                (_, _) => Task.FromResult(StepResult.Ok("configured")),
                (_, _) =>
                {
                    configurationRestored = true;
                    return Task.CompletedTask;
                }),
            new RestartGatewayStep((_, _) => Task.FromResult(StepResult.Ok("restarted"))),
            new MockStep(
                "finalize-local-ai-model-replacement",
                (_, _) => Task.FromResult(StepResult.Fail("finalization failed"))),
        ]);

        PipelineResult result = await pipeline.RunAsync(context);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.True(configurationRestored);
        Assert.True(restartedAfterRestore);
        Assert.False(context.LocalAiRecoveryStoppedWsl);
    }

    [Fact]
    public async Task LaterFailure_RestoresWslNetworkingBeforeRecoveryGuardRestartsGateway()
    {
        SetupConfig config = LocalAiRecoveryConfig();
        config.RollbackOnFailure = true;
        var context = CreateContext(config);
        var rollbackOrder = new List<string>();
        var pipeline = new SetupPipeline([
            new PreserveLocalAiRecoveryGatewayStep((_, _) =>
            {
                rollbackOrder.Add("restart");
                return Task.FromResult(StepResult.Ok("restarted"));
            }),
            new MockStep(
                "configure-local-ai-wsl-networking",
                (ctx, _) =>
                {
                    ctx.LocalAiRecoveryStoppedWsl = true;
                    return Task.FromResult(StepResult.Ok("configured"));
                },
                (_, _) =>
                {
                    rollbackOrder.Add("networking");
                    return Task.CompletedTask;
                }),
            new MockStep("failure", (_, _) => Task.FromResult(StepResult.Fail("failed"))),
        ]);

        PipelineResult result = await pipeline.RunAsync(context);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal(["networking", "restart"], rollbackOrder);
    }

    [Fact]
    public async Task WslNetworkingFailure_RollsBackArmedRecoveryGuard()
    {
        SetupConfig config = LocalAiRecoveryConfig();
        config.RollbackOnFailure = true;
        var context = CreateContext(config);
        var restartCalls = 0;
        var pipeline = new SetupPipeline([
            new PreserveLocalAiRecoveryGatewayStep((_, _) =>
            {
                restartCalls++;
                return Task.FromResult(StepResult.Ok("restarted"));
            }),
            new MockStep(
                "configure-local-ai-wsl-networking",
                (ctx, _) =>
                {
                    ctx.LocalAiRecoveryStoppedWsl = true;
                    return Task.FromResult(StepResult.Fail("failed after stopping WSL"));
                }),
        ]);

        PipelineResult result = await pipeline.RunAsync(context);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal(1, restartCalls);
        Assert.False(context.LocalAiRecoveryStoppedWsl);
    }

    [Fact]
    public async Task BorrowedRuntimeFailure_RollsBackArmedRecoveryGuard()
    {
        using var temp = new TempDirectory("local-ai-borrowed-runtime-failure-");
        SetupConfig config = LocalAiRecoveryConfig();
        config.LocalAi.Enabled = true;
        config.RollbackOnFailure = true;
        var context = CreateContext(config, localDataDir: temp.Path);
        LocalAiResolvedInstall install = CreateLocalAiResolvedInstall(temp.Path, 18801);
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(install.Manifest);
        context.LocalAiResolvedInstall = install;
        context.LocalAiRecoveryOriginalInstall = install;
        context.LocalAiRecoveryReceiptRollbackAllowed = true;
        var restartCalls = 0;
        var runtime = new DisposeTrackingRuntime(HealthySnapshot(install))
        {
            RestartForSetupHandler = _ =>
            {
                restartCalls++;
                LocalAiRuntimeSnapshot snapshot = HealthySnapshot(install);
                return Task.FromResult(restartCalls == 1
                    ? snapshot with
                    {
                        State = LocalAiRuntimeState.Failed,
                        Ownership = LocalAiOwnership.None,
                        ProcessId = null,
                    }
                    : snapshot);
            },
        };
        context.LocalAiRuntime = runtime;
        context.LocalAiRuntimeBorrowed = true;
        var pipeline = new SetupPipeline([
            new PreserveLocalAiRecoveryGatewayStep(
                (_, _) => Task.FromResult(StepResult.Ok("not needed")),
                (_, _) => Task.FromResult(true)),
            new StartLocalAiRuntimeStep(_ => runtime),
        ]);

        PipelineResult result = await pipeline.RunAsync(context);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal(1, runtime.RestartForSetupCalls);
        Assert.Equal(1, runtime.RestartForSetupRollbackCalls);
        Assert.True(context.LocalAiBorrowedRuntimeRestored);
        Assert.Equal(LocalAiRuntimeState.Healthy, runtime.Snapshot.State);
    }

    [Fact]
    public async Task OrdinaryRecoveryRollback_PreservesConcurrentlyUpdatedReceipt()
    {
        using var temp = new TempDirectory("local-ai-borrowed-runtime-concurrent-receipt-");
        SetupConfig config = LocalAiRecoveryConfig();
        config.LocalAi.Enabled = true;
        config.RollbackOnFailure = true;
        var context = CreateContext(config, localDataDir: temp.Path);
        LocalAiResolvedInstall original = CreateLocalAiResolvedInstall(temp.Path, 18801);
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(original.Manifest);
        context.LocalAiResolvedInstall = original;
        context.LocalAiRecoveryOriginalInstall = original;
        context.LocalAiRecoveryReceiptRollbackAllowed = true;
        var runtime = new DisposeTrackingRuntime(HealthySnapshot(original));
        context.LocalAiRuntime = runtime;
        context.LocalAiRuntimeBorrowed = true;
        LocalAiInstallManifest concurrent = original.Manifest with
        {
            Endpoint = "http://127.0.0.1:18803/v1",
        };
        var pipeline = new SetupPipeline([
            new PreserveLocalAiRecoveryGatewayStep(
                (_, _) => Task.FromResult(StepResult.Ok("not needed")),
                (_, _) => Task.FromResult(true)),
            new MockStep(
                "failure",
                (_, _) => Task.FromResult(StepResult.Fail("failed")),
                async (_, ct) => await store.SaveAsync(concurrent, ct)),
        ]);

        PipelineResult result = await pipeline.RunAsync(context);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal(concurrent.Endpoint, (await store.LoadAsync())!.Manifest.Endpoint);
        Assert.True(context.LocalAiRecoveryRollbackUncertain);
        Assert.Equal(0, runtime.RestartForSetupRollbackCalls);
    }

    /// <summary>
    /// Regression guard for a rollback race: if the Gateway could not be confirmed switched back
    /// to the original (A) endpoint, the replacement (B) runtime must be kept alive rather than
    /// disposed, otherwise the Gateway is left routing to a dead process.
    /// </summary>
    [Fact]
    public async Task StartLocalAiRuntimeStep_KeepsReplacementRuntimeWhenGatewayRollbackUnconfirmed()
    {
        var context = CreateContext(LocalAiRecoveryConfig());
        context.LocalAiRecoveryProviderTransition = true;
        context.LocalAiRecoveryRollbackUncertain = true;
        context.LocalAiRecoveryReceiptRollbackAllowed = false;
        var runtime = new DisposeTrackingRuntime();
        context.LocalAiRuntime = runtime;
        var step = new StartLocalAiRuntimeStep(_ => runtime);

        await step.RollbackAsync(context, CancellationToken.None);

        Assert.Equal(0, runtime.DisposeCalls);
        Assert.NotNull(context.LocalAiRuntime);
    }

    [Fact]
    public async Task StartLocalAiRuntimeStep_DisposesRuntimeWhenGatewayRollbackConfirmed()
    {
        var context = CreateContext(LocalAiRecoveryConfig());
        context.LocalAiRecoveryProviderTransition = true;
        context.LocalAiRecoveryRollbackUncertain = true;
        context.LocalAiRecoveryReceiptRollbackAllowed = true;
        var runtime = new DisposeTrackingRuntime();
        context.LocalAiRuntime = runtime;
        var step = new StartLocalAiRuntimeStep(_ => runtime);

        await step.RollbackAsync(context, CancellationToken.None);

        Assert.Equal(1, runtime.DisposeCalls);
        Assert.Null(context.LocalAiRuntime);
    }

    [Fact]
    public async Task StartLocalAiRuntimeStep_DisposesRuntimeOutsideRecoveryTransition()
    {
        var context = CreateContext(LocalAiRecoveryConfig());
        context.LocalAiRecoveryProviderTransition = false;
        context.LocalAiRecoveryReceiptRollbackAllowed = false;
        var runtime = new DisposeTrackingRuntime();
        context.LocalAiRuntime = runtime;
        var step = new StartLocalAiRuntimeStep(_ => runtime);

        await step.RollbackAsync(context, CancellationToken.None);

        Assert.Equal(1, runtime.DisposeCalls);
        Assert.Null(context.LocalAiRuntime);
    }

    [Fact]
    public async Task StartLocalAiRuntimeStep_RestartsBorrowedTrayRuntimeForReplacement()
    {
        using var temp = new TempDirectory("local-ai-borrowed-runtime-");
        var context = CreateContext(LocalAiRecoveryConfig(), localDataDir: temp.Path);
        LocalAiResolvedInstall install = CreateLocalAiResolvedInstall(context.LocalDataDir, port: 18802);
        await new LocalAiManifestStore(new LocalAiPaths(context.LocalDataDir)).SaveAsync(install.Manifest);
        context.LocalAiResolvedInstall = install;
        context.LocalAiUpgradeOriginalInstall = install;
        var runtime = new DisposeTrackingRuntime(HealthySnapshot(install));
        context.LocalAiRuntime = runtime;
        context.LocalAiRuntimeBorrowed = true;

        StepResult result = await new StartLocalAiRuntimeStep().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal(1, runtime.RestartForSetupCalls);
        Assert.Equal(0, runtime.RestartCalls);
        Assert.Equal(0, runtime.EnsureStartedCalls);
        Assert.Equal(0, runtime.DisposeCalls);
        Assert.Same(runtime, context.LocalAiRuntime);
    }

    [Fact]
    public async Task StartLocalAiRuntimeStep_RestartsBorrowedTrayRuntimeForOrdinaryRecovery()
    {
        using var temp = new TempDirectory("local-ai-borrowed-runtime-no-replacement-");
        var context = CreateContext(LocalAiRecoveryConfig(), localDataDir: temp.Path);
        LocalAiResolvedInstall install = CreateLocalAiResolvedInstall(context.LocalDataDir, port: 18802);
        await new LocalAiManifestStore(new LocalAiPaths(context.LocalDataDir)).SaveAsync(install.Manifest);
        context.LocalAiResolvedInstall = install;
        context.LocalAiRecoveryOriginalInstall = install;
        var runtime = new DisposeTrackingRuntime(HealthySnapshot(install));
        context.LocalAiRuntime = runtime;
        context.LocalAiRuntimeBorrowed = true;
        var step = new StartLocalAiRuntimeStep();

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);
        await step.RollbackAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal(1, runtime.RestartForSetupCalls);
        Assert.Equal(1, runtime.StopForSetupCalls);
        Assert.True(context.LocalAiBorrowedRuntimeRestartedThisRun);
    }

    [Fact]
    public async Task StartLocalAiRuntimeStep_RestartsBorrowedTrayRuntimeForFirstRecoveryInstall()
    {
        using var temp = new TempDirectory("local-ai-borrowed-runtime-first-install-");
        var context = CreateContext(LocalAiRecoveryConfig(), localDataDir: temp.Path);
        LocalAiResolvedInstall install = CreateLocalAiResolvedInstall(context.LocalDataDir, port: 18802);
        await new LocalAiManifestStore(new LocalAiPaths(context.LocalDataDir)).SaveAsync(install.Manifest);
        context.LocalAiResolvedInstall = install;
        var runtime = new DisposeTrackingRuntime(HealthySnapshot(install));
        context.LocalAiRuntime = runtime;
        context.LocalAiRuntimeBorrowed = true;

        StepResult result = await new StartLocalAiRuntimeStep().ExecuteAsync(
            context,
            CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal(1, runtime.RestartForSetupCalls);
        Assert.True(context.LocalAiBorrowedRuntimeRestartedThisRun);
    }

    [Fact]
    public async Task StartLocalAiRuntimeStep_CancellationAfterEndpointCommitAdoptsRollbackBaseline()
    {
        using var temp = new TempDirectory("local-ai-borrowed-runtime-cancel-after-endpoint-");
        using var cancellation = new CancellationTokenSource();
        SetupConfig config = LocalAiRecoveryConfig();
        config.LocalAi.Enabled = true;
        config.RollbackOnFailure = true;
        var context = CreateContext(config, cancellation.Token, localDataDir: temp.Path);
        LocalAiResolvedInstall original = CreateLocalAiResolvedInstall(temp.Path, port: 18801);
        LocalAiInstallManifest pendingManifest = original.Manifest with
        {
            ModelCatalogId = "replacement-model",
            ModelAlias = "replacement-model",
            Endpoint = "http://127.0.0.1:18802/v1",
            ReplacedManifest = original.Manifest,
            PreviousEndpoints = [original.Manifest.Endpoint!],
        };
        LocalAiInstallManifest movedManifest = pendingManifest with
        {
            Endpoint = "http://127.0.0.1:18803/v1",
            PreviousEndpoints = [original.Manifest.Endpoint!, pendingManifest.Endpoint!],
        };
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(pendingManifest);
        LocalAiResolvedInstall pending = store.ResolveAndValidate(pendingManifest);
        LocalAiResolvedInstall moved = store.ResolveAndValidate(movedManifest);
        context.LocalAiResolvedInstall = pending;
        context.LocalAiRecoveryOriginalInstall = original;
        context.LocalAiRecoveryProviderTransition = true;
        context.LocalAiRecoveryReceiptRollbackAllowed = true;
        var runtime = new DisposeTrackingRuntime(HealthySnapshot(pending))
        {
            RestartForSetupHandler = async _ =>
            {
                await store.SaveAsync(movedManifest, CancellationToken.None);
                cancellation.Cancel();
                return HealthySnapshot(moved);
            },
            RestartHandler = async ct =>
            {
                LocalAiResolvedInstall restored = await store.LoadAsync(ct)
                    ?? throw new InvalidDataException("restored receipt missing");
                return HealthySnapshot(restored);
            },
        };
        context.LocalAiRuntime = runtime;
        context.LocalAiRuntimeBorrowed = true;
        var pipeline = new SetupPipeline([
            new PreserveLocalAiRecoveryGatewayStep(
                (_, _) => Task.FromResult(StepResult.Ok("not needed")),
                (_, _) => Task.FromResult(true)),
            new StartLocalAiRuntimeStep(_ => runtime),
        ]);

        PipelineResult result = await pipeline.RunAsync(context);

        Assert.Equal(PipelineOutcome.Cancelled, result.Outcome);
        LocalAiResolvedInstall restored = (await store.LoadAsync())!;
        Assert.Equal(original.Manifest.ModelCatalogId, restored.Manifest.ModelCatalogId);
        Assert.Equal(1, runtime.RestartForSetupRollbackCalls);
        Assert.True(context.LocalAiBorrowedRuntimeRestored);
        Assert.False(context.LocalAiRecoveryRollbackUncertain);
    }

    [Fact]
    public async Task StartLocalAiRuntimeStep_RejectsBorrowedRuntimeOutsideRecovery()
    {
        using var temp = new TempDirectory("local-ai-borrowed-runtime-unarmed-");
        var context = CreateContext(new SetupConfig(), localDataDir: temp.Path);
        LocalAiResolvedInstall install = CreateLocalAiResolvedInstall(context.LocalDataDir, port: 18802);
        await new LocalAiManifestStore(new LocalAiPaths(context.LocalDataDir)).SaveAsync(install.Manifest);
        context.LocalAiResolvedInstall = install;
        var runtime = new DisposeTrackingRuntime(HealthySnapshot(install));
        context.LocalAiRuntime = runtime;
        context.LocalAiRuntimeBorrowed = true;

        StepResult result = await new StartLocalAiRuntimeStep().ExecuteAsync(
            context,
            CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Equal(0, runtime.RestartForSetupCalls);
        Assert.False(context.LocalAiBorrowedRuntimeRestartedThisRun);
    }

    [Fact]
    public async Task ResetRouterAsync_UsesSetupScopedRestartForBorrowedRuntime()
    {
        var runtime = new DisposeTrackingRuntime();

        await VerifyLocalAiInferenceStep.ResetRouterAsync(runtime, setupScoped: true);

        Assert.Equal(1, runtime.RestartForSetupCalls);
        Assert.Equal(0, runtime.RestartCalls);
    }

    [Fact]
    public async Task ResetRouterAsync_RefreshesBorrowedRuntimeReceiptAfterAutomaticPortMove()
    {
        using var temp = new TempDirectory("local-ai-borrowed-runtime-reset-");
        var context = CreateContext(LocalAiRecoveryConfig(), localDataDir: temp.Path);
        LocalAiResolvedInstall original = CreateLocalAiResolvedInstall(temp.Path, port: 18801);
        LocalAiResolvedInstall moved = original with
        {
            Manifest = original.Manifest with { Endpoint = "http://127.0.0.1:18803/v1" },
            Endpoint = new Uri("http://127.0.0.1:18803/v1"),
        };
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(original.Manifest);
        context.LocalAiResolvedInstall = original;
        context.LocalAiRuntimeBorrowed = true;
        var runtime = new DisposeTrackingRuntime(HealthySnapshot(original))
        {
            RestartForSetupHandler = async ct =>
            {
                await store.SaveAsync(moved.Manifest, ct);
                return HealthySnapshot(moved);
            },
        };

        LocalAiRuntimeSnapshot reset = await VerifyLocalAiInferenceStep.ResetRouterAsync(
            context,
            runtime);

        Assert.Equal(moved.Endpoint, reset.Endpoint);
        Assert.Equal(moved.Endpoint, context.LocalAiResolvedInstall.Endpoint);
    }

    [Fact]
    public async Task ResetRouterAsync_DoesNotAdoptConcurrentReceiptChanges()
    {
        using var temp = new TempDirectory("local-ai-borrowed-runtime-reset-concurrent-");
        var context = CreateContext(LocalAiRecoveryConfig(), localDataDir: temp.Path);
        LocalAiResolvedInstall original = CreateLocalAiResolvedInstall(temp.Path, port: 18801);
        LocalAiResolvedInstall concurrent = original with
        {
            Manifest = original.Manifest with
            {
                Endpoint = "http://127.0.0.1:18803/v1",
                ContextLength = original.Manifest.ContextLength + 1,
            },
            Endpoint = new Uri("http://127.0.0.1:18803/v1"),
        };
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(original.Manifest);
        context.LocalAiResolvedInstall = original;
        context.LocalAiRuntimeBorrowed = true;
        var runtime = new DisposeTrackingRuntime(HealthySnapshot(original))
        {
            RestartForSetupHandler = async ct =>
            {
                await store.SaveAsync(concurrent.Manifest, ct);
                return HealthySnapshot(concurrent);
            },
        };

        await VerifyLocalAiInferenceStep.ResetRouterAsync(context, runtime);

        Assert.Same(original, context.LocalAiResolvedInstall);
        Assert.Equal(concurrent.Manifest.ContextLength, (await store.LoadAsync())!.Manifest.ContextLength);
    }

    [Fact]
    public async Task ResetRouterAsync_RefreshesEndpointBaselineAfterFailedReset()
    {
        using var temp = new TempDirectory("local-ai-borrowed-runtime-reset-failed-");
        var context = CreateContext(LocalAiRecoveryConfig(), localDataDir: temp.Path);
        LocalAiResolvedInstall original = CreateLocalAiResolvedInstall(temp.Path, port: 18801);
        LocalAiResolvedInstall moved = original with
        {
            Manifest = original.Manifest with { Endpoint = "http://127.0.0.1:18803/v1" },
            Endpoint = new Uri("http://127.0.0.1:18803/v1"),
        };
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(original.Manifest);
        context.LocalAiResolvedInstall = original;
        context.LocalAiRuntimeBorrowed = true;
        var runtime = new DisposeTrackingRuntime(HealthySnapshot(original))
        {
            RestartForSetupHandler = async ct =>
            {
                await store.SaveAsync(moved.Manifest, ct);
                return HealthySnapshot(moved) with
                {
                    State = LocalAiRuntimeState.Failed,
                    Ownership = LocalAiOwnership.None,
                    ProcessId = null,
                };
            },
        };

        LocalAiRuntimeSnapshot reset = await VerifyLocalAiInferenceStep.ResetRouterAsync(
            context,
            runtime);

        Assert.Equal(LocalAiRuntimeState.Failed, reset.State);
        Assert.Equal(moved.Endpoint, context.LocalAiResolvedInstall.Endpoint);
    }

    [Fact]
    public async Task ReleaseBorrowedLocalAiRuntimeAfterFailureAsync_ReleasesNoRollbackOwnership()
    {
        var context = CreateContext(LocalAiRecoveryConfig());
        var runtime = new DisposeTrackingRuntime();
        context.LocalAiRuntime = runtime;
        context.LocalAiRuntimeBorrowed = true;
        context.LocalAiBorrowedRuntimeRestartedThisRun = true;

        await SetupPipeline.ReleaseBorrowedLocalAiRuntimeAfterFailureAsync(
            context,
            new PipelineResult(PipelineOutcome.Failed));

        Assert.Equal(1, runtime.ReleaseSetupGatewayRouteCalls);

        context.LocalAiBorrowedRuntimeRestartedThisRun = false;
        await SetupPipeline.ReleaseBorrowedLocalAiRuntimeAfterFailureAsync(
            context,
            new PipelineResult(PipelineOutcome.Failed));

        Assert.Equal(1, runtime.ReleaseSetupGatewayRouteCalls);

        await SetupPipeline.ReleaseBorrowedLocalAiRuntimeAfterFailureAsync(
            context,
            new PipelineResult(PipelineOutcome.Success));

        Assert.Equal(1, runtime.ReleaseSetupGatewayRouteCalls);
    }

    [Fact]
    public async Task BorrowedTrayRuntime_RollbackRestoresRuntimeBeforeReconcilingAutomaticPort()
    {
        using var temp = new TempDirectory("local-ai-borrowed-runtime-rollback-");
        SetupConfig config = LocalAiRecoveryConfig();
        config.RollbackOnFailure = true;
        var context = CreateContext(config, localDataDir: temp.Path);
        LocalAiResolvedInstall original = CreateLocalAiResolvedInstall(context.LocalDataDir, port: 18801);
        original = original with
        {
            Manifest = original.Manifest with { RequestedPort = 0 },
        };
        LocalAiInstallManifest pendingManifest = original.Manifest with
        {
            ModelCatalogId = "replacement-model",
            ModelAlias = "replacement-model",
            Endpoint = "http://127.0.0.1:18802/v1",
            ReplacedManifest = original.Manifest,
            PreviousEndpoints = [original.Manifest.Endpoint!],
        };
        var store = new LocalAiManifestStore(new LocalAiPaths(context.LocalDataDir));
        await store.SaveAsync(pendingManifest);
        context.LocalAiResolvedInstall = store.ResolveAndValidate(pendingManifest);
        context.LocalAiRecoveryOriginalInstall = original;
        context.LocalAiRecoveryProviderTransition = true;
        var runtime = new DisposeTrackingRuntime(HealthySnapshot(context.LocalAiResolvedInstall))
        {
            RestartForSetupHandler = async ct =>
            {
                LocalAiResolvedInstall restored = await store.LoadAsync(ct)
                    ?? throw new InvalidDataException("restored receipt missing");
                LocalAiInstallManifest movedManifest = restored.Manifest with
                {
                    Endpoint = "http://127.0.0.1:18803/v1",
                };
                await store.SaveAsync(movedManifest, ct);
                return HealthySnapshot(store.ResolveAndValidate(movedManifest));
            },
        };
        context.LocalAiRuntime = runtime;
        context.LocalAiRuntimeBorrowed = true;
        context.LocalAiBorrowedRuntimeRestartedThisRun = true;
        var rollbackOrder = new List<string>();
        Uri? restoredRoute = null;
        Uri? probedEndpoint = null;
        var persist = new PersistLocalAiManifestStep();
        var start = new StartLocalAiRuntimeStep(_ => runtime);
        var preserve = new PreserveLocalAiRecoveryGatewayStep(
            (_, _) => Task.FromResult(StepResult.Ok("gateway restarted")),
            (install, _) =>
            {
                rollbackOrder.Add("probe");
                Assert.Equal(0, runtime.AcknowledgeSetupGatewayRouteCalls);
                probedEndpoint = install.Endpoint;
                return Task.FromResult(true);
            },
            (_, _, _, install, _) =>
            {
                rollbackOrder.Add("route");
                restoredRoute = install.Endpoint;
                return Task.FromResult(true);
            });
        var pipeline = new SetupPipeline([
            new MockStep(
                "persist-local-ai-manifest",
                (_, _) => Task.FromResult(StepResult.Ok("persisted")),
                persist.RollbackAsync),
            new MockStep(
                "start-local-ai-runtime",
                (_, _) => Task.FromResult(StepResult.Ok("started")),
                start.RollbackAsync),
            preserve,
            new MockStep(
                "configure-local-ai-gateway",
                (ctx, _) =>
                {
                    ctx.LocalAiGatewayPriorState = new LocalAiGatewayPriorState(
                        ProviderExisted: true,
                        ProviderJson: "{}",
                        PrimaryModelExisted: true,
                        PrimaryModelJson: "\"prior-model\"");
                    ctx.LocalAiRecoveryGatewayConfigurationStartedThisRun = true;
                    ctx.LocalAiRecoveryRollbackUncertain = true;
                    return Task.FromResult(StepResult.Ok("configured"));
                },
                (ctx, _) =>
                {
                    rollbackOrder.Add("gateway");
                    ctx.LocalAiRecoveryReceiptRollbackAllowed = true;
                    ctx.LocalAiRecoveryRollbackUncertain = false;
                    return Task.CompletedTask;
                }),
            new MockStep(
                "failure",
                (_, _) => Task.FromResult(StepResult.Fail("failed after gateway configuration"))),
        ]);

        PipelineResult result = await pipeline.RunAsync(context);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal("failure", result.FailedStepId);
        LocalAiResolvedInstall restored = (await store.LoadAsync())!;
        Assert.Equal(new Uri("http://127.0.0.1:18803/v1"), probedEndpoint);
        Assert.Equal(1, runtime.RestartForSetupCalls);
        Assert.Equal(1, runtime.AcknowledgeSetupGatewayRouteCalls);
        Assert.Equal(["gateway", "route", "probe"], rollbackOrder);
        Assert.Equal(original.Manifest.ModelCatalogId, restored.Manifest.ModelCatalogId);
        Assert.Equal(new Uri("http://127.0.0.1:18803/v1"), restored.Endpoint);
        Assert.Equal(restored.Endpoint, restoredRoute);
        Assert.Equal(restored.Endpoint, probedEndpoint);
        Assert.Equal(0, runtime.RestartCalls);
        Assert.Equal(0, runtime.StopForSetupCalls);
        Assert.Equal(0, runtime.StopCalls);
        Assert.Equal(0, runtime.DisposeCalls);
        Assert.Same(runtime, context.LocalAiRuntime);
        Assert.True(context.LocalAiBorrowedRuntimeRestored);
        Assert.False(context.LocalAiRecoveryRollbackUncertain);
    }

    [Fact]
    public async Task BorrowedTrayRuntime_PreGatewayRollbackPublishesRestoredAutomaticPort()
    {
        using var temp = new TempDirectory("local-ai-borrowed-runtime-pre-gateway-rollback-");
        SetupConfig config = LocalAiRecoveryConfig();
        config.RollbackOnFailure = true;
        var context = CreateContext(config, localDataDir: temp.Path);
        LocalAiResolvedInstall original = CreateLocalAiResolvedInstall(context.LocalDataDir, port: 18801);
        original = original with
        {
            Manifest = original.Manifest with { RequestedPort = 0 },
        };
        LocalAiInstallManifest pendingManifest = original.Manifest with
        {
            ModelCatalogId = "replacement-model",
            ModelAlias = "replacement-model",
            Endpoint = "http://127.0.0.1:18802/v1",
            ReplacedManifest = original.Manifest,
            PreviousEndpoints = [original.Manifest.Endpoint!],
        };
        var store = new LocalAiManifestStore(new LocalAiPaths(context.LocalDataDir));
        await store.SaveAsync(pendingManifest);
        context.LocalAiResolvedInstall = store.ResolveAndValidate(pendingManifest);
        context.LocalAiRecoveryOriginalInstall = original;
        context.LocalAiRecoveryProviderTransition = true;
        Uri? publishedEndpoint = null;
        var runtime = new DisposeTrackingRuntime(HealthySnapshot(context.LocalAiResolvedInstall))
        {
            RestartHandler = async ct =>
            {
                LocalAiResolvedInstall restored = await store.LoadAsync(ct)
                    ?? throw new InvalidDataException("restored receipt missing");
                LocalAiInstallManifest movedManifest = restored.Manifest with
                {
                    Endpoint = "http://127.0.0.1:18803/v1",
                };
                await store.SaveAsync(movedManifest, ct);
                LocalAiResolvedInstall moved = store.ResolveAndValidate(movedManifest);
                publishedEndpoint = moved.Endpoint;
                return HealthySnapshot(moved);
            },
        };
        context.LocalAiRuntime = runtime;
        context.LocalAiRuntimeBorrowed = true;
        context.LocalAiBorrowedRuntimeRestartedThisRun = true;
        var persist = new PersistLocalAiManifestStep();
        var start = new StartLocalAiRuntimeStep(_ => runtime);
        var pipeline = new SetupPipeline([
            new MockStep(
                "persist-local-ai-manifest",
                (_, _) => Task.FromResult(StepResult.Ok("persisted")),
                persist.RollbackAsync),
            new MockStep(
                "start-local-ai-runtime",
                (_, _) => Task.FromResult(StepResult.Ok("started")),
                start.RollbackAsync),
            new MockStep(
                "failure-before-gateway",
                (_, _) => Task.FromResult(StepResult.Fail("failed before gateway configuration"))),
        ]);

        PipelineResult result = await pipeline.RunAsync(context);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        LocalAiResolvedInstall restored = (await store.LoadAsync())!;
        Assert.Equal(original.Manifest.ModelCatalogId, restored.Manifest.ModelCatalogId);
        Assert.Equal(new Uri("http://127.0.0.1:18803/v1"), restored.Endpoint);
        Assert.Equal(restored.Endpoint, publishedEndpoint);
        Assert.Equal(1, runtime.StopForSetupCalls);
        Assert.Equal(1, runtime.RestartForSetupRollbackCalls);
        Assert.Equal(0, runtime.RestartCalls);
        Assert.Equal(0, runtime.RestartForSetupCalls);
        Assert.Equal(0, runtime.DisposeCalls);
        Assert.False(context.LocalAiRecoveryRollbackUncertain);
    }

    [Fact]
    public async Task RestoredBorrowedRuntimeTransfersExactAcquisitionCleanupOwnership()
    {
        using var temp = new TempDirectory("local-ai-restored-runtime-cleanup-ownership-");
        SetupConfig config = LocalAiRecoveryConfig();
        config.LocalAi.Enabled = true;
        var context = CreateContext(config, localDataDir: temp.Path);
        LocalAiResolvedInstall original = CreateLocalAiResolvedInstall(temp.Path, port: 18801);
        LocalAiInstallManifest pendingManifest = original.Manifest with
        {
            ModelCatalogId = "replacement-model",
            ModelAlias = "replacement-model",
            Endpoint = "http://127.0.0.1:18802/v1",
            ReplacedManifest = original.Manifest,
            PreviousEndpoints = [original.Manifest.Endpoint!],
        };
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(pendingManifest);
        context.LocalAiResolvedInstall = store.ResolveAndValidate(pendingManifest);
        context.LocalAiRecoveryOriginalInstall = original;
        context.LocalAiRecoveryProviderTransition = true;
        context.LocalAiRuntimeInstall = new LlamaRuntimeInstallResult(
            Path.GetDirectoryName(original.ExecutablePath)!,
            original.ExecutablePath,
            LlamaRuntimeInstallDisposition.Installed,
            CreatedThisRun: true,
            VerifiedArchives: [],
            Rollback: null);
        var runtime = new DisposeTrackingRuntime(HealthySnapshot(context.LocalAiResolvedInstall))
        {
            RestartHandler = async ct =>
            {
                LocalAiResolvedInstall restored = await store.LoadAsync(ct)
                    ?? throw new InvalidDataException("restored receipt missing");
                return HealthySnapshot(restored);
            },
        };
        context.LocalAiRuntime = runtime;
        context.LocalAiRuntimeBorrowed = true;
        var acquirer = new TrackingRuntimeAcquirer();

        await new PersistLocalAiManifestStep().RollbackAsync(context, CancellationToken.None);
        await new AcquireLocalAiRuntimeStep(acquirer).RollbackAsync(context, CancellationToken.None);

        Assert.Equal(original.Manifest.ModelCatalogId, (await store.LoadAsync())!.Manifest.ModelCatalogId);
        Assert.Null(context.LocalAiRuntimeInstall);
        Assert.Equal(0, acquirer.RemoveCalls);
        Assert.Equal(1, runtime.RestartForSetupRollbackCalls);
    }

    /// <summary>
    /// Regression guard: a stale manifest receipt is not enough to prove the original (A)
    /// endpoint is still alive. Rollback must probe it before pointing the Gateway back at it.
    /// </summary>
    [Fact]
    public async Task PreserveLocalAiRecoveryGateway_PreservesReplacementReceiptWhenOriginalEndpointUnhealthy()
    {
        using var temp = new TempDirectory("local-ai-recovery-rollback-");
        var context = CreateContext(LocalAiRecoveryConfig(), localDataDir: temp.Path);
        LocalAiResolvedInstall originalInstall = CreateLocalAiResolvedInstall(context.LocalDataDir, port: 18801);
        LocalAiResolvedInstall replacementInstall = CreateLocalAiResolvedInstall(context.LocalDataDir, port: 18802);
        context.LocalAiRecoveryOriginalInstall = originalInstall;
        context.LocalAiRecoveryReceiptRollbackAllowed = true;
        context.LocalAiResolvedInstall = replacementInstall;
        var probedEndpoints = new List<Uri?>();
        var step = new PreserveLocalAiRecoveryGatewayStep(
            (_, _) => Task.FromResult(StepResult.Ok("restarted")),
            (install, _) =>
            {
                probedEndpoints.Add(install.Endpoint);
                return Task.FromResult(false);
            });

        await step.RollbackAsync(context, CancellationToken.None);

        Assert.Equal([originalInstall.Endpoint], probedEndpoints);
        Assert.Same(replacementInstall, context.LocalAiResolvedInstall);
        Assert.False(context.LocalAiRecoveryReceiptRollbackAllowed);
        Assert.False(context.LocalAiRecoveryProviderTransition);
    }

    [Fact]
    public async Task PreserveLocalAiRecoveryGateway_RestoresReceiptWhenOriginalEndpointHealthy()
    {
        using var temp = new TempDirectory("local-ai-recovery-rollback-");
        var context = CreateContext(LocalAiRecoveryConfig(), localDataDir: temp.Path);
        LocalAiResolvedInstall originalInstall = CreateLocalAiResolvedInstall(context.LocalDataDir, port: 18801);
        LocalAiResolvedInstall replacementInstall = CreateLocalAiResolvedInstall(context.LocalDataDir, port: 18802);
        LocalAiInstallManifest pendingManifest = replacementInstall.Manifest with
        {
            ModelCatalogId = "replacement-model",
            ModelAlias = "replacement-model",
            ReplacedManifest = originalInstall.Manifest,
            PreviousEndpoints = [originalInstall.Manifest.Endpoint!],
        };
        replacementInstall = replacementInstall with { Manifest = pendingManifest };
        await new LocalAiManifestStore(new LocalAiPaths(context.LocalDataDir)).SaveAsync(pendingManifest);
        context.LocalAiRecoveryOriginalInstall = originalInstall;
        context.LocalAiRecoveryReceiptRollbackAllowed = true;
        context.LocalAiResolvedInstall = replacementInstall;
        var step = new PreserveLocalAiRecoveryGatewayStep(
            (_, _) => Task.FromResult(StepResult.Ok("restarted")),
            (_, _) => Task.FromResult(true));

        await step.RollbackAsync(context, CancellationToken.None);

        Assert.NotNull(context.LocalAiResolvedInstall);
        Assert.Equal(originalInstall.Manifest.ModelAlias, context.LocalAiResolvedInstall!.Manifest.ModelAlias);
        Assert.Equal(originalInstall.Endpoint, context.LocalAiResolvedInstall!.Endpoint);
    }

    [Fact]
    public async Task RecoveryRollback_PreservesReplacementWhenCompensatedOriginalEndpointIsUnhealthy()
    {
        using var temp = new TempDirectory("local-ai-recovery-rollback-");
        SetupConfig config = LocalAiRecoveryConfig();
        config.RollbackOnFailure = true;
        var context = CreateContext(config, localDataDir: temp.Path);
        LocalAiResolvedInstall originalInstall = CreateLocalAiResolvedInstall(context.LocalDataDir, port: 18801);
        LocalAiResolvedInstall replacementInstall = CreateLocalAiResolvedInstall(context.LocalDataDir, port: 18802);
        LocalAiInstallManifest pendingManifest = replacementInstall.Manifest with
        {
            ModelCatalogId = "replacement-model",
            ModelAlias = "replacement-model",
            ReplacedManifest = originalInstall.Manifest,
            PreviousEndpoints = [originalInstall.Manifest.Endpoint!],
        };
        replacementInstall = replacementInstall with { Manifest = pendingManifest };
        await new LocalAiManifestStore(new LocalAiPaths(context.LocalDataDir)).SaveAsync(pendingManifest);
        context.LocalAiRecoveryOriginalInstall = originalInstall;
        context.LocalAiResolvedInstall = replacementInstall;
        context.LocalAiRecoveryProviderTransition = true;
        context.LocalAiGatewayPriorState = new LocalAiGatewayPriorState(
            ProviderExisted: true,
            ProviderJson: "{}",
            PrimaryModelExisted: true,
            PrimaryModelJson: "\"test-model\"");
        var runtime = new DisposeTrackingRuntime();
        context.LocalAiRuntime = runtime;
        var probedEndpoints = new List<Uri?>();
        var persist = new PersistLocalAiManifestStep();
        var start = new StartLocalAiRuntimeStep(_ => runtime);
        var pipeline = new SetupPipeline([
            new MockStep(
                "persist-local-ai-manifest",
                (_, _) => Task.FromResult(StepResult.Ok("persisted")),
                persist.RollbackAsync),
            new MockStep(
                "start-local-ai-runtime",
                (_, _) => Task.FromResult(StepResult.Ok("started")),
                start.RollbackAsync),
            new PreserveLocalAiRecoveryGatewayStep(
                (_, _) => Task.FromResult(StepResult.Ok("restarted")),
                (install, _) =>
                {
                    probedEndpoints.Add(install.Endpoint);
                    return Task.FromResult(false);
                }),
            new MockStep(
                "configure-local-ai-gateway",
                (_, _) => Task.FromResult(StepResult.Ok("configured")),
                (ctx, _) =>
                {
                    ctx.LocalAiRecoveryReceiptRollbackAllowed = true;
                    ctx.LocalAiRecoveryRollbackUncertain = false;
                    return Task.CompletedTask;
                }),
            new MockStep(
                "finalize-local-ai-model-replacement",
                (_, _) => Task.FromResult(StepResult.Fail("finalization failed"))),
        ]);

        PipelineResult result = await pipeline.RunAsync(context);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal([originalInstall.Endpoint], probedEndpoints);
        Assert.Equal(0, runtime.DisposeCalls);
        Assert.False(context.LocalAiRecoveryCleanupAllowed);
        LocalAiInstallManifest retained = (await new LocalAiManifestStore(
            new LocalAiPaths(context.LocalDataDir)).LoadAsync())!.Manifest;
        Assert.Equal(pendingManifest.ModelCatalogId, retained.ModelCatalogId);
        Assert.NotNull(retained.ReplacedManifest);
    }

    private sealed class DisposeTrackingRuntime(LocalAiRuntimeSnapshot? snapshot = null) : ILocalAiRuntime
    {
        public int DisposeCalls { get; private set; }
        public int EnsureStartedCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int RestartCalls { get; private set; }
        public int StopForSetupCalls { get; private set; }
        public int RestartForSetupCalls { get; private set; }
        public int RestartForSetupRollbackCalls { get; private set; }
        public int AcknowledgeSetupGatewayRouteCalls { get; private set; }
        public int ReleaseSetupGatewayRouteCalls { get; private set; }
        public Func<CancellationToken, Task<LocalAiRuntimeSnapshot>>? RestartHandler { get; init; }
        public Func<CancellationToken, Task<LocalAiRuntimeSnapshot>>? RestartForSetupHandler { get; init; }

        public LocalAiRuntimeSnapshot Snapshot { get; private set; } = snapshot ??
            LocalAiRuntimeSnapshot.Initial(new Uri("http://127.0.0.1:18800/v1"), DateTimeOffset.UtcNow);

        public Task<LocalAiRuntimeSnapshot> ResumeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default)
        {
            EnsureStartedCalls++;
            return Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            Snapshot = Snapshot with
            {
                State = LocalAiRuntimeState.Stopped,
                Ownership = LocalAiOwnership.None,
                ProcessId = null,
                ProcessStartedAtUtc = null,
            };
            return Task.FromResult(Snapshot);
        }

        public async Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default)
        {
            RestartCalls++;
            Snapshot = RestartHandler is null
                ? snapshot ?? Snapshot
                : await RestartHandler(cancellationToken);
            return Snapshot;
        }

        public Task<LocalAiRuntimeSnapshot> StopForSetupAsync(CancellationToken cancellationToken = default)
        {
            StopForSetupCalls++;
            Snapshot = Snapshot with
            {
                State = LocalAiRuntimeState.Stopped,
                Ownership = LocalAiOwnership.None,
                ProcessId = null,
                ProcessStartedAtUtc = null,
            };
            return Task.FromResult(Snapshot);
        }

        public async Task<LocalAiRuntimeSnapshot> RestartForSetupAsync(CancellationToken cancellationToken = default)
        {
            RestartForSetupCalls++;
            Snapshot = RestartForSetupHandler is null
                ? snapshot ?? Snapshot
                : await RestartForSetupHandler(cancellationToken);
            return Snapshot;
        }

        public async Task<LocalAiRuntimeSnapshot> RestartForSetupRollbackAsync(
            CancellationToken cancellationToken = default)
        {
            RestartForSetupRollbackCalls++;
            Snapshot = RestartHandler is null
                ? snapshot ?? Snapshot
                : await RestartHandler(cancellationToken);
            return Snapshot;
        }

        public Task<LocalAiRuntimeSnapshot> ReleaseSetupGatewayRouteAsync(
            CancellationToken cancellationToken = default)
        {
            ReleaseSetupGatewayRouteCalls++;
            return Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> AcknowledgeSetupGatewayRouteAsync(
            CancellationToken cancellationToken = default)
        {
            AcknowledgeSetupGatewayRouteCalls++;
            return Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingRuntimeAcquirer : ILlamaRuntimeAcquirer
    {
        public int RemoveCalls { get; private set; }

        public Task<LlamaRuntimeInstallResult> InstallAsync(
            string localDataDirectory,
            LlamaRuntimeVariant runtime,
            IProgress<LocalAiArtifactInstallProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void RemoveInstalledRuntime(
            string localDataDirectory,
            LlamaRuntimeInstallResult install) => RemoveCalls++;
    }

    private static LocalAiRuntimeSnapshot HealthySnapshot(LocalAiResolvedInstall install) => new(
        LocalAiRuntimeState.Healthy,
        LocalAiOwnership.CompanionManaged,
        install.Endpoint!,
        install.Manifest.EngineVersion,
        install.Manifest.ModelCatalogId,
        new LocalAiModelEvidence(
            LocalAiModelAvailabilityState.Verified,
            DateTimeOffset.UtcNow,
            install.Manifest.ModelAsset.Sha256,
            install.Manifest.ModelAsset.SizeBytes),
        42,
        DateTimeOffset.UtcNow,
        null,
        DateTimeOffset.UtcNow)
    {
        GatewayRouteRequiresResolution = false,
    };

    private static LocalAiResolvedInstall CreateLocalAiResolvedInstall(string localDataDirectory, int port)
    {
        var paths = new LocalAiPaths(localDataDirectory);
        const string revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string executableRelative = Path.Combine("engines", "llama-server", "b1", "win-x64", "llama-server.exe");
        string modelRelative = Path.Combine("models", "model.gguf");
        var manifest = new LocalAiInstallManifest
        {
            EngineVersion = "b1",
            Architecture = "x64",
            RuntimeId = "llama-server-b1-win-x64-cpu",
            ModelCatalogId = "test-model",
            SelectedGpuId = "CPU",
            ExecutablePath = executableRelative,
            RuntimeAssets = ImmutableArray.Create(new LocalAiAssetReceipt
            {
                FileName = "llama-server.exe",
                SourceUrl = "https://github.com/example/example/releases/download/b1/llama-server.exe",
                SizeBytes = 1,
                Sha256 = new string('a', 64),
            }),
            ModelPath = modelRelative,
            ModelId = $"owner/repo@{revision}",
            ModelAlias = "test-model",
            ModelAsset = new LocalAiAssetReceipt
            {
                FileName = "model.gguf",
                SourceUrl = $"https://huggingface.co/owner/repo/resolve/{revision}/model.gguf",
                SizeBytes = 1,
                Sha256 = new string('a', 64),
            },
            Endpoint = $"http://127.0.0.1:{port}/v1",
            ContextLength = 4096,
        };
        return new LocalAiResolvedInstall(
            manifest,
            Path.Combine(paths.RootDirectory, executableRelative),
            Path.Combine(paths.RootDirectory, modelRelative),
            new Uri(manifest.Endpoint!));
    }

    [Fact]
    public void LocalAiDisabled_SkipsEveryLocalAiMutation()
    {
        var ctx = CreateContext(new SetupConfig
        {
            LocalAi = new LocalAiConfig { Enabled = false }
        });
        var steps = SetupStepFactory.BuildDefaultSteps();
        SetupStep[] localAiSteps =
        [
            steps.Single(step => step is PreflightLocalAiHardwareStep),
            steps.Single(step => step is ReconcileLocalAiInstallationStep),
            steps.Single(step => step is AcquireLocalAiRuntimeStep),
            steps.Single(step => step is AcquireLocalAiModelStep),
            steps.Single(step => step is PersistLocalAiManifestStep),
            steps.Single(step => step is StartLocalAiRuntimeStep),
            steps.Single(step => step is ConfigureLocalAiWslNetworkingStep),
            steps.Single(step => step is VerifyLocalAiWslStep),
            steps.Single(step => step is ConfigureLocalAiGatewayStep),
        ];

        Assert.All(localAiSteps, step => Assert.True(step.CanSkip(ctx), step.Id));
        Assert.False(steps.Single(step => step is PreflightWslStep).CanSkip(ctx));
        Assert.False(steps.Single(step => step is EnsureWslPlatformStep).CanSkip(ctx));
    }

    private static ExistingConfigDetector.ExistingConfig ExistingLocalAiGateway(
        bool hasDistro,
        bool appOwned) =>
        new(
            HasLocalGateway: true,
            LocalGatewayId: "gateway-id",
            LocalGatewayUrl: "ws://127.0.0.1:18789",
            HasDistro: hasDistro,
            HasDistroDataDirectory: hasDistro,
            DistroIsAppOwned: appOwned,
            DistroName: hasDistro ? "OpenClawGateway" : null,
            HasIdentityFiles: true,
            PreservedGatewayCount: 0,
            PreservedGatewayNames: []);

    private static SetupConfig LocalAiRecoveryConfig() => new()
    {
        DistroName = "OpenClawGateway",
        GatewayPort = 18789,
        LocalAiRecoveryGatewayId = "gateway-id",
    };

    private static OpenClaw.Connection.GatewayRecord ManagedGatewayRecord() => new()
    {
        Id = "gateway-id",
        Url = "ws://127.0.0.1:18789",
        IsLocal = true,
        SetupManagedDistroName = "OpenClawGateway",
    };

    [Fact]
    public void ModelLoadingProof_IsOptInAndNeverPartOfDefaultSetup()
    {
        var setup = SetupStepFactory.BuildDefaultSteps();
        var proof = SetupStepFactory.BuildLocalAiInferenceProofSteps();

        Assert.Collection(proof,
            step => Assert.IsType<CaptureLocalAiGpuBaselineStep>(step),
            step => Assert.IsType<VerifyLocalAiInferenceStep>(step),
            step => Assert.IsType<VerifyLocalAiGpuLoadStep>(step));
        Assert.All(proof, step => Assert.DoesNotContain(setup, candidate => candidate.Id == step.Id));
        Assert.All(proof, step => Assert.True(step.CanSkip(CreateContext(new SetupConfig
        {
            LocalAi = new LocalAiConfig { Enabled = false }
        }))));

        string wslProbe = VerifyLocalAiWslStep.BuildProbeScript(49152);
        Assert.Contains("/health", wslProbe);
        Assert.Contains("/models?autoload=false", wslProbe);
        Assert.DoesNotContain("/completion", wslProbe);
        Assert.DoesNotContain("/models/load", wslProbe);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TailscaleDisabled_FreshAndReplacementPipelinesSkipOnlyTailscaleSteps(bool replacement)
    {
        var executed = new List<string>();
        var config = new SetupConfig
        {
            CleanBeforeRun = replacement,
            Tailscale = new TailscaleConfig { Enabled = false }
        };
        var ctx = CreateContext(config);
        var baselineStepId = replacement ? "replace-gateway" : "create-gateway";
        var pipeline = new SetupPipeline([
            new MockStep(baselineStepId, (_, _) =>
            {
                executed.Add(baselineStepId);
                return Task.FromResult(StepResult.Ok());
            }),
            new PreflightWindowsTailscaleStep(),
            new InstallTailscaleStep(),
            new AuthorizeTailscaleStep(),
            new FinalizeTailscaleServeStep(),
            new MockStep("pair", (_, _) =>
            {
                executed.Add("pair");
                return Task.FromResult(StepResult.Ok());
            }),
        ]);

        var result = await pipeline.RunAsync(ctx);

        Assert.Equal(PipelineOutcome.Success, result.Outcome);
        Assert.Equal([baselineStepId, "pair"], executed);
    }

    [Fact]
    public void BuildWizardOnlySteps_FinalizesWindowsNodeContextAfterWizard()
    {
        var steps = SetupStepFactory.BuildWizardOnlySteps();

        Assert.Collection(
            steps,
            step => Assert.IsType<RunGatewayWizardStep>(step),
            step => Assert.IsType<WindowsNodeBootstrapContextStep>(step));
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
                    // slopwatch-ignore: SW004 Test deliberately blocks until cancellation to exercise cancellation behavior deterministically.
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
    public async Task RunAsync_StepFails_WithRollbackOverrideDisabled_NoRollback()
    {
        var rollbackCalled = false;
        var config = new SetupConfig { RollbackOnFailure = true };
        var ctx = CreateContext(config);
        var pipeline = new SetupPipeline([
            new MockStep(
                "refresh",
                (_, _) => Task.FromResult(StepResult.Fail("refresh failed")),
                (_, _) => { rollbackCalled = true; return Task.CompletedTask; }),
        ], rollbackOnFailureOverride: false);

        var result = await pipeline.RunAsync(ctx);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.False(rollbackCalled);
        Assert.True(config.RollbackOnFailure);
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
    public async Task RunAsync_CancelledDuringStep_RollsBackInterruptedThenCompletedWithFreshTokens()
    {
        using var cts = new CancellationTokenSource();
        var rollbacks = new List<(string StepId, bool WasCancelled)>();
        var config = new SetupConfig { RollbackOnFailure = true };
        var ctx = CreateContext(config, cts.Token);

        Task RecordRollback(string stepId, CancellationToken ct)
        {
            rollbacks.Add((stepId, ct.IsCancellationRequested));
            return Task.CompletedTask;
        }

        var pipeline = new SetupPipeline([
            new MockStep(
                "s1",
                (_, _) => Task.FromResult(StepResult.Ok()),
                (_, ct) => RecordRollback("s1", ct)),
            new MockStep(
                "s2",
                (_, _) => Task.FromResult(StepResult.Ok()),
                (_, ct) => RecordRollback("s2", ct)),
            new MockStep(
                "s3",
                (_, ct) =>
                {
                    cts.Cancel();
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(StepResult.Ok());
                },
                (_, ct) => RecordRollback("s3", ct)),
        ]);

        var result = await pipeline.RunAsync(ctx);

        Assert.Equal(PipelineOutcome.Cancelled, result.Outcome);
        Assert.Equal(
            [("s3", false), ("s2", false), ("s1", false)],
            rollbacks);
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
    public async Task UninstallAsync_RejectsUnsafeDistroBeforeRollbacks()
    {
        var rollbackCalled = false;
        var config = new SetupConfig
        {
            ConfirmDestructive = true,
            DistroName = @"..\..",
        };
        var ctx = CreateContext(config);
        var pipeline = new SetupPipeline(
        [
            new MockStep(
                "unsafe",
                (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) =>
                {
                    rollbackCalled = true;
                    return Task.CompletedTask;
                }),
        ]);

        var result = await pipeline.UninstallAsync(ctx);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal(ValidateDistroInstallPathStep.StepId, result.FailedStepId);
        Assert.Contains("Invalid managed WSL distro name", result.Message);
        Assert.False(rollbackCalled);
        Assert.False(SetupPipeline.ShouldRunTrayArtifactCleanup(result, dryRun: false));
    }

    [Fact]
    public void TrayArtifactCleanup_RunsOnlyAfterValidatedLiveUninstall()
    {
        var validationFailure = new PipelineResult(
            PipelineOutcome.Failed,
            ValidateDistroInstallPathStep.StepId,
            "unsafe");

        Assert.False(SetupPipeline.ShouldRunTrayArtifactCleanup(validationFailure, dryRun: false));
        Assert.False(SetupPipeline.ShouldRunTrayArtifactCleanup(
            new PipelineResult(PipelineOutcome.Success),
            dryRun: true));
        Assert.True(SetupPipeline.ShouldRunTrayArtifactCleanup(
            new PipelineResult(PipelineOutcome.Failed, "other-step", "failed"),
            dryRun: false));
        Assert.True(SetupPipeline.ShouldRunTrayArtifactCleanup(
            new PipelineResult(PipelineOutcome.Cancelled),
            dryRun: false));
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
                    // slopwatch-ignore: SW004 Test deliberately blocks until cancellation to exercise cancellation behavior deterministically.
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
