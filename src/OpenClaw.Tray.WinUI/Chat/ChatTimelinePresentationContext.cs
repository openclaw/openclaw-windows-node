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
    Func<Task>? OnLoadMoreHistory,
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
    ChatRunProgressPresentation? RunProgress = null,
    Func<string, ChatMediaContentInfo, CancellationToken, Task<AssistantFileDownloadStatus>>?
        DownloadAssistantFileAsync = null,
    ChatThread? Session = null,
    bool ShowReasoning = false,
    bool ShowReasoningPreview = false,
    ChatReasoningStream? ReasoningStream = null,
    long HistoryRevision = 0,
    long? ToolOutputConnectionEpoch = null,
    Func<ChatToolOutputRequest, CancellationToken, Task<ChatToolOutputResult>>? LoadToolOutputAsync = null,
    ChatToolDetailsProfileState ToolDetailsProfileState = default,
    Func<ChatToolDetailsProfileState>? ReadToolDetailsProfileState = null,
    IReadOnlyList<ChatThread>? ChildThreads = null,
    SessionProviderReviewInfo? ProviderReview = null,
    bool LoadingOlderHistory = false,
    ChatToolActivityExpansionState? ToolExpansionState = null,
    string UserDisplayName = "",
    Microsoft.UI.Xaml.Media.ImageSource? UserProfilePicture = null);
