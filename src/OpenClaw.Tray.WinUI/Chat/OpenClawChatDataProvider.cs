using System.Collections.Concurrent;
using System.IO;
using System.Linq;
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
    private readonly Action<Action>? _post;
    private readonly object _gate = new();
    private readonly object _toolMetaSaveGate = new();
    private readonly string _toolMetaCacheFilePath;
    private System.Threading.Timer? _toolMetaSaveTimer; // debounce cache writes
    private long _toolMetaSaveVersion;
    private readonly Dictionary<string, ChatTimelineState> _timelines = new();
    private readonly Dictionary<string, string> _activeRunIds = new();   // sessionKey → runId
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
    // Per-session cache of tool metadata from live SSE events.
    // Keyed by gateway sessionId (immutable UUID).  Persisted to disk
    // so that history reconstruction on restart can recover tool names.
    private Dictionary<string, List<CachedToolMeta>> _toolMetaCache;
    // Track recently-sent local user message texts so we can suppress
    // SSE echoes while still displaying messages from other clients.
    private readonly Dictionary<string, Queue<LocalSentText>> _localSentTexts = new();
    private int _keylessEventDiagnosticRaised;
    // Threads where we locally initiated the current turn (via SendMessageAsync).
    // When lifecycle.start arrives for a thread NOT in this set, we know a remote
    // client started the turn and should fetch the user message from history.
    private readonly HashSet<string> _locallyInitiatedThreads = new();
    // Per-thread retry count for LoadHistoryAsync to prevent unbounded retry loops.
    private readonly Dictionary<string, int> _historyRetryCount = new();
    private const int MaxHistoryRetries = 3;
    private static readonly TimeSpan LocalEchoSuppressionWindow = TimeSpan.FromSeconds(30);
    private readonly record struct LocalSentText(string Text, DateTimeOffset SentAt);
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

    internal OpenClawChatDataProvider(IChatGatewayBridge bridge, Action<Action>? post, string toolMetaCacheFilePath)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _post = post;
        _toolMetaCacheFilePath = !string.IsNullOrWhiteSpace(toolMetaCacheFilePath)
            ? toolMetaCacheFilePath
            : throw new ArgumentException("Tool metadata cache path is required.", nameof(toolMetaCacheFilePath));
        _status = bridge.CurrentStatus;
        _persistedAbortedIds = LoadAbortedIds();
        _toolMetaCache = LoadToolMetaCache(_toolMetaCacheFilePath);
        _lastChatState = LoadLastChatState();

        // Seed models from whatever the bridge already knows about (a connect
        // that completed before the provider was constructed will have its
        // models.list snapshot cached on the bridge).
        if (bridge.GetCurrentModelsList() is { } seedModels)
            _availableModels = ExtractModelNames(seedModels);
        // Fall back to last-known models so the composer shows a real model
        // name while reconnecting instead of the generic "model" placeholder.
        else if (_lastChatState?.AvailableModels is { Length: > 0 } cached)
            _availableModels = cached;

        _bridge.StatusChanged += OnStatusChanged;
        _bridge.SessionsUpdated += OnSessionsUpdated;
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
            return Task.FromResult(BuildSnapshotLocked());
        }
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
                    catch { /* skip un-decodable bytes */ }
                }
            }
        }

        // Build the display text for the user bubble. When attachments are
        // present, append a structured indicator line so the bubble is never
        // blank even if the typed message was empty. Uses a unique prefix
        // ("\u200B📎 " / "\u200B🖼️ ") with a zero-width space to prevent
        // false positives from normal user text.
        var displayText = trimmed;
        if (hasAttachments)
        {
            var chips = string.Join("\n", attachments!.Select(a =>
                a.Type == "image"
                    ? $"\u200B🖼️ {a.FileName}"
                    : $"\u200B📎 {a.FileName}"));
            displayText = string.IsNullOrEmpty(trimmed)
                ? chips
                : $"{trimmed}\n{chips}";
        }

        // 1. Optimistically add the user message + flag turn active.
        ChatDataSnapshot snapshot;
        string? sessionId;
        lock (_gate)
        {
            var current = GetOrCreateTimelineLocked(threadId);
            var beforeNextId = current.NextId;
            _timelines[threadId] = ChatTimelineReducer.AddLocalUser(current, displayText, nonce);
            _sessionIds.TryGetValue(threadId, out sessionId);

            // Track this text so we can suppress the SSE echo.
            if (!_localSentTexts.TryGetValue(threadId, out var q))
            {
                q = new Queue<LocalSentText>();
                _localSentTexts[threadId] = q;
            }
            q.Enqueue(new LocalSentText(trimmed, DateTimeOffset.UtcNow));
            // Cap at 20 to avoid unbounded growth.
            while (q.Count > 20) q.Dequeue();

            // Clear abort suppression — the user is starting a new interaction.
            // Also clear pending abort counts: if the user sends a new message,
            // any queued aborts from before should not fire against the new turn.
            _abortedThreads.Remove(threadId);
            _pendingAbortCounts.Remove(threadId);
            _locallyInitiatedThreads.Add(threadId);

            // Capture metadata for the just-added user entry.
            var meta = BuildLiveMetaLocked(threadId);
            var threadMeta = GetOrCreateThreadMetaLocked(threadId);
            threadMeta[$"e{beforeNextId}"] = meta;

            snapshot = BuildSnapshotLocked();
        }
        Publish(snapshot);

        // 2. Send to gateway.
        try
        {
            await _bridge.SendChatMessageAsync(trimmed, threadId, sessionId, attachments);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                RemovePendingLocalEchoLocked(threadId, trimmed);
                _locallyInitiatedThreads.Remove(threadId);
            }

            // Surface as an error in the timeline + notification — keeps the
            // user message visible so they can edit/retry.
            ApplyEventAndPublish(threadId, new ChatErrorEvent($"Send failed: {ex.Message}"));
            ApplyEventAndPublish(threadId, new ChatTurnEndEvent());
            RaiseNotification(new ChatProviderNotification(
                ChatProviderNotificationKind.Error, threadId, LocalizationHelper.GetString("Chat_Notification_SendFailed"), ex.Message));
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
                }
                Logger.Warn($"[ABORT] chat.abort failed, cleared suppression: {ex.Message}");
                RaiseNotification(new ChatProviderNotification(
                    ChatProviderNotificationKind.Error, threadId, LocalizationHelper.GetString("Chat_Notification_AbortFailed"), ex.Message));
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

        lock (_gate)
        {
            if (!force && _historyLoaded.Contains(threadId)) return;
            if (!_historyInFlight.Add(threadId)) return; // another loader already in progress
        }

        try
        {
            var history = await _bridge.RequestChatHistoryAsync(threadId);

            ChatDataSnapshot snapshot;
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(history.SessionId))
                    _sessionIds[threadId] = history.SessionId!;

                // Rebuild timeline from history; preserve any in-flight turn
                // entries that arrived between the request and the response by
                // appending them after the historical entries.
                var prior = GetOrCreateTimelineLocked(threadId);
                var rebuilt = ChatTimelineState.Initial() with { HistoryLoaded = true };

                // Sort by timestamp ascending as a safety net — the gateway is
                // expected to return chronological order, but don't trust it.
                // Stable secondary sort preserves the original index for ties.
                var ordered = history.Messages
                    .Select((m, i) => (m, i))
                    .OrderBy(t => t.m.Ts)
                    .ThenBy(t => t.i)
                    .Select(t => t.m)
                    .ToList();

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
                var cachedTools = GetCachedToolMetaForSession(history.SessionId);
                if (cachedTools is not null)
                    Logger.Info($"[ChatHistory] Found {cachedTools.Count} cached tool metadata entries for session");

                bool nextAssistantIsAborted = false;

                foreach (var msg in ordered)
                {
                    if (string.IsNullOrEmpty(msg.Text)) continue;

                    var ts = msg.Ts > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(msg.Ts).ToLocalTime()
                        : (DateTimeOffset?)null;
                    var msgMeta = new ChatEntryMetadata(ts, modelAtLoad);

                    var roleLower = msg.Role?.ToLowerInvariant() ?? "";
                    // Cap per-message text up front so heuristics, logging,
                    // and the reducer all see the same bounded value
                    // (chat rubber-duck MEDIUM 4).
                    var text = TruncateForChatEntry(msg.Text);

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

                    // (kind|text) → list of unix-second timestamps for rebuilt
                    // entries that have a real timestamp. Only these can match.
                    var rebuiltContentTimestamps = new Dictionary<string, List<long>>(StringComparer.Ordinal);
                    foreach (var entry in rebuilt.Entries)
                    {
                        rebuiltMeta.TryGetValue(entry.Id, out var em);
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

                        // Rule 2: content+timestamp dedup only when BOTH sides
                        // have valid timestamps within 2 seconds. Otherwise
                        // (Rule 3) fall through and keep the entry — silent
                        // data loss is worse than visible duplicates.
                        if (priorTs is { } pts && pts != default &&
                            rebuiltContentTimestamps.TryGetValue(ContentKey(entry.Kind, entry.Text), out var rebuiltTimes))
                        {
                            var priorSec = pts.ToUnixTimeSeconds();
                            var matched = false;
                            foreach (var rebSec in rebuiltTimes)
                            {
                                if (Math.Abs(rebSec - priorSec) <= 2)
                                {
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
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    await LoadHistoryAsync(threadId, force: true);
                });
            }
        }
        finally
        {
            lock (_gate) { _historyInFlight.Remove(threadId); }
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
        await _bridge.PatchSessionModelAsync(threadId, model);
    }

    public async Task SetThinkingLevelAsync(string threadId, string thinkingLevel, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _bridge.PatchSessionThinkingLevelAsync(threadId, thinkingLevel);
    }

    public Task SetPermissionModeAsync(string threadId, bool allowAll, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task RespondToPermissionAsync(string threadId, string requestId, bool allow, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(threadId) || string.IsNullOrEmpty(requestId))
            return;

        // The gateway accepts the same slash-command format the dashboard
        // emits ("Reply with: `/approve <slug> allow-once`"). We use that
        // here instead of a bespoke RPC so we don't have to track gateway
        // protocol versions. The slash command is normal chat input — the
        // gateway echoes back the resolved approval as an ``exec.approval.resolved``
        // agent event, which clears the banner via OnAgentEventReceived.
        // We also clear optimistically so the banner doesn't linger if the
        // gateway is slow.
        var slashCommand = allow
            ? $"/approve {requestId} allow-once"
            : $"/deny {requestId}";

        Logger.Info($"[Approval] user response requestId={requestId} decision={(allow ? "allow-once" : "deny")} thread='{threadId}'");

        // Pre-register the slash command in _localSentTexts so the live
        // SSE echo path (OnChatMessageReceived) can recognize this as
        // OUR send and suppress the echo. Slash commands issued by other
        // clients on the same thread will NOT find a match and will be
        // rendered as a dim audit-trail status entry instead of dropped
        // silently.
        lock (_gate)
        {
            if (!_localSentTexts.TryGetValue(threadId, out var sq))
            {
                sq = new Queue<LocalSentText>();
                _localSentTexts[threadId] = sq;
            }
            sq.Enqueue(new LocalSentText(slashCommand, DateTimeOffset.UtcNow));
            while (sq.Count > 20) sq.Dequeue();
        }

        try
        {
            await _bridge.SendChatMessageAsync(slashCommand, threadId, sessionId: null);
        }
        catch (Exception ex)
        {
            // Send failed: leave the Allow/Deny banner up so the user can
            // retry. Clearing it on failure would silently swallow the
            // problem and leave the agent waiting on an approval that the
            // user has no way to re-issue.
            //
            // Also pull the pre-registered slash entry back out of the
            // echo-suppression queue on send failure.
            // If we leave it there, the head-only matcher in
            // ``TryConsumeLocalEchoLocked`` will block legitimate echoes
            // for subsequent user prose (duplicate bubbles), and a
            // successful retry from another client will get its echo
            // silently consumed as if we'd sent it ourselves — defeating
            // the round-3 audit-trail rendering. Mirrors the recovery
            // in ``SendUserMessageAsync``.
            lock (_gate) { RemovePendingLocalEchoLocked(threadId, slashCommand); }
            Logger.Warn($"[Approval] response send failed requestId={requestId}: {ex.Message} (banner preserved for retry)");
            return;
        }

        ClearPendingPermissionAndPublish(threadId, expectedRequestId: requestId,
            decision: allow ? ChatPermissionDecision.Allowed : ChatPermissionDecision.Denied);
    }

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
        lock (_gate)
        {
            timerToDispose = _toolMetaSaveTimer;
            _toolMetaSaveTimer = null;
            _toolMetaSaveVersion++;
            chatStateTimerToDispose = _lastChatStateSaveTimer;
            _lastChatStateSaveTimer = null;
        }
        timerToDispose?.Dispose();
        chatStateTimerToDispose?.Dispose();
        SaveToolMetaCache();
        _bridge.StatusChanged -= OnStatusChanged;
        _bridge.SessionsUpdated -= OnSessionsUpdated;
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
        string[] threadsToReload;
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

            // Reset the approval-dedupe LRU on every transition out of
            // Connected. IDs from a prior session must not block a fresh
            // approval with a colliding slug from the next connection.
            if (justDisconnected)
                ResetApprovalDedupe();

            // On (re)connect, reload any thread that either previously loaded
            // successfully or has a timeline but never completed loading.
            // The second case covers initial connect: the UI may have created
            // timeline entries while the WebSocket was still negotiating, and
            // the first LoadHistoryAsync attempt likely timed out or returned
            // empty. Clear _historyInFlight too in case a previous load is
            // still pending (the request ID is stale after reconnect).
            if (justReconnected)
            {
                var reload = new HashSet<string>(_historyLoaded);
                foreach (var key in _timelines.Keys)
                    reload.Add(key);
                threadsToReload = reload.Count > 0
                    ? reload.ToArray()
                    : Array.Empty<string>();
                _historyLoaded.Clear();
                _historyInFlight.Clear();
                _locallyInitiatedThreads.Clear();
                _localSentTexts.Clear();
                _historyRetryCount.Clear();
            }
            else
            {
                threadsToReload = Array.Empty<string>();
            }

            if (justDisconnected)
            {
                var list = new List<string>();
                foreach (var (key, tl) in _timelines)
                {
                    if (tl.TurnActive) list.Add(key);
                }
                threadsToInterrupt = list.ToArray();
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

        // Eagerly re-issue history loads off the lock so the UI sees fresh
        // transcripts without waiting for the user to re-select the thread.
        foreach (var threadId in threadsToReload)
        {
            _ = LoadHistoryAsync(threadId, force: true);
        }
    }

    private void OnSessionsUpdated(object? sender, SessionInfo[] sessions)
    {
        ChatDataSnapshot snapshot;
        string[] newThreadsToLoad;
        lock (_gate)
        {
            _sessions = sessions ?? Array.Empty<SessionInfo>();
            _sessionsListReceived = true;
            EnsureTimelinesForSessionsLocked();
            snapshot = BuildSnapshotLocked();

            // When sessions arrive while connected, eagerly load history
            // for any thread that hasn't been loaded yet. This covers the
            // initial connect scenario: Connected fires before sessions
            // arrive, so OnStatusChanged can't reload (no threads exist
            // yet). Once sessions come in, trigger the history fetch.
            if (_status == ConnectionStatus.Connected)
            {
                var toLoad = new List<string>();
                foreach (var key in _timelines.Keys)
                {
                    if (!_historyLoaded.Contains(key) && !_historyInFlight.Contains(key))
                        toLoad.Add(key);
                }
                newThreadsToLoad = toLoad.Count > 0 ? toLoad.ToArray() : Array.Empty<string>();
            }
            else
            {
                newThreadsToLoad = Array.Empty<string>();
            }
        }
        Publish(snapshot);

        foreach (var threadId in newThreadsToLoad)
        {
            _ = LoadHistoryAsync(threadId, force: false);
        }
    }

    private void OnModelsListUpdated(object? sender, ModelsListInfo info)
    {
        ChatDataSnapshot snapshot;
        lock (_gate)
        {
            _availableModels = ExtractModelNames(info);
            snapshot = BuildSnapshotLocked();
        }
        Logger.Info($"[ChatBridge] OnModelsListUpdated: count={_availableModels.Length}");
        Publish(snapshot);
    }

    private static string[] ExtractModelNames(ModelsListInfo info)
    {
        if (info?.Models is null || info.Models.Count == 0) return Array.Empty<string>();
        // Use model Id (wire format, e.g. "claude-opus-4.5") so the composer
        // can match against SessionInfo.Model (which is also the wire Id).
        // The ComboBox will show Ids directly; a future pass could introduce
        // a separate display-name array if prettier labels are desired.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>(info.Models.Count);
        foreach (var m in info.Models)
        {
            var id = m.Id;
            if (string.IsNullOrEmpty(id)) continue;
            if (seen.Add(id)) list.Add(id);
        }
        return list.ToArray();
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
        lock (_gate)
        {
            if (_abortedThreads.Contains(msgThreadId))
            {
                Logger.Debug($"[ABORT] Suppressed ChatMessage for threadId='{msgThreadId}' (role={message.Role})");
                return;
            }
        }

        var role = message.Role ?? "";
        var roleLower = role.ToLowerInvariant();

        // User messages from the SSE stream. System control notes are rendered
        // as dim status entries. Normal user messages: suppress echoes of
        // locally-sent messages (already displayed), show messages from other
        // clients (e.g. gateway web UI) so the conversation is coherent.
        if (roleLower == "user")
        {
            // Approval slash-commands ("/approve <slug> allow-once",
            // "/deny <slug>") are transport, not user prose. If WE sent
            // it (matched + consumed from _localSentTexts) suppress the
            // echo entirely — RespondToPermissionAsync already cleared
            // the banner. If it came from ANOTHER client subscribed to
            // this thread, render a dim audit-trail status so the user
            // can still see that an approval decision was made elsewhere
            // (preserves audit signal).
            var rawText = message.Text ?? string.Empty;
            if (LooksLikeApprovalSlashCommand(rawText))
            {
                var slashEcho = rawText.Trim();
                bool weSentIt = false;
                lock (_gate)
                {
                    if (_localSentTexts.TryGetValue(msgThreadId, out var sq) && sq.Count > 0
                        && TryConsumeLocalEchoLocked(msgThreadId, sq, slashEcho))
                    {
                        weSentIt = true;
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
            lock (_gate)
            {
                if (_localSentTexts.TryGetValue(msgThreadId, out var q) && q.Count > 0
                    && TryConsumeLocalEchoLocked(msgThreadId, q, echoText))
                {
                    isLocalEcho = true;
                }
            }
            if (isLocalEcho) return;

            // Not a local echo — show it as a user message from another client.
            if (!string.IsNullOrEmpty(message.Text))
            {
                ChatEntryMetadata? userMeta;
                lock (_gate) { userMeta = BuildLiveMetaLocked(msgThreadId, message.Ts); }
                ApplyEventAndPublish(msgThreadId,
                    new ChatUserMessageEvent(TruncateForChatEntry(message.Text)),
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
            lock (_gate) { trMeta = BuildLiveMetaLocked(trThread, message.Ts); }
            var capped = TruncateForChatEntry(message.Text);
            var kind = ClassifyFlattenedToolOutput(capped);
            var label = ExtractFlattenedToolSummary(capped);
            ApplyEventAndPublish(trThread, new ChatToolStartEvent(label, kind), trMeta);
            ApplyEventAndPublish(trThread, new ChatToolOutputEvent(capped), trMeta);
            return;
        }

        if (roleLower != "assistant")
            return;
        if (string.IsNullOrEmpty(message.Text))
            return;

        var threadId = message.SessionKey;
        ChatEntryMetadata? meta;
        lock (_gate)
        {
            meta = BuildLiveMetaLocked(threadId, message.Ts);
            // If the gateway included a usage block on this chat event,
            // attach it so the assistant footer pills (↑/↓/R/ctx%) can
            // render. Mostly arrives on state="final" frames.
            if (message.InputTokens is not null || message.OutputTokens is not null
                || message.ResponseTokens is not null || message.ContextPercent is not null)
            {
                meta = meta with
                {
                    InputTokens = message.InputTokens ?? meta.InputTokens,
                    OutputTokens = message.OutputTokens ?? meta.OutputTokens,
                    ResponseTokens = message.ResponseTokens ?? meta.ResponseTokens,
                    ContextPercent = message.ContextPercent ?? meta.ContextPercent
                };
            }
        }

        // Both `state: "delta"` and `state: "final"` carry the cumulative
        // assistant text (the gateway's EmbeddedBlockChunker emits completed
        // blocks, not token deltas — see spec §"Block Streaming"). Map both
        // to ChatMessageEvent so the reducer REPLACES the active assistant
        // entry's text. Final additionally ends the turn.
        ApplyEventAndPublish(
            threadId,
            new ChatMessageEvent(RepairContentBlockSeams(TruncateForChatEntry(message.Text)), ReconcilePrevious: true),
            meta);

        if (message.IsFinal)
        {
            ApplyEventAndPublish(threadId, new ChatTurnEndEvent());
            RaiseNotification(new ChatProviderNotification(
                ChatProviderNotificationKind.TurnComplete, threadId, LocalizationHelper.GetString("Chat_Notification_AssistantReplied")));
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

        // Always update run tracking first (state maintenance must not be skipped).
        UpdateActiveRunId(evt, threadId);

        // Fire deferred chat.abort and persist if pending aborts were queued.
        var deferredRunId = _deferredAbortRunId;
        var shouldPersist = _deferredAbortCount > 0;
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
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(evt.RunId) && _abortedRunIds.Contains(evt.RunId))
                return;
            if (_abortedThreads.Contains(threadId))
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
                        ClearPendingPermissionAndPublish(threadId, expectedRequestId: pendingId);
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
            return;
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

    private string? _deferredAbortRunId; // set inside lock when pending abort fires; read outside lock to send RPC
    private int _deferredAbortCount;     // how many user messages to force-persist as aborted

    private void UpdateActiveRunId(AgentEventInfo evt, string threadId)
    {
        _deferredAbortRunId = null;
        _deferredAbortCount = 0;

        if (string.Equals(evt.Stream, "lifecycle", StringComparison.OrdinalIgnoreCase) &&
            evt.Data.ValueKind == System.Text.Json.JsonValueKind.Object &&
            evt.Data.TryGetProperty("phase", out var phaseProp))
        {
            var phase = phaseProp.GetString()?.ToLowerInvariant();
            lock (_gate)
            {
                if (phase == "start" && !string.IsNullOrEmpty(evt.RunId))
                {
                    _activeRunIds[threadId] = evt.RunId;

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
                        _deferredAbortRunId = evt.RunId;
                        _deferredAbortCount = pendingCount;
                        Logger.Info($"[ABORT] Deferred abort fired — lifecycle.start arrived with runId='{evt.RunId}' for threadId='{threadId}' (pendingCount={pendingCount})");
                    }
                }
                else if (phase == "end" || phase == "error")
                {
                    // Clean up: remove aborted runId tracking on terminal events.
                    if (!string.IsNullOrEmpty(evt.RunId))
                        _abortedRunIds.Remove(evt.RunId);
                    _activeRunIds.Remove(threadId);

                    // Clear thread-level abort suppression on terminal lifecycle events.
                    // The turn is over — any remaining abort suppression is no longer needed.
                    _abortedThreads.Remove(threadId);
                    // Clear locally-initiated flag — this turn is done.
                    _locallyInitiatedThreads.Remove(threadId);

                    // Edge case: if we have pending aborts but never saw lifecycle.start
                    // (gateway responded so fast start+end were batched), fire the
                    // deferred abort now so the persist still runs.
                    if (_pendingAbortCounts.TryGetValue(threadId, out var lateCount) && lateCount > 0)
                    {
                        _pendingAbortCounts.Remove(threadId);
                        _deferredAbortRunId = evt.RunId; // may be null, that's ok — persist doesn't need it
                        _deferredAbortCount = lateCount;
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
                if ((state == "done" || state == "error") && !string.IsNullOrEmpty(evt.RunId))
                {
                    _abortedRunIds.Remove(evt.RunId);
                    _activeRunIds.Remove(threadId);
                }
            }
        }
    }

    private bool TryConsumeLocalEchoLocked(string threadId, Queue<LocalSentText> queue, string text)
    {
        var now = DateTimeOffset.UtcNow;
        while (queue.Count > 0 && now - queue.Peek().SentAt > LocalEchoSuppressionWindow)
            queue.Dequeue();

        if (queue.Count == 0)
        {
            _localSentTexts.Remove(threadId);
            return false;
        }

        if (!string.Equals(queue.Peek().Text, text, StringComparison.Ordinal))
            return false;

        queue.Dequeue();
        if (queue.Count == 0)
            _localSentTexts.Remove(threadId);
        return true;
    }

    private void RemovePendingLocalEchoLocked(string threadId, string text)
    {
        if (!_localSentTexts.TryGetValue(threadId, out var queue))
            return;

        var kept = new Queue<LocalSentText>(queue.Count);
        var removed = false;
        while (queue.Count > 0)
        {
            var pending = queue.Dequeue();
            if (!removed && string.Equals(pending.Text, text, StringComparison.Ordinal))
            {
                removed = true;
                continue;
            }
            kept.Enqueue(pending);
        }

        if (kept.Count == 0)
            _localSentTexts.Remove(threadId);
        else
            _localSentTexts[threadId] = kept;
    }

    /// <summary>
    /// Fetch the latest user message from history for a remotely-initiated turn.
    /// Called when lifecycle.start arrives for a thread we didn't locally initiate.
    /// </summary>
    private async Task FetchRemoteUserMessageAsync(string threadId)
    {
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

            // Check if we already have this user message as the last User entry
            // in the timeline (avoid duplicates on reconnect/reload).
            lock (_gate)
            {
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
            }

            ChatEntryMetadata? meta;
            lock (_gate) { meta = BuildLiveMetaLocked(threadId, lastUser.Ts); }
            ApplyEventAndPublish(threadId,
                new ChatUserMessageEvent(TruncateForChatEntry(lastUser.Text)),
                meta);
            Logger.Info($"[REMOTE] Injected remote user message for threadId='{threadId}' len={lastUser.Text.Length}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[REMOTE] Failed to fetch remote user message for threadId='{threadId}': {ex.Message}");
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
    private const int ApprovalSeenCap = 64;

    // Approval id-asymmetry tracking.
    // The gateway sometimes emits ``approvalSlug`` only on ``requested``
    // and the full ``approvalId`` only on terminal events (or vice versa).
    // We prefer slug on both sides for matching (see ``MapApprovalEvent``)
    // but record the alternate identifier here so a terminal event that
    // carries only the "other" id can still resolve back to the live
    // pending banner. Bounded by ApprovalSeenCap via the same trim loop.
    private readonly Dictionary<string, string> _approvalAltIds = new(System.StringComparer.Ordinal);

    private bool MarkApprovalSeen(string approvalId)
    {
        if (string.IsNullOrEmpty(approvalId)) return true; // can't dedupe — render
        lock (_approvalSeenLock)
        {
            if (!_approvalSeen.Add(approvalId)) return false;
            _approvalSeenOrder.AddLast(approvalId);
            while (_approvalSeenOrder.Count > ApprovalSeenCap)
            {
                var oldest = _approvalSeenOrder.First!.Value;
                _approvalSeenOrder.RemoveFirst();
                _approvalSeen.Remove(oldest);
                _approvalAltIds.Remove(oldest);
            }
            return true;
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

        if (!MarkApprovalSeen(requestId))
        {
            Logger.Info($"[Approval] suppressed duplicate requestId={requestId}");
            return null;
        }

        // Record the alternate id (the one we didn't pick as requestId) so
        // a later terminal event that carries ONLY the alternate can still
        // resolve back to this pending banner. See ``ApprovalIdMatches``.
        // Recorded AFTER MarkApprovalSeen so a duplicate-replay can't
        // overwrite the mapping after the dedup short-circuit.
        var altId = !string.IsNullOrEmpty(slug) ? approvalId : slug;
        if (!string.IsNullOrEmpty(altId) && !string.Equals(altId, requestId, System.StringComparison.Ordinal))
        {
            lock (_approvalSeenLock)
            {
                _approvalAltIds[requestId] = altId;
            }
        }

        // PermissionKind is the short tool/category label the composer shows;
        // ToolName is the contextual subtitle (host); Detail is the body
        // (command + optional message).
        var permissionKind = !string.IsNullOrEmpty(title) ? title : "Exec approval";
        var toolName = !string.IsNullOrEmpty(host) ? host : "node";

        var detail = command;
        if (!string.IsNullOrEmpty(message))
            detail = string.IsNullOrEmpty(detail) ? message : message + "\n\n" + detail;

        Logger.Info($"[Approval] emitting ChatPermissionRequestEvent requestId={requestId} kind='{permissionKind}' tool='{toolName}' detail.len={detail.Length}");
        return new ChatPermissionRequestEvent(requestId, permissionKind, toolName, detail);
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
                return new ChatReasoningDeltaEvent(delta);
        }

        var contentText = evt.Data.TryGetProperty("content", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String
            ? c.GetString()
            : (evt.Data.TryGetProperty("text", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String
                ? t.GetString()
                : null);
        if (!string.IsNullOrEmpty(contentText))
            return new ChatReasoningEvent(contentText!);

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
        if (!string.Equals(kind, "tool", StringComparison.OrdinalIgnoreCase))
            return null;

        var phase = evt.Data.TryGetProperty("phase", out var phaseProp) ? phaseProp.GetString() ?? "" : "";
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
    /// user's behalf (<c>/approve &lt;slug&gt; allow-once</c> or
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
        new(@"^/(?:approve\s+[A-Za-z0-9_-]{4,64}(?:\s+allow-once)?|deny\s+[A-Za-z0-9_-]{4,64})\s*$",
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
            text.IndexOfAny(new[] { '.', '!', '?', ':' }) < 0)
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
                sb.Append(RepairProseSegment(text.AsSpan(i).ToString()));
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

            snapshot = BuildSnapshotLocked();
        }
        Publish(snapshot);
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

    private ChatEntryMetadata BuildLiveMetaLocked(string threadId, long? tsMs = null)
    {
        var ts = tsMs is { } v && v > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(v).ToLocalTime()
            : (DateTimeOffset?)DateTimeOffset.Now;
        var session = Array.Find(_sessions, s => s.Key == threadId);
        return new ChatEntryMetadata(ts, session?.Model);
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
        for (int i = 0; i < _sessions.Length; i++)
            threadList.Add(ToThread(_sessions[i]));

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
        // already has an optimistic timeline (because the user sent a message
        // before the gateway echoed back sessions.list), surface a synthetic
        // thread record so the UI can render the optimistic bubble without
        // falling back into the "no thread selected" zero state. The synthetic
        // thread's Id is the canonical compose key, so when SessionsUpdated
        // eventually arrives with the same key it replaces the synthetic in
        // place — no migration, no re-keying.
        if (composeReady
            && composeKey is { } ck
            && _timelines.TryGetValue(ck, out var pendingTl)
            && pendingTl.Entries.Count > 0
            && !_sessions.Any(s => string.Equals(s.Key, ck, StringComparison.Ordinal)))
        {
            threadList.Add(new ChatThread
            {
                Id = ck,
                Title = _lastChatState?.ThreadTitle ?? "OpenClaw Windows Tray",
                Model = _lastChatState?.Model,
                Status = ChatThreadStatus.Running,
                Activity = ChatActivity.Idle,
            });
        }

        var threads = threadList.ToArray();

        // Snapshot a defensive copy of the timeline dict.
        var timelinesCopy = new Dictionary<string, ChatTimelineState>(_timelines);

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
            ComposeTarget: composeTarget);
    }

    private string? ResolveDefaultThreadIdLocked()
    {
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

    private static ChatThread ToThread(SessionInfo s)
    {
        var title = BuildSessionTitle(s);

        return new ChatThread
        {
            Id = s.Key ?? string.Empty,
            Title = title,
            Status = ChatThreadStatus.Running,
            Activity = string.IsNullOrEmpty(s.CurrentActivity) ? ChatActivity.Idle : ChatActivity.Working,
            Workspace = s.Channel,
            Model = s.Model,
            ThinkingLevel = s.ThinkingLevel,
            CreatedAt = s.StartedAt is { } st ? ToOffset(st) : null,
            UpdatedAt = s.UpdatedAt is { } ut ? ToOffset(ut) : null,
        };
    }

    /// <summary>
    /// Builds a human-readable title from the session key and display name.
    /// Keys follow the pattern agent:{agentId}:{sessionSlot} (e.g. agent:main:main, agent:assistant:main).
    /// When a DisplayName is set, we append the agent/slot as a qualifier to disambiguate
    /// sessions that share the same DisplayName.
    /// </summary>
    private static string BuildSessionTitle(SessionInfo s)
    {
        var baseName = !string.IsNullOrWhiteSpace(s.DisplayName)
            ? s.DisplayName!
            : (s.IsMain ? "OpenClaw Windows Tray" : s.ShortKey);

        // Parse agent:agentId:sessionSlot from the key
        var parts = (s.Key ?? "").Split(':');
        if (parts.Length >= 3 && parts[0] == "agent")
        {
            var agentId = parts[1];     // e.g. "main", "assistant"
            var sessionSlot = parts[2]; // e.g. "main", "assistant", "cron"

            // For the canonical main session (agent:main:main), just show the base name
            if (agentId == "main" && sessionSlot == "main")
                return baseName;

            // Otherwise, qualify with agent/slot to distinguish
            var qualifier = agentId == sessionSlot
                ? agentId                       // e.g. "assistant" when both match
                : $"{agentId}/{sessionSlot}";   // e.g. "assistant/main"

            return $"{baseName} ({qualifier})";
        }

        return baseName;
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
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenClawTray", "last-chat-state.json");

    private System.Threading.Timer? _lastChatStateSaveTimer;

    internal sealed class LastChatState
    {
        public string? DefaultThreadId { get; set; }
        public string? ThreadTitle { get; set; }
        public string? Model { get; set; }
        public string[]? AvailableModels { get; set; }
    }

    private LastChatState? _lastChatState;

    internal static LastChatState? LoadLastChatState()
    {
        try
        {
            if (!File.Exists(LastChatStateFilePath)) return null;
            var json = File.ReadAllText(LastChatStateFilePath);
            return System.Text.Json.JsonSerializer.Deserialize<LastChatState>(json);
        }
        catch { return null; }
    }

    private void DebounceSaveLastChatState(ChatDataSnapshot snapshot)
    {
        // Find the default thread to capture its title/model
        var defaultThread = snapshot.DefaultThreadId is { } dtId
            ? Array.Find(snapshot.Threads, t => t.Id == dtId)
            : snapshot.Threads.Length > 0 ? snapshot.Threads[0] : null;

        if (defaultThread is null && snapshot.AvailableModels.Length == 0) return;

        var state = new LastChatState
        {
            DefaultThreadId = snapshot.DefaultThreadId,
            ThreadTitle = defaultThread?.Title,
            Model = defaultThread?.Model,
            AvailableModels = snapshot.AvailableModels,
        };

        lock (_gate)
        {
            _lastChatState = state;
            _lastChatStateSaveTimer?.Dispose();
            _lastChatStateSaveTimer = new System.Threading.Timer(_ => SaveLastChatState(state), null, 2000, Timeout.Infinite);
        }
    }

    private static void SaveLastChatState(LastChatState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(LastChatStateFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = System.Text.Json.JsonSerializer.Serialize(state);
            var tmp = LastChatStateFilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, LastChatStateFilePath, overwrite: true);
        }
        catch { }
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
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenClawTray", "aborted-messages.json");

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
        catch
        {
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
        catch { /* best-effort persistence */ }
    }

    // ── Tool metadata persistence ─────────────────────────────────────

    /// <summary>Cached tool call metadata entry persisted to disk.</summary>
    internal sealed class CachedToolMeta
    {
        public long Ts { get; set; }
        public string ToolName { get; set; } = "";
        public string Label { get; set; } = "";
    }

    private static string DefaultToolMetaCacheFilePath
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") is { Length: > 0 } overrideDir
                ? overrideDir
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OpenClawTray");
            return Path.Combine(root, "tool-metadata.json");
        }
    }

    /// <summary>Max sessions to keep in the tool metadata cache.</summary>
    internal const int MaxCachedSessions = 20;

    /// <summary>Max tool entries per session in the cache.</summary>
    internal const int MaxToolEntriesPerSession = 500;

    private static Dictionary<string, List<CachedToolMeta>> LoadToolMetaCache(string cacheFilePath)
    {
        try
        {
            if (!File.Exists(cacheFilePath))
                return new();
            var json = File.ReadAllText(cacheFilePath);
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<CachedToolMeta>>>(json);
            return dict ?? new();
        }
        catch
        {
            return new();
        }
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

                snapshot = _toolMetaCache.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Select(e => new CachedToolMeta
                    {
                        Ts = e.Ts,
                        ToolName = e.ToolName,
                        Label = e.Label
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

            var json = System.Text.Json.JsonSerializer.Serialize(snapshot,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

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
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath))
                            File.Delete(tempPath);
                    }
                    catch
                    {
                        // Best-effort cleanup; persistence remains best-effort.
                    }
                }
            }
        }
        catch { /* best-effort persistence */ }
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

            if (!_sessionIds.TryGetValue(threadId, out var sessionId) || string.IsNullOrEmpty(sessionId))
                return;

            if (!_toolMetaCache.TryGetValue(sessionId, out var list))
            {
                list = new List<CachedToolMeta>();
                _toolMetaCache[sessionId] = list;
            }

            // Deduplicate by timestamp (same tool event shouldn't be cached twice)
            if (list.Count > 0 && list[^1].Ts == tsMs && list[^1].ToolName == toolName)
                return;

            list.Add(new CachedToolMeta { Ts = tsMs, ToolName = toolName, Label = label });

            // Cap per-session entries
            if (list.Count > MaxToolEntriesPerSession)
                list.RemoveRange(0, list.Count - MaxToolEntriesPerSession);

            // Debounce save — reset the timer on each cache addition so we only
            // write once after 500ms of quiescence, avoiding concurrent file writes.
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
    private Queue<CachedToolMeta>? GetCachedToolMetaForSession(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        lock (_gate)
        {
            if (_toolMetaCache.TryGetValue(sessionId!, out var list) && list.Count > 0)
                return new Queue<CachedToolMeta>(list.OrderBy(e => e.Ts));
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

        return cache.Dequeue();
    }

    /// <summary>
    /// After a successful abort, reload chat.history to capture the __openclaw.id
    /// of the aborted user message and persist it for future sessions.
    /// </summary>
    private async Task PersistAbortedMessageIdAsync(string threadId)
    {
        await _persistLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Delay(500).ConfigureAwait(false); // let gateway finalize
            var history = await _bridge.RequestChatHistoryAsync(threadId).ConfigureAwait(false);

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
