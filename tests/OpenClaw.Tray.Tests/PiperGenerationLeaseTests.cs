using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class PiperGenerationLeaseTests
{
    [Fact(Timeout = 5000)]
    public async Task Dispose_ReturnsWhileGenerationHoldsTheGate_AndNativeReleaseFollowsGeneration()
    {
        var order = new List<string>();
        var generationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGeneration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new PiperGenerationLease(() =>
        {
            lock (order)
                order.Add("released");
        });

        var generate = lease.RunAsync(_ =>
        {
            generationEntered.TrySetResult();
            releaseGeneration.Task.GetAwaiter().GetResult();
            lock (order)
                order.Add("generated");
            return 1;
        }, CancellationToken.None);

        await generationEntered.Task;

        var dispose = lease.DisposeAsync();
        Assert.False(dispose.IsCompleted);
        lock (order)
            Assert.DoesNotContain("released", order);

        lease.Dispose();
        Assert.False(dispose.IsCompleted);

        releaseGeneration.TrySetResult();
        Assert.Equal(1, await generate);
        await dispose;

        Assert.Equal(new[] { "generated", "released" }, order);
    }

    [Fact]
    public async Task RunAsync_AfterDispose_DoesNotCallGenerate()
    {
        var calls = 0;
        var lease = new PiperGenerationLease(static () => { });
        await lease.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            lease.RunAsync(_ =>
            {
                Interlocked.Increment(ref calls);
                return 0;
            }, CancellationToken.None));

        Assert.Equal(0, calls);
    }
}
