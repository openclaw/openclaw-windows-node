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
    [InlineData("connection")]
    [InlineData("recovery")]
    [InlineData("model")]
    public async Task EachVerificationPhaseHasAnEnforcedDeadline(string phase)
    {
        var budget = phase switch
        {
            "connection" => SetupNativeCompletionTiming.Connection,
            "recovery" => SetupNativeCompletionTiming.ModelRecovery,
            _ => SetupNativeCompletionTiming.ModelVerification
        };
        var clock = new ManualTimeProvider();
        var work = SetupNativeCompletionTiming.RunAsync(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return true;
        }, budget, phase, default, clock);
        clock.Advance(budget);
        var error = await Assert.ThrowsAsync<TimeoutException>(() => work);
        Assert.Contains(phase, error.Message);
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
        }, SetupNativeCompletionTiming.ModelRecovery, "recovery", lifetime.Token, clock);
        lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
    }
}
