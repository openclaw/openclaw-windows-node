using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static OpenClawTray.FunctionalUI.Factories;
using static OpenClawTray.FunctionalUI.Core.Theme;

namespace OpenClawTray.Chat;

/// <summary>
    /// FunctionalUI root component used to render the OpenClaw chat surface (Header
/// + Timeline + InputBar + StatusBar). The surrounding XAML window/page owns
/// session navigation (via the existing NavigationView/SessionsPage) so
/// no Sidebar is rendered here.
/// </summary>
public sealed class OpenClawChatRoot : Component
{
    private static bool s_showToolCalls = true;
    private static int s_toolCallsCollapseVersion;
    private static event EventHandler? ToolCallsVisibilityChanged;

    /// <summary>
    /// Sets whether tool-call / usage chips are shown in the chat timeline. This
    /// is the single writer for the tool-call visibility state now that the
    /// toggle lives in the Settings "Chat" section (previously a composer
    /// toggle). Bumps the collapse version when hiding so already-expanded tool
    /// chips collapse, updates the shared static, and notifies any mounted
    /// <see cref="OpenClawChatRoot"/> so its timeline re-renders.
    /// </summary>
    public static void SetToolCallsVisible(bool visible)
    {
        if (!visible && s_showToolCalls)
            s_toolCallsCollapseVersion++;
        s_showToolCalls = visible;
        ToolCallsVisibilityChanged?.Invoke(null, EventArgs.Empty);
    }

    private readonly IChatDataProvider _provider;
    private readonly string? _initialThreadId;
    private readonly Func<string, Task>? _onReadAloud;
    private readonly Action? _onStopSpeaking;
    private readonly Func<CancellationToken, Action?, Task<string?>>? _onVoiceRequest;
    private readonly Action? _onAttachClick;
    private readonly Action? _onSettingsClick;
    private readonly Action<bool>? _onSpeakerMuteChanged;
    private readonly Func<string, string?, Task<bool>>? _confirmResetAsync;
    private readonly bool _initialMuted;
    private readonly bool _isCompact;
    private Action<IReadOnlyList<ChatAttachment>>? _onFilesAttached;
    private Action<string?>? _setVoiceTranscript;
    private Action<float>? _setVoiceAudioLevel;
    private Action? _scrollToBottomToken;
    private Action<string>? _selectThread;
    private string? _pendingSelectedThreadId;
    /// <summary>
    /// Programmatically start voice recording from outside the composer.
    /// Set by the composer during render.
    /// </summary>
    public Action? TriggerVoiceRecording { get; set; }

    /// <summary>
    /// Push mute state from outside (e.g. when another chat view toggles mute).
    /// Set by render.
    /// </summary>
    public Action<bool>? SetSpeakerMuted { get; set; }

    /// <summary>
    /// Callback invoked by the host window/page after a file is selected.
    /// Appends pending attachments and triggers a re-render.
    /// </summary>
    public Action<IReadOnlyList<ChatAttachment>>? OnFilesAttached
    {
        get => _onFilesAttached;
        set => _onFilesAttached = value;
    }

    /// <summary>
    /// Push streaming voice transcript text into the composer UI.
    /// Set to null when recording stops to clear the display.
    /// </summary>
    public Action<string?>? SetVoiceTranscript
    {
        get => _setVoiceTranscript;
        set => _setVoiceTranscript = value;
    }

    /// <summary>
    /// Push the current audio input level (0.0–1.0) into the composer UI.
    /// </summary>
    public Action<float>? SetVoiceAudioLevel
    {
        get => _setVoiceAudioLevel;
        set => _setVoiceAudioLevel = value;
    }

