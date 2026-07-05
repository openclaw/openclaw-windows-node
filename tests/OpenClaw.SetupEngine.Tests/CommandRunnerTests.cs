using System.Diagnostics;

namespace OpenClaw.SetupEngine.Tests;

public class CommandRunnerTests
{
    private static readonly string s_largeStdin = new('x', 8 * 1024 * 1024);

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

    private static CommandRunner CreateRunner()
        => new(new SetupLogger(filePath: null, LogLevel.Trace));

    private static (string Executable, string[] Arguments) SleepingCommand()
        => OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/d", "/s", "/c", "ping 127.0.0.1 -n 30 >nul"])
            : ("/bin/sh", ["-c", "sleep 30"]);
}
