using System.Collections.Immutable;
using OpenClaw.Shared.Dashboards;

namespace OpenClawTray.Dashboards;

internal enum DashboardLoadStatus
{
    Loading,
    Ready,
    Error,
    /// <summary>No read access, or the Gateway does not provide dashboards.</summary>
    Unavailable,
    Disconnected,
}

internal sealed record DashboardGallerySnapshot(
    DashboardLoadStatus Status,
    ImmutableArray<DashboardSessionRow> Rows,
    string? Error,
    string? AgentId)
{
    public static DashboardGallerySnapshot Initial { get; } = new(DashboardLoadStatus.Loading, [], null, null);

    /// <summary>A failed refresh keeps the previous rows visible as stale data.</summary>
    public bool HasRows => !Rows.IsDefaultOrEmpty;
}

/// <summary>
/// Owns the dashboard gallery for one agent scope: reads every
/// <c>sessions.list</c> dashboard page, keeps rows on refresh failure, and
/// refreshes when a board changes. Publishes immutable snapshots through
/// <paramref name="post"/> (the UI dispatcher).
/// </summary>
internal sealed class DashboardGalleryModel : IDisposable
{
    private readonly IBoardGatewayClient _client;
    private readonly Action<Action> _post;
    private readonly object _loadGate = new();
    private int _generation;
    private int _refreshQueued;
    private int _activeLoads;
    private bool _refreshPending;
    private bool _disposed;

    public DashboardGalleryModel(IBoardGatewayClient client, Action<Action> post)
    {
        _client = client;
        _post = post;
        _client.BoardChanged += OnBoardChanged;
    }

    public DashboardGallerySnapshot Snapshot { get; private set; } = DashboardGallerySnapshot.Initial;

    public event Action<DashboardGallerySnapshot>? Changed;

    /// <summary>Loads the gallery for <paramref name="agentId"/>; a newer call supersedes an older one.</summary>
    public async Task LoadAsync(string? agentId)
    {
        lock (_loadGate)
            _activeLoads++;
        try
        {
            await LoadCoreAsync(agentId).ConfigureAwait(false);
        }
        finally
        {
            bool rerun;
            lock (_loadGate)
            {
                rerun = --_activeLoads == 0 && _refreshPending && !_disposed;
                if (rerun)
                    _refreshPending = false;
            }
            if (rerun)
                _post(RefreshForBoardChange);
        }
    }

    private async Task LoadCoreAsync(string? agentId)
    {
        var generation = Interlocked.Increment(ref _generation);
        var previous = string.Equals(Snapshot.AgentId, agentId, StringComparison.Ordinal) ? Snapshot.Rows : [];
        if (!_client.IsConnectedToGateway)
        {
            await Publish(generation, new DashboardGallerySnapshot(DashboardLoadStatus.Disconnected, previous, null, agentId)).ConfigureAwait(false);
            return;
        }
        if (!BoardAccessPolicy.CanRead(_client))
        {
            await Publish(generation, new DashboardGallerySnapshot(DashboardLoadStatus.Unavailable, [], null, agentId)).ConfigureAwait(false);
            return;
        }
        if (previous.IsDefaultOrEmpty)
            await Publish(generation, new DashboardGallerySnapshot(DashboardLoadStatus.Loading, [], null, agentId)).ConfigureAwait(false);

        try
        {
            var rows = ImmutableArray.CreateBuilder<DashboardSessionRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var offset = 0;
            for (var page = 0; page < DashboardProtocol.MaxGalleryPages; page++)
            {
                var result = await _client.ListDashboardSessionsAsync(agentId, offset).ConfigureAwait(false);
                if (generation != _generation)
                    return;
                if (result is null)
                {
                    await Publish(generation, new DashboardGallerySnapshot(DashboardLoadStatus.Unavailable, [], null, agentId)).ConfigureAwait(false);
                    return;
                }
                foreach (var row in result.Rows)
                {
                    // Pages are live: deduplicate rows that moved across a page boundary.
                    if (seen.Add(row.AgentId + "\0" + row.Key))
                        rows.Add(row);
                }
                if (DashboardProtocol.NextPageOffset(result, offset) is not { } next)
                    break;
                offset = next;
            }
            await Publish(generation, new DashboardGallerySnapshot(DashboardLoadStatus.Ready, rows.ToImmutable(), null, agentId)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Publish(generation, new DashboardGallerySnapshot(DashboardLoadStatus.Error, previous, ex.Message, agentId)).ConfigureAwait(false);
        }
    }

    private void OnBoardChanged(object? sender, BoardChangedEvent e)
    {
        // Coalesce bursts of board revisions into one gallery refresh.
        if (_disposed || Interlocked.Exchange(ref _refreshQueued, 1) == 1)
            return;
        _post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            RefreshForBoardChange();
        });
    }

    /// <summary>
    /// Reloads for a board change. While a load runs, the change waits and
    /// reloads once afterwards, so a burst of changes never stacks full
    /// multi-page reloads or keeps every response stale.
    /// </summary>
    private void RefreshForBoardChange()
    {
        if (_disposed)
            return;
        lock (_loadGate)
        {
            if (_activeLoads > 0)
            {
                _refreshPending = true;
                return;
            }
        }
        if (Snapshot.Status is DashboardLoadStatus.Ready or DashboardLoadStatus.Error)
            _ = LoadAsync(Snapshot.AgentId);
    }

    /// <summary>
    /// Publishes on the UI dispatcher. The load awaits it, so the load stays
    /// active until its result is applied and a queued board-change refresh
    /// waits behind it instead of superseding it.
    /// </summary>
    private Task Publish(int generation, DashboardGallerySnapshot snapshot)
    {
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(() =>
        {
            try
            {
                if (_disposed || generation != _generation)
                    return;
                Snapshot = snapshot;
                Changed?.Invoke(snapshot);
            }
            finally
            {
                applied.TrySetResult();
            }
        });
        return applied.Task;
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _generation);
        _client.BoardChanged -= OnBoardChanged;
        Changed = null;
    }
}