    public OpenClawChatRoot(
        IChatDataProvider provider,
        string? initialThreadId = null,
        Func<string, Task>? onReadAloud = null,
        Action? onStopSpeaking = null,
        Func<CancellationToken, Action?, Task<string?>>? onVoiceRequest = null,
        Action? onAttachClick = null,
        Action? onSettingsClick = null,
        Action<bool>? onSpeakerMuteChanged = null,
        Func<string, string?, Task<bool>>? confirmResetAsync = null,
        bool initialMuted = false,
        bool isCompact = false)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _initialThreadId = initialThreadId;
        _onReadAloud = onReadAloud;
        _onStopSpeaking = onStopSpeaking;
        _onVoiceRequest = onVoiceRequest;
        _onAttachClick = onAttachClick;
        _onSettingsClick = onSettingsClick;
        _onSpeakerMuteChanged = onSpeakerMuteChanged;
        _confirmResetAsync = confirmResetAsync;
        _initialMuted = initialMuted;
        _isCompact = isCompact;
    }

    public override Element Render()
    {
        var pendingAttachments = UseState<IReadOnlyList<ChatAttachment>>(Array.Empty<ChatAttachment>(), threadSafe: true);
        var pendingAttachmentsRef = UseRef<IReadOnlyList<ChatAttachment>>(pendingAttachments.Value);
        pendingAttachmentsRef.Current = pendingAttachments.Value;
        var speakerMuted = UseState(_initialMuted, threadSafe: true);
        var voiceTranscript = UseState<string?>(null, threadSafe: true);
        var voiceAudioLevel = UseState(0f, threadSafe: true);
        var scrollToBottomToken = UseState(0, threadSafe: true);
        var showToolCalls = UseState(s_showToolCalls, threadSafe: true);
        var toolCallsCollapseVersion = UseState(s_toolCallsCollapseVersion, threadSafe: true);
        var chatSurfaceHeight = UseState<double?>(null, threadSafe: true);
        // Guards a duplicate suggestion-button click before the snapshot
        // reflects the optimistic local user entry (which then ordinarily
        // hides the zero-state buttons via the isEmptyConversation check).
        // Cleared automatically when the next snapshot arrives.
        var firstSendInFlight = UseState(false, threadSafe: true);

        void SetPendingAttachments(IReadOnlyList<ChatAttachment> attachments)
        {
            pendingAttachmentsRef.Current = attachments;
            pendingAttachments.Set(attachments);
        }

        // Wire the attachment callback so the host window/page can append
        // pending attachments after the file picker completes.
        _onFilesAttached = attachments =>
        {
            if (attachments.Count == 0)
                return;

            SetPendingAttachments(pendingAttachmentsRef.Current.Concat(attachments).ToArray());
        };
        _setVoiceTranscript = voiceTranscript.Set;
        _setVoiceAudioLevel = voiceAudioLevel.Set;
        _scrollToBottomToken = () => scrollToBottomToken.Set(scrollToBottomToken.Value + 1);
        SetSpeakerMuted = muted => speakerMuted.Set(muted);
        var snapshotState = UseState<ChatDataSnapshot?>(null, threadSafe: true);
        var initialSelectedId = _initialThreadId ?? (_provider as OpenClawChatDataProvider)?.CachedLastChatState?.DefaultThreadId;
        var selectedIdState = UseState<string?>(initialSelectedId, threadSafe: true);
        // UseRef tracks the selected ID across renders so that closures captured
        // inside UseEffect always read the latest value (UseState structs go stale).
        var selectedIdRef = UseRef<string?>(initialSelectedId);
        selectedIdRef.Current = selectedIdState.Value;
        _selectThread = threadId =>
        {
            _pendingSelectedThreadId = threadId;
            selectedIdState.Set(threadId);
            selectedIdRef.Current = threadId;
        };

        UseEffect((Func<Action>)(() =>
        {
            EventHandler onToolCallsVisibilityChanged = (_, _) =>
            {
                showToolCalls.Set(s_showToolCalls);
                toolCallsCollapseVersion.Set(s_toolCallsCollapseVersion);
            };

            ToolCallsVisibilityChanged += onToolCallsVisibilityChanged;
            return () => ToolCallsVisibilityChanged -= onToolCallsVisibilityChanged;
        }));

        UseEffect((Func<Action>)(() =>
        {
            var setSnapshot = snapshotState.Set;
            var setSelected = selectedIdState.Set;

            EventHandler<ChatDataChangedEventArgs> onChanged = (_, e) =>
            {
                setSnapshot(e.Snapshot);
                // The debounce must clear only when the new snapshot is evidence
                // that the send round-trip has progressed for the compose key —
                // either the optimistic user entry landed (Timelines has it) or
                // an error event ended the turn. Clearing on every snapshot
                // (presence, models, status, channel health …) would re-enable
                // the suggestion buttons before the optimistic entry rendered
                // and let a double-click duplicate-send.
                if (e.Snapshot.ComposeTarget.SessionKey is { } ck &&
                    e.Snapshot.Timelines.TryGetValue(ck, out var ctl) &&
                    ctl.Entries.Any(x => x.Kind == ChatTimelineItemKind.User))
                {
                    firstSendInFlight.Set(false);
                }
                if (selectedIdRef.Current is null && e.Snapshot.DefaultThreadId is { } d)
                {
                    setSelected(d);
                    selectedIdRef.Current = d;
                }
            };
            _provider.Changed += onChanged;
            _ = LoadAsync(_provider, setSnapshot, () => selectedIdRef.Current, v => { setSelected(v); selectedIdRef.Current = v; });
            return () => _provider.Changed -= onChanged;
        }));

        var snapshot = snapshotState.Value;
        var selectedIdForMetadata = selectedIdState.Value ?? snapshot?.DefaultThreadId;
        var entryMetaSnapshot = UseMemo<IReadOnlyDictionary<string, ChatEntryMetadata>?>(() =>
        {
            if (selectedIdForMetadata is null)
                return null;

            return _provider switch
            {
                OpenClawChatDataProvider nativeForMeta => nativeForMeta.GetEntryMetadata(selectedIdForMetadata),
                _ => null
            };
        }, selectedIdForMetadata ?? string.Empty, snapshot is null ? string.Empty : snapshot);

        Element BuildLoadingElement()
        {
            return Border(
                VStack(8,
                    ProgressRing().Size(28, 28).HAlign(HorizontalAlignment.Center),
                    Caption(LocalizationHelper.GetString("Chat_Root_ConnectingToGateway")).Foreground(SecondaryText).HAlign(HorizontalAlignment.Center)
                ).VAlign(VerticalAlignment.Center).HAlign(HorizontalAlignment.Center)
            ).Background(new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent));
        }

        if (snapshot is null)
        {
            return BuildLoadingElement();
        }

        var selectedId = selectedIdState.Value ?? snapshot.DefaultThreadId;
        var selectedThread = selectedId is { } id
            ? Array.Find(snapshot.Threads, t => t.Id == id)
            : null;
        if (selectedThread is not null &&
            string.Equals(_pendingSelectedThreadId, selectedThread.Id, StringComparison.Ordinal))
        {
            _pendingSelectedThreadId = null;
        }
        if (selectedThread is null
            && selectedIdState.Value is { } staleSelectedId
            && snapshot.DefaultThreadId is { } fallbackThreadId
            && ChatLifecycleSelectionPolicy.ShouldFallback(
                staleSelectedId,
                _pendingSelectedThreadId,
                fallbackThreadId))
        {
            selectedId = fallbackThreadId;
            selectedThread = Array.Find(snapshot.Threads, t => t.Id == fallbackThreadId);
            selectedIdState.Set(fallbackThreadId);
            selectedIdRef.Current = fallbackThreadId;
        }

        // If no real session is selected yet but the provider exposes a ready
        // compose target (gateway connected + handshake snapshot resolved),
        // synthesize a transient compose-only ChatThread so the composer is
        // visible from the welcome screen. The synthetic thread's Id is the
        // canonical compose key — so when the gateway materializes the session
        // and SessionsUpdated arrives, Threads contains a real entry with the
        // same Id and `selectedThread` resolves to it on the next render
        // without any re-keying or migration.
        ChatThread? composeOnlyThread = null;
        var pendingComposeKey = ChatLifecycleSelectionPolicy.RetainPendingForSelection(
            _pendingSelectedThreadId,
            selectedIdState.Value);
        var composeKey = pendingComposeKey ??
            (snapshot.ComposeTarget.IsReady ? snapshot.ComposeTarget.SessionKey : null);
        if (selectedThread is null && composeKey is not null)
        {
            // Use last-known state from the data provider so the composer shows
            // the previous session title/model while reconnecting instead of
            // generic "Main session"/"model" placeholders.
            var lastState = (_provider as OpenClawChatDataProvider)?.CachedLastChatState;
            composeOnlyThread = new ChatThread
            {
                Id = composeKey,
                AgentId = snapshot.ComposeTarget.AgentId,
                Title = _pendingSelectedThreadId is not null
                    ? LocalizationHelper.GetString("Chat_PendingNewSessionTitle")
                    : lastState?.ThreadTitle ?? "OpenClaw Windows Tray",
                Model = lastState?.Model,
                ModelProvider = lastState?.ModelProvider,
                Status = ChatThreadStatus.Running,
                Activity = ChatActivity.Idle,
            };
        }

        // For everything below, `effectiveThread` is the thread the UI should
        // render against. `selectedThread` stays null when nothing materialized
        // exists yet so the zero-state still shows; `composeOnlyThread` exists
        // so the composer can be wired up.
        var effectiveThread = selectedThread ?? composeOnlyThread;
        var connectedRaw = snapshot.ConnectionStatus;
        var hostConnected = connectedRaw is not null
            && connectedRaw.StartsWith("Connected", StringComparison.OrdinalIgnoreCase);

        // Lazy-load history the first time a real (materialized) thread is
        // selected. Don't fire for the compose-only synthetic thread — it
        // doesn't exist server-side yet, so chat.history would 404. A
        // disconnected render must not replace a request canceled by the
        // connection-generation boundary.
        if (hostConnected &&
            selectedThread is not null &&
            _provider is OpenClawChatDataProvider native)
        {
            var threadId = selectedThread.Id;
            RunFireAndForget(ct => native.LoadHistoryAsync(threadId, force: false, ct));
        }

        // Pull the timeline from the effective thread (so optimistic entries
        // from a pre-materialization first send are visible immediately).
        var timeline = effectiveThread is not null && snapshot.Timelines.TryGetValue(effectiveThread.Id, out var tl)
            ? tl
            : ChatTimelineState.Initial();
        var timelineGeneration = 0L;
        if (effectiveThread is not null
            && snapshot.TimelineGenerations is { } generations
            && generations.TryGetValue(effectiveThread.Id, out var generation))
        {
            timelineGeneration = generation;
        }
        var queuedMessages = Array.Empty<ChatQueuedMessage>();
        if (effectiveThread is not null
            && snapshot.QueuedMessagesByThread is { } queuedByThread
            && queuedByThread.TryGetValue(effectiveThread.Id, out var queuedForThread))
        {
            queuedMessages = queuedForThread.ToArray();
        }
        var hasPendingQueuedSend = queuedMessages.Any(message =>
            message.SendState is ChatQueuedMessageSendState.Queued or ChatQueuedMessageSendState.Sending);

        var entries = (IReadOnlyList<ChatTimelineItem>)timeline.Entries;
        var connState = (connectedRaw is not null && connectedRaw.StartsWith("Incompatible", StringComparison.OrdinalIgnoreCase))
            ? "incompatible-gateway"
            : hostConnected ? "connected"
            : (connectedRaw is not null && connectedRaw.StartsWith("Connecting", StringComparison.OrdinalIgnoreCase))
                ? "connecting"
                : "disconnected";

        // Header & divider intentionally hidden — the surrounding chrome
        // (NavigationView page or tray popup TitleBar) already shows the
        // session title; the in-chat header just duplicates it.
        Element header = Empty();

        // Per-entry metadata for the OpenClaw timeline footer (sender · time · model).
        // Keep the same dictionary instance across composer-only renders so the
        // timeline can skip re-rendering while the user types.
        var entryMeta = effectiveThread is null ? null : entryMetaSnapshot;
        var usageSummary = showToolCalls.Value
            ? (ChatUsageFormatter.Format(entries, entryMeta)
                ?? ChatUsageFormatter.Format(effectiveThread))
            : null;

        // The gateway's default agent identity is "Field" (matches the web UI footer),
        // but for the WinUI tray we surface a generic "Assistant" label so the
        // thinking indicator and sender chip read naturally to all users.
        // TODO: wire to a real agent-name source (agents.list response or
        // sessionDefaults.defaultAgentId from hello-ok) once available, then
        // restore the per-agent name here.
        const string assistantSenderLabel = "Assistant";

        // Show inline "thinking" indicator only until this turn has an
        // assistant bubble. Tool calls can arrive before the first assistant
        // delta; those should nest under the thinking bubble instead of
        // suppressing it. Once an assistant entry exists in the current turn,
        // tool calls nest there and the thinking placeholder goes away.
        var currentTurnHasAssistant = false;
        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            var kind = timeline.Entries[i].Kind;
            if (kind == ChatTimelineItemKind.User)
                break;
            if (kind == ChatTimelineItemKind.Assistant)
            {
                currentTurnHasAssistant = true;
                break;
            }
        }
        var showThinking = timeline.TurnActive && !currentTurnHasAssistant;

        var pendingPermissionOverride = timeline.PendingPermission;
        var turnActiveOverride = timeline.TurnActive;

        // Production zero-state: triggered when a thread is selected
        // but has no messages yet (true "empty conversation"). We only
        // surface the welcome zero-state once we're confident the
        // conversation is genuinely empty — i.e. either the thread is
        // the synthetic compose-only thread (fresh install, no real
        // session yet) or `chat.history` has actually completed
        // (timeline.HistoryLoaded). For a real session whose history is
        // still being fetched, fall back to the reconnecting view so
        // the welcome screen doesn't flicker on top of an as-yet
        // unloaded timeline. See OpenClawChatDataProvider.HistoryLoaded
        // — set to true only inside LoadHistoryAsync's rebuild.
        // Note: `pendingPermissionOverride is null` is now redundant for
        // live data — the reducer's ApplyPermissionRequest pushes a
        // PermissionRequest timeline entry whenever PendingPermission is
        // set, so `entries.Count > 0` already covers that case.
        var isEmptyConversation = entries.Count == 0
            && !showThinking
            && pendingPermissionOverride is null;
        var isComposeOnlyThread = composeOnlyThread is not null
            && ReferenceEquals(effectiveThread, composeOnlyThread);
        var gatewayConnected = string.Equals(connState, "connected", StringComparison.Ordinal);
        // Raw eligibility: would we *otherwise* render the welcome zero-state
        // right now? We still need this signal to drive the settling effect
        // below, but the actual decision to render welcome is gated on
        // `welcomeSettledState` so a brief, race-driven eligibility window
        // (e.g. an empty sessions.list briefly arriving before the populated
        // one for a returning user) never flashes the suggestion buttons.
        //
        // Two distinct paths qualify for welcome:
        //   1. Fresh install — the synthetic compose-only thread is selected
        //      AND the snapshot truly has no real threads yet. If real
        //      threads exist but the compose-only thread is *briefly*
        //      selected during a session-switch race, we explicitly do NOT
        //      qualify — that's the case that previously flashed welcome
        //      on returning users.
        //   2. Returning user with an empty real session — a real thread is
        //      selected and its history has fully loaded (HistoryLoaded=true)
        //      but contains zero messages.
        var hasRealThreads = snapshot.Threads.Length > 0;
        var welcomeEligibleRaw = isEmptyConversation
            && gatewayConnected
            && (
                (isComposeOnlyThread && !hasRealThreads)
                || (!isComposeOnlyThread && timeline.HistoryLoaded)
            );

        // Settling debounce: only promote to "authoritative" once the
        // welcome-eligible signal has been stable for ~800ms. This protects
        // against transient mid-handshake windows where threads briefly
        // appear empty, ComposeTarget becomes ready, and the synthetic
        // compose-only thread otherwise tricks the renderer into showing the
        // suggestion buttons before the real session list lands. Fresh-
        // install users still see the welcome screen — just ~800ms after
        // connect — which is still well within perceived "loading" time.
        // 800ms (up from 300ms) absorbs gateway sequences where an empty
        // sessions.list precedes the populated one by several hundred ms.
        var welcomeSettledState = UseState<bool>(false);
        UseEffect((Func<Action>)(() =>
        {
            if (!welcomeEligibleRaw)
            {
                if (welcomeSettledState.Value) welcomeSettledState.Set(false);
                return () => { };
            }
            // Schedule the promote-to-settled call once the eligibility
            // window has been stable for the debounce interval. The hook
            // dependency key includes every input that influences the
            // welcome decision, so any change cancels the pending callback
            // via the returned cleanup before re-arming on the next pass.
            var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            var cancelled = false;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(800);
                    if (cancelled) return;
                    dq?.TryEnqueue(() => { if (!cancelled) welcomeSettledState.Set(true); });
                }
                catch (OperationCanceledException)
                {
                    // Cancellation is expected when the welcome eligibility signal changes.
                }
                catch (Exception ex) { OpenClawTray.Services.Logger.Debug($"ChatRoot: welcome settle race: {ex.Message}"); }
            });
            return () => { cancelled = true; };
        }),
            welcomeEligibleRaw,
            effectiveThread?.Id ?? string.Empty,
            isComposeOnlyThread,
            timeline.HistoryLoaded,
            hasRealThreads);

        var emptyConversationIsAuthoritative = welcomeEligibleRaw && welcomeSettledState.Value;

        Element body;
        var bodyIsSkeleton = false;
        if (effectiveThread is null)
        {
            // Pre-connect window: no real session and no compose target
            // ready yet. Skip the welcome zero-state so returning users
            // don't get the prompt suggestion screen while the node is
            // still connecting. Show skeleton placeholders instead of a
            // spinner so the surface visually resembles the chat that
            // will land in a moment.
            body = RenderSkeletonTimeline();
            bodyIsSkeleton = true;
        }
        else if (isEmptyConversation && !emptyConversationIsAuthoritative)
        {
            // Real session selected but its history hasn't finished
            // loading yet. Render skeleton message bubbles so the user
            // sees the chat's structural shape forming up; the real
            // entries replace the skeleton once chat.history lands.
            body = RenderSkeletonTimeline();
            bodyIsSkeleton = true;
        }
        else if (isEmptyConversation)
        {
            body = RenderZeroState(suggestion =>
                {
                    if (firstSendInFlight.Value) return; // debounce double-click
                    if (effectiveThread is { } t)
                    {
                        firstSendInFlight.Set(true);
                        ObserveFireAndForget(SendSuggestionAsync(t.Id, t.Title, suggestion));
                    }
                }, suggestionsDisabled: firstSendInFlight.Value);
        }
        else
        {
            body = Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(new(
                SessionId: effectiveThread.Id,
                Entries: entries,
                HasMoreHistory: false,
                OnLoadMoreHistory: null,
                EntryMetadata: entryMeta,
                TimelineGeneration: timelineGeneration,
                UserSenderLabel: "OpenClaw Windows Tray",
                AssistantSenderLabel: assistantSenderLabel,
                DefaultModel: effectiveThread.Model,
                DefaultUsageSummary: usageSummary,
                ShowThinkingIndicator: showThinking,
                ShowToolCalls: showToolCalls.Value,
                ToolCallsCollapseVersion: toolCallsCollapseVersion.Value,
                OnReadAloud: _onReadAloud is not null
                    ? (text => _onReadAloud(text))
                    : null,
                OnStopSpeaking: _onStopSpeaking,
                ScrollToBottomToken: scrollToBottomToken.Value,
                OnPermissionResponse: (rid, action) => OnPermission(effectiveThread.Id!, rid, action)));
        }

        // Session list for the composer dropdown — grouped by the Gateway's
        // agent presentation metadata. Background sessions stay hidden unless
        // the user explicitly navigated to one, in which case it remains usable.
        // Exclude ended sessions (completed/failed/killed/timeout) from the picker.
        var channelGroups = SessionVisibilityFilter.VisibleChatPickerThreads(snapshot.Threads)
            .Where(t => !string.IsNullOrEmpty(t.Title)
                     && t.IsVisibleInSessionPicker(effectiveThread?.Id))
            .GroupBy(t => string.IsNullOrWhiteSpace(t.AgentId) ? "other" : t.AgentId!)
            // "main" first (sort key 0), then alphabetical
            .OrderBy(g => g.Key.Equals("main", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ChannelGroup(
                AgentLabel: g.Key.Length > 0 ? char.ToUpperInvariant(g.Key[0]) + g.Key[1..] : "Unknown",
                Sessions: g.Select(t => (Id: t.Id, Title: t.Title!, Model: t.Model, ModelProvider: t.ModelProvider)).ToArray()))
            .ToArray();

        // If the compose-only synthetic thread isn't represented in any group
        // (e.g. fresh install: the gateway has no real sessions yet), inject a
        // single-entry "Main" group so the composer's channel combo isn't blank.
        //
        // Keep routing ids opaque here too. The provider carries the Gateway's
        // agent identity separately, including for compose-only threads.
        // Don't inject ended sessions into the synthetic group.
        if (effectiveThread is not null
            && SessionVisibilityFilter.IsVisibleInChatPicker(effectiveThread)
            && !ChannelGroupsContain(channelGroups, effectiveThread.Id))
        {
            var agentId = string.IsNullOrWhiteSpace(effectiveThread.AgentId) ? "main" : effectiveThread.AgentId!;
            var agentLabel = agentId.Length > 0 ? char.ToUpperInvariant(agentId[0]) + agentId[1..] : "Main";
            var syntheticGroup = new ChannelGroup(
                AgentLabel: agentLabel,
                Sessions: new[] { (Id: effectiveThread.Id!, Title: effectiveThread.Title ?? "OpenClaw Windows Tray", Model: effectiveThread.Model, ModelProvider: effectiveThread.ModelProvider) });

            var augmented = new ChannelGroup[channelGroups.Length + 1];
            augmented[0] = syntheticGroup;
            Array.Copy(channelGroups, 0, augmented, 1, channelGroups.Length);
            channelGroups = augmented;
        }

        Element composer = effectiveThread is { } composerThread
            ? Component<OpenClawComposer, OpenClawComposerProps>(new(
                ConnectionState: connState,
                TurnActive: turnActiveOverride,
                ChannelLabel: composerThread.Title ?? "OpenClaw Windows Tray",
                ChannelId: composerThread.Id!,
                AvailableChannels: channelGroups,
                AvailableModels: snapshot.AvailableModels,
                CurrentModel: composerThread.Model,
                CurrentModelProvider: composerThread.ModelProvider,
                CurrentThinkingLevel: composerThread.ThinkingLevel,
                MessageOptionsDisabled: turnActiveOverride || hasPendingQueuedSend,
                ModelChoices: snapshot.ModelChoices,
                OnSend: async (msg, attachments) =>
                {
                    var accepted = await OnSend(
                        composerThread.Id!,
                        composerThread.Title,
                        msg,
                        attachments);
                    if (accepted)
                    {
                        SetPendingAttachments(ChatComposerSubmissionPolicy.RemoveSubmittedAttachments(
                            pendingAttachmentsRef.Current,
                            attachments));
                    }
                    return accepted;
                },
                OnStop: () => OnStop(composerThread.Id!),
                OnChannelChanged: id =>
                {
                    _pendingSelectedThreadId = ChatLifecycleSelectionPolicy.RetainPendingForSelection(
                        _pendingSelectedThreadId,
                        id);
                    selectedIdState.Set(id);
                    selectedIdRef.Current = id;
                    if (_provider is OpenClawChatDataProvider nativeProvider)
                        nativeProvider.RememberSelectedThread(id);
                },
                OnModelChanged: model => ObserveFireAndForget(_provider.SetModelAsync(composerThread.Id!, model)),
                OnModelCleared: () => ObserveFireAndForget(_provider.ClearModelAsync(composerThread.Id!)),
                OnThinkingLevelChanged: level => RunFireAndForget(ct => _provider.SetThinkingLevelAsync(composerThread.Id!, level, ct)),
                OnPermissionsChanged: allowAll => RunFireAndForget(ct => _provider.SetPermissionModeAsync(composerThread.Id!, allowAll, ct)),
                OnVoiceRequest: _onVoiceRequest,
                OnAttachClick: _onAttachClick,
                PendingAttachments: pendingAttachments.Value,
                QueuedMessages: queuedMessages,
                OnQueuedMessageCancel: queuedMessageId => RunFireAndForget(ct => _provider.CancelQueuedMessageAsync(composerThread.Id!, queuedMessageId, ct)),
                OnAttachmentRemoved: attachment => SetPendingAttachments(RemoveAttachment(pendingAttachmentsRef.Current, attachment)),
                IsSpeakerMuted: speakerMuted.Value,
                OnSpeakerToggle: () =>
                {
                    var newMuted = !speakerMuted.Value;
                    speakerMuted.Set(newMuted);
                    _onSpeakerMuteChanged?.Invoke(newMuted);
                },
                OnSettingsClick: _onSettingsClick,
                VoiceTranscript: voiceTranscript.Value,
                VoiceAudioLevel: voiceAudioLevel.Value,
                RegisterVoiceStarter: starter => TriggerVoiceRecording = starter,
                OnAttachmentPasted: att => SetPendingAttachments(pendingAttachmentsRef.Current.Concat(new[] { att }).ToArray()),
                IsCompact: _isCompact,
                AvailableCommands: snapshot.AvailableCommands,
                CommandsSupported: snapshot.CommandsSupported,
                OnCommandsRequested: () => RunFireAndForget(ct => _provider.EnsureCommandCatalogAsync(ct)),
                AvailableHeight: chatSurfaceHeight.Value))
            : (bodyIsSkeleton ? RenderSkeletonComposer() : Empty());

        var divider = Empty();
        // Composer absorbs the old StatusBar.
        void SetChatSurfaceHeight(double height)
        {
            if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0)
                return;

            chatSurfaceHeight.Set(Math.Round(height));
        }

        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto],
            header.Grid(row: 0, column: 0),
            divider.Grid(row: 1, column: 0),
            body.Grid(row: 2, column: 0),
            composer.Grid(row: 3, column: 0)
        ).OnMount(root =>
        {
            SetChatSurfaceHeight(root.ActualHeight);
            root.SizeChanged += (_, e) => SetChatSurfaceHeight(e.NewSize.Height);
        });
    }

    // Cheap allocation-free probe for "does any group contain a session with
    // the given id?" — avoids the LINQ Any().Any() allocation in the render
    // hot path.
    private static bool ChannelGroupsContain(ChannelGroup[] groups, string id)
    {
        foreach (var g in groups)
        {
            foreach (var s in g.Sessions)
            {
                if (s.Id == id) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Skeleton timeline shown in place of the welcome zero-state and the
    /// snapshot-null loading screen while the gateway/node handshake is in
    /// flight or <c>chat.history</c> is still being fetched. Renders a
    /// short stack of muted, message-shaped placeholder bubbles that
    /// alternate left/right alignment so the surface visually resembles
    /// the timeline that will replace it once entries arrive. A returning
    /// user therefore sees a clearly intentional "messages are loading"
    /// affordance instead of either the first-launch prompt suggestions or
    /// a centered spinner that has no relationship to the chat structure.
    /// Uses a fixed 8px bubble corner radius so the skeleton matches the
    /// composer placeholder; this is loading chrome, not the live timeline.
    /// </summary>
    private static Element RenderSkeletonTimeline()
    {
        // Two-tier palette: a softer "bubble" fill and a marginally stronger
        // "text line" stripe so each bubble reads as a real message with
        // text inside. Both lean subtle — the line alpha is ~20%, the bubble
        // ~22%, so the placeholders read on light/dark/acrylic without
        // competing with the real timeline's visual weight.
        var bubbleBrush = (Microsoft.UI.Xaml.Media.Brush)new Microsoft.UI.Xaml.Media.SolidColorBrush(
            global::Windows.UI.Color.FromArgb(0x38, 0x80, 0x80, 0x80));
        var lineBrush = (Microsoft.UI.Xaml.Media.Brush)new Microsoft.UI.Xaml.Media.SolidColorBrush(
            global::Windows.UI.Color.FromArgb(0x35, 0x80, 0x80, 0x80));

        Element Line(double width) =>
            Border()
                .Background(lineBrush)
                .Set(b =>
                {
                    b.CornerRadius = new CornerRadius(4);
                    b.Width = width;
                    b.Height = 8;
                    b.HorizontalAlignment = HorizontalAlignment.Left;
                });

        // Bubble with N subtle text-line stripes inside. lineWidths drives
        // both line count and width variation so each bubble reads like a
        // different message length. phaseMs staggers the shimmer pulse so
        // the bubbles breathe one after another instead of in unison.
        Element Bubble(double[] lineWidths, HorizontalAlignment align, double phaseMs)
        {
            var lines = new Element?[lineWidths.Length];
            for (int i = 0; i < lineWidths.Length; i++) lines[i] = Line(lineWidths[i]);
            return Border(
                VStack(8, lines)
            ).Background(bubbleBrush)
             .Set(b =>
             {
                 b.CornerRadius = new CornerRadius(8);
                 b.Padding = new Thickness(16, 12, 16, 12);
                 b.HorizontalAlignment = align;
                 b.Margin = new Thickness(16, 8, 16, 8);
             })
             .OnMount(MakeShimmer(phaseMs));
        }

        return Border(
            VStack(0,
                Bubble(new[] { 240.0, 180.0 }, HorizontalAlignment.Left, 0),
                Bubble(new[] { 140.0 }, HorizontalAlignment.Right, 140),
                Bubble(new[] { 280.0, 240.0, 160.0 }, HorizontalAlignment.Left, 280),
                Bubble(new[] { 120.0 }, HorizontalAlignment.Right, 420),
                Bubble(new[] { 200.0 }, HorizontalAlignment.Left, 560)
            )
        ).Set(b => b.Padding = new Thickness(0, 16, 0, 16));
    }

    /// <summary>
    /// Skeleton composer shown at the bottom of the chat surface while the
    /// real composer is still gated. Renders a rounded input-field placeholder
    /// and a circular send-button placeholder, both pulsing in sync with the
    /// skeleton bubbles above. Keeps the overall chat surface visually intact
    /// so the layout doesn't shift when the real composer lands.
    /// </summary>
    private static Element RenderSkeletonComposer()
    {
        var bubbleBrush = (Microsoft.UI.Xaml.Media.Brush)new Microsoft.UI.Xaml.Media.SolidColorBrush(
            global::Windows.UI.Color.FromArgb(0x30, 0x80, 0x80, 0x80));

        var inputField = Border()
            .Background(bubbleBrush)
            .Set(b =>
            {
                b.CornerRadius = new CornerRadius(8);
                b.Height = 56;
                b.Margin = new Thickness(0, 0, 8, 0);
                b.HorizontalAlignment = HorizontalAlignment.Stretch;
            })
            .OnMount(MakeShimmer(0));

        var sendButton = Border()
            .Background(bubbleBrush)
            .Set(b =>
            {
                b.CornerRadius = new CornerRadius(20);
                b.Width = 40;
                b.Height = 40;
                b.VerticalAlignment = VerticalAlignment.Center;
            })
            .OnMount(MakeShimmer(160));

        return Border(
            Grid(new[] { GridSize.Star(), GridSize.Auto }, new[] { GridSize.Auto },
                inputField.Grid(row: 0, column: 0),
                sendButton.Grid(row: 0, column: 1)
            )
        ).Set(b => b.Padding = new Thickness(16, 8, 16, 16));
    }

    /// <summary>
    /// Builds an OnMount action that attaches a Storyboard-driven opacity
    /// pulse to the target element. <paramref name="beginOffsetMs"/> staggers
    /// the pulse phase so multiple bubbles wave in sequence rather than
    /// blinking in unison. The animation auto-reverses and repeats forever;
    /// the storyboard is dropped from scope once started but kept alive by
    /// the visual tree via its target ref.
    /// </summary>
    private static Action<FrameworkElement> MakeShimmer(double beginOffsetMs)
    {
        return fe =>
        {
            try
            {
                var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = 1.0,
                    To = 0.45,
                    Duration = new Duration(TimeSpan.FromMilliseconds(900)),
                    AutoReverse = true,
                    RepeatBehavior = Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever,
                    EasingFunction = new Microsoft.UI.Xaml.Media.Animation.SineEase
                    {
                        EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseInOut,
                    },
                    BeginTime = TimeSpan.FromMilliseconds(beginOffsetMs),
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, fe);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Opacity");
                var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                sb.Children.Add(anim);
                sb.Begin();
            }
            catch (Exception ex)
            {
                OpenClawTray.Services.Logger.Debug($"ChatRoot: skeleton storyboard animation failed (non-essential): {ex.Message}");
            }
        };
    }

    /// <summary>
    /// Unified zero-state for the chat surface — shown when there is no
    /// thread selected OR the selected thread has no messages yet. Renders
    /// the app icon, a welcome message, and three prompt suggestions that
    /// invoke <paramref name="onSuggestionPicked"/> when clicked. The caller
    /// is responsible for routing the suggestion text into a send (typically
    /// via the active thread's OnSend handler).
    /// </summary>
    private static Element RenderZeroState(Action<string> onSuggestionPicked, bool suggestionsDisabled = false)
    {
        var welcomeTitle = LocalizedOrDefault("Chat_ZeroState_WelcomeTitle", "Welcome to OpenClaw");
        var welcomeSubtitle = LocalizedOrDefault("Chat_ZeroState_WelcomeSubtitle", "How can I help you today?");

        var suggestions = new[]
        {
            "Say hi 👋",
            "What can you do?",
            "Give me a quick tour of OpenClaw",
        };

        Element SuggestionButton(string text) =>
            Button(text, () => onSuggestionPicked(text))
                .Set(b =>
                {
                    b.HorizontalAlignment = HorizontalAlignment.Stretch;
                    b.HorizontalContentAlignment = HorizontalAlignment.Left;
                    b.Padding = new Thickness(12, 10, 12, 10);
                    b.CornerRadius = new CornerRadius(8);
                    b.IsEnabled = !suggestionsDisabled;
                });

        return Border(
            VStack(12,
                Image("ms-appx:///Assets/Square44x44Logo.targetsize-256_altform-unplated.png")
                    .Set(im =>
                    {
                        im.Width = 64;
                        im.Height = 64;
                        im.Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform;
                        im.HorizontalAlignment = HorizontalAlignment.Center;
                    }),
                TextBlock(welcomeTitle)
                    .Set(t =>
                    {
                        t.FontSize = 20;
                        t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                        t.HorizontalAlignment = HorizontalAlignment.Center;
                    }),
                Caption(welcomeSubtitle).Foreground(SecondaryText).HAlign(HorizontalAlignment.Center),
                VStack(6,
                    SuggestionButton(suggestions[0]),
                    SuggestionButton(suggestions[1]),
                    SuggestionButton(suggestions[2])
                ).Set(s =>
                {
                    s.HorizontalAlignment = HorizontalAlignment.Stretch;
                    s.MaxWidth = 360;
                    s.Margin = new Thickness(0, 8, 0, 0);
                })
            ).VAlign(VerticalAlignment.Center).HAlign(HorizontalAlignment.Center)
        ).Padding(24, 24, 24, 24);
    }

    private static string LocalizedOrDefault(string key, string fallback)
    {
        var value = LocalizationHelper.GetString(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }

    private static Element PlaceholderEmptyThreadState(string connectionState)
    {
        var isConnected = string.Equals(connectionState, "connected", StringComparison.Ordinal);
        var msg = isConnected
            ? "Start a new OpenClaw chat from the composer below."
            : LocalizationHelper.GetString("Chat_Root_ConnectingToGateway");

        return Border(
            VStack(8,
                TextBlock("💬").FontSize(48).HAlign(HorizontalAlignment.Center),
                Caption(msg).Foreground(SecondaryText).HAlign(HorizontalAlignment.Center)
            ).VAlign(VerticalAlignment.Center).HAlign(HorizontalAlignment.Center)
        );
    }

    private async Task<bool> OnSend(
        string threadId,
        string? displayName,
        string message,
        IReadOnlyList<ChatAttachment> attachments)
    {
        _scrollToBottomToken?.Invoke();
        if (_provider is OpenClawChatDataProvider native &&
            ChatLifecycleCommandParser.TryParse(message, attachments.Count > 0, out var command))
        {
            if (ChatLifecycleCommandExecutionPolicy.ShouldQueue(command))
                return await native.EnqueueCompactCommandAsync(threadId);

            if (command == ChatLifecycleCommandKind.Reset &&
                _confirmResetAsync is not null &&
                !await _confirmResetAsync(threadId, displayName))
            {
                return false;
            }

            var result = await native.ExecuteLifecycleCommandAsync(threadId, command);
            if (result.Succeeded && result.NewSessionKey is { } newSessionKey)
                _selectThread?.Invoke(newSessionKey);
            return result.Succeeded;
        }

        try
        {
            if (attachments.Count > 0)
                await _provider.SendMessageAsync(threadId, message, CancellationToken.None, attachments.ToArray());
            else
                await _provider.SendMessageAsync(threadId, message);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[chat] send failed: {ex}");
            return false;
        }
        return true;
    }

    private async Task SendSuggestionAsync(string threadId, string? displayName, string suggestion)
    {
        await OnSend(threadId, displayName, suggestion, Array.Empty<ChatAttachment>());
    }

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

        return removed ? next.ToArray() : attachments;
    }

    private void OnStop(string threadId)
    {
        RunFireAndForget(ct => _provider.StopResponseAsync(threadId, ct));
    }

    private void OnPermission(string threadId, string requestId, string action)
    {
        RunFireAndForget(ct => _provider.RespondToPermissionAsync(threadId, requestId, action, ct));
    }

    private static void RunFireAndForget(Func<CancellationToken, Task> op)
    {
        _ = Task.Run(async () =>
        {
            try { await op(CancellationToken.None); }
            // slopwatch-ignore: SW003 Shutdown cancellation or disposal is expected and the caller already preserves the safe state.
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[chat] op failed: {ex}"); }
        });
    }

    private static void ObserveFireAndForget(Task task)
    {
        _ = ObserveAsync(task);

        static async Task ObserveAsync(Task task)
        {
            try { await task; }
            // slopwatch-ignore: SW003 Shutdown cancellation or disposal is expected and the caller already preserves the safe state.
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[chat] op failed: {ex}"); }
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
            var snap = await provider.LoadAsync();
            setSnapshot(snap);
            if (getSelected() is null && snap.DefaultThreadId is { } d)
                setSelected(d);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[chat] LoadAsync failed: {ex}");
        }
    }
}
