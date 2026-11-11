// Owns the Gateway-sourced Inbox facts for the current operator connection:
// pending approvals (typed approval events plus *.approval.list hydration),
// failed or overdue automations (cron.list / cron.status) and model-provider
// accounts that need attention (models.authStatus). It observes the operator
// client supplied by GatewayConnectionManager and never opens its own
// connection. Facts are cleared on disconnect and recomputed on a timer so
// approvals leave when they expire and automations become overdue on time.
// Gateway text is untrusted display data: only methods and error types are logged.
//
// Usage:
//   var source = new InboxGatewaySource(new WinUIDispatcher(dispatcherQueue));
//   connectionManager.OperatorClientChanged += (_, e) => source.AttachClient(e.NewClient);
//   source.Changed += (_, _) => Render(InboxProjection.Build(notifications, source.Items));
//   source.Dismiss(item.Id);

using OpenClaw.Shared;
using OpenClawTray.Presentation;
using System.Text.Json;

namespace OpenClawTray.Services;

internal sealed class InboxGatewaySource : IDisposable
{
    private const string RequestTimeoutVariable = "OPENCLAW_INBOX_GATEWAY_REQUEST_TIMEOUT_MS";
    private const int DefaultRequestTimeoutMs = 15_000;
    private const string ModelAuthStatusMethod = "models.authStatus";
    private const string CronListMethod = "cron.list";
    private const string ApprovalsScope = "operator.approvals";
    private const string AdminScope = "operator.admin";
    // Retain observations for the whole connection without unbounded growth. At capacity,
    // stop admitting unknown IDs rather than evicting a terminal ID and resurrecting a replay.
    private const int ApprovalObservationLimit = 4096;
    private static readonly TimeSpan IdleRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ModelAuthRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ModelAuthRetryDelay = TimeSpan.FromMinutes(1);
    // Expiry timers fire a little late so the Gateway's own expiry wins any race.
    private static readonly TimeSpan ExpiryGrace = TimeSpan.FromMilliseconds(500);
    private static readonly GatewayApprovalKind[] ApprovalKinds =
        [GatewayApprovalKind.Exec, GatewayApprovalKind.Plugin, GatewayApprovalKind.SystemAgent];

