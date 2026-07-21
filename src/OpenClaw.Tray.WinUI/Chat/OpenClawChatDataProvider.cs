using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenClaw.Chat;
using OpenClaw.Shared;
#if !OPENCLAW_TRAY_TESTS
using OpenClawTray.Helpers;
#endif
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

#if OPENCLAW_TRAY_TESTS
// Shim for the test-only compilation. The real LocalizationHelper lives in
// OpenClaw.Tray.WinUI and depends on Microsoft.Windows.ApplicationModel.Resources
// which isn't available to the test project. Returning the resource key keeps
// the notification text identifiable in tests without pulling in WinAppSDK.
internal static class LocalizationHelper
{
    public static string GetString(string resourceKey) => resourceKey switch
    {
        "Chat_TruncationMarkerFormat" => " … [{0} bytes truncated]",
        "Chat_Permission_Allow" => "Allow once",
        "Chat_Permission_AllowAlways" => "Always allow",
        "Chat_Permission_Deny" => "Deny once",
        "Chat_Permission_CommandApprovalTitle" => "Command approval requested",
        "Chat_Permission_ResultSubmittedFormat" => "Approval {0} submitted for {1}.",
        "Chat_Error_SendReturnedStatusFormat" => "Gateway returned send status '{0}'.",
        "Chat_Error_SendFailedFormat" => "Send failed: {0}",
        _ => resourceKey
    };
}
#endif

/// <summary>
/// Adapts <see cref="IChatGatewayBridge"/> (which wraps a live
/// <see cref="OpenClawGatewayClient"/>) into the
/// <see cref="IChatDataProvider"/> contract consumed by the native chat components.
/// </summary>
/// <remarks>
/// Maps gateway signals into <see cref="ChatTimelineState"/> events:
/// <list type="bullet">
///   <item><c>SessionsUpdated</c> → rebuild <see cref="ChatThread"/> set.</item>
///   <item><c>chat.history</c> RPC → fold past messages into the timeline
///         (called automatically once per thread on first selection).</item>
///   <item><c>ChatMessageReceived</c> (role=assistant, final) →
///         <see cref="ChatMessageEvent"/> + <see cref="ChatTurnEndEvent"/>.</item>
///   <item><c>ChatMessageReceived</c> (role=user) → ignored (the local
///         <see cref="SendMessageAsync"/> already added the user entry).</item>
///   <item><c>AgentEventReceived</c> stream=assistant → streaming deltas
///         (<see cref="ChatMessageDeltaEvent"/>).</item>
///   <item><c>AgentEventReceived</c> stream=reasoning → reasoning entry
///         (<see cref="ChatReasoningEvent"/>/<see cref="ChatReasoningDeltaEvent"/>).</item>
///   <item><c>AgentEventReceived</c> stream=lifecycle phase=start/end/error →
///         <see cref="ChatThinkingEvent"/>/<see cref="ChatTurnEndEvent"/>/<see cref="ChatErrorEvent"/>.</item>
///   <item><c>AgentEventReceived</c> stream=tool/job → tool start/output/error
///         and turn-end timeline events.</item>
/// </list>
/// <para>
/// Active <c>runId</c>s are tracked per thread (set on lifecycle.start,
/// cleared on lifecycle.end) so <see cref="StopResponseAsync"/> can issue
/// a <c>chat.abort</c> RPC. Immutable session IDs returned by
/// <c>chat.history</c> are persisted per thread and forwarded on
/// subsequent <see cref="SendMessageAsync"/> calls.
/// </para>
/// </remarks>
public sealed class OpenClawChatDataProvider : IChatDataProvider
{
    private const long ResetTimestampToleranceMs = 1000;
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Process-wide cache mapping an attachment's filename to its raw image
    /// bytes. Populated by <see cref="SendMessageAsync"/> for image
    /// attachments so the timeline can render an actual thumbnail in the
    /// user bubble (the display-text marker only carries the filename, not
    /// the base64 content). Static so any timeline render after a re-mount
    /// can still find the image.
    /// </summary>
    public static readonly ConcurrentDictionary<string, byte[]> ImagePreviewCache = new();

    private readonly IChatGatewayBridge _bridge;
    private readonly ChatTelemetryTracker _telemetry = new();
    private readonly Action<Action>? _post;
    private readonly object _gate = new();
    private readonly object _toolMetaSaveGate = new();
    private readonly object _attachmentMetaSaveGate = new();
    private readonly string _toolMetaCacheFilePath;
    private readonly string _attachmentMetaCacheFilePath;
    private readonly string _lastChatStateFilePath;
    private readonly TimeSpan _lastChatStateSaveDelay;
    private readonly Func<TimeSpan, Func<Task>, Task> _scheduleHistoryRetry;
    private System.Threading.Timer? _toolMetaSaveTimer; // debounce cache writes
    private long _toolMetaSaveVersion;
    private bool _toolMetaCacheDirty;
    private readonly Dictionary<string, ChatTimelineState> _timelines = new();
    private readonly Dictionary<string, LocalInlineApproval> _localInlineApprovals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _activeRunIds = new();   // sessionKey → runId
    private readonly Dictionary<string, long> _activeRunStartSequences = new(); // sessionKey → lifecycle.start sequence
    private readonly Dictionary<string, int> _pendingAbortCounts = new(); // threads → count of pending aborts waiting for lifecycle.start
    private readonly HashSet<string> _abortedRunIds = new();             // runIds whose events should be suppressed
    private readonly HashSet<string> _abortedThreads = new();            // threads with active abort — suppress chat messages (no runId on those)
    private Dictionary<string, HashSet<string>> _persistedAbortedIds;    // threadId → set of __openclaw.id values (loaded from disk)
    private readonly SemaphoreSlim _persistLock = new(1, 1);             // serialize persist calls to avoid races

    /// <summary>Whether any thread is in an aborted state (suppress TTS/notifications).</summary>
    public bool IsResponseSuppressed { get { lock (_gate) return _abortedThreads.Count > 0; } }

    private readonly Dictionary<string, string> _sessionIds = new();      // sessionKey → immutable sessionId
    private readonly HashSet<string> _historyLoaded = new();              // sessionKey
    private readonly HashSet<string> _historyInFlight = new();            // sessionKey
    private long _historyConnectionVersion;
    private readonly Dictionary<string, Task> _pendingModelPatches = new(); // sessionKey -> in-flight model set/clear
    private readonly Dictionary<string, long> _resetVersions = new(); // sessionKey -> reset generation
    private readonly Dictionary<string, long> _resetCutoffUtcMs = new(); // sessionKey -> local reset time
    private readonly HashSet<string> _resetAwaitingUserMessage = new(); // threads reset and waiting for first post-reset turn
    private readonly Dictionary<string, HashSet<string>> _resetIgnoredRunIds = new(); // sessionKey -> pre-reset run IDs to drop
    private readonly Dictionary<string, Dictionary<string, Queue<DateTimeOffset>>> _resetSubmittedLocalEchoTexts = new(); // sessionKey -> pre-reset local user echoes that reached the gateway
    private readonly Dictionary<string, HashSet<string>> _resetAcceptedRunIds = new(); // sessionKey -> post-reset run IDs allowed to open the gate
    private readonly Dictionary<string, long> _resetLocalSendWithoutRunVersions = new(); // sessionKey -> reset generation for no-runId sends
    private readonly Dictionary<string, long> _resetLocalSendWithoutRunStartSequences = new(); // sessionKey -> lifecycle sequence at local send start
    private readonly Dictionary<string, long> _resetLocalEchoSequences = new(); // sessionKey -> lifecycle sequence when local echo was observed
    private readonly Dictionary<string, List<PendingResetLifecycleStart>> _resetPendingLifecycleStarts = new(); // sessionKey -> lifecycle.start seen before proof
    private readonly HashSet<string> _resetRemoteBackfillInFlight = new(); // threads proving a timestamp-less remote user frame via history
    private long _resetLifecycleStartSequence;
    private long _lifecycleStartSequence;
    private readonly HashSet<string> _resetRemoteUserSeen = new(); // threads with a fresh remote post-reset user frame
    private readonly Dictionary<string, string> _resetClearedSessionIds = new(); // sessionKey -> sessionId cleared by reset
    // Per-session cache of tool metadata from live SSE events.
    // Keyed by gateway sessionId (immutable UUID).  Persisted to disk
    // so that history reconstruction on restart can recover tool names.
    private Dictionary<string, List<CachedToolMeta>> _toolMetaCache;
    private Dictionary<string, List<CachedAttachmentMeta>> _attachmentMetaCache;
    // Track recently-sent local user message texts so we can suppress
    // SSE echoes while still displaying messages from other clients.
    private readonly Dictionary<string, Queue<LocalSentText>> _localSentTexts = new();
    private readonly Dictionary<string, List<ChatQueuedMessage>> _queuedMessages = new();
    private readonly Dictionary<string, List<QueuedSendRequest>> _queuedSendRequests = new();
    private readonly Dictionary<string, Dictionary<string, string>> _queuedMessageIdsByRunId = new();
    private readonly Dictionary<string, List<string>> _terminalRunIdsByThread = new();
    private readonly HashSet<string> _queuedDrainScheduledThreads = new(StringComparer.Ordinal);
    private readonly HashSet<string> _assistantFallbackPromotedThreads = new(StringComparer.Ordinal);
    private long _queuedMessageSequence;
    private int _keylessEventDiagnosticRaised;
    // Threads where we locally initiated the current turn (via SendMessageAsync).
    // When lifecycle.start arrives for a thread NOT in this set, we know a remote
    // client started the turn and should fetch the user message from history.
    private readonly HashSet<string> _locallyInitiatedThreads = new();
    // Per-thread retry count for LoadHistoryAsync to prevent unbounded retry loops.
    private readonly Dictionary<string, int> _historyRetryCount = new();
    private const int MaxHistoryRetries = 3;
    private static readonly TimeSpan HistoryRetryDelay = TimeSpan.FromSeconds(2);
    private const int MaxDeferredAdmissionRetries = 8;
    private static readonly TimeSpan LocalEchoSuppressionWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeferredQueueDrainDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaxDeferredAdmissionRetryDelay = TimeSpan.FromSeconds(1);
    private readonly record struct LocalSentText(string Text, DateTimeOffset SentAt, string QueuedMessageId);
    private sealed record QueuedSendRequest(
        string Id,
        string SendRunId,
        string ThreadId,
        string Text,
        string DisplayText,
        string LocalNonce,
        IReadOnlyList<ChatAttachment>? Attachments,
        int DeferredAdmissionRetryCount = 0,
        DateTimeOffset? DeferredAdmissionRetryAfter = null);
    private sealed record QueuedSendDispatch(
        QueuedSendRequest Request,
        string? SessionId,
        long ResetVersion,
        long StartedLifecycleSequence,
        long StartedRunStartSequence,
        ChatTelemetryTracker.QueuePhaseCompletion? QueueCompletion,
        bool StartedDirectly);
    private enum AssistantQueueFrameDisposition
    {
        Render,
        Drop,
    }
    private sealed record LocalInlineApproval(
        string ThreadId,
        string RequestId,
        string Detail,
        TaskCompletionSource<ExecApprovalPromptDecision> Response);
    private static readonly TimeSpan LocalInlineApprovalTimeout = TimeSpan.FromSeconds(30);
    // Per-thread, per-entry metadata: timestamp + model snapshot at the
    // moment the entry was created. Built up as events are applied so the
    // timeline renderer can show a "<sender> · <local time> · <model>" footer
    // beneath each message without having to extend the vendored
    // <see cref="ChatTimelineItem"/> record.
    private readonly Dictionary<string, Dictionary<string, ChatEntryMetadata>> _entryMeta = new();
    private SessionInfo[] _sessions = Array.Empty<SessionInfo>();
    // True once the gateway has delivered a sessions list (even an empty
    // one) for the current connection. Used to gate the synthetic
    // compose-only thread so the UI doesn't briefly render the welcome
    // zero-state in the window between hello-ok (HasHandshakeSnapshot)
    // and the first sessions.list — at that point the gateway may still
    // be about to deliver real sessions for a returning user. Reset to
    // false on disconnect alongside `_status`.
    private bool _sessionsListReceived;
    private string[] _availableModels = Array.Empty<string>();
    private IReadOnlyList<ChatModelChoice> _modelChoices = Array.Empty<ChatModelChoice>();
    // Gateway command catalog (commands.list), fetched on demand via the typed
    // protocol API. Null until the first fetch completes so the UI can
    // distinguish "still loading" from "loaded but empty". When the gateway
    // reports the method unsupported the catalog carries IsSupported=false.
    private CommandCatalog? _commandCatalog;
    // Guards against overlapping in-flight commands.list fetches.
    private bool _commandsFetchInFlight;
    // Bumped on every transition out of Connected so a commands.list fetch that
    // was already in flight at disconnect time is discarded on completion rather
    // than resurrecting a catalog for a stale connection.
    private int _commandsEpoch;
    private ConnectionStatus _status;
    private bool _disposed;

    public string DisplayName => "OpenClaw gateway";

    /// <summary>Last-known chat state from a previous session, used for pre-connection UI.</summary>
    internal LastChatState? CachedLastChatState => _lastChatState;

    public event EventHandler<ChatDataChangedEventArgs>? Changed;
    public event EventHandler<ChatProviderNotificationEventArgs>? NotificationRequested;

    /// <param name="bridge">Adapter wrapping the live gateway client.</param>
    /// <param name="post">
    /// Optional UI-thread marshaling callback. Pass
    /// <c>action =&gt; dispatcherQueue.TryEnqueue(() =&gt; action())</c> from
    /// production code so that <see cref="Changed"/>/<see cref="NotificationRequested"/>
    /// callbacks observed by FunctionalUI components fire on the UI thread.
    /// When <c>null</c>, callbacks fire on whatever thread the gateway raised
    /// the source event on (acceptable in unit tests).
    /// </param>
    public OpenClawChatDataProvider(IChatGatewayBridge bridge, Action<Action>? post = null)
        : this(bridge, post, DefaultToolMetaCacheFilePath)
    {
    }

    internal OpenClawChatDataProvider(
        IChatGatewayBridge bridge,
        Action<Action>? post,
        string toolMetaCacheFilePath,
        string? attachmentMetaCacheFilePath = null,
        string? lastChatStateFilePath = null,
        TimeSpan? lastChatStateSaveDelay = null,
        Func<TimeSpan, Func<Task>, Task>? historyRetryScheduler = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _post = post;
        _toolMetaCacheFilePath = !string.IsNullOrWhiteSpace(toolMetaCacheFilePath)
            ? toolMetaCacheFilePath
            : throw new ArgumentException("Tool metadata cache path is required.", nameof(toolMetaCacheFilePath));
        _attachmentMetaCacheFilePath = !string.IsNullOrWhiteSpace(attachmentMetaCacheFilePath)
            ? attachmentMetaCacheFilePath
            : DefaultAttachmentMetaCacheFilePath(_toolMetaCacheFilePath);
        _lastChatStateFilePath = !string.IsNullOrWhiteSpace(lastChatStateFilePath)
            ? lastChatStateFilePath
            : LastChatStateFilePath;
        _lastChatStateSaveDelay = lastChatStateSaveDelay ?? TimeSpan.FromSeconds(2);
        _scheduleHistoryRetry = historyRetryScheduler ?? (static (delay, retry) =>
            Task.Run(async () =>
            {
                await Task.Delay(delay).ConfigureAwait(false);
                await retry().ConfigureAwait(false);
            }));
        _status = bridge.CurrentStatus;
        _persistedAbortedIds = LoadAbortedIds();
        _toolMetaCache = LoadToolMetaCache(_toolMetaCacheFilePath);
        _attachmentMetaCache = LoadAttachmentMetaCache(_attachmentMetaCacheFilePath);
        _lastChatState = LoadLastChatState(_lastChatStateFilePath);

        // Seed models from whatever the bridge already knows about (a connect
        // that completed before the provider was constructed will have its
        // models.list snapshot cached on the bridge).
        if (bridge.GetCurrentModelsList() is { } seedModels)
        {
            _modelChoices = ChatModelChoice.FromModelsList(seedModels);
            _availableModels = ModelIdsFromChoices(_modelChoices);
        }
        // Fall back to last-known models so the composer shows a real model
        // name while reconnecting instead of the generic "model" placeholder.
        else if (_lastChatState?.AvailableModels is { Length: > 0 } cached)
        {
            _availableModels = cached;
            _modelChoices = ChoicesFromIds(cached);
        }

        _bridge.StatusChanged += OnStatusChanged;
        _bridge.SessionsUpdated += OnSessionsUpdated;
        _bridge.SessionCommandCompleted += OnSessionCommandCompleted;
        _bridge.ChatMessageReceived += OnChatMessageReceived;
        _bridge.AgentEventReceived += OnAgentEventReceived;
        _bridge.ModelsListUpdated += OnModelsListUpdated;

        // Bridge ctor may have been invoked AFTER the gateway client was
        // already Connected, in which case the StatusChanged → Connected
        // edge that would normally trigger the models.list / sessions.list
        // refresh was missed. Now that our handlers are wired, ask the
        // bridge to send those requests proactively so the composer's
        // channel + model dropdowns populate on first paint — without this,
        // the dropdowns sit on a single placeholder until the user sends
        // their first message.
        _bridge.StartProactiveBootstrap();
    }

