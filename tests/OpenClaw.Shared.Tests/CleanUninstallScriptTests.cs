using System.Diagnostics;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class CleanUninstallScriptTests
{
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

        using var process = Process.Start(start);
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await output.WaitAsync(timeout.Token);
            var stderr = await error.WaitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, $"{stdout}{Environment.NewLine}{stderr}");
            Assert.Contains("Clean uninstall regressions passed:", stdout);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
