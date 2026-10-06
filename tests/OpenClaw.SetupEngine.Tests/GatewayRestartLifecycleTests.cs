using OpenClaw.Connection;

namespace OpenClaw.SetupEngine.Tests;

public class GatewayRestartLifecycleTests
{
    [Theory]
    [InlineData("200")]
    [InlineData("401")]
    [InlineData("403")]
    public async Task Restart_AllowsSlowCliBeforeHttpReachability(string httpStatus)
    {
        var commands = new Commands((command, timeout) => command.Contains("gateway restart")
            ? new CommandResult(0, "", "", TimeSpan.FromSeconds(65), false)
            : Ok(httpStatus));
        var result = await StartGatewayStep.RestartAndWaitForHealthAsync(Context(commands), default);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(TimeSpan.FromSeconds(420), commands.Calls[0].Timeout);
        Assert.Contains("curl", commands.Calls[1].Command);
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(true, -1)]
    [InlineData(true, 0)]
    public async Task CliTimeout_IsTerminalDespiteRetryMarkers(bool restart, int exitCode)
    {
        var commands = new Commands((command, _) => command.StartsWith("ss ")
            ? Ok("")
            : new CommandResult(exitCode, SetupWizardRunner.RestartServingOwnerDiagnostic,
                "start-limit", TimeSpan.FromSeconds(90), true));
        var ctx = Context(commands);

        var result = restart
            ? await StartGatewayStep.RestartAndWaitForHealthAsync(ctx, default)
            : await new StartGatewayStep().ExecuteAsync(ctx, default);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("timedOut=True", result.Message);
        Assert.Contains("elapsed=90.0s", result.Message);
        Assert.Contains("state is unknown", result.Message);
        Assert.DoesNotContain(commands.Calls, c => c.Command.Contains("reset-failed") || c.Command.Contains("curl"));
        Assert.Single(commands.Calls, c => c.Command.Contains("openclaw gateway"));
    }

    [Fact]
    public async Task Failure_RetainsBothStreamsWithBoundedRedactedOutput()
    {
        var secret = new string('a', 64);
        var commands = new Commands((_, _) => new CommandResult(1,
            $"token={secret}\nstdout reason", "stderr reason " + new string('!', 5000),
            TimeSpan.FromSeconds(12), false));

        var result = await StartGatewayStep.RestartAndWaitForHealthAsync(Context(commands), default);

        Assert.Contains("stdout reason", result.Message);
        Assert.Contains("stderr reason", result.Message);
        Assert.Contains("[truncated]", result.Message);
        Assert.DoesNotContain(secret, result.Message);
        Assert.True(result.Message!.Length < 4400);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReloadTransition_RechecksOwnershipBeforeSingleGuardedRetry(bool contention)
    {
        var restored = false;
        var inspected = false;
        var restarts = 0;
        var commands = new Commands((command, _) =>
        {
            if (command.Contains("config set"))
            {
                restored = true;
                return Ok();
            }
            if (!command.Contains("gateway restart"))
                return Ok();
            Assert.True(restored);
            if (++restarts == 1)
                return Refusal(contention);
            Assert.True(inspected);
            return Ok();
        });
        var ctx = Context(commands);
        ctx.EndpointProvenanceProbe = (_, _) =>
        {
            inspected = true;
            return Task.FromResult(Owned(ctx));
        };
        var result = await Runner(ctx).RestoreReloadModeAsync();

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, restarts);
        Assert.DoesNotContain(commands.Calls, c => c.Command.Contains("systemctl"));
    }

