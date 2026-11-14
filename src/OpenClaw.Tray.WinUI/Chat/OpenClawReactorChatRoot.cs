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
    ChatComposerSession ComposerSession,
    string? InitialThreadId = null,
    Func<string, Task>? OnReadAloud = null,
    Action? OnStopSpeaking = null,
    Action<string>? OnOpenCheckpoints = null,
    Func<string, bool>? TryCopyText = null,
    bool InitialThreadIsExplicitSelection = true,
    IChatAgentRosterSource? AgentRosterSource = null,
    string UserDisplayName = "",
    ImageSource? UserProfilePicture = null,
    bool Compact = false)
{
    /// <summary>Optional session dashboard source for the conversation side panel.</summary>
    internal ChatDashboardBinding? Dashboards { get; init; }
}

/// <summary>
/// Production Reactor root for the native chat surface. It owns the provider
/// subscription and renders the message timeline and composer in one tree.
/// </summary>
public sealed class OpenClawReactorChatRoot : Component<OpenClawReactorChatRootProps>
{
    private static bool s_showToolCalls = true;
    private static bool s_showChatThinking = true;
    private static int s_toolCallsCollapseVersion;
    private static event EventHandler? ToolCallsVisibilityChanged;

    private string? _pendingSelectedThreadId;
    private bool _initialSelectionApplied;
    private readonly ChatConversationPanelState _panels = new();
    private readonly ChatSessionFilesState _files = new();

