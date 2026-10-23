using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupNativeCompletionTimingTests
{
    [Fact]
    public void PublishedBudgetIncludesBothConnectionsRecoveryModelAndNavigation()
    {
        Assert.Equal(TimeSpan.FromMinutes(27), SetupNativeCompletionTiming.Execution);
        // A fresh WSL runtime can withdraw and publish its route with two guarded
        // Gateway restarts, then spend its full startup allowance reaching Healthy.
        Assert.True(SetupNativeCompletionTiming.ModelRecovery >
            StartGatewayStep.RestartCommandTimeout * 2 + TimeSpan.FromSeconds(90));
        Assert.Equal(SetupNativeCompletionTiming.Connection * 2 + SetupNativeCompletionTiming.ModelRecovery +
            SetupNativeCompletionTiming.ModelVerification + SetupNativeCompletionTiming.Navigation,
            SetupNativeCompletionTiming.Execution);
        Assert.True(SetupNativeCompletionTiming.ModelVerification > TimeSpan.FromSeconds(120));
    }

    [Fact]
    public async Task ModelRecoveryAllowsTwoFullGatewayRestartsAndRuntimeStartup()
    {
        var clock = new ManualTimeProvider();
        var firstRestarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRestarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRestartScheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRestartScheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtimeScheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restartTimers = 0;
        clock.TimerScheduled += due =>
        {
            if (due == StartGatewayStep.RestartCommandTimeout)
            {
                if (Interlocked.Increment(ref restartTimers) == 1)
                    firstRestartScheduled.TrySetResult();
                else
                    secondRestartScheduled.TrySetResult();
            }
            else if (due == TimeSpan.FromSeconds(90))
                runtimeScheduled.TrySetResult();
        };
        var recovery = SetupNativeCompletionTiming.RunAsync(async ct =>
        {
            await Task.Delay(StartGatewayStep.RestartCommandTimeout, clock, ct);
            firstRestarted.SetResult();
            await Task.Delay(StartGatewayStep.RestartCommandTimeout, clock, ct);
            secondRestarted.SetResult();
            await Task.Delay(TimeSpan.FromSeconds(90), clock, ct);
            return true;
        }, SetupNativeCompletionTiming.ModelRecovery, SetupNativeCompletionPhase.ModelRecovery, default, clock);

        await firstRestartScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(StartGatewayStep.RestartCommandTimeout);
        await firstRestarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await secondRestartScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(StartGatewayStep.RestartCommandTimeout);
        await secondRestarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runtimeScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(90));
        Assert.True(await recovery);
    }

    [Theory]
    [InlineData(30, 1620)]
    [InlineData(180, 1620)]
    [InlineData(181, 1622)]
    [InlineData(600, 2460)]
    [InlineData(3600, 8460)]
    public void ExecutionWindowFollowsStartupOverrideWithinPersistedBounds(int startupSeconds, int executionSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(executionSeconds), SetupNativeCompletionTiming.ExecutionFor(
            SetupNativeCompletionTiming.ConnectionFor(TimeSpan.FromSeconds(startupSeconds))));
        Assert.Equal(TimeSpan.FromSeconds(1620), SetupNativeCompletionTiming.MinimumExecution);
        Assert.Equal(TimeSpan.FromSeconds(8460), SetupNativeCompletionTiming.MaximumExecution);
    }

    [Theory]
    [InlineData(SetupNativeCompletionPhase.Connection)]
    [InlineData(SetupNativeCompletionPhase.ModelRecovery)]
    [InlineData(SetupNativeCompletionPhase.ModelVerification)]
    public async Task EachVerificationPhaseHasAnEnforcedDeadline(SetupNativeCompletionPhase phase)
    {
        var budget = phase switch
        {
            SetupNativeCompletionPhase.Connection => SetupNativeCompletionTiming.Connection,
            SetupNativeCompletionPhase.ModelRecovery => SetupNativeCompletionTiming.ModelRecovery,
            _ => SetupNativeCompletionTiming.ModelVerification
        };
        var clock = new ManualTimeProvider();
        var work = SetupNativeCompletionTiming.RunAsync(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return true;
        }, budget, phase, default, clock);
        clock.Advance(budget);
        var error = await Assert.ThrowsAsync<SetupNativeCompletionTimeoutException>(() => work);
        Assert.Equal(phase, error.Phase);
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellationNotPhaseTimeout()
    {
        var clock = new ManualTimeProvider();
        using var lifetime = new CancellationTokenSource();
        var work = SetupNativeCompletionTiming.RunAsync(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return true;
        }, SetupNativeCompletionTiming.ModelRecovery, SetupNativeCompletionPhase.ModelRecovery, lifetime.Token, clock);
        lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
    }

    [Fact]
    public void TimeoutDiagnosticsDoNotUseUnderlyingCommandOrModelContent()
    {
        var error = new SetupNativeCompletionTimeoutException(SetupNativeCompletionPhase.ModelRecovery,
            new OperationCanceledException("sensitive fixture content"));
        Assert.DoesNotContain("sensitive", error.Message);
        Assert.Equal(SetupNativeCompletionPhase.ModelRecovery, error.Phase);
    }
}
