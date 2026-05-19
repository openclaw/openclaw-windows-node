namespace OpenClaw.Chat;

public enum ChatThreadStatus
{
    Created,
    Running,
    Suspended
}

public enum ChatActivity
{
    Idle,
    Working,
    AwaitingInput,
    AwaitingPermission,
    Error
}

public enum ChatTimelineItemKind
{
    User,
    Assistant,
    ToolCall,
    Reasoning,
    Status,
    Raw
}

public enum ChatToolCallStatus
{
    InProgress,
    Success,
    Error,
    Interrupted
}

public enum ChatTone
{
    Info,
    Success,
    Warning,
    Error,
    Dim
}

public record ChatThread
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public ChatThreadStatus Status { get; init; }
    public ChatActivity Activity { get; init; }
    public string? Cwd { get; init; }
    public string? Workspace { get; init; }
    public string? Repository { get; init; }
    public string? Branch { get; init; }
    public string? HostName { get; init; }
    public string? Compute { get; init; }
    public string? ProfileName { get; init; }
    public string? Model { get; init; }
    public string? ThinkingLevel { get; init; }
    public int? HistoryCursor { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }

    public string DisplayTitle => Title;
}

public record ChatTimelineItem(
    string Id,
    ChatTimelineItemKind Kind,
    string Text,
    bool IsStreaming = false,
    string? ToolName = null,
    ChatToolCallStatus? ToolResult = null,
    string? ToolOutput = null,
    string? IntentSummary = null,
    JsonObject? ToolArgs = null,
    ChatTone? Tone = null);

public record ChatPermissionRequest(string RequestId, string PermissionKind, string ToolName, string Detail);

public record ChatTimelineState(
    System.Collections.Immutable.ImmutableList<ChatTimelineItem> Entries,
    bool TurnActive,
    int NextId,
    string? ActiveAssistantId,
    string? ActiveReasoningId,
    string? ActiveToolCallId,
    string? CurrentIntent,
    System.Collections.Immutable.ImmutableHashSet<string> LocalNonces,
    System.Collections.Immutable.ImmutableDictionary<string, string> ActiveToolCalls,
    bool HistoryLoaded = false,
    ChatPermissionRequest? PendingPermission = null)
{
    public static ChatTimelineState Initial() => new(
        System.Collections.Immutable.ImmutableList<ChatTimelineItem>.Empty,
        false, 1, null, null, null, null,
        System.Collections.Immutable.ImmutableHashSet<string>.Empty,
        System.Collections.Immutable.ImmutableDictionary<string, string>.Empty);
}

public record ChatHistoryPage(ChatEvent[] Events, int NextSince, int PrevBefore, bool HasMore);

public abstract record ChatEvent;
public record ChatUserMessageEvent(string Text, string? Nonce = null) : ChatEvent;
public record ChatThinkingEvent(string Text) : ChatEvent;
public record ChatReasoningEvent(string Text) : ChatEvent;
public record ChatReasoningDeltaEvent(string Text) : ChatEvent;
public record ChatMessageEvent(string Text, string? ReasoningText = null, bool ReconcilePrevious = false) : ChatEvent;
public record ChatMessageDeltaEvent(string Text) : ChatEvent;
public record ChatTurnEndEvent() : ChatEvent;
public record ChatIntentEvent(string Intent) : ChatEvent;
public record ChatToolStartEvent(string Text, string ToolName, JsonObject? ToolArgs = null, string? ToolCallId = null) : ChatEvent;
public record ChatToolOutputEvent(string Text, string? ToolCallId = null) : ChatEvent;
public record ChatToolErrorEvent(string Text, string? ToolCallId = null) : ChatEvent;
public record ChatContextChangedEvent(string? Cwd, string? GitBranch) : ChatEvent;
public record ChatStatusEvent(string Text, ChatTone Tone) : ChatEvent;
public record ChatErrorEvent(string Text) : ChatEvent;
public record ChatRestoredEvent(string Text) : ChatEvent;
public record ChatPermissionRequestEvent(string RequestId, string PermissionKind, string ToolName, string Detail) : ChatEvent;
public record ChatModelChangedEvent(string Model) : ChatEvent;
public record ChatRawEvent(string EventType, string? Text = null) : ChatEvent;

