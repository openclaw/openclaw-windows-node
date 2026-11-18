// CSS keyframe animations for the composer lobster, ported as data with CSS semantics: per-property keyframes (a
// property missing at 0%/100% interpolates from its identity value), the animation timing function applied per segment
// between that property's keyframes, grouped offsets ("0%, 14%") as holds, iteration counts, begin delays, per-index
// stagger and fill modes. LobsterMotionPlayer turns the tables into storyboards.
// OpenClaw 4cffd43ee043275f6a4c693aaacee8d8e5a61288: ui/src/styles/lobster-pet.css (travel, entrances, acts,
// breathe, blink, vigil and away rules) and ui/src/components/lobster-pet-scene-view.tsx (MIT; see
// THIRD_PARTY_NOTICES.md).
//
// Usage:
//   var act = LobsterMotion.ForAct(LobsterAct.Wave);                 // animations to start together
//   var y = act[0].Track(LobsterMotionProperty.TranslateY)?.ValueAt(.2, act[0].Easing);
//   var walk = LobsterMotion.TravelWalk(fromX: -120, fromY: 0, durationMs: 1800);
namespace OpenClawTray.Chat.LobsterVisits;

/// <summary>Element an animation targets. SVG-internal targets use viewBox units for px lengths and origins.</summary>
internal enum LobsterMotionTarget
{
    /// <summary><c>.lobster-pet__motion</c>: the travel wrapper (px translations only).</summary>
    Motion,
    /// <summary><c>.lobster-pet</c>: the positioned sprite box, used by the away (leave) transition.</summary>
    Sprite,
    /// <summary><c>.lobster-pet__body</c>: the full sprite box (16x14 * scale px).</summary>
    Body,
    /// <summary><c>.lobster-pet__svg</c>: the artwork root (breathe).</summary>
    Svg,
    ClawLeft,
    ClawRight,
    Antennae,
    EyesOpen,
    EyesClosed,
    Balloon,
    EntryBubble,
    Heart,
    Broom,
    Z,
    Bubble,
    /// <summary><c>.lobster-pet__sparkle</c>: the two stars around a shiny visitor.</summary>
    Sparkle,
}

internal enum LobsterMotionProperty { TranslateX, TranslateY, Rotate, RotateY, ScaleX, ScaleY, Opacity }

internal enum LobsterOriginUnit { Percent, ViewBox }

/// <summary>CSS transform-origin: percent of the target box, or viewBox units for SVG elements.</summary>
internal readonly record struct LobsterOrigin(double X, double Y, LobsterOriginUnit Unit)
{
    internal static readonly LobsterOrigin Center = new(50, 50, LobsterOriginUnit.Percent);
}

internal enum LobsterFill { None, Forwards, Both }

/// <summary>A CSS cubic-bezier timing function. <see cref="Linear"/> is the identity curve.</summary>
internal readonly record struct LobsterEasing(double X1, double Y1, double X2, double Y2)
{
    internal static readonly LobsterEasing Linear = new(0, 0, 1, 1);
    internal static readonly LobsterEasing Ease = new(.25, .1, .25, 1);
    internal static readonly LobsterEasing EaseIn = new(.42, 0, 1, 1);
    internal static readonly LobsterEasing EaseOut = new(0, 0, .58, 1);
    internal static readonly LobsterEasing EaseInOut = new(.42, 0, .58, 1);

    internal bool IsLinear => X1 == Y1 && X2 == Y2;

    /// <summary>Progress at input progress <paramref name="x"/> in [0, 1], solving the curve for x as browsers do.</summary>
    internal double Evaluate(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        if (IsLinear) return x;
        double t = x;
        for (var i = 0; i < 8; i++)
        {
            var error = Bezier(t, X1, X2) - x;
            if (Math.Abs(error) < 1e-7) return Bezier(t, Y1, Y2);
            var slope = BezierSlope(t, X1, X2);
            if (Math.Abs(slope) < 1e-6) break;
            t -= error / slope;
        }
        double lo = 0, hi = 1;
        t = x;
        while (hi - lo > 1e-7)
        {
            if (Bezier(t, X1, X2) < x) lo = t; else hi = t;
            t = (lo + hi) / 2;
        }
        return Bezier(t, Y1, Y2);
    }

    private static double Bezier(double t, double p1, double p2) =>
        ((((1 - 3 * p2 + 3 * p1) * t) + (3 * p2 - 6 * p1)) * t + 3 * p1) * t;

    private static double BezierSlope(double t, double p1, double p2) =>
        3 * (1 - 3 * p2 + 3 * p1) * t * t + 2 * (3 * p2 - 6 * p1) * t + 3 * p1;
}