    public static void SetToolCallsVisible(bool visible)
    {
        if (s_showToolCalls == visible)
            return;

        if (!visible && s_showToolCalls)
            s_toolCallsCollapseVersion++;

        s_showToolCalls = visible;
        ToolCallsVisibilityChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Applies the Settings "Show thinking" preference (web <c>chatShowThinking</c>) to every
    /// live chat host. It gates supplied reasoning only; run progress stays visible.
    /// </summary>
    public static void SetThinkingVisible(bool visible)
    {
        if (s_showChatThinking == visible)
            return;

        s_showChatThinking = visible;
        ToolCallsVisibilityChanged?.Invoke(null, EventArgs.Empty);
    }

    public override Element Render()
    {
        var props = Props;
        var (snapshot, setSnapshot) = UseState<ChatDataSnapshot?>(null, threadSafe: true);
        var latestSnapshot = UseRef<ChatDataSnapshot?>(null);
        var (_, setPanelRevision) = UseState(0L, threadSafe: true);
        UseEffect((Func<Action>)(() =>
        {
            long revision = 0;
            var native = props.Provider as OpenClawChatDataProvider;
            void Invalidated(object? sender, EventArgs args) => setPanelRevision(Interlocked.Increment(ref revision));
            if (native is not null) native.ToolDetailsInvalidated += Invalidated;
            return () =>
            {
                if (native is not null) native.ToolDetailsInvalidated -= Invalidated;
                _panels.Invalidate();
                _files.Bind(null, 0, 0, 0, false, null);
            };
        }), props.Provider);
        var dashboardBinding = props.Dashboards;
        var (_, setDashboardRevision) = UseState(0L, threadSafe: true);
        UseEffect((Func<Action>)(() =>
        {
            if (dashboardBinding is null) return static () => { };
            long revision = 0;
            void Changed() => setDashboardRevision(Interlocked.Increment(ref revision));
            dashboardBinding.View.Changed += Changed;
            dashboardBinding.PendingOpenChanged += Changed;
            return () =>
            {
                dashboardBinding.View.Changed -= Changed;
                dashboardBinding.PendingOpenChanged -= Changed;
            };
        }), (object?)dashboardBinding ?? this);
        var (viewportWidth, setViewportWidth) = UseState(800d);
        var (viewportHeight, setViewportHeight) = UseState(620d);
        var (collapsedQuestionId, setCollapsedQuestionId) = UseState<string?>(null);
        var questionsOwner = (props.Provider as OpenClawChatDataProvider)?.Questions;
        var (questionSnapshot, setQuestionSnapshot) = UseState(questionsOwner?.Snapshot(), threadSafe: true);
        UseEffect((Func<Action>)(() =>
        {
            if (questionsOwner is null)
            {
                setQuestionSnapshot(null);
                return static () => { };
            }
            EventHandler changed = (_, _) => setQuestionSnapshot(questionsOwner.Snapshot());
            questionsOwner.Changed += changed;
            setQuestionSnapshot(questionsOwner.Snapshot());
            return () => questionsOwner.Changed -= changed;
        }), questionsOwner);
        var initialSelection = props.InitialThreadId
            ?? (props.Provider as OpenClawChatDataProvider)?.CachedLastChatState?.DefaultThreadId;
        if (!_initialSelectionApplied)
        {
            _pendingSelectedThreadId = props.InitialThreadIsExplicitSelection ? props.InitialThreadId : null;
            _initialSelectionApplied = true;
        }
        var (selectedId, setSelectedId) = UseState<string?>(initialSelection, threadSafe: true);
        var selectedIdRef = UseRef<string?>(initialSelection);
        selectedIdRef.Current = selectedId;
        var (scrollToBottomToken, setScrollToBottomToken) = UseState(0, threadSafe: true);
        var (showToolCalls, setShowToolCalls) = UseState(s_showToolCalls, threadSafe: true);
        var (showChatThinking, setShowChatThinking) = UseState(s_showChatThinking, threadSafe: true);
        var (toolCallsCollapseVersion, setToolCallsCollapseVersion) =
            UseState(s_toolCallsCollapseVersion, threadSafe: true);
        var (firstSendInFlight, setFirstSendInFlight) = UseState(false, threadSafe: true);

        UseEffect((Func<Action>)(() =>
        {
            EventHandler visibilityChanged = (_, _) =>
            {
                setShowToolCalls(s_showToolCalls);
                setShowChatThinking(s_showChatThinking);
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
                latestSnapshot.Current = args.Snapshot;
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
                value =>
                {
                    latestSnapshot.Current = value;
                    setSnapshot(value);
                },
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
            && !props.Compact
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

        var effectiveThread = selectedMaterializedThread
            ?? CreateComposeOnlyThread(props.Provider, snapshot, props.Compact ? selectedId : null);
        if (selectedMaterializedThread is { } selected && string.Equals(_pendingSelectedThreadId, selected.Id, StringComparison.Ordinal))
            _pendingSelectedThreadId = null;

        var connectionState = ToConnectionState(snapshot.ConnectionStatus);
        var isGatewayConnected = string.Equals(connectionState, "connected", StringComparison.Ordinal);
        var nativeProvider = props.Provider as OpenClawChatDataProvider;
        if (isGatewayConnected
            && selectedMaterializedThread is not null
            && nativeProvider is not null)
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
        var contextUsage = ChatUsageFormatter.ResolveContext(entries, entryMetadata, effectiveThread);
        var queuedMessages = effectiveThread is not null
            && snapshot.QueuedMessagesByThread?.TryGetValue(effectiveThread.Id, out var queued) == true
                ? queued
                : Array.Empty<ChatQueuedMessage>();
        var hasPendingQueuedSend = queuedMessages.Any(message =>
            message.SendState is ChatQueuedMessageSendState.Queued or ChatQueuedMessageSendState.Sending);
        var showThinking = timeline.TurnActive;
        var isEmptyConversation = entries.Count == 0 && queuedMessages.Count == 0
            && !showThinking && timeline.PendingPermission is null;
        var isComposeOnly = effectiveThread is not null && selectedMaterializedThread is null;
        var placementHistoryReady = isGatewayConnected
            && (isComposeOnly
                ? snapshot.ComposeTarget.IsReady
                    && string.Equals(snapshot.ComposeTarget.SessionKey, effectiveThread?.Id, StringComparison.Ordinal)
                : effectiveThread is not null
                    && snapshot.CurrentHistoryThreads?.Contains(effectiveThread.Id) == true);
        var welcomeEligible = isEmptyConversation
            && isGatewayConnected
            && (
                (isComposeOnly && snapshot.ComposeTarget.IsReady
                    && snapshot.ComposeTarget.SessionKey == effectiveThread?.Id)
                || (!isComposeOnly && timeline.HistoryLoaded));
        var welcomeEligibilityKey = welcomeEligible
            ? $"{effectiveThread?.Id}|{isComposeOnly}|{timeline.HistoryLoaded}"
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
        var historyFailed = isGatewayConnected
            && !isComposeOnly
            && effectiveThread is not null
            && snapshot.FailedHistoryThreads?.Contains(effectiveThread.Id) == true;
        var mode = historyFailed && isEmptyConversation
            ? ReactorChatTimelineMode.HistoryUnavailable
            : effectiveThread is null
                   || (isEmptyConversation && !emptyConversationIsAuthoritative)
            ? ReactorChatTimelineMode.Loading
            : isEmptyConversation
                ? ReactorChatTimelineMode.Empty
                : ReactorChatTimelineMode.Timeline;
        var dataProvider = props.Provider as OpenClawChatDataProvider;
        Func<string, ChatMediaContentInfo, CancellationToken, Task<AssistantMediaResolutionResult>>?
            mediaResolver = dataProvider is null ? null : dataProvider.ResolveAssistantMediaAsync;
        Func<string, ChatMediaContentInfo, CancellationToken, Task<AssistantFileDownloadStatus>>?
            fileDownloader = dataProvider is null ? null : dataProvider.DownloadAssistantFileAsync;
        var panelSession = effectiveThread?.Id;
        var panelConnection = nativeProvider?.ToolDetailsGeneration ?? 0;
        var panelProfile = nativeProvider?.ToolDetailsProfileState ?? default;
        bool PanelSourceCurrent() => selectedIdRef.Current == panelSession &&
            (nativeProvider is null || panelProfile.IsAvailable &&
                nativeProvider.ToolDetailsProfileState == panelProfile &&
                nativeProvider.ToolDetailsGeneration == panelConnection) &&
            latestSnapshot.Current is { } current &&
            string.Equals(ToConnectionState(current.ConnectionStatus), "connected", StringComparison.Ordinal) &&
            (nativeProvider is null || panelSession is not null && current.CurrentHistoryThreads?.Contains(panelSession) == true) &&
            (panelSession is null || (current.TimelineGenerations?.GetValueOrDefault(panelSession) ?? 0) == timelineGeneration &&
                (current.HistoryRevisions?.GetValueOrDefault(panelSession) ?? 0) == historyRevision);
        bool SubagentsSourceCurrent() => selectedIdRef.Current == panelSession &&
            (nativeProvider is null || nativeProvider.ToolDetailsGeneration == panelConnection) &&
            latestSnapshot.Current is { } current &&
            string.Equals(ToConnectionState(current.ConnectionStatus), "connected", StringComparison.Ordinal);
        _panels.Bind(props.Provider, panelSession, panelConnection, timelineGeneration, historyRevision,
            entries, PanelSourceCurrent, panelProfile.Revision, SubagentsSourceCurrent);
        var panelRevision = _panels.Revision;
        var dashboardTarget = isGatewayConnected && selectedMaterializedThread is { } boardThread
            ? new OpenClaw.Shared.Dashboards.BoardSessionTarget(boardThread.Id, boardThread.AgentId)
            : null;
        // Explicit opens may target a session missing from the chat list, such as an archived board.
        var explicitDashboardTarget = dashboardTarget ?? (isGatewayConnected && effectiveThread is { } openThread
            ? new OpenClaw.Shared.Dashboards.BoardSessionTarget(openThread.Id, openThread.AgentId)
            : null);
        var dashboardView = dashboardBinding?.View;
        UseEffect((Func<Action>)(() => dashboardBinding is null || dashboardTarget is null
                ? static () => { }
                : dashboardBinding.Watch(_panels, dashboardTarget)),
            (object?)dashboardTarget ?? this, (object?)dashboardView?.Client ?? this,
            dashboardView?.Connected ?? false, dashboardView?.ConnectionRevision ?? 0);
        UseEffect((Func<Action>)(() =>
        {
            if (explicitDashboardTarget is not null &&
                dashboardBinding?.TryTakePendingOpen(explicitDashboardTarget.SessionKey) == true)
                _panels.OpenDashboard(explicitDashboardTarget);
            return static () => { };
        }), (object?)explicitDashboardTarget ?? this, (object?)dashboardBinding?.PendingOpen ?? this);
        _files.Bind(isGatewayConnected && !isComposeOnly ? panelSession : null, panelConnection, timelineGeneration,
            historyRevision, timeline.TurnActive, nativeProvider is null ? null : nativeProvider.ListSessionFilesAsync);

        // One child list feeds the Subagents pill, its tab and the spawn rows in the timeline.
        var subagentChildren = isGatewayConnected && effectiveThread is not null
            ? snapshot.Threads.Where(child => child.IsSubagent && !child.IsArchived
                && child.Id != effectiveThread.Id && child.ParentSessionKey == effectiveThread.Id).ToArray()
            : Array.Empty<ChatThread>();
        var timelineProps = new ChatTimelinePresentationContext(
            effectiveThread?.Id,
            entries,
            effectiveThread is not null && snapshot.OlderHistoryThreads?.Contains(effectiveThread.Id) == true,
            dataProvider is not null && effectiveThread is { } historyThread
                ? () => dataProvider.LoadOlderHistoryAsync(historyThread.Id) : null,
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
                : null,
            mediaResolver,
            queuedMessages,
            props.ComposerSession.Controller.CancelQueuedMessage,
            ChatRunProgressProjection.Project(timeline,
                effectiveThread is not null && questionSnapshot?.Prompts.Any(prompt =>
                    prompt.Request.Status == "pending" && ChatQuestionCoordinator.Matches(
                        prompt.Request, effectiveThread.Id, effectiveThread.AgentId)) == true,
                awaitingSubagents: isGatewayConnected && effectiveThread?.WaitingForSubagents == true),
            fileDownloader,
            Session: effectiveThread,
            ShowReasoning: showChatThinking &&
                (effectiveThread?.ReasoningLevel ?? effectiveThread?.ReasoningDefault) == "on",
            ShowReasoningPreview: showChatThinking &&
                (effectiveThread?.ReasoningLevel ?? effectiveThread?.ReasoningDefault) is "on" or "stream",
            ReasoningStream: showChatThinking && timeline.TurnActive &&
                (timeline.RunUsage?.RunId is null || timeline.RunUsage.RunId == timeline.ReasoningStream?.RunId)
                    ? timeline.ReasoningStream : null,
            HistoryRevision: historyRevision,
            ToolOutputConnectionEpoch: dataProvider?.ToolOutputConnectionEpoch,
            LoadToolOutputAsync: dataProvider is null ? null : dataProvider.LoadToolOutputAsync,
            ToolDetailsProfileState: dataProvider?.ToolDetailsProfileState ?? default,
            ReadToolDetailsProfileState: dataProvider is null ? null : dataProvider.ReadToolDetailsProfileState,
            UserDisplayName: props.UserDisplayName,
            UserProfilePicture: props.UserProfilePicture,
            OnOpenToolDetails: _panels.IsReady ? request => _panels.Open(request, panelRevision) : null,
            ChildThreads: subagentChildren,
            OnOpenWorkspaceFile: nativeProvider is not null && _panels.AreSubagentsReady && panelSession is { } fileSession
                ? path => _panels.OpenFile(new(fileSession, path), panelRevision) : null,
            CanOpenSession: isGatewayConnected && panelSession is { } parentSession
                ? key => ChatToolRowLinks.IsOpenableSubagent(snapshot.Threads, parentSession, key)
                : null,
            OnOpenSession: isGatewayConnected
                ? key => props.ComposerSession.Controller.TrySelectSession(key) : null,
            OnOpenAssistantFile: nativeProvider is not null && _panels.AreSubagentsReady && panelSession is { } artifactSession
                ? media => _panels.OpenFile(new(artifactSession,
                    ChatAssistantMediaRenderer.DisplayName(media), media.Reference.ArtifactId, media.Reference.AgentId,
                    media.MimeType ?? media.Reference.MimeType, media.Reference.SizeBytes), panelRevision)
                : null,
            ActiveReasoningId: timeline.ActiveReasoningId);
        var identitySource = props.Provider as IChatAgentIdentitySource;
        var assistantAgentId = effectiveThread?.AgentId ?? "main";
        var avatarCacheRef = UseRef<AgentAvatarImageCache?>(null);
        var avatarCache = avatarCacheRef.Current ??= new AgentAvatarImageCache();
        UseEffect((Func<Action>)(() => () => avatarCache.Dispose()), avatarCache);
        var rosterSource = props.AgentRosterSource;
        var (rosterAgent, setRosterAgent) =
            UseState<ChatAgentPresentation?>(rosterSource?.FindAgent(assistantAgentId), threadSafe: true);
        UseEffect((Func<Action>)(() =>
        {
            if (rosterSource is null)
            {
                setRosterAgent(null);
                return static () => { };
            }

            void RefreshRoster()
            {
                avatarCache.Clear();
                setRosterAgent(rosterSource.FindAgent(assistantAgentId));
            }

            rosterSource.AgentRosterChanged += OnAgentRosterChanged;
            RefreshRoster();
            return () => rosterSource.AgentRosterChanged -= OnAgentRosterChanged;

            void OnAgentRosterChanged(object? sender, EventArgs args) => RefreshRoster();
        }), assistantAgentId, (object?)rosterSource ?? this);
        var selectedRosterAgent = string.Equals(rosterAgent?.AgentId, assistantAgentId, StringComparison.Ordinal)
            ? rosterAgent
            : null;
        var (loadedAssistantIdentity, setLoadedAssistantIdentity) =
            UseState<ChatAgentIdentity?>(null, threadSafe: true);
        // One Gateway identity serves the welcome header and transcript avatars for the current agent.
        UseEffect((Func<Action>)(() =>
        {
            if (!isGatewayConnected || identitySource is null || selectedRosterAgent is not null)
            {
                setLoadedAssistantIdentity(null);
                return static () => { };
            }

            var cancelled = false;
            _ = LoadIdentityAsync();
            return () => cancelled = true;

            async Task LoadIdentityAsync()
            {
                try
                {
                    var identity = await identitySource.LoadAgentIdentityAsync(assistantAgentId);
                    if (!cancelled)
                        setLoadedAssistantIdentity(identity);
                }
                catch (Exception ex)
                {
                    if (!cancelled)
                        OpenClawTray.Services.Logger.Warn($"[Chat] Agent identity unavailable: {ex.GetType().Name}");
                }
            }
        }), assistantAgentId, isGatewayConnected, (object?)identitySource ?? this, selectedRosterAgent ?? (object)this);
        var assistantIdentity = selectedRosterAgent?.ToChatAgentIdentity() ??
            (string.Equals(loadedAssistantIdentity?.AgentId, assistantAgentId, StringComparison.Ordinal)
                ? loadedAssistantIdentity
                : null);

        void SelectThread(string threadId)
        {
            if (selectedIdRef.Current != threadId) _panels.Invalidate();
            props.Dashboards?.CancelPendingOpenExcept(threadId);
            _pendingSelectedThreadId = threadId;
            selectedIdRef.Current = threadId;
            setSelectedId(threadId);
            if (!props.Compact && props.Provider is OpenClawChatDataProvider native)
                native.RememberSelectedThread(threadId);
        }

        // Bound once (idempotent) so the composer controller can hand a freshly
        // created "/new" session or Workspace selection back to the root's
        // selection state without the controller depending on Reactor state directly.
        props.ComposerSession.Controller.BindSelectionHandoff(SelectThread);

        Action<string>? onSuggestionPicked = null;
        if (mode == ReactorChatTimelineMode.Empty && effectiveThread is { } suggestionThread)
        {
            onSuggestionPicked = suggestion =>
            {
                if (firstSendInFlight)
                    return;

                setFirstSendInFlight(true);
                setScrollToBottomToken(scrollToBottomToken + 1);
                async Task SendSuggestionAsync()
                {
                    try
                    {
                        await props.ComposerSession.Controller.SendCoreAsync(
                            suggestionThread.Id, suggestionThread.Title, suggestion, Array.Empty<ChatAttachment>());
                    }
                    finally
                    {
                        setFirstSendInFlight(false);
                    }
                }
                ObserveFireAndForget(SendSuggestionAsync());
            };
        }

        var timelineElement = Component<ReactorChatTimeline, ReactorChatTimelineProps>(new(
            mode,
            timelineProps,
            OnOpenCheckpoints: props.OnOpenCheckpoints,
            HistoryRevision: historyRevision,
            TryCopyText: props.TryCopyText,
            HistoryFailed: historyFailed,
            OnRetryHistory: historyFailed && nativeProvider is not null && effectiveThread is { } retryThread
                ? () => RunFireAndForget(ct => nativeProvider.RetryHistoryAsync(retryThread.Id, ct))
                : null,
            AssistantIdentity: assistantIdentity,
            AssistantAgentId: assistantAgentId,
            AssistantCatalog: props.ComposerSession.Placement?.State.Catalog,
            AssistantAvatarImageSource: avatarCache,
            AssistantRosterAgent: selectedRosterAgent));

        var pendingQuestion = effectiveThread is null ? null : questionSnapshot?.Prompts.FirstOrDefault(prompt =>
            prompt.Request.Status == "pending"
            && ChatQuestionCoordinator.Matches(prompt.Request, effectiveThread.Id, effectiveThread.AgentId));
        var questionCollapsed = pendingQuestion is not null && collapsedQuestionId == pendingQuestion.Request.Id;
        var composerVisible = pendingQuestion is null || questionCollapsed;
        Element composerElement;
        if (effectiveThread is null)
        {
            composerElement = Empty();
        }
        else
        {
            var composerInputs = new ChatComposerInputs(
                ConnectionState: connectionState,
                TurnActive: timeline.TurnActive,
                CurrentThread: effectiveThread,
                AvailableModels: snapshot.AvailableModels,
                ModelChoices: snapshot.ModelChoices,
                MessageOptionsDisabled: timeline.TurnActive || hasPendingQueuedSend,
                QueuedMessages: queuedMessages,
                AvailableCommands: snapshot.AvailableCommands,
                CommandsSupported: snapshot.CommandsSupported,
                SessionModelCatalogEligible: selectedMaterializedThread is not null
                    && nativeProvider?.SupportsSessionScopedModelCatalog == true,
                ModelCatalogRevision: nativeProvider?.ModelCatalogRevision ?? 0)
            {
                IsVisible = composerVisible,
                ShowPlacementPickers = !props.Compact && placementHistoryReady && mode == ReactorChatTimelineMode.Empty,
                PlacementTargetIsEmpty = isEmptyConversation,
                PlacementHistoryReady = placementHistoryReady,
                PlacementTargetIsNew = isComposeOnly,
                AgentIdentity = assistantIdentity,
                AgentRosterPresentation = selectedRosterAgent,
                ContextUsage = contextUsage
            };
            composerElement = Component<ReactorChatComposer, ReactorChatComposerViewProps>(new(
                props.ComposerSession,
                composerInputs,
                snapshot,
                () => setScrollToBottomToken(scrollToBottomToken + 1),
                Compact: props.Compact));
        }

        var questions = props.Provider is OpenClawChatDataProvider { Questions: { } questionCoordinator }
            && effectiveThread is not null
                ? Component<ReactorQuestionPanel, ReactorQuestionPanelProps>(
                    new(questionCoordinator, effectiveThread.Id, effectiveThread.AgentId, viewportWidth, viewportHeight,
                        collapsed => setCollapsedQuestionId(collapsed ? pendingQuestion?.Request.Id : null), questionCollapsed))
                : Empty();
        var showWelcome = !props.Compact && mode == ReactorChatTimelineMode.Empty;
        // Keep the composer at the same tree position across the first send. Moving
        // the control into a different parent would lose focus, caret and flyouts.
        var composerArea = ScrollViewer(
            Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto],
                composerElement.WithKey("chat-composer").Grid(row: 0),
                showWelcome
                    ? ReactorChatWelcome.Secondary(
                            ChatWelcomePresentation.RecentChats(snapshot.Threads, effectiveThread?.Id, effectiveThread?.AgentId),
                            key => props.ComposerSession.Controller.TrySelectSession(key), onSuggestionPicked, firstSendInFlight)
                        .Margin(ChatVisuals.Gutter(viewportWidth), 12, ChatVisuals.Gutter(viewportWidth), 24)
                        .Grid(row: 1)
                    : Empty().Grid(row: 1))
                .Margin(0, showWelcome ? 24 : 0, 0, 0)
                .VAlign(showWelcome ? VerticalAlignment.Center : VerticalAlignment.Top))
            .Set(scroll =>
            {
                scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                scroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                scroll.VerticalContentAlignment = showWelcome ? VerticalAlignment.Center : VerticalAlignment.Top;
            })
            .MaxHeight(props.Compact ? 260 : double.PositiveInfinity)
            .IsVisible(composerVisible)
            .AutomationId("ChatComposerArea")
            .WithKey("chat-composer-area");
        var conversation = Grid(
            [GridSize.Star()],
            [showWelcome ? GridSize.Auto : props.Compact ? (isEmptyConversation ? GridSize.Auto : GridSize.Star()) : GridSize.Star(),
                GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Auto, showWelcome ? GridSize.Star() : GridSize.Auto],
            showWelcome || (props.Compact && isEmptyConversation)
                ? Empty().Grid(row: 0)
                : props.Compact
                    ? Border(timelineElement).Grid(row: 0)
                    : timelineElement.Grid(row: 0),
            Border(questions).WithKey("chat-question-area").Grid(row: 1),
            ReactorChatPendingMessages.Render(timelineProps, viewportWidth, viewportHeight)
                .WithKey("chat-pending-messages").Grid(row: 2),
            Component<ReactorChatProviderReview, ReactorChatProviderReviewProps>(
                    new(isGatewayConnected ? effectiveThread?.ProviderReview : null, viewportWidth))
                .WithKey("chat-provider-review").Grid(row: 3),
            // Panel pills share one reading-width row; each pill hides itself when it has nothing to open.
            Border(HStack(6,
                    Component<ReactorChatSubagentsPill, ReactorChatSubagentsPillProps>(new(_panels, subagentChildren))
                        .WithKey("chat-subagents-pill"),
                    Component<ReactorChatFilesPill, ReactorChatFilesPillProps>(
                            new(_panels, _files, panelRevision, _panels.AreSubagentsReady, _files.Version))
                        .WithKey("chat-files-pill")))
                .MaxWidth(ChatVisuals.ReadingWidth)
                .Margin(ChatVisuals.Gutter(viewportWidth), 0, ChatVisuals.Gutter(viewportWidth), 0)
                .HAlign(HorizontalAlignment.Stretch)
                .WithKey("chat-panel-pills").Grid(row: 4),
            composerArea.Grid(row: 5))
            .Background(Theme.Ref("ChatCanvasBrush"))
            .Set(grid =>
            {
                grid.Style = (Style)Application.Current.Resources["ChatCanvasStyle"];
                grid.ClearValue(Microsoft.UI.Xaml.Controls.Panel.BackgroundProperty);
            })
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch)
            .OnMount(control => ChatVisuals.Observe(control, setViewportWidth, setViewportHeight))
            .OnUnmount(ChatVisuals.StopObserving);
        var subagents = Component<ReactorChatSubagents, ReactorChatSubagentsProps>(new(subagentChildren, _panels));
        var files = Component<ReactorChatFiles, ReactorChatFilesProps>(
            new(_files, _files.Version, timelineProps.OnOpenWorkspaceFile));
        return Component<ReactorConversationPanelHost, ReactorConversationPanelHostProps>(
            new(conversation, _panels, props.ComposerSession.SetConversationVisible, timelineProps, subagents,
                dashboardView, files,
                nativeProvider is null ? null : nativeProvider.LoadWorkspaceFileAsync));
    }

    private static Element RenderLoading() =>
        Component<ReactorChatTimeline, ReactorChatTimelineProps>(new(
            ReactorChatTimelineMode.Loading,
            new ChatTimelinePresentationContext(null, Array.Empty<ChatTimelineItem>(), false, null)));

    private ChatThread? CreateComposeOnlyThread(
        IChatDataProvider provider,
        ChatDataSnapshot snapshot,
        string? explicitThreadId = null)
    {
        var composeKey = explicitThreadId ?? _pendingSelectedThreadId
            ?? (snapshot.ComposeTarget.IsReady ? snapshot.ComposeTarget.SessionKey : null);
        if (composeKey is null)
            return null;

        var cached = (provider as OpenClawChatDataProvider)?.CachedLastChatState;
        return new ChatThread
        {
            Id = composeKey,
            AgentId = _pendingSelectedThreadId is null
                ? snapshot.ComposeTarget.AgentId
                : SessionDisplayResolver.Resolve(new SessionInfo { Key = composeKey }).AgentId
                    ?? snapshot.ComposeTarget.AgentId,
            Title = _pendingSelectedThreadId is null
                ? cached?.ThreadTitle ?? "OpenClaw Windows Tray"
                : LocalizationHelper.GetString("Chat_PendingNewSessionTitle"),
            Model = cached?.Model,
            ModelProvider = cached?.ModelProvider,
            Status = ChatThreadStatus.Running,
            Activity = ChatActivity.Idle,
        };
    }

    private void OnPermission(string threadId, string requestId, string action) =>
        RunFireAndForget(ct => Props.Provider.RespondToPermissionAsync(threadId, requestId, action, ct));

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
