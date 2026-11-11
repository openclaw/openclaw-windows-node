using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace OpenClaw.Shared.Cron;

public enum AutomationsNoticeKind
{
    JobCompleted,
    RunQueued,
    RunNotStarted,
    RunFailed,
    Saved,
    Created,
    Removed,
    Paused,
    Resumed,
    Conflict,
    ConflictUnloadable,
    MissingRevision,
    OperationFailed,
    /// <summary>A job asked for by id (<see cref="AutomationsNotice.JobName"/>) is not on the Gateway any more.</summary>
    JobUnavailable,
}

/// <summary>How a reveal request resolved. <see cref="Superseded"/> means a newer load, a disconnect or disposal took over.</summary>
public enum AutomationRevealOutcome { Revealed, NotFound, Failed, Superseded }

/// <summary>Structured feedback; the UI picks wording. <see cref="Reason"/> is a Gateway reason code when known.</summary>
public sealed record AutomationsNotice(AutomationsNoticeKind Kind, string JobName, string? Reason = null, string? Detail = null, string? RunId = null)
{
    public long Id { get; init; }
    public bool IsError => Kind is AutomationsNoticeKind.RunFailed or AutomationsNoticeKind.OperationFailed
        or AutomationsNoticeKind.ConflictUnloadable or AutomationsNoticeKind.MissingRevision
        || (Kind == AutomationsNoticeKind.RunNotStarted && Reason is not ("not-due" or "already-running"));
    /// <summary>Neither a success nor a failure: the request cannot be honored as asked.</summary>
    public bool IsInformational => Kind == AutomationsNoticeKind.JobUnavailable
        || (Kind == AutomationsNoticeKind.RunNotStarted && !IsError);
}

public sealed record AutomationsSnapshot
{
    /// <summary>Agent of the route that opened the page; owns new automations when no single agent is filtered.</summary>
    public string AgentId { get; init; } = "main";
    /// <summary>Agent whose automations are listed, or null for every agent on the Gateway.</summary>
    public string? AgentFilter { get; init; } = "main";
    public bool Connected { get; init; }
    public bool CanManage { get; init; }
    /// <summary>Retains disabled offline controls only for a previously authorized operator.</summary>
    public bool LastKnownCanManage { get; init; }

    public CronListQuery ListQuery { get; init; } = new();
    public IReadOnlyList<CronJob> Jobs { get; init; } = Array.Empty<CronJob>();
    public int? Total { get; init; }
    public bool HasMore { get; init; }
    public bool ListLoading { get; init; }
    public bool ListLoadingMore { get; init; }
    public bool ListLoaded { get; init; }
    public string? ListError { get; init; }
    public bool ReadDenied { get; init; }
    /// <summary>Complete agent-filter inventory (all agents when null), independent of browse filters and pagination.</summary>
    public IReadOnlyList<CronJob> OverviewJobs { get; init; } = Array.Empty<CronJob>();
    public bool OverviewLoaded { get; init; }
    public string? OverviewError { get; init; }
    public long? LastSeenAtMs { get; init; }

    public CronSchedulerStatus? Status { get; init; }
    /// <summary>Local stats refresh failure, not a Gateway scheduler health field.</summary>
    public string? StatusError { get; init; }
    public int? ScopedTotal { get; init; }
    public int? FailingCount { get; init; }
    public long? ScopedNextWakeAtMs { get; init; }

    public string? SelectedJobId { get; init; }
    /// <summary>Exact definition from <c>cron.get</c>; falls back to the list row until it arrives.</summary>
    public CronJob? SelectedJob { get; init; }
    public string? SelectedError { get; init; }

    public ImmutableHashSet<string> RunningJobIds { get; init; } = ImmutableHashSet<string>.Empty;
    public ImmutableHashSet<string> BusyJobIds { get; init; } = ImmutableHashSet<string>.Empty;

    public CronRunsQuery RunsQuery { get; init; } = new();
    public IReadOnlyList<CronRunEntry> Runs { get; init; } = Array.Empty<CronRunEntry>();
    public bool RunsLoading { get; init; }
    public bool RunsLoaded { get; init; }
    public string? RunsError { get; init; }

    public AutomationsNotice? Notice { get; init; }

    public bool IsAllAgents => AgentFilter is null;
    /// <summary>Scoped inventory size from the Gateway, independent of browse filters; null until known.</summary>
    public int? InventoryTotal => OverviewLoaded ? OverviewJobs.Count : ScopedTotal;
    /// <summary>On/off split comes only from the complete overview; null rather than a partial-page guess.</summary>
    public int? ActiveCount => OverviewLoaded ? OverviewJobs.Count(j => j.Enabled && !j.IsAutoDisabled) : null;
    public int? PausedCount => OverviewLoaded ? OverviewJobs.Count(j => !j.Enabled || j.IsAutoDisabled) : null;
    /// <summary>Owner for a new automation: the filtered agent, else the route agent.</summary>
    public string DefaultAgentId => AgentFilter ?? AgentId;
    /// <summary>Agent narrowing preserves the ordinary empty state rather than a no-matches result.</summary>
    public bool IsFiltered => ListQuery.Query.Trim().Length > 0 || ListQuery.Enabled != CronEnabledFilter.All || ListQuery.HasAdvancedFilters;
    public bool IsRunning(string jobId) => RunningJobIds.Contains(jobId);

