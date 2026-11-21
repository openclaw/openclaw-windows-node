using System.Collections.Immutable;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.Shared.Dashboards;
using OpenClawTray.Services;

namespace OpenClawTray.Dashboards;

internal enum BoardWidgetTicketFailure
{
    RefreshUnavailable,
    RepeatedRejection,
}

internal enum BoardProgressLoadStatus
{
    Loading,
    Ready,
    Empty,
    Unavailable,
    Denied,
    Error,
}

/// <summary>
/// One progress widget's view. <paramref name="Stale"/> marks a retained card whose
/// latest refresh failed transiently; denied and unsupported reads never keep a card.
/// </summary>
internal sealed record BoardProgressViewState(BoardProgressLoadStatus Status, BoardProgressCard? Card, bool Stale = false);

internal sealed record BoardDocumentSnapshot(
    DashboardLoadStatus Status,
    BoardSnapshot? Board,
    string? Error,
    string ActiveTabId,
    ImmutableHashSet<string> VisitedTabs,
    ImmutableDictionary<string, BoardWidgetTicketFailure> TicketFailures,
    ImmutableDictionary<string, BoardProgressViewState> ProgressCards,
    ImmutableArray<PluginWidgetKind> PluginWidgetKinds,
    Uri? GatewayOrigin)
{
    public static BoardDocumentSnapshot Initial { get; } = new(
        DashboardLoadStatus.Loading, null, null, "", [], [], [], [], null);
}

/// <summary>
/// Owns one session board: loads <c>board.get</c>, follows
/// <c>board.changed</c> and <c>board.command</c>, renews widget view tickets
/// before they expire, and refreshes when a widget document is unauthorized.
/// Tab selection and visited tabs (whose widgets stay mounted) live here so
/// the view only renders snapshots.
/// </summary>
internal sealed class BoardDocumentModel : IDisposable
{
    /// <summary>Consecutive unauthorized documents before a widget shows an authorization error.</summary>
    public const int MaxUnauthorizedRefreshes = 3;

    private static readonly TimeSpan s_minimumRenewal = TimeSpan.FromSeconds(5);

    private readonly IBoardGatewayClient _client;
    private readonly BoardSessionTarget _target;
    private readonly Action<Action> _post;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly Dictionary<string, int> _unauthorizedCounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _waitingForTicket = new(StringComparer.Ordinal);
    // Keyed by the normalized Gateway scope key, so aliases of one session share one read.
    private readonly Dictionary<string, ProgressRead> _progressReads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _widgetProgressTargets = new(StringComparer.Ordinal);
    private readonly object _loadGate = new();
    private BoardSessionDefaults? _sessionDefaults;
    private IDisposable? _renewal;
    private int _generation;
    private int _refreshQueued;
    private bool _loading;
    private bool _reloadPending;
    private Task _loadTask = Task.CompletedTask;
    private (string TabId, int AfterGeneration)? _pendingFocus;
    private bool _disposed;

    private sealed class ProgressRead(string key, ProgressCardTarget target)
    {
        public string Key { get; } = key;
        public ProgressCardTarget Target { get; } = target;
        public BoardProgressViewState State { get; set; } = new(BoardProgressLoadStatus.Loading, null);
        public bool HasAttempted { get; set; }
        public bool InFlight { get; set; }
        public bool RefreshPending { get; set; }
        /// <summary>A change was announced that no successful read has applied yet.</summary>
        public bool Dirty { get; set; }
    }

    private enum ProgressReadFailure
    {
        Transient,
        Unavailable,
        Denied,
    }

