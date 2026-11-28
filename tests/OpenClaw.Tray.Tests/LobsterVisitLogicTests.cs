using OpenClawTray.Chat.LobsterVisits;
using static System.FormattableString;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Parity for lobster identity, colors, names, collision lanes and visit plans. The Upstream* tables in
/// LobsterVisitGoldens.g.cs come from executing the pinned upstream modules
/// (ui/src/components/lobster-pet-{identity,scene,plans}.ts) under Node through
/// scripts/Update-LobsterVisitGoldens.ps1. The remaining rows reproduce upstream's own unit-test tables where
/// noted. No expected value comes from this port.
/// </summary>
public sealed partial class LobsterVisitLogicTests
{
    private static readonly DateTime Neutral = new(2026, 7, 15, 12, 0, 0);

    [Theory]
    [MemberData(nameof(UpstreamRandom))]
    public void Mulberry32_MatchesUpstreamSequence(uint seed, double[] expected)
    {
        var rng = LobsterRandom.Mulberry32(seed);
        Assert.Equal(expected, expected.Select(_ => rng()));
    }

    // seed | palette | scale | accessory | antennae | spot | facing | personality | blink | claws | tail fan |
    // shiny | crusher | freckles | glint | chimera body/left/right/antennae | lobsterLookStyle | name
    [Theory]
    [MemberData(nameof(UpstreamLooks))]
    public void CreateLook_RollsTheUpstreamLookColorsAndName(string row)
    {
        var expected = row.Split(" | ");
        var seed = uint.Parse(expected[0]);
        var look = LobsterLooks.Create(seed, Neutral);
        var colors = LobsterLooks.Colors(look);
        var chimera = look.ChimeraParts is { } parts
            ? $"{parts.Body}/{parts.ClawLeft}/{parts.ClawRight}/{parts.Antennae}"
            : "-";
        // Blink and breathe delays are renderer timing, so the style column keeps only paint and claw scale.
        var style = string.Join(";", new[]
        {
            $"--lob-shell:{colors.Shell}",
            $"--lob-claw:{colors.Claw}",
            Invariant($"--lob-claw-l:{colors.ClawLeftScale}"),
            Invariant($"--lob-claw-r:{colors.ClawRightScale}"),
            colors.ClawLeft is { } left ? $"--lob-chimera-l:{left}" : null,
            colors.ClawRight is { } right ? $"--lob-chimera-r:{right}" : null,
            colors.Antennae is { } antennae ? $"--lob-antennae-color:{antennae}" : null,
            colors.Glint is { } glint ? $"--lob-glint-seed:{glint}" : null,
        }.OfType<string>());
        var actual = string.Join(" | ",
            Invariant($"{seed} | {look.Palette.Id} | {look.Scale} | {Lower(look.Accessory)} | {Lower(look.Antennae)}"),
            Invariant($"{look.SpotPct} | {look.Facing} | {Lower(look.Personality)} | {look.BlinkDelaySeconds} | {Lower(look.ClawSize)}"),
            look.TailFan ? 1 : 0, look.Shiny ? 1 : 0, look.CrusherSide is { } side ? Lower(side) : "-", look.Freckles ? 1 : 0,
            look.Glint ?? "-", chimera, style, LobsterLooks.Name(look, seed));
        Assert.Equal(row, actual);
    }

    // Accessory sets over seeds 0..399 per date, and seed 7's dress.
    [Theory]
    [MemberData(nameof(UpstreamWardrobe))]
    public void SeasonalWardrobe_FollowsTheLocalDate(int year, int month, int day, string accessories, string seven)
    {
        var date = new DateTime(year, month, day, 12, 0, 0);
        var seen = Enumerable.Range(0, 400)
            .Select(seed => Lower(LobsterLooks.Create((uint)seed, date).Accessory))
            .Distinct().Order(StringComparer.Ordinal);
        Assert.Equal(accessories, string.Join(",", seen));
        var look = LobsterLooks.Create(7, date);
        Assert.Equal(seven, $"{look.Palette.Id} {Lower(look.Accessory)}");
        Assert.Null(look.ChimeraParts);
    }

