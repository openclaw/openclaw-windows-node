using System.Diagnostics;

namespace OpenClaw.SetupEngine.Tests;

public sealed class BoundedProcessOutputTests
{
    [Fact]
    public async Task AwaitRedirectedOutput_ReturnsNullWhenStdoutNeverCloses()
    {
        using var process = StartExitingProcess();
        Assert.NotNull(process);

        var never = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously).Task;
        var helper = Task.Run(() =>
            BoundedProcessOutput.AwaitRedirectedOutput(process, never, timeoutMs: 400));

        var completed = await Task.WhenAny(helper, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(helper, completed);
        Assert.Null(await helper);
    }

    [Fact]
    public async Task AwaitRedirectedOutput_ReturnsNullWhenProcessDoesNotExit()
    {
        var (fileName, arguments) = LongRunningCommand();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(process);

        var readTask = process.StandardOutput.ReadToEndAsync();
        var stopwatch = Stopwatch.StartNew();
        var output = BoundedProcessOutput.AwaitRedirectedOutput(
            process,
            readTask,
            timeoutMs: 400);

        Assert.Null(output);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Assert.True(process.HasExited);
        var readException = await Record.ExceptionAsync(
            () => readTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(
            readException is null or ObjectDisposedException,
            $"Abandoned stdout read failed unexpectedly: {readException}");
    }

    [Fact]
    public void AwaitRedirectedOutput_PreservesOutputCompletedDuringDrainGrace()
    {
        using var process = StartExitingProcess();
        Assert.True(process.WaitForExit(3_000));
        var outputSource = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string? output = null;
        Exception? helperException = null;
        var helperThread = new Thread(() =>
        {
            try
            {
                output = BoundedProcessOutput.AwaitRedirectedOutput(
                    process,
                    outputSource.Task,
                    timeoutMs: 5_000);
            }
            catch (Exception ex)
            {
                helperException = ex;
            }
        })
        {
            IsBackground = true,
            Name = "setup-tailscale-output-drain-test"
        };

        helperThread.Start();
        Assert.True(
            SpinWait.SpinUntil(
                () => helperThread.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin),
                TimeSpan.FromSeconds(3)),
            "Helper never entered the redirected-output drain wait.");
        outputSource.SetResult("{\"BackendState\":\"Running\"}");

        Assert.True(helperThread.Join(TimeSpan.FromSeconds(3)));
        Assert.Null(helperException);
        Assert.Equal("{\"BackendState\":\"Running\"}", output);
    }

    [Fact]
    public void Read_CapturesStdoutFromExitingProcess()
    {
        var (fileName, arguments) = EchoCommand("status-ok");
        var result = BoundedProcessOutput.Read(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }, timeoutMs: 5_000);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("status-ok", result.Output);
    }

    private static Process StartExitingProcess() =>
        Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/d /c exit 0" : "-c \"exit 0\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

    private static (string FileName, string Arguments) LongRunningCommand() =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", "/d /c ping 127.0.0.1 -n 20")
            : ("/bin/sh", "-c \"sleep 20\"");

    private static (string FileName, string Arguments) EchoCommand(string text) =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", $"/d /c echo {text}")
            : ("/bin/sh", $"-c \"printf '%s\\n' '{text}'\"");
}
