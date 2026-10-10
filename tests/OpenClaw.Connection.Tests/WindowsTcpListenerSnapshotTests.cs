using System.Diagnostics;

namespace OpenClaw.Connection.Tests;

public sealed class WindowsTcpListenerSnapshotTests
{
    [Fact]
    public void GetProcessCommandLine_InvalidPid_ReturnsNull()
    {
        Assert.Null(WindowsTcpListenerSnapshot.GetProcessCommandLine(0));
        Assert.Null(WindowsTcpListenerSnapshot.GetProcessCommandLine(-1));
    }

    [Fact]
    public async Task AwaitRedirectedOutput_ReturnsNullWhenStdoutNeverCloses()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c exit 0" : "-c \"exit 0\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(process);

        var never = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously).Task;
        var helper = Task.Run(() =>
            WindowsTcpListenerSnapshot.AwaitRedirectedOutput(process, never, timeoutMs: 400));

        var completed = await Task.WhenAny(helper, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(helper, completed);
        Assert.Null(await helper);
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
                output = WindowsTcpListenerSnapshot.AwaitRedirectedOutput(
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
            Name = "redirected-output-drain-test"
        };

        helperThread.Start();
        Assert.True(
            SpinWait.SpinUntil(
                () => helperThread.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin),
                TimeSpan.FromSeconds(3)),
            "Helper never entered the redirected-output drain wait.");
        outputSource.SetResult("complete output");

        Assert.True(helperThread.Join(TimeSpan.FromSeconds(3)));
        Assert.Null(helperException);
        Assert.Equal("complete output", output);
    }

    [Fact]
    public async Task AwaitRedirectedOutput_ReturnsNullWhenDescendantKeepsStdoutOpen()
    {
        var (fileName, arguments) = DescendantPipeHolderCommand();
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
        var output = WindowsTcpListenerSnapshot.AwaitRedirectedOutput(
            process,
            readTask,
            timeoutMs: 400);

        Assert.Null(output);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        var readException = await Record.ExceptionAsync(
            () => readTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(
            readException is null or ObjectDisposedException,
            $"Abandoned stdout read failed unexpectedly: {readException}");
    }

    [Fact]
    public void GetProcessCommandLine_CurrentProcess_IsBoundedOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var stopwatch = Stopwatch.StartNew();
        _ = WindowsTcpListenerSnapshot.GetProcessCommandLine(Environment.ProcessId);

        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(7));
    }

    private static Process StartExitingProcess() =>
        Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/d /c exit 0" : "-c \"exit 0\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

    private static (string FileName, string Arguments) DescendantPipeHolderCommand() =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", "/d /s /c \"start /b ping 127.0.0.1 -n 3\"")
            : ("/bin/sh", "-c \"sleep 2 & exit 0\"");
}