    // Upstream lobster-dex.test.ts milestones; the shiny sparkle comes from the scene view's hover title.
    [Theory]
    [InlineData(7u, 0, "Pinchy")]
    [InlineData(7u, 49, "Pinchy")]
    [InlineData(7u, 50, "Sir Pinchy")]
    [InlineData(7u, 99, "Sir Pinchy")]
    [InlineData(7u, 100, "Captain Pinchy")]
    [InlineData(7u, 250, "Elder Pinchy")]
    [InlineData(7u, 9001, "Elder Pinchy")]
    [InlineData(235u, 50, "\u2726 Sir Goldie")]
    public void Title_AddsTheShinySparkleAndLoadStartHonorific(uint seed, int visits, string expected) =>
        Assert.Equal(expected, LobsterLooks.Title(LobsterLooks.Create(seed, Neutral), seed, visits));

    private static LobsterBox Box(double l, double t, double r, double b) => new(l, t, r, b);

    private static readonly LobsterBox[] HubLikeControls =
        [Box(116, 384, 148, 408), Box(152, 384, 184, 408), Box(560, 384, 760, 408), Box(800, 384, 832, 408), Box(840, 382, 876, 410)];

    // Composer, footer and editor boxes mirror the Hub's dense composer; the editor's bottom edge decides the floor.
    [Theory]
    [MemberData(nameof(UpstreamLanes))]
    public void Scene_MatchesUpstreamLanes(double editorBottom, bool twins, bool pickersAbove, string top, string floor, string passage)
    {
        var scene = LobsterSceneResolver.Resolve(Box(100, 300, 900, 420), Box(108, 380, 892, 412), HubLikeControls,
            pickersAbove ? [Box(100, 240, 580, 272)] : [], Box(116, 312, 884, editorBottom), 400, twins);

        Assert.Equal(top, Lane(scene.Top));
        Assert.Equal(floor, Lane(scene.Floor));
        Assert.Equal(passage, scene.Passage is { } gap ? Invariant($"{gap.Start}-{gap.End}") : "none");
    }

    // Upstream's narrow-footer rule: no lanes rather than a guessed strip across the controls.
    [Fact]
    public void Scene_NarrowComposerUnderAFullWidthObstacleHasNoLanes()
    {
        var scene = LobsterSceneResolver.Resolve(Box(0, 100, 320, 200), Box(8, 160, 312, 192),
            [Box(16, 164, 48, 188), Box(52, 164, 84, 188), Box(100, 164, 260, 188), Box(270, 162, 306, 190)],
            [Box(0, 40, 300, 72)], Box(16, 112, 304, 150), 200);

        Assert.Equal(LobsterScene.Empty, scene);
    }

    private static readonly LobsterScene Roomy =
        new(new LobsterLane(66, 734, 0), new LobsterLane(150, 394, 107), (366, 394));

