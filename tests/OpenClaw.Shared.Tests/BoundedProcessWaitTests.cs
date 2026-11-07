using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using OpenClaw.Shared.Audio;

namespace OpenClaw.Shared.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BoundedProcessWaitCollection
{
    public const string Name = "Bounded process wait";
}

// Real-process deadlines must not compete with the suite's thread-pool-heavy tests.
[Collection(BoundedProcessWaitCollection.Name)]
public sealed class BoundedProcessWaitTests
{
    [Fact]
    public async Task WaitAsync_ReturnsCompleteOutput_WhenProcessSucceeds()
    {
        var process = StartFixture("success");

        var result = await BoundedProcessWait.WaitAsync(process, TimeSpan.FromSeconds(5));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("stdout-first-stdout-last", result.StandardOutput);
        Assert.Equal("stderr-first-stderr-last", result.StandardError);
        Assert.True(process.Disposal.IsCompleted);
    }

    [Fact]
    public async Task WaitAsync_TimesOutAndKillsProcess()
    {
        var process = StartFixture("hold", "30000");
        using var observer = ObserveProcess(process);
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(
            () => BoundedProcessWait.WaitAsync(process, TimeSpan.FromMilliseconds(200)));

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"Timeout cleanup took {stopwatch.ElapsedMilliseconds} ms.");
        await AssertExitsEventuallyAsync(observer);
    }

    [Fact]
    public async Task WaitAsync_CancellationKillsProcessWithoutUsingTimeoutBudget()
    {
        var process = StartFixture("hold", "30000");
        using var observer = ObserveProcess(process);
        using var cancellation = new CancellationTokenSource();
        var wait = BoundedProcessWait.WaitAsync(
            process,
            BoundedProcessWait.DefaultTimeout,
            cancellation.Token);
        await Task.Delay(100);
        var stopwatch = Stopwatch.StartNew();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"Cancellation cleanup took {stopwatch.ElapsedMilliseconds} ms.");
        await AssertExitsEventuallyAsync(observer);
    }

    [Fact]
    public async Task WaitAsync_AlreadyCanceledTokenStillKillsStartedProcess()
    {
        var process = StartFixture("hold", "30000");
        using var observer = ObserveProcess(process);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BoundedProcessWait.WaitAsync(
                process,
                BoundedProcessWait.DefaultTimeout,
                cancellation.Token));

        await AssertExitsEventuallyAsync(observer);
    }

    [Fact]
    public async Task WaitAsync_PreservesOutputWrittenLateWithinDeadline()
    {
        var process = StartFixture("late-output", "250");

        var result = await BoundedProcessWait.WaitAsync(process, TimeSpan.FromSeconds(3));

        Assert.Equal("late-stdout", result.StandardOutput);
        Assert.Equal("late-stderr", result.StandardError);
    }

    [Fact]
    public async Task WaitAsync_CancellationDoesNotWaitForInheritedPipeHandles()
    {
        var pidFile = Path.GetTempFileName();
        var childPid = 0;
        try
        {
            var process = StartFixture("inherit-handles", "30000", pidFile);
            using var observer = ObserveProcess(process);
            using var cancellation = new CancellationTokenSource();
            var wait = BoundedProcessWait.WaitAsync(
                process,
                BoundedProcessWait.DefaultTimeout,
                cancellation.Token);
            await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(wait.IsCompleted);
            childPid = await ReadChildPidAsync(pidFile);
            var stopwatch = Stopwatch.StartNew();

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);

            stopwatch.Stop();
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(3),
                $"Inherited-handle cancellation took {stopwatch.ElapsedMilliseconds} ms.");
        }
        finally
        {
            KillProcessTree(childPid);
            File.Delete(pidFile);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitAsync_DelayedKillOwnsProcessAfterBoundedReturn(bool cancel)
    {
        var process = StartFixture("hold", "30000");
        using var observer = ObserveProcess(process);
        using var cancellation = new CancellationTokenSource();
        var scheduler = new HeldTaskScheduler();
        var wait = BoundedProcessWait.WaitAsync(
            process,
            cancel ? BoundedProcessWait.DefaultTimeout : TimeSpan.FromMilliseconds(200),
            cancellation.Token,
            scheduler);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            if (cancel)
                cancellation.Cancel();

            await scheduler.Queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => wait.WaitAsync(TimeSpan.FromSeconds(5)));
            else
                await Assert.ThrowsAsync<TimeoutException>(
                    () => wait.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(3),
                $"Delayed-worker cleanup took {stopwatch.ElapsedMilliseconds} ms.");
            Assert.False(process.Disposal.IsCompleted);
            Assert.False(observer.HasExited);

            scheduler.RunQueuedTask();
            await AssertExitsEventuallyAsync(observer);
            await process.Disposal.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            scheduler.RunQueuedTask();
            if (!observer.HasExited)
                observer.Kill(entireProcessTree: true);
            await AssertExitsEventuallyAsync(observer);
        }
    }

    private static Process ObserveProcess(Process process)
    {
        var observer = Process.GetProcessById(process.Id);
        // Pin the original Windows process identity before it can exit.
        _ = observer.SafeHandle;
        return observer;
    }

    private sealed class HeldTaskScheduler : TaskScheduler
    {
        internal TaskCompletionSource<Task> Queued { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override void QueueTask(Task task) => Queued.SetResult(task);

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task>? GetScheduledTasks() =>
            Queued.Task.IsCompletedSuccessfully ? [Queued.Task.Result] : [];

        internal void RunQueuedTask()
        {
            if (Queued.Task.IsCompletedSuccessfully)
                TryExecuteTask(Queued.Task.Result);
        }
    }

    private sealed class TrackedProcess : Process
    {
        private readonly TaskCompletionSource _disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Disposal => _disposed.Task;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                _disposed.TrySetResult();
        }
    }

    private static TrackedProcess StartFixture(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FindTestHost(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--process-fixture");
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        var process = new TrackedProcess { StartInfo = startInfo };
        Assert.True(process.Start(), "Could not start process fixture.");
        return process;
    }

    private static string FindTestHost()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null
            && !File.Exists(Path.Combine(current.FullName, "openclaw-windows-node.slnx")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        var configuration = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?
            .Configuration;
        Assert.False(string.IsNullOrWhiteSpace(configuration));
        var executableName = OperatingSystem.IsWindows()
            ? "OpenClaw.Shared.TestHost.exe"
            : "OpenClaw.Shared.TestHost";
        var hostPath = Path.Combine(
            current.FullName,
            "tests",
            "OpenClaw.Shared.TestHost",
            "bin",
            configuration,
            "net10.0",
            executableName);
        Assert.True(File.Exists(hostPath), $"Process test host was not built: {hostPath}");
        return hostPath;
    }

    private static async Task AssertExitsEventuallyAsync(Process process)
    {
        // Cleanup is intentionally bounded, so Windows may report the killed
        // process exit just after WaitAsync has returned to its caller.
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<int> ReadChildPidAsync(string pidFile)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var text = await File.ReadAllTextAsync(pidFile);
            if (int.TryParse(text, out var processId))
                return processId;

            await Task.Delay(10);
        }

        throw new TimeoutException("Inherited-handle fixture did not publish its child PID.");
    }

    private static void KillProcessTree(int processId)
    {
        if (processId <= 0)
            return;

        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(2_000);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
        catch (AggregateException)
        {
        }
    }
}
