using System.Diagnostics;

namespace OpenClaw.Tray.Tests;

public sealed class KokoroDownloadOwnershipTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("canceled")]
    [InlineData("failure")]
    [InlineData("duplicate-click")]
    [InlineData("preview-failure")]
    [InlineData("storage-failure")]
    [InlineData("storage-refresh-after-success")]
    public async Task PreviewCompletion_PreservesPendingDownloadAndTerminalState(string scenario)
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var start = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
            Path.Combine(root, "tests", "OpenClaw.Tray.Tests", "KokoroDownloadOwnershipHarness.ps1"),
            "-RepositoryRoot", root, "-Scenario", scenario,
        })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the Kokoro source-method harness.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        var output = await stdout;
        var error = await stderr;
        Assert.True(process.ExitCode == 0, $"{scenario}: {output}{Environment.NewLine}{error}");
        Assert.Contains($"PASS {scenario}", output);
    }
}
