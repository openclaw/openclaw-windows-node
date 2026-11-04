using OpenClaw.Connection.NativeGateway;

namespace OpenClaw.SetupEngine.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class NativeGatewayPackageAcquisitionTests
{
    private static readonly NativeGatewayPackage Package = new(
        "OpenClawFoundation.OpenClawGateway_123456789abcd", "1.0.0.0", @"qualified\openclaw.exe", @"qualified\clawctl.exe");

    [Theory]
    [InlineData("OpenClawFoundation.OpenClawGateway", true)]
    [InlineData("OpenClawFoundation.OpenClawGateway-patch", false)]
    [InlineData("OpenClaw.Gateway", false)]
    public async Task PresentPackage_RefreshesStoreAndPreservesDevelopmentPackages(string name, bool refresh)
    {
        var package = Package with { PackageFamilyName = name + "_test" };
        var updated = package with { Version = "2.0.0.0" };
        var installed = false;
        var waiting = false;
        var resolver = new Resolver(_ => Task.FromResult(installed ? updated : package));
        var actual = await NativeGatewayPackageAcquisition.EnsureAsync(resolver,
            _ => { installed = true; return Task.CompletedTask; },
            _ => throw new Xunit.Sdk.XunitException("Must not register an already registered package"),
            () => waiting = true);
        Assert.Same(refresh ? updated : package, actual);
        Assert.Equal(refresh, installed);
        Assert.Equal(refresh, waiting);
        Assert.Equal(refresh ? 2 : 1, resolver.Calls);
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
            ct =>
            {
                events.Add("register");
                return NotStaged(ct);
            },
            () => events.Add("waiting"),
            pollInterval: TimeSpan.FromMilliseconds(1));

