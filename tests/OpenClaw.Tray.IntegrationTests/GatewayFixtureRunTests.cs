using System.Diagnostics;
using OpenClaw.GatewayFixtureHost;

namespace OpenClaw.Tray.IntegrationTests;

public sealed class GatewayFixtureRunTests
{
    private const string Description = "history loaded for agent:main:fixture-long";
    private const string ArtifactsDirectory = @"C:\fixture-artifacts\test-run";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public void BrowserCleanup_RetainsNestedOwnedChildrenButRejectsForeignAndStaleParents()
    {
        var started = DateTime.UnixEpoch.AddDays(1);
        var app = new GatewayFixtureRun.BrowserProcessIdentity(10, 0, started);
        var descendants = GatewayFixtureRun.SelectOwnedBrowserDescendants(app,
        [
            new(30, 20, started.AddSeconds(2)),
            new(20, 10, started.AddSeconds(1)),
            new(40, 999, started.AddSeconds(1)),
            new(50, 40, started.AddSeconds(2)),
            new(60, 20, started),
            new(70, 60, started.AddSeconds(3)),
            new(80, 20, started.AddMinutes(2)),
        ], started.AddMinutes(1));
        Assert.Equal([20, 30], descendants);
    }

    [Fact]
    public async Task Teardown_PreservesProcessAndProfileFailuresAndAttemptsEveryOwnerInOrder()
    {
        var processFailure = new InvalidOperationException("Synthetic owned browser remains live.");
        var profileFailure = new IOException("Synthetic profile file remains locked.");
        var steps = new List<string>();
        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            GatewayFixtureRun.DisposeOwnedResourcesAsync(
                () => { steps.Add("process"); return Task.FromException(processFailure); },
                () => { steps.Add("gateway"); return Task.CompletedTask; },
                () => { steps.Add("profile"); throw profileFailure; }));
        Assert.Equal(["process", "gateway", "profile"], steps);
        Assert.Equal([processFailure, profileFailure], error.InnerExceptions);
    }

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

    private static readonly TimeSpan FailurePhase = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FailureDeadline = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task SynchronouslyBlockedCapture_ReachesStop_CancelsLateWrite_AndJoinsBeforeDisposal()
    {
        using var unblock = new ManualResetEventSlim();
        var entered = Signal();
        var unwound = Signal();
        var original = new InvalidOperationException("Original UI assertion");
        var released = false;
        var disposed = false;
        var wroteLateImage = false;
        IReadOnlyList<string> receipt = [];
        // Also bound a regression that calls capture synchronously before Finish returns.
        var completion = Task.Run(() => Finish(original, capture: token =>
        {
            entered.TrySetResult();
            try
            {
                unblock.Wait(); // Deliberately synchronous, before returning a Task.
                token.ThrowIfCancellationRequested();
                wroteLateImage = true;
                return Task.CompletedTask;
            }
            finally { unwound.TrySetResult(); }
        }, release: () => released = true, stop: async () =>
        {
            await entered.Task.WaitAsync(FailureDeadline);
            Assert.True(released);
            unblock.Set();
        }, dispose: () =>
        {
            Assert.True(unwound.Task.IsCompleted);
            disposed = true;
            return Task.CompletedTask;
        }, record: stages => receipt = stages));
        try
        {
            var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => completion.WaitAsync(FailureDeadline));
            Assert.Same(original, observed);
            Assert.True(disposed);
            Assert.False(wroteLateImage);
            Assert.Contains("capture:timeout", receipt);
            Assert.Contains("drain:completed", receipt);
            Assert.Contains("dispose:completed", receipt);
        }
        finally
        {
            unblock.Set();
            await unwound.Task.WaitAsync(FailureDeadline);
            try { await completion.WaitAsync(FailureDeadline); }
            catch (Exception) { /* Observe completion after releasing the deliberately blocked delegate. */ }
        }
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("release")]
    [InlineData("dispose")]
    [InlineData("receipt")]
    public async Task SecondaryFailure_PreservesOriginalAssertion_AndStillStopsAndDisposes(string failingStage)
    {
        var original = new InvalidOperationException("Original UI assertion");
        var stopped = false;
        var disposed = false;
        Task Capture(CancellationToken _) => failingStage == "capture"
            ? throw new IOException("Capture failed before Task return") : Task.CompletedTask;
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => Finish(original, capture: Capture,
            release: () => { if (failingStage == "release") throw new IOException("Release failed"); },
            stop: () => { stopped = true; return Task.CompletedTask; },
            dispose: () =>
            {
                disposed = true;
                if (failingStage == "dispose") throw new IOException("Cleanup failed");
                return Task.CompletedTask;
            }, record: _ => { if (failingStage == "receipt") throw new IOException("Receipt failed"); }).WaitAsync(FailureDeadline));
        Assert.Same(original, observed);
        Assert.True(stopped);
        Assert.True(disposed);
        Assert.Contains(failingStage + ":error:IOException", Assert.IsType<string>(original.Data["GatewayFixtureFailureStages"]));
    }

    [Fact]
    public async Task RunningBody_SkipsConcurrentCapture_AndJoinsBodyBeforeDisposal()
    {
        var original = new TimeoutException("Body deadline");
        var unblock = Signal();
        var disposed = false;
        var bodyWrote = false;
        var body = Task.Run(async () =>
        {
            await unblock.Task;
            Assert.False(disposed);
            bodyWrote = true;
        });
        IReadOnlyList<string> receipt = [];
        try
        {
            var observed = await Assert.ThrowsAsync<TimeoutException>(() => Finish(original, body: body,
                capture: _ => throw new InvalidOperationException("Must not enter concurrent UIA"),
                stop: () => { unblock.TrySetResult(); return Task.CompletedTask; },
                dispose: () =>
                {
                    Assert.True(body.IsCompleted);
                    Assert.True(bodyWrote);
                    disposed = true;
                    return Task.CompletedTask;
                }, record: stages => receipt = stages).WaitAsync(FailureDeadline));
            Assert.Same(original, observed);
            Assert.True(disposed);
            Assert.Contains("capture:skipped-running-body", receipt);
        }
        finally { unblock.TrySetResult(); await body.WaitAsync(FailureDeadline); }
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("body")]
    [InlineData("dispose")]
    public async Task WorkerThatCannotUnwind_TerminatesFixtureWorkerInsteadOfStartingAnotherRun(string blockedStage)
    {
        var original = new InvalidOperationException("Original UI assertion");
        var unblock = Signal();
        var exited = Signal();
        var stopped = false;
        var disposed = false;
        IReadOnlyList<string> receipt = [];
        async Task Blocked()
        {
            try { await unblock.Task; }
            finally { exited.TrySetResult(); }
        }
        var body = blockedStage == "body" ? Blocked() : Task.CompletedTask;
        try
        {
            await Assert.ThrowsAsync<WorkerTerminated>(() => Finish(original, body: body,
                capture: _ => blockedStage == "capture" ? Blocked() : Task.CompletedTask,
                stop: () => { stopped = true; return Task.CompletedTask; },
                dispose: () =>
                {
                    disposed = true;
                    return blockedStage == "dispose" ? Blocked() : Task.CompletedTask;
                }, record: stages => receipt = stages,
                terminate: () => throw new WorkerTerminated()).WaitAsync(FailureDeadline));
            Assert.True(stopped);
            Assert.Equal(blockedStage == "dispose", disposed);
            Assert.Contains(blockedStage == "dispose" ? "dispose:timeout" : "drain:timeout", receipt);
        }
        finally { unblock.TrySetResult(); await exited.Task.WaitAsync(FailureDeadline); }
    }

    [Fact]
    public async Task StopFailure_DoesNotDisposeProfileUnderAnUnconfirmedLiveApp()
    {
        var original = new InvalidOperationException("Original UI assertion");
        var disposed = false;
        IReadOnlyList<string> receipt = [];
        await Assert.ThrowsAsync<WorkerTerminated>(() => Finish(original,
            stop: () => throw new IOException("Owned termination failed"),
            dispose: () => { disposed = true; return Task.CompletedTask; },
            record: stages => receipt = stages,
            terminate: () => throw new WorkerTerminated()).WaitAsync(FailureDeadline));
        Assert.False(disposed);
        Assert.Contains("stop:error:IOException", receipt);
        Assert.Contains("dispose:withheld-live-worker-or-app", receipt);
    }

    private static Task Finish(Exception original, Task? body = null,
        Func<CancellationToken, Task>? capture = null, Action? release = null,
        Func<Task>? stop = null, Func<Task>? dispose = null,
        Action<IReadOnlyList<string>>? record = null, Action? terminate = null) =>
        GatewayFixtureUiFailure.FinishAsync(original, body ?? Task.CompletedTask,
            capture ?? (_ => Task.CompletedTask), release ?? (() => { }),
            stop ?? (() => Task.CompletedTask), dispose ?? (() => Task.CompletedTask),
            stages => { record?.Invoke(stages); return Task.CompletedTask; },
            terminate ?? (() => throw new InvalidOperationException("Unexpected worker termination")), FailurePhase);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class WorkerTerminated : Exception { }

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
