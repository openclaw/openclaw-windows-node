using System.Diagnostics;
using System.Text.Json;
using OpenClaw.TestSupport;
using OpenClaw.Tray.IntegrationTests;

namespace OpenClaw.SetupEngine.Tests;

public sealed class CleanupProofWorkerTests
{
    [IntegrationFact]
    public async Task ScriptStatusAndNativeExit_ArePreservedAcrossActualPowerShellInvocation()
    {
        foreach (var (script, expectedExit, expectedSuccess) in new[]
        {
            ("param($Family); 'successful cmdlet-only cleanup' | Out-Null", 0, true),
            ("param($Family); exit 37", 37, false),
            ("param($Family); throw 'fixture worker failure'", 1, false),
        })
        {
            using var directory = new TempDirectory();
            var worker = directory.Combine("production-worker.ps1");
            var result = directory.Combine("worker-result.json");
            var pidFile = directory.Combine("worker-pid.txt");
            await File.WriteAllTextAsync(worker, script);
            var token = "Local\\CleanupProofWorker-" + Guid.NewGuid().ToString("N");
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, token + "-ready");
            using var release = new EventWaitHandle(true, EventResetMode.ManualReset, token + "-release");
            using var finish = new EventWaitHandle(false, EventResetMode.ManualReset, token + "-finish");
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment["PSExecutionPolicyPreference"] = "Restricted";
            foreach (var argument in new[]
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                Path.Combine(AppContext.BaseDirectory, "InvokeCleanupProofWorker.ps1"),
                "-WorkerPath", worker, "-Family", "Fixture_123", "-ReadyName", token + "-ready",
                "-ReleaseName", token + "-release", "-FinishName", token + "-finish",
                "-ExternalPidFile", pidFile, "-ResultPath", result,
            })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(stdout, stderr);
                Assert.Equal(expectedExit, process.ExitCode);
                Assert.True(ready.WaitOne(0));
                Assert.True(finish.WaitOne(0));
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(result));
                Assert.Equal(expectedExit, document.RootElement.GetProperty("ExitCode").GetInt32());
                Assert.Equal(expectedSuccess, document.RootElement.GetProperty("Succeeded").GetBoolean());
                if (expectedSuccess)
                    Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("LastNativeExitAfterWorker").ValueKind);
                Assert.Equal(process.Id, int.Parse(await File.ReadAllTextAsync(pidFile)));
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
}
