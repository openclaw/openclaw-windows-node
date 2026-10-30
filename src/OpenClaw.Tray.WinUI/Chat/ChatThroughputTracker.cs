using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Chat;

internal sealed record ChatThroughputStats(
    double? LatestTokensPerSecond,
    double? LiveTokensPerSecond,
    double? MeanTokensPerSecond,
    double? P95TokensPerSecond,
    int SampleCount)
{
    internal static ChatThroughputStats Empty { get; } = new(null, null, null, null, 0);
}

/// <summary>
/// Per-session model output throughput. Live deltas carry no token counts, so the live value is a
/// characters-per-token estimate over streamed reasoning and assistant text. Exact samples are
/// output tokens over the streaming window of one model call: from its first streamed text to the
/// gateway <c>usage</c> event that closes it (or, for gateways without that stream, to the final
/// assistant message). Only exact samples feed mean and p95.
/// Called from gateway threads and read on the UI thread, hence the lock.
/// </summary>
internal sealed class ChatThroughputTracker(TimeProvider timeProvider)
{
    internal const int MaxSamples = 50;
    internal const double CharactersPerToken = 4d;
    internal static readonly TimeSpan MinimumWindow = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private readonly Dictionary<string, ThreadState> _threads = new(StringComparer.Ordinal);

    /// <summary>Raised with the thread id after a change that can alter <see cref="Get"/>; outside the lock.</summary>
    public event Action<string>? Changed;

    /// <summary>
    /// Starts a run: closes any open segment and resets the run's cumulative token baseline to a
    /// known zero, so the first <c>usage</c> total of this run is attributable to its first call.
    /// </summary>
    public void StartRun(string threadId)
    {
        lock (_gate)
        {
            var state = GetOrCreateLocked(threadId);
            state.ClearSegment();
            state.RunOutputTokens = 0;
            state.RunBaselineKnown = true;
            state.RunReportsUsage = false;
        }
        Changed?.Invoke(threadId);
    }

    /// <summary>
    /// Ends a run (lifecycle end/error). The baseline becomes unknown so a later run whose start is
    /// missed cannot attribute its cumulative total to one call. A segment is closed only when the
    /// run reported usage; otherwise it stays open for the final-message fallback, which the gateway
    /// sends after lifecycle end.
    /// </summary>
    public void EndRun(string threadId)
    {
        lock (_gate)
        {
            if (!_threads.TryGetValue(threadId, out var state))
                return;
            state.RunBaselineKnown = false;
            if (state.RunReportsUsage)
                state.ClearSegment();
        }
        Changed?.Invoke(threadId);
    }

    /// <summary>
    /// Connection lost or re-established: run lifecycle events may have been missed for every
    /// thread, so close open segments and mark baselines unknown. The next <c>usage</c> total of a
    /// run in progress then only becomes the baseline instead of one outlier sample. Completed
    /// samples are kept.
    /// </summary>
    public void InvalidateRuns()
    {
        string[] threadIds;
        lock (_gate)
        {
            threadIds = [.. _threads.Keys];
            foreach (var state in _threads.Values)
            {
                state.ClearSegment();
                state.RunBaselineKnown = false;
            }
        }
        foreach (var threadId in threadIds)
            Changed?.Invoke(threadId);
    }

    /// <summary>Records streamed assistant or reasoning text; the first call opens the segment window.</summary>
    public void ObserveText(string threadId, int characters)
    {
        lock (_gate)
        {
            var state = GetOrCreateLocked(threadId);

            var now = timeProvider.GetTimestamp();
            if (state.SegmentStart is null)
            {
                state.SegmentStart = now;
                state.Characters = 0;
            }
            state.LastTextAt = now;
            state.Characters += Math.Max(0, characters);
        }
        if (characters > 0)
            Changed?.Invoke(threadId);
    }

