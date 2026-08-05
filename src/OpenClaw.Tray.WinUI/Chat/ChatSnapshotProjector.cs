using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

internal sealed record ChatSnapshotProjectionInput(
    SessionInfo[] Sessions,
    IReadOnlyDictionary<string, ChatTimelineState> Timelines,
    IReadOnlyDictionary<string, long> TimelineGenerations,
    IReadOnlyDictionary<string, long> HistoryRevisions,
    IReadOnlyDictionary<string, IReadOnlyList<ChatQueuedMessage>> QueuedMessages,
    bool SessionsListReceived,
    string[] AvailableModels,
    IReadOnlyList<ChatModelChoice> ModelChoices,
    CommandCatalog? CommandCatalog,
    ConnectionStatus Status,
    string? MainSessionKey,
    bool HasHandshakeSnapshot,
    string? RememberedDefaultThreadId,
    string? RememberedThreadTitle,
    string? RememberedModel,
    string? RememberedModelProvider);

internal static class ChatSnapshotProjector
{
    internal static ChatDataSnapshot Project(ChatSnapshotProjectionInput input)
    {
        var threadList = new List<ChatThread>(input.Sessions.Length + 1);
        var threadTitles = SessionTitleFormatter.FormatUnique(input.Sessions);
        for (var i = 0; i < input.Sessions.Length; i++)
            threadList.Add(ToThread(input.Sessions[i], threadTitles[i]));

        var composeKey = input.MainSessionKey;
        var composeAgentId = input.Sessions
            .FirstOrDefault(session =>
                string.Equals(session.Key, composeKey, StringComparison.Ordinal)) is { } mainSession
                    ? SessionDisplayResolver.Resolve(mainSession).AgentId ?? "main"
                    : "main";
        var composeReady = input.HasHandshakeSnapshot
            && !string.IsNullOrWhiteSpace(composeKey)
            && input.Status == ConnectionStatus.Connected
            && input.SessionsListReceived;

        if (composeReady
            && composeKey is { } key
            && input.Timelines.TryGetValue(key, out var pendingTimeline)
            && (pendingTimeline.Entries.Count > 0
                || pendingTimeline.TurnActive
                || input.QueuedMessages.TryGetValue(key, out var pendingQueue) &&
                   pendingQueue.Count > 0)
            && !input.Sessions.Any(session =>
                string.Equals(session.Key, key, StringComparison.Ordinal)))
        {
            threadList.Add(new ChatThread
            {
                Id = key,
                AgentId = composeAgentId,
                Title = input.RememberedThreadTitle ?? "OpenClaw Windows Tray",
                Model = input.RememberedModel,
                ModelProvider = input.RememberedModelProvider,
                Status = ChatThreadStatus.Running,
                Activity = ChatActivity.Idle,
            });
        }

        var connectionLabel = input.Status == ConnectionStatus.Connected
                              && input.HasHandshakeSnapshot
                              && string.IsNullOrWhiteSpace(composeKey)
            ? "Incompatible gateway"
            : input.Status switch
            {
                ConnectionStatus.Connected => "Connected",
                ConnectionStatus.Connecting => "Connecting…",
                ConnectionStatus.Disconnected => "Disconnected",
                ConnectionStatus.Error => "Disconnected — error",
                _ => input.Status.ToString(),
            };

        return new ChatDataSnapshot(
            Threads: threadList.ToArray(),
            Timelines: input.Timelines,
            DefaultThreadId: ResolveDefaultThreadId(input),
            ConnectionStatus: connectionLabel,
            AvailableModels: input.AvailableModels,
            ComposeTarget: composeReady
                ? new ChatComposeTarget(composeKey, true, composeAgentId)
                : ChatComposeTarget.NotReady,
            ModelChoices: input.ModelChoices,
            AvailableCommands: input.CommandCatalog?.Commands,
            CommandsSupported: input.CommandCatalog?.IsSupported ?? true,
            TimelineGenerations: input.TimelineGenerations,
            HistoryRevisions: input.HistoryRevisions,
            QueuedMessagesByThread: input.QueuedMessages);
    }

    internal static string? ResolveDefaultThreadId(ChatSnapshotProjectionInput input)
    {
        if (input.RememberedDefaultThreadId is { Length: > 0 } remembered &&
            (input.Sessions.Any(session =>
                 string.Equals(session.Key, remembered, StringComparison.Ordinal)) ||
             !input.SessionsListReceived))
        {
            return remembered;
        }

        foreach (var session in input.Sessions)
        {
            if (session.IsMain && !string.IsNullOrEmpty(session.Key))
                return session.Key;
        }
        if (input.HasHandshakeSnapshot &&
            input.MainSessionKey is { } mainKey &&
            !string.IsNullOrWhiteSpace(mainKey))
        {
            return mainKey;
        }
        return input.Sessions.FirstOrDefault(session =>
            !string.IsNullOrEmpty(session.Key))?.Key;
    }

    private static ChatThread ToThread(SessionInfo session, string title)
    {
        var display = SessionDisplayResolver.Resolve(session);
        return new ChatThread
        {
            Id = session.Key ?? string.Empty,
            Title = title,
            AgentId = display.AgentId,
            IsBackground = display.IsBackground,
            Status = SessionVisibilityFilter.ToChatThreadStatus(session),
            Activity = SessionVisibilityFilter.ToChatThreadActivity(session),
            Workspace = session.Channel,
            Model = session.Model,
            ModelProvider = session.Provider,
            ThinkingLevel = session.ThinkingLevel,
            InputTokens = session.InputTokens,
            OutputTokens = session.OutputTokens,
            TotalTokens = session.TotalTokens,
            ContextTokens = session.ContextTokens,
            CreatedAt = session.StartedAt is { } started ? ToOffset(started) : null,
            UpdatedAt = session.UpdatedAt is { } updated ? ToOffset(updated) : null,
        };
    }

    private static DateTimeOffset ToOffset(DateTime value)
    {
        if (value.Kind == DateTimeKind.Unspecified)
        {
            return new DateTimeOffset(
                DateTime.SpecifyKind(value, DateTimeKind.Utc),
                TimeSpan.Zero);
        }
        return new DateTimeOffset(value);
    }
}
