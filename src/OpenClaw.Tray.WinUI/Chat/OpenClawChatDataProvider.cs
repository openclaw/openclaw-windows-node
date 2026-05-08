using ChatSample.Chat.Model;
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

/// <summary>
/// Adapts <see cref="IChatGatewayBridge"/> (which wraps a live
/// <see cref="OpenClawGatewayClient"/>) into the
/// <see cref="IChatDataProvider"/> contract consumed by the vendored
/// <c>Chat.UI</c> Reactor components.
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
    private readonly IChatGatewayBridge _bridge;
    private readonly Action<Action>? _post;
    private readonly object _gate = new();
    private readonly Dictionary<string, ChatTimelineState> _timelines = new();
    private readonly Dictionary<string, string> _activeRunIds = new();   // sessionKey → runId
    private readonly Dictionary<string, string> _sessionIds = new();      // sessionKey → immutable sessionId
    private readonly HashSet<string> _historyLoaded = new();              // sessionKey
    private readonly HashSet<string> _historyInFlight = new();            // sessionKey
    // Per-thread, per-entry metadata: timestamp + model snapshot at the
    // moment the entry was created. Built up as events are applied so the
    // timeline renderer can show a "<sender> · <local time> · <model>" footer
    // beneath each message without having to extend the vendored
    // <see cref="ChatTimelineItem"/> record.
    private readonly Dictionary<string, Dictionary<string, ChatEntryMetadata>> _entryMeta = new();
    private SessionInfo[] _sessions = Array.Empty<SessionInfo>();
    private string[] _availableModels = Array.Empty<string>();
    private ConnectionStatus _status;
    private bool _disposed;

    public string DisplayName => "OpenClaw gateway";

    public event EventHandler<ChatDataChangedEventArgs>? Changed;
    public event EventHandler<ChatProviderNotificationEventArgs>? NotificationRequested;

    /// <param name="bridge">Adapter wrapping the live gateway client.</param>
    /// <param name="post">
    /// Optional UI-thread marshaling callback. Pass
    /// <c>action =&gt; dispatcherQueue.TryEnqueue(() =&gt; action())</c> from
    /// production code so that <see cref="Changed"/>/<see cref="NotificationRequested"/>
    /// callbacks observed by Reactor components fire on the UI thread.
    /// When <c>null</c>, callbacks fire on whatever thread the gateway raised
    /// the source event on (acceptable in unit tests).
    /// </param>
    public OpenClawChatDataProvider(IChatGatewayBridge bridge, Action<Action>? post = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _post = post;
        _status = bridge.CurrentStatus;

        // Seed models from whatever the bridge already knows about (a connect
        // that completed before the provider was constructed will have its
        // models.list snapshot cached on the bridge).
        if (bridge.GetCurrentModelsList() is { } seedModels)
            _availableModels = ExtractModelNames(seedModels);

        _bridge.StatusChanged += OnStatusChanged;
        _bridge.SessionsUpdated += OnSessionsUpdated;
        _bridge.ChatMessageReceived += OnChatMessageReceived;
        _bridge.AgentEventReceived += OnAgentEventReceived;
        _bridge.ModelsListUpdated += OnModelsListUpdated;
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

    public Task<ChatThread> CreateThreadAsync(string? initialMessage = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The gateway has no "create new chat session" RPC today; the operator
        // is always wired to the gateway's main session. Return that thread
        // and (optionally) send the initial message into it.
        ChatThread thread;
        lock (_gate)
        {
            EnsureTimelinesForSessionsLocked();
            thread = ResolveMainOrFirstThreadLocked()
                     ?? new ChatThread { Id = "main", Title = "Main session", Status = ChatThreadStatus.Running };
        }

        if (!string.IsNullOrWhiteSpace(initialMessage))
        {
            return SendMessageAsync(thread.Id, initialMessage!, cancellationToken)
                .ContinueWith(_ => thread, cancellationToken, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }
        return Task.FromResult(thread);
    }

    public async Task SendMessageAsync(string threadId, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Message cannot be empty.", nameof(message));

        var trimmed = message.Trim();
        var nonce = Guid.NewGuid().ToString("N");

        // 1. Optimistically add the user message + flag turn active.
        ChatDataSnapshot snapshot;
        string? sessionId;
        lock (_gate)
        {
            var current = GetOrCreateTimelineLocked(threadId);
            var beforeNextId = current.NextId;
            _timelines[threadId] = ChatTimelineReducer.AddLocalUser(current, trimmed, nonce);
            _sessionIds.TryGetValue(threadId, out sessionId);

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
            await _bridge.SendChatMessageAsync(trimmed, threadId, sessionId);
        }
        catch (Exception ex)
        {
            // Surface as an error in the timeline + notification — keeps the
            // user message visible so they can edit/retry.
            ApplyEventAndPublish(threadId, new ChatErrorEvent($"Send failed: {ex.Message}"));
            ApplyEventAndPublish(threadId, new ChatTurnEndEvent());
            RaiseNotification(new ChatProviderNotification(
                ChatProviderNotificationKind.Error, threadId, "Send failed", ex.Message));
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
        }

        if (!string.IsNullOrEmpty(runId))
        {
            try
            {
                await _bridge.SendChatAbortAsync(runId);
            }
            catch (Exception ex)
            {
                RaiseNotification(new ChatProviderNotification(
                    ChatProviderNotificationKind.Error, threadId, "Abort failed", ex.Message));
            }
        }

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

                ChatTimelineState ApplyAndCaptureMeta(ChatTimelineState s, ChatEvent e, ChatEntryMetadata meta)
                {
                    var beforeIds = new HashSet<string>(s.Entries.Count);
                    for (int i = 0; i < s.Entries.Count; i++) beforeIds.Add(s.Entries[i].Id);
                    var nextState = ChatTimelineReducer.Apply(s, e);
                    for (int i = 0; i < nextState.Entries.Count; i++)
                    {
                        var id = nextState.Entries[i].Id;
                        if (!beforeIds.Contains(id) && !rebuiltMeta.ContainsKey(id))
                            rebuiltMeta[id] = meta;
                    }
                    return nextState;
                }

                foreach (var msg in ordered)
                {
                    if (string.IsNullOrEmpty(msg.Text)) continue;

                    var ts = msg.Ts > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(msg.Ts).ToLocalTime()
                        : (DateTimeOffset?)null;
                    var msgMeta = new ChatEntryMetadata(ts, modelAtLoad);

                    var roleLower = msg.Role?.ToLowerInvariant() ?? "";
                    switch (roleLower)
                    {
                        case "user":
                            // ApplyUserMessage will set TurnActive=true; if the previous
                            // assistant turn never received a turn-end (because the
                            // gateway transcript doesn't emit one explicitly), clear
                            // ActiveAssistantId here so the next assistant message
                            // starts a fresh entry instead of overwriting the previous.
                            rebuilt = rebuilt with { ActiveAssistantId = null, ActiveReasoningId = null };
                            rebuilt = ApplyAndCaptureMeta(rebuilt, new ChatUserMessageEvent(msg.Text), msgMeta);
                            break;

                        case "assistant":
                            // ── Heuristic recovery for history-flattened tool calls ──
                            // The gateway strips ``stream:"item"`` / ``command_output``
                            // detail server-side when serving ``chat.history`` —
                            // raw exec output is replayed as plain assistant text.
                            // Detect these telltale shapes and route them through
                            // the chip pipeline so historic turns look like live ones.
                            if (LooksLikeSystemControlNote(msg.Text))
                            {
                                rebuilt = ApplyAndCaptureMeta(
                                    rebuilt,
                                    new ChatStatusEvent(msg.Text, ChatTone.Dim),
                                    msgMeta);
                                break;
                            }
                            if (LooksLikeFlattenedToolOutput(msg.Text))
                            {
                                var kind = ClassifyFlattenedToolOutput(msg.Text);
                                // Produce a synthetic chip pair so it renders the
                                // same way live tool events do.
                                rebuilt = ApplyAndCaptureMeta(
                                    rebuilt,
                                    new ChatToolStartEvent(kind, kind),
                                    msgMeta);
                                rebuilt = ApplyAndCaptureMeta(
                                    rebuilt,
                                    new ChatToolOutputEvent(msg.Text),
                                    msgMeta);
                                break;
                            }
                            rebuilt = ApplyAndCaptureMeta(rebuilt, new ChatMessageEvent(msg.Text), msgMeta);
                            // End the turn so the next assistant message starts a new
                            // entry rather than replacing this one (UpsertAssistant
                            // upserts by ActiveAssistantId, which TurnEnd clears).
                            rebuilt = ChatTimelineReducer.Apply(rebuilt, new ChatTurnEndEvent());
                            break;

                        case "system":
                        case "tool":
                            // Render system / tool transcript notes as muted Status
                            // entries so they're visible but de-emphasized vs. the
                            // user/assistant turn flow.
                            rebuilt = ApplyAndCaptureMeta(
                                rebuilt,
                                new ChatStatusEvent(msg.Text, ChatTone.Dim),
                                msgMeta);
                            break;

                        default:
                            // Unknown role — fall back to assistant rendering so it's
                            // at least visible. Bracket with TurnEnd to avoid
                            // collapsing into adjacent assistant entries.
                            rebuilt = ApplyAndCaptureMeta(rebuilt, new ChatMessageEvent(msg.Text), msgMeta);
                            rebuilt = ChatTimelineReducer.Apply(rebuilt, new ChatTurnEndEvent());
                            break;
                    }
                }
                // Final safety: ensure no lingering active turn after history load.
                rebuilt = rebuilt with { TurnActive = false, ActiveAssistantId = null, ActiveReasoningId = null };

                // Append any prior live entries that weren't part of history.
                // Preserve the metadata we already captured for those IDs.
                if (prior.Entries.Count > 0)
                {
                    var priorMeta = _entryMeta.TryGetValue(threadId, out var pm)
                        ? pm
                        : new Dictionary<string, ChatEntryMetadata>();
                    foreach (var entry in prior.Entries)
                    {
                        rebuilt.Entries.Add(entry);
                        if (priorMeta.TryGetValue(entry.Id, out var existing) && !rebuiltMeta.ContainsKey(entry.Id))
                            rebuiltMeta[entry.Id] = existing;
                    }
                    rebuilt = rebuilt with { TurnActive = prior.TurnActive };
                }

                _timelines[threadId] = rebuilt;
                _entryMeta[threadId] = rebuiltMeta;
                _historyLoaded.Add(threadId);
                snapshot = BuildSnapshotLocked();
            }
            Publish(snapshot);
        }
        catch (Exception ex)
        {
            RaiseNotification(new ChatProviderNotification(
                ChatProviderNotificationKind.Error, threadId, "Failed to load history", ex.Message));
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

    public Task SetModelAsync(string threadId, string model, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask; // Not supported by gateway today.
    }

    public Task SetPermissionModeAsync(string threadId, bool allowAll, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task RespondToPermissionAsync(string threadId, string requestId, bool allow, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _bridge.StatusChanged -= OnStatusChanged;
        _bridge.SessionsUpdated -= OnSessionsUpdated;
        _bridge.ChatMessageReceived -= OnChatMessageReceived;
        _bridge.AgentEventReceived -= OnAgentEventReceived;
        _bridge.ModelsListUpdated -= OnModelsListUpdated;
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
        lock (_gate)
        {
            justReconnected = status == ConnectionStatus.Connected
                              && _status != ConnectionStatus.Connected;
            _status = status;

            // On reconnect we may have missed streamed events for the active
            // turn (spec edge case). Invalidate the per-thread history cache
            // so the next selection / explicit LoadHistoryAsync call refetches
            // the canonical transcript from the gateway.
            if (justReconnected && _historyLoaded.Count > 0)
            {
                threadsToReload = _historyLoaded.ToArray();
                _historyLoaded.Clear();
            }
            else
            {
                threadsToReload = Array.Empty<string>();
            }
            snapshot = BuildSnapshotLocked();
        }
        Publish(snapshot);

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
        lock (_gate)
        {
            _sessions = sessions ?? Array.Empty<SessionInfo>();
            EnsureTimelinesForSessionsLocked();
            snapshot = BuildSnapshotLocked();
        }
        Publish(snapshot);
    }

    private void OnModelsListUpdated(object? sender, ModelsListInfo info)
    {
        ChatDataSnapshot snapshot;
        lock (_gate)
        {
            _availableModels = ExtractModelNames(info);
            snapshot = BuildSnapshotLocked();
        }
        Publish(snapshot);
    }

    private static string[] ExtractModelNames(ModelsListInfo info)
    {
        if (info?.Models is null || info.Models.Count == 0) return Array.Empty<string>();
        // Prefer DisplayName (which already falls back to Id when Name is null).
        // Filter out duplicates and empty entries deterministically.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>(info.Models.Count);
        foreach (var m in info.Models)
        {
            var n = m.DisplayName;
            if (string.IsNullOrEmpty(n)) continue;
            if (seen.Add(n)) list.Add(n);
        }
        return list.ToArray();
    }

    private void OnChatMessageReceived(object? sender, ChatMessageInfo message)
    {
        if (message is null) return;

        // User echoes are dropped — SendMessageAsync already added the local
        // entry that drove the round-trip.
        if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            return;
        if (!string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            return;
        if (string.IsNullOrEmpty(message.Text))
            return;

        var threadId = string.IsNullOrEmpty(message.SessionKey) ? "main" : message.SessionKey;
        ChatEntryMetadata? meta;
        lock (_gate) { meta = BuildLiveMetaLocked(threadId, message.Ts); }

        // Both `state: "delta"` and `state: "final"` carry the cumulative
        // assistant text (the gateway's EmbeddedBlockChunker emits completed
        // blocks, not token deltas — see spec §"Block Streaming"). Map both
        // to ChatMessageEvent so the reducer REPLACES the active assistant
        // entry's text. Final additionally ends the turn.
        ApplyEventAndPublish(threadId, new ChatMessageEvent(message.Text), meta);

        if (message.IsFinal)
        {
            ApplyEventAndPublish(threadId, new ChatTurnEndEvent());
            RaiseNotification(new ChatProviderNotification(
                ChatProviderNotificationKind.TurnComplete, threadId, "Assistant replied"));
        }
    }

    private void OnAgentEventReceived(object? sender, AgentEventInfo evt)
    {
        if (evt is null) return;
        var threadId = string.IsNullOrEmpty(evt.SessionKey) ? "main" : evt.SessionKey;

        // Track the active runId per session for chat.abort. Lifecycle.start
        // sets it; lifecycle.end/error clears it.
        UpdateActiveRunId(evt, threadId);

        ChatEvent? mapped = MapAgentEvent(evt);
        if (mapped is null) return;

        // AgentEventInfo.Ts is a double of unix-epoch ms (per OpenClawGatewayClient).
        var tsMs = evt.Ts > 0 ? (long)evt.Ts : 0L;
        ChatEntryMetadata? meta;
        lock (_gate) { meta = BuildLiveMetaLocked(threadId, tsMs); }

        ApplyEventAndPublish(threadId, mapped, meta);
    }

    private void UpdateActiveRunId(AgentEventInfo evt, string threadId)
    {
        if (string.Equals(evt.Stream, "lifecycle", StringComparison.OrdinalIgnoreCase) &&
            evt.Data.ValueKind == System.Text.Json.JsonValueKind.Object &&
            evt.Data.TryGetProperty("phase", out var phaseProp))
        {
            var phase = phaseProp.GetString()?.ToLowerInvariant();
            lock (_gate)
            {
                if (phase == "start" && !string.IsNullOrEmpty(evt.RunId))
                    _activeRunIds[threadId] = evt.RunId;
                else if (phase == "end" || phase == "error")
                    _activeRunIds.Remove(threadId);
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
                    _activeRunIds.Remove(threadId);
            }
        }
    }

    private static ChatEvent? MapAgentEvent(AgentEventInfo evt)
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
            default:
                return null;
        }
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

        // Block content: data.content / data.text — final or chunked block.
        if (evt.Data.TryGetProperty("content", out var contentProp) &&
            contentProp.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var content = contentProp.GetString();
            if (!string.IsNullOrEmpty(content))
                return new ChatMessageEvent(content);
        }
        if (evt.Data.TryGetProperty("text", out var textProp) &&
            textProp.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var text = textProp.GetString();
            if (!string.IsNullOrEmpty(text))
                return new ChatMessageEvent(text);
        }

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

        return phase.ToLowerInvariant() switch
        {
            "start" => new ChatToolStartEvent(label, toolName),
            "result" => new ChatToolOutputEvent(ExtractToolResultText(evt.Data, fallback: label)),
            "error" => new ChatToolErrorEvent(ExtractToolErrorText(evt.Data, fallback: label)),
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

        return phase.ToLowerInvariant() switch
        {
            "start" => new ChatToolStartEvent(title, toolName),
            // ``end`` flips the active tool's status to Success even when no
            // command_output arrived (e.g. ``read``, ``glob`` — non-shell).
            // Use the title as a no-op output so the reducer marks Success.
            "end" => new ChatToolOutputEvent(string.Empty),
            "error" => new ChatToolErrorEvent(title),
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

        return new ChatToolOutputEvent(output);
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

    // ── chat.history flattened-tool-output recovery ──

    /// <summary>
    /// True when an assistant-role <c>chat.history</c> message looks like a
    /// gateway control note that the web UI hides. We render these as a
    /// dim Status entry instead of a full assistant bubble so the
    /// conversation flow doesn't get overwhelmed by transcript scaffolding.
    /// </summary>
    private static bool LooksLikeSystemControlNote(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var t = text.TrimStart();
        return t.StartsWith("System (untrusted):", StringComparison.Ordinal)
            || t.StartsWith("System:", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when an assistant-role <c>chat.history</c> message is almost
    /// certainly the verbatim output of an exec tool that the gateway
    /// flattened into plain text on the way out (the spec confirms it
    /// strips ``<tool_call>`` / ``<function_call>`` XML and tool blocks
    /// before serving history). Detected via well-known terminator
    /// markers and shell-output-shaped openings.
    /// </summary>
    private static bool LooksLikeFlattenedToolOutput(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 40) return false;

        // Strong terminator markers — the gateway emits these verbatim
        // around exec results.
        if (text.Contains("Process exited with code", StringComparison.Ordinal)) return true;
        if (text.Contains("Command still running (session", StringComparison.Ordinal)) return true;
        if (text.Contains("Exec completed (", StringComparison.Ordinal)) return true;

        // Opening looks like a UNC / POSIX path — common shape for ``ls``
        // / ``file`` / ``stat`` style tools.
        var head = text.AsSpan(0, Math.Min(80, text.Length));
        if (head.StartsWith("\\\\wsl.localhost\\")) return true;
        if (head.StartsWith("/usr/") || head.StartsWith("/home/") || head.StartsWith("/var/") ||
            head.StartsWith("/etc/") || head.StartsWith("/tmp/")) return true;

        return false;
    }

    /// <summary>
    /// Best-guess kind label for a flattened-tool-output assistant
    /// message. Used to populate the tool chip's monospace
    /// kind suffix (matches the live ``stream:"item"`` extraction).
    /// </summary>
    private static string ClassifyFlattenedToolOutput(string text)
    {
        if (text.Contains("Command still running", StringComparison.Ordinal) ||
            text.Contains("Process exited with code", StringComparison.Ordinal))
            return "process";
        if (text.Contains("Exec completed (", StringComparison.Ordinal))
            return "exec";
        return "exec";
    }

    // ── State helpers ──

    private void ApplyEventAndPublish(string threadId, ChatEvent evt, ChatEntryMetadata? meta = null)
    {
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
            if (meta is not null)
            {
                var threadMeta = GetOrCreateThreadMetaLocked(threadId);
                for (int i = 0; i < next.Entries.Count; i++)
                {
                    var id = next.Entries[i].Id;
                    if (!beforeIds.Contains(id) && !threadMeta.ContainsKey(id))
                        threadMeta[id] = meta;
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
            current = ChatTimelineState.Initial() with { HistoryLoaded = true };
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
                _timelines[s.Key] = ChatTimelineState.Initial() with { HistoryLoaded = true };
        }
    }

    private ChatThread? ResolveMainOrFirstThreadLocked()
    {
        if (_sessions.Length == 0) return null;
        var main = Array.Find(_sessions, s => s.IsMain);
        return ToThread(main ?? _sessions[0]);
    }

    private ChatDataSnapshot BuildSnapshotLocked()
    {
        var threads = new ChatThread[_sessions.Length];
        for (int i = 0; i < _sessions.Length; i++)
            threads[i] = ToThread(_sessions[i]);

        // Snapshot a defensive copy of the timeline dict.
        var timelinesCopy = new Dictionary<string, ChatTimelineState>(_timelines);

        var defaultThreadId = ResolveDefaultThreadIdLocked(threads);

        var connectionLabel = _status switch
        {
            ConnectionStatus.Connected => "Connected",
            ConnectionStatus.Connecting => "Connecting…",
            ConnectionStatus.Disconnected => "Disconnected",
            ConnectionStatus.Error => "Disconnected — error",
            _ => _status.ToString()
        };

        return new ChatDataSnapshot(
            Threads: threads,
            Timelines: timelinesCopy,
            DefaultThreadId: defaultThreadId,
            ConnectionStatus: connectionLabel,
            AvailableModels: _availableModels);
    }

    private static string? ResolveDefaultThreadIdLocked(ChatThread[] threads)
    {
        if (threads.Length == 0) return null;
        for (int i = 0; i < threads.Length; i++)
        {
            // ChatThread doesn't carry the IsMain flag explicitly, so detect it
            // by a heuristic: the upstream Title we set for main sessions.
            if (string.Equals(threads[i].Id, "main", StringComparison.OrdinalIgnoreCase))
                return threads[i].Id;
        }
        return threads[0].Id;
    }

    private static ChatThread ToThread(SessionInfo s)
    {
        return new ChatThread
        {
            Id = string.IsNullOrEmpty(s.Key) ? "main" : s.Key,
            Title = !string.IsNullOrWhiteSpace(s.DisplayName)
                ? s.DisplayName!
                : (s.IsMain ? "Main session" : s.ShortKey),
            Status = ChatThreadStatus.Running,
            Activity = string.IsNullOrEmpty(s.CurrentActivity) ? ChatActivity.Idle : ChatActivity.Working,
            Workspace = s.Channel,
            Model = s.Model,
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
            return;
        }
        _post(() => Changed?.Invoke(this, args));
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
}
