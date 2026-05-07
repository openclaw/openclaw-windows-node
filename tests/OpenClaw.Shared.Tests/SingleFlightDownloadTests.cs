using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared.Audio;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class SingleFlightDownloadTests
{
    [Fact]
    public async Task ConcurrentCallers_StartOnlyOneSharedOperation()
    {
        var inFlight = new ConcurrentDictionary<string, Lazy<Task>>(StringComparer.OrdinalIgnoreCase);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        Task Start(CancellationToken _)
        {
            Interlocked.Increment(ref started);
            return release.Task;
        }

        var callers = new Task[50];
        for (var i = 0; i < callers.Length; i++)
        {
            callers[i] = SingleFlightDownload.RunAsync(inFlight, "asset", Start);
        }

        await WaitUntilAsync(() => Volatile.Read(ref started) == 1);
        release.SetResult();
        await Task.WhenAll(callers);

        Assert.Equal(1, Volatile.Read(ref started));
        await WaitUntilAsync(() => inFlight.IsEmpty);
    }

    [Fact]
    public async Task CancelingOneWaiter_DoesNotCancelSharedOperation()
    {
        var inFlight = new ConcurrentDictionary<string, Lazy<Task>>(StringComparer.OrdinalIgnoreCase);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        CancellationToken sharedToken = default;

        Task Start(CancellationToken token)
        {
            sharedToken = token;
            Interlocked.Increment(ref started);
            return release.Task;
        }

        using var callerCts = new CancellationTokenSource();
        var canceledWaiter = SingleFlightDownload.RunAsync(inFlight, "asset", Start, callerCts.Token);
        await WaitUntilAsync(() => Volatile.Read(ref started) == 1);

        var continuingWaiter = SingleFlightDownload.RunAsync(inFlight, "asset", Start);
        callerCts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() => canceledWaiter);
        Assert.False(sharedToken.CanBeCanceled);

        release.SetResult();
        await continuingWaiter;

        Assert.Equal(1, Volatile.Read(ref started));
        await WaitUntilAsync(() => inFlight.IsEmpty);
    }

    [Fact]
    public async Task FailedSharedOperation_IsRemovedSoRetryCanStart()
    {
        var inFlight = new ConcurrentDictionary<string, Lazy<Task>>(StringComparer.OrdinalIgnoreCase);
        var attempts = 0;

        Task Start(CancellationToken _)
        {
            return Interlocked.Increment(ref attempts) == 1
                ? Task.FromException(new InvalidOperationException("first failure"))
                : Task.CompletedTask;
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SingleFlightDownload.RunAsync(inFlight, "asset", Start));
        Assert.Equal("first failure", ex.Message);

        await WaitUntilAsync(() => inFlight.IsEmpty);
        await SingleFlightDownload.RunAsync(inFlight, "asset", Start);

        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task SynchronousFactoryFailure_IsRemovedSoRetryCanStart()
    {
        var inFlight = new ConcurrentDictionary<string, Lazy<Task>>(StringComparer.OrdinalIgnoreCase);
        var attempts = 0;

        Task Start(CancellationToken _)
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new InvalidOperationException("sync failure");
            }

            return Task.CompletedTask;
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SingleFlightDownload.RunAsync(inFlight, "asset", Start));
        Assert.Equal("sync failure", ex.Message);

        await WaitUntilAsync(() => inFlight.IsEmpty);
        await SingleFlightDownload.RunAsync(inFlight, "asset", Start);

        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