/// <summary>One keyframe of one property. Offset is in [0, 1].</summary>
internal readonly record struct LobsterKeyframe(double Offset, double Value);

/// <summary>
/// One animated property. Translations with <see cref="Percent"/> are a percentage of the target box (width for X,
/// height for Y); otherwise px (viewBox units on SVG targets). Rotations are degrees.
/// </summary>
internal sealed record LobsterMotionTrack(LobsterMotionProperty Property, IReadOnlyList<LobsterKeyframe> Keyframes, bool Percent = false)
{
    internal static double Identity(LobsterMotionProperty property) =>
        property is LobsterMotionProperty.ScaleX or LobsterMotionProperty.ScaleY or LobsterMotionProperty.Opacity ? 1 : 0;

    /// <summary>Value at iteration progress <paramref name="p"/> with the animation's per-segment easing.</summary>
    internal double ValueAt(double p, LobsterEasing easing)
    {
        var frames = Keyframes;
        if (p <= frames[0].Offset) return frames[0].Value;
        // Later keyframes at the same offset win, so search from the end.
        for (var i = frames.Count - 1; i > 0; i--)
        {
            var from = frames[i - 1];
            var to = frames[i];
            if (p < from.Offset) continue;
            if (p >= to.Offset) return to.Value;
            var span = to.Offset - from.Offset;
            if (span <= 0) return to.Value;
            return from.Value + (to.Value - from.Value) * easing.Evaluate((p - from.Offset) / span);
        }
        return frames[^1].Value;
    }
}

/// <summary>
/// A CSS animation on one target. <see cref="Iterations"/> may be <see cref="double.PositiveInfinity"/>.
/// <see cref="StaggerMs"/> adds <c>index * StaggerMs</c> to the delay for repeated elements (z's, bubbles).
/// </summary>
internal sealed record LobsterAnimation(
    string Name,
    LobsterMotionTarget Target,
    double DurationMs,
    LobsterEasing Easing,
    IReadOnlyList<LobsterMotionTrack> Tracks,
    double Iterations = 1,
    double DelayMs = 0,
    double StaggerMs = 0,
    LobsterFill Fill = LobsterFill.None,
    LobsterOrigin? Origin = null)
{
    internal LobsterOrigin TransformOrigin => Origin ?? LobsterOrigin.Center;

    internal bool IsInfinite => double.IsPositiveInfinity(Iterations);

    /// <summary>Active duration (excluding delay); infinite for infinite iteration counts.</summary>
    internal double ActiveMs => IsInfinite ? double.PositiveInfinity : DurationMs * Iterations;

    internal double DelayFor(int index) => DelayMs + index * StaggerMs;

    internal LobsterMotionTrack? Track(LobsterMotionProperty property) => Tracks.FirstOrDefault(t => t.Property == property);
}

/// <summary>The upstream animation tables.</summary>
internal static class LobsterMotion
{
    internal const double BreatheMs = 3400;
    internal const double BlinkMs = 5200;
    internal const double ZStaggerMs = 700;
    internal const double BubbleStaggerMs = 420;
    internal const double HopLiftPx = 22;

    private static readonly LobsterOrigin Bottom = new(50, 100, LobsterOriginUnit.Percent);
    private static readonly LobsterOrigin ClawLeftOrigin = new(25, 52, LobsterOriginUnit.ViewBox);
    private static readonly LobsterOrigin ClawRightOrigin = new(95, 52, LobsterOriginUnit.ViewBox);
    private static readonly LobsterOrigin AntennaeOrigin = new(60, 14, LobsterOriginUnit.ViewBox);
    private static readonly LobsterOrigin BroomOrigin = new(50, 92, LobsterOriginUnit.Percent);

    private static LobsterMotionTrack T(LobsterMotionProperty p, params (double Offset, double Value)[] frames) =>
        Build(p, false, frames);

    private static LobsterMotionTrack Pct(LobsterMotionProperty p, params (double Offset, double Value)[] frames) =>
        Build(p, true, frames);

    // Offsets are written in percent like the CSS; missing 0%/100% frames take the property's identity value.
    private static LobsterMotionTrack Build(LobsterMotionProperty p, bool percent, (double Offset, double Value)[] frames)
    {
        var list = frames.Select(f => new LobsterKeyframe(f.Offset / 100, f.Value)).OrderBy(f => f.Offset).ToList();
        var identity = LobsterMotionTrack.Identity(p);
        if (list[0].Offset > 0) list.Insert(0, new LobsterKeyframe(0, identity));
        if (list[^1].Offset < 1) list.Add(new LobsterKeyframe(1, identity));
        return new LobsterMotionTrack(p, list, percent);
    }

