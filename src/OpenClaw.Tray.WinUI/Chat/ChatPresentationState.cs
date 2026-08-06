using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

internal sealed record ChatUsageSnapshot(
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    long ContextTokens);

/// <summary>
/// Owns session/model/catalog presentation inputs, serialized model patches,
/// keyless diagnostics, and remembered chat selection. The root serializes
/// every operation and supplies immutable timeline/queue snapshots.
/// </summary>
internal sealed class ChatPresentationState
{
    private readonly Dictionary<string, Task> _pendingModelPatches = new();

    private SessionInfo[] _sessions = [];
    private bool _sessionsListReceived;
    private string[] _availableModels = [];
    private IReadOnlyList<ChatModelChoice> _modelChoices = [];
    private CommandCatalog? _commandCatalog;
    private bool _commandsFetchInFlight;
    private int _commandsEpoch;
    private int _keylessEventDiagnosticRaised;
    private OpenClawChatDataProvider.LastChatState? _lastChatState;

    internal ChatPresentationState(
        OpenClawChatDataProvider.LastChatState? lastChatState,
        ModelsListInfo? seedModels)
    {
        _lastChatState = lastChatState;
        if (seedModels is not null)
        {
            _modelChoices = ChatModelChoice.FromModelsList(seedModels);
            _availableModels = ModelIdsFromChoices(_modelChoices);
        }
        else if (lastChatState?.AvailableModels is { Length: > 0 } cached)
        {
            _availableModels = cached.ToArray();
            _modelChoices = ChoicesFromIds(cached);
        }
    }

    internal OpenClawChatDataProvider.LastChatState? CachedLastChatState =>
        _lastChatState;

    internal SessionInfo[] SessionSnapshot() => _sessions.ToArray();

    internal SessionInfo[] ReplaceSessions(
        SessionInfo[] sessions,
        bool receivedFromGateway = true)
    {
        var previous = _sessions;
        _sessions = sessions.ToArray();
        if (receivedFromGateway)
            _sessionsListReceived = true;
        return previous;
    }

    internal IReadOnlyDictionary<string, ChatUsageSnapshot> SnapshotUsage() =>
        _sessions
            .Where(session => !string.IsNullOrEmpty(session.Key))
            .ToDictionary(
                session => session.Key,
                session => new ChatUsageSnapshot(
                    session.InputTokens,
                    session.OutputTokens,
                    session.TotalTokens,
                    session.ContextTokens));

