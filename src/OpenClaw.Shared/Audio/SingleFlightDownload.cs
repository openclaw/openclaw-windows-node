using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Audio;

internal static class SingleFlightDownload
{
    public static Task RunAsync(
        ConcurrentDictionary<string, Lazy<Task>> inFlight,
        string key,
        Func<CancellationToken, Task> startDownload,
        CancellationToken waitCancellationToken = default)
    {
        var candidate = new Lazy<Task>(() =>
        {
            try
            {
                return startDownload(CancellationToken.None)
                    ?? Task.FromException(new InvalidOperationException("Download factory returned null."));
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        var lazy = inFlight.GetOrAdd(key, candidate);
        Task task;
        try
        {
            task = lazy.Value;
        }
        catch
        {
            inFlight.TryRemove(new KeyValuePair<string, Lazy<Task>>(key, lazy));
            throw;
        }

        _ = task.ContinueWith(
            _ => inFlight.TryRemove(new KeyValuePair<string, Lazy<Task>>(key, lazy)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return waitCancellationToken.CanBeCanceled
            ? task.WaitAsync(waitCancellationToken)
            : task;
    }
}
