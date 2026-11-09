using OpenClaw.Shared;
using OpenClaw.Shared.Usage;

namespace OpenClawTray.Presentation.Usage;

internal enum UsageAvailability
{
    Connected,
    Disconnected,
    /// <summary>
    /// The Gateway has no usable <c>sessions.usage</c>. Every Gateway setup installs (2026.9.36 and later) has it,
    /// so the page asks for an update and shows only provider plans.
    /// </summary>
    Unsupported,
    /// <summary>The connection lacks <c>operator.read</c>.</summary>
    Forbidden,
}

/// <summary>The selected session: its list row is shown at once, and a single-session read refines it.</summary>
internal sealed record UsageDetailState(string Key, string? AgentId, UsageRead<SessionUsageRow> Row);

internal sealed record UsagePageState
{
    public required UsageFilters Filters { get; init; }
    public UsageMetric Metric { get; init; } = UsageMetric.Cost;
    public string? FocusProvider { get; init; }
    public int FocusProviderRequest { get; init; }
    public UsageAvailability Availability { get; init; } = UsageAvailability.Disconnected;
    public UsageLoadPhase Phase { get; init; } = UsageLoadPhase.Idle;
    public string? Error { get; init; }
    public SessionsUsageReport? Report { get; init; }
    public UsageDashboard? Dashboard { get; init; }
    public bool? SupportsOverview { get; init; }
    public DateTimeOffset? LoadedAt { get; init; }
    /// <summary>True while an incomplete result is being re-checked automatically.</summary>
    public bool AutoRefreshing { get; init; }
    public bool AutoRefreshPaused { get; init; }
    public UsageRead<ProviderUsageSummary> Providers { get; init; } = UsageRead<ProviderUsageSummary>.Idle;
    /// <summary>Invalidates time-dependent provider presentation even when the retained figures are unchanged.</summary>
    public long ProviderFreshnessRevision { get; init; }
    public UsageDetailState? Detail { get; init; }
    public bool Exporting { get; init; }
    public bool ExportInteraction { get; init; }
    public UsageExportCoverage? ExportCoverage { get; init; }
    public UsageExportBlock? ExportBlock { get; init; }
    /// <summary>Outcome of the last user-requested export, shown until the next export or dismissal.</summary>
    public UsageExportNotice? ExportNotice { get; init; }

    /// <summary>
    /// The connected Gateway returns full rows without the server-side overview (every released Gateway through
    /// 2026.10.9), so search, facets, session sort and paging are unavailable.
    /// </summary>
    public bool BasicGateway => Availability == UsageAvailability.Connected
        && (SupportsOverview == false || Report is { Overview: null });

    /// <summary>
    /// Search, sort and paging need the server-side overview. Basic Gateways ignore them, so the page neither sends
    /// nor shows them.
    /// </summary>
    public bool ServerFiltersUnavailable => BasicGateway || Availability == UsageAvailability.Unsupported;

    public bool ShowLoadError => Phase == UsageLoadPhase.Failed && Availability == UsageAvailability.Connected;
}

/// <summary>Timing for re-checking incomplete usage. Override with OPENCLAW_USAGE_RECHECK_INTERVAL_MS.</summary>
internal static class UsageRefreshPolicy
{
    public const int DefaultRecheckIntervalMs = 5000;
    public const int MaxAutomaticRechecks = 6;

    public static TimeSpan RecheckInterval =>
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_USAGE_RECHECK_INTERVAL_MS"), out var ms) && ms >= 250
            ? TimeSpan.FromMilliseconds(ms)
            : TimeSpan.FromMilliseconds(DefaultRecheckIntervalMs);
}

/// <summary>
/// Owns Usage page query state and Gateway reads; the Reactor view renders <see cref="State"/> snapshots.
/// All public members run on the UI thread; async completions are marshalled through <c>post</c> and
/// fenced by generation counters so superseded or disconnected replies never overwrite newer state.
/// </summary>
internal sealed class UsagePageController : IDisposable
{
    private readonly Func<IOperatorGatewayClient?> _client;
    private readonly Func<DateOnly> _today;
    private readonly Func<DateOnly> _utcToday;
    private readonly Action<Action> _post;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource _overviewCts = new();
    private readonly IUsageProviderReadSource _providers;
    private readonly UsageProviderReadSource? _ownedProviders;
    private long _providerBindingRevision;
    private CancellationTokenSource _detailCts = new();
    private CancellationTokenSource _recheckCts = new();
    private readonly CancellationTokenSource _exportCts = new();
    private int _overviewGeneration;
    private int _detailGeneration;
    private int _rechecks;
    private bool _disposed;
    private bool _providerRefreshPending;
    // The operator client whose reads produced the current figures, selection and plans.
    private IOperatorGatewayClient? _stateClient;