    private static LobsterAnimation[] Both(LobsterAnimation claw) =>
        [claw, claw with { Target = LobsterMotionTarget.ClawRight, Origin = ClawRightOrigin }];

    private const LobsterMotionProperty X = LobsterMotionProperty.TranslateX;
    private const LobsterMotionProperty Y = LobsterMotionProperty.TranslateY;
    private const LobsterMotionProperty R = LobsterMotionProperty.Rotate;
    private const LobsterMotionProperty RY = LobsterMotionProperty.RotateY;
    private const LobsterMotionProperty SX = LobsterMotionProperty.ScaleX;
    private const LobsterMotionProperty SY = LobsterMotionProperty.ScaleY;
    private const LobsterMotionProperty O = LobsterMotionProperty.Opacity;

    /// <summary>Idle breathe on the svg root; <paramref name="face"/> is the facing scaleX (1 or -1).</summary>
    internal static LobsterAnimation Breathe(double delaySeconds, double face = 1) => new(
        "breathe", LobsterMotionTarget.Svg, BreatheMs, LobsterEasing.EaseInOut,
        [T(SX, (0, face), (100, face)), T(SY, (0, 1), (50, .96), (100, 1))],
        double.PositiveInfinity, delaySeconds * 1000, Origin: Bottom);

    /// <summary>Blink pair (open eyes, closed eyes). Upstream suspends it during nap and pet.</summary>
    internal static LobsterAnimation[] Blink(double delaySeconds) =>
    [
        new("blink-open", LobsterMotionTarget.EyesOpen, BlinkMs, LobsterEasing.Linear,
            [T(O, (0, 1), (93, 1), (94, 0), (97, 0), (98, 1), (100, 1))], double.PositiveInfinity, delaySeconds * 1000),
        new("blink-closed", LobsterMotionTarget.EyesClosed, BlinkMs, LobsterEasing.Linear,
            [T(O, (0, 0), (93, 0), (94, 1), (97, 1), (98, 0), (100, 0))], double.PositiveInfinity, delaySeconds * 1000),
    ];

    /// <summary>Walk travel on the motion wrapper from a px offset to rest.</summary>
    internal static LobsterAnimation TravelWalk(double fromX, double fromY, double durationMs) => new(
        "travel-walk", LobsterMotionTarget.Motion, durationMs, LobsterEasing.EaseInOut,
        [T(X, (0, fromX), (100, 0)), T(Y, (0, fromY), (100, 0))], Fill: LobsterFill.Both, Origin: new(0, 0, LobsterOriginUnit.Percent));

    /// <summary>Hop travel (motion wrapper, linear) plus the landing squash on the body.</summary>
    internal static LobsterAnimation[] TravelHop(double fromX, double fromY, double durationMs) =>
    [
        new("travel-hop", LobsterMotionTarget.Motion, durationMs, LobsterEasing.Linear,
            [
                T(X, (0, fromX), (14, fromX), (38, fromX * .7), (65, fromX * .3), (86, 0), (100, 0)),
                T(Y, (0, fromY), (14, fromY), (38, fromY * .85 - HopLiftPx), (65, fromY * .35 - 18), (86, 0), (100, 0)),
            ], Fill: LobsterFill.Both, Origin: new(0, 0, LobsterOriginUnit.Percent)),
        new("landing", LobsterMotionTarget.Body, durationMs, LobsterEasing.EaseInOut,
            [
                T(SX, (0, 1), (14, 1.03), (38, .98), (70, .99), (86, 1.04), (94, .99), (100, 1)),
                T(SY, (0, 1), (14, .96), (38, 1.03), (70, 1.02), (86, .94), (94, 1.02), (100, 1)),
                T(R, (0, 0), (14, 0), (38, -4), (70, 3), (86, 0), (94, 0), (100, 0)),
            ], Fill: LobsterFill.Both, Origin: Bottom),
    ];

