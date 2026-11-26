// Hub adapters between chat state and the ported visit logic. They map chat threads onto the upstream session rows
// that decide busy and offline modes and run reactions, derive each composer opening's seed, and decide when the
// new-session composer keeps its visitor. Pure functions, no WinUI, so the Tray tests link this file directly.
//
// Usage:
//   var runs = LobsterComposerVisits.Runs(snapshot.Threads);
//   var mode = LobsterVisitPlans.ResolveMode(connected: !offlineStable, runs);
//   var seed = LobsterComposerVisits.Seed(opening, thread.Id);
//   (show, sticky) = LobsterComposerVisits.ShowVisitor(sticky, inputs.ShowPlacementPickers, thread.Id,
//       inputs.PlacementTargetIsEmpty);
using OpenClaw.Chat;

namespace OpenClawTray.Chat.LobsterVisits;

internal static class LobsterComposerVisits
{
    /// <summary>
    /// Upstream reads every row of <c>sessions.list</c>: any live run makes the visitor busy, and the most recently
    /// ended terminal run picks its reaction. The Hub's chat threads carry the same Gateway fields.
    /// </summary>
    internal static IReadOnlyList<LobsterSessionRun> Runs(IEnumerable<ChatThread>? threads) =>
        threads?.Select(thread => new LobsterSessionRun(
            thread.RunStatus,
            thread.HasActiveRun,
            thread.EndedAt,
            UpdatedAt: thread.UpdatedAt?.ToUnixTimeMilliseconds())).ToArray() ?? [];

    /// <summary>
    /// Upstream seeds a composer opening from a random draw and the draft owner, salted per page load. The Hub draws
    /// <paramref name="opening"/> once per visitor mount and keys it with the session.
    /// </summary>
    internal static uint Seed(string opening, string threadId) =>
        LobsterLooks.Seed($"{opening}:{threadId}", LobsterLooks.LoadSalt);

    /// <summary>
    /// Whether the composer shows a visitor, and the session it stays sticky for. The web composer stays mounted
    /// while the Gateway is away, so an empty draft's visitor can pace offline. The Hub only draws its new-session
    /// chrome once that session's history is current, so the visitor stays for the session that last showed it while
    /// that session remains empty, through disconnects and reconnects. Switching sessions or sending ends it. Compact
    /// composers never show new-session chrome, so they never host a visitor.
    /// </summary>
    internal static (bool Show, string? Sticky) ShowVisitor(
        string? sticky, bool newSessionComposer, string threadId, bool targetIsEmpty)
    {
        if (newSessionComposer)
            return (true, threadId);
        return targetIsEmpty && string.Equals(sticky, threadId, StringComparison.Ordinal)
            ? (true, sticky)
            : (false, null);
    }
}
