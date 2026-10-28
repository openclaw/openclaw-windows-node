using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace OpenClaw.Shared.Cron;

public enum AutomationsView { Jobs, RunHistory }

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
}

/// <summary>Structured feedback; the UI picks wording. <see cref="Reason"/> is a Gateway reason code when known.</summary>
public sealed record AutomationsNotice(AutomationsNoticeKind Kind, string JobName, string? Reason = null, string? Detail = null, string? RunId = null)
{
    public long Id { get; init; }
    public bool IsError => Kind is AutomationsNoticeKind.RunFailed or AutomationsNoticeKind.OperationFailed
        or AutomationsNoticeKind.ConflictUnloadable or AutomationsNoticeKind.MissingRevision
        || (Kind == AutomationsNoticeKind.RunNotStarted && Reason is not ("not-due" or "already-running"));
}

public sealed record AutomationsSnapshot
{
    public string AgentId { get; init; } = "main";
    public bool Connected { get; init; }
    public bool CanManage { get; init; }
    public AutomationsView View { get; init; } = AutomationsView.Jobs;

    public CronListQuery ListQuery { get; init; } = new();
    public IReadOnlyList<CronJob> Jobs { get; init; } = Array.Empty<CronJob>();
    public int Total { get; init; }
    public bool HasMore { get; init; }
    public bool ListLoading { get; init; }
    public bool ListLoadingMore { get; init; }
    public bool ListLoaded { get; init; }
    public string? ListError { get; init; }
    public bool ReadDenied { get; init; }

    public CronSchedulerStatus? Status { get; init; }
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
    public bool RunsLoadingMore { get; init; }
    public bool RunsLoaded { get; init; }
    public bool RunsHasMore { get; init; }
    public int RunsTotal { get; init; }
    public string? RunsError { get; init; }

    public AutomationsNotice? Notice { get; init; }

    public bool IsFiltered => ListQuery.Query.Trim().Length > 0 || ListQuery.Enabled != CronEnabledFilter.All || ListQuery.HasAdvancedFilters;
    /// <summary>Starter automations are offered only on a loaded, empty, unfiltered list the user may change.</summary>
    public bool ShowSuggestions => CanManage && Connected && ListLoaded && ListError is null && Jobs.Count == 0 && !IsFiltered;
    public bool IsRunning(string jobId) => RunningJobIds.Contains(jobId);
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
/// Owns the agent-scoped automation inventory, scheduler stats, selection, run
/// history, run-now latching and mutations. UI-agnostic: every state change is
/// published through <see cref="Changed"/> on the <c>post</c> dispatcher.
/// </summary>
public sealed class AutomationsStore : IDisposable
{
    public static readonly TimeSpan RunPollInterval = TimeSpan.FromSeconds(3);
    public const int RunPollAttempts = 30;
    public static readonly TimeSpan RunSafetyTimeout = TimeSpan.FromSeconds(90);
    private const int RunPollLimit = 20;

    private readonly Func<IOperatorGatewayClient?> _client;
    private readonly Action<Action> _post;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, CancellationTokenSource> _runWatchers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _runBaseline = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removedByUser = new(StringComparer.Ordinal);
    private AutomationsSnapshot _state;
    private long _listGeneration;
    private long _runsGeneration;
    private long _selectionGeneration;
    private long _noticeId;
    private bool _listReloadPending;
    private bool _disposed;

