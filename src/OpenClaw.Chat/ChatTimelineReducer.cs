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
            ChatTurnEndEvent => state with { TurnActive = false, ActiveAssistantId = null, ActiveReasoningId = null, PendingPermission = null },
            ChatIntentEvent e => state with { CurrentIntent = e.Intent },
            ChatToolStartEvent e => ApplyToolStart(state, e),
            ChatToolOutputEvent e => ApplyToolOutput(state, e),
            ChatToolErrorEvent e => ApplyToolError(state, e),
            ChatErrorEvent e => PushEntry(EndTurn(state), ChatTimelineItemKind.Status, e.Text, ChatTone.Error),
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
        return state with
        {
            Entries = state.Entries.Add(new(id, ChatTimelineItemKind.ToolCall, e.Text,
                ToolName: e.ToolName, ToolResult: ChatToolCallStatus.InProgress,
                IntentSummary: e.Text, ToolArgs: e.ToolArgs, ToolCallId: e.ToolCallId)),
            NextId = state.NextId + 1,
            ActiveToolCallId = id,
            TurnActive = true
        };
    }

    static ChatTimelineState ApplyToolOutput(ChatTimelineState state, ChatToolOutputEvent e)
    {
        var entries = state.Entries;
        var targetId = FindToolEntryId(state, e.ToolCallId);
        if (targetId is not null)
        {
            var idx = entries.FindIndex(en => en.Id == targetId);
            if (idx >= 0)
            {
                entries = entries.SetItem(idx, entries[idx] with
                {
                    ToolResult = ChatToolCallStatus.Success,
                    ToolOutput = e.Text
                });
            }
        }
        // Clear ActiveToolCallId only if we just completed it (or no ToolCallId was specified)
        var nextActiveId = (e.ToolCallId is null || targetId == state.ActiveToolCallId)
            ? null : state.ActiveToolCallId;
        return state with { Entries = entries, ActiveToolCallId = nextActiveId, PendingPermission = null };
    }

    static ChatTimelineState ApplyToolError(ChatTimelineState state, ChatToolErrorEvent e)
    {
        var entries = state.Entries;
        var targetId = FindToolEntryId(state, e.ToolCallId);
        if (targetId is not null)
        {
            var idx = entries.FindIndex(en => en.Id == targetId);
            if (idx >= 0)
            {
                entries = entries.SetItem(idx, entries[idx] with
                {
                    ToolResult = ChatToolCallStatus.Error,
                    ToolOutput = e.Text
                });
            }
        }
        var nextActiveId = (e.ToolCallId is null || targetId == state.ActiveToolCallId)
            ? null : state.ActiveToolCallId;
        return state with { Entries = entries, ActiveToolCallId = nextActiveId, PendingPermission = null };
    }

    /// <summary>
    /// Find the timeline entry id for a tool call. When <paramref name="toolCallId"/>
    /// is provided, match by the stored ToolCallId on the entry. Otherwise fall
    /// back to <see cref="ChatTimelineState.ActiveToolCallId"/>.
    /// </summary>
    static string? FindToolEntryId(ChatTimelineState state, string? toolCallId)
    {
        if (toolCallId is not null)
        {
            for (int i = state.Entries.Count - 1; i >= 0; i--)
            {
                if (state.Entries[i].ToolCallId == toolCallId)
                    return state.Entries[i].Id;
            }
            // ToolCallId was specified but not found — don't fall back to
            // ActiveToolCallId as that would corrupt an unrelated tool entry.
            return null;
        }
        return state.ActiveToolCallId;
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

    static ChatTimelineState EndTurn(ChatTimelineState state) => state with
    {
        TurnActive = false,
        ActiveAssistantId = null,
        ActiveReasoningId = null,
        ActiveToolCallId = null,
        PendingPermission = null
    };

    static ChatTimelineState BeginTurn(ChatTimelineState state) =>
        state.TurnActive ? state : state with { TurnActive = true };
}
