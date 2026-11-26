// Visit plans for composer lobsters: acts and their durations, personalities, the visit schedule, familiarity
// tuning, and how Gateway and session state become a visit mode or a run-finish reaction. A faithful port from
// OpenClaw's Control UI at 4cffd43ee043275f6a4c693aaacee8d8e5a61288 (MIT; see THIRD_PARTY_NOTICES.md):
// ui/src/components/lobster-pet-plans.ts, lobster-pet-contract.ts, lobster-dex.ts (familiarity) and
// src/shared/session-run-state.ts.
//
// Usage:
//   var mode = LobsterVisitPlans.ResolveMode(!offline.OfflineStable, runs);
//   var outcome = LobsterVisitPlans.ResolveOutcome(runs);
//   var profile = LobsterVisitPlans.ResolveActProfile(mode, look.Personality, DateTime.Now);
namespace OpenClawTray.Chat.LobsterVisits;

internal enum LobsterVisitMode { Idle, Busy, Offline }

internal enum LobsterRunOutcome { Ok, Error, Aborted }

internal enum LobsterAct { Wave, Snip, Hop, Spin, Peek, Nap, Bubble, Scuttle, Startle, Cheer, Pet, Droop, Sweep }

/// <summary>How an arriving lobster gets onto the ledge; "walk" is the classic pop-up from behind it.</summary>
internal enum LobsterEntrance { Walk, Balloon, Bubble }

internal enum LobsterFamiliarityTier { Shy, Regular, Friend }

/// <summary>Lifetime visit and shoo counts, read once per composer opening.</summary>
internal sealed record LobsterFamiliarity(int Visits, int Shoos)
{
    internal static readonly LobsterFamiliarity New = new(0, 0);

    internal LobsterFamiliarityTier Tier => Visits < 3 ? LobsterFamiliarityTier.Shy
        : Visits < 15 ? LobsterFamiliarityTier.Regular
        : LobsterFamiliarityTier.Friend;

    /// <summary>Frequent shooing makes visitors keep their distance.</summary>
    internal bool Wary => Shoos >= 3 && Shoos > Visits * 0.3;
}

/// <summary>Visit-length multipliers per tier: shy pets arrive late and leave early; friends linger.</summary>
internal sealed record LobsterFamiliarityTuning(double StayMultiplier, double FirstDelayMultiplier, double GapMultiplier)
{
    internal const double WaryGapMultiplier = 1.7;

    internal static LobsterFamiliarityTuning For(LobsterFamiliarityTier tier) => tier switch
    {
        LobsterFamiliarityTier.Shy => new(.6, 1.3, 1),
        LobsterFamiliarityTier.Friend => new(1.6, .7, .8),
        _ => new(1, 1, 1),
    };
}

/// <summary>
/// One session row as the Gateway reports it. <see cref="Status"/> carries the raw Gateway run status
/// (<c>queued</c>, <c>running</c>, <c>done</c>, <c>failed</c>, <c>killed</c>, <c>timeout</c>).
/// </summary>
internal sealed record LobsterSessionRun(
    string? Status,
    bool? HasActiveRun = null,
    long? EndedAt = null,
    long? LastActivityAt = null,
    long? UpdatedAt = null);

internal sealed record LobsterActProfile((double Min, double Max) DelayMs, IReadOnlyList<(LobsterAct Value, double Weight)> Acts);

internal static class LobsterVisitPlans
{
    /// <summary>Half of composer openings never visit on their own; offline and long runs still summon.</summary>
    internal const double ShyChance = .5;
    internal static readonly (double Min, double Max) FirstDelayMs = (1800, 7500);
    internal static readonly (double Min, double Max) StayMs = (90_000, 300_000);
    internal static readonly (double Min, double Max) GapMs = (1_800_000, 3_600_000);

    /// <summary>A run this long turns busy fidgeting into a quiet vigil.</summary>
    internal const double VigilAfterMs = 600_000;

    internal const double LeaveMs = 350;
    internal const double PetHoldMs = 600;
    internal const double GrumpyMs = 60_000;
    internal const double PokeWindowMs = 6000;
    internal const int GrumpyPokes = 3;
    internal const int HuffPokes = 10;
    internal const double GazeThrottleMs = 120;

    /// <summary>Acts end on these clocks, which mirror the upstream CSS animation durations.</summary>
    internal static double ActDurationMs(LobsterAct act) => act switch
    {
        LobsterAct.Wave => 1400,
        LobsterAct.Snip => 1000,
        LobsterAct.Hop => 750,
        LobsterAct.Spin => 950,
        LobsterAct.Peek => 1700,
        LobsterAct.Nap => 4400,
        LobsterAct.Bubble => 2600,
        LobsterAct.Scuttle => 1250,
        LobsterAct.Startle => 750,
        LobsterAct.Cheer => 1300,
        LobsterAct.Pet => 1500,
        LobsterAct.Droop => 1600,
        LobsterAct.Sweep => 1800,
        _ => throw new ArgumentOutOfRangeException(nameof(act)),
    };

