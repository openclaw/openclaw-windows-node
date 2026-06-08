namespace OpenClaw.Chat;

public static class ChatTimelineReducer
{
    private const int MaxLocalNonces = 256;

    public static ChatTimelineState Apply(ChatTimelineState state, ChatEvent evt)
    {
        return evt switch
        {
            ChatUserMessageEvent e => ApplyUserMessage(state, e),
            ChatThinkingEvent => state with { TurnActive = true },
            ChatReasoningEvent e => UpsertReasoning(BeginTurn(state), e.Text, replace: true),
            ChatReasoningDeltaEvent e => UpsertReasoning(BeginTurn(state), e.Text, replace: false),
            ChatMessageDeltaEvent e => UpsertAssistant(BeginTurn(state), e.Text, replace: false, streaming: true),
            ChatMessageEvent e => UpsertAssistant(BeginTurn(state), e.Text, replace: true, streaming: false, e.ReconcilePrevious),
            ChatTurnEndEvent => ApplyTurnEnd(state),
            ChatIntentEvent e => state with { CurrentIntent = e.Intent },
            ChatToolStartEvent e => ApplyToolStart(state, e),
            ChatToolOutputEvent e => ApplyToolOutput(state, e),
            ChatToolErrorEvent e => ApplyToolError(state, e),
            ChatErrorEvent e => PushEntry(ApplyTurnEnd(state), ChatTimelineItemKind.Status, e.Text, ChatTone.Error),
            ChatStatusEvent e => PushEntry(state, ChatTimelineItemKind.Status, e.Text, e.Tone),
            ChatRestoredEvent e => PushEntry(state, ChatTimelineItemKind.Status, e.Text, ChatTone.Info),
            ChatContextChangedEvent => state,
            ChatModelChangedEvent e => PushEntry(state, ChatTimelineItemKind.Status, $"Model -> {e.Model}", ChatTone.Success),
            ChatPermissionRequestEvent e => ApplyPermissionRequest(state, e),
            ChatRawEvent e => e.Text is { Length: > 0 } t ? PushEntry(state, ChatTimelineItemKind.Raw, t) : state,
            _ => state
        };
    }

    public static ChatTimelineState AddLocalUser(ChatTimelineState state, string text, string nonce)
    {
        var id = $"e{state.NextId}";
        var localNonces = state.LocalNonces;
        if (localNonces.Count >= MaxLocalNonces)
        {
            foreach (var nonceToDrop in localNonces)
            {
                localNonces = localNonces.Remove(nonceToDrop);
                break;
            }
        }

        return state with
        {
            Entries = state.Entries.Add(new(id, ChatTimelineItemKind.User, text)),
            LocalNonces = localNonces.Add(nonce),
            NextId = state.NextId + 1,
            TurnActive = true
        };
    }

    public static ChatTimelineState AddSystem(ChatTimelineState state, string text, ChatTone tone = ChatTone.Info)
        => PushEntry(state, ChatTimelineItemKind.Status, text, tone);

    /// <summary>
    /// Push a PermissionRequest entry that is already in a decided state.
    /// Used for synthetic cards that originate locally (e.g. node-side
    /// exec-approval prompt) where there was never a Pending bubble — the
    /// decision was made outside the gateway approval stream. Does not
    /// touch <see cref="ChatTimelineState.PendingPermission"/>; the synthetic
    /// RequestId is a fresh GUID that won't collide with gateway-issued ids.
    /// </summary>
    public static ChatTimelineState AddDecidedPermission(
        ChatTimelineState state,
        string permissionKind,
        string toolName,
        string detail,
        ChatPermissionDecision decision)
    {
        var id = $"e{state.NextId}";
        var entry = new ChatTimelineItem(
            id,
            ChatTimelineItemKind.PermissionRequest,
            detail,
            ToolName: toolName,
            IntentSummary: permissionKind,
            PermissionRequestId: System.Guid.NewGuid().ToString("N"),
            PermissionDecision: decision);

        return state with
        {
            Entries = state.Entries.Add(entry),
            NextId = state.NextId + 1
        };
    }

    public static ChatTimelineState ClearPermission(ChatTimelineState state)
        => ResolvePermission(state, requestId: state.PendingPermission?.RequestId, decision: ChatPermissionDecision.Expired);