        Assert.Same(Package, actual);
        Assert.Equal(["resolve", "install", "waiting", "resolve", "register", "resolve", "resolve"], events);
    }

    [Fact]
    public async Task StagedPackage_RegistersOnceThenContinuesWithoutWaiting()
    {
        var events = new List<string>();
        var registered = false;
        var resolver = new Resolver(_ =>
        {
            events.Add("resolve");
            return registered ? Task.FromResult(Package) : Missing();
        });

        var actual = await NativeGatewayPackageAcquisition.EnsureAsync(resolver,
            _ =>
            {
                events.Add("install");
                return Task.CompletedTask;
            },
            _ =>
            {
                events.Add("register");
                registered = true;
                return Task.FromResult(NativeGatewayStagedRegistrationResult.Registered);
            },
            () => events.Add("waiting"),
            timeout: TimeSpan.FromMinutes(5),
            pollInterval: TimeSpan.FromMinutes(1)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Same(Package, actual);
        Assert.Equal(["resolve", "install", "waiting", "resolve", "register", "resolve"], events);
    }

    [Fact]
    public async Task NotStaged_KeepsPollingUntilDeadline()
    {
        var resolver = new Resolver(_ => Missing());
        var registrations = 0;
        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            NativeGatewayPackageAcquisition.EnsureAsync(resolver, _ => Task.CompletedTask,
                ct =>
                {
                    registrations++;
                    return NotStaged(ct);
                },
                timeout: TimeSpan.FromMilliseconds(100), pollInterval: TimeSpan.FromMilliseconds(1)));

        Assert.Equal(1, registrations);
        Assert.True(resolver.Calls >= 3);
        Assert.Contains("still not registered for this Windows user", exception.Message);
        Assert.DoesNotContain("may still finish", exception.Message);
    }

    [Fact]
    public async Task NotStaged_ThenLateRegistration_Succeeds()
    {
        var calls = 0;
        var registrations = 0;
        var resolver = new Resolver(_ => ++calls < 5 ? Missing() : Task.FromResult(Package));

        var actual = await NativeGatewayPackageAcquisition.EnsureAsync(resolver, _ => Task.CompletedTask,
            ct =>
            {
                registrations++;
                return NotStaged(ct);
            },
            pollInterval: TimeSpan.FromMilliseconds(1));

        Assert.Same(Package, actual);
        Assert.Equal(1, registrations);
        Assert.Equal(5, resolver.Calls);
    }

    [Fact]
    public async Task RegistrationFailure_FailsFastWithSpecificError()
    {
        var failure = new NativeGatewayPackageRegistrationException(unchecked((int)0x80073CF6), 0, Guid.Empty);
        var resolver = new Resolver(_ => Missing());

        var actual = await Assert.ThrowsAsync<NativeGatewayPackageRegistrationException>(() =>
            NativeGatewayPackageAcquisition.EnsureAsync(resolver, _ => Task.CompletedTask,
                _ => Task.FromException<NativeGatewayStagedRegistrationResult>(failure),
                timeout: TimeSpan.FromMinutes(1), pollInterval: TimeSpan.FromMilliseconds(1))
                .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Same(failure, actual);
        // Pre-install, post-install, and one re-resolve in case Windows registered concurrently.
        Assert.Equal(3, resolver.Calls);
    }

    [Fact]
    public async Task RegistrationFailure_ButWindowsRegisteredConcurrently_Continues()
    {
        var calls = 0;
        var resolver = new Resolver(_ => ++calls <= 2 ? Missing() : Task.FromResult(Package));

        var actual = await NativeGatewayPackageAcquisition.EnsureAsync(resolver, _ => Task.CompletedTask,
            _ => Task.FromException<NativeGatewayStagedRegistrationResult>(
                new NativeGatewayPackageRegistrationException(unchecked((int)0x80073D02), 0, Guid.Empty)),
            pollInterval: TimeSpan.FromMilliseconds(1));

        Assert.Same(Package, actual);
    }

    [Fact]
    public async Task RegisteredPackage_FailingValidation_IsStillRejected()
    {
        var failure = new InvalidOperationException("missing qualified aliases");
        var registered = false;
        var resolver = new Resolver(_ => registered
            ? Task.FromException<NativeGatewayPackage>(failure)
            : Missing());

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NativeGatewayPackageAcquisition.EnsureAsync(resolver, _ => Task.CompletedTask,
                _ =>
                {
                    registered = true;
                    return Task.FromResult(NativeGatewayStagedRegistrationResult.Registered);
                }));

        Assert.Same(failure, actual);
        Assert.Equal(3, resolver.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrDeadline_ReachesRunningRegistration(bool timeout)
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new Resolver(_ => Missing());
        var acquisition = NativeGatewayPackageAcquisition.EnsureAsync(resolver, _ => Task.CompletedTask,
            async ct =>
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
                return NativeGatewayStagedRegistrationResult.Registered;
            },
            cancellationToken: cancellation.Token,
            timeout: timeout ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromMinutes(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!timeout)
            cancellation.Cancel();

        if (timeout)
        {
            var exception = await Assert.ThrowsAsync<TimeoutException>(() => acquisition);
            Assert.Contains("still not registered for this Windows user", exception.Message);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition);
        }
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, resolver.Calls);
    }

    [Theory]
    [InlineData(0x80073CF9, 0x80070490, true)]
    [InlineData(0x80073CF1, 0, true)]
    [InlineData(0x80073CF9, 0x80073CF1, true)]
    [InlineData(0x80073CF9, 0x80073CF6, false)]
    [InlineData(0x80073D02, 0, false)]
    [InlineData(0x80073CF9, 0, false)]
    public void IsNotStaged_ClassifiesWindowsDeploymentCodes(uint errorCode, uint extendedErrorCode, bool expected) =>
        Assert.Equal(expected, NativeGatewayPackageRegistration.IsNotStaged(
            unchecked((int)errorCode), unchecked((int)extendedErrorCode)));

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
            }, NotStaged));

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
            NativeGatewayPackageAcquisition.EnsureAsync(resolver, _ => throw failure, NotStaged,
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
                _ => throw new InvalidOperationException("Must not install"), NotStaged,
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
        }, NotStaged, cancellationToken: cancellation.Token, pollInterval: TimeSpan.FromMinutes(1));
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
                _ => Task.FromCanceled(new CancellationToken(true)), NotStaged,
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
        }, NotStaged, () => throw new Xunit.Sdk.XunitException("Must not verify a cancelled installation"),
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
        }, NotStaged, cancellationToken: cancellation.Token,
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
                _ => throw new InvalidOperationException("Must not install"), NotStaged));
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
            }, NotStaged, timeout: TimeSpan.FromMilliseconds(100), pollInterval: TimeSpan.FromMilliseconds(1)));

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
            }, NotStaged, timeout: TimeSpan.FromMilliseconds(100)));
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
            NativeGatewayPackageAcquisition.EnsureAsync(resolver, _ => Task.CompletedTask, NotStaged,
                timeout: TimeSpan.FromMilliseconds(timeout), pollInterval: TimeSpan.FromMilliseconds(interval)));
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public void Timeout_DefaultsTo30Minutes()
    {
        var previous = Environment.GetEnvironmentVariable(NativeGatewayPackageAcquisition.TimeoutEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(NativeGatewayPackageAcquisition.TimeoutEnvironmentVariable, null);
            Assert.Equal(TimeSpan.FromMinutes(30), NativeGatewayPackageAcquisition.ResolveTimeout());
        }
        finally
        {
            Environment.SetEnvironmentVariable(NativeGatewayPackageAcquisition.TimeoutEnvironmentVariable, previous);
        }
    }

    [Theory]
    [InlineData(" 90 ", 90)]
    [InlineData("0", null)]
    [InlineData("not-a-number", null)]
    [InlineData("2147484", null)]
    public void Timeout_EnvironmentOverride_IsUsedOnlyForValidValues(string value, int? expectedSeconds)
    {
        var previous = Environment.GetEnvironmentVariable(NativeGatewayPackageAcquisition.TimeoutEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(NativeGatewayPackageAcquisition.TimeoutEnvironmentVariable, value);
            Assert.Equal(expectedSeconds is { } seconds
                ? TimeSpan.FromSeconds(seconds)
                : NativeGatewayPackageAcquisition.DefaultTimeout,
                NativeGatewayPackageAcquisition.ResolveTimeout());
        }
        finally
        {
            Environment.SetEnvironmentVariable(NativeGatewayPackageAcquisition.TimeoutEnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task EnsureAsync_ExplicitTimeoutStillWinsOverEnvironment()
    {
        var previous = Environment.GetEnvironmentVariable(NativeGatewayPackageAcquisition.TimeoutEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(NativeGatewayPackageAcquisition.TimeoutEnvironmentVariable, "300");
            var resolver = new Resolver(_ => Missing());
            // Bound the wait well below the environment's 300 s so an env-wins regression fails
            // fast: WaitAsync's own TimeoutException lacks the acquisition retry guidance below.
            var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
                NativeGatewayPackageAcquisition.EnsureAsync(resolver, _ => Task.CompletedTask, NotStaged,
                    timeout: TimeSpan.FromMilliseconds(50), pollInterval: TimeSpan.FromMilliseconds(1))
                    .WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("Check WinGet and Microsoft Store access", exception.Message);
            Assert.Contains("retry native setup", exception.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(NativeGatewayPackageAcquisition.TimeoutEnvironmentVariable, previous);
        }
    }

    private static Task<NativeGatewayPackage> Missing() =>
        Task.FromException<NativeGatewayPackage>(new NativeGatewayPackageNotInstalledException());

    private static Task<NativeGatewayStagedRegistrationResult> NotStaged(CancellationToken _) =>
        Task.FromResult(NativeGatewayStagedRegistrationResult.NotStaged);

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
