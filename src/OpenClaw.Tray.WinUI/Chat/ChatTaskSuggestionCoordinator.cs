// One bridge's transient suggestion lifecycle. Example: Start(), Snapshot(), AcceptAsync(id, options).
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

internal interface IChatTaskSuggestionGateway
{
    long? TaskConnectionEpoch { get; }
    bool TaskSuggestionsSupported { get; }
    bool TaskAcceptanceSupported { get; }
    bool TaskAdministratorRequired { get; }
    bool TaskCloudSupported => false;
    bool TaskDismissalSupported { get; }
    event EventHandler? TaskSuggestionsInvalidated;
    event EventHandler? TaskOperatorIdentityInvalidated { add { } remove { } }
    event EventHandler<ConnectionStatus>? StatusChanged;
    Task<GatewayTaskSuggestionList> ListTaskSuggestionsAsync(long epoch);
    Task<string> AcceptTaskSuggestionAsync(GatewayTaskAcceptance request, long epoch);
    Task<bool> DismissTaskSuggestionAsync(string id, long epoch);
    Task RequestSessionsAsync();
    Task<string?> ReadTaskOperatorIdentityAsync(long epoch) => Task.FromResult<string?>(null);
}

internal sealed record ChatTaskCard(GatewayTaskSuggestion Suggestion, bool Busy = false,
    GatewayTaskAcceptance? Attempt = null, long? AttemptEpoch = null, bool Unconfirmed = false,
    string? StartedKey = null, bool RepositoryRequired = false, bool CloudProfileRequired = false, bool DismissFailed = false, bool CanRetry = true);
internal sealed record ChatTaskSnapshot(IReadOnlyList<ChatTaskCard> Cards, bool Connected,
    bool CanAccept, bool CanDismiss, bool RefreshFailed, long? ConnectionEpoch,
    bool AdministratorRequired, bool CanHideLocally, bool IdentityUnavailable = false, string? IdentityNoticeSession = null, bool CloudSupported = false,
    long PresentationRevision = 0, long SnapshotRevision = 0);

