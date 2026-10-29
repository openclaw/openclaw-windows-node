using System.Diagnostics;
using OpenClaw.GatewayFixtureHost;

namespace OpenClaw.Tray.IntegrationTests;

public sealed class GatewayFixtureRunTests
{
    private const string Description = "history loaded for agent:main:fixture-long";
    private const string ArtifactsDirectory = @"C:\fixture-artifacts\test-run";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupFailure_PreservesTheOriginalAndStillCleansUpAfterReportFailure(bool reportFails)
    {
        var startupFailure = new InvalidOperationException("synthetic startup failure");
        var reportFailure = new IOException("synthetic report failure");
        var cleanupFailure = new AggregateException(new System.ComponentModel.Win32Exception(5));
        var cleanupAttempted = false;

        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            GatewayFixtureRun.HandleStartupFailureAsync(startupFailure,
                () => reportFails ? Task.FromException(reportFailure) : Task.CompletedTask,
                () =>
                {
                    cleanupAttempted = true;
                    return Task.FromException(cleanupFailure);
                }));

        Assert.True(cleanupAttempted);
        Assert.Same(startupFailure, error.InnerExceptions[0]);
        Assert.Same(cleanupFailure, error.InnerExceptions[^1]);
        Assert.Equal(reportFails ? 3 : 2, error.InnerExceptions.Count);
        if (reportFails)
            Assert.Same(reportFailure, error.InnerExceptions[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOwnedProcesses_AttemptsAndDisposesEveryProcessAfterAFailure(bool aggregate)
    {
        using var first = Process.GetCurrentProcess();
        using var second = Process.GetCurrentProcess();
        var handles = new[] { first.SafeHandle, second.SafeHandle };
        var attempted = new List<Process>();
        Exception original = aggregate
            ? new AggregateException(new System.ComponentModel.Win32Exception(5))
            : new TimeoutException("synthetic process exit timeout");

        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            GatewayFixtureRun.StopOwnedProcessesAsync([first, second], process =>
            {
                attempted.Add(process);
                return ReferenceEquals(process, first) ? Task.FromException(original) : Task.CompletedTask;
            }));

        Assert.Equal([first, second], attempted);
        Assert.All(handles, handle => Assert.True(handle.IsClosed));
        Assert.Same(original, Assert.Single(error.InnerExceptions).InnerException);
    }

    [Fact]
    public async Task WaitForAsync_SlowConditionPreservesTimeoutContext()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(() =>
                WaitAsync(() => pending.Task, timeout: TimeSpan.FromSeconds(1)));

            AssertTimeoutContext(error);
            Assert.IsType<TimeoutException>(error.InnerException);
        }
        finally
        {
            pending.TrySetResult(false);
        }
    }

    [Fact]
    public async Task WaitForAsync_ConditionTimeoutPreservesOriginalExceptionAndContext()
    {
        var original = new TimeoutException("Synthetic MCP timeout.");
        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            WaitAsync(() => Task.FromException<bool>(original)));

        AssertTimeoutContext(error);
        Assert.Same(original, error.InnerException);
    }

    [Fact]
    public async Task WaitForAsync_ExpiredDeadlinePreservesTimeoutContext()
    {
        var invoked = false;
        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            WaitAsync(() =>
            {
                invoked = true;
                return Task.FromResult(true);
            }, timeout: TimeSpan.Zero));

        AssertTimeoutContext(error);
        Assert.False(invoked);
    }

    [Fact]
    public async Task WaitForAsync_RetriesFalseConditionAndChecksAppBeforeEachProbe()
    {
        var probes = 0;
        var checks = 0;
        await WaitAsync(() =>
        {
            Assert.Equal(probes + 1, checks);
            return Task.FromResult(++probes == 2);
        }, ensureRunning: () => checks++);

        Assert.Equal(2, probes);
        Assert.Equal(2, checks);
    }

    [Fact]
    public async Task WaitForAsync_AppFailurePropagatesBeforeCondition()
    {
        var original = new InvalidOperationException("Synthetic app exit.");
        var invoked = false;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitAsync(() =>
            {
                invoked = true;
                return Task.FromResult(true);
            }, ensureRunning: () => throw original));

        Assert.Same(original, error);
        Assert.False(invoked);
    }

    [Fact]
    public async Task WaitForAsync_ConditionFailureIsNotConvertedToTimeout()
    {
        var original = new InvalidDataException("Synthetic invalid MCP response.");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            WaitAsync(() => Task.FromException<bool>(original)));

        Assert.Same(original, error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WaitForAsync_CancellationIsNotConvertedToTimeout(bool cancelBeforeProbe)
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = false;
        if (cancelBeforeProbe)
            cancellation.Cancel();
        try
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                WaitAsync(() =>
                {
                    invoked = true;
                    cancellation.Cancel();
                    return pending.Task;
                }, cancellationToken: cancellation.Token));

            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Equal(!cancelBeforeProbe, invoked);
        }
        finally
        {
            pending.TrySetResult(false);
        }
    }

    private static Task WaitAsync(
        Func<Task<bool>> condition,
        TimeSpan? timeout = null,
        Action? ensureRunning = null,
        CancellationToken cancellationToken = default) =>
        GatewayFixtureRun.WaitForConditionAsync(
            condition, Description, ArtifactsDirectory, ensureRunning ?? (() => { }),
            timeout ?? Deadline, cancellationToken).WaitAsync(Deadline);

    private static void AssertTimeoutContext(TimeoutException error) =>
        Assert.Equal($"Timed out waiting for {Description}. Artifacts: {ArtifactsDirectory}", error.Message);
}
