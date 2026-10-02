using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupNativeCompletionTimingTests
{
    [Fact]
    public void PublishedBudgetIncludesBothConnectionsRecoveryModelAndNavigation()
    {
        Assert.Equal(TimeSpan.FromMinutes(13.5), SetupNativeCompletionTiming.Execution);
        Assert.Equal(SetupNativeCompletionTiming.Connection * 2 + SetupNativeCompletionTiming.ModelRecovery +
            SetupNativeCompletionTiming.ModelVerification + SetupNativeCompletionTiming.Navigation,
            SetupNativeCompletionTiming.Execution);
        Assert.True(SetupNativeCompletionTiming.ModelVerification > TimeSpan.FromSeconds(120));
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
