// Owns per-page Systems reads and connection fences. Usage: controller.UpdateInputs(inputs), then observe State/Changed.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.Shared.Systems;

namespace OpenClawTray.Systems;

/// <summary>Inputs owned by the app (AppState, connection, scopes) that the Systems page observes.</summary>
internal sealed record SystemsInputs
{
    public bool Connected { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<PresenceEntry> Presence { get; init; } = Array.Empty<PresenceEntry>();
    public IReadOnlyList<SessionInfo> Sessions { get; init; } = Array.Empty<SessionInfo>();
    public IReadOnlyList<PendingApproval> PendingApprovals { get; init; } = Array.Empty<PendingApproval>();
    public PairingListInfo? NodePairList { get; init; }
    public IReadOnlyCollection<string> OwnNodeIds { get; init; } = Array.Empty<string>();
    public string? LocalNodeId { get; init; }
    public string? LocalHost { get; init; }
    /// <summary>The active Gateway record; a change means a different Gateway, not a reconnect.</summary>
    public string? GatewayId { get; init; }
    public string? GatewayName { get; init; }
}

/// <summary>Immutable snapshot rendered by the Reactor Systems page.</summary>
internal sealed record SystemsViewState
{
    public bool Connected { get; init; }
    public bool Loading { get; init; }
    public bool HasLoaded { get; init; }
    public string? InventoryError { get; init; }
    public bool InventoryTimedOut { get; init; }
    public SystemsPartialErrors PartialErrors { get; init; } = new();
    public IReadOnlyList<SystemRow> Rows { get; init; } = Array.Empty<SystemRow>();
    public string? SelectedId { get; init; }
    public int SelectionRequest { get; init; }
    public IReadOnlyList<PendingApproval> PendingApprovals { get; init; } = Array.Empty<PendingApproval>();
    public bool CanRead { get; init; }
    /// <summary>
    /// Rename and forget need <c>operator.pairing</c> (or admin) on the Gateway. Gateways that
    /// do not advertise scopes keep the earlier behavior and report a denial as an error.
    /// </summary>
    public bool CanManageNodes { get; init; }
    /// <summary>
    /// The Gateway reports <c>environments.list</c>, which its web Systems page requires. Older
    /// Gateways open the web interface root instead.
    /// </summary>
    public bool HasWebSystemsPage { get; init; }
    public string? LocalNodeId { get; init; }
    public string? GatewayName { get; init; }
    public DateTimeOffset? LastRefreshedAt { get; init; }
    /// <summary>
    /// Changes whenever the connection drops, returns or moves to another Gateway. Confirmation
    /// dialogs record it so a confirmation never runs against a connection the user did not review.
    /// </summary>
    public int ConnectionEpoch { get; init; } = 1;
    /// <summary>Local clock minus Gateway clock; ages of Gateway timestamps subtract it from local now.</summary>
    public TimeSpan GatewayClockOffset { get; init; }
    /// <summary>
    /// Advanced by each page tick that published nothing else. Report ages and Live versus Last known
    /// are computed at render time, so this keeps them current without another Gateway read.
    /// </summary>
    public DateTimeOffset? PresentedAt { get; init; }

    public SystemRow? Selected => SelectedId is null ? null : Rows.FirstOrDefault(row => row.Id == SelectedId);

    /// <summary>A selection that no longer resolves; the page keeps it and explains instead of jumping.</summary>
    public bool SelectionMissing => SelectedId is not null && Selected is null && HasLoaded;

    /// <summary>The page's own node is waiting for approval; its capabilities stay inactive until then.</summary>
    public bool OwnNodePending { get; init; }
    public int PendingPairingCount => PendingApprovals.Count + (OwnNodePending ? 1 : 0);
}

/// <summary>
/// Owns Systems inventory reads, refresh scheduling and the per-page presentation
/// state. WinUI-free: the page forwards app events and renders <see cref="State"/>.
/// All public members are expected on one logical thread (the UI dispatcher).
/// </summary>
internal sealed class SystemsController : IDisposable
{
    public static readonly TimeSpan InventoryTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_SYSTEMS_POLL_SECONDS"), out var seconds) && seconds >= 5
            ? seconds
            : 15);

