namespace OpenClawTray.Chat;

// OpenClaw db2d951: icon-data-tools.ts, components.css and chat/working-indicator.css.
// Keep the SVG coordinates, percentages and CSS cubic-bezier timing intact.
// MIT attribution and source links are in THIRD_PARTY_NOTICES.md.
internal static class ChatWorkingClawMotion
{
    internal const string BodyPath = "M8.2 10 A5.2 5.2 0 1 0 8.2 20.4 A5.2 5.2 0 0 0 8.2 10 Z M10.2 20 C14.5 20.8 19 18.6 22.3 13.2 C21 12.9 19.7 12.7 18.4 12.8 L17.5 14.6 L16 12.9 L14.3 14.5 L13.5 13 L11.5 14.2 Z";
    internal const string JawPath = "M5.6 12.2 C5.2 5.6 10.4 1.4 15.6 2 C19.4 2.6 21.8 5.2 22.6 8.2 C20.9 7.7 19.2 7.6 17.6 7.9 L16.9 6.3 L15.2 8.5 C13.6 9.4 12.2 10.9 11.6 12.4 L6.8 13 Z";
    internal const double CycleSeconds = 2.4;
    internal const double SurpriseDelaySeconds = 4.8;
    internal const int SurpriseCount = 12;

    internal readonly record struct Pose(
        double Rotation = 0, double X = 0, double Y = 0, double Scale = 1, double RotationY = 0);
    internal readonly record struct Sample(Pose Body, double Jaw, double Flip = 1, double SparkOpacity = 0, double SparkScale = 1);
    private readonly record struct Frame(double Percent, Pose Pose);
    private sealed record Motion(double Duration, Frame[] Body, Frame[]? Jaw = null, bool EaseInOut = false, int Count = 1);
    private static Frame F(double percent, double rotation = 0, double x = 0, double y = 0, double scale = 1, double rotationY = 0) =>
        new(percent, new(rotation, x, y, scale, rotationY));

    private static readonly Frame[] Flex =
        [F(0), F(6), F(10, -4), F(16, 3), F(22, -4), F(26, -4), F(32, 3), F(42), F(100)];
    private static readonly Frame[] Snip =
        [F(0, -10), F(6, -10), F(10, -26), F(16, 4), F(22, -24), F(26, -24), F(32, 4), F(42, -10), F(100, -10)];

    // Order is the upstream SURPRISE_CLASSES order. A negative index means the ordinary loop.
    private static readonly Motion[] Surprises =
    [
        new(1.2, [F(0, scale: 1), F(20, scale: -1), F(80, scale: -1), F(100, scale: 1)], EaseInOut: true),
        new(.65, Flex, Snip, Count: 2),
        new(.9, [F(0), F(100, rotationY: 360)]),
        new(1.2, [F(0), F(8), F(12, x: -2), F(16, x: 5), F(22, x: -2), F(26, x: -2), F(30, x: 5), F(38), F(46, -6, x: -3), F(52, 4, x: 8), F(62), F(100)],
            [F(0, -10), F(8, -10), F(12, -16), F(16, 4), F(22, -16), F(26, -16), F(30, 4), F(38, -10), F(46, -18), F(52, 6), F(62, -10), F(100, -10)]),
        new(.9, [F(0), F(55), F(62, -120, y: -3), F(70, -240, y: -3), F(78, -360), F(100, -360)]),
        new(2.4, [F(0), F(30, scale: 1.08), F(55), F(100)],
            [F(0, -10), F(60, -10), F(70, -24), F(76, 2), F(86, -10), F(100, -10)], EaseInOut: true),
        new(1.2, [F(0), F(15, -8), F(30), F(55, 8), F(70), F(100)],
            [F(0, -10), F(10, -20), F(15, 2), F(25, -10), F(50, -20), F(55, 2), F(65, -10), F(100, -10)]),
        new(1.2, [F(0), F(55), F(62, y: 5, scale: .72), F(72, y: 5, scale: .72), F(78, y: -1.5, scale: 1.06), F(84), F(100)],
            [F(0, -10), F(55, -10), F(62, -2), F(72, -2), F(78, -28), F(86, -10), F(100, -10)]),
        new(2.8, [F(0), F(10), F(35, 10, y: 1), F(55, 14, y: 1.4), F(60, 15, y: 1.5), F(64, -3, y: -.5), F(70), F(100)],
            [F(0, -10), F(10, -10), F(35, -16), F(60, -21), F(64, -6), F(70, -24), F(74, 4), F(80, -22), F(84, 4), F(90, -10), F(100, -10)]),
        new(1.8, [F(0), F(30), F(40, -14), F(62, -14), F(70, 4), F(76), F(100)],
            [F(0, -10), F(30, -10), F(40, -16), F(62, -16), F(70, -6), F(76, -10), F(100, -10)]),
        new(1.8, [F(0), F(30), F(36, x: 2.5), F(40, x: 1), F(46, x: 2.5), F(50, x: 1), F(56, x: 2.5), F(64), F(100)],
            [F(0, -10), F(26, -10), F(30, -30), F(36, 8), F(42, -30), F(46, 8), F(52, -30), F(56, 8), F(64, -10), F(100, -10)]),
        new(2.4, [F(0), F(6), F(10, -4), F(55, -4), F(58, 3), F(62, -4), F(66, 3), F(70, -4), F(74, 3), F(80), F(100)],
            [F(0, -10), F(6, -10), F(10, -26), F(55, -26), F(58, 4), F(62, -24), F(66, 4), F(70, -22), F(74, 4), F(80, -10), F(100, -10)]),
    ];
    private static readonly Frame[] SparkOpacity = [F(0, 0), F(46, 0), F(52, 1), F(58, 0), F(100, 0)];
    private static readonly Frame[] SparkScale = [F(0, .4), F(46, .4), F(52, 1.2), F(58, 1.5), F(100, 1.5)];

