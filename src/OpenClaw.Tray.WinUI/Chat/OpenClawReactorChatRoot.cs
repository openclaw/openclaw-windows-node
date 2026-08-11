using Microsoft.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

public sealed record OpenClawReactorChatRootProps(
    IChatDataProvider Provider,
    ReactorChatHostCallbacks HostCallbacks,
    string? InitialThreadId = null,
    Func<string, Task>? OnReadAloud = null,
    Action? OnStopSpeaking = null,
    Func<CancellationToken, Action?, Task<string?>>? OnVoiceRequest = null,
    Action? OnAttachClick = null,
    Action? OnSettingsClick = null,
    Action<string>? OnOpenCheckpoints = null,
    Action<bool>? OnSpeakerMuteChanged = null,
    Func<string, string?, Task<bool>>? ConfirmResetAsync = null,
    bool InitialMuted = false,
    bool IsCompact = false);

/// <summary>
/// Production Reactor root for the native chat surface. It owns the provider
/// subscription and renders the message timeline and composer in one tree.
/// </summary>
public sealed class OpenClawReactorChatRoot : Component<OpenClawReactorChatRootProps>
{
    private static bool s_showToolCalls = true;
    private static int s_toolCallsCollapseVersion;
    private static event EventHandler? ToolCallsVisibilityChanged;

    private string? _pendingSelectedThreadId;

    public static void SetToolCallsVisible(bool visible)
    {
        if (s_showToolCalls == visible)
            return;

        if (!visible && s_showToolCalls)
            s_toolCallsCollapseVersion++;

        s_showToolCalls = visible;
        ToolCallsVisibilityChanged?.Invoke(null, EventArgs.Empty);
    }

