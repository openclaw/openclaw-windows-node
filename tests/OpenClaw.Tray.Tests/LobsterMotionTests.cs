using OpenClawTray.Chat.LobsterVisits;

namespace OpenClaw.Tray.Tests;

public sealed class LobsterMotionTests
{
    private static LobsterAnimation Synthetic(LobsterEasing easing, double iterations = 1, LobsterFill fill = LobsterFill.None) =>
        new("t", LobsterMotionTarget.Body, 1000, easing,
            [new(LobsterMotionProperty.TranslateY,
                [new(0, 0), new(.5, 10), new(.5, 20), new(.6, 20), new(1, 40)])],
            iterations, DelayMs: 100, Fill: fill);

    private static double Y(LobsterAnimation a, double t, int index = 0) =>
        a.Sample(t, index)!.Get(LobsterMotionProperty.TranslateY);

    [Fact]
    public void Sampler_AppliesEasingPerSegmentAndHoldsDuplicateOffsets()
    {
        var linear = Synthetic(LobsterEasing.Linear);
        Assert.Equal(5, Y(linear, 100 + 250), 6);
        // Later keyframe at a duplicated offset wins from that offset on, then holds until 60%.
        Assert.Equal(20, Y(linear, 100 + 500), 6);
        Assert.Equal(20, Y(linear, 100 + 550), 6);
        Assert.Equal(30, Y(linear, 100 + 800), 6);

        // ease-in at segment midpoint: y(0.5) of cubic-bezier(.42,0,1,1) is about 0.3153.
        var easeIn = Synthetic(LobsterEasing.EaseIn);
        Assert.Equal(10 * .3153, Y(easeIn, 100 + 250), 2);
        Assert.Equal(20 + 20 * .3153, Y(easeIn, 100 + 800), 2);
    }

    [Fact]
    public void NapZs_AreStaggeredBySevenHundredMilliseconds()
    {
        var z = LobsterMotion.ForAct(LobsterAct.Nap).Single();

        Assert.Null(z.Sample(500, index: 1));
        Assert.Equal(z.Sample(300, 0)!.Get(LobsterMotionProperty.Opacity), z.Sample(1000, 1)!.Get(LobsterMotionProperty.Opacity), 9);
        Assert.Equal(z.Sample(300, 0)!.Get(LobsterMotionProperty.Opacity), z.Sample(1700, 2)!.Get(LobsterMotionProperty.Opacity), 9);
    }

    [Fact]
    public void Breathe_NegativeDelayStartsMidCycle()
    {
        var breathe = LobsterMotion.Breathe(-1.7);

        Assert.Equal(.96, breathe.Sample(0)!.Get(LobsterMotionProperty.ScaleY), 6);
    }

    [Fact]
    public void Wave_RaisesTheRightClawAtTwentyPercent()
    {
        var pose = LobsterMotion.ForAct(LobsterAct.Wave).Single().Sample(280)!;

        Assert.Equal(-30, pose.Get(LobsterMotionProperty.Rotate), 6);
        Assert.Equal(-6, pose.Get(LobsterMotionProperty.TranslateY), 6);
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
