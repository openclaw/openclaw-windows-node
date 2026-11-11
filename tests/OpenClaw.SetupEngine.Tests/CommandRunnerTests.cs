using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace OpenClaw.SetupEngine.Tests;

public class CommandRunnerTests
{
    private static readonly string s_largeStdin = new('x', 8 * 1024 * 1024);
    private static readonly TimeSpan s_cancellationTestDeadline = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task RunAsync_CancellationDuringInputLogsStateWithoutOutputAndPreservesToken()
    {
        using var logger = new SetupLogger(filePath: null);
        var entries = new List<LogEntry>();
        logger.LogEmitted += (_, entry) => entries.Add(entry);
        using var cancellation = new CancellationTokenSource(s_cancellationTestDeadline);
        await using var input = new CancelOnCopyStream(cancellation);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CommandRunner(logger).RunAsync(
                FindTestHost(), ["--process-fixture", "hold-silent", "30000"],
                TimeSpan.FromSeconds(30), ct: cancellation.Token, stdinStream: input));

        Assert.Equal(input.CopyToken, error.CancellationToken);
        Assert.True(input.CopyStarted);
        AssertCancellationState(entries, "execution");
        Assert.DoesNotContain(entries, entry => entry.Message.StartsWith("cmd.done:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_CancellationDuringPostExitDrainRecordsExitButNotPipeEof(bool allowInheritedPipeHandleEscape)
    {
        using var logger = new SetupLogger(filePath: null);
        var entries = new List<LogEntry>();
        using var cancellation = new CancellationTokenSource(s_cancellationTestDeadline);
        var exitObserved = false;
        logger.LogEmitted += (_, entry) =>
        {
            entries.Add(entry);
            if (entry.Message == "cmd.exit: direct child exit observed")
            {
                exitObserved = true;
                cancellation.Cancel();
            }
        };
        var pidFile = Path.GetTempFileName();
        var childPid = 0;
        try
        {
            var runner = new CommandRunner(logger);
            string[] arguments = ["--process-fixture", "inherit-handles", "30000", pidFile];
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                allowInheritedPipeHandleEscape
                    ? runner.RunAsyncAllowingInheritedPipeHandleEscape(
                        FindTestHost(), arguments, TimeSpan.FromSeconds(30), ct: cancellation.Token)
                    : runner.RunAsync(
                        FindTestHost(), arguments, TimeSpan.FromSeconds(30), ct: cancellation.Token));

            Assert.Equal(cancellation.Token, error.CancellationToken);
            childPid = int.Parse(await File.ReadAllTextAsync(pidFile));
            var state = AssertCancellationState(entries, "output-drain");
            Assert.True(exitObserved);
            Assert.True(state.GetProperty("direct_child_exit_observed").GetBoolean());
            Assert.False(state.GetProperty("stdout_eof_observed").GetBoolean());
            Assert.False(state.GetProperty("stderr_eof_observed").GetBoolean());
            var exit = Assert.Single(entries, entry => entry.Message == "cmd.exit: direct child exit observed");
            var exitState = JsonSerializer.SerializeToElement(exit.Data);
            Assert.Equal(0, exitState.GetProperty("exit_code").GetInt32());
            Assert.Equal(new[] { "elapsed_ms", "exit_code" },
                exitState.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
            Assert.DoesNotContain(entries, entry => entry.Message.StartsWith("cmd.done:", StringComparison.Ordinal));
        }
        finally
        {
            // Also recover the owned descendant when an assertion fails before parsing its PID.
            if (childPid == 0 && int.TryParse(await File.ReadAllTextAsync(pidFile), out var writtenPid))
                childPid = writtenPid;
            KillProcessTree(childPid);
            File.Delete(pidFile);
        }
    }

    private static JsonElement AssertCancellationState(List<LogEntry> entries, string phase)
    {
        var entry = Assert.Single(entries, e => e.Message == "cmd.cancelled: caller cancellation observed");
        Assert.Equal(LogLevel.Warn, entry.Level);
        var state = JsonSerializer.SerializeToElement(entry.Data);
        Assert.Equal(phase, state.GetProperty("phase").GetString());
        Assert.False(state.GetProperty("timed_out").GetBoolean());
        Assert.True(state.GetProperty("elapsed_ms").GetDouble() >= 0);
        Assert.Contains(state.GetProperty("direct_child_exit_observed").ValueKind,
            new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.Equal(
            new[] { "direct_child_exit_observed", "elapsed_ms", "phase", "stderr_eof_observed", "stdout_eof_observed", "timed_out" },
            state.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        return state;
    }

    private sealed class CancelOnCopyStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public bool CopyStarted { get; private set; }
        public CancellationToken CopyToken { get; private set; }

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            CopyStarted = true;
            CopyToken = cancellationToken;
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunAsync_WslVersion_DecodesOutputBeforeLogging()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(WslConstants.WslExePath))
            return;

        var logPath = Path.Combine(Path.GetTempPath(), $"openclaw-wsl-output-{Guid.NewGuid():N}.jsonl");

        try
        {
            CommandResult result;
            using (var logger = new SetupLogger(logPath, LogLevel.Trace))
            {
                var runner = new CommandRunner(logger);
                result = await runner.RunAsync(
                    WslConstants.WslExePath,
                    ["--version"],
                    TimeSpan.FromSeconds(15),
                    environment: new Dictionary<string, string>
                    {
                        ["WSL_UTF8"] = "0",
                    });
            }

            var output = result.Stdout + result.Stderr;
            Assert.NotEmpty(output);
            Assert.Contains("WSL version", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('\0', output);

            var jsonl = await File.ReadAllTextAsync(logPath);
            Assert.DoesNotContain("\\u0000", jsonl, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Theory]
    [InlineData("wsl.exe")]
    [InlineData(@"C:\Windows\System32\WSL.EXE")]
    public void IsWslExecutable_WslPath_ReturnsTrue(string executable)
    {
        Assert.True(CommandRunner.IsWslExecutable(executable));
    }

    [Theory]
    [InlineData("wsl")]
    [InlineData("powershell.exe")]
    [InlineData("")]
    public void IsWslExecutable_OtherPath_ReturnsFalse(string executable)
    {
        Assert.False(CommandRunner.IsWslExecutable(executable));
    }

    [Fact]
    public async Task RunAsync_LargeStdinWriteObeysTimeout()
    {
        var runner = CreateRunner();
        var (executable, arguments) = SleepingCommand();
        var stopwatch = Stopwatch.StartNew();

        var result = await runner.RunAsync(
            executable,
            arguments,
            TimeSpan.FromMilliseconds(250),
            stdinInput: s_largeStdin);

        Assert.True(result.TimedOut);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunAsync_LargeStdinWriteObeysCallerCancellation()
    {
        var runner = CreateRunner();
        var (executable, arguments) = SleepingCommand();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            executable,
            arguments,
            TimeSpan.FromSeconds(30),
            stdinInput: s_largeStdin,
            ct: cts.Token));

        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunAsync_StreamStdinPreservesBinaryBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openclaw-stdin-{Guid.NewGuid():N}.bin");
        var bytes = new byte[] { 0, 1, 2, 0x7F, 0x80, 0xFF };
        var (executable, arguments) = CopyStdinCommand(path);
        await using var input = new MemoryStream(bytes);

        try
        {
            var result = await CreateRunner().RunAsync(
                executable,
                arguments,
                TimeSpan.FromSeconds(15),
                stdinStream: input);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_RejectsMultipleStdinSources()
    {
        var (executable, arguments) = SleepingCommand();
        await using var input = new MemoryStream([1]);

        await Assert.ThrowsAsync<ArgumentException>(() => CreateRunner().RunAsync(
            executable,
            arguments,
            TimeSpan.FromSeconds(1),
            stdinInput: "text",
            stdinStream: input));
    }

    [Fact]
    public async Task RunAsync_ReturnsWhenDescendantKeepsOutputPipesOpen()
    {
        var runner = CreateRunner();
        var pidFile = Path.GetTempFileName();
        var childPid = 0;

        try
        {
            var result = await runner.RunAsyncAllowingInheritedPipeHandleEscape(
                FindTestHost(),
                ["--process-fixture", "inherit-handles", "30000", pidFile],
                TimeSpan.FromSeconds(30));
            var completedAt = DateTime.UtcNow;

            Assert.False(
                result.TimedOut,
                $"Command timed out. stdout={result.Stdout} stderr={result.Stderr}");
            Assert.True(
                result.ExitCode == 0,
                $"exit={result.ExitCode} stdout={result.Stdout} stderr={result.Stderr}");

            childPid = int.Parse(await File.ReadAllTextAsync(pidFile));
            var drainElapsed = completedAt - File.GetLastWriteTimeUtc(pidFile);

            using var child = Process.GetProcessById(childPid);
            Assert.False(child.HasExited);
            Assert.InRange(drainElapsed, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15));
        }
        finally
        {
            KillProcessTree(childPid);
            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task WaitForOutputDrainAsync_WaitsPastCommandDeadlineOnNormalExit()
    {
        var outputClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = CommandRunner.WaitForOutputDrainAsync(
            outputClosed.Task,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.Zero,
            timedOut: false,
            allowInheritedPipeHandleEscape: false);

        var completed = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.NotSame(wait, completed);

        outputClosed.SetResult();
        await wait;
    }

    [Fact]
    public async Task WaitForOutputDrainAsync_CancellationInterruptsNormalPostExitDrain()
    {
        var outputClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var wait = CommandRunner.WaitForOutputDrainAsync(
            outputClosed.Task,
            TimeSpan.FromSeconds(30),
            TimeSpan.Zero,
            timedOut: false,
            allowInheritedPipeHandleEscape: false,
            cancellation.Token);

        Assert.False(wait.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task WaitForOutputDrainAsync_AlreadyObservedEofWinsCancellation(
        bool timedOut, bool allowInheritedPipeHandleEscape)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await CommandRunner.WaitForOutputDrainAsync(
            Task.CompletedTask, TimeSpan.FromSeconds(30), TimeSpan.Zero,
            timedOut, allowInheritedPipeHandleEscape, cancellation.Token);
    }

    [Fact]
    public async Task RunAsync_DrainsHighVolumeStdoutAndStderrThroughTrailingMarkers()
    {
        const int lineCount = 8_000;
        var (executable, arguments) = HighVolumeOutputCommand(lineCount);

        var result = await CreateRunner().RunAsync(
            executable,
            arguments,
            TimeSpan.FromSeconds(30));

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(lineCount, CountLinesStartingWith(result.Stdout, "stdout-"));
        Assert.Equal(lineCount, CountLinesStartingWith(result.Stderr, "stderr-"));
        Assert.EndsWith($"STDOUT_MARKER{Environment.NewLine}", result.Stdout, StringComparison.Ordinal);
        Assert.EndsWith($"STDERR_MARKER{Environment.NewLine}", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(200, 175, false, 25)]
    [InlineData(200, 250, false, 0)]
    [InlineData(5_000, 1_000, false, 3_000)]
    [InlineData(200, 250, true, 250)]
    public void OutputDrainBudget_ClampsToDeadlineAndCap(
        int timeoutMilliseconds,
        int elapsedMilliseconds,
        bool timedOut,
        int expectedMilliseconds)
    {
        TimeSpan budget = CommandRunner.GetOutputDrainBudget(
            TimeSpan.FromMilliseconds(timeoutMilliseconds),
            TimeSpan.FromMilliseconds(elapsedMilliseconds),
            timedOut);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), budget);
    }

    private static CommandRunner CreateRunner()
        => new(new SetupLogger(filePath: null, LogLevel.Trace));

    private static (string Executable, string[] Arguments) SleepingCommand()
        => OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/d", "/s", "/c", "ping 127.0.0.1 -n 30 >nul"])
            : ("/bin/sh", ["-c", "sleep 30"]);

    private static string FindTestHost()
    {
        var executableName = OperatingSystem.IsWindows()
            ? "OpenClaw.Shared.TestHost.exe"
            : "OpenClaw.Shared.TestHost";
        var hostPath = Path.Combine(AppContext.BaseDirectory, executableName);
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

    private static (string Executable, string[] Arguments) HighVolumeOutputCommand(int lineCount)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ("/bin/sh",
            [
                "-c",
                "i=0; while [ $i -lt " + lineCount + " ]; do printf 'stdout-%s\\n' \"$i\"; i=$((i+1)); done; " +
                "printf 'STDOUT_MARKER\\n'; " +
                "i=0; while [ $i -lt " + lineCount + " ]; do printf 'stderr-%s\\n' \"$i\" >&2; i=$((i+1)); done; " +
                "printf 'STDERR_MARKER\\n' >&2"
            ]);
        }

        var script =
            $"for ($i = 0; $i -lt {lineCount}; $i++) {{ [Console]::Out.WriteLine(\"stdout-$i\") }}; " +
            "[Console]::Out.WriteLine(\"STDOUT_MARKER\"); " +
            $"for ($i = 0; $i -lt {lineCount}; $i++) {{ [Console]::Error.WriteLine(\"stderr-$i\") }}; " +
            "[Console]::Error.WriteLine(\"STDERR_MARKER\")";
        return ("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script]);
    }

    private static int CountLinesStartingWith(string value, string prefix) =>
        value.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.StartsWith(prefix, StringComparison.Ordinal));

    private static (string Executable, string[] Arguments) CopyStdinCommand(string path)
    {
        if (!OperatingSystem.IsWindows())
            return ("/bin/sh", ["-c", $"cat > '{path.Replace("'", "'\\''", StringComparison.Ordinal)}'"]);

        var escapedPath = path.Replace("'", "''", StringComparison.Ordinal);
        var script =
            $"$inputStream = [Console]::OpenStandardInput(); $output = [IO.File]::Create('{escapedPath}'); " +
            "try { $inputStream.CopyTo($output) } finally { $output.Dispose() }";
        return ("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script]);
    }
}
