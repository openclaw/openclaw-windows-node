using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
using OpenClaw.Chat;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.FunctionalUI.Hosting;
using static OpenClawTray.FunctionalUI.Factories;

namespace OpenClawTray.Chat;

public static class FunctionalChatHostExtensions
{
    public static IDisposable MountFunctionalChat(
        this Window window,
        Border target,
        IChatDataProvider provider,
        string? initialThreadId = null,
        Func<string, Task>? onReadAloud = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(provider);

        var host = new FunctionalHostControl();
        target.Child = host;
        host.Mount(new FunctionalChatRoot(provider, initialThreadId, onReadAloud));
        return new MountedFunctionalChat(target, host);
    }

    private sealed class MountedFunctionalChat(Border target, FunctionalHostControl host) : IDisposable
    {
        public void Dispose()
        {
            host.Dispose();
            if (ReferenceEquals(target.Child, host))
                target.Child = null;
        }
    }
}

public sealed class FunctionalChatRoot : Component
{
    private readonly IChatDataProvider _provider;
    private readonly string? _initialThreadId;
    private readonly Func<string, Task>? _onReadAloud;

    public FunctionalChatRoot(
        IChatDataProvider provider,
        string? initialThreadId = null,
        Func<string, Task>? onReadAloud = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _initialThreadId = initialThreadId;
        _onReadAloud = onReadAloud;
    }

    public override Element Render()
    {
        var (snapshot, setSnapshot) = UseState<ChatDataSnapshot?>(null, threadSafe: true);
        var (selectedThreadId, setSelectedThreadId) = UseState<string?>(_initialThreadId, threadSafe: true);
        var (draft, setDraft) = UseState(string.Empty);
        var (error, setError) = UseState(string.Empty, threadSafe: true);

        UseEffect((Func<Action>)(() =>
        {
            EventHandler<ChatDataChangedEventArgs> changed = (_, args) =>
            {
                setSnapshot(args.Snapshot);
            };

            _provider.Changed += changed;
            _ = LoadSnapshotAsync(_provider, setSnapshot, setSelectedThreadId, setError);
            return () => _provider.Changed -= changed;
        }));

        if (snapshot is null)
        {
            return Border(
                VStack(8,
                    ProgressRing().Width(28).Height(28).HAlign(HorizontalAlignment.Center),
                    TextBlock("Connecting to gateway chat...").ForegroundResource("TextFillColorSecondaryBrush")
                        .HAlign(HorizontalAlignment.Center)
                ).VAlign(VerticalAlignment.Center).HAlign(HorizontalAlignment.Center)
            ).BackgroundResource("SolidBackgroundFillColorBaseBrush");
        }

        var activeThreadId = ResolveThreadId(snapshot, selectedThreadId);
        var activeThread = Array.Find(snapshot.Threads, t => t.Id == activeThreadId);
        var timeline = activeThreadId is not null && snapshot.Timelines.TryGetValue(activeThreadId, out var state)
            ? state
            : ChatTimelineState.Initial();

        return Border(
            Grid(
                ["*"],
                ["Auto", "*", "Auto", "Auto"],
                BuildHeader(snapshot, activeThread, activeThreadId, setSelectedThreadId).Grid(0),
                BuildTimeline(timeline, activeThread).Grid(1),
                string.IsNullOrWhiteSpace(error)
                    ? null
                    : Border(TextBlock(error).TextWrapping().ForegroundResource("SystemFillColorCriticalBrush"))
                        .Padding(12)
                        .BackgroundResource("SystemFillColorCriticalBackgroundBrush")
                        .Grid(2),
                BuildComposer(snapshot, activeThread, activeThreadId, timeline, draft, setDraft, setError).Grid(3)
            )
        ).BackgroundResource("SolidBackgroundFillColorBaseBrush");
    }

    private static async Task LoadSnapshotAsync(
        IChatDataProvider provider,
        Action<ChatDataSnapshot?> setSnapshot,
        Action<string?> setSelectedThreadId,
        Action<string> setError)
    {
        try
        {
            var loaded = await provider.LoadAsync();
            setSnapshot(loaded);
            setSelectedThreadId(loaded.DefaultThreadId);
        }
        catch (Exception ex)
        {
            setError($"Unable to load chat: {ex.Message}");
        }
    }

    private static string? ResolveThreadId(ChatDataSnapshot snapshot, string? selectedThreadId)
    {
        if (!string.IsNullOrWhiteSpace(selectedThreadId) &&
            Array.Exists(snapshot.Threads, t => t.Id == selectedThreadId))
        {
            return selectedThreadId;
        }

        return snapshot.DefaultThreadId
               ?? (snapshot.Threads.Length > 0 ? snapshot.Threads[0].Id : null);
    }

    private Element BuildHeader(
        ChatDataSnapshot snapshot,
        ChatThread? activeThread,
        string? activeThreadId,
        Action<string?> setSelectedThreadId)
    {
        var threadLabels = snapshot.Threads.Length == 0
            ? new[] { "Main session" }
            : snapshot.Threads.Select(t => t.DisplayTitle).ToArray();
        var threadIds = snapshot.Threads.Select(t => t.Id).ToArray();
        var selectedIndex = activeThreadId is null
            ? -1
            : Array.IndexOf(threadIds, activeThreadId);

        return Border(
            HStack(12,
                VStack(2,
                    TextBlock(activeThread?.DisplayTitle ?? "OpenClaw Chat")
                        .FontSize(18)
                        .FontWeight(FontWeights.SemiBold),
                    TextBlock(snapshot.ConnectionStatus ?? "Disconnected")
                        .ForegroundResource("TextFillColorSecondaryBrush")
                ).Grid(column: 0),
                snapshot.Threads.Length <= 1
                    ? null
                    : ComboBox(threadLabels, selectedIndex, idx =>
                    {
                        if (idx >= 0 && idx < threadIds.Length)
                            setSelectedThreadId(threadIds[idx]);
                    }).MinWidth(180).HAlign(HorizontalAlignment.Right)
            )
        ).Padding(16, 12, 16, 8)
         .BorderBrushResource("CardStrokeColorDefaultBrush")
         .BorderThickness(0, 0, 0, 1);
    }

