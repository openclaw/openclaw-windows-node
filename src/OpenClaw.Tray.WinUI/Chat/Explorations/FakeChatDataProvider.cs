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
    private int _nextId = 100;

    public string DisplayName => "Demo (preview)";

    public event EventHandler<ChatDataChangedEventArgs>? Changed;
    public event EventHandler<ChatProviderNotificationEventArgs>? NotificationRequested;

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
        var entries = new List<ChatTimelineItem>(_timeline.Entries)
        {
            new($"u{_nextId++}", ChatTimelineItemKind.User, message),
            new($"a{_nextId++}", ChatTimelineItemKind.Assistant,
                "Demo response — no real backend connected. Use the panel toggles to compare styling."),
        };
        _timeline = _timeline with { Entries = entries.ToImmutableList(), NextId = _nextId };
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task StopResponseAsync(string threadId, CancellationToken cancellationToken = default)            => Task.CompletedTask;
    public Task SetThreadSuspendedAsync(string threadId, bool suspended, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken = default)             => Task.CompletedTask;
    public Task SetModelAsync(string threadId, string model, CancellationToken cancellationToken = default)   => Task.CompletedTask;
    public Task SetThinkingLevelAsync(string threadId, string thinkingLevel, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetPermissionModeAsync(string threadId, bool allowAll, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RespondToPermissionAsync(string threadId, string requestId, bool allow, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
