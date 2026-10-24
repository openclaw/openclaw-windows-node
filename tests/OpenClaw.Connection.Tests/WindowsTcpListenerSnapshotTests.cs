using System.Diagnostics;
using System.Net;

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
    public void IsEstablishedLoopbackForwardUse_MatchesBrowserOrAcceptedRowOnly()
    {
        var loopback = IPAddress.Loopback;
        var remote = IPAddress.Parse("203.0.113.5");

        Assert.False(WindowsTcpListenerSnapshot.IsEstablishedLoopbackForwardUse(
            state: 2,
            loopback,
            localPort: 18789,
            remote,
            remotePort: 40000,
            forwardPort: 18789));
        Assert.True(WindowsTcpListenerSnapshot.IsEstablishedLoopbackForwardUse(
            WindowsTcpListenerSnapshot.TcpStateEstablished,
            loopback,
            localPort: 18789,
            remote,
            remotePort: 40000,
            forwardPort: 18789));
        Assert.True(WindowsTcpListenerSnapshot.IsEstablishedLoopbackForwardUse(
            WindowsTcpListenerSnapshot.TcpStateEstablished,
            remote,
            localPort: 40000,
            loopback,
            remotePort: 18789,
            forwardPort: 18789));
        Assert.False(WindowsTcpListenerSnapshot.IsEstablishedLoopbackForwardUse(
            WindowsTcpListenerSnapshot.TcpStateEstablished,
            remote,
            localPort: 18789,
            remote,
            remotePort: 40000,
            forwardPort: 18789));
    }

    [Fact]
    public void IsUnseenEstablishedForwardUse_RequiresTheLaunchedBrowserClient()
    {
        var loopback = IPAddress.Loopback;
        var client = IPAddress.Parse("203.0.113.8");
        var seen = new HashSet<string>(StringComparer.Ordinal)
        {
            WindowsTcpListenerSnapshot.EstablishedForwardKey(client, 40000, loopback, 18789, 88),
        };

        Assert.False(WindowsTcpListenerSnapshot.IsUnseenEstablishedForwardUse(
            WindowsTcpListenerSnapshot.TcpStateEstablished,
            client,
            localPort: 40000,
            loopback,
            remotePort: 18789,
            processId: 88,
            forwardPort: 18789,
            browserProcessId: 88,
            seen));
        Assert.True(WindowsTcpListenerSnapshot.IsUnseenEstablishedForwardUse(
            WindowsTcpListenerSnapshot.TcpStateEstablished,
            client,
            localPort: 40001,
            loopback,
            remotePort: 18789,
            processId: 88,
            forwardPort: 18789,
            browserProcessId: 88,
            seen));
        Assert.False(WindowsTcpListenerSnapshot.IsUnseenEstablishedForwardUse(
            WindowsTcpListenerSnapshot.TcpStateEstablished,
            client,
            localPort: 40001,
            loopback,
            remotePort: 18789,
            processId: 88,
            forwardPort: 18789,
            browserProcessId: 77,
            seen));
        Assert.False(WindowsTcpListenerSnapshot.IsUnseenEstablishedForwardUse(
            WindowsTcpListenerSnapshot.TcpStateEstablished,
            client,
            localPort: 40001,
            loopback,
            remotePort: 18789,
            processId: 88,
            forwardPort: 18789,
            browserProcessId: null,
            seen));
        Assert.False(WindowsTcpListenerSnapshot.IsUnseenEstablishedForwardUse(
            WindowsTcpListenerSnapshot.TcpStateEstablished,
            loopback,
            localPort: 18789,
            client,
            remotePort: 40002,
            processId: 88,
            forwardPort: 18789,
            browserProcessId: 88,
            seen));
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