    /// <summary>Agents to offer in the filter: the route agent first, then known and listed owners.</summary>
    public IReadOnlyList<string> FilterAgents(IEnumerable<string> known) =>
        new[] { AgentId }
            .Concat(known.Concat(Jobs.Concat(OverviewJobs).Select(j => j.Owner).OfType<string>()).Append(AgentFilter ?? "")
                .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim())
                .Order(StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    public bool IsBusy(string jobId) => BusyJobIds.Contains(jobId);
}

public abstract record AutomationsSaveResult
{
    public sealed record Saved(CronJob Job) : AutomationsSaveResult;
    public sealed record Invalid(IReadOnlyDictionary<CronFormField, CronFormError> Errors) : AutomationsSaveResult;
    public sealed record Conflict(CronJob? Latest) : AutomationsSaveResult;
    public sealed record Failed(string Message) : AutomationsSaveResult;
    /// <summary>The edited job has no configuration revision, so nothing was sent.</summary>
    public sealed record MissingRevision : AutomationsSaveResult;
}

/// <summary>
/// Owns the automation inventory (one agent or every agent), scoped stats, selection,
/// selected-job recent results, run-now latching and mutations. UI-agnostic: every state change is
/// published through <see cref="Changed"/> on the <c>post</c> dispatcher.
/// </summary>
public sealed class AutomationsStore : IDisposable
{
    public static readonly TimeSpan RunPollInterval = TimeSpan.FromSeconds(3);
    public const int RunPollAttempts = 30;
    public static readonly TimeSpan RunSafetyTimeout = TimeSpan.FromSeconds(90);
    private const int RunPollLimit = 20;
    private const string IncompletePageMessage = "The Gateway returned an incomplete automation page.";

    private readonly Func<IOperatorGatewayClient?> _client;
    private readonly Action<Action> _post;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly AutomationsOverviewSource _overviewSource;
    private readonly bool _ownsOverviewSource;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, CancellationTokenSource> _runWatchers = new(StringComparer.Ordinal);
    // The local owner exists before acknowledgement, so an old list/response cannot retire a pending run.
    private sealed class RunInvocation
    {
        public string? RunId { get; set; }
    }
    private readonly Dictionary<string, RunInvocation> _localRuns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removedByUser = new(StringComparer.Ordinal);
    private AutomationsSnapshot _state;
    private long _listGeneration;
    private long _runsGeneration;
    private long _statsGeneration;
    private long _selectionGeneration;
    private long _overviewGeneration;
    private long _noticeId;
    private bool _listReloadPending;
    private CronListQuery? _loadedListQuery;
    private bool _disposed;

    public AutomationsStore(
        string agentId,
        Func<IOperatorGatewayClient?> client,
        Action<Action> post,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        AutomationsOverviewSource? overviewSource = null)
    {
        _client = client;
        _post = post;
        _delay = delay ?? Task.Delay;
        _ownsOverviewSource = overviewSource is null;
        _overviewSource = overviewSource ?? new AutomationsOverviewSource(client, post);
        _overviewSource.Changed += OnGlobalOverviewChanged;
        var agent = NormalizeAgent(agentId);
        _state = new AutomationsSnapshot
        {
            AgentId = agent,
            AgentFilter = agent,
            ListQuery = new CronListQuery { AgentId = agent },
            RunsQuery = new CronRunsQuery { AgentId = agent },
        };
    }

    public event EventHandler<AutomationsSnapshot>? Changed;
    /// <summary>Raised once per notice so hosts can mirror important ones as app notifications.</summary>
    public event EventHandler<AutomationsNotice>? NoticeRaised;

    public AutomationsSnapshot State => _state;

    private static string NormalizeAgent(string? agentId) => string.IsNullOrWhiteSpace(agentId) ? "main" : agentId.Trim();

    private void Set(Func<AutomationsSnapshot, AutomationsSnapshot> update)
    {
        if (_disposed) return;
        _state = update(_state);
        Changed?.Invoke(this, _state);
    }

    private void Post(Action action) => _post(() => { if (!_disposed) action(); });

    private void Notify(AutomationsNotice notice)
    {
        var numbered = notice with { Id = ++_noticeId };
        Set(s => s with { Notice = numbered });
        NoticeRaised?.Invoke(this, numbered);
    }

    public void DismissNotice() => Set(s => s with { Notice = null });

    // ─── Connection / lifecycle ───

    /// <summary>Re-reads connection and access, then reloads every visible region.</summary>
    public void Refresh()
    {
        var client = _client();
        _overviewSource.SetConnection(client);
        var connected = client is { IsConnectedToGateway: true };
        var canManage = connected && client!.GrantedOperatorScopes.Contains(CronProtocol.AdminScope, StringComparer.Ordinal);
        Set(s => s with
        {
            Connected = connected, CanManage = canManage,
            LastKnownCanManage = connected ? canManage : s.LastKnownCanManage,
        });
        if (!connected)
        {
            ++_overviewGeneration;
            ++_statsGeneration;
            ++_listGeneration;
            CancelAllRunWatchers();
            _localRuns.Clear();
            Set(s => s with
            {
                ListLoading = false,
                ListLoadingMore = false,
                RunsLoading = false,
                RunningJobIds = ImmutableHashSet<string>.Empty,
                BusyJobIds = ImmutableHashSet<string>.Empty,
            });
            return;
        }
        _ = LoadListAsync();
        _ = LoadStatsAsync();
        if (_state.SelectedJobId is { } selected)
            _ = LoadSelectedAsync(selected);
        if (_state.SelectedJobId is not null || _state.RunsLoaded)
            _ = LoadRunsAsync();
    }

