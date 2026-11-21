// Read-only body of the conversation panel's File tab, opened from a tool row's file link.
// Usage: Component<ReactorChatWorkspaceFile, ReactorChatWorkspaceFileProps>(new(request, provider.LoadWorkspaceFileAsync));
// The loader performs one bounded sessions.files.get per request or Retry; leaving the tab cancels it.
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ReactorChatWorkspaceFileProps(
    ChatWorkspaceFileRequest Request,
    Func<ChatWorkspaceFileRequest, CancellationToken, Task<ChatWorkspaceFileResult>>? Load);

/// <summary>Shows one workspace file's text, or why it could not be shown, with a manual retry.</summary>
internal sealed class ReactorChatWorkspaceFile : Component<ReactorChatWorkspaceFileProps>
{
    private sealed record Loaded(ChatWorkspaceFileRequest Request, int Attempt, ChatWorkspaceFileResult Result);

    public override Element Render()
    {
        var request = Props.Request;
        var load = Props.Load;
        var (attempt, setAttempt) = UseState(0);
        var (loaded, setLoaded) = UseState<Loaded?>(null, threadSafe: true);
        UseEffect((Func<Action>)(() =>
        {
            if (load is null)
                return static () => { };
            var cancellation = new CancellationTokenSource();
            _ = RunAsync();
            return () => cancellation.Cancel();

            async Task RunAsync()
            {
                ChatWorkspaceFileResult result;
                try
                {
                    result = await load(request, cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    OpenClawTray.Services.Logger.Warn($"[Chat] Workspace file load failed: {ex.GetType().Name}");
                    result = new(ChatWorkspaceFileStatus.Unavailable);
                }
                // Only the request and attempt this effect started may publish its outcome.
                if (!cancellation.IsCancellationRequested)
                    setLoaded(new(request, attempt, result));
            }
        }), request, attempt, (object?)load ?? this);

        var current = loaded is { } value && value.Request == request && value.Attempt == attempt ? value.Result : null;
        var path = NativeToolProjector.SanitizeToolDisplayValue(request.Path);
        Element body = load is null
            ? Note(L("Chat_File_Unsupported"))
            : current is null
                ? Note(L("Chat_File_Loading"))
                : current.Status switch
                {
                    ChatWorkspaceFileStatus.Loaded when string.IsNullOrEmpty(current.Content) => Note(L("Chat_File_Empty")),
                    ChatWorkspaceFileStatus.Loaded => Content(current.Content!),
                    ChatWorkspaceFileStatus.Missing => Note(L("Chat_File_Missing")),
                    ChatWorkspaceFileStatus.NotPreviewable => Note(L("Chat_File_NotPreviewable")),
                    ChatWorkspaceFileStatus.Unsupported => Note(L("Chat_File_Unsupported")),
                    _ => VStack(8,
                        Note(L(current.Status == ChatWorkspaceFileStatus.Stale ? "Chat_File_Stale" : "Chat_File_Unavailable")),
                        Button(L("Chat_File_Retry"), () => setAttempt(attempt + 1))
                            .AutomationName(L("Chat_File_Retry"))
                            .AutomationId("ChatWorkspaceFileRetry")
                            .HAlign(HorizontalAlignment.Left)
                            .Margin(16, 0)),
                };
        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Star()],
                TextBlock(path).FontSize(12).TextWrapping(TextWrapping.Wrap).IsTextSelectionEnabled(true)
                    .Set(ChatVisuals.StyleSecondaryText).Margin(16, 12, 16, 8)
                    .AutomationId("ChatWorkspaceFilePath").Grid(row: 0),
                body.Grid(row: 1))
            .AutomationId("ChatWorkspaceFilePane")
            .AutomationName(path);
    }

    private static Element Note(string text) =>
        TextBlock(text).TextWrapping(TextWrapping.Wrap).Margin(16, 0, 16, 8)
            .AutomationId("ChatWorkspaceFileStatus");

    private static Element Content(string text) =>
        ScrollViewer(TextBlock(text)
                .FontFamily(ChatVisuals.CodeFontFamily)
                .FontSize(12)
                .IsTextSelectionEnabled(true)
                .TextWrapping(TextWrapping.NoWrap)
                .Margin(16, 0, 16, 16))
            .Set(scroll =>
            {
                scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                scroll.HorizontalScrollMode = ScrollMode.Auto;
            })
            .AutomationId("ChatWorkspaceFileContent");

    private static string L(string key) => LocalizationHelper.GetString(key);
}
