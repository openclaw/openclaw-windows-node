using OpenClawTray.Chat.LobsterVisits;

namespace OpenClaw.Tray.Tests;

public sealed class LobsterMotionTests
{
    [Fact]
    public void TrackValues_ApplyEasingPerSegmentAndHoldDuplicateOffsets()
    {
        // The player samples rotateY tracks through ValueAt, so these CSS semantics reach the screen.
        var track = new LobsterMotionTrack(LobsterMotionProperty.TranslateY,
            [new(0, 0), new(.5, 10), new(.5, 20), new(.6, 20), new(1, 40)]);

        Assert.Equal(5, track.ValueAt(.25, LobsterEasing.Linear), 6);
        // Later keyframe at a duplicated offset wins from that offset on, then holds until 60%.
        Assert.Equal(20, track.ValueAt(.5, LobsterEasing.Linear), 6);
        Assert.Equal(20, track.ValueAt(.55, LobsterEasing.Linear), 6);
        Assert.Equal(30, track.ValueAt(.8, LobsterEasing.Linear), 6);

        // ease-in at segment midpoint: y(0.5) of cubic-bezier(.42,0,1,1) is about 0.3153.
        Assert.Equal(10 * .3153, track.ValueAt(.25, LobsterEasing.EaseIn), 2);
        Assert.Equal(20 + 20 * .3153, track.ValueAt(.8, LobsterEasing.EaseIn), 2);
    }
    [Fact]
    public void ActDelay_ShiftsTheBodyButNotTheDecorations()
    {
        Assert.All(LobsterMotion.ForAct(LobsterAct.Hop, 200), a => Assert.Equal(200, a.DelayMs));
        Assert.All(LobsterMotion.ForAct(LobsterAct.Nap, 200), a => Assert.Equal(0, a.DelayMs));
    }

    [Fact]
    public void FiniteActs_EndWithinTheirPlannedDuration()
    {
        // Upstream's CSS outlasts the act timer in a few places: the startle claw snip (2 x .38s = 760ms vs 750ms),
        // and the bubble act (1.6s + 2 x .42s stagger = 2440ms of 2600ms is inside). Infinite loops (scuttle waddle,
        // nap z's) are cut by the act timer.
        foreach (var act in Enum.GetValues<LobsterAct>())
        {
            var planned = LobsterVisitPlans.ActDurationMs(act);
            var lastIndex = act == LobsterAct.Bubble ? 2 : 0;
            var finite = LobsterMotion.ForAct(act).Where(a => !a.IsInfinite).ToArray();
            if (finite.Length == 0)
                continue;
            var end = finite.Max(a => a.DelayFor(lastIndex) + a.ActiveMs);
            if (act == LobsterAct.Startle)
                Assert.Equal(planned + 10, end);
            else if (act is LobsterAct.Bubble or LobsterAct.Pet)
                Assert.True(end <= planned, $"{act}: {end} > {planned}");
            else
                Assert.Equal(planned, end);
        }
    }

    [Fact]
    public void Entrances_MatchPlannedDurations()
    {
        foreach (var entrance in Enum.GetValues<LobsterEntrance>())
            Assert.Equal(LobsterVisitPlans.EntranceMs(entrance), LobsterMotion.ForEntrance(entrance).Max(a => a.ActiveMs));
    }
}