    public BoardDocumentModel(
        IBoardGatewayClient client,
        BoardSessionTarget target,
        Action<Action> post,
        Func<TimeSpan, Action, IDisposable> schedule,
        BoardDocumentSnapshot? previousView = null)
    {
        _client = client;
        _target = target;
        _post = post;
        _schedule = schedule;
        if (previousView is not null)
            Snapshot = BoardDocumentSnapshot.Initial with
            {
                ActiveTabId = previousView.ActiveTabId,
                VisitedTabs = previousView.VisitedTabs,
            };
        _client.BoardChanged += OnBoardChanged;
        _client.BoardCommandReceived += OnBoardCommand;
        _client.ProgressCardChanged += OnProgressCardChanged;
        _client.SessionListChanged += OnSessionListChanged;
    }

    public BoardDocumentSnapshot Snapshot { get; private set; } = BoardDocumentSnapshot.Initial;

    public event Action<BoardDocumentSnapshot>? Changed;

    /// <summary>
    /// Loads the board. Only one <c>board.get</c> runs at a time: a request made
    /// while one is running reruns once after it, so bursts of changes coalesce
    /// and a fast event stream can never keep every response stale.
    /// </summary>
    public Task LoadAsync()
    {
        lock (_loadGate)
        {
            if (_loading)
            {
                _reloadPending = true;
                return _loadTask;
            }
            _loading = true;
            return _loadTask = RunLoadsAsync();
        }
    }

    public Task RefreshAsync()
    {
        RefreshProgressCards();
        return LoadAsync();
    }

    private async Task RunLoadsAsync()
    {
        while (true)
        {
            await LoadOnceAsync().ConfigureAwait(false);
            lock (_loadGate)
            {
                if (!_reloadPending || _disposed)
                {
                    _loading = false;
                    return;
                }
                _reloadPending = false;
            }
        }
    }

