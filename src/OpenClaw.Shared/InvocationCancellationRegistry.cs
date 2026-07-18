namespace OpenClaw.Shared;

internal enum InvocationCancellationResult
{
    NotFound,
    Cancelled,
    Pending,
    Ambiguous,
}

internal sealed class InvocationCancellationRegistry
{
    private readonly Dictionary<string, List<InvocationCancellation>> _active =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExpiringEntry> _pendingCancellations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExpiringEntry> _recentCompletions =
        new(StringComparer.Ordinal);
    private readonly object _registrationGate = new();
    private readonly bool _allowDuplicateIds;
    private readonly TimeSpan _pendingCancellationTtl;
    private readonly int _maxPendingCancellations;
    private readonly int _maxRecentCompletions;
    private readonly TimeProvider _timeProvider;
    private long _nextSequence;

    public InvocationCancellationRegistry(
        bool allowDuplicateIds = false,
        TimeSpan? pendingCancellationTtl = null,
        int maxPendingCancellations = 1_024,
        int maxRecentCompletions = 1_024,
        TimeProvider? timeProvider = null)
    {
        if (pendingCancellationTtl < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pendingCancellationTtl));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPendingCancellations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecentCompletions);

        _allowDuplicateIds = allowDuplicateIds;
        _pendingCancellationTtl = pendingCancellationTtl ?? TimeSpan.Zero;
        _maxPendingCancellations = maxPendingCancellations;
        _maxRecentCompletions = maxRecentCompletions;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal int PendingCancellationCount
    {
        get
        {
            lock (_registrationGate)
            {
                PruneExpired(_timeProvider.GetUtcNow());
                return _pendingCancellations.Count;
            }
        }
    }

    internal int RecentCompletionCount
    {
        get
        {
            lock (_registrationGate)
            {
                PruneExpired(_timeProvider.GetUtcNow());
                return _recentCompletions.Count;
            }
        }
    }

    public bool TryRegister(
        string requestId,
        CancellationToken transportToken,
        out InvocationCancellation? invocation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        var candidate = new InvocationCancellation(this, requestId, transportToken);
        var cancelAfterRegistration = false;
        lock (_registrationGate)
        {
            PruneExpired(_timeProvider.GetUtcNow());
            if (_active.TryGetValue(requestId, out var active))
            {
                if (!_allowDuplicateIds)
                {
                    invocation = null;
                }
                else
                {
                    active.Add(candidate);
                    invocation = candidate;
                }
            }
            else
            {
                _active.Add(requestId, [candidate]);
                invocation = candidate;
            }

            if (invocation != null)
            {
                _recentCompletions.Remove(requestId);
                cancelAfterRegistration = _pendingCancellations.Remove(requestId);
            }
        }

        if (invocation == null)
        {
            candidate.DisposeDetached();
            return false;
        }

        if (cancelAfterRegistration)
        {
            candidate.ApplyPendingCallerCancellation();
        }

        return true;
    }

    public bool TryCancel(string requestId) =>
        TryCancelOrRemember(requestId) == InvocationCancellationResult.Cancelled;

    public bool TryCancel(string requestId, out bool ambiguous)
    {
        var result = TryCancelOrRemember(requestId);
        ambiguous = result == InvocationCancellationResult.Ambiguous;
        return result == InvocationCancellationResult.Cancelled;
    }

    public InvocationCancellationResult TryCancelOrRemember(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return InvocationCancellationResult.NotFound;
        }

        InvocationCancellation? invocation;
        lock (_registrationGate)
        {
            var now = _timeProvider.GetUtcNow();
            PruneExpired(now);
            invocation = GetCancellationTarget(requestId, out var ambiguous);
            if (ambiguous)
            {
                return InvocationCancellationResult.Ambiguous;
            }

            if (invocation == null)
            {
                if (_pendingCancellationTtl == TimeSpan.Zero ||
                    _recentCompletions.ContainsKey(requestId))
                {
                    return InvocationCancellationResult.NotFound;
                }

                if (_pendingCancellations.ContainsKey(requestId))
                {
                    return InvocationCancellationResult.Pending;
                }

                AddBoundedEntry(
                    _pendingCancellations,
                    requestId,
                    now + _pendingCancellationTtl,
                    _maxPendingCancellations);
                return InvocationCancellationResult.Pending;
            }
        }

        return invocation.CancelByCaller()
            ? InvocationCancellationResult.Cancelled
            : InvocationCancellationResult.NotFound;
    }

    public void CancelAll()
    {
        InvocationCancellation[] active;
        lock (_registrationGate)
        {
            active = _active.Values.SelectMany(invocations => invocations).ToArray();
        }

        foreach (var invocation in active)
        {
            invocation.CancelFromTransport();
        }
    }

    private void Remove(string requestId, InvocationCancellation invocation)
    {
        lock (_registrationGate)
        {
            var now = _timeProvider.GetUtcNow();
            PruneExpired(now);
            if (!_active.TryGetValue(requestId, out var active))
            {
                return;
            }

            active.Remove(invocation);
            if (active.Count > 0)
            {
                return;
            }

            _active.Remove(requestId);
            _pendingCancellations.Remove(requestId);
            if (_pendingCancellationTtl > TimeSpan.Zero)
            {
                AddBoundedEntry(
                    _recentCompletions,
                    requestId,
                    now + _pendingCancellationTtl,
                    _maxRecentCompletions);
            }
        }
    }

    private void Complete(string requestId, InvocationCancellation invocation) =>
        Remove(requestId, invocation);

    private InvocationCancellation? GetCancellationTarget(
        string requestId,
        out bool ambiguous)
    {
        ambiguous = false;
        if (!_active.TryGetValue(requestId, out var active) || active.Count == 0)
        {
            return null;
        }

        if (active.Count > 1)
        {
            ambiguous = true;
            return null;
        }

        return active[0];
    }

    private void AddBoundedEntry(
        Dictionary<string, ExpiringEntry> entries,
        string requestId,
        DateTimeOffset expiresAt,
        int maxEntries)
    {
        if (!entries.ContainsKey(requestId) && entries.Count >= maxEntries)
        {
            var oldest = entries
                .OrderBy(entry => entry.Value.ExpiresAt)
                .ThenBy(entry => entry.Value.Sequence)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .First();
            entries.Remove(oldest.Key);
        }

        entries[requestId] = new ExpiringEntry(expiresAt, _nextSequence++);
    }

    private void PruneExpired(DateTimeOffset now)
    {
        PruneExpired(_pendingCancellations, now);
        PruneExpired(_recentCompletions, now);
    }

    private static void PruneExpired(
        Dictionary<string, ExpiringEntry> entries,
        DateTimeOffset now)
    {
        foreach (var requestId in entries
                     .Where(entry => entry.Value.ExpiresAt <= now)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            entries.Remove(requestId);
        }
    }

    private sealed record ExpiringEntry(DateTimeOffset ExpiresAt, long Sequence);

    internal sealed class InvocationCancellation : IDisposable
    {
        private readonly InvocationCancellationRegistry _owner;
        private readonly string _requestId;
        private readonly CancellationTokenSource _cts;
        private readonly object _gate = new();
        private bool _disposed;
        private bool _cancelledByCaller;
        private bool _completed;

        internal InvocationCancellation(
            InvocationCancellationRegistry owner,
            string requestId,
            CancellationToken transportToken)
        {
            _owner = owner;
            _requestId = requestId;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(transportToken);
        }

        public CancellationToken Token => _cts.Token;

        public bool CancelledByCaller
        {
            get
            {
                lock (_gate)
                {
                    return _cancelledByCaller;
                }
            }
        }

        internal bool CancelByCaller()
        {
            lock (_gate)
            {
                if (_disposed || _completed || _cts.IsCancellationRequested)
                {
                    return false;
                }

                _cancelledByCaller = true;
                _cts.Cancel();
                return true;
            }
        }

        internal void ApplyPendingCallerCancellation()
        {
            lock (_gate)
            {
                if (_disposed || _completed)
                {
                    return;
                }

                _cancelledByCaller = true;
                if (!_cts.IsCancellationRequested)
                {
                    _cts.Cancel();
                }
            }
        }

        public bool TryComplete()
        {
            lock (_gate)
            {
                if (_disposed || _completed || _cts.IsCancellationRequested)
                {
                    return false;
                }

                _completed = true;
                _owner.Complete(_requestId, this);
                return true;
            }
        }

        internal void CancelFromTransport()
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _cts.Cancel();
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _owner.Remove(_requestId, this);
                _cts.Dispose();
            }
        }

        internal void DisposeDetached()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _cts.Dispose();
            }
        }
    }
}
