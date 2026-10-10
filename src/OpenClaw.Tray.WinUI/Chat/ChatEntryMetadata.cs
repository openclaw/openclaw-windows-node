namespace OpenClawTray.Chat;

/// <summary>
/// Per-entry metadata maintained by <see cref="OpenClawChatDataProvider"/>
/// in parallel to the vendored <see cref="OpenClaw.Chat.ChatTimelineItem"/>.
/// Tracks values that the upstream <c>ChatTimelineItem</c> record doesn't
/// carry — specifically the wall-clock timestamp of when the entry was
/// created, the model active at that moment, and gateway-reported usage
/// counters — so the timeline renderer can show a richer footer like
/// <c>7:54 PM · gpt-5.5 · 45.4K/200.0K (23%) · $0.225</c>.
/// </summary>
/// <param name="Timestamp">
/// Local-time timestamp of when the entry was created. <c>null</c> when the
/// source event didn't carry a timestamp (e.g. live UI-only status entries).
/// </param>
/// <param name="Model">
/// Snapshot of the model name active when the entry was created (typically
/// taken from <see cref="OpenClaw.Shared.SessionInfo.Model"/>). <c>null</c>
/// when the model is unknown.
/// </param>
/// <param name="InputTokens">
/// Cumulative input (prompt) tokens reported by the gateway for this turn.
/// <c>null</c> when not reported (most live ``chat`` deltas don't carry usage
/// info — only the final summary does).
/// </param>
/// <param name="OutputTokens">
/// Cumulative output tokens reported by the gateway for this turn.
/// </param>
/// <param name="ResponseTokens">
/// Total tokens spent on the response (prompt + completion).
/// </param>
/// <param name="ContextPercent">
/// Percentage of the model's context window consumed by the conversation
/// when this entry was generated (0–100).
/// </param>
/// <param name="CostUsd">
/// Gateway-reported cost in USD for this assistant entry
/// (<c>usage.cost.total</c>), as carried on the message frame or history
/// row. <c>null</c> when the gateway reported none.
/// </param>
/// <param name="ContextTokens">
/// Total context window size captured with a session usage snapshot. Used by
/// timestamp usage placement to render <c>usage/total (%)</c> exactly.
/// </param>
/// <param name="UsageContributionTokens">
/// Raw token contribution reported for this assistant response before it was
/// converted into the displayed cumulative session snapshot.
/// </param>
/// <param name="GatewayMessageId">
/// Gateway-assigned stable message id from <c>__openclaw.id</c>, when known.
/// Used to reconcile live entries with later <c>chat.history</c> rows.
/// </param>
/// <param name="OpenClawSeq">
/// Monotonic per-session sequence from <c>__openclaw.seq</c>, when known.
/// Prefer this over timestamps for transcript ordering and dedupe.
/// </param>
/// <param name="IsLocalQueuedSend">
/// True for a locally queued user prompt promoted into the transcript before
/// gateway history has provided its stable id/sequence.
/// </param>
/// <param name="LocalQueuedMessageId">
/// Stable client-side id for a local send. Used to attach a later gateway
/// identity to the exact optimistic transcript row without text matching.
/// </param>
/// <param name="Attachments">
/// Structured attachment presentation metadata. Gateway references never carry
/// preview cache keys.
/// </param>
/// <param name="AssistantContent">
/// Renderer-safe assistant media presentation. Transport references remain
/// opaque and are never encoded into timeline text.
/// </param>
public sealed record ChatEntryMetadata(
    DateTimeOffset? Timestamp,
    string? Model,
    int? InputTokens = null,
    int? OutputTokens = null,
    int? ResponseTokens = null,
    int? ContextPercent = null,
    double? CostUsd = null,
    long? ContextTokens = null,
    int? UsageContributionTokens = null,
    string? GatewayMessageId = null,
    int? OpenClawSeq = null,
    string? OpenClawKind = null,
    long? CompactionTokensBefore = null,
    long? CompactionTokensAfter = null,
    bool IsLocalQueuedSend = false,
    string? LocalQueuedMessageId = null,
    IReadOnlyList<ChatAttachmentPresentation>? Attachments = null,
    ChatAssistantContentPresentation? AssistantContent = null);