    /// <summary>
    /// Marks the timeline entry for <paramref name="requestId"/> with a
    /// terminal <paramref name="decision"/> and (if it is the live one)
    /// clears <see cref="ChatTimelineState.PendingPermission"/>.
    /// </summary>
    /// <remarks>
    /// <para>This is the source of truth for "the inline approval bubble
    /// is now decided". UI callers route Allow/Deny clicks here with
    /// <see cref="ChatPermissionDecision.Allowed"/> / <see cref="ChatPermissionDecision.Denied"/>
    /// so the bubble collapses to its decided badge immediately, without
    /// waiting for the gateway round-trip.</para>
    /// <para>Gateway-side terminal events (the legacy ClearPermission
    /// path) call this with <see cref="ChatPermissionDecision.Expired"/>
    /// as a backstop in case the user never clicked — visually
    /// distinguishes "decided by user" from "decided elsewhere or timed
    /// out".</para>
    /// <para>If <paramref name="requestId"/> is null or no matching entry
    /// exists, the entry list is left untouched and only
    /// <see cref="ChatTimelineState.PendingPermission"/> is cleared (mirrors
    /// the prior ClearPermission contract).</para>
    /// <para>Entries whose <see cref="ChatTimelineItem.PermissionDecision"/>
    /// is already non-Pending are not overwritten — once the user has made
    /// a choice locally, a later gateway "Expired" event won't downgrade it.</para>
    /// </remarks>
    public static ChatTimelineState ResolvePermission(ChatTimelineState state, string? requestId, ChatPermissionDecision decision)
    {
        var entries = state.Entries;
        if (!string.IsNullOrEmpty(requestId))
        {
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i];
                if (entry.Kind != ChatTimelineItemKind.PermissionRequest) continue;
                if (!string.Equals(entry.PermissionRequestId, requestId, StringComparison.Ordinal)) continue;
                if (entry.PermissionDecision != ChatPermissionDecision.Pending) break;
                entries = entries.SetItem(i, entry with { PermissionDecision = decision });
                break;
            }
        }

        var clearedPending = state.PendingPermission is null
            || (requestId is null)
            || string.Equals(state.PendingPermission.RequestId, requestId, StringComparison.Ordinal)
                ? null
                : state.PendingPermission;

        return state with { Entries = entries, PendingPermission = clearedPending };
    }

    static ChatTimelineState ApplyPermissionRequest(ChatTimelineState state, ChatPermissionRequestEvent e)
    {
        // A second exec-approval can arrive before the first is resolved.
        // Mark any still-Pending prior approval entry as Expired so the
        // timeline doesn't show two live Allow/Deny prompts at once — the
        // gateway has implicitly superseded the earlier one by issuing a
        // new approval. This mirrors the prior single-slot PendingPermission
        // behavior, which silently replaced the older request.
        var entries = state.Entries;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var existing = entries[i];
            if (existing.Kind != ChatTimelineItemKind.PermissionRequest) continue;
            if (existing.PermissionDecision != ChatPermissionDecision.Pending) continue;
            entries = entries.SetItem(i, existing with { PermissionDecision = ChatPermissionDecision.Expired });
        }

        var detail = e.Detail;
        // Defensive: an empty/whitespace RequestId in the gateway event
        // would otherwise be committed to state. ResolvePermission's
        // entry-scan guard skips on IsNullOrEmpty, so PendingPermission
        // would be cleared by ClearPermission while the Pending entry
        // stays stuck with disabled buttons. Drop such malformed events.
        if (string.IsNullOrWhiteSpace(e.RequestId))
        {
            return state;
        }

        var id = $"e{state.NextId}";
        var entry = new ChatTimelineItem(
            id,
            ChatTimelineItemKind.PermissionRequest,
            detail,
            ToolName: e.ToolName,
            IntentSummary: e.PermissionKind,
            PermissionRequestId: e.RequestId,
            PermissionDecision: ChatPermissionDecision.Pending);

        return state with
        {
            Entries = entries.Add(entry),
            NextId = state.NextId + 1,
            PendingPermission = new ChatPermissionRequest(e.RequestId, e.PermissionKind, e.ToolName, detail)
        };
    }

    static ChatTimelineState ApplyUserMessage(ChatTimelineState state, ChatUserMessageEvent e)
    {
        if (e.Nonce is { } nonce && state.LocalNonces.Contains(nonce))
        {
            return state with { LocalNonces = state.LocalNonces.Remove(nonce) };
        }

        var id = $"e{state.NextId}";
        return state with
        {
            Entries = state.Entries.Add(new(id, ChatTimelineItemKind.User, e.Text)),
            NextId = state.NextId + 1,
            TurnActive = true
        };
    }

    static ChatTimelineState ApplyToolStart(ChatTimelineState state, ChatToolStartEvent e)
    {
        var id = $"e{state.NextId}";
        var activeToolCalls = state.ActiveToolCalls;

        // Register by ToolCallId if available (parallel-safe correlation).
        if (e.ToolCallId is { } tcId)
            activeToolCalls = activeToolCalls.SetItem(tcId, id);

        return state with
        {
            Entries = state.Entries.Add(new(id, ChatTimelineItemKind.ToolCall, e.Text,
                ToolName: e.ToolName, ToolResult: ChatToolCallStatus.InProgress,
                IntentSummary: e.Text, ToolArgs: e.ToolArgs, ToolCallId: e.ToolCallId)),
            NextId = state.NextId + 1,
            // Only update legacy positional slot for events without a correlation ID.
            ActiveToolCallId = e.ToolCallId is null ? id : state.ActiveToolCallId,
            ActiveToolCalls = activeToolCalls,
            TurnActive = true
        };
    }

    static ChatTimelineState ApplyToolOutput(ChatTimelineState state, ChatToolOutputEvent e)
    {
        var (entries, entryId) = ResolveToolEntry(state, e.ToolCallId);
        if (entryId is { } tid)
        {
            var idx = entries.FindIndex(en => en.Id == tid);
            if (idx >= 0)
            {
                var existingOutput = entries[idx].ToolOutput;
                entries = entries.SetItem(idx, entries[idx] with
                {
                    ToolResult = ChatToolCallStatus.Success,
                    ToolOutput = string.IsNullOrEmpty(e.Text) && existingOutput is not null
                        ? existingOutput
                        : e.Text
                });
            }
        }
        return state with
        {
            Entries = entries,
            // Don't remove from ActiveToolCalls here: multiple output events can arrive
            // for the same tool (command_output + item end). Mapping is cleared at turn end.
            ActiveToolCallId = (entryId == state.ActiveToolCallId) ? null : state.ActiveToolCallId,
            // NOTE: PendingPermission intentionally preserved. Exec-approval
            // events interleave with tool item events (chip start → approval
            // → tool output), so wiping the banner on tool output would race
            // it off-screen. Callers clear via ClearPermission on user click
            // or on phase=resolved.
        };
    }

    static ChatTimelineState ApplyToolError(ChatTimelineState state, ChatToolErrorEvent e)
    {
        var (entries, entryId) = ResolveToolEntry(state, e.ToolCallId);
        if (entryId is { } tid)
        {
            var idx = entries.FindIndex(en => en.Id == tid);
            if (idx >= 0)
            {
                entries = entries.SetItem(idx, entries[idx] with
                {
                    ToolResult = ChatToolCallStatus.Error,
                    ToolOutput = e.Text
                });
            }
        }
        return state with
        {
            Entries = entries,
            ActiveToolCallId = (entryId == state.ActiveToolCallId) ? null : state.ActiveToolCallId,
            ActiveToolCalls = e.ToolCallId is { } k ? state.ActiveToolCalls.Remove(k) : state.ActiveToolCalls,
            // PendingPermission preserved — see ApplyToolOutput note.
        };
    }

    /// <summary>
    /// Resolve which timeline entry a tool output/error belongs to.
    /// Prefers ID-based lookup (parallel-safe); falls back to ActiveToolCallId only for legacy events (no ID).
    /// </summary>
    static (System.Collections.Immutable.ImmutableList<ChatTimelineItem> Entries, string? EntryId) ResolveToolEntry(
        ChatTimelineState state, string? toolCallId)
    {
        if (toolCallId is { } tcId)
        {
            // ID provided: strict lookup only. If mapping already consumed, no-op (don't misroute).
            return state.ActiveToolCalls.TryGetValue(tcId, out var entryId)
                ? (state.Entries, entryId)
                : (state.Entries, null);
        }

        // No ID: legacy positional fallback.
        return (state.Entries, state.ActiveToolCallId);
    }

    static ChatTimelineState UpsertAssistant(ChatTimelineState state, string text, bool replace, bool streaming, bool reconcilePrevious = false)
    {
        if (state.ActiveAssistantId is { } aid)
        {
            var idx = state.Entries.FindIndex(e => e.Id == aid);
            if (idx >= 0)
            {
                var existing = state.Entries[idx];
                return state with
                {
                    Entries = state.Entries.SetItem(idx, existing with
                    {
                        Text = replace ? text : existing.Text + text,
                        IsStreaming = streaming
                    })
                };
            }
        }

        // A final ChatMessageEvent that arrives without an ActiveAssistantId
        // must, in certain cases, reconcile into the most recent Assistant
        // entry instead of creating a duplicate bubble:
        //
        //  • `reconcilePrevious` flag: explicit opt-in from the provider,
        //    used when the gateway emits the final message AFTER tool entries
        //    have been appended (text → tool → tool output → final text). The
        //    flag lets the reducer collapse the streaming preview into the
        //    final text even though the immediate last entry is a ToolCall.
        //
        //  • Identical-text safety net: if the most recent Assistant entry
        //    (within the same turn — i.e. before any User boundary) has
        //    byte-equal text to the incoming message, collapse them
        //    regardless of any flag. This catches duplicate ChatMessageEvent
        //    emissions from the gateway (see the duplicate-bubble screenshot
        //    bug where the same final text was rendered twice in a row).
        if (replace && state.Entries.Count > 0)
        {
            // Scan backward for the most recent Assistant entry — not just
            // the very last one (see reasons above).
            for (var li = state.Entries.Count - 1; li >= 0; li--)
            {
                var candidate = state.Entries[li];
                if (candidate.Kind == ChatTimelineItemKind.Assistant)
                {
                    var shouldMerge =
                        reconcilePrevious
                        || string.Equals(candidate.Text, text, StringComparison.Ordinal);
                    if (!shouldMerge)
                        break;

                    return state with
                    {
                        Entries = state.Entries.SetItem(li, candidate with
                        {
                            Text = text,
                            IsStreaming = streaming
                        })
                    };
                }
                // Stop scanning once we hit a User entry — that's a turn
                // boundary, the assistant entry above it belongs to a
                // previous turn and must not be reconciled into.
                if (candidate.Kind == ChatTimelineItemKind.User)
                    break;
            }
        }

        var id = $"e{state.NextId}";
        return state with
        {
            Entries = state.Entries.Add(new(id, ChatTimelineItemKind.Assistant, text, IsStreaming: streaming)),
            NextId = state.NextId + 1,
            ActiveAssistantId = id
        };
    }

    static ChatTimelineState UpsertReasoning(ChatTimelineState state, string text, bool replace)
    {
        if (state.ActiveReasoningId is { } rid)
        {
            var idx = state.Entries.FindIndex(e => e.Id == rid);
            if (idx >= 0)
            {
                var existing = state.Entries[idx];
                return state with
                {
                    Entries = state.Entries.SetItem(idx, existing with { Text = replace ? text : existing.Text + text })
                };
            }
        }

        var id = $"e{state.NextId}";
        return state with
        {
            Entries = state.Entries.Add(new(id, ChatTimelineItemKind.Reasoning, text)),
            NextId = state.NextId + 1,
            ActiveReasoningId = id
        };
    }

    static ChatTimelineState PushEntry(ChatTimelineState state, ChatTimelineItemKind kind, string text, ChatTone? tone = null)
    {
        var id = $"e{state.NextId}";
        return state with
        {
            Entries = state.Entries.Add(new(id, kind, text, Tone: tone)),
            NextId = state.NextId + 1
        };
    }

    static ChatTimelineState ApplyTurnEnd(ChatTimelineState state)
    {
        var entries = state.Entries;

        // Collect all entry IDs that are still tracked as active.
        var activeEntryIds = new System.Collections.Generic.HashSet<string>();
        if (state.ActiveToolCallId is { } fallbackId)
            activeEntryIds.Add(fallbackId);
        foreach (var kvp in state.ActiveToolCalls)
            activeEntryIds.Add(kvp.Value);

        // Mark all remaining in-progress tools as Interrupted (reality: they never completed).
        foreach (var entryId in activeEntryIds)
        {
            var idx = entries.FindIndex(en => en.Id == entryId);
            if (idx >= 0 && entries[idx].ToolResult == ChatToolCallStatus.InProgress)
            {
                entries = entries.SetItem(idx, entries[idx] with
                {
                    ToolResult = ChatToolCallStatus.Interrupted
                });
            }
        }

        return state with
        {
            Entries = entries,
            TurnActive = false,
            ActiveAssistantId = null,
            ActiveReasoningId = null,
            ActiveToolCallId = null,
            ActiveToolCalls = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty,
            // PendingPermission preserved — exec approvals may outlive their
            // originating turn (gateway emits phase=resolved to clear).
        };
    }


    static ChatTimelineState BeginTurn(ChatTimelineState state) =>
        state.TurnActive ? state : state with { TurnActive = true };
}
