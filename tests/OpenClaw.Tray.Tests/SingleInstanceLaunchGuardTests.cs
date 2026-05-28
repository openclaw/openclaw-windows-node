using OpenClawTray.Services;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.Tray.Tests;

public sealed class SingleInstanceLaunchGuardTests
{
    [Fact]
    public void BuildMutexName_UsesStableDataDirSuffix()
    {
        var first = SingleInstanceLaunchGuard.BuildMutexName(@"C:\temp\openclaw-test");
        var second = SingleInstanceLaunchGuard.BuildMutexName(@"C:\temp\openclaw-test");

        Assert.Equal(first, second);
        Assert.StartsWith($"{SingleInstanceLaunchGuard.DefaultMutexName}-", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Acquire_ReturnsAcquired_WhenMutexIsFree()
    {
        var result = AcquireUnique(retryWhenBusy: false);

        try
        {
            Assert.Equal(SingleInstanceLaunchGuard.AcquisitionStatus.Acquired, result.Status);
            Assert.NotNull(result.Mutex);
            Assert.Equal(0, result.Attempts);
        }
        finally
        {
            Release(result);
        }
    }

    [Fact]
    public void Acquire_ReturnsAlreadyRunning_AndDoesNotKeepMutexAlive_WhenRetryIsDisabled()
    {
        var name = UniqueMutexName();
        var owner = new Mutex(true, name, out var createdNew);
        Assert.True(createdNew);

        var busy = SingleInstanceLaunchGuard.Acquire(
            name,
            retryWhenBusy: false,
            retryTimeout: TimeSpan.Zero,
            retryDelay: TimeSpan.FromMilliseconds(1));

        Assert.Equal(SingleInstanceLaunchGuard.AcquisitionStatus.AlreadyRunning, busy.Status);
        Assert.Null(busy.Mutex);

        owner.ReleaseMutex();
        owner.Dispose();
        var afterRelease = SingleInstanceLaunchGuard.Acquire(
            name,
            retryWhenBusy: false,
            retryTimeout: TimeSpan.Zero,
            retryDelay: TimeSpan.FromMilliseconds(1));

        try
        {
            Assert.Equal(SingleInstanceLaunchGuard.AcquisitionStatus.Acquired, afterRelease.Status);
            Assert.NotNull(afterRelease.Mutex);
        }
        finally
        {
            Release(afterRelease);
        }
    }

    [Fact]
    public async Task Acquire_RetriesUntilMutexOwnerExits()
    {
        var name = UniqueMutexName();
        using var ownerReady = new ManualResetEventSlim(false);
        var owner = Task.Run(() =>
        {
            using var mutex = new Mutex(true, name, out var createdNew);
            Assert.True(createdNew);
            ownerReady.Set();
            Thread.Sleep(75);
            mutex.ReleaseMutex();
        });

        Assert.True(ownerReady.Wait(TimeSpan.FromSeconds(1)));

        var result = SingleInstanceLaunchGuard.Acquire(
            name,
            retryWhenBusy: true,
            retryTimeout: TimeSpan.FromSeconds(2),
            retryDelay: TimeSpan.FromMilliseconds(10));

        try
        {
            Assert.Equal(SingleInstanceLaunchGuard.AcquisitionStatus.AcquiredAfterWait, result.Status);
            Assert.NotNull(result.Mutex);
            Assert.True(result.Attempts > 0);
        }
        finally
        {
            Release(result);
            await owner;
        }
    }

    [Fact]
    public void Acquire_TimesOut_WhenMutexRemainsBusy()
    {
        var name = UniqueMutexName();
        using var owner = new Mutex(true, name, out var createdNew);
        Assert.True(createdNew);

        var result = SingleInstanceLaunchGuard.Acquire(
            name,
            retryWhenBusy: true,
            retryTimeout: TimeSpan.FromMilliseconds(50),
            retryDelay: TimeSpan.FromMilliseconds(5));

        Assert.Equal(SingleInstanceLaunchGuard.AcquisitionStatus.TimedOut, result.Status);
        Assert.Null(result.Mutex);
        Assert.True(result.Attempts > 0);
        owner.ReleaseMutex();
    }

    private static SingleInstanceLaunchGuard.AcquisitionResult AcquireUnique(bool retryWhenBusy)
        => SingleInstanceLaunchGuard.Acquire(
            UniqueMutexName(),
            retryWhenBusy,
            retryTimeout: TimeSpan.FromMilliseconds(50),
            retryDelay: TimeSpan.FromMilliseconds(5));

    private static string UniqueMutexName()
        => $"{SingleInstanceLaunchGuard.DefaultMutexName}.Tests.{Guid.NewGuid():N}";

    private static void Release(SingleInstanceLaunchGuard.AcquisitionResult result)
    {
        if (result.Mutex is null)
            return;

        result.Mutex.ReleaseMutex();
        result.Mutex.Dispose();
    }
}