    /// <summary>Entrance animations; durations mirror <see cref="LobsterVisitPlans.EntranceMs"/>.</summary>
    internal static LobsterAnimation[] ForEntrance(LobsterEntrance entrance) => entrance switch
    {
        LobsterEntrance.Balloon =>
        [
            new("descend", LobsterMotionTarget.Body, 1250, LobsterEasing.EaseOut,
                [Pct(Y, (0, -170), (75, 4), (88, -2), (100, 0)), T(SY, (0, 1), (75, 1), (88, .96), (100, 1))]),
            new("balloon", LobsterMotionTarget.Balloon, 1250, LobsterEasing.EaseOut,
                [T(O, (0, 1), (70, 1), (100, 0)), Pct(Y, (100, -40))], Fill: LobsterFill.Forwards),
        ],
        LobsterEntrance.Bubble =>
        [
            new("bubble-rise", LobsterMotionTarget.Body, 700, LobsterEasing.EaseOut,
                [Pct(Y, (0, 30), (60, -4), (100, 0)), T(SX, (0, .35), (60, 1.05), (100, 1)), T(SY, (0, .35), (60, 1.05), (100, 1))]),
            new("bubble-pop", LobsterMotionTarget.EntryBubble, 700, LobsterEasing.EaseOut,
                [
                    T(O, (0, .9), (55, .75), (70, 0), (100, 0)),
                    T(SX, (0, .45), (55, 1), (70, 1.25), (100, 1.25)),
                    T(SY, (0, .45), (55, 1), (70, 1.25), (100, 1.25)),
                ], Fill: LobsterFill.Forwards),
        ],
        _ => [new("enter", LobsterMotionTarget.Body, 450, LobsterEasing.EaseOut, [Pct(Y, (0, 105), (100, 0))])],
    };

    /// <summary>
    /// Animations upstream runs for <paramref name="act"/>, all started together. <paramref name="delayMs"/> is
    /// <c>--lob-act-delay</c> (the twin's offset). Nap and pet also hold eyes closed: see <see cref="HoldsEyesClosed"/>.
    /// </summary>
    internal static LobsterAnimation[] ForAct(LobsterAct act, double delayMs = 0)
    {
        LobsterAnimation[] list = act switch
        {
            LobsterAct.Wave => [new("wave", LobsterMotionTarget.ClawRight, 1400, LobsterEasing.EaseInOut,
                [T(Y, (0, 0), (20, -6), (40, -6), (60, -6), (80, -6), (100, 0)), T(R, (0, 0), (20, -30), (40, -6), (60, -30), (80, -6), (100, 0))],
                Origin: ClawRightOrigin)],
            LobsterAct.Snip => Both(Snip(500)),
            LobsterAct.Hop => [new("hop", LobsterMotionTarget.Body, 750, LobsterEasing.EaseInOut,
                [Pct(Y, (0, 0), (30, -38), (60, 0), (75, 3), (100, 0)), T(SY, (0, 1), (30, 1), (60, 1), (75, .94), (100, 1))])],
            LobsterAct.Spin => [new("spin", LobsterMotionTarget.Body, 950, LobsterEasing.EaseInOut, [T(RY, (0, 0), (100, 360))])],
            LobsterAct.Peek => [new("peek", LobsterMotionTarget.Body, 1700, LobsterEasing.EaseInOut,
                [Pct(Y, (0, 0), (15, 96), (35, 58), (75, 58), (100, 0))])],
            LobsterAct.Scuttle => [new("waddle", LobsterMotionTarget.Body, 300, LobsterEasing.EaseInOut,
                [Pct(Y, (0, 0), (50, -6), (100, 0)), T(R, (0, 2.5), (50, -2.5), (100, 2.5))], double.PositiveInfinity)],
            LobsterAct.Startle =>
            [
                new("startle", LobsterMotionTarget.Body, 750, LobsterEasing.EaseOut,
                    [Pct(Y, (0, 0), (25, -45), (55, 0), (70, -12), (85, 0), (100, 0)), T(R, (0, 0), (25, -4), (55, 0), (70, 0), (85, 0), (100, 0))]),
                .. Both(Snip(380)),
            ],
            LobsterAct.Cheer =>
            [
                new("cheer", LobsterMotionTarget.Body, 1300, LobsterEasing.EaseInOut,
                    [Pct(Y, (0, 0), (20, -42), (40, 0), (60, -34), (80, 0), (100, 0)), T(RY, (0, 0), (20, 0), (40, 0), (60, 180), (80, 360), (100, 0))]),
                .. Both(new("cheer-claws", LobsterMotionTarget.ClawLeft, 1300, LobsterEasing.EaseInOut,
                    [T(Y, (0, 0), (25, -5), (65, -5), (100, 0)), T(R, (0, 0), (25, -18), (65, -18), (100, 0))], Origin: ClawLeftOrigin)),
            ],
            LobsterAct.Pet => [new("heart", LobsterMotionTarget.Heart, 1400, LobsterEasing.EaseOut,
                [T(O, (0, 0), (25, .95), (100, 0)), T(X, (0, 0), (100, 4)), T(Y, (0, 0), (100, -18)), T(SX, (0, .6), (100, 1.15)), T(SY, (0, .6), (100, 1.15))])],
            LobsterAct.Sweep =>
            [
                new("sweep-body", LobsterMotionTarget.Body, 1800, LobsterEasing.EaseInOut,
                    [Pct(X, (0, 0), (20, -4), (40, 3), (60, -3), (80, 2), (100, 0)), T(R, (0, 0), (20, -3), (40, 3), (60, -3), (80, 2), (100, 0))]),
                new("broom", LobsterMotionTarget.Broom, 1800, LobsterEasing.EaseInOut,
                    [T(O, (0, 0), (10, 1), (90, 1), (100, 0)), T(R, (0, 0), (25, -24), (45, 18), (65, -20), (85, 12), (100, 0))], Origin: BroomOrigin),
            ],
            LobsterAct.Droop =>
            [
                new("droop", LobsterMotionTarget.Body, 1600, LobsterEasing.EaseInOut,
                    [Pct(Y, (0, 0), (20, 4), (45, 4), (70, 4), (90, 2), (100, 0)), T(R, (0, 0), (20, -2.5), (45, 2.5), (70, -1.5), (90, 0), (100, 0))]),
                new("droop-antennae", LobsterMotionTarget.Antennae, 1600, LobsterEasing.EaseInOut,
                    [T(Y, (0, 0), (30, 3), (75, 3), (100, 0)), T(SY, (0, 1), (30, .72), (75, .72), (100, 1))], Origin: AntennaeOrigin),
            ],
            LobsterAct.Nap => [new("z", LobsterMotionTarget.Z, 2000, LobsterEasing.EaseOut,
                [T(O, (0, 0), (20, .9), (100, 0)), T(X, (0, 0), (100, 9)), T(Y, (0, 0), (100, -14))], double.PositiveInfinity, StaggerMs: ZStaggerMs)],
            LobsterAct.Bubble => [new("bubble", LobsterMotionTarget.Bubble, 1600, LobsterEasing.EaseOut,
                [T(O, (0, 0), (25, .85), (100, 0)), T(X, (0, 0), (100, -7)), T(Y, (0, 0), (100, -16)), T(SX, (0, .6), (100, 1.25)), T(SY, (0, .6), (100, 1.25))],
                StaggerMs: BubbleStaggerMs)],
            _ => [],
        };
        // The heart, z's, bubbles and droop antennae rules do not read --lob-act-delay upstream.
        return delayMs == 0
            ? list
            : list.Select(a => a.Target is LobsterMotionTarget.Heart or LobsterMotionTarget.Z or LobsterMotionTarget.Bubble or LobsterMotionTarget.Antennae
                ? a
                : a with { DelayMs = a.DelayMs + delayMs }).ToArray();
    }

