namespace OpenClaw.Chat;

/// <summary>
/// Owns the per-thread state needed to reconcile a cleared thinking-level override
/// with response-correlated session snapshots. Callers remain responsible for
/// issuing gateway requests and storing the resulting session snapshots.
/// </summary>
public sealed class ThinkingLevelClearReconciler : IDisposable
{
    public const int DefaultMaxRefreshAttempts = 3;
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan DefaultConfirmationTimeout = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _confirmationTimeout;
    private readonly int _maxRefreshAttempts;

    private bool _connected;
    private bool _disposed;
    private long _connectionGeneration;
    private long _nextOperationVersion;
    private long _nextRefreshRequestId;

    public ThinkingLevelClearReconciler(
        bool connected,
        TimeSpan? confirmationTimeout = null,
        TimeSpan? retryDelay = null,
        int maxRefreshAttempts = DefaultMaxRefreshAttempts,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (maxRefreshAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRefreshAttempts));

        _connected = connected;
        _confirmationTimeout = confirmationTimeout ?? DefaultConfirmationTimeout;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
        _maxRefreshAttempts = maxRefreshAttempts;
        _delay = delay ?? Task.Delay;
    }

    public long ConnectionGeneration
    {
        get
        {
            lock (_gate)
                return _connectionGeneration;
        }
    }

    public ReconciliationState GetState(string threadId)
    {
        ValidateThreadId(threadId);
        lock (_gate)
        {
            if (_disposed)
                return ReconciliationState.Disposed;

            return _entries.TryGetValue(threadId, out var entry)
                ? entry.State
                : ReconciliationState.Idle;
        }
    }

    public ClearOperation BeginClear(string threadId, string? canonicalThinkingLevel)
    {
        ValidateThreadId(threadId);
        lock (_gate)
        {
            ThrowIfDisposed();

            var protectedThinkingLevel = canonicalThinkingLevel;
            if (_entries.Remove(threadId, out var existing))
            {
                protectedThinkingLevel = existing.ProtectedThinkingLevel;
                Supersede(existing);
            }

            var operation = new ClearOperation(
                threadId,
                ++_nextOperationVersion,
                _connectionGeneration);
            _entries[threadId] = new Entry(
                threadId,
                operation,
                operation.Version,
                operation.ConnectionGeneration,
                protectedThinkingLevel,
                ReconciliationState.AwaitingPatchAck);
            return operation;
        }
    }

    public ConcreteSelection BeginConcreteSelection(
        string threadId,
        string thinkingLevel,
        string? canonicalThinkingLevel)
    {
        ValidateThreadId(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(thinkingLevel);

        lock (_gate)
        {
            ThrowIfDisposed();

            var version = ++_nextOperationVersion;
            if (!_entries.Remove(threadId, out var existing))
            {
                return new ConcreteSelection(
                    threadId,
                    version,
                    _connectionGeneration,
                    canonicalThinkingLevel,
                    tracksReconciliation: false);
            }

            var previousProtectedThinkingLevel = existing.ProtectedThinkingLevel;
            Supersede(existing);
            _entries[threadId] = new Entry(
                threadId,
                clearOperation: null,
                version,
                _connectionGeneration,
                thinkingLevel,
                ReconciliationState.Superseded);
            return new ConcreteSelection(
                threadId,
                version,
                _connectionGeneration,
                previousProtectedThinkingLevel,
                tracksReconciliation: true);
        }
    }

    public void RejectConcreteSelection(ConcreteSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        lock (_gate)
        {
            if (_disposed ||
                !selection.TracksReconciliation ||
                !_entries.TryGetValue(selection.ThreadId, out var entry) ||
                entry.Version != selection.Version ||
                entry.ConnectionGeneration != selection.ConnectionGeneration)
            {
                return;
            }

            entry.ProtectedThinkingLevel = selection.PreviousProtectedThinkingLevel;
        }
    }

    public bool TryAcknowledgePatch(
        ClearOperation operation,
        out RefreshRequest refreshRequest)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            refreshRequest = default;
            if (_disposed ||
                !_connected ||
                !TryGetCurrentEntry(operation, out var entry) ||
                entry.State != ReconciliationState.AwaitingPatchAck ||
                entry.ConnectionGeneration != _connectionGeneration)
            {
                return false;
            }

            operation.MarkPatchAcknowledged();
            entry.ClearCommitted = true;
            Transition(entry, ReconciliationState.CommittedAwaitingCanonicalNull);
            refreshRequest = StartRefresh(entry, isRetry: false);
            return true;
        }
    }

    public bool RejectPatch(ClearOperation operation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            if (_disposed ||
                !TryGetCurrentEntry(operation, out var entry) ||
                entry.State != ReconciliationState.AwaitingPatchAck ||
                operation.PatchAcknowledged)
            {
                return false;
            }

            entry.ClearCommitted = false;
            entry.ActiveRefreshRequestId = 0;
            Transition(entry, ReconciliationState.Interrupted);
            operation.ConfirmationSource.TrySetException(exception);
            return true;
        }
    }

    public async Task<ClearOutcome> WaitForConfirmationAsync(
        ClearOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            await operation.Confirmation
                .WaitAsync(_confirmationTimeout, cancellationToken)
                .ConfigureAwait(false);
            return ClearOutcome.Confirmed;
        }
        catch (Exception ex)
        {
            bool committedAndCurrent;
            lock (_gate)
            {
                committedAndCurrent =
                    !_disposed &&
                    operation.PatchAcknowledged &&
                    _entries.TryGetValue(operation.ThreadId, out var entry) &&
                    ReferenceEquals(entry.ClearOperation, operation);
            }

            if (committedAndCurrent &&
                ex is OperationCanceledException or TimeoutException or ThinkingLevelClearInterruptedException)
            {
                return ClearOutcome.CommittedAwaitingCanonicalNull;
            }

            throw;
        }
    }

    public SnapshotResolution ObserveSnapshot(
        string threadId,
        string? incomingThinkingLevel)
    {
        ValidateThreadId(threadId);
        lock (_gate)
        {
            if (_disposed)
            {
                return new SnapshotResolution(
                    Accepted: false,
                    incomingThinkingLevel,
                    ProtectedCanonicalIntent: false,
                    RefreshRequest: null,
                    ReconciliationState.Disposed);
            }

            if (!_entries.TryGetValue(threadId, out var entry))
            {
                return new SnapshotResolution(
                    Accepted: true,
                    incomingThinkingLevel,
                    ProtectedCanonicalIntent: false,
                    RefreshRequest: null,
                    ReconciliationState.Idle);
            }

            if (string.Equals(
                    incomingThinkingLevel,
                    entry.ProtectedThinkingLevel,
                    StringComparison.Ordinal))
            {
                return Resolution(entry, incomingThinkingLevel, protectedIntent: false);
            }

            RefreshRequest? refresh = null;
            var awaitingPatchAck =
                entry.State == ReconciliationState.AwaitingPatchAck &&
                entry.ClearOperation is { PatchAcknowledged: false };
            if (_connected && !awaitingPatchAck && entry.ActiveRefreshRequestId == 0)
            {
                if (entry.RefreshAttempts >= _maxRefreshAttempts)
                    entry.RefreshAttempts = 0;
                refresh = StartRefresh(entry, isRetry: true);
            }

            return Resolution(
                entry,
                entry.ProtectedThinkingLevel,
                protectedIntent: true,
                refresh);
        }
    }

    public SnapshotResolution ApplyCorrelatedSnapshot(
        RefreshRequest request,
        string? incomingThinkingLevel)
    {
        lock (_gate)
        {
            if (_disposed ||
                !_entries.TryGetValue(request.ThreadId, out var entry) ||
                !IsCurrentRefresh(entry, request))
            {
                return new SnapshotResolution(
                    Accepted: false,
                    incomingThinkingLevel,
                    ProtectedCanonicalIntent: false,
                    RefreshRequest: null,
                    _disposed ? ReconciliationState.Disposed : ReconciliationState.Idle);
            }

            entry.ActiveRefreshRequestId = 0;
            if (entry.ClearCommitted)
            {
                if (incomingThinkingLevel is null)
                {
                    Transition(entry, ReconciliationState.Confirmed);
                    entry.ClearOperation?.ConfirmationSource.TrySetResult();
                    RemoveEntry(entry);
                    return new SnapshotResolution(
                        Accepted: true,
                        EffectiveThinkingLevel: null,
                        ProtectedCanonicalIntent: false,
                        RefreshRequest: null,
                        ReconciliationState.Confirmed);
                }

                RefreshRequest? retry = null;
                if (entry.RefreshAttempts < _maxRefreshAttempts)
                    retry = StartRefresh(entry, isRetry: true);
                else
                    Transition(entry, ReconciliationState.CommittedAwaitingCanonicalNull);

                return Resolution(
                    entry,
                    entry.ProtectedThinkingLevel,
                    protectedIntent: true,
                    retry);
            }

            Transition(entry, ReconciliationState.Confirmed);
            RemoveEntry(entry);
            return new SnapshotResolution(
                Accepted: true,
                incomingThinkingLevel,
                ProtectedCanonicalIntent: false,
                RefreshRequest: null,
                ReconciliationState.Confirmed);
        }
    }

    public async Task<RefreshRequest?> RetryAfterFailureAsync(
        RefreshRequest failedRequest,
        CancellationToken cancellationToken = default)
    {
        Entry entry;
        long retryVersion;
        CancellationToken lifetimeCancellation;
        lock (_gate)
        {
            if (_disposed ||
                !_entries.TryGetValue(failedRequest.ThreadId, out entry!) ||
                !IsCurrentRefresh(entry, failedRequest))
            {
                return null;
            }

            entry.ActiveRefreshRequestId = 0;
            if (!entry.ClearCommitted ||
                !_connected ||
                entry.RefreshAttempts >= _maxRefreshAttempts)
            {
                Transition(
                    entry,
                    entry.ClearCommitted
                        ? ReconciliationState.CommittedAwaitingCanonicalNull
                        : ReconciliationState.Interrupted);
                return null;
            }

            Transition(entry, ReconciliationState.RetryingRefresh);
            retryVersion = ++entry.RetryVersion;
            lifetimeCancellation = entry.LifetimeCancellation.Token;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeCancellation);
        try
        {
            await _delay(_retryDelay, linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        lock (_gate)
        {
            if (_disposed ||
                !_connected ||
                !_entries.TryGetValue(failedRequest.ThreadId, out var current) ||
                !ReferenceEquals(current, entry) ||
                current.RetryVersion != retryVersion ||
                current.ConnectionGeneration != _connectionGeneration ||
                current.ActiveRefreshRequestId != 0 ||
                !current.ClearCommitted ||
                current.RefreshAttempts >= _maxRefreshAttempts)
            {
                return null;
            }

            return StartRefresh(current, isRetry: true);
        }
    }

    /// <summary>
    /// Advances connection authority. A reconnect or client swap invalidates all
    /// earlier refresh requests and requests current-generation convergence for
    /// every protected thread.
    /// </summary>
    public IReadOnlyList<RefreshRequest> OnConnectionChanged(
        bool connected,
        bool clientChanged = false)
    {
        lock (_gate)
        {
            if (_disposed || (!clientChanged && connected == _connected))
                return Array.Empty<RefreshRequest>();

            _connected = connected;
            _connectionGeneration++;
            var refreshes = connected
                ? new List<RefreshRequest>(_entries.Count)
                : null;

            foreach (var entry in _entries.Values)
            {
                ReplaceLifetime(entry);
                entry.ActiveRefreshRequestId = 0;
                entry.RetryVersion++;
                Transition(entry, ReconciliationState.Interrupted);
                entry.ClearOperation?.ConfirmationSource.TrySetException(
                    new ThinkingLevelClearInterruptedException());

                if (!connected)
                    continue;

                entry.ConnectionGeneration = _connectionGeneration;
                entry.ClearCommitted = false;
                entry.RefreshAttempts = 0;
                refreshes!.Add(StartRefresh(entry, isRetry: true));
            }

            return refreshes is null
                ? Array.Empty<RefreshRequest>()
                : refreshes;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _connected = false;
            _connectionGeneration++;
            foreach (var entry in _entries.Values)
            {
                Transition(entry, ReconciliationState.Disposed);
                entry.ClearOperation?.ConfirmationSource.TrySetCanceled();
                CancelAndDispose(entry.LifetimeCancellation);
            }
            _entries.Clear();
        }
    }

    private bool TryGetCurrentEntry(ClearOperation operation, out Entry entry)
    {
        return _entries.TryGetValue(operation.ThreadId, out entry!) &&
               ReferenceEquals(entry.ClearOperation, operation) &&
               entry.Version == operation.Version &&
               entry.ConnectionGeneration == operation.ConnectionGeneration;
    }

    private RefreshRequest StartRefresh(Entry entry, bool isRetry)
    {
        entry.RefreshAttempts++;
        entry.ActiveRefreshRequestId = ++_nextRefreshRequestId;
        if (isRetry)
            Transition(entry, ReconciliationState.RetryingRefresh);

        return new RefreshRequest(
            entry.ThreadId,
            entry.Version,
            entry.ConnectionGeneration,
            entry.RefreshAttempts,
            entry.ActiveRefreshRequestId);
    }

    private static bool IsCurrentRefresh(Entry entry, RefreshRequest request)
    {
        return entry.Version == request.OperationVersion &&
               entry.ConnectionGeneration == request.ConnectionGeneration &&
               entry.ActiveRefreshRequestId == request.RequestId;
    }

    private static SnapshotResolution Resolution(
        Entry entry,
        string? effectiveThinkingLevel,
        bool protectedIntent,
        RefreshRequest? refreshRequest = null)
    {
        return new SnapshotResolution(
            Accepted: true,
            effectiveThinkingLevel,
            protectedIntent,
            refreshRequest,
            entry.State);
    }

    private void RemoveEntry(Entry entry)
    {
        _entries.Remove(entry.ThreadId);
        CancelAndDispose(entry.LifetimeCancellation);
    }

    private static void Supersede(Entry entry)
    {
        Transition(entry, ReconciliationState.Superseded);
        entry.ClearOperation?.ConfirmationSource.TrySetCanceled();
        CancelAndDispose(entry.LifetimeCancellation);
    }

    private static void ReplaceLifetime(Entry entry)
    {
        CancelAndDispose(entry.LifetimeCancellation);
        entry.LifetimeCancellation = new CancellationTokenSource();
    }

    private static void Transition(Entry entry, ReconciliationState state)
    {
        entry.State = state;
        if (entry.ClearOperation is { } operation && !operation.Confirmation.IsCompleted)
            operation.SetState(state);
    }

    private static void CancelAndDispose(CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void ValidateThreadId(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
    }

    private sealed class Entry
    {
        public Entry(
            string threadId,
            ClearOperation? clearOperation,
            long version,
            long connectionGeneration,
            string? protectedThinkingLevel,
            ReconciliationState state)
        {
            ThreadId = threadId;
            ClearOperation = clearOperation;
            Version = version;
            ConnectionGeneration = connectionGeneration;
            ProtectedThinkingLevel = protectedThinkingLevel;
            State = state;
        }

        public string ThreadId { get; }
        public ClearOperation? ClearOperation { get; }
        public long Version { get; }
        public long ConnectionGeneration { get; set; }
        public string? ProtectedThinkingLevel { get; set; }
        public ReconciliationState State { get; set; }
        public bool ClearCommitted { get; set; }
        public int RefreshAttempts { get; set; }
        public long ActiveRefreshRequestId { get; set; }
        public long RetryVersion { get; set; }
        public CancellationTokenSource LifetimeCancellation { get; set; } = new();
    }

    public enum ReconciliationState
    {
        Idle,
        AwaitingPatchAck,
        CommittedAwaitingCanonicalNull,
        RetryingRefresh,
        Confirmed,
        Superseded,
        Interrupted,
        Disposed,
    }

    public enum ClearOutcome
    {
        Confirmed,
        CommittedAwaitingCanonicalNull,
    }

    public sealed class ClearOperation
    {
        private int _state = (int)ReconciliationState.AwaitingPatchAck;
        private int _patchAcknowledged;

        internal ClearOperation(
            string threadId,
            long version,
            long connectionGeneration)
        {
            ThreadId = threadId;
            Version = version;
            ConnectionGeneration = connectionGeneration;
        }

        public string ThreadId { get; }
        public long Version { get; }
        public long ConnectionGeneration { get; }
        public ReconciliationState State => (ReconciliationState)Volatile.Read(ref _state);
        public bool PatchAcknowledged => Volatile.Read(ref _patchAcknowledged) != 0;
        public Task Confirmation => ConfirmationSource.Task;
        internal TaskCompletionSource ConfirmationSource { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void MarkPatchAcknowledged() =>
            Volatile.Write(ref _patchAcknowledged, 1);

        internal void SetState(ReconciliationState state) =>
            Volatile.Write(ref _state, (int)state);
    }

    public sealed class ConcreteSelection
    {
        internal ConcreteSelection(
            string threadId,
            long version,
            long connectionGeneration,
            string? previousProtectedThinkingLevel,
            bool tracksReconciliation)
        {
            ThreadId = threadId;
            Version = version;
            ConnectionGeneration = connectionGeneration;
            PreviousProtectedThinkingLevel = previousProtectedThinkingLevel;
            TracksReconciliation = tracksReconciliation;
        }

        public string ThreadId { get; }
        public long Version { get; }
        public long ConnectionGeneration { get; }
        public bool TracksReconciliation { get; }
        internal string? PreviousProtectedThinkingLevel { get; }
    }

    public readonly record struct RefreshRequest(
        string ThreadId,
        long OperationVersion,
        long ConnectionGeneration,
        int Attempt,
        long RequestId);

    public readonly record struct SnapshotResolution(
        bool Accepted,
        string? EffectiveThinkingLevel,
        bool ProtectedCanonicalIntent,
        RefreshRequest? RefreshRequest,
        ReconciliationState State);
}

public sealed class ThinkingLevelClearInterruptedException : InvalidOperationException
{
    public ThinkingLevelClearInterruptedException()
        : base("The thinking-level reconciliation was interrupted by a connection change.")
    {
    }
}
