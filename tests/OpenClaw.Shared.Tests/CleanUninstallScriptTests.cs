using System.Diagnostics;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class CleanUninstallScriptTests
{
    private const string ScriptTimeoutVariable = "OPENCLAW_CLEAN_UNINSTALL_TEST_TIMEOUT_SECONDS";
    // Bounds one Windows PowerShell 5.1 run of the interpreted cleanup regressions. Hosted runners
    // take 16-37 s, but run time grows with host load and a contended developer host took 161 s.
    // Together with CleanupTimeout it stays below the 300 s VSTest hang watchdog, so this test
    // reports the script's progress before the watchdog fires.
    private static readonly TimeSpan ScriptTimeout =
        int.TryParse(Environment.GetEnvironmentVariable(ScriptTimeoutVariable), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(240);
    // Killing the tree closes the pipes almost at once. This bound only stops a surviving
    // descendant that still holds a pipe from hiding the timeout report.
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task StandaloneCleanup_PassesIsolatedSafetyRegressions()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = ProductionSourceFiles.FindRepoRoot();
        var start = new ProcessStartInfo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // CI can pass PowerShell 7's incompatible modules through the .NET test host.
        // Let Windows PowerShell construct its own module search path.
        start.Environment.Remove("PSModulePath");
        foreach (var argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", Path.Combine(root, "scripts", "test-clean-uninstall.ps1"),
        })
            start.ArgumentList.Add(argument);

        // Create the deadline first so an invalid override cannot leave PowerShell running.
        using var timeout = new CancellationTokenSource(ScriptTimeout);
        using var process = Process.Start(start);
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            string stdout;
            string stderr;
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                stdout = await output.WaitAsync(timeout.Token);
                stderr = await error.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException(await DescribeTimeoutAsync(process, output, error));
            }
            Assert.True(process.ExitCode == 0, $"{stdout}{Environment.NewLine}{stderr}");
            Assert.Contains("Clean uninstall regressions passed:", stdout);
        }
        finally
        {
            if (!process.HasExited)
                await TryStopTreeAsync(process);
        }
    }

    private static async Task<string> DescribeTimeoutAsync(Process process, Task<string> output, Task<string> error)
    {
        using var cleanup = new CancellationTokenSource(CleanupTimeout);
        var stopProblem = await TryStopTreeAsync(process, cleanup.Token);
        var stdout = await TryReadAsync(output, cleanup.Token);
        var stderr = await TryReadAsync(error, cleanup.Token);
        return $"Clean uninstall regressions did not finish within {ScriptTimeout.TotalSeconds:0} s " +
            $"(override with {ScriptTimeoutVariable}).{stopProblem}" +
            $"{Environment.NewLine}Last output:{Environment.NewLine}{Tail(stdout, "no stdout before the deadline")}" +
            $"{Environment.NewLine}Last errors:{Environment.NewLine}{Tail(stderr, "no stderr")}";
    }

    // Best effort: cleanup failures must not replace the primary result.
    private static async Task<string> TryStopTreeAsync(Process process, CancellationToken cancellationToken = default)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            using var exit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            exit.CancelAfter(CleanupTimeout);
            await process.WaitForExitAsync(exit.Token);
            return string.Empty;
        }
        catch (Exception ex)
        {
            return $" Stopping the process tree failed ({ex.GetType().Name}: {ex.Message}).";
        }
    }

    private static async Task<string> TryReadAsync(Task<string> stream, CancellationToken cancellationToken)
    {
        try { return await stream.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { return "(stream was not drained)"; }
        catch (Exception ex) { return $"(stream read failed: {ex.GetType().Name})"; }
    }

    private static string Tail(string text, string empty)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? $"({empty})" : string.Join(Environment.NewLine, lines.TakeLast(5));
    }
}