    public AutomationsStore(
        string agentId,
        Func<IOperatorGatewayClient?> client,
        Action<Action> post,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _client = client;
        _post = post;
        _delay = delay ?? Task.Delay;
        _state = new AutomationsSnapshot
        {
            AgentId = NormalizeAgent(agentId),
            ListQuery = new CronListQuery { AgentId = NormalizeAgent(agentId) },
            RunsQuery = new CronRunsQuery { AgentId = NormalizeAgent(agentId) },
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
        var connected = client is { IsConnectedToGateway: true };
        var canManage = connected && client!.GrantedOperatorScopes.Contains(CronProtocol.AdminScope, StringComparer.Ordinal);
        Set(s => s with { Connected = connected, CanManage = canManage });
        if (!connected)
        {
            CancelAllRunWatchers();
            _runBaseline.Clear();
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
        if (_state.View == AutomationsView.RunHistory || _state.SelectedJobId is not null || _state.RunsLoaded)
            _ = LoadRunsAsync();
    }

    /// <summary>Gateway <c>cron</c> events invalidate the list, stats, the open job and its history.</summary>
    public void OnGatewayCronEvent()
    {
        if (!_state.Connected) return;
        _ = LoadListAsync();
        _ = LoadStatsAsync();
        if (_state.SelectedJobId is { } selected)
            _ = LoadSelectedAsync(selected, quiet: true);
        if (_state.RunsLoaded)
            _ = LoadRunsAsync(quiet: true);
    }

    // ─── List ───

    public void SetQuery(CronListQuery query)
    {
        Set(s => s with { ListQuery = query with { AgentId = s.AgentId, Offset = 0 } });
        _ = LoadListAsync();
    }

    public void SetView(AutomationsView view)
    {
        if (_state.View == view) return;
        Set(s => s with { View = view });
        if (view == AutomationsView.RunHistory)
            ShowRuns(new CronRunsQuery { AgentId = _state.AgentId });
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
            var page = await client.ListCronJobsAsync(query).ConfigureAwait(false);
            Post(() =>
            {
                if (generation != _listGeneration) return;
                ApplyListPage(page, append);
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
                if (_listReloadPending)
                {
                    _listReloadPending = false;
                    _ = LoadListAsync();
                }
            });
        }
    }

    private void ApplyListPage(CronListPage page, bool append)
    {
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
            if (IsTrackedRun(id))
            {
                if (rows.TryGetValue(id, out var tracked) && !tracked.IsRunning && HasCompletedSinceBaseline(tracked))
                {
                    running = running.Remove(id);
                    ForgetRun(id);
                }
            }
            else if (!rows.TryGetValue(id, out var observed) || !observed.IsRunning)
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

    private bool HasCompletedSinceBaseline(CronJob job)
    {
        var baseline = _runBaseline.TryGetValue(job.Id, out var b) ? b : 0;
        return job.State.LastRunAtMs is { } last && last > baseline;
    }

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

    public async Task LoadStatsAsync()
    {
        var client = _client();
        if (client is null || !_state.Connected) return;
        var agent = _state.AgentId;
        try
        {
            var statusTask = client.GetCronStatusAsync();
            var allTask = client.ListCronJobsAsync(new CronListQuery { AgentId = agent, Limit = 1 });
            var nextTask = client.ListCronJobsAsync(new CronListQuery { AgentId = agent, Enabled = CronEnabledFilter.Enabled, Limit = 1 });
            var failingTask = client.ListCronJobsAsync(new CronListQuery { AgentId = agent, LastRunStatus = "error", Limit = 1 });
            await Task.WhenAll(statusTask, allTask, nextTask, failingTask).ConfigureAwait(false);
            Post(() =>
            {
                Set(s => s with
                {
                    Status = statusTask.Result,
                    ScopedTotal = allTask.Result.Total,
                    ScopedNextWakeAtMs = nextTask.Result.Jobs.FirstOrDefault()?.State.NextRunAtMs,
                    FailingCount = failingTask.Result.Total,
                });
            });
        }
        catch (Exception)
        {
            Post(() => Set(s => s with { ScopedTotal = null, ScopedNextWakeAtMs = null, FailingCount = null }));
        }
    }

    // ─── Selection ───

    public void Select(string? jobId)
    {
        if (jobId == _state.SelectedJobId && (jobId is null || _state.View == AutomationsView.Jobs)) return;
        var row = jobId is null ? null : _state.Jobs.FirstOrDefault(j => j.Id == jobId);
        Set(s => s with { SelectedJobId = jobId, SelectedJob = row, SelectedError = null, View = AutomationsView.Jobs });
        if (jobId is null) return;
        _ = LoadSelectedAsync(jobId);
        ShowRuns(new CronRunsQuery { JobId = jobId, AgentId = _state.AgentId });
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

    public void ShowRuns(CronRunsQuery query)
    {
        Set(s => s with
        {
            RunsQuery = query with { AgentId = s.AgentId, Offset = 0 },
            Runs = Array.Empty<CronRunEntry>(),
            RunsLoaded = false,
            RunsHasMore = false,
            RunsError = null,
        });
        _ = LoadRunsAsync();
    }

    public void SetRunsFilter(CronRunsQuery query)
    {
        Set(s => s with
        {
            RunsQuery = query with { AgentId = s.AgentId, JobId = s.RunsQuery.JobId, Offset = 0 },
            Runs = Array.Empty<CronRunEntry>(),
            RunsHasMore = false,
        });
        _ = LoadRunsAsync();
    }

    public async Task LoadRunsAsync(bool append = false, bool quiet = false)
    {
        var client = _client();
        if (client is null || !_state.Connected) return;
        // Appending while a first page is in flight would mix two filters.
        if (append && (!_state.RunsHasMore || _state.RunsLoadingMore || _state.RunsLoading)) return;
        var generation = ++_runsGeneration;
        var query = _state.RunsQuery with { Offset = append ? _state.Runs.Count : 0 };
        Set(s => append ? s with { RunsLoadingMore = true } : quiet ? s : s with { RunsLoading = true, RunsError = null });
        try
        {
            var page = await client.ListCronRunsAsync(query).ConfigureAwait(false);
            Post(() =>
            {
                if (generation != _runsGeneration) return;
                var runs = append ? _state.Runs.Concat(page.Entries.Where(e => _state.Runs.All(r => r.Key != e.Key))).ToList() : page.Entries.ToList();
                Set(s => s with { Runs = runs, RunsHasMore = page.HasMore, RunsTotal = page.Total, RunsLoaded = true, RunsError = null });
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
                Set(s => s with { RunsLoading = false, RunsLoadingMore = false });
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

    /// <summary>Starts a run. The running latch clears when history or the list reports a newer run.</summary>
    public async Task RunNowAsync(string jobId, CronRunMode mode = CronRunMode.Force)
    {
        var client = _client();
        var job = FindJob(jobId);
        if (client is null || job is null || _state.RunningJobIds.Contains(jobId) || !BeginMutation(jobId)) return;
        _runBaseline[jobId] = job.State.LastRunAtMs ?? 0;
        Set(s => s with { RunningJobIds = s.RunningJobIds.Add(jobId) });
        try
        {
            var result = await client.RunCronJobDetailedAsync(jobId, mode == CronRunMode.Force).ConfigureAwait(false);
            Post(() =>
            {
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
                StartRunWatcher(jobId);
            });
        }
        catch (Exception ex)
        {
            Post(() =>
            {
                EndMutation(jobId);
                ClearRunning(jobId);
                Notify(new AutomationsNotice(AutomationsNoticeKind.RunFailed, job.Title, Detail: ex.Message));
            });
        }
    }

    private void StartRunWatcher(string jobId)
    {
        CancelRunWatcher(jobId);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _runWatchers[jobId] = cts;
        var token = cts.Token;
        // Captured on the dispatcher; the background loop never touches store fields.
        var baseline = _runBaseline.TryGetValue(jobId, out var b) ? b : 0;
        _ = Task.Run(async () =>
        {
            var started = DateTimeOffset.UtcNow;
            try
            {
                for (var attempt = 0; attempt < RunPollAttempts && !token.IsCancellationRequested; attempt++)
                {
                    await _delay(RunPollInterval, token).ConfigureAwait(false);
                    if (await PollRunCompletedAsync(jobId, baseline, cts).ConfigureAwait(false))
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

    private async Task<bool> PollRunCompletedAsync(string jobId, long baseline, CancellationTokenSource watcher)
    {
        var client = _client();
        if (client is null) return false;
        try
        {
            var page = await client.ListCronRunsAsync(new CronRunsQuery { JobId = jobId, Limit = RunPollLimit }).ConfigureAwait(false);
            if (!page.Entries.Any(e => e.EffectiveRunAtMs > baseline))
                return false;
            Post(() =>
            {
                if (!OwnsWatcher(jobId, watcher)) return;
                ClearRunning(jobId);
                _ = LoadListAsync();
                _ = LoadStatsAsync();
                if (_state.SelectedJobId == jobId)
                    _ = LoadSelectedAsync(jobId, quiet: true);
                if (_state.RunsLoaded && (_state.RunsQuery.JobId == jobId || !_state.RunsQuery.IsJobScope))
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

    /// <summary>A run started here: its baseline lives from Run now until history or the list reports it.</summary>
    private bool IsTrackedRun(string jobId) => _runBaseline.ContainsKey(jobId);

    private void ForgetRun(string jobId)
    {
        CancelRunWatcher(jobId);
        _runBaseline.Remove(jobId);
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
                Total = Math.Max(0, s.Total - 1),
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
        var errors = normalized.Validate();
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
                if (!definition.ContainsKey("agentId") && !normalized.ClearAgent)
                    definition["agentId"] = _state.AgentId;
                saved = await client.AddCronJobDetailedAsync(definition).ConfigureAwait(false);
            }
            Post(() =>
            {
                ApplyUpdated(saved);
                Notify(new AutomationsNotice(editing is null ? AutomationsNoticeKind.Created : AutomationsNoticeKind.Saved, saved.Title));
                Set(s => s with { SelectedJobId = saved.Id, SelectedJob = saved });
                ShowRuns(new CronRunsQuery { JobId = saved.Id });
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
    }
}
