using OpenClaw.Shared;
using OpenClaw.Shared.Usage;

namespace OpenClawTray.Presentation.Usage;

internal enum UsageAvailability
{
    Connected,
    Disconnected,
    /// <summary>The Gateway predates <c>sessions.usage</c>; the page shows <c>usage.cost</c> totals only.</summary>
    Legacy,
    /// <summary>The connection lacks <c>operator.read</c>.</summary>
    Forbidden,
}

internal enum UsageLoadPhase
{
    Idle,
    Loading,
    Loaded,
    Failed,
}

/// <summary>One section's read. <see cref="Failure"/> names the failed outcome when the Gateway sent no error text.</summary>
internal sealed record UsageRead<T>(UsageLoadPhase Phase, T? Value, string? Error, UsageReadStatus? Failure = null) where T : class
{
    public static readonly UsageRead<T> Idle = new(UsageLoadPhase.Idle, null, null);
    public bool IsLoading => Phase == UsageLoadPhase.Loading;
}

internal sealed record UsageDetailState(
    string Key,
    string? AgentId,
    UsageRead<SessionUsageRow> Row,
    UsageRead<SessionUsageTimeseries> Timeseries,
    UsageRead<IReadOnlyList<UsageLogView>> Logs,
    UsageTimelineMode TimelineMode = UsageTimelineMode.Cumulative,
    bool TimelineByType = false);

internal sealed record UsagePageState
{
    public required UsageFilters Filters { get; init; }
    public UsageMetric Metric { get; init; } = UsageMetric.Tokens;
    public UsageDailyChartMode ChartMode { get; init; } = UsageDailyChartMode.Total;
    public UsageAvailability Availability { get; init; } = UsageAvailability.Disconnected;
    public UsageLoadPhase Phase { get; init; } = UsageLoadPhase.Idle;
    public string? Error { get; init; }
    public SessionsUsageReport? Report { get; init; }
    public UsageDashboard? Dashboard { get; init; }
    public DateTimeOffset? LoadedAt { get; init; }
    /// <summary>True while an incomplete result is being re-checked automatically.</summary>
    public bool AutoRefreshing { get; init; }
    public bool AutoRefreshPaused { get; init; }
    public UsageRead<ProviderUsageSummary> Providers { get; init; } = UsageRead<ProviderUsageSummary>.Idle;
    public UsageDetailState? Detail { get; init; }
    public IReadOnlyList<string> RecentKeys { get; init; } = Array.Empty<string>();
    public bool HeaderPinned { get; init; }
    public bool Exporting { get; init; }
    /// <summary>Outcome of the last user-requested export, shown until the next export or dismissal.</summary>
    public UsageExportNotice? ExportNotice { get; init; }

    /// <summary>
    /// The connected Gateway returns full rows without the server-side overview (released 2026.10.7), so query,
    /// day and hour filters, session sort and paging, and the activity view are unavailable.
    /// </summary>
    public bool BasicGateway => Availability == UsageAvailability.Connected && Report is { Overview: null };

    /// <summary>
    /// Query, day, hour, sort, paging and recent filters need the server-side overview. Basic Gateways ignore them and
    /// legacy <c>usage.cost</c> Gateways cannot apply any filter, so the page neither sends nor shows them.
    /// </summary>
    public bool ServerFiltersUnavailable => BasicGateway || Availability == UsageAvailability.Legacy;

    public bool ShowLoadError => Phase == UsageLoadPhase.Failed
        && Availability is UsageAvailability.Connected or UsageAvailability.Legacy;
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
    private CancellationTokenSource _providersCts = new();
    private CancellationTokenSource _detailCts = new();
    private CancellationTokenSource _recheckCts = new();
    private readonly CancellationTokenSource _exportCts = new();
    private int _overviewGeneration;
    private int _providersGeneration;
    private int _detailGeneration;
    private int _rechecks;
    private bool _disposed;

    public UsagePageController(
        Func<IOperatorGatewayClient?> client, Func<DateOnly> today, Action<Action> post,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateOnly>? utcToday = null)
    {
        _client = client;
        _today = today;
        _utcToday = utcToday ?? (() => DateOnly.FromDateTime(DateTime.UtcNow));
        _post = post;
        _delay = delay ?? Task.Delay;
        State = new UsagePageState { Filters = UsageFilters.Default(today()) };
    }