    /// <summary>Gateway events arrive in bursts (presence beacons, pairing); one reload covers a burst.</summary>
    public static readonly TimeSpan EventRefreshDelay = TimeSpan.FromMilliseconds(
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_SYSTEMS_EVENT_REFRESH_MS"), out var ms) && ms >= 0
            ? ms
            : 1500);

    /// <summary>Gateway clock differences smaller than this are event latency, not skew.</summary>
    private static readonly TimeSpan ClockOffsetTolerance = TimeSpan.FromSeconds(5);
    /// <summary>
    /// A larger offset is confirmed only by a sample seen at least this long after the first one,
    /// so reports queued behind one UI stall and delivered together cannot confirm each other.
    /// </summary>
    private static readonly TimeSpan ClockOffsetConfirmAfter = TimeSpan.FromSeconds(20);

    private readonly Func<ISystemsGateway?> _gateway;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Dictionary<string, NodeHostStats> _pushedHostStats = new(StringComparer.Ordinal);
    private TimeSpan _gatewayClockOffset;
    private bool _clockCalibrated;
    private (TimeSpan Offset, DateTimeOffset SeenAt)? _pendingClockOffset;
    private bool _eventRefreshScheduled;
    private string? _lastGatewayId;
    // Cancelled whenever the connection epoch changes, so no request outlives the connection it ran on.
    private CancellationTokenSource _connection;
    private readonly CancellationTokenSource _lifetime = new();
    private SystemsInventorySnapshot? _snapshot;
    private SystemsInputs _inputs = new();
    private CancellationTokenSource? _inventoryCts;
    private bool _inventoryPending;
    private bool _refreshQueued;
    private bool _measurementsPending;
    private bool _disposed;
    private long _publishCount;

    public SystemsController(
        Func<ISystemsGateway?> gateway,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        _connection = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
    }

    public SystemsViewState State { get; private set; } = new();

    public event EventHandler? Changed;

    /// <summary>Applies new app inputs. A reconnect clears pushed statistics and starts a fresh refresh.</summary>
    public void UpdateInputs(SystemsInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        // Compare with the last known Gateway so A, then none, then B still counts as a switch.
        var gatewayChanged = inputs.GatewayId is not null && _lastGatewayId is not null &&
            !string.Equals(inputs.GatewayId, _lastGatewayId, StringComparison.Ordinal);
        if (inputs.GatewayId is not null)
            _lastGatewayId = inputs.GatewayId;
        var reconnected = inputs.Connected && (!_inputs.Connected || gatewayChanged);
        var disconnected = !inputs.Connected && _inputs.Connected;
        var epochChanged = inputs.Connected != _inputs.Connected || gatewayChanged;
        // The app clears presence and sessions on disconnect; keep the last report on screen.
        if (!inputs.Connected && !gatewayChanged)
            inputs = inputs with { Presence = _inputs.Presence, Sessions = _inputs.Sessions };
        _inputs = inputs;
        if (disconnected || gatewayChanged)
        {
            CancelInventory();
            _refreshQueued = false;
        }
        var next = State;
        if (gatewayChanged)
        {
            // Another Gateway's machines must never mix with this one's last-known inventory.
            _snapshot = null;
            next = next with
            {
                HasLoaded = false,
                SelectedId = null,
                SelectionRequest = 0,
                InventoryError = null,
                PartialErrors = new(),
                HasWebSystemsPage = false,
                LastRefreshedAt = null,
            };
        }
        if (reconnected)
        {
            _pushedHostStats.Clear();
            _gatewayClockOffset = TimeSpan.Zero;
            _clockCalibrated = false;
            _pendingClockOffset = null;
            next = next with { GatewayClockOffset = TimeSpan.Zero };
        }
        if (epochChanged)
        {
            RenewConnection();
            next = next with { ConnectionEpoch = next.ConnectionEpoch + 1 };
        }
        Publish(next with
        {
            Connected = inputs.Connected,
            CanRead = HasScope(inputs.Scopes, "operator.read", legacyDefault: true),
            CanManageNodes = HasScope(inputs.Scopes, "operator.pairing", legacyDefault: true),
            PendingApprovals = inputs.PendingApprovals,
            OwnNodePending = inputs.Connected && inputs.NodePairList?.Pending.Any(request =>
                !string.IsNullOrWhiteSpace(request.NodeId) &&
                inputs.OwnNodeIds.Contains(request.NodeId, StringComparer.OrdinalIgnoreCase)) == true,
            LocalNodeId = inputs.LocalNodeId,
            GatewayName = inputs.GatewayName,
            Loading = inputs.Connected && next.Loading,
            Rows = Project(),
        });
        if (reconnected)
            _ = RefreshAsync();
    }

