namespace OpenClaw.Shared;

/// <summary>
/// Owns bounded sessions.list paging, search debounce, and connection-generation
/// cancellation. It publishes only complete accumulated snapshots.
/// </summary>
internal sealed class SessionQueryCoordinator : IDisposable
{
    public const int PageSize = 100;
    public const int MaximumPages = 20;
    public const int MaximumMaterializedSessions = 2000;
    public static readonly TimeSpan DefaultSearchDebounce = TimeSpan.FromMilliseconds(250);

    private readonly Func<SessionListRequest, CancellationToken, Task<SessionListResult>> _fetchPage;
    private readonly TimeSpan _searchDebounce;
    private readonly object _gate = new();
    private CancellationTokenSource _generationCancellation = new();
    private CancellationTokenSource? _recentCancellation;
    private CancellationTokenSource? _searchCancellation;
    private SessionQuerySnapshot? _recent;
    private RecentServerQueryIdentity? _recentQueryIdentity;
    private int _generation;
    private long _recentIdentity;
    private long _searchIdentity;
    private bool _disposed;

    public SessionQueryCoordinator(
        Func<SessionListRequest, CancellationToken, Task<SessionListResult>> fetchPage,
        TimeSpan? searchDebounce = null)
    {
        _fetchPage = fetchPage ?? throw new ArgumentNullException(nameof(fetchPage));
        _searchDebounce = searchDebounce ?? DefaultSearchDebounce;
    }

    public int ConnectionGeneration
    {
        get { lock (_gate) return _generation; }
    }

    public void AdvanceConnectionGeneration()
    {
        CancellationTokenSource oldGeneration;
        lock (_gate)
        {
            if (_disposed) return;
            _generation++;
            _recent = null;
            _recentQueryIdentity = null;
            _searchIdentity++;
            _recentIdentity++;
            oldGeneration = _generationCancellation;
            _generationCancellation = new CancellationTokenSource();
            _recentCancellation = null;
            _searchCancellation = null;
        }
        CancelAndDispose(oldGeneration);
    }

    public Task<SessionQuerySnapshot> LoadRecentAsync(
        SessionQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new SessionQuery();
        return LoadAndRememberRecentAsync(query, cancellationToken);
    }

    public async Task<SessionQuerySnapshot> SearchAsync(
        SessionQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalized = query.Search?.Trim();
        if (string.IsNullOrEmpty(normalized))
            return ClearSearch(query);

        CancellationTokenSource searchCancellation;
        CancellationToken generationToken;
        int generation;
        long identity;
        CancellationTokenSource? oldSearch;
        lock (_gate)
        {
            ThrowIfDisposed();
            generation = _generation;
            generationToken = _generationCancellation.Token;
            identity = ++_searchIdentity;
            oldSearch = _searchCancellation;
            searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                generationToken, cancellationToken);
            _searchCancellation = searchCancellation;
        }
        Cancel(oldSearch);