    /// <summary>Gateway <c>cron</c> events invalidate the list, stats, the open job and its history.</summary>
    public void OnGatewayCronEvent(bool globalOverviewHandled = false)
    {
        if (!_state.Connected) return;
        _ = LoadListAsync();
        _ = LoadStatsAsync(refreshGlobalOverview: !globalOverviewHandled);
        if (_state.SelectedJobId is { } selected)
            _ = LoadSelectedAsync(selected, quiet: true);
        if (_state.RunsLoaded)
            _ = LoadRunsAsync(quiet: true);
    }

    // ─── List ───

    public void SetQuery(CronListQuery query)
    {
        Set(s => s with { ListQuery = query with { AgentId = s.AgentFilter, Offset = 0 } });
        _ = LoadListAsync();
    }

    /// <summary>
    /// Lists one agent's automations, or every agent's when <paramref name="agentId"/> is null.
    /// Retires a selection outside the new scope without cancelling locally requested runs.
    /// </summary>
    public void SetAgentFilter(string? agentId) => SetAgentFilter(agentId, reloadList: true);

    /// <param name="reloadList">False when the caller applies the new scope's first page itself.</param>
    private void SetAgentFilter(string? agentId, bool reloadList)
    {
        var agent = string.IsNullOrWhiteSpace(agentId) ? null : agentId.Trim();
        if (agent == _state.AgentFilter) return;
        var selected = _state.SelectedJob ?? _state.Jobs.FirstOrDefault(job => job.Id == _state.SelectedJobId);
        var retireSelection = _state.SelectedJobId is not null && agent is not null
            && !string.Equals(selected?.Owner, agent, StringComparison.Ordinal);
        if (retireSelection)
        {
            ++_selectionGeneration;
            ++_runsGeneration;
        }
        // Rows from another agent must never be shown or acted on under this filter:
        // retire any in-flight page and start the new inventory from empty.
        ++_listGeneration;
        ++_overviewGeneration;
        _listReloadPending = false;
        Set(s =>
        {
            var next = s with
            {
                AgentFilter = agent,
                ListQuery = s.ListQuery with { AgentId = agent, Offset = 0 },
                Jobs = Array.Empty<CronJob>(),
                Total = null,
                HasMore = false,
                ListLoaded = false,
                ListLoading = false,
                ListLoadingMore = false,
                ListError = null,
                ScopedTotal = null,
                FailingCount = null,
                ScopedNextWakeAtMs = null,
                OverviewJobs = Array.Empty<CronJob>(),
                OverviewLoaded = false,
                OverviewError = null,
                LastSeenAtMs = null,
            };
            return !retireSelection ? next : next with
            {
                SelectedJobId = null, SelectedJob = null, SelectedError = null,
                RunsQuery = new CronRunsQuery { AgentId = agent },
                Runs = Array.Empty<CronRunEntry>(), RunsLoaded = false, RunsLoading = false,
                RunsError = null,
            };
        });
        if (reloadList) _ = LoadListAsync();
        _ = LoadStatsAsync();
    }

    public async Task LoadListAsync(bool append = false)
    {
        var client = _client();
        if (client is null || !_state.Connected) return;
        if (append && (!_state.HasMore || _state.ListLoadingMore || _state.ListLoading)) return;
        if (!append && (_state.ListLoading || _state.ListLoadingMore))
        {
            _listReloadPending = true;
            return;
        }

        var generation = ++_listGeneration;
        var query = _state.ListQuery with { Offset = append ? _state.Jobs.Count : 0 };
        Set(s => append ? s with { ListLoadingMore = true } : s with { ListLoading = true, ListError = null });
        try
        {
            var loaded = new List<CronJob>();
            var goal = !append && _loadedListQuery == query
                ? Math.Max(query.Limit, _state.Jobs.Count) : query.Limit;
            CronListPage page;
            do
            {
                page = await client.ListCronJobsAsync(query with { Offset = query.Offset + loaded.Count }).ConfigureAwait(false);
                if (generation != _listGeneration || !_state.Connected || _disposed) return;
                loaded.AddRange(page.Jobs);
                if (page.HasMore && page.Jobs.Count == 0) throw new InvalidOperationException(IncompletePageMessage);
            } while (!append && page.HasMore && loaded.Count < goal);
            var refreshed = page with { Jobs = loaded, Offset = query.Offset };
            Post(() =>
            {
                if (generation != _listGeneration) return;
                ApplyListPage(refreshed, append, query);
            });
        }
        catch (Exception ex)
        {
            Post(() =>
            {
                if (generation != _listGeneration) return;
                var denied = CronGatewayRequests.IsForbidden(ex);
                Set(s => s with
                {
                    ListError = ex.Message,
                    ReadDenied = denied,
                    Jobs = denied ? Array.Empty<CronJob>() : s.Jobs,
                    ListLoaded = s.ListLoaded || denied,
                });
            });
        }
        finally
        {
            Post(() =>
            {
                if (generation != _listGeneration) return;
                Set(s => s with { ListLoading = false, ListLoadingMore = false });
                ReloadListIfPending();
            });
        }
    }

    private void ReloadListIfPending()
    {
        if (!_listReloadPending) return;
        _listReloadPending = false;
        _ = LoadListAsync();
    }