    public UsagePageState State { get; private set; }

    public event Action? Changed;

    // ── lifecycle ──

    /// <summary>Re-evaluates the connection and reloads. Call on open, reconnect and client replacement.</summary>
    public void Activate()
    {
        var client = _client();
        if (client is not { IsConnectedToGateway: true })
        {
            CancelAll();
            Update(s => SettleLoading(s) with { Availability = UsageAvailability.Disconnected, AutoRefreshing = false });
            return;
        }
        // Availability is re-learned from each load, so a Gateway upgraded while disconnected is retried.
        // Relative presets move with the calendar, so a page left open past midnight reloads the current range.
        Update(s => s with
        {
            Availability = s.Availability == UsageAvailability.Disconnected ? UsageAvailability.Connected : s.Availability,
            Filters = Rebased(s.Filters),
        });
        Reload(resetRechecks: true);
        LoadProviders(client);
        if (State.Detail is { } detail) LoadDetail(detail.Key, detail.AgentId);
    }

    public void Refresh()
    {
        Update(s => s with { AutoRefreshPaused = false });
        Activate();
    }

    // Cancelled loads never post a result, so any section still marked Loading falls back to its last value.
    private static UsagePageState SettleLoading(UsagePageState s) => s with
    {
        Phase = s.Report is null ? UsageLoadPhase.Idle : UsageLoadPhase.Loaded,
        Providers = Settle(s.Providers),
        Detail = s.Detail is { } d
            ? d with { Row = Settle(d.Row), Timeseries = Settle(d.Timeseries), Logs = Settle(d.Logs) }
            : null,
    };

    private static UsageRead<T> Settle<T>(UsageRead<T> read) where T : class =>
        read.Phase == UsageLoadPhase.Loading
            ? read with { Phase = read.Value is null ? UsageLoadPhase.Idle : UsageLoadPhase.Loaded }
            : read;

    /// <summary>
    /// Legacy <c>usage.cost</c> only counts days back from today, so request enough days to reach the range start
    /// and keep only the selected days, with totals recomputed from them.
    /// </summary>
    internal static int LegacyRequestDays(UsageFilters filters, DateOnly today) =>
        Math.Clamp(today.DayNumber - filters.Start.DayNumber + 1, 1, 366);

    internal static SessionsUsageReport ClipLegacyReport(SessionsUsageReport report, UsageFilters filters)
    {
        var daily = report.Aggregates.CostDaily;
        if (daily.Count == 0) return report;
        var start = filters.Start.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var end = filters.End.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var kept = daily.Where(d => string.CompareOrdinal(d.Date, start) >= 0 && string.CompareOrdinal(d.Date, end) <= 0).ToList();
        if (kept.Count == daily.Count) return report;
        var totals = new UsageTotals
        {
            Report = kept.Any(d => d.Totals.IsLowerBound) ? UsageReportState.Unavailable : UsageReportState.Unknown,
            Input = kept.Sum(d => d.Totals.Input),
            Output = kept.Sum(d => d.Totals.Output),
            CacheRead = kept.Sum(d => d.Totals.CacheRead),
            CacheWrite = kept.Sum(d => d.Totals.CacheWrite),
            TotalTokens = kept.Sum(d => d.Totals.TotalTokens),
            TotalCost = kept.Sum(d => d.Totals.TotalCost),
            InputCost = kept.Sum(d => d.Totals.InputCost),
            OutputCost = kept.Sum(d => d.Totals.OutputCost),
            CacheReadCost = kept.Sum(d => d.Totals.CacheReadCost),
            CacheWriteCost = kept.Sum(d => d.Totals.CacheWriteCost),
            MissingCostEntries = kept.Sum(d => d.Totals.MissingCostEntries),
        };
        return report with
        {
            StartDate = kept.Count > 0 ? kept[0].Date : start,
            EndDate = kept.Count > 0 ? kept[^1].Date : end,
            Totals = totals,
            Aggregates = report.Aggregates with { CostDaily = kept },
        };
    }

    public void Dispose()
    {
        _disposed = true;
        CancelAll();
        _exportCts.Cancel();
        Changed = null;
    }

