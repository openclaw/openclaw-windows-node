namespace OpenClaw.Chat;

public enum ChatThreadStatus
{
    Created,
    Running,
    Suspended,
    Ended
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
    Raw,
    PermissionRequest
}

/// <summary>
/// Outcome of an exec-approval prompt, attached to a
/// <see cref="ChatTimelineItemKind.PermissionRequest"/> timeline entry.
/// </summary>
/// <remarks>
/// <para><see cref="Pending"/> is the initial state — Allow/Deny buttons
/// render and the matching <see cref="ChatTimelineState.PendingPermission"/>
/// slot is non-null.</para>
/// <para><see cref="Allowed"/> / <see cref="Denied"/> are set locally as
/// soon as the user clicks a button, so the inline bubble collapses to a
/// "decided" badge without waiting for the gateway round-trip.</para>
/// <para><see cref="Expired"/> is the backstop set when the gateway emits
/// a terminal approval phase (resolved / cancelled / timed-out) before the
/// user picked an option — e.g. another client decided, or the gateway
/// timed the prompt out. Visually distinguishes it from a user choice.</para>
/// </remarks>
public enum ChatPermissionDecision
{
    Pending = 0,
    Allowed = 1,
    Denied = 2,
    Expired = 3,
    AllowedAlways = 4
}

public static class ChatPermissionActionKeys
{
    public const string AllowOnce = "allow-once";
    public const string AllowAlways = "allow-always";
    public const string Deny = "deny";

    public static readonly string[] ExecApprovalDefaults = [AllowOnce, AllowAlways, Deny];

    public static string[] NormalizeActions(IReadOnlyList<string>? actions)
    {
        if (actions is not { Count: > 0 })
            return ExecApprovalDefaults;

        var normalized = actions
            .Where(action => !string.IsNullOrWhiteSpace(action))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length > 0 ? normalized : ExecApprovalDefaults;
    }
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
    public string? ModelProvider { get; init; }
    public string? ThinkingLevel { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long TotalTokens { get; init; }
    public long ContextTokens { get; init; }
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
    ChatTone? Tone = null,
    string? ToolCallId = null,
    string? PermissionRequestId = null,
    ChatPermissionDecision PermissionDecision = ChatPermissionDecision.Pending,
    IReadOnlyList<string>? PermissionActions = null);

public record ChatPermissionRequest(string RequestId, string PermissionKind, string ToolName, string Detail, IReadOnlyList<string>? Actions = null);

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

public enum ChatQueuedMessageSendState
{
    Queued,
    Sending,
    Failed
}

public record ChatQueuedMessage(
    string Id,
    string Text,
    DateTimeOffset CreatedAt,
    string LocalNonce,
    ChatQueuedMessageSendState SendState = ChatQueuedMessageSendState.Queued,
    string? ErrorText = null);

public record ChatHistoryPage(ChatEvent[] Events, int NextSince, int PrevBefore, bool HasMore);

public abstract record ChatEvent;
public record ChatUserMessageEvent(string Text, string? Nonce = null) : ChatEvent;
public record ChatThinkingEvent(string Text) : ChatEvent;
public record ChatReasoningEvent(string Text) : ChatEvent;
public record ChatReasoningDeltaEvent(string Text) : ChatEvent;
/// <summary>
/// Closes the current reasoning section so the next reasoning chunk starts a
/// fresh bubble instead of appending/replacing the previous one. Emitted from
/// the gateway's <c>stream:"item", kind:"reasoning", phase:"end"</c> bracket
/// marker that delimits each distinct thinking pass within a single turn.
/// </summary>
public record ChatReasoningEndEvent() : ChatEvent;
public record ChatMessageEvent(string Text, string? ReasoningText = null, bool ReconcilePrevious = false, bool IsStreaming = false) : ChatEvent;
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
public record ChatPermissionRequestEvent(string RequestId, string PermissionKind, string ToolName, string Detail, IReadOnlyList<string>? Actions = null) : ChatEvent;
public record ChatModelChangedEvent(string Model) : ChatEvent;
public record ChatRawEvent(string EventType, string? Text = null) : ChatEvent;

public record ChatDataSnapshot(
    ChatThread[] Threads,
    IReadOnlyDictionary<string, ChatTimelineState> Timelines,
    string? DefaultThreadId,
    string? ConnectionStatus,
    string[] AvailableModels,
    ChatComposeTarget ComposeTarget,
    IReadOnlyList<ChatModelChoice>? ModelChoices = null,
    IReadOnlyList<OpenClaw.Shared.GatewayCommand>? AvailableCommands = null,
    bool CommandsSupported = true,
    IReadOnlyDictionary<string, long>? TimelineGenerations = null,
    IReadOnlyDictionary<string, IReadOnlyList<ChatQueuedMessage>>? QueuedMessagesByThread = null);

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
    Task<bool> CancelQueuedMessageAsync(string threadId, string queuedMessageId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    Task StopResponseAsync(string threadId, CancellationToken cancellationToken = default);
    Task SetThreadSuspendedAsync(string threadId, bool suspended, CancellationToken cancellationToken = default);
    Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken = default);
    Task SetModelAsync(string threadId, string model, CancellationToken cancellationToken = default);
    /// <summary>
    /// Clears the session's explicit model override so it tracks the gateway's
    /// agent/default model again. Providers that don't support clearing leave
    /// the default no-op. Distinct from <see cref="SetModelAsync"/> because the
    /// gateway models this as an explicit null (tri-state), not an empty string.
    /// </summary>
    Task ClearModelAsync(string threadId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetThinkingLevelAsync(string threadId, string thinkingLevel, CancellationToken cancellationToken = default);
    Task SetPermissionModeAsync(string threadId, bool allowAll, CancellationToken cancellationToken = default);
    Task RespondToPermissionAsync(string threadId, string requestId, string action, CancellationToken cancellationToken = default);
    Task RespondToPermissionAsync(string threadId, string requestId, bool allow, CancellationToken cancellationToken = default) =>
        RespondToPermissionAsync(
            threadId,
            requestId,
            allow ? ChatPermissionActionKeys.AllowOnce : ChatPermissionActionKeys.Deny,
            cancellationToken);

    /// <summary>
    /// Requests a refresh of the gateway command catalog surfaced via
    /// <see cref="ChatDataSnapshot.AvailableCommands"/>. Providers that have no
    /// command catalog (e.g. previews/fakes) may treat this as a no-op.
    /// </summary>
    Task EnsureCommandCatalogAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
