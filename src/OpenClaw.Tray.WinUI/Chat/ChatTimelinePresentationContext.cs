using OpenClaw.Chat;

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
    Action<string, string>? OnPermissionResponse = null);