    private DateOnly Today(UsageTimeZoneMode zone) => zone == UsageTimeZoneMode.Utc ? _utcToday() : _today();

    // Relative presets resolve against today in the selected time zone. Unchanged dates keep day selections.
    private UsageFilters Rebased(UsageFilters filters)
    {
        if (filters.Preset == UsagePreset.Custom) return filters;
        var rebased = UsageFilters.ForPreset(filters, filters.Preset, Today(filters.TimeZone));
        return rebased.Start == filters.Start && rebased.End == filters.End ? filters : rebased;
    }

    // ── filters and display ──

    public void SetPreset(UsagePreset preset) => Query(s => UsageFilters.ForPreset(s, preset, Today(s.TimeZone)));

    public void SetRange(DateOnly start, DateOnly end) => Query(s => s.WithRange(start, end));

    public void SetTimeZone(UsageTimeZoneMode zone) =>
        Query(s => Rebased(s with { TimeZone = zone, SelectedHours = [], SelectedDays = [] }));

    public void SetScope(UsageScope scope) => Query(s => s with { Scope = scope, Offset = 0 });

    public void SetCreator(string? key) => Query(s => s with { CreatorKey = s.CreatorKey == key ? null : key, Offset = 0 });

    public void SetQuery(string query) => ServerQuery(s => s with { Query = query.Trim(), Offset = 0 });