/// <summary>
/// Events only invalidate. Authoritative lists recover dropped events, including other clients' removals.
/// Acceptance retries are explicit, same-ID/same-options and restricted to the original connection.
/// No outcome is inferred from a pending-only list. Custody can survive provider replacement, never to disk.
/// </summary>
internal sealed class ChatTaskSuggestionCoordinator(IChatTaskSuggestionGateway gateway, TimeProvider? clock = null,
    ChatTaskSuggestionCustody? custody = null, string? gatewayId = null) : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    // Coalesce bursts without requiring an event-free snapshot window.
    private static readonly TimeSpan InvalidationRefreshInterval = TimeSpan.FromSeconds(1);
    private const int MaximumCards = 100;
    // Provider-lifetime suppression is bounded and never mistakes a pending snapshot for settlement.
    private readonly HashSet<string> _locallyHidden = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private Dictionary<string, ChatTaskCard> _cards = new(StringComparer.Ordinal);
    private ITimer? _poll;
    private Task? _refresh;
    private long _generation;
    // Observation-only metadata. Never participates in dispatch/retry/custody.
    private long _presentationRevision;
    private long _snapshotRevision;
    private ITimer? _eventRefresh;
    private bool _refreshFailed;
    private bool _identityUnavailable;
    private string? _identityNoticeSession;
    private string? _operatorIdentity;
    private long? _verifiedIdentityEpoch;
    private bool _started;
    private bool _disposed;

    internal event EventHandler? Changed;
    internal ChatTaskSnapshot Snapshot()
    {
        lock (_gate)
            return new((custody is not null && gatewayId is not null && _verifiedIdentityEpoch != gateway.TaskConnectionEpoch
                    ? Array.Empty<ChatTaskCard>() : _cards.Values.OrderByDescending(card => card.Suggestion.CreatedAt).ToArray()),
                gateway.TaskConnectionEpoch.HasValue, gateway.TaskAcceptanceSupported,
                gateway.TaskDismissalSupported, _refreshFailed, gateway.TaskConnectionEpoch,
                gateway.TaskAdministratorRequired, _locallyHidden.Count < MaximumCards,
                _identityUnavailable && gateway.TaskSuggestionsSupported && gateway.TaskConnectionEpoch.HasValue, _identityNoticeSession, gateway.TaskCloudSupported,
                _presentationRevision, ++_snapshotRevision);
    }

    /// <summary>Scope a connection prerequisite notice once to the first presented chat; never expose private receipt targets.</summary>
    internal void ObserveSession(string sessionKey)
    {
        lock (_gate)
        {
            if (_disposed || _identityNoticeSession is not null) return;
            _identityNoticeSession = sessionKey;
        }
        Notify();
    }

    internal void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
            gateway.StatusChanged += OnStatus;
            gateway.TaskSuggestionsInvalidated += OnInvalidated;
            gateway.TaskOperatorIdentityInvalidated += OnIdentityInvalidated;
            _poll = _clock.CreateTimer(_ => _ = PollAsync(), null, RefreshInterval, Timeout.InfiniteTimeSpan);
        }
        _ = RefreshAsync();
    }

    private async Task PollAsync()
    {
        await RefreshAsync().ConfigureAwait(false);
        lock (_gate)
            if (!_disposed) _poll?.Change(RefreshInterval, Timeout.InfiniteTimeSpan);
    }

    private void OnInvalidated(object? sender, EventArgs args)
    {
        lock (_gate)
        {
            if (_disposed || _eventRefresh is not null) return;
            _eventRefresh = _clock.CreateTimer(_ =>
            {
                lock (_gate) { _eventRefresh?.Dispose(); _eventRefresh = null; }
                _ = RefreshAsync();
            }, null, InvalidationRefreshInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnIdentityInvalidated(object? sender, EventArgs args)
    {
        if (custody is null || gatewayId is null) return;
        lock (_gate)
        {
            if (_disposed) return;
            _generation++;
            _presentationRevision++;
            _verifiedIdentityEpoch = null;
            _identityUnavailable = false;
            _refresh = null;
            _cards = _cards.Where(pair => pair.Value.Attempt is not null && pair.Value.StartedKey is null)
                .ToDictionary(pair => pair.Key, pair => pair.Value with { Busy = false, Unconfirmed = true }, StringComparer.Ordinal);
        }
        Notify();
        _ = RefreshAsync();
    }

    private void OnStatus(object? sender, ConnectionStatus status)
    {
        lock (_gate)
        {
            _generation++;
            _verifiedIdentityEpoch = null;
            _identityUnavailable = false;
            _refresh = null;
            _eventRefresh?.Dispose();
            _eventRefresh = null;
            // Retain ambiguous acceptance as a non-replayable receipt, not as a fresh proposal.
            _cards = _cards.Where(pair => pair.Value.Attempt is not null && pair.Value.StartedKey is null)
                .ToDictionary(pair => pair.Key, pair => pair.Value with { Busy = false, Unconfirmed = true }, StringComparer.Ordinal);
            _refreshFailed = false;
        }
        Notify();
        if (status == ConnectionStatus.Connected) _ = RefreshAsync();
    }

    internal Task RefreshAsync()
    {
        lock (_gate)
        {
            if (_disposed || gateway.TaskConnectionEpoch is not { } epoch || !gateway.TaskSuggestionsSupported)
                return Task.CompletedTask;
            if (_refresh is not null) return _refresh;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _refresh = completion.Task;
            _ = RefreshCoreAsync(epoch, _generation, completion);
            return completion.Task;
        }
    }

    private async Task RefreshCoreAsync(long epoch, long generation, TaskCompletionSource completion)
    {
        try
        {
            // Registry identity comes from the connection manager, never the URL. The profile and
            // signing device are verified on this exact authenticated client before importing private data.
            string? identity;
            lock (_gate)
            {
                if (!Current(epoch, generation)) return;
                identity = _verifiedIdentityEpoch == epoch ? _operatorIdentity : null;
            }
            if (custody is not null && gatewayId is not null && identity is null)
                identity = await gateway.ReadTaskOperatorIdentityAsync(epoch).ConfigureAwait(false);
            lock (_gate)
            {
                if (!Current(epoch, generation)) return;
                if (custody is not null && gatewayId is not null)
                {
                    if (identity is null) { _verifiedIdentityEpoch = null; _identityUnavailable = true; return; } // Unknown operator cannot see or replay retained data.
                    if (_operatorIdentity != identity)
                    {
                        _presentationRevision++;
                        var restored = custody.Restore(gatewayId, identity);
                        _cards = restored.Cards.ToDictionary(card => card.Suggestion.Id, StringComparer.Ordinal);
                        _locallyHidden.Clear();
                        foreach (var id in restored.Hidden) _locallyHidden.Add(id);
                        _operatorIdentity = identity;
                    }
                    _verifiedIdentityEpoch = epoch;
                    _identityUnavailable = false;
                }
            }
            var result = await gateway.ListTaskSuggestionsAsync(epoch).ConfigureAwait(false);
            lock (_gate)
            {
                if (!Current(epoch, generation)) return;
                var next = new Dictionary<string, ChatTaskCard>(StringComparer.Ordinal);
                foreach (var suggestion in result.Suggestions)
                    if (!_locallyHidden.Contains(suggestion.Id))
                        next[suggestion.Id] = _cards.TryGetValue(suggestion.Id, out var prior)
                            ? prior with { Suggestion = suggestion } : new(suggestion);
                // A missing ID does not distinguish accepting, accepted, expired or a restarted server.
                foreach (var card in _cards.Values.Where(card => card.Attempt is not null && card.StartedKey is null))
                    next.TryAdd(card.Suggestion.Id, card);
                // Match the producer's bounded volatile registry. Never accumulate receipts indefinitely.
                _cards = next.Values.OrderByDescending(card => card.Attempt is not null)
                    .ThenByDescending(card => card.Suggestion.CreatedAt).Take(MaximumCards)
                    .ToDictionary(card => card.Suggestion.Id, StringComparer.Ordinal);
                _refreshFailed = false;
            }
        }
        catch
        {
            lock (_gate)
                if (Current(epoch, generation))
                {
                    if (custody is not null && gatewayId is not null && _verifiedIdentityEpoch != epoch)
                        _identityUnavailable = true;
                    else _refreshFailed = true;
                }
        }
        finally
        {
            lock (_gate) if (ReferenceEquals(_refresh, completion.Task)) _refresh = null;
            completion.TrySetResult();
            Notify();
        }
    }

    internal async Task AcceptAsync(GatewayTaskAcceptance request)
    {
        GatewayTaskSuggestions.Validate(request);
        long epoch;
        long generation;
        bool retryingUnconfirmed;
        lock (_gate)
        {
            if (_disposed || !gateway.TaskAcceptanceSupported || gateway.TaskConnectionEpoch is not { } current
                || (custody is not null && gatewayId is not null && _verifiedIdentityEpoch != current)
                || !_cards.TryGetValue(request.TaskId, out var card) || card.Busy || card.StartedKey is not null) return;
            retryingUnconfirmed = card.Attempt is not null;
            if (card.Attempt is not null)
            {
                if (!card.CanRetry || card.AttemptEpoch != current) return;
                request = card.Attempt; // Never change parameters after an uncertain dispatch.
            }
            if (request.Mode == "worktree" && request.Cwd is not null
                && !GatewayTaskSuggestions.IsAbsoluteGatewayPath(request.Cwd))
            {
                _cards[request.TaskId] = card with { RepositoryRequired = true };
                Notify();
                return;
            }
            epoch = current;
            generation = _generation;
            _cards[request.TaskId] = card with { Busy = true, Attempt = request, AttemptEpoch = epoch,
                Unconfirmed = false, RepositoryRequired = false, CloudProfileRequired = false, DismissFailed = false };
        }
        Notify();
        try
        {
            var key = await gateway.AcceptTaskSuggestionAsync(request, epoch).ConfigureAwait(false);
            lock (_gate)
            {
                if (!Current(epoch, generation)) return;
                _cards[request.TaskId] = _cards[request.TaskId] with { Busy = false, StartedKey = key };
            }
            // A confirmed launch updates the sidebar without selecting it or touching the retained composer.
            try { await gateway.RequestSessionsAsync().ConfigureAwait(false); } catch { }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (Current(epoch, generation) && _cards.TryGetValue(request.TaskId, out var card))
                {
                    // This producer code is emitted only before side effects, after restoring the claim.
                    // Other INVALID_REQUEST/UNAVAILABLE replies can follow partial work and stay uncertain.
                    var repositoryRequired = !retryingUnconfirmed && request.Mode == "worktree"
                        && GatewayRequestErrors.Code(ex) == "INVALID_REQUEST"
                        && GatewayRequestErrors.DetailCode(ex) == GatewayRequestErrors.TaskWorktreeSourceRequired;
                    _cards[request.TaskId] = ex is GatewayTaskCloudProfileException && !retryingUnconfirmed
                        ? card with { Busy = false, Attempt = null, AttemptEpoch = null, CloudProfileRequired = true }
                        : repositoryRequired
                        ? card with { Busy = false, Attempt = null, AttemptEpoch = null, RepositoryRequired = true }
                        : card with { Busy = false, Unconfirmed = true };
                }
            }
        }
        finally { Notify(); }
    }

    /// <summary>Hides only this provider's settled local uncertainty receipt. No RPC or outcome inference.</summary>
    internal bool HideLocally(string id)
    {
        lock (_gate)
        {
            if (_disposed || _locallyHidden.Count >= MaximumCards || !_cards.TryGetValue(id, out var card)
                || card.Busy || card.Attempt is null || card.StartedKey is not null) return false;
            _locallyHidden.Add(id);
            _cards.Remove(id);
        }
        Notify();
        return true;
    }

    internal async Task DismissAsync(string id)
    {
        long epoch;
        long generation;
        lock (_gate)
        {
            if (_disposed || !gateway.TaskDismissalSupported || gateway.TaskConnectionEpoch is not { } current
                || !_cards.TryGetValue(id, out var card) || card.Busy || card.Attempt is not null) return;
            epoch = current;
            generation = _generation;
            _cards[id] = card with { Busy = true, DismissFailed = false };
        }
        Notify();
        try
        {
            await gateway.DismissTaskSuggestionAsync(id, epoch).ConfigureAwait(false);
            // Even dismissed:false can mean accepting elsewhere. Only the next list decides visibility.
        }
        catch
        {
            lock (_gate)
                if (Current(epoch, generation) && _cards.TryGetValue(id, out var failed))
                    _cards[id] = failed with { DismissFailed = true };
        }
        finally
        {
            lock (_gate)
                if (Current(epoch, generation) && _cards.TryGetValue(id, out var card)) _cards[id] = card with { Busy = false };
            await RefreshAsync().ConfigureAwait(false);
            Notify();
        }
    }

    private bool Current(long epoch, long generation) => !_disposed && _generation == generation && gateway.TaskConnectionEpoch == epoch;
    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _poll?.Dispose();
            _eventRefresh?.Dispose();
            gateway.StatusChanged -= OnStatus;
            gateway.TaskSuggestionsInvalidated -= OnInvalidated;
            gateway.TaskOperatorIdentityInvalidated -= OnIdentityInvalidated;
            if (gatewayId is not null && _operatorIdentity is not null)
                custody?.Retain(gatewayId, _operatorIdentity, _cards.Values, _locallyHidden);
            _cards.Clear();
            _locallyHidden.Clear();
        }
    }
}