    internal static double EntranceMs(LobsterEntrance entrance) => entrance switch
    {
        LobsterEntrance.Balloon => 1250,
        LobsterEntrance.Bubble => 700,
        _ => 450,
    };

    /// <summary>Entrance rolls have their own stream so they never shift visit scheduling.</summary>
    internal static LobsterEntrance PickEntrance(double roll) =>
        roll < .06 ? LobsterEntrance.Balloon : roll < .13 ? LobsterEntrance.Bubble : LobsterEntrance.Walk;

    private static readonly LobsterActProfile Sleepy = new((6000, 12000),
    [
        (LobsterAct.Nap, 40), (LobsterAct.Bubble, 20), (LobsterAct.Wave, 12),
        (LobsterAct.Scuttle, 12), (LobsterAct.Peek, 10), (LobsterAct.Hop, 6),
    ]);

    private static readonly LobsterActProfile Zoomy = new((2800, 6000),
        [(LobsterAct.Scuttle, 42), (LobsterAct.Hop, 22), (LobsterAct.Spin, 12), (LobsterAct.Peek, 12), (LobsterAct.Wave, 12)]);

    private static readonly LobsterActProfile Friendly = new((3600, 7500),
        [(LobsterAct.Wave, 32), (LobsterAct.Snip, 22), (LobsterAct.Scuttle, 18), (LobsterAct.Hop, 14), (LobsterAct.Bubble, 14)]);

    private static readonly LobsterActProfile Showoff = new((3600, 7500),
        [(LobsterAct.Spin, 24), (LobsterAct.Snip, 22), (LobsterAct.Peek, 20), (LobsterAct.Hop, 18), (LobsterAct.Wave, 16)]);

    private static readonly LobsterActProfile Busy = new((2200, 4500),
        [(LobsterAct.Scuttle, 40), (LobsterAct.Hop, 20), (LobsterAct.Snip, 20), (LobsterAct.Wave, 12), (LobsterAct.Spin, 8)]);

    private static readonly LobsterActProfile Offline = new((2800, 5600),
        [(LobsterAct.Scuttle, 55), (LobsterAct.Peek, 30), (LobsterAct.Hop, 15)]);

    /// <summary>Busy and offline modes override personality; idle visitors yawn through the night shift.</summary>
    internal static LobsterActProfile ResolveActProfile(LobsterVisitMode mode, LobsterPersonality personality, DateTime localNow) =>
        mode switch
        {
            LobsterVisitMode.Busy => Busy,
            LobsterVisitMode.Offline => Offline,
            _ when localNow.Hour >= 22 || localNow.Hour < 6 => Sleepy,
            _ => personality switch
            {
                LobsterPersonality.Sleepy => Sleepy,
                LobsterPersonality.Zoomy => Zoomy,
                LobsterPersonality.Friendly => Friendly,
                _ => Showoff,
            },
        };

    /// <summary>Lobsters take pride in finished work and take failures seriously.</summary>
    internal static LobsterAct FinishAct(LobsterRunOutcome outcome) => outcome switch
    {
        LobsterRunOutcome.Error => LobsterAct.Droop,
        LobsterRunOutcome.Aborted => LobsterAct.Startle,
        _ => LobsterAct.Cheer,
    };

    /// <summary>
    /// Upstream <c>isSessionRunStateActive</c>: a terminal status always wins, then the Gateway's live-run
    /// flag, then a queued or running status.
    /// </summary>
    internal static bool IsRunActive(LobsterSessionRun run)
    {
        if (!string.IsNullOrEmpty(run.Status) && run.Status is not ("queued" or "running"))
            return false;
        return run.HasActiveRun ?? run.Status is "queued" or "running";
    }

    /// <summary>Upstream <c>resolveLobsterPetMode</c>; pass the stable offline signal, not raw socket state.</summary>
    internal static LobsterVisitMode ResolveMode(bool connected, IEnumerable<LobsterSessionRun>? runs) =>
        !connected ? LobsterVisitMode.Offline
            : runs?.Any(IsRunActive) == true ? LobsterVisitMode.Busy
            : LobsterVisitMode.Idle;

    /// <summary>
    /// Upstream <c>resolveLobsterRunOutcome</c>: the most recently ended terminal run decides the reaction.
    /// <c>EndedAt</c> outranks activity stamps that also move on unrelated events.
    /// </summary>
    internal static LobsterRunOutcome ResolveOutcome(IEnumerable<LobsterSessionRun>? runs)
    {
        (long At, LobsterRunOutcome Outcome)? latest = null;
        foreach (var run in runs ?? [])
        {
            if (string.IsNullOrEmpty(run.Status) || run.Status == "running")
                continue;
            var at = run.EndedAt ?? run.LastActivityAt ?? run.UpdatedAt ?? 0;
            if (latest is { } current && at <= current.At)
                continue;
            latest = (at, run.Status switch
            {
                "failed" or "timeout" => LobsterRunOutcome.Error,
                "killed" => LobsterRunOutcome.Aborted,
                _ => LobsterRunOutcome.Ok,
            });
        }
        return latest?.Outcome ?? LobsterRunOutcome.Ok;
    }
}
