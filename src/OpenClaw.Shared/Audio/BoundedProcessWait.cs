using System.Diagnostics;

namespace OpenClaw.Shared.Audio;

/// <summary>
/// Wait for a short-lived child with a budget, drain redirected stderr
/// asynchronously, and kill the process tree on timeout or cancel.
/// Synchronous WaitForExit with stderr redirected and unread can hang
/// when the child never exits or fills the pipe.
/// </summary>
internal static class BoundedProcessWait
{
    internal const int DefaultTimeoutMs = 120_000;
    private const int MinDrainMs = 250;
    private const int KillWaitMs = 1_000;

    internal static (int ExitCode, string StandardError) Wait(
        Process process,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutMs);

        var stderrTask = TryStartStderrDrain(process);
        using var reg = cancellationToken.Register(() => TryKillTree(process));
        if (cancellationToken.IsCancellationRequested)
        {
            TryKillTree(process);
            AbandonRead(process, stderrTask);
            cancellationToken.ThrowIfCancellationRequested();
        }

        var sw = Stopwatch.StartNew();
        if (!process.WaitForExit(timeoutMs))
        {
            TryKillTree(process);
            AbandonRead(process, stderrTask);
            throw new TimeoutException($"Process did not exit within {timeoutMs}ms.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            AbandonRead(process, stderrTask);
            cancellationToken.ThrowIfCancellationRequested();
        }

        var elapsedMs = (int)Math.Min(sw.ElapsedMilliseconds, timeoutMs);
        var drainBudgetMs = Math.Max(timeoutMs - elapsedMs, MinDrainMs);
        var stderr = DrainStderr(stderrTask, process, drainBudgetMs);

        try
        {
            return (process.HasExited ? process.ExitCode : -1, stderr);
        }
        catch (InvalidOperationException)
        {
            return (-1, stderr);
        }
    }

    private static Task<string>? TryStartStderrDrain(Process process)
    {
        try
        {
            if (process.StartInfo.RedirectStandardError)
                return process.StandardError.ReadToEndAsync();
        }
        catch (InvalidOperationException)
        {
        }

        return null;
    }

    private static string DrainStderr(Task<string>? stderrTask, Process process, int drainBudgetMs)
    {
        if (stderrTask is null)
            return string.Empty;

        try
        {
            if (!stderrTask.Wait(drainBudgetMs))
            {
                TryKillTree(process);
                AbandonRead(process, stderrTask);
                return string.Empty;
            }

            return stderrTask.Status == TaskStatus.RanToCompletion
                ? stderrTask.Result
                : string.Empty;
        }
        catch (AggregateException)
        {
            return string.Empty;
        }
    }

    private static void TryKillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex)
        {
            Trace.WriteLine($"BoundedProcessWait.TryKillTree: {ex.GetType().Name}: {ex.Message}");
        }

        try { process.WaitForExit(KillWaitMs); }
        catch (Exception ex)
        {
            Trace.WriteLine($"BoundedProcessWait.WaitForExit: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void AbandonRead(Process process, Task? readTask)
    {
        if (readTask is not null)
        {
            _ = readTask.ContinueWith(
                static t => { _ = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        try { process.StandardError.Dispose(); }
        catch (Exception ex)
        {
            Trace.WriteLine($"BoundedProcessWait.AbandonRead: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
