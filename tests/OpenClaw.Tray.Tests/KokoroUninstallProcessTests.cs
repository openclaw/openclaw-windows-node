using System.Diagnostics;
using OpenClaw.Shared.Audio;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

// Exercises the shipped supervisor/worker protocol on Windows, not an Inno UI mock.
public sealed class KokoroUninstallProcessTests
{
    private const string FixtureHash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_MODEL_UNINSTALL_TEST_TIMEOUT_SECONDS"), out var seconds)
            && seconds > 0 ? seconds : 120);

    [Theory]
    [InlineData(0, false, false, 33, true)]
    [InlineData(1, true, false, 32, false)]
    [InlineData(1, true, true, 48, true)]
    [InlineData(1, false, false, 49, true)]
    public async Task Completion_PreservesEncodedSelectionAndPartialOutcomes(
        int selection, bool confirm, bool readOnly, int expectedCode, bool retained)
    {
        using var temp = new TempDirectory("kokoro-process-");
        Stage(temp);
        // Only catalog input hashes change. Shipped operation and supervisor source
        // execute unchanged against real files, without downloading hundreds of MB.
        var catalogPath = temp.Combine("KokoroDownloadCatalog.cs");
        var catalog = File.ReadAllText(catalogPath);
        foreach (var asset in KokoroDownloadCatalog.All) catalog = catalog.Replace(asset.Sha256, FixtureHash);
        File.WriteAllText(catalogPath, catalog);
        var model = temp.Combine("data", "models", "kokoro",
            KokoroDownloadCatalog.All[0].PackId, KokoroDownloadCatalog.All[0].FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        File.WriteAllText(model, "abc");
        File.WriteAllText(temp.Combine("data", "settings.json"), "keep");
        if (readOnly) File.SetAttributes(model, FileAttributes.ReadOnly);
        try
        {
            using var process = Start(temp, selection, confirm);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(Timeout); }
            finally { await StopIfRunning(process); }
            Assert.True(process.ExitCode == expectedCode, await stdout + await stderr);
            var report = File.ReadAllLines(temp.Combine("report.txt"));
            Assert.Equal(expectedCode.ToString(), report[0]);
            Assert.True(report.Length > 1);
            Assert.False(File.Exists(temp.Combine("report.txt.pending")));
            Assert.Equal(retained, File.Exists(model));
            Assert.Equal("keep", File.ReadAllText(temp.Combine("data", "settings.json")));
            Assert.Contains(confirm || selection == 0 ? model : temp.Combine("data", "models", "kokoro"), string.Join('\n', report));
            if (selection == 0) Assert.Contains("3 bytes", string.Join('\n', report));
            if (readOnly) Assert.Contains("incomplete", string.Join('\n', report));
        }
        finally { if (File.Exists(model)) File.SetAttributes(model, FileAttributes.Normal); }
    }

    [Fact]
    public async Task Cancellation_PublishesOnlyAfterTheRunningWorkerReleasesItsFile()
    {
        using var temp = new TempDirectory("kokoro-cancel-");
        Stage(temp);
        Directory.CreateDirectory(temp.Combine("data"));
        // Controlled external worker for the supervisor's cancellation boundary.
        // This is not file-cleanup or real-installer proof: the real-worker cases above
        // cover file operations. No test-only switches enter production code.
        File.WriteAllText(temp.Combine("KokoroUninstallFiles.cs"), """
            using System;
            using System.IO;
            using System.Threading;
            namespace OpenClaw.Shared.Audio {
                public static class KokoroUninstallFiles {
                    public static object Run(string root, int selection) {
                        using (var held = new FileStream(Path.Combine(root, "held"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                            File.WriteAllText(Path.Combine(root, "ready"), "ready");
                            // Self-bounded even if the supervisor regresses or its test crashes.
                            using (var release = new ManualResetEvent(false))
                                release.WaitOne(int.Parse(Environment.GetEnvironmentVariable("OPENCLAW_TEST_STALLED_WORKER_TIMEOUT_MS")));
                        }
                        return null;
                    }
                }
            }
            """);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(temp.Combine("data"), "ready");
        watcher.Created += (_, _) => ready.TrySetResult(true);
        watcher.EnableRaisingEvents = true;
        var receipt = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reports = new FileSystemWatcher(temp.Path, "report.txt");
        void ObserveReceipt()
        {
            try
            {
                using var released = new FileStream(temp.Combine("data", "held"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                receipt.TrySetResult(null);
            }
            catch (Exception error) { receipt.TrySetResult(error); }
        }
        reports.Created += (_, _) => ObserveReceipt();
        reports.Renamed += (_, _) => ObserveReceipt();
        reports.EnableRaisingEvents = true;
        using var process = Start(temp, 0, false);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            var exit = process.WaitForExitAsync();
            var first = await Task.WhenAny(ready.Task, exit).WaitAsync(Timeout);
            Assert.True(first == ready.Task, first == exit ? await stdout + await stderr : "Worker did not become ready.");
            Assert.False(File.Exists(temp.Combine("report.txt"))); // No premature completion.
            File.WriteAllText(temp.Combine("cancel.txt"), "cancel");
            // Check the lock when completion becomes observable, before awaiting
            // supervisor exit. An early published receipt must fail this assertion.
            Assert.Null(await receipt.Task.WaitAsync(Timeout));
            await exit.WaitAsync(Timeout);
            Assert.True(process.ExitCode == 49, await stdout + await stderr);
            var report = File.ReadAllLines(temp.Combine("report.txt"));
            Assert.Equal("49", report[0]);
            Assert.Contains("cancelled", string.Join('\n', report));
        }
        finally { await StopIfRunning(process); }
    }

    private static void Stage(TempDirectory temp)
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        File.Copy(Path.Combine(root, "scripts", "Uninstall-DownloadedModels.ps1"), temp.Combine("Uninstall-DownloadedModels.ps1"));
        foreach (var name in new[] { "KokoroDownloadCatalog.cs", "KokoroUninstallFiles.cs" })
            File.Copy(Path.Combine(root, "src", "OpenClaw.Shared", "Audio", name), temp.Combine(name));
    }

    private static Process Start(TempDirectory temp, int selection, bool confirm)
    {
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = temp.Path,
        };
        foreach (var argument in new[] {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", temp.Combine("Uninstall-DownloadedModels.ps1"),
            "-DataDirectory", temp.Combine("data"), "-ReportPath", temp.Combine("report.txt"),
            "-CancellationPath", temp.Combine("cancel.txt"), "-SelectionMask", selection.ToString(),
        }) start.ArgumentList.Add(argument);
        start.Environment["OPENCLAW_TEST_STALLED_WORKER_TIMEOUT_MS"] =
            checked((int)Timeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (confirm) start.ArgumentList.Add("-ConfirmDestructive");
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the model supervisor.");
    }

    private static async Task StopIfRunning(Process process)
    {
        if (process.HasExited) return;
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(Timeout);
    }
}