    [Fact]
    public async Task GuardedRetry_SharesRemainingBudgetWithOwnershipAndFirstAttempt()
    {
        var clock = new Clock();
        var restarts = 0;
        var commands = new Commands((command, timeout) =>
        {
            if (!command.Contains("gateway restart"))
                return Ok();
            if (++restarts == 1)
            {
                clock.Advance(410);
                return Refusal();
            }

            Assert.Equal(TimeSpan.FromSeconds(95), timeout);
            return Ok();
        });
        var ctx = Context(commands);
        var inspections = 0;
        ctx.EndpointProvenanceProbe = (_, _) =>
        {
            if (++inspections == 1)
                clock.Advance(65);
            return Task.FromResult(Owned(ctx));
        };

        var result = await Runner(ctx, clock).RestoreReloadModeAsync();

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, restarts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LongOutput_PreservesExactGuardedRecovery(bool contention, bool stderr)
    {
        var restarts = 0;
        var commands = new Commands((command, _) =>
        {
            if (!command.Contains("gateway restart") || ++restarts != 1)
                return Ok();
            var refusal = Refusal(contention);
            var output = new string('!', 3000) + refusal.Stdout + "\n" + refusal.Stderr;
            return refusal with { Stdout = stderr ? "" : output, Stderr = stderr ? output : "" };
        });

        var result = await Runner(Context(commands)).RestoreReloadModeAsync();

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, restarts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LongOutput_TimeoutNeverAuthorizesRecovery(bool contention, bool stderr)
    {
        var commands = new Commands((command, _) =>
        {
            if (!command.Contains("gateway restart"))
                return Ok();
            var refusal = Refusal(contention);
            var output = new string('!', 3000) + refusal.Stdout + "\n" + refusal.Stderr;
            return refusal with
            {
                Stdout = stderr ? "" : output,
                Stderr = stderr ? output : "",
                TimedOut = true,
            };
        });

        var result = await Runner(Context(commands)).RestoreReloadModeAsync();

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.False(result.GatewayRestartServingOwnerUnavailable);
        Assert.False(result.GatewayRestartIntentContention);
        Assert.Single(commands.Calls, c => c.Command.Contains("gateway restart"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongOutput_ContentionRequiresBothExactMarkers(bool typedErrorOnly)
    {
        var commands = new Commands((command, _) => !command.Contains("gateway restart")
            ? Ok()
            : new CommandResult(1, new string('!', 3000) +
                (typedErrorOnly
                    ? GatewayWizardRestartRecoveryPolicy.RestartIntentCoordinatorContentionError
                    : GatewayWizardRestartRecoveryPolicy.RestartIntentRecordingRefusal),
                "", TimeSpan.Zero, false));

        var result = await Runner(Context(commands)).RestoreReloadModeAsync();

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.False(result.GatewayRestartIntentContention);
        Assert.Single(commands.Calls, c => c.Command.Contains("gateway restart"));
    }

    [Fact]
    public async Task OwnershipWaitExhaustsBudget_NoSecondRestartAndOriginalFailureRetained()
    {
        var clock = new Clock();
        var commands = new Commands((command, _) => command.Contains("gateway restart") ? Refusal() : Ok());
        var ctx = Context(commands);
        ctx.EndpointProvenanceProbe = (_, _) =>
        {
            clock.Advance(571);
            return Task.FromResult(Owned(ctx));
        };

        var result = await Runner(ctx, clock).RestoreReloadModeAsync();

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("lifecycle deadline exhausted", result.Message);
        Assert.Contains(SetupWizardRunner.RestartServingOwnerDiagnostic, result.Message);
        Assert.Single(commands.Calls, c => c.Command.Contains("gateway restart"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutDuringRestorationCleanup_DoesNotRetryOrLoseTerminalOutcome(bool wizardThrows)
    {
        var commands = new Commands((command, _) => command.Contains("gateway restart")
            ? Refusal() with { TimedOut = true }
            : Ok());
        var runner = Runner(Context(commands));
        runner.MarkReloadSuspended();

        var result = await runner.RunWithReloadRestorationAsync(() => wizardThrows
            ? Task.FromException<StepResult>(new OperationCanceledException())
            : Task.FromResult(StepResult.Fail("wizard failed")));

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Single(commands.Calls, c => c.Command.Contains("gateway restart"));
    }

    [Fact]
    public async Task ReloadWriteTimeout_WithContentionMarker_DoesNotRetryOrRestart()
    {
        var commands = new Commands((_, _) =>
            new CommandResult(-1, SetupWizardRunner.StartupMigrationLeaseDiagnostic, "", TimeSpan.FromSeconds(15), true));

        var result = await Runner(Context(commands)).RestoreReloadModeAsync();

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Single(commands.Calls);
    }

    [Fact]
    public async Task FailedOwnershipRetainsOriginalRestartClassification()
    {
        var commands = new Commands((command, _) => command.Contains("gateway restart") ? Refusal(true) : Ok());
        var ctx = Context(commands);
        ctx.EndpointProvenanceProbe = (_, _) => Task.FromResult(new GatewayEndpointProvenance(
            GatewayEndpointProvenanceKind.UnknownListener, ctx.Config.GatewayPort));

        var result = await Runner(ctx).RestoreReloadModeAsync();

        Assert.Contains("Gateway restart after wizard failed:", result.Message);
        Assert.Contains(GatewayWizardRestartRecoveryPolicy.RestartIntentCoordinatorContentionError, result.Message);
        Assert.Contains("ownership verification failed", result.Message);
        Assert.Single(commands.Calls, c => c.Command.Contains("gateway restart"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GuardedRetryFailure_RetainsInitialRefusalAndFinalOutcome(bool contention, bool timedOut)
    {
        var restarts = 0;
        var commands = new Commands((command, _) => !command.Contains("gateway restart")
            ? Ok()
            : ++restarts == 1
                ? Refusal(contention)
                : new CommandResult(-1, "", "retry failed", TimeSpan.FromSeconds(12), timedOut));

        var result = await Runner(Context(commands)).RestoreReloadModeAsync();

        Assert.Equal(timedOut ? StepOutcome.FailedTerminal : StepOutcome.Failed, result.Outcome);
        Assert.Contains("retry failed", result.Message);
        Assert.Contains("Initial restart failure:", result.Message);
        Assert.Contains(contention
            ? GatewayWizardRestartRecoveryPolicy.RestartIntentCoordinatorContentionError
            : SetupWizardRunner.RestartServingOwnerDiagnostic, result.Message);
        Assert.Equal(2, restarts);
        Assert.DoesNotContain(commands.Calls, c => c.Command.Contains("curl"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FinalOwnershipDeadline_ReportsCompletedCliAndReachability(bool retry, bool honorCancellation)
    {
        var clock = new Clock();
        var restarts = 0;
        var commands = new Commands((command, _) => command.Contains("gateway restart") &&
            ++restarts == 1 && retry ? Refusal() : Ok());
        var ctx = Context(commands);
        var inspections = 0;
        ctx.EndpointProvenanceProbe = async (_, ct) =>
        {
            if (retry && ++inspections == 1)
                return Owned(ctx);
            var pending = honorCancellation ? Task.Delay(Timeout.InfiniteTimeSpan, ct) : Task.CompletedTask;
            clock.Advance(571);
            await pending;
            return Owned(ctx);
        };

        var result = await Runner(ctx, clock).RestoreReloadModeAsync();

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("final ownership verification", result.Message);
        Assert.Contains("CLI restart and HTTP reachability completed", result.Message);
        Assert.Contains("final ownership verification stage exceeded the deadline", result.Message);
        Assert.Contains("no automatic retry", result.Message);
        Assert.DoesNotContain("Gateway state is unknown", result.Message);
        if (retry)
            Assert.Contains(SetupWizardRunner.RestartServingOwnerDiagnostic, result.Message);
        Assert.Equal(retry ? 2 : 1, restarts);
        Assert.Single(commands.Calls, c => c.Command.Contains("curl"));
    }

    [Fact]
    public async Task FinalOwnershipFailure_RemainsEligibleForRestartDiagnostics()
    {
        var commands = new Commands((_, _) => Ok());
        var ctx = Context(commands);
        ctx.EndpointProvenanceProbe = (_, _) => Task.FromResult(new GatewayEndpointProvenance(
            GatewayEndpointProvenanceKind.UnknownListener, ctx.Config.GatewayPort));

        var result = await Runner(ctx).RestoreReloadModeAsync();

        Assert.False(result.IsSuccess);
        Assert.StartsWith("Gateway restart after wizard failed:", result.Message);
        Assert.Contains("after restoring gateway reload", result.Message);
        Assert.Single(commands.Calls, c => c.Command.Contains("gateway restart"));
    }

    [Fact]
    public async Task CallerCancellation_DoesNotLaunchRestart()
    {
        var commands = new Commands((_, _) => Ok());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            StartGatewayStep.RestartAndWaitForHealthAsync(Context(commands), cancellation.Token));

        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task LifecycleDeadline_CancelsInFlightOwnershipProbe()
    {
        var clock = new Clock();
        var commands = new Commands((command, _) => command.Contains("gateway restart") ? Refusal() : Ok());
        var ctx = Context(commands);
        ctx.EndpointProvenanceProbe = async (_, ct) =>
        {
            var pending = Task.Delay(Timeout.InfiniteTimeSpan, ct);
            clock.Advance(571);
            await pending;
            return Owned(ctx);
        };

        var result = await Runner(ctx, clock).RestoreReloadModeAsync();

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("guarded retry ownership verification", result.Message);
        Assert.Contains("Gateway state is unknown", result.Message);
        Assert.DoesNotContain("CLI restart and HTTP reachability completed", result.Message);
        Assert.Single(commands.Calls, c => c.Command.Contains("gateway restart"));
    }

    [Fact]
    public async Task PipelineRetryExecutor_DoesNotRepeatUnknownRestart()
    {
        var commands = new Commands((_, _) => Refusal() with { TimedOut = true });
        var ctx = Context(commands);
        var step = new RestartGatewayStep();

        var result = await RetryExecutor.ExecuteWithRetry(
            () => step.ExecuteAsync(ctx, default), step.Retry, ctx.Logger, step.Id, default);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Single(commands.Calls);
    }

    private static SetupWizardRunner Runner(SetupContext ctx, TimeProvider? clock = null) =>
        new(ctx, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }, clock);

    private static CommandResult Ok(string stdout = "200") => new(0, stdout, "", TimeSpan.Zero, false);

    private static CommandResult Refusal(bool contention = false) => new(1,
        contention ? GatewayWizardRestartRecoveryPolicy.RestartIntentCoordinatorContentionError
            : SetupWizardRunner.RestartServingOwnerDiagnostic,
        contention ? GatewayWizardRestartRecoveryPolicy.RestartIntentRecordingRefusal : "",
        TimeSpan.Zero, false);

    private static GatewayEndpointProvenance Owned(SetupContext ctx) =>
        new(GatewayEndpointProvenanceKind.ExpectedManagedGateway, ctx.Config.GatewayPort);

    private static SetupContext Context(Commands commands)
    {
        var ctx = new SetupContext(new SetupConfig
        {
            Gateway = new GatewayConfig { HealthTimeoutSeconds = 90 },
        }, new SetupLogger(null), new TransactionJournal(null), commands, default)
        {
            DistroName = "test-distro",
        };
        ctx.EndpointProvenanceProbe = (_, _) => Task.FromResult(Owned(ctx));
        return ctx;
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        private readonly List<ClockTimer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(int seconds)
        {
            _ticks += TimeSpan.FromSeconds(seconds).Ticks;
            foreach (var timer in _timers.ToArray())
                timer.FireIfDue();
        }

        private sealed class ClockTimer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            private long _due = long.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
                return true;
            }
            public void FireIfDue()
            {
                if (clock._ticks < _due)
                    return;
                _due = long.MaxValue;
                callback(state);
            }
            public void Dispose() => _due = long.MaxValue;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Commands(Func<string, TimeSpan, CommandResult> execute) : ICommandRunner
    {
        public List<(string Command, TimeSpan Timeout)> Calls { get; } = [];
        public Task<CommandResult> RunAsync(string executable, string[] arguments, TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null,
            string? stdinInput = null, CancellationToken ct = default, Stream? stdinStream = null) =>
            throw new NotSupportedException();

        public Task<CommandResult> RunInWslAsync(string distroName, string command, TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null, CancellationToken ct = default,
            string? user = null, bool inputViaStdin = false)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((command, timeout));
            return Task.FromResult(execute(command, timeout));
        }
    }
}