    public override Element Render()
    {
        var props = Props;
        var (snapshot, setSnapshot) = UseState<ChatDataSnapshot?>(null, threadSafe: true);
        var initialSelection = props.InitialThreadId
            ?? (props.Provider as OpenClawChatDataProvider)?.CachedLastChatState?.DefaultThreadId;
        var (selectedId, setSelectedId) = UseState<string?>(initialSelection, threadSafe: true);
        var selectedIdRef = UseRef<string?>(initialSelection);
        selectedIdRef.Current = selectedId;
        var (pendingAttachments, setPendingAttachments) =
            UseState<IReadOnlyList<ChatAttachment>>(Array.Empty<ChatAttachment>(), threadSafe: true);
        var pendingAttachmentsRef = UseRef<IReadOnlyList<ChatAttachment>>(pendingAttachments);
        pendingAttachmentsRef.Current = pendingAttachments;
        var (speakerMuted, setSpeakerMuted) = UseState(props.InitialMuted, threadSafe: true);
        var (voiceTranscript, setVoiceTranscript) = UseState<string?>(null, threadSafe: true);
        var (voiceAudioLevel, setVoiceAudioLevel) = UseState(0f, threadSafe: true);
        var (scrollToBottomToken, setScrollToBottomToken) = UseState(0, threadSafe: true);
        var (showToolCalls, setShowToolCalls) = UseState(s_showToolCalls, threadSafe: true);
        var (toolCallsCollapseVersion, setToolCallsCollapseVersion) =
            UseState(s_toolCallsCollapseVersion, threadSafe: true);
        var (firstSendInFlight, setFirstSendInFlight) = UseState(false, threadSafe: true);

        void UpdatePendingAttachments(IReadOnlyList<ChatAttachment> attachments)
        {
            pendingAttachmentsRef.Current = attachments;
            setPendingAttachments(attachments);
        }

        props.HostCallbacks.AttachFiles = attachments =>
        {
            if (attachments.Count > 0)
                UpdatePendingAttachments(pendingAttachmentsRef.Current.Concat(attachments).ToArray());
        };
        props.HostCallbacks.SetVoiceTranscript = setVoiceTranscript;
        props.HostCallbacks.SetVoiceAudioLevel = setVoiceAudioLevel;
        props.HostCallbacks.SetSpeakerMuted = setSpeakerMuted;

        UseEffect((Func<Action>)(() => () => props.HostCallbacks.Clear()), props.HostCallbacks);

        UseEffect((Func<Action>)(() =>
        {
            EventHandler visibilityChanged = (_, _) =>
            {
                setShowToolCalls(s_showToolCalls);
                setToolCallsCollapseVersion(s_toolCallsCollapseVersion);
            };
            ToolCallsVisibilityChanged += visibilityChanged;
            return () => ToolCallsVisibilityChanged -= visibilityChanged;
        }), Array.Empty<object>());

        UseEffect((Func<Action>)(() =>
        {
            var provider = props.Provider;
            EventHandler<ChatDataChangedEventArgs> onChanged = (_, args) =>
            {
                setSnapshot(args.Snapshot);
                if (args.Snapshot.ComposeTarget.SessionKey is { } composeKey
                    && args.Snapshot.Timelines.TryGetValue(composeKey, out var timeline)
                    && timeline.Entries.Any(entry => entry.Kind == ChatTimelineItemKind.User))
                {
                    setFirstSendInFlight(false);
                }

                if (selectedIdRef.Current is null && args.Snapshot.DefaultThreadId is { } defaultThreadId)
                {
                    selectedIdRef.Current = defaultThreadId;
                    setSelectedId(defaultThreadId);
                }
            };

            provider.Changed += onChanged;
            _ = LoadAsync(
                provider,
                setSnapshot,
                () => selectedIdRef.Current,
                next =>
                {
                    selectedIdRef.Current = next;
                    setSelectedId(next);
                });
            return () => provider.Changed -= onChanged;
        }), props.Provider);

        if (snapshot is null)
            return RenderLoading();

        var selectedMaterializedThread = selectedId is null
            ? null
            : snapshot.Threads.FirstOrDefault(thread => string.Equals(thread.Id, selectedId, StringComparison.Ordinal));
        if (selectedMaterializedThread is null
            && selectedId is not null
            && snapshot.DefaultThreadId is { } fallbackId
            && ChatLifecycleSelectionPolicy.ShouldFallback(
                selectedId,
                _pendingSelectedThreadId,
                fallbackId))
        {
            selectedIdRef.Current = fallbackId;
            setSelectedId(fallbackId);
            selectedMaterializedThread = snapshot.Threads.FirstOrDefault(thread =>
                string.Equals(thread.Id, fallbackId, StringComparison.Ordinal));
        }

        var effectiveThread = selectedMaterializedThread ?? CreateComposeOnlyThread(props.Provider, snapshot);
        if (effectiveThread is { } selected && string.Equals(_pendingSelectedThreadId, selected.Id, StringComparison.Ordinal))
            _pendingSelectedThreadId = null;

        var connectionState = ToConnectionState(snapshot.ConnectionStatus);
        var isGatewayConnected = string.Equals(connectionState, "connected", StringComparison.Ordinal);
        if (isGatewayConnected
            && selectedMaterializedThread is not null
            && props.Provider is OpenClawChatDataProvider nativeProvider)
        {
            RunFireAndForget(ct => nativeProvider.LoadHistoryAsync(selectedMaterializedThread.Id, force: false, ct));
        }

        var timeline = effectiveThread is not null
            && snapshot.Timelines.TryGetValue(effectiveThread.Id, out var currentTimeline)
            ? currentTimeline
            : ChatTimelineState.Initial();
        var timelineGeneration = effectiveThread is not null
            && snapshot.TimelineGenerations?.TryGetValue(effectiveThread.Id, out var generation) == true
                ? generation
                : 0L;
        var historyRevision = effectiveThread is not null
            && snapshot.HistoryRevisions?.TryGetValue(effectiveThread.Id, out var revision) == true
                ? revision
                : 0L;
        var entryMetadata = effectiveThread is not null && props.Provider is OpenClawChatDataProvider metadataProvider
            ? metadataProvider.GetEntryMetadata(effectiveThread.Id)
            : null;
        var entries = (IReadOnlyList<ChatTimelineItem>)timeline.Entries;
        var queuedMessages = effectiveThread is not null
            && snapshot.QueuedMessagesByThread?.TryGetValue(effectiveThread.Id, out var queued) == true
                ? queued
                : Array.Empty<ChatQueuedMessage>();
        var hasPendingQueuedSend = queuedMessages.Any(message =>
            message.SendState is ChatQueuedMessageSendState.Queued or ChatQueuedMessageSendState.Sending);
        var currentTurnHasAssistant = false;
        for (var index = timeline.Entries.Count - 1; index >= 0; index--)
        {
            if (timeline.Entries[index].Kind == ChatTimelineItemKind.User)
                break;
            if (timeline.Entries[index].Kind == ChatTimelineItemKind.Assistant)
            {
                currentTurnHasAssistant = true;
                break;
            }
        }

        var showThinking = timeline.TurnActive && !currentTurnHasAssistant;
        var isEmptyConversation = entries.Count == 0 && !showThinking && timeline.PendingPermission is null;
        var isComposeOnly = effectiveThread is not null && selectedMaterializedThread is null;
        var hasRealThreads = snapshot.Threads.Length > 0;
        var welcomeEligible = isEmptyConversation
            && isGatewayConnected
            && (
                (isComposeOnly && !hasRealThreads)
                || (!isComposeOnly && timeline.HistoryLoaded));
        var welcomeEligibilityKey = welcomeEligible
            ? $"{effectiveThread?.Id}|{isComposeOnly}|{timeline.HistoryLoaded}|{hasRealThreads}"
            : null;
        var welcomeEligibilityKeyRef = UseRef<string?>(welcomeEligibilityKey);
        welcomeEligibilityKeyRef.Current = welcomeEligibilityKey;
        var (settledWelcomeKey, setSettledWelcomeKey) = UseState<string?>(null, threadSafe: true);
        UseEffect((Func<Action>)(() =>
        {
            if (welcomeEligibilityKey is null)
            {
                setSettledWelcomeKey(null);
                return static () => { };
            }

            var cancelled = false;
            var expectedKey = welcomeEligibilityKey;
            _ = Task.Run(async () =>
            {
                await Task.Delay(800);
                if (!cancelled
                    && string.Equals(
                        welcomeEligibilityKeyRef.Current,
                        expectedKey,
                        StringComparison.Ordinal))
                {
                    setSettledWelcomeKey(expectedKey);
                }
            });
            return () => cancelled = true;
        }),
            welcomeEligibilityKey);

        var emptyConversationIsAuthoritative = welcomeEligibilityKey is not null
            && string.Equals(
                settledWelcomeKey,
                welcomeEligibilityKey,
                StringComparison.Ordinal);
        var mode = effectiveThread is null
                   || (isEmptyConversation && !emptyConversationIsAuthoritative)
            ? ReactorChatTimelineMode.Loading
            : isEmptyConversation
                ? ReactorChatTimelineMode.Empty
                : ReactorChatTimelineMode.Timeline;

        var timelineProps = new OpenClawChatTimelineProps(
            effectiveThread?.Id,
            entries,
            false,
            null,
            entryMetadata,
            timelineGeneration,
            "OpenClaw Windows Tray",
            "Assistant",
            effectiveThread?.Model,
            showToolCalls
                ? ChatUsageFormatter.Format(entries, entryMetadata) ?? ChatUsageFormatter.Format(effectiveThread)
                : null,
            showThinking,
            showToolCalls,
            toolCallsCollapseVersion,
            props.OnReadAloud,
            props.OnStopSpeaking,
            scrollToBottomToken,
            effectiveThread is { } permissionThread
                ? (requestId, action) => OnPermission(permissionThread.Id, requestId, action)
                : null);

        void SelectThread(string threadId)
        {
            _pendingSelectedThreadId = threadId;
            selectedIdRef.Current = threadId;
            setSelectedId(threadId);
            if (props.Provider is OpenClawChatDataProvider native)
                native.RememberSelectedThread(threadId);
        }

        Action<string>? onSuggestionPicked = null;
        if (mode == ReactorChatTimelineMode.Empty && effectiveThread is { } suggestionThread)
        {
            onSuggestionPicked = suggestion =>
            {
                if (firstSendInFlight)
                    return;

                setFirstSendInFlight(true);
                ObserveFireAndForget(SendAsync(
                    suggestionThread.Id,
                    suggestionThread.Title,
                    suggestion,
                    Array.Empty<ChatAttachment>(),
                    setScrollToBottomToken,
                    scrollToBottomToken,
                    SelectThread));
            };
        }

        var timelineElement = Component<ReactorChatTimeline, ReactorChatTimelineProps>(new(
            mode,
            timelineProps,
            onSuggestionPicked,
            firstSendInFlight,
            OnOpenCheckpoints: props.OnOpenCheckpoints,
            HistoryRevision: historyRevision));
        var composerElement = effectiveThread is null
            ? Empty()
            : Component<ReactorChatComposer, ReactorChatComposerProps>(new(
                connectionState,
                timeline.TurnActive,
                effectiveThread,
                VisibleChannels(snapshot.Threads, effectiveThread),
                snapshot.AvailableModels,
                snapshot.ModelChoices,
                timeline.TurnActive || hasPendingQueuedSend,
                pendingAttachments,
                queuedMessages,
                async (message, attachments) =>
                {
                    var accepted = await SendAsync(
                        effectiveThread.Id,
                        effectiveThread.Title,
                        message,
                        attachments,
                        setScrollToBottomToken,
                        scrollToBottomToken,
                        SelectThread);
                    if (accepted)
                        UpdatePendingAttachments(RemoveSubmittedAttachments(pendingAttachmentsRef.Current, attachments));
                    return accepted;
                },
                () => OnStop(effectiveThread.Id),
                SelectThread,
                model => ObserveFireAndForget(props.Provider.SetModelAsync(effectiveThread.Id, model)),
                () => ObserveFireAndForget(props.Provider.ClearModelAsync(effectiveThread.Id)),
                level => RunFireAndForget(ct => props.Provider.SetThinkingLevelAsync(effectiveThread.Id, level, ct)),
                allowAll => RunFireAndForget(ct => props.Provider.SetPermissionModeAsync(effectiveThread.Id, allowAll, ct)),
                props.OnVoiceRequest,
                props.OnAttachClick,
                speakerMuted,
                () =>
                {
                    var next = !speakerMuted;
                    setSpeakerMuted(next);
                    props.OnSpeakerMuteChanged?.Invoke(next);
                },
                props.OnSettingsClick,
                voiceTranscript,
                voiceAudioLevel,
                starter => props.HostCallbacks.TriggerVoiceRecording = starter,
                attachment => UpdatePendingAttachments(pendingAttachmentsRef.Current.Concat(new[] { attachment }).ToArray()),
                attachment => UpdatePendingAttachments(RemoveAttachment(pendingAttachmentsRef.Current, attachment)),
                queuedMessageId => RunFireAndForget(ct => props.Provider.CancelQueuedMessageAsync(effectiveThread.Id, queuedMessageId, ct)),
                snapshot.AvailableCommands,
                snapshot.CommandsSupported,
                () => RunFireAndForget(ct => props.Provider.EnsureCommandCatalogAsync(ct)),
                props.IsCompact));

        return Grid(
            [GridSize.Star()],
            [GridSize.Star(), GridSize.Auto],
            timelineElement.Grid(row: 0),
            composerElement.Grid(row: 1))
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch);
    }

    private static Element RenderLoading() =>
        Component<ReactorChatTimeline, ReactorChatTimelineProps>(new(
            ReactorChatTimelineMode.Loading,
            new OpenClawChatTimelineProps(null, Array.Empty<ChatTimelineItem>(), false, null),
            null,
            false));

    private ChatThread? CreateComposeOnlyThread(
        IChatDataProvider provider,
        ChatDataSnapshot snapshot)
    {
        var composeKey = _pendingSelectedThreadId
            ?? (snapshot.ComposeTarget.IsReady ? snapshot.ComposeTarget.SessionKey : null);
        if (composeKey is null)
            return null;

        var cached = (provider as OpenClawChatDataProvider)?.CachedLastChatState;
        return new ChatThread
        {
            Id = composeKey,
            AgentId = snapshot.ComposeTarget.AgentId,
            Title = _pendingSelectedThreadId is null
                ? cached?.ThreadTitle ?? "OpenClaw Windows Tray"
                : LocalizationHelper.GetString("Chat_PendingNewSessionTitle"),
            Model = cached?.Model,
            ModelProvider = cached?.ModelProvider,
            Status = ChatThreadStatus.Running,
            Activity = ChatActivity.Idle,
        };
    }

    private static IReadOnlyList<ChatThread> VisibleChannels(ChatThread[] threads, ChatThread effectiveThread)
    {
        var visible = SessionVisibilityFilter.VisibleChatPickerThreads(threads, effectiveThread.Id)
            .Where(thread => !string.IsNullOrWhiteSpace(thread.Title)
                && thread.IsVisibleInSessionPicker(effectiveThread.Id))
            .ToList();
        if (!visible.Any(thread => string.Equals(thread.Id, effectiveThread.Id, StringComparison.Ordinal)))
            visible.Insert(0, effectiveThread);
        return visible;
    }

    private async Task<bool> SendAsync(
        string threadId,
        string? displayName,
        string message,
        IReadOnlyList<ChatAttachment> attachments,
        Action<int> setScrollToBottomToken,
        int scrollToBottomToken,
        Action<string> onLifecycleSessionCreated)
    {
        setScrollToBottomToken(scrollToBottomToken + 1);
        var provider = Props.Provider;
        if (provider is OpenClawChatDataProvider native
            && ChatLifecycleCommandParser.TryParse(message, attachments.Count > 0, out var command))
        {
            if (ChatLifecycleCommandExecutionPolicy.ShouldQueue(command))
                return await native.EnqueueCompactCommandAsync(threadId);

            if (command == ChatLifecycleCommandKind.Reset
                && Props.ConfirmResetAsync is not null
                && !await Props.ConfirmResetAsync(threadId, displayName))
            {
                return false;
            }

            var result = await native.ExecuteLifecycleCommandAsync(threadId, command);
            if (result.Succeeded && result.NewSessionKey is { } sessionKey)
                onLifecycleSessionCreated(sessionKey);
            return result.Succeeded;
        }

        try
        {
            await provider.SendMessageAsync(threadId, message, CancellationToken.None, attachments);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[chat] send failed: {ex}");
            return false;
        }
    }

    private void OnStop(string threadId) =>
        RunFireAndForget(ct => Props.Provider.StopResponseAsync(threadId, ct));

    private void OnPermission(string threadId, string requestId, string action) =>
        RunFireAndForget(ct => Props.Provider.RespondToPermissionAsync(threadId, requestId, action, ct));

    private static IReadOnlyList<ChatAttachment> RemoveAttachment(
        IReadOnlyList<ChatAttachment> attachments,
        ChatAttachment attachment)
    {
        var next = new List<ChatAttachment>(attachments.Count);
        var removed = false;
        foreach (var current in attachments)
        {
            if (!removed && ReferenceEquals(current, attachment))
            {
                removed = true;
                continue;
            }

            next.Add(current);
        }
        return removed ? next : attachments;
    }

    private static IReadOnlyList<ChatAttachment> RemoveSubmittedAttachments(
        IReadOnlyList<ChatAttachment> attachments,
        IReadOnlyList<ChatAttachment> submitted) =>
        attachments.Where(attachment => !submitted.Contains(attachment)).ToArray();

    private static string ToConnectionState(string? value) =>
        value?.StartsWith("Incompatible", StringComparison.OrdinalIgnoreCase) == true
            ? "incompatible-gateway"
            : value?.StartsWith("Connected", StringComparison.OrdinalIgnoreCase) == true
                ? "connected"
                : value?.StartsWith("Connecting", StringComparison.OrdinalIgnoreCase) == true
                    ? "connecting"
                    : "disconnected";

    private static void RunFireAndForget(Func<CancellationToken, Task> operation)
    {
        _ = Task.Run(async () =>
        {
            try { await operation(CancellationToken.None); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[chat] operation failed: {ex}"); }
        });
    }

    private static void ObserveFireAndForget(Task task)
    {
        _ = ObserveAsync(task);

        static async Task ObserveAsync(Task operation)
        {
            try { await operation; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[chat] operation failed: {ex}"); }
        }
    }

    private static async Task LoadAsync(
        IChatDataProvider provider,
        Action<ChatDataSnapshot?> setSnapshot,
        Func<string?> getSelected,
        Action<string?> setSelected)
    {
        try
        {
            var snapshot = await provider.LoadAsync();
            setSnapshot(snapshot);
            if (getSelected() is null && snapshot.DefaultThreadId is { } defaultThreadId)
                setSelected(defaultThreadId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[chat] load failed: {ex}");
        }
    }
}

public sealed record ReactorChatComposerProps(
    string ConnectionState,
    bool TurnActive,
    ChatThread CurrentThread,
    IReadOnlyList<ChatThread> AvailableChannels,
    string[] AvailableModels,
    IReadOnlyList<ChatModelChoice>? ModelChoices,
    bool MessageOptionsDisabled,
    IReadOnlyList<ChatAttachment> PendingAttachments,
    IReadOnlyList<ChatQueuedMessage> QueuedMessages,
    Func<string, IReadOnlyList<ChatAttachment>, Task<bool>> OnSend,
    Action OnStop,
    Action<string> OnChannelChanged,
    Action<string> OnModelChanged,
    Action OnModelCleared,
    Action<string> OnThinkingLevelChanged,
    Action<bool> OnPermissionsChanged,
    Func<CancellationToken, Action?, Task<string?>>? OnVoiceRequest,
    Action? OnAttachClick,
    bool IsSpeakerMuted,
    Action OnSpeakerToggle,
    Action? OnSettingsClick,
    string? VoiceTranscript,
    float VoiceAudioLevel,
    Action<Action> RegisterVoiceStarter,
    Action<ChatAttachment> OnAttachmentPasted,
    Action<ChatAttachment> OnAttachmentRemoved,
    Action<string> OnQueuedMessageCancel,
    IReadOnlyList<GatewayCommand>? AvailableCommands,
    bool CommandsSupported,
    Action? OnCommandsRequested,
    bool IsCompact);

public sealed class ReactorChatComposer : Component<ReactorChatComposerProps>
{
    private static readonly string[] ThinkingLevels = ["off", "minimal", "low", "medium", "high"];

    public override Element Render()
    {
        var props = Props;
        var colorScheme = UseColorScheme();
        var (text, setText) = UseState(string.Empty, threadSafe: true);
        var (isSending, setIsSending) = UseState(false, threadSafe: true);
        var (isRecording, setIsRecording) = UseState(false, threadSafe: true);
        var (slashMenuState, setSlashMenuState) = UseState(ReactorSlashMenuState.Closed, threadSafe: true);
        var inputRevision = UseRef(0);
        var sendInFlight = UseRef(false);
        var voiceCancellation = UseRef<CancellationTokenSource?>(null);
        var voiceOperation = UseRef(0);
        var voiceStopOperation = UseRef(0);
        var onAttachmentPasted = UseRef<Action<ChatAttachment>>(props.OnAttachmentPasted);
        onAttachmentPasted.Current = props.OnAttachmentPasted;
        var pasteHandler = UseRef<TextControlPasteEventHandler>(async (_, args) =>
        {
            if (GetBitmapClipboardContent() is not { } clipboardContent)
                return;

            // Paste is a synchronous routed event. Suppress the default text paste
            // before awaiting bitmap extraction so a multi-format clipboard cannot
            // insert text alongside the image attachment.
            args.Handled = true;
            await PasteImageFromClipboardAsync(clipboardContent, onAttachmentPasted.Current);
        });
        var inputText = UseRef(text);
        var inputControl = UseRef<TextBox?>(null);
        var slashPopup = UseRef<Microsoft.UI.Xaml.Controls.Primitives.Popup?>(null);
        var slashPopupContentRef = UseRef<(string Key, FrameworkElement? Content)>((string.Empty, null));
        var awaitingCatalog = UseRef(false);
        var dismissedSlashInputRevision = UseRef<int?>(null);
        var mounted = UseRef(true);
        inputText.Current = text;
        var slashDisplay = ReactorSlashCommandController.Evaluate(
            text,
            slashMenuState,
            props.ConnectionState == "connected" && !isRecording,
            props.CommandsSupported,
            props.AvailableCommands);
        UseEffect((Func<Action>)(() => () =>
        {
            mounted.Current = false;
            voiceCancellation.Current?.Cancel();
            voiceCancellation.Current?.Dispose();
            voiceCancellation.Current = null;
            voiceOperation.Current++;
            CloseSlashPopup(slashPopup);
        }), Array.Empty<object>());
        UseEffect((Func<Action>)(() =>
        {
            if (ReactorSlashCommandController.ShouldRequestCatalogOnOpen(awaitingCatalog.Current, slashDisplay))
                props.OnCommandsRequested?.Invoke();
            awaitingCatalog.Current = slashDisplay.ShouldRequestCatalog;
            return static () => { };
        }), slashDisplay.ShouldRequestCatalog);
        UseEffect((Func<Action>)(() =>
        {
            if (ReactorSlashCommandController.ShouldReconcileAfterCatalogRefresh(
                    inputRevision.Current,
                    dismissedSlashInputRevision.Current))
            {
                setSlashMenuState(ReactorSlashCommandController.ReconcileState(
                    inputText.Current,
                    props.AvailableCommands,
                    slashMenuState));
            }
            return static () => { };
        }), props.AvailableCommands);

        void StartVoiceRecording()
        {
            if (props.OnVoiceRequest is null || isRecording)
                return;

            var cancellation = new CancellationTokenSource();
            voiceCancellation.Current?.Cancel();
            voiceCancellation.Current?.Dispose();
            voiceCancellation.Current = cancellation;
            var operation = ++voiceOperation.Current;
            voiceStopOperation.Current = 0;
            setIsRecording(true);
            _ = ReceiveVoiceAsync(
                props.OnVoiceRequest,
                cancellation,
                operation,
                voiceOperation,
                voiceStopOperation,
                voiceCancellation,
                mounted,
                AppendVoiceTranscript,
                setIsRecording);
        }

        props.RegisterVoiceStarter(StartVoiceRecording);

        void SetText(string value)
        {
            inputRevision.Current++;
            dismissedSlashInputRevision.Current = null;
            inputText.Current = value;
            setText(value);
            setSlashMenuState(ReactorSlashCommandController.ReconcileState(
                value,
                props.AvailableCommands,
                slashMenuState));
        }

        void AppendVoiceTranscript(string transcript)
        {
            var draft = inputText.Current.TrimEnd();
            SetText(draft.Length == 0 ? transcript : $"{draft} {transcript}");
        }

        void Send()
        {
            var message = text.Trim();
            if ((message.Length == 0 && props.PendingAttachments.Count == 0)
                || sendInFlight.Current
                || slashDisplay.IsLoading
                || props.ConnectionState != "connected")
                return;

            sendInFlight.Current = true;
            setIsSending(true);
            _ = SendAsync(
                props.OnSend,
                message,
                props.PendingAttachments,
                inputRevision.Current,
                inputRevision,
                sendInFlight,
                SetText,
                setIsSending);
        }

        var modelChoices = props.ModelChoices is { Count: > 0 }
            ? props.ModelChoices
            : props.AvailableModels
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => new ChatModelChoice(model, model))
                .ToArray();
        var selectableModels = modelChoices.Where(model => model.IsSelectable).ToArray();
        var modelNames = new[] { Localized("Chat_Composer_Reasoning_Default", "Default") }
            .Concat(selectableModels.Select(ChatModelLabels.BuildMenuLabel))
            .ToArray();
        var modelIndex = string.IsNullOrWhiteSpace(props.CurrentThread.Model)
            ? 0
            : Math.Max(0, Array.FindIndex(
                selectableModels,
                model => model.MatchesModel(props.CurrentThread.Model, props.CurrentThread.ModelProvider)) + 1);
        var thinkingIndex = Math.Max(0, Array.IndexOf(
            ThinkingLevels,
            props.CurrentThread.ThinkingLevel ?? "medium"));
        var actionLabel = props.TurnActive
            ? Localized("Chat_Composer_Tooltip_Stop", "Stop")
            : Localized("Chat_Composer_Tooltip_Send", "Send");
        var controlCornerRadius = new CornerRadius(4);

        Element IconButton(
            string glyph,
            string automationName,
            Action onClick,
            bool enabled = true,
            string? automationId = null)
        {
            return Button(
                    TextBlock(glyph).Set(textBlock =>
                    {
                        textBlock.FontFamily = FluentIconCatalog.SymbolThemeFontFamily;
                        textBlock.FontSize = 16;
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
                            textBlock,
                            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                    }),
                    onClick)
                .AutomationName(automationName)
                .Foreground(Theme.SecondaryText)
                .Resources(resources => resources
                    .Set("ButtonBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ButtonBackgroundPointerOver", Theme.SubtleFill)
                    .Set("ButtonBackgroundPressed", Theme.Ref("SubtleFillColorTertiaryBrush"))
                    .Set("ButtonBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ButtonBorderBrushPointerOver", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ButtonBorderBrushPressed", Theme.Ref("SubtleFillColorTransparentBrush")))
                .Set(button =>
                {
                    button.Width = 32;
                    button.Height = 32;
                    button.MinWidth = 32;
                    button.MinHeight = 32;
                    button.Padding = new Thickness(0);
                    button.CornerRadius = controlCornerRadius;
                    button.IsEnabled = enabled;
                    button.BorderThickness = new Thickness(0);
                    if (!string.IsNullOrWhiteSpace(automationId))
                    {
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                            button,
                            automationId);
                    }
                    ComposerAutomationVisibility.Prepare(button);
                    ToolTipService.SetToolTip(button, automationName);
                })
                .OnUnmount(control => ComposerAutomationVisibility.Detach(
                    (FrameworkElement)control));
        }

        Element PickerButton(
            string label,
            string automationName,
            string automationId,
            bool enabled,
            double maxLabelWidth)
        {
            return Button(
                    HStack(
                        4,
                        TextBlock(label).Set(textBlock =>
                        {
                            textBlock.FontSize = 13;
                            textBlock.MaxWidth = maxLabelWidth;
                            textBlock.TextTrimming = TextTrimming.CharacterEllipsis;
                            textBlock.TextWrapping = TextWrapping.NoWrap;
                        }),
                        TextBlock("\uE70D").Set(textBlock =>
                        {
                            textBlock.FontFamily = FluentIconCatalog.SymbolThemeFontFamily;
                            textBlock.FontSize = 10;
                            textBlock.Margin = new Thickness(2, 4, 0, 0);
                            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
                                textBlock,
                                Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                        })),
                    () => { })
                .AutomationName(automationName)
                .Foreground(Theme.SecondaryText)
                .Resources(resources => resources
                    .Set("ButtonBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ButtonBackgroundPointerOver", Theme.SubtleFill)
                    .Set("ButtonBackgroundPressed", Theme.Ref("SubtleFillColorTertiaryBrush"))
                    .Set("ButtonBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ButtonBorderBrushPointerOver", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ButtonBorderBrushPressed", Theme.Ref("SubtleFillColorTransparentBrush")))
                .Set(button =>
                {
                    button.Height = 32;
                    button.MinHeight = 32;
                    button.MinWidth = 0;
                    button.Padding = new Thickness(8, 0, 8, 0);
                    button.CornerRadius = controlCornerRadius;
                    button.IsEnabled = enabled;
                    button.BorderThickness = new Thickness(0);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                        button,
                        automationId);
                    ComposerAutomationVisibility.Prepare(button);
                })
                .OnUnmount(control => ComposerAutomationVisibility.Detach(
                    (FrameworkElement)control));
        }

        var attachmentRows = props.PendingAttachments
            .Select(attachment =>
                (Element)HStack(
                    6,
                    TextBlock(attachment.FileName).FontSize(12),
                    Button("×", () => props.OnAttachmentRemoved(attachment))
                        .SubtleButton()
                        .AutomationName("Remove attachment")))
            .ToArray();
        var audioLevel = Math.Clamp(props.VoiceAudioLevel, 0f, 1f);
        var voiceFeedbackText = string.IsNullOrWhiteSpace(props.VoiceTranscript)
            ? Localized("Chat_Voice_ListeningPrompt", "Listening…")
            : props.VoiceTranscript;
        var waveformBars = Enumerable.Range(0, 8)
            .Select(index =>
                (Element)Border(Empty())
                    .Width(2)
                    .Height(2 + (audioLevel * (index % 3 == 1 ? 10 : 7)))
                    .CornerRadius(1)
                    .VAlign(VerticalAlignment.Center)
                    .Background(Theme.SecondaryText))
            .ToArray();
        Element voiceFeedback = !isRecording
            ? Empty()
            : Border(
                    HStack(
                        6,
                        Border(Empty())
                            .Width(6)
                            .Height(6)
                            .CornerRadius(3)
                            .Background(Theme.SecondaryText),
                        TextBlock(voiceFeedbackText)
                            .FontSize(11)
                            .Foreground(Theme.SecondaryText),
                        HStack(1, waveformBars)))
                .Padding(8, 4)
                .HAlign(HorizontalAlignment.Left);
        var queuedRows = props.QueuedMessages
            .Select((message, index) =>
            {
                var failed = message.SendState == ChatQueuedMessageSendState.Failed;
                var actionKey = failed
                    ? "Chat_Composer_QueuedMessageRemoveFailed"
                    : "Chat_Composer_QueuedMessageCancel";
                var actionAutomationKey = failed
                    ? "Chat_Composer_QueuedMessageRemoveFailedAutomationFormat"
                    : "Chat_Composer_QueuedMessageCancelAutomationFormat";
                var rowAutomationKey = failed
                    ? "Chat_Composer_QueuedMessageFailedAutomationFormat"
                    : "Chat_Composer_QueuedMessageAutomationFormat";
                var action = message.SendState == ChatQueuedMessageSendState.Sending
                    ? Empty()
                    : Button(Localized(actionKey, failed ? "Remove failed message" : "Cancel"),
                            () => props.OnQueuedMessageCancel(message.Id))
                        .SubtleButton()
                        .AutomationId($"{(failed ? "ChatQueuedMessageRemoveFailed" : "ChatQueuedMessageCancel")}_{message.Id}")
                        .AutomationName(string.Format(
                            CultureInfo.CurrentCulture,
                            Localized(actionAutomationKey, "{0}: {1}"),
                            index + 1,
                            message.Text));
                var state = failed
                    ? (Element)TextBlock(Localized("Chat_Composer_QueuedMessageFailed", "Failed"))
                        .FontSize(12)
                    : Empty();
                var error = failed && !string.IsNullOrWhiteSpace(message.ErrorText)
                    ? (Element)TextBlock(message.ErrorText!).FontSize(12)
                    : Empty();
                return (Element)HStack(
                        6,
                        VStack(
                                4,
                                state,
                                TextBlock(message.Text).FontSize(12).MaxWidth(260),
                                error)
                            .HAlign(HorizontalAlignment.Left),
                        action)
                    .AutomationName(string.Format(
                        CultureInfo.CurrentCulture,
                        Localized(rowAutomationKey, "{0}"),
                        message.Text));
            })
            .ToArray();
        var queuedCountText = string.Format(
            CultureInfo.CurrentCulture,
            Localized("Chat_Composer_QueuedCountFormat", "{0} queued messages"),
            queuedRows.Length);
        Element queuedPanel = queuedRows.Length == 0
            ? Empty()
            : Border(
                    VStack(
                        8,
                        TextBlock(queuedCountText)
                            .FontSize(13)
                            .Set(textBlock => textBlock.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold),
                        ScrollView(VStack(4, queuedRows))
                            .MaxHeight(props.IsCompact ? 144 : 220)
                            .Set(scrollView =>
                            {
                                scrollView.VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Auto;
                                scrollView.HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden;
                                scrollView.HorizontalScrollMode = ScrollingScrollMode.Disabled;
                                scrollView.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                            })))
                .Set(border => Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(
                    border,
                    Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite))
                .AutomationName(queuedCountText);

        void DismissSlashMenu()
        {
            dismissedSlashInputRevision.Current = inputRevision.Current;
            setSlashMenuState(ReactorSlashMenuState.Closed);
        }

        void CommitSlashText(string value, ReactorSlashMenuState nextState)
        {
            inputRevision.Current++;
            inputText.Current = value;
            setText(value);
            setSlashMenuState(nextState);
            inputControl.Current?.DispatcherQueue?.TryEnqueue(() =>
            {
                if (inputControl.Current is not { } textBox)
                    return;

                textBox.Focus(FocusState.Programmatic);
                var caret = textBox.Text?.Length ?? 0;
                textBox.SelectionStart = caret;
                textBox.SelectionLength = 0;
            });
        }

        var slashPopupVisible = slashDisplay.IsVisible
            && (slashDisplay.IsLoading
                || (slashDisplay.IsArgsMode && slashDisplay.ArgCommand is not null)
                || slashDisplay.Commands.Count > 0);
        var popupCatalogKey = props.AvailableCommands is null
            ? "missing"
            : RuntimeHelpers.GetHashCode(props.AvailableCommands).ToString(CultureInfo.InvariantCulture);
        var popupArgumentCommandKey = slashDisplay.ArgCommand?.Name
            ?? slashDisplay.ArgCommand?.DisplayName()
            ?? string.Empty;
        var popupStateKey = string.Join(
            "|",
            slashPopupVisible,
            slashDisplay.IsLoading,
            slashDisplay.IsArgsMode,
            popupArgumentCommandKey,
            slashDisplay.Query,
            slashDisplay.SelectedIndex,
            slashDisplay.SelectableCount,
            popupCatalogKey,
            colorScheme);
        FrameworkElement? slashPopupContent;
        if (!slashPopupVisible)
        {
            slashPopupContentRef.Current = (string.Empty, null);
            slashPopupContent = null;
        }
        else if (slashPopupContentRef.Current.Key == popupStateKey)
        {
            slashPopupContent = slashPopupContentRef.Current.Content;
        }
        else if (slashDisplay.IsLoading)
        {
            slashPopupContent = CreateSlashPopupHost(BuildSlashHintPopup(
                Localized("Chat_Composer_Slash_Loading", "Loading commands...")));
            slashPopupContentRef.Current = (popupStateKey, slashPopupContent);
        }
        else if (slashDisplay.IsArgsMode && slashDisplay.ArgCommand is { } argCommand)
        {
            slashPopupContent = CreateSlashPopupHost(BuildSlashArgPopup(
                argCommand,
                slashDisplay.ArgChoices,
                slashDisplay.SelectedIndex,
                choice => CommitSlashText(
                    argCommand.BuildArgInsertionText(choice.Value),
                    ReactorSlashMenuState.Closed)));
            slashPopupContentRef.Current = (popupStateKey, slashPopupContent);
        }
        else
        {
            slashPopupContent = CreateSlashPopupHost(BuildSlashPopup(
                slashDisplay.Groups,
                slashDisplay.SelectedIndex,
                slashDisplay.Query,
                colorScheme,
                command =>
                {
                    CommitSlashText(
                        command.FirstArgChoices().Count > 0 ? command.DisplayName() + " " : command.BuildInsertionText(),
                        command.FirstArgChoices().Count > 0
                            ? new ReactorSlashMenuState(true, string.Empty, 0, true)
                            : ReactorSlashMenuState.Closed);
                }));
            slashPopupContentRef.Current = (popupStateKey, slashPopupContent);
        }

        var input = TextBox(
                text,
                SetText,
                PlaceholderFor(props.ConnectionState))
            .AutomationId("ChatComposerInput")
            .AutomationName(PlaceholderFor(props.ConnectionState))
            .OnKeyDown((sender, args) =>
            {
                if (slashDisplay.IsVisible)
                {
                    switch (args.Key)
                    {
                        case global::Windows.System.VirtualKey.Down when slashDisplay.HasSelection:
                            args.Handled = true;
                            setSlashMenuState(ReactorSlashCommandController.MoveSelection(
                                slashMenuState,
                                slashDisplay,
                                1));
                            return;

                        case global::Windows.System.VirtualKey.Up when slashDisplay.HasSelection:
                            args.Handled = true;
                            setSlashMenuState(ReactorSlashCommandController.MoveSelection(
                                slashMenuState,
                                slashDisplay,
                                -1));
                            return;

                        case global::Windows.System.VirtualKey.Enter:
                        case global::Windows.System.VirtualKey.Tab:
                            if (slashDisplay.HasSelection)
                            {
                                args.Handled = true;
                                var commit = ReactorSlashCommandController.CommitSelection(slashDisplay);
                                if (commit.Accepted)
                                    CommitSlashText(commit.Text, commit.NextState);
                                return;
                            }

                            if (slashDisplay.IsLoading)
                            {
                                args.Handled = true;
                                if (args.Key == global::Windows.System.VirtualKey.Tab)
                                    DismissSlashMenu();
                                return;
                            }
                             break;

                        case global::Windows.System.VirtualKey.Escape:
                            args.Handled = true;
                            DismissSlashMenu();
                            return;
                    }

                    if (slashDisplay.IsLoading
                        && (args.Key == global::Windows.System.VirtualKey.Up
                            || args.Key == global::Windows.System.VirtualKey.Down))
                    {
                        args.Handled = true;
                        return;
                    }
                }

                if (args.Key != global::Windows.System.VirtualKey.Enter)
                    return;

                args.Handled = true;
                var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                    global::Windows.System.VirtualKey.Shift);
                if (shift.HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down)
                    && sender is Microsoft.UI.Xaml.Controls.TextBox textBox)
                {
                    var current = textBox.Text ?? string.Empty;
                    var start = Math.Clamp(textBox.SelectionStart, 0, current.Length);
                    var end = Math.Clamp(start + textBox.SelectionLength, start, current.Length);
                    SetText(current[..start] + "\n" + current[end..]);
                    textBox.SelectionStart = start + 1;
                    textBox.SelectionLength = 0;
                    return;
                }

                Send();
            })
            .TextWrapping(TextWrapping.Wrap)
            .Set(control =>
            {
                inputControl.Current = control;
                var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                control.MinHeight = 56;
                control.MaxHeight = 200;
                control.FontSize = 14;
                control.Padding = new Thickness(8);
                control.IsEnabled = props.ConnectionState == "connected";
                control.AcceptsReturn = false;
                control.BorderThickness = new Thickness(0);
                control.BorderBrush = transparent;
                control.Background = transparent;
                control.Resources["TextControlBorderThemeThickness"] = new Thickness(0);
                control.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
                control.Resources["TextControlBackground"] = transparent;
                control.Resources["TextControlBackgroundFocused"] = transparent;
                control.Resources["TextControlBackgroundPointerOver"] = transparent;
                control.Resources["TextControlBorderBrush"] = transparent;
                control.Resources["TextControlBorderBrushFocused"] = transparent;
                control.Resources["TextControlBorderBrushPointerOver"] = transparent;
                ComposerAutomationVisibility.Prepare(control);
            })
            .OnMount(control =>
            {
                var textBox = (TextBox)control;
                textBox.Paste += pasteHandler.Current;
                textBox.ContextFlyout = CreateComposerContextFlyout(
                    textBox,
                    () => onAttachmentPasted.Current);
            })
            .OnUnmount(control =>
            {
                var textBox = (TextBox)control;
                textBox.Paste -= pasteHandler.Current;
                textBox.ContextFlyout = null;
                ComposerAutomationVisibility.Detach(textBox);
            });
        UseEffect((Func<Action>)(() =>
        {
            if (inputControl.Current is { } anchor)
                DriveSlashPopup(slashPopup, anchor, slashPopupContent, slashPopupVisible);
            else
                CloseSlashPopup(slashPopup);
            return static () => { };
        }), popupStateKey);

        var sessionPicker = MenuFlyout(
            PickerButton(
                props.CurrentThread.Title,
                $"{Localized("Chat_Composer_Accessibility_Session", "Session")}: {props.CurrentThread.Title}",
                "ChatComposerSessionPicker",
                !props.MessageOptionsDisabled && props.AvailableChannels.Count > 1,
                props.IsCompact ? 56 : 160),
            props.AvailableChannels
                .Select(thread => RadioMenuItem(
                    thread.Title,
                    "chat-sessions",
                    string.Equals(thread.Id, props.CurrentThread.Id, StringComparison.Ordinal),
                    () => props.OnChannelChanged(thread.Id)))
                .ToArray());

        var modelPickerLabel = modelIndex == 0
            ? Localized("Chat_Composer_Reasoning_Default", "Default")
            : selectableModels[modelIndex - 1].DisplayName;
        var modelPicker = MenuFlyout(
            PickerButton(
                modelPickerLabel,
                $"{Localized("Chat_Composer_Accessibility_Model", "Model")}: {modelPickerLabel}",
                "ChatComposerModelPicker",
                !props.MessageOptionsDisabled,
                props.IsCompact ? 68 : 180),
            modelNames
                .Select((modelName, index) => RadioMenuItem(
                    modelName,
                    "chat-models",
                    index == modelIndex,
                    () =>
                    {
                        if (index == 0)
                            props.OnModelCleared();
                        else if (index <= selectableModels.Length)
                            props.OnModelChanged(selectableModels[index - 1].SelectionId);
                    }))
                .ToArray());

        var reasoningPicker = MenuFlyout(
            PickerButton(
                ThinkingLevels[thinkingIndex],
                $"{Localized("Chat_Composer_Accessibility_Reasoning", "Reasoning")}: {ThinkingLevels[thinkingIndex]}",
                "ChatComposerReasoningPicker",
                !props.MessageOptionsDisabled,
                props.IsCompact ? 54 : 96),
            ThinkingLevels
                .Select((level, index) => RadioMenuItem(
                    level,
                    "chat-thinking-level",
                    index == thinkingIndex,
                    () => props.OnThinkingLevelChanged(level)))
                .ToArray());

        var attachButton = IconButton(
            "\uE723",
            Localized("Chat_Composer_Tooltip_Attach", "Attach"),
            () => props.OnAttachClick?.Invoke(),
            props.OnAttachClick is not null,
            "ChatComposerAttach");
        var voiceButton = IconButton(
            isRecording
                ? "\uE15B"
                : "\uE720",
            isRecording
                ? Localized("Chat_Composer_Tooltip_Stop", "Stop")
                : Localized("Chat_Composer_Tooltip_Voice", "Voice"),
            () =>
            {
                if (isRecording)
                {
                    voiceStopOperation.Current = voiceOperation.Current;
                    voiceCancellation.Current?.Cancel();
                }
                else
                    StartVoiceRecording();
            },
            props.OnVoiceRequest is not null,
            "ChatComposerVoice");
        var speakerButton = IconButton(
            props.IsSpeakerMuted ? "\uE74F" : "\uE767",
            props.IsSpeakerMuted ? "Unmute" : "Mute",
            props.OnSpeakerToggle,
            automationId: "ChatComposerSpeakerToggle");
        Element settingsButton = props.IsCompact || props.OnSettingsClick is null
            ? Empty()
            : IconButton(
                "\uE713",
                Localized("Chat_Composer_Tooltip_Settings", "Settings"),
                props.OnSettingsClick,
                automationId: "ChatComposerSettings");

        Element primaryAction = props.TurnActive
            ? IconButton(
                "\uE71A",
                actionLabel,
                props.OnStop,
                automationId: "ChatComposerPrimaryAction")
            : Button(
                    TextBlock("\uE724").Set(textBlock =>
                    {
                        textBlock.FontFamily = FluentIconCatalog.SymbolThemeFontFamily;
                        textBlock.FontSize = 16;
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
                            textBlock,
                            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                    }),
                    Send)
                .AccentButton()
                .AutomationName(actionLabel)
                .Set(button =>
                {
                    button.Width = 32;
                    button.Height = 32;
                    button.MinWidth = 32;
                    button.MinHeight = 32;
                    button.Padding = new Thickness(0);
                    button.CornerRadius = controlCornerRadius;
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                        button,
                        "ChatComposerPrimaryAction");
                    button.IsEnabled = props.ConnectionState == "connected"
                        && !isSending
                        && !slashDisplay.IsLoading
                        && (!string.IsNullOrWhiteSpace(text) || props.PendingAttachments.Count > 0);
                    ComposerAutomationVisibility.Prepare(button);
                    ToolTipService.SetToolTip(button, actionLabel);
                })
                .OnUnmount(control => ComposerAutomationVisibility.Detach(
                    (FrameworkElement)control));

        var leftToolbar = HStack(8, attachButton, sessionPicker, modelPicker, reasoningPicker)
            .HAlign(HorizontalAlignment.Left)
            .VAlign(VerticalAlignment.Center);
        var rightToolbar = HStack(8, voiceButton, speakerButton, settingsButton, primaryAction)
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Center);
        var toolbar = Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            leftToolbar.Grid(row: 0, column: 0),
            rightToolbar.Grid(row: 0, column: 1));

        var composerChildren = new List<Element>();
        if (isRecording)
            composerChildren.Add(voiceFeedback);
        if (attachmentRows.Length > 0)
            composerChildren.Add(VStack(4, attachmentRows));
        if (queuedRows.Length > 0)
            composerChildren.Add(queuedPanel);
        composerChildren.Add(input);
        composerChildren.Add(toolbar);

        return Border(
            VStack(8, composerChildren.ToArray())
            .Padding(8, 2, 8, 8))
            .BorderThickness(1)
            .CornerRadius(8)
            .Margin(12)
            .Background(Theme.ControlFill)
            .BorderBrush(Theme.ControlStroke)
            .HAlign(HorizontalAlignment.Stretch);
    }

    private static void CloseSlashPopup(Ref<Microsoft.UI.Xaml.Controls.Primitives.Popup?> popupRef)
    {
        if (popupRef.Current is not { } popup)
            return;

        popup.IsOpen = false;
        if (popup.Child is ReactorHostControl host)
            host.Dispose();
        popup.Child = null;
        popup.PlacementTarget = null;
    }

    private static ReactorHostControl CreateSlashPopupHost(Element content)
    {
        var host = new ReactorHostControl();
        host.Mount(_ => content);
        return host;
    }

    private static void DriveSlashPopup(
        Ref<Microsoft.UI.Xaml.Controls.Primitives.Popup?> popupRef,
        TextBox anchor,
        FrameworkElement? content,
        bool visible)
    {
        var popup = popupRef.Current;
        if (popup is null)
        {
            popup = new Microsoft.UI.Xaml.Controls.Primitives.Popup
            {
                IsLightDismissEnabled = false,
                ShouldConstrainToRootBounds = true,
            };
            popupRef.Current = popup;
        }

        if (!visible || content is null || anchor.XamlRoot is null)
        {
            CloseSlashPopup(popupRef);
            return;
        }

        content.Width = Math.Max(280, anchor.ActualWidth > 0 ? anchor.ActualWidth : 360);
        popup.XamlRoot = anchor.XamlRoot;
        popup.PlacementTarget = anchor;
        popup.DesiredPlacement = Microsoft.UI.Xaml.Controls.Primitives.PopupPlacementMode.Top;
        if (popup.Child is ReactorHostControl previousHost
            && !ReferenceEquals(previousHost, content))
            previousHost.Dispose();
        popup.Child = content;
        popup.IsOpen = true;
    }

    private static Element BuildSlashHintPopup(string text)
    {
        return SlashShell(
            TextBlock(text)
                .FontSize(12)
                .Foreground(Theme.SecondaryText)
                .Margin(8, 6, 8, 6));
    }

    private static Element BuildSlashPopup(
        IReadOnlyList<CommandCategoryGroup> groups,
        int selectedIndex,
        string query,
        ColorScheme colorScheme,
        Action<GatewayCommand> onPick)
    {
        var rows = new List<Element>();
        var index = 0;
        foreach (var group in groups)
        {
            rows.Add(SlashCategoryHeader(CommandCategories.Label(group.Category)));
            foreach (var command in group.Commands)
            {
                rows.Add(SlashRow(command, index == selectedIndex, query, colorScheme, onPick));
                index++;
            }
        }

        return SlashShell(
            ScrollView(VStack(0, rows.ToArray()))
                .MaxHeight(280)
                .Set(scrollViewer =>
                {
                    scrollViewer.VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Auto;
                    scrollViewer.HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden;
                }));
    }

    private static Element SlashCategoryHeader(string text)
    {
        return TextBlock((text ?? string.Empty).ToUpperInvariant())
            .FontSize(11)
            .SemiBold()
            .CharacterSpacing(60)
            .Foreground(Theme.TertiaryText)
            .Margin(8, 8, 8, 2);
    }

    private static Element BuildSlashArgPopup(
        GatewayCommand command,
        IReadOnlyList<GatewayCommandArgChoice> choices,
        int selectedIndex,
        Action<GatewayCommandArgChoice> onPick)
    {
        var argDescription = command.Args?.FirstOrDefault()?.Description;
        var headerText = !string.IsNullOrWhiteSpace(argDescription)
            ? $"{command.DisplayName()}  {argDescription}"
            : !string.IsNullOrWhiteSpace(command.Description)
                ? $"{command.DisplayName()}  {command.Description}"
                : command.DisplayName();
        var rows = new List<Element>
        {
            TextBlock(headerText)
                .FontSize(11)
                .SemiBold()
                .TextTrimming(TextTrimming.CharacterEllipsis)
                .MaxLines(1)
                .Foreground(Theme.TertiaryText)
                .Margin(8, 6, 8, 2),
        };
        for (var index = 0; index < choices.Count; index++)
            rows.Add(SlashArgRow(command, choices[index], index == selectedIndex, onPick));

        return SlashShell(
            ScrollView(VStack(0, rows.ToArray()))
                .MaxHeight(280)
                .Set(scrollViewer =>
                {
                    scrollViewer.VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Auto;
                    scrollViewer.HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden;
                }));
    }

    private static Element SlashArgRow(
        GatewayCommand command,
        GatewayCommandArgChoice choice,
        bool selected,
        Action<GatewayCommandArgChoice> onPick)
    {
        var label = string.IsNullOrWhiteSpace(choice.Label) ? choice.Value : choice.Label;
        var background = selected ? Theme.SubtleFill : Theme.Ref("SubtleFillColorTransparentBrush");
        return Button(
                HStack(
                    8,
                    TextBlock(label)
                        .FontSize(13)
                        .SemiBold()
                        .VAlign(VerticalAlignment.Center)
                        .Foreground(Theme.PrimaryText),
                    TextBlock($"{command.DisplayName()} {choice.Value}")
                        .FontSize(12)
                        .VAlign(VerticalAlignment.Center)
                        .TextTrimming(TextTrimming.CharacterEllipsis)
                        .MaxLines(1)
                        .Foreground(Theme.SecondaryText)),
                () => onPick(choice))
            .Padding(8, 7, 8, 7)
            .HAlign(HorizontalAlignment.Stretch)
            .CornerRadius(6)
            .AutomationName($"Choose {label} for {command.DisplayName()}")
            .Resources(resources => resources
                .Set("ButtonBackground", background)
                .Set("ButtonBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush")))
            .Set(button =>
            {
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.BorderThickness = new Thickness(0);
            })
            .OnMount(element =>
            {
                if (selected)
                    element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            });
    }

    private static Element SlashShell(Element child)
    {
        return Border(child)
            .Padding(4)
            .CornerRadius(8)
            .Background(Theme.Ref("AcrylicBackgroundFillColorDefaultBrush"))
            .WithBorder(Theme.Ref("SurfaceStrokeColorFlyoutBrush"), 1)
            .Translation(0, 0, 32)
            .Set(border => border.Shadow = new ThemeShadow());
    }

    private static Element SlashRow(
        GatewayCommand command,
        bool selected,
        string query,
        ColorScheme colorScheme,
        Action<GatewayCommand> onPick)
    {
        var cells = new List<Element>
        {
            TextBlock(SlashGlyph(command))
                .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                .FontSize(14)
                .VAlign(VerticalAlignment.Center)
                .Foreground(Theme.SecondaryText)
                .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw)
                .Grid(row: 0, column: 0),
            TextBlock(command.DisplayName())
                .FontSize(13)
                .SemiBold()
                .VAlign(VerticalAlignment.Center)
                .Foreground(Theme.PrimaryText)
                .Set(textBlock => ApplyQueryHighlight(textBlock, query, colorScheme))
                .Grid(row: 0, column: 1),
        };
        var args = command.ArgTemplate();
        if (!string.IsNullOrWhiteSpace(args))
        {
            cells.Add(
                TextBlock(args)
                    .FontSize(12)
                    .FontFamily("Consolas")
                    .VAlign(VerticalAlignment.Center)
                    .Foreground(Theme.SecondaryText)
                    .Grid(row: 0, column: 2));
        }

        if (!string.IsNullOrWhiteSpace(command.Description))
        {
            cells.Add(
                TextBlock(command.Description!)
                    .FontSize(12)
                    .VAlign(VerticalAlignment.Center)
                    .HAlign(HorizontalAlignment.Right)
                    .TextAlignment(TextAlignment.Right)
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .MaxLines(1)
                    .Foreground(Theme.SecondaryText)
                    .Set(textBlock => ApplyQueryHighlight(textBlock, query, colorScheme))
                    .Grid(row: 0, column: 3));
        }

        var options = command.OptionCount();
        if (options > 0)
        {
            cells.Add(SlashBadge($"{options} options").Grid(row: 0, column: 4));
        }

        var background = selected ? Theme.SubtleFill : Theme.Ref("SubtleFillColorTransparentBrush");
        return Button(
                Grid(
                    [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    cells.ToArray())
                    .Set(grid => grid.ColumnSpacing = 8)
                    .VAlign(VerticalAlignment.Center),
                () => onPick(command))
            .Padding(8, 7, 8, 7)
            .HAlign(HorizontalAlignment.Stretch)
            .CornerRadius(6)
            .AutomationName($"Insert {command.DisplayName()}")
            .Resources(resources => resources
                .Set("ButtonBackground", background)
                .Set("ButtonBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush")))
            .Set(button =>
            {
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                button.BorderThickness = new Thickness(0);
            })
            .OnMount(element =>
            {
                if (selected)
                    element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            });
    }

    private static Element SlashBadge(string text)
    {
        return Border(
                TextBlock(text)
                    .FontSize(10)
                    .SemiBold()
                    .Foreground(Theme.Ref("TextOnAccentFillColorPrimaryBrush")))
            .Padding(6, 1, 6, 1)
            .CornerRadius(4)
            .VAlign(VerticalAlignment.Center)
            .Background(Theme.AccentSecondary);
    }

    private static void ApplyQueryHighlight(TextBlock textBlock, string? query, ColorScheme colorScheme)
    {
        textBlock.TextHighlighters.Clear();
        var text = textBlock.Text ?? string.Empty;
        var normalized = (query ?? string.Empty).Trim().TrimStart('/').Trim();
        if (normalized.Length == 0 || text.Length < normalized.Length || colorScheme == ColorScheme.HighContrast)
            return;

        var isDark = colorScheme == ColorScheme.Dark;
        if (ThemeRef.Resolve("AccentFillColorDefaultBrush", isDark) is not SolidColorBrush accent
            || ThemeRef.Resolve("TextFillColorPrimaryBrush", isDark) is not Brush foreground)
            return;

        var accentColor = accent.Color;
        var highlighter = new Microsoft.UI.Xaml.Documents.TextHighlighter
        {
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(31, accentColor.R, accentColor.G, accentColor.B)),
            Foreground = foreground,
        };

        for (var index = 0; index <= text.Length - normalized.Length;)
        {
            var found = text.IndexOf(normalized, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
                break;
            highlighter.Ranges.Add(new Microsoft.UI.Xaml.Documents.TextRange
            {
                StartIndex = found,
                Length = normalized.Length,
            });
            index = found + normalized.Length;
        }

        if (highlighter.Ranges.Count > 0)
            textBlock.TextHighlighters.Add(highlighter);
    }

    private static string SlashGlyph(GatewayCommand command)
    {
        var name = (command.NativeName ?? command.DisplayName()).Trim().TrimStart('/').ToLowerInvariant()
            .Replace(':', '_')
            .Replace('.', '_')
            .Replace('-', '_');
        return name switch
        {
            "help" or "commands" => "\uE82D",
            "status" or "usage" => "\uE9D9",
            "export" or "export_session" => "\uE896",
            "skill" or "fast" => "\uE945",
            "model" or "models" or "think" => "\uE713",
            "new" => "\uE710",
            "reset" or "redirect" => "\uE72C",
            "compact" => "\uE9F3",
            "stop" => "\uE71A",
            "clear" => "\uE74D",
            "agents" => "\uE7F4",
            "subagents" => "\uE8B7",
            "steer" => "\uE724",
            "tts" => "\uE767",
            _ => "\uE756",
        };
    }

    private static async Task SendAsync(
        Func<string, IReadOnlyList<ChatAttachment>, Task<bool>> send,
        string message,
        IReadOnlyList<ChatAttachment> attachments,
        int submittedRevision,
        Ref<int> inputRevision,
        Ref<bool> sendInFlight,
        Action<string> setText,
        Action<bool> setIsSending)
    {
        try
        {
            if (await send(message, attachments)
                && ChatComposerSubmissionPolicy.ShouldClearInput(
                    submittedRevision,
                    inputRevision.Current))
                setText(string.Empty);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[chat] composer send failed: {ex}");
        }
        finally
        {
            sendInFlight.Current = false;
            setIsSending(false);
        }
    }

    private static async Task ReceiveVoiceAsync(
        Func<CancellationToken, Action?, Task<string?>> request,
        CancellationTokenSource cancellation,
        int operation,
        Ref<int> voiceOperation,
        Ref<int> voiceStopOperation,
        Ref<CancellationTokenSource?> voiceCancellation,
        Ref<bool> mounted,
        Action<string> setText,
        Action<bool> setIsRecording)
    {
        try
        {
            var transcript = await request(cancellation.Token, () => setIsRecording(true));
            var stoppedByUser = voiceStopOperation.Current == operation;
            if (mounted.Current
                && (!cancellation.IsCancellationRequested || stoppedByUser)
                && !string.IsNullOrWhiteSpace(transcript))
                setText(transcript);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OpenClawTray.Services.Logger.Debug($"Reactor chat composer voice request failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(voiceCancellation.Current, cancellation))
                voiceCancellation.Current = null;
            cancellation.Dispose();
            if (voiceOperation.Current == operation)
                setIsRecording(false);
        }
    }

    private static async Task<ChatAttachment?> TryReadImageFromClipboardAsync(
        global::Windows.ApplicationModel.DataTransfer.DataPackageView content)
    {
        var streamRef = await content.GetBitmapAsync();
        using var input = await streamRef.OpenReadAsync();
        var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(input);
        using var bitmap = await decoder.GetSoftwareBitmapAsync();
        using var output = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
            output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();

        var size = (long)output.Size;
        if (size > ChatAttachment.MaxSizeBytes)
            return null;

        output.Seek(0);
        var bytes = new byte[size];
        using (var reader = new global::Windows.Storage.Streams.DataReader(output.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)size);
            reader.ReadBytes(bytes);
        }

        return new ChatAttachment
        {
            Type = "image",
            MimeType = "image/png",
            FileName = $"pasted-image-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            Content = Convert.ToBase64String(bytes),
            SizeBytes = size,
        };
    }

    private static global::Windows.ApplicationModel.DataTransfer.DataPackageView? GetBitmapClipboardContent()
    {
        try
        {
            var content = global::Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            return content is not null
                && content.Contains(
                    global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap)
                    ? content
                    : null;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            OpenClawTray.Services.Logger.Debug(
                $"Reactor chat composer: clipboard access failed: {ex.Message}");
            return null;
        }
    }

    private static MenuFlyout CreateComposerContextFlyout(
        TextBox textBox,
        Func<Action<ChatAttachment>> getOnAttachmentPasted)
    {
        var undoItem = CreateStandardMenuItem(
            Microsoft.UI.Xaml.Input.StandardUICommandKind.Undo,
            textBox.Undo);
        var redoItem = CreateStandardMenuItem(
            Microsoft.UI.Xaml.Input.StandardUICommandKind.Redo,
            textBox.Redo);
        var cutItem = CreateStandardMenuItem(
            Microsoft.UI.Xaml.Input.StandardUICommandKind.Cut,
            textBox.CutSelectionToClipboard);
        var copyItem = CreateStandardMenuItem(
            Microsoft.UI.Xaml.Input.StandardUICommandKind.Copy,
            textBox.CopySelectionToClipboard);
        var pasteItem = CreateStandardMenuItem(
            Microsoft.UI.Xaml.Input.StandardUICommandKind.Paste,
            () =>
            {
                if (GetBitmapClipboardContent() is { } clipboardContent)
                    _ = PasteImageFromClipboardAsync(clipboardContent, getOnAttachmentPasted());
                else
                    PasteTextFromClipboard(textBox);
            });
        var selectAllItem = CreateStandardMenuItem(
            Microsoft.UI.Xaml.Input.StandardUICommandKind.SelectAll,
            textBox.SelectAll);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            pasteItem,
            "ChatComposerPasteMenuItem");

        var editSeparator = new MenuFlyoutSeparator();
        var selectAllSeparator = new MenuFlyoutSeparator();
        var menu = new MenuFlyout();
        menu.Items.Add(undoItem);
        menu.Items.Add(redoItem);
        menu.Items.Add(editSeparator);
        menu.Items.Add(cutItem);
        menu.Items.Add(copyItem);
        menu.Items.Add(pasteItem);
        menu.Items.Add(selectAllSeparator);
        menu.Items.Add(selectAllItem);
        menu.Opening += (_, _) =>
        {
            var state = ChatComposerContextMenuState.Project(
                textBox.CanUndo,
                textBox.CanRedo,
                textBox.SelectionLength > 0,
                ClipboardContainsPasteContent(),
                !string.IsNullOrEmpty(textBox.Text));
            undoItem.Visibility = ToVisibility(state.ShowUndo);
            redoItem.Visibility = ToVisibility(state.ShowRedo);
            cutItem.Visibility = ToVisibility(state.ShowCut);
            copyItem.Visibility = ToVisibility(state.ShowCopy);
            pasteItem.Visibility = ToVisibility(state.ShowPaste);
            selectAllItem.Visibility = ToVisibility(state.ShowSelectAll);
            editSeparator.Visibility = ToVisibility(state.ShowEditSeparator);
            selectAllSeparator.Visibility = ToVisibility(state.ShowSelectAllSeparator);
        };
        return menu;
    }

    private static Visibility ToVisibility(bool visible) =>
        visible ? Visibility.Visible : Visibility.Collapsed;

    private static MenuFlyoutItem CreateStandardMenuItem(
        Microsoft.UI.Xaml.Input.StandardUICommandKind kind,
        Action execute)
    {
        var command = new Microsoft.UI.Xaml.Input.StandardUICommand(kind);
        command.CanExecuteRequested += (_, args) => args.CanExecute = true;
        command.ExecuteRequested += (_, _) => execute();
        return new MenuFlyoutItem
        {
            Command = command,
            Visibility = Visibility.Collapsed,
        };
    }

    private static bool ClipboardContainsPasteContent()
    {
        try
        {
            var content = global::Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            return content is not null
                && (content.Contains(
                        global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap)
                    || content.Contains(
                        global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text));
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            OpenClawTray.Services.Logger.Debug(
                $"Reactor chat composer: clipboard access failed: {ex.Message}");
            return false;
        }
    }

    private static void PasteTextFromClipboard(TextBox textBox)
    {
        try
        {
            textBox.PasteFromClipboard();
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            OpenClawTray.Services.Logger.Debug(
                $"Reactor chat composer: clipboard text paste failed: {ex.Message}");
        }
    }

    private static async Task PasteImageFromClipboardAsync(
        global::Windows.ApplicationModel.DataTransfer.DataPackageView clipboardContent,
        Action<ChatAttachment> onAttachmentPasted)
    {
        try
        {
            var attachment = await TryReadImageFromClipboardAsync(clipboardContent);
            if (attachment is not null)
                onAttachmentPasted(attachment);
        }
        catch (Exception ex)
        {
            OpenClawTray.Services.Logger.Debug(
                $"Reactor chat composer: clipboard image paste failed: {ex.Message}");
        }
    }

    private static string PlaceholderFor(string connectionState) => connectionState switch
    {
        "connected" => Localized("Chat_Composer_Placeholder_Connected", "Message Assistant (Enter to send)"),
        "connecting" => Localized("Chat_Composer_Placeholder_Connecting", "Connecting…"),
        "incompatible-gateway" => Localized(
            "Chat_Composer_Placeholder_IncompatibleGateway",
            "Gateway update required: incompatible version"),
        _ => Localized("Chat_Composer_Placeholder_NotConnected", "Not connected"),
    };

    private static string Localized(string key, string fallback)
    {
        var value = LocalizationHelper.GetString(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }
}