        try
        {
            await Task.Delay(_searchDebounce, searchCancellation.Token).ConfigureAwait(false);
            var normalizedQuery = new SessionQuery
            {
                AgentId = query.AgentId,
                Search = normalized,
                ConfiguredAgentsOnly = query.ConfiguredAgentsOnly,
                IncludeBackground = query.IncludeBackground,
                PinnedSessions = query.PinnedSessions,
            };
            var snapshot = await LoadCoreAsync(
                normalizedQuery, generation, 0, searchCancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                ThrowIfStale(generation, identity);
            }
            return snapshot;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_searchCancellation, searchCancellation))
                    _searchCancellation = null;
            }
            searchCancellation.Dispose();
        }
    }

    public SessionQuerySnapshot ClearSearch(SessionQuery? query = null)
    {
        query ??= new SessionQuery();
        CancellationTokenSource? oldSearch;
        SessionQuerySnapshot? recent;
        RecentServerQueryIdentity? recentQueryIdentity;
        int generation;
        lock (_gate)
        {
            ThrowIfDisposed();
            _searchIdentity++;
            oldSearch = _searchCancellation;
            _searchCancellation = null;
            recent = _recent;
            recentQueryIdentity = _recentQueryIdentity;
            generation = _generation;
        }
        Cancel(oldSearch);

        var requestedIdentity = RecentServerQueryIdentity.Create(query);
        if (recent is null || recentQueryIdentity != requestedIdentity)
        {
            return new SessionQuerySnapshot
            {
                ConnectionGeneration = generation,
                Sessions = PinAndFilter(Array.Empty<SessionInfo>(), query),
                MaterializedSessions = Array.Empty<SessionInfo>(),
            };
        }

        return new SessionQuerySnapshot
        {
            Search = recent.Search,
            ConnectionGeneration = recent.ConnectionGeneration,
            PagesRead = recent.PagesRead,
            IsLegacyResponse = recent.IsLegacyResponse,
            SearchExecutionMode = recent.SearchExecutionMode,
            Sessions = PinAndFilter(recent.MaterializedSessions, query),
            MaterializedSessions = recent.MaterializedSessions,
            RequestIdentity = recent.RequestIdentity,
        };
    }

    private async Task<SessionQuerySnapshot> LoadAndRememberRecentAsync(
        SessionQuery query,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource recentCancellation;
        CancellationTokenSource? oldRecent;
        int generation;
        long identity;
        lock (_gate)
        {
            ThrowIfDisposed();
            generation = _generation;
            identity = ++_recentIdentity;
            oldRecent = _recentCancellation;
            recentCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _generationCancellation.Token, cancellationToken);
            _recentCancellation = recentCancellation;
        }
        Cancel(oldRecent);
        try
        {
            var recentQuery = new SessionQuery
            {
                AgentId = query.AgentId,
                ConfiguredAgentsOnly = query.ConfiguredAgentsOnly,
                IncludeBackground = query.IncludeBackground,
                PinnedSessions = query.PinnedSessions,
            };
            var snapshot = await LoadCoreAsync(
                recentQuery, generation, identity, recentCancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_generation != generation || _recentIdentity != identity || _disposed)
                    throw new OperationCanceledException("Session query response is stale.");
                _recent = snapshot;
                _recentQueryIdentity = RecentServerQueryIdentity.Create(recentQuery);
            }
            return snapshot;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_recentCancellation, recentCancellation))
                    _recentCancellation = null;
            }
            recentCancellation.Dispose();
        }
    }

    private async Task<SessionQuerySnapshot> LoadCoreAsync(
        SessionQuery query,
        int generation,
        long requestIdentity,
        CancellationToken cancellationToken)
    {
        var materialized = new List<SessionInfo>(PageSize);
        var indicesByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenOffsets = new HashSet<int>();
        var offset = 0;
        var pagesRead = 0;
        var legacy = false;

        while (pagesRead < MaximumPages &&
               materialized.Count < MaximumMaterializedSessions &&
               seenOffsets.Add(offset))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _fetchPage(new SessionListRequest
            {
                AgentId = query.AgentId,
                Limit = PageSize,
                Offset = offset,
                Search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search,
                ConfiguredAgentsOnly = query.ConfiguredAgentsOnly ? true : null,
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCurrentGeneration(generation);

            pagesRead++;
            legacy |= page.IsLegacyResponse;
            foreach (var session in page.Sessions)
            {
                if (string.IsNullOrWhiteSpace(session.Key))
                    continue;
                if (indicesByKey.TryGetValue(session.Key, out var index))
                {
                    materialized[index] = session;
                }
                else if (materialized.Count < MaximumMaterializedSessions)
                {
                    indicesByKey[session.Key] = materialized.Count;
                    materialized.Add(session);
                }
            }

            if (page.IsLegacyResponse || materialized.Count >= MaximumMaterializedSessions)
                break;

            var nextOffset = ResolveNextOffset(page, offset);
            if (!nextOffset.HasValue || nextOffset.Value <= offset || seenOffsets.Contains(nextOffset.Value))
                break;
            offset = nextOffset.Value;
        }

        var all = materialized.Select(static session => session.Clone()).ToArray();
        var normalizedSearch = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var searchExecutionMode = normalizedSearch is null
            ? SessionSearchExecutionMode.None
            : legacy
                ? SessionSearchExecutionMode.LegacyLocal
                : SessionSearchExecutionMode.Server;
        IReadOnlyList<SessionInfo> resultRows = normalizedSearch is not null && legacy
            ? all.Where(session => MatchesLegacySearch(session, normalizedSearch)).ToArray()
            : all;
        return new SessionQuerySnapshot
        {
            Search = normalizedSearch,
            ConnectionGeneration = generation,
            PagesRead = pagesRead,
            IsLegacyResponse = legacy,
            SearchExecutionMode = searchExecutionMode,
            Sessions = PinAndFilter(resultRows, query),
            MaterializedSessions = resultRows,
            RequestIdentity = requestIdentity,
        };
    }

    internal bool TryApplyCurrentRecentSnapshot(
        SessionQuerySnapshot snapshot,
        Action<IReadOnlyList<SessionInfo>> apply)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(apply);
        lock (_gate)
        {
            if (_disposed ||
                snapshot.ConnectionGeneration != _generation ||
                snapshot.RequestIdentity != _recentIdentity ||
                !ReferenceEquals(snapshot, _recent))
            {
                return false;
            }
            apply(snapshot.MaterializedSessions);
            return true;
        }
    }

    private static int? ResolveNextOffset(SessionListResult page, int currentOffset)
    {
        if (page.HasMore == false)
            return null;
        if (page.NextOffset.HasValue)
            return page.NextOffset.Value;
        if (page.HasMore == true)
            return null;
        if (page.Sessions.Count < PageSize)
            return null;
        var next = (long)currentOffset + page.Sessions.Count;
        return next <= int.MaxValue ? (int)next : null;
    }

    private static IReadOnlyList<SessionInfo> PinAndFilter(
        IReadOnlyList<SessionInfo> sessions,
        SessionQuery query)
    {
        var result = new List<SessionInfo>(sessions.Count + query.PinnedSessions.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var pinsByKey = new Dictionary<string, SessionInfo>(StringComparer.Ordinal);
        var pinOrder = new List<string>(query.PinnedSessions.Count);
        foreach (var pinned in query.PinnedSessions)
        {
            if (string.IsNullOrWhiteSpace(pinned.Key))
                continue;
            if (!pinsByKey.ContainsKey(pinned.Key))
                pinOrder.Add(pinned.Key);
            pinsByKey[pinned.Key] = pinned;
        }

        foreach (var session in sessions)
        {
            if (pinsByKey.TryGetValue(session.Key, out var current))
            {
                if (keys.Add(session.Key))
                    result.Add(current.Clone());
                continue;
            }
            if (!query.IncludeBackground && SessionPresentationResolver.IsBackground(session))
                continue;
            if (keys.Add(session.Key))
                result.Add(session.Clone());
        }
        foreach (var key in pinOrder)
        {
            if (keys.Add(key))
                result.Add(pinsByKey[key].Clone());
        }
        return result;
    }

    private static bool MatchesLegacySearch(SessionInfo session, string search)
    {
        var presentation = SessionPresentationResolver.Resolve(session);
        return Contains(presentation.Title, search) ||
               Contains(presentation.Subtitle, search);
    }

    private static bool Contains(string? value, string search) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private readonly record struct RecentServerQueryIdentity(
        string? AgentId,
        bool ConfiguredAgentsOnly,
        bool IncludeBackground,
        int PageSize,
        int MaximumPages,
        int MaximumMaterializedSessions)
    {
        public static RecentServerQueryIdentity Create(SessionQuery query) => new(
            string.IsNullOrWhiteSpace(query.AgentId) ? null : query.AgentId,
            query.ConfiguredAgentsOnly,
            query.IncludeBackground,
            SessionQueryCoordinator.PageSize,
            SessionQueryCoordinator.MaximumPages,
            SessionQueryCoordinator.MaximumMaterializedSessions);
    }

    private void EnsureCurrentGeneration(int generation)
    {
        lock (_gate)
        {
            if (_disposed || _generation != generation)
                throw new OperationCanceledException("Session query belongs to a stale connection generation.");
        }
    }

    private void ThrowIfStale(int generation, long identity)
    {
        if (_disposed || _generation != generation || _searchIdentity != identity)
            throw new OperationCanceledException("Session search response is stale.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void CancelAndDispose(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        finally { cancellation.Dispose(); }
    }

    private static void Cancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException)
        {
            // The superseded query completed and disposed its own CTS first.
        }
    }

    public void Dispose()
    {
        CancellationTokenSource generation;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            generation = _generationCancellation;
            _recentCancellation = null;
            _searchCancellation = null;
        }
        CancelAndDispose(generation);
    }
}