    /// <summary>Closes the current segment without a sample (tool activity).</summary>
    public void ResetSegment(string threadId)
    {
        lock (_gate)
        {
            if (!_threads.TryGetValue(threadId, out var state) || state.SegmentStart is null)
                return;
            state.ClearSegment();
        }
        Changed?.Invoke(threadId);
    }

    /// <summary>
    /// Closes the segment at a completed model call. <paramref name="runOutputTokens"/> is the
    /// run-cumulative total, so the call's own tokens are the increase since the previous event.
    /// </summary>
    public bool ObserveRunUsage(string threadId, long runOutputTokens)
    {
        bool recorded;
        lock (_gate)
        {
            var state = GetOrCreateLocked(threadId);
            state.RunReportsUsage = true;
            if (!state.RunBaselineKnown || runOutputTokens < state.RunOutputTokens)
            {
                // Run start missed (provider created mid-run) or a new generation we did not see
                // start: the total spans unknown calls, so only adopt it as the baseline.
                state.RunOutputTokens = runOutputTokens;
                state.RunBaselineKnown = true;
                state.ClearSegment();
                recorded = false;
            }
            else
            {
                var callTokens = runOutputTokens - state.RunOutputTokens;
                state.RunOutputTokens = runOutputTokens;
                recorded = RecordSegmentLocked(state, callTokens);
            }
        }
        Changed?.Invoke(threadId);
        return recorded;
    }

    private ThreadState GetOrCreateLocked(string threadId)
    {
        if (!_threads.TryGetValue(threadId, out var state))
            _threads[threadId] = state = new ThreadState();
        return state;
    }

    /// <summary>
    /// Closes the segment at the final assistant message. Records a sample only when the run has not
    /// already reported per-call usage, so the same tokens are never counted twice.
    /// </summary>
    public bool CompleteAssistant(string threadId, int? outputTokens)
    {
        bool recorded;
        lock (_gate)
        {
            if (!_threads.TryGetValue(threadId, out var state))
                return false;

            recorded = state.RunReportsUsage
                ? RecordSegmentLocked(state, 0)
                : RecordSegmentLocked(state, outputTokens ?? 0);
        }
        Changed?.Invoke(threadId);
        return recorded;
    }

    private bool RecordSegmentLocked(ThreadState state, long outputTokens)
    {
        var start = state.SegmentStart;
        state.ClearSegment();
        if (start is null || outputTokens <= 0)
            return false;

        var window = timeProvider.GetElapsedTime(start.Value, timeProvider.GetTimestamp());
        if (window < MinimumWindow)
            return false;

        state.Samples.Enqueue(outputTokens / window.TotalSeconds);
        while (state.Samples.Count > MaxSamples)
            state.Samples.Dequeue();
        return true;
    }

    public ChatThroughputStats Get(string threadId)
    {
        lock (_gate)
        {
            if (!_threads.TryGetValue(threadId, out var state))
                return ChatThroughputStats.Empty;

            double? live = null;
            if (state.SegmentStart is { } start && state.Characters > 0)
            {
                var elapsed = timeProvider.GetElapsedTime(start, state.LastTextAt);
                if (elapsed >= MinimumWindow)
                    live = state.Characters / CharactersPerToken / elapsed.TotalSeconds;
            }

            var count = state.Samples.Count;
            if (count == 0)
                return new ChatThroughputStats(null, live, null, null, 0);

            var sorted = state.Samples.ToArray();
            var latest = sorted[^1];
            var mean = sorted.Average();
            Array.Sort(sorted);
            var p95 = sorted[(int)Math.Ceiling(0.95 * count) - 1];
            return new ChatThroughputStats(latest, live, mean, p95, count);
        }
    }

    private sealed class ThreadState
    {
        public long? SegmentStart;
        public long LastTextAt;
        public long Characters;
        public long RunOutputTokens;
        public bool RunReportsUsage;
        public bool RunBaselineKnown;
        public readonly Queue<double> Samples = new();

        public void ClearSegment()
        {
            SegmentStart = null;
            Characters = 0;
        }
    }
}
