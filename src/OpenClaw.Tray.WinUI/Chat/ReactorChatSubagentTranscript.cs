// Reuses the provider's keyed history and the native timeline without a second composer.
// Mount only for an authorized current child selected in ReactorChatSubagents.
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using OpenClaw.Chat;
using OpenClawTray.Services;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

internal sealed record ReactorChatSubagentTranscriptProps(
    ChatThread Thread, ChatDataSnapshot Snapshot, OpenClawChatDataProvider? Provider,
    bool ShowToolCalls, bool ShowThinking, int CollapseVersion, Func<string, bool>? TryCopyText);

/// <summary>Read-only child history. It never binds primary selection or sends chat/approval actions.</summary>
internal sealed class ReactorChatSubagentTranscript : Component<ReactorChatSubagentTranscriptProps>
{
    public override Element Render()
    {
        var props = Props;
        var id = props.Thread.Id;
        var provider = props.Provider;
        var snapshot = props.Snapshot;
        var timeline = snapshot.Timelines.GetValueOrDefault(id) ?? ChatTimelineState.Initial();
        var generation = snapshot.TimelineGenerations?.GetValueOrDefault(id) ?? 0;
        var revision = snapshot.HistoryRevisions?.GetValueOrDefault(id) ?? 0;
        var (failed, setFailed) = UseState(false, threadSafe: true);
        var (retry, setRetry) = UseState(0);
        UseEffect((Func<Action>)(() =>
        {
            setFailed(false);
            var cancellation = new CancellationTokenSource();
            if (provider is not null)
                _ = LoadAsync();
            return () => { cancellation.Cancel(); cancellation.Dispose(); };

            async Task LoadAsync()
            {
                var token = cancellation.Token;
                try
                {
                    if (retry > 0)
                        await provider.RetryHistoryAsync(id, token);
                    else
                        await provider.LoadHistoryAsync(id, cancellationToken: token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    Logger.Warn($"[ChatSubagents] History read failed: {ex.GetType().Name}");
                    if (!token.IsCancellationRequested) setFailed(true);
                }
            }
        }), (object?)provider ?? this, id, generation, retry);

        var historyFailed = failed || snapshot.FailedHistoryThreads?.Contains(id) == true
            || (provider is null && !timeline.HistoryLoaded);
        var mode = historyFailed && timeline.Entries.Count == 0
            ? ReactorChatTimelineMode.HistoryUnavailable
            : !timeline.HistoryLoaded && timeline.Entries.Count == 0
                ? ReactorChatTimelineMode.Loading : ReactorChatTimelineMode.Timeline;
        if (!historyFailed && timeline.HistoryLoaded && timeline.Entries.Count == 0 && !timeline.TurnActive)
            return TextBlock(ReactorQuestionPanel.L("Chat_Subagents_NoMessages", "No messages yet."))
                .TextWrapping(TextWrapping.Wrap).Padding(ChatVisuals.DetailHorizontalInset);
        var context = new ChatTimelinePresentationContext(
            id, timeline.Entries, snapshot.OlderHistoryThreads?.Contains(id) == true,
            provider is null ? null : () => provider.LoadOlderHistoryAsync(id),
            EntryMetadata: provider?.GetEntryMetadata(id),
            TimelineGeneration: generation,
            AssistantSenderLabel: ReactorQuestionPanel.L("Chat_Subagents_Title", "Subagents"),
            DefaultModel: props.Thread.Model,
            ShowToolCalls: props.ShowToolCalls,
            ToolCallsCollapseVersion: props.CollapseVersion,
            ShowThinkingIndicator: timeline.TurnActive,
            RunProgress: ChatRunProgressProjection.Project(timeline,
                awaitingSubagents: props.Thread.WaitingForSubagents),
            Session: props.Thread,
            ShowReasoning: props.ShowThinking &&
                (props.Thread.ReasoningLevel ?? props.Thread.ReasoningDefault) == "on",
            ShowReasoningPreview: props.ShowThinking &&
                (props.Thread.ReasoningLevel ?? props.Thread.ReasoningDefault) is "on" or "stream",
            ReasoningStream: props.ShowThinking && timeline.TurnActive &&
                (timeline.RunUsage?.RunId is null || timeline.RunUsage.RunId == timeline.ReasoningStream?.RunId)
                    ? timeline.ReasoningStream : null,
            HistoryRevision: revision,
            ResolveAssistantMediaAsync: provider is null ? null : provider.ResolveAssistantMediaAsync,
            DownloadAssistantFileAsync: provider is null ? null : provider.DownloadAssistantFileAsync,
            ToolOutputConnectionEpoch: provider?.ToolOutputConnectionEpoch,
            LoadToolOutputAsync: provider is null ? null : provider.LoadToolOutputAsync,
            ToolDetailsProfileState: provider?.ToolDetailsProfileState ?? default,
            ReadToolDetailsProfileState: provider is null ? null : provider.ReadToolDetailsProfileState,
            ProviderReview: props.Thread.ProviderReview);
        return Component<ReactorChatTimeline, ReactorChatTimelineProps>(new(
            mode, context, HistoryRevision: revision, TryCopyText: props.TryCopyText,
            HistoryFailed: historyFailed, OnRetryHistory: provider is null ? null : () => setRetry(retry + 1),
            AssistantAgentId: props.Thread.AgentId));
    }
}
