using System.Runtime.InteropServices;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.SetupEngine.Tests;

public sealed class LocalAiHardwareProbeCacheTests
{
    private static readonly HostHardwareInfo Complete = new(Architecture.X64, 128L << 30, 96L << 30,
        [new(GpuVendor.Nvidia, "Synthetic GPU", 48L << 30, 44L << 30,
            DriverVersion: "999.0", CudaMajorVersion: 13, StableId: "fixture-gpu")], false);

    [Fact]
    public async Task IncompleteFactsAreReprobedWithoutAnExplicitRefreshThenCompleteFactsAreReused()
    {
        var incomplete = new HostHardwareInfo(Architecture.X64, null, null,
            [new(GpuVendor.Nvidia, "Synthetic incomplete GPU")], false);
        Assert.Equal(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete,
            LocalInferenceEligibility.Evaluate(incomplete).FailureCode);
        var calls = 0;
        var cache = new LocalAiHardwareProbeCache(() =>
            Interlocked.Increment(ref calls) == 1 ? incomplete : Complete);
        var first = cache.GetAsync();
        Assert.Same(incomplete, await first);
        var retry = cache.GetAsync();
        Assert.NotSame(first, retry);
        Assert.Same(Complete, await retry);
        Assert.Same(retry, cache.GetAsync());
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FaultedOrCancelledProbeDoesNotPoisonTheWindowCache(bool cancelled)
    {
        var calls = 0;
        var cache = new LocalAiHardwareProbeCache(() =>
        {
            if (Interlocked.Increment(ref calls) > 1) return Complete;
            if (cancelled) throw new OperationCanceledException();
            throw new InvalidOperationException("Synthetic probe failure");
        });
        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync());
        else await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync());
        Assert.Same(Complete, await cache.GetAsync());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PendingProbeIsSharedAndExplicitRefreshCreatesANewProbe()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var cache = new LocalAiHardwareProbeCache(() =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("The test probe was not released.");
            return Complete;
        });
        var first = cache.GetAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(first, cache.GetAsync());
            Assert.Equal(1, calls);
        }
        finally
        {
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
        }
        var refreshed = cache.GetAsync(forceRefresh: true);
        Assert.NotSame(first, refreshed);
        Assert.Same(Complete, await refreshed);
        Assert.Equal(2, calls);
    }
}
