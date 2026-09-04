using System.Diagnostics;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Wait for a short-lived setup probe, then drain leftover redirected
/// stdout. Synchronous ReadToEnd before WaitForExit never reaches the
/// timeout when the child hangs or a descendant holds the pipe open.
/// </summary>
internal static class BoundedProcessOutput
{
    internal const int DefaultTimeoutMs = 5_000;
    private const int MinDrainMs = 250;

    internal static (int ExitCode, string Output) Read(
        ProcessStartInfo startInfo,
        int timeoutMs = DefaultTimeoutMs)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        using var process = Process.Start(startInfo);
        if (process is null)
            return (-1, string.Empty);

        var stdoutTask = startInfo.RedirectStandardOutput
            ? process.StandardOutput.ReadToEndAsync()
            : Task.FromResult(string.Empty);
        var stderrTask = startInfo.RedirectStandardError
            ? process.StandardError.ReadToEndAsync()
            : null;

        var output = AwaitRedirectedOutput(process, stdoutTask, timeoutMs) ?? string.Empty;
        if (stderrTask is not null)
            ObserveQuietly(stderrTask);

        try
        {
            return (process.HasExited ? process.ExitCode : -1, output);
        }
        catch (InvalidOperationException)
        {
            return (-1, output);
        }
    }

    internal static string? AwaitRedirectedOutput(Process process, Task<string> readTask, int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(readTask);
        if (timeoutMs < 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs));

        var sw = Stopwatch.StartNew();
        if (!process.WaitForExit(timeoutMs))
        {
            TryKillTree(process);
            AbandonRead(process, readTask);
            return null;
        }

        var elapsedMs = (int)Math.Min(sw.ElapsedMilliseconds, timeoutMs);
        var drainBudgetMs = Math.Max(timeoutMs - elapsedMs, MinDrainMs);
        try
        {
            if (!readTask.Wait(drainBudgetMs))
            {
                TryKillTree(process);
                AbandonRead(process, readTask);
                return null;
            }
        }
        catch (AggregateException)
        {
            return null;
        }

        return readTask.Status == TaskStatus.RanToCompletion ? readTask.Result : null;
    }

    private static void TryKillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex)
        {
            Trace.WriteLine($"BoundedProcessOutput.TryKillTree: {ex.GetType().Name}: {ex.Message}");
        }

        try { process.WaitForExit(1_000); }
        catch (Exception ex)
        {
            Trace.WriteLine($"BoundedProcessOutput.WaitForExit: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void AbandonRead(Process process, Task readTask)
    {
        ObserveQuietly(readTask);
        try { process.StandardOutput.Dispose(); } catch { }
    }

    private static void ObserveQuietly(Task task) =>
        _ = task.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}