    internal SessionInfo? FindSession(string threadId) =>
        _sessions.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, threadId, StringComparison.Ordinal));

    internal string? ModelForThread(string threadId) =>
        FindSession(threadId)?.Model;

    internal long? ContextTokensForThread(string threadId) =>
        FindSession(threadId) is { ContextTokens: > 0 } session
            ? session.ContextTokens
            : null;

    internal OpenClawChatDataProvider.LastChatState? RememberSelectedThread(
        string threadId)
    {
        var session = FindSession(threadId);
        if (session is null)
            return null;

        _lastChatState = new OpenClawChatDataProvider.LastChatState
        {
            DefaultThreadId = threadId,
            ThreadTitle = SessionTitleFormatter.Format(session, _sessions),
            Model = session.Model,
            ModelProvider = session.Provider,
            AvailableModels = _availableModels.ToArray(),
        };
        return _lastChatState;
    }

    internal void RememberLastSessionState(ChatProjectionContext context)
    {
        if (_sessions.Length == 0)
            return;

        var defaultThreadId = ChatSnapshotProjector.ResolveDefaultThreadId(
            CaptureProjectionInput(
                timelines: new Dictionary<string, ChatTimelineState>(),
                timelineGenerations: new Dictionary<string, long>(),
                historyRevisions: new Dictionary<string, long>(),
                queuedMessages: new Dictionary<string, IReadOnlyList<ChatQueuedMessage>>(),
                status: ConnectionStatus.Disconnected,
                context));
        var session = defaultThreadId is { Length: > 0 }
            ? FindSession(defaultThreadId)
            : null;
        session ??= _sessions.FirstOrDefault(candidate =>
            candidate.IsMain && !string.IsNullOrEmpty(candidate.Key));
        session ??= _sessions.FirstOrDefault(candidate =>
            !string.IsNullOrEmpty(candidate.Key));
        if (session is null)
            return;

        _lastChatState = new OpenClawChatDataProvider.LastChatState
        {
            DefaultThreadId = session.Key,
            ThreadTitle = SessionTitleFormatter.Format(session, _sessions),
            Model = session.Model,
            ModelProvider = session.Provider,
            AvailableModels = _availableModels.ToArray(),
        };
    }

    internal void ApplyModels(ModelsListInfo models)
    {
        _modelChoices = ChatModelChoice.FromModelsList(models);
        _availableModels = ModelIdsFromChoices(_modelChoices);
    }

    internal void LeaveConnected()
    {
        _sessionsListReceived = false;
        _commandsEpoch++;
        _commandCatalog = null;
        _commandsFetchInFlight = false;
    }

    internal ChatModelPatchLease BeginModelPatch(string threadId)
    {
        _pendingModelPatches.TryGetValue(threadId, out var previous);
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingModelPatches[threadId] = completion.Task;
        return new(threadId, previous, completion);
    }

    internal void CompleteModelPatch(
        ChatModelPatchLease lease,
        Exception? error)
    {
        if (error is null)
            lease.Completion.TrySetResult();
        else
            lease.Completion.TrySetException(error);

        if (_pendingModelPatches.TryGetValue(lease.ThreadId, out var current) &&
            ReferenceEquals(current, lease.Completion.Task))
        {
            _pendingModelPatches.Remove(lease.ThreadId);
        }
    }

    internal Task? GetPendingModelPatch(string threadId) =>
        _pendingModelPatches.TryGetValue(threadId, out var pending)
            ? pending
            : null;

    internal bool TryBeginCommandCatalogFetch(
        ConnectionStatus status,
        out int epoch)
    {
        epoch = _commandsEpoch;
        if (status != ConnectionStatus.Connected ||
            _commandsFetchInFlight ||
            _commandCatalog is not null)
        {
            return false;
        }
        _commandsFetchInFlight = true;
        return true;
    }

    internal bool CompleteCommandCatalogFetch(
        int epoch,
        ConnectionStatus status,
        CommandCatalog catalog)
    {
        if (epoch != _commandsEpoch || status != ConnectionStatus.Connected)
            return false;
        _commandsFetchInFlight = false;
        _commandCatalog = catalog;
        return true;
    }

    internal bool FailCommandCatalogFetch(
        int epoch,
        ConnectionStatus status)
    {
        if (epoch != _commandsEpoch || status != ConnectionStatus.Connected)
            return false;
        _commandsFetchInFlight = false;
        _commandCatalog = new CommandCatalog { IsSupported = false };
        return true;
    }

    internal bool IsCommandCatalogEpochCurrent(int epoch) =>
        epoch == _commandsEpoch;

    internal bool TryRaiseKeylessDiagnostic()
    {
        if (_keylessEventDiagnosticRaised != 0)
            return false;
        _keylessEventDiagnosticRaised = 1;
        return true;
    }

    internal void ResetKeylessDiagnostic() =>
        _keylessEventDiagnosticRaised = 0;

    internal ChatSnapshotProjectionInput CaptureProjectionInput(
        IReadOnlyDictionary<string, ChatTimelineState> timelines,
        IReadOnlyDictionary<string, long> timelineGenerations,
        IReadOnlyDictionary<string, long> historyRevisions,
        IReadOnlyDictionary<string, IReadOnlyList<ChatQueuedMessage>> queuedMessages,
        ConnectionStatus status,
        ChatProjectionContext context) => new(
        Sessions: _sessions.ToArray(),
        Timelines: timelines,
        TimelineGenerations: timelineGenerations,
        HistoryRevisions: historyRevisions,
        QueuedMessages: queuedMessages,
        SessionsListReceived: _sessionsListReceived,
        AvailableModels: _availableModels.ToArray(),
        ModelChoices: _modelChoices.ToArray(),
        CommandCatalog: _commandCatalog,
        Status: status,
        MainSessionKey: context.MainSessionKey,
        HasHandshakeSnapshot: context.HasHandshakeSnapshot,
        RememberedDefaultThreadId: _lastChatState?.DefaultThreadId,
        RememberedThreadTitle: _lastChatState?.ThreadTitle,
        RememberedModel: _lastChatState?.Model,
        RememberedModelProvider: _lastChatState?.ModelProvider);

    internal string ResolveTimelineKey(
        SessionInfo session,
        IReadOnlyDictionary<string, ChatTimelineState> timelines)
    {
        if (session.IsMain &&
            timelines.TryGetValue("main", out var mainTimeline) &&
            mainTimeline.Entries.Count > 0)
        {
            return "main";
        }
        if (!string.IsNullOrEmpty(session.Key) && timelines.ContainsKey(session.Key))
            return session.Key;
        if (session.IsMain && timelines.ContainsKey("main"))
            return "main";
        return session.Key ?? string.Empty;
    }

    internal SessionInfo? ResolveSessionForThread(
        string threadId,
        string? mainSessionKey)
    {
        var byKey = FindSession(threadId);
        if (byKey is not null)
            return byKey;
        if (string.Equals(threadId, "main", StringComparison.Ordinal) &&
            !string.IsNullOrEmpty(mainSessionKey))
        {
            var main = FindSession(mainSessionKey);
            if (main is not null)
                return main;
        }
        return string.Equals(threadId, "main", StringComparison.Ordinal)
            ? _sessions.FirstOrDefault(session => session.IsMain)
            : null;
    }

    private static string[] ModelIdsFromChoices(
        IReadOnlyList<ChatModelChoice> choices)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return choices
            .Where(choice => choice.IsSelectable && seen.Add(choice.Id))
            .Select(choice => choice.Id)
            .ToArray();
    }

    private static IReadOnlyList<ChatModelChoice> ChoicesFromIds(string[] ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return ids
            .Where(id => !string.IsNullOrEmpty(id) && seen.Add(id))
            .Select(id => new ChatModelChoice(id, id))
            .ToArray();
    }
}
