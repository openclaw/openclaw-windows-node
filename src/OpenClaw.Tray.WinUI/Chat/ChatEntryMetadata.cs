namespace OpenClawTray.Chat;

/// <summary>
/// Per-entry metadata maintained by <see cref="OpenClawChatDataProvider"/>
/// in parallel to the vendored <see cref="OpenClaw.Chat.ChatTimelineItem"/>.
/// Tracks values that the upstream <c>ChatTimelineItem</c> record doesn't
/// carry — specifically the wall-clock timestamp of when the entry was
/// created, the model active at that moment, and gateway-reported usage
/// counters — so the timeline renderer can show a richer footer like
/// <c>Field · 7:54 PM · ↑1475 ↓12 R45.4k 23% ctx · gpt-5.5</c>.
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
/// Cumulative input (prompt) tokens reported by the gateway for this turn,
/// shown in the footer with an up arrow (<c>↑</c>). <c>null</c> when not
/// reported (most live ``chat`` deltas don't carry usage info — only the
/// final summary does).
/// </param>
/// <param name="OutputTokens">
/// Cumulative output tokens reported by the gateway for this turn, shown in
/// the footer with a down arrow (<c>↓</c>).
/// </param>
/// <param name="ResponseTokens">
/// Total tokens spent on the response (prompt + completion) — surfaces as
/// <c>R&lt;n&gt;</c> in the footer (e.g. <c>R45.4k</c>).
/// </param>
/// <param name="ContextPercent">
/// Percentage of the model's context window consumed by the conversation
/// when this entry was generated (0–100). Shown as <c>23% ctx</c>.
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
public sealed record ChatEntryMetadata(
    DateTimeOffset? Timestamp,
    string? Model,
    int? InputTokens = null,
    int? OutputTokens = null,
    int? ResponseTokens = null,
    int? ContextPercent = null,
    long? ContextTokens = null,
    int? UsageContributionTokens = null,
    string? GatewayMessageId = null,
    int? OpenClawSeq = null,
    string? OpenClawKind = null,
    long? CompactionTokensBefore = null,
    long? CompactionTokensAfter = null,
    bool IsLocalQueuedSend = false,
    string? LocalQueuedMessageId = null);