    public Task<ChatDataSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Seed from whatever the bridge already knows about.
        var sessions = _bridge.GetSessionList() ?? Array.Empty<SessionInfo>();
        lock (_gate)
        {
            _sessions = sessions;
            EnsureTimelinesForSessionsLocked();
            RememberLastSessionStateLocked();
            return Task.FromResult(BuildSnapshotLocked());
        }
    }

    internal void RememberSelectedThread(string? threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
            return;

        LastChatState? state;
        lock (_gate)
        {
            if (!TryGetSessionLocked(threadId, out var session))
                return;

            state = new LastChatState
            {
                DefaultThreadId = threadId,
                ThreadTitle = SessionTitleFormatter.Format(session, _sessions),
                Model = session.Model,
                ModelProvider = session.Provider,
                AvailableModels = _availableModels,
            };
            _lastChatState = state;
            _lastChatStateSaveVersion++;
            _lastChatStateSaveTimer?.Dispose();
            _lastChatStateSaveTimer = null;
        }

        SaveLastChatState(state, _lastChatStateFilePath);
    }

    // Explicit interface implementation (no attachments).
    Task IChatDataProvider.SendMessageAsync(string threadId, string message, CancellationToken cancellationToken)
        => SendMessageAsync(threadId, message, cancellationToken, attachments: null);

    public async Task SendMessageAsync(string threadId, string message, CancellationToken cancellationToken = default, IReadOnlyList<ChatAttachment>? attachments = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var hasAttachments = attachments is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(message) && !hasAttachments)
            throw new ArgumentException("Message or attachment is required.", nameof(message));

        var trimmed = message.Trim();
        var nonce = Guid.NewGuid().ToString("N");

        // Cache image attachments by filename so the timeline can render an
        // actual thumbnail preview (the display-text marker only carries the
        // filename — see ImagePreviewCache notes).
        if (hasAttachments)
        {
            foreach (var a in attachments!)
            {
                if (a.Type == "image" && !string.IsNullOrEmpty(a.FileName) && !string.IsNullOrEmpty(a.Content))
                {
                    try { ImagePreviewCache[a.FileName] = Convert.FromBase64String(a.Content); }
                    catch (Exception ex) { Logger.Debug($"ChatDataProvider: image attachment base64 decode failed for '{a.FileName}': {ex.Message}"); }
                }
            }
        }

        // Build the display text for the user bubble. When attachments are
        // present, append a structured indicator line so the bubble is never
        // blank even if the typed message was empty. Uses a unique prefix
        // ("\u200B📎 " / "\u200B🖼️ ") with a zero-width space to prevent
        // false positives from normal user text.
        var safeUserText = EscapeUntrustedAttachmentMarkerLines(trimmed);
        var displayText = safeUserText;
        if (hasAttachments)
        {
            var chips = BuildAttachmentMarkerLines(attachments!);
            displayText = string.IsNullOrEmpty(safeUserText)
                ? chips
                : $"{safeUserText}\n{chips}";
        }

        // 1. Render immediately when this thread is idle. Follow-up messages
        // enter the visible queue and stay client-only until the turn ends.
        ChatDataSnapshot snapshot;
        string messageId;
        QueuedSendDispatch? dispatch;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            messageId = $"q{++_queuedMessageSequence}";
            if (CanClearAssistantFallbackPromotionLocked(threadId))
                _assistantFallbackPromotedThreads.Remove(threadId);

            // Clear abort suppression — the user is starting a new interaction.
            // Also clear pending abort counts: if the user sends a new message,
            // any queued aborts from before should not fire against the new turn.
            _abortedThreads.Remove(threadId);
            _pendingAbortCounts.Remove(threadId);

            var request = new QueuedSendRequest(
                messageId,
                Guid.NewGuid().ToString(),
                threadId,
                trimmed,
                displayText,
                nonce,
                attachments?.ToArray());

            var sendDirectly = CanSendDirectlyLocked(threadId);
            _telemetry.StartLocalTurn(request.Id, threadId, queued: !sendDirectly);
            if (sendDirectly)
            {
                dispatch = StartDirectSendLocked(request);
            }
            else
            {
                AddQueuedMessageLocked(threadId, new ChatQueuedMessage(
                    messageId,
                    displayText,
                    DateTimeOffset.UtcNow,
                    nonce));
                AddQueuedSendRequestLocked(request);
                dispatch = TryStartNextQueuedSendLocked(threadId, requireConnected: false, out _);
            }

            snapshot = BuildSnapshotLocked();
        }
        Publish(snapshot);

        if (dispatch is not null)
            await DispatchQueuedSendAsync(dispatch, rethrow: true, cancellationToken);
    }

    public Task<bool> CancelQueuedMessageAsync(string threadId, string queuedMessageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(threadId))
            throw new ArgumentException("Thread id is required.", nameof(threadId));
        if (string.IsNullOrEmpty(queuedMessageId))
            throw new ArgumentException("Queued message id is required.", nameof(queuedMessageId));

        ChatDataSnapshot? snapshot = null;
        ChatTelemetryTracker.PreparedTurnCompletion? telemetryCompletion = null;
        var canceled = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            canceled = CancelQueuedMessageLocked(threadId, queuedMessageId);
            if (canceled)
            {
                telemetryCompletion = _telemetry.PrepareFinishByMessageId(
                    queuedMessageId,
                    ChatTelemetryOutcome.Canceled,
                    ChatTurnTelemetryReason.QueuedCanceled);
                snapshot = BuildSnapshotLocked();
            }
        }

        _telemetry.CompletePreparedTurn(telemetryCompletion);
        if (snapshot is not null)
            Publish(snapshot);

        return Task.FromResult(canceled);
    }

    private async Task DispatchQueuedSendAsync(
        QueuedSendDispatch dispatch,
        bool rethrow,
        CancellationToken cancellationToken = default)
    {
        _telemetry.CompleteQueueDispatch(dispatch.QueueCompletion);
        var request = dispatch.Request;
        var threadId = request.ThreadId;
        var hasAttachments = request.Attachments is { Count: > 0 };
        ChatTelemetryOperation? sendOperation = null;

        try
        {
            await AwaitPendingModelPatchAsync(threadId, cancellationToken);
            lock (_gate)
            {
                if (_disposed)
                    return;
                if (GetResetVersionLocked(threadId) == dispatch.ResetVersion)
                    TrackQueuedMessageRunLocked(threadId, request.SendRunId, request.Id);
            }
            sendOperation = _telemetry.StartSendAttempt(request.Id);
            var sendResult = await _bridge.SendChatMessageForRunAsync(
                request.Text,
                threadId,
                dispatch.SessionId,
                request.Attachments,
                idempotencyKey: request.SendRunId);
            var admissionStatus = MapAdmissionTelemetryStatus(sendResult);
            var admissionOutcome = admissionStatus == ChatAdmissionTelemetryStatus.Canceled
                ? ChatTelemetryOutcome.Canceled
                : sendResult.IsTerminalFailure
                    ? ChatTelemetryOutcome.Failure
                    : ChatTelemetryOutcome.Success;
            _telemetry.FinishSendAttempt(
                sendOperation,
                admissionStatus,
                admissionOutcome);
            if (admissionStatus == ChatAdmissionTelemetryStatus.Accepted)
                _telemetry.ObserveAdmissionAccepted(request.Id);
            if (sendResult.IsTerminalFailure)
            {
                ChatTelemetryTracker.PreparedTurnCompletion? rejectedCompletion;
                lock (_gate)
                {
                    rejectedCompletion = _telemetry.PrepareFinishByMessageId(
                        request.Id,
                        admissionOutcome,
                        ChatTurnTelemetryReason.SendRejected);
                }
                _telemetry.CompletePreparedTurn(rejectedCompletion);
                var failure = !string.IsNullOrWhiteSpace(sendResult.Error)
                    ? sendResult.Error!
                    : string.Format(
                        CultureInfo.CurrentCulture,
                        LocalizationHelper.GetString("Chat_Error_SendReturnedStatusFormat"),
                        sendResult.Status);
                throw new InvalidOperationException(failure);
            }

            bool sendStillCurrent;
            string? staleRunIdToAbort = null;
            ChatTelemetryTracker.PreparedTurnCompletion? staleCompletion = null;
            ChatDataSnapshot? acceptedSnapshot = null;
            ChatDataSnapshot? requeuedSnapshot = null;
            var retryDeferredSend = false;
            var deferredRetryDelay = DeferredQueueDrainDelay;
            var acceptedRunId = string.IsNullOrWhiteSpace(sendResult.RunId)
                ? null
                : sendResult.RunId!;
            lock (_gate)
            {
                sendStillCurrent = GetResetVersionLocked(threadId) == dispatch.ResetVersion;
                if (!sendStillCurrent)
                {
                    staleRunIdToAbort = acceptedRunId ?? request.SendRunId;
                    staleCompletion = _telemetry.PrepareFinishByMessageId(
                        request.Id,
                        ChatTelemetryOutcome.Canceled,
                        ChatTurnTelemetryReason.Superseded);
                    AddResetIgnoredRunIdLocked(threadId, staleRunIdToAbort);
                }
                else if (IsDeferredAdmissionStatus(sendResult.Status))
                {
                    var runAlreadyStarted = !string.IsNullOrEmpty(acceptedRunId)
                        && _activeRunIds.TryGetValue(threadId, out var activeRunId)
                        && _activeRunStartSequences.TryGetValue(threadId, out var activeStartSequence)
                        && string.Equals(activeRunId, acceptedRunId, StringComparison.Ordinal)
                        && activeStartSequence > dispatch.StartedRunStartSequence;
                    if (runAlreadyStarted)
                    {
                        _telemetry.BindAcceptedRun(request.Id, acceptedRunId);
                        TrackQueuedMessageRunLocked(threadId, acceptedRunId!, request.Id);
                        AddResetAcceptedRunIdLocked(threadId, acceptedRunId!);
                        if (PromoteQueuedMessageLocked(threadId, request.Id))
                        {
                            acceptedSnapshot = BuildSnapshotLocked();
                        }
                        else
                        {
                            RemoveQueuedRunMappingByMessageIdLocked(threadId, request.Id);
                        }
                    }
                    else if (RequeueDeferredAdmissionLocked(threadId, request.Id, out deferredRetryDelay))
                    {
                        _telemetry.RequeueLocalTurn(request.Id);
                        if (!string.IsNullOrEmpty(acceptedRunId))
                        {
                            TrackQueuedMessageRunLocked(threadId, acceptedRunId, request.Id);
                            AddResetAcceptedRunIdLocked(threadId, acceptedRunId);
                        }
                        requeuedSnapshot = BuildSnapshotLocked();
                        retryDeferredSend = true;
                    }
                    else if (dispatch.StartedDirectly)
                    {
                        throw new InvalidOperationException(
                            $"Gateway returned chat.send status {sendResult.Status} before admitting the direct send.");
                    }
                }
                else if (!string.IsNullOrEmpty(acceptedRunId))
                {
                    _telemetry.BindAcceptedRun(request.Id, acceptedRunId);
                    TrackQueuedMessageRunLocked(threadId, acceptedRunId, request.Id);
                    AddResetAcceptedRunIdLocked(threadId, acceptedRunId);
                    var runAlreadyStarted = _activeRunIds.TryGetValue(threadId, out var activeRunId)
                        && _activeRunStartSequences.TryGetValue(threadId, out var activeStartSequence)
                        && string.Equals(activeRunId, acceptedRunId, StringComparison.Ordinal)
                        && activeStartSequence > dispatch.StartedRunStartSequence;
                    if (PromoteQueuedMessageLocked(threadId, request.Id))
                    {
                        acceptedSnapshot = BuildSnapshotLocked();
                    }
                    else if (runAlreadyStarted)
                    {
                        RemoveQueuedRunMappingByMessageIdLocked(threadId, request.Id);
                    }
                }
                else if (_resetAwaitingUserMessage.Contains(threadId))
                {
                    RemoveQueuedRunMappingByRunIdLocked(threadId, request.SendRunId);
                    _resetLocalSendWithoutRunVersions[threadId] = dispatch.ResetVersion;
                    _resetLocalSendWithoutRunStartSequences[threadId] = dispatch.StartedLifecycleSequence;
                    TryOpenResetGateFromPendingLifecycleLocked(threadId, acceptedRunId: null);
                    if (PromoteQueuedMessageLocked(threadId, request.Id))
                    {
                        acceptedSnapshot = BuildSnapshotLocked();
                    }
                }
                else if (PromoteQueuedMessageLocked(threadId, request.Id))
                {
                    RemoveQueuedRunMappingByRunIdLocked(threadId, request.SendRunId);
                    acceptedSnapshot = BuildSnapshotLocked();
                }
            }

            if (acceptedSnapshot is not null)
            {
                Publish(acceptedSnapshot);
            }
            if (requeuedSnapshot is not null)
            {
                Publish(requeuedSnapshot);
            }
            if (retryDeferredSend)
            {
                ScheduleQueuedSendDrain(threadId, deferredRetryDelay);
            }

            if (staleRunIdToAbort is not null)
            {
                _telemetry.CompletePreparedTurn(staleCompletion);
                try
                {
                    Logger.Info($"[Reset] Aborting late pre-reset send runId='{staleRunIdToAbort}' threadId='{threadId}'");
                    await _bridge.SendChatAbortAsync(staleRunIdToAbort, threadId);
                }
                catch (Exception abortEx)
                {
                    Logger.Warn($"[Reset] Failed to abort late pre-reset send runId='{staleRunIdToAbort}': {abortEx.Message}");
                }
            }

            if (hasAttachments && sendStillCurrent)
                CacheAttachmentMeta(dispatch.SessionId, threadId, request.Text, request.Attachments!, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), dispatch.ResetVersion);
        }
        catch (Exception ex)
        {
            _telemetry.FinishSendAttempt(
                sendOperation,
                ChatAdmissionTelemetryStatus.Exception,
                ex is OperationCanceledException
                    ? ChatTelemetryOutcome.Canceled
                    : ChatTelemetryOutcome.Failure,
                ex);
            bool sendStillCurrent;
            ChatTelemetryTracker.PreparedTurnCompletion? rejectedCompletion = null;
            ChatDataSnapshot? failureSnapshot = null;
            lock (_gate)
            {
                sendStillCurrent = GetResetVersionLocked(threadId) == dispatch.ResetVersion;
                if (sendStillCurrent)
                {
                    rejectedCompletion = _telemetry.PrepareFinishByMessageId(
                        request.Id,
                        ex is OperationCanceledException
                            ? ChatTelemetryOutcome.Canceled
                            : ChatTelemetryOutcome.Failure,
                        ChatTurnTelemetryReason.SendRejected);
                    RemovePendingLocalEchoLocked(threadId, request.Id);
                    MarkQueuedMessageFailedLocked(threadId, request.Id, ex.Message);
                    RemoveQueuedSendRequestLocked(threadId, request.Id);
                    RemoveQueuedRunMappingByMessageIdLocked(threadId, request.Id);
                    if (!HasSendingQueuedMessagesLocked(threadId))
                        _locallyInitiatedThreads.Remove(threadId);
                    failureSnapshot = ApplyEventLocked(
                        threadId,
                        TruncateChatEvent(new ChatErrorEvent(string.Format(
                            CultureInfo.CurrentCulture,
                            LocalizationHelper.GetString("Chat_Error_SendFailedFormat"),
                            ex.Message))),
                        meta: null);
                    failureSnapshot = ApplyEventLocked(threadId, new ChatTurnEndEvent(), meta: null);
                }
            }

            if (!sendStillCurrent)
                return;

            _telemetry.CompletePreparedTurn(rejectedCompletion);
            Logger.Warn($"[Queue] chat.send failed threadId='{threadId}' queuedMessageId='{request.Id}' sendRunId='{request.SendRunId}': {ex.Message}");
            // Surface as an error in the timeline + notification, while the
            // failed queue card keeps the attempted text visible for retry/edit.
            Publish(failureSnapshot!);
            RaiseNotification(new ChatProviderNotification(
                ChatProviderNotificationKind.Error, threadId, LocalizationHelper.GetString("Chat_Notification_SendFailed"), ex.Message));
            TryDispatchNextQueuedSend(threadId);
            if (rethrow)
                throw;
        }
    }

    public async Task StopResponseAsync(string threadId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? runId;
        bool hadActiveTurn;
        lock (_gate)
        {
            _activeRunIds.TryGetValue(threadId, out runId);
            hadActiveTurn = _timelines.TryGetValue(threadId, out var tl) && tl.TurnActive;

            // Suppress all incoming messages for this thread until the next user send.
            _abortedThreads.Add(threadId);

            if (!string.IsNullOrEmpty(runId))
                _abortedRunIds.Add(runId);
            else
            {
                _pendingAbortCounts.TryGetValue(threadId, out var count);
                _pendingAbortCounts[threadId] = count + 1;
            }

            _telemetry.FinishActiveTurn(
                threadId,
                ChatTelemetryOutcome.Canceled,
                ChatTurnTelemetryReason.AbortRequested);
        }

        Logger.Info($"[ABORT] StopResponseAsync threadId='{threadId}' runId='{runId ?? "(null)"}' hadActiveTurn={hadActiveTurn} deferred={string.IsNullOrEmpty(runId)}");

        if (!string.IsNullOrEmpty(runId))
        {
            try
            {
                Logger.Info($"[ABORT] Sending chat.abort for runId='{runId}'");
                await _bridge.SendChatAbortAsync(runId, threadId);
                Logger.Info($"[ABORT] chat.abort sent successfully");
            }
            catch (Exception ex)
            {
                // Abort RPC failed — clear suppression so the thread isn't permanently blocked.
                lock (_gate)
                {
                    _abortedThreads.Remove(threadId);
                    _abortedRunIds.Remove(runId);
                    _activeRunIds.Remove(threadId);
                    _activeRunStartSequences.Remove(threadId);
                    if (!HasSendingQueuedMessagesLocked(threadId))
                        _locallyInitiatedThreads.Remove(threadId);
                }
                Logger.Warn($"[ABORT] chat.abort failed, cleared suppression: {ex.Message}");
                RaiseNotification(new ChatProviderNotification(
                    ChatProviderNotificationKind.Error, threadId, LocalizationHelper.GetString("Chat_Notification_AbortFailed"), ex.Message));
                ApplyEventAndPublish(threadId, new ChatTurnEndEvent());
                return;
            }
        }
        else
        {
            Logger.Info($"[ABORT] No runId yet — queued pending abort for threadId='{threadId}'");
        }

        // Persist is handled by the deferred abort path (lifecycle.start or
        // lifecycle.end) which runs after the gateway has recorded the message.

        // If there was a real in-flight turn, mark the partial assistant text
        // as aborted so users can tell it isn't a complete response (per spec
        // Edge Cases — "Aborted runs: Show with abort indicator").
        if (hadActiveTurn)
        {
            ApplyEventAndPublish(threadId, new ChatStatusEvent("Aborted", ChatTone.Warning));
        }

        lock (_gate)
        {
            if (!string.IsNullOrEmpty(runId))
            {
                _activeRunIds.Remove(threadId);
                _activeRunStartSequences.Remove(threadId);
            }
            _abortedThreads.Remove(threadId);
            if (!HasSendingQueuedMessagesLocked(threadId))
                _locallyInitiatedThreads.Remove(threadId);
        }

        // Always clear local "turn active" state — the gateway will emit a
        // lifecycle.end if the abort succeeds, but we want the UI to reflect
        // the user's intent immediately.
        ApplyEventAndPublish(threadId, new ChatTurnEndEvent());
    }

    /// <summary>
    /// Fetch the conversation transcript for <paramref name="threadId"/> from
    /// the gateway (via <c>chat.history</c>) and fold it into the local
    /// timeline. Idempotent — the first successful call per thread populates
    /// the timeline; subsequent calls are no-ops unless <paramref name="force"/>
    /// is true. Safe to call from any thread.
    /// </summary>
    public async Task LoadHistoryAsync(string threadId, bool force = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(threadId)) return;

        long requestResetVersion;
        long requestConnectionVersion;
        lock (_gate)
        {
            if (!force && _historyLoaded.Contains(threadId)) return;
            if (!_historyInFlight.Add(threadId)) return; // another loader already in progress
            requestResetVersion = GetResetVersionLocked(threadId);
            requestConnectionVersion = _historyConnectionVersion;
        }

        var historyOperation = _telemetry.StartHistoryLoad(
            force ? ChatHistoryTelemetrySource.Forced : ChatHistoryTelemetrySource.Initial);
        var historyOutcome = ChatTelemetryOutcome.Success;
        Exception? historyException = null;
        try
        {
            var history = await _bridge.RequestChatHistoryAsync(threadId);

            ChatDataSnapshot snapshot;
            lock (_gate)
            {
                if (_historyConnectionVersion != requestConnectionVersion ||
                    GetResetVersionLocked(threadId) != requestResetVersion)
                {
                    Logger.Info($"[ChatHistory] Ignoring stale history for thread '{threadId}'");
                    historyOutcome = ChatTelemetryOutcome.Canceled;
                    return;
                }

                if (!string.IsNullOrEmpty(history.SessionId))
                    _sessionIds[threadId] = history.SessionId!;

                // Rebuild timeline from history; preserve any in-flight turn
                // entries that arrived between the request and the response by
                // appending them after the historical entries.
                var prior = GetOrCreateTimelineLocked(threadId);
                var rebuilt = ChatTimelineState.Initial() with { HistoryLoaded = true };

                // Prefer the gateway's per-session sequence over timestamps.
                // Spam/queue bursts can produce persisted rows whose timestamps
                // don't reflect the actual processing order; __openclaw.seq is
                // the stable transcript order when present.
                var orderedItems = history.Messages
                    .Select((m, i) => (Message: m, Index: i))
                    .ToList();
                var ordered = OrderHistoryMessages(orderedItems);

                // Build per-entry metadata in lockstep with the reducer.
                var rebuiltMeta = new Dictionary<string, ChatEntryMetadata>();
                var session = Array.Find(_sessions, s => s.Key == threadId);
                var modelAtLoad = session?.Model;

                ChatTimelineState ApplyAndCaptureMeta(ChatTimelineState s, ChatEvent e, ChatEntryMetadata? meta)
                {
                    var beforeIds = new HashSet<string>(s.Entries.Count);
                    for (int i = 0; i < s.Entries.Count; i++) beforeIds.Add(s.Entries[i].Id);
                    var nextState = ChatTimelineReducer.Apply(s, e);
                    if (meta is not null)
                    {
                        for (int i = 0; i < nextState.Entries.Count; i++)
                        {
                            var id = nextState.Entries[i].Id;
                            if (!beforeIds.Contains(id) && !rebuiltMeta.ContainsKey(id))
                                rebuiltMeta[id] = meta;
                        }
                    }
                    return nextState;
                }

                Logger.Info($"[ChatHistory] Loading thread '{threadId}' — {ordered.Count} messages from gateway");

                // Load cached tool metadata for this session to restore tool names
                // that the gateway strips from history responses.
                var cachedTools = GetCachedToolMetaForSession(history.SessionId, threadId);
                if (cachedTools is not null)
                    Logger.Info($"[ChatHistory] Found {cachedTools.Count} cached tool metadata entries for session");

                bool nextAssistantIsAborted = false;
                var attachmentMatcher = CreateAttachmentMetaMatcher(history.SessionId, threadId);

                foreach (var msg in ordered)
                {
                    var roleLower = msg.Role?.ToLowerInvariant() ?? "";
                    var rawText = msg.Text ?? string.Empty;
                    var ts = msg.Ts > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(msg.Ts).ToLocalTime()
                        : (DateTimeOffset?)null;
                    var msgMeta = new ChatEntryMetadata(
                        ts,
                        modelAtLoad,
                        msg.InputTokens,
                        msg.OutputTokens,
                        msg.ResponseTokens,
                        msg.ContextPercent,
                        GatewayMessageId: msg.OpenClawId,
                        OpenClawSeq: msg.OpenClawSeq);

                    // Cap per-message text up front so heuristics, logging,
                    // and the reducer all see the same bounded value
                    // (chat rubber-duck MEDIUM 4).
                    var text = TruncateForChatEntry(EscapeUntrustedAttachmentMarkerLines(rawText));
                    if (roleLower == "user")
                        text = RehydrateAttachmentMarkers(attachmentMatcher, text, msg.Ts);

                    if (string.IsNullOrEmpty(text)) continue;

                    // Check if this user message was aborted (persisted __openclaw.id match)
                    if (roleLower == "user")
                    {
                        Logger.Debug($"[ChatHistory] user msg OpenClawId='{msg.OpenClawId ?? "(null)"}' seq={msg.OpenClawSeq}");
                        if (IsMessageAborted(threadId, msg.OpenClawId))
                            nextAssistantIsAborted = true;
                    }

                    // Check if the gateway itself flagged this as an aborted response
                    bool gatewayAborted = roleLower == "assistant" &&
                        !string.IsNullOrEmpty(msg.StopReason) &&
                        !string.Equals(msg.StopReason, "stop", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(msg.StopReason, "toolUse", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(msg.StopReason, "end_turn", StringComparison.OrdinalIgnoreCase);

                    bool shouldMarkAborted = (roleLower == "assistant" && nextAssistantIsAborted) || gatewayAborted;
                    if (roleLower == "assistant") nextAssistantIsAborted = false; // reset after consuming

                    // Diagnostic: log shape (role + length + heuristic flags) only.
                    // Never log the message text — see HIGH 4 logging audit.
                    var isFlat = LooksLikeFlattenedToolOutput(text);
                    var isSys  = LooksLikeSystemControlNote(text);
                    Logger.Debug($"[ChatHistory] role='{roleLower}' len={text.Length} flat={isFlat} sys={isSys} aborted={shouldMarkAborted}");

                    switch (roleLower)
                    {
                        case "user":
                            // Approval slash commands ("/approve <slug> allow-once",
                            // "/deny <slug>") are transport, not user prose. On
                            // history replay we render them as a dim audit-trail
                            // status entry so the user can scroll back and see
                            // that an approval decision was made on this thread
                            // (whether by us or another client — origin is
                            // indistinguishable on replay).
                            if (LooksLikeApprovalSlashCommand(text))
                            {
                                Logger.Debug($"[ChatHistory]   → routed: AUDIT (approval slash command, dim status)");
                                rebuilt = ApplyAndCaptureMeta(
                                    rebuilt,
                                    new ChatStatusEvent(text, ChatTone.Dim),
                                    msgMeta);
                                break;
                            }
                            // System-injected notes (the gateway sometimes wraps
                            // exec result reports in ``System (untrusted): ...``
                            // and sends them as role=user) — render dim instead
                            // of as a giant user bubble. See the ChatHistory log.
                            if (LooksLikeSystemControlNote(text))
                            {
                                Logger.Debug($"[ChatHistory]   → routed: SYSTEM (dim status, role=user with control prefix)");
                                rebuilt = ApplyAndCaptureMeta(
                                    rebuilt,
                                    new ChatStatusEvent(text, ChatTone.Dim),
                                    msgMeta);
                                break;
                            }
                            // ApplyUserMessage will set TurnActive=true; if the previous
                            // assistant turn never received a turn-end (because the
                            // gateway transcript doesn't emit one explicitly), clear
                            // ActiveAssistantId here so the next assistant message
                            // starts a fresh entry instead of overwriting the previous.
                            rebuilt = rebuilt with { ActiveAssistantId = null, ActiveReasoningId = null };
                            rebuilt = ApplyAndCaptureMeta(rebuilt, new ChatUserMessageEvent(text), msgMeta);
                            break;

                        case "assistant":
                            if (ChatMessageInfo.IsSilentAssistantDirective(roleLower, text))
                            {
                                Logger.Debug("[ChatHistory]   → routed: SILENT assistant directive");
                                break;
                            }

                            // If this assistant response was aborted, show a placeholder
                            // instead of the actual (partial) content.
                            if (shouldMarkAborted)
                            {
                                Logger.Debug($"[ChatHistory]   → routed: ABORTED (response was stopped)");
                                rebuilt = ApplyAndCaptureMeta(
                                    rebuilt,
                                    new ChatStatusEvent("Response was stopped", ChatTone.Warning),
                                    msgMeta);
                                rebuilt = ChatTimelineReducer.Apply(rebuilt, new ChatTurnEndEvent());
                                break;
                            }
                            // ── Heuristic recovery for history-flattened tool calls ──
                            // The gateway strips ``stream:"item"`` / ``command_output``
                            // detail server-side when serving ``chat.history`` —
                            // raw exec output is replayed as plain assistant text.
                            // Detect these telltale shapes and route them through
                            // the chip pipeline so historic turns look like live ones.
                            if (LooksLikeSystemControlNote(text))
                            {
                                Logger.Debug($"[ChatHistory]   → routed: SYSTEM (dim status)");
                                rebuilt = ApplyAndCaptureMeta(
                                    rebuilt,
                                    new ChatStatusEvent(text, ChatTone.Dim),
                                    msgMeta);
                                break;
                            }
                            if (LooksLikeFlattenedToolOutput(text))
                            {
                                var cached = TryMatchCachedTool(cachedTools, msg.Ts);
                                var kind = cached?.ToolName ?? ClassifyFlattenedToolOutput(text);
                                var label = cached?.Label ?? ExtractFlattenedToolSummary(text);
                                Logger.Debug($"[ChatHistory]   → routed: TOOL chip kind='{kind}' cached={cached is not null}");
                                rebuilt = ApplyAndCaptureMeta(
                                    rebuilt,
                                    new ChatToolStartEvent(label, kind),
                                    msgMeta);
                                rebuilt = ApplyAndCaptureMeta(
                                    rebuilt,
                                    new ChatToolOutputEvent(text),
                                    msgMeta);
                                break;
                            }
                            Logger.Debug($"[ChatHistory]   → routed: ASSISTANT bubble (no flatten/system match)");
                            rebuilt = ApplyAndCaptureMeta(rebuilt, new ChatMessageEvent(RepairContentBlockSeams(text)), msgMeta);
                            // End the turn so the next assistant message starts a new
                            // entry rather than replacing this one (UpsertAssistant
                            // upserts by ActiveAssistantId, which TurnEnd clears).
                            rebuilt = ChatTimelineReducer.Apply(rebuilt, new ChatTurnEndEvent());
                            break;

                        case "toolresult":
                        case "tool_result":
                            // Verified empirically — gateway 2026.4.x emits
                            // ``role: "toolresult"`` for shell/exec tool output
                            // in chat.history (not the spec's ``"tool"``).
                            // Always route to a chip pair regardless of whether
                            // the heuristic fires, since the role itself confirms
                            // it's tool output.
                            {
                                var cached = TryMatchCachedTool(cachedTools, msg.Ts);
                                var kind = cached?.ToolName ?? ClassifyFlattenedToolOutput(text);
                                var label = cached?.Label ?? ExtractFlattenedToolSummary(text);
                                Logger.Debug($"[ChatHistory]   → routed: TOOL chip (role=toolresult, kind='{kind}' cached={cached is not null})");
                                rebuilt = ApplyAndCaptureMeta(
                                    rebuilt,
                                    new ChatToolStartEvent(label, kind),
                                    msgMeta);
                                rebuilt = ApplyAndCaptureMeta(
                                    rebuilt,
                                    new ChatToolOutputEvent(text),
                                    msgMeta);
                            }
                            break;

                        case "system":
                        case "tool":
                            // Render system / tool transcript notes as muted Status
                            // entries so they're visible but de-emphasized vs. the
                            // user/assistant turn flow.
                            Logger.Debug($"[ChatHistory]   → routed: STATUS (role={roleLower})");
                            rebuilt = ApplyAndCaptureMeta(
                                rebuilt,
                                new ChatStatusEvent(text, ChatTone.Dim),
                                msgMeta);
                            break;

                        default:
                            // Unknown role — fall back to assistant rendering so it's
                            // at least visible. Bracket with TurnEnd to avoid
                            // collapsing into adjacent assistant entries.
                            Logger.Debug($"[ChatHistory]   → routed: ASSISTANT (unknown role '{roleLower}', fallback)");
                            rebuilt = ApplyAndCaptureMeta(rebuilt, new ChatMessageEvent(RepairContentBlockSeams(text)), msgMeta);
                            rebuilt = ChatTimelineReducer.Apply(rebuilt, new ChatTurnEndEvent());
                            break;
                    }
                }
                // If the last user message was aborted but there's no subsequent
                // assistant message in history (gateway didn't record one), synthesize
                // the "Response was stopped" indicator so the user sees it.
                if (nextAssistantIsAborted)
                {
                    Logger.Debug("[ChatHistory] Trailing aborted user message with no assistant response — synthesizing abort indicator");
                    rebuilt = ApplyAndCaptureMeta(
                        rebuilt,
                        new ChatStatusEvent("Response was stopped", ChatTone.Warning),
                        null);
                    rebuilt = ChatTimelineReducer.Apply(rebuilt, new ChatTurnEndEvent());
                }

                // Final safety: ensure no lingering active turn after history load.
                rebuilt = rebuilt with { TurnActive = false, ActiveAssistantId = null, ActiveReasoningId = null };

                // Append any prior live entries that weren't part of history.
                // Dedup rules (HIGH 2 / rubber-duck round 2):
                //   1. ID-only dedup is a no-op here because both rebuilt and
                //      prior assign sequential e{n} IDs that always collide;
                //      treat collisions as coincidences and re-id them.
                //   2. Content+timestamp dedup: only when BOTH sides have a
                //      non-zero timestamp AND they agree within 2 seconds.
                //   3. If either side's timestamp is missing/zero, KEEP the
                //      live entry — visible duplication beats silent loss.
                if (prior.Entries.Count > 0)
                {
                    var priorMeta = _entryMeta.TryGetValue(threadId, out var pm)
                        ? pm
                        : new Dictionary<string, ChatEntryMetadata>();

                    static string ContentKey(ChatTimelineItemKind kind, string text) => $"{kind}|{text}";
                    static string SequenceKey(ChatTimelineItemKind kind, int sequence) => $"{kind}|{sequence}";

                    // (kind|text) → list of unix-second timestamps for rebuilt
                    // entries that have a real timestamp. Only these can match.
                    var rebuiltContentTimestamps = new Dictionary<string, List<long>>(StringComparer.Ordinal);
                    var rebuiltMessageIds = new HashSet<string>(StringComparer.Ordinal);
                    var rebuiltSequenceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var entry in rebuilt.Entries)
                    {
                        rebuiltMeta.TryGetValue(entry.Id, out var em);
                        if (!string.IsNullOrEmpty(em?.GatewayMessageId))
                            rebuiltMessageIds.Add(em.GatewayMessageId);
                        if (em?.OpenClawSeq is { } seq)
                            IncrementCount(rebuiltSequenceCounts, SequenceKey(entry.Kind, seq));
                        if (em?.Timestamp is { } rts && rts != default)
                        {
                            var key = ContentKey(entry.Kind, entry.Text);
                            if (!rebuiltContentTimestamps.TryGetValue(key, out var list))
                                rebuiltContentTimestamps[key] = list = new List<long>();
                            list.Add(rts.ToUnixTimeSeconds());
                        }
                    }

                    var existingIds = new HashSet<string>(StringComparer.Ordinal);
                    var maxSuffix = 0;
                    foreach (var entry in rebuilt.Entries)
                    {
                        existingIds.Add(entry.Id);
                        if (entry.Id.Length > 1 && entry.Id[0] == 'e' &&
                            int.TryParse(entry.Id.AsSpan(1), out var n) && n > maxSuffix)
                            maxSuffix = n;
                    }

                    var nextId = Math.Max(rebuilt.NextId, maxSuffix + 1);
                    var newEntries = rebuilt.Entries.ToBuilder();
                    var skippedDup = 0;
                    var reidCount = 0;

                    foreach (var entry in prior.Entries)
                    {
                        priorMeta.TryGetValue(entry.Id, out var em);
                        var priorTs = em?.Timestamp;
                        if (!string.IsNullOrEmpty(em?.GatewayMessageId) &&
                            rebuiltMessageIds.Contains(em.GatewayMessageId))
                        {
                            ConsumeAnyTimestamp(rebuiltContentTimestamps, ContentKey(entry.Kind, entry.Text));
                            skippedDup++;
                            continue;
                        }

                        if (em?.OpenClawSeq is { } seq &&
                            TryConsumeCount(rebuiltSequenceCounts, SequenceKey(entry.Kind, seq)))
                        {
                            ConsumeAnyTimestamp(rebuiltContentTimestamps, ContentKey(entry.Kind, entry.Text));
                            skippedDup++;
                            continue;
                        }

                        // Rule 2: content+timestamp dedup only when BOTH sides
                        // have valid timestamps within 2 seconds. Otherwise
                        // (Rule 3) fall through and keep the entry — silent
                        // data loss is worse than visible duplicates.
                        if (priorTs is { } pts && pts != default &&
                            rebuiltContentTimestamps.TryGetValue(ContentKey(entry.Kind, entry.Text), out var rebuiltTimes))
                        {
                            var priorSec = pts.ToUnixTimeSeconds();
                            var matched = false;
                            for (var rebIndex = 0; rebIndex < rebuiltTimes.Count; rebIndex++)
                            {
                                var rebSec = rebuiltTimes[rebIndex];
                                if (Math.Abs(rebSec - priorSec) <= 2)
                                {
                                    rebuiltTimes.RemoveAt(rebIndex);
                                    matched = true;
                                    break;
                                }
                            }
                            if (matched)
                            {
                                skippedDup++;
                                continue;
                            }
                        }

                        // Re-id on collision (sequential IDs always collide
                        // between rebuilt and prior).
                        var entryToAdd = entry;
                        if (existingIds.Contains(entry.Id))
                        {
                            var newId = $"e{nextId++}";
                            entryToAdd = entry with { Id = newId };
                            reidCount++;
                        }
                        else if (entry.Id.Length > 1 && entry.Id[0] == 'e' &&
                                 int.TryParse(entry.Id.AsSpan(1), out var nn) && nn >= nextId)
                        {
                            // Bump nextId past this entry's suffix to avoid future collisions.
                            nextId = nn + 1;
                        }

                        newEntries.Add(entryToAdd);
                        existingIds.Add(entryToAdd.Id);
                        if (em?.Timestamp is { } addTs && addTs != default)
                        {
                            var key = ContentKey(entryToAdd.Kind, entryToAdd.Text);
                            if (!rebuiltContentTimestamps.TryGetValue(key, out var list))
                                rebuiltContentTimestamps[key] = list = new List<long>();
                            list.Add(addTs.ToUnixTimeSeconds());
                        }
                        if (!string.IsNullOrEmpty(em?.GatewayMessageId))
                            rebuiltMessageIds.Add(em.GatewayMessageId);
                        if (em?.OpenClawSeq is { } addSeq)
                            IncrementCount(rebuiltSequenceCounts, SequenceKey(entryToAdd.Kind, addSeq));
                        if (em is not null && !rebuiltMeta.ContainsKey(entryToAdd.Id))
                            rebuiltMeta[entryToAdd.Id] = em;
                    }

                    if (skippedDup > 0 || reidCount > 0)
                        Logger.Debug($"[ChatHistory] dedup: skipped={skippedDup} reid={reidCount} prior={prior.Entries.Count}");

                    rebuilt = rebuilt with
                    {
                        Entries = newEntries.ToImmutable(),
                        NextId = nextId,
                        TurnActive = prior.TurnActive
                    };
                }

                _timelines[threadId] = rebuilt;
                _entryMeta[threadId] = rebuiltMeta;
                _historyLoaded.Add(threadId);
                _historyRetryCount.Remove(threadId);
                snapshot = BuildSnapshotLocked();
            }
            Publish(snapshot);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_historyConnectionVersion != requestConnectionVersion)
                {
                    historyOutcome = ChatTelemetryOutcome.Canceled;
                    return;
                }
            }

            historyOutcome = ex is OperationCanceledException
                ? ChatTelemetryOutcome.Canceled
                : ChatTelemetryOutcome.Failure;
            historyException = ex;
            RaiseNotification(new ChatProviderNotification(
                ChatProviderNotificationKind.Error, threadId, LocalizationHelper.GetString("Chat_Notification_LoadHistoryFailed"), ex.Message));

            // If still connected and under the retry limit, retry after a
            // short delay so the UI auto-recovers when the gateway becomes
            // ready to serve history.
            bool shouldRetry;
            lock (_gate)
            {
                _historyRetryCount.TryGetValue(threadId, out var retries);
                shouldRetry = _status == ConnectionStatus.Connected
                              && !_historyLoaded.Contains(threadId)
                              && retries < MaxHistoryRetries;
                if (shouldRetry)
                    _historyRetryCount[threadId] = retries + 1;
            }
            if (shouldRetry)
            {
                _ = _scheduleHistoryRetry(HistoryRetryDelay, async () =>
                {
                    lock (_gate)
                    {
                        if (_historyConnectionVersion != requestConnectionVersion ||
                            _status != ConnectionStatus.Connected)
                        {
                            return;
                        }
                    }
                    await LoadHistoryAsync(threadId, force: true);
                });
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_historyConnectionVersion == requestConnectionVersion)
                    _historyInFlight.Remove(threadId);
            }
            _telemetry.FinishHistoryLoad(historyOperation, historyOutcome, historyException);
        }
    }

    public Task SetThreadSuspendedAsync(string threadId, bool suspended, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask; // Not supported by gateway — no-op.
    }

    public Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask; // Not supported by gateway — no-op.
    }

    public async Task SetModelAsync(string threadId, string model, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The gateway's sessions.patch schema treats `model` as a non-empty
        // string; a blank value here is a no-op rather than a clear. Use
        // ClearModelAsync to revert a session to the gateway default.
        if (string.IsNullOrWhiteSpace(model)) return;
        await TrackModelPatchAsync(threadId, () => _bridge.PatchSessionModelAsync(threadId, model));
    }

    public async Task ClearModelAsync(string threadId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Tri-state clear: removes the session's model override (explicit null)
        // so it tracks the gateway/agent default again.
        await TrackModelPatchAsync(threadId, () => _bridge.ClearSessionModelAsync(threadId));
    }

    private async Task TrackModelPatchAsync(string threadId, Func<Task> patchOperation)
    {
        var startSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? previous;
        Task pending;
        lock (_gate)
        {
            _pendingModelPatches.TryGetValue(threadId, out previous);
            pending = RunModelPatchAsync(previous, patchOperation, startSignal.Task);
            _pendingModelPatches[threadId] = pending;
        }

        startSignal.SetResult();
        try
        {
            await pending;
        }
        finally
        {
            lock (_gate)
            {
                if (_pendingModelPatches.TryGetValue(threadId, out var current)
                    && ReferenceEquals(current, pending))
                    _pendingModelPatches.Remove(threadId);
            }
        }
    }

    private static async Task RunModelPatchAsync(Task? previous, Func<Task> patchOperation, Task startSignal)
    {
        await startSignal;
        if (previous is not null)
        {
            try { await previous; }
            catch (Exception ex)
            {
                Logger.Debug($"ChatDataProvider: continuing model patch after previous patch failed: {ex.Message}");
            }
        }

        await patchOperation();
    }

    private async Task AwaitPendingModelPatchAsync(string threadId, CancellationToken cancellationToken)
    {
        Task? pending;
        lock (_gate)
        {
            _pendingModelPatches.TryGetValue(threadId, out pending);
        }

        if (pending is not null)
        {
            try { await pending.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.Debug($"ChatDataProvider: continuing send after model patch failed: {ex.Message}");
            }
        }
    }

    public async Task SetThinkingLevelAsync(string threadId, string thinkingLevel, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _bridge.PatchSessionThinkingLevelAsync(threadId, thinkingLevel);
    }

    public async Task EnsureCommandCatalogAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int epoch;
        lock (_gate)
        {
            // Only fetch while connected — the command catalog is a property of
            // the live gateway connection. A not-connected caller would just
            // land in the catch below.
            if (_status != ConnectionStatus.Connected)
                return;
            // Already loaded (or a fetch is running) → reuse the cached catalog
            // rather than hammering commands.list every time the palette opens.
            // A reconnect clears _commandCatalog (see OnStatusChanged), so a
            // fresh fetch happens after reconnect.
            if (_commandsFetchInFlight || _commandCatalog is not null)
                return;
            _commandsFetchInFlight = true;
            // Capture the connection epoch BEFORE the await. If a disconnect (or
            // reconnect) happens while ListCommandsAsync is in flight,
            // OnStatusChanged bumps the epoch; the late result is then discarded
            // rather than resurrecting a stale catalog for the new connection.
            epoch = _commandsEpoch;
        }

        CommandCatalog catalog;
        try
        {
            // Chat composer slash completion can only insert text-invokable
            // commands. Request the protocol's text scope so native-only
            // commands never surface in the composer catalog.
            catalog = await _bridge.ListCommandsAsync(new CommandCatalogQuery { Scope = "text" }).ConfigureAwait(false)
                      ?? new CommandCatalog { IsSupported = true };
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ChatProvider] EnsureCommandCatalogAsync failed: {ex.Message}");
            var shouldPublishFallback = false;
            lock (_gate)
            {
                // Only publish a fallback if no status change superseded this
                // fetch. A failure must still move the UI out of its "loading"
                // state; otherwise slash-leading text would keep trapping Enter
                // until reconnect. Treat the catalog as temporarily unavailable
                // for this connection and let reconnect clear/refetch it.
                if (epoch == _commandsEpoch && _status == ConnectionStatus.Connected)
                {
                    _commandsFetchInFlight = false;
                    _commandCatalog = new CommandCatalog { IsSupported = false };
                    shouldPublishFallback = true;
                }
            }
            if (shouldPublishFallback)
                PublishCommandCatalogIfFresh(epoch);
            return;
        }

        lock (_gate)
        {
            // Drop the result if the connection changed during the await.
            if (epoch != _commandsEpoch || _status != ConnectionStatus.Connected)
                return;
            _commandsFetchInFlight = false;
            _commandCatalog = catalog;
        }
        Logger.Info($"[ChatProvider] commands.list: supported={catalog.IsSupported} count={catalog.Commands.Count}");
        // Re-validate freshness at UI-thread delivery time rather than
        // publishing a snapshot captured under the lock above. This closes the
        // window where a disconnect occurring between snapshot build and
        // Publish could let a stale "connected + commands" snapshot arrive after
        // the disconnect snapshot.
        PublishCommandCatalogIfFresh(epoch);
    }

    /// <summary>
    /// Publishes a freshly-built snapshot on the UI thread, but only if the
    /// connection <paramref name="epoch"/> captured for this commands.list fetch
    /// is still current when delivery runs. If a disconnect/reconnect superseded
    /// the fetch in the meantime, the stale publish is dropped (the status
    /// handler's own publish carries the authoritative state).
    /// </summary>
    private void PublishCommandCatalogIfFresh(int epoch)
    {
        void Deliver()
        {
            ChatDataSnapshot snapshot;
            lock (_gate)
            {
                if (epoch != _commandsEpoch) return;
                snapshot = BuildSnapshotLocked();
            }
            Changed?.Invoke(this, new ChatDataChangedEventArgs(snapshot));
        }

        if (_post is null)
            Deliver();
        else
            _post(Deliver);
    }

    public Task SetPermissionModeAsync(string threadId, bool allowAll, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task RespondToPermissionAsync(string threadId, string requestId, bool allow, CancellationToken cancellationToken = default) =>
        RespondToPermissionAsync(
            threadId,
            requestId,
            allow ? ChatPermissionActionKeys.AllowOnce : ChatPermissionActionKeys.Deny,
            cancellationToken);

    public async Task RespondToPermissionAsync(string threadId, string requestId, string action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(threadId) || string.IsNullOrEmpty(requestId))
            return;

        var decision = NormalizeApprovalAction(action);
        if (TryResolveLocalInlineApproval(threadId, requestId, decision))
            return;

        // Use the operator-approvals gateway RPC (``exec.approval.resolve``)
        // rather than the ``/approve <id> <decision>`` chat slash command.
        //
        // Why: slash commands are processed as ordinary chat input on the
        // agent's main turn — but when an exec approval is pending, the agent
        // is BLOCKED waiting on that approval. The slash command therefore
        // sits in the input queue until the run times out, by which point the
        // approval has already expired and the approve/deny is a no-op. The
        // RPC bypasses the chat queue and resolves the approval immediately.
        Logger.Info($"[Approval] user response requestId={requestId} decision={decision} thread='{threadId}'");

        try
        {
            await _bridge.ResolveExecApprovalAsync(requestId, decision);
        }
        catch (Exception ex)
        {
            // Send failed: leave the Allow/Deny banner up so the user can
            // retry. Clearing it on failure would silently swallow the
            // problem and leave the agent waiting on an approval that the
            // user has no way to re-issue.
            Logger.Warn($"[Approval] response send failed requestId={requestId}: {ex.Message} (banner preserved for retry)");
            return;
        }

        ClearPendingPermissionAndPublish(threadId, expectedRequestId: requestId,
            decision: ChatDecisionForApprovalAction(decision));
    }

    internal async Task<ExecApprovalPromptDecision?> RequestLocalExecApprovalAsync(
        ExecApprovalPromptRequest request,
        CancellationToken cancellationToken = default,
        TimeSpan? approvalTimeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.SessionKey) || _disposed)
            return null;

        var threadId = request.SessionKey!;
        var requestId = !string.IsNullOrWhiteSpace(request.CorrelationId)
            ? $"local-{request.CorrelationId}"
            : $"local-{Guid.NewGuid():N}";
        var response = new TaskCompletionSource<ExecApprovalPromptDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detail = request.Command ?? string.Empty;
        var inline = new LocalInlineApproval(threadId, requestId, detail, response);

        ChatDataSnapshot snapshot;
        lock (_gate)
        {
            if (_disposed)
                return null;

            _localInlineApprovals[requestId] = inline;
            var meta = BuildLiveMetaLocked(threadId);
            snapshot = ApplyEventLocked(
                threadId,
                new ChatPermissionRequestEvent(
                    requestId,
                    LocalizationHelper.GetString("Chat_Permission_CommandApprovalTitle"),
                    request.Shell ?? "exec",
                    detail,
                    ChatPermissionActionKeys.ExecApprovalDefaults),
                meta);
        }
        Publish(snapshot);

        using var registration = cancellationToken.Register(() =>
            TryResolveLocalInlineApproval(threadId, requestId, ChatPermissionActionKeys.Deny));

        _ = ExpireLocalInlineApprovalAfterDelayAsync(threadId, requestId, approvalTimeout ?? LocalInlineApprovalTimeout);

        return await response.Task.ConfigureAwait(false);
    }

    private async Task ExpireLocalInlineApprovalAfterDelayAsync(string threadId, string requestId, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay).ConfigureAwait(false);
            TryExpireLocalInlineApproval(threadId, requestId);
        }
        catch (Exception ex)
        {
            Logger.Debug($"[Approval] inline approval timeout task failed: {ex.Message}");
        }
    }

    private bool TryExpireLocalInlineApproval(string threadId, string requestId)
    {
        LocalInlineApproval inline;
        ChatDataSnapshot snapshot;
        lock (_gate)
        {
            if (!_localInlineApprovals.TryGetValue(requestId, out var found) ||
                !string.Equals(found.ThreadId, threadId, StringComparison.Ordinal))
            {
                return false;
            }

            inline = found;
            _localInlineApprovals.Remove(requestId);
            var current = GetOrCreateTimelineLocked(threadId);
            _timelines[threadId] = ChatTimelineReducer.ResolvePermission(
                current,
                requestId,
                ChatPermissionDecision.Expired);
            snapshot = BuildSnapshotLocked();
        }

        Publish(snapshot);
        inline.Response.TrySetResult(ExecApprovalPromptDecision.TimedOut());
        return true;
    }

    private bool TryResolveLocalInlineApproval(string threadId, string requestId, string decision)
    {
        LocalInlineApproval inline;
        ChatDataSnapshot snapshot;
        lock (_gate)
        {
            if (!_localInlineApprovals.TryGetValue(requestId, out var found) ||
                !string.Equals(found.ThreadId, threadId, StringComparison.Ordinal))
            {
                return false;
            }

            inline = found;
            _localInlineApprovals.Remove(requestId);
            var current = GetOrCreateTimelineLocked(threadId);
            _timelines[threadId] = ChatTimelineReducer.ResolvePermission(
                current,
                requestId,
                ChatDecisionForApprovalAction(decision));
            var meta = BuildLiveMetaLocked(threadId);
            snapshot = ApplyEventLocked(
                threadId,
                new ChatStatusEvent(FormatApprovalResult(decision, inline.Detail, requestId), ApprovalToneForDecision(decision)),
                meta);
        }

        Publish(snapshot);
        inline.Response.TrySetResult(DecisionForApprovalAction(decision));
        return true;
    }

    private static ExecApprovalPromptDecision DecisionForApprovalAction(string decision) =>
        string.Equals(decision, ChatPermissionActionKeys.AllowAlways, StringComparison.OrdinalIgnoreCase)
            ? ExecApprovalPromptDecision.AlwaysAllow()
            : string.Equals(decision, ChatPermissionActionKeys.AllowOnce, StringComparison.OrdinalIgnoreCase)
                ? ExecApprovalPromptDecision.AllowOnce()
                : ExecApprovalPromptDecision.Deny();

    private static string FormatApprovalResult(string decision, string detail, string requestId)
        => string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            LocalizationHelper.GetString("Chat_Permission_ResultSubmittedFormat"),
            LabelForApprovalAction(decision),
            string.IsNullOrWhiteSpace(detail) ? requestId : detail);

    private static string LabelForApprovalAction(string decision)
    {
        if (string.Equals(decision, ChatPermissionActionKeys.AllowAlways, StringComparison.OrdinalIgnoreCase))
            return LocalizationHelper.GetString("Chat_Permission_AllowAlways");
        if (string.Equals(decision, ChatPermissionActionKeys.AllowOnce, StringComparison.OrdinalIgnoreCase))
            return LocalizationHelper.GetString("Chat_Permission_Allow");
        return LocalizationHelper.GetString("Chat_Permission_Deny");
    }

    private static ChatTone ApprovalToneForDecision(string decision)
        => string.Equals(decision, ChatPermissionActionKeys.Deny, StringComparison.OrdinalIgnoreCase)
            ? ChatTone.Warning
            : ChatTone.Success;

    private string NormalizeApprovalAction(string? action)
    {
        if (string.Equals(action, ChatPermissionActionKeys.AllowAlways, StringComparison.OrdinalIgnoreCase))
            return ChatPermissionActionKeys.AllowAlways;
        if (string.Equals(action, ChatPermissionActionKeys.AllowOnce, StringComparison.OrdinalIgnoreCase))
            return ChatPermissionActionKeys.AllowOnce;
        if (!string.Equals(action, ChatPermissionActionKeys.Deny, StringComparison.OrdinalIgnoreCase))
            Logger.Warn($"[Approval] unknown action '{action ?? "<null>"}'; defaulting to deny");
        return ChatPermissionActionKeys.Deny;
    }

    private static ChatPermissionDecision ChatDecisionForApprovalAction(string action)
        => string.Equals(action, ChatPermissionActionKeys.AllowAlways, StringComparison.OrdinalIgnoreCase)
            ? ChatPermissionDecision.AllowedAlways
            : string.Equals(action, ChatPermissionActionKeys.Deny, StringComparison.OrdinalIgnoreCase)
                ? ChatPermissionDecision.Denied
                : ChatPermissionDecision.Allowed;

    // expectedRequestId: when non-null, the clear is a no-op unless the
    // currently-pending banner's RequestId matches. This protects against
    // the responder-race where a fresh approval arrives between the
    // user's tap and the post-send clear.
    //
    // decision: terminal state to stamp on the matching inline timeline
    // entry. The user's local Allow/Deny click passes Allowed/Denied so
    // the bubble collapses to the correct badge immediately. The
    // backstop path triggered by the gateway echo passes Expired, which
    // only takes effect if the user hasn't already decided locally
    // (ResolvePermission protects already-decided entries).
    private void ClearPendingPermissionAndPublish(string threadId, string? expectedRequestId = null,
        ChatPermissionDecision decision = ChatPermissionDecision.Expired)
    {
        ChatDataSnapshot snapshot;
        lock (_gate)
        {
            var current = GetOrCreateTimelineLocked(threadId);
            if (current.PendingPermission is null)
            {
                Logger.Info($"[Approval] clear requested but no PendingPermission for thread='{threadId}'");
                return;
            }
            if (expectedRequestId is not null
                && !string.Equals(current.PendingPermission.RequestId, expectedRequestId, System.StringComparison.Ordinal))
            {
                Logger.Info($"[Approval] clear skipped — pending is '{current.PendingPermission.RequestId}', expected '{expectedRequestId}' (newer approval superseded)");
                return;
            }
            Logger.Info($"[Approval] clearing PendingPermission requestId='{current.PendingPermission.RequestId}' on thread='{threadId}' decision={decision}");
            _timelines[threadId] = ChatTimelineReducer.ResolvePermission(current, current.PendingPermission.RequestId, decision);
            snapshot = BuildSnapshotLocked();
        }
        Publish(snapshot);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        System.Threading.Timer? timerToDispose;
        System.Threading.Timer? chatStateTimerToDispose;
        List<LocalInlineApproval> pendingLocalApprovals;
        lock (_gate)
        {
            _telemetry.FinishAll(ChatTelemetryOutcome.Canceled, ChatTurnTelemetryReason.Disposed);
            timerToDispose = _toolMetaSaveTimer;
            _toolMetaSaveTimer = null;
            _toolMetaSaveVersion++;
            chatStateTimerToDispose = _lastChatStateSaveTimer;
            _lastChatStateSaveTimer = null;
            pendingLocalApprovals = _localInlineApprovals.Values.ToList();
            _localInlineApprovals.Clear();
            _queuedMessages.Clear();
            _queuedSendRequests.Clear();
            _queuedDrainScheduledThreads.Clear();
            _queuedMessageIdsByRunId.Clear();
            _terminalRunIdsByThread.Clear();
            _localSentTexts.Clear();
            _locallyInitiatedThreads.Clear();
            _resetSubmittedLocalEchoTexts.Clear();
        }
        foreach (var approval in pendingLocalApprovals)
            approval.Response.TrySetResult(ExecApprovalPromptDecision.Deny());
        timerToDispose?.Dispose();
        chatStateTimerToDispose?.Dispose();
        SaveToolMetaCache();
        _bridge.StatusChanged -= OnStatusChanged;
        _bridge.SessionsUpdated -= OnSessionsUpdated;
        _bridge.SessionCommandCompleted -= OnSessionCommandCompleted;
        _bridge.ChatMessageReceived -= OnChatMessageReceived;
        _bridge.AgentEventReceived -= OnAgentEventReceived;
        _bridge.ModelsListUpdated -= OnModelsListUpdated;
        _bridge.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Snapshot of per-entry metadata for one thread, defensively copied so
    /// callers (typically the renderer) can read it concurrently with future
    /// adapter mutations. Returns an empty dictionary if nothing is tracked.
    /// </summary>
    public IReadOnlyDictionary<string, ChatEntryMetadata> GetEntryMetadata(string threadId)
    {
        lock (_gate)
        {
            return _entryMeta.TryGetValue(threadId, out var m)
                ? new Dictionary<string, ChatEntryMetadata>(m)
                : new Dictionary<string, ChatEntryMetadata>();
        }
    }

    // ── Event handlers ──

    private void OnStatusChanged(object? sender, ConnectionStatus status)
    {
        ChatDataSnapshot snapshot;
        bool justReconnected;
        string[] threadsToInterrupt;
        lock (_gate)
        {
            justReconnected = status == ConnectionStatus.Connected
                              && _status != ConnectionStatus.Connected;
            // MEDIUM 5: detect Connected → Disconnected/Error transitions so
            // we can synthesise a turn-end + status entry on every thread that
            // had an in-flight turn (otherwise the UI sits "thinking" forever).
            var justDisconnected = (status == ConnectionStatus.Disconnected || status == ConnectionStatus.Error)
                                   && _status == ConnectionStatus.Connected;
            _status = status;

            // Reset the sessions-list-received gate whenever we leave the
            // Connected state. Any cached sessions belong to the previous
            // connection; the UI must treat the composer as not-yet-ready
            // until the next sessions.list arrives.
            if (status != ConnectionStatus.Connected)
                _sessionsListReceived = false;

            // Drop the cached command catalog whenever we leave Connected so a
            // reconnect re-fetches commands.list (the catalog can change across
            // gateways / agent reconfigurations). Bumping the epoch invalidates
            // any commands.list fetch still in flight so its late result is
            // discarded instead of resurrecting a stale catalog.
            if (status != ConnectionStatus.Connected)
            {
                _commandsEpoch++;
                _commandCatalog = null;
                _commandsFetchInFlight = false;
            }

            // Reset the approval-dedupe LRU on every transition out of
            // Connected. IDs from a prior session must not block a fresh
            // approval with a colliding slug from the next connection.
            if (justDisconnected)
                ResetApprovalDedupe();

            // On (re)connect, invalidate transcript freshness without fetching
            // every session. The selected-thread render path requests the one
            // transcript the user is viewing; other sessions remain metadata-only
            // until selected. Bumping the version also prevents responses from
            // the prior connection from overwriting a newly selected transcript.
            if (justReconnected)
            {
                _telemetry.FinishAll(ChatTelemetryOutcome.Canceled, ChatTurnTelemetryReason.Disconnected);
                _historyConnectionVersion++;
                _historyLoaded.Clear();
                _historyInFlight.Clear();
                _locallyInitiatedThreads.Clear();
                _localSentTexts.Clear();
                _queuedMessages.Clear();
                _queuedSendRequests.Clear();
                _queuedDrainScheduledThreads.Clear();
                _assistantFallbackPromotedThreads.Clear();
                _queuedMessageIdsByRunId.Clear();
                _terminalRunIdsByThread.Clear();
                _resetSubmittedLocalEchoTexts.Clear();
                _activeRunIds.Clear();
                _activeRunStartSequences.Clear();
                _historyRetryCount.Clear();
                // Reset keyless-event diagnostic so a fresh reconnect to a
                // still-broken gateway surfaces the notification again.
                System.Threading.Interlocked.Exchange(ref _keylessEventDiagnosticRaised, 0);
            }
            if (justDisconnected)
            {
                _telemetry.FinishAll(ChatTelemetryOutcome.Canceled, ChatTurnTelemetryReason.Disconnected);
                var list = new List<string>();
                foreach (var (key, tl) in _timelines)
                {
                    if (tl.TurnActive) list.Add(key);
                }
                threadsToInterrupt = list.ToArray();
                foreach (var threadId in threadsToInterrupt)
                {
                    _activeRunIds.Remove(threadId);
                    _activeRunStartSequences.Remove(threadId);
                }
            }
            else
            {
                threadsToInterrupt = Array.Empty<string>();
            }

            snapshot = BuildSnapshotLocked();
        }
        Publish(snapshot);

        // MEDIUM 5: synthesize the turn-end + status note for any threads
        // that were mid-turn when the connection dropped.
        var interruptedMsg = LocalizationHelper.GetString("Chat_Notification_ConnectionInterrupted");
        foreach (var threadId in threadsToInterrupt)
        {
            ApplyEventAndPublish(threadId, new ChatStatusEvent(interruptedMsg, ChatTone.Warning));
            ApplyEventAndPublish(threadId, new ChatTurnEndEvent());
        }

    }

    private void OnSessionsUpdated(object? sender, SessionInfo[] sessions)
    {
        ChatDataSnapshot snapshot;
        string[] queuedThreadsToDrain;
        lock (_gate)
        {
            var previousUsage = _sessions
                .Where(s => !string.IsNullOrEmpty(s.Key))
                .ToDictionary(s => s.Key, s => (s.InputTokens, s.OutputTokens, s.TotalTokens, s.ContextTokens));
            _sessions = sessions ?? Array.Empty<SessionInfo>();
            SeedSessionIdsFromSessionsLocked(_sessions);
            _sessionsListReceived = true;
            EnsureTimelinesForSessionsLocked();
            RememberLastSessionStateLocked();
            foreach (var s in _sessions)
            {
                if (string.IsNullOrEmpty(s.Key)) continue;
                var currentUsage = (s.InputTokens, s.OutputTokens, s.TotalTokens, s.ContextTokens);
                var usageChanged = !previousUsage.TryGetValue(s.Key, out var prevUsage)
                    || prevUsage != currentUsage;
                if (usageChanged)
                    SnapshotLatestAssistantUsageLocked(s, ResolveTimelineKeyForSessionLocked(s));
            }
            snapshot = BuildSnapshotLocked();

            if (_status == ConnectionStatus.Connected)
            {
                queuedThreadsToDrain = _queuedMessages.Keys.ToArray();
            }
            else
            {
                queuedThreadsToDrain = Array.Empty<string>();
            }
        }
        Publish(snapshot);

        foreach (var threadId in queuedThreadsToDrain)
        {
            TryDispatchNextQueuedSend(threadId);
        }
    }

    private void OnSessionCommandCompleted(object? sender, SessionCommandResult result)
    {
        if (result is not { Ok: true } ||
            !string.Equals(result.Method, "sessions.reset", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(result.Key))
        {
            return;
        }

        ChatDataSnapshot snapshot;
        ResetClearPersistence persistence;
        lock (_gate)
        {
            persistence = ClearThreadHistoryAfterResetLocked(result.Key);
            snapshot = BuildSnapshotLocked();
        }

        Publish(snapshot);
        PersistClearedResetState(persistence);
        AbortSubmittedRunsAfterReset(result.Key, persistence.SubmittedRunIds);
    }

    private void AbortSubmittedRunsAfterReset(string threadId, IReadOnlyList<string> runIds)
    {
        if (runIds.Count == 0)
            return;

        _ = Task.Run(async () =>
        {
            foreach (var runId in runIds)
            {
                if (string.IsNullOrWhiteSpace(runId))
                    continue;

                try
                {
                    Logger.Info($"[Reset] Sending chat.abort for pre-reset submitted runId='{runId}' threadId='{threadId}'");
                    await _bridge.SendChatAbortAsync(runId, threadId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Reset] chat.abort failed for pre-reset runId='{runId}' threadId='{threadId}': {ex.Message}");
                }
            }
        });
    }

    private void OnModelsListUpdated(object? sender, ModelsListInfo info)
    {
        ChatDataSnapshot snapshot;
        lock (_gate)
        {
            _modelChoices = ChatModelChoice.FromModelsList(info);
            _availableModels = ModelIdsFromChoices(_modelChoices);
            snapshot = BuildSnapshotLocked();
        }
        Logger.Info($"[ChatBridge] OnModelsListUpdated: count={_availableModels.Length}");
        Publish(snapshot);
    }

    // Wire ids (e.g. "claude-opus-4.5") in gateway order, used by the composer
    // to match against SessionInfo.Model. Kept as a parallel string[] for
    // back-compat with callers/persistence that only need the id list.
    private static string[] ModelIdsFromChoices(IReadOnlyList<ChatModelChoice> choices)
    {
        if (choices.Count == 0) return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ids = new List<string>(choices.Count);
        foreach (var choice in choices)
        {
            if (seen.Add(choice.Id))
                ids.Add(choice.Id);
        }
        return ids.ToArray();
    }

    // Rehydrate minimal choices from a cached id list (reconnect / pre-connect
    // path) when richer gateway metadata isn't available yet.
    private static IReadOnlyList<ChatModelChoice> ChoicesFromIds(string[] ids)
    {
        if (ids.Length == 0) return Array.Empty<ChatModelChoice>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<ChatModelChoice>(ids.Length);
        foreach (var id in ids)
        {
            if (string.IsNullOrEmpty(id)) continue;
            if (!seen.Add(id)) continue;
            list.Add(new ChatModelChoice(id, id));
        }
        return list;
    }

    private void OnChatMessageReceived(object? sender, ChatMessageInfo message)
    {
        if (message is null) return;

        // The gateway must include a canonical sessionKey on every chat event.
        // If it doesn't, that's a protocol bug — drop the event rather than
        // routing it to a literal "main" bucket that can't possibly match the
        // optimistic timeline keyed by the canonical key. Surfacing the drop
        // here makes future protocol gaps visible instead of silently merging
        // into a synthetic key.
        if (string.IsNullOrEmpty(message.SessionKey))
        {
            Logger.Warn($"[ChatProvider] Dropping chat message with empty sessionKey (role={message.Role})");
            RaiseKeylessEventDiagnosticOnce();
            return;
        }

        // Permanent low-volume trace for chat-message arrivals. One line per
        // frame the gateway sends, ordered by arrival. Includes a short
        // per-process-salted hash so two near-duplicate frames can be told
        // apart at a glance when hunting the duplicate-bubble bug (the
        // reducer's identical-text safety net only catches BYTE-equal
        // dupes; if the two frames differ by a single char the dupe
        // survives). The hash is seeded with a random value that rotates
        // on every tray restart, so it cannot be reproduced from a guessed
        // plaintext outside this process — it is a per-run frame
        // discriminator, not a content fingerprint.
        var traceText = message.Text ?? string.Empty;
        Logger.Info(
            $"[ChatTrace] chat.message thread='{message.SessionKey}' role='{message.Role}' " +
            $"final={message.IsFinal} len={traceText.Length} h={ChatTraceHash(traceText)}");

        // Suppress chat messages for threads that were aborted by the user.
        // Chat messages don't carry a runId, so we use thread-level suppression.
        var msgThreadId = message.SessionKey;
        var role = message.Role ?? "";
        var roleLower = role.ToLowerInvariant();
        var rawText = message.Text ?? string.Empty;
        ChatDataSnapshot? resetLocalEchoSnapshot = null;
        var dropAfterReset = false;
        var requestRemoteBackfillAfterReset = false;
        lock (_gate)
        {
            if (ShouldDropChatMessageAfterResetLocked(
                msgThreadId,
                roleLower,
                rawText,
                message.Ts,
                out var consumeEchoText,
                out var requestRemoteBackfill))
            {
                dropAfterReset = true;
                requestRemoteBackfillAfterReset = requestRemoteBackfill;
                if (consumeEchoText is not null &&
                    _localSentTexts.TryGetValue(msgThreadId, out var resetEchoQueue) &&
                    resetEchoQueue.Count > 0 &&
                    TryConsumeLocalEchoLocked(msgThreadId, resetEchoQueue, consumeEchoText, out var queuedMessageId))
                {
                    var confirmedMeta = BuildLiveMetaLocked(
                        msgThreadId,
                        message.Ts,
                        message.OpenClawId,
                        message.OpenClawSeq);
                    if (ReconcileQueuedMessageEchoLocked(msgThreadId, queuedMessageId, confirmedMeta))
                        resetLocalEchoSnapshot = BuildSnapshotLocked();
                }
            }
            else if (_abortedThreads.Contains(msgThreadId))
            {
                Logger.Debug($"[ABORT] Suppressed ChatMessage for threadId='{msgThreadId}' (role={message.Role})");
                return;
            }
        }
        if (dropAfterReset)
        {
            if (resetLocalEchoSnapshot is not null)
            {
                Publish(resetLocalEchoSnapshot);
            }
            if (requestRemoteBackfillAfterReset)
                _ = FetchRemoteUserMessageAsync(msgThreadId, openResetGateOnSuccess: true);

            Logger.Debug($"[Reset] Dropping stale chat message after reset for threadId='{msgThreadId}' role='{roleLower}'");
            return;
        }

        // User messages from the SSE stream. System control notes are rendered
        // as dim status entries. Normal user messages: promote echoes of
        // locally-sent queued messages into the transcript, show messages from
        // other clients (e.g. gateway web UI) so the conversation is coherent.
        if (roleLower == "user")
        {
            // Approval slash-commands ("/approve <slug> allow-once",
            // "/approve <slug> allow-always",
            // "/deny <slug>") are transport, not user prose. If WE sent
            // it (matched + consumed from _localSentTexts) suppress the
            // echo entirely — RespondToPermissionAsync already cleared
            // the banner. If it came from ANOTHER client subscribed to
            // this thread, render a dim audit-trail status so the user
            // can still see that an approval decision was made elsewhere
            // (preserves audit signal).
            if (LooksLikeApprovalSlashCommand(rawText))
            {
                var slashEcho = rawText.Trim();
                bool weSentIt = false;
                lock (_gate)
                {
                    if (_localSentTexts.TryGetValue(msgThreadId, out var sq) && sq.Count > 0
                        && TryConsumeLocalEchoLocked(msgThreadId, sq, slashEcho, out var slashEntryId))
                    {
                        weSentIt = true;
                        RemoveQueuedMessageLocked(msgThreadId, slashEntryId);
                    }
                }
                if (weSentIt)
                {
                    Logger.Debug($"[Approval] suppressed echo of our slash command on thread='{msgThreadId}'");
                    return;
                }
                // From another client — render as dim audit status.
                ChatEntryMetadata? approvalMeta;
                lock (_gate) { approvalMeta = BuildLiveMetaLocked(msgThreadId, message.Ts); }
                ApplyEventAndPublish(msgThreadId,
                    new ChatStatusEvent(slashEcho, ChatTone.Dim),
                    approvalMeta);
                return;
            }

            if (LooksLikeSystemControlNote(rawText))
            {
                if (string.IsNullOrEmpty(message.Text)) return;
                var sysThread = message.SessionKey;
                ChatEntryMetadata? sysMeta;
                lock (_gate) { sysMeta = BuildLiveMetaLocked(sysThread, message.Ts); }
                ApplyEventAndPublish(sysThread,
                    new ChatStatusEvent(TruncateForChatEntry(message.Text), ChatTone.Dim),
                    sysMeta);
                return;
            }

            // Check if this is an echo of a locally-sent message.
            var echoText = (message.Text ?? "").Trim();
            bool isLocalEcho = false;
            ChatDataSnapshot? echoSnapshot = null;
            lock (_gate)
            {
                if (_localSentTexts.TryGetValue(msgThreadId, out var q) && q.Count > 0
                    && TryConsumeLocalEchoLocked(msgThreadId, q, echoText, out var echoEntryId))
                {
                    isLocalEcho = true;
                    var confirmedMeta = BuildLiveMetaLocked(
                        msgThreadId,
                        message.Ts,
                        message.OpenClawId,
                        message.OpenClawSeq);
                    if (ReconcileQueuedMessageEchoLocked(msgThreadId, echoEntryId, confirmedMeta))
                        echoSnapshot = BuildSnapshotLocked();
                }
            }
            if (isLocalEcho)
            {
                if (echoSnapshot is not null)
                {
                    Publish(echoSnapshot);
                }
                return;
            }

            // Not a local echo — show it as a user message from another client.
            if (!string.IsNullOrEmpty(message.Text))
            {
                var userText = TruncateForChatEntry(EscapeUntrustedAttachmentMarkerLines(message.Text));
                ChatEntryMetadata? userMeta;
                ChatDataSnapshot? reconciledLocalQueuedSnapshot = null;
                lock (_gate)
                {
                    userMeta = BuildLiveMetaLocked(
                        msgThreadId,
                        message.Ts,
                        message.OpenClawId,
                        message.OpenClawSeq);
                    if (TryReconcileExistingLocalQueuedUserEchoLocked(msgThreadId, userText, userMeta))
                        reconciledLocalQueuedSnapshot = BuildSnapshotLocked();
                }
                if (reconciledLocalQueuedSnapshot is not null)
                {
                    Publish(reconciledLocalQueuedSnapshot);
                    return;
                }

                ApplyEventAndPublish(msgThreadId,
                    new ChatUserMessageEvent(userText),
                    userMeta);
            }
            return;
        }

        // ``role=toolresult`` frames are tool-output provenance and need to
        // render as a tool chip, the same way history does at lines 372-390
        // (chat rubber-duck MEDIUM 2).
        if (roleLower == "toolresult" || roleLower == "tool_result")
        {
            if (string.IsNullOrEmpty(message.Text)) return;
            var trThread = message.SessionKey;
            ChatEntryMetadata? trMeta;
            string? trRunId;
            lock (_gate)
            {
                trMeta = BuildLiveMetaLocked(
                    trThread,
                    message.Ts,
                    message.OpenClawId,
                    message.OpenClawSeq);
                _activeRunIds.TryGetValue(trThread, out trRunId);
            }
            var capped = TruncateForChatEntry(message.Text);
            var kind = ClassifyFlattenedToolOutput(capped);
            var label = ExtractFlattenedToolSummary(capped);
            _telemetry.ObserveInboundOutput(
                trThread,
                trRunId,
                ChatResponseOutputKind.Tool);
            ApplyEventAndPublish(trThread, new ChatToolStartEvent(label, kind), trMeta);
            ApplyEventAndPublish(trThread, new ChatToolOutputEvent(capped), trMeta);
            return;
        }

        if (roleLower != "assistant")
            return;
        if (ChatMessageInfo.IsSilentAssistantDirective(roleLower, message.Text))
            return;
        if (string.IsNullOrEmpty(message.Text))
            return;

        var threadId = message.SessionKey;
        var cappedAssistantText = RepairContentBlockSeams(TruncateForChatEntry(message.Text));
        AssistantQueueFrameDisposition assistantDisposition;
        lock (_gate)
        {
            assistantDisposition = ClassifyAssistantQueueFrameLocked(
                threadId,
                cappedAssistantText,
                message.OpenClawId,
                message.OpenClawSeq);
        }
        if (assistantDisposition != AssistantQueueFrameDisposition.Render)
        {
            Logger.Debug($"[Queue] Dropping retransmitted assistant frame around queued user boundary threadId='{threadId}'");
            return;
        }

        PromoteOldestQueuedMessageBeforeAssistantIfNeeded(threadId);
        ChatEntryMetadata? meta;
        string? telemetryRunId;
        var hasUsage = message.InputTokens is not null || message.OutputTokens is not null
            || message.ResponseTokens is not null || message.ContextPercent is not null;
        lock (_gate)
        {
            meta = BuildLiveMetaLocked(
                threadId,
                message.Ts,
                message.OpenClawId,
                message.OpenClawSeq);
            _activeRunIds.TryGetValue(threadId, out telemetryRunId);
            // If the gateway included a usage block on this chat event,
            // attach it so the assistant footer pills (↑/↓/R/ctx%) can
            // render. Mostly arrives on state="final" frames.
            if (hasUsage)
            {
                var session = Array.Find(_sessions, s => s.Key == threadId);
                meta = meta with
                {
                    InputTokens = message.InputTokens ?? meta.InputTokens,
                    OutputTokens = message.OutputTokens ?? meta.OutputTokens,
                    ResponseTokens = message.ResponseTokens ?? meta.ResponseTokens,
                    ContextPercent = message.ContextPercent ?? meta.ContextPercent,
                    ContextTokens = session?.ContextTokens > 0 ? session.ContextTokens : meta.ContextTokens
                };
            }
        }

        if (!message.IsFinal && IsLateNonFinalAssistantFrame(threadId))
        {
            Logger.Warn($"[ChatProvider] Dropping late non-final assistant frame after completed turn for threadId='{threadId}' len={traceText.Length}");
            return;
        }

        _telemetry.ObserveInboundOutput(
            threadId,
            telemetryRunId,
            ChatResponseOutputKind.Assistant);
        // Both `state: "delta"` and `state: "final"` carry the cumulative
        // assistant text (the gateway's EmbeddedBlockChunker emits completed
        // blocks, not token deltas — see spec §"Block Streaming"). Map both
        // to ChatMessageEvent so the reducer REPLACES the active assistant
        // entry's text. We tag delta frames with IsStreaming:true so the
        // reducer's reconcile-into-previous logic only collapses follow-up
        // finals into a still-streaming preview — a finalised assistant
        // from a completed earlier turn must not be silently overwritten
        // by a brand-new turn's reply (e.g. user → reply → tool → reply).
        // Final additionally ends the turn.
        ApplyEventAndPublish(
            threadId,
            new ChatMessageEvent(
                cappedAssistantText,
                ReconcilePrevious: true,
                IsStreaming: !message.IsFinal),
            meta);

        if (hasUsage)
            SnapshotAssistantUsageContribution(threadId, meta);

        if (message.IsFinal)
        {
            ChatTelemetryTracker.PreparedTurnCompletion? turnCompletion = null;
            lock (_gate)
            {
                if (_activeRunIds.Remove(threadId, out var completedRunId))
                {
                    turnCompletion = _telemetry.PrepareFinishByRunId(
                        completedRunId,
                        ChatTelemetryOutcome.Success,
                        ChatTurnTelemetryReason.AssistantFinal);
                    RememberTerminalRunIdLocked(threadId, completedRunId);
                    _abortedRunIds.Remove(completedRunId);
                }
                _activeRunStartSequences.Remove(threadId);
                _abortedThreads.Remove(threadId);
                if (!HasSendingQueuedMessagesLocked(threadId))
                    _locallyInitiatedThreads.Remove(threadId);
            }
            _telemetry.CompletePreparedTurn(turnCompletion);
            SnapshotLatestAssistantUsage(threadId);
            ApplyEventAndPublish(threadId, new ChatTurnEndEvent());
            RaiseNotification(new ChatProviderNotification(
                ChatProviderNotificationKind.TurnComplete, threadId, LocalizationHelper.GetString("Chat_Notification_AssistantReplied")));
            ScheduleQueuedSendDrain(threadId);
        }
    }

    private bool IsLateNonFinalAssistantFrame(string threadId)
    {
        lock (_gate)
        {
            if (!_timelines.TryGetValue(threadId, out var timeline))
                return false;
            if (timeline.TurnActive)
                return false;

            for (var i = timeline.Entries.Count - 1; i >= 0; i--)
            {
                var entry = timeline.Entries[i];
                if (entry.Kind == ChatTimelineItemKind.User)
                    return false;
                if (entry.Kind == ChatTimelineItemKind.Assistant)
                    return !entry.IsStreaming;
            }

            return false;
        }
    }

    private void OnAgentEventReceived(object? sender, AgentEventInfo evt)
    {
        if (evt is null) return;
        // As with chat events, every agent event must carry a canonical
        // sessionKey. Drop the event rather than routing to "main" if missing —
        // see the rationale in OnChatMessageReceived.
        if (string.IsNullOrEmpty(evt.SessionKey))
        {
            Logger.Warn($"[ChatProvider] Dropping agent event with empty sessionKey (stream={evt.Stream})");
            RaiseKeylessEventDiagnosticOnce();
            return;
        }
        var threadId = evt.SessionKey;
        var isTerminalRunEvent = IsTerminalRunEvent(evt);

        var reloadHistoryAfterResetDrop = false;
        var shouldProcessEvent = false;
        ChatTerminalEventDropReason? droppedTerminalReason = null;
        lock (_gate)
        {
            if (ShouldDropAgentEventAfterResetLocked(evt, threadId, out reloadHistoryAfterResetDrop))
            {
                Logger.Debug($"[Reset] Dropping stale agent event after reset for threadId='{threadId}' stream='{evt.Stream}' runId='{evt.RunId}'");
            }
            else if (ShouldDropTerminalAgentEventLocked(evt, threadId, out droppedTerminalReason))
            {
                Logger.Debug($"[Queue] Dropping stale terminal agent event for threadId='{threadId}' stream='{evt.Stream}' runId='{evt.RunId}'");
            }
            else
            {
                shouldProcessEvent = true;
            }
        }
        if (!shouldProcessEvent)
        {
            if (droppedTerminalReason.HasValue)
                RecordDroppedTerminalEvent(droppedTerminalReason.Value);
            if (reloadHistoryAfterResetDrop)
                _ = LoadHistoryAsync(threadId, force: true);
            return;
        }

        // Always update run tracking first (state maintenance must not be skipped).
        var deferredAbort = UpdateActiveRunId(evt, threadId);
        if (deferredAbort.DroppedTerminalReason.HasValue)
            RecordDroppedTerminalEvent(deferredAbort.DroppedTerminalReason.Value);
        ClearQueuedMessageOnLocalTurnStart(evt, threadId);

        // Fire deferred chat.abort and persist if pending aborts were queued.
        var deferredRunId = deferredAbort.RunId;
        var shouldPersist = deferredAbort.Count > 0;
        if (deferredRunId is not null || shouldPersist)
        {
            _ = Task.Run(async () =>
            {
                if (deferredRunId is not null)
                {
                    try
                    {
                        Logger.Info($"[ABORT] Sending deferred chat.abort for runId='{deferredRunId}'");
                        await _bridge.SendChatAbortAsync(deferredRunId, threadId);
                        Logger.Info($"[ABORT] Deferred chat.abort sent successfully");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[ABORT] Deferred chat.abort failed: {ex.Message}");
                    }
                }
                // Always persist — scan history for user messages with missing/truncated responses.
                await PersistAbortedMessageIdAsync(threadId);
            });
        }

        // Suppress rendering for aborted runs/threads (but lifecycle events
        // already ran above for state cleanup).
        var suppressRendering = false;
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(evt.RunId) && _abortedRunIds.Contains(evt.RunId))
                suppressRendering = true;
            else if (_abortedThreads.Contains(threadId))
                suppressRendering = true;
        }
        if (suppressRendering)
        {
            if (isTerminalRunEvent)
                ScheduleQueuedSendDrain(threadId);
            return;
        }

        ChatEvent? mapped = MapAgentEvent(evt);
        if (mapped is null)
        {
            // Approval lifecycle: clear the composer's Allow/Deny banner when
            // the gateway tells us this approval has reached a terminal state
            // (whether the dashboard answered first, the run was aborted, or
            // it expired). MapApprovalEvent only emits for ``requested``, so
            // every other approval phase lands here as a null mapping.
            //
            // Guardrails:
            //  • Whitelist terminal phases — we explicitly enumerate the
            //    phases that mean "approval is done". Anything else (e.g. a
            //    future ``acknowledged``/``in_progress`` phase the gateway
            //    might add) must not wipe a live banner.
            //  • Match by requestId — clear ONLY on a proven positive match
            //    between (evtSlug, evtApprovalId) and (pendingId, its
            //    recorded alternate id). The previous "clear unless we can
            //    prove a mismatch" default would wipe the banner when the
            //    terminal event arrived with both ids empty, or when ids
            //    used different precedence on the two ends of the lifecycle
            //    (slug-only ``requested`` vs approvalId-only ``resolved``).
            if (string.Equals(evt.Stream, "approval", System.StringComparison.OrdinalIgnoreCase)
                && evt.Data.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var phase = evt.Data.TryGetProperty("phase", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String
                    ? (p.GetString() ?? "")
                    : "";
                if (IsTerminalApprovalPhase(phase))
                {
                    var evtApprovalId = evt.Data.TryGetProperty("approvalId", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.String
                        ? (a.GetString() ?? "")
                        : "";
                    var evtSlug = evt.Data.TryGetProperty("approvalSlug", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String
                        ? (s.GetString() ?? "")
                        : "";
                    var evtDecision = evt.Data.TryGetProperty("decision", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String
                        ? (d.GetString() ?? "")
                        : "";

                    string? pendingId;
                    lock (_gate)
                    {
                        pendingId = GetOrCreateTimelineLocked(threadId).PendingPermission?.RequestId;
                    }

                    if (string.IsNullOrEmpty(pendingId))
                    {
                        // No live banner — nothing to clear, nothing to log loudly.
                        Logger.Debug($"[Approval] terminal phase='{phase}' for slug='{evtSlug}' approvalId='{evtApprovalId}' — no PendingPermission");
                    }
                    else if (ApprovalIdMatches(pendingId!, evtSlug, evtApprovalId))
                    {
                        // Honor the gateway's actual decision instead of always
                        // stamping Expired. The resolved echo races the local
                        // RPC response on the same WebSocket — if Expired wins
                        // here, ResolvePermission's no-overwrite guard then
                        // blocks the user's Allow/Denied stamp from landing.
                        // Phase already passed IsTerminalApprovalPhase; use
                        // the exact decision when present so allow-always is
                        // preserved, then fall back to phase mapping.
                        var resolvedDecision = MapTerminalPhaseToDecision(phase, evtDecision);
                        ClearPendingPermissionAndPublish(threadId, expectedRequestId: pendingId, decision: resolvedDecision);
                    }
                    else
                    {
                        // Either the event carried no id (gateway protocol drift)
                        // or ids didn't match the live banner. Either way we must
                        // not clear — preserving the banner is the safer default.
                        Logger.Info($"[Approval] terminal phase='{phase}' slug='{evtSlug}' approvalId='{evtApprovalId}' did not match pending '{pendingId}' — banner preserved");
                    }
                }
            }
            if (isTerminalRunEvent)
                ScheduleQueuedSendDrain(threadId);
            return;
        }

        var outputKind = ClassifyInboundOutput(evt, mapped);
        if (outputKind.HasValue)
        {
            _telemetry.ObserveInboundOutput(
                threadId,
                evt.RunId,
                outputKind.Value);
        }

        // Cache tool metadata from live SSE events so it survives app restarts.
        if (mapped is ChatToolStartEvent toolStart && !string.IsNullOrEmpty(toolStart.ToolName))
        {
            var tsMs0 = evt.Ts > 0 ? (long)evt.Ts : 0L;
            CacheToolMeta(threadId, tsMs0, toolStart.ToolName, toolStart.Text);
        }

        // AgentEventInfo.Ts is a double of unix-epoch ms (per OpenClawGatewayClient).
        var tsMs = evt.Ts > 0 ? (long)evt.Ts : 0L;
        ChatEntryMetadata? meta;
        lock (_gate) { meta = BuildLiveMetaLocked(threadId, tsMs); }

        ApplyEventAndPublish(threadId, mapped, meta);
        if (isTerminalRunEvent)
            ScheduleQueuedSendDrain(threadId);
    }

    private static ChatResponseOutputKind? ClassifyInboundOutput(
        AgentEventInfo evt,
        ChatEvent mapped)
    {
        if (string.Equals(evt.Stream, "lifecycle", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(evt.Stream, "job", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return mapped switch
        {
            ChatMessageEvent or ChatMessageDeltaEvent => ChatResponseOutputKind.Assistant,
            ChatThinkingEvent or ChatReasoningEvent or ChatReasoningDeltaEvent or
                ChatIntentEvent => ChatResponseOutputKind.Reasoning,
            ChatToolStartEvent or ChatToolOutputEvent or ChatToolErrorEvent or
                ChatPermissionRequestEvent => ChatResponseOutputKind.Tool,
            ChatStatusEvent or ChatErrorEvent or ChatReasoningEndEvent or
                ChatTurnEndEvent or ChatUserMessageEvent => null,
            _ => null,
        };
    }

    private void RaiseKeylessEventDiagnosticOnce()
    {
        if (System.Threading.Interlocked.Exchange(ref _keylessEventDiagnosticRaised, 1) != 0)
            return;

        var threadId = GetKeylessEventDiagnosticThreadId();
        var title = LocalizationHelper.GetString("Chat_Notification_KeylessEventDropped");
        var message = LocalizationHelper.GetString("Chat_Notification_KeylessEventDroppedMessage");

        RaiseNotification(new ChatProviderNotification(
            ChatProviderNotificationKind.Error,
            threadId ?? string.Empty,
            title,
            message));

        if (!string.IsNullOrWhiteSpace(threadId))
            ApplyEventAndPublish(threadId, new ChatStatusEvent(message, ChatTone.Warning));
    }

    private string? GetKeylessEventDiagnosticThreadId()
    {
        lock (_gate)
        {
            return ResolveDefaultThreadIdLocked()
                ?? _timelines.Keys.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k));
        }
    }

    private (string? RunId, int Count, ChatTerminalEventDropReason? DroppedTerminalReason) UpdateActiveRunId(
        AgentEventInfo evt,
        string threadId)
    {
        string? deferredAbortRunId = null;
        var deferredAbortCount = 0;
        ChatTerminalEventDropReason? droppedTerminalReason = null;
        ChatTelemetryTracker.PreparedTurnCompletion? turnCompletion = null;

        if (string.Equals(evt.Stream, "lifecycle", StringComparison.OrdinalIgnoreCase) &&
            evt.Data.ValueKind == System.Text.Json.JsonValueKind.Object &&
            evt.Data.TryGetProperty("phase", out var phaseProp))
        {
            var phase = phaseProp.GetString()?.ToLowerInvariant();
            lock (_gate)
            {
                if (phase == "start")
                {
                    _telemetry.ObserveLifecycleStart(
                        threadId,
                        evt.RunId,
                        allowRemoteTurn: !_locallyInitiatedThreads.Contains(threadId) &&
                            !_abortedThreads.Contains(threadId) &&
                            !_pendingAbortCounts.ContainsKey(threadId));
                    if (!string.IsNullOrEmpty(evt.RunId))
                    {
                        _activeRunIds[threadId] = evt.RunId;
                        _activeRunStartSequences[threadId] = ++_lifecycleStartSequence;

                        // Detect remote turn: if the turn was NOT locally initiated,
                        // a remote client (e.g. gateway web UI) sent the message.
                        // Fetch the last user message from history so it appears in
                        // the timeline before the assistant response.
                        if (!_locallyInitiatedThreads.Contains(threadId))
                        {
                            _ = FetchRemoteUserMessageAsync(threadId);
                        }

                        // Deferred abort: if user clicked stop before lifecycle.start,
                        // fire chat.abort now that we have the runId.
                        if (_pendingAbortCounts.TryGetValue(threadId, out var pendingCount) && pendingCount > 0)
                        {
                            _pendingAbortCounts.Remove(threadId);
                            _abortedRunIds.Add(evt.RunId);
                            deferredAbortRunId = evt.RunId;
                            deferredAbortCount = pendingCount;
                            Logger.Info($"[ABORT] Deferred abort fired — lifecycle.start arrived with runId='{evt.RunId}' for threadId='{threadId}' (pendingCount={pendingCount})");
                        }
                    }
                }
                else if (phase == "end" || phase == "error")
                {
                    var wasAborted = !string.IsNullOrWhiteSpace(evt.RunId) &&
                        _abortedRunIds.Contains(evt.RunId);
                    turnCompletion = _telemetry.PrepareFinishByRunId(
                        evt.RunId,
                        phase == "error" ? ChatTelemetryOutcome.Failure : ChatTelemetryOutcome.Success,
                        phase == "error"
                            ? ChatTurnTelemetryReason.LifecycleError
                            : ChatTurnTelemetryReason.LifecycleEnd);
                    if (turnCompletion is null && !wasAborted)
                    {
                        droppedTerminalReason = string.IsNullOrWhiteSpace(evt.RunId)
                            ? ChatTerminalEventDropReason.MissingRunId
                            : ChatTerminalEventDropReason.MismatchedRunId;
                    }
                    // Clean up: remove aborted runId tracking on terminal events.
                    if (!string.IsNullOrEmpty(evt.RunId))
                        _abortedRunIds.Remove(evt.RunId);
                    _activeRunIds.Remove(threadId);
                    _activeRunStartSequences.Remove(threadId);

                    // Clear thread-level abort suppression on terminal lifecycle events.
                    // The turn is over — any remaining abort suppression is no longer needed.
                    _abortedThreads.Remove(threadId);
                    // Clear locally-initiated flag only when no locally queued
                    // follow-up prompts remain for this thread. Multiple rapid
                    // sends can queue runs behind the current one; treating the
                    // next lifecycle.start as remote would orphan those queued
                    // cards and let assistant fallback promote the wrong item.
                    RemoveQueuedRunMappingByRunIdLocked(threadId, evt.RunId);
                    if (!HasPendingQueuedMessagesLocked(threadId))
                        _locallyInitiatedThreads.Remove(threadId);

                    // Edge case: if we have pending aborts but never saw lifecycle.start
                    // (gateway responded so fast start+end were batched), fire the
                    // deferred abort now so the persist still runs.
                    if (_pendingAbortCounts.TryGetValue(threadId, out var lateCount) && lateCount > 0)
                    {
                        _pendingAbortCounts.Remove(threadId);
                        deferredAbortRunId = evt.RunId; // may be null, that's ok — persist doesn't need it
                        deferredAbortCount = lateCount;
                        Logger.Info($"[ABORT] Late deferred abort — lifecycle.end arrived with pending aborts for threadId='{threadId}' (pendingCount={lateCount})");
                    }
                }
            }
        }
        // Also catch lifecycle via legacy job stream.
        else if (string.Equals(evt.Stream, "job", StringComparison.OrdinalIgnoreCase) &&
                 evt.Data.ValueKind == System.Text.Json.JsonValueKind.Object &&
                 evt.Data.TryGetProperty("state", out var stateProp))
        {
            var state = stateProp.GetString()?.ToLowerInvariant();
            lock (_gate)
            {
                if (state == "done" || state == "error")
                {
                    var wasAborted = !string.IsNullOrWhiteSpace(evt.RunId) &&
                        _abortedRunIds.Contains(evt.RunId);
                    turnCompletion = _telemetry.PrepareFinishByRunId(
                        evt.RunId,
                        state == "error" ? ChatTelemetryOutcome.Failure : ChatTelemetryOutcome.Success,
                        state == "error"
                            ? ChatTurnTelemetryReason.LifecycleError
                            : ChatTurnTelemetryReason.LifecycleEnd);
                    if (turnCompletion is null && !wasAborted)
                    {
                        droppedTerminalReason = string.IsNullOrWhiteSpace(evt.RunId)
                            ? ChatTerminalEventDropReason.MissingRunId
                            : ChatTerminalEventDropReason.MismatchedRunId;
                    }
                    if (!string.IsNullOrWhiteSpace(evt.RunId))
                    {
                        _abortedRunIds.Remove(evt.RunId);
                        RemoveQueuedRunMappingByRunIdLocked(threadId, evt.RunId);
                    }
                    _activeRunIds.Remove(threadId);
                    _activeRunStartSequences.Remove(threadId);
                }
            }
        }

        _telemetry.CompletePreparedTurn(turnCompletion);
        return (deferredAbortRunId, deferredAbortCount, droppedTerminalReason);
    }

    private bool ShouldDropTerminalAgentEventLocked(
        AgentEventInfo evt,
        string threadId,
        out ChatTerminalEventDropReason? droppedTerminalReason)
    {
        droppedTerminalReason = null;
        if (!TryGetTerminalAgentRunId(evt, out var runId))
            return false;
        if (string.IsNullOrWhiteSpace(runId))
        {
            droppedTerminalReason = ChatTerminalEventDropReason.MissingRunId;
            return true;
        }

        if (_terminalRunIdsByThread.TryGetValue(threadId, out var terminalRunIds) &&
            terminalRunIds.Contains(runId, StringComparer.Ordinal))
        {
            return true;
        }

        if (_activeRunIds.TryGetValue(threadId, out var activeRunId) &&
            !string.Equals(activeRunId, runId, StringComparison.Ordinal))
        {
            droppedTerminalReason = ChatTerminalEventDropReason.MismatchedRunId;
            return true;
        }

        if (!_activeRunIds.ContainsKey(threadId) &&
            _queuedMessageIdsByRunId.TryGetValue(threadId, out var queuedRunIds) &&
            queuedRunIds.Count > 0 &&
            !queuedRunIds.ContainsKey(runId) &&
            _timelines.TryGetValue(threadId, out var timeline) &&
            timeline.TurnActive)
        {
            droppedTerminalReason = ChatTerminalEventDropReason.MismatchedRunId;
            return true;
        }

        RememberTerminalRunIdLocked(threadId, runId);
        return false;
    }

    private void RecordDroppedTerminalEvent(ChatTerminalEventDropReason reason)
    {
        _telemetry.RecordDroppedTerminalEvent(reason);
        Logger.Warn(
            $"[ChatTelemetry] Dropped terminal chat event because safe run correlation was unavailable " +
            $"(reason='{ChatTelemetryTracker.ToTelemetryValue(reason)}').");
    }

    private void RememberTerminalRunIdLocked(string threadId, string runId)
    {
        if (!_terminalRunIdsByThread.TryGetValue(threadId, out var terminalRunIds))
        {
            terminalRunIds = new List<string>();
            _terminalRunIdsByThread[threadId] = terminalRunIds;
        }

        terminalRunIds.RemoveAll(existing => string.Equals(existing, runId, StringComparison.Ordinal));
        terminalRunIds.Add(runId);
        if (terminalRunIds.Count > 64)
            terminalRunIds.RemoveRange(0, terminalRunIds.Count - 64);
    }

    private static bool TryGetTerminalAgentRunId(AgentEventInfo evt, out string runId)
    {
        runId = evt.RunId ?? string.Empty;
        if (evt.Data.ValueKind != System.Text.Json.JsonValueKind.Object)
            return false;

        if (string.Equals(evt.Stream, "lifecycle", StringComparison.OrdinalIgnoreCase) &&
            evt.Data.TryGetProperty("phase", out var phaseProp))
        {
            var phase = phaseProp.GetString();
            return string.Equals(phase, "end", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(phase, "error", StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(evt.Stream, "job", StringComparison.OrdinalIgnoreCase) &&
            evt.Data.TryGetProperty("state", out var stateProp))
        {
            var state = stateProp.GetString();
            return string.Equals(state, "done", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(state, "error", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private bool TryConsumeLocalEchoLocked(string threadId, Queue<LocalSentText> queue, string text, out string queuedMessageId)
    {
        queuedMessageId = string.Empty;
        var now = DateTimeOffset.UtcNow;
        while (queue.Count > 0 && now - queue.Peek().SentAt > LocalEchoSuppressionWindow)
            queue.Dequeue();

        if (queue.Count == 0)
        {
            _localSentTexts.Remove(threadId);
            return false;
        }

        var matched = false;
        string matchedMessageId = string.Empty;
        var pendingEchoes = queue.ToArray();
        queue.Clear();
        foreach (var pending in pendingEchoes)
        {
            if (matched || !string.Equals(pending.Text, text, StringComparison.Ordinal))
                continue;

            queuedMessageId = pending.QueuedMessageId;
            matchedMessageId = pending.QueuedMessageId;
            matched = true;
        }

        var kept = new Queue<LocalSentText>(pendingEchoes.Length);
        if (!matched)
        {
            foreach (var pending in pendingEchoes)
                kept.Enqueue(pending);
            StoreLocalEchoQueueLocked(threadId, kept);
            return false;
        }

        foreach (var pending in pendingEchoes)
        {
            if (string.Equals(pending.QueuedMessageId, matchedMessageId, StringComparison.Ordinal))
                continue;

            kept.Enqueue(pending);
        }

        StoreLocalEchoQueueLocked(threadId, kept);
        return true;
    }

    private void StoreLocalEchoQueueLocked(string threadId, Queue<LocalSentText> queue)
    {
        if (queue.Count == 0)
            _localSentTexts.Remove(threadId);
        else
            _localSentTexts[threadId] = queue;
    }

    private bool TryReconcileExistingLocalQueuedUserEchoLocked(
        string threadId,
        string text,
        ChatEntryMetadata confirmedMeta)
    {
        if (!HasGatewayIdentity(confirmedMeta))
            return false;
        if (!_entryMeta.TryGetValue(threadId, out var threadMeta) ||
            !_timelines.TryGetValue(threadId, out var timeline))
            return false;

        foreach (var entry in timeline.Entries)
        {
            if (entry.Kind != ChatTimelineItemKind.User)
                continue;
            if (!string.Equals(entry.Text, text, StringComparison.Ordinal))
                continue;
            if (!threadMeta.TryGetValue(entry.Id, out var existing) || !existing.IsLocalQueuedSend)
                continue;
            if (HasGatewayIdentity(existing))
                continue;
            if (!IsFreshLocalQueuedPromotion(existing, confirmedMeta))
                continue;

            threadMeta[entry.Id] = confirmedMeta with
            {
                IsLocalQueuedSend = false,
                LocalQueuedMessageId = existing.LocalQueuedMessageId,
            };
            return true;
        }

        return false;
    }

    private static bool HasGatewayIdentity(ChatEntryMetadata meta)
        => !string.IsNullOrEmpty(meta.GatewayMessageId) || meta.OpenClawSeq is not null;

    private static bool IsFreshLocalQueuedPromotion(ChatEntryMetadata existing, ChatEntryMetadata confirmed)
    {
        if (existing.Timestamp is not { } existingTimestamp)
            return false;
        if (confirmed.Timestamp is { } confirmedTimestamp)
            return (confirmedTimestamp - existingTimestamp).Duration() <= LocalEchoSuppressionWindow;

        return DateTimeOffset.Now - existingTimestamp <= LocalEchoSuppressionWindow;
    }

    private void AddQueuedMessageLocked(string threadId, ChatQueuedMessage message)
    {
        if (!_queuedMessages.TryGetValue(threadId, out var list))
        {
            list = new List<ChatQueuedMessage>();
            _queuedMessages[threadId] = list;
        }

        list.RemoveAll(existing => existing.Id == message.Id);
        list.Add(message);
    }

    private void AddQueuedSendRequestLocked(QueuedSendRequest request)
    {
        if (!_queuedSendRequests.TryGetValue(request.ThreadId, out var list))
        {
            list = new List<QueuedSendRequest>();
            _queuedSendRequests[request.ThreadId] = list;
        }

        list.RemoveAll(existing => existing.Id == request.Id);
        list.Add(request);
    }

    private void RemoveQueuedSendRequestLocked(string threadId, string messageId)
    {
        if (!_queuedSendRequests.TryGetValue(threadId, out var list))
            return;

        list.RemoveAll(request => request.Id == messageId);
        if (list.Count == 0)
            _queuedSendRequests.Remove(threadId);
    }

    private QueuedSendRequest? FindQueuedSendRequestLocked(string threadId, string messageId)
    {
        if (!_queuedSendRequests.TryGetValue(threadId, out var list))
            return null;

        return list.FirstOrDefault(request => string.Equals(request.Id, messageId, StringComparison.Ordinal));
    }

    private bool CanSendDirectlyLocked(string threadId)
    {
        if (_activeRunIds.ContainsKey(threadId))
            return false;
        if (_timelines.TryGetValue(threadId, out var timeline) && timeline.TurnActive)
            return false;
        return !HasPendingQueuedMessagesLocked(threadId);
    }

    private bool CanClearAssistantFallbackPromotionLocked(string threadId)
    {
        if (HasSendingQueuedMessagesLocked(threadId))
            return false;
        if (_activeRunIds.ContainsKey(threadId))
            return false;
        return !_timelines.TryGetValue(threadId, out var timeline) || !timeline.TurnActive;
    }

    private QueuedSendDispatch StartDirectSendLocked(QueuedSendRequest request)
    {
        var threadId = request.ThreadId;
        var resetVersion = GetResetVersionLocked(threadId);
        var startedLifecycleSequence = _resetLifecycleStartSequence;
        var startedRunStartSequence = _lifecycleStartSequence;
        var current = GetOrCreateTimelineLocked(threadId);
        var entryId = $"e{current.NextId}";
        _timelines[threadId] = ChatTimelineReducer.AddLocalUser(current, request.DisplayText, request.LocalNonce);
        GetOrCreateThreadMetaLocked(threadId)[entryId] = BuildLiveMetaLocked(
            threadId,
            isLocalQueuedSend: true,
            localQueuedMessageId: request.Id);
        _sessionIds.TryGetValue(threadId, out var sessionId);

        EnqueueLocalEchoLocked(threadId, request.Text, request.Id);
        _locallyInitiatedThreads.Add(threadId);
        _assistantFallbackPromotedThreads.Add(threadId);
        var queueCompletion = _telemetry.PrepareDispatchLocalTurn(request.Id, request.SendRunId);
        return new QueuedSendDispatch(
            request,
            sessionId,
            resetVersion,
            startedLifecycleSequence,
            startedRunStartSequence,
            queueCompletion,
            StartedDirectly: true);
    }

    private QueuedSendDispatch? TryStartNextQueuedSendLocked(
        string threadId,
        bool requireConnected,
        out TimeSpan? delayedRetry)
    {
        delayedRetry = null;
        if (requireConnected && _status != ConnectionStatus.Connected)
            return null;
        if (_activeRunIds.ContainsKey(threadId))
            return null;
        if (_timelines.TryGetValue(threadId, out var timeline) && timeline.TurnActive)
            return null;
        if (!_queuedMessages.TryGetValue(threadId, out var queuedMessages))
            return null;

        for (var i = 0; i < queuedMessages.Count; i++)
        {
            if (queuedMessages[i].SendState != ChatQueuedMessageSendState.Queued)
                continue;

            var request = FindQueuedSendRequestLocked(threadId, queuedMessages[i].Id);
            if (request is null)
                continue;

            var now = DateTimeOffset.UtcNow;
            if (request.DeferredAdmissionRetryAfter is { } retryAfter)
            {
                if (retryAfter > now)
                {
                    delayedRetry = retryAfter - now;
                    return null;
                }

                request = request with { DeferredAdmissionRetryAfter = null };
                AddQueuedSendRequestLocked(request);
            }

            // Each dispatched prompt gets one opportunity for assistant-frame
            // fallback promotion before its lifecycle/user echo arrives.
            _assistantFallbackPromotedThreads.Remove(threadId);
            queuedMessages[i] = queuedMessages[i] with { SendState = ChatQueuedMessageSendState.Sending, ErrorText = null };
            var resetVersion = GetResetVersionLocked(threadId);
            var startedLifecycleSequence = _resetLifecycleStartSequence;
            var startedRunStartSequence = _lifecycleStartSequence;
            _timelines[threadId] = ChatTimelineReducer.BeginLocalUserTurn(GetOrCreateTimelineLocked(threadId));
            _sessionIds.TryGetValue(threadId, out var sessionId);

            EnqueueLocalEchoLocked(threadId, request.Text, request.Id);
            _locallyInitiatedThreads.Add(threadId);
            var queueCompletion = _telemetry.PrepareDispatchLocalTurn(request.Id, request.SendRunId);
            return new QueuedSendDispatch(
                request,
                sessionId,
                resetVersion,
                startedLifecycleSequence,
                startedRunStartSequence,
                queueCompletion,
                StartedDirectly: false);
        }

        return null;
    }

    private void EnqueueLocalEchoLocked(string threadId, string text, string messageId)
    {
        RemovePendingLocalEchoLocked(threadId, messageId);
        if (!_localSentTexts.TryGetValue(threadId, out var localEchoQueue))
        {
            localEchoQueue = new Queue<LocalSentText>();
            _localSentTexts[threadId] = localEchoQueue;
        }

        localEchoQueue.Enqueue(new LocalSentText(text, DateTimeOffset.UtcNow, messageId));
        while (localEchoQueue.Count > 20)
            localEchoQueue.Dequeue();
    }

    private void TryDispatchNextQueuedSend(string threadId)
    {
        ChatDataSnapshot? snapshot = null;
        QueuedSendDispatch? dispatch;
        TimeSpan? delayedRetry;
        lock (_gate)
        {
            if (_disposed)
                return;
            dispatch = TryStartNextQueuedSendLocked(threadId, requireConnected: true, out delayedRetry);
            if (dispatch is not null)
                snapshot = BuildSnapshotLocked();
        }

        if (snapshot is not null)
            Publish(snapshot);
        if (dispatch is not null)
            _ = DispatchQueuedSendAsync(dispatch, rethrow: false);
        else if (delayedRetry is { } delay)
            ScheduleQueuedSendDrain(threadId, delay);
    }

    private void ScheduleQueuedSendDrain(string threadId)
        => ScheduleQueuedSendDrain(threadId, DeferredQueueDrainDelay);

    private void ScheduleQueuedSendDrain(string threadId, TimeSpan delay)
    {
        lock (_gate)
        {
            if (_disposed || !_queuedMessages.ContainsKey(threadId))
                return;
            if (!_queuedDrainScheduledThreads.Add(threadId))
                return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _queuedDrainScheduledThreads.Remove(threadId);
                }
            }

            try
            {
                TryDispatchNextQueuedSend(threadId);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Queue] Scheduled queued send drain failed for threadId='{threadId}': {ex.Message}");
            }
        });
    }

    private static bool IsDeferredAdmissionStatus(string? status) =>
        string.Equals(status, "in_flight", StringComparison.OrdinalIgnoreCase);

    private static ChatAdmissionTelemetryStatus MapAdmissionTelemetryStatus(ChatSendResult result)
    {
        if (IsDeferredAdmissionStatus(result.Status))
            return ChatAdmissionTelemetryStatus.Deferred;
        if (result.IsTerminalFailure)
        {
            return IsCanceledAdmissionStatus(result.Status)
                ? ChatAdmissionTelemetryStatus.Canceled
                : ChatAdmissionTelemetryStatus.Rejected;
        }
        if (string.IsNullOrWhiteSpace(result.Status) ||
            string.Equals(result.Status, "started", StringComparison.OrdinalIgnoreCase))
        {
            return ChatAdmissionTelemetryStatus.Accepted;
        }
        return ChatAdmissionTelemetryStatus.Other;
    }

    private static bool IsCanceledAdmissionStatus(string? status) =>
        string.Equals(status, "aborted", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan DeferredAdmissionRetryDelay(int retryCount)
    {
        var exponent = Math.Min(Math.Max(retryCount - 1, 0), 5);
        var delayMs = DeferredQueueDrainDelay.TotalMilliseconds * (1 << exponent);
        return TimeSpan.FromMilliseconds(Math.Min(delayMs, MaxDeferredAdmissionRetryDelay.TotalMilliseconds));
    }

    private void TrackQueuedMessageRunLocked(string threadId, string runId, string messageId)
    {
        if (!_queuedMessageIdsByRunId.TryGetValue(threadId, out var byRunId))
        {
            byRunId = new Dictionary<string, string>(StringComparer.Ordinal);
            _queuedMessageIdsByRunId[threadId] = byRunId;
        }

        byRunId[runId] = messageId;
    }

    private bool RemoveQueuedMessageLocked(string threadId, string messageId)
    {
        if (!_queuedMessages.TryGetValue(threadId, out var list))
            return false;

        var removed = list.RemoveAll(message => message.Id == messageId) > 0;
        if (removed)
        {
            RemoveQueuedRunMappingByMessageIdLocked(threadId, messageId);
            RemoveQueuedSendRequestLocked(threadId, messageId);
        }
        if (list.Count == 0)
        {
            _queuedMessages.Remove(threadId);
            ClearQueuedDrainScheduleLocked(threadId);
            ClearLocallyInitiatedIfIdleLocked(threadId);
        }
        return removed;
    }

    private bool CancelQueuedMessageLocked(string threadId, string messageId)
    {
        if (!_queuedMessages.TryGetValue(threadId, out var list))
            return false;

        var index = list.FindIndex(message => string.Equals(message.Id, messageId, StringComparison.Ordinal));
        if (index < 0)
            return false;

        if (list[index].SendState == ChatQueuedMessageSendState.Sending)
            return false;

        list.RemoveAt(index);
        RemovePendingLocalEchoLocked(threadId, messageId);
        RemoveQueuedRunMappingByMessageIdLocked(threadId, messageId);
        RemoveQueuedSendRequestLocked(threadId, messageId);
        if (list.Count == 0)
        {
            _queuedMessages.Remove(threadId);
            ClearQueuedDrainScheduleLocked(threadId);
            ClearLocallyInitiatedIfIdleLocked(threadId);
        }
        return true;
    }

    private void ClearQueuedDrainScheduleLocked(string threadId)
        => _queuedDrainScheduledThreads.Remove(threadId);

    private bool PromoteQueuedMessageLocked(
        string threadId,
        string messageId,
        ChatEntryMetadata? confirmedMeta = null)
    {
        if (!_queuedMessages.TryGetValue(threadId, out var list))
            return false;

        var index = list.FindIndex(message => message.Id == messageId);
        if (index < 0)
            return false;

        var queued = list[index];
        var current = GetOrCreateTimelineLocked(threadId);
        var entryId = $"e{current.NextId}";
        _timelines[threadId] = ChatTimelineReducer.AddLocalUser(current, queued.Text, queued.LocalNonce);

        var hasGatewayIdentity = confirmedMeta is not null && HasGatewayIdentity(confirmedMeta);
        var meta = hasGatewayIdentity
            ? confirmedMeta! with { IsLocalQueuedSend = false, LocalQueuedMessageId = messageId }
            : BuildLiveMetaLocked(
                threadId,
                isLocalQueuedSend: true,
                localQueuedMessageId: messageId);
        var threadMeta = GetOrCreateThreadMetaLocked(threadId);
        threadMeta[entryId] = meta;

        list.RemoveAt(index);
        _assistantFallbackPromotedThreads.Add(threadId);
        RemoveQueuedSendRequestLocked(threadId, messageId);
        if (list.Count == 0)
        {
            _queuedMessages.Remove(threadId);
            ClearQueuedDrainScheduleLocked(threadId);
        }
        return true;
    }

    private void ClearLocallyInitiatedIfIdleLocked(string threadId)
    {
        if (_activeRunIds.ContainsKey(threadId))
            return;
        if (_timelines.TryGetValue(threadId, out var timeline) && timeline.TurnActive)
            return;
        if (HasPendingQueuedMessagesLocked(threadId))
            return;

        _locallyInitiatedThreads.Remove(threadId);
    }

    private bool ReconcileQueuedMessageEchoLocked(
        string threadId,
        string messageId,
        ChatEntryMetadata confirmedMeta)
    {
        if (PromoteQueuedMessageLocked(threadId, messageId, confirmedMeta))
            return true;
        if (!HasGatewayIdentity(confirmedMeta) ||
            !_entryMeta.TryGetValue(threadId, out var threadMeta))
        {
            return false;
        }

        string? matchedEntryId = null;
        foreach (var (entryId, existing) in threadMeta)
        {
            if (!string.Equals(existing.LocalQueuedMessageId, messageId, StringComparison.Ordinal))
                continue;
            matchedEntryId = entryId;
            break;
        }

        if (matchedEntryId is null)
            return false;

        threadMeta[matchedEntryId] = confirmedMeta with
        {
            IsLocalQueuedSend = false,
            LocalQueuedMessageId = messageId,
        };
        return true;
    }

    private void RemoveQueuedRunMappingByMessageIdLocked(string threadId, string messageId)
    {
        if (!_queuedMessageIdsByRunId.TryGetValue(threadId, out var byRunId))
            return;

        foreach (var runId in byRunId.Where(kvp => kvp.Value == messageId).Select(kvp => kvp.Key).ToArray())
            byRunId.Remove(runId);

        if (byRunId.Count == 0)
            _queuedMessageIdsByRunId.Remove(threadId);
    }

    private void RemoveQueuedRunMappingByRunIdLocked(string threadId, string runId)
    {
        if (!_queuedMessageIdsByRunId.TryGetValue(threadId, out var byRunId))
            return;

        if (byRunId.TryGetValue(runId, out var messageId))
        {
            foreach (var aliasRunId in byRunId.Where(kvp => kvp.Value == messageId).Select(kvp => kvp.Key).ToArray())
                byRunId.Remove(aliasRunId);
        }
        else
        {
            byRunId.Remove(runId);
        }

        if (byRunId.Count == 0)
            _queuedMessageIdsByRunId.Remove(threadId);
    }

    private void MarkQueuedMessageFailedLocked(string threadId, string messageId, string error)
    {
        if (!_queuedMessages.TryGetValue(threadId, out var list))
            return;

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Id == messageId)
            {
                list[i] = list[i] with
                {
                    SendState = ChatQueuedMessageSendState.Failed,
                    ErrorText = error
                };
                return;
            }
        }
    }

    private bool RequeueDeferredAdmissionLocked(string threadId, string messageId, out TimeSpan retryDelay)
    {
        retryDelay = DeferredQueueDrainDelay;
        var hasActiveRun = _activeRunIds.ContainsKey(threadId);
        if (!_queuedMessages.TryGetValue(threadId, out var list))
            return false;

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Id != messageId ||
                list[i].SendState != ChatQueuedMessageSendState.Sending)
            {
                continue;
            }

            var retryCount = IncrementDeferredAdmissionRetryCountLocked(threadId, messageId);
            if (retryCount > MaxDeferredAdmissionRetries)
            {
                throw new InvalidOperationException(
                    $"Gateway kept chat.send status in_flight after {MaxDeferredAdmissionRetries} retries.");
            }

            list[i] = list[i] with
            {
                SendState = ChatQueuedMessageSendState.Queued,
                ErrorText = null
            };
            retryDelay = DeferredAdmissionRetryDelay(retryCount);
            SetDeferredAdmissionRetryAfterLocked(threadId, messageId, DateTimeOffset.UtcNow + retryDelay);
            _assistantFallbackPromotedThreads.Remove(threadId);
            if (!hasActiveRun)
            {
                _timelines[threadId] = ChatTimelineReducer.Apply(
                    GetOrCreateTimelineLocked(threadId),
                    new ChatTurnEndEvent());
            }
            return true;
        }

        return false;
    }

    private void SetDeferredAdmissionRetryAfterLocked(string threadId, string messageId, DateTimeOffset retryAfter)
    {
        if (!_queuedSendRequests.TryGetValue(threadId, out var requests))
            return;

        for (var i = 0; i < requests.Count; i++)
        {
            if (!string.Equals(requests[i].Id, messageId, StringComparison.Ordinal))
                continue;

            requests[i] = requests[i] with { DeferredAdmissionRetryAfter = retryAfter };
            return;
        }
    }

    private int IncrementDeferredAdmissionRetryCountLocked(string threadId, string messageId)
    {
        if (!_queuedSendRequests.TryGetValue(threadId, out var requests))
            return MaxDeferredAdmissionRetries + 1;

        for (var i = 0; i < requests.Count; i++)
        {
            if (!string.Equals(requests[i].Id, messageId, StringComparison.Ordinal))
                continue;

            var retryCount = requests[i].DeferredAdmissionRetryCount + 1;
            requests[i] = requests[i] with { DeferredAdmissionRetryCount = retryCount };
            return retryCount;
        }

        return MaxDeferredAdmissionRetries + 1;
    }

    private void ClearQueuedMessageOnLocalTurnStart(AgentEventInfo evt, string threadId)
    {
        if (!IsLifecycleStart(evt))
            return;

        ChatDataSnapshot? snapshot = null;
        lock (_gate)
        {
            if (TryPromoteQueuedMessageOnLocalTurnStartLocked(evt, threadId))
                snapshot = BuildSnapshotLocked();
        }

        if (snapshot is not null)
        {
            Publish(snapshot);
        }
    }

    private bool TryPromoteQueuedMessageOnLocalTurnStartLocked(AgentEventInfo evt, string threadId)
    {
        if (!_locallyInitiatedThreads.Contains(threadId))
            return false;

        var runId = evt.RunId;
        if (!string.IsNullOrEmpty(runId) &&
            _queuedMessageIdsByRunId.TryGetValue(threadId, out var byRunId) &&
            byRunId.TryGetValue(runId, out var queuedMessageId))
        {
            return PromoteQueuedMessageLocked(threadId, queuedMessageId);
        }

        if (string.IsNullOrEmpty(runId) &&
            TryGetSingleSendingQueuedMessageLocked(threadId, out var queued))
        {
            return PromoteQueuedMessageLocked(threadId, queued.Id);
        }

        return false;
    }

    private void RemovePendingLocalEchoLocked(string threadId, string messageId)
    {
        if (!_localSentTexts.TryGetValue(threadId, out var queue))
            return;

        var kept = new Queue<LocalSentText>(queue.Count);
        while (queue.Count > 0)
        {
            var pending = queue.Dequeue();
            if (string.Equals(pending.QueuedMessageId, messageId, StringComparison.Ordinal))
                continue;

            kept.Enqueue(pending);
        }

        StoreLocalEchoQueueLocked(threadId, kept);
    }

    /// <summary>
    /// Fetch the latest user message from history for a remotely-initiated turn.
    /// Called when lifecycle.start arrives for a thread we didn't locally initiate.
    /// </summary>
    private async Task FetchRemoteUserMessageAsync(string threadId, bool openResetGateOnSuccess = false)
    {
        var telemetryReason = openResetGateOnSuccess
            ? ChatBackfillTelemetryReason.ResetReconciliation
            : ChatBackfillTelemetryReason.RemoteTurn;
        var historyOperation = _telemetry.StartHistoryBackfill(telemetryReason);
        var historyOutcome = ChatTelemetryOutcome.Success;
        Exception? historyException = null;
        long requestResetVersion;
        long resetCutoffUtcMs;
        lock (_gate)
        {
            requestResetVersion = GetResetVersionLocked(threadId);
            resetCutoffUtcMs = GetResetCutoffUtcMsLocked(threadId);
        }

        try
        {
            var history = await _bridge.RequestChatHistoryAsync(threadId);
            if (history?.Messages is null || history.Messages.Count == 0) return;

            // Find the last user message in history.
            ChatMessageInfo? lastUser = null;
            for (int i = history.Messages.Count - 1; i >= 0; i--)
            {
                var role = (history.Messages[i].Role ?? "").ToLowerInvariant();
                var hText = history.Messages[i].Text;
                if (role == "user"
                    && !LooksLikeSystemControlNote(hText)
                    && !LooksLikeApprovalSlashCommand(hText))
                {
                    lastUser = history.Messages[i];
                    break;
                }
            }
            if (lastUser is null || string.IsNullOrEmpty(lastUser.Text)) return;

            ChatDataSnapshot? snapshotToPublish = null;

            // Check if we already have this user message as the last User entry
            // in the timeline (avoid duplicates on reconnect/reload).
            lock (_gate)
            {
                if (GetResetVersionLocked(threadId) != requestResetVersion ||
                    IsPreResetTimestampLocked(threadId, lastUser.Ts, resetCutoffUtcMs))
                {
                    Logger.Info($"[REMOTE] Ignoring stale remote user backfill after reset for threadId='{threadId}'");
                    return;
                }

                if (_timelines.TryGetValue(threadId, out var tl))
                {
                    for (int i = tl.Entries.Count - 1; i >= 0; i--)
                    {
                        if (tl.Entries[i].Kind == ChatTimelineItemKind.User)
                        {
                            if (tl.Entries[i].Text == lastUser.Text)
                                return; // already displayed
                            break;
                        }
                    }
                }

                if (openResetGateOnSuccess)
                {
                    _resetRemoteUserSeen.Add(threadId);
                    TryOpenResetGateFromPendingLifecycleLocked(threadId, acceptedRunId: null);
                }

                var meta = BuildLiveMetaLocked(
                    threadId,
                    lastUser.Ts,
                    lastUser.OpenClawId,
                    lastUser.OpenClawSeq);
                snapshotToPublish = ApplyEventLocked(
                    threadId,
                    new ChatUserMessageEvent(TruncateForChatEntry(lastUser.Text)),
                    meta);
            }

            Publish(snapshotToPublish);
            Logger.Info($"[REMOTE] Injected remote user message for threadId='{threadId}' len={lastUser.Text.Length}");
        }
        catch (Exception ex)
        {
            historyOutcome = ex is OperationCanceledException
                ? ChatTelemetryOutcome.Canceled
                : ChatTelemetryOutcome.Failure;
            historyException = ex;
            Logger.Warn($"[REMOTE] Failed to fetch remote user message for threadId='{threadId}': {ex.Message}");
        }
        finally
        {
            if (openResetGateOnSuccess)
            {
                lock (_gate) { _resetRemoteBackfillInFlight.Remove(threadId); }
            }
            _telemetry.FinishHistoryBackfill(historyOperation, historyOutcome, historyException);
        }
    }

    private ChatEvent? MapAgentEvent(AgentEventInfo evt)
    {
        var stream = evt.Stream?.ToLowerInvariant();
        if (string.IsNullOrEmpty(stream)) return null;

        switch (stream)
        {
            case "assistant":
                return MapAssistantEvent(evt);
            case "reasoning":
                return MapReasoningEvent(evt);
            case "lifecycle":
                return MapLifecycleEvent(evt);
            case "tool":
                // Spec name; gateway 2026.4.x uses ``item`` (kind=tool) instead.
                return MapToolEvent(evt);
            case "item":
                // Verified live shape: stream="item", data.kind ∈
                // {"tool","command","reasoning","message"}, data.phase ∈
                // {"start","end"}, data.title/itemId/details. We surface
                // tool items as chips and ignore the redundant command
                // children (their output arrives on ``command_output``).
                return MapItemEvent(evt);
            case "command_output":
                // Shell command stdout/stderr — attach to the active tool
                // chip as its ``Tool output`` body.
                return MapCommandOutputEvent(evt);
            case "job":
                return MapJobEvent(evt);
            case "approval":
                return MapApprovalEvent(evt);
            default:
                return null;
        }
    }

    // Whitelist of approval phases that mean "the approval is finished and
    // the banner should go away". Anything outside this set is treated as
    // an intermediate / unknown phase and leaves the banner alone — that
    // way a future gateway phase like ``acknowledged`` or ``in_progress``
    // can't accidentally wipe a live banner.
    private static bool IsTerminalApprovalPhase(string phase)
    {
        if (string.IsNullOrEmpty(phase)) return false;
        return string.Equals(phase, "resolved", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(phase, "denied", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(phase, "aborted", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(phase, "canceled", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(phase, "cancelled", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(phase, "expired", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(phase, "timeout", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(phase, "error", System.StringComparison.OrdinalIgnoreCase);
    }

    // Map a terminal approval phase (already validated by IsTerminalApprovalPhase)
    // to the timeline decision badge. ``resolved`` carries an allow-* decision
    // upstream (see OpenClawGatewayClient.HandleExecApprovalEvent), so it maps to
    // Allowed. ``denied`` maps to Denied. Every other terminal phase (aborted,
    // canceled/cancelled, expired, timeout, error) collapses to Expired — the
    // "decided elsewhere or never decided" badge.
    private static ChatPermissionDecision MapTerminalPhaseToDecision(string phase, string? decision = null)
    {
        if (string.Equals(phase, "resolved", System.StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(decision, ChatPermissionActionKeys.AllowAlways, System.StringComparison.OrdinalIgnoreCase))
                return ChatPermissionDecision.AllowedAlways;
            if (string.Equals(decision, ChatPermissionActionKeys.Deny, System.StringComparison.OrdinalIgnoreCase))
                return ChatPermissionDecision.Denied;
            return ChatPermissionDecision.Allowed;
        }
        if (string.Equals(phase, "denied", System.StringComparison.OrdinalIgnoreCase))
            return ChatPermissionDecision.Denied;
        return ChatPermissionDecision.Expired;
    }

    // Approval dedupe: gateway can resend ``requested`` on reconnect/replay.
    // Bounded LRU to keep this from growing unbounded across a long session.
    //
    // Instance-scoped (not static) so the LRU is bound to a single
    // provider/connection lifetime. Static state would survive across a
    // disconnect+reconnect+new-provider cycle in tests and host scenarios,
    // and could silently drop a fresh approval whose ID collides with a
    // long-dead one from a prior run. ``ResetApprovalDedupe`` is also
    // invoked when we leave the Connected state so the next connection
    // starts clean.
    private readonly object _approvalSeenLock = new();
    private readonly System.Collections.Generic.LinkedList<string> _approvalSeenOrder = new();
    private readonly System.Collections.Generic.HashSet<string> _approvalSeen
        = new(System.StringComparer.Ordinal);
    // Capacity is counted by id, not by logical approval. Paired slug/UUID
    // approvals consume two entries, so 128 preserves the prior ~64-approval
    // dedupe window.
    private const int ApprovalSeenCap = 128;

    // Approval id-asymmetry tracking.
    // The gateway sometimes emits ``approvalSlug`` only on ``requested``
    // and the full ``approvalId`` only on terminal events (or vice versa).
    // We prefer slug on both sides for matching (see ``MapApprovalEvent``)
    // but record the alternate identifier here so a terminal event that
    // carries only the "other" id can still resolve back to the live
    // pending banner. Stored bidirectionally and bounded by ApprovalSeenCap
    // via the same trim loop.
    private readonly Dictionary<string, string> _approvalAltIds = new(System.StringComparer.Ordinal);

    // Dedupe accepts both id forms (slug and full approvalId) so the same
    // approval doesn't render twice when two upstream paths surface it with
    // different ids — e.g. the top-level ``exec.approval.requested``
    // translator emits with the UUID while the agent-stream variant emits
    // with the shorter slug. If either form has been seen (or is already
    // linked to a seen form), suppress; when both forms are known, record the
    // link before suppressing so terminal events in either form can resolve.
    private bool MarkApprovalSeen(string requestId, string? altId = null)
    {
        if (string.IsNullOrEmpty(requestId)) return true; // can't dedupe — render
        lock (_approvalSeenLock)
        {
            RecordApprovalAltIdLocked(requestId, altId);

            if (ApprovalIdSeenLocked(requestId)) return false;
            if (IsDistinctApprovalId(requestId, altId) && ApprovalIdSeenLocked(altId!))
            {
                return false;
            }

            if (_approvalSeen.Add(requestId))
            {
                _approvalSeenOrder.AddLast(requestId);
            }

            if (IsDistinctApprovalId(requestId, altId) && _approvalSeen.Add(altId!))
            {
                _approvalSeenOrder.AddLast(altId!);
            }

            while (_approvalSeenOrder.Count > ApprovalSeenCap)
            {
                var oldest = _approvalSeenOrder.First!.Value;
                _approvalSeenOrder.RemoveFirst();
                EvictApprovalSeenIdLocked(oldest);
            }
            return true;
        }
    }

    private static bool IsDistinctApprovalId(string requestId, string? altId)
        => !string.IsNullOrEmpty(altId)
            && !string.Equals(altId, requestId, System.StringComparison.Ordinal);

    private bool ApprovalIdSeenLocked(string approvalId)
    {
        if (_approvalSeen.Contains(approvalId)) return true;
        return _approvalAltIds.TryGetValue(approvalId, out var altId)
            && _approvalSeen.Contains(altId);
    }

    private void RecordApprovalAltIdLocked(string requestId, string? altId)
    {
        if (!IsDistinctApprovalId(requestId, altId)) return;

        _approvalAltIds[requestId] = altId!;
        _approvalAltIds[altId!] = requestId;
    }

    private void EvictApprovalSeenIdLocked(string approvalId)
    {
        _approvalSeen.Remove(approvalId);
        if (!_approvalAltIds.TryGetValue(approvalId, out var altId))
            return;

        _approvalAltIds.Remove(approvalId);
        if (_approvalAltIds.TryGetValue(altId, out var reverse)
            && string.Equals(reverse, approvalId, System.StringComparison.Ordinal))
        {
            _approvalAltIds.Remove(altId);
        }

        if (_approvalSeen.Remove(altId))
        {
            RemoveApprovalSeenOrderValueLocked(altId);
        }
    }

    private void RemoveApprovalSeenOrderValueLocked(string approvalId)
    {
        for (var node = _approvalSeenOrder.First; node is not null; node = node.Next)
        {
            if (!string.Equals(node.Value, approvalId, System.StringComparison.Ordinal))
                continue;

            _approvalSeenOrder.Remove(node);
            return;
        }
    }

    private void ResetApprovalDedupe()
    {
        lock (_approvalSeenLock)
        {
            _approvalSeen.Clear();
            _approvalSeenOrder.Clear();
            _approvalAltIds.Clear();
        }
    }

    // Returns true if either of (evtPrimary, evtAlt) matches the pending
    // request — checking pendingId directly AND its recorded alternate id
    // (see ``_approvalAltIds`` above). All three inputs may be empty;
    // empty values never match.
    private bool ApprovalIdMatches(string pendingId, string evtPrimary, string evtAlt)
    {
        if (string.IsNullOrEmpty(pendingId)) return false;

        string? pendingAlt;
        lock (_approvalSeenLock)
        {
            _approvalAltIds.TryGetValue(pendingId, out pendingAlt);
        }

        bool Matches(string evt) =>
            !string.IsNullOrEmpty(evt)
            && (string.Equals(evt, pendingId, System.StringComparison.Ordinal)
                || (!string.IsNullOrEmpty(pendingAlt) && string.Equals(evt, pendingAlt, System.StringComparison.Ordinal)));

        return Matches(evtPrimary) || Matches(evtAlt);
    }

    // Render exec-approval prompts as a Permission-Request event so the
    // composer's existing Allow/Deny banner surfaces, matching the
    // dashboard modal's Allow once / Deny buttons. We only render on
    // phase=``requested``; ``resolved`` clears the banner via the
    // dedicated path in OnAgentEventReceived.
    //
    // Privacy: title/host/command are reflected back into the chat UI
    // (the user already sees them in the dashboard); no separate
    // telemetry log is emitted from this handler.
    private ChatEvent? MapApprovalEvent(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

        static string SafeStr(System.Text.Json.JsonElement obj, string name)
            => obj.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? (v.GetString() ?? "")
                : "";

        var phase = SafeStr(evt.Data, "phase");
        if (!string.Equals(phase, "requested", System.StringComparison.OrdinalIgnoreCase))
            return null;

        var approvalId = SafeStr(evt.Data, "approvalId");
        var slug = SafeStr(evt.Data, "approvalSlug");
        var host = SafeStr(evt.Data, "host");
        var command = SafeStr(evt.Data, "command");
        var title = SafeStr(evt.Data, "title");
        var message = SafeStr(evt.Data, "message");

        // Prefer the short slug (matches dashboard "/approve <slug>" format).
        // Fall back to full UUID only if slug is missing.
        var requestId = !string.IsNullOrEmpty(slug) ? slug : approvalId;
        if (string.IsNullOrEmpty(requestId)) return null;

        // The alternate id (the one we didn't pick as requestId). Pass it
        // to MarkApprovalSeen so a duplicate emission from the sibling
        // upstream path (slug-form vs UUID-form for the same approval) is
        // suppressed instead of creating a second timeline entry, which
        // would mark the first as Expired via ApplyPermissionRequest.
        var altId = !string.IsNullOrEmpty(slug) ? approvalId : slug;

        if (!MarkApprovalSeen(requestId, altId))
        {
            Logger.Info($"[Approval] suppressed duplicate requestId={requestId} altId={altId}");
            return null;
        }

        // MarkApprovalSeen also records the alternate id, including on the
        // duplicate-suppression path, so terminal events in either id form
        // can resolve back to this pending banner.

        // PermissionKind is the short tool/category label the composer shows;
        // ToolName is the contextual subtitle (host); Detail is the body
        // (command + optional message).
        var permissionKind = !string.IsNullOrEmpty(title) ? title : "Exec approval";
        var toolName = !string.IsNullOrEmpty(host) ? host : "node";

        var detail = command;
        if (!string.IsNullOrEmpty(message))
            detail = string.IsNullOrEmpty(detail) ? message : message + "\n\n" + detail;

        Logger.Info($"[Approval] emitting ChatPermissionRequestEvent requestId={requestId} kind='{permissionKind}' tool='{toolName}' detail.len={detail.Length}");
        return new ChatPermissionRequestEvent(requestId, permissionKind, toolName, detail, ChatPermissionActionKeys.ExecApprovalDefaults);
    }

    private static ChatEvent? MapAssistantEvent(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

        // Streaming token deltas: data.delta = "...next chunk..."
        if (evt.Data.TryGetProperty("delta", out var deltaProp) &&
            deltaProp.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var delta = deltaProp.GetString();
            if (!string.IsNullOrEmpty(delta))
                return new ChatMessageDeltaEvent(delta);
        }

        // NOTE: Cumulative `content`/`text` blocks are intentionally ignored
        // here — the gateway also fires a `chat.message` (role=assistant)
        // event carrying the same cumulative text, which OnChatMessageReceived
        // already maps to ChatMessageEvent. Honoring both paths produced two
        // identical assistant bubbles per turn (delta-bubble sealed by
        // lifecycle.end, then a fresh bubble from the chat.message arrival).
        return null;
    }

    private static ChatEvent? MapReasoningEvent(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

        if (evt.Data.TryGetProperty("delta", out var deltaProp) &&
            deltaProp.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var delta = deltaProp.GetString();
            if (!string.IsNullOrEmpty(delta))
            {
                try { Logger.Trace($"[ReasoningStream] kind=delta len={delta.Length}"); } catch { }
                return new ChatReasoningDeltaEvent(delta);
            }
        }

        var contentText = evt.Data.TryGetProperty("content", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String
            ? c.GetString()
            : (evt.Data.TryGetProperty("text", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String
                ? t.GetString()
                : null);
        if (!string.IsNullOrEmpty(contentText))
        {
            try { Logger.Trace($"[ReasoningStream] kind=full len={contentText!.Length}"); } catch { }
            return new ChatReasoningEvent(contentText!);
        }

        return null;
    }

    private static ChatEvent? MapLifecycleEvent(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        if (!evt.Data.TryGetProperty("phase", out var phaseProp)) return null;
        var phase = phaseProp.GetString()?.ToLowerInvariant();

        return phase switch
        {
            "start" => new ChatThinkingEvent(""),
            "end" => new ChatTurnEndEvent(),
            "error" => new ChatErrorEvent(evt.Summary
                ?? (evt.Data.TryGetProperty("message", out var m) ? m.GetString() ?? "Agent error" : "Agent error")),
            _ => null
        };
    }

    private static ChatEvent? MapToolEvent(AgentEventInfo evt)
    {
        // Expected payload shape: data.phase ∈ {"start","result","error"}, data.name, data.args
        if (evt.Data.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

        var phase = evt.Data.TryGetProperty("phase", out var phaseProp) ? phaseProp.GetString() ?? "" : "";
        var toolName = evt.Data.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
        var label = ExtractToolLabel(evt.Data);
        var toolCallId = evt.Data.TryGetProperty("itemId", out var idProp) ? idProp.GetString()
            : (evt.Data.TryGetProperty("callId", out var cProp) ? cProp.GetString() : null);

        return phase.ToLowerInvariant() switch
        {
            "start" => new ChatToolStartEvent(label, toolName, ToolCallId: toolCallId),
            "result" => new ChatToolOutputEvent(ExtractToolResultText(evt.Data, fallback: label), ToolCallId: toolCallId),
            "error" => new ChatToolErrorEvent(ExtractToolErrorText(evt.Data, fallback: label), ToolCallId: toolCallId),
            _ => null
        };
    }

    /// <summary>
    /// Map ``stream: "item"`` agent events (the gateway's actual tool/command
    /// lifecycle channel as of 2026.4.x — distinct from the spec's ``"tool"``
    /// stream which has not been observed in the wild).
    ///
    /// Verified payload shape:
    /// <code>
    /// {
    ///   "stream": "item",
    ///   "data": {
    ///     "itemId": "tool:call_xxx|fc_yyy",
    ///     "phase": "start" | "end",
    ///     "kind": "tool" | "command" | "reasoning" | "message",
    ///     "title": "exec run command openclaw → ..."
    ///   }
    /// }
    /// </code>
    ///
    /// We only surface ``kind: "tool"`` items as chips; ``kind: "command"``
    /// items are children of the parent tool whose output stream is
    /// ``command_output`` (handled separately).
    /// </summary>
    private static ChatEvent? MapItemEvent(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

        var kind = evt.Data.TryGetProperty("kind", out var kindProp) ? kindProp.GetString() ?? "" : "";
        var phase = evt.Data.TryGetProperty("phase", out var phaseProp) ? phaseProp.GetString() ?? "" : "";

        // ``kind=reasoning`` brackets each distinct thinking pass the model
        // performs within a turn (model reasons → tool call → reasons again).
        // The reasoning prose itself arrives on ``stream:"reasoning"``; here
        // we only need the ``phase=end`` boundary so the timeline reducer can
        // close the active reasoning bubble. Without this signal consecutive
        // reasoning passes concatenate into a single ever-growing entry,
        // because ActiveReasoningId is otherwise only cleared on turn end.
        if (string.Equals(kind, "reasoning", StringComparison.OrdinalIgnoreCase))
        {
            try { Logger.Trace($"[ReasoningItem] phase={phase}"); } catch { }
            return string.Equals(phase, "end", StringComparison.OrdinalIgnoreCase)
                ? new ChatReasoningEndEvent()
                : null;
        }

        if (!string.Equals(kind, "tool", StringComparison.OrdinalIgnoreCase))
            return null;

        var title = evt.Data.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
        var toolName = ExtractToolKindFromTitle(title);
        var itemId = evt.Data.TryGetProperty("itemId", out var idProp) ? idProp.GetString() : null;

        return phase.ToLowerInvariant() switch
        {
            "start" => new ChatToolStartEvent(title, toolName, ToolCallId: itemId),
            // ``end`` flips the active tool's status to Success even when no
            // command_output arrived (e.g. ``read``, ``glob`` — non-shell).
            // Use the title as a no-op output so the reducer marks Success.
            "end" => new ChatToolOutputEvent(string.Empty, ToolCallId: itemId),
            "error" => new ChatToolErrorEvent(title, ToolCallId: itemId),
            _ => null
        };
    }

    /// <summary>
    /// Map ``stream: "command_output"`` agent events. These carry shell
    /// stdout/stderr and may arrive in chunks (phase=delta) and as a final
    /// (phase=end) — we attach the text to the currently-active tool chip.
    /// </summary>
    private static ChatEvent? MapCommandOutputEvent(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

        var phase = evt.Data.TryGetProperty("phase", out var phaseProp) ? phaseProp.GetString() ?? "" : "";
        // Only emit on ``end`` — accumulating deltas into the same chip
        // would require a new reducer event; the consolidated final
        // payload is enough to populate the body in one go.
        if (!string.Equals(phase, "end", StringComparison.OrdinalIgnoreCase))
            return null;

        var output = ExtractCommandOutputText(evt.Data);
        if (string.IsNullOrEmpty(output))
            return null;

        // command_output events may carry an itemId or parentItemId that
        // identifies the parent tool call this output belongs to.
        var itemId = evt.Data.TryGetProperty("parentItemId", out var pidProp) ? pidProp.GetString()
            : (evt.Data.TryGetProperty("itemId", out var idProp) ? idProp.GetString() : null);

        return new ChatToolOutputEvent(output, ToolCallId: itemId);
    }

    /// <summary>
    /// Pull a short ``kind`` token out of the gateway's free-form ``title``
    /// for display in the chip header. Titles look like
    /// ``"exec run command ..."`` or ``"read ./foo"`` — we take the first
    /// token before whitespace, lower-cased.
    /// </summary>
    private static string ExtractToolKindFromTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "tool";
        var space = title.IndexOf(' ');
        var head = space > 0 ? title[..space] : title;
        return head.ToLowerInvariant();
    }

    /// <summary>
    /// Extract a printable text payload from a ``command_output`` end event.
    /// Walks the common fields the gateway uses: ``output``, ``text``,
    /// ``content``, ``stdout``, ``stderr``, ``preview``, ``body``.
    /// </summary>
    private static string ExtractCommandOutputText(System.Text.Json.JsonElement data)
    {
        foreach (var key in new[] { "output", "text", "content", "stdout", "preview", "body", "stderr" })
        {
            if (data.TryGetProperty(key, out var v))
            {
                if (v.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrEmpty(s))
                        return TruncateForToolOutput(s);
                }
                else if (v.ValueKind == System.Text.Json.JsonValueKind.Object &&
                         v.TryGetProperty("text", out var inner) &&
                         inner.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = inner.GetString();
                    if (!string.IsNullOrEmpty(s))
                        return TruncateForToolOutput(s);
                }
            }
        }

        // Fall back to the title field so the chip body isn't empty.
        if (data.TryGetProperty("title", out var titleProp) &&
            titleProp.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var s = titleProp.GetString();
            if (!string.IsNullOrEmpty(s))
                return TruncateForToolOutput(s);
        }

        return string.Empty;
    }

    private static ChatEvent? MapJobEvent(AgentEventInfo evt)
    {
        if (evt.Data.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        var state = evt.Data.TryGetProperty("state", out var stateProp) ? stateProp.GetString() ?? "" : "";
        return state.ToLowerInvariant() switch
        {
            "done" => new ChatTurnEndEvent(),
            "error" => new ChatErrorEvent(evt.Summary ?? "Agent error"),
            _ => null
        };
    }

    private static string ExtractToolLabel(System.Text.Json.JsonElement data)
    {
        if (data.TryGetProperty("args", out var args) && args.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var key in new[] { "command", "path", "file_path", "query", "url", "pattern" })
            {
                if (args.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrEmpty(s))
                        return s.Length > 80 ? s[..77] + "…" : s;
                }
            }
        }
        return data.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
    }

    /// <summary>
    /// Pulls a human-readable result snippet out of an agent tool result
    /// payload. Tries (in order): <c>data.result.content</c> (per spec),
    /// <c>data.result</c> as string, <c>data.output</c>, <c>data.content</c>,
    /// <c>data.text</c>. Falls back to <paramref name="fallback"/>.
    /// </summary>
    private static string ExtractToolResultText(System.Text.Json.JsonElement data, string fallback)
    {
        if (data.TryGetProperty("result", out var result))
        {
            if (result.ValueKind == System.Text.Json.JsonValueKind.String)
                return TruncateForToolOutput(result.GetString() ?? "");
            if (result.ValueKind == System.Text.Json.JsonValueKind.Object &&
                result.TryGetProperty("content", out var resultContent) &&
                resultContent.ValueKind == System.Text.Json.JsonValueKind.String)
                return TruncateForToolOutput(resultContent.GetString() ?? "");
        }

        foreach (var key in new[] { "output", "content", "text", "stdout" })
        {
            if (data.TryGetProperty(key, out var v) &&
                v.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrEmpty(s)) return TruncateForToolOutput(s);
            }
        }
        return fallback;
    }

    private static string ExtractToolErrorText(System.Text.Json.JsonElement data, string fallback)
    {
        foreach (var key in new[] { "error", "message", "stderr", "content" })
        {
            if (data.TryGetProperty(key, out var v))
            {
                if (v.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrEmpty(s)) return TruncateForToolOutput(s);
                }
                else if (v.ValueKind == System.Text.Json.JsonValueKind.Object &&
                         v.TryGetProperty("message", out var inner) &&
                         inner.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = inner.GetString();
                    if (!string.IsNullOrEmpty(s)) return TruncateForToolOutput(s);
                }
            }
        }
        return fallback;
    }

    private const int ToolOutputMaxChars = 4000;
    private static string TruncateForToolOutput(string text)
    {
        if (text.Length <= ToolOutputMaxChars) return text;
        return text[..ToolOutputMaxChars] + "\n…(truncated)";
    }

    /// <summary>
    /// Per-message UTF-8 byte cap applied to ANY chat-bubble payload that
    /// flows from the gateway into the timeline (live assistant text, live
    /// tool output, live system control notes, history replays, status /
    /// reasoning / error entries). Above this size the entry text is
    /// truncated at a code-point boundary and a marker is appended.
    /// </summary>
    /// <remarks>
    /// SECURITY (chat rubber-duck MEDIUM 4): very large markdown payloads
    /// can hang reducers or rendering work, and a
    /// multi-MB string can hang the reducer / virtualized list. 256 KiB is
    /// well above any reasonable chat message (a typical book chapter is
    /// ~50 KB). Truncation events are logged at <c>Debug</c> level so they
    /// don't dominate the operator log under normal use.
    /// </remarks>
    internal const int MaxEntryTextBytes = 256 * 1024;

    /// <summary>
    /// Truncate <paramref name="text"/> to at most
    /// <see cref="MaxEntryTextBytes"/> bytes when encoded as UTF-8 and
    /// append a <c> … [N bytes truncated]</c> marker. Slices at a UTF-16
    /// code-unit boundary that doesn't split a surrogate pair, then
    /// verifies the byte budget. Returns the input unchanged when it
    /// already fits or is null/empty.
    /// </summary>
    internal static string TruncateForChatEntry(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var enc = System.Text.Encoding.UTF8;
        // Cheap upper bound: every char is at most 3 UTF-8 bytes for the
        // BMP and surrogate pairs encode to 4 bytes / 2 chars (still ≤ 3
        // bytes per char). 4 is the worst case and keeps the cheap path
        // safe. If even the worst case fits, we're done.
        if ((long)text.Length * 4 <= MaxEntryTextBytes) return text;
        var actual = enc.GetByteCount(text);
        if (actual <= MaxEntryTextBytes) return text;

        // Binary search for the largest char-count whose UTF-8 byte count
        // fits in MaxEntryTextBytes minus a generous margin for the marker.
        var marker = string.Format(LocalizationHelper.GetString("Chat_TruncationMarkerFormat"), actual);
        int budget = MaxEntryTextBytes - enc.GetByteCount(marker);
        if (budget <= 0) budget = MaxEntryTextBytes / 2;

        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            // Don't split a surrogate pair: nudge mid back if it lands on
            // a low surrogate.
            if (mid < text.Length && char.IsLowSurrogate(text[mid])) mid--;
            if (mid <= lo)
            {
                hi = lo;
                continue;
            }
            int bytes = enc.GetByteCount(text.AsSpan(0, mid));
            if (bytes <= budget) lo = mid;
            else hi = mid - 1;
        }
        if (lo > 0 && char.IsHighSurrogate(text[lo - 1])) lo--;

        Logger.Debug($"[ChatTruncate] message {actual} bytes → {lo} chars (~{enc.GetByteCount(text.AsSpan(0, lo))} bytes); cap={MaxEntryTextBytes}");
        return string.Concat(text.AsSpan(0, lo), marker.AsSpan());
    }

    // ── chat.history flattened-tool-output recovery ──

    /// <summary>
    /// True when an assistant- or user-role <c>chat.history</c> message
    /// looks like a gateway control note that the web UI hides. We render
    /// these as a dim Status entry instead of a full bubble so the
    /// conversation flow doesn't get overwhelmed by transcript scaffolding.
    /// </summary>
    /// <remarks>
    /// SECURITY (chat-rubber-duck round 2 MEDIUM 2): the previous
    /// implementation matched on the bare ``System (untrusted):`` /
    /// ``System:`` prefix. That allowed a user (or a prompt-injected
    /// model) to craft a real user message that started with that prefix
    /// and have it silently reclassified as a dim system note (visible
    /// trust-taxonomy spoofing). We now require BOTH the prefix AND a
    /// known structural marker that the gateway actually emits.
    /// Plain user prose like ``System (untrusted): hello world`` no
    /// longer triggers the hide-as-status path and renders as a regular
    /// user/assistant bubble.
    /// </remarks>
    /// <summary>
    /// True when text is one of the approval slash-commands we send on the
    /// user's behalf (<c>/approve &lt;slug&gt; allow-once</c>,
    /// <c>/approve &lt;slug&gt; allow-always</c>, or
    /// <c>/deny &lt;slug&gt;</c>). Matches the exact dashboard grammar
    /// — not just the prefix — so legitimate user prose like
    /// "/approve the design changes" still renders as a normal bubble.
    /// </summary>
    /// <remarks>
    /// Slug shape: hex-ish identifier (letters, digits, dashes, underscores;
    /// 4–64 chars). This mirrors what the gateway emits for
    /// ``approvalSlug``; we don't anchor on a specific length because the
    /// gateway has changed it before.
    /// </remarks>
    internal static bool LooksLikeApprovalSlashCommand(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var t = text.Trim();
        return s_approvalSlashCommandRegex.IsMatch(t);
    }

    private static readonly System.Text.RegularExpressions.Regex s_approvalSlashCommandRegex =
        new(@"^/(?:approve\s+[A-Za-z0-9_-]{4,64}(?:\s+(?:allow-once|allow-always))?|deny\s+[A-Za-z0-9_-]{4,64})\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static bool LooksLikeSystemControlNote(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var t = text.TrimStart();
        bool hasPrefix =
            t.StartsWith("System (untrusted):", StringComparison.Ordinal) ||
            t.StartsWith("System:", StringComparison.Ordinal);
        if (!hasPrefix) return false;

        // We do not control the gateway protocol, and these frames currently
        // arrive as plain role=user text rather than structured provenance.
        // Keep this intentionally narrow: prefix + gateway-emitted structural
        // marker. If gateway wording changes, update this list and tests rather
        // than loosening to generic "System:" substring matches that could
        // misclassify ordinary user prose.
        return t.Contains("Exec completed (", StringComparison.Ordinal)
            || t.Contains("Process exited with code", StringComparison.Ordinal)
            || t.Contains("Command still running (session", StringComparison.Ordinal)
            || t.Contains("An async command you ran", StringComparison.Ordinal)
            || t.Contains("Tool reported", StringComparison.Ordinal)
            || t.Contains("exec result for ", StringComparison.Ordinal)
            || t.Contains("tool_call_", StringComparison.Ordinal)
            || t.Contains("Reset session", StringComparison.Ordinal);
    }

    /// <summary>
    /// Pre-compiled regex that matches a CLI option flag (e.g. <c>--help</c>,
    /// <c>--idempotency-key</c>, <c>-h</c>). Used by
    /// <see cref="LooksLikeFlattenedToolOutput"/> as a strong signal that an
    /// assistant message is verbatim CLI <c>--help</c> output.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex s_cliFlagRegex =
        new(@"(?:^|\s)(?:--[a-z][\w-]*|-[a-zA-Z])(?=\s|=|$)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // ── Content-block-seam repair ──────────────────────────────────────
    // Anthropic Claude returns an assistant turn as an ordered list of
    // content blocks ({text}, {tool_use}, {text}, …). The OpenClaw gateway
    // strips out the tool_use blocks (they're surfaced as tool chips) but
    // currently joins the remaining text blocks into a single
    // chat.message.text string WITHOUT inserting any whitespace between
    // them. That produces visibly glued seams in the assistant bubble,
    // e.g. (real captures from Sonnet 4.5 / Opus 4.7):
    //
    //   "...C:\Windows\System32**The command was blocked..."   (** + capital)
    //   "...with PowerShell:The C:\temp directory doesn't..."  (: + capital)
    //   "...on your Windows node.Looks like there's a..."      (. + capital)
    //   "...the deletion works?Got it - less exploring..."     (? + capital)
    //
    // The proper fix lives in the gateway. Until that ships, this pass
    // re-inserts a paragraph break at each high-confidence seam so the
    // rendered Markdown bubble reads naturally. Patterns are kept narrow
    // to minimize false positives in normal prose:
    //
    //   • Bold-close seam: lowercase/digit, then ``**``, then a capital
    //     letter — i.e. a heading-style bold span immediately followed by
    //     new sentence text. Inline emphasis like ``**foo**bar`` is left
    //     alone (next char is lowercase).
    //   • Punctuation seam: lowercase/digit, then ``. ! ? :``, then a
    //     capital letter — i.e. a sentence terminator immediately followed
    //     by a new sentence. Single-letter abbreviations such as ``U.S.A``
    //     are skipped (the lookbehind requires lowercase/digit, so ``S.A``
    //     doesn't match). File paths like ``C:\temp`` and URLs like
    //     ``https://`` don't match either (next char after the punctuation
    //     is not a capital letter).
    //
    // Fenced code blocks are skipped entirely so we never inject newlines
    // inside JSON/code samples. Inline single-backtick spans are left
    // unhandled (false-positive rate inside short inline code is low).
    private static readonly System.Text.RegularExpressions.Regex s_seamBoldClose =
        new(@"(?<=[a-z0-9])(\*\*)(?=[A-Z])",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // The sentence-punctuation seam matches a sentence-terminator (``. ! ? :``)
    // glued to the start of a new sentence (capital + lowercase run). The
    // tricky case is distinguishing real sentence seams (``done.Next step``)
    // from member-access in code identifiers (``Path.Combine``,
    // ``System.IO.File``, ``obj.Method``). Two guards do the work:
    //
    //  • Lookbehind ``[a-z0-9][.!?:]`` — the punctuation must follow a
    //    lowercase letter or digit. This already rejects ALL-CAPS
    //    abbreviations like ``U.S.A`` (S before the trailing ``.`` is
    //    uppercase, so the lookbehind fails).
    //
    //  • Lookahead ``[A-Z][a-z]+(?:[\s,;:!?]|$)`` — the next word must be
    //    a Pascal-case run (capital + ≥1 lowercase) followed immediately by
    //    whitespace, sentence punctuation, or end-of-string. That single
    //    trailing-char constraint is what rejects identifiers:
    //      ``Path.Combine(a, b)``       → ``Combine`` is followed by ``(`` ✗
    //      ``obj.Method()``             → ``Method`` is followed by ``(`` ✗
    //      ``System.IO.File.ReadAll…``  → ``Read`` is followed by ``A`` ✗
    //      ``db.Server01``              → ``Server`` is followed by ``0`` ✗
    //      ``MyVar.OtherVar``           → ``Other`` is followed by ``V`` ✗
    //      ``the field is x.Baz`` (EOS) → ``Baz`` is at end-of-string ✗
    //    while legitimate seams pass:
    //      ``PowerShell:The C:\\…``     → ``The`` is followed by `` `` ✓
    //      ``done.Next step``           → ``Next`` is followed by `` `` ✓
    //      ``All set!Anything else?``   → ``Anything`` is followed by `` `` ✓
    //      ``All done!Next, let's…``    → ``Next`` is followed by ``,`` ✓
    //
    // Note: we intentionally do NOT include end-of-string as a valid
    // "trailing" position. LLMs end explanations with bare identifiers
    // (``the field is x.Baz``, ``stored in obj.Foo``) and chat-message
    // frames don't always carry a trailing punctuation/newline, so the
    // ``$`` alternative would shred those into two paragraphs. Real
    // content-block seams always have more prose after them.
    //
    // The whole pattern is a pair of zero-width assertions, so the
    // replacement is a pure ``\n\n`` insert at the seam — no captured
    // punctuation to re-emit.
    private static readonly System.Text.RegularExpressions.Regex s_seamSentencePunct =
        new(@"(?<=[a-z0-9][.!?:])(?=[A-Z][a-z]+[\s,;:!?])",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // SearchValues gives SIMD-accelerated scan without a per-call heap allocation.
    private static readonly SearchValues<char> s_seamPunctChars = SearchValues.Create(".!?:");

    /// <summary>
    /// Re-insert paragraph breaks at gateway-glued content-block seams in
    /// an assistant message. Safe to call on any text — short text, text
    /// without seams, and text that is entirely fenced code all pass
    /// through unchanged. Fenced code blocks (``` ``` ``` ```) are skipped
    /// so JSON/code samples never get whitespace injected inside them.
    /// </summary>
    internal static string RepairContentBlockSeams(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (text.Length < 4) return text;

        // Fast path: if neither marker is present we can skip entirely.
        if (!text.Contains("**", System.StringComparison.Ordinal) &&
            text.AsSpan().IndexOfAny(s_seamPunctChars) < 0)
        {
            return text;
        }

        // Walk the string, alternating between prose and fenced-code
        // segments. Apply seam regexes to prose only. We tolerate
        // unclosed fences by treating everything after the dangling
        // opener as code (matches Markdown renderer behavior).
        var sb = new System.Text.StringBuilder(text.Length + 16);
        int i = 0;
        while (i < text.Length)
        {
            int fenceStart = text.IndexOf("```", i, System.StringComparison.Ordinal);
            if (fenceStart < 0)
            {
                sb.Append(RepairProseSegment(text[i..]));
                break;
            }

            sb.Append(RepairProseSegment(text.Substring(i, fenceStart - i)));

            int fenceEnd = text.IndexOf("```", fenceStart + 3, System.StringComparison.Ordinal);
            if (fenceEnd < 0)
            {
                // Unclosed fence — append the rest verbatim as code.
                sb.Append(text, fenceStart, text.Length - fenceStart);
                break;
            }

            // Append fenced block verbatim (including both fence markers).
            sb.Append(text, fenceStart, fenceEnd - fenceStart + 3);
            i = fenceEnd + 3;
        }

        return sb.ToString();
    }

    private static string RepairProseSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment)) return segment;
        segment = s_seamBoldClose.Replace(segment, "$1\n\n");
        // s_seamSentencePunct is a zero-width assertion (lookbehind +
        // lookahead) so the replacement is a pure insert of "\n\n" at
        // the seam — no captured punctuation to re-emit.
        segment = s_seamSentencePunct.Replace(segment, "\n\n");
        return segment;
    }

    // ── [ChatTrace] helpers ─────────────────────────────────────────────
    // Per-process random seed for ChatTraceHash. Mixing this into the FNV
    // initial state keeps identical-text frames colliding within a single
    // tray run (so duplicate-bubble diagnostics still work) while making
    // the hash useless as a content fingerprint outside this process: an
    // attacker with the log file can no longer rebuild the hash for a
    // guessed plaintext, and the value rotates on every tray restart.
    private static readonly uint ChatTraceHashSeed = unchecked((uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue));

    // Short FNV-1a-style 32-bit fold of the message text, seeded with a
    // per-process random value. Used in trace logs to tell two near-
    // duplicate frames apart at a glance without dumping the text itself.
    // Not a security hash; not reproducible outside this process.
    private static string ChatTraceHash(string text)
    {
        if (string.IsNullOrEmpty(text)) return "00000000";
        uint h = ChatTraceHashSeed;
        for (int i = 0; i < text.Length; i++)
        {
            h ^= text[i];
            h *= 16777619u;
        }
        return h.ToString("x8");
    }


    /// <summary>
    /// True when an assistant-role <c>chat.history</c> message is almost
    /// certainly the verbatim output of an exec tool that the gateway
    /// flattened into plain text on the way out (the spec confirms it
    /// strips ``<tool_call>`` / ``<function_call>`` XML and tool blocks
    /// before serving history).
    ///
    /// Detection strategy (any one match → flattened tool output):
    /// <list type="bullet">
    ///   <item>Verbatim exec terminator markers ("Process exited with code",
    ///     "Command still running (session", "Exec completed (").</item>
    ///   <item>Opens with a UNC / POSIX system path that's almost always a
    ///     tool result (e.g. <c>\\wsl.localhost\</c>, <c>/usr/</c>).</item>
    ///   <item>Opens with the OpenClaw CLI version banner
    ///     (<c>"OpenClaw 2026.4.23 ..."</c>) — these are <c>--help</c>
    ///     dumps captured by an exec tool.</item>
    ///   <item>Contains both <c>Usage:</c> AND any of <c>Options:</c> /
    ///     <c>Commands:</c> / <c>Examples:</c> / <c>Aliases:</c> —
    ///     classic CLI help layout.</item>
    ///   <item>Has ≥ 5 CLI flag tokens (matches <c>s_cliFlagRegex</c>) —
    ///     dense flag listings only show up in <c>--help</c> output.</item>
    /// </list>
    /// </summary>
    internal static bool LooksLikeFlattenedToolOutput(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 40) return false;

        // ── Strong terminator markers (exec wrappers).
        if (text.Contains("Process exited with code", StringComparison.Ordinal)) return true;
        if (text.Contains("Command still running (session", StringComparison.Ordinal)) return true;
        if (text.Contains("Exec completed (", StringComparison.Ordinal)) return true;

        // ── System-path openings.
        var head = text.AsSpan(0, Math.Min(80, text.Length));
        if (head.StartsWith("\\\\wsl.localhost\\")) return true;
        if (head.StartsWith("/usr/") || head.StartsWith("/home/") || head.StartsWith("/var/") ||
            head.StartsWith("/etc/") || head.StartsWith("/tmp/")) return true;

        // ── OpenClaw / common CLI tool version banner. Catches ``openclaw
        // help``, ``openclaw nodes invoke --help``, etc.
        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.StartsWith("OpenClaw 20") ||
            trimmed.StartsWith("OpenClaw v") ||
            trimmed.StartsWith("openclaw ")) return true;

        // ── Usage: + (Options:|Commands:|Examples:|Aliases:) — generic CLI
        // help layout regardless of which tool emitted it.
        if (text.Contains("Usage:", StringComparison.Ordinal) &&
            (text.Contains("Options:", StringComparison.Ordinal) ||
             text.Contains("Commands:", StringComparison.Ordinal) ||
             text.Contains("Examples:", StringComparison.Ordinal) ||
             text.Contains("Aliases:", StringComparison.Ordinal)))
            return true;

        // ── Dense ``--flag`` presence (≥ 5 matches is well above false-
        // positive rate for normal prose). Only run the regex when text is
        // long enough to potentially carry that many tokens.
        if (text.Length >= 200)
        {
            int flagCount = 0;
            foreach (System.Text.RegularExpressions.Match _ in s_cliFlagRegex.Matches(text))
            {
                if (++flagCount >= 5) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Best-guess kind label for a flattened-tool-output assistant
    /// message. Used to populate the tool chip's monospace kind suffix.
    /// Detects tool types from common output patterns as a heuristic
    /// fallback when cached metadata is unavailable.
    /// </summary>
    internal static string ClassifyFlattenedToolOutput(string text)
    {
        if (string.IsNullOrEmpty(text)) return "exec";

        // Shell/process markers
        if (text.Contains("Command still running", StringComparison.Ordinal) ||
            text.Contains("Process exited with code", StringComparison.Ordinal))
            return "bash";

        // File read patterns (numbered lines like "1. ", "42. ")
        if (s_numberedLineRegex.IsMatch(text))
            return "view";

        // Grep / search result patterns ("path/file.ext:123:matched line")
        if (s_grepResultRegex.IsMatch(text))
            return "grep";

        // Directory listing / glob patterns
        if (text.Contains("Directory:", StringComparison.Ordinal) ||
            text.Contains("Mode                ", StringComparison.Ordinal))
            return "glob";

        // Git output
        if (text.StartsWith("commit ", StringComparison.Ordinal) ||
            text.StartsWith("diff --git", StringComparison.Ordinal) ||
            text.Contains("Author:", StringComparison.Ordinal) && text.Contains("Date:", StringComparison.Ordinal))
            return "git";

        // Edit/write patterns
        if (text.Contains("successfully created", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("File written", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Applied edit", StringComparison.OrdinalIgnoreCase))
            return "edit";

        // Exec completed marker
        if (text.Contains("Exec completed (", StringComparison.Ordinal))
            return "exec";

        return "exec";
    }

    /// <summary>Matches numbered output lines typical of file view output (e.g. "  1. content").</summary>
    private static readonly System.Text.RegularExpressions.Regex s_numberedLineRegex =
        new(@"^\s*\d+\.\s", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>Matches grep-style results (path:line:content).</summary>
    private static readonly System.Text.RegularExpressions.Regex s_grepResultRegex =
        new(@"^[^\s:]+\.\w+:\d+:", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>
    /// Extract a short one-line summary from flattened tool output text
    /// for use as the tool chip label. Truncates to 80 chars.
    /// </summary>
    internal static string ExtractFlattenedToolSummary(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        // Use the first non-empty line as the summary
        var firstLine = text.AsSpan().TrimStart();
        var lineEnd = firstLine.IndexOfAny('\r', '\n');
        if (lineEnd > 0) firstLine = firstLine[..lineEnd];
        var summary = firstLine.Length > 80
            ? new string(firstLine[..77]) + "…"
            : new string(firstLine);
        return summary;
    }

    // ── State helpers ──

    /// <summary>
    /// Apply <see cref="TruncateForChatEntry(string?)"/> to whichever text
    /// payload a <see cref="ChatEvent"/> carries. Returns the input
    /// unchanged when there is nothing to truncate or the text already
    /// fits. Used by <see cref="ApplyEventAndPublish"/> to enforce the
    /// per-message size cap on every code path.
    /// </summary>
    /// <remarks>
    /// Coverage: every <see cref="ChatEvent"/> subtype that carries a
    /// caller-supplied text payload is truncated here, including the
    /// currently-unused
    /// <see cref="ChatModelChangedEvent"/> /
    /// <see cref="ChatPermissionRequestEvent"/> /
    /// <see cref="ChatIntentEvent"/> shapes — these don't flow through
    /// <see cref="ApplyEventAndPublish"/> today but covering them now
    /// prevents a future caller from bypassing the cap when wiring
    /// them up. The <see cref="ChatTurnEndEvent"/> /
    /// <see cref="ChatContextChangedEvent"/> shapes have no untrusted
    /// text fields and fall through unchanged.
    /// </remarks>
    internal static ChatEvent TruncateChatEvent(ChatEvent evt) => evt switch
    {
        ChatUserMessageEvent e => e with { Text = TruncateForChatEntry(e.Text) },
        ChatThinkingEvent e => e with { Text = TruncateForChatEntry(e.Text) },
        ChatReasoningEvent e => e with { Text = TruncateForChatEntry(e.Text) },
        ChatReasoningDeltaEvent e => e with { Text = TruncateForChatEntry(e.Text) },
        ChatMessageEvent e => e with
        {
            Text = TruncateForChatEntry(e.Text),
            ReasoningText = e.ReasoningText is null ? null : TruncateForChatEntry(e.ReasoningText)
        },
        ChatMessageDeltaEvent e => e with { Text = TruncateForChatEntry(e.Text) },
        ChatToolStartEvent e => e with
        {
            Text = TruncateForChatEntry(e.Text),
            ToolName = TruncateForChatEntry(e.ToolName)
        },
        ChatToolOutputEvent e => e with { Text = TruncateForChatEntry(e.Text) },
        ChatToolErrorEvent e => e with { Text = TruncateForChatEntry(e.Text) },
        ChatStatusEvent e => e with { Text = TruncateForChatEntry(e.Text) },
        ChatErrorEvent e => e with { Text = TruncateForChatEntry(e.Text) },
        ChatRestoredEvent e => e with { Text = TruncateForChatEntry(e.Text) },
        ChatRawEvent e => e with { Text = e.Text is null ? null : TruncateForChatEntry(e.Text) },
        ChatModelChangedEvent e => e with { Model = TruncateForChatEntry(e.Model) },
        ChatIntentEvent e => e with { Intent = TruncateForChatEntry(e.Intent) },
        ChatPermissionRequestEvent e => e with
        {
            PermissionKind = TruncateForChatEntry(e.PermissionKind),
            ToolName = TruncateForChatEntry(e.ToolName),
            Detail = TruncateForChatEntry(e.Detail)
        },
        _ => evt
    };

    private void ApplyEventAndPublish(string threadId, ChatEvent evt, ChatEntryMetadata? meta = null)
    {
        // Defense-in-depth (chat rubber-duck MEDIUM 4): cap text on every
        // event that lands in the timeline. Live history-load and
        // OnChatMessageReceived already truncate at the call site, but
        // agent-event paths (reasoning deltas, status notes, raw tool
        // output, errors) flow through here directly. Keeping the cap
        // here too guarantees no untrusted gateway payload bypasses the
        // limit.
        evt = TruncateChatEvent(evt);

        ChatDataSnapshot snapshot;
        lock (_gate)
        {
            snapshot = ApplyEventLocked(threadId, evt, meta);
        }
        Publish(snapshot);
    }

    private ChatDataSnapshot ApplyEventLocked(string threadId, ChatEvent evt, ChatEntryMetadata? meta)
    {
        var current = GetOrCreateTimelineLocked(threadId);
        var beforeIds = new HashSet<string>(current.Entries.Count);
        for (int i = 0; i < current.Entries.Count; i++) beforeIds.Add(current.Entries[i].Id);

        var next = ChatTimelineReducer.Apply(current, evt);
        _timelines[threadId] = next;

        // Capture metadata for any newly-created entries. Updates to
        // existing entries (e.g. UpsertAssistant on the active assistant)
        // intentionally don't overwrite — the original creation timestamp
        // for the turn is more useful than the most-recent-delta time.
        // EXCEPTION: if the new metadata carries usage tokens (only
        // emitted on terminal frames), merge them into the existing entry
        // so the footer pills (↑/↓/R/ctx%) light up at end-of-turn.
        if (meta is not null)
        {
            var threadMeta = GetOrCreateThreadMetaLocked(threadId);
            var hasUsage = meta.InputTokens is not null || meta.OutputTokens is not null
                || meta.ResponseTokens is not null || meta.ContextPercent is not null;
            for (int i = 0; i < next.Entries.Count; i++)
            {
                var id = next.Entries[i].Id;
                var isNew = !beforeIds.Contains(id);
                if (isNew && !threadMeta.ContainsKey(id))
                {
                    threadMeta[id] = meta;
                }
                else if (hasUsage && threadMeta.TryGetValue(id, out var existing)
                    && (existing.InputTokens is null && existing.OutputTokens is null))
                {
                    // Merge usage onto the existing assistant entry whose
                    // text was just upserted by this final delta.
                    threadMeta[id] = existing with
                    {
                        InputTokens = meta.InputTokens ?? existing.InputTokens,
                        OutputTokens = meta.OutputTokens ?? existing.OutputTokens,
                        ResponseTokens = meta.ResponseTokens ?? existing.ResponseTokens,
                        ContextPercent = meta.ContextPercent ?? existing.ContextPercent
                    };
                }
            }
        }

        return BuildSnapshotLocked();
    }

    private Dictionary<string, ChatEntryMetadata> GetOrCreateThreadMetaLocked(string threadId)
    {
        if (!_entryMeta.TryGetValue(threadId, out var meta))
        {
            meta = new Dictionary<string, ChatEntryMetadata>();
            _entryMeta[threadId] = meta;
        }
        return meta;
    }

    private readonly record struct ResetClearPersistence(
        bool SaveAbortedIds,
        bool SaveToolMeta,
        bool SaveAttachmentMeta,
        string[] SubmittedRunIds);

    private long GetResetVersionLocked(string threadId) =>
        _resetVersions.TryGetValue(threadId, out var version) ? version : 0;

    private long GetResetCutoffUtcMsLocked(string threadId) =>
        _resetCutoffUtcMs.TryGetValue(threadId, out var cutoff) ? cutoff : 0;

    private ResetClearPersistence ClearThreadHistoryAfterResetLocked(string threadId)
    {
        _telemetry.FinishThread(threadId, ChatTelemetryOutcome.Canceled, ChatTurnTelemetryReason.Reset);
        var oldSessionId = _sessionIds.TryGetValue(threadId, out var sid) ? sid : null;
        var saveToolMeta = false;
        var saveAttachmentMeta = false;
        var saveAbortedIds = _persistedAbortedIds.Remove(threadId);

        if (!string.IsNullOrEmpty(oldSessionId))
        {
            saveToolMeta = _toolMetaCache.Remove(oldSessionId);
            saveAttachmentMeta = _attachmentMetaCache.Remove(oldSessionId);
            _resetClearedSessionIds[threadId] = oldSessionId;
        }
        else
        {
            _resetClearedSessionIds.Remove(threadId);
        }
        saveToolMeta = _toolMetaCache.Remove(threadId) || saveToolMeta;
        saveAttachmentMeta = _attachmentMetaCache.Remove(threadId) || saveAttachmentMeta;

        if (saveToolMeta)
        {
            _toolMetaCacheDirty = true;
            _toolMetaSaveVersion++;
        }

        var submittedRunIds = new HashSet<string>(StringComparer.Ordinal);
        if (_activeRunIds.TryGetValue(threadId, out var activeRunId) && !string.IsNullOrEmpty(activeRunId))
            submittedRunIds.Add(activeRunId);
        if (_queuedMessageIdsByRunId.TryGetValue(threadId, out var queuedRunIds))
        {
            foreach (var queuedRunId in queuedRunIds.Keys)
                submittedRunIds.Add(queuedRunId);
        }
        if (_localSentTexts.TryGetValue(threadId, out var localEchoes))
        {
            foreach (var localEcho in localEchoes)
                AddResetSubmittedLocalEchoTextLocked(threadId, localEcho.Text, localEcho.SentAt);
        }

        _resetVersions[threadId] = GetResetVersionLocked(threadId) + 1;
        _resetCutoffUtcMs[threadId] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _resetAwaitingUserMessage.Add(threadId);
        _timelines[threadId] = ChatTimelineState.Initial() with { HistoryLoaded = true };
        _entryMeta.Remove(threadId);
        _sessionIds.Remove(threadId);
        _historyLoaded.Add(threadId);
        _historyRetryCount.Remove(threadId);
        _activeRunIds.Remove(threadId);
        _activeRunStartSequences.Remove(threadId);
        _pendingAbortCounts.Remove(threadId);
        _abortedThreads.Remove(threadId);
        _locallyInitiatedThreads.Remove(threadId);
        _localSentTexts.Remove(threadId);
        _queuedMessages.Remove(threadId);
        _queuedSendRequests.Remove(threadId);
        ClearQueuedDrainScheduleLocked(threadId);
        _queuedMessageIdsByRunId.Remove(threadId);
        _terminalRunIdsByThread.Remove(threadId);
        _assistantFallbackPromotedThreads.Remove(threadId);
        _resetAcceptedRunIds.Remove(threadId);
        _resetLocalSendWithoutRunVersions.Remove(threadId);
        _resetLocalSendWithoutRunStartSequences.Remove(threadId);
        _resetLocalEchoSequences.Remove(threadId);
        _resetPendingLifecycleStarts.Remove(threadId);
        _resetRemoteBackfillInFlight.Remove(threadId);
        _resetRemoteUserSeen.Remove(threadId);
        foreach (var submittedRunId in submittedRunIds)
            AddResetIgnoredRunIdLocked(threadId, submittedRunId);

        return new ResetClearPersistence(saveAbortedIds, saveToolMeta, saveAttachmentMeta, submittedRunIds.ToArray());
    }

    private void PersistClearedResetState(ResetClearPersistence persistence)
    {
        if (persistence.SaveAbortedIds)
            SaveAbortedIds();
        if (persistence.SaveToolMeta)
            SaveToolMetaCache();
        if (persistence.SaveAttachmentMeta)
            SaveAttachmentMetaCache();
    }

    private void AddResetIgnoredRunIdLocked(string threadId, string runId)
    {
        if (!_resetIgnoredRunIds.TryGetValue(threadId, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _resetIgnoredRunIds[threadId] = set;
        }
        set.Add(runId);
    }

    private void AddResetSubmittedLocalEchoTextLocked(string threadId, string text, DateTimeOffset sentAt)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (!_resetSubmittedLocalEchoTexts.TryGetValue(threadId, out var texts))
        {
            texts = new Dictionary<string, Queue<DateTimeOffset>>(StringComparer.Ordinal);
            _resetSubmittedLocalEchoTexts[threadId] = texts;
        }

        var normalized = text.Trim();
        if (!texts.TryGetValue(normalized, out var timestamps))
        {
            timestamps = new Queue<DateTimeOffset>();
            texts[normalized] = timestamps;
        }
        timestamps.Enqueue(sentAt);
    }

    private bool TryConsumeResetSubmittedLocalEchoTextLocked(string threadId, string text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            !_resetSubmittedLocalEchoTexts.TryGetValue(threadId, out var texts))
        {
            return false;
        }

        var normalized = text.Trim();
        if (!texts.TryGetValue(normalized, out var timestamps))
            return false;

        var now = DateTimeOffset.UtcNow;
        while (timestamps.Count > 0 && now - timestamps.Peek() > LocalEchoSuppressionWindow)
            timestamps.Dequeue();

        if (timestamps.Count == 0)
        {
            texts.Remove(normalized);
            if (texts.Count == 0)
                _resetSubmittedLocalEchoTexts.Remove(threadId);
            return false;
        }

        timestamps.Dequeue();
        if (timestamps.Count == 0)
            texts.Remove(normalized);

        if (texts.Count == 0)
            _resetSubmittedLocalEchoTexts.Remove(threadId);
        return true;
    }

    private bool HasPendingLocalEchoTextLocked(string threadId, string text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            !_localSentTexts.TryGetValue(threadId, out var queue) ||
            queue.Count == 0)
        {
            return false;
        }

        var normalized = text.Trim();
        return queue.Any(pending => string.Equals(pending.Text, normalized, StringComparison.Ordinal));
    }

    private void AddResetAcceptedRunIdLocked(string threadId, string runId)
    {
        if (!_resetAwaitingUserMessage.Contains(threadId))
            return;

        if (!_resetAcceptedRunIds.TryGetValue(threadId, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _resetAcceptedRunIds[threadId] = set;
        }
        set.Add(runId);
        TryOpenResetGateFromPendingLifecycleLocked(threadId, acceptedRunId: runId);
    }

    private readonly record struct PendingResetLifecycleStart(AgentEventInfo Event, long Sequence);

    private bool ShouldDropChatMessageAfterResetLocked(
        string threadId,
        string roleLower,
        string rawText,
        long tsMs,
        out string? consumeEchoText,
        out bool requestRemoteBackfill)
    {
        consumeEchoText = null;
        requestRemoteBackfill = false;
        var isNormalUserText = roleLower == "user" &&
            !LooksLikeApprovalSlashCommand(rawText) &&
            !LooksLikeSystemControlNote(rawText);

        if (isNormalUserText &&
            !HasPendingLocalEchoTextLocked(threadId, rawText) &&
            TryConsumeResetSubmittedLocalEchoTextLocked(threadId, rawText))
        {
            return true;
        }

        if (!_resetAwaitingUserMessage.Contains(threadId))
        {
            return IsPreResetTimestampLocked(threadId, tsMs, GetResetCutoffUtcMsLocked(threadId));
        }

        var isFreshUser = isNormalUserText &&
            !IsPreResetTimestampLocked(threadId, tsMs, GetResetCutoffUtcMsLocked(threadId));

        if (isFreshUser &&
            _localSentTexts.TryGetValue(threadId, out var echoQueue) &&
            echoQueue.Count > 0 &&
            echoQueue.Any(pending => string.Equals(pending.Text, rawText.Trim(), StringComparison.Ordinal)))
        {
            consumeEchoText = rawText.Trim();
            _resetLocalEchoSequences[threadId] = _resetLifecycleStartSequence;
            if (TryOpenResetGateFromPendingLifecycleLocked(threadId, acceptedRunId: null))
                return false;
        }
        else if (isFreshUser && tsMs > 0)
        {
            _resetRemoteUserSeen.Add(threadId);
            if (TryOpenResetGateFromPendingLifecycleLocked(threadId, acceptedRunId: null))
                return false;
        }
        else if (isFreshUser && _resetRemoteBackfillInFlight.Add(threadId))
        {
            requestRemoteBackfill = true;
        }

        return true;
    }

    private void PromoteOldestQueuedMessageBeforeAssistantIfNeeded(string threadId)
    {
        ChatDataSnapshot? snapshot = null;
        lock (_gate)
        {
            // This fallback covers the degenerate case where an assistant frame
            // arrives before any user echo or lifecycle.start/run mapping. When
            // a run is active, lifecycle/ACK correlation owns the handoff; when
            // multiple queued prompts exist, positional assistant fallback is
            // ambiguous and can create false user boundaries that duplicate the
            // assistant bubble.
            if (_locallyInitiatedThreads.Contains(threadId)
                && TryGetSingleSendingQueuedMessageLocked(threadId, out var queued)
                && !_activeRunIds.ContainsKey(threadId)
                && !_assistantFallbackPromotedThreads.Contains(threadId)
                && PromoteQueuedMessageLocked(threadId, queued.Id))
            {
                snapshot = BuildSnapshotLocked();
            }
        }

        if (snapshot is not null)
            Publish(snapshot);
    }

    private AssistantQueueFrameDisposition ClassifyAssistantQueueFrameLocked(
        string threadId,
        string assistantText,
        string? gatewayMessageId,
        int? openClawSeq)
    {
        if ((!string.IsNullOrEmpty(gatewayMessageId) || openClawSeq is not null) &&
            IsIdentifiedCompletedAssistantDuplicateLocked(
                threadId,
                assistantText,
                gatewayMessageId,
                openClawSeq))
        {
            return AssistantQueueFrameDisposition.Drop;
        }

        if (string.IsNullOrEmpty(gatewayMessageId) &&
            openClawSeq is null &&
            IsIdentitylessAssistantRetransmitAcrossLocalUserBoundaryLocked(threadId, assistantText))
        {
            return AssistantQueueFrameDisposition.Drop;
        }

        if (!_locallyInitiatedThreads.Contains(threadId) ||
            !TryGetSingleSendingQueuedMessageLocked(threadId, out _) ||
            _activeRunIds.ContainsKey(threadId) ||
            _assistantFallbackPromotedThreads.Contains(threadId) ||
            !_timelines.TryGetValue(threadId, out var timeline))
        {
            return AssistantQueueFrameDisposition.Render;
        }

        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            var entry = timeline.Entries[i];
            if (entry.Kind != ChatTimelineItemKind.Assistant)
                continue;
            if (entry.IsStreaming || !string.Equals(entry.Text, assistantText, StringComparison.Ordinal))
                return AssistantQueueFrameDisposition.Render;
            if (string.IsNullOrEmpty(gatewayMessageId) && openClawSeq is null)
                // In this queue-boundary window, an identity-less same-text frame cannot be tied
                // to the queued prompt; replaying it can attach stale output to the next prompt.
                return AssistantQueueFrameDisposition.Drop;
            if (!_entryMeta.TryGetValue(threadId, out var threadMeta) ||
                !threadMeta.TryGetValue(entry.Id, out var existing))
            {
                return AssistantQueueFrameDisposition.Render;
            }

            var sameGatewayIdentity =
                (!string.IsNullOrEmpty(gatewayMessageId) &&
                 string.Equals(existing.GatewayMessageId, gatewayMessageId, StringComparison.Ordinal)) ||
                (openClawSeq is not null && existing.OpenClawSeq == openClawSeq);
            return sameGatewayIdentity
                ? AssistantQueueFrameDisposition.Drop
                : AssistantQueueFrameDisposition.Render;
        }

        return AssistantQueueFrameDisposition.Render;
    }

    private bool IsIdentitylessAssistantRetransmitAcrossLocalUserBoundaryLocked(string threadId, string assistantText)
    {
        if (!_locallyInitiatedThreads.Contains(threadId) ||
            _activeRunIds.ContainsKey(threadId) ||
            !_timelines.TryGetValue(threadId, out var timeline) ||
            !_entryMeta.TryGetValue(threadId, out var threadMeta))
        {
            return false;
        }

        var sawLatestLocalUserBoundary = false;
        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            var entry = timeline.Entries[i];
            if (!sawLatestLocalUserBoundary)
            {
                if (entry.Kind == ChatTimelineItemKind.Assistant)
                    return false;
                if (entry.Kind == ChatTimelineItemKind.User &&
                    threadMeta.TryGetValue(entry.Id, out var meta) &&
                    meta.IsLocalQueuedSend)
                {
                    sawLatestLocalUserBoundary = true;
                }
                continue;
            }

            if (entry.Kind == ChatTimelineItemKind.Assistant)
                return !entry.IsStreaming && string.Equals(entry.Text, assistantText, StringComparison.Ordinal);
            if (entry.Kind == ChatTimelineItemKind.User)
                return false;
        }

        return false;
    }

    private bool IsIdentifiedCompletedAssistantDuplicateLocked(
        string threadId,
        string assistantText,
        string? gatewayMessageId,
        int? openClawSeq)
    {
        if (!_timelines.TryGetValue(threadId, out var timeline) ||
            !_entryMeta.TryGetValue(threadId, out var threadMeta))
        {
            return false;
        }

        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            var entry = timeline.Entries[i];
            if (entry.Kind != ChatTimelineItemKind.Assistant ||
                entry.IsStreaming ||
                !threadMeta.TryGetValue(entry.Id, out var existing))
            {
                continue;
            }

            var bothHaveGatewayIds =
                !string.IsNullOrEmpty(gatewayMessageId) &&
                !string.IsNullOrEmpty(existing.GatewayMessageId);
            if (bothHaveGatewayIds &&
                string.Equals(existing.GatewayMessageId, gatewayMessageId, StringComparison.Ordinal))
            {
                return true;
            }
            if (!bothHaveGatewayIds &&
                openClawSeq is not null &&
                existing.OpenClawSeq == openClawSeq &&
                string.Equals(entry.Text, assistantText, StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(gatewayMessageId) &&
                    string.IsNullOrEmpty(existing.GatewayMessageId))
                {
                    threadMeta[entry.Id] = existing with { GatewayMessageId = gatewayMessageId };
                }
                return true;
            }
        }

        return false;
    }

    private bool HasSendingQueuedMessagesLocked(string threadId)
        => _queuedMessages.TryGetValue(threadId, out var queued) &&
           queued.Any(message => message.SendState == ChatQueuedMessageSendState.Sending);

    private bool HasPendingQueuedMessagesLocked(string threadId)
        => _queuedMessages.TryGetValue(threadId, out var queued) &&
           queued.Any(message => message.SendState is ChatQueuedMessageSendState.Queued or ChatQueuedMessageSendState.Sending);

    private bool TryGetSingleSendingQueuedMessageLocked(string threadId, out ChatQueuedMessage message)
    {
        message = default!;
        if (!_queuedMessages.TryGetValue(threadId, out var queued))
            return false;

        ChatQueuedMessage? found = null;
        foreach (var candidate in queued)
        {
            if (candidate.SendState != ChatQueuedMessageSendState.Sending)
                continue;
            if (found is not null)
                return false;
            found = candidate;
        }

        if (found is null)
            return false;

        message = found;
        return true;
    }

    private bool ShouldDropAgentEventAfterResetLocked(AgentEventInfo evt, string threadId, out bool reloadHistoryAfterDrop)
    {
        reloadHistoryAfterDrop = false;
        if (IsResetIgnoredRunLocked(threadId, evt.RunId, evt, out reloadHistoryAfterDrop))
            return true;

        var eventTsMs = evt.Ts > 0 ? (long)evt.Ts : 0L;
        var cutoff = GetResetCutoffUtcMsLocked(threadId);
        if (!_resetAwaitingUserMessage.Contains(threadId))
            return IsPreResetTimestampLocked(threadId, eventTsMs, cutoff);

        if (IsAcceptedPostResetLifecycleStartLocked(threadId, evt, _resetLifecycleStartSequence + 1))
        {
            OpenResetGateForLifecycleStartLocked(threadId, evt);
            return false;
        }

        if (IsPreResetTimestampLocked(threadId, eventTsMs, cutoff))
            return true;

        if (IsLifecycleStart(evt))
            BufferResetLifecycleStartLocked(threadId, evt);

        return true;
    }

    private bool IsAcceptedPostResetLifecycleStartLocked(string threadId, AgentEventInfo evt, long lifecycleStartSequence)
    {
        if (!IsLifecycleStart(evt))
            return false;

        if (!string.IsNullOrEmpty(evt.RunId) &&
            _resetAcceptedRunIds.TryGetValue(threadId, out var acceptedRunIds) &&
            acceptedRunIds.Contains(evt.RunId))
        {
            return true;
        }

        if (_resetLocalSendWithoutRunVersions.TryGetValue(threadId, out var localSendVersion) &&
            localSendVersion == GetResetVersionLocked(threadId) &&
            _resetLocalSendWithoutRunStartSequences.TryGetValue(threadId, out var localSendStartSequence) &&
        _resetLocalEchoSequences.TryGetValue(threadId, out var localEchoSequence) &&
        localEchoSequence >= localSendStartSequence &&
        lifecycleStartSequence > localSendStartSequence &&
        evt.Ts > 0 &&
        !IsPreResetTimestampLocked(threadId, (long)evt.Ts, GetResetCutoffUtcMsLocked(threadId)))
        {
            return true;
        }

        return _resetRemoteUserSeen.Contains(threadId) &&
            !IsPreResetTimestampLocked(threadId, evt.Ts > 0 ? (long)evt.Ts : 0L, GetResetCutoffUtcMsLocked(threadId));
    }

    private void BufferResetLifecycleStartLocked(string threadId, AgentEventInfo evt)
    {
        if (!_resetPendingLifecycleStarts.TryGetValue(threadId, out var pending))
        {
            pending = new List<PendingResetLifecycleStart>();
            _resetPendingLifecycleStarts[threadId] = pending;
        }

        if (!string.IsNullOrEmpty(evt.RunId) &&
            pending.Exists(e => string.Equals(e.Event.RunId, evt.RunId, StringComparison.Ordinal)))
        {
            return;
        }

        pending.Add(new PendingResetLifecycleStart(evt, ++_resetLifecycleStartSequence));
        if (pending.Count > 8)
            pending.RemoveRange(0, pending.Count - 8);
    }

    private bool TryOpenResetGateFromPendingLifecycleLocked(string threadId, string? acceptedRunId)
    {
        if (!_resetAwaitingUserMessage.Contains(threadId) ||
            !_resetPendingLifecycleStarts.TryGetValue(threadId, out var pending))
        {
            return false;
        }

        for (var i = 0; i < pending.Count; i++)
        {
            var pendingStart = pending[i];
            var evt = pendingStart.Event;
            if (acceptedRunId is not null)
            {
                if (!string.Equals(evt.RunId, acceptedRunId, StringComparison.Ordinal))
                    continue;
            }
            else if (!IsAcceptedPostResetLifecycleStartLocked(threadId, evt, pendingStart.Sequence))
            {
                continue;
            }

            pending.RemoveAt(i);
            OpenResetGateForLifecycleStartLocked(threadId, evt);
            return true;
        }

        return false;
    }

    private void SnapshotLatestAssistantUsage(string threadId)
    {
        ChatDataSnapshot? snapshot = null;
        lock (_gate)
        {
            var session = ResolveSessionForThreadLocked(threadId);
            if (session is null) return;
            if (SnapshotLatestAssistantUsageLocked(session, threadId))
                snapshot = BuildSnapshotLocked();
        }

        if (snapshot is not null)
            Publish(snapshot);
    }

    private void SnapshotAssistantUsageContribution(string threadId, ChatEntryMetadata meta)
    {
        ChatDataSnapshot? snapshot = null;
        lock (_gate)
        {
            if (SnapshotAssistantUsageContributionLocked(threadId, meta))
                snapshot = BuildSnapshotLocked();
        }

        if (snapshot is not null)
            Publish(snapshot);
    }

    private bool SnapshotAssistantUsageContributionLocked(string threadId, ChatEntryMetadata meta)
    {
        var currentUsage = UsageValue(meta);
        if (currentUsage is null || currentUsage <= 0)
            return false;

        if (!_timelines.TryGetValue(threadId, out var timeline))
            return false;

        var contextTokens = meta.ContextTokens;
        if ((contextTokens is null || contextTokens <= 0)
            && _sessions.FirstOrDefault(s => string.Equals(s.Key, threadId, StringComparison.Ordinal)) is { ContextTokens: > 0 } session)
        {
            contextTokens = session.ContextTokens;
        }

        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            var entry = timeline.Entries[i];
            if (entry.Kind != ChatTimelineItemKind.Assistant)
                continue;

            var threadMeta = GetOrCreateThreadMetaLocked(threadId);
            threadMeta.TryGetValue(entry.Id, out var existing);
            var previousUsage = LatestAssistantUsageBeforeLocked(timeline, threadMeta, i);
            var candidateUsage = (previousUsage ?? 0) + currentUsage.Value;
            var cumulativeUsage = Math.Max(candidateUsage, existing?.ResponseTokens ?? 0);
            if (existing?.ResponseTokens == cumulativeUsage
                && existing.UsageContributionTokens == currentUsage
                && existing.ContextTokens == contextTokens)
            {
                return false;
            }

            threadMeta[entry.Id] = (existing ?? BuildLiveMetaLocked(threadId)) with
            {
                InputTokens = meta.InputTokens ?? existing?.InputTokens,
                OutputTokens = meta.OutputTokens ?? existing?.OutputTokens,
                ResponseTokens = cumulativeUsage,
                ContextPercent = meta.ContextPercent ?? existing?.ContextPercent,
                ContextTokens = contextTokens ?? existing?.ContextTokens,
                UsageContributionTokens = currentUsage,
            };
            return true;
        }

        return false;
    }

    private void OpenResetGateForLifecycleStartLocked(string threadId, AgentEventInfo evt)
    {
        _resetAwaitingUserMessage.Remove(threadId);
        _resetRemoteUserSeen.Remove(threadId);
        _resetLocalSendWithoutRunVersions.Remove(threadId);
        _resetLocalSendWithoutRunStartSequences.Remove(threadId);
        _resetLocalEchoSequences.Remove(threadId);
        _resetPendingLifecycleStarts.Remove(threadId);

        if (!string.IsNullOrEmpty(evt.RunId))
        {
            _activeRunIds[threadId] = evt.RunId;
            _activeRunStartSequences[threadId] = ++_lifecycleStartSequence;
            if (_resetAcceptedRunIds.TryGetValue(threadId, out var acceptedRunIds))
            {
                acceptedRunIds.Remove(evt.RunId);
                if (acceptedRunIds.Count == 0)
                    _resetAcceptedRunIds.Remove(threadId);
            }
        }
    }

    private bool IsResetIgnoredRunLocked(string threadId, string? runId, AgentEventInfo evt, out bool reloadHistoryAfterDrop)
    {
        reloadHistoryAfterDrop = false;
        if (string.IsNullOrEmpty(runId) ||
            !_resetIgnoredRunIds.TryGetValue(threadId, out var runIds) ||
            !runIds.Contains(runId))
        {
            return false;
        }

        if (IsTerminalRunEvent(evt))
        {
            runIds.Remove(runId);
            if (runIds.Count == 0)
            {
                _resetIgnoredRunIds.Remove(threadId);
                _resetSubmittedLocalEchoTexts.Remove(threadId);
            }
            reloadHistoryAfterDrop = true;
        }

        return true;
    }

    private bool IsPreResetTimestampLocked(string threadId, long eventTsMs, long resetCutoffUtcMs)
    {
        if (eventTsMs <= 0 || resetCutoffUtcMs <= 0)
            return false;

        return _resetVersions.ContainsKey(threadId) &&
            eventTsMs + ResetTimestampToleranceMs <= resetCutoffUtcMs;
    }

    private static bool IsLifecycleStart(AgentEventInfo evt) =>
        string.Equals(evt.Stream, "lifecycle", StringComparison.OrdinalIgnoreCase) &&
        evt.Data.ValueKind == System.Text.Json.JsonValueKind.Object &&
        evt.Data.TryGetProperty("phase", out var phaseProp) &&
        string.Equals(phaseProp.GetString(), "start", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalRunEvent(AgentEventInfo evt)
    {
        if (string.Equals(evt.Stream, "lifecycle", StringComparison.OrdinalIgnoreCase) &&
            evt.Data.ValueKind == System.Text.Json.JsonValueKind.Object &&
            evt.Data.TryGetProperty("phase", out var phaseProp))
        {
            var phase = phaseProp.GetString();
            return string.Equals(phase, "end", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(phase, "error", StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(evt.Stream, "job", StringComparison.OrdinalIgnoreCase) &&
            evt.Data.ValueKind == System.Text.Json.JsonValueKind.Object &&
            evt.Data.TryGetProperty("state", out var stateProp))
        {
            var state = stateProp.GetString();
            return string.Equals(state, "done", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(state, "error", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static int? LatestAssistantUsageBeforeLocked(ChatTimelineState timeline, Dictionary<string, ChatEntryMetadata> threadMeta, int beforeIndex)
    {
        for (var i = beforeIndex - 1; i >= 0; i--)
        {
            var entry = timeline.Entries[i];
            if (entry.Kind != ChatTimelineItemKind.Assistant)
                continue;

            if (!threadMeta.TryGetValue(entry.Id, out var meta))
                continue;

            var usage = UsageValue(meta);
            if (usage is null)
                continue;

            return usage;
        }

        return null;
    }

    private static int? UsageValue(ChatEntryMetadata meta)
        => meta.ResponseTokens
           ?? (meta.InputTokens is int input && meta.OutputTokens is int output
               ? input + output
               : null);

    private bool SnapshotLatestAssistantUsageLocked(SessionInfo session, string? timelineKey = null)
    {
        if (string.IsNullOrEmpty(session.Key)) return false;

        var usedTokens = session.TotalTokens;
        if (usedTokens <= 0)
            usedTokens = session.InputTokens + session.OutputTokens;
        if (usedTokens <= 0) return false;

        timelineKey ??= session.Key;
        if (string.IsNullOrEmpty(timelineKey)) return false;
        if (!_timelines.TryGetValue(timelineKey, out var timeline)) return false;

        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            var entry = timeline.Entries[i];
            if (entry.Kind != ChatTimelineItemKind.Assistant) continue;

            var threadMeta = GetOrCreateThreadMetaLocked(timelineKey);
            threadMeta.TryGetValue(entry.Id, out var existing);
            var usageSnapshot = Math.Max(usedTokens, existing?.ResponseTokens ?? 0);
            var usageSnapshotTokens = ToIntIfPositive(usageSnapshot);
            var contextSnapshot = session.ContextTokens > 0 ? session.ContextTokens : existing?.ContextTokens;
            if (existing is not null
                && existing.ResponseTokens == usageSnapshotTokens
                && existing.ContextTokens == contextSnapshot)
                return false;

            threadMeta[entry.Id] = (existing ?? BuildLiveMetaLocked(timelineKey)) with
            {
                InputTokens = ToIntIfPositive(session.InputTokens),
                OutputTokens = ToIntIfPositive(session.OutputTokens),
                ResponseTokens = usageSnapshotTokens,
                ContextTokens = contextSnapshot,
                ContextPercent = existing?.ContextPercent,
                UsageContributionTokens = existing?.UsageContributionTokens
            };
            return true;
        }

        return false;
    }

    private SessionInfo? ResolveSessionForThreadLocked(string threadId)
    {
        var session = Array.Find(_sessions, s => string.Equals(s.Key, threadId, StringComparison.Ordinal));
        if (session is not null) return session;

        if (string.Equals(threadId, "main", StringComparison.Ordinal)
            && _bridge.MainSessionKey is { Length: > 0 } mainKey)
        {
            session = Array.Find(_sessions, s => string.Equals(s.Key, mainKey, StringComparison.Ordinal));
            if (session is not null) return session;
        }

        if (string.Equals(threadId, "main", StringComparison.Ordinal))
            return Array.Find(_sessions, s => s.IsMain);

        return null;
    }

    private string ResolveTimelineKeyForSessionLocked(SessionInfo session)
    {
        if (session.IsMain && _timelines.TryGetValue("main", out var mainTimeline)
            && mainTimeline.Entries.Count > 0)
        {
            return "main";
        }

        if (!string.IsNullOrEmpty(session.Key) && _timelines.ContainsKey(session.Key))
            return session.Key;

        if (session.IsMain && _timelines.ContainsKey("main"))
            return "main";

        return session.Key;
    }

    private static int? ToIntIfPositive(long value)
        => value > 0 && value <= int.MaxValue ? (int)value : null;

    private ChatEntryMetadata BuildLiveMetaLocked(
        string threadId,
        long? tsMs = null,
        string? gatewayMessageId = null,
        int? openClawSeq = null,
        bool isLocalQueuedSend = false,
        string? localQueuedMessageId = null)
    {
        var ts = tsMs is { } v && v > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(v).ToLocalTime()
            : (DateTimeOffset?)DateTimeOffset.Now;
        var session = Array.Find(_sessions, s => s.Key == threadId);
        return new ChatEntryMetadata(
            ts,
            session?.Model,
            GatewayMessageId: gatewayMessageId,
            OpenClawSeq: openClawSeq,
            IsLocalQueuedSend: isLocalQueuedSend,
            LocalQueuedMessageId: localQueuedMessageId);
    }

    private static List<ChatMessageInfo> OrderHistoryMessages(List<(ChatMessageInfo Message, int Index)> messages)
    {
        if (messages.Count == 0)
            return new List<ChatMessageInfo>();

        var sequencedCount = messages.Count(item => item.Message.OpenClawSeq is not null);
        if (sequencedCount == messages.Count)
        {
            return messages
                .OrderBy(item => item.Message.OpenClawSeq)
                .ThenBy(item => item.Index)
                .Select(item => item.Message)
                .ToList();
        }

        if (sequencedCount == 0)
        {
            return messages
                .OrderBy(item => item.Message.Ts)
                .ThenBy(item => item.Index)
                .Select(item => item.Message)
                .ToList();
        }

        // Mixed old/new rows are already in gateway transcript order. Sorting
        // timestamped-but-unsequenced rows against sequenced rows can drag a
        // later queued burst (e.g. "t") ahead of the actual transcript start.
        return messages
            .OrderBy(item => item.Index)
            .Select(item => item.Message)
            .ToList();
    }

    private static void IncrementCount(Dictionary<string, int> counts, string key)
        => counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;

    private static bool TryConsumeCount(Dictionary<string, int> counts, string key)
    {
        if (!counts.TryGetValue(key, out var count) || count <= 0)
            return false;

        if (count == 1)
            counts.Remove(key);
        else
            counts[key] = count - 1;
        return true;
    }

    private static void ConsumeAnyTimestamp(Dictionary<string, List<long>> timestamps, string key)
    {
        if (timestamps.TryGetValue(key, out var values) && values.Count > 0)
            values.RemoveAt(0);
    }

    private void SeedSessionIdsFromSessionsLocked(IEnumerable<SessionInfo> sessions)
    {
        foreach (var session in sessions)
        {
            if (!string.IsNullOrWhiteSpace(session.Key) &&
                !string.IsNullOrWhiteSpace(session.SessionId))
            {
                if (_resetClearedSessionIds.TryGetValue(session.Key, out var clearedSessionId) &&
                    string.Equals(clearedSessionId, session.SessionId, StringComparison.Ordinal))
                {
                    continue;
                }

                _sessionIds[session.Key] = session.SessionId!;
                _resetClearedSessionIds.Remove(session.Key);
            }
        }
    }

    private ChatTimelineState GetOrCreateTimelineLocked(string threadId)
    {
        if (!_timelines.TryGetValue(threadId, out var current))
        {
            // HistoryLoaded stays false until LoadHistoryAsync rebuilds
            // the timeline from the gateway. The UI relies on this flag
            // to distinguish "session exists, history still fetching"
            // (show reconnecting view) from "session truly empty"
            // (show welcome zero-state).
            current = ChatTimelineState.Initial();
            _timelines[threadId] = current;
        }
        return current;
    }

    private void EnsureTimelinesForSessionsLocked()
    {
        foreach (var s in _sessions)
        {
            if (string.IsNullOrEmpty(s.Key)) continue;
            if (!_timelines.ContainsKey(s.Key))
                _timelines[s.Key] = ChatTimelineState.Initial();
        }
    }

    private ChatDataSnapshot BuildSnapshotLocked()
    {
        // Build threads from the gateway's authoritative session list.
        // No synthesis based on local timeline keys — the UI's compose target
        // is exposed separately via ChatComposeTarget so the renderer can show
        // a usable composer even before the first session materializes server-
        // side (e.g. fresh install with zero sessions).
        var threadList = new List<ChatThread>(_sessions.Length + 1);
        var threadTitles = SessionTitleFormatter.FormatUnique(_sessions);
        for (int i = 0; i < _sessions.Length; i++)
            threadList.Add(ToThread(_sessions[i], threadTitles[i]));

        var composeKey = _bridge.MainSessionKey;
        var composeReady = _bridge.HasHandshakeSnapshot
            && !string.IsNullOrWhiteSpace(composeKey)
            && _status == ConnectionStatus.Connected
            // Wait until sessions.list has been delivered for this
            // connection — otherwise the UI may synthesize a compose-only
            // thread (and render the welcome zero-state) in the brief
            // window before a returning user's real sessions arrive.
            && _sessionsListReceived;

        // If the compose target hasn't materialized as a real session yet but
        // already has local pending chat state (because the user sent a message
        // before the gateway echoed back sessions.list), surface a synthetic
        // thread record so the UI can render the queued card/transcript without
        // falling back into the "no thread selected" zero state. The synthetic
        // thread's Id is the canonical compose key, so when SessionsUpdated
        // eventually arrives with the same key it replaces the synthetic in
        // place — no migration, no re-keying.
        if (composeReady
            && composeKey is { } ck
            && _timelines.TryGetValue(ck, out var pendingTl)
            && (pendingTl.Entries.Count > 0
                || pendingTl.TurnActive
                || (_queuedMessages.TryGetValue(ck, out var pendingQueue) && pendingQueue.Count > 0))
            && !_sessions.Any(s => string.Equals(s.Key, ck, StringComparison.Ordinal)))
        {
            threadList.Add(new ChatThread
            {
                Id = ck,
                Title = _lastChatState?.ThreadTitle ?? "OpenClaw Windows Tray",
                Model = _lastChatState?.Model,
                ModelProvider = _lastChatState?.ModelProvider,
                Status = ChatThreadStatus.Running,
                Activity = ChatActivity.Idle,
            });
        }

        foreach (var approval in _localInlineApprovals.Values)
        {
            if (threadList.Any(s => string.Equals(s.Id, approval.ThreadId, StringComparison.Ordinal)))
                continue;

            threadList.Add(new ChatThread
            {
                Id = approval.ThreadId,
                Title = _lastChatState?.ThreadTitle ?? "OpenClaw Windows Tray",
                Status = ChatThreadStatus.Running,
                Activity = ChatActivity.AwaitingPermission,
                Model = _lastChatState?.Model,
                ModelProvider = _lastChatState?.ModelProvider,
            });
        }

        var threads = threadList.ToArray();

        // Snapshot a defensive copy of the timeline dict.
        var timelinesCopy = new Dictionary<string, ChatTimelineState>(_timelines);
        var timelineGenerationsCopy = new Dictionary<string, long>(_resetVersions);
        var queuedMessagesCopy = _queuedMessages.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<ChatQueuedMessage>)kvp.Value.ToArray());

        var defaultThreadId = ResolveDefaultThreadIdLocked();

        // When the gateway is connected and the handshake completed but no
        // session key was advertised, distinguish this from a normal "Connected"
        // state so the UI can surface a clear compatibility warning.
        var connectionLabel = (_status == ConnectionStatus.Connected
                               && _bridge.HasHandshakeSnapshot
                               && string.IsNullOrWhiteSpace(composeKey))
            ? "Incompatible gateway"
            : _status switch
            {
                ConnectionStatus.Connected => "Connected",
                ConnectionStatus.Connecting => "Connecting…",
                ConnectionStatus.Disconnected => "Disconnected",
                ConnectionStatus.Error => "Disconnected — error",
                _ => _status.ToString()
            };

        var composeTarget = composeReady
            ? new ChatComposeTarget(composeKey, true)
            : ChatComposeTarget.NotReady;

        return new ChatDataSnapshot(
            Threads: threads,
            Timelines: timelinesCopy,
            DefaultThreadId: defaultThreadId,
            ConnectionStatus: connectionLabel,
            AvailableModels: _availableModels,
            ComposeTarget: composeTarget,
            ModelChoices: _modelChoices,
            // Null until the first commands.list fetch completes so the UI can
            // distinguish "loading" from "loaded but empty". IsSupported=false
            // surfaces the unsupported state.
            AvailableCommands: _commandCatalog?.Commands,
            CommandsSupported: _commandCatalog?.IsSupported ?? true,
            TimelineGenerations: timelineGenerationsCopy,
            QueuedMessagesByThread: queuedMessagesCopy);
    }

    private string? ResolveDefaultThreadIdLocked()
    {
        if (_lastChatState?.DefaultThreadId is { Length: > 0 } rememberedThreadId)
        {
            if (TryGetSessionLocked(rememberedThreadId, out _) || !_sessionsListReceived)
                return rememberedThreadId;
        }

        // Prefer the gateway's canonical main session (IsMain on SessionInfo)
        // so we never have to guess from a literal like "main". Only fall back
        // to the compose target (pre-materialization) or the first available
        // session when no main is present.
        for (int i = 0; i < _sessions.Length; i++)
        {
            var s = _sessions[i];
            if (s.IsMain && !string.IsNullOrEmpty(s.Key))
                return s.Key;
        }
        if (_bridge.HasHandshakeSnapshot
            && _bridge.MainSessionKey is { } mk
            && !string.IsNullOrWhiteSpace(mk))
            return mk;
        if (_sessions.Length > 0 && !string.IsNullOrEmpty(_sessions[0].Key))
            return _sessions[0].Key;
        return null;
    }

    private void RememberLastSessionStateLocked()
    {
        if (_sessions.Length == 0) return;
        var defaultThreadId = ResolveDefaultThreadIdLocked();
        var session = defaultThreadId is { Length: > 0 } && TryGetSessionLocked(defaultThreadId, out var selected)
            ? selected
            : _sessions.FirstOrDefault(s => s.IsMain && !string.IsNullOrEmpty(s.Key))
                ?? _sessions.FirstOrDefault(s => !string.IsNullOrEmpty(s.Key));
        if (session is null) return;

        _lastChatState = new LastChatState
        {
            DefaultThreadId = session.Key,
            ThreadTitle = SessionTitleFormatter.Format(session, _sessions),
            Model = session.Model,
            ModelProvider = session.Provider,
            AvailableModels = _availableModels,
        };
    }

    private bool TryGetSessionLocked(string threadId, out SessionInfo session)
    {
        for (int i = 0; i < _sessions.Length; i++)
        {
            var candidate = _sessions[i];
            if (string.Equals(candidate.Key, threadId, StringComparison.Ordinal))
            {
                session = candidate;
                return true;
            }
        }

        session = default!;
        return false;
    }

    private static ChatThread ToThread(SessionInfo s, string title)
    {
        return new ChatThread
        {
            Id = s.Key ?? string.Empty,
            Title = title,
            Status = SessionVisibilityFilter.ToChatThreadStatus(s),
            Activity = string.IsNullOrEmpty(s.CurrentActivity) ? ChatActivity.Idle : ChatActivity.Working,
            Workspace = s.Channel,
            Model = s.Model,
            ModelProvider = s.Provider,
            ThinkingLevel = s.ThinkingLevel,
            InputTokens = s.InputTokens,
            OutputTokens = s.OutputTokens,
            TotalTokens = s.TotalTokens,
            ContextTokens = s.ContextTokens,
            CreatedAt = s.StartedAt is { } st ? ToOffset(st) : null,
            UpdatedAt = s.UpdatedAt is { } ut ? ToOffset(ut) : null,
        };
    }

    private static DateTimeOffset ToOffset(DateTime dt)
    {
        // SessionInfo.StartedAt/UpdatedAt arrive as DateTimeKind.Local or
        // Unspecified depending on the parser path; new DateTimeOffset(local, Zero)
        // throws because the offset must match the kind. Treat Unspecified as
        // UTC (matches the gateway's wire format), and let the DateTimeOffset(dt)
        // single-arg ctor handle Local/Utc using the value's actual offset.
        if (dt.Kind == DateTimeKind.Unspecified)
            return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero);
        return new DateTimeOffset(dt);
    }

    // ── Dispatcher marshaling ──

    private void Publish(ChatDataSnapshot snapshot)
    {
        var args = new ChatDataChangedEventArgs(snapshot);
        if (_post is null)
        {
            Changed?.Invoke(this, args);
        }
        else
        {
            _post(() => Changed?.Invoke(this, args));
        }

        // Debounce-save last-known UI state so the next launch can show
        // meaningful labels while reconnecting instead of "Main session"/"model".
        if (snapshot.Threads.Length > 0 || snapshot.AvailableModels.Length > 0)
            DebounceSaveLastChatState(snapshot);
    }

    // ── Last-chat-state cache ──────────────────────────────────────────
    // Persists the last-known thread title, model, and available models so
    // the UI can show them while reconnecting instead of generic placeholders.

    private static readonly string LastChatStateFilePath = Path.Combine(
        AppIdentity.ResolveLocalDataDirectory(), "last-chat-state.json");

    private System.Threading.Timer? _lastChatStateSaveTimer;
    private long _lastChatStateSaveVersion;

    internal sealed class LastChatState
    {
        public string? DefaultThreadId { get; set; }
        public string? ThreadTitle { get; set; }
        public string? Model { get; set; }
        public string? ModelProvider { get; set; }
        public string[]? AvailableModels { get; set; }
    }

    private LastChatState? _lastChatState;

    internal static LastChatState? LoadLastChatState(string? pathOverride = null)
    {
        var path = pathOverride ?? LastChatStateFilePath;
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<LastChatState>(json);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to load last chat state from '{path}': {ex.Message}");
            return null;
        }
    }

    private void DebounceSaveLastChatState(ChatDataSnapshot snapshot)
    {
        // Find the default thread to capture its title/model
        var defaultThread = snapshot.DefaultThreadId is { } dtId
            ? Array.Find(snapshot.Threads, t => t.Id == dtId)
            : snapshot.Threads.Length > 0 ? snapshot.Threads[0] : null;

        if (defaultThread is null && snapshot.AvailableModels.Length == 0) return;
        var previous = _lastChatState;

        var state = new LastChatState
        {
            DefaultThreadId = snapshot.DefaultThreadId ?? previous?.DefaultThreadId,
            ThreadTitle = defaultThread?.Title ?? previous?.ThreadTitle,
            Model = defaultThread?.Model ?? previous?.Model,
            ModelProvider = defaultThread?.ModelProvider ?? previous?.ModelProvider,
            AvailableModels = snapshot.AvailableModels,
        };

        lock (_gate)
        {
            _lastChatState = state;
            _lastChatStateSaveVersion++;
            var saveVersion = _lastChatStateSaveVersion;
            _lastChatStateSaveTimer?.Dispose();
            var path = _lastChatStateFilePath;
            _lastChatStateSaveTimer = new System.Threading.Timer(_ => SaveLastChatStateIfCurrent(state, path, saveVersion), null, _lastChatStateSaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void SaveLastChatStateIfCurrent(LastChatState state, string path, long saveVersion)
    {
        lock (_gate)
        {
            if (saveVersion != _lastChatStateSaveVersion)
                return;

            SaveLastChatState(state, path);
            _lastChatStateSaveTimer?.Dispose();
            _lastChatStateSaveTimer = null;
        }
    }

    private static void SaveLastChatState(LastChatState state, string? pathOverride = null)
    {
        var path = pathOverride ?? LastChatStateFilePath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = System.Text.Json.JsonSerializer.Serialize(state);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { Logger.Debug($"ChatDataProvider: persist LastChatState failed: {ex.Message}"); }
    }

    private void RaiseNotification(ChatProviderNotification notification)
    {
        var args = new ChatProviderNotificationEventArgs(notification);
        if (_post is null)
        {
            NotificationRequested?.Invoke(this, args);
            return;
        }
        _post(() => NotificationRequested?.Invoke(this, args));
    }

    // ── Abort persistence ──────────────────────────────────────────────

    private static readonly string AbortedIdsFilePath = Path.Combine(
        AppIdentity.ResolveLocalDataDirectory(), "aborted-messages.json");

    private static Dictionary<string, HashSet<string>> LoadAbortedIds()
    {
        try
        {
            if (!File.Exists(AbortedIdsFilePath))
                return new();
            var json = File.ReadAllText(AbortedIdsFilePath);
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
            if (dict is null) return new();
            var result = new Dictionary<string, HashSet<string>>();
            foreach (var (k, v) in dict)
                result[k] = new HashSet<string>(v);
            return result;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Aborted message IDs could not be loaded: {ex.Message}");
            return new();
        }
    }

    private void SaveAbortedIds()
    {
        try
        {
            Dictionary<string, HashSet<string>> snapshot;
            lock (_gate) snapshot = new Dictionary<string, HashSet<string>>(_persistedAbortedIds);

            var dir = Path.GetDirectoryName(AbortedIdsFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Convert HashSet to List for JSON serialization
            var serializable = new Dictionary<string, List<string>>();
            foreach (var (k, v) in snapshot)
                serializable[k] = new List<string>(v);

            var json = System.Text.Json.JsonSerializer.Serialize(serializable,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AbortedIdsFilePath, json);
        }
        catch (Exception ex) { Logger.Debug($"ChatDataProvider: persist aborted IDs failed: {ex.Message}"); }
    }

    // ── Tool metadata persistence ─────────────────────────────────────

    /// <summary>Cached tool call metadata entry persisted to disk.</summary>
    internal sealed class CachedToolMeta
    {
        public long Ts { get; set; }
        public string ToolName { get; set; } = "";
        public string Label { get; set; } = "";
    }

    /// <summary>Attachment display metadata persisted without attachment bytes.</summary>
    internal sealed class CachedAttachmentMeta
    {
        public long Ts { get; set; }
        public string Text { get; set; } = "";
        public List<CachedAttachmentItem> Attachments { get; set; } = new();
    }

    internal sealed class CachedAttachmentItem
    {
        public string FileName { get; set; } = "";
        public bool IsImage { get; set; }
    }

    private static string DefaultToolMetaCacheFilePath
    {
        get
        {
            return Path.Combine(AppIdentity.ResolveLocalDataDirectory(), "tool-metadata.json");
        }
    }

    private static string DefaultAttachmentMetaCacheFilePath(string toolMetaCacheFilePath)
    {
        var dir = Path.GetDirectoryName(toolMetaCacheFilePath);
        return Path.Combine(
            string.IsNullOrEmpty(dir)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : dir,
            "attachment-metadata.json");
    }

    /// <summary>Max sessions to keep in the tool metadata cache.</summary>
    internal const int MaxCachedSessions = 20;

    /// <summary>Max tool entries per session in the cache.</summary>
    internal const int MaxToolEntriesPerSession = 500;

    /// <summary>Max attachment-bearing user messages per session in the cache.</summary>
    internal const int MaxAttachmentEntriesPerSession = 500;

    private static Dictionary<string, List<CachedToolMeta>> LoadToolMetaCache(string cacheFilePath)
    {
        try
        {
            if (!File.Exists(cacheFilePath))
                return new();
            var json = File.ReadAllText(cacheFilePath);
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<CachedToolMeta>>>(json);
            if (dict is not null)
            {
                foreach (var entry in dict.Values.SelectMany(entries => entries))
                {
                    entry.ToolName = NormalizeCachedDisplayText(entry.ToolName);
                    entry.Label = NormalizeCachedDisplayText(entry.Label);
                }
            }
            return dict ?? new();
        }
        catch (Exception ex)
        {
            Logger.Debug($"Tool metadata cache could not be loaded: {ex.Message}");
            return new();
        }
    }

    private static Dictionary<string, List<CachedAttachmentMeta>> LoadAttachmentMetaCache(string cacheFilePath)
    {
        try
        {
            if (!File.Exists(cacheFilePath))
                return new();
            var json = File.ReadAllText(cacheFilePath);
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<CachedAttachmentMeta>>>(json);
            if (dict is not null)
            {
                foreach (var entry in dict.Values.SelectMany(entries => entries))
                {
                    entry.Text = NormalizeCachedDisplayText(entry.Text);
                    foreach (var attachment in entry.Attachments)
                        attachment.FileName = NormalizeCachedDisplayText(attachment.FileName);
                }
            }
            return dict ?? new();
        }
        catch (Exception ex)
        {
            Logger.Debug($"Attachment metadata cache could not be loaded: {ex.Message}");
            return new();
        }
    }

    private void SaveAttachmentMetaCache()
    {
        try
        {
            Dictionary<string, List<CachedAttachmentMeta>> snapshot;
            lock (_gate)
            {
                snapshot = _attachmentMetaCache.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Select(e => new CachedAttachmentMeta
                    {
                        Ts = e.Ts,
                        Text = NormalizeCachedDisplayText(e.Text),
                        Attachments = e.Attachments.Select(a => new CachedAttachmentItem
                        {
                            FileName = NormalizeCachedDisplayText(a.FileName),
                            IsImage = a.IsImage
                        }).ToList()
                    }).ToList(),
                    StringComparer.Ordinal);
            }

            if (snapshot.Count > MaxCachedSessions)
            {
                var toRemove = snapshot
                    .OrderBy(kv => kv.Value.Count > 0 ? kv.Value[^1].Ts : 0)
                    .Take(snapshot.Count - MaxCachedSessions)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var k in toRemove) snapshot.Remove(k);
            }

            var json = System.Text.Json.JsonSerializer.Serialize(snapshot, CacheJsonOptions);

            lock (_attachmentMetaSaveGate)
            {
                var dir = Path.GetDirectoryName(_attachmentMetaCacheFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var tempPath = _attachmentMetaCacheFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, _attachmentMetaCacheFilePath, overwrite: true);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath))
                            File.Delete(tempPath);
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"Attachment metadata temp file cleanup failed: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Attachment metadata cache could not be saved: {ex.Message}");
        }
    }

    private void CacheAttachmentMeta(
        string? sessionId,
        string threadId,
        string text,
        IReadOnlyList<ChatAttachment> attachments,
        long tsMs,
        long? expectedResetVersion = null)
    {
        if (attachments.Count == 0)
            return;

        var items = attachments
            .Where(a => !string.IsNullOrWhiteSpace(a.FileName))
            .Select(a => new CachedAttachmentItem
            {
                FileName = NormalizeCachedDisplayText(a.FileName),
                IsImage = string.Equals(a.Type, "image", StringComparison.OrdinalIgnoreCase)
            })
            .ToList();
        if (items.Count == 0)
            return;

        lock (_gate)
        {
            if (_disposed)
                return;

            if (expectedResetVersion is { } version &&
                GetResetVersionLocked(threadId) != version)
            {
                return;
            }

            var key = !string.IsNullOrEmpty(sessionId) ? sessionId! : threadId;
            if (!_attachmentMetaCache.TryGetValue(key, out var list))
            {
                list = new List<CachedAttachmentMeta>();
                _attachmentMetaCache[key] = list;
            }

            list.Add(new CachedAttachmentMeta
            {
                Ts = tsMs,
                Text = NormalizeCachedDisplayText(TruncateForChatEntry(EscapeUntrustedAttachmentMarkerLines(text))),
                Attachments = items
            });

            if (list.Count > MaxAttachmentEntriesPerSession)
                list.RemoveRange(0, list.Count - MaxAttachmentEntriesPerSession);
        }

        SaveAttachmentMetaCache();
    }

    private AttachmentMetaMatcher CreateAttachmentMetaMatcher(string? sessionId, string threadId)
    {
        var entries = new List<CachedAttachmentMeta>();
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(sessionId) &&
                _attachmentMetaCache.TryGetValue(sessionId!, out var sessionEntries))
                entries.AddRange(CloneAttachmentMeta(sessionEntries));

            if (!string.IsNullOrEmpty(threadId) &&
                (string.IsNullOrEmpty(sessionId) || !string.Equals(sessionId, threadId, StringComparison.Ordinal)) &&
                _attachmentMetaCache.TryGetValue(threadId, out var threadEntries))
                entries.AddRange(CloneAttachmentMeta(threadEntries));
        }

        return new AttachmentMetaMatcher(entries.OrderBy(e => e.Ts).ToList());
    }

    private static List<CachedAttachmentMeta> CloneAttachmentMeta(List<CachedAttachmentMeta> entries) =>
        entries.Select(e => new CachedAttachmentMeta
        {
            Ts = e.Ts,
            Text = NormalizeCachedDisplayText(e.Text),
            Attachments = e.Attachments.Select(a => new CachedAttachmentItem
            {
                FileName = NormalizeCachedDisplayText(a.FileName),
                IsImage = a.IsImage
            }).ToList()
        }).ToList();

    private sealed class AttachmentMetaMatcher
    {
        private static readonly TimeSpan MatchWindow = TimeSpan.FromHours(24);
        private readonly List<CachedAttachmentMeta> _entries;
        private readonly bool[] _used;

        public AttachmentMetaMatcher(List<CachedAttachmentMeta> entries)
        {
            _entries = entries;
            _used = new bool[entries.Count];
        }

        public CachedAttachmentMeta? TryMatch(string text, long historyTsMs)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_used[i])
                    continue;

                var entry = _entries[i];
                if (!string.Equals(entry.Text, text, StringComparison.Ordinal))
                    continue;

                if (historyTsMs > 0 && entry.Ts > 0 &&
                    Math.Abs(historyTsMs - entry.Ts) > MatchWindow.TotalMilliseconds)
                    continue;

                _used[i] = true;
                return entry;
            }

            return null;
        }
    }

    private static string RehydrateAttachmentMarkers(AttachmentMetaMatcher matcher, string text, long historyTsMs)
    {
        var match = matcher.TryMatch(text, historyTsMs);
        if (match is null || match.Attachments.Count == 0)
            return text;

        var markerLines = BuildAttachmentMarkerLines(match.Attachments);
        return string.IsNullOrEmpty(text)
            ? markerLines
            : $"{text}\n{markerLines}";
    }

    private static string BuildAttachmentMarkerLines(IEnumerable<ChatAttachment> attachments) =>
        string.Join("\n", attachments.Select(a =>
            string.Equals(a.Type, "image", StringComparison.OrdinalIgnoreCase)
                ? $"\u200B🖼️ {a.FileName}"
                : $"\u200B📎 {a.FileName}"));

    private static string BuildAttachmentMarkerLines(IEnumerable<CachedAttachmentItem> attachments) =>
        string.Join("\n", attachments.Select(a =>
            a.IsImage
                ? $"\u200B🖼️ {a.FileName}"
                : $"\u200B📎 {a.FileName}"));

    internal static string EscapeUntrustedAttachmentMarkerLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        var lines = text.Split('\n');
        var changed = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmedStart = line.TrimStart();
            if (trimmedStart.StartsWith("\u200B🖼️ ", StringComparison.Ordinal) ||
                trimmedStart.StartsWith("\u200B📎 ", StringComparison.Ordinal))
            {
                var prefixLength = line.Length - trimmedStart.Length;
                lines[i] = string.Concat(line.AsSpan(0, prefixLength), trimmedStart.AsSpan(1));
                changed = true;
            }
        }

        return changed ? string.Join('\n', lines) : text;
    }

    private void SaveToolMetaCache(long? expectedVersion = null)
    {
        try
        {
            Dictionary<string, List<CachedToolMeta>> snapshot;
            lock (_gate)
            {
                if (expectedVersion is long version && (version != _toolMetaSaveVersion || _disposed))
                    return;
                if (!_toolMetaCacheDirty)
                    return;

                snapshot = _toolMetaCache.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Select(e => new CachedToolMeta
                    {
                        Ts = e.Ts,
                        ToolName = NormalizeCachedDisplayText(e.ToolName),
                        Label = NormalizeCachedDisplayText(e.Label)
                    }).ToList(),
                    StringComparer.Ordinal);
            }

            // Evict oldest sessions if over the cap
            if (snapshot.Count > MaxCachedSessions)
            {
                var toRemove = snapshot
                    .OrderBy(kv => kv.Value.Count > 0 ? kv.Value[^1].Ts : 0)
                    .Take(snapshot.Count - MaxCachedSessions)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var k in toRemove) snapshot.Remove(k);
            }

            var json = System.Text.Json.JsonSerializer.Serialize(snapshot, CacheJsonOptions);

            lock (_toolMetaSaveGate)
            {
                if (expectedVersion is long version)
                {
                    lock (_gate)
                    {
                        if (version != _toolMetaSaveVersion || _disposed)
                            return;
                    }
                }

                var dir = Path.GetDirectoryName(_toolMetaCacheFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // Write to a unique temp file then atomic move to avoid partial JSON on crash.
                var tempPath = _toolMetaCacheFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, _toolMetaCacheFilePath, overwrite: true);
                    MarkToolMetaCacheSaved(expectedVersion);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath))
                            File.Delete(tempPath);
                    }
                    catch (Exception ex)
                    {
                        // Best-effort cleanup; persistence remains best-effort.
                        Logger.Debug($"ChatDataProvider: temp tool-meta file delete failed: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex) { Logger.Debug($"ChatDataProvider: persist tool meta cache failed: {ex.Message}"); }
    }

    /// <summary>
    /// Cache a tool call's metadata so it can be recovered when the gateway
    /// flattens it during history replay on a future app launch.
    /// </summary>
    internal void CacheToolMeta(string threadId, long tsMs, string toolName, string label)
    {
        System.Threading.Timer? timerToDispose = null;
        long saveVersion;
        lock (_gate)
        {
            if (_disposed)
                return;

            var key = _sessionIds.TryGetValue(threadId, out var sessionId) && !string.IsNullOrEmpty(sessionId)
                ? sessionId
                : threadId;

            if (!_toolMetaCache.TryGetValue(key, out var list))
            {
                list = new List<CachedToolMeta>();
                _toolMetaCache[key] = list;
            }

            // Deduplicate by timestamp (same tool event shouldn't be cached twice)
            if (list.Count > 0 && list[^1].Ts == tsMs && list[^1].ToolName == toolName)
                return;

            list.Add(new CachedToolMeta
            {
                Ts = tsMs,
                ToolName = NormalizeCachedDisplayText(toolName),
                Label = NormalizeCachedDisplayText(label)
            });

            // Cap per-session entries
            if (list.Count > MaxToolEntriesPerSession)
                list.RemoveRange(0, list.Count - MaxToolEntriesPerSession);

            // Debounce save — reset the timer on each cache addition so we only
            // write once after 500ms of quiescence, avoiding concurrent file writes.
            _toolMetaCacheDirty = true;
            saveVersion = ++_toolMetaSaveVersion;
            timerToDispose = _toolMetaSaveTimer;
            _toolMetaSaveTimer = new System.Threading.Timer(_ => SaveToolMetaCache(saveVersion), null, 500, Timeout.Infinite);
        }
        timerToDispose?.Dispose();
    }

    /// <summary>
    /// Look up cached tool metadata for a session's history reconstruction.
    /// Returns a queue of entries sorted by timestamp for sequential consumption.
    /// </summary>
    private Queue<CachedToolMeta>? GetCachedToolMetaForSession(string? sessionId, string threadId)
    {
        if (string.IsNullOrEmpty(sessionId) && string.IsNullOrEmpty(threadId)) return null;
        lock (_gate)
        {
            var entries = new List<CachedToolMeta>();
            if (!string.IsNullOrEmpty(sessionId) &&
                _toolMetaCache.TryGetValue(sessionId!, out var sessionEntries))
            {
                entries.AddRange(sessionEntries);
            }

            if (!string.IsNullOrEmpty(threadId) &&
                (string.IsNullOrEmpty(sessionId) || !string.Equals(sessionId, threadId, StringComparison.Ordinal)) &&
                _toolMetaCache.TryGetValue(threadId, out var threadEntries))
            {
                entries.AddRange(threadEntries);
            }

            if (entries.Count > 0)
                return new Queue<CachedToolMeta>(entries.OrderBy(e => e.Ts));
        }
        return null;
    }

    /// <summary>
    /// Try to match a history tool entry to a cached metadata entry.
    /// Both the cache and history are chronologically ordered, so we consume
    /// entries sequentially. The cache stores tool-start timestamps while
    /// history stores tool-result timestamps (which can be minutes later),
    /// so we match by order rather than timestamp proximity.
    /// </summary>
    internal static CachedToolMeta? TryMatchCachedTool(Queue<CachedToolMeta>? cache, long historyTsMs)
    {
        if (cache is null || cache.Count == 0) return null;

        // Both sequences are chronological. Consume the next cached entry
        // for each tool result we encounter in history.
        // Guard: if the history timestamp is much OLDER than the next cached
        // entry, this toolresult predates our cache — skip it.
        var candidate = cache.Peek();
        if (historyTsMs > 0 && candidate.Ts > 0 && candidate.Ts > historyTsMs + 300_000)
            return null; // cached entry is >5 min after this history entry — not a match

        var match = cache.Dequeue();
        match.ToolName = NormalizeCachedDisplayText(match.ToolName);
        match.Label = NormalizeCachedDisplayText(match.Label);
        return match;
    }

    private static string NormalizeCachedDisplayText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }

    private void MarkToolMetaCacheSaved(long? savedVersion)
    {
        lock (_gate)
        {
            if (savedVersion is null || savedVersion == _toolMetaSaveVersion)
                _toolMetaCacheDirty = false;
        }
    }

    /// <summary>
    /// After a successful abort, reload chat.history to capture the __openclaw.id
    /// of the aborted user message and persist it for future sessions.
    /// </summary>
    private async Task PersistAbortedMessageIdAsync(string threadId)
    {
        long requestResetVersion;
        lock (_gate)
        {
            requestResetVersion = GetResetVersionLocked(threadId);
        }

        await _persistLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Delay(500).ConfigureAwait(false); // let gateway finalize
            var history = await _bridge.RequestChatHistoryAsync(threadId).ConfigureAwait(false);

            lock (_gate)
            {
                if (GetResetVersionLocked(threadId) != requestResetVersion)
                {
                    Logger.Info($"[ABORT-PERSIST] Ignoring stale abort persistence after reset for thread {threadId}");
                    return;
                }
            }

            var newAbortedIds = new List<string>();
            var msgs = history.Messages;

            // Scan for user messages with missing/truncated assistant responses
            for (int i = 0; i < msgs.Count; i++)
            {
                var msg = msgs[i];
                if (!string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (msg.OpenClawId is null) continue;
                if (IsMessageAborted(threadId, msg.OpenClawId)) continue;
                if (newAbortedIds.Contains(msg.OpenClawId)) continue;

                ChatMessageInfo? nextAssistant = null;
                for (int j = i + 1; j < msgs.Count; j++)
                {
                    var candidate = msgs[j];
                    var role = candidate.Role?.ToLowerInvariant();
                    if (role == "assistant") { nextAssistant = candidate; break; }
                    if (role == "user") break;
                }

                if (nextAssistant is null)
                {
                    newAbortedIds.Add(msg.OpenClawId);
                }
                else if (!string.IsNullOrEmpty(nextAssistant.StopReason) &&
                         !string.Equals(nextAssistant.StopReason, "stop", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(nextAssistant.StopReason, "end_turn", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(nextAssistant.StopReason, "toolUse", StringComparison.OrdinalIgnoreCase))
                {
                    newAbortedIds.Add(msg.OpenClawId);
                }
            }

            if (newAbortedIds.Count == 0)
            {
                Logger.Debug($"[ABORT-PERSIST] No new aborted message IDs found for thread {threadId}");
                return;
            }

            lock (_gate)
            {
                if (GetResetVersionLocked(threadId) != requestResetVersion)
                {
                    Logger.Info($"[ABORT-PERSIST] Ignoring stale abort persistence write after reset for thread {threadId}");
                    return;
                }

                if (!_persistedAbortedIds.TryGetValue(threadId, out var set))
                {
                    set = new HashSet<string>();
                    _persistedAbortedIds[threadId] = set;
                }
                foreach (var id in newAbortedIds)
                    set.Add(id);
            }

            SaveAbortedIds();
            Logger.Info($"[ABORT-PERSIST] Persisted {newAbortedIds.Count} aborted IDs for thread {threadId}: {string.Join(", ", newAbortedIds)}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ABORT-PERSIST] Failed to persist abort for thread {threadId}: {ex.Message}");
        }
        finally
        {
            _persistLock.Release();
        }
    }

    /// <summary>Check if a user message's __openclaw.id is in the persisted aborted set.</summary>
    private bool IsMessageAborted(string threadId, string? openClawId)
    {
        if (openClawId is null) return false;
        lock (_gate)
        {
            var found = _persistedAbortedIds.TryGetValue(threadId, out var set);
            var contains = found && set!.Contains(openClawId);
            Logger.Debug($"[IsMessageAborted] thread='{threadId}' id='{openClawId}' dictHasThread={found} setCount={set?.Count ?? 0} match={contains}");
            return contains;
        }
    }
}