    /// <summary>Nap and pet pin open eyes at opacity 0 and closed eyes at 1, and suspend the blink.</summary>
    internal static bool HoldsEyesClosed(LobsterAct? act) => act is LobsterAct.Nap or LobsterAct.Pet;

    /// <summary>The shiny visitor's twinkling stars (<c>lobster-pet-twinkle</c>), staggered by <c>--i * 1.1s</c>.</summary>
    internal static LobsterAnimation Sparkle() => new(
        "twinkle", LobsterMotionTarget.Sparkle, 2200, LobsterEasing.EaseInOut,
        [T(O, (0, 0), (50, .95), (100, 0)), T(SX, (0, .5), (50, 1.1), (100, .5)), T(SY, (0, .5), (50, 1.1), (100, .5)), T(R, (0, 0), (50, 24), (100, 0))],
        double.PositiveInfinity, StaggerMs: 1100);

    private static LobsterAnimation Snip(double durationMs) => new(
        "snip", LobsterMotionTarget.ClawLeft, durationMs, LobsterEasing.EaseInOut,
        [T(SX, (0, 1), (50, 1.3), (100, 1)), T(SY, (0, 1), (50, .78), (100, 1))], 2, Origin: ClawLeftOrigin);

    /// <summary>
    /// The away transition on the sprite: translateY from rest down 55% of the sprite height (CSS -100% to -45%)
    /// over 300 ms ease-in, opacity to 0 over 200 ms ease after 100 ms. Done well inside <see cref="LobsterVisitPlans.LeaveMs"/>.
    /// </summary>
    internal static LobsterAnimation[] Leave() =>
    [
        new("leave-move", LobsterMotionTarget.Sprite, 300, LobsterEasing.EaseIn, [Pct(Y, (0, 0), (100, 55))], Fill: LobsterFill.Forwards),
        new("leave-fade", LobsterMotionTarget.Sprite, 200, LobsterEasing.Ease, [T(O, (0, 1), (100, 0))], DelayMs: 100, Fill: LobsterFill.Both),
    ];
}
