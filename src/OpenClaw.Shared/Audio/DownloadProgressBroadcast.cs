using System;
using System.Collections.Generic;

namespace OpenClaw.Shared.Audio;

/// <summary>
/// Fans one shared download's progress out to every caller waiting on it.
/// <see cref="SingleFlightDownload"/> starts the transfer with the first caller's
/// factory, so without this a caller that joins later (Voice Settings joining a
/// background prefetch) would never see progress. A subscriber receives the latest
/// report immediately so a late joiner does not start at zero.
/// </summary>
internal sealed class DownloadProgressBroadcast : IProgress<(long downloaded, long total)>
{
    private readonly object _gate = new();
    private readonly List<IProgress<(long downloaded, long total)>> _subscribers = [];
    private (long downloaded, long total)? _latest;

    public void Subscribe(IProgress<(long downloaded, long total)> subscriber)
    {
        (long, long)? latest;
        lock (_gate)
        {
            _subscribers.Add(subscriber);
            latest = _latest;
        }
        if (latest is { } value) subscriber.Report(value);
    }

    public void Unsubscribe(IProgress<(long downloaded, long total)> subscriber)
    {
        lock (_gate) _subscribers.Remove(subscriber);
    }

    /// <summary>Forget the previous transfer's progress before a new one starts.</summary>
    public void Reset()
    {
        lock (_gate) _latest = null;
    }

    public void Report((long downloaded, long total) value)
    {
        IProgress<(long downloaded, long total)>[] subscribers;
        lock (_gate)
        {
            _latest = value;
            subscribers = _subscribers.ToArray();
        }
        foreach (var subscriber in subscribers) subscriber.Report(value);
    }
}
