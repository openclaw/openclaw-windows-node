using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using OpenClaw.Shared.Mxc;
using OpenClaw.TestSupport;
using Xunit;

namespace OpenClaw.Shared.Tests.Mxc;

public class MxcExecutorTests
{
    [Fact]
    public async Task RunAsync_CapturesOutput_WhenLauncherExitsNormally()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var cmdPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var executor = new MxcExecutor(
            cmdPath,
            stdoutCapBytes: null,
            stderrCapBytes: null,
            processFactory: _ => CreateProcess(
                cmdPath,
                "echo stdout-line & echo stderr-line 1>&2"),
            processTreeKiller: process => process.Kill(entireProcessTree: true),
            cleanupTimeout: TimeSpan.FromSeconds(1));

        var result = await executor.RunAsync(new MxcConfig
        {
            ContainerId = "output-capture-test",
            Process = new MxcProcess { CommandLine = "ignored-by-test-process" },
        });

        Assert.True(result.Success);
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("stdout-line", result.Output);
        Assert.Contains("stderr-line", result.Error);
    }

    [Fact]
    public async Task RunAsync_DetectsRedirectedOutputByteOrderMarks()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var testHostPath = FindTestHost();
        var executor = new MxcExecutor(
            testHostPath,
            stdoutCapBytes: null,
            stderrCapBytes: null,
            processFactory: _ => CreateFixtureProcess(testHostPath, "bom-output"),
            processTreeKiller: process => process.Kill(entireProcessTree: true),
            cleanupTimeout: TimeSpan.FromSeconds(1));

        var result = await executor.RunAsync(new MxcConfig
        {
            ContainerId = "bom-output-test",
            Process = new MxcProcess { CommandLine = "ignored-by-test-process" },
        });

        Assert.True(result.Success);
        Assert.Equal("wide-stdout", result.Output);
        Assert.Equal("wide-stderr", result.Error);
    }

    [Fact]
    public async Task RunAsync_DecodesFragmentedPreamblesWithoutOverflow()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var testHostPath = FindTestHost();
        var executor = new MxcExecutor(
            testHostPath,
            stdoutCapBytes: null,
            stderrCapBytes: null,
            processFactory: _ => CreateFixtureProcess(testHostPath, "fragmented-bom-output"),
            processTreeKiller: process => process.Kill(entireProcessTree: true),
            cleanupTimeout: TimeSpan.FromSeconds(1));

        var result = await executor.RunAsync(new MxcConfig
        {
            ContainerId = "fragmented-bom-output-test",
            Process = new MxcProcess { CommandLine = "ignored-by-test-process" },
        });

        Assert.True(result.Success);
        Assert.Equal("utf32-stdout", result.Output);
        Assert.EndsWith(new string('x', 4_096), result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CancellationCleanupIsBounded_WhenProcessTreeKillDoesNotStopLauncher()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var launcherPid = 0;
        var killAttempted = false;
        Process? launcher = null;
        var cmdPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var executor = new MxcExecutor(
            cmdPath,
            stdoutCapBytes: null,
            stderrCapBytes: null,
            processFactory: _ => launcher = CreateProcess(
                cmdPath,
                "echo launcher-started & ping -n 31 127.0.0.1 >nul"),
            processTreeKiller: process =>
            {
                killAttempted = true;
                launcherPid = process.Id;
            },
            cleanupTimeout: TimeSpan.FromMilliseconds(100));
        using var cancellation = new CancellationTokenSource();

        try
        {
            var run = executor.RunAsync(
                new MxcConfig
                {
                    ContainerId = "bounded-cleanup-test",
                    Process = new MxcProcess { CommandLine = "ignored-by-test-process" },
                },
                cancellation.Token);
            await WaitForProcessStartAsync(() => launcher, TimeSpan.FromSeconds(5));
            launcherPid = launcher!.Id;
            var stopwatch = Stopwatch.StartNew();
            cancellation.Cancel();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
            stopwatch.Stop();

            Assert.True(killAttempted);
            Assert.True(result.TimedOut, result.Error);
            Assert.Equal(-1, result.ExitCode);
            Assert.Contains("cancelled", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                $"Post-cancel cleanup took {stopwatch.ElapsedMilliseconds} ms.");
        }
        finally
        {
            KillProcessTree(launcherPid);
        }
    }

    [Fact]
    public async Task RunAsync_CancellationCleanupIsBounded_WhenProcessTreeKillBlocks()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var launcherPid = 0;
        Process? launcher = null;
        using var killRelease = new ManualResetEventSlim(false);
        var killStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var killFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var killProcessUseError = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Action<Process> blockingKiller = _ =>
        {
            killStarted.TrySetResult();
            killRelease.Wait();
        };
        var cmdPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var executor = new MxcExecutor(
            cmdPath,
            stdoutCapBytes: null,
            stderrCapBytes: null,
            processFactory: _ => launcher = CreateProcess(
                cmdPath,
                "echo launcher-started & ping -n 31 127.0.0.1 >nul"),
            processTreeKiller: process =>
            {
                blockingKiller(process);
                try
                {
                    _ = process.Id;
                    killProcessUseError.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    killProcessUseError.TrySetResult(ex);
                }
                killFinished.TrySetResult();
            },
            cleanupTimeout: TimeSpan.FromMilliseconds(100));
        using var cancellation = new CancellationTokenSource();

        try
        {
            var run = executor.RunAsync(
                new MxcConfig
                {
                    ContainerId = "bounded-kill-test",
                    Process = new MxcProcess { CommandLine = "ignored-by-test-process" },
                },
                cancellation.Token);
            await WaitForProcessStartAsync(() => launcher, TimeSpan.FromSeconds(5));
            launcherPid = launcher!.Id;
            var stopwatch = Stopwatch.StartNew();
            cancellation.Cancel();
            await killStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
            stopwatch.Stop();

            Assert.True(result.TimedOut, result.Error);
            Assert.Equal(-1, result.ExitCode);
            Assert.Contains("cancelled", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                $"Post-cancel cleanup took {stopwatch.ElapsedMilliseconds} ms.");
        }
        finally
        {
            killRelease.Set();
            await killFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(await killProcessUseError.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            KillProcessTree(launcherPid);
        }
    }

    [Fact]
    public async Task KillProcessTreeWithTimeoutAsync_RepeatedBlockedKillsDoNotExceedProcessWideWorkerLimit()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var killStartedCount = 0;
        using var killRelease = new ManualResetEventSlim(false);
        using var killStarted = new CountdownEvent(MxcExecutor.ProcessTreeKillWorkerLimit);
        using var killFinished = new CountdownEvent(MxcExecutor.ProcessTreeKillWorkerLimit);
        var cmdPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var attemptCount = MxcExecutor.ProcessTreeKillWorkerLimit + 2;
        var workersFinished = false;
        using var process = Process.GetCurrentProcess();

        try
        {
            var killAttempts = Enumerable.Range(0, attemptCount).Select(_ =>
            {
                var executor = new MxcExecutor(
                    cmdPath,
                    stdoutCapBytes: null,
                    stderrCapBytes: null,
                    processFactory: _ => throw new InvalidOperationException("Not used by this test."),
                    processTreeKiller: _ =>
                    {
                        Interlocked.Increment(ref killStartedCount);
                        killStarted.Signal();
                        try
                        {
                            killRelease.Wait();
                        }
                        finally
                        {
                            killFinished.Signal();
                        }
                    },
                    cleanupTimeout: TimeSpan.FromMilliseconds(100));
                return executor.KillProcessTreeWithTimeoutAsync(process);
            }).ToArray();

            var results = await Task.WhenAll(killAttempts).WaitAsync(TimeSpan.FromSeconds(5));
            var workersStarted = killStarted.Wait(TimeSpan.FromSeconds(5));

            Assert.All(results, Assert.False);
            Assert.True(workersStarted, "Blocked kill workers did not all start within the test bound.");
            Assert.Equal(
                MxcExecutor.ProcessTreeKillWorkerLimit,
                Volatile.Read(ref killStartedCount));
        }
        finally
        {
            killRelease.Set();
            workersFinished = killFinished.Wait(TimeSpan.FromSeconds(5));
        }

        Assert.True(workersFinished, "Blocked kill workers did not exit after the test released them.");

        var recoveredKillStarted = false;
        var recoveredExecutor = new MxcExecutor(
            cmdPath,
            stdoutCapBytes: null,
            stderrCapBytes: null,
            processFactory: _ => throw new InvalidOperationException("Not used by this test."),
            processTreeKiller: _ => recoveredKillStarted = true,
            cleanupTimeout: TimeSpan.FromMilliseconds(100));
        var recovered = false;
        for (var attempt = 0; attempt < 50 && !recovered; attempt++)
        {
            recovered = await recoveredExecutor.KillProcessTreeWithTimeoutAsync(process);
            if (!recovered)
                await Task.Delay(10);
        }

        Assert.True(recovered, "Process-wide kill worker capacity did not recover.");
        Assert.True(recoveredKillStarted);
    }

    [Fact]
    public async Task KillProcessTreeWithTimeoutAsync_WaitsForTransientWorkerCapacityWithinCleanupBudget()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        using var process = Process.GetCurrentProcess();
        using var killRelease = new SemaphoreSlim(0, MxcExecutor.ProcessTreeKillWorkerLimit);
        using var killStarted = new CountdownEvent(MxcExecutor.ProcessTreeKillWorkerLimit);
        var cmdPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var blockingAttempts = Array.Empty<Task<bool>>();

        try
        {
            blockingAttempts = Enumerable.Range(0, MxcExecutor.ProcessTreeKillWorkerLimit)
                .Select(_ =>
                {
                    var executor = new MxcExecutor(
                        cmdPath,
                        stdoutCapBytes: null,
                        stderrCapBytes: null,
                        processFactory: _ => throw new InvalidOperationException("Not used by this test."),
                        processTreeKiller: _ =>
                        {
                            killStarted.Signal();
                            killRelease.Wait();
                        },
                        cleanupTimeout: TimeSpan.FromSeconds(5));
                    return executor.KillProcessTreeWithTimeoutAsync(process);
                })
                .ToArray();
            Assert.True(
                killStarted.Wait(TimeSpan.FromSeconds(5)),
                "Initial kill workers did not occupy the process-wide limit.");

            var queuedKillStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var queuedExecutor = new MxcExecutor(
                cmdPath,
                stdoutCapBytes: null,
                stderrCapBytes: null,
                processFactory: _ => throw new InvalidOperationException("Not used by this test."),
                processTreeKiller: _ => queuedKillStarted.TrySetResult(),
                cleanupTimeout: TimeSpan.FromSeconds(2));
            var queuedAttempt = queuedExecutor.KillProcessTreeWithTimeoutAsync(process);

            await Task.Delay(100);
            Assert.False(queuedKillStarted.Task.IsCompleted);
            killRelease.Release();

            Assert.True(await queuedAttempt.WaitAsync(TimeSpan.FromSeconds(5)));
            await queuedKillStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            killRelease.Release(MxcExecutor.ProcessTreeKillWorkerLimit);
            try { await Task.WhenAll(blockingAttempts).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
        }
    }

    [Fact]
    public async Task RunAsync_OutputDrainIsBoundedAndPreservesFinalFragments_WhenDescendantRetainsHandles()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        using var temp = new TempDirectory("mxc-drain-test-");
        var descendantPidPath = temp.Combine("descendant.pid");
        var testHostPath = FindTestHost();
        var executor = new MxcExecutor(
            testHostPath,
            stdoutCapBytes: null,
            stderrCapBytes: null,
            processFactory: _ => CreateFixtureProcess(
                testHostPath,
                "inherit-handles-identity",
                "30000",
                descendantPidPath),
            processTreeKiller: process => process.Kill(entireProcessTree: true),
            cleanupTimeout: TimeSpan.FromMilliseconds(100));
        var run = executor.RunAsync(new MxcConfig
        {
            ContainerId = "bounded-output-drain-test",
            Process = new MxcProcess { CommandLine = "ignored-by-test-process" },
        });
        ChildProcessIdentity? descendant = null;
        try
        {
            descendant = await ReadChildIdentityAsync(descendantPidPath, TimeSpan.FromSeconds(5));
            var stopwatch = Stopwatch.StartNew();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(2));
            stopwatch.Stop();

            Assert.True(result.Success, result.Error);
            Assert.False(result.TimedOut);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("parent-stdout", result.Output);
            Assert.Contains("parent-stderr", result.Error);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                $"Post-exit output drain took {stopwatch.ElapsedMilliseconds} ms.");
        }
        finally
        {
            KillProcessTree(descendant);
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
        }
    }

    [Fact]
    public async Task RunAsync_OutputDrainPreservesExactPipeBufferFragments_WhenDescendantRetainsHandles()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        const int outputLength = 4_096;
        using var temp = new TempDirectory("mxc-drain-buffer-test-");
        var descendantPidPath = temp.Combine("descendant.pid");
        var testHostPath = FindTestHost();
        var executor = new MxcExecutor(
            testHostPath,
            stdoutCapBytes: outputLength + 1,
            stderrCapBytes: outputLength + 1,
            processFactory: _ => CreateFixtureProcess(
                testHostPath,
                "inherit-handles-sized",
                "30000",
                descendantPidPath,
                outputLength.ToString()),
            processTreeKiller: process => process.Kill(entireProcessTree: true),
            cleanupTimeout: TimeSpan.FromMilliseconds(100));
        var run = executor.RunAsync(new MxcConfig
        {
            ContainerId = "bounded-output-buffer-drain-test",
            Process = new MxcProcess { CommandLine = "ignored-by-test-process" },
        });
        ChildProcessIdentity? descendant = null;

        try
        {
            descendant = await ReadChildIdentityAsync(descendantPidPath, TimeSpan.FromSeconds(5));
            var result = await run.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(result.Success, result.Error);
            Assert.Equal(new string('o', outputLength), result.Output);
            Assert.Equal(new string('e', outputLength), result.Error);
        }
        finally
        {
            KillProcessTree(descendant);
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
        }
    }

    [Fact]
    public async Task WaitForCleanupAsync_ReturnsTrue_WhenProcessAndPipesComplete()
    {
        var completed = Task.CompletedTask;

        var result = await MxcExecutor.WaitForCleanupAsync(
            completed,
            completed,
            completed,
            TimeSpan.FromSeconds(1));

        Assert.True(result);
    }

    private static async Task WaitForProcessStartAsync(
        Func<Process?> processProvider,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (processProvider()?.Id > 0)
                    return;
            }
            catch (InvalidOperationException)
            {
                // Process.Start has not completed yet.
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Test launcher did not start within the expected time.");
    }

    private static async Task<ChildProcessIdentity> ReadChildIdentityAsync(
        string path,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var text = await File.ReadAllTextAsync(path);
                var parts = text.TrimEnd('\r', '\n').Split('|');
                if (text.EndsWith('\n')
                    && parts.Length == 2
                    && int.TryParse(parts[0], out var processId)
                    && long.TryParse(parts[1], out var startTimeUtcTicks))
                {
                    return new ChildProcessIdentity(processId, startTimeUtcTicks);
                }
            }
            catch (IOException)
            {
                // The fixture may still be publishing the PID.
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Test descendant did not report its process ID within the expected time.");
    }

    private static Process CreateProcess(string cmdPath, string command)
    {
        var startInfo = new ProcessStartInfo(cmdPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(command);
        return new Process { StartInfo = startInfo };
    }

    private static Process CreateFixtureProcess(string testHostPath, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(testHostPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--process-fixture");
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return new Process { StartInfo = startInfo };
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
            // The process already exited.
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
    }

    private static void KillProcessTree(ChildProcessIdentity? identity)
    {
        if (identity is null)
            return;

        try
        {
            using var process = Process.GetProcessById(identity.Value.ProcessId);
            if (process.StartTime.ToUniversalTime().Ticks != identity.Value.StartTimeUtcTicks)
                return;

            process.Kill(entireProcessTree: true);
            process.WaitForExit(2_000);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private readonly record struct ChildProcessIdentity(
        int ProcessId,
        long StartTimeUtcTicks);
}