    public UsagePageController(
        Func<IOperatorGatewayClient?> client, Func<DateOnly> today, Action<Action> post,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateOnly>? utcToday = null,
        IUsageProviderReadSource? providers = null)
    {
        _client = client;
        _today = today;
        _utcToday = utcToday ?? (() => DateOnly.FromDateTime(DateTime.UtcNow));
        _post = post;
        _delay = delay ?? Task.Delay;
        State = new UsagePageState { Filters = UsageFilters.Default(today()) };
        _providers = providers ?? (_ownedProviders = new UsageProviderReadSource(post));
        _providers.Changed += OnProvidersChanged;
    }

    public UsagePageState State { get; private set; }
    internal object? ExportConnectionIdentity => _client();

    public event Action? Changed;

    internal void PostUi(Action action) => _post(() =>
    {
        if (!_disposed) action();
    });

    // ── lifecycle ──

    /// <summary>Re-evaluates the connection and reloads. Call on open, reconnect and client replacement.</summary>
    public void Activate()
    {
        _providerRefreshPending = false;
        var client = _client();
        ForgetReplacedGateway(client);
        _ownedProviders?.SetConnection(client, _providerBindingRevision);
        if (client is not { IsConnectedToGateway: true })
        {
            _providerRefreshPending = false;
            CancelAll();
            Update(s => SettleLoading(s) with { Availability = UsageAvailability.Disconnected, AutoRefreshing = false });
            return;
        }
        // Availability is re-learned from each load, so a Gateway upgraded while disconnected is retried.
        // Relative presets move with the calendar, so a page left open past midnight reloads the current range.
        ChangeFilters(Rebased(State.Filters));
        Update(s => s with
        {
            Availability = s.Availability == UsageAvailability.Disconnected ? UsageAvailability.Connected : s.Availability,
        });
        Reload(client, resetRechecks: true);
        _providerRefreshPending = true;
        RefreshBoundProviders(client);
        if (State.Detail is { } detail) LoadDetail(client, detail.Key, detail.AgentId);
    }

    public void Refresh()
    {
        Update(s => s with { AutoRefreshPaused = false });
        Activate();
    }

    /// <summary>
    /// A replacement operator client may belong to another Gateway (the connection window can switch while Usage is
    /// open), so everything read from the previous client is dropped and its in-flight reads are fenced. Only
    /// portable preferences survive: range, time zone, scope, search, sort and metric. Returns true when it reset.
    /// The manager can replace its client between two reads, so each operation reads the client once, passes it
    /// here, and starts its reads on that same client.
    /// </summary>
    private bool ForgetReplacedGateway(IOperatorGatewayClient? client)
    {
        if (client is null || ReferenceEquals(client, _stateClient)) return false;
        var replaced = _stateClient is not null;
        _stateClient = client;
        _providerBindingRevision++;
        if (!replaced) return false;
        CancelAll();
        _rechecks = 0;
        _exportAlternative?.TrySetResult(false);
        _exportAlternative = null;
        var connected = client.IsConnectedToGateway;
        Update(s => s with
        {
            Filters = s.Filters with { Offset = 0 },
            FocusProvider = null,
            Availability = connected ? UsageAvailability.Connected : UsageAvailability.Disconnected,
            Phase = UsageLoadPhase.Idle,
            Error = null,
            Report = null,
            Dashboard = null,
            SupportsOverview = null,
            LoadedAt = null,
            AutoRefreshing = false,
            AutoRefreshPaused = false,
            Providers = UsageRead<ProviderUsageSummary>.Idle,
            Detail = null,
            ExportCoverage = null,
            ExportBlock = null,
        });
        return true;
    }

    // Cancelled loads never post a result, so any section still marked Loading falls back to its last value.
    private static UsagePageState SettleLoading(UsagePageState s) => s with
    {
        Phase = s.Report is null ? UsageLoadPhase.Idle : UsageLoadPhase.Loaded,
        Providers = Settle(s.Providers),
        Detail = s.Detail is { } d ? d with { Row = Settle(d.Row) } : null,
    };