    internal static int SelectSurprise(string key, uint salt, bool eligible = true)
    {
        var hash = 2166136261u;
        foreach (var value in key)
            hash = unchecked((hash ^ value) * 16777619u);
        hash ^= salt;
        return eligible && hash % 1000 < 50 ? (int)(hash / 1000 % SurpriseCount) : -1;
    }

    internal static Sample At(double seconds, bool active, bool reducedMotion, int surprise = -1)
    {
        if (!active)
            return new(new(Rotation: 8, Scale: 1), -4);
        if (reducedMotion)
            return new(new(Scale: 1), -10);
        seconds = Math.Max(0, seconds);
        var phase = seconds % CycleSeconds / CycleSeconds * 100;
        var result = new Sample(Interpolate(Flex, phase), Interpolate(Snip, phase).Rotation);
        if (surprise < 0 || surprise >= Surprises.Length)
            return result;
        var motion = Surprises[surprise];
        var elapsed = seconds - SurpriseDelaySeconds;
        if (elapsed < 0 || elapsed >= motion.Duration * motion.Count)
            return result;
        phase = elapsed % motion.Duration / motion.Duration * 100;
        var body = Interpolate(motion.Body, phase, motion.EaseInOut, linear: surprise == 2);
        if (surprise == 0)
            return result with { Flip = body.Scale };
        return result with
        {
            Body = body,
            Jaw = motion.Jaw is null ? result.Jaw : Interpolate(motion.Jaw, phase).Rotation,
            SparkOpacity = surprise == 3 ? Interpolate(SparkOpacity, phase).Rotation : 0,
            SparkScale = surprise == 3 ? Interpolate(SparkScale, phase).Rotation : 1,
        };
    }

    private static Pose Interpolate(Frame[] frames, double percent, bool easeInOut = false, bool linear = false)
    {
        for (var index = 1; index < frames.Length; index++)
        {
            if (percent > frames[index].Percent)
                continue;
            var from = frames[index - 1];
            var to = frames[index];
            var fraction = Math.Clamp((percent - from.Percent) / (to.Percent - from.Percent), 0, 1);
            var amount = linear ? fraction : Bezier(fraction, easeInOut ? .42 : 0, 0, .58, 1);
            double Mix(double a, double b) => a + (b - a) * amount;
            return new(Mix(from.Pose.Rotation, to.Pose.Rotation), Mix(from.Pose.X, to.Pose.X),
                Mix(from.Pose.Y, to.Pose.Y), Mix(from.Pose.Scale, to.Pose.Scale),
                Mix(from.Pose.RotationY, to.Pose.RotationY));
        }
        return frames[^1].Pose;
    }

    internal static double Bezier(double x, double x1, double y1, double x2, double y2)
    {
        static double Coordinate(double t, double a, double b) =>
            3 * (1 - t) * (1 - t) * t * a + 3 * (1 - t) * t * t * b + t * t * t;
        var low = 0d;
        var high = 1d;
        for (var step = 0; step < 32; step++)
        {
            var middle = (low + high) / 2;
            if (Coordinate(middle, x1, x2) < x) low = middle;
            else high = middle;
        }
        return Coordinate((low + high) / 2, y1, y2);
    }
}
