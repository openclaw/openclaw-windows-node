using System.Diagnostics;
using OpenClaw.Shared.Audio;

namespace OpenClaw.Shared.Tests;

public sealed class BoundedProcessWaitTests
{
    [Fact]
    public async Task Wait_TimesOutAndKillsWhenProcessDoesNotExit()
    {
        var (fileName, arguments) = LongRunningCommand();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(process);

        var helper = Task.Run(() =>
            BoundedProcessWait.Wait(process, timeoutMs: 400));

        var completed = await Task.WhenAny(helper, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(helper, completed);
        await Assert.ThrowsAsync<TimeoutException>(() => helper);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task Wait_ThrowsWhenCanceledAndKillsProcess()
    {
        var (fileName, arguments) = LongRunningCommand();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(process);

        using var cts = new CancellationTokenSource();
        var helper = Task.Run(
            () => BoundedProcessWait.Wait(process, timeoutMs: 30_000, cts.Token),
            CancellationToken.None);
        cts.Cancel();

        var completed = await Task.WhenAny(helper, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(helper, completed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => helper);
        Assert.True(process.HasExited);
    }

    [Fact]
    public void Wait_ReturnsExitCodeAndStderrFromFailingProcess()
    {
        var (fileName, arguments) = FailWithStderrCommand("extract-failed");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(process);

        var result = BoundedProcessWait.Wait(process, timeoutMs: 5_000);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("extract-failed", result.StandardError);
    }

    [Fact]
    public void Wait_ReturnsZeroFromExitingProcess()
    {
        using var process = StartExitingProcess();
        Assert.NotNull(process);

        var result = BoundedProcessWait.Wait(process, timeoutMs: 5_000);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
    }

    private static Process StartExitingProcess() =>
        Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/d /c exit 0" : "-c \"exit 0\"",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

    private static (string FileName, string Arguments) LongRunningCommand() =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", "/d /c ping 127.0.0.1 -n 20")
            : ("/bin/sh", "-c \"sleep 20\"");

    private static (string FileName, string Arguments) FailWithStderrCommand(string text) =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", $"/d /c echo {text} 1>&2 & exit 7")
            : ("/bin/sh", $"-c \"printf '%s\\n' '{text}' >&2; exit 7\"");
}
