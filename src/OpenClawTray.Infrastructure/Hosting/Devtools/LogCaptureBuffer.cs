using System.Text.RegularExpressions;

namespace OpenClawTray.Infrastructure.Hosting.Devtools;

internal enum LogSource
{
    Stdout,
    Stderr,
    Debug,
}

/// <summary>
/// One captured log line. Monotonic <see cref="Seq"/> lets agents poll with
/// <c>since</c> cursors instead of re-reading the whole buffer.
/// </summary>
internal sealed record LogEntry(
    long Seq,
    DateTime TimestampUtc,
    LogSource Source,
    string? Level,
    int ThreadId,
    string Text);

/// <summary>
/// Bounded ring buffer for captured app logs. Thread-safe, zero-lock on reads
/// of the drop counter. Drops oldest entries when the byte budget is exceeded
/// so a chatty app doesn't starve the host. Appends wake any waiter blocked in
/// <see cref="WaitForNewAsync"/> so the <c>reactor.logs</c> tool can long-poll.
/// </summary>
internal sealed class LogCaptureBuffer
{
    private readonly object _lock = new();
    private readonly LinkedList<LogEntry> _entries = new();
    private long _nextSeq = 1;
    private long _dropped;
    private long _approxBytes;
    private readonly long _capacityBytes;
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Default cap per spec: ~4 MB of text content.</summary>
    public const long DefaultCapacityBytes = 4L * 1024 * 1024;

    public LogCaptureBuffer(long capacityBytes = DefaultCapacityBytes)
    {
        if (capacityBytes <= 0) throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        _capacityBytes = capacityBytes;
    }

    public long CapacityBytes => _capacityBytes;
    public long Dropped => Volatile.Read(ref _dropped);

    /// <summary>
    /// Append a line. <paramref name="text"/> is truncated if absurdly long
    /// (single entry above 1 MB) so one pathological write can't evict
    /// everything else.
    /// </summary>
    public void Append(LogSource source, string? level, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Cap single-entry size so one rogue dump doesn't flush the whole ring.
        // Char count, not bytes — UTF-16 in-memory is 2 bytes/char, so this
        // caps a single entry at ~2 MB of storage.
        const int MaxEntryChars = 1 << 20;
        if (text.Length > MaxEntryChars) text = text[..MaxEntryChars];

        var entry = new LogEntry(
            Seq: 0, // replaced under lock
            TimestampUtc: DateTime.UtcNow,
            Source: source,
            Level: level,
            ThreadId: Environment.CurrentManagedThreadId,
            Text: text);

        TaskCompletionSource? toSignal = null;
        lock (_lock)
        {
            var seq = _nextSeq++;
            entry = entry with { Seq = seq };
            var entryBytes = ApproxBytes(entry);
            _entries.AddLast(entry);
            _approxBytes += entryBytes;

            while (_approxBytes > _capacityBytes && _entries.First is { } head)
            {
                _approxBytes -= ApproxBytes(head.Value);
                _entries.RemoveFirst();
                _dropped++;
            }

            // Swap signal *inside* the lock so no append races with a waiter
            // that captures the old TCS between our complete and swap.
            toSignal = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        toSignal?.TrySetResult();
    }

    /// <summary>
    /// Snapshot entries matching the filters. <paramref name="sinceSeq"/> is
    /// inclusive — pass the previous call's <c>nextSeq</c> directly to continue.
    /// <c>nextSeq</c> in the result is the value to pass on the next call.
    /// </summary>
    public LogQueryResult Query(
        long sinceSeq = 0,
        int? tail = null,
        string? filterRegex = null,
        LogSource? source = null,
        string? level = null)
    {
        Regex? re = null;
        if (!string.IsNullOrEmpty(filterRegex))
        {
            try { re = new Regex(filterRegex, RegexOptions.CultureInvariant); }
            catch (ArgumentException)
            {
                // Invalid regex becomes a literal substring match — friendlier
                // than a tool error when the user is iterating on a pattern.
                re = null;
            }
        }

        LogEntry[] matched;
        long dropped;
        long nextSeq;
        lock (_lock)
        {
            dropped = _dropped;
            nextSeq = _nextSeq;
            var buf = new List<LogEntry>(Math.Min(_entries.Count, 1024));
            foreach (var e in _entries)
            {
                if (e.Seq < sinceSeq) continue;
                if (source is { } s && e.Source != s) continue;
                if (!string.IsNullOrEmpty(level) && !string.Equals(e.Level, level, StringComparison.OrdinalIgnoreCase)) continue;
                if (re is not null && !re.IsMatch(e.Text)) continue;
                if (filterRegex is not null && re is null && !e.Text.Contains(filterRegex, StringComparison.Ordinal)) continue;
                buf.Add(e);
            }
            if (tail is { } t && t > 0 && buf.Count > t)
                buf.RemoveRange(0, buf.Count - t);
            matched = buf.ToArray();
        }
        return new LogQueryResult(matched, nextSeq, dropped);
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for an entry with seq &gt;=
    /// <paramref name="sinceSeq"/>. Returns immediately if one already exists.
    /// Long-poll primitive for the <c>reactor.logs</c> tool.
    /// </summary>
    public async Task WaitForNewAsync(long sinceSeq, int timeoutMs, CancellationToken ct = default)
    {
        Task wait;
        lock (_lock)
        {
            if (_nextSeq - 1 >= sinceSeq) return;
            wait = _signal.Task;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try { await wait.WaitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* timeout is a normal exit */ }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _approxBytes = 0;
            // Keep _nextSeq and _dropped monotonic across clears.
        }
    }

    // Rough byte accounting: 2 bytes per char (UTF-16 in-memory) + a fixed
    // per-entry overhead for the other fields. Cheap and deterministic;
    // exactness doesn't matter — the cap is a soft ceiling.
    private static int ApproxBytes(LogEntry e) => 64 + (e.Text.Length * 2);
}

internal sealed record LogQueryResult(
    IReadOnlyList<LogEntry> Entries,
    long NextSeq,
    long Dropped);