    /// <summary>
    /// A pushed node.list replaces the node rows without waiting for the next poll. Ignored
    /// while disconnected so the cleared app list does not erase the last-known nodes.
    /// </summary>
    public void UpdateNodes(IReadOnlyList<GatewayNodeInfo> nodes)
    {
        if (_snapshot is null || !_inputs.Connected)
            return;
        _snapshot = _snapshot with { Nodes = nodes, Errors = _snapshot.Errors with { Nodes = null } };
        PublishRows();
    }

    /// <summary>
    /// Applies a <c>node.hostStats</c> event. Its receipt time was stamped by the Gateway moments
    /// ago, so it also measures how far the Gateway clock is from the local one. Reports older than
    /// the one already shown are ignored.
    /// </summary>
    public void ApplyHostStats(NodeHostStatsEvent update)
    {
        if (_snapshot is null || !_inputs.Connected)
            return;
        var listed = _snapshot.Nodes.FirstOrDefault(node => string.Equals(node.NodeId, update.NodeId, StringComparison.Ordinal));
        var newest = _pushedHostStats.TryGetValue(update.NodeId, out var pushed) && pushed.UpdatedAt >= (listed?.HostStats?.UpdatedAt ?? DateTimeOffset.MinValue)
            ? pushed
            : listed?.HostStats;
        // Unknown nodes and replayed or late reports are ignored, and never calibrate the clock.
        if (listed is null || (newest is not null && update.Stats.UpdatedAt <= newest.UpdatedAt))
            return;
        _pushedHostStats[update.NodeId] = update.Stats;
        if (CalibrateClock(_clock() - update.Stats.UpdatedAt) is { } offset)
        {
            _gatewayClockOffset = offset;
            Publish(State with { GatewayClockOffset = offset });
        }
        PublishRows();
    }

    /// <summary>
    /// Returns a new offset when the measured one should replace the current one, otherwise null.
    /// Delivery delay only ever makes the measured offset larger, so a smaller one is trusted at once,
    /// while a larger one must be seen again <see cref="ClockOffsetConfirmAfter"/> later: late events
    /// must not move the clock. The first sample is adopted at once so a skewed Gateway shows correct
    /// ages from its first report; if that sample was late, the next on-time report corrects it.
    /// </summary>
    private TimeSpan? CalibrateClock(TimeSpan measured)
    {
        TimeSpan? adopt = null;
        if (!_clockCalibrated || measured < _gatewayClockOffset - ClockOffsetTolerance)
            adopt = measured;
        else if (measured > _gatewayClockOffset + ClockOffsetTolerance)
        {
            var now = _clock();
            if (_pendingClockOffset is not { } pending || (measured - pending.Offset).Duration() > ClockOffsetTolerance)
                _pendingClockOffset = (measured, now);
            else if (now - pending.SeenAt >= ClockOffsetConfirmAfter)
                adopt = measured;
        }
        else
            _pendingClockOffset = null;

        if (adopt is not { } value)
            return null;
        _clockCalibrated = true;
        _pendingClockOffset = null;
        // Differences inside the tolerance are latency, not skew.
        var offset = value.Duration() > ClockOffsetTolerance ? value : TimeSpan.Zero;
        return (offset - _gatewayClockOffset).Duration() > ClockOffsetTolerance ? offset : null;
    }

    /// <summary>Gateway events coalesce into one inventory reload after <see cref="EventRefreshDelay"/>.</summary>
    public void RequestRefresh()
    {
        if (_disposed || _eventRefreshScheduled || !_inputs.Connected)
            return;
        _eventRefreshScheduled = true;
        _ = RunRequestedRefreshAsync();
    }

