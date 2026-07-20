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
            ChatReasoningEndEvent => state.ActiveReasoningId is null ? state : state with { ActiveReasoningId = null },
            ChatMessageDeltaEvent e => UpsertAssistant(BeginTurn(state), e.Text, replace: false, streaming: true),
            ChatMessageEvent e => UpsertAssistant(BeginTurn(state), e.Text, replace: true, streaming: e.IsStreaming, e.ReconcilePrevious),
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
        // User message = hard turn boundary. Must apply BEFORE appending the
        // new user entry so a future reconcile-flagged final can't fast-path-
        // overwrite the prior turn's assistant reply.
        state = ClearStreamingAtTurnBoundary(state);

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

    public static ChatTimelineState BeginLocalUserTurn(ChatTimelineState state)
    {
        state = ClearStreamingAtTurnBoundary(state);
        return state with { TurnActive = true };
    }

    // Hard turn boundary cleanup shared by both user-message entry points
    // (AddLocalUser for typed input, ApplyUserMessage for gateway-injected
    // events). Clears ActiveAssistantId/ActiveReasoningId and demotes any
    // still-streaming assistant entry so the next reconcile-flagged final
    // cannot silently overwrite the prior turn's reply when ChatTurnEndEvent
    // is dropped or delayed by the gateway.
    static ChatTimelineState ClearStreamingAtTurnBoundary(ChatTimelineState state)
    {
        var entries = state.Entries;
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Kind == ChatTimelineItemKind.Assistant && entries[i].IsStreaming)
            {
                entries = entries.SetItem(i, entries[i] with { IsStreaming = false });
            }
        }

        return state with
        {
            Entries = entries,
            ActiveAssistantId = null,
            ActiveReasoningId = null
        };
    }

    public static ChatTimelineState AddSystem(ChatTimelineState state, string text, ChatTone tone = ChatTone.Info)
        => PushEntry(state, ChatTimelineItemKind.Status, text, tone);

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
            PermissionDecision: ChatPermissionDecision.Pending,
            PermissionActions: e.Actions);

        return state with
        {
            Entries = entries.Add(entry),
            NextId = state.NextId + 1,
            PendingPermission = new ChatPermissionRequest(e.RequestId, e.PermissionKind, e.ToolName, detail, e.Actions)
        };
    }

    static ChatTimelineState ApplyUserMessage(ChatTimelineState state, ChatUserMessageEvent e)
    {
        if (e.Nonce is { } nonce && state.LocalNonces.Contains(nonce))
        {
            return state with { LocalNonces = state.LocalNonces.Remove(nonce) };
        }

        // User message = hard turn boundary (see ClearStreamingAtTurnBoundary).
        state = ClearStreamingAtTurnBoundary(state);

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
        text ??= string.Empty;
        if (state.ActiveAssistantId is { } aid)
        {
            var idx = state.Entries.FindIndex(e => e.Id == aid);
            // Only take the fast-path when the active assistant entry is still
            // the timeline frontier (the last entry). If a tool call (or any
            // other entry kind) has been appended after it, the active
            // assistant belongs to a closed intra-turn segment — merging into
            // it would silently overwrite the prior segment's bubble when the
            // model resumes speaking after a tool call (assistant₁ → tool →
            // assistant₂ within a single turn). Fall through to the scan-back
            // logic instead; its reconcile/byte-equal guards still collapse
            // the deltas-then-final case correctly because the streaming
            // preview remains marked IsStreaming=true.
            if (idx >= 0 && idx == state.Entries.Count - 1)
            {
                var existing = state.Entries[idx];
                // Even on the fast-path, if the incoming text is cumulative-
                // for-turn (replace=true) it may carry an EARLIER assistant
                // bubble's text as a prefix — the bubble whose final arrived
                // AFTER a tool call within the same turn. Stripping the LCP of
                // that earlier bubble keeps the post-tool segment alone in
                // this bubble. Without this, the second cumulative chat.message
                // for the same post-tool segment would re-introduce the
                // previous bubble's text as a prefix.
                //
                // For delta appends (replace=false), use an overlap-aware
                // concatenation: if the existing bubble already ends with
                // (any prefix of) the incoming delta, only append the
                // non-overlapping suffix. This collapses the gateway's
                // redundant tail-emissions — e.g. a final chat.message
                // carrying "...mounted here." followed by an
                // agent.message.delta carrying " D: is mounted here." that
                // would otherwise produce "...mounted here. D: is mounted here.".
                var fastPathText = replace
                    ? StripCumulativeTurnPrefix(state.Entries, text, idx)
                    : AppendWithOverlap(existing.Text, text);
                return state with
                {
                    Entries = state.Entries.SetItem(idx, existing with
                    {
                        Text = fastPathText,
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
                    // ``reconcilePrevious`` only collapses into a still-streaming
                    // assistant entry (a delta-state chat.message preview that
                    // hasn't been replaced by its final yet). A finalised
                    // assistant entry (IsStreaming=false, left behind by a
                    // prior final chat.message or ApplyTurnEnd) belongs to a
                    // completed turn and must NOT be overwritten by a new
                    // turn's reply — doing so silently swaps an older bubble's
                    // text in place and the new reply appears nowhere (see
                    // the system.run-denied repro: user → reply → tool →
                    // tool-output → reply, where the second reply was
                    // overwriting the first instead of appending).
                    //
                    // Additionally, the reconcile path must NOT cross
                    // non-Assistant entries (tool calls, tool output, etc.):
                    // if any such entry has been appended after the
                    // candidate, the candidate belongs to a closed intra-turn
                    // segment. The next chat.message in the same turn is a
                    // NEW block (e.g. text₁ → tool → text₂ within one turn)
                    // and must create a new bubble — otherwise text₂ silently
                    // merges into the text₁ bubble and the two-bubble split
                    // visible in the HTML UI disappears.
                    //
                    // The byte-equal duplicate safety net is ALSO gated by
                    // !hasInterveningEntries: identical short replies
                    // ("Done.", "OK.") across a tool entry are legitimately
                    // distinct assistant turns and must NOT collapse back
                    // into one bubble — that would reintroduce the symptom
                    // this PR fixes for the exact-match case.
                    var hasInterveningEntries = (li < state.Entries.Count - 1);
                    var shouldMerge = !hasInterveningEntries &&
                        (string.Equals(candidate.Text, text, StringComparison.Ordinal)
                         || (reconcilePrevious && candidate.IsStreaming));
                    if (!shouldMerge)
                        break;

                    // The incoming text may be CUMULATIVE-for-the-turn (gateway
                    // re-emits all-blocks-so-far in each chat.message frame).
                    // When reconciling into a still-streaming assistant bubble
                    // whose stored text is already the STRIPPED tail (because
                    // earlier fast-path SetItems stripped prior segments), we
                    // must apply the same strip here — otherwise we silently
                    // overwrite the bubble with the full cumulative, re-
                    // introducing every earlier intra-turn segment as a prefix
                    // on this bubble (live repro: B3 appears correctly
                    // initially, then later cumulative frames that arrive
                    // after ActiveAssistantId has been cleared by
                    // ApplyTurnEnd come through this reconcile path and
                    // prepend B1+B2's content to B3 — see the
                    // IntraTurnLateReconcile_AfterTurnEnd_DoesNotPrependPriorSegments
                    // test).
                    var mergedText = string.Equals(candidate.Text, text, StringComparison.Ordinal)
                        ? text
                        : StripCumulativeTurnPrefix(state.Entries, text, excludeIndex: li);

                    // Duplicate-resend / gateway-regression guard: if the
                    // incoming cumulative is fully consumed by the OTHER
                    // priors in this turn (strip returned ""), the frame
                    // carries no new content for the candidate — treat as
                    // no-op rather than blanking the bubble. Mirrors the
                    // symmetric guard on the new-bubble path below.
                    if (!string.IsNullOrEmpty(text) && string.IsNullOrEmpty(mergedText))
                        return state;

                    return state with
                    {
                        Entries = state.Entries.SetItem(li, candidate with
                        {
                            Text = mergedText,
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

        // The text we received may be CUMULATIVE-for-the-turn (the gateway's
        // chat.message events carry block-streamed content where every frame
        // includes all prior blocks in this turn — see the comment in
        // OpenClawChatDataProvider.OnChatMessageReceived). When we fall
        // through to "create a new bubble" because a tool call was appended
        // mid-turn, naïvely persisting the cumulative text would duplicate
        // the previous assistant bubble's content as a prefix on the new
        // bubble. Strip that prefix so the new bubble shows only the new
        // segment.
        var effectiveText = replace
            ? StripCumulativeTurnPrefix(state.Entries, text, excludeIndex: -1)
            : text;

        // DUPLICATE-RESEND: when the gateway re-emits a final chat.message
        // whose cumulative text is identical to (or fully consumed by) the
        // already-rendered prior bubbles, StripCumulativeTurnPrefix returns
        // an empty string. The !hasInterveningEntries guard above
        // intentionally blocks the scan-back byte-equal merge from
        // collapsing it in place; rather than appending an empty new
        // bubble, treat the event as a no-op resend of an existing
        // bubble and leave the timeline unchanged.
        if (replace && !string.IsNullOrEmpty(text) && string.IsNullOrEmpty(effectiveText))
        {
            return state;
        }

        var id = $"e{state.NextId}";
        return state with
        {
            Entries = state.Entries.Add(new(id, ChatTimelineItemKind.Assistant, effectiveText, IsStreaming: streaming)),
            NextId = state.NextId + 1,
            // INVARIANT: ActiveAssistantId is only ever assigned here — it is
            // always the id of the most recently *created* Assistant entry.
            // Do NOT reassign it from the scan-back merge target above; the
            // frontier gate at the top of this method relies on this invariant
            // to detect when the active assistant is no longer the timeline
            // frontier.
            ActiveAssistantId = id
        };
    }

    /// <summary>
    /// Concatenates <paramref name="delta"/> onto <paramref name="existing"/>
    /// after stripping the largest leading portion of the delta that is
    /// already present as a suffix of the existing text. This collapses
    /// duplicate-tail emissions where the gateway sends an
    /// agent.message.delta carrying content the prior chat.message already
    /// committed — e.g. existing = "...mounted here.", delta =
    /// " D: is mounted here." — without the dedup the bubble would render
    /// "...mounted here. D: is mounted here.".
    ///
    /// A minimum overlap of <see cref="MinOverlapForDedup"/> characters is
    /// required, OR the entire delta must be present as a suffix. This
    /// avoids collapsing natural single-character boundaries between
    /// adjacent streaming tokens ("Hel" + "lo " must not become "Helo ").
    /// </summary>
    const int MinOverlapForDedup = 8;

    static string AppendWithOverlap(string existing, string delta)
    {
        if (string.IsNullOrEmpty(delta)) return existing;
        if (string.IsNullOrEmpty(existing)) return delta;
        var max = Math.Min(existing.Length, delta.Length);
        for (var k = max; k > 0; k--)
        {
            // Only consider overlaps that are either substantial (>= 8 chars)
            // or fully consume the delta (delta is entirely redundant).
            if (k < MinOverlapForDedup && k < delta.Length)
                break;
            var match = true;
            for (var i = 0; i < k; i++)
            {
                if (existing[existing.Length - k + i] != delta[i])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return existing + delta.Substring(k);
        }
        return existing + delta;
    }

    /// <summary>
    /// Walks back through <paramref name="entries"/> from the end (skipping the
    /// entry at <paramref name="excludeIndex"/> if non-negative) looking for
    /// the most recent Assistant entry within the current turn (stops at User).
    /// If found, strips the longest common prefix between that entry's text
    /// and <paramref name="cumulativeText"/>. Returns the original text when
    /// no prior assistant is found in the turn or when the LCP is too short
    /// to be confident it represents a genuine cumulative-for-turn prefix.
    ///
    /// Used by both the fast-path replace and the new-bubble create paths so
    /// the same cumulative-prefix-strip rule applies regardless of which
    /// branch UpsertAssistant takes. Longest-common-prefix (rather than a
    /// plain StartsWith) is used because a prior bubble's stored text may
    /// have been mutated by a late `agent.message.delta` append; the LCP
    /// still matches everything up to the divergence point.
    ///
    /// CALLER CONTRACT — <paramref name="excludeIndex"/>: when this strip is
    /// applied to compute the merged text for an EXISTING assistant entry
    /// (the scan-back-reconcile merge target), the caller MUST pass that
    /// entry's index as excludeIndex. The candidate's stored text is the
    /// already-stripped tail of its own segment; including it in the peel
    /// walk would self-strip the candidate's content out of the cumulative
    /// and return an empty string for a genuine streaming continuation.
    /// Pass -1 for the new-bubble create path where no candidate yet exists.
    /// </summary>
    static string StripCumulativeTurnPrefix(System.Collections.Immutable.ImmutableList<ChatTimelineItem> entries, string cumulativeText, int excludeIndex)
    {
        // The gateway emits cumulative text spanning the ENTIRE current
        // turn — including ALL prior post-tool assistant segments:
        //   cumulative = A1.Text + sep + A2.Text + sep + ... + An.Text + sep + new
        // The original implementation only stripped against the
        // most-recent prior assistant, which left a 3+ segment turn
        // displaying duplicated content (bubble₃ would show
        // A1.Text + A2.Text + A3.Text instead of just A3.Text).
        //
        // Iterate prior assistants in chronological order (oldest-first)
        // within the current turn and peel each in sequence. The User
        // entry marks the start of the turn.
        //
        // DUPLICATE-RESEND DETECTION: when the gateway re-emits an
        // unchanged final chat.message after appending more tool calls,
        // the scan-back byte-equal merge is blocked by
        // !hasInterveningEntries, and the new-bubble path lands here
        // with cumulative equal to (A1.Text + sep + ... + An.Text)
        // exactly. The walk peels every prior, including the last one,
        // and returns the empty string. The caller treats a non-empty
        // input that strips to empty as "duplicate of a prior bubble"
        // and skips creating a new bubble.
        var turnStart = 0;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Kind == ChatTimelineItemKind.User)
            {
                turnStart = i + 1;
                break;
            }
        }

        var remaining = cumulativeText;
        var anyStripped = false;
        for (var i = turnStart; i < entries.Count; i++)
        {
            if (i == excludeIndex) continue;
            var prior = entries[i];
            if (prior.Kind != ChatTimelineItemKind.Assistant) continue;
            if (string.IsNullOrEmpty(prior.Text)) continue;

            var lcp = 0;
            var max = Math.Min(remaining.Length, prior.Text.Length);
            while (lcp < max && remaining[lcp] == prior.Text[lcp]) lcp++;

            // Require the common prefix to cover at least half of the
            // prior bubble (allowing some late-append divergence) and be
            // substantial in absolute terms to avoid false positives on
            // short shared openings (e.g. "Let me "). lcp == remaining.Length
            // is allowed: that's the duplicate-resend case described above.
            if (lcp >= 8 && lcp * 2 >= prior.Text.Length)
            {
                // Strip the prior text and the boundary newline(s)
                // introduced by cumulative framing — but NOT
                // spaces/tabs, which may be legitimate indentation in
                // the post-tool segment (code blocks, nested lists,
                // ASCII art).
                remaining = remaining.Substring(lcp);
                var trimAt = 0;
                while (trimAt < remaining.Length &&
                       (remaining[trimAt] == '\r' || remaining[trimAt] == '\n'))
                {
                    trimAt++;
                }
                if (trimAt > 0) remaining = remaining.Substring(trimAt);
                anyStripped = true;
                continue;
            }

            // This prior didn't match at the front of remaining. If
            // we've already stripped at least one ancestor the cumulative
            // prefix is exhausted — stop. Otherwise this assistant may
            // simply not have been included in the cumulative (or was
            // mutated post-strip); skip and try the next chronological
            // assistant in the turn.
            if (anyStripped) break;
        }
        return remaining;
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

        // Demote assistant entries that became "closed segments" mid-turn —
        // i.e. an assistant entry that is no longer the most recent assistant
        // because the model continued with a tool call (and possibly more
        // text) afterwards. Without this, the abandoned pre-tool bubble keeps
        // IsStreaming=true after the UpsertAssistant frontier-gate fix and
        // shows a typing indicator past the turn boundary.
        //
        // We deliberately leave the LAST assistant entry's IsStreaming flag
        // alone: a turn may end with a delta-only preview that never received
        // its final frame, and a late ChatMessageEvent(reconcile=true) is
        // still expected to collapse into it (see the
        // FinalAssistant_UpdatesStreamingAssistantAfterTurnEnd contract).
        // That late-final reconcile is gated on `IsStreaming=true` in
        // UpsertAssistant's scan-back block, so demoting the frontier would
        // silently break it.
        // Preserve IsStreaming ONLY on the last Assistant entry AND only
        // when it is also the last entry overall (i.e., no Tool sits after
        // it). The late cross-lifecycle final-reconcile contract
        // (FinalAssistant_UpdatesStreamingAssistantAfterTurnEnd) requires
        // the streaming flag to remain set so a delayed reconcile can
        // collapse into it — but if there's a Tool between this Assistant
        // and the timeline tail, the scan-back's hasInterveningEntries
        // gate will block any merge anyway, so preserving the flag would
        // leave a stranded typing indicator on an orphaned pre-tool bubble.
        var lastAssistantIdx = -1;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Kind == ChatTimelineItemKind.Assistant)
            {
                lastAssistantIdx = i;
                break;
            }
        }
        var preserveIdx = (lastAssistantIdx == entries.Count - 1) ? lastAssistantIdx : -1;
        for (var i = 0; i < entries.Count; i++)
        {
            if (i == preserveIdx) continue;
            if (entries[i].Kind == ChatTimelineItemKind.Assistant && entries[i].IsStreaming)
            {
                entries = entries.SetItem(i, entries[i] with { IsStreaming = false });
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