internal static class ComposerAutomationVisibility
{
    public static void Prepare(FrameworkElement control)
    {
        Detach(control);
        if (HasUsableLayout(control))
        {
            ApplyReadyState(control);
            return;
        }

        control.IsHitTestVisible = false;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
            control,
            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        control.Loaded += OnLoaded;
        control.SizeChanged += OnSizeChanged;
    }

    public static void Detach(FrameworkElement control)
    {
        control.Loaded -= OnLoaded;
        control.SizeChanged -= OnSizeChanged;
    }

    private static void OnLoaded(object sender, RoutedEventArgs args) =>
        TryEnableHitTesting(sender);

    private static void OnSizeChanged(object sender, SizeChangedEventArgs args) =>
        TryEnableHitTesting(sender);

    private static void TryEnableHitTesting(object sender)
    {
        if (sender is not FrameworkElement control || !HasUsableLayout(control))
            return;

        ApplyReadyState(control);
    }

    private static void ApplyReadyState(FrameworkElement control)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
            control,
            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Control);
        control.IsHitTestVisible = true;
        var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer
            .FromElement(control)
            ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer
                .CreatePeerForElement(control);
        peer?.RaisePropertyChangedEvent(
            Microsoft.UI.Xaml.Automation.AutomationElementIdentifiers.IsOffscreenProperty,
            true,
            false);
        Detach(control);
    }

    private static bool HasUsableLayout(FrameworkElement control) =>
        control.IsLoaded
        && control.Visibility == Visibility.Visible
        && control.ActualWidth > 0
        && control.ActualHeight > 0;
}