    private async Task RunRequestedRefreshAsync()
    {
        try
        {
            await _delay(EventRefreshDelay, _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _eventRefreshScheduled = false;
        }
        await RefreshAsync().ConfigureAwait(true);
    }

    public void Select(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;
        var alreadySelected = id == State.SelectedId;
        Publish(State with
        {
            SelectedId = id,
            SelectionRequest = State.SelectionRequest + 1,
        });
        if (alreadySelected)
            return;
        // Fresh measurements for the newly selected machine instead of waiting for the next poll.
        _ = RefreshMeasurementsAsync();
    }

    public void ToggleSelection(string id)
    {
        if (State.SelectedId == id)
            Publish(State with { SelectedId = null });
        else
            Select(id);
    }

    /// <summary>
    /// Periodic work: inventory when needed, otherwise the selected machine's measurements. When no
    /// read is eligible (nothing, a worker or a client selected, or disconnected), still re-renders so
    /// visible ages and freshness keep advancing.
    /// </summary>
    public async Task TickAsync()
    {
        if (_disposed)
            return;
        var published = _publishCount;
        if (_inputs.Connected)
        {
            if (_snapshot is null || _refreshQueued)
                await RefreshAsync().ConfigureAwait(true);
            else
                await RefreshMeasurementsAsync().ConfigureAwait(true);
        }
        if (!_disposed && _publishCount == published)
            Publish(State with { PresentedAt = _clock() });
    }

    /// <summary>Reloads the inventory. Event bursts during a load collapse into one follow-up refresh.</summary>
    public async Task RefreshAsync()
    {
        if (_disposed || !_inputs.Connected || _gateway() is not { } gateway)
            return;
        if (!State.CanRead)
            return;
        if (_inventoryPending)
        {
            _refreshQueued = true;
            return;
        }

        _refreshQueued = false;
        _inventoryPending = true;
        CancelInventory();
        using var timeout = new CancellationTokenSource(InventoryTimeout);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, timeout.Token);
        _inventoryCts = cts;
        Publish(State with { Loading = true, InventoryError = null, InventoryTimedOut = false });
        try
        {
            var snapshot = await gateway.LoadInventoryAsync(cts.Token).ConfigureAwait(true);
            // A superseded load is dropped, but the tail below still runs any refresh queued meanwhile.
            if (!cts.IsCancellationRequested && ReferenceEquals(_inventoryCts, cts))
                ApplyInventory(snapshot);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested &&
                                                  ReferenceEquals(_inventoryCts, cts) &&
                                                  !_lifetime.IsCancellationRequested)
        {
            Publish(State with { Loading = false, InventoryError = "timeout", InventoryTimedOut = true });
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_inventoryCts, cts))
                Publish(State with { Loading = false, InventoryError = ex.Message, InventoryTimedOut = false });
        }
        finally
        {
            _inventoryPending = false;
            if (ReferenceEquals(_inventoryCts, cts))
            {
                _inventoryCts = null;
                if (State.Loading)
                    Publish(State with { Loading = false });
            }
            cts.Dispose();
        }
        if (_refreshQueued && _inputs.Connected && !_disposed)
            await RefreshAsync().ConfigureAwait(true);
    }

    private void ApplyInventory(SystemsInventorySnapshot snapshot)
    {
        // A failed node or host read keeps its last-known data; the partial error marks it stale.
        if (_snapshot is { } previous)
        {
            if (snapshot.Errors.Nodes is not null)
                snapshot = snapshot with { Nodes = previous.Nodes };
            if (snapshot.Errors.SystemInfo is not null)
                snapshot = snapshot with { SystemInfo = previous.SystemInfo, SystemInfoSampledAt = previous.SystemInfoSampledAt };
        }
        _snapshot = snapshot;
        var rows = Project();
        var initial = !State.HasLoaded;
        Publish(State with
        {
            Rows = rows,
            Loading = false,
            HasLoaded = true,
            InventoryError = null,
            InventoryTimedOut = false,
            PartialErrors = snapshot.Errors,
            HasWebSystemsPage = snapshot.Environments is not null,
            LastRefreshedAt = _clock(),
            SelectedId = initial && State.SelectedId is null
                ? SystemsProjection.DefaultSelection(rows)
                : State.SelectedId,
        });
    }

    /// <summary>Refreshes only the selected machine's measurements without reloading the inventory.</summary>
    public async Task RefreshMeasurementsAsync()
    {
        if (_measurementsPending || _inventoryPending || _snapshot is null || _gateway() is not { } gateway ||
            State.Selected is not { } row || !_inputs.Connected)
        {
            return;
        }
        var host = row.Id == SystemsProjection.GatewayId;
        if (!host && row.Kind != SystemKind.Node)
            return;
        _measurementsPending = true;
        var token = _connection.Token;
        var epoch = State.ConnectionEpoch;
        try
        {
            if (host)
            {
                var info = await gateway.ReadSystemInfoAsync(token).ConfigureAwait(true);
                if (_snapshot is null || epoch != State.ConnectionEpoch)
                    return;
                _snapshot = _snapshot with
                {
                    SystemInfo = info?.Info ?? _snapshot.SystemInfo,
                    SystemInfoSampledAt = info?.SampledAt ?? _snapshot.SystemInfoSampledAt,
                    Errors = _snapshot.Errors with { SystemInfo = null },
                };
            }
            else
            {
                var nodes = await gateway.ReadNodesAsync(token).ConfigureAwait(true);
                if (_snapshot is null || epoch != State.ConnectionEpoch)
                    return;
                _snapshot = _snapshot with { Nodes = nodes, Errors = _snapshot.Errors with { Nodes = null } };
            }
            PublishRows();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_snapshot is not null && epoch == State.ConnectionEpoch)
            {
                _snapshot = _snapshot with
                {
                    Errors = host ? _snapshot.Errors with { SystemInfo = ex.Message } : _snapshot.Errors with { Nodes = ex.Message },
                };
                PublishRows();
            }
        }
        finally
        {
            // A newer connection resets the flag itself and may already own it again.
            if (epoch == State.ConnectionEpoch)
                _measurementsPending = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CancelInventory();
        _lifetime.Cancel();
        _connection.Dispose();
        _lifetime.Dispose();
        Changed = null;
    }

    private void PublishRows() =>
        Publish(State with { Rows = Project(), PartialErrors = _snapshot?.Errors ?? new() });

    private IReadOnlyList<SystemRow> Project()
    {
        if (_snapshot is null)
            return Array.Empty<SystemRow>();
        var snapshot = _snapshot with { Presence = _inputs.Presence };
        var sessions = _inputs.Sessions
            .Where(session => !session.Archived)
            .Select(session => new SystemsSessionRef(
                session.Key,
                FirstNonEmpty(session.DisplayName, session.Label, session.DerivedTitle) ?? session.Key,
                session.Placement,
                session.ExecNode)
            {
                AgentId = session.AgentId,
                UpdatedAt = session.UpdatedAt is { } updated
                    ? new DateTimeOffset(DateTime.SpecifyKind(updated, DateTimeKind.Utc)) : null,
            })
            .ToArray();
        return SystemsProjection.Project(snapshot, sessions, new SystemsProjectionOptions
        {
            LocalNodeId = _inputs.LocalNodeId,
            LocalHost = _inputs.LocalHost,
            NowUtc = _clock().UtcDateTime,
            GatewayConnected = _inputs.Connected,
            HostStats = _pushedHostStats,
            GatewayClockOffset = _gatewayClockOffset,
        });
    }

    private void Publish(SystemsViewState next)
    {
        if (_disposed)
            return;
        _publishCount++;
        State = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RenewConnection()
    {
        var previous = _connection;
        _connection = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _measurementsPending = false;
        try { previous.Cancel(); }
        catch (ObjectDisposedException) { }
        previous.Dispose();
    }

    private void CancelInventory()
    {
        var cts = Interlocked.Exchange(ref _inventoryCts, null);
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Uses the Gateway's scope rules. Gateways that do not advertise scopes keep legacy read access only.</summary>
    internal static bool HasScope(IReadOnlyList<string> scopes, string scope, bool legacyDefault) =>
        scopes.Count == 0 ? legacyDefault : OperatorScopeHelper.Satisfies(scopes, scope);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