    private async Task LoadOnceAsync()
    {
        var generation = Interlocked.Increment(ref _generation);
        if (!_client.IsConnectedToGateway)
        {
            await Apply(generation, current => current with { Status = DashboardLoadStatus.Disconnected }).ConfigureAwait(false);
            return;
        }
        if (!BoardAccessPolicy.CanRead(_client))
        {
            await Apply(generation, current => current with { Status = DashboardLoadStatus.Unavailable, Board = null }).ConfigureAwait(false);
            return;
        }
        try
        {
            var board = await _client.GetBoardAsync(_target).ConfigureAwait(false);
            var tabIds = board.Tabs.Select(tab => tab.TabId).ToHashSet(StringComparer.Ordinal);
            if (board.Widgets.Any(widget => !tabIds.Contains(widget.TabId)))
                throw new InvalidOperationException("The Gateway returned a board with widgets referencing missing tabs.");
            var kinds = _client.PluginWidgetKinds;
            var origin = _client.GatewayHttpOrigin;
            await Apply(
                generation,
                current => ApplyPendingFocus(AcceptTickets(current, board, kinds, origin), generation),
                () => ScheduleRenewal(board)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Apply(generation, current => current with
            {
                // A failed refresh keeps the rendered board; only a first load shows the error state.
                Status = current.Board is null ? DashboardLoadStatus.Error : current.Status,
                Error = ex.Message,
                // Widgets waiting on this refresh for a renewed ticket won't get one; show their error with Retry.
                TicketFailures = current.TicketFailures.SetItems(_waitingForTicket.Select(name =>
                    KeyValuePair.Create(name, BoardWidgetTicketFailure.RefreshUnavailable))),
            }).ConfigureAwait(false);
        }
    }

    public void SelectTab(string tabId)
    {
        var current = Snapshot;
        if (current.Board is null || !HasTab(current.Board, tabId) || current.ActiveTabId == tabId)
            return;
        Publish(current with { ActiveTabId = tabId, VisitedTabs = current.VisitedTabs.Add(tabId) });
    }

    /// <summary>
    /// A widget's ticket was rejected. Refetch the board for a new ticket; after
    /// <see cref="MaxUnauthorizedRefreshes"/> consecutive failures the widget shows an error.
    /// </summary>
    public void ReportWidgetUnauthorized(string name)
    {
        _unauthorizedCounts.TryGetValue(name, out var count);
        _unauthorizedCounts[name] = ++count;
        if (count >= MaxUnauthorizedRefreshes)
        {
            _waitingForTicket.Remove(name);
            Publish(Snapshot with
            {
                TicketFailures = Snapshot.TicketFailures.SetItem(name, BoardWidgetTicketFailure.RepeatedRejection),
            });
            return;
        }
        _waitingForTicket.Add(name);
        QueueRefresh();
    }

    public void ReportWidgetReady(string name)
    {
        _unauthorizedCounts.Remove(name);
        _waitingForTicket.Remove(name);
        if (Snapshot.TicketFailures.ContainsKey(name))
            Publish(Snapshot with { TicketFailures = Snapshot.TicketFailures.Remove(name) });
    }

    /// <summary>Clears a widget's authorization failure and refetches its ticket.</summary>
    public void RetryWidget(string name)
    {
        _unauthorizedCounts.Remove(name);
        _waitingForTicket.Remove(name);
        Publish(Snapshot with { TicketFailures = Snapshot.TicketFailures.Remove(name) });
        QueueRefresh();
    }

    public void RetryProgress(string widgetName)
    {
        if (!_widgetProgressTargets.TryGetValue(widgetName, out var key) ||
            !_progressReads.TryGetValue(key, out var read))
            return;
        read.HasAttempted = false;
        if (read.State.Card is null)
            read.State = new(BoardProgressLoadStatus.Loading, null);
        RequestProgressRead(read);
        Publish(Snapshot);
    }

    private BoardDocumentSnapshot AcceptTickets(
        BoardDocumentSnapshot current,
        BoardSnapshot board,
        ImmutableArray<PluginWidgetKind> kinds,
        Uri? gatewayOrigin)
    {
        var failures = current.TicketFailures;
        foreach (var name in _waitingForTicket.ToArray())
        {
            var widget = board.Widgets.FirstOrDefault(widget => widget.Name == name);
            var previous = current.Board?.Widgets.FirstOrDefault(widget => widget.Name == name);
            if (widget is null || widget.FrameUrl is not null && widget.FrameUrl != previous?.FrameUrl)
            {
                _waitingForTicket.Remove(name);
                failures = failures.Remove(name);
            }
        }
        return Accept(current, board, kinds, gatewayOrigin) with { TicketFailures = failures };
    }

    internal static BoardDocumentSnapshot Accept(
        BoardDocumentSnapshot current,
        BoardSnapshot board,
        ImmutableArray<PluginWidgetKind> kinds,
        Uri? gatewayOrigin)
    {
        var active = HasTab(board, current.ActiveTabId) ? current.ActiveTabId : board.DefaultTabId;
        var visited = current.VisitedTabs.Where(tab => HasTab(board, tab)).ToImmutableHashSet(StringComparer.Ordinal);
        if (active.Length > 0)
            visited = visited.Add(active);
        return current with
        {
            Status = DashboardLoadStatus.Ready,
            Board = board,
            Error = null,
            ActiveTabId = active,
            VisitedTabs = visited,
            PluginWidgetKinds = kinds,
            GatewayOrigin = gatewayOrigin,
        };
    }

    /// <summary>
    /// A <c>focus_tab</c> for a tab missing from the rendered board (typically a
    /// tab the agent just created) applies to the first board fetched after it.
    /// </summary>
    private BoardDocumentSnapshot ApplyPendingFocus(BoardDocumentSnapshot next, int generation)
    {
        if (_pendingFocus is not { } focus)
            return next;
        if (next.Board is { } board && HasTab(board, focus.TabId))
        {
            _pendingFocus = null;
            return next with { ActiveTabId = focus.TabId, VisitedTabs = next.VisitedTabs.Add(focus.TabId) };
        }
        // A board fetched after the command still lacks the tab: drop the request.
        if (generation > focus.AfterGeneration)
            _pendingFocus = null;
        return next;
    }

    private void FocusTab(string tabId)
    {
        if (Snapshot.Board is { } board && HasTab(board, tabId))
        {
            _pendingFocus = null;
            SelectTab(tabId);
            return;
        }
        _pendingFocus = (tabId, Volatile.Read(ref _generation));
        QueueRefresh();
    }

    private static bool HasTab(BoardSnapshot board, string tabId) =>
        tabId.Length > 0 &&
        (board.Tabs.Any(tab => tab.TabId == tabId) || board.Widgets.Any(widget => widget.TabId == tabId));

    /// <summary>Refetch before the earliest widget ticket expires (at 80 percent of its lifetime).</summary>
    private void ScheduleRenewal(BoardSnapshot board)
    {
        _renewal?.Dispose();
        _renewal = null;
        var ttl = board.Widgets
            .Where(widget => widget.ViewTicket is not null && widget.ViewTicketTtlMs is > 0)
            .Select(widget => widget.ViewTicketTtlMs!.Value)
            .DefaultIfEmpty(0)
            .Min();
        if (ttl <= 0 || _disposed)
            return;
        var delay = TimeSpan.FromMilliseconds(ttl * 0.8);
        _renewal = _schedule(delay < s_minimumRenewal ? s_minimumRenewal : delay, QueueRefresh);
    }

    private void OnBoardChanged(object? sender, BoardChangedEvent e)
    {
        var key = Snapshot.Board?.SessionKey ?? _target.SessionKey;
        if (string.Equals(e.SessionKey, key, StringComparison.Ordinal) ||
            string.Equals(e.SessionKey, _target.SessionKey, StringComparison.Ordinal))
            QueueRefresh();
    }

    private void OnBoardCommand(object? sender, BoardCommandEvent e)
    {
        if (e.Kind != "focus_tab" || e.TabId is null)
            return;
        var key = Snapshot.Board?.SessionKey ?? _target.SessionKey;
        if (string.Equals(e.SessionKey, key, StringComparison.Ordinal) ||
            string.Equals(e.SessionKey, _target.SessionKey, StringComparison.Ordinal))
            _post(() => FocusTab(e.TabId));
    }

    private void QueueRefresh()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshQueued, 1) == 1)
            return;
        _post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (!_disposed)
                _ = LoadAsync();
        });
    }

    /// <summary>
    /// Publishes a load result on the UI dispatcher. The returned task completes
    /// once it has run, so a pending rerun never supersedes a result still queued.
    /// </summary>
    private Task Apply(int generation, Func<BoardDocumentSnapshot, BoardDocumentSnapshot> update, Action? afterPublish = null)
    {
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(() =>
        {
            try
            {
                if (!_disposed && generation == _generation)
                {
                    Publish(update(Snapshot));
                    afterPublish?.Invoke();
                }
            }
            finally
            {
                applied.TrySetResult();
            }
        });
        return applied.Task;
    }

    private void Publish(BoardDocumentSnapshot snapshot)
    {
        if (_disposed)
            return;
        var next = ReconcileProgress(snapshot, out var toLoad);
        Snapshot = next;
        Changed?.Invoke(next);
        foreach (var read in toLoad)
            RequestProgressRead(read);
    }

    private BoardDocumentSnapshot ReconcileProgress(BoardDocumentSnapshot snapshot, out List<ProgressRead> toLoad)
    {
        toLoad = [];
        var board = snapshot.Board;
        var activeWidgets = board is null
            ? Array.Empty<BoardWidget>()
            : board.WidgetsForTab(snapshot.ActiveTabId)
                .Where(widget => BoardWidgetBodyPolicy.Resolve(widget, snapshot.PluginWidgetKinds) == BoardWidgetBodyKind.Progress)
                .ToArray();
        var groups = activeWidgets
            .Select(widget => (Widget: widget, Target: ProgressTarget(widget)))
            .GroupBy(item => ProgressCardSessionIdentity.NormalizeSessionKeyForComparison(item.Target.WireKey), StringComparer.Ordinal)
            .ToArray();
        var activeKeys = groups.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _progressReads.Keys.Where(key => !activeKeys.Contains(key)).ToArray())
            _progressReads.Remove(key);
        _widgetProgressTargets.Clear();

        var canRead = _client.IsConnectedToGateway && BoardAccessPolicy.CanReadProgress(_client);
        var progressCards = ImmutableDictionary.CreateBuilder<string, BoardProgressViewState>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (!_progressReads.TryGetValue(group.Key, out var read))
            {
                read = new ProgressRead(group.Key, group.First().Target);
                _progressReads.Add(group.Key, read);
            }

            var widgetNames = group.Select(item => item.Widget.Name).ToArray();
            foreach (var name in widgetNames)
                _widgetProgressTargets[name] = group.Key;

            if (!BoardAccessPolicy.CanReadProgress(_client))
            {
                read.State = new(BoardProgressLoadStatus.Unavailable, null);
                read.HasAttempted = true;
            }
            else if (!canRead && read.State.Card is null)
            {
                read.State = new(BoardProgressLoadStatus.Unavailable, null);
            }
            else if (canRead && !read.HasAttempted && !read.InFlight)
            {
                if (read.State.Card is null)
                    read.State = new(BoardProgressLoadStatus.Loading, null);
                toLoad.Add(read);
            }
            foreach (var name in widgetNames)
                progressCards[name] = read.State;
        }

        return snapshot with { ProgressCards = progressCards.ToImmutable() };
    }

    /// <summary>
    /// Matches the web widget target: <c>props.sessionKey</c> when present, owned by the
    /// agent its key names or else the board session's agent. <c>props.agentId</c> is not
    /// an authority, so a widget cannot point a bare key at another agent.
    /// </summary>
    private ProgressCardTarget ProgressTarget(BoardWidget widget)
    {
        var sessionKey = _target.SessionKey;
        if (widget.Props is { ValueKind: JsonValueKind.Object } props &&
            props.TryGetProperty("sessionKey", out var sessionValue) &&
            sessionValue.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(sessionValue.GetString()))
            sessionKey = sessionValue.GetString()!.Trim();
        var agentId = ProgressCardSessionIdentity.TryParseAgentSessionKey(sessionKey, out var keyAgentId, out _) ? keyAgentId
            : ProgressCardSessionIdentity.TryParseAgentSessionKey(_target.SessionKey, out var boardAgentId, out _) ? boardAgentId
            : _target.AgentId;
        return ProgressCardSessionIdentity.Resolve(new BoardSessionTarget(sessionKey, agentId), SessionDefaults());
    }

    // The first hello's defaults hold for this model; a reconnect builds a new model.
    private BoardSessionDefaults? SessionDefaults() => _sessionDefaults ??= _client.SessionDefaults;

    private bool IsCurrent(ProgressRead read) =>
        !_disposed && _progressReads.TryGetValue(read.Key, out var current) && ReferenceEquals(current, read);

    private void RequestProgressRead(ProgressRead read)
    {
        if (_disposed || !_client.IsConnectedToGateway || !BoardAccessPolicy.CanReadProgress(_client))
            return;
        if (read.InFlight)
        {
            read.RefreshPending = true;
            return;
        }
        read.InFlight = true;
        read.HasAttempted = true;
        _ = LoadProgressAsync(read);
    }

    private async Task LoadProgressAsync(ProgressRead read)
    {
        BoardProgressCard? card = null;
        ProgressReadFailure? failure = null;
        try
        {
            card = await _client.GetProgressCardAsync(read.Target).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ClassifyProgressFailure(ex);
            Logger.Warn($"Progress card read failed ({ex.GetType().Name}, {GatewayRequestErrors.Code(ex) ?? "local"}).");
        }

        _post(() =>
        {
            if (!IsCurrent(read))
                return;
            read.InFlight = false;
            read.State = failure switch
            {
                // A reread replaces the card even at a lower revision: a reset session restarts at 1.
                null => new(card is null ? BoardProgressLoadStatus.Empty : BoardProgressLoadStatus.Ready, card),
                ProgressReadFailure.Transient => read.State.Card is { } previousCard
                    ? new(BoardProgressLoadStatus.Ready, previousCard, Stale: true)
                    : new(BoardProgressLoadStatus.Error, null),
                ProgressReadFailure.Denied => new(BoardProgressLoadStatus.Denied, null),
                _ => new(BoardProgressLoadStatus.Unavailable, null),
            };
            if (failure is null)
                read.Dirty = false;
            var refreshAgain = read.RefreshPending;
            read.RefreshPending = false;
            Publish(Snapshot);
            if (refreshAgain && IsCurrent(read))
                RequestProgressRead(read);
        });
    }

    /// <summary>
    /// Denied reads clear the card like the web client: the caller may no longer see
    /// that session. Unsupported reads are unavailable. Anything else keeps the last
    /// card as stale so a transient failure does not blank visible progress.
    /// </summary>
    private static ProgressReadFailure ClassifyProgressFailure(Exception ex)
    {
        if (ex is NotSupportedException)
            return ProgressReadFailure.Unavailable;
        var code = GatewayRequestErrors.Code(ex);
        var detailCode = GatewayRequestErrors.DetailCode(ex);
        if (code == "METHOD_NOT_FOUND" || code == "FORBIDDEN" && detailCode == "MISSING_SCOPE")
            return ProgressReadFailure.Unavailable;
        if (code == "FORBIDDEN" || detailCode == GatewayRequestErrors.SessionParticipationRequired)
            return ProgressReadFailure.Denied;
        return ProgressReadFailure.Transient;
    }

    private void RefreshProgressCards()
    {
        foreach (var read in _progressReads.Values.ToArray())
            RequestProgressRead(read);
    }

    private void OnProgressCardChanged(object? sender, BoardProgressChangedEvent e)
    {
        _post(() =>
        {
            if (_disposed)
                return;
            foreach (var read in _progressReads.Values.ToArray())
            {
                if (!ProgressCardSessionIdentity.SameSession(e.SessionKey, read.Target.WireKey))
                    continue;
                if (!read.InFlight && !read.Dirty && e.Revision is { } revision &&
                    read.State.Card is { } card && card.Revision >= revision)
                    continue;
                read.Dirty = true;
                RequestProgressRead(read);
            }
        });
    }

    /// <summary>A reset or deleted session ends its card; drop it and reread so revisions can restart.</summary>
    private void OnSessionListChanged(object? sender, SessionListChange e)
    {
        if (e.Reason is not ("reset" or "delete") || string.IsNullOrWhiteSpace(e.SessionKey))
            return;
        _post(() =>
        {
            if (_disposed)
                return;
            var eventKey = ProgressCardSessionIdentity.Resolve(new BoardSessionTarget(e.SessionKey, e.AgentId), SessionDefaults()).WireKey;
            var changed = false;
            foreach (var read in _progressReads.Values.ToArray())
            {
                if (!ProgressCardSessionIdentity.SameSession(eventKey, read.Target.WireKey))
                    continue;
                read.State = new(BoardProgressLoadStatus.Loading, null);
                read.Dirty = true;
                RequestProgressRead(read);
                changed = true;
            }
            if (changed)
                Publish(Snapshot);
        });
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _generation);
        _renewal?.Dispose();
        _client.BoardChanged -= OnBoardChanged;
        _client.BoardCommandReceived -= OnBoardCommand;
        _client.ProgressCardChanged -= OnProgressCardChanged;
        _client.SessionListChanged -= OnSessionListChanged;
        _progressReads.Clear();
        _widgetProgressTargets.Clear();
        Changed = null;
    }
}
