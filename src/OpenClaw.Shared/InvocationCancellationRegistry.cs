namespace OpenClaw.Shared;

internal sealed class InvocationCancellationRegistry
{
    private readonly Dictionary<string, List<InvocationCancellation>> _active =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CancellationProbe>> _cancellationProbes =
        new(StringComparer.Ordinal);
    private readonly object _registrationGate = new();
    private readonly bool _allowDuplicateIds;

    public InvocationCancellationRegistry(bool allowDuplicateIds = false)
        => _allowDuplicateIds = allowDuplicateIds;

    public bool TryRegister(
        string requestId,
        CancellationToken transportToken,
        out InvocationCancellation? invocation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        var candidate = new InvocationCancellation(this, requestId, transportToken);
        lock (_registrationGate)
        {
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

            if (invocation != null &&
                _cancellationProbes.TryGetValue(requestId, out var probes))
            {
                foreach (var probe in probes)
                {
                    probe.RegistrationCount++;
                    probe.Candidate ??= candidate;
                }
            }
        }

        if (invocation != null)
        {
            return true;
        }

        candidate.DisposeDetached();
        return false;
    }

    public bool TryCancel(string requestId) =>
        TryCancel(requestId, out _);

    public bool TryCancel(string requestId, out bool ambiguous)
    {
        ambiguous = false;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return false;
        }

        InvocationCancellation? invocation;
        lock (_registrationGate)
        {
            invocation = GetCancellationTarget(requestId, out ambiguous);
        }

        return invocation?.CancelByCaller() == true;
    }

    public async Task<(bool Cancelled, bool Ambiguous)> TryCancelAfterRegistrationWindowAsync(
        string requestId,
        TimeSpan registrationWindow,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentOutOfRangeException.ThrowIfLessThan(registrationWindow, TimeSpan.Zero);

        CancellationProbe? probe = null;
        InvocationCancellation? invocation;
        bool ambiguous;
        lock (_registrationGate)
        {
            invocation = GetCancellationTarget(requestId, out ambiguous);
            if (invocation == null && !ambiguous)
            {
                probe = new CancellationProbe();
                if (!_cancellationProbes.TryGetValue(requestId, out var probes))
                {
                    probes = [];
                    _cancellationProbes.Add(requestId, probes);
                }

                probes.Add(probe);
            }
        }

        if (invocation != null)
        {
            return (invocation.CancelByCaller(), false);
        }

        if (ambiguous)
        {
            return (false, true);
        }

        try
        {
            await Task.Delay(registrationWindow, cancellationToken);
        }
        finally
        {
            lock (_registrationGate)
            {
                if (_cancellationProbes.TryGetValue(requestId, out var probes))
                {
                    probes.Remove(probe!);
                    if (probes.Count == 0)
                    {
                        _cancellationProbes.Remove(requestId);
                    }
                }
            }
        }

        if (probe!.RegistrationCount > 1)
        {
            return (false, true);
        }

        return probe.Candidate == null
            ? (false, false)
            : (probe.Candidate.CancelByCaller(), false);
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
            if (!_active.TryGetValue(requestId, out var active))
            {
                return;
            }

            active.Remove(invocation);
            if (active.Count == 0)
            {
                _active.Remove(requestId);
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

    private sealed class CancellationProbe
    {
        public int RegistrationCount { get; set; }
        public InvocationCancellation? Candidate { get; set; }
    }

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
