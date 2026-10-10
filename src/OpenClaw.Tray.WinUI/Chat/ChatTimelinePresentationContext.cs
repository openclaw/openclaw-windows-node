using OpenClaw.Chat;
using OpenClaw.Shared;

namespace OpenClawTray.Chat;


/// <summary>
/// Presentation inputs shared by the Reactor chat timeline and its focused card renderers.
/// </summary>
public sealed record ChatTimelinePresentationContext(
    string? SessionId,
    IReadOnlyList<ChatTimelineItem> Entries,
    bool HasMoreHistory,
    Action? OnLoadMoreHistory,
    IReadOnlyDictionary<string, ChatEntryMetadata>? EntryMetadata = null,
    long TimelineGeneration = 0,
    string UserSenderLabel = "OpenClaw Windows Tray",
    string AssistantSenderLabel = "Field",
    string? DefaultModel = null,
    string? DefaultUsageSummary = null,
    bool ShowThinkingIndicator = false,
    bool ShowToolCalls = true,
    int ToolCallsCollapseVersion = 0,
    Func<string, Task>? OnReadAloud = null,
    Action? OnStopSpeaking = null,
    int ScrollToBottomToken = 0,
    Action<string, string>? OnPermissionResponse = null,
    Func<string, ChatMediaContentInfo, CancellationToken, Task<AssistantMediaResolutionResult>>?
        ResolveAssistantMediaAsync = null,
    IReadOnlyList<ChatQueuedMessage>? QueuedMessages = null,
    Action<string>? OnCancelQueuedMessage = null,
    ChatLoadOlderState LoadOlderState = ChatLoadOlderState.Unavailable,
    Action? OnRetryLoadOlder = null,
    bool CanShowNewerHistory = false,
    Action? OnShowNewerHistory = null,
    string? LatestUsageEntryId = null,
    ChatToolActivityPresentation.ChatToolActivityWindowContext? ToolActivityWindow = null,
    bool ToolActivityPending = false,
    // True while a DEEP anchor resolution is in flight: the consumer shows a loading/holding state and must not
    // present the visible slice as a resolved window.
    bool WindowPending = false);
