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
            ChatPermissionRequestEvent e => state with
            {
                PendingPermission = new ChatPermissionRequest(e.RequestId, e.PermissionKind, e.ToolName, e.Detail)
            },
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

    public static ChatTimelineState ClearPermission(ChatTimelineState state)
        => state with { PendingPermission = null };

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
                IntentSummary: e.Text, ToolArgs: e.ToolArgs)),
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
            PendingPermission = null
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
            PendingPermission = null
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

        if (replace && reconcilePrevious && state.Entries.Count > 0)
        {
            var lastIndex = state.Entries.Count - 1;
            var last = state.Entries[lastIndex];
            if (last.Kind == ChatTimelineItemKind.Assistant)
            {
                return state with
                {
                    Entries = state.Entries.SetItem(lastIndex, last with
                    {
                        Text = text,
                        IsStreaming = streaming
                    })
                };
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
            PendingPermission = null
        };
    }


    static ChatTimelineState BeginTurn(ChatTimelineState state) =>
        state.TurnActive ? state : state with { TurnActive = true };
}