    /// <summary>Adds or removes a <c>key:value</c> term, as the web filter chips do.</summary>
    public void ToggleQueryTerm(string term)
    {
        var tokens = State.Filters.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!tokens.Remove(term)) tokens.Add(term);
        SetQuery(string.Join(' ', tokens));
    }

    public void ToggleDay(string iso, bool append) =>
        ServerQuery(s => s with { SelectedDays = UsageFilters.Toggle(s.SelectedDays, iso, append), Offset = 0 });

    public void ToggleHour(int hour, bool append) =>
        ServerQuery(s => s with { SelectedHours = UsageFilters.Toggle(s.SelectedHours, hour, append), Offset = 0 });

    public void ClearAllFilters() =>
        Query(s => s with { Query = "", CreatorKey = null, SelectedDays = [], SelectedHours = [], SelectedSessions = [], Offset = 0 }, closeDetail: true);

    public void SetSort(UsageSort sort) => ServerQuery(s => s with { Sort = sort, Offset = 0 });

    public void ToggleSortDirection() => ServerQuery(s => s with { SortDescending = !s.SortDescending, Offset = 0 });

    public void SetRecentOnly(bool recent) => ServerQuery(s => s with { RecentOnly = recent, Offset = 0 });

    public void SetPage(int offset) => ServerQuery(s => s with { Offset = Math.Max(0, offset) });

    // Gateways without the server-side overview cannot apply these, so they are ignored rather than shown as active.
    private void ServerQuery(Func<UsageFilters, UsageFilters> change)
    {
        if (!State.ServerFiltersUnavailable) Query(change);
    }

    public void SetMetric(UsageMetric metric) => Display(s => s with { Metric = metric });

    public void SetChartMode(UsageDailyChartMode mode) => Display(s => s with { ChartMode = mode });

    public void SetHeaderPinned(bool pinned) => Update(s => s with { HeaderPinned = pinned });

    public void SetExportNotice(UsageExportNotice? notice) => Update(s => s with { ExportNotice = notice });

    // ── sessions and details ──

    /// <summary>Plain click selects one session and opens its details; append toggles a multi-selection.</summary>
    public void SelectSession(string key, string? agentId, bool append)
    {
        if (State.Availability == UsageAvailability.Legacy) return;
        var selected = UsageFilters.Toggle(State.Filters.SelectedSessions, key, append);
        var recent = new[] { key }.Concat(State.RecentKeys.Where(k => k != key)).Take(SessionsUsageQuery.MaxRecentKeys).ToArray();
        Update(s => s with { RecentKeys = recent });
        Query(s => s with { SelectedSessions = selected }, closeDetail: selected.Count == 0, selectionOnly: true);
        if (selected.Count > 0)
        {
            var primary = selected[0];
            if (State.Detail?.Key != primary)
                LoadDetail(primary, primary == key ? agentId : State.Report?.Sessions.FirstOrDefault(r => r.Key == primary)?.AgentId);
        }
    }

    public void CloseDetail() => Query(s => s with { SelectedSessions = [] }, closeDetail: true, selectionOnly: true);

    public void SetTimelineMode(UsageTimelineMode mode) => DetailUpdate(d => d with { TimelineMode = mode });

    public void SetTimelineByType(bool byType) => DetailUpdate(d => d with { TimelineByType = byType });

    // ── export ──

    /// <summary>
    /// Re-reads every matching session (upstream behavior) and returns file content, or an error kind. Returns
    /// neither when the page closed or the Gateway changed during the read, so no save dialog opens for it.
    /// Called on the UI thread; the read resumes there so the client check sees the current Gateway.
    /// </summary>
    public async Task<(string? Content, UsageExportError? Error)> ExportAsync(UsageExportFormat format)
    {
        var client = _client();
        if (client is not { IsConnectedToGateway: true })
            return (null, UsageExportError.Offline);
        var filters = State.Filters;
        var token = _exportCts.Token;
        Update(s => s with { Exporting = true });
        try
        {
            var query = filters.ToOverviewQuery(null) with
            {
                Offset = 0,
                RecentKeys = null,
                IncludeContextWeight = format == UsageExportFormat.Json,
                OverviewProjection = format == UsageExportFormat.DailyCsv,
                Limit = format == UsageExportFormat.DailyCsv ? SessionsUsageQuery.OverviewRowLimit : SessionsUsageQuery.FullRowLimit,
            };
            var result = await client.GetSessionsUsageAsync(query, cancellationToken: token);
            if (_disposed || !ReferenceEquals(_client(), client)) return (null, null);
            if (!result.IsOk) return (null, UsageExportError.Failed);
            var report = result.Value!;
            if (!UsageExport.IsExportable(report)) return (null, UsageExportError.Incomplete);
            if (format != UsageExportFormat.DailyCsv && UsageExport.IsTruncated(report, filters.SelectedSessions))
                return (null, UsageExportError.TooMany);
            return (UsageExport.Build(format, report, filters.SelectedSessions), null);
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

    private void Query(Func<UsageFilters, UsageFilters> change, bool closeDetail = false, bool selectionOnly = false)
    {
        var filters = change(State.Filters);
        if (filters == State.Filters && !closeDetail) return;
        Update(s => s with { Filters = filters, Detail = closeDetail ? null : s.Detail });
        if (closeDetail) CancelDetail();
        // Released Gateways ignore session selection, so re-reading every row would only repeat the same reply.
        if (selectionOnly && State.BasicGateway)
        {
            Reproject();
            return;
        }
        Reload(resetRechecks: true);
        if (!closeDetail && State.Detail is { } detail && filters.SelectedSessions.Contains(detail.Key))
            LoadDetail(detail.Key, detail.AgentId);
    }

    private void Display(Func<UsagePageState, UsagePageState> change)
    {
        Update(change);
        Reproject();
    }

    private void Reload(bool resetRechecks)
    {
        if (_disposed) return;
        var client = _client();
        if (client is not { IsConnectedToGateway: true })
        {
            Update(s => s with { Availability = UsageAvailability.Disconnected });
            return;
        }
        if (resetRechecks) _rechecks = 0;
        _recheckCts.Cancel();
        _overviewCts.Cancel();
        _overviewCts = new CancellationTokenSource();
        var token = _overviewCts.Token;
        var generation = ++_overviewGeneration;
        var filters = State.Filters;
        var recent = State.RecentKeys;
        Update(s => s with { Phase = UsageLoadPhase.Loading, Error = null });
        _ = LoadOverviewAsync(client, filters, recent, generation, token);
    }

    private async Task LoadOverviewAsync(
        IOperatorGatewayClient client, UsageFilters filters, IReadOnlyList<string> recent, int generation, CancellationToken token)
    {
        UsageReadResult<SessionsUsageReport> result;
        var legacy = false;
        try
        {
            result = await client.GetSessionsUsageAsync(filters.ToOverviewQuery(recent), cancellationToken: token).ConfigureAwait(false);
            if (result.Status == UsageReadStatus.Unsupported)
            {
                legacy = true;
                var today = Today(filters.TimeZone);
                result = await client.GetUsageCostSummaryAsync(LegacyRequestDays(filters, today), cancellationToken: token).ConfigureAwait(false);
                if (result is { IsOk: true, Value: { } legacyReport })
                    result = UsageReadResult<SessionsUsageReport>.Ok(ClipLegacyReport(legacyReport, filters));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        _post(() =>
        {
            if (_disposed || generation != _overviewGeneration) return;
            ApplyOverview(result, legacy);
        });
    }

    // Keeps only the filters the answering Gateway applied. Legacy usage.cost applies none beyond the date range.
    private static UsageFilters Supported(UsageFilters filters, bool legacy, SessionsUsageReport report) =>
        legacy ? filters.WithoutServerFilters() with { CreatorKey = null, SelectedSessions = [] }
        : report.Overview is null ? filters.WithoutServerFilters()
        : filters;

    private void ApplyOverview(UsageReadResult<SessionsUsageReport> result, bool legacy)
    {
        switch (result.Status)
        {
            case UsageReadStatus.Ok:
                var report = result.Value!;
                if (legacy) CancelDetail();
                // Projected with the current filters: basic Gateways change the session selection locally, without a
                // reload, so the filters captured when this read started can be older. Other filter changes start a
                // new read, which discards this reply by generation.
                Update(s =>
                {
                    var filters = Supported(s.Filters, legacy, report);
                    return s with
                    {
                        Availability = legacy ? UsageAvailability.Legacy : UsageAvailability.Connected,
                        Phase = UsageLoadPhase.Loaded,
                        Filters = filters,
                        Detail = legacy ? null : s.Detail,
                        Report = report,
                        Dashboard = UsageDashboardProjection.Project(report, filters, s.Metric, s.ChartMode, Today(filters.TimeZone)),
                        LoadedAt = DateTimeOffset.Now,
                        Error = null,
                    };
                });
                ScheduleRecheck(report);
                break;
            case UsageReadStatus.Disconnected:
                Update(s => s with { Availability = UsageAvailability.Disconnected, Phase = s.Report is null ? UsageLoadPhase.Idle : UsageLoadPhase.Loaded, AutoRefreshing = false });
                break;
            case UsageReadStatus.Forbidden:
                Update(s => s with { Availability = UsageAvailability.Forbidden, Phase = UsageLoadPhase.Failed, Error = result.Error, AutoRefreshing = false });
                break;
            case UsageReadStatus.Unsupported:
                // Neither sessions.usage nor usage.cost: an empty legacy page, not an error.
                CancelDetail();
                Update(s =>
                {
                    var empty = new SessionsUsageReport();
                    var filters = Supported(s.Filters, legacy: true, empty);
                    return s with
                    {
                        Availability = UsageAvailability.Legacy,
                        Phase = UsageLoadPhase.Loaded,
                        Filters = filters,
                        Detail = null,
                        Report = empty,
                        Dashboard = UsageDashboardProjection.Project(empty, filters, s.Metric, s.ChartMode, Today(filters.TimeZone)),
                        LoadedAt = DateTimeOffset.Now,
                    };
                });
                break;
            default:
                Update(s => s with { Phase = UsageLoadPhase.Failed, Error = result.Error, AutoRefreshing = false });
                break;
        }
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
            if (!_disposed && !token.IsCancellationRequested) Reload(resetRechecks: false);
        });
    }

    // usage.status can call provider billing APIs, so it is read on activation and refresh, not on every filter change.
    private void LoadProviders(IOperatorGatewayClient client)
    {
        _providersCts.Cancel();
        _providersCts = new CancellationTokenSource();
        var token = _providersCts.Token;
        var generation = ++_providersGeneration;
        Update(s => s with { Providers = s.Providers with { Phase = UsageLoadPhase.Loading } });
        _ = LoadProvidersAsync(client, generation, token);
    }

    private async Task LoadProvidersAsync(IOperatorGatewayClient client, int generation, CancellationToken token)
    {
        UsageReadResult<ProviderUsageSummary> result;
        try
        {
            result = await client.GetProviderUsageAsync(cancellationToken: token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        _post(() =>
        {
            if (_disposed || generation != _providersGeneration) return;
            // A refreshing or failed provider read keeps the last complete snapshot, like the web page.
            Update(s => s with
            {
                Providers = result.IsOk
                    ? new UsageRead<ProviderUsageSummary>(UsageLoadPhase.Loaded, result.Value, null)
                    : new UsageRead<ProviderUsageSummary>(UsageLoadPhase.Failed, s.Providers.Value, result.Error, result.Status),
            });
        });
    }

    private void LoadDetail(string key, string? agentId)
    {
        var client = _client();
        CancelDetail();
        var previous = State.Detail?.Key == key ? State.Detail : null;
        Update(s => s with
        {
            Detail = new UsageDetailState(
                key, agentId,
                new UsageRead<SessionUsageRow>(UsageLoadPhase.Loading, previous?.Row.Value, null),
                new UsageRead<SessionUsageTimeseries>(UsageLoadPhase.Loading, previous?.Timeseries.Value, null),
                new UsageRead<IReadOnlyList<UsageLogView>>(UsageLoadPhase.Loading, previous?.Logs.Value, null),
                previous?.TimelineMode ?? UsageTimelineMode.Cumulative,
                previous?.TimelineByType ?? false),
        });
        if (client is not { IsConnectedToGateway: true })
        {
            Update(s => s with { Detail = s.Detail is { } d ? d with { Row = Settle(d.Row), Timeseries = Settle(d.Timeseries), Logs = Settle(d.Logs) } : null });
            return;
        }
        var token = _detailCts.Token;
        var generation = ++_detailGeneration;
        var filters = State.Filters;
        _ = LoadDetailPartAsync(generation, token,
            () => client.GetSessionsUsageAsync(filters.ToDetailQuery(key, agentId), cancellationToken: token),
            (d, r) => d with { Row = Read(r, r.Value?.Sessions.FirstOrDefault(s => s.Key == key)) });
        _ = LoadDetailPartAsync(generation, token,
            () => client.GetSessionUsageTimeseriesAsync(key, agentId, cancellationToken: token),
            (d, r) => d with { Timeseries = Read(r, r.Value) });
        _ = LoadDetailPartAsync(generation, token,
            () => client.GetSessionUsageLogsAsync(key, agentId, cancellationToken: token),
            (d, r) => d with
            {
                Logs = Read(r, r.Value is { } logs
                    ? (IReadOnlyList<UsageLogView>)logs.Select(UsageSessionDetailProjection.ParseLog).ToList()
                    : null),
            });
    }

    private static UsageRead<TOut> Read<TIn, TOut>(UsageReadResult<TIn> result, TOut? value)
        where TIn : class where TOut : class =>
        result.IsOk
            ? new UsageRead<TOut>(UsageLoadPhase.Loaded, value, null)
            : new UsageRead<TOut>(UsageLoadPhase.Failed, null, result.Error, result.Status);

    private async Task LoadDetailPartAsync<T>(
        int generation, CancellationToken token, Func<Task<UsageReadResult<T>>> read,
        Func<UsageDetailState, UsageReadResult<T>, UsageDetailState> apply)
        where T : class
    {
        UsageReadResult<T> result;
        try
        {
            result = await read().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        _post(() =>
        {
            if (_disposed || generation != _detailGeneration || State.Detail is null) return;
            DetailUpdate(d => apply(d, result));
        });
    }

    // ── state plumbing ──

    private void Reproject()
    {
        if (State.Report is { } report)
            Update(s => s with { Dashboard = UsageDashboardProjection.Project(report, s.Filters, s.Metric, s.ChartMode, Today(s.Filters.TimeZone)) });
    }

    private void DetailUpdate(Func<UsageDetailState, UsageDetailState> change)
    {
        if (State.Detail is { } detail) Update(s => s with { Detail = change(detail) });
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
        _providersCts.Cancel();
        _recheckCts.Cancel();
        _overviewGeneration++;
        _providersGeneration++;
        CancelDetail();
    }
}

internal enum UsageExportError
{
    Offline,
    Failed,
    Incomplete,
    /// <summary>More sessions match than one Gateway read returns, so a sessions export would drop rows.</summary>
    TooMany,
}

/// <summary>Export outcome: a saved file name, or an error kind (null when the user cancelled).</summary>
internal sealed record UsageExportNotice(string? FileName, UsageExportError? Error);
