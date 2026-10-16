using OpenClaw.TestSupport;

namespace OpenClaw.Tray.UITests;

public sealed class OnboardingOcrDeadlineTests
{
    [Fact]
    public async Task ColdRecognitionCanCompleteAfterFormerThreeSecondDeadline()
    {
        var clock = new ManualTimeProvider();
        var recognition = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = OnboardingNativeProof.RunOcrPassAsync(ct => recognition.Task.WaitAsync(ct), clock);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(pending.IsCompleted);
        recognition.SetResult("not available");
        Assert.Equal("not available", await pending);
    }

    [Fact]
    public async Task RecognitionStillFailsAtBoundedThirtySecondDeadline()
    {
        var clock = new ManualTimeProvider();
        var recognition = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = OnboardingNativeProof.RunOcrPassAsync(ct => recognition.Task.WaitAsync(ct), clock);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsAsync<TimeoutException>(() => pending);
        Assert.Contains("30-second", error.Message);
    }

    [Fact]
    public async Task RecognitionErrorsAreNotReplacedBySuccessfulProof()
    {
        var failure = new IOException("Synthetic OCR failure");
        var error = await Assert.ThrowsAsync<IOException>(() =>
            OnboardingNativeProof.RunOcrPassAsync(_ => Task.FromException<string>(failure)));
        Assert.Same(failure, error);
    }
}