    private void ApplyListPage(CronListPage page, bool append, CronListQuery? loadedQuery = null)
    {
        _loadedListQuery = (loadedQuery ?? _state.ListQuery) with { Offset = 0 };
        var previous = _state.Jobs;
        // A page with a repeated id would break keyed rows; keep the first.
        var fresh = page.Jobs.DistinctBy(j => j.Id, StringComparer.Ordinal);
        var jobs = append ? previous.Concat(fresh.Where(j => previous.All(p => p.Id != j.Id))).ToList() : fresh.ToList();
        if (!append)
            DetectFinishedOneShots(previous, jobs);

        var running = _state.RunningJobIds;
        var rows = jobs.ToDictionary(j => j.Id, StringComparer.Ordinal);
        // The selected job may be filtered out; its exact definition still speaks for it.
        if (_state.SelectedJob is { } selectedJob)
            rows.TryAdd(selectedJob.Id, selectedJob);
        foreach (var job in jobs.Where(j => j.IsRunning))
            running = running.Add(job.Id);
        foreach (var id in running)
        {
            // Runs started here are tracked by their watcher (with its safety timeout).
            // Runs only observed in the list stay latched only while a visible row says so.
            // Job-list timestamps identify neither the accepted invocation nor its result.
            // Keep locally owned runs until matching history, confirmed removal or their own timeout.
            if (!IsTrackedRun(id) && (!rows.TryGetValue(id, out var observed) || !observed.IsRunning))
            {
                running = running.Remove(id);
            }
        }

        var selected = _state.SelectedJob;
        if (_state.SelectedJobId is { } selectedId && jobs.FirstOrDefault(j => j.Id == selectedId) is { } row
            && (selected is null || (row.UpdatedAtMs ?? 0) >= (selected.UpdatedAtMs ?? 0)))
        {
            selected = MergeRow(selected, row);
        }

        Set(s => s with
        {
            Jobs = jobs,
            Total = page.Total,
            HasMore = page.HasMore,
            ListLoaded = true,
            ListError = null,
            ReadDenied = false,
            RunningJobIds = running,
            SelectedJob = selected,
        });
    }

    /// <summary>List rows carry live state; the exact definition stays authoritative for config.</summary>
    private static CronJob MergeRow(CronJob? exact, CronJob row) =>
        exact is null ? row : exact with { State = row.State, Enabled = row.Enabled };

    /// <summary>
    /// A delete-after-run job that disappears from the list may simply be filtered
    /// out, so completion is confirmed with <c>cron.get</c> before notifying.
    /// </summary>
    private void DetectFinishedOneShots(IReadOnlyList<CronJob> previous, IReadOnlyList<CronJob> current)
    {
        var ids = current.Select(j => j.Id).ToHashSet(StringComparer.Ordinal);
        // A user removal is settled once neither the shown rows nor a full page carry it.
        _removedByUser.RemoveWhere(id => !ids.Contains(id) && previous.All(j => j.Id != id));
        foreach (var gone in previous.Where(j => !ids.Contains(j.Id)))
        {
            if (_removedByUser.Remove(gone.Id))
                continue;
            // Missing from this page may only mean filtered out; running state is cleared by
            // its watcher or by a confirmed removal, never by list membership.
            if (gone.DeleteAfterRun == true && gone.Schedule.Kind == CronScheduleKind.At)
                _ = ConfirmOneShotFinishedAsync(gone);
        }
    }

