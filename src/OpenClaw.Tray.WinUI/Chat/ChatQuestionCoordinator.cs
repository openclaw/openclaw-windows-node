using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

internal sealed record ChatQuestionPrompt(
    GatewayQuestionRequest Request, bool Submitting = false, string? Error = null,
    bool NeedsRefresh = false, long Revision = 0, bool AwaitingResolution = false);
internal sealed record ChatQuestionSnapshot(IReadOnlyList<ChatQuestionPrompt> Prompts, bool Connected, string? Error);

/// <summary>
/// Owns transient question recovery and submissions for one gateway bridge.
/// This domain is independent of transcript runs: questions can outlive a turn.
/// No answers or question drafts are persisted.
/// </summary>
internal sealed class ChatQuestionCoordinator : IDisposable
{
    private readonly IChatQuestionGateway _gateway;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, ChatQuestionPrompt> _prompts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Status, long Revision)> _resolved = new(StringComparer.Ordinal);
    private ITimer? _expiry;
    private ITimer? _retry;
    private Task? _refreshTask;
    private int _retryAttempt;
    private long _retryVersion;
    private long _generation;
    private long _revision;
    private bool _started;
    private bool _disposed;
    private bool _connected;
    private string? _error;

    internal ChatQuestionCoordinator(IChatQuestionGateway gateway, TimeProvider? clock = null)
    {
        _gateway = gateway;
        _clock = clock ?? TimeProvider.System;
    }

    internal event EventHandler? Changed;

    internal ChatQuestionSnapshot Snapshot()
    {
        lock (_gate)
            return new(_prompts.Values.OrderBy(prompt => prompt.Request.CreatedAtMs).ToArray(), _connected, _error);
    }

    internal void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
                return;
            _started = true;
            _gateway.QuestionReceived += OnQuestion;
            _gateway.QuestionProtocolError += OnProtocolError;
            _gateway.StatusChanged += OnStatus;
            _connected = _gateway.IsConnected;
            _expiry = _clock.CreateTimer(_ => Expire(), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
        if (_gateway.IsConnected)
            _ = RefreshAsync();
    }

    internal Task RefreshAsync() => RefreshAsync(resetRetry: true);

    private Task RefreshAsync(bool resetRetry)
    {
        long generation;
        long revision;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_disposed || !_connected)
                return Task.CompletedTask;
            if (_refreshTask is not null)
                return _refreshTask;
            CancelRetryLocked();
            if (resetRetry)
                _retryAttempt = 0;
            generation = _generation;
            revision = _revision;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _refreshTask = completion.Task;
        }
        _ = RefreshCoreAsync(generation, revision, completion);
        return completion.Task;
    }

    private async Task RefreshCoreAsync(long generation, long revision, TaskCompletionSource completion)
    {
        var complete = false;
        try
        {
            var result = await _gateway.ListQuestionsAsync().ConfigureAwait(false);
            string[] missing;
            lock (_gate)
            {
                if (!Current(generation))
                    return;
                _error = null;
                if (!result.IsSupported)
                {
                    complete = true;
                    return;
                }
                // Fence each identity independently so a live event cannot hide unrelated recovered prompts.
                foreach (var request in result.Questions)
                    if (!_prompts.TryGetValue(request.Id, out var prior) || prior.Revision <= revision)
                        Store(request, recovered: true);
                var listed = result.Questions.Select(request => request.Id).ToHashSet(StringComparer.Ordinal);
                missing = _prompts.Values
                    .Where(prompt => (prompt.Request.Status == "pending" || prompt.NeedsRefresh) && prompt.Revision <= revision
                        && !listed.Contains(prompt.Request.Id))
                    .Select(prompt => prompt.Request.Id).ToArray();
            }
            Notify();
            var recovered = await Task.WhenAll(missing.Select(id =>
                RecoverQuestionAsync(id, generation, revision))).ConfigureAwait(false);
            lock (_gate)
                complete = recovered.All(success => success)
                    && !_prompts.Values.Any(prompt => prompt.NeedsRefresh && !prompt.Submitting);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ChatQuestions] Recovery failed: {ex.GetType().Name}");
            lock (_gate)
            {
                if (!Current(generation))
                    return;
                _error = "Questions could not be refreshed. Retry or open web chat.";
            }
            Notify();
        }
        finally
        {
            lock (_gate)
            {
                if (Current(generation) && ReferenceEquals(_refreshTask, completion.Task))
                {
                    _refreshTask = null;
                    if (complete)
                        _retryAttempt = 0;
                    else
                        ScheduleRetryLocked(generation);
                }
            }
            completion.TrySetResult();
            Notify();
        }
    }

    private async Task<bool> RecoverQuestionAsync(string id, long generation, long revision)
    {
        try
        {
            var request = await _gateway.GetQuestionAsync(id).ConfigureAwait(false);
            lock (_gate)
            {
                if (!Current(generation))
                    return true;
                if (request.Id != id)
                    throw new InvalidOperationException("Question identity changed during recovery.");
                if (_prompts.TryGetValue(id, out var prior) && prior.Revision <= revision)
                    Store(request, recovered: true);
            }
            return true;
        }
        catch (GatewayQuestionUnavailableException)
        {
            lock (_gate)
                if (Current(generation) && _prompts.TryGetValue(id, out var prior) && prior.Revision <= revision)
                    Resolve(id, "unavailable");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ChatQuestions] Question recovery failed: {ex.GetType().Name}");
            lock (_gate)
            {
                if (!Current(generation))
                    return true;
                if (_prompts.TryGetValue(id, out var prior) && prior.Revision <= revision)
                    _prompts[id] = prior with
                    {
                        NeedsRefresh = true,
                        Error = "Questions could not be refreshed. Retry or open web chat.",
                    };
            }
            return false;
        }
        finally
        {
            Notify();
        }
    }

    private void ScheduleRetryLocked(long generation)
    {
        if (!Current(generation) || _retry is not null || _retryAttempt >= 3)
            return;
        var delay = TimeSpan.FromSeconds(1 << _retryAttempt++);
        var version = ++_retryVersion;
        _retry = _clock.CreateTimer(_ =>
        {
            lock (_gate)
            {
                if (!Current(generation) || _retryVersion != version)
                    return;
                CancelRetryLocked();
            }
            _ = RefreshAsync(resetRetry: false);
        }, null, delay, Timeout.InfiniteTimeSpan);
    }

    private void CancelRetryLocked()
    {
        _retryVersion++;
        _retry?.Dispose();
        _retry = null;
    }

    internal async Task SubmitAsync(string sessionKey, string? agentId, string id,
        IReadOnlyDictionary<string, string[]>? answers)
    {
        long generation;
        lock (_gate)
        {
            if (_disposed)
                return;
            if (!_prompts.TryGetValue(id, out var prompt)
                || !Matches(prompt.Request, sessionKey, agentId))
                throw new InvalidOperationException("Question does not belong to this conversation.");
            if (prompt.Submitting || prompt.NeedsRefresh || prompt.Request.Status != "pending")
                return;
            if (!_connected || prompt.Request.ExpiresAtMs <= _clock.GetUtcNow().ToUnixTimeMilliseconds())
            {
                _prompts[id] = prompt with { Error = "This question is expired or disconnected. Refresh before answering." };
                generation = -1;
            }
            else
            {
                if (answers is not null)
                {
                    answers = answers.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
                    ChatQuestionDraft.Validate(prompt.Request, answers);
                }
                generation = _generation;
                _prompts[id] = prompt with { Submitting = true, Error = null, Revision = ++_revision };
            }
        }
        Notify();
        if (generation < 0)
            return;
        try
        {
            var status = await _gateway.ResolveQuestionAsync(id, answers).ConfigureAwait(false);
            if (status != (answers is null ? "cancelled" : "answered"))
                throw new InvalidOperationException("Unexpected question resolution.");
            lock (_gate)
            {
                if (!Current(generation))
                    return;
                if (!_resolved.ContainsKey(id))
                    Resolve(id, status);
            }
        }
        catch (GatewayQuestionUnavailableException)
        {
            lock (_gate)
                if (Current(generation) && !_resolved.ContainsKey(id))
                    Resolve(id, "unavailable");
        }
        catch (GatewayQuestionAlreadyTerminalException)
        {
            long revision;
            lock (_gate)
            {
                if (!Current(generation) || _resolved.ContainsKey(id) || !_prompts.TryGetValue(id, out var prompt))
                    return;
                revision = ++_revision;
                _prompts[id] = prompt with
                {
                    Submitting = false, NeedsRefresh = true, AwaitingResolution = true, Revision = revision,
                    Error = "This question is being resolved. Refresh to check its status.",
                };
            }
            Notify();
            await RecoverQuestionAsync(id, generation, revision).ConfigureAwait(false);
            lock (_gate)
                if (Current(generation) && _prompts.TryGetValue(id, out var prompt) && prompt.NeedsRefresh)
                {
                    _retryAttempt = 0;
                    ScheduleRetryLocked(generation);
                }
        }
        catch (GatewayQuestionInvalidAnswerException)
        {
            lock (_gate)
                if (Current(generation) && !_resolved.ContainsKey(id)
                    && _prompts.TryGetValue(id, out var prompt) && prompt.Request.Status == "pending")
                    _prompts[id] = prompt with
                    {
                        Submitting = false, NeedsRefresh = false, Revision = ++_revision,
                        Error = "Choose a valid answer for each question.",
                    };
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ChatQuestions] Submission failed: {ex.GetType().Name}");
            lock (_gate)
            {
                if (!Current(generation))
                    return;
                if (_prompts.TryGetValue(id, out var prompt) && prompt.Request.Status == "pending")
                    _prompts[id] = prompt with
                    {
                        Submitting = false,
                        NeedsRefresh = true,
                        Revision = ++_revision,
                        Error = "The answer was not confirmed. Refresh to check its status before retrying.",
                    };
                _retryAttempt = 0;
                ScheduleRetryLocked(generation);
            }
        }
        Notify();
    }

    internal static bool Matches(GatewayQuestionRequest request, string sessionKey, string? agentId) =>
        (request.SessionKey is null || string.Equals(request.SessionKey, sessionKey, StringComparison.Ordinal))
        && (request.AgentId is null || string.Equals(request.AgentId, agentId, StringComparison.Ordinal));

    private bool Current(long generation) => !_disposed && _connected && _generation == generation;

    private void OnQuestion(object? sender, GatewayQuestionEvent evt)
    {
        lock (_gate)
        {
            if (_disposed || !_connected)
                return;
            if (evt.Request is { } request)
                Store(request);
            else
                Resolve(evt.Id, evt.Status);
        }
        Notify();
    }

    private void Store(GatewayQuestionRequest request, bool recovered = false)
    {
        _prompts.TryGetValue(request.Id, out var previous);
        if (_resolved.TryGetValue(request.Id, out var terminal))
            request = request with { Status = terminal.Status };
        else if (request.Status != "pending")
            _resolved[request.Id] = (request.Status, _revision + 1);
        // ALREADY_TERMINAL also covers in-flight or unknown Gateway commits.
        // A pending read cannot make that answer safe to submit again.
        var awaitingResolution = request.Status == "pending" && previous?.AwaitingResolution == true;
        if (request.ExpiresAtMs <= _clock.GetUtcNow().ToUnixTimeMilliseconds() && request.Status == "pending")
            request = request with { Status = "expired" };
        var pending = request.Status == "pending";
        _prompts[request.Id] = new(request,
            Submitting: pending && previous?.Submitting == true,
            Error: pending && (!recovered || awaitingResolution) ? previous?.Error : null,
            NeedsRefresh: awaitingResolution || (pending && !recovered && previous?.NeedsRefresh == true),
            Revision: ++_revision, AwaitingResolution: awaitingResolution);
        Trim();
    }

    private void Resolve(string id, string status)
    {
        _revision++;
        _resolved[id] = (status, _revision);
        if (_prompts.TryGetValue(id, out var prompt))
            _prompts[id] = prompt with
            {
                Request = prompt.Request with { Status = status }, Submitting = false,
                NeedsRefresh = false, AwaitingResolution = false, Error = null, Revision = _revision,
            };
        Trim();
    }

    private void Trim()
    {
        while (_resolved.Count > 256)
            _resolved.Remove(_resolved.MinBy(pair => pair.Value.Revision).Key);
        foreach (var prompt in _prompts.Values.Where(prompt => prompt.Request.Status != "pending")
                     .OrderByDescending(prompt => prompt.Request.CreatedAtMs).Skip(3).ToArray())
            _prompts.Remove(prompt.Request.Id);
    }

    private void OnStatus(object? sender, ConnectionStatus status)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _generation++;
            CancelRetryLocked();
            _refreshTask = null;
            _retryAttempt = 0;
            _connected = status == ConnectionStatus.Connected;
            foreach (var pair in _prompts.ToArray())
                _prompts[pair.Key] = pair.Value with
                {
                    Submitting = false, NeedsRefresh = pair.Value.Request.Status == "pending",
                    Revision = ++_revision,
                };
        }
        Notify();
        if (status == ConnectionStatus.Connected)
            _ = RefreshAsync();
    }

    private void OnProtocolError(object? sender, EventArgs args)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _error = "An agent question could not be read. Refresh or open web chat to respond.";
            ScheduleRetryLocked(_generation);
        }
        Notify();
    }

    private void Expire()
    {
        var changed = false;
        lock (_gate)
        {
            if (_disposed)
                return;
            foreach (var pair in _prompts.ToArray())
            {
                if (pair.Value.Request.Status != "pending"
                    || pair.Value.Request.ExpiresAtMs > _clock.GetUtcNow().ToUnixTimeMilliseconds())
                    continue;
                // Local clock expiry is not an authoritative terminal tombstone.
                _prompts[pair.Key] = pair.Value with
                {
                    Request = pair.Value.Request with { Status = "expired" },
                    Submitting = false, Revision = ++_revision,
                };
                changed = true;
            }
            Trim();
            _expiry?.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
        if (changed)
            Notify();
    }

    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _generation++;
            _expiry?.Dispose();
            CancelRetryLocked();
            _refreshTask = null;
            _gateway.QuestionReceived -= OnQuestion;
            _gateway.QuestionProtocolError -= OnProtocolError;
            _gateway.StatusChanged -= OnStatus;
            _prompts.Clear();
            _resolved.Clear();
        }
    }
}