    private static UsageRead<T> Settle<T>(UsageRead<T> read) where T : class =>
        read.Phase == UsageLoadPhase.Loading
            ? read with { Phase = read.Value is null ? UsageLoadPhase.Idle : UsageLoadPhase.Loaded }
            : read;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _providers.Changed -= OnProvidersChanged;
        _ownedProviders?.Dispose();
        _noticeCts.Cancel();
        _noticeCts.Dispose();
        _exportAlternative?.TrySetResult(false);
        _exportAlternative = null;
        CancelAll();
        _exportCts.Cancel();
        Changed = null;
    }

    private DateOnly Today(UsageTimeZoneMode zone) => zone == UsageTimeZoneMode.Utc ? _utcToday() : _today();

    // Relative presets resolve against today in the selected time zone. Unchanged dates keep the filters as they are.
    private UsageFilters Rebased(UsageFilters filters)
    {
        if (filters.Preset == UsagePreset.Custom) return filters;
        var rebased = UsageFilters.ForPreset(filters, filters.Preset, Today(filters.TimeZone));
        return rebased.Start == filters.Start && rebased.End == filters.End ? filters : rebased;
    }

    // ── filters and display ──

    public void SetPreset(UsagePreset preset) => Query(s => UsageFilters.ForPreset(s, preset, Today(s.TimeZone)));

    public void SetRange(DateOnly start, DateOnly end) => Query(s => s.WithRange(start, end));

    public void SetTimeZone(UsageTimeZoneMode zone) => Query(s => Rebased(s with { TimeZone = zone }));

    public void SetScope(UsageScope scope) => Query(s => s with { Scope = scope, Offset = 0 });

    public void SetQuery(string query) => ServerQuery(s => s with { Query = query.Trim(), Offset = 0 });

    /// <summary>Adds or removes a <c>key:value</c> facet term in the search text.</summary>
    public void ToggleQueryTerm(string term)
    {
        var tokens = State.Filters.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!tokens.Remove(term)) tokens.Add(term);
        SetQuery(string.Join(' ', tokens));
    }

    public void ClearQuery() => ServerQuery(s => s with { Query = "", Offset = 0 });

    public void SetSort(UsageSort sort) => ServerQuery(s => s with { Sort = sort, Offset = 0 });

    public void ToggleSortDirection() => ServerQuery(s => s with { SortDescending = !s.SortDescending, Offset = 0 });

    public void SetPage(int offset) => ServerQuery(s => s with { Offset = Math.Max(0, offset) });

    // Gateways without the server-side overview cannot apply these, so they are ignored rather than shown as active.
    private void ServerQuery(Func<UsageFilters, UsageFilters> change)
    {
        if (!State.ServerFiltersUnavailable) Query(change);
    }

    public void SetMetric(UsageMetric metric)
    {
        Update(s => s with { Metric = metric });
        Reproject();
    }

    private CancellationTokenSource _noticeCts = new();
    private TaskCompletionSource<bool>? _exportAlternative;

    public void SetExportInteraction(bool active) => Update(s => s with { ExportInteraction = active });

    public void SetExportNotice(UsageExportNotice? notice)
    {
        _noticeCts.Cancel();
        _noticeCts.Dispose();
        _noticeCts = new();
        Update(s => s with { ExportNotice = notice });
        if (notice is not null) _ = DismissNoticeAsync(notice, _noticeCts.Token);
    }

    private async Task DismissNoticeAsync(UsageExportNotice notice, CancellationToken token)
    {
        try
        {
            await _delay(TimeSpan.FromMilliseconds(UsageNotificationPolicy.AutoDismissMs(notice.Folder is not null)), token).ConfigureAwait(false);
            _post(() =>
            {
                if (!token.IsCancellationRequested && ReferenceEquals(State.ExportNotice, notice))
                    Update(s => s with { ExportNotice = null });
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public Task<bool> OfferDailyExportAsync(UsageFilters filters)
    {
        _exportAlternative?.TrySetResult(false);
        _exportAlternative = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Update(s => s with { ExportBlock = new(filters, s.ExportCoverage ?? new(0, null)) });
        return _exportAlternative.Task;
    }

    public void CompleteExportAlternative(bool daily)
    {
        var completion = _exportAlternative;
        _exportAlternative = null;
        Update(s => s with { ExportBlock = null });
        completion?.TrySetResult(daily);
    }

    public void InspectProvider(string provider) => Update(s => s with
    {
        FocusProvider = provider,
        FocusProviderRequest = s.FocusProviderRequest + 1,
    });

    // ── session details ──

    /// <summary>Opens a session's details, or closes them when the same session is selected again.</summary>
    public void SelectSession(string key, string? agentId)
    {
        var client = _client();
        // A row clicked before the replacement client's activation belongs to the previous Gateway.
        if (ForgetReplacedGateway(client)) return;
        if (State.Availability == UsageAvailability.Unsupported) return;
        if (State.Detail?.Key == key)
        {
            CloseDetail();
            return;
        }
        LoadDetail(client, key, agentId);
        Reproject();
    }

    public void CloseDetail()
    {
        if (State.Detail is null) return;
        CancelDetail();
        Update(s => s with { Detail = null });
        Reproject();
    }

    // ── export ──

    /// <summary>
    /// Re-reads every matching session and returns file content, or an error kind. Returns neither when the page
    /// closed or the Gateway changed during the read, so no save dialog opens for it. Called on the UI thread; the
    /// read resumes there so the client check sees the current Gateway.
    /// </summary>
    public async Task<(string? Content, UsageExportError? Error)> ExportAsync(UsageExportFormat format, UsageFilters? snapshot = null)
    {
        var client = _client();
        if (client is not { IsConnectedToGateway: true })
            return (null, UsageExportError.Offline);
        var filters = snapshot ?? State.Filters;
        var token = _exportCts.Token;
        Update(s => s with { Exporting = true, ExportCoverage = null });
        try
        {
            var query = filters.ToOverviewQuery() with
            {
                Offset = 0,
                OverviewProjection = format == UsageExportFormat.DailyCsv,
                Limit = format == UsageExportFormat.DailyCsv ? SessionsUsageQuery.OverviewRowLimit : SessionsUsageQuery.FullRowLimit,
            };
            var result = await client.GetSessionsUsageAsync(query, cancellationToken: token);
            if (_disposed || !ReferenceEquals(_client(), client)) return (null, null);
            if (!result.IsOk) return (null, UsageExportError.Failed);
            var report = result.Value!;
            var count = report.Aggregates.SessionCount ?? report.Overview?.Total;
            Update(s => s with { ExportCoverage = new(report.Sessions.Count, count >= report.Sessions.Count ? count : null) });
            if (!UsageExport.IsExportable(report)) return (null, UsageExportError.Incomplete);
            if (format == UsageExportFormat.SessionsCsv && UsageExport.IsTruncated(report))
                return (null, UsageExportError.TooMany);
            if (format == UsageExportFormat.SessionsCsv
                && report.Sessions.Any(session => session.Computing || session.Usage is null || session.Usage.Totals.IsLowerBound))
                return (null, UsageExportError.Incomplete);
            if (format == UsageExportFormat.DailyCsv && report.Aggregates.CostDaily.Any(day => day.Totals.IsLowerBound))
                return (null, UsageExportError.Incomplete);
            return (UsageExport.Build(format, report), null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return (null, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or System.Text.Json.JsonException)
        {
            return (null, UsageExportError.Failed);
        }
        finally
        {
            _post(() => Update(s => s with { Exporting = false }));
        }
    }

    // ── loading ──

    private void Query(Func<UsageFilters, UsageFilters> change)
    {
        var previous = State.Filters;
        var filters = change(previous);
        if (filters == previous) return;
        var client = _client();
        ChangeFilters(filters);
        Reload(client, resetRechecks: true);
        // Details cover the selected range, time zone and scope; search, sort and paging do not change them.
        if (State.Detail is { } detail
            && (filters.Start != previous.Start || filters.End != previous.End
                || filters.TimeZone != previous.TimeZone || filters.Scope != previous.Scope))
            LoadDetail(client, detail.Key, detail.AgentId);
    }

    private void ChangeFilters(UsageFilters filters)
    {
        if (filters == State.Filters) return;
        var detailChanged = filters.Start != State.Filters.Start || filters.End != State.Filters.End
            || filters.TimeZone != State.Filters.TimeZone || filters.Scope != State.Filters.Scope;
        if (detailChanged) CancelDetail();
        Update(s => s with
        {
            Filters = filters,
            Report = null,
            Dashboard = null,
            LoadedAt = null,
            Phase = UsageLoadPhase.Idle,
            Error = null,
            Detail = detailChanged && s.Detail is { } detail
                ? detail with { Row = UsageRead<SessionUsageRow>.Idle } : s.Detail,
        });
    }

    private void Reload(IOperatorGatewayClient? client, bool resetRechecks)
    {
        if (_disposed) return;
        ForgetReplacedGateway(client);
        if (client is not { IsConnectedToGateway: true })
        {
            CancelAll();
            Update(s => SettleLoading(s) with { Availability = UsageAvailability.Disconnected, AutoRefreshing = false });
            return;
        }
        if (resetRechecks) _rechecks = 0;
        _recheckCts.Cancel();
        _overviewCts.Cancel();
        _overviewCts = new CancellationTokenSource();
        var token = _overviewCts.Token;
        var generation = ++_overviewGeneration;
        var filters = State.Filters;
        Update(s => s with { Phase = UsageLoadPhase.Loading, Error = null });
        _ = LoadOverviewAsync(client, filters, generation, token);
    }

    private async Task LoadOverviewAsync(
        IOperatorGatewayClient client, UsageFilters filters, int generation, CancellationToken token)
    {
        UsageReadResult<SessionsUsageReport> result;
        try
        {
            result = await client.GetSessionsUsageAsync(filters.ToOverviewQuery(), cancellationToken: token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        _post(() =>
        {
            if (_disposed || generation != _overviewGeneration) return;
            ApplyOverview(result);
        });
    }

    // Keeps only the filters the answering Gateway applied.
    private static UsageFilters Supported(UsageFilters filters, SessionsUsageReport report) =>
        report.Overview is null ? filters.WithoutServerFilters() : filters;

    private void ApplyOverview(UsageReadResult<SessionsUsageReport> result)
    {
        switch (result.Status)
        {
            case UsageReadStatus.Ok:
                ApplyReport(result.Value!);
                ScheduleRecheck(result.Value!);
                break;
            case UsageReadStatus.Disconnected:
                Update(s => s with { Availability = UsageAvailability.Disconnected, Phase = s.Report is null ? UsageLoadPhase.Idle : UsageLoadPhase.Loaded, AutoRefreshing = false });
                break;
            case UsageReadStatus.Forbidden:
                Update(s => s with { Availability = UsageAvailability.Forbidden, Phase = UsageLoadPhase.Failed, Error = result.Error, AutoRefreshing = false });
                break;
            case UsageReadStatus.Unsupported:
                // Older than any Gateway setup installs: ask for an update instead of showing partial figures.
                CancelDetail();
                Update(s => s with
                {
                    Availability = UsageAvailability.Unsupported,
                    Phase = UsageLoadPhase.Loaded,
                    Filters = s.Filters.WithoutServerFilters(),
                    Detail = null,
                    Report = null,
                    Dashboard = null,
                    LoadedAt = DateTimeOffset.Now,
                    Error = null,
                    AutoRefreshing = false,
                });
                break;
            default:
                Update(s => s with { Phase = UsageLoadPhase.Failed, Error = result.Error, AutoRefreshing = false });
                break;
        }
    }

    private void ApplyReport(SessionsUsageReport report)
    {
        Update(s =>
        {
            var filters = Supported(s.Filters, report);
            return s with
            {
                Availability = UsageAvailability.Connected,
                Phase = UsageLoadPhase.Loaded,
                Filters = filters,
                Report = report,
                SupportsOverview = report.Overview is not null,
                Dashboard = UsageDashboardProjection.Project(report, filters, s.Metric, s.Detail?.Key),
                LoadedAt = DateTimeOffset.Now,
                Error = null,
            };
        });
    }

    private void ScheduleRecheck(SessionsUsageReport report)
    {
        var incomplete = report.CacheStatus?.IsIncomplete == true || report.Partial is not null
            || report.Sessions.Any(s => s.Computing);
        if (!incomplete)
        {
            Update(s => s with { AutoRefreshing = false, AutoRefreshPaused = false });
            return;
        }
        if (_rechecks >= UsageRefreshPolicy.MaxAutomaticRechecks)
        {
            Update(s => s with { AutoRefreshing = false, AutoRefreshPaused = true });
            return;
        }
        _rechecks++;
        _recheckCts = new CancellationTokenSource();
        var token = _recheckCts.Token;
        Update(s => s with { AutoRefreshing = true, AutoRefreshPaused = false });
        _ = RecheckAfterDelayAsync(token);
    }

    private async Task RecheckAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await _delay(UsageRefreshPolicy.RecheckInterval, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        _post(() =>
        {
            if (!_disposed && !token.IsCancellationRequested) Reload(_client(), resetRechecks: false);
        });
    }

    private void OnProvidersChanged()
    {
        var providers = _providers.State;
        Update(s => s with
        {
            Providers = ReferenceEquals(providers.Client, _stateClient)
                ? providers.Read : UsageRead<ProviderUsageSummary>.Idle,
            ProviderFreshnessRevision = s.ProviderFreshnessRevision + 1,
        });
        if (_providerRefreshPending) RefreshBoundProviders(_client());
    }

    private void RefreshBoundProviders(IOperatorGatewayClient? client)
    {
        if (_disposed || !_providerRefreshPending) return;
        var providers = _providers.State;
        if (!ReferenceEquals(client, _stateClient) || !ReferenceEquals(providers.Client, client)
            || client is not { IsConnectedToGateway: true }) return;
        // Consume before Refresh raises Changed. A matching in-flight read already fulfills activation.
        _providerRefreshPending = false;
        if (providers.Read.IsLoading) return;
        _providers.Refresh(client);
        if (_providers.State.Read.Phase == UsageLoadPhase.Idle)
            _providerRefreshPending = true;
    }

    private void LoadDetail(IOperatorGatewayClient? client, string key, string? agentId)
    {
        CancelDetail();
        var previous = State.Detail?.Key == key ? State.Detail.Row.Value : null;
        Update(s => s with
        {
            Detail = new UsageDetailState(key, agentId, new UsageRead<SessionUsageRow>(UsageLoadPhase.Loading, previous, null)),
        });
        if (client is not { IsConnectedToGateway: true })
        {
            Update(s => s with { Detail = s.Detail is { } d ? d with { Row = Settle(d.Row) } : null });
            return;
        }
        var token = _detailCts.Token;
        var generation = ++_detailGeneration;
        var filters = State.Filters;
        _ = LoadDetailAsync(client, filters.ToDetailQuery(key, agentId), key, generation, token);
    }

    private async Task LoadDetailAsync(
        IOperatorGatewayClient client, SessionsUsageQuery query, string key, int generation, CancellationToken token)
    {
        UsageReadResult<SessionsUsageReport> result;
        try
        {
            result = await client.GetSessionsUsageAsync(query, cancellationToken: token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        _post(() =>
        {
            if (_disposed || generation != _detailGeneration || State.Detail?.Key != key) return;
            var row = result.IsOk
                ? new UsageRead<SessionUsageRow>(UsageLoadPhase.Loaded, result.Value!.Sessions.FirstOrDefault(s => s.Key == key), null)
                : new UsageRead<SessionUsageRow>(UsageLoadPhase.Failed, null, result.Error, result.Status);
            Update(s => s with { Detail = s.Detail is { } d ? d with { Row = row } : null });
        });
    }

    // ── state plumbing ──

    private void Reproject()
    {
        if (State.Report is { } report)
            Update(s => s with
            {
                Dashboard = UsageDashboardProjection.Project(report, s.Filters, s.Metric, s.Detail?.Key),
            });
    }

    private void Update(Func<UsagePageState, UsagePageState> change)
    {
        if (_disposed) return;
        var next = change(State);
        if (ReferenceEquals(next, State)) return;
        State = next;
        Changed?.Invoke();
    }

    private void CancelDetail()
    {
        _detailCts.Cancel();
        _detailCts = new CancellationTokenSource();
        _detailGeneration++;
    }

    private void CancelAll()
    {
        _overviewCts.Cancel();
        _recheckCts.Cancel();
        _overviewGeneration++;
        CancelDetail();
    }
}

internal enum UsageExportError
{
    Offline,
    Failed,
    OpenFolder,
    Incomplete,
    /// <summary>More sessions match than one Gateway read returns, so a sessions export would drop rows.</summary>
    TooMany,
}

/// <summary>Export outcome: a saved file name, or an error kind (null when the user cancelled).</summary>
internal sealed record UsageExportNotice(string? FileName, UsageExportError? Error, string? Folder = null);

internal static class UsageNotificationPolicy
{
    public const int DefaultAutoDismissMs = 7000;
    public const int DefaultPassiveAutoDismissMs = 4000;
    public static int AutoDismissMs(bool hasAction) => int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_USAGE_NOTIFICATION_MS"), out var value)
        && value >= 1000 ? value : hasAction ? DefaultAutoDismissMs : DefaultPassiveAutoDismissMs;
}
