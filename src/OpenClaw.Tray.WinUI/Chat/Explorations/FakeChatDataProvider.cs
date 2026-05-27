using OpenClaw.Chat;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Chat.Explorations;

/// <summary>
/// 가짜 IChatDataProvider — ChatExplorationsWindow 의 라이브 프리뷰용. 실제 백엔드 없이
/// user / assistant / tool 데모 메시지를 미리 채워서 mount 하면 바로 버블, 아바타,
/// 툴 카드, 어시스턴트 카드, footer 가 그려진다.
/// SendMessageAsync 는 user 메시지를 추가하고 짧은 fake assistant reply 를 붙인다.
/// </summary>
public sealed class FakeChatDataProvider : IChatDataProvider
{
    private const string ThreadId = "demo-thread";
    private static readonly string[] Models = ["gpt-5.5", "gpt-5.4", "claude-opus-4.7"];

    private ChatTimelineState _timeline;
    private readonly Dictionary<string, ChatEntryMetadata> _metadata = new();
    private int _nextId = 100;

    public string DisplayName => "Demo (preview)";

    public event EventHandler<ChatDataChangedEventArgs>? Changed;
    public event EventHandler<ChatProviderNotificationEventArgs>? NotificationRequested
    {
        add { }
        remove { }
    }

    public FakeChatDataProvider()
    {
        var entries = new List<ChatTimelineItem>
        {
            new("d1", ChatTimelineItemKind.User,      "Hi! Show me how the chat looks with all the toggles applied."),
            new("d2", ChatTimelineItemKind.Assistant, "Hi there! This is an assistant bubble. **Markdown** is supported, and long lines wrap automatically inside the bubble so you can see how the layout breathes."),
            // d3 / d3b / d3c form a 3-entry tool burst to exercise burst grouping
            // (single trailing "Tool · <time>" footer, tight inner margins).
            new("d3", ChatTimelineItemKind.ToolCall,
                Text: "search files",
                ToolName: "FileSearch",
                ToolResult: ChatToolCallStatus.Success,
                ToolOutput: "Found 12 matches in 3 files."),
            new("d3b", ChatTimelineItemKind.ToolCall,
                Text: "read file",
                ToolName: "ReadFile",
                ToolResult: ChatToolCallStatus.Success,
                ToolOutput: "Read 248 lines from src/foo.cs."),
            new("d3c", ChatTimelineItemKind.ToolCall,
                Text: "exec",
                ToolName: "Exec",
                ToolResult: ChatToolCallStatus.InProgress,
                ToolOutput: null),
            new("d3d", ChatTimelineItemKind.Assistant,
                "Looks like 3 files match your query — `Foo.cs`, `Bar.cs`, and `Baz.cs`. Want me to open the first one?"),
            new("d4", ChatTimelineItemKind.User,      "Nice — the tool card looks great."),
            new("d5", ChatTimelineItemKind.Assistant, "Thanks! Toggle bubbles, tool cards, and avatars on and off in the panel to compare side by side."),
            new("d6", ChatTimelineItemKind.Assistant, "This is a second assistant bubble in the same burst — handy for testing avatar alignment and burst spacing."),
        };

        var now = DateTimeOffset.Now;
        _metadata["d1"] = new(now.AddMinutes(-5), Models[0]);
        _metadata["d2"] = new(now.AddMinutes(-4), Models[0], InputTokens: 4200, OutputTokens: 720, ResponseTokens: 15300, ContextPercent: 2);
        _metadata["d3"] = new(now.AddMinutes(-3), Models[0]);
        _metadata["d3b"] = new(now.AddMinutes(-3), Models[0]);
        _metadata["d3c"] = new(now.AddMinutes(-3), Models[0]);
        _metadata["d3d"] = new(now.AddMinutes(-2), Models[0], InputTokens: 5100, OutputTokens: 980, ResponseTokens: 15300, ContextPercent: 2);
        _metadata["d4"] = new(now.AddMinutes(-1), Models[0]);
        _metadata["d5"] = new(now.AddSeconds(-40), Models[0], InputTokens: 5700, OutputTokens: 1150, ResponseTokens: 15300, ContextPercent: 2);
        _metadata["d6"] = new(now.AddSeconds(-30), Models[0], InputTokens: 5700, OutputTokens: 1280, ResponseTokens: 15300, ContextPercent: 2);

        _timeline = new ChatTimelineState(
            Entries: entries.ToImmutableList(),
            TurnActive: false,
            NextId: _nextId,
            ActiveAssistantId: null,
            ActiveReasoningId: null,
            ActiveToolCallId: null,
            CurrentIntent: null,
            LocalNonces: ImmutableHashSet<string>.Empty,
            ActiveToolCalls: ImmutableDictionary<string, string>.Empty,
            HistoryLoaded: true,
            PendingPermission: null);
    }

    private ChatDataSnapshot BuildSnapshot()
    {
        var thread = new ChatThread
        {
            Id = ThreadId,
            Title = "Exploration preview",
            Status = ChatThreadStatus.Running,
            Activity = ChatActivity.Idle,
            Model = Models[0],
            InputTokens = 5700,
            OutputTokens = 1280,
            TotalTokens = 15300,
            ContextTokens = 1_000_000,
            CreatedAt = DateTimeOffset.Now.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.Now,
        };
        var timelines = new Dictionary<string, ChatTimelineState> { [ThreadId] = _timeline };
        return new ChatDataSnapshot(
            Threads: [thread],
            Timelines: timelines,
            DefaultThreadId: ThreadId,
            ConnectionStatus: "connected",
            AvailableModels: Models,
            ComposeTarget: new ChatComposeTarget(ThreadId, true));
    }

    private void RaiseChanged() =>
        Changed?.Invoke(this, new ChatDataChangedEventArgs(BuildSnapshot()));

    public Task<ChatDataSnapshot> LoadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(BuildSnapshot());

    public Task SendMessageAsync(string threadId, string message, CancellationToken cancellationToken = default)
    {
        var userId = $"u{_nextId++}";
        var assistantId = $"a{_nextId++}";
        var entries = new List<ChatTimelineItem>(_timeline.Entries)
        {
            new(userId, ChatTimelineItemKind.User, message),
            new(assistantId, ChatTimelineItemKind.Assistant,
                "Demo response — no real backend connected. Use the panel toggles to compare styling."),
        };
        var now = DateTimeOffset.Now;
        _metadata[userId] = new(now, Models[0]);
        _metadata[assistantId] = new(now.AddSeconds(1), Models[0], InputTokens: 5900, OutputTokens: 1400, ResponseTokens: 15300, ContextPercent: 2);
        _timeline = _timeline with { Entries = entries.ToImmutableList(), NextId = _nextId };
        RaiseChanged();
        return Task.CompletedTask;
    }

    public IReadOnlyDictionary<string, ChatEntryMetadata> GetEntryMetadata(string threadId)
        => threadId == ThreadId
            ? new Dictionary<string, ChatEntryMetadata>(_metadata)
            : new Dictionary<string, ChatEntryMetadata>();

    public Task StopResponseAsync(string threadId, CancellationToken cancellationToken = default)            => Task.CompletedTask;
    public Task SetThreadSuspendedAsync(string threadId, bool suspended, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken = default)             => Task.CompletedTask;
    public Task SetModelAsync(string threadId, string model, CancellationToken cancellationToken = default)   => Task.CompletedTask;
    public Task SetThinkingLevelAsync(string threadId, string thinkingLevel, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetPermissionModeAsync(string threadId, bool allowAll, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RespondToPermissionAsync(string threadId, string requestId, bool allow, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
