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
    bool IsCompact = false,
    Func<string, bool>? TryCopyText = null,
    bool ShowSessionPicker = true);

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
    private readonly ChatTimelineProjectionCache _displayWindowCache = new();
    // Source-fact producer for bounded-window activity/run context (weak-root keyed, off the render hot path
    // via a small fixed-capacity cache; never a full-retained index computed per render).
    // Lifecycle owner for the bounded activity/run fact work. Begin(...) is called EVERY render and cancels
    // + supersedes the previous lease before any bounded compute, so a superseded window/session/root can
    // never publish or re-render; Dispose() cancels everything on unmount.
    private readonly ChatToolActivityPresentation.ChatToolActivityRenderCoordinator _activityLifecycle =
        new(new ChatToolActivityPresentation.ChatToolActivitySourceFacts());
    // MONOTONIC completion trigger (never derived from a stale render closure, so two completions cannot
    // collapse to the same value and lose a re-render).
    private long _toolFactsCompletionVersion;
    // Last STABLE bounded display window, SCOPED to the logical identity (provider + thread): held (same IDs)
    // while a deep anchor is pending, but NEVER reused across a different session/provider.
    private readonly ChatTimelineStableWindowScope _stableWindowScope = new();
    // Authoritative provider REFERENCE (never a hash): a provider replacement discards the held window.
    private object? _stableWindowProviderRef;
    // Retained logical-history facts (bounded fast path + off-UI completion), each with its OWN lifecycle so
    // the first-send callback and the render path never cancel each other.
    private readonly ChatTimelineRetainedFactsCoordinator _retainedFactsLifecycle =
        new(new ChatTimelineRetainedFactsProducer());
    private readonly ChatTimelineRetainedFactsCoordinator _firstSendFactsLifecycle =
        new(new ChatTimelineRetainedFactsProducer());
    private readonly ChatTimelineNavigationState _navigation = new();
    // Owned off-UI resolver for a DEEP pending anchor: complete source-position index, one flight per scope.
    private readonly ChatTimelineAnchorResolutionCoordinator _anchorResolution;

    public OpenClawReactorChatRoot()
    {
        // Constructor (not an instance-field initializer): CS0236 forbids referencing _navigation there.
        _anchorResolution = new ChatTimelineAnchorResolutionCoordinator(_navigation);
    }

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
        var (scrollToBottomToken, setScrollToBottomToken) = UseState(0, threadSafe: true);
        var (displayWindowVersion, setDisplayWindowVersion) = UseState(0, threadSafe: true);
        // Re-render trigger for the bounded async activity/run fact completion (off the render path).
        var (toolFactsVersion, setToolFactsVersion) = UseState(0L, threadSafe: true);
        var (showToolCalls, setShowToolCalls) = UseState(s_showToolCalls, threadSafe: true);
        var (toolCallsCollapseVersion, setToolCallsCollapseVersion) =
            UseState(s_toolCallsCollapseVersion, threadSafe: true);
        var (firstSendInFlight, setFirstSendInFlight) = UseState(false, threadSafe: true);

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
                    && args.Snapshot.Timelines.TryGetValue(composeKey, out var timeline))
                {
                    // Bounded facts on the UI callback path (no synchronous full-scan Get). The completion reads
                    // the FAITHFUL facts and clears first-send under the EXACT current lease (composeKey/root),
                    // so a deep user resolves WITHOUT another provider.Changed callback. Unknown while pending
                    // never clears first-send.
                    var factsLease = _firstSendFactsLifecycle.Begin(composeKey, timeline.Entries, 0);
                    _ = _firstSendFactsLifecycle.RequestCompletion(
                        factsLease,
                        ClearFirstSendIfUserPresent);
                    ClearFirstSendIfUserPresent();

                    void ClearFirstSendIfUserPresent()
                    {
                        if (_firstSendFactsLifecycle.TryBounded(factsLease)?.HasAnyUser == true)
                            setFirstSendInFlight(false);
                    }
                }
                else if (args.Snapshot.ComposeTarget.SessionKey is null)
                {
                    // Compose/producer changed to null: invalidate any in-flight first-send work.
                    _ = _firstSendFactsLifecycle.Begin(null, Array.Empty<ChatTimelineItem>(), 0);
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
            return () =>
            {
                provider.Changed -= onChanged;
                // PROVIDER CHANGE (not unmount): CANCEL in-flight work but keep the lifecycles USABLE with
                // the new provider. A permanent Dispose here would leave the fields inert.
                _retainedFactsLifecycle.Reset();
                _firstSendFactsLifecycle.Reset();
                _anchorResolution.Reset();
                _activityLifecycle.Reset();
            };
        }), props.Provider);

        // FINAL UNMOUNT only: permanently dispose the owned lifecycles.
        UseEffect((Func<Action>)(() => () =>
        {
            _retainedFactsLifecycle.Dispose();
            _firstSendFactsLifecycle.Dispose();
            _anchorResolution.Dispose();
            _activityLifecycle.Dispose();
        }), Array.Empty<object>());

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
        var entries = (IReadOnlyList<ChatTimelineItem>)timeline.Entries;
        // STRUCTURED identity: authoritative provider REFERENCE + thread + the AUTHORITATIVE accepted session
        // UUID published in the SAME snapshot (with the same coherent timeline). Unknown UUID is EXPLICIT and
        // never reuses another materialized identity. The history revision is deliberately NOT part of the
        // identity, so ordinary older pages (which advance it) do not discard the held view.
        var acceptedUuid = effectiveThread is { } uuidThread && snapshot.AcceptedSessionIds is { } uuids &&
            uuids.TryGetValue(uuidThread.Id, out var uuidValue)
                ? uuidValue
                : null;
        // AUTHORITATIVE provider REFERENCE change: discard ALL navigation identities/anchors owned by the
        // previous provider (not only the current thread) and reset the same-root anchor lifecycle, BEFORE
        // ensuring/resolving below. Otherwise a previously-selected thread's identity keeps rooting the old
        // provider graph. The SAME provider/thread/UUID preserves ordinary page navigation.
        if (!ReferenceEquals(_stableWindowProviderRef, props.Provider))
        {
            _stableWindowProviderRef = props.Provider;
            _stableWindowScope.Clear();   // provider replaced: discard any held window
            _navigation.Clear();          // release ALL old-provider identities + anchors
            _anchorResolution.Reset();    // cancel any in-flight anchor flight (provider change, not unmount)
        }
        // INVALIDATE the scoped navigation identity BEFORE computing THIS render's window. A UUID/provider
        // transition on the SAME thread drops a pre-reset resolved ANCHOR even when NO anchor flight is pending
        // (the previous lease-based check only fired while a flight was active). The SAME identity preserves the
        // thread's navigation across ordinary older pages.
        if (effectiveThread is { } identityThread)
            _navigation.EnsureNavigationIdentity(props.Provider, identityThread.Id, acceptedUuid);
        // Bounded MOVING display window: the expensive per-render projection consumes only a fixed-size
        // window, so per-render CPU is bounded by the window rather than the retained history length.
        // The complete logical/durable history stays intact in state/snapshot (never truncated). The
        // portable navigation seam supplies the anchored window; the cache avoids recounting it.
        _ = displayWindowVersion;   // re-render dependency for navigation moves
        var navigationWindow = _navigation.Resolve(
            effectiveThread?.Id, timeline, ChatTimelineDisplayWindowPolicy.DefaultWindowSize);
        // DEEP pending anchor: resolve the source position OFF the UI path (complete index, one owned flight)
        // and re-render on completion. While pending we DO NOT feed the tail/HiddenEarlierCount into the
        // projection cache, so the display never shows a fake resolved jump.
        var anchorLease = effectiveThread is null
            ? null
            : _anchorResolution.Begin(
                effectiveThread.Id, timeline, ChatTimelineDisplayWindowPolicy.DefaultWindowSize, acceptedUuid);
        if (anchorLease is not null)
        {
            _ = _anchorResolution.TryResolvePending(
                timeline,
                () => setToolFactsVersion(Interlocked.Increment(ref _toolFactsCompletionVersion)));
        }
        var stableOrResolved = navigationWindow.Pending
            ? navigationWindow
            : _displayWindowCache.Get(
                effectiveThread?.Id,
                timeline,
                timelineGeneration,
                ChatTimelineDisplayWindowPolicy.DefaultWindowSize,
                navigationWindow.HiddenEarlierCount + navigationWindow.VisibleEntries.Count);
        // PENDING presentation: hold the last stable bounded window (or an explicit loading window) - NEVER the
        // new tail / stale offset as if resolved.
        // (provider replacement was handled BEFORE resolving the window, see above)
        // STRUCTURED scope key: provider REFERENCE + thread + accepted UUID (revision deliberately excluded).
        var stableKey = new ChatTimelineWindowScopeKey(props.Provider, effectiveThread?.Id, acceptedUuid);
        var displayWindow = ChatTimelinePendingPresentationPolicy.Resolve(
            _stableWindowScope.Get(stableKey), stableOrResolved);
        if (!displayWindow.Pending)
            _stableWindowScope.Set(stableKey, displayWindow);   // scope-checked: never across identities
        entries = displayWindow.VisibleEntries;
        var hiddenEarlierEntries = displayWindow.HiddenEarlierCount;
        var hiddenLaterEntries = displayWindow.HiddenLaterCount;
        // SOURCE-FAITHFUL window context: resolve the logical activity-group / assistant-run continuation
        // facts from the FULL retained timeline at the window edges only (bounded resume, cached). This keeps
        // stable activity identity across moved/cut windows without a per-render full-retained scan.
        _ = toolFactsVersion;   // re-render dependency once async facts are published
        var windowStart = hiddenEarlierEntries;
        var windowEnd = hiddenEarlierEntries + displayWindow.VisibleEntries.Count;
        // Invalidate the PREVIOUS render lease on EVERY render (any session/root/window/edit/generation change),
        // BEFORE any compute, so a superseded long provisional window can never publish or re-render. Then take
        // the BOUNDED facts (never O(full history)); a provisional window yields null => clear PENDING state.
        ChatToolActivityPresentation.ChatToolActivityWindowContext? toolActivityWindow;
        bool toolActivityPending;
        if (displayWindow.Pending)
        {
            // The visible window is NOT this root's slice (deep anchor pending): do NOT query activity facts
            // with stale held offsets against the current timeline. Invalidate the obsolete lease and report an
            // explicit PENDING (null) fact so the UI holds instead of binding wrong identities.
            _ = _activityLifecycle.Begin(
                effectiveThread?.Id, Array.Empty<ChatTimelineItem>(), 0, 0, timelineGeneration);
            toolActivityWindow = null;
            toolActivityPending = true;
        }
        else
        {
            var activityLease = _activityLifecycle.Begin(
                effectiveThread?.Id, timeline.Entries, windowStart, windowEnd, timelineGeneration);
            // Register the completion BEFORE reading the bounded facts so a fact that becomes faithful between
            // the read and the request can never lose its wakeup; RequestCompletion returns false when faithful.
            _ = _activityLifecycle.RequestCompletion(
                activityLease,
                () => setToolFactsVersion(Interlocked.Increment(ref _toolFactsCompletionVersion)));
            toolActivityWindow = _activityLifecycle.TryBounded(activityLease);
            toolActivityPending = toolActivityWindow is null;
        }

        // Bounded metadata snapshot for the VISIBLE entries only (no full retained-dictionary copy under
        // the state lock), plus the full-retained latest qualifying usage so windowing cannot lose it.
        var entryMetadata = effectiveThread is not null && props.Provider is OpenClawChatDataProvider metadataProvider
            ? metadataProvider.GetVisibleEntryMetadata(
                effectiveThread.Id,
                displayWindow.VisibleEntries.Select(entry => entry.Id).ToArray())
            : null;
        var latestUsage = effectiveThread is not null && props.Provider is OpenClawChatDataProvider usageProvider
            ? usageProvider.GetLatestUsage(effectiveThread.Id)
            : (Summary: (string?)null, EntryId: (string?)null);
        var usageSummary = latestUsage.Summary;
        var queuedMessages = effectiveThread is not null
            && snapshot.QueuedMessagesByThread?.TryGetValue(effectiveThread.Id, out var queued) == true
                ? queued
                : Array.Empty<ChatQueuedMessage>();
        var hasPendingQueuedSend = queuedMessages.Any(message =>
            message.SendState is ChatQueuedMessageSendState.Queued or ChatQueuedMessageSendState.Sending);
        // Source-faithful retained facts (weak-root-keyed, bounded fast path + off-UI completion) replace the
        // per-render full-retained backward scan. While PENDING the flag is UNKNOWN: do NOT invent a value -
        // treat unknown as "assistant may exist" so a false thinking spinner is never shown.
        var factsLease = _retainedFactsLifecycle.Begin(
            effectiveThread?.Id, timeline.Entries, timelineGeneration);
        // Register the completion BEFORE reading the bounded facts, so a fact becoming faithful between the
        // read and the request can never lose its wakeup (RequestCompletion returns false when faithful).
        _ = _retainedFactsLifecycle.RequestCompletion(
            factsLease,
            () => setToolFactsVersion(Interlocked.Increment(ref _toolFactsCompletionVersion)));
        var retainedFacts = _retainedFactsLifecycle.TryBounded(factsLease);
        var currentTurnHasAssistant = retainedFacts?.CurrentTurnHasAssistant ?? true;

        var showThinking = timeline.TurnActive && !currentTurnHasAssistant;
        var isEmptyConversation = entries.Count == 0 && queuedMessages.Count == 0
            && !showThinking && timeline.PendingPermission is null;
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
        Func<string, ChatMediaContentInfo, CancellationToken, Task<AssistantMediaResolutionResult>>?
            mediaResolver = props.Provider is OpenClawChatDataProvider dataProvider
                ? dataProvider.ResolveAssistantMediaAsync
                : null;

        // Load-earlier is wired from the accepted bounded window of the selected thread only. The
        // thread identity is captured at action time; unknown/unavailable windows never claim more.
        // Load-earlier is driven by the explicit state of the SELECTED thread only. The thread identity
        // is captured at action time; a fault is observed (never an unobserved task); the accepted
        // server offset is unchanged and no unbounded collection is kept.
        var loadOlderState = effectiveThread is { } stateThread &&
            props.Provider is OpenClawChatDataProvider stateProvider
            ? stateProvider.GetLoadOlderState(stateThread.Id)
            : ChatLoadOlderState.Unavailable;
        // Locally retained older entries are shown by growing the display window (no fetch); only when
        // none remain does the server-side bounded page path apply.
        var hasMoreHistory = hiddenEarlierEntries > 0 || loadOlderState is
            ChatLoadOlderState.Available or ChatLoadOlderState.Loading or ChatLoadOlderState.Error;
        Action? onLoadMoreHistory = !hasMoreHistory || effectiveThread is not { } olderThread
            ? null
            : hiddenEarlierEntries > 0
                ? () => MoveDisplayWindowOlder(olderThread.Id)
                : props.Provider is OpenClawChatDataProvider olderProvider
                    ? () => _ = LoadOlderObservedAsync(olderProvider, olderThread.Id)
                    : null;

        // Actual newer / return-to-live control: after navigating earlier, retained newer entries are
        // reachable again (moving one page, then returning to the live tail).
        var canShowNewerHistory = hiddenLaterEntries > 0;
        Action? onShowNewerHistory = !canShowNewerHistory || effectiveThread is not { } newerThread
            ? null
            : hiddenLaterEntries <= ChatTimelineDisplayWindowPolicy.NavigationPageSize
                ? () => ReturnDisplayWindowToLive(newerThread.Id)   // one click back to the live tail
                : () => MoveDisplayWindowNewer(newerThread.Id);

        var timelineProps = new ChatTimelinePresentationContext(
            effectiveThread?.Id,
            entries,
            hasMoreHistory,
            onLoadMoreHistory,
            entryMetadata,
            timelineGeneration,
            "OpenClaw Windows Tray",
            "Assistant",
            effectiveThread?.Model,
            // Global latest qualifying usage ONLY. Never fall back to the windowed slice here, which would
            // mislabel a window-local assistant with usage that belongs to a newer global entry.
            showToolCalls
                ? usageSummary ?? ChatUsageFormatter.Format(effectiveThread)
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
            loadOlderState,
            onLoadMoreHistory,
            canShowNewerHistory,
            onShowNewerHistory,
            latestUsage.EntryId,
            toolActivityWindow,
            toolActivityPending,
            displayWindow.Pending);   // WindowPending: consumer shows loading/holding, not a fake resolved jump

        // Observed action: the provider records a generic retryable Error state internally, so a
        // transport/protocol fault never leaves an unobserved task or raw exception text on the UI.
        static async Task LoadOlderObservedAsync(OpenClawChatDataProvider provider, string threadId)
        {
            try
            {
                await provider.LoadOlderAsync(threadId).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        void MoveDisplayWindowOlder(string threadId)
        {
            // Move the bounded window one page toward older entries (fixed size; never grows to all).
            _navigation.MoveOlder(threadId, timeline, ChatTimelineDisplayWindowPolicy.DefaultWindowSize);
            setDisplayWindowVersion(displayWindowVersion + 1);   // value setter (Reactor UseState), not a Func
        }

        void MoveDisplayWindowNewer(string threadId)
        {
            _navigation.MoveNewer(threadId, timeline, ChatTimelineDisplayWindowPolicy.DefaultWindowSize);
            setDisplayWindowVersion(displayWindowVersion + 1);
        }

        void ReturnDisplayWindowToLive(string threadId)
        {
            _navigation.ReturnToLive(threadId, timeline, ChatTimelineDisplayWindowPolicy.DefaultWindowSize);
            setDisplayWindowVersion(displayWindowVersion + 1);
        }

        void SelectThread(string threadId)
        {
            // Lifecycle pruning: a thread selection starts from the live tail (bounded nav state).
            _navigation.Forget(threadId);
            _pendingSelectedThreadId = threadId;
            selectedIdRef.Current = threadId;
            setSelectedId(threadId);
            if (props.Provider is OpenClawChatDataProvider native)
                native.RememberSelectedThread(threadId);
        }

        // Bound once (idempotent) so the composer controller can hand a freshly
        // created "/new" session, or a session-picker selection, back to the root's
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
                ObserveFireAndForget(props.ComposerSession.Controller.SendCoreAsync(
                    suggestionThread.Id,
                    suggestionThread.Title,
                    suggestion,
                    Array.Empty<ChatAttachment>()));
            };
        }

        var timelineElement = Component<ReactorChatTimeline, ReactorChatTimelineProps>(new(
            mode,
            timelineProps,
            onSuggestionPicked,
            firstSendInFlight,
            OnOpenCheckpoints: props.OnOpenCheckpoints,
            HistoryRevision: historyRevision,
            TryCopyText: props.TryCopyText));

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
                AvailableChannels: VisibleChannels(snapshot.Threads, effectiveThread),
                AvailableModels: snapshot.AvailableModels,
                ModelChoices: snapshot.ModelChoices,
                MessageOptionsDisabled: timeline.TurnActive || hasPendingQueuedSend,
                QueuedMessages: queuedMessages,
                AvailableCommands: snapshot.AvailableCommands,
                CommandsSupported: snapshot.CommandsSupported);
            composerElement = Component<ReactorChatComposer, ReactorChatComposerViewProps>(new(
                props.ComposerSession,
                composerInputs,
                snapshot,
                () => setScrollToBottomToken(scrollToBottomToken + 1),
                props.IsCompact,
                props.ShowSessionPicker));
        }

        return Grid(
            [GridSize.Star()],
            [GridSize.Star(), GridSize.Auto],
            timelineElement.Grid(row: 0),
            composerElement.Grid(row: 1))
            .Background(Theme.Ref("ChatCanvasBrush"))
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch);
    }

    private static Element RenderLoading() =>
        Component<ReactorChatTimeline, ReactorChatTimelineProps>(new(
            ReactorChatTimelineMode.Loading,
            new ChatTimelinePresentationContext(null, Array.Empty<ChatTimelineItem>(), false, null),
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
