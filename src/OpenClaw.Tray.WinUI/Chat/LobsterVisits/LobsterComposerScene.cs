// Composer collision lanes for lobster visits: where a visitor may perch and walk without covering text
// or controls. A faithful port of resolveLobsterComposerScene, planWalk, planHop and lobsterTravelDuration
// from OpenClaw's Control UI at 4cffd43ee043275f6a4c693aaacee8d8e5a61288 (MIT; see THIRD_PARTY_NOTICES.md),
// ui/src/components/lobster-pet-scene.ts. Measurement stays with the renderer; this file only does math.
//
// Usage (boxes in one coordinate space; lanes come back relative to the composer's top-left):
//   var scene = LobsterSceneResolver.Resolve(composer, footer, controls, obstaclesAbove, editor, placeholderRight);
//   if (LobsterSceneResolver.PlanWalk(scene, LobsterAnchor.Top, spotPct, roll) is { } move) Animate(move.Travel);
namespace OpenClawTray.Chat.LobsterVisits;

internal enum LobsterAnchor { Top, Floor }

internal readonly record struct LobsterBox(double Left, double Top, double Right, double Bottom)
{
    internal double Width => Right - Left;
    internal double Height => Bottom - Top;
}

internal sealed record LobsterLane(double Start, double End, double Y);

internal readonly record struct LobsterPoint(double X, double Y);

internal sealed record LobsterTravel(LobsterPoint From, LobsterPoint To, bool Hop)
{
    /// <summary>Upstream <c>lobsterTravelDuration</c>: hops take a fixed beat, walks scale with distance.</summary>
    internal double DurationMs => Hop ? 1100 : Math.Max(850, Math.Min(2600, Math.Abs(To.X - From.X) * 8));
}

internal sealed record LobsterMove(LobsterAnchor Anchor, double SpotPct, int Facing, LobsterTravel Travel);

/// <summary>
/// A perch along the composer's top edge, a walkable stretch of its empty bottom row, and a clear column
/// through the empty editor between them. Any of the three may be absent.
/// </summary>
internal sealed record LobsterScene(LobsterLane? Top, LobsterLane? Floor, (double Start, double End)? Passage)
{
    internal static readonly LobsterScene Empty = new(null, null, null);

    internal LobsterLane? Lane(LobsterAnchor anchor) => anchor == LobsterAnchor.Top ? Top : Floor;

    internal static LobsterPoint LanePoint(LobsterLane? lane, double pct) =>
        new(lane is null ? 0 : lane.Start + (lane.End - lane.Start) * pct / 100, lane?.Y ?? 0);
}

internal static class LobsterSceneResolver
{
    /// <summary>Room for elder/mighty claws, body sway and a small margin.</summary>
    internal const double Clearance = 38;

    /// <summary>Space above the composer reserved for top-lane hops and balloon entrances.</summary>
    internal const double Headroom = 88;

    private const double TwinClearance = 28;
    private const double FloorLift = 48;
    private const double MinFloorWidth = 24;
    private const double MinPassageWidth = 28;

