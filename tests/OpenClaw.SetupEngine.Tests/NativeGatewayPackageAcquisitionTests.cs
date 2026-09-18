using OpenClaw.Connection.NativeGateway;

namespace OpenClaw.SetupEngine.Tests;

public sealed class NativeGatewayPackageAcquisitionTests
{
    private static readonly NativeGatewayPackage Package = new(
        "OpenClaw.Gateway_123456789abcd", "1.0.0.0", @"qualified\openclaw.exe", @"qualified\clawctl.exe");

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
    public async Task MissingInstallerSource_StopsImmediately()
    {
        var resolver = new Resolver(_ => Missing());
        var failure = new FileNotFoundException("Installer source missing");
        var actual = await Assert.ThrowsAsync<FileNotFoundException>(() =>
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
        Assert.Contains("Complete or cancel Windows App Installer", exception.Message);
        Assert.Contains("retry native setup", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_BoundsUnresponsiveResolverAndInstaller(bool duringInstall)
    {
        var pending = new TaskCompletionSource<NativeGatewayPackage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new Resolver(_ => duringInstall ? Missing() : pending.Task);
        var installerCalls = 0;
        await Assert.ThrowsAsync<TimeoutException>(() =>
            NativeGatewayPackageAcquisition.EnsureAsync(resolver, _ =>
            {
                installerCalls++;
                return pending.Task;
            }, timeout: TimeSpan.FromMilliseconds(100)));
        Assert.Equal(duringInstall ? 1 : 0, installerCalls);
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