    // anchor spot facing from to hop duration, from upstream planWalk/planHop/lobsterTravelDuration; "null"
    // when the move needs a lane the scene lacks.
    [Theory]
    [MemberData(nameof(UpstreamMoves))]
    public void Moves_MatchUpstreamPlans(string sceneName, string kind, string anchor, double spot, double roll, string expected)
    {
        var scene = sceneName == "roomy" ? Roomy : new LobsterScene(Roomy.Top, null, null);
        var lane = Enum.Parse<LobsterAnchor>(anchor, ignoreCase: true);
        var move = kind == "walk"
            ? LobsterSceneResolver.PlanWalk(scene, lane, spot, roll)
            : LobsterSceneResolver.PlanHop(scene, lane, spot);

        var actual = move is { Travel: var t }
            ? Invariant(
                $"{Lower(move.Anchor)} {move.SpotPct:R} {move.Facing} {t.From.X:R},{t.From.Y:R} {t.To.X:R},{t.To.Y:R} {t.Hop} {t.DurationMs:R}")
            : "null";
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(UpstreamEntrances))]
    public void Entrance_RollsIntoRarityBands(double roll, string expected) =>
        Assert.Equal(expected, Lower(LobsterVisitPlans.PickEntrance(roll)));

    // Upstream lobster-pet.test.ts "connection and run outcomes" tables.
    [Fact]
    public void Mode_FollowsConnectionThenLiveRuns()
    {
        Assert.Equal(LobsterVisitMode.Offline, LobsterVisitPlans.ResolveMode(false, [new(null, HasActiveRun: true)]));
        Assert.Equal(LobsterVisitMode.Idle, LobsterVisitPlans.ResolveMode(true, null));
        Assert.Equal(LobsterVisitMode.Busy,
            LobsterVisitPlans.ResolveMode(true, [new(null, HasActiveRun: false), new(null, HasActiveRun: true)]));
        // A terminal status outranks a stale live-run flag; queued counts as active without one.
        Assert.Equal(LobsterVisitMode.Idle, LobsterVisitPlans.ResolveMode(true, [new("done", HasActiveRun: true)]));
        Assert.Equal(LobsterVisitMode.Busy, LobsterVisitPlans.ResolveMode(true, [new("queued")]));
    }

    private static readonly (LobsterSessionRun[]? Runs, LobsterRunOutcome Expected)[] Outcomes =
    [
        (null, LobsterRunOutcome.Ok),
        ([new("done", LastActivityAt: 10), new("failed", LastActivityAt: 20)], LobsterRunOutcome.Error),
        ([new("failed", LastActivityAt: 10), new("done", LastActivityAt: 20)], LobsterRunOutcome.Ok),
        ([new("running", LastActivityAt: 99)], LobsterRunOutcome.Ok),
        ([new("timeout", UpdatedAt: 5)], LobsterRunOutcome.Error),
        ([new("killed", EndedAt: 50)], LobsterRunOutcome.Aborted),
        ([new("failed", EndedAt: 30, LastActivityAt: 10), new("done", EndedAt: 20, LastActivityAt: 40)], LobsterRunOutcome.Error),
    ];

    [Fact]
    public void Outcome_UsesTheMostRecentlyEndedRun()
    {
        foreach (var (runs, expected) in Outcomes)
            Assert.Equal(expected, LobsterVisitPlans.ResolveOutcome(runs));
    }

    // Upstream lobster-dex.test.ts: tiers by visit count, wary after frequent shooing.
    [Theory]
    [InlineData(0, 0, "shy", false)]
    [InlineData(3, 0, "regular", false)]
    [InlineData(15, 3, "friend", false)]
    [InlineData(15, 6, "friend", true)]
    public void Familiarity_TiersAndWariness(int visits, int shoos, string tier, bool wary)
    {
        var familiarity = new LobsterFamiliarity(visits, shoos);
        Assert.Equal(tier, Lower(familiarity.Tier));
        Assert.Equal(wary, familiarity.Wary);
    }

    [Fact]
    public void ActProfile_SendsIdleVisitorsToNapAtNight()
    {
        var night = LobsterVisitPlans.ResolveActProfile(LobsterVisitMode.Idle, LobsterPersonality.Zoomy, new DateTime(2026, 7, 15, 23, 0, 0));
        var day = LobsterVisitPlans.ResolveActProfile(LobsterVisitMode.Idle, LobsterPersonality.Zoomy, Neutral);
        var busyNight = LobsterVisitPlans.ResolveActProfile(LobsterVisitMode.Busy, LobsterPersonality.Zoomy, new DateTime(2026, 7, 15, 23, 0, 0));

        Assert.Equal(LobsterAct.Nap, night.Acts[0].Value);
        Assert.Equal(LobsterAct.Scuttle, day.Acts[0].Value);
        Assert.Equal((2200d, 4500d), busyNight.DelayMs);
    }

    private static string Lane(LobsterLane? lane) => lane is null ? "none" : Invariant($"x{lane.Start}-{lane.End}/y{lane.Y}");

    private static string Lower<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();
}