    /// <summary>
    /// A narrow footer gets no floor rather than a guessed strip across its controls, and the floor only
    /// exists when it sits at least <see cref="FloorLift"/> below the editor's actual text area.
    /// </summary>
    internal static LobsterScene Resolve(
        LobsterBox composer,
        LobsterBox? footer,
        IReadOnlyList<LobsterBox> controls,
        IReadOnlyList<LobsterBox> topObstacles,
        LobsterBox? editor,
        double placeholderRight,
        bool twins = false)
    {
        if (composer.Width <= 0 || composer.Height <= 0)
            return LobsterScene.Empty;
        var clearance = Clearance + (twins ? TwinClearance : 0);
        var topGap = WidestGap(
            composer.Left + clearance,
            composer.Right - clearance,
            topObstacles.Where(box => box.Height > 0 && box.Bottom > composer.Top - Headroom && box.Top < composer.Top),
            clearance);
        var top = topGap is { } gap ? new LobsterLane(gap.Start - composer.Left, gap.End - composer.Left, 0) : null;
        if (footer is not { Height: > 0 } floorBox)
            return new(top, null, null);
        var floorY = Math.Min(composer.Bottom - 8, floorBox.Bottom - 5);
        if (floorY - FloorLift < (editor?.Bottom ?? composer.Top))
            return new(top, null, null);
        var floorGap = WidestGap(
            Math.Max(composer.Left, floorBox.Left) + clearance,
            Math.Min(composer.Right, floorBox.Right) - clearance,
            controls.Where(box => box.Width > 0 && box.Height > 0 && box.Top < floorY && box.Bottom > floorY - FloorLift),
            clearance);
        var floor = floorGap is { } walk && walk.End - walk.Start >= MinFloorWidth
            ? new LobsterLane(walk.Start - composer.Left, walk.End - composer.Left, floorY - composer.Top)
            : null;
        var passageStart = Math.Max(
            Math.Max(top?.Start ?? double.PositiveInfinity, floor?.Start ?? double.PositiveInfinity),
            placeholderRight - composer.Left + clearance);
        var passageEnd = Math.Min(top?.End ?? double.NegativeInfinity, floor?.End ?? double.NegativeInfinity);
        return new(top, floor, passageEnd - passageStart >= MinPassageWidth ? (passageStart, passageEnd) : null);
    }

    /// <summary>A new spot on the same lane; a same-spot walk reads as a glitch, so it takes the farther edge.</summary>
    internal static LobsterMove? PlanWalk(LobsterScene scene, LobsterAnchor anchor, double spotPct, double roll)
    {
        if (scene.Lane(anchor) is not { } lane)
            return null;
        var target = LobsterRandom.Round(roll * 100);
        if (Math.Abs(target - spotPct) < 4)
            target = spotPct > 50 ? 0 : 100;
        return new(anchor, target, target < spotPct ? -1 : 1,
            new(LobsterScene.LanePoint(lane, spotPct), LobsterScene.LanePoint(lane, target), false));
    }

    /// <summary>
    /// Walks to the clear column before hopping, rather than cutting through text, then hops to the
    /// opposite lane at the far side of the passage.
    /// </summary>
    internal static LobsterMove? PlanHop(LobsterScene scene, LobsterAnchor anchor, double spotPct)
    {
        if (scene.Passage is not { } passage || scene.Floor is null || scene.Lane(anchor) is not { } lane)
            return null;
        var from = LobsterScene.LanePoint(lane, spotPct);
        var middle = (passage.Start + passage.End) / 2;
        if (from.X < passage.Start || from.X > passage.End)
        {
            return new(anchor, (middle - lane.Start) / (lane.End - lane.Start) * 100, middle < from.X ? -1 : 1,
                new(from, new(middle, lane.Y), false));
        }
        var next = anchor == LobsterAnchor.Top ? LobsterAnchor.Floor : LobsterAnchor.Top;
        if (scene.Lane(next) is not { } destination)
            return null;
        var x = from.X < middle ? passage.End : passage.Start;
        return new(next, (x - destination.Start) / (destination.End - destination.Start) * 100, x < from.X ? -1 : 1,
            new(from, new(x, destination.Y), true));
    }

    private static (double Start, double End)? WidestGap(
        double start, double end, IEnumerable<LobsterBox> obstacles, double padding)
    {
        var cursor = start;
        (double Start, double End)? best = null;
        foreach (var box in obstacles.OrderBy(box => box.Left))
        {
            var stop = Math.Min(end, box.Left - padding);
            if (stop > cursor && (best is null || stop - cursor > best.Value.End - best.Value.Start))
                best = (cursor, stop);
            cursor = Math.Max(cursor, box.Right + padding);
        }
        if (end > cursor && (best is null || end - cursor > best.Value.End - best.Value.Start))
            best = (cursor, end);
        return best;
    }
}
