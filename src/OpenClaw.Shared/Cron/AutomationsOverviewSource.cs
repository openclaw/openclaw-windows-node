// Owns the single host-lifetime, gateway-wide automation overview.
// Hosts bind the connection; an All agents page calls RefreshAsync to activate it.
using System.Collections.Immutable;
using System.Text.Json;

namespace OpenClaw.Shared.Cron;

/// <summary>Accepted global inventory and explicit freshness for page and navigation consumers.</summary>
public sealed record AutomationsOverviewSnapshot
{
    public ImmutableArray<CronJob> Jobs { get; init; } = [];
    public IOperatorGatewayClient? Client { get; init; }
    public long? ConnectionEpoch { get; init; }
    public bool HasLoaded { get; init; }
    public bool IsCurrent { get; init; }
    public bool IsLoading { get; init; }
    public string? Error { get; init; }
    public long? LastSyncAtMs { get; init; }
    public int? AttentionCount => IsCurrent ? Jobs.Count(job => job.IsFailing) : null;
}

/// <summary>
/// Fully pages enabled and disabled jobs once per global refresh, independently of page filters.
/// Call <see cref="SetConnection"/> on connection transitions and dispose with the owning host.
/// </summary>
public sealed class AutomationsOverviewSource(
    Func<IOperatorGatewayClient?> currentClient,
    Action<Action> post) : IDisposable
{
    private const string IncompletePageMessage = "The Gateway returned an incomplete automation page.";
    private readonly object _gate = new();
    private AutomationsOverviewSnapshot _state = new();
    private IOperatorGatewayClient? _client;
    private long? _epoch;
    private EventHandler<JsonElement>? _cronHandler;
    private long _generation;
    private Task? _pending;
    private bool _refreshAgain;
    private bool _activated;
    private bool _disposed;

    public event EventHandler<AutomationsOverviewSnapshot>? Changed;

    /// <summary>Masks stale socket bindings immediately, before queued connection notifications run.</summary>
    public AutomationsOverviewSnapshot State
    {
        get
        {
            lock (_gate)
            {
                if (IsBindingCurrent(_client, _epoch) && !_disposed) return _state;
                var current = currentClient();
                var replaceInventory = current is not null && (!ReferenceEquals(current, _client)
                    || (current.IsConnectedToGateway && current.SessionMutationConnectionEpoch != _epoch));
                return _state with
                {
                    Jobs = replaceInventory ? [] : _state.Jobs,
                    HasLoaded = !replaceInventory && _state.HasLoaded,
                    IsCurrent = false, IsLoading = false,
                };
            }
        }
    }

    /// <summary>Rebinds event ownership without initiating an unsolicited first inventory read.</summary>
    public void SetConnection(IOperatorGatewayClient? client)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var epoch = client is { IsConnectedToGateway: true } ? client.SessionMutationConnectionEpoch : null;
            if (ReferenceEquals(client, _client) && epoch == _epoch) return;
            if (_client is not null && _cronHandler is not null)
                _client.CronEventReceived -= _cronHandler;
            ++_generation;
            _refreshAgain = false;
            _pending = null;
            var replaceInventory = client is not null && (!ReferenceEquals(client, _client) || epoch != _epoch);
            _client = client;
            _epoch = epoch;
            _state = replaceInventory ? new() : _state with { IsCurrent = false, IsLoading = false, Error = null };
            _state = _state with { Client = client, ConnectionEpoch = epoch };
            _cronHandler = null;
            if (client is not null)
            {
                var bindingClient = client;
                var bindingEpoch = epoch;
                _cronHandler = (_, _) => OnCronEvent(bindingClient, bindingEpoch);
                client.CronEventReceived += _cronHandler;
            }
        }
        Publish();
    }

    /// <summary>Activates global reads and coalesces concurrent page/event refresh requests.</summary>
    public Task RefreshAsync()
    {
        SetConnection(currentClient());
        TaskCompletionSource completion;
        IOperatorGatewayClient client;
        long epoch;
        long generation;
        lock (_gate)
        {
            if (_disposed || !IsBindingCurrent(_client, _epoch)) return Task.CompletedTask;
            client = _client!;
            epoch = _epoch!.Value;
            _activated = true;
            _state = _state with { IsCurrent = false, IsLoading = true, Error = null };
            if (_pending is { IsCompleted: false })
            {
                _refreshAgain = true;
                ++_generation;
                Publish();
                return _pending;
            }
            generation = ++_generation;
            _refreshAgain = false;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = completion.Task;
        }
        Publish();
        _ = LoadAsync(client, epoch, generation, completion);
        return completion.Task;
    }

    private async Task LoadAsync(IOperatorGatewayClient client, long epoch, long generation, TaskCompletionSource completion)
    {
        try
        {
            while (true)
            {
                ImmutableArray<CronJob> jobs = [];
                Exception? failure = null;
                try
                {
                    var collected = new List<CronJob>();
                    foreach (var enabled in new[] { CronEnabledFilter.Enabled, CronEnabledFilter.Disabled })
                    {
                        var offset = 0;
                        while (true)
                        {
                            var page = await client.ListCronJobsAsync(new CronListQuery
                            {
                                AgentId = null, Enabled = enabled, SortBy = CronSortBy.NextRunAtMs,
                                Limit = 200, Offset = offset,
                            }).ConfigureAwait(false);
                            lock (_gate)
                                if (_disposed || !IsBindingCurrent(client, epoch)) return;
                            collected.AddRange(page.Jobs);
                            if (!page.HasMore) break;
                            if (page.Jobs.Count == 0) throw new InvalidOperationException(IncompletePageMessage);
                            offset += page.Jobs.Count;
                        }
                    }
                    jobs = collected.DistinctBy(job => job.Id).ToImmutableArray();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                lock (_gate)
                {
                    if (_disposed || !IsBindingCurrent(client, epoch)) return;
                    if (_refreshAgain)
                    {
                        _refreshAgain = false;
                        generation = _generation;
                        continue;
                    }
                    // A refresh after this handoff must start a loader, not invalidate
                    // the completed read while coalescing into its exiting task.
                    _pending = null;
                }
                var acceptedGeneration = generation;
                post(() =>
                {
                    lock (_gate)
                    {
                        if (_disposed || acceptedGeneration != _generation || !IsBindingCurrent(client, epoch)) return;
                        _state = failure is null
                            ? new AutomationsOverviewSnapshot
                            {
                                Jobs = jobs, Client = client, ConnectionEpoch = epoch, HasLoaded = true,
                                IsCurrent = true, LastSyncAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            }
                            : _state with
                            {
                                Jobs = CronGatewayRequests.IsForbidden(failure) ? [] : _state.Jobs,
                                HasLoaded = !CronGatewayRequests.IsForbidden(failure) && _state.HasLoaded,
                                IsCurrent = false, IsLoading = false, Error = failure.Message,
                            };
                    }
                    Changed?.Invoke(this, State);
                });
                return;
            }
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    private bool IsBindingCurrent(IOperatorGatewayClient? client, long? epoch) =>
        client is { IsConnectedToGateway: true } && epoch is not null &&
        ReferenceEquals(client, _client) && ReferenceEquals(client, currentClient()) &&
        client.SessionMutationConnectionEpoch == epoch && _epoch == epoch;

    private void OnCronEvent(IOperatorGatewayClient client, long? epoch)
    {
        lock (_gate)
        {
            if (_disposed || !_activated || !IsBindingCurrent(client, epoch)) return;
            _state = _state with { IsCurrent = false };
            ++_generation;
            if (_pending is { IsCompleted: false }) _refreshAgain = true;
        }
        post(() =>
        {
            lock (_gate)
                if (_disposed || !IsBindingCurrent(client, epoch)) return;
            // Notify consumers before a refresh can queue another publication.
            Changed?.Invoke(this, State);
            _ = RefreshAsync();
        });
    }

    private void Publish() => post(() =>
    {
        lock (_gate)
            if (_disposed) return;
        Changed?.Invoke(this, State);
    });

    /// <summary>Stops global observation; late RPC/event completions cannot publish into another host.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ++_generation;
            if (_client is not null && _cronHandler is not null)
                _client.CronEventReceived -= _cronHandler;
            _cronHandler = null;
            _state = new();
        }
    }
}
