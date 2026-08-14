using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenClaw.Shared.Mxc;
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
            Assert.True(result.TimedOut);
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
}