    private async Task ConfirmOneShotFinishedAsync(CronJob job)
    {
        var client = _client();
        if (client is null) return;
        try
        {
            await client.GetCronJobAsync(job.Id).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (CronGatewayRequests.IsJobNotFound(ex))
        {
            Post(() =>
            {
                ClearRunning(job.Id);
                Notify(new AutomationsNotice(AutomationsNoticeKind.JobCompleted, job.Title));
                if (_state.SelectedJobId == job.Id)
                    Set(s => s with { SelectedJobId = null, SelectedJob = null });
            });
        }
        catch (Exception)
        {
            // Other failures prove nothing about completion.
        }
    }

    public async Task LoadStatsAsync(bool refreshGlobalOverview = true)
    {
        var client = _client();
        if (client is null || !_state.Connected) return;
        if (_state.IsAllAgents)
        {
            ApplyGlobalOverview(_overviewSource.State);
            if (refreshGlobalOverview) _ = _overviewSource.RefreshAsync();
        }
        else _ = LoadScopedOverviewAsync();
        var agent = _state.AgentFilter;
        var generation = ++_statsGeneration;
        try
        {
            var statusTask = client.GetCronStatusAsync();
            var allTask = client.ListCronJobsAsync(new CronListQuery { AgentId = agent, Limit = 1 });
            var nextTask = client.ListCronJobsAsync(new CronListQuery { AgentId = agent, Enabled = CronEnabledFilter.Enabled, Limit = 1 });
            var failingTask = client.ListCronJobsAsync(new CronListQuery { AgentId = agent, LastRunStatus = "error", Limit = 1 });
            await Task.WhenAll(statusTask, allTask, nextTask, failingTask).ConfigureAwait(false);
            Post(() =>
            {
                if (generation != _statsGeneration) return;
                Set(s => s with
                {
                    Status = statusTask.Result,
                    StatusError = null,
                    ScopedTotal = allTask.Result.Total,
                    ScopedNextWakeAtMs = nextTask.Result.Jobs.FirstOrDefault()?.State.NextRunAtMs,
                    FailingCount = failingTask.Result.Total,
                });
            });
        }

        catch (Exception ex)
        {
            Post(() =>
            {
                if (generation != _statsGeneration) return;
                Set(s => s with { Status = null, StatusError = ex.Message, ScopedTotal = null, ScopedNextWakeAtMs = null, FailingCount = null });
            });
        }
    }

    private void OnGlobalOverviewChanged(object? sender, AutomationsOverviewSnapshot snapshot)
    {
        if (_state.IsAllAgents) ApplyGlobalOverview(snapshot);
    }

    private void ApplyGlobalOverview(AutomationsOverviewSnapshot snapshot) => Set(s => s with
    {
        OverviewJobs = snapshot.Jobs,
        OverviewLoaded = snapshot.HasLoaded,
        OverviewError = snapshot.Error,
        LastSeenAtMs = snapshot.LastSyncAtMs,
    });

    private async Task LoadScopedOverviewAsync()
        {
            var client = _client();
            if (client is null || !_state.Connected) return;
            var generation = ++_overviewGeneration;
            var agent = _state.AgentFilter;
            if (agent is null) return;
            try
            {
                var jobs = new List<CronJob>();
                // Active jobs are paged in next-run order, not inferred from the first browse
                // page. Also read disabled jobs so attention and status counts cover this scope.
                foreach (var enabled in new[] { CronEnabledFilter.Enabled, CronEnabledFilter.Disabled })
                {
                    var offset = 0;
                    while (true)
                    {
                        var page = await client.ListCronJobsAsync(new CronListQuery
                        {
                            AgentId = agent, Enabled = enabled, SortBy = CronSortBy.NextRunAtMs,
                            Limit = 200, Offset = offset,
                        }).ConfigureAwait(false);
                        if (generation != _overviewGeneration || !_state.Connected || _disposed) return;
                        jobs.AddRange(page.Jobs);
                        if (!page.HasMore) break;
                        if (page.Jobs.Count == 0) throw new InvalidOperationException(IncompletePageMessage);
                        offset += page.Jobs.Count;
                    }
                }
                Post(() =>
                {
                    if (generation != _overviewGeneration || agent != _state.AgentFilter) return;
                    Set(s => s with
                    {
                        OverviewJobs = jobs.DistinctBy(j => j.Id).ToArray(),
                        OverviewLoaded = true, OverviewError = null,
                        LastSeenAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    });
                });
            }
            catch (Exception ex)
            {
                Post(() =>
                {
                    if (generation != _overviewGeneration || agent != _state.AgentFilter) return;
                    var denied = CronGatewayRequests.IsForbidden(ex);
                    Set(s => s with
                    {
                        OverviewError = ex.Message,
                        OverviewJobs = denied ? Array.Empty<CronJob>() : s.OverviewJobs,
                        OverviewLoaded = denied ? false : s.OverviewLoaded,
                    });
                });
            }
        }

    /// <summary>One reveal scan: the query it ran, the rows it loaded and whether the job was among them.</summary>
    private readonly record struct RevealScan(CronListQuery Query, CronListPage Page, bool Found);

    /// <summary>
    /// Reveals a job the user asked for by id (an agenda link, a saved job or a search result)
    /// through the real inventory row, even beyond its first page. Browse filters reset so the
    /// row is reachable. With <paramref name="widenToAllAgents"/>, a job outside the filtered
    /// agent switches the list to All agents; a job found nowhere leaves the filter alone and
    /// raises <see cref="AutomationsNoticeKind.JobUnavailable"/> instead of selecting a row the
    /// list does not hold. The outcome is what the Gateway answered; the list and selection are
    /// applied on the dispatcher afterwards.
    /// </summary>
    public async Task<AutomationRevealOutcome> RevealAsync(string jobId, bool widenToAllAgents = false)
    {
        var client = _client();
        if (client is null || !_state.Connected) return AutomationRevealOutcome.Failed;
        var generation = ++_listGeneration;
        _listReloadPending = false;
        var agent = _state.AgentFilter;
        var query = new CronListQuery { AgentId = agent };
        Set(s => s with { ListQuery = query, ListLoading = true, ListLoadingMore = false });
        try
        {
            if (await ScanForJobAsync(client, query, jobId, generation).ConfigureAwait(false) is not { } scan)
                return AutomationRevealOutcome.Superseded;
            if (!scan.Found && widenToAllAgents && agent is not null)
            {
                if (await ScanForJobAsync(client, query with { AgentId = null }, jobId, generation).ConfigureAwait(false) is not { } widened)
                    return AutomationRevealOutcome.Superseded;
                if (widened.Found) scan = widened;
            }
            Post(() =>
            {
                if (generation != _listGeneration) return;
                var pendingQuery = _listReloadPending ? _state.ListQuery : null;
                if (scan.Query.AgentId != _state.AgentFilter) SetAgentFilter(scan.Query.AgentId, reloadList: false);
                if (pendingQuery is not null)
                {
                    _listReloadPending = true;
                    Set(s => s with { ListQuery = pendingQuery with { AgentId = s.AgentFilter } });
                }
                ApplyListPage(scan.Page, false, scan.Query);
                Set(s => s with { ListLoading = false });
                if (scan.Found) Select(jobId);
                else Notify(new AutomationsNotice(AutomationsNoticeKind.JobUnavailable, jobId));
                ReloadListIfPending();
            });
            return scan.Found ? AutomationRevealOutcome.Revealed : AutomationRevealOutcome.NotFound;
        }
        catch (Exception ex)
        {
            Post(() =>
            {
                if (generation != _listGeneration) return;
                Set(s => s with { ListLoading = false, ListError = ex.Message });
                ReloadListIfPending();
            });
            return AutomationRevealOutcome.Failed;
        }
    }

    /// <summary>Pages <paramref name="query"/> until <paramref name="jobId"/> appears or the inventory ends; null when a newer request, a disconnect or disposal took over.</summary>
    private async Task<RevealScan?> ScanForJobAsync(IOperatorGatewayClient client, CronListQuery query, string jobId, long generation)
    {
        var jobs = new List<CronJob>();
        CronListPage page;
        do
        {
            page = await client.ListCronJobsAsync(query with { Offset = jobs.Count }).ConfigureAwait(false);
            if (generation != _listGeneration || !_state.Connected || _disposed) return null;
            jobs.AddRange(page.Jobs);
            if (page.HasMore && page.Jobs.Count == 0) throw new InvalidOperationException(IncompletePageMessage);
        } while (page.HasMore && jobs.All(j => j.Id != jobId));
        return new RevealScan(query, page with { Jobs = jobs }, jobs.Any(j => j.Id == jobId));
    }

    // ─── Selection ───

    public void Select(string? jobId)
    {
        if (jobId == _state.SelectedJobId) return;
        var row = jobId is null ? null : _state.Jobs.FirstOrDefault(j => j.Id == jobId);
        Set(s => s with { SelectedJobId = jobId, SelectedJob = row, SelectedError = null });
        if (jobId is null) return;
        _ = LoadSelectedAsync(jobId);
        ShowRecentRuns(jobId);
    }

    public async Task LoadSelectedAsync(string jobId, bool quiet = false)
    {
        var client = _client();
        if (client is null || !_state.Connected) return;
        var generation = ++_selectionGeneration;
        try
        {
            var job = await client.GetCronJobAsync(jobId).ConfigureAwait(false);
            Post(() =>
            {
                if (generation != _selectionGeneration || _state.SelectedJobId != jobId) return;
                var listed = _state.Jobs.FirstOrDefault(row => row.Id == jobId) ?? _state.SelectedJob;
                job = job with { EffectiveAgentId = job.EffectiveAgentId ?? listed?.EffectiveAgentId };
                // The exact definition settles a run nobody here is watching.
                var running = job.IsRunning ? _state.RunningJobIds.Add(jobId)
                    : IsTrackedRun(jobId) ? _state.RunningJobIds : _state.RunningJobIds.Remove(jobId);
                Set(s => s with { SelectedJob = job, SelectedError = null, Jobs = ReplaceRow(s.Jobs, job), RunningJobIds = running });
            });
        }
        catch (Exception ex)
        {
            Post(() =>
            {
                if (generation != _selectionGeneration || _state.SelectedJobId != jobId || quiet) return;
                Set(s => s with { SelectedError = ex.Message });
            });
        }
    }

    private static IReadOnlyList<CronJob> ReplaceRow(IReadOnlyList<CronJob> jobs, CronJob job) =>
        jobs.Select(j => j.Id == job.Id ? job : j).ToList();

    // ─── Runs ───

    private void ShowRecentRuns(string jobId)
    {
        Set(s => s with
        {
            RunsQuery = new CronRunsQuery { JobId = jobId },
            Runs = Array.Empty<CronRunEntry>(),
            RunsLoaded = false,
            RunsError = null,
        });
        _ = LoadRunsAsync();
    }

    public async Task LoadRunsAsync(bool quiet = false)
    {
        var client = _client();
        if (client is null || !_state.Connected) return;
        // Only the selected job's recent results are native. Full history/filtering stays on the web.
        if (!_state.RunsQuery.IsJobScope) return;
        var generation = ++_runsGeneration;
        var query = _state.RunsQuery with { Offset = 0 };
        if (!quiet) Set(s => s with { RunsLoading = true, RunsError = null });
        try
        {
            var page = await client.ListCronRunsAsync(query).ConfigureAwait(false);
            Post(() =>
            {
                if (generation != _runsGeneration) return;
                Set(s => s with { Runs = page.Entries.ToList(), RunsLoaded = true, RunsError = null });
            });
        }
        catch (Exception ex)
        {
            Post(() =>
            {
                if (generation != _runsGeneration) return;
                Set(s => s with { RunsError = ex.Message, RunsLoaded = true });
            });
        }
        finally
        {
            Post(() =>
            {
                if (generation != _runsGeneration) return;
                Set(s => s with { RunsLoading = false });
            });
        }
    }

    // ─── Mutations ───

    private CronJob? FindJob(string jobId) =>
        _state.SelectedJob?.Id == jobId ? _state.SelectedJob : _state.Jobs.FirstOrDefault(j => j.Id == jobId);

    private bool BeginMutation(string jobId, CronJob? revisioned = null)
    {
        if (!_state.Connected || !_state.CanManage || _state.BusyJobIds.Contains(jobId)) return false;
        if (revisioned is not null && !HasRevision(revisioned)) return false;
        Set(s => s with { BusyJobIds = s.BusyJobIds.Add(jobId) });
        return true;
    }

    private void EndMutation(string jobId) => Set(s => s with { BusyJobIds = s.BusyJobIds.Remove(jobId) });

    /// <summary>Starts a run. Only its accepted RunId can correlate history completion; list timestamps cannot.</summary>
    public async Task RunNowAsync(string jobId, CronRunMode mode = CronRunMode.Force)
    {
        var client = _client();
        var job = FindJob(jobId);
        if (client is null || job is null || _state.RunningJobIds.Contains(jobId) || !BeginMutation(jobId)) return;
        var invocation = new RunInvocation();
        _localRuns[jobId] = invocation;
        Set(s => s with { RunningJobIds = s.RunningJobIds.Add(jobId) });
        try
        {
            var result = await client.RunCronJobDetailedAsync(jobId, mode == CronRunMode.Force).ConfigureAwait(false);
            Post(() =>
            {
                if (!OwnsRun(jobId, invocation)) return;
                EndMutation(jobId);
                if (!result.Accepted || !result.Enqueued)
                {
                    ClearRunning(jobId);
                    Notify(new AutomationsNotice(AutomationsNoticeKind.RunNotStarted, job.Title, result.Reason, result.Error));
                    if (result.Reason == "invalid-spec")
                        _ = LoadRunsAsync(quiet: true);
                    _ = LoadListAsync();
                    return;
                }
                Notify(new AutomationsNotice(AutomationsNoticeKind.RunQueued, job.Title, RunId: result.RunId));
                invocation.RunId = string.IsNullOrWhiteSpace(result.RunId) ? null : result.RunId;
                StartRunWatcher(jobId, invocation);
            });
        }
        catch (Exception ex)
        {
            Post(() =>
            {
                if (!OwnsRun(jobId, invocation)) return;
                EndMutation(jobId);
                ClearRunning(jobId);
                Notify(new AutomationsNotice(AutomationsNoticeKind.RunFailed, job.Title, Detail: ex.Message));
            });
        }
    }

    private void StartRunWatcher(string jobId, RunInvocation invocation)
    {
        if (!OwnsRun(jobId, invocation)) return;
        CancelRunWatcher(jobId);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _runWatchers[jobId] = cts;
        var token = cts.Token;
        // Accepted identity is immutable for this watcher, including an explicitly ID-less acknowledgement.
        var runId = invocation.RunId;
        _ = Task.Run(async () =>
        {
            var started = DateTimeOffset.UtcNow;
            try
            {
                for (var attempt = 0; attempt < RunPollAttempts && !token.IsCancellationRequested; attempt++)
                {
                    await _delay(RunPollInterval, token).ConfigureAwait(false);
                    if (await PollRunCompletedAsync(jobId, runId, cts).ConfigureAwait(false))
                        return;
                    if (DateTimeOffset.UtcNow - started >= RunSafetyTimeout)
                        break;
                }
                Post(() =>
                {
                    // Safety timeout: the Gateway never reported completion.
                    if (!OwnsWatcher(jobId, cts)) return;
                    ClearRunning(jobId);
                    _ = LoadListAsync();
                });
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async Task<bool> PollRunCompletedAsync(string jobId, string? runId, CancellationTokenSource watcher)
    {
        var client = _client();
        // Legacy acknowledgements carry no invocation identity. Neither newer history nor a list
        // timestamp can safely complete them; retain the existing owner-bound safety timeout.
        if (client is null || runId is null) return false;
        try
        {
            var page = await client.ListCronRunsAsync(new CronRunsQuery { JobId = jobId, RunId = runId, Limit = RunPollLimit }).ConfigureAwait(false);
            if (!page.Entries.Any(e => e.JobId == jobId && string.Equals(e.RunId, runId, StringComparison.Ordinal)))
                return false;
            Post(() =>
            {
                if (!OwnsWatcher(jobId, watcher)) return;
                ClearRunning(jobId);
                _ = LoadListAsync();
                _ = LoadStatsAsync();
                if (_state.SelectedJobId == jobId)
                    _ = LoadSelectedAsync(jobId, quiet: true);
                if (_state.RunsLoaded && _state.RunsQuery.JobId == jobId)
                    _ = LoadRunsAsync(quiet: true);
            });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>False once a newer run replaced this watcher or it was cancelled.</summary>
    private bool OwnsWatcher(string jobId, CancellationTokenSource watcher) =>
        _runWatchers.TryGetValue(jobId, out var current) && ReferenceEquals(current, watcher);

    private bool OwnsRun(string jobId, RunInvocation invocation) =>
        _localRuns.TryGetValue(jobId, out var current) && ReferenceEquals(current, invocation);

    /// <summary>Locally requested runs cannot be settled by uncorrelated job-list state.</summary>
    private bool IsTrackedRun(string jobId) => _localRuns.ContainsKey(jobId);

    private void ForgetRun(string jobId)
    {
        CancelRunWatcher(jobId);
        _localRuns.Remove(jobId);
    }

    private void ClearRunning(string jobId)
    {
        ForgetRun(jobId);
        if (_state.RunningJobIds.Contains(jobId))
            Set(s => s with { RunningJobIds = s.RunningJobIds.Remove(jobId) });
    }

    private void CancelRunWatcher(string jobId)
    {
        if (_runWatchers.Remove(jobId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void CancelAllRunWatchers()
    {
        foreach (var id in _runWatchers.Keys.ToList())
            CancelRunWatcher(id);
    }

    public Task SetEnabledAsync(string jobId, bool enabled) =>
        UpdateAsync(jobId, new JsonObject { ["enabled"] = enabled },
            enabled ? AutomationsNoticeKind.Resumed : AutomationsNoticeKind.Paused);

    private async Task UpdateAsync(string jobId, JsonObject patch, AutomationsNoticeKind successNotice)
    {
        var client = _client();
        var job = FindJob(jobId);
        if (client is null || job is null || !BeginMutation(jobId, revisioned: job)) return;
        try
        {
            var updated = await client.UpdateCronJobDetailedAsync(jobId, job.ConfigRevision, patch).ConfigureAwait(false);
            Post(() =>
            {
                EndMutation(jobId);
                ApplyUpdated(updated);
                Notify(new AutomationsNotice(successNotice, updated.Title));
                _ = LoadListAsync();
                _ = LoadStatsAsync();
            });
        }
        catch (Exception ex)
        {
            await HandleMutationFailureAsync(client, job, ex).ConfigureAwait(false);
        }
    }

    private async Task HandleMutationFailureAsync(IOperatorGatewayClient client, CronJob job, Exception ex)
    {
        if (!CronGatewayRequests.IsRevisionConflict(ex))
        {
            Post(() =>
            {
                EndMutation(job.Id);
                Notify(new AutomationsNotice(AutomationsNoticeKind.OperationFailed, job.Title, Detail: ex.Message));
            });
            return;
        }
        CronJob? latest = null;
        try { latest = await client.GetCronJobAsync(job.Id).ConfigureAwait(false); }
        catch (Exception) { }
        Post(() =>
        {
            EndMutation(job.Id);
            if (latest is not null) ApplyUpdated(latest);
            Notify(new AutomationsNotice(latest is null ? AutomationsNoticeKind.ConflictUnloadable : AutomationsNoticeKind.Conflict, job.Title));
            _ = LoadListAsync();
        });
    }

    /// <summary>Edits are compare-and-swap; without a revision the Gateway would skip the conflict check.</summary>
    private bool HasRevision(CronJob job)
    {
        if (!string.IsNullOrEmpty(job.ConfigRevision)) return true;
        Notify(new AutomationsNotice(AutomationsNoticeKind.MissingRevision, job.Title));
        return false;
    }

    private void ApplyUpdated(CronJob job) =>
        Set(s => s with
        {
            Jobs = ReplaceRow(s.Jobs, job),
            SelectedJob = s.SelectedJobId == job.Id ? job : s.SelectedJob,
        });

    public async Task RemoveAsync(string jobId)
    {
        var client = _client();
        var job = FindJob(jobId);
        if (client is null || job is null || _state.RunningJobIds.Contains(jobId) || !BeginMutation(jobId, revisioned: job)) return;
        _removedByUser.Add(jobId);
        void Removed()
        {
            EndMutation(jobId);
            // _removedByUser keeps the id until a fresh list confirms it is gone, so an
            // older in-flight page that restores the row cannot later read as "completed".
            Set(s => s with
            {
                Jobs = s.Jobs.Where(j => j.Id != jobId).ToList(),
                Total = s.Total is { } total ? Math.Max(0, total - 1) : null,
                SelectedJobId = s.SelectedJobId == jobId ? null : s.SelectedJobId,
                SelectedJob = s.SelectedJobId == jobId ? null : s.SelectedJob,
            });
            Notify(new AutomationsNotice(AutomationsNoticeKind.Removed, job.Title));
            _ = LoadListAsync();
            _ = LoadStatsAsync();
        }
        try
        {
            await client.RemoveCronJobDetailedAsync(jobId, job.ConfigRevision).ConfigureAwait(false);
            Post(Removed);
        }
        catch (Exception ex) when (CronGatewayRequests.IsJobNotFound(ex))
        {
            // Already removed elsewhere: the outcome the user asked for.
            Post(Removed);
        }
        catch (Exception ex)
        {
            Post(() => _removedByUser.Remove(jobId));
            await HandleMutationFailureAsync(client, job, ex).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Validates and saves an editor form. <paramref name="editing"/> updates that job;
    /// otherwise a new job is created (from <paramref name="cloneSource"/> when cloning).
    /// </summary>
    public async Task<AutomationsSaveResult> SaveAsync(CronJobForm form, CronJob? editing, CronJob? cloneSource = null, bool runAfterCreate = false)
    {
        var client = _client();
        if (client is null || !_state.Connected)
            return new AutomationsSaveResult.Failed("The Gateway is not connected.");
        if (!_state.CanManage)
            return new AutomationsSaveResult.Failed("Automation changes require operator.admin access.");
        var normalized = form.Normalize();
        var errors = normalized.ValidateForNative(editing, cloneSource);
        if (errors.Count > 0)
            return new AutomationsSaveResult.Invalid(errors);

        if (editing is not null && string.IsNullOrEmpty(editing.ConfigRevision))
            return new AutomationsSaveResult.MissingRevision();

        JsonObject definition;
        try
        {
            definition = normalized.BuildDefinition(editing, cloneSource);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            return new AutomationsSaveResult.Failed(ex.Message);
        }

        try
        {
            CronJob saved;
            if (editing is not null)
            {
                saved = await client.UpdateCronJobDetailedAsync(editing.Id, editing.ConfigRevision, definition).ConfigureAwait(false);
            }
            else
            {
                if (!definition.ContainsKey("agentId"))
                    definition["agentId"] = _state.DefaultAgentId;
                saved = await client.AddCronJobDetailedAsync(definition).ConfigureAwait(false);
            }
            Post(() =>
            {
                ApplyUpdated(saved);
                Notify(new AutomationsNotice(editing is null ? AutomationsNoticeKind.Created : AutomationsNoticeKind.Saved, saved.Title));
                Set(s => s with { SelectedJobId = saved.Id, SelectedJob = saved });
                ShowRecentRuns(saved.Id);
                _ = LoadListAsync();
                _ = LoadStatsAsync();
                if (runAfterCreate && editing is null)
                    _ = RunNowAsync(saved.Id);
            });
            return new AutomationsSaveResult.Saved(saved);
        }
        catch (Exception ex) when (editing is not null && CronGatewayRequests.IsRevisionConflict(ex))
        {
            CronJob? latest = null;
            try { latest = await client.GetCronJobAsync(editing.Id).ConfigureAwait(false); }
            catch (Exception) { }
            if (latest is not null)
                Post(() => ApplyUpdated(latest));
            return new AutomationsSaveResult.Conflict(latest);
        }
        catch (Exception ex)
        {
            return new AutomationsSaveResult.Failed(ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        CancelAllRunWatchers();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _disposed = true;
        _overviewSource.Changed -= OnGlobalOverviewChanged;
        if (_ownsOverviewSource) _overviewSource.Dispose();
    }
}