public record ChatDataSnapshot(
    ChatThread[] Threads,
    IReadOnlyDictionary<string, ChatTimelineState> Timelines,
    string? DefaultThreadId,
    string? ConnectionStatus,
    string[] AvailableModels,
    ChatComposeTarget ComposeTarget);

/// <summary>
/// Describes where the UI may send the next chat message. Distinct from
/// <see cref="ChatDataSnapshot.Threads"/> because, in protocols like the
/// OpenClaw gateway, there is always a canonical "main" target whose session
/// row may not have been materialized yet (zero sessions on fresh install).
/// The UI uses this to decide whether the composer should be enabled even
/// when <see cref="ChatDataSnapshot.Threads"/> is empty.
/// </summary>
/// <param name="SessionKey">
/// The provider-resolved canonical session key for the default send target,
/// or <c>null</c> when not yet known (e.g. handshake incomplete).
/// </param>
/// <param name="IsReady">
/// True when the provider is connected, has resolved a canonical send target,
/// and accepts <see cref="IChatDataProvider.SendMessageAsync"/> calls keyed by
/// <see cref="SessionKey"/>.
/// </param>
public sealed record ChatComposeTarget(string? SessionKey, bool IsReady)
{
    public static ChatComposeTarget NotReady { get; } = new(null, false);
}

public sealed class ChatDataChangedEventArgs(ChatDataSnapshot snapshot) : EventArgs
{
    public ChatDataSnapshot Snapshot { get; } = snapshot;
}

public enum ChatProviderNotificationKind
{
    TurnComplete,
    PermissionRequested,
    Error
}

public record ChatProviderNotification(
    ChatProviderNotificationKind Kind,
    string ThreadId,
    string Title,
    string? Message = null,
    string? ToolName = null);

public sealed class ChatProviderNotificationEventArgs(ChatProviderNotification notification) : EventArgs
{
    public ChatProviderNotification Notification { get; } = notification;
}

public interface IChatDataProvider : IAsyncDisposable
{
    string DisplayName { get; }

    event EventHandler<ChatDataChangedEventArgs>? Changed;
    event EventHandler<ChatProviderNotificationEventArgs>? NotificationRequested;

    Task<ChatDataSnapshot> LoadAsync(CancellationToken cancellationToken = default);
    // Note: there is intentionally no CreateThreadAsync. The gateway protocol
    // has no "create new session" RPC; the canonical send target is exposed via
    // ChatDataSnapshot.ComposeTarget and the first SendMessageAsync against it
    // implicitly materializes the session on the server.
    Task SendMessageAsync(string threadId, string message, CancellationToken cancellationToken = default);
    Task SendMessageAsync(string threadId, string message, CancellationToken cancellationToken, IReadOnlyList<OpenClaw.Shared.ChatAttachment>? attachments) =>
        SendMessageAsync(threadId, message, cancellationToken);
    Task StopResponseAsync(string threadId, CancellationToken cancellationToken = default);
    Task SetThreadSuspendedAsync(string threadId, bool suspended, CancellationToken cancellationToken = default);
    Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken = default);
    Task SetModelAsync(string threadId, string model, CancellationToken cancellationToken = default);
    Task SetThinkingLevelAsync(string threadId, string thinkingLevel, CancellationToken cancellationToken = default);
    Task SetPermissionModeAsync(string threadId, bool allowAll, CancellationToken cancellationToken = default);
    Task RespondToPermissionAsync(string threadId, string requestId, bool allow, CancellationToken cancellationToken = default);
}
