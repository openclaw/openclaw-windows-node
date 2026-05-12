using System.Text.Json.Nodes;

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
    Error
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
    bool HistoryLoaded = false,
    ChatPermissionRequest? PendingPermission = null)
{
    public static ChatTimelineState Initial() => new(
        System.Collections.Immutable.ImmutableList<ChatTimelineItem>.Empty,
        false, 1, null, null, null, null,
        System.Collections.Immutable.ImmutableHashSet<string>.Empty);
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
public record ChatToolStartEvent(string Text, string ToolName, JsonObject? ToolArgs = null) : ChatEvent;
public record ChatToolOutputEvent(string Text) : ChatEvent;
public record ChatToolErrorEvent(string Text) : ChatEvent;
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
    string[] AvailableModels);

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
    Task<ChatThread> CreateThreadAsync(string? initialMessage = null, CancellationToken cancellationToken = default);
    Task SendMessageAsync(string threadId, string message, CancellationToken cancellationToken = default);
    Task StopResponseAsync(string threadId, CancellationToken cancellationToken = default);
    Task SetThreadSuspendedAsync(string threadId, bool suspended, CancellationToken cancellationToken = default);
    Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken = default);
    Task SetModelAsync(string threadId, string model, CancellationToken cancellationToken = default);
    Task SetPermissionModeAsync(string threadId, bool allowAll, CancellationToken cancellationToken = default);
    Task RespondToPermissionAsync(string threadId, string requestId, bool allow, CancellationToken cancellationToken = default);
}
