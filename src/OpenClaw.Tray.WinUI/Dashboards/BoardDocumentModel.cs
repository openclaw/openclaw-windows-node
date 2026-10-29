using System.Collections.Immutable;
using OpenClaw.Shared.Dashboards;

namespace OpenClawTray.Dashboards;

internal enum BoardWidgetTicketFailure
{
    RefreshUnavailable,
    RepeatedRejection,
}

internal sealed record BoardDocumentSnapshot(
    DashboardLoadStatus Status,
    BoardSnapshot? Board,
    string? Error,
    string ActiveTabId,
    ImmutableHashSet<string> VisitedTabs,
    ImmutableDictionary<string, BoardWidgetTicketFailure> TicketFailures,
    ImmutableArray<PluginWidgetKind> PluginWidgetKinds,
    Uri? GatewayOrigin)
{
    public static BoardDocumentSnapshot Initial { get; } = new(
        DashboardLoadStatus.Loading, null, null, "", [], [], [], null);
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
    private readonly object _loadGate = new();
    private IDisposable? _renewal;
    private int _generation;
    private int _refreshQueued;
    private bool _loading;
    private bool _reloadPending;
    private Task _loadTask = Task.CompletedTask;
    private (string TabId, int AfterGeneration)? _pendingFocus;
    private bool _disposed;

    public BoardDocumentModel(
        IBoardGatewayClient client,
        BoardSessionTarget target,
        Action<Action> post,
        Func<TimeSpan, Action, IDisposable> schedule)
    {
        _client = client;
        _target = target;
        _post = post;
        _schedule = schedule;
        _client.BoardChanged += OnBoardChanged;
        _client.BoardCommandReceived += OnBoardCommand;
    }

    public BoardSessionTarget Target => _target;

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
        Snapshot = snapshot;
        Changed?.Invoke(snapshot);
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _generation);
        _renewal?.Dispose();
        _client.BoardChanged -= OnBoardChanged;
        _client.BoardCommandReceived -= OnBoardCommand;
        Changed = null;
    }
}