    private readonly IUiDispatcher _dispatcher;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Timer? _timer;
    // Each pending approval keeps the local deadline fixed when it first arrived.
    private readonly Dictionary<string, (GatewayPendingApproval Approval, DateTimeOffset Deadline)> _approvals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset Deadline, bool Terminal)> _approvalObservations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dismissedIds = new(StringComparer.Ordinal);
    private IOperatorGatewayClient? _client;
    private int _generation;
    // Advances on every load, disconnect and client swap; late replies from an older epoch are dropped.
    private int _epoch;
    private JsonElement? _cronList;
    private int _cronRefresh;
    private int _cronStatusRefresh;
    private bool _approvalLimitReported;
    private bool? _schedulerEnabled;
    private JsonElement? _modelAuthStatus;
    private DateTimeOffset _modelAuthRefreshAt = DateTimeOffset.MinValue;
    private int? _modelAuthFlightEpoch;
    private bool _disposed;
    private volatile IReadOnlyList<InboxGatewayItem> _items = [];

    public InboxGatewaySource(IUiDispatcher dispatcher, Func<DateTimeOffset>? clock = null, bool useTimer = true)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        if (useTimer)
            _timer = new Timer(_ => _dispatcher.TryEnqueue(Tick), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised on the UI thread after <see cref="Items"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Current Gateway facts that need the user. Safe to read from any thread.</summary>
    public IReadOnlyList<InboxGatewayItem> Items => _items;

    /// <summary>Observes <paramref name="client"/> (or nothing) instead of the previous operator client. UI thread.</summary>
    public void AttachClient(IOperatorGatewayClient? client)
    {
        if (ReferenceEquals(client, _client) || _disposed)
            return;

        if (_client is { } old)
            Unsubscribe(old);
        _generation++;
        _client = client;
        ClearConnectionState();
        // Dismissed facts belong to the Gateway they were seen on.
        _dismissedIds.Clear();
        if (client is not null)
        {
            Subscribe(client);
            if (client.IsConnectedToGateway && client.HasHandshakeSnapshot)
                Load(client);
        }
        Recompute();
    }

    /// <summary>Hides a dismissible Gateway fact until it stops and later recurs. Approvals cannot be dismissed.</summary>
    public void Dismiss(string id)
    {
        if (!InboxGatewayItems.IsGatewayItemId(id) ||
            _items.FirstOrDefault(item => item.Id == id) is not { } item ||
            item.Kind == InboxGatewayItemKind.Approval)
            return;
        _dismissedIds.Add(id);
        Recompute();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer?.Dispose();
        if (_client is { } client)
            Unsubscribe(client);
        _client = null;
    }

    /// <summary>Prunes expired approvals, re-evaluates overdue automations and refreshes stale account status.</summary>
    internal void Tick()
    {
        if (_disposed)
            return;
        if (_client is { IsConnectedToGateway: true } client && _clock() >= _modelAuthRefreshAt)
            _ = LoadModelAuthAsync(client, _epoch);
        Recompute();
    }

    private void Subscribe(IOperatorGatewayClient client)
    {
        client.StatusChanged += OnStatusChanged;
        client.PendingApprovalRequested += OnApprovalRequested;
        client.PendingApprovalResolved += OnApprovalResolved;
        client.CronEventReceived += OnCronEventReceived;
        client.CronListUpdated += OnCronListUpdated;
        client.CronStatusUpdated += OnCronStatusUpdated;
    }

    private void Unsubscribe(IOperatorGatewayClient client)
    {
        client.StatusChanged -= OnStatusChanged;
        client.PendingApprovalRequested -= OnApprovalRequested;
        client.PendingApprovalResolved -= OnApprovalResolved;
        client.CronEventReceived -= OnCronEventReceived;
        client.CronListUpdated -= OnCronListUpdated;
        client.CronStatusUpdated -= OnCronStatusUpdated;
    }

    // Client events arrive on socket threads; state only changes on the UI thread for the current client.
    private void Post(object? sender, Action<IOperatorGatewayClient> action)
    {
        var generation = _generation;
        _dispatcher.TryEnqueue(() =>
        {
            if (!_disposed && generation == _generation && _client is { } client && ReferenceEquals(sender, client))
                action(client);
        });
    }

    private void OnStatusChanged(object? sender, ConnectionStatus status) => Post(sender, client =>
    {
        if (status == ConnectionStatus.Connected)
        {
            Load(client);
        }
        else if (status is ConnectionStatus.Disconnected or ConnectionStatus.Error)
        {
            ClearConnectionState();
            Recompute();
        }
    });

    private void OnApprovalRequested(object? sender, GatewayPendingApproval approval) => Post(sender, _ =>
    {
        Track(approval);
        Recompute();
    });

    private void OnApprovalResolved(object? sender, string id) => Post(sender, _ =>
    {
        RetireApproval(id);
        Recompute();
    });

    // Legacy cron.list events can contain only the first page. Treat them as invalidations,
    // never as inventories: partial data must not remove rows or forget dismissals.
    private void OnCronListUpdated(object? sender, JsonElement payload) =>
        Post(sender, client => _ = LoadCronAsync(client, _epoch));

    private void OnCronEventReceived(object? sender, JsonElement payload) => Post(sender, client =>
    {
        if (!client.IsConnectedToGateway || !client.HasHandshakeSnapshot)
            return;
        _ = LoadCronAsync(client, _epoch);
        _ = LoadCronStatusAsync(client, _epoch);
    });

    private void OnCronStatusUpdated(object? sender, JsonElement payload) =>
        Post(sender, client => _ = LoadCronStatusAsync(client, _epoch));

    private async Task LoadCronStatusAsync(IOperatorGatewayClient client, int epoch)
    {
        if (!Supports(client, "cron.status"))
            return;
        var refresh = ++_cronStatusRefresh;
        bool? enabled;
        try
        {
            var payload = await client.SendWizardRequestAsync("cron.status", new { }, RequestTimeoutMs()).ConfigureAwait(false);
            enabled = payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("enabled", out var value) &&
                value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value.GetBoolean() : null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Inbox] cron.status failed: {ex.GetType().Name}");
            return;
        }
        Post(client, _ =>
        {
            if (epoch != _epoch || refresh != _cronStatusRefresh)
                return;
            _schedulerEnabled = enabled;
            Recompute();
        });
    }

    private void Load(IOperatorGatewayClient client)
    {
        var epoch = ++_epoch;
        if (Supports(client, CronListMethod))
        {
            _ = LoadCronAsync(client, epoch);
            _ = LoadCronStatusAsync(client, epoch);
        }
        if (HasScope(client, ApprovalsScope))
        {
            foreach (var kind in ApprovalKinds)
                _ = HydrateApprovalsAsync(client, epoch, kind);
        }
        _ = LoadModelAuthAsync(client, epoch);
    }

    private async Task LoadCronAsync(IOperatorGatewayClient client, int epoch)
    {
        if (!Supports(client, CronListMethod))
            return;
        var refresh = ++_cronRefresh;
        JsonElement inventory;
        try
        {
            // Same operator client and shared pagination owner as search. Search may return
            // a bounded partial result; the Inbox requires a complete snapshot before publishing.
            inventory = await client.ReadSearchCatalogAsync(
                CronListMethod, new { includeDisabled = true }, CancellationToken.None).ConfigureAwait(false);
            if (inventory.ValueKind != JsonValueKind.Object ||
                !inventory.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array ||
                (inventory.TryGetProperty("hasMore", out var more) && more.ValueKind != JsonValueKind.False) ||
                (inventory.TryGetProperty("total", out var total) &&
                    (total.ValueKind != JsonValueKind.Number || !total.TryGetInt32(out var count) || count != jobs.GetArrayLength())))
                throw new InvalidDataException("Incomplete automation inventory.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Inbox] {CronListMethod} failed: {ex.GetType().Name}");
            return;
        }

        Post(client, _ =>
        {
            if (epoch != _epoch || refresh != _cronRefresh)
                return;
            _cronList = inventory;
            Recompute();
        });
    }

    private async Task HydrateApprovalsAsync(IOperatorGatewayClient client, int epoch, GatewayApprovalKind kind)
    {
        var method = GatewayPendingApprovalParser.ListMethod(kind);
        if (!Supports(client, method))
            return;

        var known = _approvals.Values.Where(entry => entry.Approval.Kind == kind).Select(entry => entry.Approval.Id).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<GatewayPendingApproval> listed;
        try
        {
            var reply = await client.SendWizardRequestAsync(method, new { }, RequestTimeoutMs()).ConfigureAwait(false);
            listed = GatewayPendingApprovalParser.ParseList(kind, reply);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Inbox] {method} failed: {ex.GetType().Name}");
            return;
        }

        Post(client, _ =>
        {
            if (epoch != _epoch)
                return;
            var listedIds = listed.Select(approval => approval.Id).ToHashSet(StringComparer.Ordinal);
            // Known before the read and absent from it: decided or expired elsewhere.
            foreach (var id in known.Where(id => !listedIds.Contains(id)))
                RetireApproval(id);
            foreach (var approval in listed)
                Track(approval);
            Recompute();
        });
    }

    private async Task LoadModelAuthAsync(IOperatorGatewayClient client, int epoch)
    {
        // One read per epoch; a read owned by an older epoch never blocks or clears a newer one.
        if (_modelAuthFlightEpoch == epoch || epoch != _epoch || !Supports(client, ModelAuthStatusMethod))
            return;
        _modelAuthFlightEpoch = epoch;
        var requestedAt = _clock();
        JsonElement? status = null;
        try
        {
            status = (await client.SendWizardRequestAsync(ModelAuthStatusMethod, new { }, RequestTimeoutMs()).ConfigureAwait(false)).Clone();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Inbox] {ModelAuthStatusMethod} failed: {ex.GetType().Name}");
        }

        Post(client, _ =>
        {
            if (_modelAuthFlightEpoch == epoch)
                _modelAuthFlightEpoch = null;
            if (epoch != _epoch)
                return;
            if (status is null)
            {
                _modelAuthRefreshAt = requestedAt + ModelAuthRetryDelay;
                ScheduleTick(_clock());
                return;
            }
            // Refresh again when a credential reaches its expiry, or after the regular interval.
            var fallback = requestedAt + ModelAuthRefreshInterval;
            _modelAuthRefreshAt = InboxGatewayItems.NextModelAuthRefreshAt(status, requestedAt) is { } expiry && expiry < fallback
                ? expiry
                : fallback;
            _modelAuthStatus = status;
            Recompute();
        });
    }

    // A repeated request (event replay or list hydration) keeps its original deadline.
    private void Track(GatewayPendingApproval approval)
    {
        if (!_approvalObservations.TryGetValue(approval.Id, out var observation))
        {
            if (_approvalObservations.Count >= ApprovalObservationLimit)
            {
                if (!_approvalLimitReported)
                {
                    _approvalLimitReported = true;
                    Logger.Warn("[Inbox] Approval observation limit reached; new identities are hidden until reconnect.");
                }
                return;
            }
            observation = (InboxGatewayItems.LocalDeadline(approval, _clock()), false);
            _approvalObservations.Add(approval.Id, observation);
        }
        if (observation.Terminal)
            return;
        if (observation.Deadline <= _clock())
        {
            RetireApproval(approval.Id);
            return;
        }
        _approvals[approval.Id] = (approval, observation.Deadline);
    }

    private void RetireApproval(string id)
    {
        _approvals.Remove(id);
        if (_approvalObservations.TryGetValue(id, out var observed))
            _approvalObservations[id] = (observed.Deadline, true);
        else if (_approvalObservations.Count < ApprovalObservationLimit)
            _approvalObservations.Add(id, (_clock(), true));
    }

    private void ClearConnectionState()
    {
        _epoch++;
        _approvals.Clear();
        _approvalObservations.Clear();
        _approvalLimitReported = false;
        _cronList = null;
        _schedulerEnabled = null;
        _modelAuthStatus = null;
        _modelAuthRefreshAt = DateTimeOffset.MinValue;
    }

    private void Recompute()
    {
        var now = _clock();
        var nowMs = now.ToUnixTimeMilliseconds();
        foreach (var expired in _approvals.Where(pair => pair.Value.Deadline <= now).Select(pair => pair.Key).ToList())
            RetireApproval(expired);

        var facts = _approvals.Values
            .Select(entry => InboxGatewayItems.FromApproval(entry.Approval, entry.Deadline))
            .Concat(InboxGatewayItems.BuildCron(_cronList, _schedulerEnabled, nowMs))
            .Concat(InboxGatewayItems.BuildModelAuth(_modelAuthStatus, nowMs))
            .ToList();

        // Forget a dismissal once its fact is gone from loaded data, so a recurrence shows again.
        var presentIds = facts.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        _dismissedIds.RemoveWhere(id => !presentIds.Contains(id) && IsLoaded(id));

        var items = facts.Where(item => !_dismissedIds.Contains(item.Id)).ToList();
        ScheduleTick(now);
        if (items.SequenceEqual(_items))
            return;
        _items = items;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool IsLoaded(string id) =>
        id.StartsWith($"{InboxGatewayItems.IdPrefix}modelAuth:", StringComparison.Ordinal)
            ? _modelAuthStatus is not null
            : _cronList is not null;

    private void ScheduleTick(DateTimeOffset now)
    {
        if (_timer is null || _disposed)
            return;
        var due = IdleRefreshInterval;
        if (_modelAuthStatus is not null && _modelAuthFlightEpoch is null && _modelAuthRefreshAt - now < due)
            due = _modelAuthRefreshAt < now ? TimeSpan.Zero : _modelAuthRefreshAt - now;
        if (_approvals.Count > 0)
        {
            var nextExpiry = _approvals.Values.Min(entry => entry.Deadline);
            var untilExpiry = nextExpiry - now + ExpiryGrace;
            if (untilExpiry < due)
                due = untilExpiry < TimeSpan.Zero ? TimeSpan.Zero : untilExpiry;
        }
        try
        {
            _timer.Change(due, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // Older Gateways advertise no method list; let them answer (and fail) instead of hiding the facts.
    private static bool Supports(IOperatorGatewayClient client, string method)
    {
        var methods = client.AdvertisedServerMethods;
        return methods.Count == 0 || methods.Contains(method, StringComparer.Ordinal);
    }

    private static bool HasScope(IOperatorGatewayClient client, string scope)
    {
        var scopes = client.GrantedOperatorScopes;
        return scopes.Count == 0 ||
            scopes.Contains(scope, StringComparer.Ordinal) ||
            scopes.Contains(AdminScope, StringComparer.Ordinal);
    }

    private static int RequestTimeoutMs()
    {
        var configured = Environment.GetEnvironmentVariable(RequestTimeoutVariable);
        if (configured is null)
            return DefaultRequestTimeoutMs;
        if (int.TryParse(configured, out var milliseconds) && milliseconds > 0)
            return milliseconds;
        Logger.Warn($"{RequestTimeoutVariable} must be a positive integer; using the default Inbox request timeout.");
        return DefaultRequestTimeoutMs;
    }
}
