using OpenClaw.Connection.NativeGateway;

namespace OpenClaw.SetupEngine.Tests;

public sealed class NativeGatewayPackageAcquisitionTests
{
    private static readonly NativeGatewayPackage Package = new(
        "OpenClawFoundation.OpenClawGateway_123456789abcd", "1.0.0.0", @"qualified\openclaw.exe", @"qualified\clawctl.exe");

    [Fact]
    public async Task PresentPackage_DoesNotOpenInstallerOrReportWaiting()
    {
        var resolver = new Resolver(_ => Task.FromResult(Package));
        var actual = await NativeGatewayPackageAcquisition.EnsureAsync(resolver,
            _ => throw new InvalidOperationException("Must not install"),
            () => throw new InvalidOperationException("Must not wait"));
        Assert.Same(Package, actual);
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task MissingPackage_OpensOnceAndWaitsUntilResolverValidatesRegistration()
    {
        var events = new List<string>();
        var calls = 0;
        var resolver = new Resolver(_ =>
        {
            events.Add("resolve");
            return ++calls < 4 ? Missing() : Task.FromResult(Package);
        });

        var actual = await NativeGatewayPackageAcquisition.EnsureAsync(resolver,
            _ =>
            {
                events.Add("install");
                return Task.CompletedTask;
            },
            () => events.Add("waiting"),
            pollInterval: TimeSpan.FromMilliseconds(1));

        Assert.Same(Package, actual);
        Assert.Equal(["resolve", "install", "waiting", "resolve", "resolve", "resolve"], events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidationError_PropagatesWithoutInstallOrFurtherPolling(bool afterInstall)
    {
        var failure = new InvalidOperationException("Repair, duplicate registration, or missing qualified aliases");
        var calls = 0;
        var installerCalls = 0;
        var resolver = new Resolver(_ => afterInstall && ++calls == 1
            ? Missing()
            : Task.FromException<NativeGatewayPackage>(failure));

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NativeGatewayPackageAcquisition.EnsureAsync(resolver, _ =>
            {
                installerCalls++;
                return Task.CompletedTask;
            }));

        Assert.Same(failure, actual);
        Assert.Equal(afterInstall ? 1 : 0, installerCalls);
        Assert.Equal(afterInstall ? 2 : 1, resolver.Calls);
    }

    [Fact]
    public async Task InstallationFailure_StopsImmediately()
    {
        var resolver = new Resolver(_ => Missing());
        var failure = new InvalidOperationException("WinGet installation failed");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NativeGatewayPackageAcquisition.EnsureAsync(resolver, _ => throw failure,
                () => throw new InvalidOperationException("Must not wait")));
        Assert.Same(failure, actual);
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task CancelledBeforeStart_DoesNotResolveOrOpenInstaller()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var resolver = new Resolver(_ => throw new InvalidOperationException("Must not resolve"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativeGatewayPackageAcquisition.EnsureAsync(resolver,
                _ => throw new InvalidOperationException("Must not install"),
                cancellationToken: cancellation.Token));
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task CancelledDuringPolling_StopsWithoutReopeningInstaller()
    {
        using var cancellation = new CancellationTokenSource();
        var polling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var installerCalls = 0;
        var resolver = new Resolver(_ =>
        {
            if (++calls == 2)
                polling.SetResult();
            return Missing();
        });
        var acquisition = NativeGatewayPackageAcquisition.EnsureAsync(resolver, _ =>
        {
            installerCalls++;
            return Task.CompletedTask;
        }, cancellationToken: cancellation.Token, pollInterval: TimeSpan.FromMinutes(1));
        await polling.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition);
        Assert.Equal(1, installerCalls);
        Assert.Equal(2, resolver.Calls);
    }

    [Fact]
    public async Task InstallerCancellation_PropagatesWithoutWaitingOrPolling()
    {
        var resolver = new Resolver(_ => Missing());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativeGatewayPackageAcquisition.EnsureAsync(resolver,
                _ => Task.FromCanceled(new CancellationToken(true)),
                () => throw new InvalidOperationException("Must not wait")));
        Assert.Equal(1, resolver.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrDeadline_ReachesRunningInstaller(bool timeout)
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new Resolver(_ => Missing());
        var acquisition = NativeGatewayPackageAcquisition.EnsureAsync(resolver, async ct =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            finally
            {
                if (ct.IsCancellationRequested)
                    cancelled.TrySetResult();
            }
        }, () => throw new Xunit.Sdk.XunitException("Must not verify a cancelled installation"),
            cancellationToken: cancellation.Token,
            timeout: timeout ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromMinutes(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!timeout)
            cancellation.Cancel();

        if (timeout)
            await Assert.ThrowsAsync<TimeoutException>(() => acquisition);
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, resolver.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrDeadline_AwaitsInstallerCleanup(bool timeout)
    {
        using var cancellation = new CancellationTokenSource();
        var cleaningUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new Resolver(_ => Missing());
        var acquisition = NativeGatewayPackageAcquisition.EnsureAsync(resolver, async ct =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            finally
            {
                cleaningUp.SetResult();
                await allowCleanup.Task;
            }
        }, cancellationToken: cancellation.Token,
            timeout: timeout ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromMinutes(1));
        if (!timeout)
            cancellation.Cancel();
        try
        {
            await cleaningUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(acquisition.IsCompleted);
        }
        finally
        {
            allowCleanup.SetResult();
        }
        if (timeout)
            await Assert.ThrowsAsync<TimeoutException>(() => acquisition);
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition);
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task ResolverCancellation_IsNotAnInstallRequest()
    {
        var resolver = new Resolver(_ => Task.FromCanceled<NativeGatewayPackage>(new CancellationToken(true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativeGatewayPackageAcquisition.EnsureAsync(resolver,
                _ => throw new InvalidOperationException("Must not install")));
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task Timeout_OpensOnceAndProvidesRetryGuidance()
    {
        var resolver = new Resolver(_ => Missing());
        var installerCalls = 0;
        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            NativeGatewayPackageAcquisition.EnsureAsync(resolver, _ =>
            {
                installerCalls++;
                return Task.CompletedTask;
            }, timeout: TimeSpan.FromMilliseconds(100), pollInterval: TimeSpan.FromMilliseconds(1)));

        Assert.Equal(1, installerCalls);
        Assert.True(resolver.Calls >= 2);
        Assert.Contains("Check WinGet and Microsoft Store access", exception.Message);
        Assert.Contains("retry native setup", exception.Message);
    }

    [Fact]
    public async Task Timeout_BoundsUnresponsiveResolver()
    {
        var pending = new TaskCompletionSource<NativeGatewayPackage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new Resolver(_ => pending.Task);
        var installerCalls = 0;
        await Assert.ThrowsAsync<TimeoutException>(() =>
            NativeGatewayPackageAcquisition.EnsureAsync(resolver, _ =>
            {
                installerCalls++;
                return pending.Task;
            }, timeout: TimeSpan.FromMilliseconds(100)));
        Assert.Equal(0, installerCalls);
        Assert.Equal(1, resolver.Calls);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public async Task NonpositiveTiming_IsRejectedBeforeResolution(int timeout, int interval)
    {
        var resolver = new Resolver(_ => throw new InvalidOperationException("Must not resolve"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            NativeGatewayPackageAcquisition.EnsureAsync(resolver, _ => Task.CompletedTask,
                timeout: TimeSpan.FromMilliseconds(timeout), pollInterval: TimeSpan.FromMilliseconds(interval)));
        Assert.Equal(0, resolver.Calls);
    }

    private static Task<NativeGatewayPackage> Missing() =>
        Task.FromException<NativeGatewayPackage>(new NativeGatewayPackageNotInstalledException());

    private sealed class Resolver(Func<CancellationToken, Task<NativeGatewayPackage>> resolve) : INativeGatewayPackageResolver
    {
        public int Calls { get; private set; }

        public Task<NativeGatewayPackage> ResolveAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return resolve(cancellationToken);
        }
    }
}