    private Element BuildTimeline(ChatTimelineState timeline, ChatThread? thread)
    {
        var entries = timeline.Entries.Count == 0
            ? new[]
            {
                Border(
                    VStack(8,
                        TextBlock("No messages yet").FontSize(16).FontWeight(FontWeights.SemiBold)
                            .HAlign(HorizontalAlignment.Center),
                        TextBlock("Send a message below to start chatting with the gateway.")
                            .ForegroundResource("TextFillColorSecondaryBrush")
                            .HAlign(HorizontalAlignment.Center)
                    ).HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center)
                ).Key("empty").Padding(24)
            }
            : timeline.Entries.Select(entry => BuildTimelineEntry(entry, thread)).ToArray();

        return ScrollView(
            VStack(10, entries)
                .Padding(16, 12, 16, 12)
        ).HorizontalScrollMode(ScrollMode.Disabled);
    }

    private Element BuildTimelineEntry(ChatTimelineItem entry, ChatThread? thread)
    {
        var isUser = entry.Kind == ChatTimelineItemKind.User;
        var title = entry.Kind switch
        {
            ChatTimelineItemKind.User => "You",
            ChatTimelineItemKind.Assistant => thread?.Title ?? "Assistant",
            ChatTimelineItemKind.ToolCall => string.IsNullOrWhiteSpace(entry.ToolName) ? "Tool call" : $"Tool call: {entry.ToolName}",
            ChatTimelineItemKind.Reasoning => "Reasoning",
            ChatTimelineItemKind.Status => "Status",
            _ => entry.Kind.ToString()
        };

        var text = ChatMarkdownSanitizer.Sanitize(entry.Text);
        if (entry.IsStreaming)
            text += "  ...";

        var body = VStack(6,
            TextBlock(title).FontWeight(FontWeights.SemiBold)
                .ForegroundResource(isUser ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush"),
            TextBlock(text).TextWrapping()
                .ForegroundResource(isUser ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush"),
            string.IsNullOrWhiteSpace(entry.ToolOutput)
                ? null
                : Border(TextBlock(entry.ToolOutput).TextWrapping().FontFamily("Cascadia Code, Consolas"))
                    .Padding(8)
                    .BackgroundResource("LayerFillColorAltBrush")
                    .CornerRadius(6)
        );

        return Border(body)
            .Key(entry.Id)
            .Padding(12)
            .CornerRadius(10)
            .BackgroundResource(isUser ? "AccentFillColorDefaultBrush" : "CardBackgroundFillColorDefaultBrush")
            .BorderBrushResource("CardStrokeColorDefaultBrush")
            .BorderThickness(1)
            .HAlign(isUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch)
            .MaxWidth(860);
    }

    private Element BuildComposer(
        ChatDataSnapshot snapshot,
        ChatThread? activeThread,
        string? activeThreadId,
        ChatTimelineState timeline,
        string draft,
        Action<string> setDraft,
        Action<string> setError)
    {
        var models = snapshot.AvailableModels;
        var selectedModelIndex = activeThread?.Model is { Length: > 0 } model
            ? Array.IndexOf(models, model)
            : -1;
        var modelCombo = models.Length == 0
            ? null
            : ComboBox(models, selectedModelIndex, idx =>
            {
                if (activeThreadId is null || idx < 0 || idx >= models.Length) return;
                _ = RunProviderOperationAsync(
                    () => _provider.SetModelAsync(activeThreadId, models[idx]),
                    setError);
            }, "Model").MinWidth(180);

        var canSend = activeThreadId is not null && !string.IsNullOrWhiteSpace(draft) && !timeline.TurnActive;
        var sendButton = timeline.TurnActive
            ? Button("Stop", () =>
            {
                if (activeThreadId is null) return;
                _ = RunProviderOperationAsync(() => _provider.StopResponseAsync(activeThreadId), setError);
            })
            : Button("Send", () =>
            {
                if (!canSend || activeThreadId is null) return;
                var message = draft;
                setDraft(string.Empty);
                _ = RunProviderOperationAsync(() => _provider.SendMessageAsync(activeThreadId, message), setError);
            }).Disabled(!canSend);

        return Border(
            VStack(8,
                modelCombo,
                HStack(8,
                    TextField(draft, setDraft, placeholder: timeline.TurnActive ? "Waiting for response..." : "Message Assistant")
                        .TextWrapping()
                        .MinHeight(48)
                        .Grid(column: 0),
                    sendButton.MinWidth(88).VAlign(VerticalAlignment.Bottom)
                )
            )
        ).Padding(16)
         .BorderBrushResource("CardStrokeColorDefaultBrush")
         .BorderThickness(0, 1, 0, 0)
         .BackgroundResource("SolidBackgroundFillColorBaseBrush");
    }

    private static async Task RunProviderOperationAsync(Func<Task> operation, Action<string> setError)
    {
        try
        {
            setError(string.Empty);
            await operation();
        }
        catch (Exception ex)
        {
            setError(ex.Message);
        }
    }
}
